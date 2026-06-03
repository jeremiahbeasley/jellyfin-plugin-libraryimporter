# Jellyfin Library Importer Plugin

Bulk-imports and repairs movie &amp; TV metadata in Jellyfin from local NFO files, TMDB, and TVDB — on demand or on a schedule.

## Features

- **On-demand or scheduled** — Run instantly from the plugin page with **Run Now** (with live progress), or let the daily scheduled task (3 AM default) handle it.
- **Movies &amp; TV** — Full Series → Season → Episode hierarchy, including per-episode titles and overviews.
- **Metadata resolution chain** — NFO files → TMDB → TVDB fallback, for both movies and series.
- **Native Jellyfin integration** — Writes through Jellyfin's supported library/repository APIs, **not raw SQL**, so items, cast &amp; crew, genres, studios, images, and provider IDs all appear correctly and keep working across Jellyfin versions.
- **Plugin-friendly bulk writes** — Imports persist *quietly*: they do not raise a per-item library event for every imported episode, so other installed plugins that listen for item changes (tag caches, missing-episode providers, …) can't turn a bulk import into an hours-long crawl. Those plugins simply catch up on their own scheduled scans.
- **Fast incremental re-runs** — Items already imported with complete metadata are skipped before any network call. A full multi-thousand-item library re-scan finishes in minutes; only new, incomplete, or override-matched items do real work.
- **Resilient metadata parsing** — TMDB/TVDB responses with unexpected field types (numbers as strings, nulls, objects where arrays are expected) degrade gracefully instead of failing the whole title, and all API calls have a 30s timeout.
- **Custom overrides** — Pin exact metadata for specific titles from the web UI: title, sort/original title, year, premiere date, runtime, TMDB/TVDB/IMDB IDs, rating, genres, studios, and cast. Overrides take priority over NFO files and API lookups, and override-matched titles are always re-applied on every run.
- **Posters** — Local artwork is used when present; otherwise a poster is fetched from TMDB.
- **Purge missing** — Optionally remove library items whose files no longer exist (per library; never deletes your media).
- **Theme-aware, accessible UI** — The config page adapts to your Jellyfin theme (light or dark) and is keyboard/remote friendly. API keys are masked with Show/Copy controls, and the Import/Purge toggles are explained in plain language.

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

## How re-runs work

The importer is incremental. On each run, an item is **skipped** (no API calls, no writes) when it is already in the database with complete metadata:

- **Movies** — has an overview or provider IDs, *and* a primary image. Movies still missing a poster are retried each run so artwork that appears on TMDB later gets picked up.
- **TV** — the series and every on-disk episode exist with real titles. Episodes left with an "Episode N" placeholder by a previously failed lookup are re-fetched until they get a proper title (or a title plus overview for shows that genuinely name episodes that way).
- **Overrides** — titles matching a configured override are never skipped, so override edits always propagate on the next run.

Each item actually being imported is logged (`TV 37/1689: importing <folder>`), so a long first run is easy to follow from the Jellyfin log.

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
