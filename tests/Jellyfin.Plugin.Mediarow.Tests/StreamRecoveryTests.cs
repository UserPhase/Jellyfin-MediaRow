using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public class StreamRecoveryTests
{
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly StreamTitle Movie = new("tt0111161");

    private static AIOStream File(string url, string filename, string service = "RD+", long size = 1000) => new()
    {
        Url = url,
        Name = $"[{service}] AIOStreams 1080p",
        BehaviorHints = new AIOBehaviorHints { Filename = filename, VideoSize = size }
    };

    // ---------- Candidate identity ----------

    [Fact]
    public void SourceIds_SurviveRefreshedUrlsButDistinguishDifferentFiles()
    {
        var before = StreamResolverService.BuildCandidates(ItemId, [File("https://rd.test/dl/AAA/movie.mkv", "Movie.2019.mkv")], []);
        var after = StreamResolverService.BuildCandidates(ItemId, [File("https://rd.test/dl/BBB/movie.mkv", "Movie.2019.mkv")], []);
        Assert.Equal(before[0].SourceId, after[0].SourceId);
        Assert.NotEqual(before[0].Url, after[0].Url);

        var otherService = StreamResolverService.BuildCandidates(ItemId, [File("https://tb.test/x", "Movie.2019.mkv", "TB+")], []);
        Assert.NotEqual(before[0].SourceId, otherService[0].SourceId);

        // Without a filename the URL is the only safe identity.
        var anonymous = StreamResolverService.BuildCandidates(ItemId,
            [new AIOStream { Url = "https://http.test/a.mp4" }, new AIOStream { Url = "https://http.test/b.mp4" }], []);
        Assert.Equal(2, anonymous.Count);
        Assert.NotEqual(anonymous[0].SourceId, anonymous[1].SourceId);
    }

    // ---------- Open state machine ----------

    [Fact]
    public async Task ExpiredPrimary_IsRefreshedAndRetriedOnTheSameFile()
    {
        var addon = new FakeAddon(generation => [File($"https://rd.test/dl/token{generation}/movie.mkv", "Movie.mkv"), File("https://rd.test/other.mkv", "Other.mkv")]);
        var probe = new FakeHealthProbe(candidate => candidate.Url.Contains("token0", StringComparison.Ordinal)
            ? new StreamHealthResult(StreamHealth.Expired, 403, "HTTP 403")
            : StreamHealthResult.Ok(206));
        var resolver = new StreamResolverService(addon, probe, NullLogger<StreamResolverService>.Instance);
        var primary = (await resolver.ResolveAsync(ItemId, Movie, [], false, default)).Candidates[0];
        var opened = new List<string>();

        var result = await resolver.OpenAsync(ItemId, Movie, [], primary.SourceId, (candidate, _) =>
        {
            opened.Add(candidate.Url);
            return candidate.Url.Contains("token0", StringComparison.Ordinal)
                ? Task.FromException<string>(new HttpRequestException("probe failed"))
                : Task.FromResult("playing " + candidate.Url);
        }, default);

        Assert.Equal(["https://rd.test/dl/token0/movie.mkv", "https://rd.test/dl/token1/movie.mkv"], opened);
        Assert.Equal(primary.SourceId, result.Candidate.SourceId);
        Assert.False(result.UsedFallback(primary.SourceId));
        Assert.True(result.Refreshed);
        Assert.Empty(result.FailedAttempts);
        Assert.Equal(1, addon.Invalidations);
    }

    [Fact]
    public async Task BrokenPrimary_FallsBackToTheNextCandidate()
    {
        var addon = new FakeAddon(_ => [File("https://a.test/broken.mkv", "Broken.mkv"), File("https://b.test/good.mkv", "Good.mkv")]);
        var probe = new FakeHealthProbe(candidate => candidate.Url.Contains("broken", StringComparison.Ordinal)
            ? new StreamHealthResult(StreamHealth.Unplayable, 200, "Returned a web page instead of media")
            : StreamHealthResult.Ok(206));
        var resolver = new StreamResolverService(addon, probe, NullLogger<StreamResolverService>.Instance);
        var candidates = (await resolver.ResolveAsync(ItemId, Movie, [], false, default)).Candidates;

        var result = await resolver.OpenAsync(ItemId, Movie, [], candidates[0].SourceId, (candidate, _) =>
            candidate.Url.Contains("broken", StringComparison.Ordinal)
                ? Task.FromException<int>(new InvalidOperationException("not a video"))
                : Task.FromResult(1), default);

        Assert.Equal(candidates[1].SourceId, result.Candidate.SourceId);
        Assert.True(result.UsedFallback(candidates[0].SourceId));
        var attempt = Assert.Single(result.FailedAttempts);
        Assert.Equal(candidates[0].SourceId, attempt.SourceId);
        Assert.Equal(0, addon.Invalidations);
    }

    [Fact]
    public async Task TransientFailure_RetriesTheSameCandidateOnce()
    {
        var addon = new FakeAddon(_ => [File("https://a.test/flaky.mkv", "Flaky.mkv"), File("https://b.test/next.mkv", "Next.mkv")]);
        var probe = new FakeHealthProbe(_ => new StreamHealthResult(StreamHealth.Transient, 503, "HTTP 503"));
        var resolver = new StreamResolverService(addon, probe, NullLogger<StreamResolverService>.Instance);
        var candidates = (await resolver.ResolveAsync(ItemId, Movie, [], false, default)).Candidates;
        var calls = 0;

        var result = await resolver.OpenAsync(ItemId, Movie, [], candidates[0].SourceId, (_, _) =>
            ++calls == 1 ? Task.FromException<int>(new HttpRequestException("timeout")) : Task.FromResult(calls), default);

        Assert.Equal(2, calls);
        Assert.Equal(candidates[0].SourceId, result.Candidate.SourceId);
    }

    [Fact]
    public async Task EverythingFailing_StopsAfterTheCandidateLimitWithASummary()
    {
        var addon = new FakeAddon(_ => Enumerable.Range(0, 6).Select(index => File($"https://x.test/{index}.mkv", $"File{index}.mkv")).ToList());
        var probe = new FakeHealthProbe(_ => new StreamHealthResult(StreamHealth.Unplayable, 404, "HTTP 404"));
        var resolver = new StreamResolverService(addon, probe, NullLogger<StreamResolverService>.Instance);
        var candidates = (await resolver.ResolveAsync(ItemId, Movie, [], false, default)).Candidates;
        var calls = 0;

        var failure = await Assert.ThrowsAsync<StreamResolutionException>(() => resolver.OpenAsync<int>(ItemId, Movie, [], candidates[0].SourceId,
            (_, _) => { calls++; return Task.FromException<int>(new InvalidOperationException("bad")); }, default));

        Assert.Equal(StreamResolverService.MaxCandidates, calls);
        Assert.Equal(StreamResolverService.MaxCandidates, failure.Attempts.Count);
        Assert.IsAssignableFrom<InvalidOperationException>(failure);
    }

    [Fact]
    public async Task UnknownSourceId_RefreshesOnceThenPlaysTheBestCandidate()
    {
        var addon = new FakeAddon(_ => [File("https://a.test/1.mkv", "One.mkv")]);
        var resolver = new StreamResolverService(addon, null, NullLogger<StreamResolverService>.Instance);

        var result = await resolver.OpenAsync(ItemId, Movie, [], "0000000000000000000000000000dead", (candidate, _) => Task.FromResult(candidate.Url), default);

        Assert.Equal("https://a.test/1.mkv", result.Value);
        Assert.Equal(1, addon.Invalidations);
        Assert.Contains(result.FailedAttempts, attempt => attempt.Outcome.Contains("No longer offered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForcedRefreshes_AreRateLimitedPerTitle()
    {
        var addon = new FakeAddon(_ => [File("https://a.test/1.mkv", "One.mkv")]);
        var resolver = new StreamResolverService(addon, null, NullLogger<StreamResolverService>.Instance);

        Assert.True((await resolver.ResolveAsync(ItemId, Movie, [], true, default)).Refreshed);
        Assert.False((await resolver.ResolveAsync(ItemId, Movie, [], true, default)).Refreshed);
        Assert.True((await resolver.ResolveAsync(ItemId, new StreamTitle("tt0111161", 1, 2), [], true, default)).Refreshed);
        Assert.Equal(2, addon.Invalidations);
    }

    // ---------- Link health ----------

    [Theory]
    [InlineData(206, "video/x-matroska", StreamHealth.Healthy)]
    [InlineData(200, "text/html; charset=utf-8", StreamHealth.Unplayable)]
    [InlineData(403, null, StreamHealth.Expired)]
    [InlineData(410, null, StreamHealth.Expired)]
    [InlineData(429, null, StreamHealth.Transient)]
    [InlineData(502, null, StreamHealth.Transient)]
    [InlineData(400, null, StreamHealth.Unplayable)]
    public void HealthClassification(int status, string? contentType, StreamHealth expected) =>
        Assert.Equal(expected, HttpStreamHealthProbe.Classify(status, contentType?.Split(';')[0]).Health);

    [Fact]
    public async Task HealthProbe_RequestsOneByteAndRetries403Once()
    {
        var ranges = new List<string?>();
        var handler = new Handler((request, _) =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            Assert.Equal("ok", request.Headers.GetValues("X-Token").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });
        var probe = new NoDelayHealthProbe(new ClientFactory(handler));
        var candidate = new StreamCandidate("id", "key", "https://rd.test/x.mkv", new Dictionary<string, string> { ["X-Token"] = "ok" }, "x", null, null, "mkv");

        var result = await probe.CheckAsync(candidate, default);

        Assert.Equal(StreamHealth.Expired, result.Health);
        Assert.Equal(["bytes=0-0", "bytes=0-0"], ranges);
    }

    // ---------- AIOStreams client ----------

    [Fact]
    public async Task Client_RetriesTransientFailuresOnceThenCoolsDown()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var client = new NoDelayClient(new ClientFactory(handler), cache, new PluginConfiguration { AIOStreamsUrl = "https://addon.test/x" });

        for (var lookup = 0; lookup < AIOStreamsClient.FailuresBeforeCooldown; lookup++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieStreamsAsync($"tt{lookup + 1}"));
        }

        Assert.Equal(AIOStreamsClient.FailuresBeforeCooldown * 2, handler.Calls);
        Assert.True(client.GetCooldownRemaining() > TimeSpan.Zero);
        var paused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieStreamsAsync("tt99"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, paused.StatusCode);
        Assert.Equal(AIOStreamsClient.FailuresBeforeCooldown * 2, handler.Calls);
    }

    [Fact]
    public async Task Client_InvalidateStreamsBypassesTheCache()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"streams":[{"url":"https://a.test/1.mkv"}]}""")
        }));
        var client = new NoDelayClient(new ClientFactory(handler), cache, new PluginConfiguration { AIOStreamsUrl = "https://addon.test/x" });

        await client.GetMovieStreamsAsync("tt1");
        await client.GetMovieStreamsAsync("tt1");
        Assert.Equal(1, handler.Calls);
        client.InvalidateStreams("tt1");
        await client.GetMovieStreamsAsync("tt1");
        Assert.Equal(2, handler.Calls);
    }

    // ---------- Relay recovery mid-playback ----------

    [Fact]
    public async Task Relay_ReResolvesAnExpiredLinkAndResendsTheRangeRequest()
    {
        using MemoryCache memory = new(new MemoryCacheOptions());
        var relay = new AioStreamsHttpRelay(memory, AioStreamsPlaybackTests.Proxy<IServerApplicationHost>.Create((_, _) => "http://127.0.0.1:8096"));
        var addon = new FakeAddon(generation => [File($"https://rd.test/dl/token{generation}/movie.mkv", "Movie.mkv")]);
        var resolver = new StreamResolverService(addon, null, NullLogger<StreamResolverService>.Instance);
        var original = (await resolver.ResolveAsync(ItemId, Movie, [], false, default)).Candidates[0];
        var ticket = relay.Register(original.Url, original.Headers, recovery: new StreamRecoveryKey(ItemId, Movie, original.StableKey)).Split('/').Last();
        var requested = new List<(string Url, string? Range)>();
        var handler = new Handler((request, _) =>
        {
            requested.Add((request.RequestUri!.AbsoluteUri, request.Headers.Range?.ToString()));
            if (request.RequestUri.AbsoluteUri.Contains("token0", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([7, 8]) };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(500, 501, 1000);
            return Task.FromResult(response);
        });
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers.Range = "bytes=500-501";
        http.Response.Body = new MemoryStream();
        var controller = new AioStreamsSourceController(relay, new ClientFactory(handler), NullLogger<AioStreamsSourceController>.Instance, resolver)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        Assert.IsType<EmptyResult>(await controller.GetSource(ticket, default));

        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal(new byte[] { 7, 8 }, ((MemoryStream)http.Response.Body).ToArray());
        Assert.Equal(
            [("https://rd.test/dl/token0/movie.mkv", "bytes=500-501"), ("https://rd.test/dl/token1/movie.mkv", "bytes=500-501")],
            requested);
        Assert.Equal("https://rd.test/dl/token1/movie.mkv", relay.Get(ticket)!.Url);
    }

    [Fact]
    public async Task Relay_RetriesATransientUpstreamErrorOnce()
    {
        using MemoryCache memory = new(new MemoryCacheOptions());
        var relay = new AioStreamsHttpRelay(memory, AioStreamsPlaybackTests.Proxy<IServerApplicationHost>.Create((_, _) => "http://127.0.0.1:8096"));
        var ticket = relay.Register("https://cdn.test/a.mkv", new Dictionary<string, string>()).Split('/').Last();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Response.Body = new MemoryStream();
        var controller = new AioStreamsSourceController(relay, new ClientFactory(handler), NullLogger<AioStreamsSourceController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        await controller.GetSource(ticket, default);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(503, http.Response.StatusCode);
    }

    // ---------- Diagnostics ----------

    [Fact]
    public async Task Diagnostics_HungCheckBecomesATimedOutFailure()
    {
        var diagnostics = CreateDiagnostics(new Handler((_, _) => Task.FromResult(new HttpResponseMessage())), new PluginConfiguration(), new FakeHealthProbe(_ => StreamHealthResult.Ok(200)));

        var result = await diagnostics.RunCheckAsync("slow", "Slow", "Test", TimeSpan.FromMilliseconds(150), async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return PluginDiagnosticsService.CheckOutcome.Pass("never");
        }, default);

        Assert.Equal(DiagnosticStatus.Fail, result.Status);
        Assert.StartsWith("Timed out", result.Message, StringComparison.Ordinal);
        Assert.InRange(result.DurationMs, 100, 5000);
    }

    [Fact]
    public async Task Diagnostics_UnexpectedErrorsDoNotLeakTheirMessage()
    {
        var diagnostics = CreateDiagnostics(new Handler((_, _) => Task.FromResult(new HttpResponseMessage())), new PluginConfiguration(), new FakeHealthProbe(_ => StreamHealthResult.Ok(200)));

        var result = await diagnostics.RunCheckAsync("boom", "Boom", "Test", TimeSpan.FromSeconds(5),
            _ => throw new InvalidOperationException("https://addon.test/secret-config"), default);

        Assert.Equal(DiagnosticStatus.Fail, result.Status);
        Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_FullRunReportsEveryCheckWithoutSecrets()
    {
        var handler = new Handler((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            string? body = url switch
            {
                "https://addon.test/secret-token/manifest.json" => """{"name":"AIOStreams","version":"2.0","resources":["stream"]}""",
                "https://addon.test/secret-token/stream/movie/tt0111161.json" =>
                    """{"streams":[{"name":"[RD+] A","url":"https://cdn-a.test/k/secret-link.mkv","behaviorHints":{"filename":"a.mkv"}},{"name":"[RD+] B","url":"https://cdn-b.test/b.mkv","behaviorHints":{"filename":"b.mkv"}}]}""",
                "https://catalog.test/cat-secret/manifest.json" => """{"name":"Cinemeta","resources":["catalog","meta"],"catalogs":[{"id":"top"}]}""",
                _ => null
            };
            if (url.StartsWith("https://api.themoviedb.org/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(body == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
        var config = new PluginConfiguration
        {
            StreamProvider = StreamProvider.AIOStreams,
            AIOStreamsUrl = "https://addon.test/secret-token/manifest.json",
            CatalogProvider = CatalogProvider.StremioAddon,
            StremioCatalogUrl = "https://catalog.test/cat-secret",
            TmdbApiKey = "tmdb-secret",
            EnablePersistence = true,
            PersistentLibraryPath = Path.Combine(Path.GetTempPath(), "mediarow-diag-" + Guid.NewGuid().ToString("N"))
        };
        var health = new FakeHealthProbe(candidate => candidate.Url.Contains("cdn-a", StringComparison.Ordinal)
            ? new StreamHealthResult(StreamHealth.Expired, 403, "HTTP 403 (link expired or removed)")
            : StreamHealthResult.Ok(206));
        var diagnostics = CreateDiagnostics(handler, config, health);

        var report = (await diagnostics.TryRunAsync(new DiagnosticsOptions { ProbeStream = false }, default))!;
        var status = report.Checks.ToDictionary(check => check.Id, check => check.Status);

        Assert.Equal(DiagnosticStatus.Pass, status["configuration"]);
        Assert.Equal(DiagnosticStatus.Pass, status["aiostreams-manifest"]);
        Assert.Equal(DiagnosticStatus.Pass, status["aiostreams-streams"]);
        Assert.Equal(DiagnosticStatus.Warning, status["stream-source"]);
        Assert.Equal(DiagnosticStatus.Skipped, status["stream-probe"]);
        Assert.Equal(DiagnosticStatus.Fail, status["ffmpeg"]);
        Assert.Equal(DiagnosticStatus.Warning, status["folders"]);
        Assert.Equal(DiagnosticStatus.Warning, status["libraries"]);
        Assert.Equal(DiagnosticStatus.Fail, status["tmdb"]);
        Assert.Equal(DiagnosticStatus.Skipped, status["tvdb"]);
        Assert.Equal(DiagnosticStatus.Pass, status["catalog"]);
        Assert.Equal(DiagnosticStatus.Fail, report.Status);
        Assert.Equal(report.Checks.Count, report.Passed + report.Warnings + report.Failed + report.Skipped);
        Assert.Same(report, diagnostics.LastReport);

        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Status\":\"Fail\"", json, StringComparison.Ordinal);
        Assert.Contains("cdn-a.test", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_OnlyOneRunAtATime()
    {
        var gate = new TaskCompletionSource();
        var handler = new Handler(async (_, token) =>
        {
            await gate.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"resources":["stream"]}""") };
        });
        var config = new PluginConfiguration { StreamProvider = StreamProvider.AIOStreams, AIOStreamsUrl = "https://addon.test/x" };
        var diagnostics = CreateDiagnostics(handler, config, new FakeHealthProbe(_ => StreamHealthResult.Ok(200)));

        var first = diagnostics.TryRunAsync(new DiagnosticsOptions { ProbeStream = false }, default);
        Assert.Null(await diagnostics.TryRunAsync(new DiagnosticsOptions(), default));
        gate.SetResult();
        Assert.NotNull(await first);
    }

    [Fact]
    public void Diagnostics_FolderProbeDistinguishesWritableMissingAndCreatable()
    {
        var root = Path.Combine(Path.GetTempPath(), "mediarow-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(DiagnosticStatus.Pass, PluginDiagnosticsService.ProbeFolder(root).Status);
            Assert.Empty(Directory.GetFiles(root));
            Assert.Equal(DiagnosticStatus.Warning, PluginDiagnosticsService.ProbeFolder(Path.Combine(root, "missing", "deeper")).Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static TestDiagnostics CreateDiagnostics(Handler handler, PluginConfiguration config, IStreamHealthProbe health)
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        var relay = new AioStreamsHttpRelay(memory, AioStreamsPlaybackTests.Proxy<IServerApplicationHost>.Create((_, _) => "http://127.0.0.1:8096"));
        var encoder = AioStreamsPlaybackTests.Proxy<IMediaEncoder>.Create((method, _) => method.Name switch
        {
            "get_EncoderPath" or "get_ProbePath" => null,
            "get_EncoderVersion" => null,
            _ => throw new NotSupportedException(method.Name)
        });
        var library = AioStreamsPlaybackTests.Proxy<ILibraryManager>.Create((method, _) => method.Name == "GetVirtualFolders"
            ? new List<MediaBrowser.Model.Entities.VirtualFolderInfo>()
            : throw new NotSupportedException(method.Name));
        return new TestDiagnostics(new ClientFactory(handler), encoder, library, health, relay, config);
    }

    private sealed class TestDiagnostics(
        IHttpClientFactory factory, IMediaEncoder encoder, ILibraryManager library, IStreamHealthProbe health, AioStreamsHttpRelay relay, PluginConfiguration config)
        : PluginDiagnosticsService(factory, encoder, library, health, relay, NullLogger<PluginDiagnosticsService>.Instance)
    {
        protected override PluginConfiguration Config => config;
        protected override string? PluginDataPath => Path.GetTempPath();
        protected override string PluginVersion => "test";
    }

    private sealed class FakeAddon(Func<int, List<AIOStream>> streams) : IAIOStreamsClient
    {
        private int _generation;

        public int Invalidations { get; private set; }

        public bool IsConfigured => true;

        public Task<AIOStreamsResponse?> GetMovieStreamsAsync(string imdbId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AIOStreamsResponse?>(new AIOStreamsResponse { Streams = [.. streams(_generation)] });

        public Task<AIOStreamsResponse?> GetEpisodeStreamsAsync(string imdbId, int season, int episode, CancellationToken cancellationToken = default) =>
            GetMovieStreamsAsync(imdbId, cancellationToken);

        public void InvalidateStreams(string imdbId, int? season = null, int? episode = null)
        {
            Invalidations++;
            _generation++;
        }
    }

    private sealed class FakeHealthProbe(Func<StreamCandidate, StreamHealthResult> check) : IStreamHealthProbe
    {
        public Task<StreamHealthResult> CheckAsync(StreamCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(check(candidate));
    }

    private sealed class NoDelayHealthProbe(IHttpClientFactory factory) : HttpStreamHealthProbe(factory)
    {
        protected override TimeSpan GetRetryDelay() => TimeSpan.Zero;
    }

    private sealed class NoDelayClient(IHttpClientFactory factory, IMemoryCache cache, PluginConfiguration config)
        : AIOStreamsClient(factory, cache, NullLogger<AIOStreamsClient>.Instance)
    {
        protected override PluginConfiguration Config => config;
        protected override TimeSpan GetRetryDelay(TimeSpan? retryAfter) => TimeSpan.Zero;
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return send(request, cancellationToken);
        }
    }
}
