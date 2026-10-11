using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>Materializes one selected catalog title before native Jellyfin playback.</summary>
public sealed class PlaybackItemResolver(
    SearchResultFactory factory, PersistenceService persistence, ILibraryManager libraryManager, DynamicItemCache itemCache)
{
    private readonly SemaphoreSlim _creationLock = new(1, 1);

    public async Task<BaseItem?> ResolveAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        if (item.Type is not (BaseItemKind.Movie or BaseItemKind.Series))
        {
            throw new ArgumentException("Select a movie or series to create a playback item.", nameof(item));
        }

        var existing = persistence.FindExistingLibraryItem(item);
        if (existing != null)
        {
            return GetReadyExistingItem(existing, item);
        }

        var config = MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();
        var subfolder = item.Type == BaseItemKind.Movie ? "movies" : MediarowService.IsAnime(item) ? "anime" : "tv";
        string libraryPath = config.GetPersistencePath(subfolder);
        if (!config.EnablePersistence || !Path.IsPathFullyQualified(libraryPath) || !config.TriggerLibraryScan)
        {
            throw new InvalidOperationException("Enable persistence and automatic library scanning, and set an absolute Movies/Series/Anime path in Mediarow settings.");
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(libraryPath);
        bool ContainsRoot(string location)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            return root.Equals(path, comparison) || root.StartsWith(path + Path.DirectorySeparatorChar, comparison);
        }
        if (!libraryManager.GetVirtualFolders().Any(folder => folder.Locations.Any(ContainsRoot)))
        {
            throw new InvalidOperationException($"Add the configured {subfolder} path '{libraryPath}' as a Jellyfin library before opening this title.");
        }

        await _creationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = persistence.FindExistingLibraryItem(item);
            if (existing != null)
            {
                return GetReadyExistingItem(existing, item);
            }

            item = item.Type == BaseItemKind.Movie
                ? await factory.EnrichMovieDtoAsync(item, cancellationToken).ConfigureAwait(false)
                : await factory.EnrichSeriesDtoAsync(item, cancellationToken).ConfigureAwait(false);
            var created = item.Type == BaseItemKind.Movie
                ? await persistence.PersistMovieAsync(item, cancellationToken).ConfigureAwait(false)
                : await persistence.PersistSeriesAsync(item, cancellationToken).ConfigureAwait(false);
            if (created == null)
            {
                return persistence.FindExistingLibraryItem(item)
                    ?? throw new InvalidOperationException("The title could not be created. Check its release date, provider IDs, and available episodes.");
            }

            await persistence.AddToLibraryAsync(created, item.Type, item).ConfigureAwait(false);
        }
        finally
        {
            _creationLock.Release();
        }

        // Series are imported with all episodes at once; after a scan fallback, return once the first season is in.
        var firstSeason = item.Type == BaseItemKind.Series ? persistence.GetFirstSeasonFileCount(item) : null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            existing = persistence.FindExistingLibraryItem(item);
            if (existing != null)
            {
                if (existing is Series)
                {
                    var episodes = libraryManager.GetItemsResult(new InternalItemsQuery
                    {
                        ParentId = existing.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Episode]
                    }).Items;
                    var firstSeasonReady = firstSeason is not { } expected ||
                        episodes.Count(episode => episode is Episode e && e.ParentIndexNumber == expected.Season) >= expected.EpisodeCount;
                    var nearDeadline = deadline - DateTime.UtcNow < TimeSpan.FromSeconds(5);
                    if (episodes.Count == 0 || (!firstSeasonReady && !nearDeadline))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    // Directly imported episodes already carry the catalog metadata; only scanned ones need it here.
                    foreach (var episode in episodes.OfType<Episode>())
                    {
                        var metadata = itemCache.GetEpisodesForSeries(item.Id)?.FirstOrDefault(candidate =>
                            candidate.ParentIndexNumber == episode.ParentIndexNumber && candidate.IndexNumber == episode.IndexNumber);
                        if (metadata != null && !string.Equals(episode.Name, metadata.Name, StringComparison.Ordinal))
                        {
                            await persistence.ApplyCatalogMetadataAsync(episode, metadata, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                await persistence.ApplyCatalogMetadataAsync(existing, item, cancellationToken).ConfigureAwait(false);
                return existing;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private BaseItem? GetReadyExistingItem(BaseItem existing, BaseItemDto item)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (existing is Series && string.Equals(existing.Path, persistence.GetSeriesFolderPath(item), comparison) &&
            libraryManager.GetItemsResult(new InternalItemsQuery
            {
                ParentId = existing.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Episode]
            }).Items.Count == 0)
        {
            return null;
        }
        return existing;
    }
}
