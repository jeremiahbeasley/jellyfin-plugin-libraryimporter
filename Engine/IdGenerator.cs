using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

/// <summary>
/// Generates deterministic Jellyfin item IDs.
/// Matches the Python jellyfin_id() function: MD5(UTF-16LE(TypeFullName + Path)), bytes_le GUID format.
/// </summary>
public static class IdGenerator
{
    private static readonly Dictionary<string, string> TypeFullNames = new()
    {
        ["Movie"] = "MediaBrowser.Controller.Entities.Movies.Movie",
        ["Series"] = "MediaBrowser.Controller.Entities.TV.Series",
        ["Season"] = "MediaBrowser.Controller.Entities.TV.Season",
        ["Episode"] = "MediaBrowser.Controller.Entities.TV.Episode",
        ["Folder"] = "MediaBrowser.Controller.Entities.Folder",
        ["CollectionFolder"] = "MediaBrowser.Controller.Entities.CollectionFolder",
        ["AggregateFolder"] = "MediaBrowser.Controller.Entities.AggregateFolder",
        ["UserRootFolder"] = "MediaBrowser.Controller.Entities.UserRootFolder",
    };

    public static Guid Create(string typeName, string path)
    {
        var fullName = TypeFullNames.GetValueOrDefault(typeName, typeName);
        var input = fullName + path;
        var bytes = Encoding.Unicode.GetBytes(input); // UTF-16LE
        var hash = MD5.HashData(bytes);
        // bytes_le format: first 3 groups are little-endian
        return new Guid(hash);
    }

    public static string CreateString(string typeName, string path)
    {
        return Create(typeName, path).ToString("N");
    }
}
