using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Filters;

/// <summary>Selects a native AIOStreams source before Jellyfin negotiates client capabilities.</summary>
public sealed class AioStreamsPlaybackFilter(
    ILibraryManager libraryManager,
    DynamicAioStreamsMediaSourceProvider provider,
    ILogger<AioStreamsPlaybackFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (MediarowPlugin.Instance?.Configuration.StreamProvider != StreamProvider.AIOStreams ||
            context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "MediaInfo", ActionName: "GetPlaybackInfo" or "GetPostedPlaybackInfo" } descriptor ||
            !context.ActionArguments.TryGetValue("itemId", out var argument) || argument is not Guid itemId)
        {
            await next();
            return;
        }

        var item = libraryManager.GetItemById(itemId);
        if (item == null || !provider.IsMediarowItem(item))
        {
            await next();
            return;
        }

        try
        {
            var candidates = (await provider.GetMediaSources(item, context.HttpContext.RequestAborted)).ToArray();
            if (candidates.Length == 0)
            {
                context.Result = new NotFoundObjectResult(new { Error = "No playable HTTP streams were returned for this title." });
                return;
            }

            if (descriptor.ActionName == "GetPostedPlaybackInfo")
            {
                context.ActionArguments.TryGetValue("playbackInfoDto", out var body);
                context.ActionArguments.TryGetValue("mediaSourceId", out var selectedArgument);
                context.ActionArguments.TryGetValue("liveStreamId", out var liveArgument);
                var selected = selectedArgument as string
                    ?? body?.GetType().GetProperty("MediaSourceId")?.GetValue(body) as string;
                var liveStream = liveArgument as string
                    ?? body?.GetType().GetProperty("LiveStreamId")?.GetValue(body) as string;
                if (string.IsNullOrWhiteSpace(liveStream))
                {
                    if (string.IsNullOrWhiteSpace(selected))
                    {
                        context.ActionArguments["mediaSourceId"] = candidates[0].Id;
                    }
                    else if (!candidates.Any(source => source.Id.Equals(selected, StringComparison.OrdinalIgnoreCase)))
                    {
                        context.Result = new BadRequestObjectResult(new { Error = "The selected stream expired or is unavailable. Reload the title." });
                        return;
                    }

                    // Negotiate with real tracks in this request, not a later client-specific live-stream request.
                    context.ActionArguments["autoOpenLiveStream"] = true;
                }
            }

            context.HttpContext.Items[LanguagePreferenceFilter.CandidatesKey] = candidates;
            var executed = await next();
            if (executed.Exception is HttpRequestException or InvalidOperationException or ArgumentException or OperationCanceledException or FfmpegException)
            {
                logger.LogWarning("[Mediarow] Native source preparation failed for item {ItemId} ({ErrorType})", itemId, executed.Exception.GetType().Name);
                executed.ExceptionHandled = true;
                executed.Result = new ObjectResult(new { Error = "The selected stream could not be opened or probed. Reload the title or select another stream." }) { StatusCode = 502 };
                return;
            }
            if (executed.Result is ObjectResult { Value: PlaybackInfoResponse result })
            {
                result.MediaSources = result.MediaSources.Where(source =>
                    !string.IsNullOrEmpty(source.LiveStreamId) || candidates.Any(candidate => candidate.Id == source.Id)).ToArray();
                if (result.MediaSources.Count == 0)
                {
                    result.ErrorCode = PlaybackErrorCode.NoCompatibleStream;
                }
            }
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException or FfmpegException)
        {
            logger.LogWarning("[Mediarow] Native playback failed for item {ItemId} ({ErrorType})", itemId, ex.GetType().Name);
            context.Result = new ObjectResult(new { Error = "AIOStreams playback could not be prepared. Check addon settings or reload the title." }) { StatusCode = 502 };
        }
    }
}
