using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.ScheduledTasks;

/// <summary>
/// Sweeps series persisted by Mediarow for newly aired episodes and adds them to the library, so they reach the
/// native "Next Up" row without anyone opening the show. Runs hourly; each series is only re-checked when
/// <see cref="EpisodeSyncService.IsDue"/> says so (configured interval, air dates, ended shows, failure backoff).
/// </summary>
public class ScheduledEpisodeUpdateTask : IScheduledTask
{
    private readonly EpisodeSyncService _sync;
    private readonly ILogger<ScheduledEpisodeUpdateTask> _logger;

    public ScheduledEpisodeUpdateTask(EpisodeSyncService sync, ILogger<ScheduledEpisodeUpdateTask> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "MediarowEpisodeSync";

    /// <inheritdoc />
    public string Name => "Mediarow: Sync New Episodes";

    /// <inheritdoc />
    public string Description => "Checks saved Mediarow series for newly released episodes and adds them to the library so they appear in Next Up.";

    /// <inheritdoc />
    public string Category => "Mediarow";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (MediarowPlugin.Instance?.Configuration.EnableEpisodeSync == false)
        {
            _logger.LogDebug("[EpisodeSync] Background episode sync is turned off");
            progress.Report(100);
            return;
        }

        await _sync.SyncAllAsync(progress, force: false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            }
        ];
    }
}
