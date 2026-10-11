using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Providers;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Provides ranked virtual catalog rows without querying Jellyfin's real library.
/// </summary>
public sealed class VirtualCatalogService
{
    private readonly MediarowService _mediarowService;
    private readonly StremioCatalogProvider _stremioCatalogProvider;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<VirtualCatalogService> _logger;

    public VirtualCatalogService(
        MediarowService mediarowService,
        StremioCatalogProvider stremioCatalogProvider,
        SearchResultFactory searchResultFactory,
        IMemoryCache cache,
        ILogger<VirtualCatalogService> logger)
    {
        _mediarowService = mediarowService;
        _stremioCatalogProvider = stremioCatalogProvider;
        _searchResultFactory = searchResultFactory;
        _cache = cache;
        _logger = logger;
    }

    private PluginConfiguration Config =>
        MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();


    public Task<IReadOnlyList<BaseItemDto>> GetVirtualItemsByGenreAsync(
        string genre,
        CancellationToken cancellationToken = default,
        string? contentCategory = null)
    {
        return GetRowAsync(genre, cancellationToken, contentCategory);
    }

    public Task<IReadOnlyList<BaseItemDto>> GetPopularVirtualItemsAsync(
        CancellationToken cancellationToken = default,
        string? contentCategory = null)
    {
        return GetRowAsync(null, cancellationToken, contentCategory);
    }

    private async Task<IReadOnlyList<BaseItemDto>> GetRowAsync(
        string? genre,
        CancellationToken cancellationToken,
        string? contentCategory = null)
    {
        var config = Config;
        var limit = Math.Clamp(config.HomeRowItemCount, 1, 100);
        var contentTypes = HomeRowContentFilter.GetContentTypes(config, contentCategory);
        if (contentTypes.Count == 0)
        {
            _logger.LogDebug("[Mediarow] Home rows have no content categories selected");
            return Array.Empty<BaseItemDto>();
        }

        var cacheKey = $"mediarow-home:{genre ?? "popular"}:{string.Join(',', HomeRowContentFilter.GetCategories(config, contentCategory).Order())}:{limit}:{config.HomeRowShuffle}:{config.HomeRowShuffleRefreshMinutes}:{config.HomeRowMinimumRating}";

        if (_cache.TryGetValue<IReadOnlyList<BaseItemDto>>(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        try
        {
            _logger.LogInformation("[Mediarow] Retrieving virtual {Row} row from {Provider} for {Count} items",
                genre ?? "Popular", config.CatalogProvider, limit);
            var items = new List<BaseItemDto>();
            if (config.CatalogProvider == CatalogProvider.StremioAddon)
            {
                var catalogsByType = await Task.WhenAll(contentTypes.Select(contentType =>
                    _stremioCatalogProvider.GetHomeCatalogAsync(
                        contentType == BaseItemKind.Movie ? CatalogContentType.Movie : CatalogContentType.Series,
                        genre,
                        limit * 3,
                        cancellationToken)));

                var catalogItems = catalogsByType.SelectMany(itemsForType => itemsForType);
                items = (await Task.WhenAll(catalogItems.Select(catalogItem =>
                    _searchResultFactory.CreateDtoFromCatalogItemAsync(catalogItem, cancellationToken)))).ToList();
            }

            if (items.Count == 0)
            {
                var query = genre ?? "popular";
                items = await _mediarowService.SearchAsync(query, contentTypes, cancellationToken);
            }

            if (genre != null)
            {
                items = items
                    .Where(item => HasGenre(item, genre))
                    .ToList();
            }

            items = items
                .Where(item => HomeRowContentFilter.Matches(item, config, contentCategory))
                .Where(item => !config.HomeRowMinimumRating.HasValue() ||
                    item.CommunityRating >= config.HomeRowMinimumRating)
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .OrderByDescending(item => item.CommunityRating ?? 0)
                .ThenByDescending(item => item.ProductionYear ?? 0)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id)
                .ToList();

            if (items.Count == 0 && genre == null)
            {
                _logger.LogWarning("[Mediarow] Popular virtual row returned no provider results");
            }

            if (config.HomeRowShuffle && items.Count > 1)
            {
                Shuffle(items, BuildSeed(genre ?? "popular", config.HomeRowShuffleRefreshMinutes));
            }

            var result = items.Take(limit).ToList();
            _cache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Clamp(config.HomeRowShuffleRefreshMinutes, 1, 1440)));
            _logger.LogInformation("[Mediarow] Built virtual {Row} row with {Count} items",
                genre ?? "Popular", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mediarow] Failed to build virtual {Row} row", genre ?? "Popular");
            return Array.Empty<BaseItemDto>();
        }
    }

    private static bool HasGenre(BaseItemDto item, string genre)
    {
        if (genre.Equals("Anime", StringComparison.OrdinalIgnoreCase))
        {
            return MediarowService.IsAnime(item) ||
                item.Genres?.Any(value => value.Contains("anime", StringComparison.OrdinalIgnoreCase)) == true;
        }

        var normalizedGenre = NormalizeGenre(genre);
        return item.Genres?.Any(value => NormalizeGenre(value) == normalizedGenre) == true;
    }

    private static string NormalizeGenre(string value)
    {
        return value.Replace("-", " ", StringComparison.Ordinal)
            .Replace("&", "and", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant() switch
        {
            "sci fi" or "scifi" or "science fiction" => "science fiction",
            _ => value.Replace("-", " ", StringComparison.Ordinal).Trim().ToLowerInvariant()
        };
    }

    private static int BuildSeed(string row, int refreshMinutes)
    {
        var bucket = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60) / Math.Max(1, refreshMinutes);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{bucket}:{row.ToLowerInvariant()}"));
        return BitConverter.ToInt32(bytes, 0);
    }

    private static void Shuffle(List<BaseItemDto> items, int seed)
    {
        var random = new Random(seed);
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }
}

internal static class RatingExtensions
{
    public static bool HasValue(this double value) => value > 0;
}
