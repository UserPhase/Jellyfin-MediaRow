using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Providers;

/// <summary>
/// Catalog provider that uses a Stremio addon for catalog and metadata.
/// Supports addons like Cinemeta, AIOMetadata, and other Stremio-compatible addons.
/// </summary>
public class StremioCatalogProvider : ICatalogProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<StremioCatalogProvider> _logger;

    // Cache key prefixes
    private const string SearchCachePrefix = "stremio:catalog:search:";
    private const string MetaCachePrefix = "stremio:meta:";

    public StremioCatalogProvider(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<StremioCatalogProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    protected virtual PluginConfiguration Config =>
        MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    private TimeSpan CacheDuration => TimeSpan.FromMinutes(Math.Max(1, Config.CacheTtlMinutes));

    // Concurrent Home rows share one upstream request per manifest/catalog instead of each fetching it.
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inFlight = new(StringComparer.Ordinal);

    private async Task<T> GetOrFetchAsync<T>(
        string cacheKey,
        Func<Task<(T Value, TimeSpan? CacheFor)>> fetch,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_cache.TryGetValue<T>(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        var shared = _inFlight.GetOrAdd(cacheKey, _ => new Lazy<Task<object>>(async () =>
        {
            try
            {
                var (value, cacheFor) = await fetch().ConfigureAwait(false);
                if (cacheFor.HasValue)
                {
                    _cache.Set(cacheKey, value, cacheFor.Value);
                }

                return value;
            }
            finally
            {
                _inFlight.TryRemove(cacheKey, out var removed);
            }
        }));

        return (T)await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public string ProviderName => "Stremio Addon";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrEmpty(GetBaseUrl());

    private string GetBaseUrl()
    {
        var url = Config.StremioCatalogUrl ?? string.Empty;
        url = url.TrimEnd('/');

        // Strip manifest.json if present (Stremio addon URLs often include this)
        if (url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/manifest.json".Length];
        }
        else if (url.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"manifest.json".Length].TrimEnd('/');
        }

        return url;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogItem>> SearchMoviesAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        return await SearchCatalogAsync("movie", query, maxResults, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogItem>> SearchSeriesAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        return await SearchCatalogAsync("series", query, maxResults, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CatalogItemDetails?> GetMovieDetailsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return await GetMetaAsync("movie", id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CatalogItemDetails?> GetSeriesDetailsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return await GetMetaAsync("series", id, cancellationToken);
    }

    /// <summary>
    /// Gets a browse catalog for virtual Home rows. The provider manifest is used to
    /// select a top/popular catalog without importing any item into Jellyfin.
    /// </summary>
    public async Task<IReadOnlyList<CatalogItem>> GetHomeCatalogAsync(
        CatalogContentType contentType,
        string? genre,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<CatalogItem>();
        }

        try
        {
            var client = CreateClient();
            var type = contentType == CatalogContentType.Movie ? "movie" : "series";
            var manifest = await GetManifestAsync(client, cancellationToken);
            var catalog = manifest.RootElement.TryGetProperty("catalogs", out var catalogs)
                ? catalogs.EnumerateArray()
                    .Where(item => item.TryGetProperty("type", out var itemType) &&
                        itemType.GetString()?.Equals(type, StringComparison.OrdinalIgnoreCase) == true)
                    .OrderByDescending(item =>
                    {
                        var id = item.TryGetProperty("id", out var value) ? value.GetString() : null;
                        var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                        return (id ?? string.Empty).Contains("top", StringComparison.OrdinalIgnoreCase) ||
                            (id ?? string.Empty).Contains("popular", StringComparison.OrdinalIgnoreCase) ||
                            (name ?? string.Empty).Contains("popular", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    })
                    .FirstOrDefault()
                : default;

            if (catalog.ValueKind == JsonValueKind.Undefined ||
                !catalog.TryGetProperty("id", out var catalogIdElement))
            {
                return Array.Empty<CatalogItem>();
            }

            var catalogId = catalogIdElement.GetString();
            if (string.IsNullOrWhiteSpace(catalogId))
            {
                return Array.Empty<CatalogItem>();
            }

            var genrePath = string.IsNullOrWhiteSpace(genre)
                ? string.Empty
                : $"/genre={Uri.EscapeDataString(GetStremioGenreName(genre))}";
            var url = $"{GetBaseUrl()}/catalog/{type}/{Uri.EscapeDataString(catalogId)}{genrePath}.json";
            var catalogItems = await GetOrFetchAsync<IReadOnlyList<CatalogItem>>("stremio:home:" + url, async () =>
            {
                using var response = await client.GetAsync(url, CancellationToken.None).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[StremioCatalog] Home catalog failed with status {StatusCode} for {Type} {Genre}",
                        response.StatusCode, type, genre ?? "Popular");
                    return (Array.Empty<CatalogItem>(), null);
                }

                var result = await response.Content.ReadFromJsonAsync<StremioCatalogResponse>(CancellationToken.None).ConfigureAwait(false);
                IReadOnlyList<CatalogItem> converted = result?.Metas
                    .Select(meta => ConvertMetaPreviewToCatalogItem(meta, type))
                    .Where(item => string.IsNullOrWhiteSpace(genre) || MatchesGenre(item.Genres, genre))
                    .ToList() ?? new List<CatalogItem>();
                return (converted, converted.Count > 0 ? CacheDuration : TimeSpan.FromMinutes(1));
            }, cancellationToken).ConfigureAwait(false);

            var items = catalogItems.Take(maxResults).ToList();

            _logger.LogInformation("[StremioCatalog] Home catalog returned {Count} {Type} items for {Genre}",
                items.Count, type, genre ?? "Popular");
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StremioCatalog] Failed to browse Home catalog for {Type} and {Genre}",
                contentType, genre ?? "Popular");
            return Array.Empty<CatalogItem>();
        }
    }

    private Task<JsonDocument> GetManifestAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var baseUrl = GetBaseUrl();
        return GetOrFetchAsync<JsonDocument>("stremio:manifest:" + baseUrl, async () =>
        {
            using var response = await client.GetAsync($"{baseUrl}/manifest.json", CancellationToken.None).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var manifest = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            return (manifest, CacheDuration);
        }, cancellationToken);
    }

    private static string GetStremioGenreName(string genre)
    {
        return genre.Equals("Science Fiction", StringComparison.OrdinalIgnoreCase)
            ? "Sci-Fi"
            : genre;
    }

    private static bool MatchesGenre(IEnumerable<string> genres, string genre)
    {
        if (genre.Equals("Anime", StringComparison.OrdinalIgnoreCase))
        {
            return genres.Any(value => value.Contains("anime", StringComparison.OrdinalIgnoreCase));
        }

        string expected = NormalizeGenre(genre);
        return genres.Any(value => NormalizeGenre(value) == expected);
    }

    private static string NormalizeGenre(string genre)
    {
        string normalized = genre.Replace("-", " ", StringComparison.Ordinal).Trim();
        return normalized.Equals("sci fi", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("science fiction", StringComparison.OrdinalIgnoreCase)
            ? "science fiction"
            : normalized;
    }

    // ==================== Internal Methods ====================

    private async Task<IReadOnlyList<CatalogItem>> SearchCatalogAsync(
        string type,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[StremioCatalog] Not configured, skipping search");
            return Array.Empty<CatalogItem>();
        }

        // Check cache first
        var cacheKey = $"{SearchCachePrefix}{type}:{query.ToLowerInvariant()}";
        if (_cache.TryGetValue<IReadOnlyList<CatalogItem>>(cacheKey, out var cachedResults) && cachedResults != null)
        {
            _logger.LogDebug("[StremioCatalog] Cache hit for {Type} search: {Query} ({Count} results)",
                type, query, cachedResults.Count);
            return cachedResults.Take(maxResults).ToList();
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();
            var encodedQuery = Uri.EscapeDataString(query);

            // Stremio catalog search format: /catalog/{type}/search/search={query}.json
            // The "search" catalog ID is used by AIOMetadata, Cinemeta, and other Stremio addons
            var url = $"{baseUrl}/catalog/{type}/search/search={encodedQuery}.json";

            _logger.LogDebug("[StremioCatalog] Searching: {Url}", url);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[StremioCatalog] Search failed with status {StatusCode} for {Type}: {Query}",
                    response.StatusCode, type, query);
                return Array.Empty<CatalogItem>();
            }

            var catalogResponse = await response.Content.ReadFromJsonAsync<StremioCatalogResponse>(cancellationToken);

            var results = catalogResponse?.Metas
                .Select(m => ConvertMetaPreviewToCatalogItem(m, type))
                .ToList() ?? new List<CatalogItem>();

            // Cache the results
            _cache.Set(cacheKey, (IReadOnlyList<CatalogItem>)results, CacheDuration);

            _logger.LogDebug("[StremioCatalog] Search returned {Count} results for {Type}: {Query}",
                results.Count, type, query);

            return results.Take(maxResults).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StremioCatalog] Error searching for {Type}: {Query}", type, query);
            return Array.Empty<CatalogItem>();
        }
    }

    private async Task<CatalogItemDetails?> GetMetaAsync(
        string type,
        string id,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[StremioCatalog] Not configured, skipping meta lookup");
            return null;
        }

        // Check cache first
        var cacheKey = $"{MetaCachePrefix}{type}:{id}";
        if (_cache.TryGetValue<CatalogItemDetails>(cacheKey, out var cachedMeta) && cachedMeta != null)
        {
            _logger.LogDebug("[StremioCatalog] Cache hit for {Type} meta: {Id}", type, id);
            return cachedMeta;
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            // Stremio meta format: /meta/{type}/{id}.json
            var url = $"{baseUrl}/meta/{type}/{id}.json";

            _logger.LogDebug("[StremioCatalog] Fetching meta: {Url}", url);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[StremioCatalog] Meta lookup failed with status {StatusCode} for {Type}: {Id}",
                    response.StatusCode, type, id);
                return null;
            }

            var metaResponse = await response.Content.ReadFromJsonAsync<StremioMetaResponse>(cancellationToken);
            if (metaResponse?.Meta == null)
            {
                _logger.LogWarning("[StremioCatalog] Meta response was empty for {Type}: {Id}", type, id);
                return null;
            }

            var details = ConvertMetaToCatalogItemDetails(metaResponse.Meta, type);

            // Cache the result
            _cache.Set(cacheKey, details, CacheDuration);

            _logger.LogDebug("[StremioCatalog] Meta fetched for {Type}: {Id}", type, id);

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StremioCatalog] Error fetching meta for {Type}: {Id}", type, id);
            return null;
        }
    }

    // ==================== Conversion Methods ====================

    private CatalogItem ConvertMetaPreviewToCatalogItem(StremioMetaPreview preview, string type)
    {
        var contentType = type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? CatalogContentType.Movie
            : CatalogContentType.Series;

        return new CatalogItem
        {
            Id = preview.Id,
            Source = CatalogSource.Stremio,
            ImdbId = preview.Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? preview.Id : null,
            Name = preview.Name,
            Overview = preview.Description,
            PosterUrl = preview.Poster,
            BackdropUrl = preview.Background,
            LogoUrl = preview.Logo,
            Year = preview.ParsedYear,
            Rating = preview.ParsedRating,
            Type = contentType,
            Genres = preview.Genres ?? new List<string>()
        };
    }

    private CatalogItemDetails ConvertMetaToCatalogItemDetails(StremioMeta meta, string type)
    {
        var contentType = type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? CatalogContentType.Movie
            : CatalogContentType.Series;

        var details = new CatalogItemDetails
        {
            Id = meta.Id,
            Source = CatalogSource.Stremio,
            ImdbId = meta.ImdbId ?? (meta.Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? meta.Id : null),
            Name = meta.Name,
            Overview = meta.Description,
            PosterUrl = meta.Poster,
            BackdropUrl = meta.Background,
            Year = meta.ParsedYear,
            ReleaseDate = meta.ParsedReleaseDate,
            Rating = meta.ParsedRating,
            Type = contentType,
            RuntimeMinutes = meta.ParsedRuntimeMinutes,
            Status = meta.Status,
            Genres = meta.Genres ?? new List<string>(),
            Directors = meta.Director ?? new List<string>(),
            Writers = meta.Writer ?? new List<string>(),
            LogoUrl = meta.Logo,
            Countries = !string.IsNullOrEmpty(meta.Country)
                ? new List<string> { meta.Country }
                : new List<string>()
        };

        // Add cast (names only from Stremio)
        if (meta.Cast != null)
        {
            details.Cast = meta.Cast
                .Select((name, index) => new CatalogCastMember
                {
                    Name = name,
                    Order = index
                })
                .ToList();
        }

        // Add episodes for series
        if (contentType == CatalogContentType.Series && meta.Videos != null)
        {
            var episodes = meta.Videos
                .OrderBy(v => v.Season)
                .ThenBy(v => v.Episode)
                .Select(v => new CatalogEpisodeInfo
                {
                    Id = v.Id,
                    SeasonNumber = v.Season,
                    EpisodeNumber = v.Episode,
                    Name = v.BestTitle,
                    Overview = v.BestOverview,
                    AirDate = v.ParsedAirDate,
                    ImageUrl = v.Thumbnail
                })
                .ToList();

            details.Episodes = episodes;

            // Build seasons from episodes
            details.Seasons = episodes
                .GroupBy(e => e.SeasonNumber)
                .Select(g => new CatalogSeasonInfo
                {
                    Number = g.Key,
                    EpisodeCount = g.Count()
                })
                .OrderBy(s => s.Number)
                .ToList();
        }

        return details;
    }
}
