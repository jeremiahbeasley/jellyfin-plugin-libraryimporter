# Jellyfin Library Importer Plugin

Bulk-imports and manages movie/TV metadata directly into Jellyfin's database from disk, TMDB, and TVDB.

## Features

- **Scheduled Scans** — Runs as a Jellyfin scheduled task (daily 3 AM default)
- **Per-Library Control** — Enable/disable scanning, purge missing items, resolve duplicates per library
- **Metadata Resolution Chain** — NFO files → TMDB → TVDB fallback
- **Custom Overrides** — Add/edit/delete metadata overrides via the web UI (title, genres, studios, people, etc.)
- **Deterministic IDs** — Generates Jellyfin-compatible item IDs from file paths
- **Direct DB Writes** — Updates SQLite database via WAL mode (no service restart needed during scan)
- **Poster Downloads** — Automatically fetches posters from TMDB when local images are missing
- **Orphan Purging** — Removes database entries for items no longer on disk

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

1. Enter your **TMDB API Key** (required) and optionally a **TVDB API Key**
2. Click **Load Libraries** to see your Jellyfin libraries
3. Enable the libraries you want to scan
4. Add any custom overrides for items that need manual metadata
5. Save and run the scheduled task or wait for the next scheduled scan

## Building from Source

```bash
dotnet build -c Release
```

The plugin DLL will be at `bin/Release/net9.0/Jellyfin.Plugin.LibraryImporter.dll`.

## Requirements

- Jellyfin Server 10.11.x+
- .NET 9.0 runtime (included with Jellyfin)

## License

MIT
