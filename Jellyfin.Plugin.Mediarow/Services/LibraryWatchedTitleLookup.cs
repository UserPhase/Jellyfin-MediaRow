using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
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
/// Movies count as watched when played; series when every episode is played.
/// </summary>
public sealed class LibraryWatchedTitleLookup : IWatchedTitleLookup
{
    private static readonly string[] ProviderNames = ["Imdb", "Tmdb", "Tvdb"];

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<LibraryWatchedTitleLookup> _logger;
    private readonly IMovieWatchHistory? _movieHistory;

    public LibraryWatchedTitleLookup(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<LibraryWatchedTitleLookup> logger,
        IMovieWatchHistory? movieHistory = null)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
        _movieHistory = movieHistory;
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
            var played = libraryItems
                .Where(item =>
                {
                    userData.TryGetValue(item.Id, out var data);
                    return item is Series
                        ? data != null && item.IsPlayed(user, data)
                        : data?.Played == true;
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
