using Jellyfin.Plugin.LibraryImporter.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

public class DatabaseUpdater : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ILogger _logger;
    private readonly Dictionary<(int, string), long> _valueCache = new();
    private readonly Dictionary<(string, string), long> _personCache = new();

    public DatabaseUpdater(string dbPath, ILogger logger)
    {
        _logger = logger;
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=NORMAL");
        PreloadCaches();
    }

    private void PreloadCaches()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT ItemValueId, Type, CleanValue FROM ItemValues";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetInt32(1), reader.GetString(2));
            _valueCache[key] = reader.GetInt64(0);
        }

        using var cmd2 = _conn.CreateCommand();
        cmd2.CommandText = "SELECT Id, Name, Type FROM Peoples";
        using var reader2 = cmd2.ExecuteReader();
        while (reader2.Read())
        {
            var key = (reader2.GetString(1), reader2.GetString(2));
            _personCache[key] = reader2.GetInt64(0);
        }

        _logger.LogInformation("Cached {Values} item values, {People} people", _valueCache.Count, _personCache.Count);
    }

    public bool ItemExists(string itemId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM BaseItems WHERE guid = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        return cmd.ExecuteScalar() is not null;
    }

    public Dictionary<string, object?>? GetExistingItem(string itemId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM BaseItems WHERE guid = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var row = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    public void UpsertMovie(string itemId, string path, string parentId, MovieMetadata meta, string dateModified)
    {
        var now = dateModified;
        var sortName = meta.SortName ?? MakeSortName(meta.Title);
        var cleanName = MakeCleanName(meta.Title);
        var premiereDate = meta.PremiereDate ?? (meta.Year.HasValue ? $"{meta.Year}-01-01 00:00:00" : null);

        var existing = GetExistingItem(itemId);
        if (existing is not null)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE BaseItems SET
                Name = @name, CleanName = @cleanName, SortName = @sortName,
                Overview = @overview, Tagline = @tagline,
                CommunityRating = @rating, OfficialRating = @mpaa,
                PremiereDate = @premiere, ProductionYear = @year,
                RunTimeTicks = @runtime, IsLocked = 1, DateModified = @modified,
                ProviderIds = @providerIds
                WHERE guid = @id";
            AddMovieParams(cmd, itemId, meta, sortName, cleanName, premiereDate, now);
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO BaseItems (
                guid, type, Path, Name, CleanName, SortName, Overview, Tagline,
                CommunityRating, OfficialRating, PremiereDate, ProductionYear,
                RunTimeTicks, IsLocked, DateCreated, DateModified, ParentId,
                TopParentId, ProviderIds, IsFolder, IsVirtualItem, MediaType
            ) VALUES (
                @id, 'MediaBrowser.Controller.Entities.Movies.Movie', @path,
                @name, @cleanName, @sortName, @overview, @tagline,
                @rating, @mpaa, @premiere, @year,
                @runtime, 1, @modified, @modified, @parentId,
                @parentId, @providerIds, 0, 0, 'Video'
            )";
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@parentId", parentId);
            AddMovieParams(cmd, itemId, meta, sortName, cleanName, premiereDate, now);
            cmd.ExecuteNonQuery();
        }

        InsertProviders(itemId, meta);
        InsertGenresStudios(itemId, meta);
        InsertPeople(itemId, meta.People);
        InsertAncestor(itemId, parentId);
    }

    private void AddMovieParams(SqliteCommand cmd, string itemId, MovieMetadata meta,
        string sortName, string cleanName, string? premiereDate, string dateModified)
    {
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@name", meta.Title);
        cmd.Parameters.AddWithValue("@cleanName", cleanName);
        cmd.Parameters.AddWithValue("@sortName", sortName);
        cmd.Parameters.AddWithValue("@overview", (object?)meta.Overview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tagline", (object?)meta.Tagline ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rating", (object?)meta.CommunityRating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mpaa", (object?)meta.OfficialRating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@premiere", (object?)premiereDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@year", (object?)meta.ProductionYear ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@runtime", (object?)meta.RunTimeTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modified", dateModified);
        cmd.Parameters.AddWithValue("@providerIds", BuildProviderIds(meta));
    }

    public void UpsertSeries(string itemId, string path, string parentId, TvShowMetadata meta, string dateModified)
    {
        var sortName = meta.SortName ?? MakeSortName(meta.Title);
        var cleanName = MakeCleanName(meta.Title);

        var existing = GetExistingItem(itemId);
        if (existing is not null)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE BaseItems SET
                Name = @name, CleanName = @cleanName, SortName = @sortName,
                Overview = @overview, CommunityRating = @rating,
                OfficialRating = @mpaa, PremiereDate = @premiere,
                ProductionYear = @year, IsLocked = 1, DateModified = @modified,
                ProviderIds = @providerIds
                WHERE guid = @id";
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.Parameters.AddWithValue("@name", meta.Title);
            cmd.Parameters.AddWithValue("@cleanName", cleanName);
            cmd.Parameters.AddWithValue("@sortName", sortName);
            cmd.Parameters.AddWithValue("@overview", (object?)meta.Overview ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rating", (object?)meta.CommunityRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mpaa", (object?)meta.OfficialRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@premiere", (object?)meta.PremiereDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@year", (object?)meta.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@modified", dateModified);
            cmd.Parameters.AddWithValue("@providerIds", BuildTvProviderIds(meta));
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO BaseItems (
                guid, type, Path, Name, CleanName, SortName, Overview,
                CommunityRating, OfficialRating, PremiereDate, ProductionYear,
                IsLocked, DateCreated, DateModified, ParentId, TopParentId,
                ProviderIds, IsFolder, IsVirtualItem
            ) VALUES (
                @id, 'MediaBrowser.Controller.Entities.TV.Series', @path,
                @name, @cleanName, @sortName, @overview,
                @rating, @mpaa, @premiere, @year,
                1, @modified, @modified, @parentId, @parentId,
                @providerIds, 1, 0
            )";
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@parentId", parentId);
            cmd.Parameters.AddWithValue("@name", meta.Title);
            cmd.Parameters.AddWithValue("@cleanName", cleanName);
            cmd.Parameters.AddWithValue("@sortName", sortName);
            cmd.Parameters.AddWithValue("@overview", (object?)meta.Overview ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rating", (object?)meta.CommunityRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mpaa", (object?)meta.OfficialRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@premiere", (object?)meta.PremiereDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@year", (object?)meta.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@modified", dateModified);
            cmd.Parameters.AddWithValue("@providerIds", BuildTvProviderIds(meta));
            cmd.ExecuteNonQuery();
        }

        InsertTvProviders(itemId, meta);
        InsertTvGenresStudios(itemId, meta);
        InsertPeople(itemId, meta.People);
        InsertAncestor(itemId, parentId);
    }

    public void UpsertSeason(string itemId, string? path, string seriesId, int seasonNumber, string seriesName, string dateModified)
    {
        var name = seasonNumber == 0 ? "Specials" : $"Season {seasonNumber}";
        var existing = GetExistingItem(itemId);
        if (existing is not null) return; // Seasons don't need metadata updates

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO BaseItems (
            guid, type, Path, Name, SortName, IndexNumber,
            ParentId, TopParentId, SeriesId, SeriesName,
            IsFolder, IsVirtualItem, DateCreated, DateModified, IsLocked
        ) VALUES (
            @id, 'MediaBrowser.Controller.Entities.TV.Season', @path,
            @name, @sortName, @index,
            @parentId, @parentId, @seriesId, @seriesName,
            1, @isVirtual, @modified, @modified, 1
        )";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@path", (object?)path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@sortName", name);
        cmd.Parameters.AddWithValue("@index", seasonNumber);
        cmd.Parameters.AddWithValue("@parentId", seriesId);
        cmd.Parameters.AddWithValue("@seriesId", seriesId);
        cmd.Parameters.AddWithValue("@seriesName", seriesName);
        cmd.Parameters.AddWithValue("@isVirtual", path is null ? 1 : 0);
        cmd.Parameters.AddWithValue("@modified", dateModified);
        cmd.ExecuteNonQuery();
        InsertAncestor(itemId, seriesId);
    }

    public void UpsertEpisode(string itemId, string path, string seasonId, string seriesId,
        int seasonNumber, int episodeNumber, string seriesName, EpisodeMetadata? meta, string dateModified)
    {
        var title = meta?.Title ?? $"Episode {episodeNumber}";
        var existing = GetExistingItem(itemId);

        if (existing is not null)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE BaseItems SET
                Name = @name, Overview = @overview,
                CommunityRating = @rating, PremiereDate = @premiere,
                IndexNumber = @epNum, ParentIndexNumber = @seasonNum,
                SeriesId = @seriesId, SeriesName = @seriesName,
                SeasonId = @seasonId, IsLocked = 1, DateModified = @modified
                WHERE guid = @id";
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.Parameters.AddWithValue("@name", title);
            cmd.Parameters.AddWithValue("@overview", (object?)meta?.Overview ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rating", (object?)meta?.CommunityRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@premiere", (object?)meta?.PremiereDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@epNum", episodeNumber);
            cmd.Parameters.AddWithValue("@seasonNum", seasonNumber);
            cmd.Parameters.AddWithValue("@seriesId", seriesId);
            cmd.Parameters.AddWithValue("@seriesName", seriesName);
            cmd.Parameters.AddWithValue("@seasonId", seasonId);
            cmd.Parameters.AddWithValue("@modified", dateModified);
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO BaseItems (
                guid, type, Path, Name, Overview, CommunityRating, PremiereDate,
                IndexNumber, ParentIndexNumber, ParentId, SeriesId, SeriesName,
                SeasonId, TopParentId, IsFolder, IsVirtualItem, IsLocked,
                DateCreated, DateModified, MediaType
            ) VALUES (
                @id, 'MediaBrowser.Controller.Entities.TV.Episode', @path,
                @name, @overview, @rating, @premiere,
                @epNum, @seasonNum, @seasonId, @seriesId, @seriesName,
                @seasonId, @seriesId, 0, 0, 1,
                @modified, @modified, 'Video'
            )";
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@name", title);
            cmd.Parameters.AddWithValue("@overview", (object?)meta?.Overview ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rating", (object?)meta?.CommunityRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@premiere", (object?)meta?.PremiereDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@epNum", episodeNumber);
            cmd.Parameters.AddWithValue("@seasonNum", seasonNumber);
            cmd.Parameters.AddWithValue("@seasonId", seasonId);
            cmd.Parameters.AddWithValue("@seriesId", seriesId);
            cmd.Parameters.AddWithValue("@seriesName", seriesName);
            cmd.Parameters.AddWithValue("@modified", dateModified);
            cmd.ExecuteNonQuery();
        }

        InsertAncestor(itemId, seasonId);
        InsertAncestor(itemId, seriesId);
    }

    public int PurgeMissingMovies(List<string> libraryPaths, bool dryRun)
    {
        var purged = 0;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT guid, Path FROM BaseItems WHERE type = 'MediaBrowser.Controller.Entities.Movies.Movie'";
        var toDelete = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var path = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrEmpty(path)) continue;
                if (!libraryPaths.Any(lp => path.StartsWith(lp, StringComparison.OrdinalIgnoreCase))) continue;
                if (!File.Exists(path))
                    toDelete.Add(id);
            }
        }

        foreach (var id in toDelete)
        {
            if (!dryRun)
                DeleteItem(id);
            purged++;
        }

        _logger.LogInformation("Purge: {Count} missing movies {Action}", purged, dryRun ? "(dry run)" : "deleted");
        return purged;
    }

    public int PurgeMissingTv(List<string> libraryPaths, bool dryRun)
    {
        var purged = 0;

        // Purge episodes whose files don't exist
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT guid, Path FROM BaseItems WHERE type = 'MediaBrowser.Controller.Entities.TV.Episode' AND Path IS NOT NULL";
        var toDelete = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var path = reader.GetString(1);
                if (!libraryPaths.Any(lp => path.StartsWith(lp, StringComparison.OrdinalIgnoreCase))) continue;
                if (!File.Exists(path))
                    toDelete.Add(id);
            }
        }

        foreach (var id in toDelete)
        {
            if (!dryRun) DeleteItem(id);
            purged++;
        }

        // Purge empty seasons (no children)
        PurgeEmptyContainers("MediaBrowser.Controller.Entities.TV.Season", dryRun);

        // Purge empty series (no children)
        PurgeEmptyContainers("MediaBrowser.Controller.Entities.TV.Series", dryRun);

        _logger.LogInformation("Purge: {Count} missing TV items {Action}", purged, dryRun ? "(dry run)" : "deleted");
        return purged;
    }

    private void PurgeEmptyContainers(string type, bool dryRun)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT b.guid FROM BaseItems b
            WHERE b.type = @type
            AND NOT EXISTS (SELECT 1 FROM BaseItems c WHERE c.ParentId = b.guid)";
        cmd.Parameters.AddWithValue("@type", type);
        var toDelete = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                toDelete.Add(reader.GetString(0));
        }

        if (!dryRun)
            foreach (var id in toDelete)
                DeleteItem(id);
    }

    public void DeleteItem(string itemId)
    {
        Execute("DELETE FROM BaseItemProviders WHERE BaseItemId = @id", ("@id", itemId));
        Execute("DELETE FROM BaseItemImageInfos WHERE BaseItemId = @id", ("@id", itemId));
        Execute("DELETE FROM ItemValuesMap WHERE BaseItemId = @id", ("@id", itemId));
        Execute("DELETE FROM PeopleBaseItemMap WHERE BaseItemId = @id", ("@id", itemId));
        Execute("DELETE FROM AncestorIds WHERE ItemId = @id", ("@id", itemId));
        Execute("DELETE FROM AncestorIds WHERE ParentItemId = @id", ("@id", itemId));
        Execute("DELETE FROM BaseItems WHERE guid = @id", ("@id", itemId));
    }

    private void InsertProviders(string itemId, MovieMetadata meta)
    {
        if (!string.IsNullOrEmpty(meta.TmdbId))
            UpsertProvider(itemId, "Tmdb", meta.TmdbId);
        if (!string.IsNullOrEmpty(meta.TvdbId))
            UpsertProvider(itemId, "Tvdb", meta.TvdbId);
        if (!string.IsNullOrEmpty(meta.ImdbId))
            UpsertProvider(itemId, "Imdb", meta.ImdbId);
    }

    private void InsertTvProviders(string itemId, TvShowMetadata meta)
    {
        if (!string.IsNullOrEmpty(meta.TmdbId))
            UpsertProvider(itemId, "Tmdb", meta.TmdbId);
        if (!string.IsNullOrEmpty(meta.TvdbId))
            UpsertProvider(itemId, "Tvdb", meta.TvdbId);
        if (!string.IsNullOrEmpty(meta.ImdbId))
            UpsertProvider(itemId, "Imdb", meta.ImdbId);
    }

    private void UpsertProvider(string itemId, string name, string value)
    {
        Execute(
            "INSERT OR REPLACE INTO BaseItemProviders (BaseItemId, Name, Value) VALUES (@id, @name, @value)",
            ("@id", itemId), ("@name", name), ("@value", value));
    }

    private void InsertGenresStudios(string itemId, MovieMetadata meta)
    {
        foreach (var g in meta.Genres)
            GetOrCreateItemValue(itemId, 0, g); // type 0 = Genre
        foreach (var s in meta.Studios)
            GetOrCreateItemValue(itemId, 3, s); // type 3 = Studio
    }

    private void InsertTvGenresStudios(string itemId, TvShowMetadata meta)
    {
        foreach (var g in meta.Genres)
            GetOrCreateItemValue(itemId, 0, g);
        foreach (var s in meta.Studios)
            GetOrCreateItemValue(itemId, 3, s);
    }

    private void GetOrCreateItemValue(string itemId, int type, string value)
    {
        var cleanValue = MakeCleanName(value);
        var key = (type, cleanValue);

        if (!_valueCache.TryGetValue(key, out var valueId))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO ItemValues (Type, Value, CleanValue) VALUES (@type, @value, @clean)";
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.Parameters.AddWithValue("@clean", cleanValue);
            cmd.ExecuteNonQuery();

            using var cmd2 = _conn.CreateCommand();
            cmd2.CommandText = "SELECT ItemValueId FROM ItemValues WHERE Type = @type AND CleanValue = @clean";
            cmd2.Parameters.AddWithValue("@type", type);
            cmd2.Parameters.AddWithValue("@clean", cleanValue);
            valueId = (long)(cmd2.ExecuteScalar() ?? 0);
            _valueCache[key] = valueId;
        }

        Execute("INSERT OR IGNORE INTO ItemValuesMap (BaseItemId, ItemValueId) VALUES (@id, @vid)",
            ("@id", itemId), ("@vid", valueId));
    }

    private void InsertPeople(string itemId, List<PersonInfo> people)
    {
        // Clear existing people for this item
        Execute("DELETE FROM PeopleBaseItemMap WHERE BaseItemId = @id", ("@id", itemId));

        foreach (var p in people)
        {
            var key = (p.Name, p.Type);
            if (!_personCache.TryGetValue(key, out var personId))
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO Peoples (Name, Type) VALUES (@name, @type)";
                cmd.Parameters.AddWithValue("@name", p.Name);
                cmd.Parameters.AddWithValue("@type", p.Type);
                cmd.ExecuteNonQuery();

                using var cmd2 = _conn.CreateCommand();
                cmd2.CommandText = "SELECT Id FROM Peoples WHERE Name = @name AND Type = @type";
                cmd2.Parameters.AddWithValue("@name", p.Name);
                cmd2.Parameters.AddWithValue("@type", p.Type);
                personId = (long)(cmd2.ExecuteScalar() ?? 0);
                _personCache[key] = personId;
            }

            using var cmd3 = _conn.CreateCommand();
            cmd3.CommandText = @"INSERT OR IGNORE INTO PeopleBaseItemMap
                (BaseItemId, PeopleId, SortOrder, Role) VALUES (@id, @pid, @sort, @role)";
            cmd3.Parameters.AddWithValue("@id", itemId);
            cmd3.Parameters.AddWithValue("@pid", personId);
            cmd3.Parameters.AddWithValue("@sort", (object?)p.SortOrder ?? DBNull.Value);
            cmd3.Parameters.AddWithValue("@role", (object?)p.Role ?? DBNull.Value);
            cmd3.ExecuteNonQuery();
        }
    }

    private void InsertAncestor(string itemId, string parentId)
    {
        Execute("INSERT OR IGNORE INTO AncestorIds (ItemId, ParentItemId) VALUES (@id, @parent)",
            ("@id", itemId), ("@parent", parentId));
    }

    public void InsertImages(string itemId, List<(int type, string path)> images, string dateModified)
    {
        foreach (var (type, path) in images)
        {
            Execute(@"INSERT OR REPLACE INTO BaseItemImageInfos
                (BaseItemId, ImageType, Path, DateModified) VALUES (@id, @type, @path, @date)",
                ("@id", itemId), ("@type", type), ("@path", path), ("@date", dateModified));
        }
    }

    private static string BuildProviderIds(MovieMetadata meta)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(meta.TmdbId)) parts.Add($"Tmdb={meta.TmdbId}");
        if (!string.IsNullOrEmpty(meta.TvdbId)) parts.Add($"Tvdb={meta.TvdbId}");
        if (!string.IsNullOrEmpty(meta.ImdbId)) parts.Add($"Imdb={meta.ImdbId}");
        return string.Join("|", parts);
    }

    private static string BuildTvProviderIds(TvShowMetadata meta)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(meta.TmdbId)) parts.Add($"Tmdb={meta.TmdbId}");
        if (!string.IsNullOrEmpty(meta.TvdbId)) parts.Add($"Tvdb={meta.TvdbId}");
        if (!string.IsNullOrEmpty(meta.ImdbId)) parts.Add($"Imdb={meta.ImdbId}");
        return string.Join("|", parts);
    }

    public static string MakeSortName(string name)
    {
        var lower = name.ToLowerInvariant().Trim();
        if (lower.StartsWith("the ", StringComparison.Ordinal))
            lower = lower[4..];
        else if (lower.StartsWith("a ", StringComparison.Ordinal))
            lower = lower[2..];
        else if (lower.StartsWith("an ", StringComparison.Ordinal))
            lower = lower[3..];
        return lower;
    }

    public static string MakeCleanName(string name)
    {
        return name.ToLowerInvariant().Trim();
    }

    private void Execute(string sql, params (string name, object value)[] parameters)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _conn;

    public void Dispose()
    {
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }
}
