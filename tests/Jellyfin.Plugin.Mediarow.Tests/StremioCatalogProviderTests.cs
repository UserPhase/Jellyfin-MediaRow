using System.Net;
using System.Text;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class StremioCatalogProviderTests
{
    private const string Manifest = """{"catalogs":[{"type":"movie","id":"top","name":"Popular"}]}""";
    private const string Catalog = """{"metas":[{"id":"tt0000001","type":"movie","name":"One","genres":["Drama"]},{"id":"tt0000002","type":"movie","name":"Two","genres":["Comedy"]}]}""";

    [Fact]
    public async Task ConcurrentHomeRows_ShareOneManifestAndCatalogRequest()
    {
        var gate = new TaskCompletionSource();
        var requests = new List<string>();
        var handler = new Handler(async request =>
        {
            lock (requests)
            {
                requests.Add(request.RequestUri!.AbsolutePath);
            }

            await gate.Task;
            var body = request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal) ? Manifest : Catalog;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        using MemoryCache cache = new(new MemoryCacheOptions());
        var provider = new TestProvider(new ClientFactory(handler), cache, "https://catalog.test/manifest.json");

        var rows = Enumerable.Range(0, 8)
            .Select(_ => provider.GetHomeCatalogAsync(CatalogContentType.Movie, null, 1))
            .ToList();
        await Task.Delay(50);
        gate.SetResult();
        var results = await Task.WhenAll(rows);

        Assert.All(results, items => Assert.Equal("One", Assert.Single(items).Name));
        Assert.Equal(["/manifest.json", "/catalog/movie/top.json"], requests);

        var larger = await provider.GetHomeCatalogAsync(CatalogContentType.Movie, null, 10);
        Assert.Equal(2, larger.Count);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task HomeCatalog_FailuresAreNotCachedAndCallerCancellationDoesNotAbortSharedFetch()
    {
        var failCatalog = true;
        var requests = 0;
        var dramaGate = new TaskCompletionSource();
        var handler = new Handler(async request =>
        {
            Interlocked.Increment(ref requests);
            var isManifest = request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal);
            if (request.RequestUri.AbsolutePath.Contains("genre=", StringComparison.Ordinal))
            {
                await dramaGate.Task;
            }

            return !isManifest && failCatalog
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(isManifest ? Manifest : Catalog, Encoding.UTF8, "application/json") };
        });
        using MemoryCache cache = new(new MemoryCacheOptions());
        var provider = new TestProvider(new ClientFactory(handler), cache, "https://catalog.test");

        Assert.Empty(await provider.GetHomeCatalogAsync(CatalogContentType.Movie, null, 5));
        failCatalog = false;
        Assert.Equal(2, (await provider.GetHomeCatalogAsync(CatalogContentType.Movie, null, 5)).Count);
        Assert.Equal(3, requests);

        using var cancelled = new CancellationTokenSource();
        var cancelledRow = provider.GetHomeCatalogAsync(CatalogContentType.Movie, "Drama", 5, cancelled.Token);
        cancelled.Cancel();
        Assert.Empty(await cancelledRow);
        var sharedRow = provider.GetHomeCatalogAsync(CatalogContentType.Movie, "Drama", 5);
        dramaGate.SetResult();
        Assert.Equal("One", Assert.Single(await sharedRow).Name);
        Assert.Equal(4, requests);
    }

    private sealed class TestProvider(IHttpClientFactory factory, IMemoryCache cache, string url)
        : StremioCatalogProvider(factory, cache, NullLogger<StremioCatalogProvider>.Instance)
    {
        protected override PluginConfiguration Config => new() { StremioCatalogUrl = url, CacheTtlMinutes = 30 };
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
