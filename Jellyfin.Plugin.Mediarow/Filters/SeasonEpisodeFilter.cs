using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Mediarow.Extensions;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Filters;

/// <summary>
/// Filter that intercepts season and episode requests for dynamic TV series
/// and returns virtual seasons/episodes from the cache.
/// </summary>
public class SeasonEpisodeFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly PersistenceService _persistenceService;
    private readonly SeriesResumeResolver _seriesResumeResolver;
    private readonly UserPermissionGuard _guard;
    private readonly ILogger<SeasonEpisodeFilter> _logger;

    public SeasonEpisodeFilter(
        DynamicItemCache itemCache,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService,
        PersistenceService persistenceService,
        SeriesResumeResolver seriesResumeResolver,
        UserPermissionGuard guard,
        ILogger<SeasonEpisodeFilter> logger)
    {
        _guard = guard;
        _itemCache = itemCache;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
        _persistenceService = persistenceService;
        _seriesResumeResolver = seriesResumeResolver;
        _logger = logger;
    }

    private bool TryGetUser(ActionExecutingContext context, out User user)
    {
        user = null!;
        Guid requestedUserId = context.ActionArguments.TryGetValue("userId", out var value) && value is Guid id
            ? id
            : Guid.Empty;
        UserAccessResult access = _guard.Resolve(context.HttpContext.User, requestedUserId);
        if (!access.Succeeded)
        {
            return false;
        }

        var found = _userManager.GetUserById(access.Context!.UserId);
        if (found is null)
        {
            return false;
        }

        user = found;
        return true;
    }

    /// <summary>
    /// Resolves ownership and current policy before handling a virtual parent. Rejected requests
    /// retain an explicit authentication, authorization or visibility result.
    /// </summary>
    private bool CanViewSeries(ActionExecutingContext context, BaseItemDto seriesItem)
    {
        Guid requestedUserId = context.ActionArguments.TryGetValue("userId", out var value) && value is Guid id
            ? id
            : Guid.Empty;
        UserAccessResult access = _guard.Resolve(context.HttpContext.User, requestedUserId);
        if (!access.Succeeded)
        {
            context.Result = access.Failure == UserAccessFailure.Unauthenticated ? new UnauthorizedResult() : new ForbidResult();
            return false;
        }
        if (!_guard.CanView(access.Context!, PolicyItem(seriesItem)))
        { context.Result = new NotFoundResult(); return false; }
        var native = _libraryManager.GetItemById(seriesItem.Id);
        var user = _userManager.GetUserById(access.Context!.UserId);
        if (native != null && (user == null || !native.IsVisibleStandalone(user)))
        { context.Result = new NotFoundResult(); return false; }
        return true;
    }

    private BaseItemDto[] VisibleChildren(ActionExecutingContext context, BaseItemDto parent, IEnumerable<BaseItemDto> children)
    {
        if (!CanViewSeries(context, parent)) return [];
        Guid requested = context.ActionArguments.TryGetValue("userId", out var value) && value is Guid id ? id : Guid.Empty;
        var access = _guard.Resolve(context.HttpContext.User, requested);
        if (!access.Succeeded) return [];
        var user = _userManager.GetUserById(access.Context!.UserId);
        return _guard.FilterVisible(access.Context!, children.Select(PolicyItem))
            .Where(child => _libraryManager.GetItemById(child.Id) is not { } native || user != null && native.IsVisibleStandalone(user))
            .Select(VirtualItemProjection.WithoutUserData).ToArray();
    }

    private BaseItemDto PolicyItem(BaseItemDto item)
    {
        var copy = VirtualItemProjection.WithoutUserData(item);
        if (copy.Type is BaseItemKind.Season or BaseItemKind.Episode && copy.SeriesId.HasValue &&
            _itemCache.GetItem(copy.SeriesId.Value) is { } series)
        {
            // Like native children, an unrated virtual child inherits its parent's certification.
            // An explicit child rating remains authoritative and is checked separately.
            if (string.IsNullOrWhiteSpace(copy.CustomRating) && string.IsNullOrWhiteSpace(copy.OfficialRating))
                copy.OfficialRating = series.CustomRating ?? series.OfficialRating;
            copy.Tags = (copy.Tags ?? []).Concat(series.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return copy;
    }

    private void SetChildrenResult(ActionExecutingContext context, BaseItemDto parent, IEnumerable<BaseItemDto> children)
    {
        var visible = VisibleChildren(context, parent, children);
        if (context.Result != null) return;
        int offset = context.ActionArguments.TryGetValue("startIndex", out var start) && start is int index ? Math.Max(0, index) : 0;
        int? limit = context.ActionArguments.TryGetValue("limit", out var size) && size is int count ? Math.Max(0, count) : null;
        var page = visible.Skip(offset);
        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>(offset, visible.Length,
            (limit.HasValue ? page.Take(limit.Value) : page).ToArray()));
    }

    private static OkObjectResult EmptyResult() => new(new QueryResult<BaseItemDto>
    {
        Items = Array.Empty<BaseItemDto>(),
        TotalRecordCount = 0
    });

    // Run before other filters
    public int Order => 0;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

        // Debug logging to see all requests
        if (controllerName == "Items")
        {
            _logger.LogDebug("[Mediarow] SeasonEpisodeFilter: Items controller, Action={Action}, Args={Args}",
                actionName, string.Join(", ", context.ActionArguments.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        // Handle TvShows controller
        if (controllerName == "TvShows")
        {
            // Handle GetSeasons
            if (actionName == "GetSeasons")
            {
                await HandleGetSeasons(context, next);
                return;
            }

            // Handle GetEpisodes
            if (actionName == "GetEpisodes")
            {
                await HandleGetEpisodes(context, next);
                return;
            }

            // Handle GetNextUp - intercept for dynamic series to prevent random episodes showing
            if (actionName == "GetNextUp")
            {
                await HandleGetNextUp(context, next);
                return;
            }
        }

        // Handle Items controller - Android TV uses this endpoint with parentId
        if (controllerName == "Items" && actionName is "GetItems" or "GetItemsByUserIdLegacy")
        {
            await HandleGetItems(context, next);
            return;
        }

        await next();
    }

    private async Task HandleGetSeasons(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Get series ID from action arguments
        if (!context.ActionArguments.TryGetValue("seriesId", out var seriesIdObj) || seriesIdObj is not Guid seriesId)
        {
            await next();
            return;
        }

        // Check if this is a dynamic series
        var seriesItem = _itemCache.GetItem(seriesId);
        if (seriesItem == null || !SearchResultFactory.IsDynamicItem(seriesItem))
        {
            await next();
            return;
        }

        if (!CanViewSeries(context, seriesItem))
        {
            context.Result ??= EmptyResult();
            return;
        }

        // Get cached seasons
        var seasons = _itemCache.GetSeasonsForSeries(seriesId);
        if (seasons == null || seasons.Count == 0)
        {
            _logger.LogDebug("[Mediarow] No cached seasons for dynamic series {Id}", seriesId);
            // Return empty result rather than letting Jellyfin try to look it up
            context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
            {
                Items = Array.Empty<BaseItemDto>(),
                TotalRecordCount = 0
            });
            return;
        }

        _logger.LogDebug("[Mediarow] Returning {Count} cached seasons for dynamic series {Id}",
            seasons.Count, seriesId);

        SetChildrenResult(context, seriesItem, seasons);
    }

    private async Task HandleGetEpisodes(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Get series ID from action arguments
        if (!context.ActionArguments.TryGetValue("seriesId", out var seriesIdObj) || seriesIdObj is not Guid seriesId)
        {
            await next();
            return;
        }

        // Check if this is a dynamic series
        var seriesItem = _itemCache.GetItem(seriesId);
        if (seriesItem == null || !SearchResultFactory.IsDynamicItem(seriesItem))
        {
            await next();
            return;
        }

        if (!CanViewSeries(context, seriesItem))
        {
            context.Result ??= EmptyResult();
            return;
        }

        // Get season filter - can be seasonId (Guid) or season (int)
        int? seasonNumber = null;

        // First check for seasonId (Guid) - this is what Jellyfin passes when clicking a season
        if (context.ActionArguments.TryGetValue("seasonId", out var seasonIdObj) && seasonIdObj is Guid seasonId)
        {
            // Look up the season to get its number
            var seasonDto = _itemCache.GetItem(seasonId);
            if (seasonDto == null || seasonDto.SeriesId != seriesId || !CanViewSeries(context, seasonDto))
            { context.Result ??= new NotFoundResult(); return; }
            if (seasonDto?.IndexNumber != null)
            {
                seasonNumber = seasonDto.IndexNumber;
            }
        }
        // Also check for season number directly
        else if (context.ActionArguments.TryGetValue("season", out var seasonObj) && seasonObj is int sn)
        {
            seasonNumber = sn;
        }

        // Get cached episodes
        List<BaseItemDto>? episodes;
        if (seasonNumber.HasValue)
        {
            episodes = _itemCache.GetEpisodesForSeason(seriesId, seasonNumber.Value);
        }
        else
        {
            episodes = _itemCache.GetEpisodesForSeries(seriesId);
        }

        if (episodes == null || episodes.Count == 0)
        {
            _logger.LogDebug("[Mediarow] No cached episodes for dynamic series {Id} (season: {Season})",
                seriesId, seasonNumber?.ToString() ?? "all");
            // Return empty result
            context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
            {
                Items = Array.Empty<BaseItemDto>(),
                TotalRecordCount = 0
            });
            return;
        }

        episodes = VisibleChildren(context, seriesItem, episodes).ToList();
        int total = episodes.Count;
        if (context.Result != null) return;
        // Handle startItemId - Android TV uses this to indicate which episode to start from
        // The client expects the list to start from this item
        if (context.ActionArguments.TryGetValue("startItemId", out var startItemIdObj) && startItemIdObj is Guid startItemId)
        {
            var startIndex = episodes.FindIndex(e => e.Id == startItemId);
            if (startIndex > 0)
            {
                _logger.LogDebug("[Mediarow] Reordering episodes to start from {StartItemId} (index {Index})",
                    startItemId, startIndex);
                // Reorder: put the requested episode first, then the rest
                var reordered = new List<BaseItemDto>();
                reordered.AddRange(episodes.Skip(startIndex));
                reordered.AddRange(episodes.Take(startIndex));
                episodes = reordered;
            }
        }

        int pageOffset = context.ActionArguments.TryGetValue("startIndex", out var offsetObj) && offsetObj is int offset ? Math.Max(0, offset) : 0;
        episodes = episodes.Skip(pageOffset).ToList();
        // Handle limit parameter
        if (context.ActionArguments.TryGetValue("limit", out var limitObj) && limitObj is int limit && limit >= 0)
        {
            episodes = episodes.Take(limit).ToList();
        }

        // Log MediaSources info for first episode
        var firstEp = episodes.FirstOrDefault();
        _logger.LogDebug("[Mediarow] Returning {Count} cached episodes for dynamic series {Id} (season: {Season}). First episode '{Name}' HasMediaSources={HasSources}, Count={SourceCount}",
            episodes.Count, seriesId, seasonNumber?.ToString() ?? "all",
            firstEp?.Name ?? "N/A", firstEp?.MediaSources != null, firstEp?.MediaSources?.Length ?? 0);

        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = episodes.ToArray(),
            StartIndex = pageOffset,
            TotalRecordCount = total
        });
    }

    private async Task HandleGlobalNextUp(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        if (executed.Exception != null ||
            executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> result } ||
            result.Items.Count == 0 ||
            !TryGetUser(context, out var user))
        {
            return;
        }

        var resumeEpisodes = _seriesResumeResolver.FindResumeEpisodes(user);
        if (resumeEpisodes.Count == 0)
        {
            return;
        }

        // Jellyfin only advances Next Up after an episode is finished; show the episode the user is part-way through instead.
        var dtoOptions = new DtoOptions(true);
        result.Items = result.Items
            .Select(item => item.SeriesId is { } id && resumeEpisodes.TryGetValue(id, out var episode) && episode.Id != item.Id && episode.IsVisible(user, false) && episode.IsVisibleStandalone(user)
                ? _dtoService.GetBaseItemDto(episode, dtoOptions, user)
                : item)
            .ToArray();
    }

    private async Task HandleGetNextUp(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Check if seriesId is provided - this means we're getting next up for a specific series
        if (!context.ActionArguments.TryGetValue("seriesId", out var seriesIdObj) || seriesIdObj is not Guid seriesId)
        {
            await HandleGlobalNextUp(context, next);
            return;
        }

        var seriesItem = _itemCache.GetItem(seriesId);
        var isDynamic = seriesItem != null && SearchResultFactory.IsDynamicItem(seriesItem);
        if (isDynamic && !CanViewSeries(context, seriesItem!)) return;
        var librarySeries = isDynamic
            ? _persistenceService.FindExistingLibraryItem(seriesItem!)
            : _libraryManager.GetItemById(seriesId);

        if (librarySeries?.GetBaseItemKind() == BaseItemKind.Series)
        {
            if (TryGetUser(context, out var user))
            {
                if (!librarySeries.IsVisible(user, false) || !librarySeries.IsVisibleStandalone(user))
                { context.Result = new NotFoundResult(); return; }
                var resumeEpisode = _seriesResumeResolver.FindResumeEpisode(user, librarySeries);
                if (resumeEpisode != null && resumeEpisode.IsVisible(user, false) && resumeEpisode.IsVisibleStandalone(user))
                {
                    _logger.LogDebug("[Mediarow] HandleGetNextUp: Returning in-progress episode {Name} for series {Id}", resumeEpisode.Name, librarySeries.Id);
                    context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                    {
                        Items = [_dtoService.GetBaseItemDto(resumeEpisode, new DtoOptions(true), user)],
                        TotalRecordCount = 1
                    });
                    return;
                }
            }

            if (isDynamic)
            {
                // Let Jellyfin compute Next Up from the imported series' watch history.
                context.ActionArguments["seriesId"] = librarySeries.Id;
            }

            await next();
            return;
        }

        if (!isDynamic)
        {
            // Not a dynamic series, let Jellyfin handle it
            await next();
            return;
        }

        _logger.LogDebug("[Mediarow] HandleGetNextUp: Intercepting for dynamic series {Id}", seriesId);

        // For dynamic series, return the first episode of season 1 as "next up"
        // (since user hasn't watched any episodes yet)
        var episodes = _itemCache.GetEpisodesForSeason(seriesId, 1);
        if (episodes != null && episodes.Count > 0)
        {
            var firstEpisode = VisibleChildren(context, seriesItem!, episodes).OrderBy(e => e.IndexNumber ?? 0).FirstOrDefault();
            if (context.Result != null) return;
            if (firstEpisode == null) { context.Result = EmptyResult(); return; }
            _logger.LogDebug("[Mediarow] HandleGetNextUp: Returning first episode {Name} for dynamic series", firstEpisode.Name);

            context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
            {
                Items = new[] { firstEpisode },
                TotalRecordCount = 1
            });
            return;
        }

        // No episodes available, return empty result
        _logger.LogDebug("[Mediarow] HandleGetNextUp: No episodes found for dynamic series {Id}", seriesId);
        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = Array.Empty<BaseItemDto>(),
            TotalRecordCount = 0
        });
    }

    private async Task HandleGetItems(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Check for parentId parameter - Android TV uses this to get children of items
        // parentId can be Guid or Guid? (nullable) - when boxed, Guid? becomes Guid or null
        if (!context.ActionArguments.TryGetValue("parentId", out var parentIdObj) ||
            parentIdObj == null ||
            parentIdObj is not Guid parentId)
        {
            await next();
            return;
        }

        _logger.LogDebug("[Mediarow] HandleGetItems: Checking parentId {ParentId}", parentId);

        // Check if parent is a dynamic item
        var parentItem = _itemCache.GetItem(parentId);
        if (parentItem == null || !SearchResultFactory.IsDynamicItem(parentItem))
        {
            await next();
            return;
        }
        if (!CanViewSeries(context, parentItem)) return;
        if (parentItem.Type == BaseItemKind.Season)
        {
            var series = parentItem.SeriesId.HasValue ? _itemCache.GetItem(parentItem.SeriesId.Value) : null;
            if (series == null || !CanViewSeries(context, series))
            { context.Result ??= new NotFoundResult(); return; }
        }

        // If parent is a series, return seasons
        if (parentItem.Type == BaseItemKind.Series)
        {
            var seasons = _itemCache.GetSeasonsForSeries(parentId);
            if (seasons != null && seasons.Count > 0)
            {
                _logger.LogDebug("[Mediarow] Items/GetItems: Returning {Count} seasons for dynamic series {Id}",
                    seasons.Count, parentId);

                SetChildrenResult(context, parentItem, seasons);
                return;
            }
        }

        // If parent is a season, return episodes
        if (parentItem.Type == BaseItemKind.Season)
        {
            // Get the series ID and season number
            var seriesId = parentItem.SeriesId ?? Guid.Empty;
            var seasonNumber = parentItem.IndexNumber;

            if (seriesId != Guid.Empty && seasonNumber.HasValue)
            {
                var episodes = _itemCache.GetEpisodesForSeason(seriesId, seasonNumber.Value);
                if (episodes != null && episodes.Count > 0)
                {
                    _logger.LogDebug("[Mediarow] Items/GetItems: Returning {Count} episodes for dynamic season {SeasonNum} of series {SeriesId}",
                        episodes.Count, seasonNumber.Value, seriesId);

                    SetChildrenResult(context, parentItem, episodes);
                    return;
                }
            }
        }

        // Return empty result for dynamic items with no children
        _logger.LogDebug("[Mediarow] Items/GetItems: No children found for dynamic item {Id}", parentId);
        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = Array.Empty<BaseItemDto>(),
            TotalRecordCount = 0
        });
    }
}
