using System.Globalization;
using Jellyfin.Plugin.LibraryImporter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LibraryImporter;

public class LibraryImporterPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static LibraryImporterPlugin? Instance { get; private set; }

    public LibraryImporterPlugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Library Importer";

    public override string Description =>
        "Bulk-imports and manages movie/TV metadata from disk, TMDB, and TVDB. " +
        "Supports scheduled scans, custom overrides, duplicate resolution, and orphan purging.";

    public override Guid Id => new("b74b2d8c-2ab3-411f-9631-c5e8270d7e12");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        ];
    }
}
