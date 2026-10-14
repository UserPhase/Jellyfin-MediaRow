using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Api;

/// <summary>Client for the configured AIOStreams Stremio addon.</summary>
public class AIOStreamsClient : IAIOStreamsClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AIOStreamsClient> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inFlight = new(StringComparer.Ordinal);
    private static readonly Regex ImdbIdPattern = new(@"^tt[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AIOStreamsClient(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<AIOStreamsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public bool IsConfigured => TryGetBaseUrl(Config.AIOStreamsUrl, out _);

    public static bool TryGetBaseUrl(string? configuredUrl, out string baseUrl)
    {
        baseUrl = (configuredUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/manifest.json".Length];
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    public Task<AIOStreamsResponse?> GetMovieStreamsAsync(string imdbId, CancellationToken cancellationToken = default)
    {
        ValidateImdbId(imdbId);
        return GetStreamsAsync("movie", imdbId, cancellationToken);
    }

    public Task<AIOStreamsResponse?> GetEpisodeStreamsAsync(string imdbId, int season, int episode, CancellationToken cancellationToken = default)
    {
        ValidateImdbId(imdbId);
        ArgumentOutOfRangeException.ThrowIfNegative(season);
        ArgumentOutOfRangeException.ThrowIfLessThan(episode, 1);
        return GetStreamsAsync("series", $"{imdbId}:{season}:{episode}", cancellationToken);
    }

    private async Task<AIOStreamsResponse?> GetStreamsAsync(string type, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetBaseUrl(Config.AIOStreamsUrl, out var baseUrl))
        {
            _logger.LogWarning("[Mediarow] AIOStreams requires a valid HTTP(S) addon URL without a query or fragment");
            throw new InvalidOperationException("Configure a valid AIOStreams addon URL before playback.");
        }

        var addonIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl)));
        var cacheKey = $"aiostreams:{addonIdentity}:{type}:{id}";
        if (_cache.TryGetValue<byte[]>(cacheKey, out var cached) && cached != null)
        {
            return JsonSerializer.Deserialize<AIOStreamsResponse>(cached, JsonOptions);
        }

        // Callers asking for the same title at once (an early prefetch, the details page, PlaybackInfo) share one
        // request; it runs without their cancellation so one closed page does not abort the others.
        var fetch = _inFlight.GetOrAdd(cacheKey, key => new Lazy<Task<byte[]>>(() => FetchAndCacheAsync(baseUrl, type, id, key)));
        var data = await fetch.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AIOStreamsResponse>(data, JsonOptions);
    }

    private async Task<byte[]> FetchAndCacheAsync(string baseUrl, string type, string id, string cacheKey)
    {
        try
        {
            return await FetchAsync(baseUrl, type, id, cacheKey).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(cacheKey, out _);
        }
    }

    private async Task<byte[]> FetchAsync(string baseUrl, string type, string id, string cacheKey)
    {
        using var client = _httpClientFactory.CreateClient(nameof(AIOStreamsClient));
        client.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/stream/{type}/{id}.json");
        request.Headers.UserAgent.ParseAdd("Mediarow/2.0.0");
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Mediarow] AIOStreams returned HTTP {Status} for {Type} {Id}", (int)response.StatusCode, type, id);
                throw new HttpRequestException($"AIOStreams returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }

            var result = await response.Content.ReadFromJsonAsync<AIOStreamsResponse>(JsonOptions).ConfigureAwait(false);
            if (result?.Streams == null)
            {
                throw new JsonException("AIOStreams did not return a streams array.");
            }

            // Addon ordering and notWebReady hints are not codec evidence: Jellyfin probes the selected file.
            var data = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
            _cache.Set(cacheKey, data, result.Streams.Count == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5));
            return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            // Exception messages from HTTP clients may contain the private addon URL.
            _logger.LogWarning("[Mediarow] AIOStreams request failed for {Type} {Id} ({ErrorType})", type, id, ex.GetType().Name);
            throw new HttpRequestException("AIOStreams could not return streams. Check the addon configuration and server connectivity.",
                null, (ex as HttpRequestException)?.StatusCode);
        }
    }

    private static void ValidateImdbId(string imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || !ImdbIdPattern.IsMatch(imdbId))
        {
            throw new ArgumentException("A valid IMDb ID beginning with tt is required.", nameof(imdbId));
        }
    }
}
