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
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.ScheduledTasks;

/// <summary>Deletes only owned, fully watched titles after current retention and activity checks.</summary>
public class WatchedItemCleanupTask(
    ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager,
    ISessionManager sessionManager, ILogger<WatchedItemCleanupTask> logger,
    TemporaryItemActivity? activity = null, EpisodeSyncService? episodeSync = null, DynamicItemCache? itemCache = null) : IScheduledTask
{
    private readonly TemporaryItemActivity _activity = activity ?? new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new();

    public string Key => "MediarowWatchedItemCleanup";
    public string Name => "Mediarow: Clean Up Watched Items";
    public string Description => "Removes owned watched titles after all enabled viewers finish. Keeps every account's favorites and defers incomplete inventories, scans, imports and playback.";
    public string Category => "Mediarow";

    private static bool Enabled(PluginConfiguration config, bool? series = null) => config.Enabled && !config.CleanupUnsavedItems &&
        (series.HasValue ? series.Value ? config.CleanupCompletedSeries : config.CleanupWatchedMovies : config.CleanupWatchedMovies || config.CleanupCompletedSeries);

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var config = Config;
            if (!Enabled(config) || libraryManager.IsScanRunning || episodeSync?.IsSweeping == true) return;
            var categories = new List<string>();
            if (config.CleanupWatchedMovies) categories.Add("movies");
            if (config.CleanupCompletedSeries) categories.AddRange(["tv", "anime"]);
            if (categories.Any(category => !Path.IsPathFullyQualified(config.GetPersistencePath(category))))
                throw new InvalidOperationException("Watched cleanup requires absolute persistent library folders.");

            var items = libraryManager.GetItemsResult(new InternalItemsQuery
            {
                Recursive = true, IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series]
            }).Items;
            for (int index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (libraryManager.IsScanRunning || episodeSync?.IsSweeping == true) break;
                var item = items[index];
                bool series = item is Series;
                if (item is not (Movie or Series) || !Enabled(Config, series) || !Path.IsPathFullyQualified(item.Path) ||
                    (!series && !string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase))) continue;
                var titlePath = series ? item.Path : Path.GetDirectoryName(item.Path)!;
                var root = TemporaryItemActivity.GetLibraryRoot(Config, titlePath, series);
                if (root == null) continue;

                try
                {
                    var children = GetChildren(item);
                    if (!Eligible(item, children)) continue;
                    var streams = GetStreams(item, children);
                    var files = WatchedItemCleanup.GetOwnedFilesInLibrary(root, titlePath, series, streams);
                    if (files.Count == 0 || files.Any(file => File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-10))) continue;

                    // Use the same per-title gate as request/import leases and unsaved cleanup.
                    // File age is checked separately; enabling this gate must not reset a cleanup timer.
                    _activity.SetEnabled(true);
                    _activity.TryCleanup(titlePath, files.Max(File.GetLastWriteTimeUtc), TimeSpan.Zero, () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Enabled(Config, series) || libraryManager.IsScanRunning || episodeSync?.IsSweeping == true ||
                            TemporaryItemActivity.GetLibraryRoot(Config, titlePath, series) != root) return false;
                        var currentChildren = GetChildren(item);
                        if (!Eligible(item, currentChildren)) return false;
                        var currentStreams = GetStreams(item, currentChildren);
                        var currentFiles = WatchedItemCleanup.GetOwnedFilesInLibrary(root, titlePath, series, currentStreams);
                        if (currentFiles.Count == 0 || currentFiles.Any(file => File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-10))) return false;
                        // Recheck after the potentially slow inventory/user-data and filesystem reads.
                        if (!Eligible(item, currentChildren) || !Enabled(Config, series) || libraryManager.IsScanRunning || episodeSync?.IsSweeping == true ||
                            TemporaryItemActivity.GetLibraryRoot(Config, titlePath, series) != root) return false;
                        WatchedItemCleanup.DeleteOwnedFilesInLibrary(root, titlePath, series, currentStreams);
                        libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, notifyParentItem: true);
                        logger.LogInformation("[Mediarow] Removed watched {Type} {ItemId}", series ? "series" : "movie", item.Id);
                        return true;
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { logger.LogWarning(ex, "[Mediarow] Cleanup deferred for {ItemId}", item.Id); }
                progress.Report((index + 1) * 100d / items.Count);
                await Task.Yield();
            }
        }
        finally { progress.Report(100); _runLock.Release(); }
    }

    private BaseItem[] GetChildren(BaseItem item) => item is Series
        ? libraryManager.GetItemsResult(new InternalItemsQuery
        {
            ParentId = item.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode]
        }).Items.Where(child => child is Season or Episode).ToArray() : [];

    private static string[] GetStreams(BaseItem item, BaseItem[] children) => item is Series
        ? children.OfType<Episode>().Select(episode => episode.Path).ToArray() : [item.Path];

    private bool Eligible(BaseItem item, BaseItem[] children)
    {
        var allUsers = userManager.GetUsers().ToArray();
        var retained = children.Prepend(item).ToArray();
        // Retention includes disabled accounts and accounts that no longer have access.
        if (allUsers.Any(user => retained.Any(child => userDataManager.GetUserData(user, child)?.IsFavorite == true))) return false;
        var ids = retained.Select(child => child.Id).ToHashSet();
        if (sessionManager.Sessions.Any(session => session.NowPlayingItem is { } playing &&
            (ids.Contains(playing.Id) || item is Series && playing.SeriesId == item.Id))) return false;
        var viewers = allUsers.Where(user => !user.HasPermission(PermissionKind.IsDisabled) && item.IsVisibleStandalone(user)).ToArray();
        if (viewers.Length == 0) return false;
        var watched = item is Series ? children.OfType<Episode>().Cast<BaseItem>().ToArray() : [item];
        if (watched.Length == 0 || viewers.Any(user => watched.Any(child => userDataManager.GetUserData(user, child)?.Played != true))) return false;
        if (item is not Series series) return true;
        if (series.Status != SeriesStatus.Ended || children.OfType<Episode>().Any(episode =>
            !episode.PremiereDate.HasValue || episode.PremiereDate > DateTime.UtcNow)) return false;
        var expected = GetExpectedEpisodes(series);
        var regular = children.OfType<Episode>().Where(episode => episode.ParentIndexNumber != 0).ToArray();
        return expected is { Count: > 0 } && regular.All(episode => episode.ParentIndexNumber is > 0 && episode.IndexNumber is > 0) &&
            expected.SetEquals(regular.Select(episode => (episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value)));
    }

    /// <summary>Without a successful full inventory, defer destructive completed-series cleanup.</summary>
    protected virtual IReadOnlySet<(int Season, int Episode)>? GetExpectedEpisodes(Series series)
    {
        var inventory = episodeSync?.GetState();
        if (inventory == null || !inventory.TryGetValue(series.Id, out var state) ||
            !string.Equals(state.Status, "Ended", StringComparison.OrdinalIgnoreCase) || state.ConsecutiveFailures != 0 || state.LastError != null ||
            state.LastCheckedUtc == null || state.LastCheckedUtc < DateTime.UtcNow.AddDays(-8) || state.LastCheckedUtc > DateTime.UtcNow ||
            state.NextAirDateUtc != null || state.KnownEpisodeCount <= 0) return null;
        var episodes = itemCache?.GetEpisodesForSeries(series.Id)?.Where(episode => episode.ParentIndexNumber != 0).ToArray();
        if (episodes == null || episodes.Length != state.KnownEpisodeCount || episodes.Any(episode =>
            episode.ParentIndexNumber is not > 0 || episode.IndexNumber is not > 0 ||
            !episode.PremiereDate.HasValue || episode.PremiereDate > DateTime.UtcNow)) return null;
        var keys = episodes.Select(episode => (episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value)).ToHashSet();
        return keys.Count == state.KnownEpisodeCount ? keys : null;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks };
    }
}
