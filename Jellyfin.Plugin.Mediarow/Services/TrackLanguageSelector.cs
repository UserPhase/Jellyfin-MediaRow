using System.Text.RegularExpressions;
using Jellyfin.Plugin.Mediarow.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Effective language preference for one user and title.
/// </summary>
public sealed record LanguagePreference(
    IReadOnlyList<string> AudioLanguages,
    IReadOnlyList<string> SubtitleLanguages,
    bool PreferOriginalAudio,
    bool PreferNonForcedSubtitles,
    bool PreferForcedWhenAudioMatches,
    bool IsUserSpecific,
    SubtitleMode SubtitleMode = SubtitleMode.WhenNeeded)
{
    /// <summary>
    /// Languages used to rank AIOStreams releases: the original language first when preferred, then the audio list.
    /// </summary>
    public IReadOnlyList<string> SourceLanguages(string? originalLanguage)
    {
        var languages = new List<string>();
        if (PreferOriginalAudio && TrackLanguageSelector.Normalize(originalLanguage) is { } original)
        {
            languages.Add(original);
        }

        languages.AddRange(AudioLanguages.Select(TrackLanguageSelector.Normalize).OfType<string>());
        return languages.Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// Preferred tracks. A null index leaves Jellyfin's choice; a subtitle index of -1 turns subtitles off.
/// </summary>
public readonly record struct TrackSelection(int? AudioStreamIndex, int? SubtitleStreamIndex);

/// <summary>
/// Chooses audio and subtitle tracks from per-user language preferences.
/// Selection rules adapted from the MIT-licensed Language Failover plugin by Hightmar.
/// </summary>
public static partial class TrackLanguageSelector
{
    public const int SubtitlesOff = -1;

    [GeneratedRegex(@"\b(original|version\s+originale|v\.?\s*o\.?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalVersionRegex();

    [GeneratedRegex(@"\b(forced|forc[ée]e?s?|forzado(?:s)?|forzat[io]|erzwungen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForcedSubtitleRegex();

    [GeneratedRegex(@"\b(?:non|not)[\s-]?forc", RegexOptions.IgnoreCase)]
    private static partial Regex NonForcedSubtitleRegex();

    // Anime releases name signs-only tracks instead of flagging them forced ("Signs & Songs", "S&S", "Signs").
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:s\s*[&+n]\s*s|signs(?:\s*(?:&|and|/)\s*songs)?|songs\s*(?:&|and|/)\s*signs)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex SignsAndSongsRegex();

    [GeneratedRegex(@"\b(?:full|dialogues?|dialogs?|complete)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompleteTrackRegex();

    /// <summary>
    /// Resolves the effective preference for a user and title. Series overrides replace the user's lists;
    /// users without preferences fall back to the global original-language option when <paramref name="useGlobalFallback"/> is set.
    /// </summary>
    public static LanguagePreference? Resolve(PluginConfiguration config, Guid userId, BaseItem item, bool useGlobalFallback)
    {
        var user = FindUser(config, userId);
        if (user == null)
        {
            return useGlobalFallback && config.PreferOriginalLanguageStreams
                ? new LanguagePreference([], [], true, true, true, false)
                : null;
        }

        IReadOnlyList<string> audio = user.AudioLanguages ?? [];
        IReadOnlyList<string> subtitles = user.SubtitleLanguages ?? [];
        var mode = user.SubtitleMode;
        var preferOriginal = user.PreferOriginalAudio;
        if (item is Episode { SeriesId: var seriesId } && seriesId != Guid.Empty)
        {
            var match = FindSeriesOverride(user, seriesId);
            if (match != null)
            {
                audio = match.AudioLanguages is { Count: > 0 } ? match.AudioLanguages : audio;
                subtitles = match.SubtitleLanguages is { Count: > 0 } ? match.SubtitleLanguages : subtitles;
                mode = match.SubtitleMode ?? mode;
                preferOriginal = match.PreferOriginalAudio ?? preferOriginal;
            }
        }

        audio = Clean(audio);
        subtitles = Clean(subtitles);
        var subtitlesActive = mode is SubtitleMode.Off or SubtitleMode.ForcedOnly ||
            (mode is SubtitleMode.WhenNeeded or SubtitleMode.Always && subtitles.Count > 0);
        if (!preferOriginal && audio.Count == 0 && !subtitlesActive)
        {
            return null;
        }

        return new LanguagePreference(audio, subtitles, preferOriginal,
            user.PreferNonForcedSubtitles, user.PreferForcedWhenAudioMatches, true, mode);
    }

    /// <summary>Finds the enabled preference for a user.</summary>
    public static UserLanguagePreference? FindUser(PluginConfiguration config, Guid userId)
    {
        var key = userId.ToString("N");
        return config.UserLanguagePreferences?.FirstOrDefault(preference =>
            preference.Enabled && string.Equals(preference.UserId?.Replace("-", string.Empty, StringComparison.Ordinal), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds a user's override for a series.</summary>
    public static SeriesLanguageOverride? FindSeriesOverride(UserLanguagePreference user, Guid seriesId)
    {
        var seriesKey = seriesId.ToString("N");
        return user.SeriesOverrides?.FirstOrDefault(entry =>
            string.Equals(entry.SeriesId?.Replace("-", string.Empty, StringComparison.Ordinal), seriesKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Picks audio and subtitle tracks for the preference.
    /// </summary>
    public static TrackSelection Select(IReadOnlyList<MediaStream> streams, LanguagePreference preference, string? originalLanguage, int? manualAudioIndex = null)
    {
        var audio = streams.Where(stream => stream.Type == MediaStreamType.Audio).ToList();
        MediaStream? chosenAudio = manualAudioIndex is int manual ? audio.FirstOrDefault(stream => stream.Index == manual) : null;
        if (chosenAudio != null)
        {
            // The user picked the audio; subtitles follow the language actually being heard.
            return new TrackSelection(chosenAudio.Index, SelectSubtitle(streams, preference, LanguageOf(chosenAudio)));
        }

        if (preference.PreferOriginalAudio)
        {
            chosenAudio = SelectOriginalAudio(audio, originalLanguage);
        }

        chosenAudio ??= SelectByPriority(audio, preference.AudioLanguages, BestOf);
        var playingAudio = chosenAudio ?? audio.FirstOrDefault(stream => stream.IsDefault) ?? audio.FirstOrDefault();
        return new TrackSelection(chosenAudio?.Index, SelectSubtitle(streams, preference, LanguageOf(playingAudio)));
    }

    /// <summary>
    /// Finds the original-version audio track: container flag, then track title, then the title's original language.
    /// </summary>
    public static MediaStream? SelectOriginalAudio(IReadOnlyList<MediaStream> audio, string? originalLanguage)
    {
        var flagged = audio.Where(stream => stream.IsOriginal).ToList();
        if (flagged.Count > 0)
        {
            return BestOf(flagged);
        }

        var titled = audio.Where(stream => !string.IsNullOrEmpty(stream.Title) && OriginalVersionRegex().IsMatch(stream.Title)).ToList();
        if (titled.Count > 0)
        {
            return BestOf(titled);
        }

        var original = Normalize(originalLanguage);
        var spoken = original == null ? [] : audio.Where(stream => LanguageOf(stream) == original).ToList();
        return spoken.Count > 0 ? BestOf(spoken) : null;
    }

    public static bool IsForcedSubtitle(MediaStream stream)
        => stream.IsForced || TitleIndicatesForced(stream.Title) || TitleIndicatesForced(stream.DisplayTitle);

    /// <summary>
    /// Normalizes a language code to ISO 639-1 when known, otherwise lower-case; null for empty or undetermined.
    /// </summary>
    public static string? Normalize(string? language)
    {
        var value = language?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return StreamLanguageDetector.NormalizeCode(value) ?? value.ToLowerInvariant();
    }

    /// <summary>
    /// A track's language from its tag, or from its title when the tag is missing ("English 5.1").
    /// </summary>
    public static string? LanguageOf(MediaStream? stream)
    {
        if (stream == null)
        {
            return null;
        }

        if (Normalize(stream.Language) is { } tagged)
        {
            return tagged;
        }

        var detected = StreamLanguageDetector.Detect(stream.Title).Languages;
        return detected.Count == 1 ? detected.First() : null;
    }

    private static int? SelectSubtitle(IReadOnlyList<MediaStream> streams, LanguagePreference preference, string? audioLanguage)
    {
        var languages = preference.SubtitleLanguages;
        var subtitles = streams.Where(stream => stream.Type == MediaStreamType.Subtitle).ToList();
        switch (preference.SubtitleMode)
        {
            case SubtitleMode.Default:
                return null;
            case SubtitleMode.Off:
                return SubtitlesOff;
            case SubtitleMode.ForcedOnly:
                var forcedOnly = subtitles.Where(IsForcedSubtitle).ToList();
                var inAudioLanguage = audioLanguage == null ? null : forcedOnly.FirstOrDefault(stream => LanguageOf(stream) == audioLanguage);
                return (inAudioLanguage ?? SelectByPriority(forcedOnly, languages, matches => matches[0]))?.Index ?? SubtitlesOff;
            case SubtitleMode.Always:
                return SelectByPriority(subtitles, languages, matches => PickSubtitle(matches, preference))?.Index;
        }

        if (languages.Count == 0)
        {
            return null;
        }

        var audioPriority = audioLanguage == null ? -1 : IndexOf(languages, audioLanguage);
        var bestAvailable = Enumerable.Range(0, languages.Count)
            .FirstOrDefault(index => subtitles.Any(stream => LanguageOf(stream) == Normalize(languages[index])), -1);

        // The viewer understands the audio at least as well as any subtitle on offer.
        if (audioPriority >= 0 && (bestAvailable < 0 || audioPriority <= bestAvailable))
        {
            var forced = preference.PreferForcedWhenAudioMatches
                ? subtitles.FirstOrDefault(stream => IsForcedSubtitle(stream) && LanguageOf(stream) == audioLanguage)
                : null;
            return forced?.Index ?? SubtitlesOff;
        }

        return SelectByPriority(subtitles, languages, matches => PickSubtitle(matches, preference))?.Index;
    }

    private static MediaStream PickSubtitle(List<MediaStream> matches, LanguagePreference preference)
        => preference.PreferNonForcedSubtitles
            ? matches.FirstOrDefault(stream => !IsForcedSubtitle(stream)) ?? matches[0]
            : matches[0];

    private static MediaStream? SelectByPriority(List<MediaStream> candidates, IReadOnlyList<string> languages, Func<List<MediaStream>, MediaStream> pick)
    {
        foreach (var language in languages.Select(Normalize).OfType<string>())
        {
            var matches = candidates.Where(stream => LanguageOf(stream) == language).ToList();
            if (matches.Count > 0)
            {
                return pick(matches);
            }
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<string> languages, string language)
    {
        for (var index = 0; index < languages.Count; index++)
        {
            if (Normalize(languages[index]) == language)
            {
                return index;
            }
        }

        return -1;
    }

    // Most channels wins (7.1 > 5.1 > stereo); ties keep stream order.
    private static MediaStream BestOf(List<MediaStream> matches) => matches.OrderByDescending(stream => stream.Channels ?? 0).First();

    private static bool TitleIndicatesForced(string? title)
    {
        if (string.IsNullOrEmpty(title) || NonForcedSubtitleRegex().IsMatch(title))
        {
            return false;
        }

        return ForcedSubtitleRegex().IsMatch(title) || (SignsAndSongsRegex().IsMatch(title) && !CompleteTrackRegex().IsMatch(title));
    }

    private static List<string> Clean(IEnumerable<string> languages)
        => languages.Where(language => Normalize(language) != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
