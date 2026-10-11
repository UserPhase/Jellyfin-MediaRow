using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
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
        var resumable = GetLatest(user, series, resumable: true);
        if (resumable is null)
        {
            return null;
        }

        var played = GetLatest(user, series, resumable: false);
        if (played is null)
        {
            return resumable;
        }

        var resumedAt = _userDataManager.GetUserData(user, resumable)?.LastPlayedDate ?? DateTime.MinValue;
        var playedAt = _userDataManager.GetUserData(user, played)?.LastPlayedDate ?? DateTime.MinValue;
        return resumedAt >= playedAt ? resumable : null;
    }

    private BaseItem? GetLatest(User user, BaseItem series, bool resumable)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [series.Id],
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
