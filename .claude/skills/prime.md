---
name: prime
description: Load full context of the Jellyfin Mediarow plugin architecture, components, and data flow
user_invocable: true
---

Read the following key files to build context of the plugin. Use parallel reads where possible.

## Step 0: Git Context

Run these git commands to understand current state and recent work:
- `git log --oneline -20` — Recent commit history to see where development stands
- `git log --oneline --since="2 weeks ago"` — What's been worked on recently
- `git status` — Any uncommitted work in progress
- `git diff --stat HEAD~5` — Files changed in last 5 commits to see active areas

## Step 1: Core Architecture (read in parallel)

Read these files to understand plugin registration and configuration:
- `Jellyfin.Plugin.Mediarow/Plugin.cs` — Plugin entry point
- `Jellyfin.Plugin.Mediarow/ServiceRegistrator.cs` — DI registration, filter ordering
- `Jellyfin.Plugin.Mediarow/Configuration/PluginConfiguration.cs` — All settings

## Step 2: ID Generation & Item Creation (read in parallel)

Read these files to understand how virtual items are created and identified:
- `Jellyfin.Plugin.Mediarow/Models/DynamicUri.cs` — Deterministic GUID generation from `dynamic://{source}/{id}` URIs using MD5 hash with prefix `"jellyfin-dynamiclibrary-plugin:"`
- `Jellyfin.Plugin.Mediarow/Services/SearchResultFactory.cs` — Converts TMDB/TVDB/Stremio API results to `BaseItemDto` objects with proper IDs, provider IDs, images, seasons, and episodes
- `Jellyfin.Plugin.Mediarow/Services/DynamicItemCache.cs` — In-memory cache (1hr TTL) keyed by GUID for items, images, seasons, episodes, subtitles, and MediaSource mappings

## Step 3: Request Interception Filters (read in parallel)

These MVC action filters intercept Jellyfin API requests. Playback of AIOStreams sources goes through the native IMediaSourceProvider in Providers/DynamicAioStreamsMediaSourceProvider.cs plus Filters/AioStreamsPlaybackFilter.cs:
- `Jellyfin.Plugin.Mediarow/Filters/SearchActionFilter.cs` — Merges dynamic search results with native Jellyfin results, deduplicates by provider ID
- `Jellyfin.Plugin.Mediarow/Filters/ItemLookupFilter.cs` — Intercepts item detail lookups, returns cached BaseItemDto with MediaSources, handles enrichment and persistence triggers
- `Jellyfin.Plugin.Mediarow/Filters/ImageFilter.cs` — Proxies poster/backdrop/logo images from TMDB/TVDB URLs stored in cache
- `Jellyfin.Plugin.Mediarow/Filters/SeasonEpisodeFilter.cs` — Returns virtual seasons/episodes for dynamic series, handles Android TV quirks
- `Jellyfin.Plugin.Mediarow/Filters/AioStreamsPlaybackFilter.cs` — Selects the native AIOStreams media source before Jellyfin negotiates client capabilities
- `Jellyfin.Plugin.Mediarow/Filters/VirtualPlaybackProgressFilter.cs` — Tracks resume progress for virtual items
- `Jellyfin.Plugin.Mediarow/Filters/SubtitleFilter.cs` — Serves cached OpenSubtitles content as WebVTT or TrackEvents JSON
- `Jellyfin.Plugin.Mediarow/Filters/DynamicItemEndpointsFilter.cs` — Returns empty responses for unsupported endpoints (similar items, themes, trailers)

## Step 4: API Clients & Controllers (read in parallel)

- `Jellyfin.Plugin.Mediarow/Api/MediarowController.cs` — Plugin API endpoints: GetItem, GetItemImage, Subtitles, PersistItem
- `Jellyfin.Plugin.Mediarow/Api/StreamController.cs` — Stream redirect endpoints for .strm files (movie/tv/anime)
- `Jellyfin.Plugin.Mediarow/Api/TmdbClient.cs` — TMDB v3 API client
- `Jellyfin.Plugin.Mediarow/Api/TvdbClient.cs` — TVDB v4 API client with bearer token auth
- `Jellyfin.Plugin.Mediarow/Api/AIOStreamsClient.cs` — Stremio addon stream queries
- `Jellyfin.Plugin.Mediarow/Api/EmbedarrClient.cs` — Embedarr STRM generation API

## Step 5: Services & Providers (read in parallel)

- `Jellyfin.Plugin.Mediarow/Services/MediarowService.cs` — Search orchestration, deduplication against existing library
- `Jellyfin.Plugin.Mediarow/Services/PersistenceService.cs` — Creates .strm files with `dynamiclibrary://` URIs for persistent library items
- `Jellyfin.Plugin.Mediarow/Services/SubtitleService.cs` — OpenSubtitles integration, SRT→WebVTT conversion
- `Jellyfin.Plugin.Mediarow/Providers/ICatalogProvider.cs` — Catalog provider interface
- `Jellyfin.Plugin.Mediarow/Providers/DirectCatalogProvider.cs` — Direct TMDB/TVDB API catalog
- `Jellyfin.Plugin.Mediarow/Providers/StremioCatalogProvider.cs` — Stremio addon catalog
- `Jellyfin.Plugin.Mediarow/Providers/CatalogModels.cs` — Unified catalog data models
- `Jellyfin.Plugin.Mediarow/Extensions/ActionContextExtensions.cs` — Helper extensions for filter context

## After reading, provide a brief summary

After reading all files, provide the user with:
1. A one-line summary of the plugin's purpose
2. Current version number
3. Summary of recent git activity — what's been worked on, any patterns in recent commits
4. Current branch and working tree status
5. Any TODO comments, known issues, or areas of active development visible in the code
6. Confirmation you're ready to work on the codebase
