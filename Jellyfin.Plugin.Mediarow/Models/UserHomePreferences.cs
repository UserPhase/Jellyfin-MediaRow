using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.Mediarow.Models;

/// <summary>
/// One user's personal Home layout. Stored per user id in user-preferences.json.
/// </summary>
public sealed class UserHomePreferences
{
    /// <summary>Section ids in the user's preferred order. Sections not listed keep the server order after these.</summary>
    public List<string> SectionOrder { get; set; } = new();

    /// <summary>Section ids (catalog, genre or Popular rows) the user has hidden.</summary>
    public List<string> HiddenSections { get; set; } = new();

    /// <summary>Hides watched movies and verified completed, ended series from discovery rows only.</summary>
    public bool HideWatched { get; set; }

    /// <summary>Titles the user dismissed with "Not interested".</summary>
    public List<NotInterestedEntry> NotInterested { get; set; } = new();

    /// <summary>
    /// Enabled-section allow-list migrated from the old home-sections-users.json (null = no legacy restriction).
    /// Cleared the first time the user saves <see cref="HiddenSections"/>.
    /// </summary>
    public List<string>? LegacyEnabledSections { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public UserHomePreferences Clone() => new()
    {
        SectionOrder = new List<string>(SectionOrder),
        HiddenSections = new List<string>(HiddenSections),
        HideWatched = HideWatched,
        NotInterested = NotInterested.Select(entry => entry.Clone()).ToList(),
        LegacyEnabledSections = LegacyEnabledSections == null ? null : new List<string>(LegacyEnabledSections),
        UpdatedUtc = UpdatedUtc
    };

    public bool IsSectionHidden(string sectionId) =>
        HiddenSections.Contains(sectionId, StringComparer.OrdinalIgnoreCase) ||
        (LegacyEnabledSections != null && !LegacyEnabledSections.Contains(sectionId, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// A dismissed title. Provider ids are kept so the title stays hidden when it appears under another virtual id
/// (another catalog, or after it is imported into the library).
/// </summary>
public sealed class NotInterestedEntry
{
    public Guid ItemId { get; set; }

    public string? Name { get; set; }

    public BaseItemKind? Type { get; set; }

    public int? ProductionYear { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime AddedUtc { get; set; }

    public NotInterestedEntry Clone() => new()
    {
        ItemId = ItemId,
        Name = Name,
        Type = Type,
        ProductionYear = ProductionYear,
        ProviderIds = new Dictionary<string, string>(ProviderIds, StringComparer.OrdinalIgnoreCase),
        AddedUtc = AddedUtc
    };
}

/// <summary>
/// Partial update for POST /Plugins/AIOStreams/UserPreferences. Null fields are left unchanged.
/// </summary>
public sealed class UserPreferencesUpdate
{
    public Guid UserId { get; set; }

    public List<string>? SectionOrder { get; set; }

    public List<string>? HiddenSections { get; set; }

    public bool? HideWatched { get; set; }

    /// <summary>Item ids to remove from the Not interested list (undo).</summary>
    public List<Guid>? RemoveNotInterested { get; set; }
}

/// <summary>
/// GET /Plugins/AIOStreams/UserPreferences response: the saved preferences plus every Home row in the user's order.
/// </summary>
public sealed class UserPreferencesResponse
{
    public Guid UserId { get; init; }

    public bool HideWatched { get; init; }

    public IReadOnlyList<string> SectionOrder { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> HiddenSections { get; init; } = Array.Empty<string>();

    public IReadOnlyList<NotInterestedEntry> NotInterested { get; init; } = Array.Empty<NotInterestedEntry>();

    public IReadOnlyList<UserPreferenceSection> Sections { get; init; } = Array.Empty<UserPreferenceSection>();

    public DateTime UpdatedUtc { get; init; }
}

public sealed record UserPreferenceSection(string Id, string DisplayTitle, int Order, bool Visible, string Kind);
