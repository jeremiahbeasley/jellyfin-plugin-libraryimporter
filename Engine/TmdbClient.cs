using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LibraryImporter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

public class TmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/original";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger _logger;

    public TmdbClient(HttpClient http, string apiKey, ILogger logger)
    {
        _http = http;
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<MovieMetadata?> GetMovieAsync(string tmdbId)
    {
        try
        {
            var url = $"{BaseUrl}/movie/{tmdbId}?api_key={_apiKey}&language=en-US&append_to_response=credits";
            var json = await _http.GetFromJsonAsync<JsonElement>(url).ConfigureAwait(false);
            return ParseMovie(json, tmdbId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB movie lookup failed for {Id}", tmdbId);
            return null;
        }
    }

    public async Task<TvShowMetadata?> GetTvShowAsync(string tmdbId)
    {
        try
        {
            var url = $"{BaseUrl}/tv/{tmdbId}?api_key={_apiKey}&language=en-US&append_to_response=credits";
            var json = await _http.GetFromJsonAsync<JsonElement>(url).ConfigureAwait(false);
            return ParseTvShow(json, tmdbId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB TV lookup failed for {Id}", tmdbId);
            return null;
        }
    }

    public async Task<(string? tmdbId, string? mediaType)> SearchAsync(string title, int? year)
    {
        try
        {
            var query = Uri.EscapeDataString(title);
            var yearParam = year.HasValue ? $"&year={year}" : "";
            var url = $"{BaseUrl}/search/multi?api_key={_apiKey}&query={query}{yearParam}&language=en-US&page=1";
            var json = await _http.GetFromJsonAsync<JsonElement>(url).ConfigureAwait(false);

            foreach (var first in json.EnumerateArrayOrEmpty("results"))
            {
                var id = first.GetIdString("id");
                if (string.IsNullOrEmpty(id)) return (null, null);
                var mediaType = first.GetStringOrNull("media_type") ?? "movie";
                return (id, mediaType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB search failed for {Title}", title);
        }

        return (null, null);
    }

    public async Task<string?> DownloadPosterAsync(string tmdbId, string destDir)
    {
        try
        {
            var url = $"{BaseUrl}/movie/{tmdbId}?api_key={_apiKey}";
            var json = await _http.GetFromJsonAsync<JsonElement>(url).ConfigureAwait(false);
            if (json.GetStringOrNull("poster_path") is { } posterPath)
            {
                var imgUrl = ImageBase + posterPath;
                var dest = Path.Combine(destDir, "poster.jpg");
                if (!File.Exists(dest))
                {
                    var bytes = await _http.GetByteArrayAsync(imgUrl).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(dest, bytes).ConfigureAwait(false);
                    _logger.LogInformation("Downloaded poster for TMDB {Id} -> {Dest}", tmdbId, dest);
                    return dest;
                }
            }
            else
            {
                _logger.LogWarning("No poster_path for TMDB {Id}", tmdbId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Poster download failed for TMDB {Id}", tmdbId);
        }

        return null;
    }

    /// <summary>One call per season returns all episode metadata, keyed by episode number.</summary>
    public async Task<Dictionary<int, EpisodeMetadata>> GetSeasonEpisodesAsync(string tmdbId, int seasonNum)
    {
        var map = new Dictionary<int, EpisodeMetadata>();
        try
        {
            var url = $"{BaseUrl}/tv/{tmdbId}/season/{seasonNum}?api_key={_apiKey}&language=en-US";
            var json = await _http.GetFromJsonAsync<JsonElement>(url).ConfigureAwait(false);
            foreach (var ep in json.EnumerateArrayOrEmpty("episodes"))
            {
                if (!ep.TryGetInt("episode_number", out var en)) continue;
                var m = new EpisodeMetadata
                {
                    SeasonNumber = seasonNum,
                    EpisodeNumber = en,
                    Title = ep.GetStringOrNull("name") ?? "",
                    Overview = ep.GetStringOrNull("overview"),
                    PremiereDate = ep.GetStringOrNull("air_date"),
                };
                if (ep.TryGetDoubleSafe("vote_average", out var r)) m.CommunityRating = (float)r;
                if (ep.TryGetInt("runtime", out var mins)) m.RunTimeTicks = mins * 600_000_000L;
                map[en] = m;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB season fetch failed for {Id} S{Season}", tmdbId, seasonNum);
        }
        return map;
    }

    private static MovieMetadata ParseMovie(JsonElement json, string tmdbId)
    {
        var meta = new MovieMetadata
        {
            Title = json.GetStringOrNull("title") ?? "",
            OriginalTitle = json.GetStringOrNull("original_title"),
            Overview = json.GetStringOrNull("overview"),
            Tagline = json.GetStringOrNull("tagline"),
            TmdbId = tmdbId,
            FromTmdb = true,
            PremiereDate = json.GetStringOrNull("release_date"),
        };

        if (json.TryGetDoubleSafe("vote_average", out var rating))
            meta.CommunityRating = (float)rating;

        if (json.TryGetInt("runtime", out var mins))
            meta.RunTimeTicks = mins * 600_000_000L;

        if (!string.IsNullOrEmpty(meta.PremiereDate) && meta.PremiereDate.Length >= 4 &&
            int.TryParse(meta.PremiereDate[..4], out var y))
        {
            meta.Year = y;
            meta.ProductionYear = y;
        }

        meta.ImdbId = json.GetStringOrNull("imdb_id");

        foreach (var g in json.EnumerateArrayOrEmpty("genres"))
            if (g.GetStringOrNull("name") is { } gn)
                meta.Genres.Add(gn);

        foreach (var s in json.EnumerateArrayOrEmpty("production_companies"))
            if (s.GetStringOrNull("name") is { } sn)
                meta.Studios.Add(sn);

        if (json.GetStringOrNull("poster_path") is { } pp)
            meta.PosterUrl = ImageBase + pp;

        if (json.GetStringOrNull("backdrop_path") is { } bp)
            meta.BackdropUrl = ImageBase + bp;

        if (json.TryGetProperty("credits", out var credits))
            ParseCredits(credits, meta.People);

        return meta;
    }

    private static TvShowMetadata ParseTvShow(JsonElement json, string tmdbId)
    {
        var meta = new TvShowMetadata
        {
            Title = json.GetStringOrNull("name") ?? "",
            Overview = json.GetStringOrNull("overview"),
            TmdbId = tmdbId,
            PremiereDate = json.GetStringOrNull("first_air_date"),
            Status = json.GetStringOrNull("status"),
        };

        if (json.TryGetDoubleSafe("vote_average", out var rating))
            meta.CommunityRating = (float)rating;

        if (!string.IsNullOrEmpty(meta.PremiereDate) && meta.PremiereDate.Length >= 4 &&
            int.TryParse(meta.PremiereDate[..4], out var y))
            meta.Year = y;

        foreach (var g in json.EnumerateArrayOrEmpty("genres"))
            if (g.GetStringOrNull("name") is { } gn)
                meta.Genres.Add(gn);

        foreach (var s in json.EnumerateArrayOrEmpty("production_companies"))
            if (s.GetStringOrNull("name") is { } sn)
                meta.Studios.Add(sn);

        if (json.GetStringOrNull("poster_path") is { } pp)
            meta.PosterUrl = ImageBase + pp;

        if (json.TryGetProperty("credits", out var credits))
            ParseCredits(credits, meta.People);

        return meta;
    }

    private static void ParseCredits(JsonElement credits, List<PersonInfo> people)
    {
        var order = 0;
        foreach (var c in credits.EnumerateArrayOrEmpty("cast"))
        {
            if (order >= 30) break;
            var name = c.GetStringOrNull("name");
            if (string.IsNullOrEmpty(name)) continue;
            people.Add(new PersonInfo
            {
                Name = name,
                Type = "Actor",
                Role = c.GetStringOrNull("character"),
                SortOrder = order++,
                TmdbId = c.GetIdString("id"),
                ImageUrl = c.GetStringOrNull("profile_path") is { } path
                    ? ImageBase + path : null,
            });
        }

        foreach (var c in credits.EnumerateArrayOrEmpty("crew"))
        {
            var job = c.GetStringOrNull("job");
            var type = job switch
            {
                "Director" => "Director",
                "Writer" or "Screenplay" => "Writer",
                "Producer" => "Producer",
                _ => null,
            };
            if (type is null) continue;
            var name = c.GetStringOrNull("name");
            if (string.IsNullOrEmpty(name)) continue;
            people.Add(new PersonInfo
            {
                Name = name,
                Type = type,
                TmdbId = c.GetIdString("id"),
            });
        }
    }
}
