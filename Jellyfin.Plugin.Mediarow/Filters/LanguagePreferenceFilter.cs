using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Extensions;
using Jellyfin.Plugin.Mediarow.Providers;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
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
    ILogger<LanguagePreferenceFilter> logger) : IAsyncActionFilter
{
    /// <summary>HttpContext item holding the AIOStreams candidates for the current playback request.</summary>
    public const string CandidatesKey = "Mediarow.AioStreamsCandidates";

    public PluginConfiguration? Config { get; init; }

    public Action? SaveConfiguration { get; init; } = () => MediarowPlugin.Instance?.SaveConfiguration();

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "MediaInfo", ActionName: "GetPostedPlaybackInfo" } &&
            context.ActionArguments.TryGetValue("itemId", out var argument) && argument is Guid itemId)
        {
            try
            {
                await ApplyAsync(context, itemId).ConfigureAwait(false);
            }
            catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Language preferences must never block playback.
                logger.LogWarning("[Mediarow] Could not apply language preferences for item {ItemId} ({ErrorType})", itemId, ex.GetType().Name);
            }
        }

        await next().ConfigureAwait(false);
    }

    private async Task ApplyAsync(ActionExecutingContext context, Guid itemId)
    {
        var config = Config ?? MediarowPlugin.Instance?.Configuration;
        var item = libraryManager.GetItemById(itemId);
        if (config == null || item is not Video || !TryGetUser(context, out var user))
        {
            return;
        }

        context.ActionArguments.TryGetValue("playbackInfoDto", out var body);
        if (!string.IsNullOrWhiteSpace(Argument<string>(context, body, "liveStreamId", "LiveStreamId")))
        {
            return;
        }

        var candidates = context.HttpContext.Items[CandidatesKey] as IReadOnlyList<MediaSourceInfo>;
        var preference = TrackLanguageSelector.Resolve(config, user.Id, item, useGlobalFallback: candidates != null);
        if (preference == null)
        {
            return;
        }

        var sourceId = Argument<string>(context, body, "mediaSourceId", "MediaSourceId");
        MediaSourceInfo defaults;
        if (candidates != null)
        {
            var candidate = candidates.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
            {
                return;
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
                return;
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

        cache.Set(TrackDecision.CacheKey(user.Id, item.Id),
            new TrackDecision(audio ?? defaults.DefaultAudioStreamIndex, subtitle, audioManual, subtitleManual, defaults.MediaStreams),
            TimeSpan.FromMinutes(15));
        if ((audioManual || subtitleManual) && SeriesLanguageMemory.Remember(config, user.Id, item, defaults.MediaStreams,
                requestedAudio, requestedSubtitle, audioManual, subtitleManual))
        {
            SaveConfiguration?.Invoke();
            logger.LogInformation("[Mediarow] Remembered {User}'s track choice for {Series}", user.Username, (item as MediaBrowser.Controller.Entities.TV.Episode)?.SeriesName);
        }

        if (audio == requestedAudio && subtitle == requestedSubtitle)
        {
            return;
        }

        context.ActionArguments["audioStreamIndex"] = audio;
        context.ActionArguments["subtitleStreamIndex"] = subtitle;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            context.ActionArguments["mediaSourceId"] = defaults.Id;
        }

        logger.LogInformation("[Mediarow] Language preferences for {Item}: audio track {Audio}, subtitle track {Subtitle}",
            item.Name, audio?.ToString() ?? "default", subtitle?.ToString() ?? "default");
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

    private bool TryGetUser(ActionExecutingContext context, out User user)
    {
        user = null!;
        var userId = context.ActionArguments.TryGetValue("userId", out var value) && value is Guid id && id != Guid.Empty
            ? id
            : Guid.Empty;
        if (userId == Guid.Empty && !context.TryGetUserId(out userId))
        {
            return false;
        }

        user = userManager.GetUserById(userId)!;
        return user != null;
    }
}
