using Jellyfin.Plugin.Mediarow.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Turns a user's manual track change during an episode into a series override, so the following episodes start
/// with the same audio and subtitles.
/// </summary>
public static class SeriesLanguageMemory
{
    private static readonly object Gate = new();

    /// <summary>
    /// Records the chosen tracks for the episode's series. Returns true when the configuration changed and must be saved.
    /// </summary>
    /// <param name="subtitleIndex">The subtitle track, or null / <see cref="TrackLanguageSelector.SubtitlesOff"/> when subtitles are off.</param>
    public static bool Remember(
        PluginConfiguration config,
        Guid userId,
        BaseItem item,
        IReadOnlyList<MediaStream> streams,
        int? audioIndex,
        int? subtitleIndex,
        bool audioChanged,
        bool subtitleChanged)
    {
        if (item is not Episode { SeriesId: var seriesId } episode || seriesId == Guid.Empty || (!audioChanged && !subtitleChanged))
        {
            return false;
        }

        var audioLanguage = audioChanged
            ? TrackLanguageSelector.LanguageOf(streams.FirstOrDefault(stream => stream.Type == MediaStreamType.Audio && stream.Index == audioIndex))
            : null;
        var subtitle = subtitleChanged && subtitleIndex is int index && index >= 0
            ? streams.FirstOrDefault(stream => stream.Type == MediaStreamType.Subtitle && stream.Index == index)
            : null;
        var subtitlesOff = subtitleChanged && (subtitleIndex is null || subtitleIndex < 0);
        var subtitleLanguage = TrackLanguageSelector.LanguageOf(subtitle);
        if (audioLanguage == null && !subtitlesOff && subtitleLanguage == null)
        {
            return false;
        }

        lock (Gate)
        {
            var user = TrackLanguageSelector.FindUser(config, userId);
            if (user is not { RememberSeriesChoices: true })
            {
                return false;
            }

            user.SeriesOverrides ??= new();
            var entry = TrackLanguageSelector.FindSeriesOverride(user, seriesId);
            var created = entry == null;
            entry ??= new SeriesLanguageOverride { SeriesId = seriesId.ToString("N"), SeriesName = episode.SeriesName ?? string.Empty, Learned = true };
            var changed = false;

            if (audioLanguage != null &&
                (entry.AudioLanguages is not [var currentAudio] || !string.Equals(currentAudio, audioLanguage, StringComparison.OrdinalIgnoreCase) ||
                 entry.PreferOriginalAudio != false))
            {
                entry.AudioLanguages = [audioLanguage];
                entry.PreferOriginalAudio = false;
                changed = true;
            }

            if (subtitlesOff && entry.SubtitleMode != SubtitleMode.Off)
            {
                entry.SubtitleMode = SubtitleMode.Off;
                changed = true;
            }
            else if (subtitleLanguage != null)
            {
                var mode = TrackLanguageSelector.IsForcedSubtitle(subtitle!) ? SubtitleMode.ForcedOnly : SubtitleMode.Always;
                if (entry.SubtitleMode != mode || entry.SubtitleLanguages is not [var currentSubtitle] ||
                    !string.Equals(currentSubtitle, subtitleLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    entry.SubtitleLanguages = [subtitleLanguage];
                    entry.SubtitleMode = mode;
                    changed = true;
                }
            }

            if (changed && created)
            {
                user.SeriesOverrides.Add(entry);
            }

            return changed;
        }
    }
}
