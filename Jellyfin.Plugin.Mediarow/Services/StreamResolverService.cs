using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>The AIOStreams lookup key of a movie or episode.</summary>
public sealed record StreamTitle(string ImdbId, int? Season = null, int? Episode = null)
{
    public bool IsEpisode => Season.HasValue && Episode.HasValue;

    public override string ToString() => IsEpisode
        ? string.Create(CultureInfo.InvariantCulture, $"{ImdbId}:{Season}:{Episode}")
        : ImdbId;
}

/// <summary>
/// One playable AIOStreams result. <see cref="StableKey"/> identifies the file (service, filename and size), so a
/// refreshed result with a new, re-signed URL keeps the same <see cref="SourceId"/>.
/// </summary>
public sealed record StreamCandidate(
    string SourceId,
    string StableKey,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string DisplayName,
    string? Filename,
    long? Size,
    string Container);

public sealed record StreamResolution(Guid ItemId, StreamTitle Title, IReadOnlyList<StreamCandidate> Candidates, bool Refreshed)
{
    public StreamCandidate? Find(string? sourceId) => string.IsNullOrEmpty(sourceId)
        ? null
        : Candidates.FirstOrDefault(candidate => candidate.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

    public StreamCandidate? FindByStableKey(string stableKey) =>
        Candidates.FirstOrDefault(candidate => candidate.StableKey.Equals(stableKey, StringComparison.Ordinal));
}

/// <summary>Identifies the file behind a relay ticket so an expired URL can be re-resolved mid-playback.</summary>
public sealed record StreamRecoveryKey(Guid ItemId, StreamTitle Title, string StableKey);

public enum StreamHealth
{
    Healthy,

    /// <summary>401/403/404/410 after the single retry: the signed link expired or the file was removed.</summary>
    Expired,

    /// <summary>Timeouts, connection errors, 408/429/5xx that persisted after the single retry.</summary>
    Transient,

    /// <summary>The host answered with something that is not media (an HTML error page, other 4xx).</summary>
    Unplayable
}

public readonly record struct StreamHealthResult(StreamHealth Health, int? StatusCode, string Detail)
{
    public static StreamHealthResult Ok(int status) => new(StreamHealth.Healthy, status, "Reachable");
}

public interface IStreamHealthProbe
{
    Task<StreamHealthResult> CheckAsync(StreamCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>
/// Requests the first byte of a stream. Timeouts and 403s are retried once (403 is often a transient CDN check);
/// what remains is classified so the resolver can refresh, retry or move on.
/// </summary>
public class HttpStreamHealthProbe(IHttpClientFactory httpClientFactory) : IStreamHealthProbe
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    public async Task<StreamHealthResult> CheckAsync(StreamCandidate candidate, CancellationToken cancellationToken)
    {
        var first = await CheckOnceAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (first.Health == StreamHealth.Healthy || !ShouldRetry(first))
        {
            return first;
        }

        await Task.Delay(GetRetryDelay(), cancellationToken).ConfigureAwait(false);
        return await CheckOnceAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    protected virtual TimeSpan GetRetryDelay() => TimeSpan.FromMilliseconds(400 + Random.Shared.Next(0, 200));

    private static bool ShouldRetry(StreamHealthResult result) =>
        result.Health == StreamHealth.Transient || result.StatusCode == 403;

    private async Task<StreamHealthResult> CheckOnceAsync(StreamCandidate candidate, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AttemptTimeout);
        using var client = httpClientFactory.CreateClient(nameof(HttpStreamHealthProbe));
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
        foreach (var header in candidate.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return Classify((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StreamHealthResult(StreamHealth.Transient, null, $"No response within {AttemptTimeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            return new StreamHealthResult(StreamHealth.Transient, null, "Connection failed (" + (ex.HttpRequestError.ToString()) + ")");
        }
    }

    public static StreamHealthResult Classify(int status, string? contentType)
    {
        if (status is >= 200 and < 300)
        {
            // Debrid and CDN error pages are often served as 200 text/html.
            return contentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
                ? new StreamHealthResult(StreamHealth.Unplayable, status, "Returned a web page instead of media")
                : StreamHealthResult.Ok(status);
        }

        return status switch
        {
            401 or 403 or 404 or 410 => new StreamHealthResult(StreamHealth.Expired, status, $"HTTP {status} (link expired or removed)"),
            408 or 429 or >= 500 => new StreamHealthResult(StreamHealth.Transient, status, $"HTTP {status} (temporary failure)"),
            _ => new StreamHealthResult(StreamHealth.Unplayable, status, $"HTTP {status}")
        };
    }
}

public sealed record StreamOpenAttempt(string SourceId, string DisplayName, string Outcome);

public sealed record StreamOpenResult<T>(T Value, StreamCandidate Candidate, IReadOnlyList<StreamOpenAttempt> FailedAttempts, bool Refreshed)
{
    public bool UsedFallback(string requestedSourceId) =>
        !Candidate.SourceId.Equals(requestedSourceId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Every candidate tried while opening a title failed. Derives from InvalidOperationException so
/// existing playback error handling maps it to a 502 response.</summary>
public sealed class StreamResolutionException(string message, IReadOnlyList<StreamOpenAttempt> attempts)
    : InvalidOperationException(message)
{
    public IReadOnlyList<StreamOpenAttempt> Attempts { get; } = attempts;
}

/// <summary>
/// Resolves AIOStreams candidates and opens them with bounded recovery:
/// <list type="number">
/// <item>Open (probe) the requested candidate.</item>
/// <item>On failure, classify the link with <see cref="IStreamHealthProbe"/>.</item>
/// <item>Expired link: refresh the addon response once (bypassing the cache) and retry the same file.</item>
/// <item>Transient failure: retry the same candidate once.</item>
/// <item>Otherwise, or if the retry fails, fall back to the next candidate.</item>
/// </list>
/// At most <see cref="MaxCandidates"/> candidates and <see cref="OpenBudget"/> are spent, because Jellyfin holds a
/// server-wide live-stream lock while a source opens.
/// </summary>
public class StreamResolverService
{
    public const int MaxCandidates = 3;
    public static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(10);
    private static readonly Regex ServiceTag = new(@"^\s*\[([^\]]+)\]", RegexOptions.CultureInvariant);

    private readonly IAIOStreamsClient _client;
    private readonly IStreamHealthProbe? _healthProbe;
    private readonly ILogger<StreamResolverService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRefresh = new(StringComparer.Ordinal);

    public StreamResolverService(IAIOStreamsClient client, IStreamHealthProbe? healthProbe, ILogger<StreamResolverService> logger)
    {
        _client = client;
        _healthProbe = healthProbe;
        _logger = logger;
    }

    public TimeSpan OpenBudget { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Returns the playable candidates in preference order. <paramref name="forceRefresh"/> bypasses the cached addon
    /// response, at most once per <see cref="RefreshCooldown"/> per title so failure cascades cannot hammer the addon.
    /// </summary>
    public async Task<StreamResolution> ResolveAsync(
        Guid itemId,
        StreamTitle title,
        IReadOnlyList<string> preferredLanguages,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var refreshed = forceRefresh && TryBeginRefresh(title);
        if (refreshed)
        {
            _client.InvalidateStreams(title.ImdbId, title.Season, title.Episode);
        }

        var response = title.IsEpisode
            ? await _client.GetEpisodeStreamsAsync(title.ImdbId, title.Season!.Value, title.Episode!.Value, cancellationToken).ConfigureAwait(false)
            : await _client.GetMovieStreamsAsync(title.ImdbId, cancellationToken).ConfigureAwait(false);
        if (response?.Streams == null)
        {
            throw new InvalidOperationException("AIOStreams returned no stream response.");
        }

        return new StreamResolution(itemId, title, BuildCandidates(itemId, response.Streams, preferredLanguages), refreshed);
    }

    /// <summary>Finds a source, refreshing the addon response once if the id is not in the cached list.</summary>
    public async Task<(StreamResolution Resolution, StreamCandidate? Candidate)> FindAsync(
        Guid itemId, StreamTitle title, IReadOnlyList<string> preferredLanguages, string sourceId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(itemId, title, preferredLanguages, false, cancellationToken).ConfigureAwait(false);
        if (resolution.Find(sourceId) is { } candidate)
        {
            return (resolution, candidate);
        }

        resolution = await ResolveAsync(itemId, title, preferredLanguages, true, cancellationToken).ConfigureAwait(false);
        return (resolution, resolution.Find(sourceId));
    }

    /// <summary>
    /// Re-resolves the file behind an expired relay ticket. Only the same file (stable key) is returned: switching to
    /// a different release mid-playback would break byte-range resumption.
    /// </summary>
    public async Task<StreamCandidate?> RecoverAsync(StreamRecoveryKey key, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(key.ItemId, key.Title, [], true, cancellationToken).ConfigureAwait(false);
        var candidate = resolution.FindByStableKey(key.StableKey);
        _logger.LogInformation(
            "[Mediarow] Stream recovery for item {ItemId}: {Outcome}",
            key.ItemId,
            candidate == null ? "the file is no longer offered" : resolution.Refreshed ? "fresh link resolved" : "reused recent refresh");
        return candidate;
    }

    public async Task<StreamOpenResult<T>> OpenAsync<T>(
        Guid itemId,
        StreamTitle title,
        IReadOnlyList<string> preferredLanguages,
        string requestedSourceId,
        Func<StreamCandidate, CancellationToken, Task<T>> open,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(OpenBudget);
        var stopwatch = Stopwatch.StartNew();
        var attempts = new List<StreamOpenAttempt>();

        var (resolution, requested) = await FindAsync(itemId, title, preferredLanguages, requestedSourceId, cancellationToken).ConfigureAwait(false);
        var refreshed = resolution.Refreshed;
        if (resolution.Candidates.Count == 0)
        {
            throw new StreamResolutionException("AIOStreams returned no playable HTTP streams for this title.", attempts);
        }

        if (requested == null)
        {
            _logger.LogInformation("[Mediarow] Requested stream {SourceId} is no longer offered for item {ItemId}; using the best available streams", requestedSourceId, itemId);
            attempts.Add(new StreamOpenAttempt(requestedSourceId, "Requested stream", "No longer offered by AIOStreams"));
        }

        var plan = (requested == null ? resolution.Candidates : resolution.Candidates.Where(candidate => candidate != requested).Prepend(requested))
            .Select(candidate => candidate.StableKey)
            .Take(MaxCandidates)
            .ToList();

        foreach (var stableKey in plan)
        {
            if (budget.IsCancellationRequested)
            {
                break;
            }

            var candidate = resolution.FindByStableKey(stableKey);
            if (candidate == null)
            {
                continue;
            }

            var retried = false;
            while (true)
            {
                Exception failure;
                try
                {
                    var value = await open(candidate, budget.Token).ConfigureAwait(false);
                    if (candidate.SourceId != requestedSourceId || attempts.Count > 0)
                    {
                        _logger.LogInformation(
                            "[Mediarow] Opened stream {SourceId} for item {ItemId} after {Failed} failed attempt(s) in {Elapsed} ms",
                            candidate.SourceId, itemId, attempts.Count, stopwatch.ElapsedMilliseconds);
                    }

                    return new StreamOpenResult<T>(value, candidate, attempts, refreshed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failure = ex;
                }

                if (budget.IsCancellationRequested)
                {
                    attempts.Add(new StreamOpenAttempt(candidate.SourceId, candidate.DisplayName, "Stopped: playback start time limit reached"));
                    break;
                }

                var health = _healthProbe == null
                    ? new StreamHealthResult(StreamHealth.Unplayable, null, Describe(failure))
                    : await _healthProbe.CheckAsync(candidate, budget.Token).ConfigureAwait(false);
                _logger.LogWarning(
                    "[Mediarow] Stream {SourceId} for item {ItemId} failed to open ({ErrorType}); link health: {Health} {Status}",
                    candidate.SourceId, itemId, failure.GetType().Name, health.Health, health.StatusCode);

                if (!retried && health.Health == StreamHealth.Expired && !refreshed)
                {
                    retried = true;
                    refreshed = true;
                    resolution = await ResolveAsync(itemId, title, preferredLanguages, true, cancellationToken).ConfigureAwait(false);
                    var renewed = resolution.FindByStableKey(stableKey);
                    if (renewed != null && !renewed.Url.Equals(candidate.Url, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("[Mediarow] Retrying stream {SourceId} with a freshly resolved link", candidate.SourceId);
                        candidate = renewed;
                        continue;
                    }
                }
                else if (!retried && health.Health == StreamHealth.Transient)
                {
                    // Briefly overloaded host or flaky route: one more attempt on the same file.
                    retried = true;
                    continue;
                }

                attempts.Add(new StreamOpenAttempt(candidate.SourceId, candidate.DisplayName, health.Detail));
                break;
            }
        }

        throw new StreamResolutionException(
            $"None of the {Math.Max(1, attempts.Count)} stream(s) tried could be opened. Select another version or retry later.",
            attempts);
    }

    private static string Describe(Exception failure) => failure switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        OperationCanceledException => "Timed out",
        _ => "Could not be probed as video"
    };

    private bool TryBeginRefresh(StreamTitle title)
    {
        var now = DateTimeOffset.UtcNow;
        var key = title.ToString();
        while (true)
        {
            if (_lastRefresh.TryGetValue(key, out var last))
            {
                if (now - last < RefreshCooldown)
                {
                    return false;
                }

                if (_lastRefresh.TryUpdate(key, now, last))
                {
                    return true;
                }
            }
            else if (_lastRefresh.TryAdd(key, now))
            {
                return true;
            }
        }
    }

    public static IReadOnlyList<StreamCandidate> BuildCandidates(Guid itemId, IEnumerable<AIOStream?> streams, IReadOnlyList<string> preferredLanguages)
    {
        var ordered = StreamLanguageDetector.OrderByLanguages(
            streams,
            stream => stream == null ? null : $"{stream.Name}\n{stream.Description ?? stream.Title}\n{stream.BehaviorHints?.Filename}",
            preferredLanguages);
        var candidates = new List<StreamCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stream in ordered)
        {
            if (stream == null || !Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            {
                continue;
            }

            var headers = stream.BehaviorHints?.ProxyHeaders?.Request ?? new Dictionary<string, string>();
            if (headers.Any(header => string.IsNullOrWhiteSpace(header.Key) ||
                header.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)) ||
                header.Value == null || header.Value.Any(character => character is '\r' or '\n')))
            {
                continue;
            }

            var stableKey = GetStableKey(stream, headers);
            if (!seen.Add(stableKey))
            {
                continue;
            }

            var sourceId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{itemId:N}:{stableKey}"))).ToString("N");
            candidates.Add(new StreamCandidate(
                sourceId,
                stableKey,
                stream.Url!,
                new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
                stream.DisplayName,
                stream.BehaviorHints?.Filename,
                stream.BehaviorHints?.VideoSize is > 0 ? stream.BehaviorHints.VideoSize : null,
                StreamContainerHelper.DetectContainer(stream.Url, stream.BehaviorHints?.Filename)));
        }

        return candidates;
    }

    /// <summary>
    /// Debrid links are re-signed on every lookup, so the file (service tag, filename, size) is the identity when the
    /// addon provides a filename. Without one, the exact URL and headers are the only safe identity.
    /// </summary>
    public static string GetStableKey(AIOStream stream, IReadOnlyDictionary<string, string> headers)
    {
        var filename = stream.BehaviorHints?.Filename?.Trim();
        if (string.IsNullOrEmpty(filename))
        {
            return "url:" + stream.Url + "\n" + string.Join("\n", headers.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{pair.Value}"));
        }

        var match = ServiceTag.Match(stream.Name ?? string.Empty);
        var service = match.Success
            ? new string(match.Groups[1].Value.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant()
            : string.Empty;
        var size = stream.BehaviorHints?.VideoSize is > 0 ? stream.BehaviorHints.VideoSize.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return $"file:{service}|{filename.ToLowerInvariant()}|{size}";
    }
}
