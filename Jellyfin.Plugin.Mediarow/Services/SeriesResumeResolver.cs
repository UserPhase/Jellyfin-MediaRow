using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Finds the episode a user was last part-way through so a series' Next Up does not fall back to the pilot.
/// Jellyfin only advances Next Up after an episode is fully played.
/// </summary>
public sealed class SeriesResumeResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;

    public SeriesResumeResolver(ILibraryManager libraryManager, IUserDataManager userDataManager)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
    }

    /// <summary>
    /// Returns the most recently watched in-progress episode when it is newer than the last fully played episode.
    /// </summary>
    public BaseItem? FindResumeEpisode(User user, BaseItem series)
    {
        var resumable = GetLatest(user, series.Id, resumable: true);
        return resumable is not null && IsNewerThanLastPlayed(user, series.Id, resumable) ? resumable : null;
    }

    /// <summary>
    /// Returns, per series ID, the in-progress episode that should replace Jellyfin's Next Up suggestion.
    /// </summary>
    public IReadOnlyDictionary<Guid, BaseItem> FindResumeEpisodes(User user)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            IsResumable = true,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = 200
        };

        var result = new Dictionary<Guid, BaseItem>();
        foreach (var episode in _libraryManager.GetItemList(query).OfType<Episode>())
        {
            if (episode.SeriesId != Guid.Empty && !result.ContainsKey(episode.SeriesId) &&
                IsNewerThanLastPlayed(user, episode.SeriesId, episode))
            {
                result[episode.SeriesId] = episode;
            }
        }

        return result;
    }

    private bool IsNewerThanLastPlayed(User user, Guid seriesId, BaseItem resumable)
    {
        var played = GetLatest(user, seriesId, resumable: false);
        if (played is null)
        {
            return true;
        }

        var resumedAt = _userDataManager.GetUserData(user, resumable)?.LastPlayedDate ?? DateTime.MinValue;
        var playedAt = _userDataManager.GetUserData(user, played)?.LastPlayedDate ?? DateTime.MinValue;
        return resumedAt >= playedAt;
    }

    private BaseItem? GetLatest(User user, Guid seriesId, bool resumable)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = 1
        };
        if (resumable)
        {
            query.IsResumable = true;
        }
        else
        {
            query.IsPlayed = true;
        }

        return _libraryManager.GetItemList(query).FirstOrDefault();
    }
}
