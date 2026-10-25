using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Providers;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Cache for storing dynamic item data and image URLs.
/// This allows us to serve item details and images for virtual items
/// that don't exist in Jellyfin's database.
/// </summary>
public class DynamicItemCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<DynamicItemCache> _logger;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(1);

    private const string ItemPrefix = "dynamic_item:";
    private const string ImagePrefix = "dynamic_image:";
    private const string BackdropPrefix = "dynamic_backdrop:";
    private const string SeasonsPrefix = "dynamic_seasons:";
    private const string EpisodesPrefix = "dynamic_episodes:";
    private const string EmbedarrAddedPrefix = "embedarr_added:";
    private const string MediaSourcePrefix = "mediasource_to_episode:";
    private const string SubtitlesPrefix = "dynamic_subtitles:";
    private const string AIOStreamsPrefix = "aiostreams_stream:";
    private const string LogoPrefix = "dynamic_logo:";
    private const string CatalogSourcePrefix = "catalog_source:";

    public DynamicItemCache(IMemoryCache cache, ILogger<DynamicItemCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Store a dynamic item in the cache.
    /// </summary>
    public void StoreItem(BaseItemDto item, string? imageUrl = null, string? backdropUrl = null)
    {
        if (item.Id == Guid.Empty)
        {
            _logger.LogDebug("[DynamicItemCache] Attempted to store item with empty GUID");
            return;
        }

        // Cached catalog DTOs are shared by every user, so they must never carry user state.
        var itemKey = $"{ItemPrefix}{item.Id}";
        _cache.Set(itemKey, item.UserData == null ? item : VirtualItemProjection.WithoutUserData(item), _cacheDuration);

        if (!string.IsNullOrEmpty(imageUrl))
        {
            var imageKey = $"{ImagePrefix}{item.Id}";
            _cache.Set(imageKey, imageUrl, _cacheDuration);
        }

        if (!string.IsNullOrEmpty(backdropUrl))
        {
            var backdropKey = $"{BackdropPrefix}{item.Id}";
            _cache.Set(backdropKey, backdropUrl, _cacheDuration);
        }

        _logger.LogDebug("[DynamicItemCache] Stored item: {Name} ({Id}), HasImage: {HasImage}, HasBackdrop: {HasBackdrop}",
            item.Name, item.Id, !string.IsNullOrEmpty(imageUrl), !string.IsNullOrEmpty(backdropUrl));
    }

    /// <summary>
    /// Get a dynamic item from the cache. The instance is shared across users; use
    /// <see cref="GetItemForUser"/> for anything that is returned to a client or carries user state.
    /// </summary>
    public BaseItemDto? GetItem(Guid itemId)
    {
        var key = $"{ItemPrefix}{itemId}";
        return _cache.TryGetValue<BaseItemDto>(key, out var item) ? item : null;
    }

    /// <summary>
    /// Get a per-user copy of a cached item; mutating it never affects the shared cache entry.
    /// </summary>
    public BaseItemDto? GetItemForUser(Guid itemId, UserItemDataDto? userData)
    {
        var item = GetItem(itemId);
        return item == null ? null : VirtualItemProjection.ForUser(item, userData);
    }

    /// <summary>
    /// Get the image URL for a dynamic item.
    /// </summary>
    public string? GetImageUrl(Guid itemId)
    {
        var key = $"{ImagePrefix}{itemId}";
        return _cache.TryGetValue<string>(key, out var url) ? url : null;
    }

    /// <summary>
    /// Get the backdrop image URL for a dynamic item.
    /// </summary>
    public string? GetBackdropUrl(Guid itemId)
    {
        var key = $"{BackdropPrefix}{itemId}";
        return _cache.TryGetValue<string>(key, out var url) ? url : null;
    }

    /// <summary>
    /// Store a logo image URL for a dynamic item.
    /// </summary>
    public void StoreLogoUrl(Guid itemId, string logoUrl)
    {
        var key = $"{LogoPrefix}{itemId}";
        _cache.Set(key, logoUrl, _cacheDuration);
        _logger.LogDebug("[DynamicItemCache] Stored logo URL for item {ItemId}", itemId);
    }

    /// <summary>
    /// Get the logo image URL for a dynamic item.
    /// </summary>
    public string? GetLogoUrl(Guid itemId)
    {
        var key = $"{LogoPrefix}{itemId}";
        return _cache.TryGetValue<string>(key, out var url) ? url : null;
    }

    /// <summary>
    /// Check if an item exists in the cache.
    /// </summary>
    public bool HasItem(Guid itemId)
    {
        var key = $"{ItemPrefix}{itemId}";
        return _cache.TryGetValue<BaseItemDto>(key, out _);
    }

    /// <summary>
    /// Store the catalog source for an item (to know which provider created it).
    /// </summary>
    public void StoreCatalogSource(Guid itemId, CatalogSource source)
    {
        var key = $"{CatalogSourcePrefix}{itemId}";
        _cache.Set(key, source, _cacheDuration);
        _logger.LogDebug("[DynamicItemCache] Stored catalog source for {ItemId}: {Source}", itemId, source);
    }

    /// <summary>
    /// Get the catalog source for an item.
    /// </summary>
    public CatalogSource? GetCatalogSource(Guid itemId)
    {
        var key = $"{CatalogSourcePrefix}{itemId}";
        return _cache.TryGetValue<CatalogSource>(key, out var source) ? source : null;
    }

    /// <summary>
    /// Store seasons for a series.
    /// </summary>
    public void StoreSeasonsForSeries(Guid seriesId, List<BaseItemDto> seasons)
    {
        var key = $"{SeasonsPrefix}{seriesId}";
        _cache.Set(key, seasons, _cacheDuration);

        // Also store each season individually
        foreach (var season in seasons)
        {
            StoreItem(season);
        }

        _logger.LogDebug("[DynamicItemCache] Stored {Count} seasons for series {SeriesId}",
            seasons.Count, seriesId);
    }

    /// <summary>
    /// Get seasons for a series.
    /// </summary>
    public List<BaseItemDto>? GetSeasonsForSeries(Guid seriesId)
    {
        var key = $"{SeasonsPrefix}{seriesId}";
        return _cache.TryGetValue<List<BaseItemDto>>(key, out var seasons) ? seasons : null;
    }

    /// <summary>
    /// Store episodes for a series.
    /// </summary>
    public void StoreEpisodesForSeries(Guid seriesId, List<BaseItemDto> episodes)
    {
        var key = $"{EpisodesPrefix}{seriesId}";
        _cache.Set(key, episodes, _cacheDuration);

        // Also store each episode individually
        foreach (var episode in episodes)
        {
            StoreItem(episode);
        }

        _logger.LogDebug("[DynamicItemCache] Stored {Count} episodes for series {SeriesId}",
            episodes.Count, seriesId);
    }

    /// <summary>
    /// Get all episodes for a series.
    /// </summary>
    public List<BaseItemDto>? GetEpisodesForSeries(Guid seriesId)
    {
        var key = $"{EpisodesPrefix}{seriesId}";
        return _cache.TryGetValue<List<BaseItemDto>>(key, out var episodes) ? episodes : null;
    }

    /// <summary>
    /// Get episodes for a specific season of a series.
    /// </summary>
    public List<BaseItemDto>? GetEpisodesForSeason(Guid seriesId, int seasonNumber)
    {
        var episodes = GetEpisodesForSeries(seriesId);
        return episodes?.Where(e => e.ParentIndexNumber == seasonNumber).ToList();
    }

    /// <summary>
    /// Mark an item as having been added to Embedarr.
    /// </summary>
    public void MarkAddedToEmbedarr(Guid itemId)
    {
        var key = $"{EmbedarrAddedPrefix}{itemId}";
        _cache.Set(key, true, TimeSpan.FromHours(24)); // Longer duration since files persist
        _logger.LogDebug("[DynamicItemCache] Marked item {ItemId} as added to Embedarr", itemId);
    }

    /// <summary>
    /// Check if an item has been added to Embedarr.
    /// </summary>
    public bool IsAddedToEmbedarr(Guid itemId)
    {
        var key = $"{EmbedarrAddedPrefix}{itemId}";
        return _cache.TryGetValue<bool>(key, out var added) && added;
    }

    /// <summary>
    /// Store a mapping from MediaSource ID to Episode ID.
    /// This allows us to resolve MediaSource IDs when the client tries to fetch them as items.
    /// </summary>
    public void StoreMediaSourceMapping(string mediaSourceId, Guid episodeId)
    {
        var key = $"{MediaSourcePrefix}{mediaSourceId}";
        _cache.Set(key, episodeId, _cacheDuration);
        _logger.LogDebug("[DynamicItemCache] Stored MediaSource mapping: {MediaSourceId} -> {EpisodeId}",
            mediaSourceId, episodeId);
    }

    /// <summary>
    /// Get the Episode ID for a MediaSource ID.
    /// Returns null if the MediaSource ID is not in the cache.
    /// </summary>
    public Guid? GetEpisodeIdForMediaSource(string mediaSourceId)
    {
        var key = $"{MediaSourcePrefix}{mediaSourceId}";
        return _cache.TryGetValue<Guid>(key, out var episodeId) ? episodeId : null;
    }

    /// <summary>
    /// Store subtitles for an item.
    /// </summary>
    public void StoreSubtitles(Guid itemId, List<CachedSubtitle> subtitles)
    {
        var key = $"{SubtitlesPrefix}{itemId}";
        _cache.Set(key, subtitles, _cacheDuration);
        _logger.LogDebug("[DynamicItemCache] Stored {Count} subtitles for item {ItemId}",
            subtitles.Count, itemId);
    }

    /// <summary>
    /// Get all subtitles for an item.
    /// </summary>
    public List<CachedSubtitle>? GetSubtitles(Guid itemId)
    {
        var key = $"{SubtitlesPrefix}{itemId}";
        return _cache.TryGetValue<List<CachedSubtitle>>(key, out var subtitles) ? subtitles : null;
    }

    /// <summary>
    /// Get a specific subtitle by item ID and language code.
    /// </summary>
    public CachedSubtitle? GetSubtitle(Guid itemId, string languageCode)
    {
        var subtitles = GetSubtitles(itemId);
        return subtitles?.FirstOrDefault(s =>
            s.LanguageCode.Equals(languageCode, StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>
    /// Store a mapping from AIOStreams MediaSource ID to item ID and stream URL.
    /// This allows us to resolve MediaSource IDs to both the parent item and stream URL.
    /// </summary>
    public void StoreAIOStreamsMapping(string mediaSourceId, Guid itemId, string streamUrl, string? filename = null)
    {
        var key = $"{AIOStreamsPrefix}{mediaSourceId}";
        var mapping = new AIOStreamsMapping { ItemId = itemId, StreamUrl = streamUrl, Filename = filename };
        _cache.Set(key, mapping, _cacheDuration);
        _logger.LogDebug("[DynamicItemCache] Stored AIOStreams mapping: {MediaSourceId} -> Item {ItemId}, URL {StreamUrl}",
            mediaSourceId, itemId, streamUrl);
    }

    /// <summary>
    /// Get the full AIOStreams mapping for a MediaSource ID.
    /// Returns null if the MediaSource ID is not in the cache.
    /// </summary>
    public AIOStreamsMapping? GetAIOStreamsMapping(string mediaSourceId)
    {
        var key = $"{AIOStreamsPrefix}{mediaSourceId}";
        return _cache.TryGetValue<AIOStreamsMapping>(key, out var mapping) ? mapping : null;
    }
}

/// <summary>
/// Represents a mapping from an AIOStreams MediaSource ID to item details.
/// </summary>
public class AIOStreamsMapping
{
    public Guid ItemId { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public string? Filename { get; set; }
}
