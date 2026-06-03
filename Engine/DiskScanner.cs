using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

public static partial class DiskScanner
{
    private static readonly HashSet<string> VideoExtensions = [".mkv", ".mp4", ".avi", ".wmv", ".flv", ".m4v", ".ts", ".mpg", ".mpeg", ".mov", ".webm"];

    public static List<(string folderPath, string? videoFile, string? nfoFile)> ScanMovies(List<string> libraryPaths)
    {
        var results = new List<(string, string?, string?)>();

        foreach (var basePath in libraryPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            foreach (var movieDir in Directory.EnumerateDirectories(basePath))
            {
                var dirName = Path.GetFileName(movieDir);
                if (dirName.StartsWith('.') || dirName.StartsWith('$')
                    || dirName.Equals("lost+found", StringComparison.OrdinalIgnoreCase)) continue;

                string? videoFile = null;
                string? nfoFile = null;

                foreach (var file in Directory.EnumerateFiles(movieDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (videoFile is null && VideoExtensions.Contains(ext))
                        videoFile = file;
                    else if (nfoFile is null && ext == ".nfo")
                        nfoFile = file;
                }

                results.Add((movieDir, videoFile, nfoFile));
            }
        }

        return results;
    }

    public static List<(string showDir, string? nfoFile)> ScanTvShows(List<string> libraryPaths)
    {
        var results = new List<(string, string?)>();

        foreach (var basePath in libraryPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            foreach (var showDir in Directory.EnumerateDirectories(basePath))
            {
                var dirName = Path.GetFileName(showDir);
                if (dirName.StartsWith('.') || dirName.StartsWith('$')
                    || dirName.Equals("lost+found", StringComparison.OrdinalIgnoreCase)) continue;

                var nfoFile = Path.Combine(showDir, "tvshow.nfo");
                results.Add((showDir, File.Exists(nfoFile) ? nfoFile : null));
            }
        }

        return results;
    }

    public static List<(string seasonDir, int seasonNumber)> ScanSeasons(string showDir)
    {
        var results = new List<(string, int)>();

        foreach (var dir in Directory.EnumerateDirectories(showDir))
        {
            var name = Path.GetFileName(dir);
            var num = ExtractSeasonNumber(name);
            if (num >= 0)
                results.Add((dir, num));
        }

        return results.OrderBy(s => s.Item2).ToList();
    }

    public static List<(string filePath, int season, int episode, string baseName)> ScanEpisodes(string seasonDir, int seasonNumber)
    {
        var results = new List<(string, int, int, string)>();

        foreach (var file in Directory.EnumerateFiles(seasonDir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!VideoExtensions.Contains(ext)) continue;

            var fileName = Path.GetFileNameWithoutExtension(file);
            var (s, e) = ParseEpisodeNumbers(fileName);
            if (e <= 0) continue;
            if (s <= 0) s = seasonNumber;

            results.Add((file, s, e, fileName));
        }

        return results.OrderBy(e => e.Item3).ToList();
    }

    public static int ExtractSeasonNumber(string folderName)
    {
        var lower = folderName.ToLowerInvariant().Trim();
        if (lower == "specials") return 0;

        var match = SeasonNumberRegex().Match(lower);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : -1;
    }

    public static (int season, int episode) ParseEpisodeNumbers(string fileName)
    {
        // S01E02 or s01e02
        var match = SxxExxRegex().Match(fileName);
        if (match.Success)
        {
            int.TryParse(match.Groups[1].Value, out var s);
            int.TryParse(match.Groups[2].Value, out var e);
            return (s, e);
        }

        // 01x02
        match = NxNRegex().Match(fileName);
        if (match.Success)
        {
            int.TryParse(match.Groups[1].Value, out var s);
            int.TryParse(match.Groups[2].Value, out var e);
            return (s, e);
        }

        return (0, 0);
    }

    public static string? ExtractTvdbId(string folderName)
    {
        var match = TvdbIdRegex().Match(folderName);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? ExtractTmdbId(string folderName)
    {
        var match = TmdbIdRegex().Match(folderName);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static int? ExtractYear(string folderName)
    {
        var match = YearRegex().Match(folderName);
        return match.Success && int.TryParse(match.Groups[1].Value, out var y) ? y : null;
    }

    public static string ExtractTitle(string folderName)
    {
        // Remove [tvdbid=...], [TmdbId - ...], (year), etc.
        var title = TvdbIdRegex().Replace(folderName, "");
        title = TmdbIdRegex().Replace(title, "");
        title = YearParenRegex().Replace(title, "");
        title = TrailingDashRegex().Replace(title, "").Trim();
        return title;
    }

    public static List<(int imageType, string path)> FindImages(string dir)
    {
        var images = new List<(int, string)>();
        // ImageType: 0=Primary/Poster, 1=Art, 2=Backdrop, 3=Banner, 4=Logo, 6=Thumb
        TryAddImage(images, dir, "poster.jpg", 0);
        TryAddImage(images, dir, "poster.png", 0);
        TryAddImage(images, dir, "folder.jpg", 0);
        TryAddImage(images, dir, "fanart.jpg", 2);
        TryAddImage(images, dir, "backdrop.jpg", 2);
        TryAddImage(images, dir, "banner.jpg", 3);
        TryAddImage(images, dir, "logo.png", 4);
        TryAddImage(images, dir, "clearlogo.png", 4);
        TryAddImage(images, dir, "landscape.jpg", 6);
        TryAddImage(images, dir, "thumb.jpg", 6);
        return images;
    }

    private static void TryAddImage(List<(int, string)> images, string dir, string fileName, int type)
    {
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path))
            images.Add((type, path));
    }

    [GeneratedRegex(@"season\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonNumberRegex();

    [GeneratedRegex(@"[Ss](\d+)[Ee](\d+)")]
    private static partial Regex SxxExxRegex();

    [GeneratedRegex(@"(\d+)x(\d+)")]
    private static partial Regex NxNRegex();

    [GeneratedRegex(@"\[tvdbid[=\s-]+(\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex TvdbIdRegex();

    [GeneratedRegex(@"\[TmdbId\s*-\s*(\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbIdRegex();

    [GeneratedRegex(@"\((\d{4})\)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\(\d{4}\)")]
    private static partial Regex YearParenRegex();

    [GeneratedRegex(@"\s*-\s*$")]
    private static partial Regex TrailingDashRegex();
}
