using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LibraryImporter.Configuration;
using Jellyfin.Plugin.LibraryImporter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
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
    private readonly IItemRepository _repo;
    private readonly TmdbClient _tmdb;
    private readonly TvdbClient _tvdb;
    private readonly ILogger _logger;
    private readonly PluginConfiguration _config;
    private readonly bool _dryRun;

    public ImportEngine(ILibraryManager lib, IItemRepository repo, TmdbClient tmdb, TvdbClient tvdb,
        PluginConfiguration config, ILogger logger)
    {
        _lib = lib;
        _repo = repo;
        _tmdb = tmdb;
        _tvdb = tvdb;
        _config = config;
        _logger = logger;
        _dryRun = config.DryRun;
    }

    /// <summary>
    /// Persists NEW items exactly as ILibraryManager.CreateItems does — repository save
    /// (rows + ancestor wiring), cache registration, parent child-cache invalidation —
    /// but WITHOUT raising the ItemAdded event. Library events run every subscribed
    /// plugin's handler synchronously per item (TVDB's missing-episode provider, tag
    /// caches, ...), each often issuing its own DB queries; on a bulk import that event
    /// storm dwarfs the import itself. Quiet persistence keeps bulk imports fast no
    /// matter which other plugins are installed. Other plugins catch up on their own
    /// scheduled scans instead of per-item callbacks.
    /// </summary>
    private void CreateItemsQuiet(IReadOnlyList<BaseItem> items, BaseItem parent, CancellationToken ct)
    {
        _repo.SaveItems(items, ct);
        foreach (var item in items)
            _lib.RegisterItem(item);
        InvalidateChildCache(parent);
    }

    /// <summary>Quiet counterpart of ILibraryManager.UpdateItemsAsync (see <see cref="CreateItemsQuiet"/>).</summary>
    private async Task UpdateItemsQuietAsync(IReadOnlyList<BaseItem> items, BaseItem parent, CancellationToken ct)
    {
        foreach (var item in items)
        {
            item.DateLastSaved = DateTime.UtcNow;
            await _lib.RunMetadataSavers(item, ItemUpdateType.MetadataImport).ConfigureAwait(false);
        }

        _repo.SaveItems(items, ct);
        InvalidateChildCache(parent);
    }

    private static void InvalidateChildCache(BaseItem parent)
    {
        // Mirrors LibraryManager.CreateItems/UpdateItemsAsync: force the parent folder to
        // re-query children/user data instead of serving its stale in-memory cache.
        if (parent is Folder folder)
        {
            folder.Children = null!;
            folder.UserData = null!;
        }
    }

    public async Task<LibraryScanResult> ImportMoviesAsync(
        string libraryName, List<string> libraryPaths,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = new LibraryScanResult { LibraryName = libraryName };
        var force = _config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.ForceReimport == true;
        if (force) _logger.LogInformation("Force re-import enabled for '{Library}' — skip fast-path bypassed this run", libraryName);

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

                var itemPath = videoFile ?? folderPath;
                var itemId = _lib.GetNewItemId(itemPath, typeof(Movie));
                var existing = _lib.GetItemById<Movie>(itemId);

                // Fast path: already imported with real metadata and a poster — skip the
                // TMDB/TVDB calls and writes entirely. Movies matching a configured override
                // are never skipped, so override edits always propagate on the next run.
                if (!force && existing is not null && !HasOverride(folderPath) && IsMovieFullyImported(existing))
                {
                    result.Skipped++;
                    continue;
                }

                _logger.LogInformation("Movie {Index}/{Count}: importing {Folder}", i + 1, movies.Count, folderName);

                var meta = await ResolveMovieMetadataAsync(folderPath, folderName, nfoFile).ConfigureAwait(false);
                if (meta is null) { result.Skipped++; continue; }

                meta.FolderPath = folderPath;
                meta.VideoPath = videoFile;
                ApplyOverride(meta);

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
                CreateItemsQuiet(items, parent, ct);
            foreach (var (_, (parent, items)) in updateByParent)
                await UpdateItemsQuietAsync(items, parent, ct).ConfigureAwait(false);

            foreach (var (item, people) in pendingPeople)
            {
                ct.ThrowIfCancellationRequested();
                try { await _lib.UpdatePeopleAsync(item, people, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "People update failed for {Name}", item.Name); }
            }

            if (_config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.PurgeMissing == true)
                result.Purged = PurgeMissing(folderByPath.Values, [BaseItemKind.Movie], ct);
        }

        progress?.Report(100);
        return result;
    }

    /// <summary>
    /// Shared purge for ALL library types: removes items under each parent whose
    /// on-disk path no longer exists (file or directory — folder-backed items
    /// like Series/Season/author dirs check as directories). Deletes deepest-
    /// first in batches via core's bulk DeleteItemsUnsafeFast (single repository
    /// delete per batch, no per-item event storm — scales to tens of thousands),
    /// evicts deleted ids from LibraryManager's memory cache, and clears
    /// memoized folder children so the UI reflects the clean DB immediately,
    /// no restart needed. Mixed-folder leaf items take the per-item core path
    /// (their delete-path computation enumerates sibling files).
    /// </summary>
    private int PurgeMissing(IEnumerable<Folder> parents, BaseItemKind[] kinds, CancellationToken ct)
    {
        var purged = 0;
        foreach (var parent in parents)
        {
            var stale = _lib.GetItemList(new InternalItemsQuery
                {
                    Parent = parent,
                    Recursive = true,
                    IncludeItemTypes = kinds,
                })
                .Where(i => !string.IsNullOrEmpty(i.Path) && !File.Exists(i.Path) && !Directory.Exists(i.Path))
                .OrderByDescending(DepthRank)
                .ToList();

            if (stale.Count == 0) continue;
            _logger.LogInformation("Purge: {Count} stale items under '{Parent}'", stale.Count, parent.Name);

            const int batchSize = 200;
            for (var i = 0; i < stale.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = stale.Skip(i).Take(batchSize).ToList();

                var fast = chunk.Where(c => c.IsFolder || !c.IsInMixedFolder).ToList();
                if (fast.Count > 0)
                    _lib.DeleteItemsUnsafeFast(fast);

                foreach (var mixed in chunk.Where(c => !c.IsFolder && c.IsInMixedFolder))
                    _lib.DeleteItem(mixed, new DeleteOptions { DeleteFileLocation = false });

                EvictFromLibraryCache(fast.Select(c => c.Id));

                purged += chunk.Count;
                _logger.LogInformation("Purge: {Done}/{Total} stale items removed", Math.Min(i + batchSize, stale.Count), stale.Count);
            }

            // Remaining containers memoize their Children lists — clear them so
            // the next browse reloads from the now-clean database.
            InvalidateChildCache(parent);
            foreach (var container in _lib.GetItemList(new InternalItemsQuery
                     {
                         Parent = parent,
                         Recursive = true,
                         IncludeItemTypes = kinds.Append(BaseItemKind.Folder).Distinct().ToArray(),
                     }).Where(x => x.IsFolder))
            {
                InvalidateChildCache(container);
            }
        }

        return purged;
    }

    /// <summary>Deepest-first delete order: leaf media, then seasons, series, plain folders.</summary>
    private static int DepthRank(BaseItem item) => item switch
    {
        Episode => 3,
        Season => 2,
        Series => 1,
        _ => item.IsFolder ? 0 : 3,
    };

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
        var force = _config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.ForceReimport == true;
        if (force) _logger.LogInformation("Force re-import enabled for '{Library}' — skip fast-path bypassed this run", libraryName);
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

                // Fast path: every on-disk episode of this show is already in the DB with real
                // metadata — nothing to do. Skipping here avoids the TVDB/TMDB calls and DB
                // writes entirely, which is what keeps nightly re-runs fast on large libraries.
                var seriesId = _lib.GetNewItemId(showDir, typeof(Series));
                var existingSeries = _lib.GetItemById<Series>(seriesId);
                if (!force && existingSeries is not null && IsShowFullyImported(existingSeries, showDir))
                {
                    result.Skipped++;
                    continue;
                }

                _logger.LogInformation("TV {Index}/{Count}: importing {Folder}", i + 1, shows.Count, folderName);

                var meta = await ResolveTvMetadataAsync(showDir, folderName, nfoFile).ConfigureAwait(false)
                    ?? new TvShowMetadata
                    {
                        Title = DiskScanner.ExtractTitle(folderName),
                        Year = DiskScanner.ExtractYear(folderName),
                        TvdbId = DiskScanner.ExtractTvdbId(folderName),
                    };

                // SERIES — must exist before its seasons can be parented under it
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
                        if (newEps.Count > 0) CreateItemsQuiet(newEps, season, ct);
                        if (updEps.Count > 0) await UpdateItemsQuietAsync(updEps, season, ct).ConfigureAwait(false);
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
            result.Purged = PurgeMissing(folderByPath.Values, [BaseItemKind.Episode, BaseItemKind.Season, BaseItemKind.Series], ct);

        progress?.Report(100);
        return result;
    }

    public async Task<LibraryScanResult> ImportBooksAsync(
        string libraryName, List<string> libraryPaths, AudiobookshelfClient? abs,
        AudnexusClient audnexus, OpenLibraryClient openLibrary,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = new LibraryScanResult { LibraryName = libraryName };
        var force = _config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.ForceReimport == true;
        if (force) _logger.LogInformation("Force re-import enabled for '{Library}' — skip fast-path bypassed this run", libraryName);
        var folderByPath = ResolvePhysicalFolders(libraryPaths);
        var books = DiskScanner.ScanBooks(libraryPaths);
        _logger.LogInformation("Found {Count} books in '{Library}'", books.Count, libraryName);

        // Jellyfin's books UI is folder-driven: authors/series are plain Folder items and a
        // multi-file audiobook is one Folder containing its AudioBook parts. Mirror the
        // directory tree so books group correctly instead of listing every part flat.
        var folderCache = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < books.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)i / Math.Max(1, books.Count) * 100);
            var book = books[i];

            try
            {
                var basePath = libraryPaths.FirstOrDefault(p => book.Dir.StartsWith(p, StringComparison.Ordinal));
                if (basePath is null || !folderByPath.TryGetValue(basePath, out var parent)) { result.Skipped++; continue; }

                var isAudio = book.AudioFiles.Count > 0;
                // audiobooks: one AudioBook item per audio file inside the book's Folder;
                // ebooks: one Book item for the primary file (folder-consumed, like core)
                var files = isAudio ? OrderAudioFiles(book) : book.EbookFiles.Take(1).ToList();
                if (files.Count == 0) { result.Skipped++; continue; }

                if (!force && AreBookItemsImported(book, files, isAudio))
                {
                    result.Skipped++;
                    continue;
                }

                // ancestor Folder items for author/series dirs between library root and book
                BaseItem container = parent;
                var relSegments = Path.GetRelativePath(basePath, book.Dir)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var walk = basePath;
                for (var s = 0; s < relSegments.Length - 1; s++)
                {
                    walk = Path.Combine(walk, relSegments[s]);
                    container = EnsureFolderQuiet(walk, container, relSegments[s], null, folderCache, ct);
                }

                // audiobooks live INSIDE a folder named after the book carrying the cover art
                if (isAudio)
                    container = EnsureFolderQuiet(book.Dir, container, book.CleanName, book.CoverFile, folderCache, ct);

                if (files.Count > 500)
                    _logger.LogWarning(
                        "Book folder has {Count} audio files — likely a misfiled dump, importing anyway: {Dir}",
                        files.Count, book.Dir);

                _logger.LogInformation("Book {Index}/{Count}: importing {Author}/{Name}",
                    i + 1, books.Count, book.Author, book.CleanName);

                var meta = await ResolveBookMetadataAsync(book, isAudio, abs, audnexus, openLibrary).ConfigureAwait(false);

                var newItems = new List<BaseItem>();
                var updItems = new List<BaseItem>();
                for (var part = 0; part < files.Count; part++)
                {
                    BaseItem item;
                    BaseItem? existing;
                    if (isAudio)
                    {
                        var id = _lib.GetNewItemId(files[part], typeof(AudioBook));
                        var ex = _lib.GetItemById<AudioBook>(id);
                        var ab2 = ex ?? new AudioBook { Id = id };
                        PopulateAudioBook(ab2, files[part], part, files.Count, book, meta, container);
                        existing = ex; item = ab2;
                    }
                    else
                    {
                        var id = _lib.GetNewItemId(files[part], typeof(Book));
                        var ex = _lib.GetItemById<Book>(id);
                        var bk = ex ?? new Book { Id = id };
                        PopulateBook(bk, files[part], book, meta, container);
                        existing = ex; item = bk;
                    }

                    if (existing is null) { newItems.Add(item); result.Added++; }
                    else { updItems.Add(item); result.Updated++; }
                }

                if (!_dryRun)
                {
                    if (newItems.Count > 0) CreateItemsQuiet(newItems, container, ct);
                    if (updItems.Count > 0) await UpdateItemsQuietAsync(updItems, container, ct).ConfigureAwait(false);

                    // authors/narrators as people — first item per book is enough for search
                    var people = BookPeople(meta);
                    if (people.Count > 0 && (newItems.FirstOrDefault() ?? updItems.FirstOrDefault()) is { } first)
                    {
                        try { await _lib.UpdatePeopleAsync(first, people, ct).ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogWarning(ex, "People update failed for {Name}", first.Name); }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing book: {Folder}", book.Dir);
                result.Failed++;
                result.Errors.Add($"{book.Author}/{book.CleanName}: {ex.Message}");
            }
        }

        if (!_dryRun && _config.Libraries.FirstOrDefault(l => l.Name == libraryName)?.PurgeMissing == true)
            result.Purged = PurgeMissing(folderByPath.Values, [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.Folder], ct);

        progress?.Report(100);
        return result;
    }


    /// <summary>
    /// Removes ids from LibraryManager's private item LRU (FastConcurrentLru) —
    /// the eviction core's slow DeleteItem performs but DeleteItemsUnsafeFast
    /// skips. Reflection because the cache is not exposed on ILibraryManager.
    /// </summary>
    private void EvictFromLibraryCache(IEnumerable<Guid> ids)
    {
        try
        {
            _purgeCacheField ??= _lib.GetType().GetField(
                "_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cache = _purgeCacheField?.GetValue(_lib);
            if (cache is null) return;
            _purgeTryRemove ??= cache.GetType().GetMethod("TryRemove", [typeof(Guid)]);
            if (_purgeTryRemove is null) return;
            foreach (var id in ids)
                _purgeTryRemove.Invoke(cache, [id]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library cache eviction failed — deleted items may linger in the UI until restart");
        }
    }

    private System.Reflection.FieldInfo? _purgeCacheField;
    private System.Reflection.MethodInfo? _purgeTryRemove;

    /// <summary>Multi-file play order: playlist.ll when present, else filename sort.</summary>
    private static List<string> OrderAudioFiles(DiskScanner.BookFolder book)
    {
        if (book.PlaylistFile is null || book.AudioFiles.Count <= 1) return book.AudioFiles;
        try
        {
            var byName = book.AudioFiles.ToDictionary(f => Path.GetFileName(f)!, f => f, StringComparer.OrdinalIgnoreCase);
            var ordered = File.ReadAllLines(book.PlaylistFile)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && byName.ContainsKey(l))
                .Select(l => byName[l])
                .ToList();
            // playlist must account for every file or we fall back to name order
            return ordered.Count == book.AudioFiles.Count ? ordered : book.AudioFiles;
        }
        catch
        {
            return book.AudioFiles;
        }
    }

    /// <summary>
    /// Idempotently creates the plain Folder item for a directory (author/series/book
    /// folders) without raising library events. Book folders get the cover as their image.
    /// </summary>
    private Folder EnsureFolderQuiet(string dirPath, BaseItem parent, string displayName,
        string? coverFile, Dictionary<string, Folder> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(dirPath, out var cached)) return cached;

        var id = _lib.GetNewItemId(dirPath, typeof(Folder));
        var existing = _lib.GetItemById<Folder>(id);
        var folder = existing ?? new Folder { Id = id };
        folder.Path = dirPath;
        folder.ParentId = parent.Id;
        if (string.IsNullOrEmpty(folder.Name) || folder.Name != displayName) folder.Name = displayName;
        if (coverFile is not null) SetImageIfMissing(folder, 0, coverFile);
        if (string.IsNullOrEmpty(folder.PresentationUniqueKey))
            folder.PresentationUniqueKey = folder.CreatePresentationUniqueKey();

        if (!_dryRun) CreateItemsQuiet([folder], parent, ct);
        cache[dirPath] = folder;
        return folder;
    }

    /// <summary>
    /// Skip fast-path: every file's item exists, the first carries real metadata, and —
    /// when the folder has cover art — the item has a primary image (so books imported
    /// before a cover appeared, or before cover detection improved, heal on the next run).
    /// Audiobooks also require their book Folder item (the folder-tree migration marker).
    /// </summary>
    private bool AreBookItemsImported(DiskScanner.BookFolder book, List<string> files, bool isAudio)
    {
        if (isAudio && _lib.GetItemById<Folder>(_lib.GetNewItemId(book.Dir, typeof(Folder))) is null)
            return false;

        BaseItem? first = null;
        foreach (var file in files)
        {
            BaseItem? item = isAudio
                ? _lib.GetItemById<AudioBook>(_lib.GetNewItemId(file, typeof(AudioBook)))
                : _lib.GetItemById<Book>(_lib.GetNewItemId(file, typeof(Book)));
            if (item is null) return false;
            first ??= item;
        }

        if (first is null) return false;

        // IsLocked is set on every item we import — it marks "import completed" even for
        // books whose sidecars provide no overview or ids (common for OPF-only audiobooks,
        // which would otherwise re-import every run).
        if (!first.IsLocked && string.IsNullOrEmpty(first.Overview) && first.ProviderIds.Count == 0)
            return false;

        return book.CoverFile is null || first.GetImageInfo(ImageType.Primary, 0) is not null;
    }

    private async Task<BookMetadata> ResolveBookMetadataAsync(
        DiskScanner.BookFolder book, bool isAudio, AudiobookshelfClient? abs,
        AudnexusClient audnexus, OpenLibraryClient openLibrary)
    {
        // 1. Audiobookshelf (opt-in, audiobooks only)
        BookMetadata? meta = isAudio ? abs?.FindByFolder(book.Dir) : null;

        // 2. folder sidecars: ABS metadata.json, then OPFs fill gaps
        if (book.AbsJsonFile is not null)
        {
            var fromJson = BookSidecarParser.ParseAbsMetadataJson(book.AbsJsonFile);
            if (fromJson is not null) meta = meta is null ? fromJson : BookSidecarParser.Merge(meta, fromJson);
        }

        foreach (var opf in book.OpfFiles)
        {
            var fromOpf = BookSidecarParser.ParseOpf(opf);
            if (fromOpf is not null) meta = meta is null ? fromOpf : BookSidecarParser.Merge(meta, fromOpf);
        }

        // 3. API gap-filler: fires when local sources gave nothing OR are missing
        //    key fields (overview; narrators for audiobooks). Merged with
        //    local-wins semantics, so sidecar/ABS data is never overwritten.
        //    A sidecar-provided ASIN skips the Audible title search entirely.
        var sparse = meta is null
            || string.IsNullOrEmpty(meta.Overview)
            || (isAudio && meta.Narrators.Count == 0);
        if (sparse)
        {
            var searchTitle = StripParentheticals(
                meta?.Title is { Length: > 0 } localTitle ? localTitle : book.CleanName);
            var searchAuthor = meta?.Authors.FirstOrDefault() ?? book.Author;
            var fromApi = isAudio
                ? await audnexus.GetAsync(meta?.Asin, searchTitle, searchAuthor).ConfigureAwait(false)
                : await openLibrary.SearchAsync(searchTitle, searchAuthor).ConfigureAwait(false);
            if (fromApi is not null)
                meta = BookSidecarParser.Merge(meta, fromApi);
        }

        meta ??= new BookMetadata();
        if (string.IsNullOrEmpty(meta.Title)) meta.Title = StripParentheticals(book.CleanName);
        if (meta.Authors.Count == 0) meta.Authors.Add(book.Author);
        meta.CoverPath = book.CoverFile;
        meta.FolderPath = book.Dir;
        return meta;
    }

    private static string StripParentheticals(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"\s*\([^)]*\)", "").Trim();

    private void PopulateAudioBook(AudioBook ab, string path, int part, int totalParts,
        DiskScanner.BookFolder book, BookMetadata meta, BaseItem parent)
    {
        ab.ParentId = parent.Id;
        ab.Path = path;
        ab.Name = totalParts > 1 ? $"{meta.Title} - Part {part + 1:00}" : meta.Title;
        ab.Album = meta.Title;
        ab.AlbumArtists = meta.Authors;
        ab.Artists = meta.Narrators.Count > 0 ? meta.Narrators : meta.Authors;
        if (totalParts > 1) ab.IndexNumber = part + 1;
        PopulateBookCommon(ab, book, meta);
        if (!string.IsNullOrEmpty(meta.SeriesName)) ab.SeriesName = meta.SeriesName;
    }

    private void PopulateBook(Book bk, string path, DiskScanner.BookFolder book, BookMetadata meta, BaseItem parent)
    {
        bk.ParentId = parent.Id;
        bk.Path = path;
        bk.Name = meta.Title;
        PopulateBookCommon(bk, book, meta);
        if (!string.IsNullOrEmpty(meta.SeriesName)) bk.SeriesName = meta.SeriesName;
    }

    private void PopulateBookCommon(BaseItem item, DiskScanner.BookFolder book, BookMetadata meta)
    {
        item.Overview = meta.Overview ?? item.Overview;
        item.ProductionYear = meta.Year ?? item.ProductionYear;
        item.PremiereDate = ParseDate(meta.PremiereDate) ?? item.PremiereDate;
        if (!string.IsNullOrEmpty(meta.SortTitle)) item.ForcedSortName = meta.SortTitle;
        var genres = meta.Genres.Concat(meta.Tags).Distinct().ToArray();
        if (genres.Length > 0) item.Genres = genres;
        if (!string.IsNullOrEmpty(meta.Publisher)) item.SetStudios([meta.Publisher]);
        if (!string.IsNullOrEmpty(meta.Isbn)) item.ProviderIds["Isbn"] = meta.Isbn;
        if (!string.IsNullOrEmpty(meta.Asin)) item.ProviderIds["Asin"] = meta.Asin;
        if (meta.CommunityRating is > 0) item.CommunityRating = meta.CommunityRating;

        if (book.CoverFile is not null)
            SetImageIfMissing(item, 0, book.CoverFile);

        item.IsLocked = true;
        if (string.IsNullOrEmpty(item.PresentationUniqueKey))
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
    }

    private static List<JfPersonInfo> BookPeople(BookMetadata meta)
    {
        var people = new List<JfPersonInfo>();
        foreach (var a in meta.Authors)
            people.Add(new JfPersonInfo { Name = a, Type = PersonKind.Author });
        foreach (var n in meta.Narrators)
            people.Add(new JfPersonInfo { Name = n, Type = PersonKind.Actor, Role = "Narrator" });
        return people;
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
        if (isNew) CreateItemsQuiet([item], parent, ct);
        else await UpdateItemsQuietAsync([item], parent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the movie is already in the DB with real metadata and a primary image.
    /// Movies missing a poster fail the check so the TMDB poster download gets another
    /// chance on the next run.
    /// </summary>
    private static bool IsMovieFullyImported(Movie movie)
    {
        // No overview and no provider ids → never completed a metadata import.
        if (string.IsNullOrEmpty(movie.Overview) && movie.ProviderIds.Count == 0)
            return false;

        return movie.GetImageInfo(ImageType.Primary, 0) is not null;
    }

    private bool HasOverride(string folderPath) =>
        _config.Overrides.Any(o => folderPath.Contains(o.PathPattern, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the series and every on-disk season/episode are already in the DB with
    /// real metadata. Episodes still carrying the "Episode N" placeholder title fail the
    /// check on purpose: shows imported while metadata lookups were broken get re-fetched
    /// once (healing their titles), then skip on every run after.
    /// </summary>
    private bool IsShowFullyImported(Series series, string showDir)
    {
        // A series with no overview and no provider ids never completed a metadata import.
        if (string.IsNullOrEmpty(series.Overview) && series.ProviderIds.Count == 0)
            return false;

        foreach (var (seasonDir, seasonNum) in DiskScanner.ScanSeasons(showDir))
        {
            if (_lib.GetItemById<Season>(_lib.GetNewItemId(seasonDir, typeof(Season))) is null)
                return false;

            foreach (var (epPath, _, e, _) in DiskScanner.ScanEpisodes(seasonDir, seasonNum))
            {
                var ep = _lib.GetItemById<Episode>(_lib.GetNewItemId(epPath, typeof(Episode)));
                if (ep is null || string.IsNullOrEmpty(ep.Name))
                    return false;

                // "Episode N" with no overview = placeholder from a failed metadata import →
                // re-fetch. Some shows (lots of UK ones) genuinely title episodes "Episode N";
                // those carry an overview, so they pass and skip on subsequent runs.
                if (ep.Name == $"Episode {e}" && string.IsNullOrEmpty(ep.Overview))
                    return false;
            }
        }

        return true;
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
