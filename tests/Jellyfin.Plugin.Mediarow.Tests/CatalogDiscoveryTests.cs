using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Providers;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class CatalogDiscoveryTests
{
    private const string Manifest = """
        {"catalogs":[
          {"type":"movie","id":"top","name":"Popular","extra":[{"name":"genre","options":["Action","Drama"]},{"name":"skip"}]},
          {"type":"series","id":"top","name":"Popular","genres":["Drama"],"extraSupported":["genre"]},
          {"type":"movie","id":"aio.search","name":"Search","extra":[{"name":"search","isRequired":true}]},
          {"type":"channel","id":"tv","name":"Channels"}
        ]}
        """;

    [Theory]
    [InlineData(null, 0, "https://addon.test/catalog/movie/top.json")]
    [InlineData(null, 100, "https://addon.test/catalog/movie/top/skip=100.json")]
    [InlineData("Science Fiction", 0, "https://addon.test/catalog/movie/top/genre=Sci-Fi.json")]
    [InlineData("Action", 200, "https://addon.test/catalog/movie/top/genre=Action&skip=200.json")]
    public void BuildCatalogUrl_UsesStremioExtraPathForGenreAndSkip(string? genre, int skip, string expected)
    {
        Assert.Equal(expected, StremioCatalogProvider.BuildCatalogUrl("https://addon.test/", "movie", "top", genre, skip));
    }

    [Fact]
    public void ParseCatalogDescriptors_ReadsModernAndLegacyExtrasAndSkipsSearchOnlyCatalogs()
    {
        using var manifest = JsonDocument.Parse(Manifest);
        var catalogs = StremioCatalogProvider.ParseCatalogDescriptors(manifest.RootElement);

        Assert.Equal(2, catalogs.Count);
        var movie = catalogs.Single(catalog => catalog.Type == "movie");
        Assert.Equal(["Action", "Drama"], movie.Genres);
        Assert.True(movie.SupportsGenre);
        Assert.True(movie.SupportsPaging);
        var series = catalogs.Single(catalog => catalog.Type == "series");
        Assert.Equal(["Drama"], series.Genres);
        Assert.True(series.SupportsGenre);
        Assert.False(series.SupportsPaging);
    }

    [Fact]
    public void TmdbDiscoverUrl_UsesGenreIdsSortYearAndVoteFloor()
    {
        var request = new CatalogDiscoveryRequest
        {
            Source = DiscoverySource.Tmdb,
            ContentType = CatalogContentType.Movie,
            Genre = "Science Fiction",
            Sort = DiscoverySort.TopRated,
            Year = 1999,
            MinimumRating = 7
        };
        var url = CatalogDiscoveryService.BuildTmdbDiscoverUrl(request, 3, "key", "de-DE")!;

        Assert.StartsWith("https://api.themoviedb.org/3/discover/movie?", url, StringComparison.Ordinal);
        Assert.Contains("sort_by=vote_average.desc", url, StringComparison.Ordinal);
        Assert.Contains("with_genres=878", url, StringComparison.Ordinal);
        Assert.Contains("primary_release_year=1999", url, StringComparison.Ordinal);
        Assert.Contains("vote_average.gte=7", url, StringComparison.Ordinal);
        Assert.Contains("vote_count.gte=200", url, StringComparison.Ordinal);
        Assert.Contains("page=3", url, StringComparison.Ordinal);
        Assert.Contains("language=de-DE", url, StringComparison.Ordinal);
        Assert.DoesNotContain("query=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void TmdbDiscoverUrl_MapsSeriesGenresAndRefusesGenresTmdbTvDoesNotHave()
    {
        var series = new CatalogDiscoveryRequest { ContentType = CatalogContentType.Series, Genre = "Action", Sort = DiscoverySort.Newest };
        var url = CatalogDiscoveryService.BuildTmdbDiscoverUrl(series, 1, "key", null)!;
        Assert.Contains("/discover/tv?", url, StringComparison.Ordinal);
        Assert.Contains("with_genres=10759", url, StringComparison.Ordinal);
        Assert.Contains("sort_by=first_air_date.desc", url, StringComparison.Ordinal);
        Assert.Contains("first_air_date.lte=", url, StringComparison.Ordinal);

        Assert.Null(CatalogDiscoveryService.BuildTmdbDiscoverUrl(series with { Genre = "Horror" }, 1, "key", null));

        var anime = CatalogDiscoveryService.BuildTmdbDiscoverUrl(series with { Genre = "Anime" }, 1, "key", null)!;
        Assert.Contains("with_genres=16", anime, StringComparison.Ordinal);
        Assert.Contains("with_original_language=ja", anime, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(40, 1, new[] { 40 })]
    [InlineData(40, 2, new[] { 20, 20 })]
    [InlineData(41, 2, new[] { 21, 20 })]
    [InlineData(2, 3, new[] { 1, 1, 0 })]
    public void SplitPageSize_GivesEachTypeAFixedShare(int pageSize, int parts, int[] expected)
    {
        Assert.Equal(expected, CatalogDiscoveryService.SplitPageSize(pageSize, parts));
    }

    [Fact]
    public void CatalogPageResult_ReportsOffsetAndLowerBoundTotal()
    {
        var items = Enumerable.Range(0, 24).Select(_ => new BaseItemDto { Id = Guid.NewGuid() }).ToList();
        var page = new CatalogPageResult(items, 2, 24, hasMore: true);
        Assert.Equal(48, page.StartIndex);
        Assert.Equal(96, page.TotalRecordCount);
        var last = new CatalogPageResult(items.Take(5).ToList(), 3, 24, hasMore: false);
        Assert.Equal(72, last.StartIndex);
        Assert.Equal(77, last.TotalRecordCount);
    }

    [Fact]
    public async Task Pages_TranslateToUpstreamSkipOffsetsWithoutGapsOrRepeats()
    {
        var requests = new List<string>();
        var handler = new Handler(request =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            lock (requests)
            {
                requests.Add(path);
            }

            string body;
            if (path.EndsWith("manifest.json", StringComparison.Ordinal))
            {
                body = Manifest;
            }
            else
            {
                // A 25-title catalog served in pages of 10, like Cinemeta's skip paging.
                var skip = path.Contains("skip=", StringComparison.Ordinal)
                    ? int.Parse(path[(path.IndexOf("skip=", StringComparison.Ordinal) + 5)..^5], System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                var metas = Enumerable.Range(skip + 1, Math.Max(0, Math.Min(10, 25 - skip)))
                    .Select(n => $$"""{"id":"tt{{n:D7}}","type":"movie","name":"T{{n}}"}""");
                body = "{\"metas\":[" + string.Join(',', metas) + "]}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var factory = new ClientFactory(handler);
        var stremio = new TestStremio(factory, memory);
        var itemCache = new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance);
        var host = AioStreamsPlaybackTests.Proxy<IServerApplicationHost>.Create((_, _) => "server");
        var results = new SearchResultFactory(null!, null!, null!, stremio, itemCache, host, null!, NullLogger<SearchResultFactory>.Instance);
        var discovery = new TestDiscovery(stremio, factory, results, memory);
        var selection = new HomeCatalogSelection { Id = "c1", Source = DiscoverySource.Stremio, CatalogId = "top", MediaType = DiscoveryMediaType.Movie };
        var pageRequests = discovery.BuildRequests(selection, null);
        Assert.Single(pageRequests);

        var page0 = await discovery.GetPageAsync(pageRequests, 0, 8, null);
        var page1 = await discovery.GetPageAsync(pageRequests, 1, 8, null);
        var page2 = await discovery.GetPageAsync(pageRequests, 2, 8, null);
        var page3 = await discovery.GetPageAsync(pageRequests, 3, 8, null);

        Assert.Equal(Names(1, 8), page0.Items.Select(item => item.Name));
        Assert.Equal(Names(9, 16), page1.Items.Select(item => item.Name));
        Assert.Equal(Names(17, 24), page2.Items.Select(item => item.Name));
        Assert.Equal(["T25"], page3.Items.Select(item => item.Name));
        Assert.True(page0.HasMore && page1.HasMore && page2.HasMore);
        Assert.False(page3.HasMore);
        Assert.Contains("/catalog/movie/top/skip=10.json", requests);
        Assert.Contains("/catalog/movie/top/skip=20.json", requests);
        // Upstream pages are cached: four Home pages cost one manifest and three catalog requests.
        Assert.Equal(4, requests.Distinct().Count());
        Assert.Equal(requests.Count, requests.Distinct().Count());
    }

    [Fact]
    public void EpisodeSyncPolicy_UsesIntervalAirDatesEndedShowsAndFailureBackoff()
    {
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(EpisodeSyncService.IsDue(null, now, 6, force: false));
        var checkedHourAgo = new EpisodeSyncEntry { LastCheckedUtc = now.AddHours(-1), Status = "Continuing" };
        Assert.False(EpisodeSyncService.IsDue(checkedHourAgo, now, 6, force: false));
        Assert.True(EpisodeSyncService.IsDue(checkedHourAgo, now, 6, force: true));
        Assert.True(EpisodeSyncService.IsDue(new EpisodeSyncEntry { LastCheckedUtc = now.AddHours(-5.5) }, now, 6, false));

        // A known air date that passed since the last check makes the show due right away.
        Assert.True(EpisodeSyncService.IsDue(
            new EpisodeSyncEntry { LastCheckedUtc = now.AddHours(-1), NextAirDateUtc = now.AddMinutes(-10) }, now, 6, false));
        // A future air date defers the check, but never longer than three days.
        Assert.False(EpisodeSyncService.IsDue(
            new EpisodeSyncEntry { LastCheckedUtc = now.AddDays(-2), NextAirDateUtc = now.AddDays(5) }, now, 6, false));
        Assert.True(EpisodeSyncService.IsDue(
            new EpisodeSyncEntry { LastCheckedUtc = now.AddDays(-3), NextAirDateUtc = now.AddDays(5) }, now, 6, false));

        Assert.False(EpisodeSyncService.IsDue(new EpisodeSyncEntry { LastCheckedUtc = now.AddDays(-6), Status = "Ended" }, now, 6, false));
        Assert.True(EpisodeSyncService.IsDue(new EpisodeSyncEntry { LastCheckedUtc = now.AddDays(-7), Status = "Ended" }, now, 6, false));

        Assert.False(EpisodeSyncService.IsDue(new EpisodeSyncEntry { LastCheckedUtc = now.AddHours(-3), ConsecutiveFailures = 2 }, now, 6, false));
        Assert.True(EpisodeSyncService.IsDue(new EpisodeSyncEntry { LastCheckedUtc = now.AddHours(-4), ConsecutiveFailures = 2 }, now, 6, false));
        Assert.False(EpisodeSyncService.IsDue(new EpisodeSyncEntry { LastCheckedUtc = now.AddHours(-47), ConsecutiveFailures = 10 }, now, 6, false));
        Assert.True(EpisodeSyncService.IsDue(new EpisodeSyncEntry { LastCheckedUtc = now.AddHours(-48), ConsecutiveFailures = 10 }, now, 6, false));
    }

    private static IEnumerable<string> Names(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => "T" + n);

    private sealed class TestStremio(IHttpClientFactory factory, IMemoryCache cache)
        : StremioCatalogProvider(factory, cache, NullLogger<StremioCatalogProvider>.Instance)
    {
        protected override PluginConfiguration Config => new() { StremioCatalogUrl = "https://addon.test", CacheTtlMinutes = 30 };
    }

    private sealed class TestDiscovery(StremioCatalogProvider stremio, IHttpClientFactory factory, SearchResultFactory results, IMemoryCache cache)
        : CatalogDiscoveryService(stremio, factory, results, cache, NullLogger<CatalogDiscoveryService>.Instance)
    {
        protected override PluginConfiguration Config => new()
        {
            CatalogProvider = CatalogProvider.StremioAddon,
            StremioCatalogUrl = "https://addon.test",
            HomeRowContentCategories = ["Movies", "Series"]
        };
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
