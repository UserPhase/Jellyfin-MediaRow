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
            var type = contentType == CatalogContentType.Movie ? "movie" : "series";
            var catalogId = await GetDefaultCatalogIdAsync(type, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(catalogId))
            {
                return Array.Empty<CatalogItem>();
            }

            var catalogItems = await GetCatalogItemsAsync(type, catalogId, genre, 0, cancellationToken).ConfigureAwait(false)
                ?? Array.Empty<CatalogItem>();
            var items = catalogItems
                .Where(item => string.IsNullOrWhiteSpace(genre) || MatchesGenre(item.Genres, genre))
                .Take(maxResults)
                .ToList();

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

    /// <summary>
    /// Builds a Stremio catalog URL. Extras are path segments joined with '&amp;', e.g.
    /// <c>/catalog/movie/top/genre=Sci-Fi&amp;skip=100.json</c>.
    /// </summary>
    public static string BuildCatalogUrl(string baseUrl, string type, string catalogId, string? genre, int skip)
    {
        var extras = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(genre))
        {
            extras.Add("genre=" + Uri.EscapeDataString(GetStremioGenreName(genre)));
        }

        if (skip > 0)
        {
            extras.Add("skip=" + skip.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var extraPath = extras.Count == 0 ? string.Empty : "/" + string.Join('&', extras);
        return $"{baseUrl.TrimEnd('/')}/catalog/{type}/{Uri.EscapeDataString(catalogId)}{extraPath}.json";
    }

    /// <summary>
    /// Fetches one upstream page of a catalog (cached, one shared request per URL).
    /// Returns null when the addon is unreachable or rejects the request.
    /// </summary>
    public async Task<IReadOnlyList<CatalogItem>?> GetCatalogItemsAsync(
        string type,
        string catalogId,
        string? genre,
        int skip,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var url = BuildCatalogUrl(GetBaseUrl(), type, catalogId, genre, skip);
        var client = CreateClient();
        var result = await GetOrFetchAsync<CatalogFetchResult>("stremio:home:" + url, async () =>
        {
            try
            {
                using var response = await client.GetAsync(url, CancellationToken.None).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[StremioCatalog] Catalog {CatalogId} returned {StatusCode} for {Type} {Genre} skip {Skip}",
                        catalogId, response.StatusCode, type, genre ?? "-", skip);
                    return (new CatalogFetchResult(null), null);
                }

                var body = await response.Content.ReadFromJsonAsync<StremioCatalogResponse>(CancellationToken.None).ConfigureAwait(false);
                IReadOnlyList<CatalogItem> converted = body?.Metas
                    .Select(meta => ConvertMetaPreviewToCatalogItem(meta, type))
                    .ToList() ?? new List<CatalogItem>();
                return (new CatalogFetchResult(converted), converted.Count > 0 ? CacheDuration : TimeSpan.FromMinutes(1));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning("[StremioCatalog] Catalog {CatalogId} request failed ({ErrorType})", catalogId, ex.GetType().Name);
                return (new CatalogFetchResult(null), null);
            }
        }, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    /// <summary>
    /// Picks the addon's top/popular catalog for a type, used when a row names no explicit catalog.
    /// </summary>
    public async Task<string?> GetDefaultCatalogIdAsync(string type, CancellationToken cancellationToken = default)
    {
        var catalogs = await GetCatalogDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        return catalogs
            .Where(catalog => catalog.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(catalog =>
                catalog.CatalogId.Contains("top", StringComparison.OrdinalIgnoreCase) ||
                catalog.CatalogId.Contains("popular", StringComparison.OrdinalIgnoreCase) ||
                catalog.Name.Contains("popular", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(catalog => catalog.CatalogId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Lists browseable movie/series catalogs declared by the addon manifest, with their genre options
    /// and whether they accept the <c>skip</c> extra used for paging.
    /// </summary>
    public async Task<IReadOnlyList<CatalogDescriptor>> GetCatalogDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<CatalogDescriptor>();
        }

        try
        {
            var manifest = await GetManifestAsync(CreateClient(), cancellationToken).ConfigureAwait(false);
            return ParseCatalogDescriptors(manifest.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[StremioCatalog] Could not read the addon manifest ({ErrorType})", ex.GetType().Name);
            return Array.Empty<CatalogDescriptor>();
        }
    }

    /// <summary>
    /// Parses manifest catalogs. Supports both the current <c>extra</c> array and the legacy
    /// <c>genres</c>/<c>extraSupported</c>/<c>extraRequired</c> fields.
    /// </summary>
    public static IReadOnlyList<CatalogDescriptor> ParseCatalogDescriptors(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("catalogs", out var catalogs) || catalogs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CatalogDescriptor>();
        }

        var result = new List<CatalogDescriptor>();
        foreach (var catalog in catalogs.EnumerateArray())
        {
            var type = GetString(catalog, "type");
            var id = GetString(catalog, "id");
            if (string.IsNullOrWhiteSpace(id) ||
                !(type?.Equals("movie", StringComparison.OrdinalIgnoreCase) == true ||
                  type?.Equals("series", StringComparison.OrdinalIgnoreCase) == true))
            {
                continue;
            }

            var genres = new List<string>();
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var declaresExtras = false;
            if (catalog.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
            {
                declaresExtras = true;
                foreach (var option in extra.EnumerateArray())
                {
                    var name = GetString(option, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    supported.Add(name);
                    if (option.TryGetProperty("isRequired", out var isRequired) && isRequired.ValueKind == JsonValueKind.True)
                    {
                        required.Add(name);
                    }

                    if (name.Equals("genre", StringComparison.OrdinalIgnoreCase) &&
                        option.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                    {
                        genres.AddRange(options.EnumerateArray()
                            .Where(value => value.ValueKind == JsonValueKind.String)
                            .Select(value => value.GetString()!)
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                    }
                }
            }

            foreach (var (property, target) in new[] { ("extraSupported", supported), ("extraRequired", required) })
            {
                if (catalog.TryGetProperty(property, out var names) && names.ValueKind == JsonValueKind.Array)
                {
                    declaresExtras = true;
                    foreach (var name in names.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String))
                    {
                        target.Add(name.GetString()!);
                    }
                }
            }

            if (genres.Count == 0 && catalog.TryGetProperty("genres", out var legacyGenres) && legacyGenres.ValueKind == JsonValueKind.Array)
            {
                genres.AddRange(legacyGenres.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!));
            }

            // Search-only catalogs (or ones needing another required extra) cannot be browsed as a row.
            if (required.Any(name => !name.Equals("genre", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new CatalogDescriptor(
                DiscoverySource.Stremio,
                id,
                GetString(catalog, "name") ?? id,
                type!.ToLowerInvariant(),
                genres.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                // Addons that declare nothing are assumed to page; the reader stops if skip is ignored.
                SupportsPaging: !declaresExtras || supported.Contains("skip"),
                SupportsGenre: genres.Count > 0 || supported.Contains("genre")));
        }

        return result;
    }

    /// <summary>
    /// Drops the cached metadata of one title so the next lookup sees newly announced episodes.
    /// </summary>
    public void InvalidateMeta(string type, string id)
    {
        _cache.Remove($"{MetaCachePrefix}{type}:{id}");
    }

    private sealed record CatalogFetchResult(IReadOnlyList<CatalogItem>? Items);

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

    public static bool MatchesGenre(IEnumerable<string> genres, string genre)
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

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();
            var catalogId = await GetSearchCatalogIdAsync(client, type, cancellationToken).ConfigureAwait(false);
            var url = $"{baseUrl}/catalog/{type}/{Uri.EscapeDataString(catalogId)}/search={Uri.EscapeDataString(query)}.json";
            var cacheKey = $"{SearchCachePrefix}{url.ToLowerInvariant()}";

            // A shared uncancelled fetch lets a slow search finish and warm the cache after its caller gives up.
            var results = await GetOrFetchAsync<IReadOnlyList<CatalogItem>>(cacheKey, async () =>
            {
                _logger.LogDebug("[StremioCatalog] Searching: {Url}", url);
                using var response = await client.GetAsync(url, CancellationToken.None).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[StremioCatalog] Search failed with status {StatusCode} for {Type}: {Query}",
                        response.StatusCode, type, query);
                    return (Array.Empty<CatalogItem>(), null);
                }

                var catalogResponse = await response.Content
                    .ReadFromJsonAsync<StremioCatalogResponse>(CancellationToken.None)
                    .ConfigureAwait(false);
                IReadOnlyList<CatalogItem> converted = catalogResponse?.Metas
                    .Select(m => ConvertMetaPreviewToCatalogItem(m, type))
                    .ToList() ?? new List<CatalogItem>();
                return (converted, CacheDuration);
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("[StremioCatalog] Search returned {Count} results for {Type}: {Query}",
                results.Count, type, query);
            return results.Take(maxResults).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<CatalogItem>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StremioCatalog] Error searching for {Type}: {Query}", type, query);
            return Array.Empty<CatalogItem>();
        }
    }

    private async Task<string> GetSearchCatalogIdAsync(HttpClient client, string type, CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await GetManifestAsync(client, cancellationToken).ConfigureAwait(false);
            if (manifest.RootElement.TryGetProperty("catalogs", out var catalogs) &&
                catalogs.ValueKind == JsonValueKind.Array)
            {
                var id = catalogs.EnumerateArray()
                    .Where(catalog => GetString(catalog, "type")?.Equals(type, StringComparison.OrdinalIgnoreCase) == true &&
                        IsSearchCapable(catalog))
                    .Select(catalog => GetString(catalog, "id"))
                    .Where(catalogId => !string.IsNullOrWhiteSpace(catalogId))
                    .OrderBy(catalogId => catalogId!.Contains("people", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (id != null)
                {
                    return id;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StremioCatalog] Could not read search catalogs from the manifest");
        }

        return "search";
    }

    private static bool IsSearchCapable(JsonElement catalog)
    {
        if (catalog.TryGetProperty("extra", out var extras) && extras.ValueKind == JsonValueKind.Array &&
            extras.EnumerateArray().Any(extra =>
                GetString(extra, "name")?.Equals("search", StringComparison.OrdinalIgnoreCase) == true))
        {
            return true;
        }

        return catalog.TryGetProperty("extraSupported", out var supported) &&
            supported.ValueKind == JsonValueKind.Array &&
            supported.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String &&
                value.GetString()?.Equals("search", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
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
