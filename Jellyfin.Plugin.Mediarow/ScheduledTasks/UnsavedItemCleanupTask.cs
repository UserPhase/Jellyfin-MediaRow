using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.ScheduledTasks;

/// <summary>Reclaims generated library entries after browsing or playback, regardless of watched state.</summary>
public class UnsavedItemCleanupTask(
    ILibraryManager library, IUserManager users, IUserDataManager userData, ISessionManager sessions,
    TemporaryItemActivity activity, ILogger<UnsavedItemCleanupTask> logger) : IScheduledTask
{
    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new();
    public string Key => "MediarowUnsavedItemCleanup";
    public string Name => "Mediarow: Remove Unsaved Titles";
    public string Description => "Removes idle generated STRM/NFO entries after the configured grace period. Keeps favorites and active or paused playback.";
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

        // A favorite retains the shared title even if its owner is disabled or currently lacks access.
        var allUsers = users.GetUsers().ToArray();
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

                bool IsRetained() => allUsers.Any(user => retainedItems.Any(child => userData.GetUserData(user, child)?.IsFavorite == true)) ||
                    sessions.Sessions.Any(session => session.NowPlayingItem is { } playing &&
                        (ids.Contains(playing.Id) || (series && playing.SeriesId == item.Id)));

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

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(1).Ticks };
    }
}
