using System.Net;
using System.Text;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class SkipSegmentTests
{
    private static readonly long Runtime = TimeSpan.FromMinutes(42).Ticks;

    [Fact]
    public async Task MergesSourcesAndPrefersTheIntroDbPerType()
    {
        var urls = new List<string>();
        var service = Service(request =>
        {
            var url = request.RequestUri!.ToString();
            urls.Add(url);
            return url.StartsWith(SkipSegmentService.TheIntroDbUrl, StringComparison.Ordinal)
                ? """{"intro":[{"start_ms":60000,"end_ms":90000}],"credits":[]}"""
                : """{"intro":{"start_ms":1000,"end_ms":2000},"recap":{"start_ms":0,"end_ms":30000},"outro":{"start_ms":2400000,"end_ms":2450000}}""";
        });

        var result = await service.GetSegmentsAsync(new SkipSegmentRequest("tt1", "42", null, null, 2, 5, Runtime), CancellationToken.None);

        Assert.Equal(2, urls.Count);
        Assert.Contains(urls, url => url.Contains("tmdb_id=42&season=2&episode=5", StringComparison.Ordinal));
        Assert.Contains(urls, url => url.Contains("imdb_id=tt1&season=2&episode=5", StringComparison.Ordinal));
        Assert.Collection(
            result,
            recap => Assert.Equal(new SkipSegment(MediaSegmentType.Recap, 0, TimeSpan.FromSeconds(30).Ticks), recap),
            intro => Assert.Equal(new SkipSegment(MediaSegmentType.Intro, TimeSpan.FromSeconds(60).Ticks, TimeSpan.FromSeconds(90).Ticks), intro),
            outro => Assert.Equal(new SkipSegment(MediaSegmentType.Outro, TimeSpan.FromSeconds(2400).Ticks, TimeSpan.FromSeconds(2450).Ticks), outro));
    }

    [Fact]
    public async Task CreditsThatRunToTheEndExtendPastTheEndForTheNextEpisodePrompt()
    {
        var service = Service(_ => """{"credits":[{"start_ms":2460000,"end_ms":null}]}""");

        var result = await service.GetSegmentsAsync(new SkipSegmentRequest(null, "42", null, null, 1, 1, Runtime), CancellationToken.None);

        var outro = Assert.Single(result);
        Assert.Equal(MediaSegmentType.Outro, outro.Type);
        Assert.Equal(TimeSpan.FromSeconds(2460).Ticks, outro.StartTicks);
        Assert.True(outro.EndTicks > Runtime);
    }

    [Fact]
    public async Task AniSkipUsesTheReleaseClosestToTheEpisodeLength()
    {
        var urls = new List<string>();
        var service = Service(request =>
        {
            urls.Add(request.RequestUri!.ToString());
            return """
                {"found":true,"results":[
                  {"skipType":"op","interval":{"startTime":10,"endTime":100},"episodeLength":1300},
                  {"skipType":"op","interval":{"startTime":20,"endTime":110},"episodeLength":1420},
                  {"skipType":"ed","interval":{"startTime":1330,"endTime":1420},"episodeLength":1420}
                ]}
                """;
        });

        var result = await service.GetSegmentsAsync(
            new SkipSegmentRequest(null, null, null, "5114", 1, 3, TimeSpan.FromSeconds(1421).Ticks), CancellationToken.None);

        Assert.Equal($"{SkipSegmentService.AniSkipUrl}5114/3?types=op&types=ed&types=recap&episodeLength=0", Assert.Single(urls));
        Assert.Equal(new SkipSegment(MediaSegmentType.Intro, TimeSpan.FromSeconds(20).Ticks, TimeSpan.FromSeconds(110).Ticks), result[0]);
        Assert.Equal(MediaSegmentType.Outro, result[1].Type);
        Assert.True(result[1].EndTicks > TimeSpan.FromSeconds(1421).Ticks);
    }

    [Fact]
    public async Task FailedSourcesAndInvalidTimesAreIgnoredAndCached()
    {
        var calls = 0;
        var service = Service(request =>
        {
            calls++;
            if (request.RequestUri!.ToString().StartsWith(SkipSegmentService.IntroDbUrl, StringComparison.Ordinal))
            {
                throw new HttpRequestException("offline");
            }

            return """{"intro":[{"start_ms":90000,"end_ms":30000}],"recap":[{"start_ms":3000000,"end_ms":3100000}]}""";
        });
        var request = new SkipSegmentRequest("tt1", "42", null, null, 1, 1, Runtime);

        Assert.Empty(await service.GetSegmentsAsync(request, CancellationToken.None));
        Assert.Empty(await service.GetSegmentsAsync(request, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EpisodesWithoutIdsOrSeasonAreNotLookedUp()
    {
        var calls = 0;
        var service = Service(_ =>
        {
            calls++;
            return "{}";
        });

        Assert.Empty(await service.GetSegmentsAsync(new SkipSegmentRequest("tt1", "42", null, null, null, 1, Runtime), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private static SkipSegmentService Service(Func<HttpRequestMessage, string> respond) => new(
        new ClientFactory(new Handler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
        }))),
        new MemoryCache(new MemoryCacheOptions()),
        null,
        NullLogger<SkipSegmentService>.Instance);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
