using Jellyfin.Plugin.LibraryImporter.Configuration;
using Jellyfin.Plugin.LibraryImporter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

public class ImportEngine
{
    private readonly DatabaseUpdater _db;
    private readonly TmdbClient _tmdb;
    private readonly TvdbClient _tvdb;
    private readonly ILogger _logger;
    private readonly PluginConfiguration _config;
    private readonly bool _dryRun;

    public ImportEngine(DatabaseUpdater db, TmdbClient tmdb, TvdbClient tvdb,
        PluginConfiguration config, ILogger logger)
    {
        _db = db;
        _tmdb = tmdb;
        _tvdb = tvdb;
        _config = config;
        _logger = logger;
        _dryRun = config.DryRun;
    }

    public async Task<LibraryScanResult> ImportMoviesAsync(
        string libraryName, List<string> libraryPaths, string physicalRootId,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = new LibraryScanResult { LibraryName = libraryName };
        var movies = DiskScanner.ScanMovies(libraryPaths);
        _logger.LogInformation("Found {Count} movie folders in '{Library}'", movies.Count, libraryName);

        for (var i = 0; i < movies.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)i / movies.Count * 100);

            var (folderPath, videoFile, nfoFile) = movies[i];
            var folderName = Path.GetFileName(folderPath);

            try
            {
                var meta = await ResolveMovieMetadataAsync(folderPath, folderName, nfoFile).ConfigureAwait(false);
                if (meta is null)
                {
                    result.Skipped++;
                    continue;
                }

                meta.FolderPath = folderPath;
                meta.VideoPath = videoFile;

                ApplyOverride(meta);

                var itemPath = videoFile ?? folderPath;
                var itemId = IdGenerator.CreateString("Movie", itemPath);
                var now = DateTime.UtcNow.ToString("o");

                if (!_dryRun)
                {
                    _db.UpsertMovie(itemId, itemPath, physicalRootId, meta, now);

                    var images = DiskScanner.FindImages(folderPath);
                    if (images.Count > 0)
                        _db.InsertImages(itemId, images, now);

                    if (!string.IsNullOrEmpty(meta.TmdbId) && !images.Any(im => im.imageType == 0))
                    {
                        var posterPath = await _tmdb.DownloadPosterAsync(meta.TmdbId, folderPath).ConfigureAwait(false);
                        if (posterPath is not null)
                        {
                            _db.InsertImages(itemId, [(0, posterPath)], now);
                            result.PostersDownloaded++;
                        }
                    }
                }

                if (_db.GetExistingItem(itemId) is not null)
                    result.Updated++;
                else
                    result.Added++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing movie: {Folder}", folderName);
                result.Failed++;
                result.Errors.Add($"{folderName}: {ex.Message}");
            }
        }

        if (_config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.PurgeMissing == true)
        {
            result.Purged = _db.PurgeMissingMovies(libraryPaths, _dryRun);
        }

        progress?.Report(100);
        return result;
    }

    public async Task<LibraryScanResult> ImportTvAsync(
        string libraryName, List<string> libraryPaths, string physicalRootId,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = new LibraryScanResult { LibraryName = libraryName };
        var shows = DiskScanner.ScanTvShows(libraryPaths);
        _logger.LogInformation("Found {Count} TV shows in '{Library}'", shows.Count, libraryName);

        for (var i = 0; i < shows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)i / shows.Count * 100);

            var (showDir, nfoFile) = shows[i];
            var folderName = Path.GetFileName(showDir);

            try
            {
                var seriesId = IdGenerator.CreateString("Series", showDir);
                var now = DateTime.UtcNow.ToString("o");

                var meta = await ResolveTvMetadataAsync(showDir, folderName, nfoFile).ConfigureAwait(false);
                if (meta is null)
                {
                    meta = new TvShowMetadata
                    {
                        Title = DiskScanner.ExtractTitle(folderName),
                        Year = DiskScanner.ExtractYear(folderName),
                        TvdbId = DiskScanner.ExtractTvdbId(folderName),
                    };
                }

                if (!_dryRun)
                {
                    _db.UpsertSeries(seriesId, showDir, physicalRootId, meta, now);

                    var images = DiskScanner.FindImages(showDir);
                    if (images.Count > 0)
                        _db.InsertImages(seriesId, images, now);
                }

                var isNew = _db.GetExistingItem(seriesId) is null;
                if (isNew) result.Added++; else result.Updated++;

                // Process seasons and episodes
                var seasons = DiskScanner.ScanSeasons(showDir);
                foreach (var (seasonDir, seasonNum) in seasons)
                {
                    var seasonId = IdGenerator.CreateString("Season", seasonDir);
                    if (!_dryRun)
                    {
                        _db.UpsertSeason(seasonId, seasonDir, seriesId, seasonNum, meta.Title, now);

                        var seasonImages = FindSeasonImages(showDir, seasonNum);
                        if (seasonImages.Count > 0)
                            _db.InsertImages(seasonId, seasonImages, now);
                    }

                    var episodes = DiskScanner.ScanEpisodes(seasonDir, seasonNum);
                    foreach (var (epPath, s, e, baseName) in episodes)
                    {
                        ct.ThrowIfCancellationRequested();
                        var epId = IdGenerator.CreateString("Episode", epPath);

                        // Try to parse episode NFO
                        var epNfo = Path.ChangeExtension(epPath, ".nfo");
                        var epMeta = File.Exists(epNfo) ? NfoParser.ParseEpisodeNfo(epNfo) : null;

                        if (!_dryRun)
                        {
                            _db.UpsertEpisode(epId, epPath, seasonId, seriesId, s, e, meta.Title, epMeta, now);

                            var epImages = FindEpisodeImages(seasonDir, baseName);
                            if (epImages.Count > 0)
                                _db.InsertImages(epId, epImages, now);
                        }
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

        if (_config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.PurgeMissing == true)
        {
            result.Purged = _db.PurgeMissingTv(libraryPaths, _dryRun);
        }

        progress?.Report(100);
        return result;
    }

    private async Task<MovieMetadata?> ResolveMovieMetadataAsync(string folderPath, string folderName, string? nfoFile)
    {
        // 1. Try NFO
        MovieMetadata? meta = null;
        if (nfoFile is not null)
            meta = NfoParser.ParseMovieNfo(nfoFile);

        // 2. Extract IDs from folder name
        var tmdbId = meta?.TmdbId ?? DiskScanner.ExtractTmdbId(folderName);
        var year = meta?.Year ?? DiskScanner.ExtractYear(folderName);
        var title = meta?.Title ?? DiskScanner.ExtractTitle(folderName);

        // 3. TMDB lookup by ID
        if (!string.IsNullOrEmpty(tmdbId))
        {
            var tmdbMeta = await _tmdb.GetMovieAsync(tmdbId).ConfigureAwait(false);
            if (tmdbMeta is not null)
                return MergeMetadata(meta, tmdbMeta);
        }

        // 4. TMDB search
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

        // 5. TVDB fallback
        if (!string.IsNullOrEmpty(title))
        {
            var (tvdbId, _) = await _tvdb.SearchAsync(title, year).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(tvdbId))
            {
                var tvdbMeta = await _tvdb.GetMovieAsync(tvdbId).ConfigureAwait(false);
                if (tvdbMeta is not null)
                {
                    // Convert TVDB MovieMetadata
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
            if (tvdbMeta is not null)
                return MergeTvMetadata(meta, tvdbMeta);
        }

        if (!string.IsNullOrEmpty(tmdbId))
        {
            var tmdbMeta = await _tmdb.GetTvShowAsync(tmdbId).ConfigureAwait(false);
            if (tmdbMeta is not null)
                return MergeTvMetadata(meta, tmdbMeta);
        }

        if (!string.IsNullOrEmpty(title))
        {
            var (searchId, mediaType) = await _tmdb.SearchAsync(title, year).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(searchId) && mediaType == "tv")
            {
                var tmdbMeta = await _tmdb.GetTvShowAsync(searchId).ConfigureAwait(false);
                if (tmdbMeta is not null)
                    return MergeTvMetadata(meta, tmdbMeta);
            }
        }

        return meta;
    }

    private static MovieMetadata MergeMetadata(MovieMetadata? nfo, MovieMetadata api)
    {
        if (nfo is null) return api;
        // NFO takes priority for user-edited fields, API fills gaps
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

    private void ApplyOverride(MovieMetadata meta)
    {
        if (meta.FolderPath is null) return;
        var overrideEntry = _config.Overrides.FirstOrDefault(o =>
            meta.FolderPath.Contains(o.PathPattern, StringComparison.OrdinalIgnoreCase));
        if (overrideEntry is null) return;

        _logger.LogInformation("Applying override for {Path}", meta.FolderPath);
        if (!string.IsNullOrEmpty(overrideEntry.Title)) meta.Title = overrideEntry.Title;
        if (overrideEntry.Year.HasValue) meta.Year = overrideEntry.Year;
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

    private static List<(int, string)> FindSeasonImages(string showDir, int seasonNum)
    {
        var images = new List<(int, string)>();
        var prefix = seasonNum == 0 ? "season-specials" : $"season{seasonNum:D2}";
        var posterPath = Path.Combine(showDir, $"{prefix}-poster.jpg");
        if (File.Exists(posterPath)) images.Add((0, posterPath));
        var bannerPath = Path.Combine(showDir, $"{prefix}-banner.jpg");
        if (File.Exists(bannerPath)) images.Add((3, bannerPath));
        return images;
    }

    private static List<(int, string)> FindEpisodeImages(string seasonDir, string baseName)
    {
        var images = new List<(int, string)>();
        var thumbPath = Path.Combine(seasonDir, $"{baseName}-thumb.jpg");
        if (File.Exists(thumbPath)) images.Add((0, thumbPath));
        return images;
    }
}
