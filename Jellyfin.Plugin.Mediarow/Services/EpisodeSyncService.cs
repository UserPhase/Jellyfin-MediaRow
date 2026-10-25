using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

public enum EpisodeSyncTrigger
{
    Scheduled,
    PageOpen,
    Manual
}

/// <summary>
/// Delta-tracking state for one persisted series.
/// </summary>
public sealed class EpisodeSyncEntry
{
    public string? Name { get; set; }

    public DateTime? LastCheckedUtc { get; set; }

    public DateTime? LastNewEpisodeUtc { get; set; }

    /// <summary>Earliest known episode air date after the last check.</summary>
    public DateTime? NextAirDateUtc { get; set; }

    public string? Status { get; set; }

    public int KnownEpisodeCount { get; set; }

    public int ConsecutiveFailures { get; set; }

    public string? LastError { get; set; }
}

public sealed record EpisodeSyncSeriesResult(Guid SeriesId, string Name, int NewEpisodes, bool Checked, string? Error);

public sealed record EpisodeSyncSummary(
    int TrackedSeries,
    int Checked,
    int Skipped,
    int NewEpisodes,
    int Failures,
    bool LibraryScanQueued,
    IReadOnlyList<EpisodeSyncSeriesResult> Updated);

/// <summary>
/// Finds new episodes for series persisted by Mediarow and adds them straight into the library, so Jellyfin's native
/// "Next Up" sees them without anybody opening the show.
/// </summary>
public class EpisodeSyncService
{
    /// <summary>Opening a show re-checks at most this often.</summary>
    public static readonly TimeSpan PageOpenCooldown = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan EndedInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaxWaitForAirDate = TimeSpan.FromDays(3);
    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromHours(48);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILibraryManager _libraryManager;
    private readonly PersistenceService _persistence;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly DynamicItemCache _itemCache;
    private readonly StremioCatalogProvider _stremio;
    private readonly ITvdbClient _tvdb;
    private readonly ILogger<EpisodeSyncService> _logger;
    private readonly string _statePath;
    private readonly object _stateLock = new();
    private readonly object _saveLock = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _seriesLocks = new();
    private readonly SemaphoreSlim _sweepLock = new(1, 1);
    private Dictionary<Guid, EpisodeSyncEntry> _state = new();

    public EpisodeSyncService(
        ILibraryManager libraryManager,
        PersistenceService persistence,
        SearchResultFactory searchResultFactory,
        DynamicItemCache itemCache,
        StremioCatalogProvider stremio,
        ITvdbClient tvdb,
        IApplicationPaths applicationPaths,
        ILogger<EpisodeSyncService> logger)
    {
        _libraryManager = libraryManager;
        _persistence = persistence;
        _searchResultFactory = searchResultFactory;
        _itemCache = itemCache;
        _stremio = stremio;
        _tvdb = tvdb;
        _logger = logger;
        var directory = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Mediarow");
        Directory.CreateDirectory(directory);
        _statePath = Path.Combine(directory, "episode-sync-state.json");
        Load();
    }

    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public DateTime? LastSweepUtc { get; private set; }

    public EpisodeSyncSummary? LastSummary { get; private set; }

    public bool IsSweeping => _sweepLock.CurrentCount == 0;

    public IReadOnlyDictionary<Guid, EpisodeSyncEntry> GetState()
    {
        lock (_stateLock)
        {
            return _state.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
        }
    }

    /// <summary>
    /// Whether a series should be re-checked now. Shows that ended are checked weekly; when the next air date is
    /// known the check waits for it (re-checking at least every 3 days in case the schedule moved); failures back off
    /// exponentially up to 48 hours.
    /// </summary>
    public static bool IsDue(EpisodeSyncEntry? entry, DateTime nowUtc, double intervalHours, bool force)
    {
        if (force || entry?.LastCheckedUtc is not { } lastChecked)
        {
            return true;
        }

        var elapsed = nowUtc - lastChecked;
        if (entry.ConsecutiveFailures > 0)
        {
            var backoff = TimeSpan.FromHours(Math.Pow(2, Math.Min(entry.ConsecutiveFailures, 6)));
            return elapsed >= (backoff < MaxFailureBackoff ? backoff : MaxFailureBackoff);
        }

        if (string.Equals(entry.Status, "Ended", StringComparison.OrdinalIgnoreCase))
        {
            return elapsed >= EndedInterval;
        }

        if (entry.NextAirDateUtc is { } nextAir)
        {
            if (nextAir > nowUtc)
            {
                return elapsed >= MaxWaitForAirDate;
            }

            if (nextAir > lastChecked)
            {
                return true;
            }
        }

        // 10% slack so a series checked a few minutes late last run is not skipped for a whole interval.
        return elapsed >= TimeSpan.FromHours(Math.Max(1, intervalHours) * 0.9);
    }

    /// <summary>
    /// Library series whose folders live under the Mediarow persistence paths and whose .strm files are ours.
    /// </summary>
    public IReadOnlyList<Series> GetTrackedSeries()
    {
        var config = Config;
        var roots = new[] { config.GetPersistencePath("tv"), config.GetPersistencePath("anime") }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            return [];
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Series>()
            .Where(series => !string.IsNullOrEmpty(series.Path) &&
                roots.Any(root => series.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) &&
                IsMediarowFolder(series.Path))
            .ToList();
    }

    public async Task<EpisodeSyncSummary> SyncAllAsync(IProgress<double>? progress, bool force, CancellationToken cancellationToken)
    {
        if (!await _sweepLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("[EpisodeSync] A sweep is already running");
            return LastSummary ?? new EpisodeSyncSummary(0, 0, 0, 0, 0, false, []);
        }

        try
        {
            var config = Config;
            var tracked = GetTrackedSeries();
            var now = DateTime.UtcNow;
            List<Series> due;
            lock (_stateLock)
            {
                due = tracked.Where(series => IsDue(_state.GetValueOrDefault(series.Id), now, config.EpisodeSyncIntervalHours, force)).ToList();
            }

            _logger.LogInformation("[EpisodeSync] {Due} of {Tracked} tracked series due for an episode check", due.Count, tracked.Count);
            var results = new ConcurrentBag<EpisodeSyncSeriesResult>();
            var needsScan = 0;
            var done = 0;
            using var gate = new SemaphoreSlim(Math.Clamp(config.EpisodeSyncConcurrency, 1, 8));
            var work = due.Select(async series =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var (result, importFailed) = await SyncCoreAsync(series, invalidateCaches: true, cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                    if (importFailed)
                    {
                        Interlocked.Exchange(ref needsScan, 1);
                    }
                }
                finally
                {
                    gate.Release();
                    progress?.Report(Interlocked.Increment(ref done) * 100d / Math.Max(1, due.Count));
                }
            });
            await Task.WhenAll(work).ConfigureAwait(false);

            var scanQueued = needsScan == 1 && TryQueueLibraryScan();
            Save();
            var summary = new EpisodeSyncSummary(
                tracked.Count,
                results.Count(result => result.Checked),
                tracked.Count - due.Count,
                results.Sum(result => result.NewEpisodes),
                results.Count(result => result.Error != null),
                scanQueued,
                results.Where(result => result.NewEpisodes > 0 || result.Error != null).OrderBy(result => result.Name).ToList());
            LastSweepUtc = DateTime.UtcNow;
            LastSummary = summary;
            progress?.Report(100);
            _logger.LogInformation(
                "[EpisodeSync] Checked {Checked} series, {New} new episode(s), {Failures} failure(s)",
                summary.Checked, summary.NewEpisodes, summary.Failures);
            return summary;
        }
        finally
        {
            _sweepLock.Release();
        }
    }

    /// <summary>
    /// Checks one series. Page opens honour a short cooldown and reuse cached metadata.
    /// </summary>
    public async Task<EpisodeSyncSeriesResult?> SyncSeriesAsync(Series series, EpisodeSyncTrigger trigger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(series.Path) || !IsMediarowFolder(series.Path))
        {
            return null;
        }

        if (trigger == EpisodeSyncTrigger.PageOpen)
        {
            lock (_stateLock)
            {
                if (_state.TryGetValue(series.Id, out var entry) &&
                    entry.LastCheckedUtc is { } last && DateTime.UtcNow - last < PageOpenCooldown)
                {
                    return null;
                }
            }
        }

        var (result, importFailed) = await SyncCoreAsync(series, invalidateCaches: trigger != EpisodeSyncTrigger.PageOpen, cancellationToken).ConfigureAwait(false);
        if (importFailed)
        {
            TryQueueLibraryScan();
        }

        Save();
        return result;
    }

    private async Task<(EpisodeSyncSeriesResult Result, bool ImportFailed)> SyncCoreAsync(
        Series series,
        bool invalidateCaches,
        CancellationToken cancellationToken)
    {
        var seriesLock = _seriesLocks.GetOrAdd(series.Id, _ => new SemaphoreSlim(1, 1));
        await seriesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dto = new BaseItemDto
            {
                Id = series.Id,
                Name = series.Name,
                Type = BaseItemKind.Series,
                ProductionYear = series.ProductionYear,
                ProviderIds = new Dictionary<string, string>(series.ProviderIds, StringComparer.OrdinalIgnoreCase),
                Genres = series.Genres
            };
            var imdb = dto.ProviderIds.GetValueOrDefault("Imdb");
            if (Config.CatalogProvider == CatalogProvider.StremioAddon && !string.IsNullOrEmpty(imdb))
            {
                _itemCache.StoreCatalogSource(series.Id, CatalogSource.Stremio);
            }

            if (invalidateCaches)
            {
                if (!string.IsNullOrEmpty(imdb))
                {
                    _stremio.InvalidateMeta("series", imdb);
                }

                if (int.TryParse(dto.ProviderIds.GetValueOrDefault("Tvdb"), out var tvdbId))
                {
                    _tvdb.InvalidateSeries(tvdbId);
                }
            }

            var enriched = await _searchResultFactory.EnrichSeriesDtoAsync(dto, cancellationToken).ConfigureAwait(false);
            var episodes = _itemCache.GetEpisodesForSeries(series.Id);
            if (episodes == null || episodes.Count == 0)
            {
                throw new InvalidOperationException("The catalog returned no episodes");
            }

            var written = await _persistence.WriteNewEpisodesAsync(enriched, series.Path, episodes, cancellationToken).ConfigureAwait(false);
            var importFailed = false;
            if (written.Count > 0)
            {
                var imported = await _persistence.ImportNewEpisodesAsync(series.Path, episodes, cancellationToken).ConfigureAwait(false);
                importFailed = imported == null;
                _logger.LogInformation(
                    "[EpisodeSync] {Name}: {Count} new episode(s) ({Mode})",
                    series.Name, written.Count, importFailed ? "library scan" : "imported directly");
            }

            var now = DateTime.UtcNow;
            UpdateEntry(series, entry =>
            {
                entry.Name = series.Name;
                entry.LastCheckedUtc = now;
                entry.Status = enriched.Status ?? entry.Status;
                entry.NextAirDateUtc = episodes
                    .Where(episode => episode.ParentIndexNumber is > 0 && episode.PremiereDate > now)
                    .Select(episode => (DateTime?)episode.PremiereDate!.Value.ToUniversalTime())
                    .Min();
                entry.KnownEpisodeCount = episodes.Count(episode => episode.ParentIndexNumber is > 0);
                entry.ConsecutiveFailures = 0;
                entry.LastError = null;
                if (written.Count > 0)
                {
                    entry.LastNewEpisodeUtc = now;
                }
            });
            return (new EpisodeSyncSeriesResult(series.Id, series.Name ?? string.Empty, written.Count, true, null), importFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[EpisodeSync] {Name}: episode check failed ({ErrorType}: {Message})", series.Name, ex.GetType().Name, ex.Message);
            UpdateEntry(series, entry =>
            {
                entry.Name = series.Name;
                entry.LastCheckedUtc = DateTime.UtcNow;
                entry.ConsecutiveFailures++;
                entry.LastError = ex.GetType().Name;
            });
            return (new EpisodeSyncSeriesResult(series.Id, series.Name ?? string.Empty, 0, true, ex.GetType().Name), false);
        }
        finally
        {
            seriesLock.Release();
        }
    }

    private bool TryQueueLibraryScan()
    {
        try
        {
            _persistence.TriggerLibraryScan();
            return Config.TriggerLibraryScan;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[EpisodeSync] Could not queue a library scan ({ErrorType})", ex.GetType().Name);
            return false;
        }
    }

    private static bool IsMediarowFolder(string seriesPath)
    {
        try
        {
            var strm = Directory.EnumerateFiles(seriesPath, "*.strm", SearchOption.AllDirectories).FirstOrDefault();
            if (strm == null)
            {
                return false;
            }

            using var reader = new StreamReader(strm);
            return StrmMarker.IsMarker(reader.ReadLine());
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void UpdateEntry(Series series, Action<EpisodeSyncEntry> update)
    {
        lock (_stateLock)
        {
            if (!_state.TryGetValue(series.Id, out var entry))
            {
                entry = new EpisodeSyncEntry();
                _state[series.Id] = entry;
            }

            update(entry);
        }
    }

    private static EpisodeSyncEntry Clone(EpisodeSyncEntry entry) => new()
    {
        Name = entry.Name,
        LastCheckedUtc = entry.LastCheckedUtc,
        LastNewEpisodeUtc = entry.LastNewEpisodeUtc,
        NextAirDateUtc = entry.NextAirDateUtc,
        Status = entry.Status,
        KnownEpisodeCount = entry.KnownEpisodeCount,
        ConsecutiveFailures = entry.ConsecutiveFailures,
        LastError = entry.LastError
    };

    private void Load()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<Guid, EpisodeSyncEntry>>(File.ReadAllText(_statePath));
                if (loaded != null)
                {
                    _state = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[EpisodeSync] Could not load sync state ({ErrorType}); starting fresh", ex.GetType().Name);
        }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_stateLock)
            {
                json = JsonSerializer.Serialize(_state, JsonOptions);
            }

            lock (_saveLock)
            {
                var temp = _statePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _statePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[EpisodeSync] Could not save sync state ({ErrorType})", ex.GetType().Name);
        }
    }
}
