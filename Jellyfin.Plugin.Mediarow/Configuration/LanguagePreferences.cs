namespace Jellyfin.Plugin.Mediarow.Configuration;

// Per-user audio/subtitle preferences, adapted from the MIT-licensed Language Failover plugin by Hightmar.

/// <summary>
/// Audio and subtitle language preferences for one Jellyfin user.
/// </summary>
public class UserLanguagePreference
{
    /// <summary>Gets or sets the Jellyfin user ID (N format).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether these preferences are applied.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the title's original-language audio wins over the priority list.</summary>
    public bool PreferOriginalAudio { get; set; } = true;

    /// <summary>Gets or sets audio languages in priority order (ISO 639 codes).</summary>
    public List<string> AudioLanguages { get; set; } = new();

    /// <summary>Gets or sets subtitle languages in priority order.</summary>
    public List<string> SubtitleLanguages { get; set; } = new();

    /// <summary>Gets or sets when subtitles are shown.</summary>
    public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.WhenNeeded;

    /// <summary>Gets or sets a value indicating whether full subtitles are preferred over forced ones.</summary>
    public bool PreferNonForcedSubtitles { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether forced subtitles are shown when the audio is already in a subtitle language.</summary>
    public bool PreferForcedWhenAudioMatches { get; set; } = true;

    /// <summary>Gets or sets per-series language overrides.</summary>
    public List<SeriesLanguageOverride> SeriesOverrides { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether track changes during an episode become a series override for this user.</summary>
    public bool RememberSeriesChoices { get; set; } = true;
}

/// <summary>
/// When a user's subtitles are shown.
/// </summary>
public enum SubtitleMode
{
    /// <summary>Leave subtitles to Jellyfin's own user setting.</summary>
    Default,

    /// <summary>Full subtitles when the audio is not in a subtitle language; otherwise forced subtitles or none.</summary>
    WhenNeeded,

    /// <summary>Full subtitles in the first available subtitle language, whatever the audio.</summary>
    Always,

    /// <summary>Only forced (signs/foreign dialogue) subtitles.</summary>
    ForcedOnly,

    /// <summary>No subtitles.</summary>
    Off,
}

/// <summary>
/// Replaces a user's language lists for one series.
/// </summary>
public class SeriesLanguageOverride
{
    /// <summary>Gets or sets the series ID (N format).</summary>
    public string SeriesId { get; set; } = string.Empty;

    /// <summary>Gets or sets the series name, for display.</summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>Gets or sets the audio languages; empty keeps the user's list.</summary>
    public List<string> AudioLanguages { get; set; } = new();

    /// <summary>Gets or sets the subtitle languages; empty keeps the user's list.</summary>
    public List<string> SubtitleLanguages { get; set; } = new();

    /// <summary>Gets or sets the subtitle mode for this series; null keeps the user's mode.</summary>
    public SubtitleMode? SubtitleMode { get; set; }

    /// <summary>Gets or sets whether the original audio wins for this series; null keeps the user's setting.</summary>
    public bool? PreferOriginalAudio { get; set; }

    /// <summary>Gets or sets a value indicating whether Mediarow created this override from a track change during playback.</summary>
    public bool Learned { get; set; }
}
