namespace Jellyfin.Plugin.LibraryImporter.Models;

public class MovieMetadata
{
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string? SortName { get; set; }
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public float? CommunityRating { get; set; }
    public float? CriticRating { get; set; }
    public string? OfficialRating { get; set; }  // MPAA rating
    public long? RunTimeTicks { get; set; }
    public string? PremiereDate { get; set; }
    public int? ProductionYear { get; set; }
    public string? TmdbId { get; set; }
    public string? TvdbId { get; set; }
    public string? ImdbId { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<PersonInfo> People { get; set; } = [];
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? NfoPath { get; set; }
    public string? FolderPath { get; set; }
    public string? VideoPath { get; set; }
    public bool FromNfo { get; set; }
    public bool FromTmdb { get; set; }
    public bool FromTvdb { get; set; }
    public bool FromOverride { get; set; }
}

public class PersonInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "Actor"; // Actor, Director, Writer, Producer
    public string? Role { get; set; }
    public int? SortOrder { get; set; }
    public string? TmdbId { get; set; }
    public string? ImageUrl { get; set; }
}

public class TvShowMetadata
{
    public string Title { get; set; } = string.Empty;
    public string? SortName { get; set; }
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public float? CommunityRating { get; set; }
    public string? OfficialRating { get; set; }
    public string? TvdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Status { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<PersonInfo> People { get; set; } = [];
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? PremiereDate { get; set; }
}

public class EpisodeMetadata
{
    public string Title { get; set; } = string.Empty;
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Overview { get; set; }
    public float? CommunityRating { get; set; }
    public string? PremiereDate { get; set; }
    public long? RunTimeTicks { get; set; }
    public string? TvdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
}

public class OverrideEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PathPattern { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int? Year { get; set; }
    public string? TmdbId { get; set; }
    public string? TvdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Overview { get; set; }
    public string? OfficialRating { get; set; }
    public float? CommunityRating { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<PersonInfo> People { get; set; } = [];
    public bool IsLocked { get; set; } = true;
}

public class LibraryScanResult
{
    public string LibraryName { get; set; } = string.Empty;
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Purged { get; set; }
    public int Renamed { get; set; }
    public int Fixed { get; set; }
    public int DuplicatesResolved { get; set; }
    public int PostersDownloaded { get; set; }
    public List<string> Errors { get; set; } = [];
}
