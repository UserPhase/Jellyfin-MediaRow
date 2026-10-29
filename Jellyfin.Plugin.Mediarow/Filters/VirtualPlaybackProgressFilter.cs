using Jellyfin.Plugin.Mediarow.Extensions;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
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
    private readonly UserPermissionGuard? _guard;
    private readonly ILibraryManager? _library;
    private readonly IUserManager? _users;
    private readonly IUserDataManager? _userData;
    private readonly IServerConfigurationManager? _configuration;

    public VirtualPlaybackProgressFilter(
        DynamicItemCache itemCache,
        VirtualResumeTracker resumeTracker,
        ILogger<VirtualPlaybackProgressFilter> logger,
        UserPermissionGuard? guard = null, ILibraryManager? library = null, IUserManager? users = null,
        IUserDataManager? userData = null, IServerConfigurationManager? configuration = null)
    {
        _itemCache = itemCache;
        _resumeTracker = resumeTracker;
        _logger = logger;
        _guard = guard; _library = library; _users = users; _userData = userData; _configuration = configuration;
    }

    public int Order => 1;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ControllerActionDescriptor? descriptor = context.ActionDescriptor as ControllerActionDescriptor;
        var isPlaybackReport = descriptor?.ControllerName == "Playstate" &&
            descriptor.ActionName is "ReportPlaybackProgress" or "ReportPlaybackStopped" or "OnPlaybackProgress" or "OnPlaybackStopped";
        var resetsResume = descriptor?.ControllerName == "Playstate" && descriptor.ActionName is
            "MarkPlayedItem" or "MarkPlayedItemLegacy" or "MarkUnplayedItem" or "MarkUnplayedItemLegacy";
        var updatesUserData = descriptor?.ControllerName == "Items" && descriptor.ActionName is "UpdateItemUserData" or "UpdateItemUserDataLegacy";
        if (!isPlaybackReport && !resetsResume && !updatesUserData)
        {
            await next();
            return;
        }

        var executed = await next();
        if (executed.Exception != null || context.HttpContext.Response.StatusCode >= 400 ||
            executed.Result is IStatusCodeActionResult { StatusCode: >= 400 }) return;

        if (context.HttpContext.User.Identity?.IsAuthenticated != true || !context.HttpContext.TryGetUserId(out Guid userId))
        {
            _logger.LogDebug("[Mediarow] Ignored virtual playback report because no user ID was available");
            return;
        }
        Guid requested = context.ActionArguments.TryGetValue("userId", out var supplied) && supplied is Guid target ? target : Guid.Empty;
        if (_guard != null)
        {
            var access = _guard.Resolve(context.HttpContext.User, requested);
            if (!access.Succeeded) return;
            userId = access.Context!.UserId;
        }
        else if (requested != Guid.Empty && requested != userId) return;

        if (resetsResume || updatesUserData)
        {
            if (context.ActionArguments.TryGetValue("itemId", out var value) && value is Guid id &&
                (resetsResume || executed.Result is ObjectResult { Value: UserItemDataDto { PlaybackPositionTicks: 0 } }))
                _resumeTracker.RemoveResumeItem(userId, id);
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
        if (itemId == Guid.Empty || !positionTicks.HasValue || ticks < 0)
        {
            return;
        }

        var cached = _itemCache.GetItem(itemId);
        var tracked = _resumeTracker.GetTrackedItem(userId, itemId);
        var item = cached != null && SearchResultFactory.IsDynamicItem(cached) ? cached : tracked;
        // Native Jellyfin already owns other titles' history; do not duplicate every playback report.
        if (item == null && _resumeTracker.GetResumeTicks(userId, itemId) <= 0) return;
        if (_guard != null && item != null)
        {
            var access = _guard.Resolve(context.HttpContext.User, userId);
            if (!access.Succeeded || !_guard.CanView(access.Context!, item)) return;
        }
        var native = _library?.GetItemById(itemId);
        var user = _users?.GetUserById(userId);
        var state = native == null || user == null ? null : _userData?.GetUserData(user, native);
        long runtime = native?.RunTimeTicks ?? item?.RunTimeTicks ?? 0;
        double completion = Math.Clamp(_configuration?.Configuration.MaxResumePct ?? 90, 1, 100);
        bool failed = context.ActionArguments.Values.OfType<PlaybackStopInfo>().FirstOrDefault()?.Failed == true;
        if (failed) return;
        if (ticks == 0 || state is { Played: true, PlaybackPositionTicks: 0 } ||
            runtime > 0 && (ticks >= runtime || (decimal)ticks / runtime * 100 > (decimal)completion))
        {
            _resumeTracker.RemoveResumeItem(userId, itemId);
            return;
        }
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
