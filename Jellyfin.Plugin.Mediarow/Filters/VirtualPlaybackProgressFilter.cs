using Jellyfin.Plugin.Mediarow.Extensions;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Filters;

/// <summary>
/// Stores per-user positions reported by Jellyfin for virtual Mediarow items.
/// </summary>
public sealed class VirtualPlaybackProgressFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly VirtualResumeTracker _resumeTracker;
    private readonly ILogger<VirtualPlaybackProgressFilter> _logger;

    public VirtualPlaybackProgressFilter(
        DynamicItemCache itemCache,
        VirtualResumeTracker resumeTracker,
        ILogger<VirtualPlaybackProgressFilter> logger)
    {
        _itemCache = itemCache;
        _resumeTracker = resumeTracker;
        _logger = logger;
    }

    public int Order => 1;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ControllerActionDescriptor? descriptor = context.ActionDescriptor as ControllerActionDescriptor;
        var isPlaybackReport = descriptor?.ControllerName == "Playstate" &&
            descriptor.ActionName is "ReportPlaybackProgress" or "ReportPlaybackStopped" or "OnPlaybackProgress" or "OnPlaybackStopped";
        if (!isPlaybackReport)
        {
            await next();
            return;
        }

        await next();

        if (!context.HttpContext.TryGetUserId(out Guid userId))
        {
            _logger.LogDebug("[Mediarow] Ignored virtual playback report because no user ID was available");
            return;
        }

        Guid itemId;
        long? positionTicks;
        if (context.ActionArguments.Values.OfType<PlaybackProgressInfo>().FirstOrDefault() is { } progress)
        {
            itemId = progress.ItemId;
            positionTicks = progress.PositionTicks;
        }
        else if (context.ActionArguments.Values.OfType<PlaybackStopInfo>().FirstOrDefault() is { } stop)
        {
            itemId = stop.ItemId;
            positionTicks = stop.PositionTicks;
        }
        else
        {
            return;
        }

        long ticks = positionTicks.GetValueOrDefault();
        if (itemId == Guid.Empty || ticks <= 0)
        {
            return;
        }

        var item = _itemCache.GetItem(itemId);
        if (item != null)
        {
            _resumeTracker.SaveResumeItem(userId, item, ticks);
        }
        else
        {
            _resumeTracker.SaveResumeTicks(userId, itemId, ticks);
        }
        _logger.LogInformation("[Mediarow] Saved virtual resume position for item {ItemId}, user {UserId}, ticks {Ticks}, Cached {Cached}",
            itemId, userId, ticks, _itemCache.HasItem(itemId));
    }
}
