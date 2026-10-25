using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Removes catalog search results that already exist in Jellyfin.
/// </summary>
public static class CatalogDuplicateFilter
{
    private static readonly string[] ProviderNames = ["Imdb", "Tmdb", "Tvdb"];

    public static List<BaseItemDto> ExcludeKnown(IEnumerable<BaseItemDto> results, IEnumerable<BaseItemDto> knownItems)
    {
        var knownKeys = knownItems
            .SelectMany(item => GetKeys(item.Type, item.ProviderIds))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return results
            .Where(item => !GetKeys(item.Type, item.ProviderIds).Any(knownKeys.Contains))
            .ToList();
    }

    /// <summary>
    /// Uses one indexed provider-ID query instead of loading every movie and series in the library.
    /// </summary>
    public static List<BaseItemDto> ExcludeLibraryItems(
        ILibraryManager libraryManager,
        IReadOnlyList<BaseItemDto> results,
        ILogger logger)
    {
        var providerIds = ProviderNames
            .Select(provider => new
            {
                Provider = provider,
                Values = results
                    .Select(item => item.ProviderIds is not null && item.ProviderIds.TryGetValue(provider, out var value) ? value : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .Where(entry => entry.Values.Length > 0)
            .ToDictionary(entry => entry.Provider, entry => entry.Values, StringComparer.OrdinalIgnoreCase);

        if (providerIds.Count == 0)
        {
            return results.ToList();
        }

        try
        {
            var libraryItems = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true,
                HasAnyProviderIds = providerIds,
                EnableTotalRecordCount = false
            });
            var libraryKeys = libraryItems
                .SelectMany(item => GetKeys(item.GetBaseItemKind(), item.ProviderIds))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return results
                .Where(item => !GetKeys(item.Type, item.ProviderIds).Any(libraryKeys.Contains))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Mediarow] Could not check search results against the Jellyfin library");
            return results.ToList();
        }
    }

    public static IEnumerable<string> GetKeys(BaseItemKind kind, IReadOnlyDictionary<string, string>? providerIds)
    {
        if (providerIds is null || (kind != BaseItemKind.Movie && kind != BaseItemKind.Series))
        {
            yield break;
        }

        if (providerIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrWhiteSpace(imdb))
        {
            yield return "imdb:" + imdb.Trim();
        }

        // TMDB and TVDB IDs are only unique within an item type.
        foreach (var provider in new[] { "Tmdb", "Tvdb" })
        {
            if (providerIds.TryGetValue(provider, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                yield return $"{kind}:{provider}:{value.Trim()}";
            }
        }
    }
}
