using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// "Test configuration": runs independent setup probes concurrently, each with its own timeout, so an administrator
/// finds a broken addon, key, folder or FFmpeg before users hit playback errors. The AIOStreams checks form one
/// chain (lookup, then link health, then ffprobe) because each step needs the previous result.
/// </summary>
public class PluginDiagnosticsService
{
    public static readonly TimeSpan ConfigurationTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StreamLookupTimeout = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan StreamSourceTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StreamProbeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LocalCheckTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);
    private const int SourcesToCheck = 3;
    private static readonly Regex ImdbIdPattern = new(@"^tt[0-9]{1,10}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMediaEncoder _encoder;
    private readonly ILibraryManager _libraryManager;
    private readonly IStreamHealthProbe _healthProbe;
    private readonly AioStreamsHttpRelay _relay;
    private readonly ILogger<PluginDiagnosticsService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public PluginDiagnosticsService(
        IHttpClientFactory httpClientFactory,
        IMediaEncoder encoder,
        ILibraryManager libraryManager,
        IStreamHealthProbe healthProbe,
        AioStreamsHttpRelay relay,
        ILogger<PluginDiagnosticsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _encoder = encoder;
        _libraryManager = libraryManager;
        _healthProbe = healthProbe;
        _relay = relay;
        _logger = logger;
    }

    public DiagnosticsReport? LastReport { get; private set; }

    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    protected virtual string? PluginDataPath => MediarowPlugin.Instance?.DataFolderPath;

    protected virtual string PluginVersion => MediarowPlugin.Instance?.Version?.ToString() ?? "unknown";

    /// <summary>Runs every check, or returns null when a run is already in progress.</summary>
    public async Task<DiagnosticsReport?> TryRunAsync(DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return await RunAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<DiagnosticsReport> RunAsync(DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var config = Config;
        var imdbId = ImdbIdPattern.IsMatch(options.ImdbId ?? string.Empty) ? options.ImdbId! : new DiagnosticsOptions().ImdbId;

        var groups = await Task.WhenAll(
            Single("configuration", "Configuration", "Setup", ConfigurationTimeout, _ => Task.FromResult(CheckConfiguration(config)), cancellationToken),
            Single("aiostreams-manifest", "AIOStreams addon", "Streams", ManifestTimeout, token => CheckAioStreamsManifestAsync(config, token), cancellationToken),
            RunStreamChainAsync(config, imdbId, options.ProbeStream, cancellationToken),
            Single("ffmpeg", "FFmpeg / FFprobe", "Playback", LocalCheckTimeout, _ => Task.FromResult(CheckFfmpeg()), cancellationToken),
            Single("folders", "Writable folders", "Storage", LocalCheckTimeout, _ => Task.FromResult(CheckFolders(config)), cancellationToken),
            Single("libraries", "Library folders", "Storage", LocalCheckTimeout, _ => Task.FromResult(CheckLibraries(config)), cancellationToken),
            Single("tmdb", "TMDB API key", "Metadata", MetadataTimeout, token => CheckTmdbAsync(config, token), cancellationToken),
            Single("tvdb", "TVDB API key", "Metadata", MetadataTimeout, token => CheckTvdbAsync(config, token), cancellationToken),
            Single("catalog", "Catalog addon", "Metadata", MetadataTimeout, token => CheckCatalogAsync(config, token), cancellationToken)).ConfigureAwait(false);

        var checks = groups.SelectMany(group => group).ToList();
        var report = new DiagnosticsReport(
            checks.Any(check => check.Status == DiagnosticStatus.Fail) ? DiagnosticStatus.Fail
                : checks.Any(check => check.Status == DiagnosticStatus.Warning) ? DiagnosticStatus.Warning
                : DiagnosticStatus.Pass,
            startedAt,
            stopwatch.ElapsedMilliseconds,
            PluginVersion,
            checks.Count(check => check.Status == DiagnosticStatus.Pass),
            checks.Count(check => check.Status == DiagnosticStatus.Warning),
            checks.Count(check => check.Status == DiagnosticStatus.Fail),
            checks.Count(check => check.Status == DiagnosticStatus.Skipped),
            checks);
        LastReport = report;
        _logger.LogInformation(
            "[Mediarow] Diagnostics finished in {Elapsed} ms: {Passed} passed, {Warnings} warnings, {Failed} failed, {Skipped} skipped",
            report.DurationMs, report.Passed, report.Warnings, report.Failed, report.Skipped);
        return report;
    }

    private async Task<IReadOnlyList<DiagnosticCheckResult>> Single(
        string id, string name, string category, TimeSpan timeout,
        Func<CancellationToken, Task<CheckOutcome>> check, CancellationToken cancellationToken)
        => [await RunCheckAsync(id, name, category, timeout, check, cancellationToken).ConfigureAwait(false)];

    /// <summary>
    /// Runs one check with its own deadline. A hung probe becomes a failed result instead of stalling the report, and
    /// exception messages are never surfaced because HTTP errors can contain private addon URLs.
    /// </summary>
    public async Task<DiagnosticCheckResult> RunCheckAsync(
        string id, string name, string category, TimeSpan timeout,
        Func<CancellationToken, Task<CheckOutcome>> check, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        CheckOutcome outcome;
        try
        {
            outcome = await check(deadline.Token).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            outcome = CheckOutcome.Fail(
                string.Create(CultureInfo.InvariantCulture, $"Timed out after {timeout.TotalSeconds:0} seconds."),
                hint: "The service did not answer in time. Check the server's network connection and the service status.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Mediarow] Diagnostic check {Check} failed unexpectedly ({ErrorType})", id, ex.GetType().Name);
            outcome = CheckOutcome.Fail($"Unexpected error ({ex.GetType().Name}). See the server log for details.");
        }

        return new DiagnosticCheckResult(id, name, category, outcome.Status, outcome.Message, stopwatch.ElapsedMilliseconds, outcome.Details, outcome.Hint);
    }

    private static CheckOutcome CheckConfiguration(PluginConfiguration config)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        var details = new Dictionary<string, string>
        {
            ["Stream provider"] = config.StreamProvider.ToString(),
            ["Catalog"] = config.CatalogProvider.ToString(),
            ["Persistence"] = config.EnablePersistence ? "On" : "Off",
            ["Stream recovery"] = config.EnableStreamRecovery ? "On" : "Off"
        };

        if (config.StreamProvider == StreamProvider.AIOStreams)
        {
            if (!AIOStreamsClient.TryGetBaseUrl(config.AIOStreamsUrl, out _))
            {
                problems.Add("The AIOStreams URL is missing or invalid");
            }

            if (!config.EnablePersistence)
            {
                problems.Add("AIOStreams playback needs persistence (titles must be added to a library)");
            }
        }
        else if (config.StreamProvider == StreamProvider.None)
        {
            warnings.Add("No stream provider is selected; titles can be browsed but not played");
        }

        if (config.CatalogProvider == CatalogProvider.StremioAddon && string.IsNullOrWhiteSpace(config.StremioCatalogUrl))
        {
            problems.Add("The catalog addon URL is empty");
        }

        if (config.CatalogProvider == CatalogProvider.Direct)
        {
            if (config.MovieApiSource == ApiSource.Tmdb && string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                problems.Add("Movies use TMDB but no TMDB API key is set");
            }

            if (config.TvShowApiSource == ApiSource.Tvdb && string.IsNullOrWhiteSpace(config.TvdbApiKey))
            {
                problems.Add("TV shows use TVDB but no TVDB API key is set");
            }
        }

        if (problems.Count > 0)
        {
            return CheckOutcome.Fail(string.Join(". ", problems) + ".", details, "Fix these settings and save before testing again.");
        }

        return warnings.Count > 0
            ? CheckOutcome.Warning(string.Join(". ", warnings) + ".", details)
            : CheckOutcome.Pass("Required settings are filled in.", details);
    }

    private async Task<CheckOutcome> CheckAioStreamsManifestAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (config.StreamProvider != StreamProvider.AIOStreams)
        {
            return CheckOutcome.Skipped("AIOStreams is not the selected stream provider.");
        }

        if (!AIOStreamsClient.TryGetBaseUrl(config.AIOStreamsUrl, out var baseUrl))
        {
            return CheckOutcome.Fail("The AIOStreams URL is missing or invalid.", hint: "Paste the full addon URL from AIOStreams (it ends with /manifest.json).");
        }

        var details = new Dictionary<string, string> { ["Host"] = new Uri(baseUrl).Host };
        using var response = await SendAsync(HttpMethod.Get, $"{baseUrl}/manifest.json", cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            return CheckOutcome.Fail("Could not connect to the addon host.", details, "Check that the server can reach the AIOStreams host (DNS, firewall, proxy).");
        }

        if (!response.IsSuccessStatusCode)
        {
            return CheckOutcome.Fail($"The addon answered HTTP {(int)response.StatusCode}.", details,
                (int)response.StatusCode is 401 or 403 or 404
                    ? "The addon configuration in the URL is probably outdated. Copy a fresh URL from AIOStreams."
                    : "The addon host is having problems. Retry later.");
        }

        using var manifest = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (manifest == null)
        {
            return CheckOutcome.Fail("The URL does not return an addon manifest.", details, "Use the addon URL, not the configuration page URL.");
        }

        AddString(details, "Addon", manifest.RootElement, "name");
        AddString(details, "Version", manifest.RootElement, "version");
        return HasResource(manifest.RootElement, "stream")
            ? CheckOutcome.Pass("The addon manifest is valid and provides streams.", details)
            : CheckOutcome.Fail("The addon does not provide streams.", details, "Make sure the URL belongs to AIOStreams.");
    }

    private async Task<IReadOnlyList<DiagnosticCheckResult>> RunStreamChainAsync(
        PluginConfiguration config, string imdbId, bool probeStream, CancellationToken cancellationToken)
    {
        const string Category = "Streams";
        if (config.StreamProvider != StreamProvider.AIOStreams || !AIOStreamsClient.TryGetBaseUrl(config.AIOStreamsUrl, out var baseUrl))
        {
            const string reason = "Needs a configured AIOStreams addon.";
            return
            [
                Skipped("aiostreams-streams", "Stream lookup", Category, reason),
                Skipped("stream-source", "Stream link", Category, reason),
                Skipped("stream-probe", "Stream probe", Category, reason)
            ];
        }

        IReadOnlyList<StreamCandidate> candidates = [];
        var lookup = await RunCheckAsync("aiostreams-streams", "Stream lookup", Category, StreamLookupTimeout, async token =>
        {
            var details = new Dictionary<string, string> { ["Title"] = imdbId };
            // Deliberately uncached: this measures what a user waits for on a title nobody opened yet.
            using var response = await SendAsync(HttpMethod.Get, $"{baseUrl}/stream/movie/{imdbId}.json", token).ConfigureAwait(false);
            if (response == null)
            {
                return CheckOutcome.Fail("Could not connect to the addon host.", details);
            }

            if (!response.IsSuccessStatusCode)
            {
                return CheckOutcome.Fail($"The addon answered HTTP {(int)response.StatusCode}.", details,
                    "AIOStreams could not search your services. Check its own status page and service credentials.");
            }

            var result = await ReadAsync<AIOStreamsResponse>(response, token).ConfigureAwait(false);
            if (result?.Streams == null)
            {
                return CheckOutcome.Fail("The addon returned an invalid stream response.", details);
            }

            candidates = StreamResolverService.BuildCandidates(Guid.Empty, result.Streams, []);
            details["Results"] = result.Streams.Count.ToString(CultureInfo.InvariantCulture);
            details["Playable"] = candidates.Count.ToString(CultureInfo.InvariantCulture);
            return candidates.Count > 0
                ? CheckOutcome.Pass($"Found {candidates.Count} playable stream(s).", details)
                : CheckOutcome.Fail("The addon returned no playable HTTP streams.", details,
                    "Enable a debrid service or HTTP sources in AIOStreams; torrent-only (P2P) results cannot be played by Jellyfin.");
        }, cancellationToken).ConfigureAwait(false);

        if (lookup.Status != DiagnosticStatus.Pass)
        {
            return
            [
                lookup,
                Skipped("stream-source", "Stream link", Category, "Skipped because the stream lookup failed."),
                Skipped("stream-probe", "Stream probe", Category, "Skipped because the stream lookup failed.")
            ];
        }

        StreamCandidate? playable = null;
        var source = await RunCheckAsync("stream-source", "Stream link", Category, StreamSourceTimeout, async token =>
        {
            var tested = candidates.Take(SourcesToCheck).ToList();
            var results = await Task.WhenAll(tested.Select(candidate => _healthProbe.CheckAsync(candidate, token))).ConfigureAwait(false);
            var details = new Dictionary<string, string>();
            for (var index = 0; index < tested.Count; index++)
            {
                details[$"Candidate {index + 1}"] = $"{results[index].Detail} ({HostOf(tested[index].Url)})";
            }

            var firstHealthy = Array.FindIndex(results, result => result.Health == StreamHealth.Healthy);
            if (firstHealthy < 0)
            {
                var expired = results.Any(result => result.Health == StreamHealth.Expired);
                return CheckOutcome.Fail($"None of the top {tested.Count} stream links answered.", details,
                    expired
                        ? "The debrid host rejected the links. Check that your debrid subscription is active and the API key in AIOStreams is valid."
                        : "The stream hosts are unreachable from this server. Check outbound connectivity, VPN or firewall rules.");
            }

            playable = tested[firstHealthy];
            return firstHealthy == 0
                ? CheckOutcome.Pass("The top stream link is reachable.", details)
                : CheckOutcome.Warning($"The top link failed; candidate {firstHealthy + 1} works. Playback will fall back automatically.", details);
        }, cancellationToken).ConfigureAwait(false);

        if (!probeStream)
        {
            return [lookup, source, Skipped("stream-probe", "Stream probe", Category, "Disabled for this run.")];
        }

        if (playable == null)
        {
            return [lookup, source, Skipped("stream-probe", "Stream probe", Category, "Skipped because no stream link was reachable.")];
        }

        var probe = await RunCheckAsync("stream-probe", "Stream probe", Category, StreamProbeTimeout,
            token => ProbeStreamAsync(playable, token), cancellationToken).ConfigureAwait(false);
        return [lookup, source, probe];
    }

    private async Task<CheckOutcome> ProbeStreamAsync(StreamCandidate candidate, CancellationToken cancellationToken)
    {
        var source = new MediaSourceInfo
        {
            Id = candidate.SourceId,
            Path = candidate.Headers.Count > 0 ? _relay.Register(candidate.Url, candidate.Headers, candidate.Container == "hls") : candidate.Url,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Container = candidate.Container,
            AnalyzeDurationMs = 3000,
            MediaStreams = [],
            RequiredHttpHeaders = []
        };
        var info = await _encoder.GetMediaInfo(new MediaInfoRequest
        {
            MediaSource = source,
            MediaType = DlnaProfileType.Video,
            ExtractChapters = false
        }, cancellationToken).ConfigureAwait(false);

        var video = info?.MediaStreams?.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);
        if (info == null || video == null)
        {
            return CheckOutcome.Fail("FFprobe could not read a video track from the stream.", hint: "The file may be broken or an unsupported container; playback will try other versions.");
        }

        var details = new Dictionary<string, string>
        {
            ["Container"] = info.Container ?? "unknown",
            ["Video"] = string.Create(CultureInfo.InvariantCulture, $"{video.Codec} {video.Width}x{video.Height}"),
            ["Audio tracks"] = info.MediaStreams!.Count(stream => stream.Type == MediaStreamType.Audio).ToString(CultureInfo.InvariantCulture)
        };
        if (info.RunTimeTicks is > 0)
        {
            details["Duration"] = TimeSpan.FromTicks(info.RunTimeTicks.Value).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        }

        return CheckOutcome.Pass("FFprobe read the stream successfully.", details);
    }

    private CheckOutcome CheckFfmpeg()
    {
        var details = new Dictionary<string, string>();
        var encoderFound = !string.IsNullOrWhiteSpace(_encoder.EncoderPath) && File.Exists(_encoder.EncoderPath);
        var probeFound = !string.IsNullOrWhiteSpace(_encoder.ProbePath) && File.Exists(_encoder.ProbePath);
        details["FFmpeg"] = encoderFound ? "Found" : "Missing";
        details["FFprobe"] = probeFound ? "Found" : "Missing";
        if (_encoder.EncoderVersion is { } version)
        {
            details["Version"] = version.ToString();
        }

        return encoderFound && probeFound
            ? CheckOutcome.Pass("FFmpeg and FFprobe are available.", details)
            : CheckOutcome.Fail("FFmpeg is not available, so streams cannot be probed or transcoded.", details,
                "Install jellyfin-ffmpeg and set its path under Dashboard > Playback > Transcoding.");
    }

    private CheckOutcome CheckFolders(PluginConfiguration config)
    {
        var folders = new List<(string Label, string? Path)> { ("Plugin data", PluginDataPath) };
        if (config.EnablePersistence)
        {
            folders.Add(("Movies", config.GetPersistencePath("movies")));
            folders.Add(("TV shows", config.GetPersistencePath("tv")));
            folders.Add(("Anime", config.GetPersistencePath("anime")));
        }

        if (config.EnableSubtitles)
        {
            folders.Add(("Subtitle cache", config.SubtitleCachePath));
        }

        var details = new Dictionary<string, string>();
        var status = DiagnosticStatus.Pass;
        foreach (var (label, path) in folders)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var (folderStatus, message) = ProbeFolder(path);
            details[label] = $"{message}: {path}";
            status = Worst(status, folderStatus);
        }

        return status switch
        {
            DiagnosticStatus.Fail => CheckOutcome.Fail("At least one folder is not writable.", details,
                "Give the Jellyfin service account write access (for Docker, check the volume mapping and PUID/PGID)."),
            DiagnosticStatus.Warning => CheckOutcome.Warning("Some folders do not exist yet; they will be created when needed.", details),
            _ => CheckOutcome.Pass("All folders are writable.", details)
        };
    }

    public static (DiagnosticStatus Status, string Message) ProbeFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path));
                while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    parent = Path.GetDirectoryName(parent);
                }

                return parent != null && CanWrite(parent)
                    ? (DiagnosticStatus.Warning, "Missing (can be created)")
                    : (DiagnosticStatus.Fail, "Missing and cannot be created");
            }

            return CanWrite(path) ? (DiagnosticStatus.Pass, "Writable") : (DiagnosticStatus.Fail, "Not writable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (DiagnosticStatus.Fail, "Not accessible");
        }
    }

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, $".mediarow-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "ok");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private CheckOutcome CheckLibraries(PluginConfiguration config)
    {
        if (!config.EnablePersistence)
        {
            return CheckOutcome.Skipped("Persistence is off, so no library folders are needed.");
        }

        var locations = _libraryManager.GetVirtualFolders()
            .SelectMany(folder => (folder.Locations ?? []).Select(location => (folder.Name, Location: NormalizePath(location))))
            .ToList();
        var details = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var (label, category) in new[] { ("Movies", "movies"), ("TV shows", "tv"), ("Anime", "anime") })
        {
            var path = NormalizePath(config.GetPersistencePath(category));
            var library = locations.FirstOrDefault(entry => IsSameOrParent(entry.Location, path));
            details[label] = library.Name ?? "Not in any library";
            if (library.Name == null)
            {
                missing.Add(label);
            }
        }

        return missing.Count == 0
            ? CheckOutcome.Pass("Every persistence folder belongs to a Jellyfin library.", details)
            : CheckOutcome.Warning($"Not in a library: {string.Join(", ", missing)}. Added titles will not appear there.", details,
                "Add the folder to a library under Dashboard > Libraries (Movies for movies, Shows for TV and anime).");
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsSameOrParent(string library, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(library, comparison) ||
            path.StartsWith(library + Path.DirectorySeparatorChar, comparison);
    }

    private async Task<CheckOutcome> CheckTmdbAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            return CheckOutcome.Skipped("No TMDB API key is set.");
        }

        using var response = await SendAsync(HttpMethod.Get,
            "https://api.themoviedb.org/3/configuration?api_key=" + Uri.EscapeDataString(config.TmdbApiKey.Trim()), cancellationToken).ConfigureAwait(false);
        return response switch
        {
            null => CheckOutcome.Fail("Could not connect to api.themoviedb.org."),
            { StatusCode: HttpStatusCode.Unauthorized } => CheckOutcome.Fail("TMDB rejected the API key.", hint: "Use the v3 API key (not the read access token) from themoviedb.org > Settings > API."),
            { IsSuccessStatusCode: true } => CheckOutcome.Pass("The TMDB API key works."),
            _ => CheckOutcome.Warning($"TMDB answered HTTP {(int)response.StatusCode}.", hint: "TMDB may be having problems. Retry later.")
        };
    }

    private async Task<CheckOutcome> CheckTvdbAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.TvdbApiKey))
        {
            return CheckOutcome.Skipped("No TVDB API key is set.");
        }

        using var response = await SendAsync(HttpMethod.Post, "https://api4.thetvdb.com/v4/login", cancellationToken,
            JsonContent.Create(new { apikey = config.TvdbApiKey.Trim() })).ConfigureAwait(false);
        return response switch
        {
            null => CheckOutcome.Fail("Could not connect to api4.thetvdb.com."),
            { StatusCode: HttpStatusCode.Unauthorized } => CheckOutcome.Fail("TVDB rejected the API key.", hint: "Check the project API key at thetvdb.com > Dashboard > API keys."),
            { IsSuccessStatusCode: true } => CheckOutcome.Pass("The TVDB API key works."),
            _ => CheckOutcome.Warning($"TVDB answered HTTP {(int)response.StatusCode}.", hint: "TVDB may be having problems. Retry later.")
        };
    }

    private async Task<CheckOutcome> CheckCatalogAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (config.CatalogProvider != CatalogProvider.StremioAddon)
        {
            return CheckOutcome.Skipped("The catalog uses TMDB/TVDB directly.");
        }

        var url = (config.StremioCatalogUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/manifest.json".Length];
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return CheckOutcome.Fail("The catalog addon URL is missing or invalid.", hint: "Use a Stremio metadata addon URL such as Cinemeta or AIOMetadata.");
        }

        var details = new Dictionary<string, string> { ["Host"] = uri.Host };
        using var response = await SendAsync(HttpMethod.Get, url + "/manifest.json", cancellationToken).ConfigureAwait(false);
        if (response == null || !response.IsSuccessStatusCode)
        {
            return CheckOutcome.Fail(response == null ? "Could not connect to the catalog addon." : $"The catalog addon answered HTTP {(int)response.StatusCode}.", details);
        }

        using var manifest = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (manifest == null)
        {
            return CheckOutcome.Fail("The URL does not return an addon manifest.", details);
        }

        AddString(details, "Addon", manifest.RootElement, "name");
        var hasCatalogs = manifest.RootElement.TryGetProperty("catalogs", out var catalogs) &&
            catalogs.ValueKind == JsonValueKind.Array && catalogs.GetArrayLength() > 0;
        if (hasCatalogs)
        {
            details["Catalogs"] = catalogs.GetArrayLength().ToString(CultureInfo.InvariantCulture);
        }

        return hasCatalogs && HasResource(manifest.RootElement, "meta")
            ? CheckOutcome.Pass("The catalog addon provides catalogs and metadata.", details)
            : CheckOutcome.Warning("The addon is missing catalogs or metadata, so rows or details pages may be empty.", details);
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string url, CancellationToken cancellationToken, HttpContent? content = null)
    {
        using var client = _httpClientFactory.CreateClient(nameof(PluginDiagnosticsService));
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.UserAgent.ParseAdd("Mediarow-Diagnostics/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool HasResource(JsonElement manifest, string resource) =>
        manifest.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array &&
        resources.EnumerateArray().Any(entry =>
            (entry.ValueKind == JsonValueKind.String && entry.GetString() == resource) ||
            (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String && name.GetString() == resource));

    private static void AddString(Dictionary<string, string> details, string label, JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 and <= 100 } text)
        {
            details[label] = text;
        }
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "unknown host";

    private static DiagnosticStatus Worst(DiagnosticStatus current, DiagnosticStatus next) =>
        (current, next) switch
        {
            (DiagnosticStatus.Fail, _) or (_, DiagnosticStatus.Fail) => DiagnosticStatus.Fail,
            (DiagnosticStatus.Warning, _) or (_, DiagnosticStatus.Warning) => DiagnosticStatus.Warning,
            _ => DiagnosticStatus.Pass
        };

    private static DiagnosticCheckResult Skipped(string id, string name, string category, string message) =>
        new(id, name, category, DiagnosticStatus.Skipped, message, 0);

    public sealed record CheckOutcome(DiagnosticStatus Status, string Message, IReadOnlyDictionary<string, string>? Details = null, string? Hint = null)
    {
        public static CheckOutcome Pass(string message, IReadOnlyDictionary<string, string>? details = null) => new(DiagnosticStatus.Pass, message, details);

        public static CheckOutcome Warning(string message, IReadOnlyDictionary<string, string>? details = null, string? hint = null) => new(DiagnosticStatus.Warning, message, details, hint);

        public static CheckOutcome Fail(string message, IReadOnlyDictionary<string, string>? details = null, string? hint = null) => new(DiagnosticStatus.Fail, message, details, hint);

        public static CheckOutcome Skipped(string message) => new(DiagnosticStatus.Skipped, message);
    }
}
