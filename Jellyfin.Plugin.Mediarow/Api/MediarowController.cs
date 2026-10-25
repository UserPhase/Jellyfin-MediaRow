using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Api;

/// <summary>
/// Controller for Mediarow API endpoints.
/// </summary>
[ApiController]
[Route("Mediarow")]
public class MediarowController : ControllerBase
{
    private readonly DynamicItemCache _itemCache;
    private readonly SubtitleService _subtitleService;
    private readonly PersistenceService _persistenceService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MediarowController> _logger;
    private readonly PlaybackItemResolver _playbackResolver;
    private readonly UserPermissionGuard _guard;

    public MediarowController(
        DynamicItemCache itemCache,
        SubtitleService subtitleService,
        PersistenceService persistenceService,
        IHttpClientFactory httpClientFactory,
        ILogger<MediarowController> logger,
        PlaybackItemResolver playbackResolver,
        IMemoryCache memoryCache,
        UserPermissionGuard guard)
    {
        _guard = guard;
        _itemCache = itemCache;
        _subtitleService = subtitleService;
        _persistenceService = persistenceService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _playbackResolver = playbackResolver;
        _memoryCache = memoryCache;
    }

    private const int MaxCachedImageBytes = 2 * 1024 * 1024;
    private readonly IMemoryCache _memoryCache;

    private PluginConfiguration Config => MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Get a dynamic item by ID.
    /// </summary>
    [HttpGet("Items/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BaseItemDto> GetItem([FromRoute] Guid itemId)
    {
        var item = _itemCache.GetItem(itemId);
        if (item == null)
        {
            _logger.LogDebug("[Mediarow] Item not found in cache: {ItemId}", itemId);
            return NotFound();
        }

        UserAccessResult access = _guard.Resolve(User);
        if (!access.Succeeded || !_guard.CanView(access.Context!, item))
        {
            return NotFound();
        }

        _logger.LogDebug("[Mediarow] Returning cached item: {Name} ({Id})", item.Name, itemId);
        return Ok(VirtualItemProjection.WithoutUserData(item));
    }

    /// <summary>
    /// Get image for a dynamic item.
    /// </summary>
    [HttpGet("Items/{itemId}/Images/Primary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetItemImage(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        var imageUrl = _itemCache.GetImageUrl(itemId);
        if (string.IsNullOrEmpty(imageUrl))
        {
            _logger.LogDebug("[Mediarow] No image URL for item: {ItemId}", itemId);
            return NotFound();
        }

        // Posters rarely change; let the browser reuse them and keep recent ones in memory for other clients.
        Response.Headers.CacheControl = "private, max-age=86400";
        var imageCacheKey = "dynamic-image:" + imageUrl;
        if (_memoryCache.TryGetValue<(byte[] Data, string ContentType)>(imageCacheKey, out var cachedImage))
        {
            return File(cachedImage.Data, cachedImage.ContentType);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(imageUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[Mediarow] Failed to fetch image for item {ItemId}: {Status}",
                    itemId, response.StatusCode);
                Response.Headers.CacheControl = "no-store";
                return NotFound();
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var imageData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (imageData.Length <= MaxCachedImageBytes)
            {
                _memoryCache.Set(imageCacheKey, (imageData, contentType), new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromHours(1),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12)
                });
            }

            return File(imageData, contentType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Mediarow] Error fetching image for item {ItemId}", itemId);
            Response.Headers.CacheControl = "no-store";
            return NotFound();
        }
    }

    /// <summary>
    /// Get subtitle for a dynamic item in WebVTT format.
    /// </summary>
    [HttpGet("Subtitles/{itemId}/{languageCode}.vtt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSubtitleVtt(
        [FromRoute] Guid itemId,
        [FromRoute] string languageCode,
        CancellationToken cancellationToken)
    {
        var content = await _subtitleService.GetSubtitleContentAsync(itemId, languageCode, cancellationToken);
        if (content == null)
        {
            _logger.LogInformation("[Mediarow] Subtitle not found: {ItemId}, {Language}", itemId, languageCode);
            return NotFound();
        }

        _logger.LogDebug("[Mediarow] Serving VTT subtitle: {ItemId}, {Language}, ContentLength={Length}",
            itemId, languageCode, content.Length);

        return Content(content, "text/vtt", Encoding.UTF8);
    }

    /// <summary>
    /// Get subtitle for a dynamic item in JSON TrackEvents format.
    /// Used by Jellyfin web player for custom subtitle rendering.
    /// </summary>
    [HttpGet("Subtitles/{itemId}/{languageCode}.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSubtitleJs(
        [FromRoute] Guid itemId,
        [FromRoute] string languageCode,
        CancellationToken cancellationToken)
    {
        var content = await _subtitleService.GetSubtitleContentAsync(itemId, languageCode, cancellationToken);
        if (content == null)
        {
            _logger.LogInformation("[Mediarow] Subtitle not found: {ItemId}, {Language}", itemId, languageCode);
            return NotFound();
        }

        // Convert WebVTT to TrackEvents JSON
        var trackEventsJson = SubtitleConverter.WebVttToTrackEvents(content);

        _logger.LogDebug("[Mediarow] Serving JS subtitle: {ItemId}, {Language}, ContentLength={Length}",
            itemId, languageCode, trackEventsJson.Length);

        return Content(trackEventsJson, "application/json", Encoding.UTF8);
    }

    /// <summary>
    /// Persist a dynamic item to the library as a .strm file.
    /// </summary>
    /// <param name="itemId">The ID of the dynamic item to persist.</param>
    /// <returns>The path to the created item, or an error if persistence failed.</returns>
    [HttpPost("Persist/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PersistItem(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        // Check if persistence is enabled
        if (!Config.EnablePersistence)
        {
            _logger.LogWarning("[Mediarow] Persistence is not enabled");
            return BadRequest(new { Error = "Persistence is not enabled in plugin settings" });
        }

        // Get the item from cache
        var item = _itemCache.GetItem(itemId);
        if (item == null)
        {
            _logger.LogDebug("[Mediarow] Item not found in cache for persistence: {ItemId}", itemId);
            return NotFound(new { Error = "Item not found in cache" });
        }

        _logger.LogInformation("[Mediarow] Persisting item: {Name} ({Type})", item.Name, item.Type);

        string? createdPath;

        try
        {
            // Handle based on item type
            if (item.Type == BaseItemKind.Movie)
            {
                createdPath = await _persistenceService.PersistMovieAsync(item, cancellationToken);
            }
            else if (item.Type == BaseItemKind.Series)
            {
                createdPath = await _persistenceService.PersistSeriesAsync(item, cancellationToken);
            }
            else
            {
                _logger.LogWarning("[Mediarow] Cannot persist item type: {Type}", item.Type);
                return BadRequest(new { Error = $"Cannot persist item type: {item.Type}" });
            }

            if (createdPath == null)
            {
                _logger.LogDebug("[Mediarow] Item already exists in library or could not be created: {Name}", item.Name);
                return Ok(new { Message = "Item already exists in library", AlreadyExists = true });
            }

            // Add to the library if configured (series are imported directly with all episodes)
            await _persistenceService.AddToLibraryAsync(createdPath, item.Type, item);

            _logger.LogInformation("[Mediarow] Successfully persisted: {Name} to {Path}", item.Name, createdPath);
            return Ok(new { Message = "Item persisted successfully", Path = createdPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mediarow] Error persisting item: {Name}", item.Name);
            return BadRequest(new { Error = $"Failed to persist item: {ex.Message}" });
        }
    }

    [HttpPost("Playback/Resolve/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResolvePlaybackItem(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = _itemCache.GetItem(itemId);
        if (item == null)
        {
            _logger.LogDebug("[Mediarow] Playback resolve item not found in cache: {ItemId}", itemId);
            return NotFound(new { Error = "Item not found in cache" });
        }

        if (item.Type != BaseItemKind.Movie && item.Type != BaseItemKind.Series)
        {
            return BadRequest(new { Error = $"Cannot resolve playback for item type: {item.Type}" });
        }

        if (Config.StreamProvider == StreamProvider.None)
        {
            return Ok(new { ItemId = item.Id, Resolved = true, BrowseOnly = true });
        }

        try
        {
            var resolved = await _playbackResolver.ResolveAsync(item, cancellationToken).ConfigureAwait(false);
            return resolved == null
                ? Accepted(new { Resolved = false, Message = "The title is waiting for Jellyfin's library scan. Try opening it again shortly." })
                : Ok(new { ItemId = resolved.Id, Resolved = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning("[Mediarow] Cannot resolve playback item {Id}: {Reason}", itemId, ex.Message);
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            _logger.LogWarning("[Mediarow] Playback item creation failed for {Id} ({ErrorType})", itemId, ex.GetType().Name);
            return StatusCode(502, new { Error = "The title could not be prepared. Check metadata-provider connectivity and persistent-folder permissions." });
        }
    }
}
