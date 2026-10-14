using System.Collections.Concurrent;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Filters;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Watches playback progress for track changes the user makes during an episode and remembers them for the series.
/// </summary>
public sealed class LanguageTrackChangeConsumer(
    IMemoryCache cache,
    IMediaSourceManager mediaSourceManager,
    ILogger<LanguageTrackChangeConsumer> logger) : IEventConsumer<PlaybackProgressEventArgs>, IEventConsumer<PlaybackStopEventArgs>
{
    // Consumers are scoped, so the last seen tracks per session live in a static map.
    private static readonly ConcurrentDictionary<string, PlayedTracks> LastSeen = new();

    public PluginConfiguration? Config { get; init; }

    public Action? SaveConfiguration { get; init; } = () => MediarowPlugin.Instance?.SaveConfiguration();

    public Task OnEvent(PlaybackProgressEventArgs eventArgs)
    {
        var config = Config ?? MediarowPlugin.Instance?.Configuration;
        if (config == null || eventArgs.Item is not Episode episode || eventArgs.Session?.PlayState is not { } state ||
            eventArgs.Users is not { Count: > 0 })
        {
            return Task.CompletedTask;
        }

        var userId = eventArgs.Users[0].Id;
        if (TrackLanguageSelector.FindUser(config, userId) is not { RememberSeriesChoices: true })
        {
            return Task.CompletedTask;
        }

        var current = new PlayedTracks(episode.Id, state.AudioStreamIndex, state.SubtitleStreamIndex ?? TrackLanguageSelector.SubtitlesOff);
        var key = eventArgs.Session.Id;
        if (!LastSeen.TryGetValue(key, out var previous))
        {
            LastSeen[key] = current;
            return Task.CompletedTask;
        }

        LastSeen[key] = current;
        if (previous.ItemId != current.ItemId)
        {
            return Task.CompletedTask;
        }

        // A switch onto the track Mediarow chose is the start-up correction, not a user choice.
        cache.TryGetValue<TrackDecision>(TrackDecision.CacheKey(userId, episode.Id), out var decision);
        var audioChanged = current.Audio is not null && previous.Audio != current.Audio && current.Audio != decision?.AudioStreamIndex;
        var subtitleChanged = previous.Subtitle != current.Subtitle &&
            current.Subtitle != (decision?.SubtitleStreamIndex ?? TrackLanguageSelector.SubtitlesOff);
        if (!audioChanged && !subtitleChanged)
        {
            return Task.CompletedTask;
        }

        var streams = decision?.Streams is { Count: > 0 } known ? known : mediaSourceManager.GetMediaStreams(episode.Id);
        if (SeriesLanguageMemory.Remember(config, userId, episode, streams, current.Audio, current.Subtitle, audioChanged, subtitleChanged))
        {
            SaveConfiguration?.Invoke();
            logger.LogInformation("[Mediarow] Remembered track change for {Series}", episode.SeriesName);
        }

        return Task.CompletedTask;
    }

    public Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        if (eventArgs.Session?.Id is { } id)
        {
            LastSeen.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    private sealed record PlayedTracks(Guid ItemId, int? Audio, int Subtitle);
}
