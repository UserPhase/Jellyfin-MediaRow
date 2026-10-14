using System.Globalization;
using System.Text.Json;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.Api;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>Identifies one episode for an intro/credits lookup.</summary>
public sealed record SkipSegmentRequest(
    string? ImdbId, string? TmdbId, string? AniListId, string? MalId, int? Season, int Episode, long? RuntimeTicks);

/// <summary>A skippable part of an episode in ticks.</summary>
public sealed record SkipSegment(MediaSegmentType Type, long StartTicks, long EndTicks);

/// <summary>
/// Finds intro, recap and credits timestamps in community databases: AniSkip for anime,
/// then TheIntroDB (TMDB ids) and IntroDB (IMDb ids). None of them needs an API key.
/// </summary>
public class SkipSegmentService(
    IHttpClientFactory httpClientFactory, IMemoryCache cache, AniListClient? aniList, ILogger<SkipSegmentService> logger)
{
    public const string AniSkipUrl = "https://api.aniskip.com/v2/skip-times/";
    public const string TheIntroDbUrl = "https://api.theintrodb.org/v2/media";
    public const string IntroDbUrl = "https://api.introdb.app/segments";

    // Credits that end this close to the end of the episode run to the end, which also lets clients show "Next episode".
    private static readonly long CreditsTailTicks = TimeSpan.FromSeconds(30).Ticks;
    private static readonly long PastEndTicks = TimeSpan.FromMinutes(10).Ticks;
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(6);

    public async Task<IReadOnlyList<SkipSegment>> GetSegmentsAsync(SkipSegmentRequest request, CancellationToken cancellationToken)
    {
        var key = $"mediarow-skip:{request.ImdbId}:{request.TmdbId}:{request.AniListId}:{request.MalId}:{request.Season}:{request.Episode}";
        if (!cache.TryGetValue<RawSegments>(key, out var raw) || raw == null)
        {
            raw = await LookupAsync(request, cancellationToken).ConfigureAwait(false);
            cache.Set(key, raw, raw.IsEmpty ? TimeSpan.FromHours(2) : TimeSpan.FromHours(12));
        }

        return Normalize(raw, request.RuntimeTicks);
    }

    internal static IReadOnlyList<SkipSegment> Normalize(RawSegments raw, long? runtimeTicks)
    {
        var result = new List<SkipSegment>();
        void Add(MediaSegmentType type, (long Start, long? End)? range)
        {
            if (range is not { } value || value.Start < 0 || (runtimeTicks is { } limit && value.Start >= limit))
            {
                return;
            }

            long end;
            if (type == MediaSegmentType.Outro &&
                (value.End is null || (runtimeTicks is { } runtime && value.End >= runtime - CreditsTailTicks)))
            {
                end = Math.Max(runtimeTicks ?? 0, value.End ?? value.Start) + PastEndTicks;
            }
            else if (value.End is { } given)
            {
                end = runtimeTicks is { } length ? Math.Min(given, length) : given;
            }
            else
            {
                return;
            }

            if (end > value.Start)
            {
                result.Add(new SkipSegment(type, value.Start, end));
            }
        }

        Add(MediaSegmentType.Recap, raw.Recap);
        Add(MediaSegmentType.Intro, raw.Intro);
        Add(MediaSegmentType.Outro, raw.Outro);
        return result.OrderBy(segment => segment.StartTicks).ToList();
    }

    private async Task<RawSegments> LookupAsync(SkipSegmentRequest request, CancellationToken cancellationToken)
    {
        var sources = new List<Task<RawSegments>>();
        if (request.Season is null or <= 1 && (request.MalId != null || request.AniListId != null))
        {
            sources.Add(Safe("AniSkip", () => FromAniSkipAsync(request, cancellationToken)));
        }

        if (!string.IsNullOrWhiteSpace(request.TmdbId) && request.Season != null)
        {
            var url = $"{TheIntroDbUrl}?tmdb_id={Uri.EscapeDataString(request.TmdbId)}&season={request.Season}&episode={request.Episode}";
            sources.Add(Safe("TheIntroDB", () => FromTheIntroDbAsync(url, cancellationToken)));
        }

        if (!string.IsNullOrWhiteSpace(request.ImdbId) && request.Season != null)
        {
            var url = $"{IntroDbUrl}?imdb_id={Uri.EscapeDataString(request.ImdbId)}&season={request.Season}&episode={request.Episode}";
            sources.Add(Safe("IntroDB", () => FromIntroDbAsync(url, cancellationToken)));
        }

        var found = await Task.WhenAll(sources).ConfigureAwait(false);
        return new RawSegments(
            found.Select(source => source.Intro).FirstOrDefault(range => range != null),
            found.Select(source => source.Recap).FirstOrDefault(range => range != null),
            found.Select(source => source.Outro).FirstOrDefault(range => range != null));
    }

    private async Task<RawSegments> Safe(string source, Func<Task<RawSegments>> lookup)
    {
        try
        {
            return await lookup().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogDebug("[Mediarow] {Source} skip lookup failed ({ErrorType})", source, ex.GetType().Name);
            return RawSegments.Empty;
        }
    }

    private async Task<RawSegments> FromAniSkipAsync(SkipSegmentRequest request, CancellationToken cancellationToken)
    {
        var malId = request.MalId;
        if (string.IsNullOrWhiteSpace(malId) && aniList != null &&
            int.TryParse(request.AniListId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var anilistId))
        {
            var mal = await aniList.GetMalIdAsync(anilistId, cancellationToken).ConfigureAwait(false);
            malId = mal?.ToString(CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(malId))
        {
            return RawSegments.Empty;
        }

        var url = $"{AniSkipUrl}{Uri.EscapeDataString(malId)}/{request.Episode}?types=op&types=ed&types=recap&episodeLength=0";
        using var json = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (json == null || !json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return RawSegments.Empty;
        }

        // Several releases may be submitted; take the one whose length is closest to ours.
        var runtimeSeconds = request.RuntimeTicks / (double)TimeSpan.TicksPerSecond;
        (long, long?)? Pick(string type) => results.EnumerateArray()
            .Where(result => result.TryGetProperty("skipType", out var skip) && skip.GetString() == type &&
                result.TryGetProperty("interval", out _))
            .OrderBy(result => runtimeSeconds is { } length && result.TryGetProperty("episodeLength", out var episodeLength) && episodeLength.ValueKind == JsonValueKind.Number &&
                episodeLength.TryGetDouble(out var seconds) ? Math.Abs(seconds - length) : 0)
            .Select(result =>
            {
                var interval = result.GetProperty("interval");
                return ((long, long?)?)(Seconds(interval, "startTime") ?? 0, Seconds(interval, "endTime"));
            })
            .FirstOrDefault();

        return new RawSegments(Pick("op"), Pick("recap"), Pick("ed"));
    }

    private async Task<RawSegments> FromTheIntroDbAsync(string url, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (json == null)
        {
            return RawSegments.Empty;
        }

        return new RawSegments(
            Range(json.RootElement, "intro"), Range(json.RootElement, "recap"), Range(json.RootElement, "credits"));
    }

    private async Task<RawSegments> FromIntroDbAsync(string url, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (json == null)
        {
            return RawSegments.Empty;
        }

        return new RawSegments(
            Range(json.RootElement, "intro"), Range(json.RootElement, "recap"),
            Range(json.RootElement, "outro") ?? Range(json.RootElement, "credits"));
    }

    // Accepts {"start_ms":..,"end_ms":..} or an array of them.
    private static (long Start, long? End)? Range(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            value = value.EnumerateArray().FirstOrDefault(entry => entry.ValueKind == JsonValueKind.Object);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var start = Milliseconds(value, "start_ms") ?? Seconds(value, "start_sec");
        var end = Milliseconds(value, "end_ms") ?? Seconds(value, "end_sec");
        return start == null && end == null ? null : (start ?? 0, end);
    }

    private static long? Milliseconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var ms)
            ? (long)(ms * TimeSpan.TicksPerMillisecond)
            : null;

    private static long? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds)
            ? (long)(seconds * TimeSpan.TicksPerSecond)
            : null;

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SourceTimeout);
        using var client = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.UserAgent.ParseAdd("Jellyfin-Mediarow");
        message.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(message, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    internal sealed record RawSegments((long Start, long? End)? Intro, (long Start, long? End)? Recap, (long Start, long? End)? Outro)
    {
        public static readonly RawSegments Empty = new(null, null, null);

        public bool IsEmpty => Intro == null && Recap == null && Outro == null;
    }
}
