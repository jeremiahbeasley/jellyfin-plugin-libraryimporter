using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LibraryImporter.Configuration;
using Jellyfin.Plugin.LibraryImporter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using JfPersonInfo = MediaBrowser.Controller.Entities.PersonInfo;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

/// <summary>
/// Path A engine: resolves metadata in bulk (NFO/TMDB/TVDB) exactly as before,
/// but persists through the supported in-process ILibraryManager API
/// (CreateItems / UpdateItemsAsync) instead of raw SQL. Jellyfin handles
/// item IDs, parent/ancestor wiring and cache coherence for us.
/// </summary>
public class ImportEngine
{
    private readonly ILibraryManager _lib;
    private readonly TmdbClient _tmdb;
    private readonly TvdbClient _tvdb;
    private readonly ILogger _logger;
    private readonly PluginConfiguration _config;
    private readonly bool _dryRun;

    public ImportEngine(ILibraryManager lib, TmdbClient tmdb, TvdbClient tvdb,
        PluginConfiguration config, ILogger logger)
    {
        _lib = lib;
        _tmdb = tmdb;
        _tvdb = tvdb;
        _config = config;
        _logger = logger;
        _dryRun = config.DryRun;
    }

    public async Task<LibraryScanResult> ImportMoviesAsync(
        string libraryName, List<string> libraryPaths,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = new LibraryScanResult { LibraryName = libraryName };

        var folderByPath = ResolvePhysicalFolders(libraryPaths);
        var movies = DiskScanner.ScanMovies(libraryPaths);
        _logger.LogInformation("Found {Count} movie folders in '{Library}'", movies.Count, libraryName);

        // group persists by parent folder
        var createByParent = new Dictionary<Guid, (Folder parent, List<BaseItem> items)>();
        var updateByParent = new Dictionary<Guid, (Folder parent, List<BaseItem> items)>();
        var pendingPeople = new List<(BaseItem item, List<JfPersonInfo> people)>();

        for (var i = 0; i < movies.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)i / Math.Max(1, movies.Count) * 100);

            var (folderPath, videoFile, nfoFile) = movies[i];
            var folderName = Path.GetFileName(folderPath);

            try
            {
                var basePath = libraryPaths.FirstOrDefault(p => folderPath.StartsWith(p, StringComparison.Ordinal));
                if (basePath is null || !folderByPath.TryGetValue(basePath, out var parent))
                {
                    result.Skipped++;
                    continue;
                }

                var meta = await ResolveMovieMetadataAsync(folderPath, folderName, nfoFile).ConfigureAwait(false);
                if (meta is null) { result.Skipped++; continue; }

                meta.FolderPath = folderPath;
                meta.VideoPath = videoFile;
                ApplyOverride(meta);

                var itemPath = videoFile ?? folderPath;
                var itemId = _lib.GetNewItemId(itemPath, typeof(Movie));

                var existing = _lib.GetItemById<Movie>(itemId);
                var isNew = existing is null;
                var movie = existing ?? new Movie { Id = itemId };

                PopulateMovie(movie, itemPath, meta, parent);

                // Download a TMDB poster when none exists locally
                if (!_dryRun && movie.GetImageInfo(ImageType.Primary, 0) is null && !string.IsNullOrEmpty(meta.TmdbId))
                {
                    var poster = await _tmdb.DownloadPosterAsync(meta.TmdbId, folderPath).ConfigureAwait(false);
                    if (poster is not null)
                    {
                        movie.SetImage(new ItemImageInfo { Path = poster, Type = ImageType.Primary, DateModified = DateTime.UtcNow }, 0);
                        result.PostersDownloaded++;
                    }
                }

                if (meta.People.Count > 0)
                    pendingPeople.Add((movie, ToJellyfinPeople(meta.People)));

                var bucket = isNew ? createByParent : updateByParent;
                if (!bucket.TryGetValue(parent.Id, out var tup))
                {
                    tup = (parent, new List<BaseItem>());
                    bucket[parent.Id] = tup;
                }
                tup.items.Add(movie);

                if (isNew) result.Added++; else result.Updated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing movie: {Folder}", folderName);
                result.Failed++;
                result.Errors.Add($"{folderName}: {ex.Message}");
            }
        }

        if (!_dryRun)
        {
            foreach (var (_, (parent, items)) in createByParent)
                _lib.CreateItems(items, parent, ct);
            foreach (var (_, (parent, items)) in updateByParent)
                await _lib.UpdateItemsAsync(items, parent, ItemUpdateType.MetadataImport, ct).ConfigureAwait(false);

            foreach (var (item, people) in pendingPeople)
            {
                ct.ThrowIfCancellationRequested();
                try { await _lib.UpdatePeopleAsync(item, people, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "People update failed for {Name}", item.Name); }
            }

            if (_config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.PurgeMissing == true)
                result.Purged = PurgeMissingMovies(folderByPath.Values, ct);
        }

        progress?.Report(100);
        return result;
    }

    private int PurgeMissingMovies(IEnumerable<Folder> parents, CancellationToken ct)
    {
        var purged = 0;
        foreach (var parent in parents)
        {
            var items = _lib.GetItemList(new InternalItemsQuery
            {
                Parent = parent,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],
            });
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(item.Path) && !File.Exists(item.Path))
                {
                    _lib.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                    purged++;
                }
            }
        }
        return purged;
    }

    private void PopulateMovie(Movie movie, string itemPath, MovieMetadata meta, Folder parent)
    {
        movie.ParentId = parent.Id;
        movie.Path = itemPath;
        if (!string.IsNullOrEmpty(meta.Title)) movie.Name = meta.Title;
        if (!string.IsNullOrEmpty(meta.OriginalTitle)) movie.OriginalTitle = meta.OriginalTitle;
        if (!string.IsNullOrEmpty(meta.SortName)) movie.ForcedSortName = meta.SortName;
        movie.Overview = meta.Overview ?? movie.Overview;
        movie.Tagline = meta.Tagline ?? movie.Tagline;
        movie.ProductionYear = meta.ProductionYear ?? meta.Year ?? movie.ProductionYear;
        movie.PremiereDate = ParseDate(meta.PremiereDate) ?? movie.PremiereDate;
        movie.CommunityRating = meta.CommunityRating ?? movie.CommunityRating;
        if (!string.IsNullOrEmpty(meta.OfficialRating)) movie.OfficialRating = meta.OfficialRating;
        movie.RunTimeTicks = meta.RunTimeTicks ?? movie.RunTimeTicks;
        if (meta.Genres.Count > 0) movie.Genres = meta.Genres.ToArray();
        if (meta.Studios.Count > 0) movie.SetStudios(meta.Studios);
        if (!string.IsNullOrEmpty(meta.TmdbId)) movie.ProviderIds["Tmdb"] = meta.TmdbId;
        if (!string.IsNullOrEmpty(meta.TvdbId)) movie.ProviderIds["Tvdb"] = meta.TvdbId;
        if (!string.IsNullOrEmpty(meta.ImdbId)) movie.ProviderIds["Imdb"] = meta.ImdbId;

        // Local images present on disk (poster/backdrop/etc.)
        foreach (var (imageType, path) in DiskScanner.FindImages(meta.FolderPath!))
            SetImageIfMissing(movie, imageType, path);

        movie.IsLocked = true; // mirror the script: our metadata wins over future scans
        if (string.IsNullOrEmpty(movie.PresentationUniqueKey))
            movie.PresentationUniqueKey = movie.CreatePresentationUniqueKey();
    }

    private static void SetImageIfMissing(BaseItem item, int imageType, string path)
    {
        var type = (ImageType)imageType;
        if (item.GetImageInfo(type, 0) is not null) return;
        item.SetImage(new ItemImageInfo { Path = path, Type = type, DateModified = DateTime.UtcNow }, 0);
    }

    private static List<JfPersonInfo> ToJellyfinPeople(List<Models.PersonInfo> src)
    {
        var people = new List<JfPersonInfo>(src.Count);
        foreach (var p in src)
        {
            people.Add(new JfPersonInfo
            {
                Name = p.Name,
                Type = ParseKind(p.Type),
                Role = p.Role,
                SortOrder = p.SortOrder,
            });
        }
        return people;
    }

    private static PersonKind ParseKind(string? type) =>
        Enum.TryParse<PersonKind>(type, true, out var k) ? k : PersonKind.Actor;

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, out var d) ? d.ToUniversalTime() : null;

    public async Task<LibraryScanResult> ImportTvAsync(
        string libraryName, List<string> libraryPaths,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = new LibraryScanResult { LibraryName = libraryName };
        var folderByPath = ResolvePhysicalFolders(libraryPaths);
        var shows = DiskScanner.ScanTvShows(libraryPaths);
        _logger.LogInformation("Found {Count} TV shows in '{Library}'", shows.Count, libraryName);

        for (var i = 0; i < shows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)i / Math.Max(1, shows.Count) * 100);

            var (showDir, nfoFile) = shows[i];
            var folderName = Path.GetFileName(showDir);

            try
            {
                var basePath = libraryPaths.FirstOrDefault(p => showDir.StartsWith(p, StringComparison.Ordinal));
                if (basePath is null || !folderByPath.TryGetValue(basePath, out var parent)) { result.Skipped++; continue; }

                var meta = await ResolveTvMetadataAsync(showDir, folderName, nfoFile).ConfigureAwait(false)
                    ?? new TvShowMetadata
                    {
                        Title = DiskScanner.ExtractTitle(folderName),
                        Year = DiskScanner.ExtractYear(folderName),
                        TvdbId = DiskScanner.ExtractTvdbId(folderName),
                    };

                // SERIES — must exist before its seasons can be parented under it
                var seriesId = _lib.GetNewItemId(showDir, typeof(Series));
                var existingSeries = _lib.GetItemById<Series>(seriesId);
                var series = existingSeries ?? new Series { Id = seriesId };
                PopulateSeries(series, showDir, meta, parent);
                await PersistAsync(series, existingSeries is null, parent, ct).ConfigureAwait(false);
                if (existingSeries is null) result.Added++; else result.Updated++;

                if (meta.People.Count > 0 && !_dryRun)
                {
                    try { await _lib.UpdatePeopleAsync(series, ToJellyfinPeople(meta.People), ct).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "People update failed for series {Name}", series.Name); }
                }

                // For TVDB-only series, fetch all episodes once (the TMDB path fetches per-season below)
                var tvdbEpisodes = string.IsNullOrEmpty(meta.TmdbId) && !string.IsNullOrEmpty(meta.TvdbId)
                    ? await _tvdb.GetAllEpisodesAsync(meta.TvdbId).ConfigureAwait(false)
                    : new Dictionary<(int, int), EpisodeMetadata>();

                // SEASONS + EPISODES
                foreach (var (seasonDir, seasonNum) in DiskScanner.ScanSeasons(showDir))
                {
                    ct.ThrowIfCancellationRequested();
                    var seasonId = _lib.GetNewItemId(seasonDir, typeof(Season));
                    var existingSeason = _lib.GetItemById<Season>(seasonId);
                    var season = existingSeason ?? new Season { Id = seasonId };
                    PopulateSeason(season, seasonDir, seasonNum, series, meta.Title);
                    await PersistAsync(season, existingSeason is null, series, ct).ConfigureAwait(false);

                    // one TMDB call per season returns all episode titles/overviews
                    var epMetaMap = !string.IsNullOrEmpty(meta.TmdbId)
                        ? await _tmdb.GetSeasonEpisodesAsync(meta.TmdbId, seasonNum).ConfigureAwait(false)
                        : new Dictionary<int, EpisodeMetadata>();

                    var newEps = new List<BaseItem>();
                    var updEps = new List<BaseItem>();
                    foreach (var (epPath, s, e, baseName) in DiskScanner.ScanEpisodes(seasonDir, seasonNum))
                    {
                        var epId = _lib.GetNewItemId(epPath, typeof(Episode));
                        var existingEp = _lib.GetItemById<Episode>(epId);
                        var ep = existingEp ?? new Episode { Id = epId };
                        var epNfo = Path.ChangeExtension(epPath, ".nfo");
                        EpisodeMetadata? epMeta = File.Exists(epNfo) ? NfoParser.ParseEpisodeNfo(epNfo) : null;
                        if (epMeta is null && epMetaMap.TryGetValue(e, out var tmdbEp)) epMeta = tmdbEp;
                        if (epMeta is null && tvdbEpisodes.TryGetValue((s, e), out var tvdbEp)) epMeta = tvdbEp;
                        PopulateEpisode(ep, epPath, s, e, baseName, seasonDir, series, season, meta.Title, epMeta);
                        if (existingEp is null) { newEps.Add(ep); result.Added++; } else { updEps.Add(ep); result.Updated++; }
                    }

                    if (!_dryRun)
                    {
                        if (newEps.Count > 0) _lib.CreateItems(newEps, season, ct);
                        if (updEps.Count > 0) await _lib.UpdateItemsAsync(updEps, season, ItemUpdateType.MetadataImport, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing TV show: {Folder}", folderName);
                result.Failed++;
                result.Errors.Add($"{folderName}: {ex.Message}");
            }
        }

        if (!_dryRun && _config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.PurgeMissing == true)
            result.Purged = PurgeMissingTv(folderByPath.Values, ct);

        progress?.Report(100);
        return result;
    }

    private Dictionary<string, Folder> ResolvePhysicalFolders(List<string> libraryPaths)
    {
        // Items must be parented under the library's PHYSICAL folder (the one in the
        // CollectionFolder's PhysicalFolderIds), not the CollectionFolder itself, or
        // they won't surface in browse.
        var map = new Dictionary<string, Folder>();
        foreach (var p in libraryPaths)
        {
            if (_lib.FindByPath(p, true) is Folder f) map[p] = f;
            else _logger.LogWarning("No physical folder item for path '{Path}' — its items are skipped", p);
        }
        return map;
    }

    private async Task PersistAsync(BaseItem item, bool isNew, BaseItem parent, CancellationToken ct)
    {
        if (_dryRun) return;
        if (isNew) _lib.CreateItems([item], parent, ct);
        else await _lib.UpdateItemsAsync([item], parent, ItemUpdateType.MetadataImport, ct).ConfigureAwait(false);
    }

    private void PopulateSeries(Series series, string showDir, TvShowMetadata meta, Folder parent)
    {
        series.ParentId = parent.Id;
        series.Path = showDir;
        if (!string.IsNullOrEmpty(meta.Title)) series.Name = meta.Title;
        series.Overview = meta.Overview ?? series.Overview;
        series.ProductionYear = meta.Year ?? series.ProductionYear;
        series.PremiereDate = ParseDate(meta.PremiereDate) ?? series.PremiereDate;
        series.CommunityRating = meta.CommunityRating ?? series.CommunityRating;
        if (!string.IsNullOrEmpty(meta.OfficialRating)) series.OfficialRating = meta.OfficialRating;
        if (meta.Genres.Count > 0) series.Genres = meta.Genres.ToArray();
        if (meta.Studios.Count > 0) series.SetStudios(meta.Studios);
        if (!string.IsNullOrEmpty(meta.TvdbId)) series.ProviderIds["Tvdb"] = meta.TvdbId;
        if (!string.IsNullOrEmpty(meta.TmdbId)) series.ProviderIds["Tmdb"] = meta.TmdbId;
        if (!string.IsNullOrEmpty(meta.ImdbId)) series.ProviderIds["Imdb"] = meta.ImdbId;
        if (!string.IsNullOrEmpty(meta.Status))
            series.Status = meta.Status.Contains("end", StringComparison.OrdinalIgnoreCase)
                ? SeriesStatus.Ended : SeriesStatus.Continuing;

        foreach (var (imageType, path) in DiskScanner.FindImages(showDir))
            SetImageIfMissing(series, imageType, path);

        series.IsLocked = true;
        if (string.IsNullOrEmpty(series.PresentationUniqueKey))
            series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
    }

    private void PopulateSeason(Season season, string seasonDir, int seasonNum, Series series, string seriesName)
    {
        season.ParentId = series.Id;
        season.Path = seasonDir;
        season.IndexNumber = seasonNum;
        season.SeriesId = series.Id;
        season.SeriesName = seriesName;
        season.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
        if (string.IsNullOrEmpty(season.Name))
            season.Name = seasonNum == 0 ? "Specials" : $"Season {seasonNum}";

        foreach (var (imageType, path) in DiskScanner.FindImages(seasonDir))
            SetImageIfMissing(season, imageType, path);

        season.IsLocked = true;
        if (string.IsNullOrEmpty(season.PresentationUniqueKey))
            season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
    }

    private void PopulateEpisode(Episode ep, string epPath, int seasonNum, int episodeNum,
        string baseName, string seasonDir, Series series, Season season, string seriesName, EpisodeMetadata? m)
    {
        ep.ParentId = season.Id;
        ep.Path = epPath;
        ep.SeriesId = series.Id;
        ep.SeriesName = seriesName;
        ep.SeasonId = season.Id;
        ep.SeasonName = season.Name;
        ep.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
        ep.ParentIndexNumber = seasonNum;
        ep.IndexNumber = episodeNum;
        if (!string.IsNullOrEmpty(m?.Title)) ep.Name = m!.Title;
        else if (string.IsNullOrEmpty(ep.Name)) ep.Name = $"Episode {episodeNum}";
        if (m?.Overview is not null) ep.Overview = m.Overview;
        if (m?.CommunityRating is not null) ep.CommunityRating = m.CommunityRating;
        if (m?.RunTimeTicks is not null) ep.RunTimeTicks = m.RunTimeTicks;
        if (!string.IsNullOrEmpty(m?.TvdbId)) ep.ProviderIds["Tvdb"] = m.TvdbId;

        var thumb = Path.Combine(seasonDir, baseName + "-thumb.jpg");
        if (File.Exists(thumb)) SetImageIfMissing(ep, 0, thumb);

        ep.IsLocked = true;
        if (string.IsNullOrEmpty(ep.PresentationUniqueKey))
            ep.PresentationUniqueKey = ep.CreatePresentationUniqueKey();
    }

    private int PurgeMissingTv(IEnumerable<Folder> parents, CancellationToken ct)
    {
        var purged = 0;
        foreach (var parent in parents)
        {
            var items = _lib.GetItemList(new InternalItemsQuery
            {
                Parent = parent,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Season, BaseItemKind.Series],
            });
            // delete deepest-first: episodes, then empty seasons/series
            foreach (var item in items.OrderByDescending(it => it is Episode ? 2 : it is Season ? 1 : 0))
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(item.Path) && !File.Exists(item.Path) && !Directory.Exists(item.Path))
                {
                    _lib.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                    purged++;
                }
            }
        }
        return purged;
    }

    private async Task<TvShowMetadata?> ResolveTvMetadataAsync(string showDir, string folderName, string? nfoFile)
    {
        TvShowMetadata? meta = null;
        if (nfoFile is not null)
            meta = NfoParser.ParseTvShowNfo(nfoFile);

        var tvdbId = meta?.TvdbId ?? DiskScanner.ExtractTvdbId(folderName);
        var tmdbId = meta?.TmdbId ?? DiskScanner.ExtractTmdbId(folderName);
        var title = meta?.Title ?? DiskScanner.ExtractTitle(folderName);
        var year = meta?.Year ?? DiskScanner.ExtractYear(folderName);

        if (!string.IsNullOrEmpty(tvdbId))
        {
            var tvdbMeta = await _tvdb.GetSeriesAsync(tvdbId).ConfigureAwait(false);
            if (tvdbMeta is not null) return MergeTvMetadata(meta, tvdbMeta);
        }

        if (!string.IsNullOrEmpty(tmdbId))
        {
            var tmdbMeta = await _tmdb.GetTvShowAsync(tmdbId).ConfigureAwait(false);
            if (tmdbMeta is not null) return MergeTvMetadata(meta, tmdbMeta);
        }

        if (!string.IsNullOrEmpty(title))
        {
            var (searchId, mediaType) = await _tmdb.SearchAsync(title, year).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(searchId) && mediaType == "tv")
            {
                var tmdbMeta = await _tmdb.GetTvShowAsync(searchId).ConfigureAwait(false);
                if (tmdbMeta is not null) return MergeTvMetadata(meta, tmdbMeta);
            }
        }

        return meta;
    }

    private static TvShowMetadata MergeTvMetadata(TvShowMetadata? nfo, TvShowMetadata api)
    {
        if (nfo is null) return api;
        nfo.Title = !string.IsNullOrEmpty(nfo.Title) ? nfo.Title : api.Title;
        nfo.Overview ??= api.Overview;
        nfo.CommunityRating ??= api.CommunityRating;
        nfo.OfficialRating ??= api.OfficialRating;
        nfo.PremiereDate ??= api.PremiereDate;
        nfo.Year ??= api.Year;
        nfo.TmdbId ??= api.TmdbId;
        nfo.TvdbId ??= api.TvdbId;
        nfo.ImdbId ??= api.ImdbId;
        nfo.Status ??= api.Status;
        if (nfo.Genres.Count == 0) nfo.Genres = api.Genres;
        if (nfo.Studios.Count == 0) nfo.Studios = api.Studios;
        if (nfo.People.Count == 0) nfo.People = api.People;
        return nfo;
    }

    private async Task<MovieMetadata?> ResolveMovieMetadataAsync(string folderPath, string folderName, string? nfoFile)
    {
        MovieMetadata? meta = null;
        if (nfoFile is not null)
            meta = NfoParser.ParseMovieNfo(nfoFile);

        var tmdbId = meta?.TmdbId ?? DiskScanner.ExtractTmdbId(folderName);
        var year = meta?.Year ?? DiskScanner.ExtractYear(folderName);
        var title = meta?.Title ?? DiskScanner.ExtractTitle(folderName);

        if (!string.IsNullOrEmpty(tmdbId))
        {
            var tmdbMeta = await _tmdb.GetMovieAsync(tmdbId).ConfigureAwait(false);
            if (tmdbMeta is not null)
                return MergeMetadata(meta, tmdbMeta);
        }

        if (!string.IsNullOrEmpty(title))
        {
            var (searchId, mediaType) = await _tmdb.SearchAsync(title, year).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(searchId) && mediaType == "movie")
            {
                var tmdbMeta = await _tmdb.GetMovieAsync(searchId).ConfigureAwait(false);
                if (tmdbMeta is not null)
                    return MergeMetadata(meta, tmdbMeta);
            }
        }

        if (!string.IsNullOrEmpty(title))
        {
            var (tvdbId, _) = await _tvdb.SearchAsync(title, year).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(tvdbId))
            {
                var tvdbMeta = await _tvdb.GetMovieAsync(tvdbId).ConfigureAwait(false);
                if (tvdbMeta is not null)
                {
                    meta ??= new MovieMetadata();
                    meta.Title = tvdbMeta.Title;
                    meta.TvdbId = tvdbMeta.TvdbId;
                    meta.Overview ??= tvdbMeta.Overview;
                    meta.Year ??= tvdbMeta.Year;
                    meta.Genres = tvdbMeta.Genres.Count > 0 ? tvdbMeta.Genres : meta.Genres;
                    meta.People = tvdbMeta.People.Count > 0 ? tvdbMeta.People : meta.People;
                    meta.FromTvdb = true;
                    return meta;
                }
            }
        }

        return meta;
    }

    private static MovieMetadata MergeMetadata(MovieMetadata? nfo, MovieMetadata api)
    {
        if (nfo is null) return api;
        nfo.Title = !string.IsNullOrEmpty(nfo.Title) ? nfo.Title : api.Title;
        nfo.Overview ??= api.Overview;
        nfo.Tagline ??= api.Tagline;
        nfo.CommunityRating ??= api.CommunityRating;
        nfo.OfficialRating ??= api.OfficialRating;
        nfo.RunTimeTicks ??= api.RunTimeTicks;
        nfo.PremiereDate ??= api.PremiereDate;
        nfo.Year ??= api.Year;
        nfo.ProductionYear ??= api.ProductionYear;
        nfo.TmdbId ??= api.TmdbId;
        nfo.TvdbId ??= api.TvdbId;
        nfo.ImdbId ??= api.ImdbId;
        nfo.PosterUrl ??= api.PosterUrl;
        nfo.BackdropUrl ??= api.BackdropUrl;
        if (nfo.Genres.Count == 0) nfo.Genres = api.Genres;
        if (nfo.Studios.Count == 0) nfo.Studios = api.Studios;
        if (nfo.People.Count == 0) nfo.People = api.People;
        nfo.FromTmdb = api.FromTmdb;
        return nfo;
    }

    private void ApplyOverride(MovieMetadata meta)
    {
        if (meta.FolderPath is null) return;
        var overrideEntry = _config.Overrides.FirstOrDefault(o =>
            meta.FolderPath.Contains(o.PathPattern, StringComparison.OrdinalIgnoreCase));
        if (overrideEntry is null) return;

        _logger.LogInformation("Applying override for {Path}", meta.FolderPath);
        if (!string.IsNullOrEmpty(overrideEntry.Title)) meta.Title = overrideEntry.Title;
        if (!string.IsNullOrEmpty(overrideEntry.SortName)) meta.SortName = overrideEntry.SortName;
        if (!string.IsNullOrEmpty(overrideEntry.OriginalTitle)) meta.OriginalTitle = overrideEntry.OriginalTitle;
        if (overrideEntry.Year.HasValue) { meta.Year = overrideEntry.Year; meta.ProductionYear = overrideEntry.Year; }
        if (!string.IsNullOrEmpty(overrideEntry.PremiereDate)) meta.PremiereDate = overrideEntry.PremiereDate;
        if (overrideEntry.RuntimeMinutes.HasValue) meta.RunTimeTicks = overrideEntry.RuntimeMinutes.Value * 600_000_000L;
        if (!string.IsNullOrEmpty(overrideEntry.TmdbId)) meta.TmdbId = overrideEntry.TmdbId;
        if (!string.IsNullOrEmpty(overrideEntry.TvdbId)) meta.TvdbId = overrideEntry.TvdbId;
        if (!string.IsNullOrEmpty(overrideEntry.ImdbId)) meta.ImdbId = overrideEntry.ImdbId;
        if (!string.IsNullOrEmpty(overrideEntry.Overview)) meta.Overview = overrideEntry.Overview;
        if (!string.IsNullOrEmpty(overrideEntry.OfficialRating)) meta.OfficialRating = overrideEntry.OfficialRating;
        if (overrideEntry.CommunityRating.HasValue) meta.CommunityRating = overrideEntry.CommunityRating;
        if (overrideEntry.Genres.Count > 0) meta.Genres = overrideEntry.Genres;
        if (overrideEntry.Studios.Count > 0) meta.Studios = overrideEntry.Studios;
        if (overrideEntry.People.Count > 0) meta.People = overrideEntry.People;
        meta.FromOverride = true;
    }
}
