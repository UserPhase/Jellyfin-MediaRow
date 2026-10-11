# Third-Party Notices

## Home-screen architecture reference

The internal Mediarow Home runtime was designed after inspecting the public `IAmParadox27/jellyfin-plugin-home-sections` repository.

No source files, GPL-licensed implementation files, embedded assets, or translations from that repository are copied into this project. The internal section manager, controller endpoints, configuration model, web assets, and startup integration are independently implemented for Mediarow and use Mediarow's existing virtual `BaseItemDto` pipeline.

The reference repository is licensed under GPL-3.0. Its license and source remain separate from this project and are not redistributed by the Mediarow build.

## Dynamic Library (pythcon)

Jellyfin Mediarow started as a fork of [pythcon/jellyfin-dynamic-library](https://github.com/pythcon/jellyfin-dynamic-library), licensed under MIT. The catalog providers, metadata pipeline and persistence code originate from that project. Its copyright notice is retained in `LICENSE`.

## Mediarow

Mediarow source remains covered by the MIT license in `LICENSE`.
