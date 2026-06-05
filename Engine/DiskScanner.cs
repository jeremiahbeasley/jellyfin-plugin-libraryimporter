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

    private static readonly HashSet<string> AudiobookExtensions = [".m4b", ".mp3", ".m4a", ".flac", ".ogg", ".opus"];
    private static readonly HashSet<string> EbookExtensions = [".epub", ".pdf", ".azw3", ".mobi", ".cbz", ".cbr"];

    public record BookFolder(
        string Dir,
        string Author,
        string CleanName,
        List<string> AudioFiles,
        List<string> EbookFiles,
        List<string> OpfFiles,
        string? AbsJsonFile,
        string? CoverFile,
        string? PlaylistFile);

    /// <summary>
    /// Finds book folders (audiobooks and/or ebooks) under Author/[Series/]Book layouts.
    /// A "book folder" is any directory directly containing audio or ebook files, up to
    /// three levels below the library root. LazyLibrarian's " (1234)" book-id suffix is
    /// stripped for the clean name, and duplicate copies of the same book (same author +
    /// clean name) are collapsed to the best candidate (most metadata, then most files).
    /// </summary>
    public static List<BookFolder> ScanBooks(List<string> libraryPaths)
    {
        var byKey = new Dictionary<string, BookFolder>(StringComparer.OrdinalIgnoreCase);

        foreach (var basePath in libraryPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            foreach (var authorDir in Directory.EnumerateDirectories(basePath))
            {
                var author = Path.GetFileName(authorDir);
                if (author.StartsWith('.') || author.StartsWith('$')
                    || author.Equals("lost+found", StringComparison.OrdinalIgnoreCase)) continue;

                CollectBookDirs(authorDir, author, depth: 0, byKey);
            }
        }

        return byKey.Values.ToList();
    }

    private static void CollectBookDirs(string dir, string author, int depth, Dictionary<string, BookFolder> byKey)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name.StartsWith('$')) continue;

            var book = ProbeBookDir(sub, author);
            if (book is not null)
            {
                var key = $"{author}|{book.CleanName}";
                if (!byKey.TryGetValue(key, out var existing) || Better(book, existing))
                    byKey[key] = book;
            }

            // Recurse regardless — a series dir can hold loose files AND book subdirs
            // (Author/Series/Book layouts), so probing and descending are not exclusive.
            if (depth < 2)
                CollectBookDirs(sub, author, depth + 1, byKey);
        }
    }

    private static BookFolder? ProbeBookDir(string dir, string author)
    {
        List<string> audio = [], ebook = [], opf = [];
        string? absJson = null, cover = null, anyImage = null, playlist = null;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var fileName = Path.GetFileName(file);
            if (AudiobookExtensions.Contains(ext)) audio.Add(file);
            else if (EbookExtensions.Contains(ext)) ebook.Add(file);
            else if (ext == ".opf") opf.Add(file);
            else if (fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase)) absJson = file;
            else if (fileName.Equals("cover.jpg", StringComparison.OrdinalIgnoreCase)
                  || fileName.Equals("cover.png", StringComparison.OrdinalIgnoreCase)) cover = file;
            else if (ext is ".jpg" or ".jpeg" or ".png") anyImage ??= file;
            else if (ext == ".ll") playlist = file;
        }

        // LazyLibrarian-style folders name the art "<Title> - <Author>.jpg" instead of
        // cover.jpg — any image in the folder is better than none.
        cover ??= anyImage;

        if (audio.Count == 0 && ebook.Count == 0) return null;

        audio.Sort(StringComparer.OrdinalIgnoreCase);
        ebook.Sort(StringComparer.OrdinalIgnoreCase);
        // metadata.opf (the canonical sidecar) first when several OPFs exist
        opf.Sort((a, b) =>
            Path.GetFileName(b).Equals("metadata.opf", StringComparison.OrdinalIgnoreCase)
                .CompareTo(Path.GetFileName(a).Equals("metadata.opf", StringComparison.OrdinalIgnoreCase)));

        return new BookFolder(dir, author, CleanBookName(Path.GetFileName(dir)),
            audio, ebook, opf, absJson, cover, playlist);
    }

    private static bool Better(BookFolder a, BookFolder b) =>
        (a.OpfFiles.Count + (a.AbsJsonFile is null ? 0 : 1), a.AudioFiles.Count + a.EbookFiles.Count)
            .CompareTo((b.OpfFiles.Count + (b.AbsJsonFile is null ? 0 : 1), b.AudioFiles.Count + b.EbookFiles.Count)) > 0;

    /// <summary>Strips LazyLibrarian's trailing " (1234)" book-id suffix.</summary>
    public static string CleanBookName(string folderName) =>
        LazyLibrarianIdRegex().Replace(folderName, "").Trim();

    [GeneratedRegex(@"\s*\(\d{1,6}\)\s*$")]
    private static partial Regex LazyLibrarianIdRegex();

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
