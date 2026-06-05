using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.LibraryImporter.Models;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

/// <summary>
/// Reads the metadata sidecars LazyLibrarian / Calibre / Audiobookshelf leave in book
/// folders: Calibre-style OPF files (metadata.opf, "&lt;title&gt;.opf") and
/// Audiobookshelf's metadata.json. Lenient like NfoParser — any parse failure returns
/// null and the caller falls through to the next source.
/// </summary>
public static partial class BookSidecarParser
{
    /// <summary>Parses a Calibre/LazyLibrarian OPF file (namespace-agnostic).</summary>
    public static BookMetadata? ParseOpf(string opfPath)
    {
        try
        {
            var doc = XDocument.Parse(File.ReadAllText(opfPath));
            var meta = new BookMetadata();

            foreach (var el in doc.Descendants())
            {
                switch (el.Name.LocalName)
                {
                    case "title" when string.IsNullOrEmpty(meta.Title):
                        meta.Title = el.Value.Trim();
                        break;
                    case "creator":
                        // role="aut" or no role at all → author
                        var role = AttrByLocalName(el, "role");
                        if ((role is null || role == "aut") && el.Value.Trim() is { Length: > 0 } author)
                            meta.Authors.Add(author);
                        break;
                    case "date" when meta.Year is null:
                        meta.Year = ExtractYear(el.Value);
                        meta.PremiereDate ??= el.Value.Trim();
                        break;
                    case "publisher" when meta.Publisher is null:
                        meta.Publisher = NonEmpty(el.Value);
                        break;
                    case "description" when meta.Overview is null:
                        meta.Overview = NonEmpty(el.Value);
                        break;
                    case "language" when meta.Language is null:
                        meta.Language = NonEmpty(el.Value);
                        break;
                    case "identifier":
                        var scheme = AttrByLocalName(el, "scheme")?.ToUpperInvariant();
                        var idVal = el.Value.Trim();
                        if (idVal.Length == 0) break;
                        if (scheme == "ISBN") meta.Isbn ??= idVal;
                        else if (scheme is "ASIN" or "MOBI-ASIN" or "AMAZON") meta.Asin ??= idVal;
                        break;
                    case "subject":
                        if (el.Value.Trim() is { Length: > 0 } subject) meta.Genres.Add(subject);
                        break;
                    case "meta":
                        var name = AttrByLocalName(el, "name");
                        var content = AttrByLocalName(el, "content");
                        if (content is null) break;
                        if (name == "calibre:series") meta.SeriesName ??= NonEmpty(content);
                        else if (name == "calibre:series_index" &&
                                 float.TryParse(content, System.Globalization.CultureInfo.InvariantCulture, out var idx))
                            meta.SeriesIndex ??= idx;
                        else if (name == "calibre:title_sort") meta.SortTitle ??= NonEmpty(content);
                        break;
                }
            }

            if (string.IsNullOrEmpty(meta.Title)) return null;
            meta.FromSidecar = true;
            return meta;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses Audiobookshelf's metadata.json (the "store metadata with item" file).</summary>
    public static BookMetadata? ParseAbsMetadataJson(string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var d = doc.RootElement;

            var meta = new BookMetadata
            {
                Title = d.GetStringOrNull("title") ?? string.Empty,
                Subtitle = d.GetStringOrNull("subtitle"),
                Publisher = d.GetStringOrNull("publisher"),
                Overview = d.GetStringOrNull("description"),
                Isbn = d.GetStringOrNull("isbn"),
                Asin = d.GetStringOrNull("asin"),
                Language = d.GetStringOrNull("language"),
            };

            foreach (var a in d.EnumerateArrayOrEmpty("authors"))
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } author)
                    meta.Authors.Add(author);
            foreach (var n in d.EnumerateArrayOrEmpty("narrators"))
                if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } narrator)
                    meta.Narrators.Add(narrator);
            foreach (var g in d.EnumerateArrayOrEmpty("genres"))
                if (g.ValueKind == JsonValueKind.String && g.GetString() is { Length: > 0 } genre)
                    meta.Genres.Add(genre);
            foreach (var t in d.EnumerateArrayOrEmpty("tags"))
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } tag)
                    meta.Tags.Add(tag);

            // ABS series entries look like "Nova Online" or "Nova Online #1.5"
            foreach (var s in d.EnumerateArrayOrEmpty("series"))
            {
                if (s.ValueKind != JsonValueKind.String || s.GetString() is not { Length: > 0 } series) continue;
                var m = SeriesIndexRegex().Match(series);
                if (m.Success)
                {
                    meta.SeriesName = series[..m.Index].Trim();
                    if (float.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var idx))
                        meta.SeriesIndex = idx;
                }
                else
                {
                    meta.SeriesName = series;
                }

                break; // first series wins
            }

            // publishedYear is loosely formatted ("2018", "2018-03-19", number)
            if (d.GetStringOrNull("publishedYear") is { } py)
            {
                meta.Year = ExtractYear(py);
                meta.PremiereDate ??= py;
            }
            else if (d.TryGetInt("publishedYear", out var yearNum))
            {
                meta.Year = yearNum;
            }

            meta.PremiereDate ??= d.GetStringOrNull("publishedDate");
            if (string.IsNullOrEmpty(meta.Title)) return null;
            meta.FromSidecar = true;
            return meta;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fills gaps in <paramref name="primary"/> from <paramref name="secondary"/>.</summary>
    public static BookMetadata Merge(BookMetadata? primary, BookMetadata secondary)
    {
        if (primary is null) return secondary;
        primary.Title = !string.IsNullOrEmpty(primary.Title) ? primary.Title : secondary.Title;
        primary.SortTitle ??= secondary.SortTitle;
        primary.Subtitle ??= secondary.Subtitle;
        primary.SeriesName ??= secondary.SeriesName;
        primary.SeriesIndex ??= secondary.SeriesIndex;
        primary.Year ??= secondary.Year;
        primary.PremiereDate ??= secondary.PremiereDate;
        primary.Publisher ??= secondary.Publisher;
        primary.Overview ??= secondary.Overview;
        primary.Isbn ??= secondary.Isbn;
        primary.Asin ??= secondary.Asin;
        primary.Language ??= secondary.Language;
        primary.CommunityRating ??= secondary.CommunityRating;
        if (primary.Authors.Count == 0) primary.Authors = secondary.Authors;
        if (primary.Narrators.Count == 0) primary.Narrators = secondary.Narrators;
        if (primary.Genres.Count == 0) primary.Genres = secondary.Genres;
        if (primary.Tags.Count == 0) primary.Tags = secondary.Tags;
        return primary;
    }

    private static string? AttrByLocalName(XElement el, string localName) =>
        el.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static string? NonEmpty(string s) =>
        s.Trim() is { Length: > 0 } v ? v : null;

    private static int? ExtractYear(string s)
    {
        var m = YearRegex().Match(s);
        return m.Success && int.TryParse(m.Value, out var y) ? y : null;
    }

    [GeneratedRegex(@"\b(\d{4})\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"#\s*([\d.]+)\s*$")]
    private static partial Regex SeriesIndexRegex();
}
