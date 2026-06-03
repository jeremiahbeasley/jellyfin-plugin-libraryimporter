using Jellyfin.Plugin.LibraryImporter.Configuration;
using Jellyfin.Plugin.LibraryImporter.Engine;
using Jellyfin.Plugin.LibraryImporter.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryImporter.ScheduledTasks;

public class LibraryImportTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryImportTask> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public LibraryImportTask(ILibraryManager libraryManager, ILogger<LibraryImportTask> logger,
        IHttpClientFactory httpClientFactory)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "Library Importer Scan";
    public string Key => "LibraryImporterScan";
    public string Description => "Scans selected libraries, imports metadata from NFO/TMDB/TVDB, and updates the database.";
    public string Category => "Library";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks // 3 AM default
            }
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var plugin = LibraryImporterPlugin.Instance;
        if (plugin is null)
        {
            _logger.LogError("Plugin instance not available");
            return;
        }

        var config = plugin.Configuration;

        var summary = new RunSummary
        {
            StartedUtc = DateTime.UtcNow.ToString("o"),
            DryRun = config.DryRun,
        };

        void SaveSummary(string status)
        {
            summary.Status = status;
            summary.FinishedUtc = DateTime.UtcNow.ToString("o");
            config.LastRun = summary;
            plugin.SaveConfiguration();
        }

        if (string.IsNullOrEmpty(config.TmdbApiKey))
        {
            _logger.LogWarning("TMDB API key not configured — skipping scan");
            SaveSummary("Skipped");
            return;
        }

        var enabledLibraries = config.Libraries.Where(l => l.Enabled).ToList();
        if (enabledLibraries.Count == 0)
        {
            _logger.LogInformation("No libraries enabled for scanning");
            SaveSummary("Skipped");
            return;
        }

        var httpClient = _httpClientFactory.CreateClient("LibraryImporter");
        var tmdb = new TmdbClient(httpClient, config.TmdbApiKey, _logger);
        var tvdb = new TvdbClient(httpClient, config.TvdbApiKey, _logger);

        // Find Jellyfin's DB path
        var dbPath = FindDatabasePath();
        if (dbPath is null)
        {
            _logger.LogError("Could not locate jellyfin.db");
            return;
        }

        using var db = new DatabaseUpdater(dbPath, _logger);
        var engine = new ImportEngine(db, tmdb, tvdb, config, _logger);

        var totalLibraries = enabledLibraries.Count;
        var completedLibraries = 0;

        foreach (var libConfig in enabledLibraries)
        {
            ct.ThrowIfCancellationRequested();

            var libInfo = ResolveLibrary(libConfig.Name);
            if (libInfo is null)
            {
                _logger.LogWarning("Library '{Name}' not found — skipping", libConfig.Name);
                completedLibraries++;
                continue;
            }

            var (paths, contentType, physicalRootId) = libInfo.Value;

            var subProgress = new Progress<double>(pct =>
            {
                var overall = ((double)completedLibraries / totalLibraries + pct / 100.0 / totalLibraries) * 100;
                progress.Report(overall);
            });

            LibraryScanResult result;
            if (contentType is "tvshows")
            {
                result = await engine.ImportTvAsync(libConfig.Name, paths, physicalRootId, subProgress, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await engine.ImportMoviesAsync(libConfig.Name, paths, physicalRootId, subProgress, ct)
                    .ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Library '{Name}': Added={Added}, Updated={Updated}, Skipped={Skipped}, Failed={Failed}, Purged={Purged}",
                result.LibraryName, result.Added, result.Updated, result.Skipped, result.Failed, result.Purged);

            summary.Added += result.Added;
            summary.Updated += result.Updated;
            summary.Skipped += result.Skipped;
            summary.Failed += result.Failed;
            summary.Purged += result.Purged;
            summary.PostersDownloaded += result.PostersDownloaded;
            summary.LibrariesProcessed++;

            completedLibraries++;
        }

        // Jellyfin's in-memory cache will pick up DB changes on next library scan
        // The scheduled "Scan All Libraries" task handles this

        progress.Report(100);
        SaveSummary(ct.IsCancellationRequested ? "Cancelled" : "Completed");
        _logger.LogInformation("Library import scan complete");
    }

    private (List<string> paths, string contentType, string physicalRootId)? ResolveLibrary(string name)
    {
        var folders = _libraryManager.GetVirtualFolders();
        var folder = folders.FirstOrDefault(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return null;

        var paths = folder.Locations?.ToList() ?? [];
        if (paths.Count == 0) return null;

        var contentType = folder.CollectionType?.ToString()?.ToLowerInvariant() ?? "movies";

        // Calculate the physical root ID
        var physicalRootId = IdGenerator.CreateString("Folder", paths[0]);
        return (paths, contentType, physicalRootId);
    }

    private static string? FindDatabasePath()
    {
        var candidates = new[]
        {
            "/var/lib/jellyfin/data/jellyfin.db",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "jellyfin", "data", "jellyfin.db"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
