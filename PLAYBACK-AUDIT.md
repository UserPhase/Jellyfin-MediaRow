# Playback Audit

Audit date: 2026-09-11

> Historical document. `PlaybackInfoFilter` and `VideoStreamFilter` described below were removed in 2.0.0; AIOStreams playback now uses the native `DynamicAioStreamsMediaSourceProvider` and `AioStreamsPlaybackFilter`.

## Executive summary

The merged plugin successfully renders Home rows using the same Mediarow virtual `BaseItemDto` objects as search. The Home runtime does not create an alternate media model or modify playback DTOs.

Virtual AIOStreams playback has no audio because the selected source is a remote MKV/Torrentio URL and the final request is handled as a raw remote stream. The plugin has no verified audio stream metadata from AIOStreams, and the live server produced no FFmpeg/ffprobe/transcode command for the failing playback. Therefore Jellyfin cannot select, map, remux, or transcode the real audio stream before the browser receives it.

The missing audio is not caused by Home sections.

## Dependency map

```text
Dynamic Home section
  Web/mediarow-home.js
  -> Api/MediarowHomeController.GetSection
  -> HomeScreen/HomeSectionManager.ExecuteAsync
  -> HomeScreen/VirtualCatalogHomeSection.GetResultsAsync
  -> Services/VirtualCatalogService
  -> Services/SearchResultFactory.CreateDtoFromCatalogItemAsync
  -> Services/DynamicItemCache.StoreItem
  -> BaseItemDto

Item click
  -> Filters/ItemLookupFilter
  -> DynamicItemCache.GetItem
  -> SearchResultFactory.EnrichMovieDtoAsync / EnrichSeriesDtoAsync
  -> item details DTO

Play
  -> Filters/PlaybackInfoFilter.OnActionExecutionAsync
  -> HandleAIOStreamsPlaybackAsync / BuildAIOStreamsSingleResponse
  -> MediaSourceInfo(Path = remote AIOStreams URL)
  -> client stream request
  -> Filters/VideoStreamFilter (raw redirect when Video endpoint is used)
  -> remote provider URL
  -> client decodes remote container/audio
```

## Virtual movie trace

1. `Web/mediarow-home.js` requests `Mediarow/Home/Section/{id}` for the signed-in user.
2. `MediarowHomeController` calls `HomeSectionManager.ExecuteAsync`.
3. `VirtualCatalogHomeSection` calls `VirtualCatalogService`, which calls the selected catalog provider and `SearchResultFactory.CreateDtoFromCatalogItemAsync`.
4. `SearchResultFactory` builds a deterministic virtual `BaseItemDto`: `Id`, provider IDs, title, poster/backdrop tags, genres, rating, and initial `UserData`. It does not have a real file path, container, chapters, or verified audio streams at this point.
5. The same DTO is stored in `DynamicItemCache`; Home rows do not replace it.
6. Opening the item reaches `ItemLookupFilter`, which returns/enriches the cached DTO. For AIOStreams it can attach candidate `MediaSourceInfo` entries, but the source metadata is inferred from title/filename rather than probed codecs.
7. Playback reaches `PlaybackInfoFilter`. For a selected AIOStreams media source it constructs `MediaSourceInfo` with the remote stream URL and detected container.
8. The failing live movie logged `BuildAIOStreamsSingleResponse for Man of Steel ... Container=mkv`.
9. A later stream request may enter `VideoStreamFilter`, which redirects it to the raw AIOStreams URL when the client uses the Videos endpoint.
10. No live FFmpeg/ffprobe/transcoding command was emitted for the failing playback.

## BaseItemDto and media metadata

`SearchResultFactory.CreateDtoFromCatalogItemAsync` correctly supplies the virtual identity and catalog metadata required by `ItemLookupFilter` and `PlaybackInfoFilter`:

- `Id`: deterministic `DynamicUri` GUID
- `Type`: Movie or Series
- `MediaType`: Video only for playable movies; series is a folder
- `ProviderIds`: Mediarow plus IMDB/TMDB/TVDB where present
- images: tags and URLs stored in `DynamicItemCache`
- `UserData`: initial zero position

It does not, and cannot, know the selected AIOStreams file's audio codec, stream index, channels, sample rate, language, or bitrate because the Stremio/AIOStreams stream model provides only `name`, `title`, `url`, `behaviorHints`, and optional filename.

`AIOStreamsModels.AIOStream` has no audio stream fields. Consequently there is no verified audio metadata to place in `MediaSourceInfo.MediaStreams`.

## Audio diagnosis

1. Does the virtual movie expose an audio stream? **Not reliably.** The catalog DTO has no audio stream. The AIOStreams response also provides no codec/index metadata.
2. Does Jellyfin receive correct audio metadata? **No.** For the selected AIOStreams remote URL, it receives container/title/URL but not a verified audio stream.
3. Is an audio stream selected? **No verified selection occurs.**
4. Is the real audio stream included in MediaSource? **No verified `MediaStreamType.Audio` entry is available.**
5. Is it included in PlaybackInfo? **Only the remote source is included; no actual audio mapping is available.**
6. Is audio mapped by FFmpeg? **No.** The live logs contain no FFmpeg command for the failing virtual movie.
7. Is the selected audio codec valid? **Unknown.** It is not exposed by AIOStreams metadata and was not probed by Jellyfin.
8. Is the output format valid? **The client is handed a raw MKV source.** Output compatibility depends on the source's actual audio codec.
9. Does the client support the resulting audio format? **Not guaranteed.** Browsers commonly cannot decode DTS, TrueHD, some E-AC3 profiles, or other torrent release audio tracks.
10. Where is audio lost? **At the remote raw-MKV handoff to the client, after `PlaybackInfoFilter` and before any Jellyfin FFmpeg processing.**

## Direct Play / Direct Stream / Transcoding

The current virtual AIOStreams route does not reach a Jellyfin-managed FFmpeg transcode session in the observed failure. `VideoStreamFilter` contains raw AIOStreams redirects for `Videos` requests. When the selected source is returned directly, the browser is responsible for decoding the remote MKV audio.

The plugin's media-source flags alone cannot create an FFmpeg session: Jellyfin's native transcoding pipeline expects a resolvable media source backed by the server media-source manager/library path. The virtual DTO/filter route returns a remote URL without verified stream data and can subsequently redirect it.

## FFmpeg audit

No FFmpeg command was generated for the failing playback. The live evidence has only the server startup line:

```text
FFmpeg: /usr/lib/jellyfin-ffmpeg/ffmpeg
```

and playback lines such as:

```text
PlaybackInfoFilter: Processing PlaybackInfo for item ...
BuildAIOStreamsSingleResponse for Man of Steel: RunTimeTicks=85800000000, Container=mkv
```

There is no `-map`, no `-an`, no selected audio stream index, no audio codec, and no FFmpeg error to inspect because FFmpeg was never involved.

## Home isolation audit

The internal Home implementation is isolated from playback:

- `mediarow-home.js` requests section DTOs and creates card links only.
- `MediarowHomeController`, `HomeSectionManager`, and `VirtualCatalogHomeSection` return the established `BaseItemDto` objects.
- Home code does not set `MediaSources`, `MediaStreams`, stream indexes, source URLs, or playback sessions.

There is no Home-specific alternate playback path.

## Original Dynamic Library comparison

Playback-relevant current differences from `v1.4.0`:

| Difference | Classification | Effect |
|---|---|---|
| Internal Home manager/controllers/web assets | SAFE | Produces existing virtual DTOs; does not alter playback. |
| Virtual resume tracker and playback progress filter | POTENTIAL REGRESSION | Adds disk writes and snapshot state; independent of audio. |
| Auto-persist-on-play attempt in `PlaybackInfoFilter` | POTENTIAL REGRESSION | Fire-and-forget and not guaranteed to complete before first playback. |
| AIOStreams compatibility ordering by title | UNRELATED | May alter default source choice; cannot provide verified codec data. |
| AIOStreams `MediaStreams` changed from guessed H264/AAC to subtitles-only | LIKELY REGRESSION FOR CLIENT COMPATIBILITY, but not proven root cause | Removes false metadata; without server probing/transcoding it also gives the client no audio track metadata. |
| AIOStreams raw URL redirects in `VideoStreamFilter` | PRE-EXISTING / LIKELY ROOT CAUSE | Bypasses Jellyfin-managed probing/transcoding and hands remote MKV to the client. |

## Jellyfin 12 compatibility

The current project targets `net10.0` and Jellyfin `12.0.0`. Build succeeds against the Jellyfin 12 packages.

The MVC filters compile against the current controller/action APIs. The weak point is architectural rather than a renamed API: virtual remote URLs returned by a filter are not registered as Jellyfin media-source-provider objects, so Jellyfin does not own enough information to safely probe and transcode them.

## Root cause

Primary root cause: **AIOStreams virtual playback is delivered as a raw remote MKV URL without actual media-stream metadata or a Jellyfin-managed FFmpeg session.** Browser decoding therefore fails when the chosen source audio codec is unsupported.

Alternative causes, lower confidence:

1. The selected AIOStreams/Torrentio release itself has no audio track.
2. The provider requires headers/cookies that are not forwarded on the raw redirect.
3. The selected source's audio track is disabled in the browser/device.

The absence of an FFmpeg command and the logged remote MKV container rule out FFmpeg audio mapping as the present failure.

## Minimal corrective architecture

The correct fix is not another flag or hard-coded codec. Register an internal `IMediaSourceProvider` for persisted Mediarow `.strm` items (or otherwise provide a server-owned media source recognized by Jellyfin's `IMediaSourceManager`). The provider must return the remote AIOStreams URL with its required request headers and allow Jellyfin's native probe/transcode pipeline to inspect the actual streams.

For the initial virtual playback, either:

1. Persist the selected movie/parent series before returning playback info, wait until the item is indexed, then play the real library item; or
2. Implement a full Mediarow `IMediaSourceProvider` capable of resolving the virtual identity directly.

Option 2 is the smallest correct long-term design. It preserves dynamic catalog semantics while allowing Jellyfin to request actual media streams and invoke FFmpeg only when device capabilities require it.

## Jellyfin 12 media-source provider feasibility addendum

The Jellyfin 12 source was inspected after this report's initial trace. It establishes an additional hard boundary for a plugin-only DTO-only implementation.

```text
MediaInfoController.GetPlaybackInfo(itemId, userId)
  -> ILibraryManager.GetItemById<BaseItem>(itemId, user)
  -> returns HTTP 404 when no BaseItem exists
  -> MediaInfoHelper.GetPlaybackInfo(item, ...)
  -> IMediaSourceManager.GetPlaybackMediaSources(item, ...)
  -> Mediarow IMediaSourceProvider.GetMediaSources(item, ...)
```

`IMediaSourceProvider` is the correct native hook only after Jellyfin has a real `BaseItem`. Its provider method accepts `BaseItem`, not a `BaseItemDto` or GUID. For current Mediarow Home/search entries, `ILibraryManager.GetItemById` returns null because the item is intentionally DTO-only and virtual. Consequently the controller returns 404 before `MediaSourceManager` invokes any provider.

Provider discovery is native and requires no external plugin: Jellyfin 12 `ApplicationHost.FindParts` calls `IMediaSourceManager.AddParts(GetExports<IMediaSourceProvider>())`. However, registration does not remove the required `BaseItem` lookup.

For an already-real item, a Mediarow provider could return a remote HTTP `MediaSourceInfo`. `MediaSourceManager` adds a provider-specific MD5 prefix to `OpenToken`/`LiveStreamId`, later calls `OpenMediaSource`, and probes open streams when `SupportsProbing` is true and stream indexes are unknown. Remote FFmpeg headers come from `MediaSourceInfo.RequiredHttpHeaders`: `EncodingHelper.AttachMediaSourceInfo` copies them into `EncodingJobInfo.RemoteHttpHeaders`, which is used to form FFmpeg input headers such as User-Agent and Referer. `ILiveStream` supplies the opened stream lifecycle; it is not itself a header container.

Therefore the requested provider design cannot be activated for the existing DTO-only virtual GUID without one of these architectural changes:

1. Allow an on-demand real Jellyfin `BaseItem`/`.strm` entry for a selected item and let it retain the Mediarow provider identity. This is plugin-only but no longer DTO-only.
2. Modify Jellyfin server source to add a virtual-item resolver before `ILibraryManager.GetItemById` in the MediaInfo controller flow. This preserves DTO-only identity but is a Jellyfin-core change, not an installable-plugin-only solution.

Creating an `IMediaSourceProvider` without either change would compile and be discovered but would never be called for Mediarow virtual Home/search items.

## Live environment and materialization validation

The Jellyfin 12 test environment was configured through its authenticated APIs using only dedicated Mediarow paths:

- `EnablePersistence = true`
- `PersistentLibraryPath = /mediarow`
- `TriggerLibraryScan = true`
- `Mediarow Movies`: `movies`, `/mediarow/movies`, library ID `e1a762fd7358d40db492e34163bb6c0c`
- `Mediarow TV`: `tvshows`, `/mediarow/tv`, library ID `a1cf0b5446584acc8f09233eef76f1d4`
- `Mediarow Anime`: `tvshows`, `/mediarow/anime`, library ID `a194c7d444571579f44a145c3d82cd60`

The container process can write these folders. However, `/mediarow` is currently container-layer storage rather than a Docker bind mount or named volume, so it survives server restart but not container recreation.

One virtual movie was materialized through the existing `PersistenceService`:

```text
Virtual GUID: d578c4d5-53c9-4a2a-2e2b-69b7d27d17f0
IMDB ID: tt0111161
STRM: /mediarow/movies/The Shawshank Redemption (1994) [imdbid-tt0111161]/The Shawshank Redemption (1994).strm
STRM contents: dynamiclibrary://movie/tt0111161
Indexed BaseItem ID: 9b1c6087-b478-3adb-59f5-9ae13546a456
Type: Movie
Provider IDs: Imdb=tt0111161, Tmdb=278
```

The indexed BaseItem ID remained unchanged after an explicit library refresh and Jellyfin restart. It is stable for this path, but it is not equal to the virtual GUID because Jellyfin computes scanned item IDs from the concrete entity type and filesystem path.

Jellyfin's `ProbeProvider` logged that `dynamiclibrary://movie/tt0111161` is an invalid/non-remote `.strm` shortcut path. The plugin's legacy `PlaybackInfoFilter` then provides the remote stream manually. This means the existing `.strm` format does not by itself activate native remote probing.

### Current plugin-only limitation

The requested strict sequence cannot be completed exactly with the existing `.strm` format and without a playback boundary adapter:

```text
Virtual GUID -> indexed path-derived BaseItem -> native provider
```

requires a durable virtual-GUID-to-BaseItem-ID mapping and a safe transition before the client asks for `PlaybackInfo`/`Videos` using the virtual GUID. Jellyfin's native controllers do not perform that mapping.

In addition, the scanned `.strm` retains a static filesystem media source whose custom `dynamiclibrary://` shortcut is invalid to Jellyfin's probe provider. A Mediarow provider can add a remote source for that BaseItem, but it does not automatically remove or supersede the static shortcut source.

Consequently, implementing an `IMediaSourceProvider` alone at this point would not meet the stated acceptance criteria. The remaining plugin-only designs require either a controlled playback-ID translation layer for on-demand materialized items, or a different real BaseItem representation that has no competing invalid `.strm` static source. Both need explicit compatibility testing against Jellyfin Web's subsequent `/Videos` streaming request.

## Secondary issues

- `MediarowHomeController` trusts client-provided user IDs; it should derive the user from authenticated claims.
- `VirtualResumeTracker` persists complete DTO snapshots and synchronously reads/writes JSON on playback reports. Stream URLs should be excluded from snapshots, and updates should be atomic/batched.
- `HomeSectionUserSettingsStore` writes under a lock synchronously; use atomic writes and reduce lock-held I/O.
- Custom `HomeSectionDefinition.Parameters` are persisted but currently ignored by `VirtualCatalogHomeSection`.
- Home row requests are sequential in `mediarow-home.js`.
- `mediarow-home.js` uses the deprecated `#!/details?id=` form rather than the Jellyfin router's current route builder.
- `PersistenceService` needs a configured writable path that is actually added as a Jellyfin library. This was not true in the test container, so native persistence/resume cannot be validated there.

## Validation performed

- Jellyfin 12/.NET 10 plugin build: passed.
- Current focused unit tests: passed (3 tests).
- Live Jellyfin 12 server: Home rows and virtual cards rendered.
- Live failing playback: captured `PlaybackInfoFilter` and selected AIOStreams mapping logs.
- Live FFmpeg audit: no FFmpeg/ffprobe/transcode command for the failing stream.
- Live container source: logged selected container was MKV.
- No external Home Screen Sections or File Transformation dependency participates in the virtual item playback path.

## Native Playback Validation

Validation date: 2026-09-11

Runtime build/deploy state:

- Containerized `.NET 10` plugin build: passed with 0 warnings and 0 errors.
- Rebuilt plugin DLL deployed to `jellyfin-test:/config/plugins/Jellyfin.Plugin.Mediarow/Jellyfin.Plugin.Mediarow.dll`.
- Jellyfin 12 restarted cleanly and loaded `Jellyfin.Plugin.Mediarow, Version=1.2.4.0`.
- No Jellyfin server source, playback controller, provider logic, FFmpeg setting, codec setting, or source metadata changes were made during this validation pass.

Chosen validation movie:

```text
Title: The Shawshank Redemption
Virtual Mediarow GUID: d578c4d5-53c9-4a2a-2e2b-69b7d27d17f0
Virtual route ID form used by Jellyfin Web: d578c4d553c94a2a2e2b69b7d27d17f0
Provider IDs: Imdb=tt0111161, Tmdb=278
Known materialized STRM path: /mediarow/movies/The Shawshank Redemption (1994) [imdbid-tt0111161]/The Shawshank Redemption (1994).strm
STRM content: dynamiclibrary://movie/tt0111161
Real materialized BaseItem ID: 9b1c6087-b478-3adb-59f5-9ae13546a456
Real route ID form used by Jellyfin Web: 9b1c6087b4783adb59f59ae13546a456
```

Virtual to real resolution:

```json
{
  "ItemId": "9b1c6087b4783adb59f59ae13546a456",
  "Resolved": true,
  "Created": false
}
```

Confirmed:

```text
playableItemId != virtualId
9b1c6087-b478-3adb-59f5-9ae13546a456 != d578c4d5-53c9-4a2a-2e2b-69b7d27d17f0
```

Real item lookup confirmed:

```text
Id: 9b1c6087b4783adb59f59ae13546a456
Name: The Shawshank Redemption
Type: Movie
ProviderIds: Imdb=tt0111161, Tmdb=278
Path: /mediarow/movies/The Shawshank Redemption (1994) [imdbid-tt0111161]/The Shawshank Redemption (1994).strm
```

Browser request/navigation sequence observed from Jellyfin Web:

```text
Start: http://127.0.0.1:8097/web/#/home
Mediarow Home virtual card href: #!/details?id=d578c4d553c94a2a2e2b69b7d27d17f0
After virtual-card click with redeployed mediarow-home.js:
  http://127.0.0.1:8097/web/#/details?id=9b1c6087b4783adb59f59ae13546a456
```

This proves the browser switched from the virtual GUID to the real BaseItem ID for the Mediarow Home card. A prior click before reloading the Web app used the stale injected script and navigated to the virtual details URL, producing 404s; after a clean Home load, the same virtual card resolved to the real item.

No browser playback request using the virtual GUID was observed after the clean resolved click. The subsequent native details/play attempts referenced the real ID, then failed inside Jellyfin Web before issuing a native playback request.

Provider invocation:

```text
[13:18:37.530] Jellyfin.Plugin.Mediarow.Providers.DynamicAioStreamsMediaSourceProvider:
[Mediarow] Provider returned 105 native AIOStreams sources for The Shawshank Redemption
```

This confirms `MediaSourceManager` reached `DynamicAioStreamsMediaSourceProvider` for the real BaseItem during native `PlaybackInfo` retrieval.

Native `PlaybackInfo` source summary:

```text
MediaSource count: 106
First returned source:
  Id: 9b1c6087b4783adb59f59ae13546a456
  Name: [RD?] Torrentio
  URL host: torrentio.strem.fun
  URL path extension: .avi
  URL SHA-256 prefix: 0EF21AA043BFEA29
  Container: avi
  SupportsProbing: true
  RequiresOpening: false
  OpenToken: null
  LiveStreamId: null
  RequiredHttpHeaders: {}
  SupportsTranscoding: true
  SupportsDirectPlay: false
  SupportsDirectStream: true
  MediaStreams: provider placeholder Video h264 index 0 1920x1080, provider placeholder Audio aac index 1 2ch

Static STRM source also present:
  Id: 9b1c6087b4783adb59f59ae13546a456
  Name: The Shawshank Redemption (1994)
  Container: strm
  MediaStreams: []

Observed MKV AIOStreams/Torrentio candidate:
  Id: bc1114909841a4ae11a27c34aff3f9c4
  Name: [RD?] Torrentio
  URL host: torrentio.strem.fun
  URL path extension: .mkv
  URL SHA-256 prefix: 6BC3D3C17C9D4DC2
  Container: mkv
  SupportsProbing: true
  RequiresOpening: false
  OpenToken: null
  LiveStreamId: null
  RequiredHttpHeaders: {}
  SupportsTranscoding: true
  SupportsDirectPlay: false
  SupportsDirectStream: true
  MediaStreams: provider placeholder Video h264 index 0 1920x1080, provider placeholder Audio aac index 1 2ch
```

Important limitation: these `MediaStreams` are the provider placeholder streams from the current implementation, not actual probed source streams. No guessed audio/video metadata was added during validation. The required proof is whether Jellyfin probes the real remote source during playback and discovers actual streams; that proof was not reached.

Browser native Play result:

```text
Attempted real details route:
  http://127.0.0.1:8097/web/#/details?id=9b1c6087b4783adb59f59ae13546a456&serverId=3ed8e596ce194f8a9caeac78fa3b4458

Jellyfin Web console error:
  failed to get item or current user: Error: item or serverId cannot be null

Attempted direct real details route:
  http://127.0.0.1:8097/web/#/details?id=9b1c6087b4783adb59f59ae13546a456

Jellyfin Web console error:
  failed to get item or current user: Error: item or serverId cannot be null

Attempted native Home card Play for the materialized item.

Jellyfin Web page error:
  Error: item or serverId cannot be null
```

Server log after native Play attempt:

```text
[13:20:01.180] Jellyfin.Plugin.Mediarow.Filters.ItemLookupFilter:
[Mediarow] ItemLookup: Resolved AIOStreams MediaSource ID 9b1c6087b4783adb59f59ae13546a456 to Item 9b1c6087-b478-3adb-59f5-9ae13546a456
[13:20:01.180] Jellyfin.Plugin.Mediarow.Filters.ItemLookupFilter:
[Mediarow] ItemLookup: Found persisted library item The Shawshank Redemption for AIOStreams MediaSource
```

No native playback request was observed from the browser Play attempt:

```text
No /Items/9b1c6087b4783adb59f59ae13546a456/PlaybackInfo request from browser Play
No /Videos/9b1c6087b4783adb59f59ae13546a456/... stream request
No provider invocation caused by browser Play
No probe result for the selected remote source
No FFmpeg command
No playback session
```

Probe results:

```text
Not reached.
No actual discovered Video codec/index/width/height was emitted for the selected AIOStreams source during browser playback.
No actual discovered Audio codec/index/channels/sample-rate/bitrate/language was emitted for the selected AIOStreams source during browser playback.
```

Playback mode:

```text
Not reached.
No Direct Play, Direct Stream/remux, or Transcoding decision was logged for a browser playback session.
```

FFmpeg command:

```text
Not reached.
No FFmpeg process/log was produced for this browser playback attempt.
No input URL, -map options, selected video stream, selected audio stream, output audio codec, or output container could be captured.
```

Audible playback result:

```text
Not passed.
Video playback did not start from Jellyfin Web during this validation attempt.
Audible audio, sync, and seeking could not be validated.
```

Additional requested checks:

```text
Known Direct Play source: not executed because Jellyfin Web playback could not start.
Multiple audio tracks: not executed because Jellyfin Web playback could not start.
Embedded/forced subtitles: not executed because Jellyfin Web playback could not start.
Restart validation: partially already known for materialized ID stability; playback after restart not validated because Jellyfin Web playback could not start.
Duplicate prevention: partially demonstrated for the selected Home item resolving to the existing real BaseItem with Created=false; Search and direct catalog paths not re-tested in this pass.
Continue Watching: not executed because Jellyfin Web playback could not start.
```

Initial failure classification:

```text
J. Browser/client playback issue, later refined during Web route validation.
```

Evidence for classification:

- Virtual to real resolution succeeded.
- Browser switched the Mediarow Home virtual card to the real BaseItem ID.
- Real item lookup succeeded and returned the expected movie and provider IDs.
- `DynamicAioStreamsMediaSourceProvider` was invoked through native `PlaybackInfo` and returned native AIOStreams sources.
- The browser details/play surfaces then failed client-side with `item or serverId cannot be null` before emitting a native browser playback request.
- No source probing, FFmpeg command, or audible playback session occurred.

Definition of done status:

```text
Home virtual movie -> virtual GUID -> resolve endpoint -> real BaseItem ID -> Jellyfin Web uses real ID: PASSED
DynamicAioStreamsMediaSourceProvider invoked through native PlaybackInfo: PASSED
AIOStreams source opened -> real media streams discovered -> playback mode chosen -> video plays -> AUDIO PLAYS: NOT REACHED
```

### Web route and item-state validation

Validation date: 2026-09-11

Normal Jellyfin control case:

```text
Control media file: /media/Native Control Movie (2026)/Native Control Movie (2026).mp4
Indexed control item ID: d11678b2-9967-ab03-f665-205eee1ffdc3
Control details URL: http://127.0.0.1:8097/web/#/details?id=d11678b29967ab03f665205eee1ffdc3
serverId in URL: no
Item DTO ServerId: 3ed8e596ce194f8a9caeac78fa3b4458
Item DTO MediaType: Video
Control Play result: Jellyfin Web started native playback.
```

Control server evidence:

```text
[13:26:12.684] Jellyfin.Api.Helpers.MediaInfoHelper: User policy for root. EnablePlaybackRemuxing: True EnableVideoPlaybackTranscoding: True EnableAudioPlaybackTranscoding: True
[13:26:13.356] Emby.Server.Implementations.Session.SessionManager: User root started playback of 'Native Control Movie (2026)' (Jellyfin Web 12.0.0)
```

Jellyfin Web 12 route/playback implementation findings:

```text
Normal details route shape: #/details?id={itemId}
Jellyfin-generated library card links may include serverId, but serverId is not required in the URL when the loaded item DTO has ServerId.
The details page loader chooses the API client from route serverId if present, otherwise currentApiClient().
The native Play button passes the loaded item to playbackManager.play({ items: [item], ... }).
The playback manager calls getApiClient(item.ServerId) before native playback setup.
```

Exact `item or serverId cannot be null` throw site:

```text
Bundle: /jellyfin/jellyfin-web/main.jellyfin.bundle.js
Function: server/API-client manager getApiClient(e)
Throw condition: !e
Error: item or serverId cannot be null
Immediate caller during Play: playback manager q(items, options)
Missing value: item.ServerId on the item object passed to Play
Failure phase: playback setup before native PlaybackInfo request
```

Root cause proved by DTO comparison before the fix:

```text
Normal control item /Items/d11678b29967ab03f665205eee1ffdc3:
  ServerId: 3ed8e596ce194f8a9caeac78fa3b4458
  MediaType: Video

Dynamic real item /Items/9b1c6087b4783adb59f59ae13546a456:
  ServerId: null
  MediaType: Unknown

Dynamic selected-source item /Items/4f5b5c55c438e792ec57797afb915b62:
  ReturnedId: 9b1c6087b4783adb59f59ae13546a456
  ServerId: null
  MediaType: Unknown
```

Precise cause:

```text
ItemLookupFilter treated AIOStreams MediaSource IDs as item lookups and returned hand-built BaseItemDto objects that omitted ServerId and MediaType.
Additionally, the first provider MediaSource can share identity with the real BaseItem ID, so /Items/{realId} had to prefer the real library item path before attempting AIOStreams MediaSource-ID resolution.
Dynamic Home also used deprecated #!/details links; the corrected route is #/details?id={id}, matching the normal Jellyfin Web 12 control route.
```

Fix applied:

```text
ItemLookupFilter now checks for an actual library item before treating an ID as an AIOStreams MediaSource ID.
Hand-built DTOs for selected MediaSource-ID lookups now include:
  ServerId = IServerApplicationHost.SystemId
  MediaType = Video
mediarow-home.js now uses #/details?id={id} for card hrefs and resolved navigation.
No provider logic, AIOStreams resolver, MediaSourceInfo flags, probing behavior, FFmpeg settings, codec settings, or audio metadata were changed.
```

DTO comparison after the fix:

```text
/Items/9b1c6087b4783adb59f59ae13546a456:
  ReturnedId: 9b1c6087b4783adb59f59ae13546a456
  Name: The Shawshank Redemption
  Type: Movie
  ServerId: 3ed8e596ce194f8a9caeac78fa3b4458
  MediaType: Video
  MediaSourceCount: 105
  FirstMediaSourceId: 4f5b5c55c438e792ec57797afb915b62

/Items/4f5b5c55c438e792ec57797afb915b62:
  ReturnedId: 9b1c6087b4783adb59f59ae13546a456
  Name: The Shawshank Redemption
  Type: Movie
  ServerId: 3ed8e596ce194f8a9caeac78fa3b4458
  MediaType: Video
  MediaSourceCount: 1
  FirstMediaSourceId: 4f5b5c55c438e792ec57797afb915b62
```

Stale JavaScript check:

```text
Live /Mediarow/Home/mediarow-home.js was fetched with a cache-busting validation query.
The served script contained #/details navigation and no #!/details links.
Only the Mediarow Home integration root [data-mediarow-home="true"] was observed in the page snapshot.
```

Final Web/router validation result:

```text
Dynamic Home
  -> virtual GUID d578c4d5-53c9-4a2a-2e2b-69b7d27d17f0
  -> POST /Mediarow/Playback/Resolve/{virtualId}
  -> real BaseItem ID 9b1c6087-b478-3adb-59f5-9ae13546a456
  -> Jellyfin Web route #/details?id=9b1c6087b4783adb59f59ae13546a456
  -> real item details page mounted with Play button and Version selector
  -> Play
  -> selected AIOStreams MediaSource lookup /Items/4f5b5c55c438e792ec57797afb915b62
  -> DynamicAioStreamsMediaSourceProvider invoked through native PlaybackInfo path
```

Post-fix server evidence:

```text
[13:32:34.937] ItemLookupFilter: Resolved AIOStreams MediaSource ID 4f5b5c55c438e792ec57797afb915b62 to Item 9b1c6087-b478-3adb-59f5-9ae13546a456
[13:32:34.937] ItemLookupFilter: Found persisted library item The Shawshank Redemption for AIOStreams MediaSource
[13:32:35.690] DynamicAioStreamsMediaSourceProvider: [Mediarow] Provider returned 105 native AIOStreams sources for The Shawshank Redemption
```

The previous `item or serverId cannot be null` browser error did not recur after the DTO state fix. The Web/router step definition is met through native PlaybackInfo/provider invocation. The media/audio validation remains the next phase; this pass did not claim source probing, FFmpeg mapping, video playback, or audible audio success.
