using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Service for persisting dynamic items as .strm files in the filesystem.
/// </summary>
public class PersistenceService
{
    private readonly ILibraryManager _libraryManager;
    private readonly DynamicItemCache _itemCache;
    private readonly ILogger<PersistenceService> _logger;
    private readonly IProviderManager? _providerManager;
    private readonly IFileSystem? _fileSystem;

    // Invalid filesystem characters
    private static readonly Regex InvalidCharsRegex = new(@"[<>:""/\\|?*]", RegexOptions.Compiled);
    private static readonly Regex SeasonFolderRegex = new(@"^Season (\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public PersistenceService(
        ILibraryManager libraryManager,
        DynamicItemCache itemCache,
        ILogger<PersistenceService> logger,
        IProviderManager? providerManager = null,
        IFileSystem? fileSystem = null)
    {
        _libraryManager = libraryManager;
        _itemCache = itemCache;
        _logger = logger;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    protected virtual PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Persist a movie as a .strm file.
    /// </summary>
    /// <returns>The path to the created folder, or null if already exists or failed.</returns>
    public async Task<string?> PersistMovieAsync(BaseItemDto movie, CancellationToken cancellationToken = default)
    {
        if (movie.Type != BaseItemKind.Movie)
        {
            _logger.LogDebug("[PersistenceService] Item is not a movie: {Name} ({Type})", movie.Name, movie.Type);
            return null;
        }

        // Check if already exists in library
        var imdbId = movie.ProviderIds?.GetValueOrDefault("Imdb");
        var tmdbId = movie.ProviderIds?.GetValueOrDefault("Tmdb");

        if (FindExistingLibraryItem(movie) != null)
        {
            _logger.LogDebug("[PersistenceService] Movie already exists in library: {Name} (IMDB: {ImdbId})", movie.Name, imdbId);
            return null;
        }

        // Check release date if CreateUnreleasedMedia is disabled
        if (!Config.CreateUnreleasedMedia)
        {
            var releaseDate = movie.PremiereDate;
            if (releaseDate == null || releaseDate > DateTime.UtcNow)
            {
                _logger.LogDebug("[PersistenceService] Movie not yet released, skipping: {Name} (Release: {Date})",
                    movie.Name, releaseDate?.ToString("yyyy-MM-dd") ?? "unknown");
                return null;
            }
        }

        ValidatePersistence(movie);

        // Build folder and file paths
        var year = movie.ProductionYear?.ToString() ?? "Unknown";
        var title = SanitizeFileName(movie.Name ?? "Unknown");
        var providerId = GetProviderIdTag(movie);

        var folderName = $"{title} ({year}) {providerId}";

        var folderPath = Path.Combine(Config.PersistentLibraryPath, "movies", folderName);
        var filePath = GetMovieFilePath(movie);

        // Create folder
        Directory.CreateDirectory(folderPath);

        // Build .strm content with placeholder URL
        // The mediarow:// placeholder identifies the title; real streams are resolved at playback time
        var strmContent = $"{StrmMarker.Scheme}movie/{imdbId ?? tmdbId}";

        // Write .strm file
        await WriteFileAtomicallyAsync(filePath, strmContent, cancellationToken);
        await WriteMetadataAsync(Path.ChangeExtension(filePath, ".nfo"), movie, "movie", cancellationToken);

        _logger.LogInformation("[PersistenceService] Created movie: {Path}", filePath);
        return folderPath;
    }

    /// <summary>
    /// Persist a TV series with all its episodes as .strm files.
    /// </summary>
    /// <returns>The path to the created folder, or null if already exists or failed.</returns>
    public async Task<string?> PersistSeriesAsync(BaseItemDto series, CancellationToken cancellationToken = default)
    {
        if (series.Type != BaseItemKind.Series)
        {
            _logger.LogDebug("[PersistenceService] Item is not a series: {Name} ({Type})", series.Name, series.Type);
            return null;
        }

        // Check if already exists in library
        var imdbId = series.ProviderIds?.GetValueOrDefault("Imdb");
        var tvdbId = series.ProviderIds?.GetValueOrDefault("Tvdb");

        if (FindExistingLibraryItem(series) != null)
        {
            _logger.LogDebug("[PersistenceService] Series already exists in library: {Name} (IMDB: {ImdbId})", series.Name, imdbId);
            return null;
        }

        // Get episodes from cache
        var episodes = _itemCache.GetEpisodesForSeries(series.Id);
        if (episodes == null || episodes.Count == 0)
        {
            _logger.LogInformation("[PersistenceService] No episodes found for series: {Name}", series.Name);
            return null;
        }

        // Filter to only aired episodes if CreateUnreleasedMedia is disabled
        if (!Config.CreateUnreleasedMedia)
        {
            var now = DateTime.UtcNow;
            episodes = episodes
                .Where(e => e.PremiereDate.HasValue && e.PremiereDate.Value <= now)
                .ToList();

            if (episodes.Count == 0)
            {
                _logger.LogDebug("[PersistenceService] No aired episodes for series, skipping: {Name}", series.Name);
                return null;
            }

            _logger.LogDebug("[PersistenceService] Filtered to {Count} aired episodes for series: {Name}",
                episodes.Count, series.Name);
        }

        ValidatePersistence(series);

        // Build folder path
        var year = series.ProductionYear?.ToString() ?? "Unknown";
        var title = SanitizeFileName(series.Name ?? "Unknown");
        var providerId = GetProviderIdTag(series);
        var isAnime = MediarowService.IsAnime(series);

        var subFolder = isAnime ? "anime" : "tv";
        var folderName = $"{title} ({year}) {providerId}";
        var seriesPath = Path.Combine(Config.PersistentLibraryPath, subFolder, folderName);

        // Create series folder
        Directory.CreateDirectory(seriesPath);
        await WriteMetadataAsync(Path.Combine(seriesPath, "tvshow.nfo"), series, "tvshow", cancellationToken);

        var seriesIdForUrl = imdbId ?? tvdbId;

        // Group episodes by season
        var episodesBySeason = episodes
            .Where(e => e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue)
            .GroupBy(e => e.ParentIndexNumber!.Value)
            .OrderBy(g => g.Key);

        var persistedCount = 0;
        foreach (var seasonGroup in episodesBySeason)
        {
            var seasonNumber = seasonGroup.Key;
            var seasonFolder = $"Season {seasonNumber:D2}";
            var seasonPath = Path.Combine(seriesPath, seasonFolder);

            Directory.CreateDirectory(seasonPath);
            await WriteSeasonMetadataAsync(seasonPath, seasonNumber, cancellationToken);

            foreach (var episode in seasonGroup.OrderBy(e => e.IndexNumber))
            {
                await PersistEpisodeAsync(
                    episode,
                    series,
                    seasonPath,
                    seasonNumber,
                    seriesIdForUrl!,
                    isAnime,
                    cancellationToken);
                persistedCount++;
            }
        }

        _logger.LogInformation("[PersistenceService] Created series with {Count} episodes: {Path}",
            persistedCount, seriesPath);
        return seriesPath;
    }

    /// <summary>
    /// Adds newly persisted files to Jellyfin. Series are imported directly with their whole episode tree, so their
    /// page opens without waiting for a full library scan; movies (and series in non-standard library layouts) use a scan.
    /// </summary>
    /// <param name="source">The catalog title, whose cached episode metadata is applied to the imported episodes.</param>
    public async Task AddToLibraryAsync(string createdPath, BaseItemKind type, BaseItemDto? source = null)
    {
        if (!Config.TriggerLibraryScan)
        {
            _logger.LogDebug("[PersistenceService] Library scan disabled in config");
            return;
        }

        if (type == BaseItemKind.Series)
        {
            var catalogEpisodes = source == null ? null : _itemCache.GetEpisodesForSeries(source.Id);
            if (await TryImportSeriesAsync(createdPath, catalogEpisodes).ConfigureAwait(false))
            {
                return;
            }

            TriggerLibraryScan();
            if (_providerManager != null && _fileSystem != null)
            {
                _ = Task.Run(() => RefreshSeriesAfterScanAsync(createdPath));
            }

            return;
        }

        TriggerLibraryScan();
    }

    /// <summary>
    /// Creates the series, its seasons and every episode in one pass, with numbers, parent links and catalog
    /// metadata set in memory (the approach Gelato uses), so the whole show is browsable right away.
    /// Reading the NFOs and fetching online metadata happen in the background afterwards.
    /// </summary>
    private async Task<bool> TryImportSeriesAsync(string seriesPath, IReadOnlyList<BaseItemDto>? catalogEpisodes)
    {
        if (_fileSystem == null || _providerManager == null)
        {
            return false;
        }

        try
        {
            // Library views are virtual; series belong under the physical folder each view points at.
            var libraryPath = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(seriesPath) ?? string.Empty);
            if (_libraryManager.FindByPath(libraryPath, true) is not Folder libraryFolder || libraryFolder is CollectionFolder)
            {
                _logger.LogInformation("[PersistenceService] No library points directly at the series folder's parent; using a library scan");
                return false;
            }

            var directoryService = new DirectoryService(_fileSystem);
            if (_libraryManager.FindByPath(seriesPath, true) is not Series series)
            {
                series = _libraryManager.ResolvePath(_fileSystem.GetDirectoryInfo(seriesPath), libraryFolder, directoryService, CollectionType.tvshows) as Series;
                if (series == null)
                {
                    return false;
                }

                libraryFolder.AddChild(series);
            }

            // Non-recursive: creates every season and reads the locked series/season NFOs, but no episodes yet.
            await series.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(directoryService), recursive: false).ConfigureAwait(false);
            var seasons = _libraryManager.GetItemsResult(new InternalItemsQuery
                {
                    ParentId = series.Id, IncludeItemTypes = [BaseItemKind.Season]
                }).Items
                .Select(season => _libraryManager.GetItemById(season.Id)).OfType<Season>()
                .OrderBy(season => season.IndexNumber is null or 0 ? int.MaxValue : season.IndexNumber)
                .ToList();

            // New episodes inherit their parents' lock, and locked items skip even their own NFO (no names or
            // numbers). Unlock the parents without saving NFOs; the locked files keep online lookups away until
            // RefreshSeriesOnlineAsync runs.
            foreach (var parent in seasons.Cast<BaseItem>().Prepend(series).Where(parent => parent.IsLocked))
            {
                parent.IsLocked = false;
                await _libraryManager.UpdateItemAsync(parent, parent.GetParent(), ItemUpdateType.None, CancellationToken.None).ConfigureAwait(false);
            }

            var created = 0;
            foreach (var season in seasons)
            {
                created += CreateSeasonEpisodes(series, season, catalogEpisodes);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var season in seasons)
                    {
                        await RefreshSeasonAsync(season).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[PersistenceService] Reading season metadata failed ({ErrorType}); queuing a library scan", ex.GetType().Name);
                    TriggerLibraryScan();
                    await RefreshSeriesAfterScanAsync(seriesPath).ConfigureAwait(false);
                    return;
                }

                await RefreshSeriesOnlineAsync(seriesPath).ConfigureAwait(false);
            });

            _logger.LogInformation("[PersistenceService] Imported {Name}: {Seasons} season(s), {Episodes} episode(s)",
                series.Name, seasons.Count, created);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[PersistenceService] Direct series import failed ({ErrorType}); falling back to a library scan", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Creates a season's episodes with season/episode numbers, series and season links and catalog metadata already
    /// set. Saved without numbers, episodes sort before S1E1 and briefly become "Next Up"; saved without a series link,
    /// they are missing from the show.
    /// </summary>
    private int CreateSeasonEpisodes(Series series, Season season, IReadOnlyList<BaseItemDto>? catalogEpisodes)
    {
        var directoryService = new DirectoryService(_fileSystem!);
        var existing = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            ParentId = season.Id, IncludeItemTypes = [BaseItemKind.Episode]
        }).Items.Select(item => item.Id).ToHashSet();
        var episodes = _libraryManager.ResolvePaths(
                directoryService.GetFileSystemEntries(season.Path), directoryService, season,
                _libraryManager.GetLibraryOptions(season), CollectionType.tvshows)
            .OfType<Episode>()
            .Where(episode => !existing.Contains(episode.Id))
            .ToList();
        foreach (var episode in episodes)
        {
            _libraryManager.FillMissingEpisodeNumbersFromPath(episode, false);
            episode.ParentIndexNumber ??= season.IndexNumber;
            episode.SeriesId = series.Id;
            episode.SeriesName = series.Name;
            episode.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
            episode.SeasonId = season.Id;
            episode.SeasonName = season.Name;
            // Episode lists group by this key; left empty, all new episodes collapse into one entry.
            episode.PresentationUniqueKey = episode.CreatePresentationUniqueKey();
            var metadata = catalogEpisodes?.FirstOrDefault(candidate =>
                candidate.ParentIndexNumber == episode.ParentIndexNumber && candidate.IndexNumber == episode.IndexNumber);
            if (metadata != null)
            {
                ApplyCatalogFields(episode, metadata);
            }
        }

        if (episodes.Count > 0)
        {
            _libraryManager.CreateItems(episodes, season, CancellationToken.None);
        }

        return episodes.Count;
    }

    /// <summary>
    /// Reads a season's NFOs and local artwork once its episodes exist, and re-links any episode that lost its series.
    /// </summary>
    private async Task RefreshSeasonAsync(Season season)
    {
        var directoryService = new DirectoryService(_fileSystem!);
        await season.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(directoryService), recursive: true).ConfigureAwait(false);

        // Rarely an episode is saved before its series link resolves; it then disappears from the show and Next Up.
        var orphans = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            ParentId = season.Id, IncludeItemTypes = [BaseItemKind.Episode]
        }).Items.OfType<Episode>().Where(episode => episode.SeriesId == Guid.Empty).ToList();
        foreach (var episode in orphans)
        {
            await episode.RefreshMetadata(new MetadataRefreshOptions(directoryService) { ForceSave = true }, CancellationToken.None).ConfigureAwait(false);
        }

        if (orphans.Count > 0)
        {
            _logger.LogInformation("[PersistenceService] Re-linked {Count} episode(s) of {Season}", orphans.Count, season.Name);
        }
    }

    /// <summary>
    /// Returns the season shown first on the details page and how many episode files it has, so callers can wait
    /// for just that season instead of the whole series scan.
    /// </summary>
    public (int Season, int EpisodeCount)? GetFirstSeasonFileCount(BaseItemDto series)
    {
        var seriesPath = GetSeriesFolderPath(series);
        if (!Directory.Exists(seriesPath))
        {
            return null;
        }

        var seasons = Directory.EnumerateDirectories(seriesPath)
            .Select(path => (Path: path, Match: SeasonFolderRegex.Match(Path.GetFileName(path))))
            .Where(entry => entry.Match.Success)
            .Select(entry => (entry.Path, Number: int.Parse(entry.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(entry => entry.Number == 0 ? int.MaxValue : entry.Number)
            .ToList();
        if (seasons.Count == 0)
        {
            return null;
        }

        return (seasons[0].Number, Directory.EnumerateFiles(seasons[0].Path, "*.strm").Count());
    }

    /// <summary>
    /// Fallback for series added by a full library scan: wait for the scan, then fetch online metadata.
    /// Refreshing while the scan still runs lets the scan save its stale, locked copy over ours.
    /// </summary>
    private async Task RefreshSeriesAfterScanAsync(string seriesPath)
    {
        try
        {
            var expectedEpisodes = Directory.EnumerateFiles(seriesPath, "*.strm", SearchOption.AllDirectories).Count();
            Series? series = null;
            var idlePolls = 0;
            for (var attempt = 0; attempt < 300; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                series ??= _libraryManager.FindByPath(seriesPath, true) as Series;
                if (series == null || _libraryManager.IsScanRunning)
                {
                    idlePolls = 0;
                    continue;
                }

                var count = _libraryManager.GetItemsResult(new InternalItemsQuery
                {
                    ParentId = series.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Episode]
                }).Items.Count;
                if (count >= expectedEpisodes || ++idlePolls >= 3)
                {
                    break;
                }
            }

            if (series == null || _libraryManager.IsScanRunning)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[PersistenceService] Waiting for the series scan failed ({ErrorType})", ex.GetType().Name);
            return;
        }

        await RefreshSeriesOnlineAsync(seriesPath).ConfigureAwait(false);
    }

    /// <summary>
    /// Series and season NFOs are written locked so the import reaches episodes in seconds.
    /// Afterwards, unlock them and fetch cast, artwork and season posters from online providers.
    /// </summary>
    private async Task RefreshSeriesOnlineAsync(string seriesPath)
    {
        try
        {
            // Re-read the series so the catalog name/artwork applied by the resolver is kept.
            if (_libraryManager.FindByPath(seriesPath, true) is not Series series)
            {
                return;
            }

            await RemoveLockAsync(Path.Combine(seriesPath, "tvshow.nfo")).ConfigureAwait(false);
            var seasons = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                ParentId = series.Id, IncludeItemTypes = [BaseItemKind.Season]
            }).Items;
            foreach (var season in seasons)
            {
                await RemoveLockAsync(Path.Combine(season.Path ?? string.Empty, "season.nfo")).ConfigureAwait(false);
            }

            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem!))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh
            };
            foreach (var item in seasons.Prepend(series))
            {
                item.IsLocked = false;
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                await _providerManager!.RefreshSingleItem(item, options, CancellationToken.None).ConfigureAwait(false);
            }

            _logger.LogInformation("[PersistenceService] Loaded online metadata for series {Name}", series.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[PersistenceService] Background series metadata refresh failed ({ErrorType})", ex.GetType().Name);
        }
    }

    private static async Task RemoveLockAsync(string nfoPath)
    {
        if (!File.Exists(nfoPath))
        {
            return;
        }

        var document = XDocument.Load(nfoPath);
        var locks = document.Root?.Elements("lockdata").ToList();
        if (locks is { Count: > 0 })
        {
            locks.ForEach(element => element.Remove());
            await WriteFileAtomicallyAsync(nfoPath, document.ToString(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Task WriteSeasonMetadataAsync(string seasonPath, int seasonNumber, CancellationToken cancellationToken)
    {
        var path = Path.Combine(seasonPath, "season.nfo");
        if (File.Exists(path))
        {
            return Task.CompletedTask;
        }

        var root = new XElement("season",
            new XElement("title", seasonNumber == 0 ? "Specials" : $"Season {seasonNumber}"),
            new XElement("seasonnumber", seasonNumber),
            new XElement("lockdata", "true"));
        return WriteFileAtomicallyAsync(path, new XDocument(root).ToString(), cancellationToken);
    }

    /// <summary>
    /// Persist a single episode as a .strm file.
    /// </summary>
    private async Task PersistEpisodeAsync(
        BaseItemDto episode,
        BaseItemDto series,
        string seasonPath,
        int seasonNumber,
        string seriesIdForUrl,
        bool isAnime,
        CancellationToken cancellationToken)
    {
        var episodeNumber = episode.IndexNumber ?? 1;
        var seriesTitle = SanitizeFileName(series.Name ?? "Unknown");
        var episodeTitle = SanitizeFileName(episode.Name ?? $"Episode {episodeNumber}");

        // Format: "Series Name - S01E01 - Episode Title.strm"
        var fileName = $"{seriesTitle} - S{seasonNumber:D2}E{episodeNumber:D2} - {episodeTitle}.strm";
        var filePath = Path.Combine(seasonPath, fileName);

        // Build .strm content with placeholder URL
        // The mediarow:// placeholder identifies the episode; real streams are resolved at playback time
        string strmContent;
        if (isAnime)
        {
            var anilistId = series.ProviderIds?.GetValueOrDefault("AniList") ?? seriesIdForUrl;
            // Single .strm file - audio version selection happens at playback time via MediaSources
            strmContent = $"{StrmMarker.Scheme}anime/{anilistId}/{episodeNumber}";
        }
        else
        {
            strmContent = $"{StrmMarker.Scheme}tv/{seriesIdForUrl}/{seasonNumber}/{episodeNumber}";
        }

        // Append Stremio source mapping if available (for mapped official order items)
        if (episode.ProviderIds != null)
        {
            var stremioS = episode.ProviderIds.GetValueOrDefault("StremioSeason");
            var stremioE = episode.ProviderIds.GetValueOrDefault("StremioEpisode");
            if (!string.IsNullOrEmpty(stremioS) && !string.IsNullOrEmpty(stremioE))
            {
                // Determine separator (? or &) based on existing URL
                var separator = strmContent.Contains('?') ? "&" : "?";
                strmContent += $"{separator}stremioS={stremioS}&stremioE={stremioE}";
            }
        }

        await WriteFileAtomicallyAsync(filePath, strmContent, cancellationToken);
        await WriteMetadataAsync(Path.ChangeExtension(filePath, ".nfo"), episode, "episodedetails", cancellationToken);
        _logger.LogDebug("[PersistenceService] Created episode: {Path}", filePath);
    }

    private void ValidatePersistence(BaseItemDto item)
    {
        if (!Config.EnablePersistence || !Path.IsPathFullyQualified(Config.PersistentLibraryPath))
        {
            throw new InvalidOperationException("Persistence requires an enabled, absolute library folder.");
        }

        if (item.ProviderIds?.Any(pair => (pair.Key is "Imdb" or "Tmdb" or "Tvdb" or "AniList") && !string.IsNullOrWhiteSpace(pair.Value)) != true)
        {
            throw new InvalidOperationException("The title requires a metadata provider ID before persistence.");
        }

        if (Config.StreamProvider == StreamProvider.AIOStreams &&
            (item.ProviderIds?.GetValueOrDefault("Imdb") is not string imdb || !Regex.IsMatch(imdb, @"^tt[0-9]+$", RegexOptions.CultureInvariant)))
        {
            throw new InvalidOperationException("AIOStreams playback requires a movie or series IMDb ID.");
        }
    }

    private async Task WriteMetadataAsync(string path, BaseItemDto item, string elementName, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        var root = new XElement(elementName, new XElement("title", item.Name ?? "Unknown"));
        if (elementName == "tvshow" && !string.IsNullOrWhiteSpace(item.Status))
        {
            root.Add(new XElement("status", item.Status));
        }
        foreach (var pair in item.ProviderIds ?? new Dictionary<string, string>())
        {
            if (pair.Key is "Imdb" or "Tmdb" or "Tvdb" or "AniList")
            {
                root.Add(new XElement("uniqueid", new XAttribute("type", pair.Key.ToLowerInvariant()),
                    new XAttribute("default", pair.Key == "Imdb" ? "true" : "false"), pair.Value));
            }
        }

        if (item.ProductionYear.HasValue) root.Add(new XElement("year", item.ProductionYear.Value));
        if (item.PremiereDate.HasValue)
        {
            var date = item.PremiereDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            root.Add(new XElement("premiered", date), new XElement("aired", date));
        }
        if (item.RunTimeTicks > 0) root.Add(new XElement("runtime", item.RunTimeTicks.Value / TimeSpan.TicksPerMinute));
        if (!string.IsNullOrWhiteSpace(item.Overview)) root.Add(new XElement("plot", item.Overview));
        if (item.ParentIndexNumber.HasValue) root.Add(new XElement("season", item.ParentIndexNumber.Value));
        if (item.IndexNumber.HasValue) root.Add(new XElement("episode", item.IndexNumber.Value));
        foreach (var genre in item.Genres ?? []) root.Add(new XElement("genre", genre));
        var poster = _itemCache.GetImageUrl(item.Id);
        if (!string.IsNullOrWhiteSpace(poster)) root.Add(new XElement("thumb", poster));
        // Catalog NFOs already carry the title, plot, dates and artwork. Locking them stops the first library scan
        // from querying online providers for every item, which made large series take minutes to appear.
        // Series locks are removed again once the scan finishes (see CompleteSeriesMetadataAsync).
        if (elementName is "episodedetails" or "tvshow") root.Add(new XElement("lockdata", "true"));
        await WriteFileAtomicallyAsync(path, new XDocument(root).ToString(), cancellationToken);
    }

    private static async Task WriteFileAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Check if an item with the given provider ID exists in Jellyfin's library.
    /// </summary>
    public bool ItemExistsInLibrary(string providerName, string providerId)
    {
        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { { providerName, providerId } },
            Recursive = true
        };

        var result = _libraryManager.GetItemsResult(query);
        return result.TotalRecordCount > 0;
    }

    public BaseItem? FindExistingLibraryItem(BaseItemDto item)
    {
        var providerIds = item.ProviderIds;
        if (providerIds == null || providerIds.Count == 0)
        {
            return null;
        }

        var providerPriority = item.Type == BaseItemKind.Series
            ? new[] { "Imdb", "Tvdb", "Tmdb", "AniList" }
            : new[] { "Imdb", "Tmdb", "Tvdb", "AniList" };

        foreach (var providerName in providerPriority)
        {
            if (!providerIds.TryGetValue(providerName, out var providerId) || string.IsNullOrWhiteSpace(providerId))
            {
                continue;
            }

            var query = new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string> { { providerName, providerId } },
                Recursive = true
            };

            if (item.Type == BaseItemKind.Movie)
            {
                query.IncludeItemTypes = new[] { BaseItemKind.Movie };
            }
            else if (item.Type == BaseItemKind.Series)
            {
                query.IncludeItemTypes = new[] { BaseItemKind.Series };
            }

            var result = _libraryManager.GetItemsResult(query);
            var match = result.Items.FirstOrDefault();
            if (match != null)
            {
                return match;
            }
        }

        if (Path.IsPathFullyQualified(Config.PersistentLibraryPath))
        {
            var path = item.Type == BaseItemKind.Series ? GetSeriesFolderPath(item) : GetMovieFilePath(item);
            var byPath = _libraryManager.FindByPath(path, item.Type == BaseItemKind.Series);
            if (byPath?.GetBaseItemKind() == item.Type) return byPath;
        }
        return null;
    }

    public string GetMovieFilePath(BaseItemDto movie)
    {
        var year = movie.ProductionYear?.ToString() ?? "Unknown";
        var title = SanitizeFileName(movie.Name ?? "Unknown");
        return Path.Combine(Config.PersistentLibraryPath, "movies", $"{title} ({year}) {GetProviderIdTag(movie)}", $"{title} ({year}).strm");
    }

    public async Task ApplyCatalogMetadataAsync(BaseItem target, BaseItemDto source, CancellationToken cancellationToken)
    {
        ApplyCatalogFields(target, source);
        await _libraryManager.UpdateItemAsync(target, target.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
    }

    private void ApplyCatalogFields(BaseItem target, BaseItemDto source)
    {
        target.Name = source.Name ?? target.Name;
        target.Overview = source.Overview ?? target.Overview;
        target.PremiereDate = source.PremiereDate ?? target.PremiereDate;
        target.ProductionYear = source.ProductionYear ?? target.ProductionYear;
        target.CommunityRating = source.CommunityRating ?? target.CommunityRating;
        target.Genres = source.Genres ?? target.Genres;
        if (source.RunTimeTicks > 0) target.RunTimeTicks = source.RunTimeTicks;
        foreach (var pair in source.ProviderIds ?? [])
        {
            if (pair.Key is "Imdb" or "Tmdb" or "Tvdb" or "AniList") target.SetProviderId(pair.Key, pair.Value);
        }

        var poster = _itemCache.GetImageUrl(source.Id);
        if (!string.IsNullOrWhiteSpace(poster))
        {
            target.SetImage(new ItemImageInfo { Path = poster, Type = ImageType.Primary, DateModified = DateTime.UtcNow }, 0);
        }
        var backdrop = _itemCache.GetBackdropUrl(source.Id);
        if (!string.IsNullOrWhiteSpace(backdrop))
        {
            target.SetImage(new ItemImageInfo { Path = backdrop, Type = ImageType.Backdrop, DateModified = DateTime.UtcNow }, 0);
        }
    }

    /// <summary>
    /// Get the provider ID tag for folder naming (e.g., "[imdbid-tt1234567]").
    /// </summary>
    private static string GetProviderIdTag(BaseItemDto item)
    {
        var providerIds = item.ProviderIds;
        if (providerIds == null)
        {
            return "";
        }

        // Check for AniList first (for anime)
        if (providerIds.TryGetValue("AniList", out var anilistId) && !string.IsNullOrEmpty(anilistId))
        {
            return $"[anilistid-{SanitizeFileName(anilistId)}]";
        }

        // Then IMDB
        if (providerIds.TryGetValue("Imdb", out var imdbId) && !string.IsNullOrEmpty(imdbId))
        {
            return $"[imdbid-{SanitizeFileName(imdbId)}]";
        }

        // Then TVDB
        if (providerIds.TryGetValue("Tvdb", out var tvdbId) && !string.IsNullOrEmpty(tvdbId))
        {
            return $"[tvdbid-{SanitizeFileName(tvdbId)}]";
        }

        // Then TMDB
        if (providerIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
        {
            return $"[tmdbid-{SanitizeFileName(tmdbId)}]";
        }

        return "";
    }

    /// <summary>
    /// Sanitize a string for use in file/folder names.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unknown";
        }

        // Remove invalid characters
        var sanitized = InvalidCharsRegex.Replace(name, "");

        // Trim whitespace and dots from ends
        sanitized = sanitized.Trim().TrimEnd('.');

        // Ensure not empty after sanitization
        return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
    }

    /// <summary>
    /// Trigger a library scan for the specified path.
    /// </summary>
    public void TriggerLibraryScan()
    {
        if (!Config.TriggerLibraryScan)
        {
            _logger.LogDebug("[PersistenceService] Library scan disabled in config");
            return;
        }

        try
        {
            // Queue a library scan
            _libraryManager.QueueLibraryScan();
            _logger.LogInformation("[PersistenceService] Triggered library scan");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PersistenceService] Failed to trigger library scan");
            throw;
        }
    }

    /// <summary>
    /// Get the folder path for a persisted series.
    /// </summary>
    public string GetSeriesFolderPath(BaseItemDto series)
    {
        var year = series.ProductionYear?.ToString() ?? "Unknown";
        var title = SanitizeFileName(series.Name ?? "Unknown");
        var providerId = GetProviderIdTag(series);
        var isAnime = MediarowService.IsAnime(series);

        var subFolder = isAnime ? "anime" : "tv";
        var folderName = $"{title} ({year}) {providerId}";
        return Path.Combine(Config.PersistentLibraryPath, subFolder, folderName);
    }

    /// <summary>
    /// Check for and persist any new episodes for an existing persisted series.
    /// </summary>
    /// <returns>The number of new episodes added.</returns>
    public async Task<int> UpdateSeriesAsync(BaseItemDto series, CancellationToken cancellationToken = default)
    {
        if (series.Type != BaseItemKind.Series)
        {
            return 0;
        }

        // Get cached episodes (already fetched by EnrichSeriesDtoAsync)
        var cachedEpisodes = _itemCache.GetEpisodesForSeries(series.Id);
        if (cachedEpisodes == null || cachedEpisodes.Count == 0)
        {
            _logger.LogDebug("[PersistenceService] No cached episodes for series: {Name}", series.Name);
            return 0;
        }

        // Get series folder path
        var seriesPath = GetSeriesFolderPath(series);
        if (!Directory.Exists(seriesPath))
        {
            _logger.LogDebug("[PersistenceService] Series not persisted yet: {Name}", series.Name);
            return 0;
        }

        // Get existing .strm files on disk
        var existingFiles = Directory.GetFiles(seriesPath, "*.strm", SearchOption.AllDirectories);
        var existingEpisodes = ParseExistingEpisodes(existingFiles);

        // Find new episodes (skip specials - season 0, filter to aired only)
        var now = DateTime.UtcNow;
        var newEpisodes = cachedEpisodes
            .Where(e => e.ParentIndexNumber.HasValue && e.ParentIndexNumber > 0)
            .Where(e => e.IndexNumber.HasValue)
            .Where(e => e.PremiereDate.HasValue && e.PremiereDate.Value <= now) // Only aired episodes
            .Where(e => !existingEpisodes.Contains((e.ParentIndexNumber!.Value, e.IndexNumber!.Value)))
            .ToList();

        if (newEpisodes.Count == 0)
        {
            _logger.LogDebug("[PersistenceService] No new episodes for series: {Name}", series.Name);
            return 0;
        }

        _logger.LogInformation("[PersistenceService] Found {Count} new episodes for series: {Name}",
            newEpisodes.Count, series.Name);

        // Get series info for persistence
        var imdbId = series.ProviderIds?.GetValueOrDefault("Imdb");
        var tvdbId = series.ProviderIds?.GetValueOrDefault("Tvdb");
        var seriesIdForUrl = imdbId ?? tvdbId;
        var isAnime = MediarowService.IsAnime(series);

        // Persist each new episode
        foreach (var episode in newEpisodes)
        {
            var seasonNumber = episode.ParentIndexNumber!.Value;
            var seasonFolder = $"Season {seasonNumber:D2}";
            var seasonPath = Path.Combine(seriesPath, seasonFolder);

            // Create season folder if needed
            Directory.CreateDirectory(seasonPath);

            await PersistEpisodeAsync(
                episode,
                series,
                seasonPath,
                seasonNumber,
                seriesIdForUrl!,
                isAnime,
                cancellationToken);
        }

        _logger.LogInformation("[PersistenceService] Added {Count} new episodes for series: {Name}",
            newEpisodes.Count, series.Name);

        return newEpisodes.Count;
    }

    /// <summary>
    /// Parse existing .strm files to get a set of (season, episode) tuples.
    /// </summary>
    private static HashSet<(int Season, int Episode)> ParseExistingEpisodes(string[] files)
    {
        var episodes = new HashSet<(int, int)>();

        // Pattern matches "S01E01" or "S1E1" format
        var regex = new Regex(@"S(\d+)E(\d+)", RegexOptions.IgnoreCase);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var match = regex.Match(fileName);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var season) &&
                int.TryParse(match.Groups[2].Value, out var episode))
            {
                episodes.Add((season, episode));
            }
        }

        return episodes;
    }
}
