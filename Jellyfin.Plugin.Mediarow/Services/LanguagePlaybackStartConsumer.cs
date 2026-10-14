using System.Globalization;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Filters;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Switches to the preferred tracks after playback starts when a client ignored the tracks chosen by the server.
/// Approach adapted from the MIT-licensed Language Failover plugin by Hightmar, but only sends commands when the
/// playing track actually differs, so clients that honoured the server choice never restart their stream.
/// </summary>
public sealed class LanguagePlaybackStartConsumer(
    IMemoryCache cache,
    ISessionManager sessionManager,
    IMediaSourceManager mediaSourceManager,
    ILogger<LanguagePlaybackStartConsumer> logger) : IEventConsumer<PlaybackStartEventArgs>
{
    // TV clients revert track commands sent while the player is still initializing.
    public TimeSpan PlayerInitDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    public TimeSpan BetweenCommandsDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public PluginConfiguration? Config { get; init; }

    public Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        var config = Config ?? MediarowPlugin.Instance?.Configuration;
        if (config is not { CorrectTracksAfterPlaybackStart: true } || eventArgs.Item == null ||
            eventArgs.Session == null || eventArgs.Users is not { Count: > 0 })
        {
            return Task.CompletedTask;
        }

        // Do not hold up Jellyfin's playback-start pipeline while waiting for the player.
        _ = Task.Run(async () =>
        {
            try
            {
                await CorrectAsync(eventArgs, config).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug("[Mediarow] Track correction skipped for {Item} ({ErrorType})", eventArgs.Item.Name, ex.GetType().Name);
            }
        });
        return Task.CompletedTask;
    }

    public async Task CorrectAsync(PlaybackStartEventArgs eventArgs, PluginConfiguration config)
    {
        var item = eventArgs.Item;
        var userId = eventArgs.Users[0].Id;
        if (!cache.TryGetValue<TrackDecision>(TrackDecision.CacheKey(userId, item.Id), out var decision) || decision == null)
        {
            // The client skipped PlaybackInfo negotiation; decide from the item's stored tracks like Language Failover did.
            var preference = TrackLanguageSelector.Resolve(config, userId, item, useGlobalFallback: false);
            if (preference == null)
            {
                return;
            }

            var streams = Guid.TryParse(eventArgs.MediaSourceId, out var sourceId) && sourceId != item.Id
                ? mediaSourceManager.GetMediaStreams(sourceId)
                : [];
            if (streams.Count == 0)
            {
                streams = mediaSourceManager.GetMediaStreams(item.Id);
            }

            var selection = TrackLanguageSelector.Select(streams, preference, item.GetInheritedOriginalLanguage());
            decision = new TrackDecision(selection.AudioStreamIndex, selection.SubtitleStreamIndex, false, false);
        }

        await Task.Delay(PlayerInitDelay).ConfigureAwait(false);
        var session = eventArgs.Session;
        if (session.NowPlayingItem?.Id != item.Id)
        {
            return;
        }

        if (!decision.AudioManual && decision.AudioStreamIndex is { } audio && session.PlayState?.AudioStreamIndex != audio &&
            await SendAsync(session, GeneralCommandType.SetAudioStreamIndex, audio).ConfigureAwait(false))
        {
            await Task.Delay(BetweenCommandsDelay).ConfigureAwait(false);
        }

        if (!decision.SubtitleManual && decision.SubtitleStreamIndex is { } subtitle &&
            (session.PlayState?.SubtitleStreamIndex ?? TrackLanguageSelector.SubtitlesOff) != subtitle)
        {
            await SendAsync(session, GeneralCommandType.SetSubtitleStreamIndex, subtitle).ConfigureAwait(false);
        }
    }

    private async Task<bool> SendAsync(SessionInfo session, GeneralCommandType type, int index)
    {
        if (!session.SupportedCommands.Contains(type))
        {
            return false;
        }

        var command = new GeneralCommand { Name = type };
        command.Arguments["Index"] = index.ToString(CultureInfo.InvariantCulture);
        await sessionManager.SendGeneralCommand(string.Empty, session.Id, command, CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("[Mediarow] Switched {Client} to preferred track {Index} ({Command})", session.Client, index, type);
        return true;
    }
}
