using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryImporter.Configuration;

public class LibraryConfig
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool PurgeMissing { get; set; }
    public bool AutoDups { get; set; }
}

public class PluginConfiguration : BasePluginConfiguration
{
    public string TmdbApiKey { get; set; } = string.Empty;
    public string TvdbApiKey { get; set; } = string.Empty;
    public List<LibraryConfig> Libraries { get; set; } = [];
    public List<Models.OverrideEntry> Overrides { get; set; } = [];
    public bool DryRun { get; set; }
    public string CronSchedule { get; set; } = string.Empty;
}
