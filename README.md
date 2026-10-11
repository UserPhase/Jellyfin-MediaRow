# Jellyfin Mediarow – Netflix-style Home Screen Plugin

**Jellyfin Mediarow is a Jellyfin plugin that turns the Jellyfin home screen into a Netflix-style streaming home page.** It adds horizontal Popular and genre rows with posters (Action, Comedy, Drama, Anime, and more) from Stremio catalogs such as Cinemeta or AIOMetadata. You can browse and stream them with native Jellyfin playback through AIOStreams, without importing a whole catalog into your library first.

> Built for Jellyfin 12 (.NET 10). Works in Jellyfin Web, the Android/iOS apps, Jellyfin Media Player, and TV apps such as Android TV, Tizen, webOS, Swiftfin and Findroid. See [Client support](#client-support).

<!-- Screenshot: add docs/home.png (Netflix-style Home rows) here -->

## Why Mediarow?

Most Jellyfin streaming plugins either import an entire catalog into your library or only add search results. Mediarow is built around the **home screen**:

- **Netflix-style Home rows out of the box.** Popular Movies, Popular Series, Continue Watching and up to six genre rows on the Jellyfin home page. Rows are draggable, loop, and support filter tabs for Movies, Series and Anime. No Home Screen Sections or File Transformation plugin is needed.
- **No bulk import.** Rows are virtual. A title is only saved as a small `.strm`/`.nfo` item when you open it.
- **Native Jellyfin playback.** AIOStreams sources show up as normal Jellyfin versions with full labels (quality, size, source), with direct play, remux or transcode, and resume and watched status.
- **Fast.** Rows are warmed at startup and posters are cached. Movies are pre-probed so Play starts quickly. A whole series opens in about a second with every season and episode browsable, and Next Up is correct.
- **Stremio catalog support.** Use Cinemeta, AIOMetadata or any Stremio catalog addon, or TMDB/TVDB directly.

| | Mediarow | Original Dynamic Library | Gelato |
|---|:---:|:---:|:---:|
| Netflix-style Home screen rows | Yes | No | No |
| Browse without importing the catalog | Yes | Yes | No (bulk import) |
| Native Jellyfin playback for AIOStreams | Yes | Custom playback filter | Yes |
| Stremio catalog addons | Yes | Yes | Yes |

Mediarow is a fork of [pythcon/jellyfin-dynamic-library](https://github.com/pythcon/jellyfin-dynamic-library) (MIT). The series import approach was inspired by [lostb1t/Gelato](https://github.com/lostb1t/Gelato).

**Keywords:** Jellyfin Netflix home screen, Jellyfin home page plugin, Jellyfin home sections, Jellyfin Stremio plugin, Jellyfin AIOStreams, Jellyfin streaming catalog, Jellyfin genre rows.

## Prerequisites

- Jellyfin 12.0 or later
- At least one metadata source:
  - [TVDB API key](https://thetvdb.com/api-information) - for TV shows and anime
  - [TMDB API key](https://www.themoviedb.org/settings/api) - for movies
  - Stremio catalog addon URL (e.g., Cinemeta, AIOMetadata)
- (Optional) Stream provider for playback:
  - AIOStreams addon (recommended), OR
  - Embedarr instance, OR
  - Direct URL templates to your streaming service

## Installation

### From Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Click **Add** and enter:
   - **Repository Name:** Jellyfin Mediarow
   - **Repository URL:** `https://raw.githubusercontent.com/UserPhase/Jellyfin-MediaRow/gh-pages/manifest.json`
3. Click **Save**
4. Go to the **Catalog** tab and find "Jellyfin Mediarow"
5. Click **Install**
6. Restart Jellyfin

The repository URL becomes available after the first version-tagged release finishes building. A successful build on `main` only creates a workflow artifact; it does not publish a release or a manifest. See [Release Process](#release-process) if you maintain the repository.

If the plugin does not appear, open the repository URL in a browser: it must return JSON, not a GitHub page or a 404. Also check your server version: this release targets Jellyfin 12.0 and will be hidden on Jellyfin 10.x or 11.x. GitHub Pages is not required for the raw manifest URL above.

### Manual Installation

1. Download the latest release from [GitHub Releases](https://github.com/UserPhase/jellyfin-mediarow/releases)
2. Extract the ZIP to your Jellyfin plugins directory:
   - **Linux:** `/var/lib/jellyfin/plugins/Mediarow/`
   - **Docker:** `/config/plugins/Mediarow/` (mapped volume)
   - **Windows:** `C:\ProgramData\Jellyfin\Server\plugins\Mediarow\`
3. Restart Jellyfin

## Quick Start

1. Go to **Dashboard > Plugins > Jellyfin Mediarow**
2. Choose a **Catalog Provider**:
   - **Stremio Addon**: Enter a Stremio catalog URL (e.g., `https://v3-cinemeta.strem.io`)
   - **Direct**: Enter your TVDB and/or TMDB API keys
3. (Optional) Configure a **Stream Provider** for playback:
   - **AIOStreams**: Enter your AIOStreams addon URL
   - **Direct**: Configure URL templates
   - **Embedarr**: Enter your Embedarr instance URL
4. Enable **Persistence** for native AIOStreams playback; leave it off for virtual browsing only.
5. Save and restart Jellyfin
6. Search for any movie or TV show - results will include virtual items from the configured sources

## Features

### Home row layout
- Home is a hybrid feed: separate Popular Movies and Popular Series rows, Continue Watching if enabled, and up to six selected genre rows interleaved across enabled categories in your configured genre order. Anime joins the rotation when enabled.
- Movies, Series, and Anime filter buttons display the full checked genre lineup for that category, without mixing content types. Disabled categories are not offered. Filters do not change ordinary Jellyfin library content.
- Choose Movies, Series, and Anime independently above the Home row list. Movies and Series are selected by default; Anime is opt-in and includes both anime movies and anime series.
- Check the rows you want and drag their `::` handles to arrange them. Up/Down buttons provide keyboard and mobile alternatives.
- Available genres include Action, Adventure, Drama, Comedy, Crime, Horror, Romance, Thriller, Science Fiction, Fantasy, Animation, Documentary, Mystery, Family, War, Western, and Music. Popular, Continue Watching, and Anime rows are also available, and you can add a custom genre.
- Genre availability and anime classification depend on your catalog provider's metadata. Western animation is not treated as anime just because it has an Animation genre.
- Save and return to Home to apply the layout without restarting Jellyfin. Unchecking every content category intentionally hides all virtual Home content; ordinary library content and search are unchanged.
- Drag posters sideways to browse Home rows. Overflowing rows loop seamlessly in either direction; previous/next buttons and the left/right arrow keys also work. Clicking a poster without dragging still opens its details.
- Jellyfin's own **Recently Added** row is moved below the Mediarow rows, because every title you open is persisted and would otherwise fill the top of Home. Other built-in sections such as My Media, Continue Watching and Next Up keep their position.

### Virtual Library Items
- Search returns results from your configured catalog source alongside your existing library
- Full metadata including posters, backdrops, cast, descriptions, and ratings
- Virtual items are distinguished by a `Mediarow` provider ID

### Dynamic Media Metadata
Dynamic items display rich metadata sourced from TMDB and Stremio addons:

| Field | TMDB Direct | Stremio |
|-------|:-----------:|:-------:|
| Poster / Backdrop | Yes | Yes |
| Logo | Yes | Yes |
| Tagline | Yes | Yes |
| Studios | Yes | Yes |
| Cast & Directors | Yes | Yes |
| Writers | Yes | Yes |
| Content Rating (e.g., PG-13) | Yes | — |
| Keywords / Tags | Yes | — |
| Countries | — | Yes |
| Status (Released, etc.) | Yes | Yes |

TMDB metadata is fetched in a single API call using `append_to_response` (credits, release_dates, keywords, and images).

### Catalog Providers
- **Stremio Addon**: Use Cinemeta, AIOMetadata, or any compatible Stremio addon
- **Direct APIs**: Query TVDB/TMDB directly for metadata

### Multiple Stream Providers
- **None**: Browse-only mode for metadata exploration
- **AIOStreams**: Use your configured AIOStreams Stremio addon for multi-source streaming with version selection
- **Embedarr**: Automatic STRM generation via Embedarr API
- **Direct**: Custom URL templates with placeholder support

### Persistent Library
- Optionally save virtual items as real Jellyfin library items (.strm files)
- Full playback tracking, watched status, and resume support
- Automatic detection of new episodes for ongoing series

### Anime Audio Versions
- Optional sub/dub track selection for anime content
- Configurable audio track options (e.g., "sub,dub")
- Works with Direct mode URL templates

### Automatic Subtitles
- Fetches subtitles from OpenSubtitles for virtual items
- Supports multiple languages
- Caches subtitles locally for performance

### Language Override
- Display metadata in your preferred language
- Supports 20+ languages via ISO 639-2/3 codes

## Configuration

Mediarow contains a self-contained Home runtime targeting Jellyfin 12.0 and .NET 10. It does not require separate Home Sections or File Transformation plugins for virtual rows, and it does not import the virtual catalog into Jellyfin's database.

## Virtual Home rows

Enable **Virtual Home rows** in the plugin configuration. The plugin registers Popular and configurable genre rows, including Action, Adventure, Comedy, Crime, Drama, Horror, Science Fiction, Thriller, Fantasy, Animation, Romance, Documentary, and Anime by default.

Rows are populated by the configured Mediarow provider and return the same complete `BaseItemDto` objects used by Mediarow search. Clicking a row item therefore follows the existing item lookup, metadata, image, stream, subtitle, and playback filters. Ranking uses available community ratings followed by year and name, with an optional deterministic shuffle and refresh interval.

Home rows are virtual. They do not require the catalog to be imported into a Jellyfin library. Enable persistence separately if selected movies or series should become real `.strm` library items and participate in Jellyfin's native Continue Watching and Next Up behavior.

When a series is opened for the first time, its catalog metadata is written into locked NFO files so Jellyfin's library scan does not query online providers for every episode. The series is imported directly instead of through a full library scan: like Gelato, the plugin builds every season and episode in memory with numbers, series links and catalog titles, plots and dates already set, and saves the whole tree at once. The details page opens within a second or two with all seasons and episodes browsable, and Next Up shows the first episode (or your real next episode) right away. Once the scan finishes, the series and season NFOs are unlocked and refreshed in the background to add cast, logos and season posters. Episodes keep the catalog's titles, plots and stills.

Rows are prepared in the background when Jellyfin starts, so the first Home visit does not wait on the catalog addon. Rows that load at the same time share a single manifest and catalog request. Posters are cached in server memory for up to 12 hours and in the browser for a day. Failed catalog responses are not cached.

## Client support

Jellyfin clients fall into two groups, and Mediarow serves both:

| Client | What you get |
|---|---|
| Jellyfin Web (any browser), Android and iOS apps, Jellyfin Media Player / Desktop, Xbox | The full Netflix-style Home: filter tabs, looping rows, drag and touch scrolling, and remote/arrow-key navigation |
| Android TV, Tizen (Samsung), webOS (LG)\*, Swiftfin (iOS/tvOS), Findroid, Kodi (Jellyfin add-on) | Catalogs are exposed as app libraries (for example *Popular Movies* or *Drama Movies*). Home rows depend on the client's support and settings for "Latest" media; otherwise browse the libraries. |

\* Tizen and some webOS builds ship their own copy of Jellyfin Web, so the injected Home script can't run there. They use the app libraries instead.

How it works:

- **Injected Home (web-based clients).** The script is plain ES5 and the CSS avoids newer features such as flex `gap` and `:focus-visible` selector lists, so it runs on older TV browsers. Cards are focusable: Left/Right on a remote or keyboard moves between posters and wraps at the end of a row, Up/Down follows Jellyfin's normal spatial navigation, and Enter opens the title. On touch screens the rows use native swipe scrolling. Layouts adapt for phones and tablets and use Jellyfin's TV layout and safe-area insets.
- **App libraries (native clients).** With **Show rows in TV and native apps** enabled (the default), Mediarow adds one virtual library per configured row to `/UserViews`. Each library supports the standard Latest, Items (sorting, filtering, paging) and image endpoints. Opening a title imports it like a click on the web Home, so it plays with native Jellyfin playback. Nothing is added to your real libraries until a title is opened.
- The web client already shows the injected rows, so it hides these libraries by default (it is recognised by a cookie set with the injected page). Enable **Also list these libraries in the web client** if you want them there too.
- Playback from any client requires AIOStreams with persistence enabled. The first time a title is opened it can take a few seconds while it is imported. After that it opens instantly.

### Libraries visible but no Home rows

Version 2.0.2 also fixes configuration loading in embedded browsers without `replaceChildren()`: the Home genre checkboxes and Stremio Addon URL can otherwise remain unavailable. Until updated, configure the plugin through your server's browser dashboard; the settings apply to all clients.

The Popular/genre library tiles are the native-client fallback, not the custom Home rows. Mediarow cannot force a native or bundled client to render a custom Home layout. Enable that client's Latest media/Home sections if available; some clients will only show the libraries. Real-device support is not yet verified for every client listed above.

For the full custom rows, open the web interface served by your Jellyfin server (not a separately hosted or bundled web client):

1. Restart Jellyfin after installing or updating the plugin, then hard-refresh the browser or clear the cached Jellyfin site data.
2. In Mediarow settings, enable **Virtual Home rows**, configure a catalog provider, and leave **Also list these libraries in the web client** off to avoid duplicate library tiles.
3. View the Home page's HTML source and look for `mediarow-home.js`. If it is missing, the page is not receiving the injected assets. If it is present, check the browser Network/Console for failed script, stylesheet or `Mediarow/Home/Sections` requests.
4. If using a Jellyfin Base URL such as `/jellyfin`, the script and stylesheet URLs must include that prefix. Version 2.0.2 fixes asset injection for these installations.

### General Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | true | Enable/disable the plugin |
| `CacheTtlMinutes` | 60 | How long to cache API responses |

### Catalog Provider


| Setting | Options | Description |
|---------|---------|-------------|
| `CatalogProvider` | StremioAddon, Direct | Source for metadata and search results |

| `StremioCatalogUrl` | - | Stremio addon URL (when using StremioAddon mode) |

**Stremio Addon Examples:**
```
# Cinemeta (official Stremio catalog)
https://v3-cinemeta.strem.io


# AIOMetadata (your configured instance)
https://your-aiometadata-instance.com/xxxxx
```

### Direct API Settings (when CatalogProvider=Direct)

| Setting | Description |
|---------|-------------|
| `TvdbApiKey` | TVDB v4 API key for TV shows and anime |
| `TmdbApiKey` | TMDB v3 API key for movies |
| `MovieApiSource` | API source for movies: None, TMDB, or TVDB |
| `TvShowApiSource` | API source for TV shows: None, TVDB, or TMDB |
| `MaxMovieResults` | Maximum movie search results (default: 20) |
| `MaxTvShowResults` | Maximum TV show search results (default: 20) |

### Stream Provider

| Setting | Description |
|---------|-------------|
| `StreamProvider` | None (browse only), AIOStreams, Embedarr, or Direct |

#### AIOStreams Settings

| Setting | Description |
|---------|-------------|
| `AIOStreamsUrl` | Full AIOStreams addon URL including encrypted config (e.g., `https://aiostreams.elfhosted.com/E2_xxxxx`) |

AIOStreams playback uses Jellyfin's native media-source and player pipeline, not a raw browser redirect. Enable persistence and automatic scans, set the **Movies path**, **Series path** and **Anime path**, then add those exact folders as Movies/Shows libraries. Mediarow does not append category subfolders to these settings.

The virtual Popular/genre libraries do not satisfy this requirement: add the filesystem folders under **Dashboard > Libraries**. Each path must be visible and writable inside the Jellyfin server (inside the container if using Docker), not merely on the client computer. For example, Movies path `/media/films` and Series path `/media/shows` need real library locations `/media/films` and `/media/shows`.

If every poster reports that it could not be opened, version 2.0.3 displays the server's specific error instead of replacing it with a generic persistence warning. Check that error before changing addon settings: configuration validation can fail before the addon is queried. Do not share addon URLs, API keys or access tokens when reporting an error.

Opening a virtual title enriches its metadata, saves small `.strm` and `.nfo` files, and waits for Jellyfin to assign a real library ID. Series episodes are populated before creation. Existing matching library titles are reused rather than overwritten.

Stream choices retain the addon's order. Only HTTP(S) media URLs are supported; torrent/info-hash-only and external-player entries are skipped. `notWebReady` is not treated as a reason to discard a stream: Jellyfin probes the selected file and can remux/transcode it. Streams requiring `behaviorHints.proxyHeaders.request` use a private, loopback-only relay because Jellyfin 12's probe does not forward all HTTP headers. The relay supports byte ranges and rewrites HLS playlist/segment/key URLs. Response headers intended for an addon proxy are not applied as request headers.

Streams cache for five minutes (empty results for 30 seconds), with isolation by addon configuration. The selected source is probed on opening; real audio/video/subtitle indexes, codecs, duration, and bitrate drive playback decisions. Initial probing uses a three-second media-analysis window and a 30-second request timeout so large remote files do not inherit excessively long server analysis defaults. The three-second window also applies when Jellyfin transcodes that opened source, preventing delayed HLS segments. Global settings and unrelated library sources are unchanged. Probe results cache for six hours, and opening a persisted title's details page warms the probe in the background so Play starts quickly. Changing addon configuration or stream URL does not reuse unrelated media information.

If a stream expires, reload the title after the cache expires. Missing configuration, unavailable streams, scan delays, and probe failures produce explicit errors rather than navigating to a success-looking but unplayable item. Addon URLs can contain secrets: do not paste them into public logs or issues.
`EnableHlsProbing` is retained for configuration compatibility; it does not disable native AIOStreams probing. Selected sources, including HLS, must be probed before playback can be negotiated safely.

#### Local playback testing

The local-only addon fixture in `tests/fixtures/aiostreams-addon.cjs` accepts a folder containing original generated test media: `compatible.mp4` (H264/AAC), `surround.mkv` (H264/AC3 5.1), and optionally `playlist.m3u8` plus its segments. It does not download media or require provider credentials.

```powershell
node .\tests\fixtures\aiostreams-addon.cjs 'C:\path\to\generated-media'
```

Use `http://127.0.0.1:18766/manifest.json` as the temporary AIOStreams URL and `http://127.0.0.1:18766` as the temporary catalog URL. Add separate temporary Movies and Shows libraries for the persistent `movies` and `tv` folders. The fixture exposes movies and two episodes, ordered MP4/MKV/HLS choices, byte ranges, and Referer-protected MKV/HLS media.

Check Home-to-native-item navigation, version selection, seeking, episode numbering, next-episode playback, and Jellyfin watch state. For transcoding, verify actual playback as well as FFmpeg output. An embedded browser can reject an audio decoder configuration even when probing and transcoding succeed; reproduce with an ordinary local Jellyfin file before treating that as an addon integration failure. Restore the original configuration, remove the temporary libraries, and stop the fixture afterward.

#### Embedarr Settings

| Setting | Description |
|---------|-------------|
| `EmbedarrUrl` | Embedarr instance URL |
| `EmbedarrApiKey` | Embedarr API key (if required) |
| `CreateMediaOnView` | Pre-trigger Embedarr when viewing item details (default: false) |

#### Direct URL Templates

| Setting | Description |
|---------|-------------|
| `DirectMovieUrlTemplate` | URL template for movie streams |
| `DirectTvUrlTemplate` | URL template for TV show streams |
| `DirectAnimeUrlTemplate` | URL template for anime streams |
| `ShowUnreleasedStreams` | Generate stream URLs for unreleased content (default: false) |

**Template Placeholders:**

| Placeholder | Description |
|-------------|-------------|
| `{id}` | Preferred provider ID based on config |
| `{imdb}` | IMDB ID (tt1234567) |
| `{tmdb}` | TMDB ID |
| `{tvdb}` | TVDB ID |
| `{anilist}` | AniList ID (anime only) |
| `{season}` | Season number |
| `{episode}` | Episode number |
| `{absolute}` | Absolute episode number |
| `{audio}` | Audio track (sub/dub) |
| `{title}` | URL-encoded title |

**Example Templates:**
```
# Movies
https://stream.example.com/movie/{imdb}

# TV Shows
https://stream.example.com/tv/{imdb}/{season}/{episode}

# Anime with audio selection
https://stream.example.com/anime/{anilist}/{episode}?audio={audio}
```

### Provider ID Preferences

Control which ID is used for stream lookups:

| Setting | Options | Default |
|---------|---------|---------|
| `MoviePreferredId` | IMDB, TMDB | IMDB |
| `TvShowPreferredId` | IMDB, TVDB | IMDB |
| `AnimePreferredId` | IMDB, TVDB, AniList | IMDB |

### Anime Audio Versions

| Setting | Default | Description |
|---------|---------|-------------|
| `EnableAnimeAudioVersions` | false | Show audio track selector for anime |
| `AnimeAudioTracks` | "sub,dub" | Comma-separated track options |

### Language Settings

| Setting | Options | Description |
|---------|---------|-------------|
| `LanguageMode` | Default, Override | Default uses original language from API |
| `PreferredLanguage` | ISO 639-2 code | Language code (eng, jpn, spa, fra, deu, etc.) |

### Subtitle Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `EnableSubtitles` | false | Enable automatic subtitle fetching |
| `OpenSubtitlesApiKey` | - | API key from [OpenSubtitles](https://www.opensubtitles.com/consumers) |
| `SubtitleLanguages` | "en" | Comma-separated ISO 639-1 codes (e.g., "en,es,fr") |
| `MaxSubtitlesPerLanguage` | 1 | Maximum subtitles to download per language |
| `UseJellyfinOpenSubtitlesCredentials` | true | Use credentials from Jellyfin's OpenSubtitles plugin |
| `SubtitleCachePath` | /tmp/jellyfin-mediarow-subtitles | Local path for cached subtitle files |

### Persistence Settings

Enable persistence to save virtual items as real Jellyfin library items with full playback tracking.

| Setting | Default | Description |
|---------|---------|-------------|
| `EnablePersistence` | false | Enable persistent library items (.strm files) |
| `MoviesLibraryPath` | Legacy root + movies | Exact destination for generated movies; add as a Movies library |
| `SeriesLibraryPath` | Legacy root + tv | Exact destination for generated series; add as a Shows library |
| `AnimeLibraryPath` | Legacy root + anime | Exact destination for generated anime series; add as a Shows library if enabled |
| `PersistentLibraryPath` | /mediarow | Legacy fallback retained for older configurations; no longer edited in the UI |
| `TriggerLibraryScan` | true | Automatically scan library after creating new items |
| `CreateUnreleasedMedia` | false | Create media for unreleased content |
| `CheckForUpdatesOnView` | true | Check for new episodes when viewing persisted series |
| `CleanupWatchedMovies` | false | Remove generated movie STRM/NFO files and library entries after all enabled users with access have watched |
| `CleanupCompletedSeries` | false | Remove a generated series only when Jellyfin metadata marks it Ended and all episodes are watched by all enabled users with access |

**Setting Up Persistence:**

1. Enable `EnablePersistence` in plugin settings
2. Set **Movies path**, **Series path** and **Anime path** to exact absolute writable server folders (e.g., `/media/films` and `/media/shows`, or `D:\Media\Movies` and `D:\Media\Series`)
3. Add the exact Movies path as a Movies library and the Series/Anime paths as Shows libraries; keep automatic library scanning enabled
4. When you click on a virtual item, it creates a .strm file in this folder
5. Library scan adds it as a real item with full tracking

Existing root-based settings retain their original destinations: the new fields initially display the legacy root plus `movies`, `tv` and `anime`. Saving stores those explicit paths. Changing a path does not move existing items, and cleanup only considers items in the currently configured folders. The plugin does not automatically register Jellyfin libraries.

### Optional watched-item cleanup

In Mediarow settings, enable **Remove Mediarow movies after watching** and/or **Remove fully watched, ended Mediarow series**. Both switches are **off by default**; enabling them also applies to existing eligible Mediarow items.

The **Mediarow: Clean Up Watched Items** scheduled task runs hourly, or can be run manually under **Dashboard > Scheduled Tasks**. It uses Jellyfin's watched flag, including items manually marked watched, rather than assuming that stopping playback means completion. It waits for every enabled user with access, skips favorites and active/paused playback, and leaves files modified in the last ten minutes until a later run.

Series must have a confirmed **Ended** status, every imported episode (including specials) watched, and no future or unknown episode air dates. Continuing/unknown-status series are never purged. If the metadata provider cannot confirm that a series has ended, it stays in the library.

Cleanup is restricted to immediate title folders under the configured Movies, Series and Anime paths. Every STRM must contain a Mediarow marker (including the legacy marker) and match an indexed library item. Mixed folders with real media, unindexed streams, or symlinks/junctions are refused. Only generated STRM files, their companion NFOs and series/season NFOs are removed; artwork, subtitles and other NFOs are preserved. Empty title/season directories are removed without deleting library roots.

The shared library entry is removed too, so a repeat viewing requires opening the virtual catalog title again to reimport it. This is not a watch-history backup feature; use Jellyfin's normal user-data backup or external watch tracking if retaining history is essential.

## How It Works

```
Search Query
    |
    v
SearchActionFilter intercepts request
    |
    v
Query catalog source (Stremio addon or TVDB/TMDB)
    |
    v
Create virtual BaseItemDto objects with unique GUIDs
    |
    v
Cache items in memory
    |
    v
Return results to Jellyfin UI
    |
    v
User clicks item -> ItemLookupFilter returns cached DTO
    |
    v
(Optional) PersistenceService creates .strm files
    |
    v
User plays item -> AioStreamsPlaybackFilter + DynamicAioStreamsMediaSourceProvider resolve AIOStreams sources
    |
    v
(Optional) SubtitleFilter serves cached subtitles
```

Virtual items use deterministic GUIDs generated from a unique prefix (kept from the original project so existing item IDs stay stable) to prevent collisions with real library items.

## Known Limitations

- Virtual items (without persistence) are ephemeral and exist only in memory cache
- Playback requires a configured stream provider (AIOStreams, Embedarr, or Direct URLs)
- Some metadata may be incomplete depending on API availability
- Episode translations may not be available for all languages in TVDB
- Android TV may have quirks with version selection (the plugin includes workarounds)

## Troubleshooting

### Version Selection Not Working on Android TV
The plugin includes workarounds for Android TV's version selection quirks. If you're still experiencing issues, try:
1. Clear the Jellyfin app cache on Android TV
2. Ensure you're on the latest plugin version

### Playback Fails Immediately
1. Check that your stream provider is correctly configured
2. For AIOStreams, verify your addon URL is valid and not expired
3. For Direct mode, ensure URL templates include all required placeholders

### Missing Subtitles
1. Verify OpenSubtitles API key is correct
2. Check that the language codes in `SubtitleLanguages` are valid ISO 639-1 codes
3. Subtitles may not be available for all content

## Support

- **Issues:** [GitHub Issues](https://github.com/UserPhase/jellyfin-mediarow/issues)
- **Discussions:** [GitHub Discussions](https://github.com/UserPhase/jellyfin-mediarow/discussions)

## Development

### Building Locally

```bash
dotnet build Jellyfin.Plugin.Mediarow/Jellyfin.Plugin.Mediarow.csproj -c Release
```

### Local Testing

```bash
dotnet test tests/Jellyfin.Plugin.Mediarow.Tests/Jellyfin.Plugin.Mediarow.Tests.csproj
node --test tests/home-layout.test.cjs tests/home-carousel.test.cjs tests/home-hybrid.test.cjs
```

To try it in Jellyfin, copy `Jellyfin.Plugin.Mediarow/bin/Release/net10.0/Jellyfin.Plugin.Mediarow.dll` into `<jellyfin data>/plugins/Mediarow/` and restart Jellyfin.

### Release Process

1. Update the version in `Jellyfin.Plugin.Mediarow/Jellyfin.Plugin.Mediarow.csproj` and `build.yaml`:
   ```xml
   <Version>X.Y.Z</Version>
   <AssemblyVersion>X.Y.Z.0</AssemblyVersion>
   <FileVersion>X.Y.Z.0</FileVersion>
   ```

2. Commit changes:
   ```bash
   git add -A && git commit -m "Bump version to X.Y.Z"
   ```

3. Create and push version tag:
   ```bash
   git tag vX.Y.Z && git push && git push --tags
   ```

4. GitHub Actions will automatically:
   - Build and test the plugin
   - Create a GitHub Release with `mediarow-X.Y.Z.zip`
   - Create or update `manifest.json` on the `gh-pages` branch

The workflow uses the repository it runs in, so a fork publishes its own release links. A push to `main` alone does not publish the plugin repository: a version tag such as `v2.0.0` must be pushed. Wait for both the `build` and `update-manifest` jobs to succeed, then verify the release ZIP is attached and `https://raw.githubusercontent.com/<owner>/<repository>/gh-pages/manifest.json` returns JSON. Add that raw URL under **Dashboard → Plugins → Repositories** in Jellyfin; no GitHub Pages setup is necessary.

If you prefer a Pages URL, enable GitHub Pages for the `gh-pages` branch at `/` in repository settings. Use the exact repository-name casing in `https://<owner>.github.io/<repository>/manifest.json`.

## License

MIT - See [LICENSE](LICENSE) file for details. Based on [jellyfin-dynamic-library](https://github.com/pythcon/jellyfin-dynamic-library) by pythcon.
