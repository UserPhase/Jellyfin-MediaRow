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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AddonHealth> _health = new(StringComparer.Ordinal);
    private static readonly Regex ImdbIdPattern = new(@"^tt[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Consecutive failed lookups after which the addon is given a cooldown instead of more requests.</summary>
    public const int FailuresBeforeCooldown = 3;

    private static readonly TimeSpan MaxCooldown = TimeSpan.FromSeconds(60);

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

        var addonIdentity = AddonIdentity(baseUrl);
        var cacheKey = StreamCacheKey(addonIdentity, type, id);
        if (_cache.TryGetValue<byte[]>(cacheKey, out var cached) && cached != null)
        {
            return JsonSerializer.Deserialize<AIOStreamsResponse>(cached, JsonOptions);
        }

        // Callers asking for the same title at once (an early prefetch, the details page, PlaybackInfo) share one
        // request; it runs without their cancellation so one closed page does not abort the others.
        var fetch = _inFlight.GetOrAdd(cacheKey, key => new Lazy<Task<byte[]>>(() => FetchAndCacheAsync(baseUrl, addonIdentity, type, id, key)));
        var data = await fetch.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AIOStreamsResponse>(data, JsonOptions);
    }

    public void InvalidateStreams(string imdbId, int? season = null, int? episode = null)
    {
        ValidateImdbId(imdbId);
        if (!TryGetBaseUrl(Config.AIOStreamsUrl, out var baseUrl))
        {
            return;
        }

        var (type, id) = season.HasValue && episode.HasValue
            ? ("series", $"{imdbId}:{season.Value}:{episode.Value}")
            : ("movie", imdbId);
        _cache.Remove(StreamCacheKey(AddonIdentity(baseUrl), type, id));
    }

    /// <summary>
    /// Remaining cooldown of the configured addon after repeated failures, or <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public TimeSpan GetCooldownRemaining()
    {
        if (!TryGetBaseUrl(Config.AIOStreamsUrl, out var baseUrl) ||
            !_health.TryGetValue(AddonIdentity(baseUrl), out var health))
        {
            return TimeSpan.Zero;
        }

        var remaining = health.CooldownUntil - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Wait before the single retry of a transient failure. Exponential with jitter; honours a short Retry-After.
    /// </summary>
    protected virtual TimeSpan GetRetryDelay(TimeSpan? retryAfter)
    {
        if (retryAfter is { } requested && requested > TimeSpan.Zero)
        {
            return requested < TimeSpan.FromSeconds(5) ? requested : TimeSpan.FromSeconds(5);
        }

        return TimeSpan.FromMilliseconds(500 + Random.Shared.Next(0, 250));
    }

    private static string AddonIdentity(string baseUrl) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl)));

    private static string StreamCacheKey(string addonIdentity, string type, string id) => $"aiostreams:{addonIdentity}:{type}:{id}";

    private async Task<byte[]> FetchAndCacheAsync(string baseUrl, string addonIdentity, string type, string id, string cacheKey)
    {
        try
        {
            var health = _health.GetOrAdd(addonIdentity, _ => new AddonHealth());
            var cooldown = health.CooldownUntil - DateTimeOffset.UtcNow;
            if (cooldown > TimeSpan.Zero)
            {
                // During a failure cascade every title would otherwise send its own doomed request.
                _logger.LogDebug("[Mediarow] AIOStreams cooling down for {Seconds}s; skipping {Type} {Id}", (int)Math.Ceiling(cooldown.TotalSeconds), type, id);
                throw new HttpRequestException(
                    $"AIOStreams is temporarily paused after repeated failures. Retrying in {(int)Math.Ceiling(cooldown.TotalSeconds)} seconds.",
                    null, System.Net.HttpStatusCode.ServiceUnavailable);
            }

            try
            {
                var data = await FetchAsync(baseUrl, type, id, cacheKey).ConfigureAwait(false);
                health.RecordSuccess();
                return data;
            }
            catch (HttpRequestException)
            {
                var failures = health.RecordFailure(MaxCooldown);
                if (failures >= FailuresBeforeCooldown)
                {
                    _logger.LogWarning("[Mediarow] AIOStreams failed {Count} times in a row; pausing lookups briefly", failures);
                }

                throw;
            }
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
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/stream/{type}/{id}.json");
            request.Headers.UserAgent.ParseAdd("Mediarow/2.0.0");
            request.Headers.Accept.ParseAdd("application/json");
            TimeSpan? retryAfter = null;
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    retryAfter = response.Headers.RetryAfter?.Delta;
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
            catch (Exception ex) when (attempt == 0 && IsTransient(ex))
            {
                _logger.LogDebug("[Mediarow] Retrying AIOStreams {Type} {Id} once after {ErrorType}", type, id, ex.GetType().Name);
                await Task.Delay(GetRetryDelay(retryAfter)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                // Exception messages from HTTP clients may contain the private addon URL.
                _logger.LogWarning("[Mediarow] AIOStreams request failed for {Type} {Id} ({ErrorType})", type, id, ex.GetType().Name);
                throw new HttpRequestException("AIOStreams could not return streams. Check the addon configuration and server connectivity.",
                    null, (ex as HttpRequestException)?.StatusCode);
            }
        }
    }

    /// <summary>
    /// Timeouts, connection failures, 403 (CDN/WAF challenges), 408, 429 and 5xx are retried once; other errors are final.
    /// </summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => IsTransientStatus((int)status),
        _ => false
    };

    public static bool IsTransientStatus(int status) => status is 403 or 408 or 429 or >= 500;

    private sealed class AddonHealth
    {
        private readonly object _lock = new();
        private int _failures;

        public DateTimeOffset CooldownUntil { get; private set; }

        public void RecordSuccess()
        {
            lock (_lock)
            {
                _failures = 0;
                CooldownUntil = default;
            }
        }

        public int RecordFailure(TimeSpan maxCooldown)
        {
            lock (_lock)
            {
                _failures++;
                if (_failures >= FailuresBeforeCooldown)
                {
                    var seconds = Math.Min(maxCooldown.TotalSeconds, 5 * Math.Pow(2, _failures - FailuresBeforeCooldown));
                    CooldownUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
                }

                return _failures;
            }
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
