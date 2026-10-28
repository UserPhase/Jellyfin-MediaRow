using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Tells which titles a user has fully watched.
/// </summary>
public interface IWatchedTitleLookup
{
    IReadOnlySet<Guid> GetWatchedItemIds(Guid userId, IReadOnlyList<BaseItemDto> items);
}

/// <summary>
/// Virtual titles only gain watch state once imported, so this reads the user's played flag from the
/// matching library item: same id first, otherwise the same IMDb/TMDB/TVDB id.
/// Movies count as watched when played. Series need an ended status and a complete, recently
/// checked episode inventory; a played flag on a partially imported show proves nothing.
/// </summary>
public sealed class LibraryWatchedTitleLookup : IWatchedTitleLookup
{
    private static readonly string[] ProviderNames = ["Imdb", "Tmdb", "Tvdb"];

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<LibraryWatchedTitleLookup> _logger;
    private readonly IMovieWatchHistory? _movieHistory;
    private readonly EpisodeSyncService? _episodeSync;

    public LibraryWatchedTitleLookup(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<LibraryWatchedTitleLookup> logger,
        IMovieWatchHistory? movieHistory = null,
        EpisodeSyncService? episodeSync = null)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
        _movieHistory = movieHistory;
        _episodeSync = episodeSync;
    }

    public IReadOnlySet<Guid> GetWatchedItemIds(Guid userId, IReadOnlyList<BaseItemDto> items)
    {
        var candidates = items.Where(item => item.Type is BaseItemKind.Movie or BaseItemKind.Series).ToArray();
        User? user = candidates.Length == 0 ? null : _userManager.GetUserById(userId);
        if (user == null)
        {
            return new HashSet<Guid>();
        }

        var retainedMovieIds = new HashSet<Guid>();
        try
        {
            if (_movieHistory != null)
            {
                // One initialization per account, then immutable in-memory lookups. Retains identities
                // when unsaved-title cleanup removes the native library entry.
                var snapshot = _movieHistory.GetSnapshotAsync(user, CancellationToken.None).GetAwaiter().GetResult();
                retainedMovieIds.UnionWith(candidates.Where(snapshot.IsWatched).Select(item => item.Id));
                candidates = candidates.Where(item => item.Type != BaseItemKind.Movie).ToArray();
                if (candidates.Length == 0) return retainedMovieIds;
            }
            List<BaseItem> libraryItems = FindLibraryItems(candidates);
            if (libraryItems.Count == 0)
            {
                return retainedMovieIds;
            }

            var userData = _userDataManager.GetUserDataBatch(libraryItems, user);
            var finishedSeries = GetFinishedSeriesIds(libraryItems.OfType<Series>().ToArray(), user);
            var played = libraryItems
                .Where(item =>
                {
                    userData.TryGetValue(item.Id, out var data);
                    return item is Series ? finishedSeries.Contains(item.Id) : data?.Played == true;
                })
                .ToArray();
            var playedIds = played.Select(item => item.Id).ToHashSet();
            var playedKeys = played
                .SelectMany(item => CatalogDuplicateFilter.GetKeys(item.GetBaseItemKind(), item.ProviderIds))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            retainedMovieIds.UnionWith(candidates
                .Where(item => playedIds.Contains(item.Id) ||
                    CatalogDuplicateFilter.GetKeys(item.Type, item.ProviderIds).Any(playedKeys.Contains))
                .Select(item => item.Id));
            return retainedMovieIds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mediarow] Could not read watched state for Hide watched");
            return retainedMovieIds;
        }
    }

    private HashSet<Guid> GetFinishedSeriesIds(IReadOnlyList<Series> series, User user)
    {
        var finished = new HashSet<Guid>();
        if (series.Count == 0 || _episodeSync == null) return finished;

        DateTime now = DateTime.UtcNow;
        var inventory = _episodeSync.GetState();
        // Ended shows are refreshed weekly. Missing, stale or failed metadata keeps the show visible.
        var candidates = series.Where(show => show.Status == SeriesStatus.Ended &&
            inventory.TryGetValue(show.Id, out var state) &&
            string.Equals(state.Status, "Ended", StringComparison.OrdinalIgnoreCase) &&
            state.LastCheckedUtc >= now.AddDays(-8) && state.LastCheckedUtc <= now &&
            state.ConsecutiveFailures == 0 && state.LastError == null &&
            state.NextAirDateUtc == null && state.KnownEpisodeCount > 0).ToArray();
        if (candidates.Length == 0) return finished;

        // Two batched calls for the entire row, rather than recursive completion queries per poster.
        // Deliberately include missing/virtual entries: unavailable episodes are not evidence of completion.
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = candidates.Select(show => show.Id).ToArray(),
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            EnableTotalRecordCount = false
        }).OfType<Episode>().ToArray();
        var data = _userDataManager.GetUserDataBatch(episodes, user);
        var episodesBySeries = episodes.ToLookup(episode => episode.SeriesId);
        foreach (Series show in candidates)
        {
            var regular = episodesBySeries[show.Id].Where(episode => episode.ParentIndexNumber != 0).ToArray();
            if (regular.Length == 0 || regular.Any(episode =>
                episode.ParentIndexNumber is not > 0 || episode.IndexNumber is not > 0 ||
                !episode.PremiereDate.HasValue || episode.PremiereDate > now)) continue;

            // Multiple versions of one episode count once; watching one version is enough.
            var distinct = regular.GroupBy(episode => (episode.ParentIndexNumber, episode.IndexNumber)).ToArray();
            if (distinct.Length == inventory[show.Id].KnownEpisodeCount && distinct.All(versions =>
                versions.Any(episode => data.TryGetValue(episode.Id, out var state) && state.Played)))
            {
                finished.Add(show.Id);
            }
        }
        return finished;
    }

    private List<BaseItem> FindLibraryItems(IReadOnlyList<BaseItemDto> candidates)
    {
        var found = new Dictionary<Guid, BaseItem>();
        foreach (BaseItem item in _libraryManager.GetItemList(new InternalItemsQuery
        {
            ItemIds = candidates.Select(item => item.Id).ToArray(),
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            EnableTotalRecordCount = false
        }))
        {
            found[item.Id] = item;
        }

        var providerIds = ProviderNames
            .Select(provider => new
            {
                Provider = provider,
                Values = candidates
                    .Where(item => !found.ContainsKey(item.Id))
                    .Select(item => item.ProviderIds is not null && item.ProviderIds.TryGetValue(provider, out var value) ? value : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .Where(entry => entry.Values.Length > 0)
            .ToDictionary(entry => entry.Provider, entry => entry.Values, StringComparer.OrdinalIgnoreCase);
        if (providerIds.Count > 0)
        {
            foreach (BaseItem item in _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true,
                HasAnyProviderIds = providerIds,
                EnableTotalRecordCount = false
            }))
            {
                found[item.Id] = item;
            }
        }

        return found.Values.ToList();
    }
}
