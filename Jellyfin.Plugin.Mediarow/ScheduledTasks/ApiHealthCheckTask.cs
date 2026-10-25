using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.ScheduledTasks;

/// <summary>
/// Runs the configuration diagnostics periodically (without the slow ffprobe step) and logs problems, so a dead
/// addon, rejected key or unwritable folder shows up in the log before users report playback failures.
/// </summary>
public class ApiHealthCheckTask : IScheduledTask
{
    private readonly PluginDiagnosticsService _diagnostics;
    private readonly ILogger<ApiHealthCheckTask> _logger;

    public ApiHealthCheckTask(PluginDiagnosticsService diagnostics, ILogger<ApiHealthCheckTask> logger)
    {
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "MediarowApiHealthCheck";

    /// <inheritdoc />
    public string Name => "Mediarow: Configuration Health Check";

    /// <inheritdoc />
    public string Description => "Tests the AIOStreams addon, stream links, metadata API keys, FFmpeg and folders, and logs any problems.";

    /// <inheritdoc />
    public string Category => "Mediarow";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        var report = await _diagnostics.TryRunAsync(new DiagnosticsOptions { ProbeStream = false }, cancellationToken).ConfigureAwait(false);
        if (report == null)
        {
            _logger.LogInformation("[Mediarow] Health check skipped: a configuration test is already running");
            progress.Report(100);
            return;
        }

        foreach (var check in report.Checks.Where(check => check.Status is DiagnosticStatus.Fail or DiagnosticStatus.Warning))
        {
            _logger.Log(
                check.Status == DiagnosticStatus.Fail ? LogLevel.Warning : LogLevel.Information,
                "[Mediarow] Health check {Status}: {Name} - {Message}",
                check.Status,
                check.Name,
                check.Message);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        }
    ];
}
