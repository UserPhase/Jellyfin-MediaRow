using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Mediarow.HomeScreen;

/// <summary>
/// Persisted virtual item data used to rebuild the Mediarow Continue Watching row.
/// </summary>
public sealed class VirtualResumeItem
{
    public required BaseItemDto Item { get; init; }

    public long PlaybackPositionTicks { get; init; }

    public DateTime LastPlayedDate { get; init; }
}
