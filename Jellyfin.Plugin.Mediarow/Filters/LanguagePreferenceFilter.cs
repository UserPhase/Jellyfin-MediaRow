using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Providers;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Filters;

/// <summary>
/// The tracks Mediarow asked Jellyfin to play. Manual flags mark tracks the client picked itself.
/// </summary>
public sealed record TrackDecision(int? AudioStreamIndex, int? SubtitleStreamIndex, bool AudioManual, bool SubtitleManual,
    IReadOnlyList<MediaStream>? Streams = null)
{
    public static string CacheKey(Guid userId, Guid itemId) => $"mediarow-tracks:{userId:N}:{itemId:N}";
}

/// <summary>
/// Applies per-user language preferences before Jellyfin negotiates playback, so the preferred tracks are in
/// the first stream URL instead of being switched after playback starts.
/// </summary>
public sealed class LanguagePreferenceFilter(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IMediaSourceManager mediaSourceManager,
    DynamicAioStreamsMediaSourceProvider provider,
    IMemoryCache cache,
    ILogger<LanguagePreferenceFilter> logger,
    UserPermissionGuard guard) : IAsyncActionFilter
{
    /// <summary>HttpContext item holding the AIOStreams candidates for the current playback request.</summary>
    public const string CandidatesKey = "Mediarow.AioStreamsCandidates";

    public PluginConfiguration? Config { get; init; }

    public Action? SaveConfiguration { get; init; } = () => MediarowPlugin.Instance?.SaveConfiguration();

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        PendingDecision? pending = null;
        if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "MediaInfo", ActionName: "GetPostedPlaybackInfo" } &&
            context.ActionArguments.TryGetValue("itemId", out var argument) && argument is Guid itemId)
        {
            try
            {
                pending = await ApplyAsync(context, itemId).ConfigureAwait(false);
            }
            catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Language preferences must never block playback.
                logger.LogWarning("[Mediarow] Could not apply language preferences for item {ItemId} ({ErrorType})", itemId, ex.GetType().Name);
            }
        }

        var executed = await next().ConfigureAwait(false);
        if (pending == null || executed.Exception != null || context.HttpContext.Response.StatusCode >= 400 ||
            executed.Result is IStatusCodeActionResult { StatusCode: >= 400 } ||
            executed.Result is ForbidResult or ChallengeResult or
                ObjectResult { Value: PlaybackInfoResponse { ErrorCode: not null } })
            return;

        // Negotiation may fail or policy may change while the native action runs. Only a successful,
        // still-authorized request can write another account's cache/configuration (for an admin).
        try
        {
            var access = guard.Resolve(context.HttpContext.User, pending.User.Id);
            if (!access.Succeeded || !guard.CanView(access.Context!, new BaseItemDto { Id = pending.Item.Id })) return;
            cache.Set(TrackDecision.CacheKey(pending.User.Id, pending.Item.Id), pending.Decision, TimeSpan.FromMinutes(15));
            var currentConfig = Config ?? MediarowPlugin.Instance?.Configuration ?? pending.Config;
            if ((pending.Decision.AudioManual || pending.Decision.SubtitleManual) &&
                SeriesLanguageMemory.Remember(currentConfig, pending.User.Id, pending.Item, pending.Streams,
                    pending.RequestedAudio, pending.RequestedSubtitle, pending.Decision.AudioManual, pending.Decision.SubtitleManual))
            {
                SaveConfiguration?.Invoke();
                logger.LogInformation("[Mediarow] Remembered {User}'s track choice for {Series}", pending.User.Username,
                    (pending.Item as MediaBrowser.Controller.Entities.TV.Episode)?.SeriesName);
            }
        }
        catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("[Mediarow] Could not remember track choices for item {ItemId} ({ErrorType})", pending.Item.Id, ex.GetType().Name);
        }
    }

    private sealed record PendingDecision(PluginConfiguration Config, User User, BaseItem Item,
        TrackDecision Decision, IReadOnlyList<MediaStream> Streams, int? RequestedAudio, int? RequestedSubtitle);

    private async Task<PendingDecision?> ApplyAsync(ActionExecutingContext context, Guid itemId)
    {
        var config = Config ?? MediarowPlugin.Instance?.Configuration;
        var item = libraryManager.GetItemById(itemId);
        Guid requestedUser = context.ActionArguments.TryGetValue("userId", out var value) && value is Guid id ? id : Guid.Empty;
        var access = guard.Resolve(context.HttpContext.User, requestedUser);
        if (config == null || item is not Video || !access.Succeeded ||
            !guard.CanView(access.Context!, new BaseItemDto { Id = itemId }))
        {
            return null;
        }
        var user = userManager.GetUserById(access.Context!.UserId);
        if (user == null) return null;

        context.ActionArguments.TryGetValue("playbackInfoDto", out var body);
        if (!string.IsNullOrWhiteSpace(Argument<string>(context, body, "liveStreamId", "LiveStreamId")))
        {
            return null;
        }

        var candidates = context.HttpContext.Items[CandidatesKey] as IReadOnlyList<MediaSourceInfo>;
        var preference = TrackLanguageSelector.Resolve(config, user.Id, item, useGlobalFallback: candidates != null);
        if (preference == null)
        {
            return null;
        }

        var sourceId = Argument<string>(context, body, "mediaSourceId", "MediaSourceId");
        MediaSourceInfo defaults;
        if (candidates != null)
        {
            var candidate = candidates.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
            {
                return null;
            }

            var streams = await provider.GetProbedStreamsAsync(candidate, context.HttpContext.RequestAborted).ConfigureAwait(false);
            defaults = new MediaSourceInfo { Id = candidate.Id, MediaStreams = streams.ToList() };
            mediaSourceManager.SetDefaultAudioAndSubtitleStreamIndices(item, defaults, user);
        }
        else
        {
            var sources = mediaSourceManager.GetStaticMediaSources(item, true, user);
            // Jellyfin only applies requested indices to the source the client names; with several versions, leave the choice to the client.
            var match = string.IsNullOrWhiteSpace(sourceId)
                ? sources.Count == 1 ? sources[0] : null
                : sources.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return null;
            }

            defaults = match;
        }

        var requestedAudio = Argument<int?>(context, body, "audioStreamIndex", "AudioStreamIndex");
        var requestedSubtitle = Argument<int?>(context, body, "subtitleStreamIndex", "SubtitleStreamIndex");

        // Clients echo the server default from the details page; anything else is a manual pick and wins.
        var audioManual = requestedAudio.HasValue && requestedAudio != defaults.DefaultAudioStreamIndex;
        var subtitleManual = requestedSubtitle.HasValue && requestedSubtitle != defaults.DefaultSubtitleStreamIndex;
        var selection = TrackLanguageSelector.Select(defaults.MediaStreams, preference, item.GetInheritedOriginalLanguage(),
            audioManual ? requestedAudio : null);
        var audio = audioManual ? requestedAudio : selection.AudioStreamIndex ?? requestedAudio;
        var subtitle = subtitleManual ? requestedSubtitle : selection.SubtitleStreamIndex ?? requestedSubtitle;

        var pending = new PendingDecision(config, user, item,
            new TrackDecision(audio ?? defaults.DefaultAudioStreamIndex, subtitle, audioManual, subtitleManual, defaults.MediaStreams),
            defaults.MediaStreams.ToArray(), requestedAudio, requestedSubtitle);

        if (audio == requestedAudio && subtitle == requestedSubtitle)
        {
            return pending;
        }

        context.ActionArguments["audioStreamIndex"] = audio;
        context.ActionArguments["subtitleStreamIndex"] = subtitle;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            context.ActionArguments["mediaSourceId"] = defaults.Id;
        }

        logger.LogInformation("[Mediarow] Language preferences for {Item}: audio track {Audio}, subtitle track {Subtitle}",
            item.Name, audio?.ToString() ?? "default", subtitle?.ToString() ?? "default");
        return pending;
    }

    // Query arguments take precedence over the request body, matching Jellyfin's MediaInfoController.
    private static T? Argument<T>(ActionExecutingContext context, object? body, string argument, string property)
    {
        if (context.ActionArguments.TryGetValue(argument, out var value) && value is T typed)
        {
            return typed;
        }

        return body?.GetType().GetProperty(property)?.GetValue(body) is T fromBody ? fromBody : default;
    }

}
