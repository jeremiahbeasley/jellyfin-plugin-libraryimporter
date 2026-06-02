using System.Xml.Linq;
using Jellyfin.Plugin.LibraryImporter.Models;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

public static class NfoParser
{
    public static MovieMetadata? ParseMovieNfo(string nfoPath)
    {
        try
        {
            var text = File.ReadAllText(nfoPath);
            // Handle multiple root elements by wrapping
            if (text.Contains("<movie", StringComparison.OrdinalIgnoreCase))
            {
                var start = text.IndexOf("<movie", StringComparison.OrdinalIgnoreCase);
                var end = text.IndexOf("</movie>", StringComparison.OrdinalIgnoreCase);
                if (start >= 0 && end > start)
                    text = text[start..(end + "</movie>".Length)];
            }

            var doc = XDocument.Parse(text);
            var root = doc.Root;
            if (root is null) return null;

            var meta = new MovieMetadata
            {
                Title = Text(root, "title") ?? string.Empty,
                OriginalTitle = Text(root, "originaltitle"),
                SortName = Text(root, "sorttitle"),
                Year = Int(root, "year"),
                Overview = Text(root, "plot") ?? Text(root, "outline"),
                Tagline = Text(root, "tagline"),
                CommunityRating = Float(root, "rating"),
                OfficialRating = Text(root, "mpaa") ?? Text(root, "certification"),
                PremiereDate = Text(root, "premiered") ?? Text(root, "releasedate"),
                TmdbId = Text(root, "tmdbid") ?? UniqueId(root, "tmdb"),
                TvdbId = Text(root, "tvdbid") ?? UniqueId(root, "tvdb"),
                ImdbId = Text(root, "imdbid") ?? Text(root, "imdb") ?? UniqueId(root, "imdb"),
                FromNfo = true,
                NfoPath = nfoPath,
            };

            if (meta.Year.HasValue)
                meta.ProductionYear = meta.Year;

            foreach (var g in root.Elements("genre"))
            {
                var val = g.Value.Trim();
                if (!string.IsNullOrEmpty(val))
                    meta.Genres.Add(val);
            }

            foreach (var s in root.Elements("studio"))
            {
                var val = s.Value.Trim();
                if (!string.IsNullOrEmpty(val))
                    meta.Studios.Add(val);
            }

            var sortOrder = 0;
            foreach (var actor in root.Elements("actor"))
            {
                var name = Text(actor, "name");
                if (string.IsNullOrEmpty(name)) continue;
                meta.People.Add(new PersonInfo
                {
                    Name = name,
                    Type = "Actor",
                    Role = Text(actor, "role"),
                    SortOrder = sortOrder++,
                    TmdbId = Text(actor, "tmdbid"),
                    ImageUrl = Text(actor, "thumb"),
                });
            }

            var director = Text(root, "director");
            if (!string.IsNullOrEmpty(director))
            {
                foreach (var d in director.Split([',', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    meta.People.Add(new PersonInfo { Name = d, Type = "Director" });
            }

            // Runtime in minutes
            var runtime = Int(root, "runtime");
            if (runtime.HasValue)
                meta.RunTimeTicks = runtime.Value * 600_000_000L;

            return meta;
        }
        catch
        {
            return null;
        }
    }

    public static TvShowMetadata? ParseTvShowNfo(string nfoPath)
    {
        try
        {
            var text = File.ReadAllText(nfoPath);
            if (text.Contains("<tvshow", StringComparison.OrdinalIgnoreCase))
            {
                var start = text.IndexOf("<tvshow", StringComparison.OrdinalIgnoreCase);
                var end = text.IndexOf("</tvshow>", StringComparison.OrdinalIgnoreCase);
                if (start >= 0 && end > start)
                    text = text[start..(end + "</tvshow>".Length)];
            }

            var doc = XDocument.Parse(text);
            var root = doc.Root;
            if (root is null) return null;

            var meta = new TvShowMetadata
            {
                Title = Text(root, "title") ?? string.Empty,
                SortName = Text(root, "sorttitle"),
                Year = Int(root, "year"),
                Overview = Text(root, "plot"),
                CommunityRating = Float(root, "rating"),
                OfficialRating = Text(root, "mpaa"),
                TvdbId = Text(root, "tvdbid") ?? UniqueId(root, "tvdb"),
                TmdbId = Text(root, "tmdbid") ?? UniqueId(root, "tmdb"),
                ImdbId = Text(root, "imdbid") ?? UniqueId(root, "imdb"),
                Status = Text(root, "status"),
                PremiereDate = Text(root, "premiered"),
            };

            foreach (var g in root.Elements("genre"))
            {
                var val = g.Value.Trim();
                if (!string.IsNullOrEmpty(val))
                    meta.Genres.Add(val);
            }

            foreach (var s in root.Elements("studio"))
            {
                var val = s.Value.Trim();
                if (!string.IsNullOrEmpty(val))
                    meta.Studios.Add(val);
            }

            var sortOrder = 0;
            foreach (var actor in root.Elements("actor"))
            {
                var name = Text(actor, "name");
                if (string.IsNullOrEmpty(name)) continue;
                meta.People.Add(new PersonInfo
                {
                    Name = name,
                    Type = "Actor",
                    Role = Text(actor, "role"),
                    SortOrder = sortOrder++,
                });
            }

            return meta;
        }
        catch
        {
            return null;
        }
    }

    public static EpisodeMetadata? ParseEpisodeNfo(string nfoPath)
    {
        try
        {
            var text = File.ReadAllText(nfoPath);
            if (text.Contains("<episodedetails", StringComparison.OrdinalIgnoreCase))
            {
                var start = text.IndexOf("<episodedetails", StringComparison.OrdinalIgnoreCase);
                var end = text.IndexOf("</episodedetails>", StringComparison.OrdinalIgnoreCase);
                if (start >= 0 && end > start)
                    text = text[start..(end + "</episodedetails>".Length)];
            }

            var doc = XDocument.Parse(text);
            var root = doc.Root;
            if (root is null) return null;

            var meta = new EpisodeMetadata
            {
                Title = Text(root, "title") ?? string.Empty,
                SeasonNumber = Int(root, "season") ?? 0,
                EpisodeNumber = Int(root, "episode") ?? 0,
                Overview = Text(root, "plot"),
                CommunityRating = Float(root, "rating"),
                PremiereDate = Text(root, "aired"),
                TvdbId = Text(root, "tvdbid") ?? UniqueId(root, "tvdb"),
                TmdbId = Text(root, "tmdbid") ?? UniqueId(root, "tmdb"),
                ImdbId = Text(root, "imdbid") ?? UniqueId(root, "imdb"),
            };

            var runtime = Int(root, "runtime");
            if (runtime.HasValue)
                meta.RunTimeTicks = runtime.Value * 600_000_000L;

            return meta;
        }
        catch
        {
            return null;
        }
    }

    private static string? Text(XElement el, string tag) =>
        el.Element(tag)?.Value?.Trim() is { Length: > 0 } v ? v : null;

    private static int? Int(XElement el, string tag) =>
        int.TryParse(Text(el, tag), out var v) ? v : null;

    private static float? Float(XElement el, string tag) =>
        float.TryParse(Text(el, tag), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? UniqueId(XElement root, string type) =>
        root.Elements("uniqueid")
            .FirstOrDefault(e => string.Equals(e.Attribute("type")?.Value, type, StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim();
}
