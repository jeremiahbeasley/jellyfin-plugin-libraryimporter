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

            if (json.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                var id = first.GetProperty("id").GetInt64().ToString();
                var mediaType = first.TryGetProperty("media_type", out var mt) ? mt.GetString() : "movie";
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
            if (json.TryGetProperty("poster_path", out var pp) && pp.GetString() is { } posterPath)
            {
                var imgUrl = ImageBase + posterPath;
                var dest = Path.Combine(destDir, "poster.jpg");
                if (!File.Exists(dest))
                {
                    var bytes = await _http.GetByteArrayAsync(imgUrl).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(dest, bytes).ConfigureAwait(false);
                    return dest;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Poster download failed for TMDB {Id}", tmdbId);
        }

        return null;
    }

    private static MovieMetadata ParseMovie(JsonElement json, string tmdbId)
    {
        var meta = new MovieMetadata
        {
            Title = json.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            OriginalTitle = json.TryGetProperty("original_title", out var ot) ? ot.GetString() : null,
            Overview = json.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
            Tagline = json.TryGetProperty("tagline", out var tl) ? tl.GetString() : null,
            TmdbId = tmdbId,
            FromTmdb = true,
            PremiereDate = json.TryGetProperty("release_date", out var rd) ? rd.GetString() : null,
        };

        if (json.TryGetProperty("vote_average", out var va) && va.TryGetDouble(out var rating))
            meta.CommunityRating = (float)rating;

        if (json.TryGetProperty("runtime", out var rt) && rt.TryGetInt32(out var mins))
            meta.RunTimeTicks = mins * 600_000_000L;

        if (!string.IsNullOrEmpty(meta.PremiereDate) && meta.PremiereDate.Length >= 4 &&
            int.TryParse(meta.PremiereDate[..4], out var y))
        {
            meta.Year = y;
            meta.ProductionYear = y;
        }

        if (json.TryGetProperty("imdb_id", out var imdb))
            meta.ImdbId = imdb.GetString();

        if (json.TryGetProperty("genres", out var genres))
        {
            foreach (var g in genres.EnumerateArray())
                if (g.TryGetProperty("name", out var gn))
                    meta.Genres.Add(gn.GetString()!);
        }

        if (json.TryGetProperty("production_companies", out var studios))
        {
            foreach (var s in studios.EnumerateArray())
                if (s.TryGetProperty("name", out var sn))
                    meta.Studios.Add(sn.GetString()!);
        }

        if (json.TryGetProperty("poster_path", out var poster) && poster.GetString() is { } pp)
            meta.PosterUrl = ImageBase + pp;

        if (json.TryGetProperty("backdrop_path", out var backdrop) && backdrop.GetString() is { } bp)
            meta.BackdropUrl = ImageBase + bp;

        if (json.TryGetProperty("credits", out var credits))
            ParseCredits(credits, meta.People);

        return meta;
    }

    private static TvShowMetadata ParseTvShow(JsonElement json, string tmdbId)
    {
        var meta = new TvShowMetadata
        {
            Title = json.TryGetProperty("name", out var t) ? t.GetString() ?? "" : "",
            Overview = json.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
            TmdbId = tmdbId,
            PremiereDate = json.TryGetProperty("first_air_date", out var rd) ? rd.GetString() : null,
            Status = json.TryGetProperty("status", out var st) ? st.GetString() : null,
        };

        if (json.TryGetProperty("vote_average", out var va) && va.TryGetDouble(out var rating))
            meta.CommunityRating = (float)rating;

        if (!string.IsNullOrEmpty(meta.PremiereDate) && meta.PremiereDate.Length >= 4 &&
            int.TryParse(meta.PremiereDate[..4], out var y))
            meta.Year = y;

        if (json.TryGetProperty("genres", out var genres))
            foreach (var g in genres.EnumerateArray())
                if (g.TryGetProperty("name", out var gn))
                    meta.Genres.Add(gn.GetString()!);

        if (json.TryGetProperty("production_companies", out var studios))
            foreach (var s in studios.EnumerateArray())
                if (s.TryGetProperty("name", out var sn))
                    meta.Studios.Add(sn.GetString()!);

        if (json.TryGetProperty("poster_path", out var poster) && poster.GetString() is { } pp)
            meta.PosterUrl = ImageBase + pp;

        if (json.TryGetProperty("credits", out var credits))
            ParseCredits(credits, meta.People);

        return meta;
    }

    private static void ParseCredits(JsonElement credits, List<PersonInfo> people)
    {
        if (credits.TryGetProperty("cast", out var cast))
        {
            var order = 0;
            foreach (var c in cast.EnumerateArray())
            {
                if (order >= 30) break;
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                people.Add(new PersonInfo
                {
                    Name = name,
                    Type = "Actor",
                    Role = c.TryGetProperty("character", out var ch) ? ch.GetString() : null,
                    SortOrder = order++,
                    TmdbId = c.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : null,
                    ImageUrl = c.TryGetProperty("profile_path", out var pp) && pp.GetString() is { } path
                        ? ImageBase + path : null,
                });
            }
        }

        if (credits.TryGetProperty("crew", out var crew))
        {
            foreach (var c in crew.EnumerateArray())
            {
                var job = c.TryGetProperty("job", out var j) ? j.GetString() : null;
                var type = job switch
                {
                    "Director" => "Director",
                    "Writer" or "Screenplay" => "Writer",
                    "Producer" => "Producer",
                    _ => null,
                };
                if (type is null) continue;
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                people.Add(new PersonInfo
                {
                    Name = name,
                    Type = type,
                    TmdbId = c.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : null,
                });
            }
        }
    }
}
