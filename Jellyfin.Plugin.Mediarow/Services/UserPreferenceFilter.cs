using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Models;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Applies a user's personal Home preferences to rows and items before they are serialized.
/// Order: section visibility/order → (permission guard, done by callers) → Not interested → Hide watched.
/// </summary>
public sealed class UserPreferenceFilter
{
    private readonly UserPreferenceStore _store;
    private readonly IWatchedTitleLookup _watched;

    public UserPreferenceFilter(UserPreferenceStore store, IWatchedTitleLookup watched)
    {
        _store = store;
        _watched = watched;
    }

    public UserHomePreferences GetPreferences(Guid userId) => _store.Get(userId);

    public bool IsSectionVisible(Guid userId, string sectionId) =>
        userId == Guid.Empty || !_store.Get(userId).IsSectionHidden(sectionId);

    /// <summary>
    /// Visible sections in the user's order. Sections the user never ordered keep their server order after the ordered ones.
    /// </summary>
    public IReadOnlyList<T> ArrangeSections<T>(Guid userId, IEnumerable<T> sections, Func<T, string> getId)
    {
        UserHomePreferences preferences = userId == Guid.Empty ? new UserHomePreferences() : _store.Get(userId);
        return Arrange(preferences, sections, getId, includeHidden: false);
    }

    public static IReadOnlyList<T> Arrange<T>(
        UserHomePreferences preferences,
        IEnumerable<T> sections,
        Func<T, string> getId,
        bool includeHidden)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in preferences.SectionOrder)
        {
            rank.TryAdd(id, rank.Count);
        }

        return sections
            .Select((section, index) => (Section: section, Index: index, Id: getId(section)))
            .Where(entry => includeHidden || !preferences.IsSectionHidden(entry.Id))
            .OrderBy(entry => rank.TryGetValue(entry.Id, out int position) ? position : int.MaxValue)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Section)
            .ToArray();
    }

    /// <summary>
    /// Removes the user's "Not interested" titles and, when enabled, titles the user has fully watched.
    /// Never mutates the input items (they may be shared cache instances).
    /// </summary>
    public IReadOnlyList<BaseItemDto> FilterItems(Guid userId, IEnumerable<BaseItemDto> items)
    {
        BaseItemDto[] input = items as BaseItemDto[] ?? items.ToArray();
        if (userId == Guid.Empty || input.Length == 0)
        {
            return input;
        }

        UserHomePreferences preferences = _store.Get(userId);
        IReadOnlyList<BaseItemDto> remaining = ExcludeNotInterested(preferences.NotInterested, input);
        if (!preferences.HideWatched || remaining.Count == 0)
        {
            return remaining;
        }

        IReadOnlySet<Guid> watched = _watched.GetWatchedItemIds(userId, remaining);
        return watched.Count == 0 ? remaining : remaining.Where(item => !watched.Contains(item.Id)).ToArray();
    }

    public static IReadOnlyList<BaseItemDto> ExcludeNotInterested(
        IReadOnlyCollection<NotInterestedEntry> entries,
        IReadOnlyList<BaseItemDto> items)
    {
        if (entries.Count == 0)
        {
            return items;
        }

        var ids = entries.Select(entry => entry.ItemId).ToHashSet();
        var keys = entries
            .Where(entry => entry.Type.HasValue)
            .SelectMany(entry => CatalogDuplicateFilter.GetKeys(entry.Type!.Value, entry.ProviderIds))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return items
            .Where(item => !ids.Contains(item.Id) && !CatalogDuplicateFilter.GetKeys(item.Type, item.ProviderIds).Any(keys.Contains))
            .ToArray();
    }

    public static NotInterestedEntry CreateEntry(Guid itemId, BaseItemDto? item) => new()
    {
        ItemId = itemId,
        Name = item?.Name,
        Type = item?.Type is BaseItemKind.Movie or BaseItemKind.Series ? item.Type : null,
        ProductionYear = item?.ProductionYear,
        ProviderIds = item?.ProviderIds == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : item.ProviderIds
                .Where(pair => pair.Key is "Imdb" or "Tmdb" or "Tvdb" && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
    };
}
