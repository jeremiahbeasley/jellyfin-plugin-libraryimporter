using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.LibraryImporter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

/// <summary>
/// Optional Audiobookshelf integration. When a server URL + API token are configured,
/// the whole ABS catalog is loaded once per run and audiobooks are matched to ABS items
/// by path tail ("Author/Book"), inheriting every correction the user made in ABS.
/// ABS and Jellyfin usually see the library through different mounts, so absolute paths
/// can't be compared — only their trailing segments.
/// </summary>
public class AudiobookshelfClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger _logger;

    // keyed by normalized "author/book" path tail and by bare book-folder name
    private readonly Dictionary<string, BookMetadata> _byTail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BookMetadata> _byFolder = new(StringComparer.OrdinalIgnoreCase);

    public AudiobookshelfClient(HttpClient http, string baseUrl, string apiKey, ILogger logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    public int ItemCount => _byTail.Count;

    /// <summary>Loads every book item from every ABS "book" library. Returns false on failure.</summary>
    public async Task<bool> LoadAsync(CancellationToken ct)
    {
        try
        {
            var libs = await GetAsync($"{_baseUrl}/api/libraries", ct).ConfigureAwait(false);
            if (libs is null) return false;

            foreach (var lib in libs.Value.EnumerateArrayOrEmpty("libraries"))
            {
                if (lib.GetStringOrNull("mediaType") != "book") continue;
                var libId = lib.GetStringOrNull("id");
                if (libId is null) continue;

                var items = await GetAsync($"{_baseUrl}/api/libraries/{libId}/items?limit=0", ct).ConfigureAwait(false);
                if (items is null) continue;

                foreach (var item in items.Value.EnumerateArrayOrEmpty("results"))
                {
                    var meta = ParseItem(item);
                    if (meta is null) continue;

                    var rel = (item.GetStringOrNull("relPath") ?? item.GetStringOrNull("path") ?? "")
                        .Replace('\\', '/').Trim('/');
                    if (rel.Length == 0) continue;

                    var segments = rel.Split('/');
                    var tail = segments.Length >= 2 ? $"{segments[^2]}/{segments[^1]}" : segments[^1];
                    _byTail.TryAdd(tail, meta);
                    _byFolder.TryAdd(segments[^1], meta);
                }
            }

            _logger.LogInformation("Audiobookshelf: loaded {Count} book items", _byTail.Count);
            return _byTail.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audiobookshelf catalog load failed — falling back to folder metadata");
            return false;
        }
    }

    /// <summary>Finds the ABS item for a scanned book folder, by "author/book" tail then folder name.</summary>
    public BookMetadata? FindByFolder(string bookDir)
    {
        var norm = bookDir.Replace('\\', '/').TrimEnd('/');
        var segments = norm.Split('/');
        if (segments.Length >= 2 && _byTail.TryGetValue($"{segments[^2]}/{segments[^1]}", out var byTail))
            return byTail;
        return _byFolder.TryGetValue(segments[^1], out var byName) ? byName : null;
    }

    private async Task<JsonElement?> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
    }

    private static BookMetadata? ParseItem(JsonElement item)
    {
        if (!item.TryGetProperty("media", out var media)
            || !media.TryGetProperty("metadata", out var md)
            || md.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var meta = new BookMetadata
        {
            Title = md.GetStringOrNull("title") ?? "",
            Subtitle = md.GetStringOrNull("subtitle"),
            Publisher = md.GetStringOrNull("publisher"),
            Overview = md.GetStringOrNull("description"),
            Isbn = md.GetStringOrNull("isbn"),
            Asin = md.GetStringOrNull("asin"),
            Language = md.GetStringOrNull("language"),
            FromAbs = true,
        };

        if (string.IsNullOrEmpty(meta.Title)) return null;

        // Full form: authors=[{name}], series=[{name,sequence}], narrators=["..."]
        // Minified form: authorName/seriesName joined strings
        foreach (var a in md.EnumerateArrayOrEmpty("authors"))
        {
            var name = a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetStringOrNull("name");
            if (!string.IsNullOrEmpty(name)) meta.Authors.Add(name);
        }

        if (meta.Authors.Count == 0 && md.GetStringOrNull("authorName") is { Length: > 0 } joined)
            meta.Authors.AddRange(joined.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        foreach (var n in md.EnumerateArrayOrEmpty("narrators"))
        {
            var name = n.ValueKind == JsonValueKind.String ? n.GetString() : n.GetStringOrNull("name");
            if (!string.IsNullOrEmpty(name)) meta.Narrators.Add(name);
        }

        foreach (var s in md.EnumerateArrayOrEmpty("series"))
        {
            meta.SeriesName = s.ValueKind == JsonValueKind.String ? s.GetString() : s.GetStringOrNull("name");
            var seq = s.ValueKind == JsonValueKind.Object ? s.GetStringOrNull("sequence") : null;
            if (seq is not null && float.TryParse(seq, System.Globalization.CultureInfo.InvariantCulture, out var idx))
                meta.SeriesIndex = idx;
            break;
        }

        meta.SeriesName ??= md.GetStringOrNull("seriesName");

        foreach (var g in md.EnumerateArrayOrEmpty("genres"))
            if (g.ValueKind == JsonValueKind.String && g.GetString() is { Length: > 0 } genre)
                meta.Genres.Add(genre);

        if (md.GetStringOrNull("publishedYear") is { } py)
        {
            if (int.TryParse(py, out var y)) meta.Year = y;
        }
        else if (md.TryGetInt("publishedYear", out var yearNum))
        {
            meta.Year = yearNum;
        }

        meta.PremiereDate = md.GetStringOrNull("publishedDate");
        return meta;
    }
}
