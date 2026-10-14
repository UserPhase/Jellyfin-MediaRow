using System.Net;
using System.Text;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class AioStreamsClientTests
{
    [Fact]
    public async Task ConcurrentLookups_ShareOneRequest_AndSurviveCallerCancellation()
    {
        var handler = new SlowStreamsHandler();
        var client = new TestClient(new HandlerFactory(handler), new MemoryCache(new MemoryCacheOptions()));

        using var cancelled = new CancellationTokenSource();
        var prefetch = client.GetMovieStreamsAsync("tt0816692", cancelled.Token);
        var details = client.GetMovieStreamsAsync("tt0816692");
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch);

        handler.Release.SetResult();
        var response = await details;
        var cached = await client.GetMovieStreamsAsync("tt0816692");

        Assert.Equal(1, handler.Calls);
        Assert.Single(response!.Streams!);
        Assert.Single(cached!.Streams!);
    }

    private sealed class TestClient(IHttpClientFactory factory, IMemoryCache cache)
        : AIOStreamsClient(factory, cache, NullLogger<AIOStreamsClient>.Instance)
    {
        protected override PluginConfiguration Config { get; } = new() { AIOStreamsUrl = "http://aio.test" };
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SlowStreamsHandler : HttpMessageHandler
    {
        private int _calls;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"streams\":[{\"name\":\"1080p\",\"url\":\"https://cdn.test/a.mkv\"}]}", Encoding.UTF8, "application/json")
            };
        }
    }
}
