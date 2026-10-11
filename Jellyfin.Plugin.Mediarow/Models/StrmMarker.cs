namespace Jellyfin.Plugin.Mediarow.Models;

/// <summary>
/// Placeholder written into persisted .strm files. Real streams are resolved at playback time.
/// </summary>
public static class StrmMarker
{
    public const string Scheme = "mediarow://";

    // Written by installs made before the rename; still recognized so existing libraries keep playing.
    private const string LegacyScheme = "dynamiclibrary://";

    public static bool IsMarker(string? content) =>
        content is not null
        && (content.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            || content.StartsWith(LegacyScheme, StringComparison.OrdinalIgnoreCase));

    public static bool IsAnime(string? content) =>
        content is not null
        && (content.StartsWith(Scheme + "anime/", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith(LegacyScheme + "anime/", StringComparison.OrdinalIgnoreCase));
}
