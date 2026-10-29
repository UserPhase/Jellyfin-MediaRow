using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.ScheduledTasks;

/// <summary>Reclaims idle unsaved entries, retaining series that an account has started but not finished.</summary>
public class UnsavedItemCleanupTask(
    ILibraryManager library, IUserManager users, IUserDataManager userData, ISessionManager sessions,
    TemporaryItemActivity activity, ILogger<UnsavedItemCleanupTask> logger,
    EpisodeSyncService? episodeSync = null, DynamicItemCache? itemCache = null) : IScheduledTask
{
    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new();
    public string Key => "MediarowUnsavedItemCleanup";
    public string Name => "Mediarow: Remove Unsaved Titles";
    public string Description => "Removes idle generated STRM/NFO entries after the configured grace period. Keeps favorites, active or paused playback, and started unfinished series.";
    public string Category => "Mediarow";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Config;
        bool enabled = config.Enabled && config.CleanupUnsavedItems;
        activity.SetEnabled(enabled);
        if (!enabled) { progress.Report(100); return; }
        if (!new[] { "movies", "tv", "anime" }.Select(config.GetPersistencePath).Any(Path.IsPathFullyQualified))
            throw new InvalidOperationException("Unsaved cleanup requires an absolute persistent library folder.");
        if (library.IsScanRunning) { progress.Report(100); return; }

        var candidates = library.GetItemsResult(new InternalItemsQuery
        {
            Recursive = true, IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series]
        }).Items;
        for (int index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = candidates[index];
            bool series = item is Series;
            var titlePath = TemporaryItemActivity.GetTitlePath(config, item.Path);
            if (item is not (Movie or Series) || titlePath == null ||
                (!series && !string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var children = series ? library.GetItemsResult(new InternalItemsQuery
                {
                    ParentId = item.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Season]
                }).Items.ToArray() : [];
                var retainedItems = children.Prepend(item).ToArray();
                var ids = retainedItems.Select(child => child.Id).ToHashSet();
                var streams = series ? children.OfType<Episode>().Select(episode => episode.Path).ToArray() : [item.Path];
                var root = TemporaryItemActivity.GetLibraryRoot(config, titlePath, series);
                if (root == null) continue;
                var files = WatchedItemCleanup.GetOwnedFilesInLibrary(root, titlePath, series, streams);
                if (files.Count == 0) continue;

                bool IsRetained()
                {
                    if (sessions.Sessions.Any(session => session.NowPlayingItem is { } playing &&
                        (ids.Contains(playing.Id) || (series && playing.SeriesId == item.Id)))) return true;

                    // A favorite or unfinished series retains the shared title even when that account
                    // is disabled or currently lacks library access. Re-read history inside the gate.
                    var histories = users.GetUsers()
                        .Select(user => retainedItems.Select(child => userData.GetUserData(user, child)).ToArray())
                        .ToArray();
                    if (histories.Any(history => history.Any(data => data?.IsFavorite == true))) return true;
                    if (item is not Series show) return false;

                    var started = histories.Where(history => history.Any(HasPlaybackHistory)).ToArray();
                    if (started.Length == 0) return false; // Opening a poster is not a commitment to watch.
                    var episodes = children.OfType<Episode>().ToArray();
                    if (!HasCompleteInventory(show, episodes)) return true;
                    var episodeIndexes = retainedItems.Select((child, index) => (child, index))
                        .Where(entry => entry.child is Episode).Select(entry => entry.index).ToArray();
                    return started.Any(history => episodeIndexes.Any(index =>
                        history[index]?.Played != true || history[index]!.PlaybackPositionTicks > 0));
                }

                if (IsRetained()) { activity.Touch(titlePath); continue; }
                activity.TryCleanup(titlePath, files.Max(File.GetLastWriteTimeUtc),
                    TimeSpan.FromMinutes(config.UnsavedItemGraceMinutes), () =>
                    {
                        // Recheck after acquiring the activity gate; another user may have started or saved it.
                        var currentConfig = Config;
                        if (!currentConfig.Enabled || !currentConfig.CleanupUnsavedItems ||
                            currentConfig.UnsavedItemGraceMinutes != config.UnsavedItemGraceMinutes ||
                            TemporaryItemActivity.GetLibraryRoot(currentConfig, titlePath, series) != root || library.IsScanRunning || IsRetained())
                        { activity.Touch(titlePath); return false; }
                        cancellationToken.ThrowIfCancellationRequested();
                        WatchedItemCleanup.DeleteOwnedFilesInLibrary(root, titlePath, series, streams);
                        library.DeleteItem(item, new DeleteOptions
                        {
                            DeleteFileLocation = false, DeleteFromExternalProvider = false
                        }, notifyParentItem: true);
                        logger.LogInformation("[Mediarow] Removed idle unsaved {Type} {ItemId}", series ? "series" : "movie", item.Id);
                        return true;
                    });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "[Mediarow] Could not clean up unsaved item {ItemId}; leaving it for a later run", item.Id);
            }
            progress.Report((index + 1) * 100d / candidates.Count);
            await Task.Yield();
        }
        progress.Report(100);
    }

    private static bool HasPlaybackHistory(UserItemData? data) => data != null &&
        (data.Played || data.PlaybackPositionTicks > 0 || data.PlayCount > 0 || data.LastPlayedDate > DateTime.MinValue);

    private bool HasCompleteInventory(Series show, IReadOnlyList<Episode> episodes)
    {
        if (show.Status != SeriesStatus.Ended || episodes.Count == 0) return false;
        var now = DateTime.UtcNow;
        var inventory = episodeSync?.GetState();
        if (inventory == null || !inventory.TryGetValue(show.Id, out var state) ||
            !string.Equals(state.Status, "Ended", StringComparison.OrdinalIgnoreCase) ||
            state.LastCheckedUtc == null || state.LastCheckedUtc < now.AddDays(-8) || state.LastCheckedUtc > now ||
            state.ConsecutiveFailures != 0 || state.LastError != null || state.NextAirDateUtc != null || state.KnownEpisodeCount <= 0)
            return false;
        var expected = itemCache?.GetEpisodesForSeries(show.Id)?.Where(episode => episode.ParentIndexNumber != 0).ToArray();
        if (expected == null || expected.Length != state.KnownEpisodeCount || expected.Any(episode =>
            episode.ParentIndexNumber is not > 0 || episode.IndexNumber is not > 0 ||
            !episode.PremiereDate.HasValue || episode.PremiereDate > now)) return false;
        if (episodes.Any(episode => !episode.PremiereDate.HasValue || episode.PremiereDate > now)) return false;
        var regular = episodes.Where(episode => episode.ParentIndexNumber != 0).ToArray();
        if (regular.Any(episode => episode.ParentIndexNumber is not > 0 || episode.IndexNumber is not > 0)) return false;
        var expectedKeys = expected.Select(episode => (episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value)).ToHashSet();
        return expectedKeys.Count == state.KnownEpisodeCount &&
            expectedKeys.SetEquals(regular.Select(episode => (episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value)));
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(1).Ticks };
    }
}
