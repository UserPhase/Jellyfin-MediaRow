using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
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

public class WatchedItemCleanupTask(
    ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager,
    ISessionManager sessionManager, ILogger<WatchedItemCleanupTask> logger) : IScheduledTask
{
    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public string Key => "MediarowWatchedItemCleanup";
    public string Name => "Mediarow: Clean Up Watched Items";
    public string Description => "Removes Mediarow STRM/NFO files for watched movies and fully watched ended series when enabled. Waits for all enabled users with access and skips favorites and active playback.";
    public string Category => "Mediarow";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Config;
        if (!config.Enabled || (!config.CleanupWatchedMovies && !config.CleanupCompletedSeries))
        {
            progress.Report(100);
            return;
        }
        if (!Path.IsPathFullyQualified(config.PersistentLibraryPath))
        {
            throw new InvalidOperationException("Watched cleanup requires an absolute persistent library folder.");
        }

        var users = userManager.GetUsers().Where(user => !user.HasPermission(PermissionKind.IsDisabled)).ToArray();
        var items = libraryManager.GetItemsResult(new InternalItemsQuery
        {
            Recursive = true, IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series]
        }).Items;

        for (int index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            bool isSeries = item is Series;
            if ((isSeries && !config.CleanupCompletedSeries) || (!isSeries && !config.CleanupWatchedMovies) ||
                item is not (Movie or Series) || string.IsNullOrEmpty(item.Path))
            {
                continue;
            }
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.PersistentLibraryPath));
            if (!Path.IsPathFullyQualified(item.Path) ||
                !Path.GetFullPath(item.Path).StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
                (item is Series candidate && candidate.Status != MediaBrowser.Model.Entities.SeriesStatus.Ended) ||
                (!isSeries && !Path.GetExtension(item.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                var viewers = users.Where(item.IsVisibleStandalone).ToArray();
                if (viewers.Length == 0)
                {
                    continue;
                }
                var episodes = isSeries
                    ? libraryManager.GetItemsResult(new InternalItemsQuery
                    {
                        ParentId = item.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Episode]
                    }).Items.OfType<Episode>().ToArray()
                    : [];
                var watchedItems = isSeries ? episodes.Cast<BaseItem>().ToArray() : [item];
                var data = viewers.SelectMany(user => watchedItems.Select(watchedItem =>
                    userDataManager.GetUserData(user, watchedItem))).ToArray();
                bool favorite = viewers.Any(user => userDataManager.GetUserData(user, item)?.IsFavorite == true) ||
                    data.Any(userData => userData?.IsFavorite == true);
                var ids = watchedItems.Select(watchedItem => watchedItem.Id).Append(item.Id).ToHashSet();
                bool inUse = sessionManager.Sessions.Any(session =>
                    session.NowPlayingItem != null && ids.Contains(session.NowPlayingItem.Id));
                var watched = data.Select(userData => userData?.Played == true).ToArray();
                bool eligible = item is Series series
                    ? WatchedItemCleanup.CanRemoveSeries(config.CleanupCompletedSeries, series.Status, watched, inUse, favorite,
                        episodes.Any(episode => !episode.PremiereDate.HasValue || episode.PremiereDate > DateTime.UtcNow))
                    : WatchedItemCleanup.CanRemoveMovie(config.CleanupWatchedMovies, watched, inUse, favorite);
                if (!eligible)
                {
                    continue;
                }

                string titlePath = isSeries ? item.Path : Path.GetDirectoryName(item.Path)!;
                var streams = isSeries ? episodes.Select(episode => episode.Path).ToArray() : [item.Path];
                var files = WatchedItemCleanup.GetOwnedFiles(config.PersistentLibraryPath, titlePath, isSeries, streams);
                if (files.Count == 0)
                {
                    logger.LogDebug("[Mediarow] Cleanup skipped unowned or mixed title {ItemId}", item.Id);
                    continue;
                }
                // Allow imports/metadata refreshes to settle, and never delete a recently changed title.
                if (files.Any(file => File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-10)))
                {
                    continue;
                }

                WatchedItemCleanup.DeleteOwnedFiles(config.PersistentLibraryPath, titlePath, isSeries, streams);
                libraryManager.DeleteItem(item, new DeleteOptions
                {
                    DeleteFileLocation = false, DeleteFromExternalProvider = false
                }, notifyParentItem: true);
                logger.LogInformation("[Mediarow] Removed watched {Type} {ItemId} and {Count} generated files",
                    isSeries ? "series" : "movie", item.Id, files.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogError(ex, "[Mediarow] Watched cleanup failed for {ItemId}", item.Id);
                throw;
            }
            progress.Report((index + 1) * 100.0 / items.Count);
            await Task.Yield();
        }
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks
        };
    }
}
