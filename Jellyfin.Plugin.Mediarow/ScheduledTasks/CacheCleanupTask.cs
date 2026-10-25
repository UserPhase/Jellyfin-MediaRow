using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.ScheduledTasks;

/// <summary>
/// Scheduled task that cleans up expired cache entries.
/// </summary>
public class CacheCleanupTask : IScheduledTask
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<CacheCleanupTask> _logger;

    public CacheCleanupTask(IMemoryCache cache, ILogger<CacheCleanupTask> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "MediarowCacheCleanup";

    /// <inheritdoc />
    public string Name => "Mediarow: Cache Cleanup";

    /// <inheritdoc />
    public string Description => "Cleans up expired cache entries from the Mediarow plugin to free memory.";

    /// <inheritdoc />
    public string Category => "Mediarow";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Mediarow cache cleanup task");

        try
        {
            progress.Report(10);

            // Trigger lazy expiration only for plugin-owned metadata. Never compact the
            // server's shared cache or touch live source/probe/relay capabilities.
            if (_cache is MemoryCache memoryCache)
            {
                var keys = memoryCache.Keys.OfType<string>().Where(IsMetadataKey).ToArray();
                foreach (var key in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _cache.TryGetValue(key, out _);
                }
                _logger.LogInformation("Checked expiration of {Count} Mediarow metadata entries", keys.Length);
            }
            else
            {
                _logger.LogDebug("Cache is not a MemoryCache instance, skipping compaction");
            }

            progress.Report(100);
            _logger.LogInformation("Mediarow cache cleanup task completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cache cleanup task was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }

        return Task.CompletedTask;
    }

    private static bool IsMetadataKey(string key) =>
        key.StartsWith("mediarow-home:", StringComparison.Ordinal) ||
        key.StartsWith("mediarow-home-section:", StringComparison.Ordinal) ||
        key.StartsWith("mediarow-discovery:", StringComparison.Ordinal) ||
        key.StartsWith("mediarow-visibility:", StringComparison.Ordinal) ||
        key.StartsWith("dynamic_item:", StringComparison.Ordinal) ||
        key.StartsWith("dynamic_seasons:", StringComparison.Ordinal) ||
        key.StartsWith("dynamic_episodes:", StringComparison.Ordinal) ||
        key.StartsWith("dynamic_image:", StringComparison.Ordinal) ||
        key.StartsWith("dynamic_backdrop:", StringComparison.Ordinal);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run daily at 4 AM
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
