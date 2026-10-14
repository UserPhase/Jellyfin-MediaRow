using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Providers;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Filters;

/// <summary>
/// Filter that intercepts item lookup requests and returns cached data
/// for dynamic (virtual) items that don't exist in Jellyfin's database.
/// Also triggers Embedarr/Persistence to add items to the library when user views item details.
/// </summary>
public class ItemLookupFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly MediarowService _mediarowService;
    private readonly PersistenceService _persistenceService;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly VirtualResumeTracker _virtualResumeTracker;
    private readonly ILogger<ItemLookupFilter> _logger;
    private readonly DynamicAioStreamsMediaSourceProvider _nativeAioProvider;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly PlaybackItemResolver _playbackResolver;

    // Action names that look up individual items
    // UserLibrary.GetItem = /Users/{userId}/Items/{itemId}
    // UserLibrary.GetItemLegacy = legacy endpoint, same URL pattern
    // Items.GetItem = /Items/{itemId} (may not exist)
    private static readonly HashSet<string> ItemLookupActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetItem",
        "GetItemLegacy",
        "GetItemByNameType",
    };

    // Controllers that have item lookup
    private static readonly HashSet<string> ItemLookupControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserLibrary",
        "Items",
    };

    public ItemLookupFilter(
        DynamicItemCache itemCache,
        SearchResultFactory searchResultFactory,
        MediarowService mediarowService,
        PersistenceService persistenceService,
        IServerApplicationHost serverApplicationHost,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        VirtualResumeTracker virtualResumeTracker,
        ILogger<ItemLookupFilter> logger,
        DynamicAioStreamsMediaSourceProvider nativeAioProvider,
        IMediaSourceManager mediaSourceManager,
        PlaybackItemResolver playbackResolver)
    {
        _itemCache = itemCache;
        _searchResultFactory = searchResultFactory;
        _mediarowService = mediarowService;
        _persistenceService = persistenceService;
        _serverApplicationHost = serverApplicationHost;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _virtualResumeTracker = virtualResumeTracker;
        _logger = logger;
        _nativeAioProvider = nativeAioProvider;
        _mediaSourceManager = mediaSourceManager;
        _playbackResolver = playbackResolver;
    }

    // Run before other filters
    public int Order => 0;

    // Details pages wait for AIOStreams at most until this point of the request (cached lookups return instantly).
    private static readonly TimeSpan DetailsStreamBudget = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan MinimumStreamWait = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Builds the version list for a persisted item and warms the probe of its first stream. Runs without the
    /// request's cancellation so a details page that stops waiting still leaves the result cached for Play.
    /// </summary>
    private async Task<MediaSourceInfo[]> LoadStreamChoicesAsync(MediaBrowser.Controller.Entities.BaseItem libraryItem)
    {
        var candidates = (await _nativeAioProvider.GetMediaSources(libraryItem, CancellationToken.None).ConfigureAwait(false)).ToArray();
        var nativeSources = await _mediaSourceManager.GetPlaybackMediaSources(
            libraryItem, null!, false, false, CancellationToken.None).ConfigureAwait(false);
        var sources = nativeSources.Where(source => candidates.Any(candidate => candidate.Id == source.Id)).ToArray();
        _nativeAioProvider.WarmProbe(candidates.FirstOrDefault(candidate => candidate.Id == sources.FirstOrDefault()?.Id));
        return sources;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var requestTimer = System.Diagnostics.Stopwatch.StartNew();
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

        // Check if this is an item lookup action on a relevant controller
        var isLookupAction = actionName != null &&
            ItemLookupActions.Contains(actionName) &&
            controllerName != null &&
            ItemLookupControllers.Contains(controllerName);

        if (!isLookupAction)
        {
            await next();
            return;
        }

        // Try to get the item ID from the action arguments
        if (!context.ActionArguments.TryGetValue("itemId", out var itemIdObj) || itemIdObj is not Guid itemId)
        {
            await next();
            return;
        }

        // Try to get the user ID from the action arguments (for UserLibrary endpoints)
        Guid? userId = null;
        if (context.ActionArguments.TryGetValue("userId", out var userIdObj) && userIdObj is Guid extractedUserId)
        {
            userId = extractedUserId;
        }

        // Check if this is a dynamic item in our cache
        var cachedItem = _itemCache.GetItem(itemId);
        var requestedMediaSourceId = (string?)null;

        var playbackConfig = MediarowPlugin.Instance?.Configuration;
        if (playbackConfig?.StreamProvider == StreamProvider.AIOStreams &&
            cachedItem?.Type is BaseItemKind.Movie or BaseItemKind.Series &&
            playbackConfig.EnablePersistence)
        {
            try
            {
                if (cachedItem.Type == BaseItemKind.Movie)
                {
                    _nativeAioProvider.PrefetchMovieStreams(cachedItem.ProviderIds?.GetValueOrDefault("Imdb"));
                }

                var resolved = await _playbackResolver.ResolveAsync(cachedItem, context.HttpContext.RequestAborted);
                if (resolved == null)
                {
                    context.Result = new ObjectResult(new { Error = "The title is waiting for the Jellyfin library scan. Retry shortly." }) { StatusCode = 409 };
                    return;
                }

                itemId = resolved.Id;
                context.ActionArguments["itemId"] = itemId;
                cachedItem = null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException)
            {
                _logger.LogWarning("[Mediarow] Cannot prepare details for item {Id} ({ErrorType})", itemId, ex.GetType().Name);
                context.Result = new ObjectResult(new { Error = "The title could not be prepared. Check persistence, library folders, and metadata settings." }) { StatusCode = 502 };
                return;
            }
        }

        if (cachedItem == null)
        {
            var mediaSourceIdStr = itemId.ToString("N");
            var directLibraryItem = _libraryManager.GetItemById(itemId);

            // Check if this is an AIOStreams MediaSource ID
            var aioMapping = directLibraryItem == null ? _itemCache.GetAIOStreamsMapping(mediaSourceIdStr) : null;
            if (aioMapping != null)
            {
                try
                {
                    directLibraryItem = _libraryManager.GetItemById(aioMapping.ItemId);
                    if (directLibraryItem == null || !_nativeAioProvider.IsMediarowItem(directLibraryItem) ||
                        !(await _nativeAioProvider.GetMediaSources(directLibraryItem, context.HttpContext.RequestAborted)).Any(source => source.Id == mediaSourceIdStr))
                    {
                        _logger.LogWarning("[Mediarow] Stream alias {SourceId} is unavailable", mediaSourceIdStr);
                        context.Result = new NotFoundObjectResult(new { Error = "The selected stream expired. Reload the title." });
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("[Mediarow] Stream alias preparation failed ({ErrorType})", ex.GetType().Name);
                    context.Result = new ObjectResult(new { Error = "AIOStreams could not load stream choices. Check addon settings or retry." }) { StatusCode = 502 };
                    return;
                }

                // Jellyfin Web requests a version's source ID as an item ID. Keep the real owner identity
                // so playback and watch state use the library movie/episode, not an ephemeral stream.
                itemId = directLibraryItem.Id;
                context.ActionArguments["itemId"] = itemId;
            }

            // Check if this is a MediaSource ID that maps to an episode
            var episodeId = _itemCache.GetEpisodeIdForMediaSource(mediaSourceIdStr);
            if (episodeId.HasValue)
            {
                _logger.LogWarning("[Mediarow] ItemLookup: Resolved MediaSource ID {MediaSourceId} to Episode {EpisodeId}",
                    itemId, episodeId.Value);

                // Try cache first (for dynamic items)
                cachedItem = _itemCache.GetItem(episodeId.Value);
                requestedMediaSourceId = mediaSourceIdStr;

                // If not in cache, check if it's a persisted anime episode in the library
                if (cachedItem == null)
                {
                    var libraryEpisode = _libraryManager.GetItemById(episodeId.Value) as Episode;
                    if (libraryEpisode != null && IsPersistedAnimeEpisode(libraryEpisode))
                    {
                        _logger.LogWarning("[Mediarow] ItemLookup: Found persisted anime episode for MediaSource ID");

                        // Build a BaseItemDto from the library episode
                        // We can't call next() because the request is for the MediaSource ID, not the episode ID
                        var dto = BuildBaseItemDtoFromEpisode(libraryEpisode, userId);

                        // Add MediaSources
                        await AddAnimeMediaSourcesToDto(dto, libraryEpisode, context.HttpContext.RequestAborted);

                        // Filter to just the requested MediaSource
                        if (dto.MediaSources != null && dto.MediaSources.Length > 0)
                        {
                            var selectedSource = dto.MediaSources.FirstOrDefault(s => s.Id == requestedMediaSourceId);
                            if (selectedSource != null)
                            {
                                dto.MediaSources = new[] { selectedSource };
                                _logger.LogWarning("[Mediarow] ItemLookup: Returning episode {Name} with MediaSource {Source}",
                                    libraryEpisode.Name, selectedSource.Name);
                            }
                        }

                        context.Result = new OkObjectResult(dto);
                        return;
                    }
                }
            }

            if (cachedItem == null)
            {
                // Check if this is a real persisted item that needs update checking
                var pluginConfig = MediarowPlugin.Instance?.Configuration;
                if (pluginConfig?.CheckForUpdatesOnView == true && pluginConfig?.EnablePersistence == true)
                {
                    _ = CheckPersistedItemForUpdatesAsync(itemId, context.HttpContext.RequestAborted);
                }

                // Check if this is a persisted item in the library
                var libraryItem = directLibraryItem;
                _logger.LogDebug("[Mediarow] ItemLookup persisted check: ItemId={Id}, Found={Found}, Type={Type}, Path={Path}",
                    itemId, libraryItem != null, libraryItem?.GetType().Name ?? "null", libraryItem?.Path ?? "null");

                // Handle AIOStreams for any persisted item (movie or episode)
                if (libraryItem != null && pluginConfig?.StreamProvider == StreamProvider.AIOStreams)
                {
                    // Check if this is a .strm file (our persisted item) or has our provider ID
                    var isPersistedItem = _nativeAioProvider.IsMediarowItem(libraryItem);
                    if (isPersistedItem)
                    {
                        _logger.LogDebug("[Mediarow] ItemLookup: Persisted item with AIOStreams provider, enriching with streams");

                        // Let Jellyfin handle the request first
                        var result = await next();
                        if (result.Exception is HttpRequestException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
                        {
                            _logger.LogWarning("[Mediarow] Native details preparation failed for {ItemId} ({ErrorType})", itemId, result.Exception.GetType().Name);
                            result.ExceptionHandled = true;
                            result.Result = new ObjectResult(new { Error = "AIOStreams could not load stream choices. Check addon settings or retry." }) { StatusCode = 502 };
                            return;
                        }

                        // Then add MediaSources from AIOStreams
                        if (result.Result is ObjectResult objResult && objResult.Value is BaseItemDto dto)
                        {
                            var streamChoices = LoadStreamChoicesAsync(libraryItem);
                            var budget = DetailsStreamBudget - requestTimer.Elapsed;
                            if (budget < MinimumStreamWait)
                            {
                                budget = MinimumStreamWait;
                            }

                            if (await Task.WhenAny(streamChoices, Task.Delay(budget, context.HttpContext.RequestAborted)) != streamChoices)
                            {
                                // A cold AIOStreams lookup takes seconds; show the page now. Play asks PlaybackInfo,
                                // which reuses this in-flight lookup, and the version list appears on the next open.
                                dto.MediaSources = null;
                                _ = streamChoices.ContinueWith(
                                    task => _logger.LogDebug("[Mediarow] Background stream choices failed ({ErrorType})", task.Exception?.InnerException?.GetType().Name),
                                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                                _logger.LogInformation("[Mediarow] Returned {Name} before AIOStreams finished; stream choices load in the background", dto.Name);
                                return;
                            }

                            try
                            {
                                dto.MediaSources = await streamChoices;
                                _logger.LogInformation("[Mediarow] Added {Count} AIOStreams MediaSources to persisted item {Name}",
                                    dto.MediaSources?.Length ?? 0, dto.Name);
                            }
                            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
                            {
                                _logger.LogWarning("[Mediarow] Stream choices failed for {ItemId} ({ErrorType})", itemId, ex.GetType().Name);
                                result.Result = new ObjectResult(new { Error = "AIOStreams could not load stream choices. Check addon settings or retry." }) { StatusCode = 502 };
                            }
                        }
                        return;
                    }
                }

                // Handle anime multi-audio for Direct mode
                if (libraryItem is Episode episode)
                {
                    var isPersistedAnime = IsPersistedAnimeEpisode(episode);
                    _logger.LogDebug("[Mediarow] ItemLookup: Episode check - IsPersistedAnime={IsAnime}", isPersistedAnime);

                    if (isPersistedAnime && pluginConfig?.StreamProvider == StreamProvider.Direct)
                    {
                        // Let Jellyfin handle the request first
                        var result = await next();

                        // Then add MediaSources to the response for anime version selector
                        if (result.Result is ObjectResult objResult && objResult.Value is BaseItemDto dto)
                        {
                            await AddAnimeMediaSourcesToDto(dto, episode, context.HttpContext.RequestAborted);
                            _logger.LogDebug("[Mediarow] Added {Count} MediaSources to persisted anime episode {Name}",
                                dto.MediaSources?.Length ?? 0, dto.Name);
                        }
                        return;
                    }
                }

                // Not a dynamic item, let Jellyfin handle it
                await next();
                return;
            }
        }

        // Verify this is actually a dynamic item by checking for our provider ID
        // This prevents returning cached items when GUIDs collide with real items
        if (!SearchResultFactory.IsDynamicItem(cachedItem))
        {
            _logger.LogDebug("[Mediarow] Cached item {Id} does not have Mediarow provider ID, passing to Jellyfin",
                itemId);
            await next();
            return;
        }

        _logger.LogDebug("[Mediarow] ItemLookup: Returning cached item: {Name} ({Id}), Type={Type}, HasMediaSources={HasSources}, MediaSourceCount={Count}",
            cachedItem.Name, itemId, cachedItem.Type, cachedItem.MediaSources != null, cachedItem.MediaSources?.Length ?? 0);

        // Log MediaSource details for episodes to debug playback issues
        if (cachedItem.Type == BaseItemKind.Episode && cachedItem.MediaSources != null)
        {
            foreach (var source in cachedItem.MediaSources)
            {
                _logger.LogDebug("[Mediarow] Episode MediaSource: Id={Id}, Name={Name}, Path={Path}, SupportsDirectPlay={DirectPlay}",
                    source.Id, source.Name, source.Path, source.SupportsDirectPlay);
            }
        }

        // If this was a MediaSource ID lookup, filter to only that MediaSource
        if (!string.IsNullOrEmpty(requestedMediaSourceId) && cachedItem.MediaSources != null)
        {
            var selectedSource = cachedItem.MediaSources.FirstOrDefault(s => s.Id == requestedMediaSourceId);
            if (selectedSource != null)
            {
                _logger.LogDebug("[Mediarow] Filtering to requested MediaSource: {Name} ({Id})",
                    selectedSource.Name, selectedSource.Id);
                cachedItem.MediaSources = new[] { selectedSource };
            }
        }

        // Enrich with full details based on item type
        var config = MediarowPlugin.Instance?.Configuration;
        var shouldTriggerEmbedarr = config?.CreateMediaOnView == true &&
                                    config?.StreamProvider == StreamProvider.Embedarr;
        var shouldTriggerPersistence = config?.EnablePersistence == true;

        if (cachedItem.Type == BaseItemKind.Movie)
        {
            cachedItem = await _searchResultFactory.EnrichMovieDtoAsync(cachedItem, context.HttpContext.RequestAborted);
            // Trigger Embedarr in background (fire-and-forget) to add to library - if enabled and using Embedarr provider
            if (shouldTriggerEmbedarr)
            {
                _ = TriggerEmbedarrAsync(cachedItem);
            }
            // Trigger persistence in background (fire-and-forget) to create .strm files - if enabled
            if (shouldTriggerPersistence)
            {
                _ = TriggerPersistenceAsync(cachedItem);
            }
        }
        else if (cachedItem.Type == BaseItemKind.Series)
        {
            cachedItem = await _searchResultFactory.EnrichSeriesDtoAsync(cachedItem, context.HttpContext.RequestAborted);
            // Trigger Embedarr in background (fire-and-forget) to add to library - if enabled and using Embedarr provider
            if (shouldTriggerEmbedarr)
            {
                _ = TriggerEmbedarrAsync(cachedItem);
            }
            // Trigger persistence in background (fire-and-forget) to create .strm files - if enabled
            if (shouldTriggerPersistence)
            {
                _ = TriggerPersistenceAsync(cachedItem);
            }
            // Check for new episodes in already-persisted series (fire-and-forget)
            if (config?.CheckForUpdatesOnView == true && config?.EnablePersistence == true)
            {
                _ = TriggerSeriesUpdateAsync(cachedItem);
            }
        }
        else if (cachedItem.Type == BaseItemKind.Episode)
        {
            // Virtual-only items remain browse-only until they have a native library identity.
            if (config?.StreamProvider == StreamProvider.AIOStreams) cachedItem.MediaSources = [];
        }

        // For movies, also populate AIOStreams MediaSources if configured (after enrichment)
        if (cachedItem.Type == BaseItemKind.Movie)
        {
            if (config?.StreamProvider == StreamProvider.AIOStreams) cachedItem.MediaSources = [];
        }

        // For persisted items also in cache, use real UserData from database.
        // For virtual-only items, fall back to the user-scoped resume tracker so home/continue-watching
        // surfaces the exact last position instead of zero.
        if (userId.HasValue)
        {
            var libraryItem = _libraryManager.GetItemById(itemId);
            if (libraryItem != null)
            {
                var user = _userManager.GetUserById(userId.Value);
                if (user != null)
                {
                    var userData = _userDataManager.GetUserData(user, libraryItem);
                    if (userData != null)
                    {
                        cachedItem.UserData = new UserItemDataDto
                        {
                            Key = libraryItem.GetUserDataKeys().FirstOrDefault() ?? itemId.ToString("N"),
                            PlaybackPositionTicks = userData.PlaybackPositionTicks,
                            PlayCount = userData.PlayCount,
                            IsFavorite = userData.IsFavorite,
                            Played = userData.Played,
                            LastPlayedDate = userData.LastPlayedDate
                        };
                    }
                }
            }
            else
            {
                var trackedUserData = _virtualResumeTracker.GetUserData(userId.Value, itemId);
                if (trackedUserData != null)
                {
                    cachedItem.UserData = trackedUserData;
                }
            }
        }

        // Return our cached item directly
        context.Result = new OkObjectResult(cachedItem);
    }

    /// <summary>
    /// Trigger Embedarr to add the item to the library in the background.
    /// This ensures the item is available for streaming when user clicks play.
    /// </summary>
    private async Task TriggerEmbedarrAsync(BaseItemDto item)
    {
        try
        {
            // Skip if already added to Embedarr
            if (_itemCache.IsAddedToEmbedarr(item.Id))
            {
                _logger.LogDebug("[Mediarow] Item {Name} already added to Embedarr, skipping", item.Name);
                return;
            }

            _logger.LogDebug("[Mediarow] Triggering Embedarr for: {Name} ({Id})", item.Name, item.Id);

            var result = await _mediarowService.AddToEmbedarrAsync(item);

            if (result?.Success == true)
            {
                _itemCache.MarkAddedToEmbedarr(item.Id);
                _logger.LogDebug("[Mediarow] Successfully added {Name} to Embedarr", item.Name);
            }
            else
            {
                _logger.LogDebug("[Mediarow] Failed to add {Name} to Embedarr: {Error}",
                    item.Name, result?.Error ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mediarow] Error triggering Embedarr for {Name}", item.Name);
        }
    }

    /// <summary>
    /// Trigger persistence to create .strm files for the item in the background.
    /// This ensures the item is added to the Jellyfin library when user views item details.
    /// </summary>
    private async Task TriggerPersistenceAsync(BaseItemDto item)
    {
        try
        {
            // Skip if already persisted
            if (_itemCache.IsAddedToEmbedarr(item.Id)) // Reuse the "added" tracking
            {
                _logger.LogDebug("[Mediarow] Item {Name} already persisted, skipping", item.Name);
                return;
            }

            _logger.LogInformation("[Mediarow] Triggering persistence for: {Name} ({Id})", item.Name, item.Id);

            string? createdPath = null;

            if (item.Type == BaseItemKind.Movie)
            {
                createdPath = await _persistenceService.PersistMovieAsync(item);
            }
            else if (item.Type == BaseItemKind.Series)
            {
                createdPath = await _persistenceService.PersistSeriesAsync(item);
            }

            if (!string.IsNullOrEmpty(createdPath))
            {
                _itemCache.MarkAddedToEmbedarr(item.Id); // Reuse the tracking to prevent re-persistence
                _logger.LogInformation("[Mediarow] Successfully persisted {Name} to: {Path}", item.Name, createdPath);

                // Add to the library if configured (series are imported directly with all episodes)
                await _persistenceService.AddToLibraryAsync(createdPath, item.Type, item);
            }
            else
            {
                _logger.LogDebug("[Mediarow] Item {Name} was not persisted (already exists or failed)", item.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mediarow] Error triggering persistence for {Name}", item.Name);
        }
    }

    /// <summary>
    /// Check for and add new episodes to an already-persisted series.
    /// This ensures ongoing shows stay up-to-date with new episodes.
    /// </summary>
    private async Task TriggerSeriesUpdateAsync(BaseItemDto series)
    {
        try
        {
            // Check if series is already persisted (folder exists)
            var seriesPath = _persistenceService.GetSeriesFolderPath(series);
            if (!Directory.Exists(seriesPath))
            {
                _logger.LogDebug("[Mediarow] Series {Name} not persisted, skipping update check", series.Name);
                return;
            }

            _logger.LogDebug("[Mediarow] Checking for new episodes: {Name}", series.Name);

            var newCount = await _persistenceService.UpdateSeriesAsync(series);

            if (newCount > 0)
            {
                _logger.LogInformation("[Mediarow] Added {Count} new episodes for {Name}", newCount, series.Name);

                // Trigger library scan to pick up new episodes
                _persistenceService.TriggerLibraryScan();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mediarow] Error checking for series updates: {Name}", series.Name);
        }
    }

    /// <summary>
    /// Check if a real (already persisted) library item needs updates.
    /// For series: check for new aired episodes.
    /// For movies: check if movie is now released and create .strm if needed.
    /// </summary>
    private async Task CheckPersistedItemForUpdatesAsync(Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            var realItem = _libraryManager.GetItemById(itemId);
            if (realItem == null)
            {
                return;
            }

            // Handle Series
            if (realItem is Series series)
            {
                await CheckPersistedSeriesForUpdatesAsync(series, cancellationToken);
            }
            // Handle Movies - check if movie folder exists but .strm doesn't (was unreleased, now released)
            else if (realItem is MediaBrowser.Controller.Entities.Movies.Movie movie)
            {
                await CheckPersistedMovieForUpdatesAsync(movie, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mediarow] Error checking persisted item for updates: {Id}", itemId);
        }
    }

    /// <summary>
    /// Check if a persisted series has new aired episodes to add.
    /// </summary>
    private async Task CheckPersistedSeriesForUpdatesAsync(Series series, CancellationToken cancellationToken)
    {
        try
        {
            // Check if this series has .strm files (indicating it's from our plugin)
            var episodes = series.GetRecursiveChildren().OfType<Episode>().ToList();
            if (episodes.Count == 0)
            {
                return;
            }

            var firstEpisode = episodes.FirstOrDefault();
            if (firstEpisode?.Path == null || !firstEpisode.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Read .strm content to verify it's ours
            if (!File.Exists(firstEpisode.Path))
            {
                return;
            }

            var strmContent = await File.ReadAllTextAsync(firstEpisode.Path, cancellationToken);
            if (!StrmMarker.IsMarker(strmContent))
            {
                return;
            }

            _logger.LogDebug("[Mediarow] Checking persisted series for updates: {Name}", series.Name);

            // Convert to BaseItemDto for the persistence service
            var seriesDto = new BaseItemDto
            {
                Id = series.Id,
                Name = series.Name,
                Type = BaseItemKind.Series,
                ProductionYear = series.ProductionYear,
                ProviderIds = series.ProviderIds,
                Genres = series.Genres
            };

            // Fetch fresh episode data and update
            var enrichedSeries = await _searchResultFactory.EnrichSeriesDtoAsync(seriesDto, cancellationToken);

            var newCount = await _persistenceService.UpdateSeriesAsync(enrichedSeries);
            if (newCount > 0)
            {
                _logger.LogInformation("[Mediarow] Added {Count} new episodes for persisted series: {Name}",
                    newCount, series.Name);
                _persistenceService.TriggerLibraryScan();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mediarow] Error checking persisted series for updates: {Name}", series.Name);
        }
    }

    /// <summary>
    /// Check if a movie that was previously unreleased is now released.
    /// </summary>
    private async Task CheckPersistedMovieForUpdatesAsync(MediaBrowser.Controller.Entities.Movies.Movie movie, CancellationToken cancellationToken)
    {
        try
        {
            // Check if movie has a .strm file
            if (movie.Path == null || !movie.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // If the .strm file already exists, nothing to do
            if (File.Exists(movie.Path))
            {
                return;
            }

            // Movie folder exists but no .strm - check if it's now released
            var movieDto = new BaseItemDto
            {
                Id = movie.Id,
                Name = movie.Name,
                Type = BaseItemKind.Movie,
                ProductionYear = movie.ProductionYear,
                ProviderIds = movie.ProviderIds,
                PremiereDate = movie.PremiereDate
            };

            // Try to create the movie now
            var createdPath = await _persistenceService.PersistMovieAsync(movieDto, cancellationToken);
            if (!string.IsNullOrEmpty(createdPath))
            {
                _logger.LogInformation("[Mediarow] Created .strm for now-released movie: {Name}", movie.Name);
                _persistenceService.TriggerLibraryScan();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Mediarow] Error checking persisted movie for updates: {Name}", movie.Name);
        }
    }

    /// <summary>
    /// Check if an episode is a persisted anime that needs MediaSources added for version selector.
    /// </summary>
    private bool IsPersistedAnimeEpisode(Episode episode)
    {
        var config = MediarowPlugin.Instance?.Configuration;
        _logger.LogWarning("[Mediarow] IsPersistedAnimeEpisode: Config={HasConfig}, EnableAudioVersions={Enable}, AudioTracks={Tracks}",
            config != null, config?.EnableAnimeAudioVersions, config?.AnimeAudioTracks ?? "null");

        if (config?.EnableAnimeAudioVersions != true || string.IsNullOrEmpty(config.AnimeAudioTracks))
        {
            _logger.LogWarning("[Mediarow] IsPersistedAnimeEpisode: Config check failed, returning false");
            return false;
        }

        // Check if it has a .strm file
        if (string.IsNullOrEmpty(episode.Path) || !episode.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[Mediarow] IsPersistedAnimeEpisode: Not a .strm file - Path={Path}", episode.Path ?? "null");
            return false;
        }

        // Read .strm to check if it's an anime URL
        try
        {
            if (!File.Exists(episode.Path))
            {
                _logger.LogWarning("[Mediarow] IsPersistedAnimeEpisode: File does not exist - Path={Path}", episode.Path);
                return false;
            }

            var strmContent = File.ReadAllText(episode.Path).Trim();
            _logger.LogWarning("[Mediarow] IsPersistedAnimeEpisode: StrmContent={Content}", strmContent);
            var isAnime = StrmMarker.IsAnime(strmContent);
            _logger.LogWarning("[Mediarow] IsPersistedAnimeEpisode: IsAnimeUrl={IsAnime}", isAnime);
            return isAnime;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mediarow] Error reading .strm file for anime check: {Path}", episode.Path);
            return false;
        }
    }

    /// <summary>
    /// Add MediaSources to a BaseItemDto for persisted anime episodes.
    /// This allows clients to show the version selector (SUB/DUB) in the player.
    /// </summary>
    private async Task AddAnimeMediaSourcesToDto(BaseItemDto dto, Episode episode, CancellationToken cancellationToken)
    {
        try
        {
            var config = MediarowPlugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            // Read .strm content to get the anime URL
            if (string.IsNullOrEmpty(episode.Path) || !File.Exists(episode.Path))
            {
                return;
            }

            var strmContent = await File.ReadAllTextAsync(episode.Path, cancellationToken);
            strmContent = strmContent.Trim();

            if (!StrmMarker.IsAnime(strmContent))
            {
                return;
            }

            // Parse the URL: mediarow://anime/{anilistId}/{episode}
            var uri = new Uri(strmContent);
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 2)
            {
                _logger.LogWarning("[Mediarow] Invalid anime URL format: {Url}", strmContent);
                return;
            }

            var anilistId = segments[0];
            var episodeNum = segments[1];

            // Fetch runtime from API if not set (critical for player progress tracking)
            var runTimeTicks = await _searchResultFactory.GetRuntimeAsync(episode, cancellationToken);
            if (runTimeTicks.HasValue && episode.RunTimeTicks != runTimeTicks.Value)
            {
                episode.RunTimeTicks = runTimeTicks.Value;
                await _libraryManager.UpdateItemAsync(episode, episode.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
                _logger.LogInformation("[Mediarow] Updated database RunTimeTicks for anime {Name} during lookup: {Ticks} ({Minutes} min)",
                    episode.Name, runTimeTicks.Value, runTimeTicks.Value / 600_000_000);
                dto.RunTimeTicks = runTimeTicks.Value;
            }

            var template = config.DirectAnimeUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogDebug("[Mediarow] DirectAnimeUrlTemplate not configured for persisted anime");
                return;
            }

            // Parse audio tracks from config
            var audioTracks = config.AnimeAudioTracks
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (audioTracks.Length == 0)
            {
                audioTracks = new[] { "sub", "dub" };
            }

            // Build MediaSource for each audio track
            var mediaSources = audioTracks.Select(track =>
            {
                var streamUrl = template
                    .Replace("{id}", anilistId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{anilist}", anilistId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{episode}", episodeNum, StringComparison.OrdinalIgnoreCase)
                    .Replace("{absolute}", episodeNum, StringComparison.OrdinalIgnoreCase)
                    .Replace("{audio}", track.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

                // Generate a stable unique ID for this MediaSource
                var input = $"persisted:{episode.Id:N}:{track.ToLowerInvariant()}";
                using var md5 = System.Security.Cryptography.MD5.Create();
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                var sourceId = new Guid(hash).ToString("N");

                // Store the mapping so we can resolve this MediaSource ID to the episode later
                _itemCache.StoreMediaSourceMapping(sourceId, episode.Id);

                _logger.LogDebug("[Mediarow] Built MediaSource for persisted anime '{Name}' ({Track}): Id={SourceId}",
                    episode.Name, track, sourceId);

                return new MediaSourceInfo
                {
                    Id = sourceId,
                    Name = track.ToUpperInvariant(),  // "SUB", "DUB" shown in version selector
                    Path = streamUrl,
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    Container = "hls",
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = false,
                    SupportsTranscoding = false,
                    SupportsProbing = false,
                    RequiresOpening = false,
                    RequiresClosing = false,
                    RunTimeTicks = dto.RunTimeTicks
                };
            }).ToArray();

            dto.MediaSources = mediaSources;

            _logger.LogInformation("[Mediarow] Added {Count} MediaSources to persisted anime episode {Name}: {Sources}",
                mediaSources.Length, episode.Name, string.Join(", ", audioTracks.Select(t => t.ToUpperInvariant())));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mediarow] Error adding MediaSources to persisted anime: {Name}", episode.Name);
        }
    }

    /// <summary>
    /// Build a BaseItemDto from a library Episode.
    /// Used when returning episode data for a MediaSource ID lookup.
    /// </summary>
    private BaseItemDto BuildBaseItemDtoFromEpisode(Episode episode, Guid? userId = null)
    {
        var dto = new BaseItemDto
        {
            Id = episode.Id,
            Name = episode.Name,
            Type = BaseItemKind.Episode,
            SeriesId = episode.SeriesId,
            ServerId = _serverApplicationHost.SystemId,
            SeriesName = episode.SeriesName,
            SeasonId = episode.SeasonId,
            MediaType = Jellyfin.Data.Enums.MediaType.Video,
            ParentIndexNumber = episode.ParentIndexNumber,
            IndexNumber = episode.IndexNumber,
            Overview = episode.Overview,
            PremiereDate = episode.PremiereDate,
            ProductionYear = episode.ProductionYear,
            RunTimeTicks = episode.RunTimeTicks,
            ProviderIds = episode.ProviderIds,
            Path = episode.Path
        };

        // Add UserData for resume position if userId is provided
        if (userId.HasValue)
        {
            var user = _userManager.GetUserById(userId.Value);
            if (user != null)
            {
                var userData = _userDataManager.GetUserData(user, episode);
                if (userData != null)
                {
                    dto.UserData = new UserItemDataDto
                    {
                        Key = episode.GetUserDataKeys().FirstOrDefault() ?? episode.Id.ToString("N"),
                        PlaybackPositionTicks = userData.PlaybackPositionTicks,
                        PlayCount = userData.PlayCount,
                        IsFavorite = userData.IsFavorite,
                        Played = userData.Played,
                        LastPlayedDate = userData.LastPlayedDate
                    };
                }
            }
        }

        return dto;
    }
}
