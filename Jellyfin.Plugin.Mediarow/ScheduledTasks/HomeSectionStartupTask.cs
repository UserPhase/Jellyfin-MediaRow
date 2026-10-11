using Jellyfin.Plugin.Mediarow.HomeScreen;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.ScheduledTasks;

/// <summary>
/// Initializes Mediarow-owned Home sections at server startup.
/// </summary>
public sealed class HomeSectionStartupTask : IScheduledTask
{
    private readonly HomeSectionBootstrapper _bootstrapper;
    private readonly ILogger<HomeSectionStartupTask> _logger;

    public HomeSectionStartupTask(
        HomeSectionBootstrapper bootstrapper,
        ILogger<HomeSectionStartupTask> logger)
    {
        _bootstrapper = bootstrapper;
        _logger = logger;
    }

    public string Name => "Initialize Mediarow Home Sections";

    public string Key => "MediarowHomeSections";

    public string Description => "Initializes Mediarow virtual Home sections";

    public string Category => "Mediarow";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            _bootstrapper.RegisterConfiguredSections();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mediarow] Failed to initialize internal Home sections");
            return;
        }

        progress.Report(10);
        try
        {
            await _bootstrapper.WarmCatalogRowsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Mediarow] Home rows could not be prepared at startup");
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}
