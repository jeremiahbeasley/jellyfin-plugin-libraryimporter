namespace Jellyfin.Plugin.LibraryImporter.Models;

/// <summary>
/// Resolved metadata for one book (ebook or audiobook). Sources, in priority order:
/// Audiobookshelf API (audiobooks, when configured) → folder sidecars
/// (metadata.json / *.opf) → API fallback (Audnexus for audiobooks, OpenLibrary for ebooks).
/// </summary>
public class BookMetadata
{
    public string Title { get; set; } = string.Empty;
    public string? SortTitle { get; set; }
    public string? Subtitle { get; set; }
    public List<string> Authors { get; set; } = [];
    public List<string> Narrators { get; set; } = [];
    public string? SeriesName { get; set; }
    public float? SeriesIndex { get; set; }
    public int? Year { get; set; }
    public string? PremiereDate { get; set; }
    public string? Publisher { get; set; }
    public string? Overview { get; set; }
    public string? Isbn { get; set; }
    public string? Asin { get; set; }
    public string? Language { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Tags { get; set; } = [];

    /// <summary>Community rating on Jellyfin's 0-10 scale (Audnexus 0-5 is doubled).</summary>
    public float? CommunityRating { get; set; }
    public string? CoverPath { get; set; }
    public string? FolderPath { get; set; }
    public bool FromSidecar { get; set; }
    public bool FromAbs { get; set; }
    public bool FromApi { get; set; }
}
