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
                Title = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                TvdbId = tvdbId,
                FromTvdb = true,
            };

            if (d.TryGetProperty("year", out var y) && y.TryGetInt32(out var year))
            {
                meta.Year = year;
                meta.ProductionYear = year;
            }

            if (d.TryGetProperty("overview", out var ov))
                meta.Overview = ov.GetString();

            if (d.TryGetProperty("runtime", out var rt) && rt.TryGetInt32(out var mins))
                meta.RunTimeTicks = mins * 600_000_000L;

            if (d.TryGetProperty("genres", out var genres))
                foreach (var g in genres.EnumerateArray())
                    if (g.TryGetProperty("name", out var gn))
                        meta.Genres.Add(gn.GetString()!);

            if (d.TryGetProperty("companies", out var cos))
                foreach (var c in cos.EnumerateArray())
                    if (c.TryGetProperty("name", out var cn))
                        meta.Studios.Add(cn.GetString()!);

            if (d.TryGetProperty("remoteIds", out var rids))
            {
                foreach (var rid in rids.EnumerateArray())
                {
                    var source = rid.TryGetProperty("sourceName", out var sn) ? sn.GetString() : null;
                    var id = rid.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (source == "TheMovieDB.com") meta.TmdbId ??= id;
                    else if (source == "IMDB") meta.ImdbId ??= id;
                }
            }

            if (d.TryGetProperty("characters", out var chars))
            {
                var order = 0;
                foreach (var c in chars.EnumerateArray())
                {
                    if (order >= 30) break;
                    var pName = c.TryGetProperty("personName", out var pn) ? pn.GetString() : null;
                    if (string.IsNullOrEmpty(pName)) continue;
                    meta.People.Add(new PersonInfo
                    {
                        Name = pName,
                        Type = "Actor",
                        Role = c.TryGetProperty("name", out var cn) ? cn.GetString() : null,
                        SortOrder = order++,
                    });
                }
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
                Title = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                TvdbId = tvdbId,
                Overview = d.TryGetProperty("overview", out var ov) ? ov.GetString() : null,
                Status = d.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn)
                    ? sn.GetString() : null,
            };

            if (d.TryGetProperty("year", out var y) && y.TryGetInt32(out var year))
                meta.Year = year;

            if (d.TryGetProperty("firstAired", out var fa))
                meta.PremiereDate = fa.GetString();

            if (d.TryGetProperty("genres", out var genres))
                foreach (var g in genres.EnumerateArray())
                    if (g.TryGetProperty("name", out var gn))
                        meta.Genres.Add(gn.GetString()!);

            return meta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TVDB series lookup failed for {Id}", tvdbId);
            return null;
        }
    }

    public async Task<(string? id, string? type)> SearchAsync(string title, int? year)
    {
        try
        {
            var query = Uri.EscapeDataString(title);
            var yearParam = year.HasValue ? $"&year={year}" : "";
            var data = await GetAsync($"{BaseUrl}/search?query={query}{yearParam}").ConfigureAwait(false);
            if (data is null) return (null, null);

            foreach (var item in data.Value.EnumerateArray())
            {
                var tvdbId = item.TryGetProperty("tvdb_id", out var id) ? id.GetString() : null;
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
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
