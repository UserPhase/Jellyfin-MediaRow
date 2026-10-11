using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Providers;

public class DynamicAioStreamsMediaSourceProvider : IMediaSourceProvider
{
    private const int MaxBackgroundProbes = 3;
    private readonly ConcurrentDictionary<string, Lazy<Task<MediaInfo>>> _probes = new(StringComparer.Ordinal);
    private readonly IAIOStreamsClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _encoder;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DynamicAioStreamsMediaSourceProvider> _logger;
    private readonly DynamicItemCache _itemCache;
    private readonly AioStreamsHttpRelay _relay;

    public DynamicAioStreamsMediaSourceProvider(
        IAIOStreamsClient client, ILibraryManager libraryManager, IMediaEncoder encoder,
        IMemoryCache cache, ILogger<DynamicAioStreamsMediaSourceProvider> logger, DynamicItemCache itemCache,
        AioStreamsHttpRelay relay)
    {
        _client = client;
        _libraryManager = libraryManager;
        _encoder = encoder;
        _cache = cache;
        _logger = logger;
        _itemCache = itemCache;
        _relay = relay;
    }

    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public bool IsMediarowItem(BaseItem item)
    {
        if (item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) != true || !File.Exists(item.Path))
        {
            return false;
        }

        try
        {
            return StrmMarker.IsMarker(File.ReadAllText(item.Path).Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("[Mediarow] Cannot read stream marker for item {Id} ({ErrorType})", item.Id, ex.GetType().Name);
            return false;
        }
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Config.StreamProvider != StreamProvider.AIOStreams || !_client.IsConfigured ||
            item is not (Movie or Episode) || !IsMediarowItem(item))
        {
            return [];
        }

        if (!Config.ShowUnreleasedStreams && item.PremiereDate > DateTime.UtcNow)
        {
            return [];
        }

        var imdbId = item is Episode episode
            ? _libraryManager.GetItemById(episode.SeriesId)?.GetProviderId("Imdb")
            : item.GetProviderId("Imdb");
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            _logger.LogWarning("[Mediarow] Missing movie/series IMDb ID for playback item {Id}", item.Id);
            throw new InvalidOperationException("The title requires a movie or series IMDb ID for AIOStreams playback.");
        }

        AIOStreamsResponse? response;
        if (item is Episode selectedEpisode)
        {
            var numbering = GetEpisodeNumbers(selectedEpisode, File.ReadAllText(item.Path));
            response = await _client.GetEpisodeStreamsAsync(imdbId, numbering.Season, numbering.Episode, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            response = await _client.GetMovieStreamsAsync(imdbId, cancellationToken).ConfigureAwait(false);
        }

        if (response?.Streams == null)
        {
            throw new InvalidOperationException("AIOStreams returned no stream response.");
        }

        var sources = new List<MediaSourceInfo>();
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stream in response.Streams)
        {
            if (stream == null || !Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            {
                _logger.LogDebug("[Mediarow] Skipping a non-HTTP stream for item {Id}", item.Id);
                continue;
            }

            var headers = stream.BehaviorHints?.ProxyHeaders?.Request ?? new Dictionary<string, string>();
            if (headers.Any(header => string.IsNullOrWhiteSpace(header.Key) ||
                header.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)) ||
                header.Value == null || header.Value.Any(character => character is '\r' or '\n')))
            {
                _logger.LogWarning("[Mediarow] Skipping stream with invalid request headers for item {Id}", item.Id);
                continue;
            }

            var identity = stream.Url + "\n" + string.Join("\n", headers.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
            if (!seenUrls.Add(identity))
            {
                continue;
            }

            var sourceId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{item.Id:N}:{identity}"))).ToString("N");
            _itemCache.StoreAIOStreamsMapping(sourceId, item.Id, stream.Url!, stream.BehaviorHints?.Filename);
            var container = StreamContainerHelper.DetectContainer(stream.Url, stream.BehaviorHints?.Filename);
            sources.Add(new MediaSourceInfo
            {
                Id = sourceId,
                Name = stream.DisplayName,
                Size = stream.BehaviorHints?.VideoSize is > 0 ? stream.BehaviorHints.VideoSize : null,
                Path = headers.Count == 0 ? stream.Url : _relay.Register(stream.Url!, headers, container == "hls"),
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                Container = container,
                RequiredHttpHeaders = [],
                IsRemote = true,
                VideoType = VideoType.VideoFile,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true,
                RequiresOpening = true,
                OpenToken = $"{item.Id:N}_{sourceId}",
                RequiresClosing = true,
                RunTimeTicks = item.RunTimeTicks,
                MediaStreams = []
            });
        }

        _logger.LogInformation("[Mediarow] Found {Count} playable AIOStreams candidates for item {Id}", sources.Count, item.Id);
        return sources;
    }

    public static (int Season, int Episode) GetEpisodeNumbers(Episode episode, string marker)
    {
        int? season = episode.ParentIndexNumber;
        int? number = episode.IndexNumber;
        var query = marker.Split('?', 2).ElementAtOrDefault(1);
        if (query != null)
        {
            var parameters = query.Split('&').Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
            if (parameters.TryGetValue("stremioS", out var mappedSeason) || parameters.ContainsKey("stremioE"))
            {
                if (!int.TryParse(mappedSeason, NumberStyles.None, CultureInfo.InvariantCulture, out var valueSeason) ||
                    !parameters.TryGetValue("stremioE", out var mappedEpisode) ||
                    !int.TryParse(mappedEpisode, NumberStyles.None, CultureInfo.InvariantCulture, out var valueEpisode))
                {
                    throw new InvalidOperationException("The episode has an invalid Stremio numbering mapping.");
                }

                season = valueSeason;
                number = valueEpisode;
            }
        }

        if (!season.HasValue || season < 0 || !number.HasValue || number < 1)
        {
            throw new InvalidOperationException("The episode requires valid season and episode numbers.");
        }

        return (season.Value, number.Value);
    }

    public async Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parts = openToken.Split('_');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var itemId))
        {
            throw new ArgumentException("Invalid AIOStreams source token.", nameof(openToken));
        }

        var item = _libraryManager.GetItemById(itemId) ?? throw new InvalidOperationException("The playback item is no longer available.");
        var sources = await GetMediaSources(item, cancellationToken).ConfigureAwait(false);
        var source = sources.FirstOrDefault(candidate => candidate.Id == parts[1])
            ?? throw new InvalidOperationException("The selected stream expired or is unavailable. Reload the title to refresh streams.");
        var info = await ProbeAsync(source, cancellationToken).ConfigureAwait(false);

        source.MediaStreams = info.MediaStreams;
        source.AnalyzeDurationMs = 3000;
        source.MediaAttachments = info.MediaAttachments;
        source.Container = info.Container;
        source.Formats = info.Formats;
        source.Bitrate = info.Bitrate;
        source.Size = info.Size ?? source.Size;
        source.RunTimeTicks = info.RunTimeTicks ?? source.RunTimeTicks;
        if (source.RunTimeTicks > 0 && item.RunTimeTicks != source.RunTimeTicks)
        {
            item.RunTimeTicks = source.RunTimeTicks;
            await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
        source.DefaultAudioStreamIndex = info.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Audio)?.Index;
        source.SupportsDirectPlay = !source.Path.StartsWith(_relay.BaseUrl + "/Mediarow/AIOStreams/Source/", StringComparison.OrdinalIgnoreCase);
        source.RequiresOpening = false;
        source.LiveStreamId = Guid.NewGuid().ToString("N");
        _logger.LogInformation("[Mediarow] Probed selected AIOStreams source {SourceId}: {Count} tracks", source.Id, source.MediaStreams.Count);
        return new RemoteStream(source);
    }

    /// <summary>
    /// Starts probing a source in the background so pressing Play does not wait for ffprobe.
    /// </summary>
    public void WarmProbe(MediaSourceInfo? source)
    {
        if (source == null || _cache.TryGetValue(ProbeCacheKey(source.Id), out _) ||
            _probes.ContainsKey(source.Id) || _probes.Count >= MaxBackgroundProbes)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ProbeAsync(source, CancellationToken.None).ConfigureAwait(false);
                _logger.LogDebug("[Mediarow] Pre-probed AIOStreams source {SourceId}", source.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Mediarow] Background probe skipped for {SourceId} ({ErrorType})", source.Id, ex.GetType().Name);
            }
        });
    }

    private static string ProbeCacheKey(string sourceId) => $"aiostreams-probe:{sourceId}";

    private async Task<MediaInfo> ProbeAsync(MediaSourceInfo source, CancellationToken cancellationToken)
    {
        var cacheKey = ProbeCacheKey(source.Id);
        if (_cache.TryGetValue<byte[]>(cacheKey, out var cached) && cached != null)
        {
            return JsonSerializer.Deserialize<MediaInfo>(cached)!;
        }

        // One shared probe per source: a background warm-up and a Play click reuse the same ffprobe run.
        var probe = _probes.GetOrAdd(source.Id, _ => new Lazy<Task<MediaInfo>>(() => RunProbeAsync(source, cacheKey)));
        return await probe.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MediaInfo> RunProbeAsync(MediaSourceInfo source, string cacheKey)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // Server defaults can analyze minutes of a large remote file before returning metadata.
            source.AnalyzeDurationMs = 3000;
            MediaInfo info;
            try
            {
                info = await _encoder.GetMediaInfo(new MediaInfoRequest
                {
                    MediaSource = source,
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new HttpRequestException("The selected stream did not respond to probing within 30 seconds. Select another version or retry.");
            }

            if (info?.MediaStreams?.Any(stream => stream.Type == MediaStreamType.Video) != true)
            {
                throw new InvalidOperationException("The selected stream could not be probed as a video.");
            }

            // A source ID is a hash of the exact stream URL and headers, so its tracks do not change.
            _cache.Set(cacheKey, JsonSerializer.SerializeToUtf8Bytes(info), TimeSpan.FromHours(6));
            return info;
        }
        finally
        {
            _probes.TryRemove(source.Id, out _);
        }
    }

    private sealed class RemoteStream(MediaSourceInfo source) : ILiveStream
    {
        public int ConsumerCount { get; set; } = 1;
        public string OriginalStreamId { get; set; } = source.Id;
        public string TunerHostId => string.Empty;
        public bool EnableStreamSharing => false;
        public MediaSourceInfo MediaSource { get; set; } = source;
        public string UniqueId { get; } = source.LiveStreamId;
        public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;
        public Task Close() => Task.CompletedTask;
        public Stream GetStream() => throw new NotSupportedException("Jellyfin reads the remote HTTP media source.");
        public void Dispose() { }
    }
}
