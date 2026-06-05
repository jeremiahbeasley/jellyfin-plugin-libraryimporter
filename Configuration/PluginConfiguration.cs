using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryImporter.Configuration;

public class LibraryConfig
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool PurgeMissing { get; set; }

    /// <summary>One-shot: bypass the skip fast-path on the next run, then auto-clear.</summary>
    public bool ForceReimport { get; set; }
}

public class RunSummary
{
    public string StartedUtc { get; set; } = string.Empty;
    public string FinishedUtc { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // Completed | Failed | Cancelled | Skipped
    public bool DryRun { get; set; }
    public int LibrariesProcessed { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Purged { get; set; }
    public int PostersDownloaded { get; set; }
}

public class PluginConfiguration : BasePluginConfiguration
{
    public string TmdbApiKey { get; set; } = string.Empty;
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>Optional Audiobookshelf server URL; with <see cref="AbsApiKey"/> set, audiobook metadata comes from ABS first.</summary>
    public string AbsUrl { get; set; } = string.Empty;
    public string AbsApiKey { get; set; } = string.Empty;
    public List<LibraryConfig> Libraries { get; set; } = [];
    public List<Models.OverrideEntry> Overrides { get; set; } = [];
    public bool DryRun { get; set; }
    public string CronSchedule { get; set; } = string.Empty;

    /// <summary>Summary of the most recent scan, surfaced on the config page's Run panel.</summary>
    public RunSummary? LastRun { get; set; }
}
