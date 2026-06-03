# Jellyfin Library Importer Plugin

Bulk-imports and repairs movie &amp; TV metadata in Jellyfin from local NFO files, TMDB, and TVDB — on demand or on a schedule.

## Features

- **On-demand or scheduled** — Run instantly from the plugin page with **Run Now** (with live progress), or let the daily scheduled task (3 AM default) handle it.
- **Movies &amp; TV** — Full Series → Season → Episode hierarchy, including per-episode titles and overviews.
- **Metadata resolution chain** — NFO files → TMDB → TVDB fallback, for both movies and series.
- **Native Jellyfin integration** — Writes through Jellyfin's library API (`ILibraryManager`), **not raw SQL**, so items, cast &amp; crew, genres, studios, images, and provider IDs all appear correctly and keep working across Jellyfin versions.
- **Custom overrides** — Pin exact metadata for specific titles from the web UI: title, sort/original title, year, premiere date, runtime, TMDB/TVDB/IMDB IDs, rating, genres, studios, and cast. Overrides take priority over NFO files and API lookups.
- **Posters** — Local artwork is used when present; otherwise a poster is fetched from TMDB.
- **Purge missing** — Optionally remove library items whose files no longer exist (per library; never deletes your media).
- **Theme-aware UI** — The config page adapts to your Jellyfin theme (light or dark) and is keyboard/remote friendly.

## Installation

### From The JB11 Repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Add repository URL: `https://raw.githubusercontent.com/jeremiahbeasley/jb11-jellyfin-repository/main/manifest.json`
3. Go to **Catalog**, find **Library Importer** under Metadata, and install it
4. Restart Jellyfin

### Manual Install

1. Download the latest release zip from [Releases](https://github.com/jeremiahbeasley/jellyfin-plugin-libraryimporter/releases)
2. Extract to `<jellyfin-data>/plugins/` (e.g., `/var/lib/jellyfin/plugins/`)
3. Restart Jellyfin

## Configuration

After installation, go to **Dashboard → Plugins → Library Importer**:

1. Enter your **TMDB API key** (required) and optionally a **TVDB API key**.
2. In the **Libraries** table, toggle which libraries to **Import** (and optionally **Purge** missing items).
3. Optionally add **Custom Overrides** for titles that need hand-corrected metadata.
4. **Save configuration**, then click **Run Now** — or wait for the nightly scheduled scan.

## Building from Source

```bash
dotnet build -c Release
```

The plugin DLL will be at `bin/Release/net9.0/Jellyfin.Plugin.LibraryImporter.dll`.

## Requirements

- Jellyfin Server 10.11.x
- .NET 9.0 (bundled with Jellyfin 10.11)

## License

MIT
