using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Providers;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class AioStreamsPlaybackTests
{
    [Theory]
    [InlineData(" https://example.test/private/manifest.json/ ", true, "https://example.test/private")]
    [InlineData("http://127.0.0.1:18766", true, "http://127.0.0.1:18766")]
    [InlineData("file:///C:/secret", false, "file:///C:/secret")]
    [InlineData("https://example.test/addon?token=secret", false, "https://example.test/addon?token=secret")]
    [InlineData("https://user:secret@example.test", false, "https://user:secret@example.test")]
    public void AddonUrls_AreNormalizedAndValidated(string url, bool valid, string expected)
    {
        Assert.Equal(valid, AIOStreamsClient.TryGetBaseUrl(url, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task Client_PreservesOrderingAndNonWebReadyCandidatesAndIsolatesCache()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"streams":[{"name":"DTS","url":"https://video.test/one.mkv","behaviorHints":{"notWebReady":true,"proxyHeaders":{"request":{"Referer":"https://video.test"},"response":{"Cache-Control":"private"}}}},{"name":"AAC","url":"https://video.test/two.mp4"}]}""")
        }));
        var config = new PluginConfiguration { AIOStreamsUrl = "https://addon.test/private/manifest.json" };
        var client = new TestClient(new ClientFactory(handler), cache, config);
        var first = await client.GetMovieStreamsAsync("tt1234");
        Assert.Equal(new[] { "DTS", "AAC" }, first!.Streams.Select(stream => stream.Name));
        Assert.Equal("https://video.test", first.Streams[0].BehaviorHints!.ProxyHeaders!.Request!["Referer"]);
        first.Streams.Clear();
        Assert.Equal(2, (await client.GetMovieStreamsAsync("tt1234"))!.Streams.Count);
        Assert.Equal(1, handler.Calls);
        config.AIOStreamsUrl = "https://addon.test/different";
        await client.GetMovieStreamsAsync("tt1234");
        Assert.Equal(2, handler.Calls);
        await client.GetEpisodeStreamsAsync("tt1234", 0, 1);
        Assert.EndsWith("/stream/series/tt1234:0:1.json", handler.LastUrl);
    }

    [Theory]
    [InlineData(503, """{"streams":[]}""")]
    [InlineData(200, """{"error":"failed"}""")]
    [InlineData(200, """{"streams":null}""")]
    [InlineData(200, "not json")]
    public async Task Client_FailuresDoNotBecomeEmptySuccessOrLeakPrivateUrls(int status, string body)
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }));
        var client = new TestClient(new ClientFactory(handler), cache, new PluginConfiguration { AIOStreamsUrl = "https://addon.test/private-secret" });
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieStreamsAsync("tt1234"));
        Assert.DoesNotContain("private-secret", failure.ToString());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieStreamsAsync("tt1234"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Client_RejectsInvalidIdsAndPropagatesCancellation()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        var client = new TestClient(new ClientFactory(handler), cache, new PluginConfiguration { AIOStreamsUrl = "https://addon.test" });
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMovieStreamsAsync("../secret"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetEpisodeStreamsAsync("tt1234", -1, 1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMovieStreamsAsync("tt1234", cancelled.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(2, 3, "dynamiclibrary://tv/tt1234/2/3", 2, 3)]
    [InlineData(2, 3, "dynamiclibrary://tv/tt1234/2/3?stremioS=1&stremioE=12", 1, 12)]
    [InlineData(0, 1, "dynamiclibrary://tv/tt1234/0/1", 0, 1)]
    public void EpisodeNumbering_PreservesSourceMappingsAndSpecials(int season, int episode, string marker, int expectedSeason, int expectedEpisode)
    {
        Assert.Equal((expectedSeason, expectedEpisode), DynamicAioStreamsMediaSourceProvider.GetEpisodeNumbers(
            new Episode { ParentIndexNumber = season, IndexNumber = episode }, marker));
    }

    [Fact]
    public void StreamDisplayName_IncludesAddonDescriptionDetailsAndSize()
    {
        var stream = JsonSerializer.Deserialize<AIOStream>("""
            {"name":"[TB⚡] Torrentio\n2160p","description":"🎥 BluRay REMUX 🎞️ HEVC\n🎧 Atmos | TrueHD 🔊 7.1\n📦 65.9 GB (48.5 Mbps)\n📁 Movie.2019.2160p.mkv","behaviorHints":{"videoSize":65906273157}}
            """)!;
        Assert.Equal("[TB⚡] Torrentio 2160p · 🎥 BluRay REMUX 🎞️ HEVC · 🎧 Atmos | TrueHD 🔊 7.1 · 📦 65.9 GB (48.5 Mbps)", stream.DisplayName);
        Assert.Equal(65906273157, stream.BehaviorHints!.VideoSize);
        Assert.Equal("Addon · 61.4 GB", new AIOStream { Name = "Addon", BehaviorHints = new AIOBehaviorHints { VideoSize = 65906273157 } }.DisplayName);
        Assert.Equal("Legacy title", new AIOStream { Title = "Legacy title" }.DisplayName);
    }

    [Fact]
    public void EpisodeNumbering_DoesNotGuessMissingOrInvalidNumbers()
    {
        Assert.Throws<InvalidOperationException>(() => DynamicAioStreamsMediaSourceProvider.GetEpisodeNumbers(new Episode(), ""));
        Assert.Throws<InvalidOperationException>(() => DynamicAioStreamsMediaSourceProvider.GetEpisodeNumbers(
            new Episode { ParentIndexNumber = 1, IndexNumber = 1 }, "?stremioS=1"));
    }

    [Fact]
    public async Task NativeSources_SkipUnsupportedEntriesAndProbeOnlySelectedSourceWithRealTracks()
    {
        var path = Path.Combine(Path.GetTempPath(), "dynamic-aio-" + Guid.NewGuid().ToString("N") + ".strm");
        try
        {
            await File.WriteAllTextAsync(path, "dynamiclibrary://movie/tt1234");
            var movie = new Movie { Id = Guid.NewGuid(), Path = path, ProviderIds = new() { ["Imdb"] = "tt1234" } };
            var client = new StubAddon();
            var metadataUpdates = 0;
            var library = Proxy<ILibraryManager>.Create((method, _) =>
            {
                if (method.Name == "GetItemById") return movie;
                if (method.Name == "UpdateItemAsync")
                {
                    metadataUpdates++;
                    return Task.CompletedTask;
                }
                throw new NotSupportedException(method.Name);
            });
            var probeCalls = 0;
            var probeHasVideo = true;
            var encoder = Proxy<IMediaEncoder>.Create((method, args) =>
            {
                Assert.Equal("GetMediaInfo", method.Name);
                var request = Assert.IsType<MediaInfoRequest>(args![0]);
                Assert.Equal(3000, request.MediaSource.AnalyzeDurationMs);
                Assert.StartsWith("http://127.0.0.1:8096/Mediarow/AIOStreams/Source/", request.MediaSource.Path);
                Assert.Empty(request.MediaSource.RequiredHttpHeaders);
                probeCalls++;
                if (!probeHasVideo) return Task.FromResult(new MediaInfo { MediaStreams = [] });
                return Task.FromResult(new MediaInfo
                {
                    Container = "matroska", RunTimeTicks = TimeSpan.FromSeconds(37).Ticks,
                    MediaStreams = new List<MediaStream>
                    {
                        new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 1280 },
                        new() { Type = MediaStreamType.Audio, Index = 2, Codec = "ac3", Channels = 6 },
                        new() { Type = MediaStreamType.Audio, Index = 3, Codec = "aac", Channels = 2 },
                        new() { Type = MediaStreamType.Subtitle, Index = 4, Codec = "subrip" }
                    }
                });
            });
            using MemoryCache cache = new(new MemoryCacheOptions());
            var provider = new TestProvider(client, library, encoder, cache);
            var sources = (await provider.GetMediaSources(movie, default)).ToArray();
            Assert.Equal(2, sources.Length);
            Assert.Equal(0, probeCalls);
            Assert.All(sources, source => { Assert.True(source.RequiresOpening); Assert.Empty(source.MediaStreams); Assert.False(source.SupportsDirectPlay); });
            var other = new Movie { Id = Guid.NewGuid(), Path = path, ProviderIds = movie.ProviderIds };
            Assert.NotEqual(sources[0].Id, (await provider.GetMediaSources(other, default)).First().Id);
            Assert.Equal(sources[1].Id, (await provider.GetMediaSources(movie, default)).Last().Id);
            using var opened = await provider.OpenMediaSource(sources[1].OpenToken, [], default);
            Assert.Equal(1, probeCalls);
            Assert.Equal(4, opened.MediaSource.MediaStreams.Count);
            Assert.Equal("ac3", opened.MediaSource.MediaStreams[1].Codec);
            Assert.Equal(2, opened.MediaSource.DefaultAudioStreamIndex);
            Assert.Equal(TimeSpan.FromSeconds(37).Ticks, opened.MediaSource.RunTimeTicks);
            Assert.Equal(opened.MediaSource.RunTimeTicks, movie.RunTimeTicks);
            Assert.Equal(1, metadataUpdates);
            Assert.False(opened.MediaSource.RequiresOpening);
            Assert.Equal(3000, opened.MediaSource.AnalyzeDurationMs);
            Assert.False(opened.MediaSource.SupportsDirectPlay); // Required headers must pass through Jellyfin.
            Assert.True(opened.MediaSource.SupportsTranscoding);
            using var reopened = await provider.OpenMediaSource(sources[1].OpenToken, [], default);
            Assert.Equal(3000, reopened.MediaSource.AnalyzeDurationMs);
            Assert.Equal(1, probeCalls);
            Assert.Equal(1, metadataUpdates);
            Assert.NotEqual(opened.MediaSource.LiveStreamId, reopened.MediaSource.LiveStreamId);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenMediaSource(sources[1].OpenToken, [], cancelled.Token));
            cache.Remove($"aiostreams-probe:{sources[1].Id}");
            probeHasVideo = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenMediaSource(sources[1].OpenToken, [], default));
            Assert.Equal(2, probeCalls);
            Assert.Equal(1, metadataUpdates);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenMediaSource($"{movie.Id:N}_{Guid.NewGuid():N}", [], default));
            await File.WriteAllTextAsync(path, "https://ordinary.test/video.mp4");
            Assert.Empty(await provider.GetMediaSources(movie, default));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NativeSources_BackgroundWarmProbeIsSharedWithPlayback()
    {
        var path = Path.Combine(Path.GetTempPath(), "dynamic-aio-" + Guid.NewGuid().ToString("N") + ".strm");
        try
        {
            await File.WriteAllTextAsync(path, "dynamiclibrary://movie/tt1234");
            var movie = new Movie { Id = Guid.NewGuid(), Path = path, ProviderIds = new() { ["Imdb"] = "tt1234" } };
            var library = Proxy<ILibraryManager>.Create((method, _) => method.Name switch
            {
                "GetItemById" => movie,
                "UpdateItemAsync" => Task.CompletedTask,
                _ => throw new NotSupportedException(method.Name)
            });
            var probeCalls = 0;
            var release = new TaskCompletionSource<MediaInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var encoder = Proxy<IMediaEncoder>.Create((_, _) =>
            {
                Interlocked.Increment(ref probeCalls);
                return release.Task;
            });
            using MemoryCache cache = new(new MemoryCacheOptions());
            var provider = new TestProvider(new StubAddon(), library, encoder, cache);
            var source = (await provider.GetMediaSources(movie, default)).First();

            provider.WarmProbe(source);
            var open = provider.OpenMediaSource(source.OpenToken, [], default);
            for (var i = 0; i < 100 && Volatile.Read(ref probeCalls) == 0; i++) await Task.Delay(10);
            release.SetResult(new MediaInfo
            {
                Container = "matroska",
                MediaStreams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" } }
            });
            using var opened = await open;

            Assert.Equal(1, probeCalls);
            Assert.Single(opened.MediaSource.MediaStreams);
            provider.WarmProbe(source);
            using var reopened = await provider.OpenMediaSource(source.OpenToken, [], default);
            Assert.Equal(1, probeCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HeaderRelay_IsLoopbackOnlyAndPreservesRangesAndRequiredHeaders()
    {
        using MemoryCache memory = new(new MemoryCacheOptions());
        var relay = new AioStreamsHttpRelay(memory, Proxy<IServerApplicationHost>.Create((_, _) => "http://127.0.0.1:8096"));
        var url = relay.Register("https://video.test/private.mkv", new Dictionary<string, string>
        {
            ["Referer"] = "https://video.test/allowed", ["Range"] = "bytes=0-9"
        });
        var ticket = url.Split('/').Last();
        var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://video.test/private.mkv", request.RequestUri!.AbsoluteUri);
            Assert.Equal("https://video.test/allowed", request.Headers.GetValues("Referer").Single());
            Assert.Equal("bytes=1-4", request.Headers.Range!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(1, 4, 10);
            return Task.FromResult(response);
        });
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Headers.Range = "bytes=1-4";
        http.Response.Body = new MemoryStream();
        var controller = new AioStreamsSourceController(relay, new ClientFactory(handler), NullLogger<AioStreamsSourceController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        http.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.IsType<NotFoundResult>(await controller.GetSource(ticket, default));
        Assert.Equal(0, handler.Calls);
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.IsType<EmptyResult>(await controller.GetSource(ticket, default));
        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal("bytes 1-4/10", http.Response.Headers.ContentRange);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ((MemoryStream)http.Response.Body).ToArray());
    }

    [Fact]
    public async Task HeaderRelay_HeadManifestDoesNotRewriteAnEmptyBodyOrAdvertiseUpstreamLength()
    {
        using MemoryCache memory = new(new MemoryCacheOptions());
        var relay = new AioStreamsHttpRelay(memory, Proxy<IServerApplicationHost>.Create((_, _) => "http://127.0.0.1:8096"));
        var url = relay.Register("https://video.test/playlist.m3u8", new Dictionary<string, string>());
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.apple.mpegurl");
            response.Content.Headers.ContentLength = 123;
            return Task.FromResult(response);
        });
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        http.Request.Method = "HEAD";
        http.Response.Body = new MemoryStream();
        var controller = new AioStreamsSourceController(relay, new ClientFactory(handler), NullLogger<AioStreamsSourceController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        Assert.IsType<EmptyResult>(await controller.GetSource(url.Split('/').Last(), default));
        Assert.Equal("application/vnd.apple.mpegurl", http.Response.ContentType);
        Assert.Null(http.Response.ContentLength);
        Assert.Equal(0, http.Response.Body.Length);
    }

    [Fact]
    public void HeaderRelay_RewritesHlsSegmentsKeysAndNestedPlaylists()
    {
        using MemoryCache memory = new(new MemoryCacheOptions());
        var relay = new AioStreamsHttpRelay(memory, Proxy<IServerApplicationHost>.Create((_, _) => "http://127.0.0.1:8096"));
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer fixture" };
        var rewritten = relay.RewriteManifest("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"keys/key.bin\"\n#EXTINF:4,\nsegments/one.ts\nvariant.m3u8\n",
            new Uri("https://video.test/hls/master.m3u8"), headers);
        var urls = System.Text.RegularExpressions.Regex.Matches(rewritten, @"http://127\.0\.0\.1:8096/Mediarow/AIOStreams/Source/[a-f0-9]+")
            .Select(match => match.Value).ToArray();
        Assert.Equal(3, urls.Length);
        var targets = urls.Select(url => relay.Get(url.Split('/').Last())!).ToArray();
        Assert.Equal(new[] { "https://video.test/hls/keys/key.bin", "https://video.test/hls/segments/one.ts", "https://video.test/hls/variant.m3u8" }, targets.Select(target => target.Url));
        Assert.All(targets, target => Assert.Equal("Bearer fixture", target.Headers["Authorization"]));
        Assert.True(targets[2].IsManifest);
        Assert.Contains(".ts", rewritten);
        Assert.Contains(".m3u8", rewritten);
        var manifestUrl = relay.Register("https://video.test/playlist", headers, isManifest: true);
        Assert.EndsWith(".m3u8", manifestUrl);
        Assert.True(relay.Get(manifestUrl.Split('/').Last())!.IsManifest);
        Assert.DoesNotContain("Bearer fixture", rewritten);
    }

    [Fact]
    public async Task Persistence_WritesProviderMetadataAndEpisodeSourceNumbering()
    {
        var root = Path.Combine(Path.GetTempPath(), "dynamic-persist-" + Guid.NewGuid().ToString("N"));
        using MemoryCache memory = new(new MemoryCacheOptions());
        var cache = new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance);
        var library = Proxy<ILibraryManager>.Create((method, _) => method.Name == "GetItemsResult"
            ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>(Array.Empty<BaseItem>())
            : method.Name == "FindByPath" ? null : throw new NotSupportedException(method.Name));
        var persistence = new TestPersistence(library, cache, root);
        var movie = new BaseItemDto
        {
            Id = Guid.NewGuid(), Type = Jellyfin.Data.Enums.BaseItemKind.Movie, Name = "Test & Movie",
            PremiereDate = DateTime.UtcNow.AddDays(-1), ProviderIds = new() { ["Imdb"] = "tt1234" }, RunTimeTicks = TimeSpan.FromMinutes(2).Ticks
        };
        try
        {
            var moviePath = await persistence.PersistMovieAsync(movie);
            var nfo = XDocument.Load(Directory.GetFiles(moviePath!, "*.nfo").Single());
            Assert.Equal("tt1234", nfo.Root!.Element("uniqueid")!.Value);
            Assert.Equal("Test & Movie", nfo.Root.Element("title")!.Value);
            var series = new BaseItemDto { Id = Guid.NewGuid(), Type = Jellyfin.Data.Enums.BaseItemKind.Series, Name = "Test Show", Status = "Ended", ProviderIds = new() { ["Imdb"] = "tt5678" } };
            cache.StoreEpisodesForSeries(series.Id, new List<BaseItemDto>
            {
                new()
                {
                    Id = Guid.NewGuid(), Type = Jellyfin.Data.Enums.BaseItemKind.Episode, Name = "Mapped episode",
                    ParentIndexNumber = 2, IndexNumber = 3, PremiereDate = DateTime.UtcNow.AddDays(-1),
                    ProviderIds = new() { ["StremioSeason"] = "1", ["StremioEpisode"] = "12" }
                }
            });
            var seriesPath = await persistence.PersistSeriesAsync(series);
            Assert.Equal("tt5678", XDocument.Load(Path.Combine(seriesPath!, "tvshow.nfo")).Root!.Element("uniqueid")!.Value);
            Assert.Equal("true", XDocument.Load(Path.Combine(seriesPath!, "tvshow.nfo")).Root!.Element("lockdata")!.Value);
            Assert.Equal("Ended", XDocument.Load(Path.Combine(seriesPath!, "tvshow.nfo")).Root!.Element("status")!.Value);
            var seasonNfo = XDocument.Load(Path.Combine(seriesPath!, "Season 02", "season.nfo")).Root!;
            Assert.Equal("2", seasonNfo.Element("seasonnumber")!.Value);
            Assert.Equal("true", seasonNfo.Element("lockdata")!.Value);
            Assert.Equal((2, 1), persistence.GetFirstSeasonFileCount(series));
            var strm = Directory.GetFiles(seriesPath!, "*.strm", SearchOption.AllDirectories).Single();
            Assert.EndsWith("?stremioS=1&stremioE=12", await File.ReadAllTextAsync(strm));
            Assert.Equal("3", XDocument.Load(Path.ChangeExtension(strm, ".nfo")).Root!.Element("episode")!.Value);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Resolver_DoesNotReturnAnOwnedSeriesBeforeItsEpisodesAreScanned()
    {
        using MemoryCache memory = new(new MemoryCacheOptions());
        var cache = new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance);
        var source = new BaseItemDto
        {
            Id = Guid.NewGuid(), Name = "Pending Series", Type = BaseItemKind.Series,
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1234" }
        };
        var series = new Series { Id = Guid.NewGuid(), ProviderIds = source.ProviderIds };
        var hasEpisodes = false;
        var library = Proxy<ILibraryManager>.Create((method, args) =>
        {
            if (method.Name != "GetItemsResult") throw new NotSupportedException(method.Name);
            var query = Assert.IsType<InternalItemsQuery>(args![0]);
            BaseItem[] items = query.IncludeItemTypes.Contains(BaseItemKind.Episode)
                ? hasEpisodes ? [new Episode()] : []
                : [series];
            return new MediaBrowser.Model.Querying.QueryResult<BaseItem>(items);
        });
        var persistence = new TestPersistence(library, cache, Path.Combine(Path.GetTempPath(), "dynamic-pending-" + Guid.NewGuid().ToString("N")));
        series.Path = persistence.GetSeriesFolderPath(source);
        var resolver = new PlaybackItemResolver(null!, persistence, library, cache);
        Assert.Null(await resolver.ResolveAsync(source, default));
        hasEpisodes = true;
        Assert.Same(series, await resolver.ResolveAsync(source, default));
        hasEpisodes = false;
        series.Path = Path.Combine(Path.GetTempPath(), "ordinary-existing-show");
        Assert.Same(series, await resolver.ResolveAsync(source, default));
    }

    private sealed class TestClient(IHttpClientFactory factory, IMemoryCache cache, PluginConfiguration config)
        : AIOStreamsClient(factory, cache, NullLogger<AIOStreamsClient>.Instance)
    {
        protected override PluginConfiguration Config => config;
    }

    private sealed class TestProvider(IAIOStreamsClient client, ILibraryManager library, IMediaEncoder encoder, IMemoryCache cache)
        : DynamicAioStreamsMediaSourceProvider(client, library, encoder, cache, NullLogger<DynamicAioStreamsMediaSourceProvider>.Instance,
            new DynamicItemCache(cache, NullLogger<DynamicItemCache>.Instance),
            new AioStreamsHttpRelay(cache, Proxy<IServerApplicationHost>.Create((_, _) => "http://127.0.0.1:8096")))
    {
        protected override PluginConfiguration Config => new() { StreamProvider = StreamProvider.AIOStreams };
    }

    private sealed class TestPersistence(ILibraryManager library, DynamicItemCache cache, string root)
        : PersistenceService(library, cache, NullLogger<PersistenceService>.Instance)
    {
        protected override PluginConfiguration Config => new() { EnablePersistence = true, PersistentLibraryPath = root, StreamProvider = StreamProvider.AIOStreams };
    }

    private sealed class StubAddon : IAIOStreamsClient
    {
        public bool IsConfigured => true;
        public Task<AIOStreamsResponse?> GetMovieStreamsAsync(string imdbId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AIOStreamsResponse?>(new()
            {
                Streams =
                [
                    new() { Url = "https://video.test/one.mp4", Name = "First" },
                    new() { Url = "https://video.test/two.mkv", Name = "Second\nsource", BehaviorHints = new() { NotWebReady = true, ProxyHeaders = new() { Request = new() { ["Referer"] = "https://video.test" } } } },
                    new() { Url = "file:///C:/video.mp4" },
                    new() { InfoHash = "torrent" },
                    new() { Url = "https://video.test/one.mp4" }
                ]
            });
        public Task<AIOStreamsResponse?> GetEpisodeStreamsAsync(string imdbId, int season, int episode, CancellationToken cancellationToken = default) =>
            GetMovieStreamsAsync(imdbId, cancellationToken);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri!.AbsoluteUri;
            return send(request, cancellationToken);
        }
    }

    public class Proxy<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[]?, object?> _invoke = null!;
        public static T Create(Func<MethodInfo, object?[]?, object?> invoke)
        {
            var proxy = Create<T, Proxy<T>>();
            ((Proxy<T>)(object)proxy)._invoke = invoke;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
    }
}
