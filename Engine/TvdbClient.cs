using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.LibraryImporter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

public class TvdbClient
{
    private const string BaseUrl = "https://api4.thetvdb.com/v4";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger _logger;
    private string? _token;
    private DateTime _tokenExpiry;

    public TvdbClient(HttpClient http, string apiKey, ILogger logger)
    {
        _http = http;
        _apiKey = apiKey;
        _logger = logger;
    }

    private async Task EnsureTokenAsync()
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpiry)
            return;

        var body = new { apikey = _apiKey };
        var response = await _http.PostAsJsonAsync($"{BaseUrl}/login", body).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        _token = json.GetProperty("data").GetProperty("token").GetString();
        _tokenExpiry = DateTime.UtcNow.AddHours(23);
    }

    private async Task<JsonElement?> GetAsync(string url)
    {
        await EnsureTokenAsync().ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        return json.TryGetProperty("data", out var data) ? data : null;
    }

    public async Task<MovieMetadata?> GetMovieAsync(string tvdbId)
    {
        try
        {
            var data = await GetAsync($"{BaseUrl}/movies/{tvdbId}/extended").ConfigureAwait(false);
            if (data is null) return null;
            var d = data.Value;

            var meta = new MovieMetadata
            {
                Title = d.GetStringOrNull("name") ?? "",
                TvdbId = tvdbId,
                FromTvdb = true,
            };

            if (d.TryGetInt("year", out var year))
            {
                meta.Year = year;
                meta.ProductionYear = year;
            }

            meta.Overview = d.GetStringOrNull("overview") ?? meta.Overview;

            if (d.TryGetInt("runtime", out var mins))
                meta.RunTimeTicks = mins * 600_000_000L;

            foreach (var g in d.EnumerateArrayOrEmpty("genres"))
                if (g.GetStringOrNull("name") is { } gn)
                    meta.Genres.Add(gn);

            // TVDB v4 "companies" is sometimes an object (studio/network/...) rather than an
            // array; EnumerateArrayOrEmpty skips it gracefully in that case.
            foreach (var c in d.EnumerateArrayOrEmpty("companies"))
                if (c.GetStringOrNull("name") is { } cn)
                    meta.Studios.Add(cn);

            foreach (var rid in d.EnumerateArrayOrEmpty("remoteIds"))
            {
                var source = rid.GetStringOrNull("sourceName");
                var id = rid.GetStringOrNull("id");
                if (string.IsNullOrEmpty(id)) continue;
                if (source == "TheMovieDB.com") meta.TmdbId ??= id;
                else if (source == "IMDB") meta.ImdbId ??= id;
            }

            var order = 0;
            foreach (var c in d.EnumerateArrayOrEmpty("characters"))
            {
                if (order >= 30) break;
                var pName = c.GetStringOrNull("personName");
                if (string.IsNullOrEmpty(pName)) continue;
                meta.People.Add(new PersonInfo
                {
                    Name = pName,
                    Type = "Actor",
                    Role = c.GetStringOrNull("name"),
                    SortOrder = order++,
                });
            }

            return meta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TVDB movie lookup failed for {Id}", tvdbId);
            return null;
        }
    }

    public async Task<TvShowMetadata?> GetSeriesAsync(string tvdbId)
    {
        try
        {
            var data = await GetAsync($"{BaseUrl}/series/{tvdbId}/extended").ConfigureAwait(false);
            if (data is null) return null;
            var d = data.Value;

            var meta = new TvShowMetadata
            {
                Title = d.GetStringOrNull("name") ?? "",
                TvdbId = tvdbId,
                Overview = d.GetStringOrNull("overview"),
                Status = d.TryGetProperty("status", out var st) ? st.GetStringOrNull("name") : null,
            };

            if (d.TryGetInt("year", out var year))
                meta.Year = year;

            meta.PremiereDate = d.GetStringOrNull("firstAired");

            foreach (var g in d.EnumerateArrayOrEmpty("genres"))
                if (g.GetStringOrNull("name") is { } gn)
                    meta.Genres.Add(gn);

            return meta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TVDB series lookup failed for {Id}", tvdbId);
            return null;
        }
    }

    /// <summary>Fetches all episodes for a series once (paginated), keyed by (season, episode).</summary>
    public async Task<Dictionary<(int season, int episode), EpisodeMetadata>> GetAllEpisodesAsync(string tvdbId)
    {
        var map = new Dictionary<(int, int), EpisodeMetadata>();
        try
        {
            await EnsureTokenAsync().ConfigureAwait(false);
            for (var page = 0; page < 50; page++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/series/{tvdbId}/episodes/default?page={page}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
                if (!json.TryGetProperty("data", out var data) || !data.TryGetProperty("episodes", out var eps)) break;

                var count = 0;
                foreach (var ep in eps.AsArrayOrEmpty())
                {
                    count++;
                    if (!ep.TryGetInt("seasonNumber", out var s)) continue;
                    if (!ep.TryGetInt("number", out var e)) continue;
                    var m = new EpisodeMetadata
                    {
                        SeasonNumber = s,
                        EpisodeNumber = e,
                        Title = ep.GetStringOrNull("name") ?? "",
                        Overview = ep.GetStringOrNull("overview"),
                        PremiereDate = ep.GetStringOrNull("aired"),
                    };
                    if (ep.TryGetInt("runtime", out var mins)) m.RunTimeTicks = mins * 600_000_000L;
                    map[(s, e)] = m;
                }

                var hasNext = json.TryGetProperty("links", out var links)
                    && links.TryGetProperty("next", out var nx) && nx.ValueKind != JsonValueKind.Null;
                if (!hasNext || count == 0) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TVDB episodes fetch failed for {Id}", tvdbId);
        }
        return map;
    }

    public async Task<(string? id, string? type)> SearchAsync(string title, int? year)
    {
        try
        {
            var query = Uri.EscapeDataString(title);
            var yearParam = year.HasValue ? $"&year={year}" : "";
            var data = await GetAsync($"{BaseUrl}/search?query={query}{yearParam}").ConfigureAwait(false);
            if (data is null) return (null, null);

            foreach (var item in data.Value.AsArrayOrEmpty())
            {
                var tvdbId = item.GetIdString("tvdb_id");
                var type = item.GetStringOrNull("type");
                if (!string.IsNullOrEmpty(tvdbId))
                    return (tvdbId, type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TVDB search failed for {Title}", title);
        }

        return (null, null);
    }
}
