using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.LibraryImporter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

/// <summary>
/// Sanity check that a search result plausibly matches what we asked for — guards
/// against fuzzy catalog search returning a different book (e.g. the same author's
/// other title when the query title finds nothing). Title passes when most of the
/// shorter side's significant words appear in the longer side (tolerates subtitle
/// variants); author passes on any shared name token when both sides are known.
/// </summary>
internal static class SearchMatch
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "and", "in", "to", "book", "novel", "series",
    };

    private static List<string> Tokens(string? s) =>
        string.IsNullOrEmpty(s)
            ? []
            : System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), "[a-z0-9']+")
                .Select(m => m.Value)
                .Where(t => !Stop.Contains(t))
                .ToList();

    public static bool Plausible(string queryTitle, string? queryAuthor, string? resultTitle, IEnumerable<string>? resultAuthors)
    {
        var q = Tokens(queryTitle);
        var r = Tokens(resultTitle);
        if (q.Count > 0 && r.Count > 0)
        {
            var small = q.Count <= r.Count ? q : r;
            var large = q.Count <= r.Count ? r : q;
            var hits = small.Count(large.Contains);
            if (hits < Math.Max(1, (int)Math.Ceiling(small.Count * 0.6)))
            {
                return false;
            }
        }

        var qa = Tokens(queryAuthor);
        var ra = (resultAuthors ?? []).SelectMany(a => Tokens(a)).ToList();
        return qa.Count == 0 || ra.Count == 0 || qa.Intersect(ra).Any();
    }
}

/// <summary>
/// Audnexus (https://api.audnex.us) — keyless audiobook metadata, looked up by Audible
/// ASIN. When a book has no ASIN from its sidecars, the public Audible catalog search is
/// used to find one by title/author first.
/// </summary>
public class AudnexusClient
{
    private const string BaseUrl = "https://api.audnex.us";
    private const string AudibleSearch = "https://api.audible.com/1.0/catalog/products";
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public AudnexusClient(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<BookMetadata?> GetAsync(string? asin, string title, string? author)
    {
        try
        {
            // An ASIN from sidecar metadata is trusted; one found via fuzzy
            // catalog search must pass the plausibility gate below.
            var fromSearch = string.IsNullOrEmpty(asin);
            asin ??= await FindAsinAsync(title, author).ConfigureAwait(false);
            if (string.IsNullOrEmpty(asin)) return null;

            var d = await _http.GetFromJsonAsync<JsonElement>($"{BaseUrl}/books/{asin}").ConfigureAwait(false);

            var meta = new BookMetadata
            {
                Title = d.GetStringOrNull("title") ?? title,
                Subtitle = d.GetStringOrNull("subtitle"),
                Publisher = d.GetStringOrNull("publisherName"),
                Overview = HtmlToPlainText(d.GetStringOrNull("summary")),
                Asin = d.GetStringOrNull("asin") ?? asin,
                Isbn = d.GetStringOrNull("isbn"),
                Language = d.GetStringOrNull("language"),
                PremiereDate = d.GetStringOrNull("releaseDate"),
                FromApi = true,
            };

            if (meta.PremiereDate is { Length: >= 4 } rd && int.TryParse(rd[..4], out var y))
                meta.Year = y;

            // Audnexus rates 0-5 (as a string); Jellyfin community rating is 0-10.
            if (d.GetStringOrNull("rating") is { } rating
                && float.TryParse(rating, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0)
                meta.CommunityRating = r * 2;

            foreach (var a in d.EnumerateArrayOrEmpty("authors"))
                if (a.GetStringOrNull("name") is { Length: > 0 } name)
                    meta.Authors.Add(name);
            foreach (var n in d.EnumerateArrayOrEmpty("narrators"))
                if (n.GetStringOrNull("name") is { Length: > 0 } name)
                    meta.Narrators.Add(name);
            foreach (var g in d.EnumerateArrayOrEmpty("genres"))
                if (g.GetStringOrNull("name") is { Length: > 0 } name)
                    meta.Genres.Add(name);

            if (d.TryGetProperty("seriesPrimary", out var series) && series.ValueKind == JsonValueKind.Object)
            {
                meta.SeriesName = series.GetStringOrNull("name");
                if (series.GetStringOrNull("position") is { } pos
                    && float.TryParse(pos, System.Globalization.CultureInfo.InvariantCulture, out var idx))
                    meta.SeriesIndex = idx;
            }

            if (string.IsNullOrEmpty(meta.Title)) return null;

            if (fromSearch && !SearchMatch.Plausible(title, author, meta.Title, meta.Authors))
            {
                _logger.LogInformation(
                    "Audnexus search hit '{Result}' rejected as implausible match for '{Title}' by '{Author}' — leaving book without API metadata",
                    meta.Title, title, author);
                return null;
            }

            return meta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audnexus lookup failed for {Title}", title);
            return null;
        }
    }

    private async Task<string?> FindAsinAsync(string title, string? author)
    {
        try
        {
            var url = $"{AudibleSearch}?num_results=1&title={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrEmpty(author)) url += $"&author={Uri.EscapeDataString(author)}";
            var d = await _http.GetFromJsonAsync<JsonElement>(url).ConfigureAwait(false);
            foreach (var p in d.EnumerateArrayOrEmpty("products"))
                if (p.GetStringOrNull("asin") is { Length: > 0 } asin)
                    return asin;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audible ASIN search failed for {Title}", title);
        }

        return null;
    }

    /// <summary>
    /// Audnexus summaries are HTML; convert to plain text, keeping paragraph breaks.
    /// </summary>
    private static string? HtmlToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var text = System.Text.RegularExpressions.Regex.Replace(html, "</p>\\s*", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}

/// <summary>
/// OpenLibrary (https://openlibrary.org) — keyless ebook metadata by title/author search,
/// with a second call to the work record for the description.
/// </summary>
public class OpenLibraryClient
{
    private const string BaseUrl = "https://openlibrary.org";
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public OpenLibraryClient(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<BookMetadata?> SearchAsync(string title, string? author)
    {
        try
        {
            var url = $"{BaseUrl}/search.json?limit=1&title={Uri.EscapeDataString(title)}"
                + (string.IsNullOrEmpty(author) ? "" : $"&author={Uri.EscapeDataString(author)}")
                + "&fields=key,title,author_name,first_publish_year,isbn,subject";
            var d = await _http.GetFromJsonAsync<JsonElement>(url).ConfigureAwait(false);

            foreach (var doc in d.EnumerateArrayOrEmpty("docs"))
            {
                var meta = new BookMetadata
                {
                    Title = doc.GetStringOrNull("title") ?? title,
                    FromApi = true,
                };

                foreach (var a in doc.EnumerateArrayOrEmpty("author_name"))
                    if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } name)
                        meta.Authors.Add(name);
                if (doc.TryGetInt("first_publish_year", out var y)) meta.Year = y;
                foreach (var i in doc.EnumerateArrayOrEmpty("isbn"))
                {
                    if (i.ValueKind == JsonValueKind.String) { meta.Isbn = i.GetString(); break; }
                }

                // OpenLibrary search is always fuzzy — gate the hit before
                // spending another call on the description.
                if (!SearchMatch.Plausible(title, author, meta.Title, meta.Authors))
                {
                    _logger.LogInformation(
                        "OpenLibrary search hit '{Result}' rejected as implausible match for '{Title}' by '{Author}' — leaving book without API metadata",
                        meta.Title, title, author);
                    return null;
                }

                var subjects = doc.EnumerateArrayOrEmpty("subject")
                    .Where(s => s.ValueKind == JsonValueKind.String)
                    .Select(s => s.GetString()!)
                    .Take(6);
                meta.Genres.AddRange(subjects);

                // description lives on the work record
                if (doc.GetStringOrNull("key") is { } workKey)
                    meta.Overview = await GetWorkDescriptionAsync(workKey).ConfigureAwait(false);

                return meta;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenLibrary search failed for {Title}", title);
        }

        return null;
    }

    private async Task<string?> GetWorkDescriptionAsync(string workKey)
    {
        try
        {
            var d = await _http.GetFromJsonAsync<JsonElement>($"{BaseUrl}{workKey}.json").ConfigureAwait(false);
            if (!d.TryGetProperty("description", out var desc)) return null;
            return desc.ValueKind switch
            {
                JsonValueKind.String => desc.GetString(),
                JsonValueKind.Object => desc.GetStringOrNull("value"),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
