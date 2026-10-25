# Mediarow 2.4.0

The server-hosted settings page now groups connections, Home rows, library management, languages, and diagnostics. It preserves the publish folder's discovery catalogs, personal Home preferences, stream recovery, episode sync, per-user track priorities, and series overrides.

The page uses native form controls, keyboard-accessible navigation and row-order buttons, and focus/spacing styles compatible with Qt 5.15's embedded browser. Saving preserves inactive-provider values and other users' preferences. Failed loads cannot overwrite settings with defaults; browser validation opens collapsed settings before focusing an invalid field.

Release safety fixes require authentication and user/library authorization before stream redirects, avoid logging private stream URLs, retain active relay capabilities during cache cleanup, recover subtitle files with indexed language suffixes, and evaluate current parental policies on each request.

The optional **Keep only what you save** policy removes idle generated STRM/NFO entries after a configurable grace period. Favorites from any user, active/paused playback, imports, and library scans protect a title. Activity tracking stays in memory. Hide watched keeps stable movie identities in one compact shared history file so cleanup does not make watched movies appear new again. Rows can be shorter or empty when most available candidates are already watched.

## Verification

- .NET 10 suite: 242 tests passed.
- Node browser-behavior suites: 46 tests passed.
- Real-browser settings check: 58 scalar controls edited, saved, and reopened; desktop/phone layout, older DOM without `replaceChildren`, section navigation, collapsed-field validation, and failed-load protection passed.
- Additional configuration tests cover catalog rows and order, language priorities for multiple users, remembered series overrides, and personal Home controls.

The automated browser check uses fixture accounts and configuration, rather than changing a live user's settings. A live save/reopen check inside Jellyfin Media Player requires signing into the local test server as an administrator. The installed player was inspected and uses Qt 5.15.2; its existing connection to another server was left unchanged.

## Local use

Open Jellyfin Media Player, connect to the local test server, and sign in as an administrator. Open **User menu → Dashboard → Plugins → Jellyfin Mediarow**. Save settings before running diagnostics or checking for new episodes, as those actions use saved server values.

The local install package is generated under `out/mediarow-2.4.0.zip`. Its root contains only `Jellyfin.Plugin.Mediarow.dll` and `meta.json`. This plugin targets Jellyfin 12 and .NET 10.

## Publishing

The repository workflow runs all five JavaScript suites and the .NET tests, then verifies that the DLL version matches the release package. A normal branch push runs CI and creates a build artifact; pushing a version tag also publishes a release and updates the plugin manifest. No push or release publication is performed during local preparation.
