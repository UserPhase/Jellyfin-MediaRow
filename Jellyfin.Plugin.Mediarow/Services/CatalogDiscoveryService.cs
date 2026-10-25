using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Providers;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Structured catalog discovery for Home rows and "See All": explicit Stremio catalogs (paged with the
/// <c>skip</c> extra) and TMDB <c>/discover</c> (genre ids, popularity/rating/date sort, year), with
/// offset paging assembled across upstream page sizes.
/// </summary>
public class CatalogDiscoveryService
{
    /// <summary>Upper bound of upstream pages read for one window, so heavy post-filters cannot loop for long.</summary>
    public const int MaxUpstreamPages = 25;

    public const int MaxPageSize = 100;

    private const string TmdbBaseUrl = "https://api.themoviedb.org/3";
    private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/";
    private const int TmdbMaxPage = 500;

    // TMDB genre ids are stable and language independent, so no genre-list request is needed.
    private static readonly IReadOnlyDictionary<string, int> TmdbMovieGenres = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = 28, ["Adventure"] = 12, ["Animation"] = 16, ["Comedy"] = 35, ["Crime"] = 80,
        ["Documentary"] = 99, ["Drama"] = 18, ["Family"] = 10751, ["Fantasy"] = 14, ["History"] = 36,
        ["Horror"] = 27, ["Music"] = 10402, ["Mystery"] = 9648, ["Romance"] = 10749,
        ["Science Fiction"] = 878, ["TV Movie"] = 10770, ["Thriller"] = 53, ["War"] = 10752, ["Western"] = 37
    };

    private static readonly IReadOnlyDictionary<string, int> TmdbSeriesGenres = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Action & Adventure"] = 10759, ["Animation"] = 16, ["Comedy"] = 35, ["Crime"] = 80,
        ["Documentary"] = 99, ["Drama"] = 18, ["Family"] = 10751, ["Kids"] = 10762, ["Mystery"] = 9648,
        ["News"] = 10763, ["Reality"] = 10764, ["Sci-Fi & Fantasy"] = 10765, ["Soap"] = 10766,
        ["Talk"] = 10767, ["War & Politics"] = 10768, ["Western"] = 37
    };

    // Home genre names that TMDB files under a combined TV genre.
    private static readonly IReadOnlyDictionary<string, string> TmdbSeriesGenreAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = "Action & Adventure", ["Adventure"] = "Action & Adventure",
        ["Science Fiction"] = "Sci-Fi & Fantasy", ["Sci-Fi"] = "Sci-Fi & Fantasy", ["Fantasy"] = "Sci-Fi & Fantasy",
        ["War"] = "War & Politics", ["Politics"] = "War & Politics"
    };

    private readonly StremioCatalogProvider _stremio;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CatalogDiscoveryService> _logger;
    private readonly SemaphoreSlim _externalIdGate = new(6, 6);

    public CatalogDiscoveryService(
        StremioCatalogProvider stremio,
        IHttpClientFactory httpClientFactory,
        SearchResultFactory searchResultFactory,
        IMemoryCache cache,
        ILogger<CatalogDiscoveryService> logger)
    {
        _stremio = stremio;
        _httpClientFactory = httpClientFactory;
        _searchResultFactory = searchResultFactory;
        _cache = cache;
        _logger = logger;
    }

    protected virtual PluginConfiguration Config =>
        MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    private bool TmdbConfigured => !string.IsNullOrWhiteSpace(Config.TmdbApiKey);

    private TimeSpan CacheDuration => TimeSpan.FromMinutes(Math.Max(1, Config.CacheTtlMinutes));

    /// <summary>
    /// Lists Stremio manifest catalogs and the structured TMDB discovery catalogs for the admin page.
    /// </summary>
    public async Task<CatalogOptions> GetAvailableCatalogsAsync(CancellationToken cancellationToken = default)
    {
        var catalogs = new List<CatalogDescriptor>(await _stremio.GetCatalogDescriptorsAsync(cancellationToken).ConfigureAwait(false));
        if (TmdbConfigured)
        {
            catalogs.Add(new CatalogDescriptor(DiscoverySource.Tmdb, "discover", "TMDB Discover", "movie",
                TmdbMovieGenres.Keys.Order().ToList(), SupportsPaging: true, SupportsGenre: true));
            catalogs.Add(new CatalogDescriptor(DiscoverySource.Tmdb, "discover", "TMDB Discover", "series",
                TmdbSeriesGenres.Keys.Order().ToList(), SupportsPaging: true, SupportsGenre: true));
        }

        return new CatalogOptions(
            catalogs,
            TmdbMovieGenres.Keys.Append("Anime").Order().ToList(),
            TmdbSeriesGenres.Keys.Append("Anime").Order().ToList(),
            _stremio.IsConfigured,
            TmdbConfigured);
    }

    /// <summary>
    /// Builds one upstream request per content type for an admin catalog row.
    /// </summary>
    public IReadOnlyList<CatalogDiscoveryRequest> BuildRequests(HomeCatalogSelection selection, string? contentCategory)
    {
        var config = Config;
        return GetContentTypes(config, contentCategory)
            .Where(type => selection.MediaType == DiscoveryMediaType.All ||
                (selection.MediaType == DiscoveryMediaType.Movie) == (type == CatalogContentType.Movie))
            .Select(type => new CatalogDiscoveryRequest
            {
                Source = selection.Source,
                ContentType = type,
                CatalogId = string.IsNullOrWhiteSpace(selection.CatalogId) ? null : selection.CatalogId.Trim(),
                Genre = string.IsNullOrWhiteSpace(selection.Genre) ? null : selection.Genre.Trim(),
                Sort = selection.Sort,
                Year = selection.Year is > 0 ? selection.Year : null,
                MinimumRating = Math.Max(selection.MinimumRating, config.HomeRowMinimumRating),
                AnimeOnly = contentCategory == "Anime"
            })
            .ToList();
    }

    /// <summary>
    /// Builds the requests behind the built-in Popular and genre rows.
    /// </summary>
    public IReadOnlyList<CatalogDiscoveryRequest> BuildRequests(string? genre, string? contentCategory, DiscoverySource source = DiscoverySource.Auto)
    {
        var config = Config;
        return GetContentTypes(config, contentCategory)
            .Select(type => new CatalogDiscoveryRequest
            {
                Source = source,
                ContentType = type,
                Genre = string.IsNullOrWhiteSpace(genre) ? null : genre,
                Sort = DiscoverySort.Popular,
                MinimumRating = config.HomeRowMinimumRating,
                AnimeOnly = contentCategory == "Anime"
            })
            .ToList();
    }

    /// <summary>
    /// Returns page <paramref name="pageIndex"/> (offset <c>pageIndex * pageSize</c>) of the combined requests.
    /// With several content types each type gets a fixed share of every page, so consecutive pages never
    /// skip or repeat a title.
    /// </summary>
    public async Task<(IReadOnlyList<BaseItemDto> Items, bool HasMore)> GetPageAsync(
        IReadOnlyList<CatalogDiscoveryRequest> requests,
        int pageIndex,
        int pageSize,
        string? contentCategory,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0 || pageSize <= 0 || pageIndex < 0)
        {
            return (Array.Empty<BaseItemDto>(), false);
        }

        var shares = SplitPageSize(pageSize, requests.Count);
        var windows = await Task.WhenAll(requests.Select((request, index) => shares[index] == 0
                ? Task.FromResult<(IReadOnlyList<BaseItemDto> Items, bool HasMore)>((Array.Empty<BaseItemDto>(), false))
                : ReadWindowAsync(request, pageIndex * shares[index], shares[index], contentCategory, cancellationToken)))
            .ConfigureAwait(false);

        var items = Interleave(windows.Select(window => window.Items).ToList())
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        return (items, windows.Any(window => window.HasMore));
    }

    /// <summary>
    /// First window of the built-in Popular/genre rows from TMDB discovery, replacing keyword searches.
    /// </summary>
    public async Task<IReadOnlyList<BaseItemDto>> DiscoverRowAsync(
        string? genre,
        string? contentCategory,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (!TmdbConfigured)
        {
            return Array.Empty<BaseItemDto>();
        }

        var (items, _) = await GetPageAsync(BuildRequests(genre, contentCategory, DiscoverySource.Tmdb), 0, count, contentCategory, cancellationToken)
            .ConfigureAwait(false);
        return items;
    }

    /// <summary>
    /// Reads <paramref name="count"/> filtered titles starting at <paramref name="offset"/>, fetching consecutive
    /// upstream pages until the window plus one look-ahead title is filled or the catalog ends.
    /// </summary>
    public async Task<(IReadOnlyList<BaseItemDto> Items, bool HasMore)> ReadWindowAsync(
        CatalogDiscoveryRequest request,
        int offset,
        int count,
        string? contentCategory,
        CancellationToken cancellationToken = default)
    {
        var config = Config;
        var needed = offset + count + 1;
        var collected = new List<BaseItemDto>(needed);
        var seen = new HashSet<Guid>();
        for (var upstreamPage = 0; upstreamPage < MaxUpstreamPages && collected.Count < needed; upstreamPage++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await FetchUpstreamPageAsync(request, upstreamPage, cancellationToken).ConfigureAwait(false);
            foreach (var catalogItem in page.Items)
            {
                if (!MatchesRequest(catalogItem, request, page.StrictGenre))
                {
                    continue;
                }

                var dto = await _searchResultFactory.CreateDtoFromCatalogItemAsync(catalogItem, cancellationToken).ConfigureAwait(false);
                if ((contentCategory == null || HomeRowContentFilter.Matches(dto, config, contentCategory)) &&
                    (!request.AnimeOnly || MediarowService.IsAnime(dto)) &&
                    seen.Add(dto.Id))
                {
                    collected.Add(dto);
                }
            }

            if (page.IsLast)
            {
                break;
            }
        }

        var window = collected.Skip(offset).Take(count).ToList();
        return (window, collected.Count > offset + count);
    }

    /// <summary>
    /// Splits a page between content types, e.g. 25 over two types as 13 + 12.
    /// </summary>
    public static int[] SplitPageSize(int pageSize, int parts)
    {
        var shares = new int[parts];
        for (var index = 0; index < parts; index++)
        {
            shares[index] = (pageSize / parts) + (index < pageSize % parts ? 1 : 0);
        }

        return shares;
    }

    /// <summary>
    /// Builds a TMDB discover URL from a structured request.
    /// </summary>
    public static string? BuildTmdbDiscoverUrl(CatalogDiscoveryRequest request, int tmdbPage, string apiKey, string? language)
    {
        var isMovie = request.ContentType == CatalogContentType.Movie;
        var genreIds = new List<int>();
        var anime = request.AnimeOnly || string.Equals(request.Genre, "Anime", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.Genre) && !request.Genre.Equals("Anime", StringComparison.OrdinalIgnoreCase))
        {
            var genreId = GetTmdbGenreId(request.Genre, isMovie);
            if (genreId == null)
            {
                // TMDB has no such genre for this type (e.g. Horror TV); an unfiltered list would be wrong.
                return null;
            }

            genreIds.Add(genreId.Value);
        }

        if (anime && !genreIds.Contains(16))
        {
            genreIds.Add(16);
        }

        var sort = request.Sort switch
        {
            DiscoverySort.TopRated => "vote_average.desc",
            DiscoverySort.Newest => isMovie ? "primary_release_date.desc" : "first_air_date.desc",
            _ => "popularity.desc"
        };
        var query = new List<string>
        {
            "sort_by=" + sort,
            "include_adult=false",
            "page=" + Math.Clamp(tmdbPage, 1, TmdbMaxPage).ToString(CultureInfo.InvariantCulture)
        };
        if (isMovie)
        {
            query.Add("include_video=false");
        }

        if (genreIds.Count > 0)
        {
            // A comma means AND in TMDB discover.
            query.Add("with_genres=" + string.Join("%2C", genreIds.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        }

        if (anime)
        {
            query.Add("with_original_language=ja");
        }

        if (request.Year is > 0)
        {
            query.Add((isMovie ? "primary_release_year=" : "first_air_date_year=") + request.Year.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (request.MinimumRating > 0)
        {
            query.Add("vote_average.gte=" + request.MinimumRating.ToString("0.#", CultureInfo.InvariantCulture));
        }

        if (request.Sort == DiscoverySort.TopRated || request.MinimumRating > 0)
        {
            // Without a vote floor, rating sorts are dominated by titles with a handful of votes.
            query.Add("vote_count.gte=200");
        }

        if (request.Sort == DiscoverySort.Newest)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            query.Add((isMovie ? "primary_release_date.lte=" : "first_air_date.lte=") + today);
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            query.Add("language=" + Uri.EscapeDataString(language));
        }

        query.Add("api_key=" + Uri.EscapeDataString(apiKey));
        return $"{TmdbBaseUrl}/discover/{(isMovie ? "movie" : "tv")}?{string.Join('&', query)}";
    }

    public static int? GetTmdbGenreId(string genre, bool isMovie)
    {
        var name = genre.Trim();
        if (name.Equals("Sci-Fi", StringComparison.OrdinalIgnoreCase))
        {
            name = isMovie ? "Science Fiction" : name;
        }

        if (isMovie)
        {
            return TmdbMovieGenres.TryGetValue(name, out var movieId) ? movieId : null;
        }

        if (TmdbSeriesGenreAliases.TryGetValue(name, out var alias))
        {
            name = alias;
        }

        return TmdbSeriesGenres.TryGetValue(name, out var seriesId) ? seriesId : null;
    }

    private static List<CatalogContentType> GetContentTypes(PluginConfiguration config, string? contentCategory)
    {
        var kinds = HomeRowContentFilter.GetContentTypes(config, contentCategory);
        var types = new List<CatalogContentType>(2);
        if (kinds.Contains(BaseItemKind.Movie))
        {
            types.Add(CatalogContentType.Movie);
        }

        if (kinds.Contains(BaseItemKind.Series))
        {
            types.Add(CatalogContentType.Series);
        }

        return types;
    }

    private static bool MatchesRequest(CatalogItem item, CatalogDiscoveryRequest request, bool strictGenre)
    {
        // TMDB filters genres natively (and files Action under "Action & Adventure" for TV). Stremio results
        // fetched with the genre extra are trusted unless they list other genres; unfiltered lists must match.
        if (!string.IsNullOrWhiteSpace(request.Genre) && item.Source != CatalogSource.Tmdb &&
            (strictGenre || item.Genres.Count > 0) &&
            !StremioCatalogProvider.MatchesGenre(item.Genres, request.Genre))
        {
            return false;
        }

        if (request.Year is > 0 && item.Year != request.Year)
        {
            return false;
        }

        return request.MinimumRating <= 0 || (item.Rating ?? 0) >= request.MinimumRating;
    }

    private static IEnumerable<BaseItemDto> Interleave(IReadOnlyList<IReadOnlyList<BaseItemDto>> lists)
    {
        var longest = lists.Count == 0 ? 0 : lists.Max(list => list.Count);
        for (var index = 0; index < longest; index++)
        {
            foreach (var list in lists)
            {
                if (index < list.Count)
                {
                    yield return list[index];
                }
            }
        }
    }

    private DiscoverySource ResolveSource(CatalogDiscoveryRequest request)
    {
        if (request.Source != DiscoverySource.Auto)
        {
            return request.Source;
        }

        return Config.CatalogProvider == CatalogProvider.StremioAddon && _stremio.IsConfigured
            ? DiscoverySource.Stremio
            : DiscoverySource.Tmdb;
    }

    private async Task<UpstreamPage> FetchUpstreamPageAsync(CatalogDiscoveryRequest request, int upstreamPage, CancellationToken cancellationToken)
    {
        if (ResolveSource(request) == DiscoverySource.Stremio)
        {
            var page = await FetchStremioPageAsync(request, upstreamPage, cancellationToken).ConfigureAwait(false);
            // Auto rows fall back to TMDB when the addon has no catalog for this type.
            if (page.Items.Count == 0 && upstreamPage == 0 && request.Source == DiscoverySource.Auto && TmdbConfigured)
            {
                return await FetchTmdbPageAsync(request, upstreamPage, cancellationToken).ConfigureAwait(false);
            }

            return page;
        }

        return await FetchTmdbPageAsync(request, upstreamPage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpstreamPage> FetchStremioPageAsync(CatalogDiscoveryRequest request, int upstreamPage, CancellationToken cancellationToken)
    {
        var type = request.ContentType == CatalogContentType.Movie ? "movie" : "series";
        var catalogs = await _stremio.GetCatalogDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        var catalogId = request.CatalogId ?? await _stremio.GetDefaultCatalogIdAsync(type, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            return UpstreamPage.End;
        }

        var descriptor = catalogs.FirstOrDefault(catalog =>
            catalog.CatalogId.Equals(catalogId, StringComparison.Ordinal) &&
            catalog.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (request.CatalogId != null && descriptor == null)
        {
            // An explicit catalog for the other type (e.g. a movie-only catalog on a mixed row).
            return UpstreamPage.End;
        }

        var genre = request.Genre;
        var upstreamGenre = genre != null && !genre.Equals("Anime", StringComparison.OrdinalIgnoreCase) &&
            descriptor?.SupportsGenre != false
            ? genre
            : null;
        var strictGenre = genre != null && upstreamGenre == null;
        var firstPage = await _stremio.GetCatalogItemsAsync(type, catalogId, upstreamGenre, 0, cancellationToken).ConfigureAwait(false);
        if (firstPage == null || firstPage.Count == 0)
        {
            return UpstreamPage.End;
        }

        var canPage = descriptor?.SupportsPaging != false;
        if (upstreamPage == 0)
        {
            return new UpstreamPage(firstPage, !canPage, strictGenre);
        }

        if (!canPage)
        {
            return UpstreamPage.End;
        }

        // Addons page in their own size (Cinemeta: 100); skip must be a multiple of it.
        var naturalSize = firstPage.Count;
        var items = await _stremio.GetCatalogItemsAsync(type, catalogId, upstreamGenre, upstreamPage * naturalSize, cancellationToken)
            .ConfigureAwait(false);
        if (items == null || items.Count == 0 || items[0].Id == firstPage[0].Id)
        {
            // Empty, failed, or the addon ignored skip and returned page one again.
            return UpstreamPage.End;
        }

        return new UpstreamPage(items, items.Count < naturalSize, strictGenre);
    }

    private async Task<UpstreamPage> FetchTmdbPageAsync(CatalogDiscoveryRequest request, int upstreamPage, CancellationToken cancellationToken)
    {
        if (!TmdbConfigured || upstreamPage + 1 > TmdbMaxPage)
        {
            return UpstreamPage.End;
        }

        var language = Config.GetTmdbLanguageCode();
        var url = BuildTmdbDiscoverUrl(request, upstreamPage + 1, Config.TmdbApiKey, language);
        if (url == null)
        {
            return UpstreamPage.End;
        }

        var cacheKey = $"discovery:tmdb:{request.CacheKey}:{language}:{upstreamPage}";
        if (_cache.TryGetValue<UpstreamPage>(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Discovery] TMDB discover returned {StatusCode} for {Type}", response.StatusCode, request.ContentType);
                return UpstreamPage.End;
            }

            var body = await response.Content.ReadFromJsonAsync<TmdbDiscoverResponse>(cancellationToken).ConfigureAwait(false);
            if (body == null)
            {
                return UpstreamPage.End;
            }

            var items = new List<CatalogItem>(body.Results.Count);
            if (request.ContentType == CatalogContentType.Movie)
            {
                items.AddRange(body.Results.Select(result => ToCatalogItem(result, CatalogContentType.Movie, null)));
            }
            else
            {
                // AIOStreams and series pages need IMDb/TVDB ids, which discover results lack.
                var externalIds = await Task.WhenAll(body.Results.Select(result => GetSeriesExternalIdsAsync(client, result.Id, cancellationToken)))
                    .ConfigureAwait(false);
                items.AddRange(body.Results
                    .Select((result, index) => (Result: result, Ids: externalIds[index]))
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Ids?.ImdbId) || entry.Ids?.TvdbId is > 0)
                    .Select(entry => ToCatalogItem(entry.Result, CatalogContentType.Series, entry.Ids)));
            }

            var page = new UpstreamPage(items, body.Page >= Math.Min(body.TotalPages, TmdbMaxPage) || body.Results.Count == 0, false);
            _cache.Set(cacheKey, page, CacheDuration);
            return page;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("[Discovery] TMDB discover failed for {Type} ({ErrorType})", request.ContentType, ex.GetType().Name);
            return UpstreamPage.End;
        }
    }

    private async Task<TmdbExternalIds?> GetSeriesExternalIdsAsync(HttpClient client, int tmdbId, CancellationToken cancellationToken)
    {
        var cacheKey = $"discovery:tmdb:tv-external:{tmdbId}";
        if (_cache.TryGetValue<TmdbExternalIds>(cacheKey, out var cached))
        {
            return cached;
        }

        await _externalIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var url = $"{TmdbBaseUrl}/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}/external_ids?api_key={Uri.EscapeDataString(Config.TmdbApiKey)}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var ids = await response.Content.ReadFromJsonAsync<TmdbExternalIds>(cancellationToken).ConfigureAwait(false);
            // Ids never change; keep them for a week.
            _cache.Set(cacheKey, ids, TimeSpan.FromDays(7));
            return ids;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _externalIdGate.Release();
        }
    }

    private static CatalogItem ToCatalogItem(TmdbDiscoverResult result, CatalogContentType type, TmdbExternalIds? ids)
    {
        var genreTable = type == CatalogContentType.Movie ? TmdbMovieGenres : TmdbSeriesGenres;
        var genres = result.GenreIds
            .Select(id => genreTable.FirstOrDefault(pair => pair.Value == id).Key)
            .Where(name => name != null)
            .Select(name => name!)
            .ToList();
        if (result.OriginalLanguage == "ja" && result.GenreIds.Contains(16))
        {
            genres.Add("Anime");
        }

        var date = type == CatalogContentType.Movie ? result.ReleaseDate : result.FirstAirDate;
        DateTime? releaseDate = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
        return new CatalogItem
        {
            Id = result.Id.ToString(CultureInfo.InvariantCulture),
            Source = CatalogSource.Tmdb,
            TmdbId = result.Id.ToString(CultureInfo.InvariantCulture),
            ImdbId = string.IsNullOrWhiteSpace(ids?.ImdbId) ? null : ids!.ImdbId,
            TvdbId = ids?.TvdbId is > 0 ? ids.TvdbId.Value.ToString(CultureInfo.InvariantCulture) : null,
            Name = (type == CatalogContentType.Movie ? result.Title : result.Name) ?? string.Empty,
            OriginalName = type == CatalogContentType.Movie ? result.OriginalTitle : result.OriginalName,
            Overview = result.Overview,
            PosterUrl = string.IsNullOrWhiteSpace(result.PosterPath) ? null : TmdbImageBaseUrl + "w500" + result.PosterPath,
            BackdropUrl = string.IsNullOrWhiteSpace(result.BackdropPath) ? null : TmdbImageBaseUrl + "w1280" + result.BackdropPath,
            Year = releaseDate?.Year,
            ReleaseDate = releaseDate,
            Rating = result.VoteAverage > 0 ? Math.Round(result.VoteAverage, 1) : null,
            Type = type,
            Genres = genres,
            OriginalLanguage = result.OriginalLanguage
        };
    }

    private sealed record UpstreamPage(IReadOnlyList<CatalogItem> Items, bool IsLast, bool StrictGenre)
    {
        public static UpstreamPage End { get; } = new(Array.Empty<CatalogItem>(), true, false);
    }

    private sealed class TmdbDiscoverResponse
    {
        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("total_pages")]
        public int TotalPages { get; set; }

        [JsonPropertyName("results")]
        public List<TmdbDiscoverResult> Results { get; set; } = new();
    }

    private sealed class TmdbDiscoverResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("backdrop_path")]
        public string? BackdropPath { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("genre_ids")]
        public List<int> GenreIds { get; set; } = new();

        [JsonPropertyName("original_language")]
        public string? OriginalLanguage { get; set; }
    }

    private sealed class TmdbExternalIds
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }
    }
}
