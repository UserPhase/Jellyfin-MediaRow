using System.Security.Claims;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Mediarow.Filters;

/// <summary>
/// Serves Mediarow app libraries through Jellyfin's standard library endpoints so native and TV apps
/// (Android TV, Swiftfin, Findroid, Kodi, bundled Tizen/webOS web apps) can browse the catalog rows.
/// </summary>
public sealed class AppLibraryFilter : IAsyncActionFilter, IOrderedFilter
{
    public const string WebClientCookie = "mediarow-web";

    private readonly AppLibraryService _libraries;

    public AppLibraryFilter(AppLibraryService libraries)
    {
        _libraries = libraries;
    }

    // Must run before ItemLookupFilter, SeasonEpisodeFilter and ImageFilter.
    public int Order => -1;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor || !AppLibraryService.IsEnabled)
        {
            await next();
            return;
        }

        string controller = descriptor.ControllerName;
        string action = descriptor.ActionName;
        CancellationToken cancellationToken = context.HttpContext.RequestAborted;

        if (controller == "UserViews" && action is "GetUserViews" or "GetUserViewsLegacy")
        {
            await AppendViewsAsync(context, next);
            return;
        }

        if (controller is "UserLibrary" or "Items" && action is "GetItem" or "GetItemLegacy")
        {
            AppLibraryDefinition? library = FindLibrary(context, "itemId");
            if (library != null)
            {
                context.Result = new OkObjectResult(_libraries.CreateView(library));
                return;
            }
        }
        else if (controller == "UserLibrary" && action is "GetLatestMedia" or "GetLatestMediaLegacy")
        {
            AppLibraryDefinition? library = FindLibrary(context, "parentId");
            if (library != null)
            {
                IReadOnlyList<BaseItemDto> items = await _libraries.GetItemsAsync(GetUserId(context), library, cancellationToken);
                context.Result = new OkObjectResult(GetLatest(items, context.ActionArguments));
                return;
            }
        }
        else if (controller == "Items" && action is "GetItems" or "GetItemsByUserIdLegacy")
        {
            AppLibraryDefinition? library = FindLibrary(context, "parentId");
            if (library != null)
            {
                IReadOnlyList<BaseItemDto> items = await _libraries.GetItemsAsync(GetUserId(context), library, cancellationToken);
                context.Result = new OkObjectResult(QueryItems(items, context.ActionArguments));
                return;
            }
        }
        else if (controller == "Image" && action.StartsWith("GetItemImage", StringComparison.Ordinal))
        {
            AppLibraryDefinition? library = FindLibrary(context, "itemId");
            if (library != null)
            {
                IReadOnlyList<BaseItemDto> items = await _libraries.GetItemsAsync(GetUserId(context), library, cancellationToken);
                if (items.Count == 0)
                {
                    context.Result = new NotFoundResult();
                    return;
                }

                // The library tile shows the backdrop of its first title; ImageFilter proxies it.
                context.ActionArguments["itemId"] = items[0].Id;
                context.ActionArguments["imageType"] = ImageType.Backdrop;
            }
        }

        await next();
    }

    private async Task AppendViewsAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        bool presetViewsRequested = context.ActionArguments.TryGetValue("presetViews", out object? preset) &&
            preset is Array { Length: > 0 };
        bool isMediarowWebClient = context.HttpContext.Request.Cookies.TryGetValue(WebClientCookie, out string? cookie) &&
            cookie == "1";
        ActionExecutedContext executed = await next();
        if (presetViewsRequested || (isMediarowWebClient && !AppLibraryService.ShowInWeb) ||
            executed.Exception != null || executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> views } result)
        {
            return;
        }

        IReadOnlyList<AppLibraryDefinition> libraries = _libraries.GetLibraries(GetUserId(context));
        if (libraries.Count > 0)
        {
            result.Value = new QueryResult<BaseItemDto>((views.Items ?? Array.Empty<BaseItemDto>())
                .Concat(libraries.Select(_libraries.CreateView))
                .ToArray());
        }
    }

    private AppLibraryDefinition? FindLibrary(ActionExecutingContext context, string argument)
    {
        if (!context.ActionArguments.TryGetValue(argument, out object? value))
        {
            return null;
        }

        Guid? id = value as Guid?;
        return id.HasValue ? _libraries.Find(GetUserId(context), id.Value) : null;
    }

    private static Guid GetUserId(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("userId", out object? value) && value is Guid userId && userId != Guid.Empty)
        {
            return userId;
        }

        string? claim = context.HttpContext.User.FindFirstValue("Jellyfin-UserId");
        return Guid.TryParse(claim, out Guid claimedUserId) ? claimedUserId : Guid.Empty;
    }

    public static BaseItemDto[] GetLatest(IReadOnlyList<BaseItemDto> items, IDictionary<string, object?> arguments)
    {
        if (Get<bool?>(arguments, "isPlayed") == true)
        {
            return Array.Empty<BaseItemDto>();
        }

        BaseItemKind[] include = Get<BaseItemKind[]>(arguments, "includeItemTypes") ?? Array.Empty<BaseItemKind>();
        int limit = Math.Clamp(Get<int?>(arguments, "limit") ?? 20, 1, 100);
        return items.Where(item => MatchesTypes(item, include, Array.Empty<BaseItemKind>())).Take(limit).ToArray();
    }

    public static QueryResult<BaseItemDto> QueryItems(IReadOnlyList<BaseItemDto> items, IDictionary<string, object?> arguments)
    {
        ItemFilter[] filters = Get<ItemFilter[]>(arguments, "filters") ?? Array.Empty<ItemFilter>();
        bool personalOnly = Get<bool?>(arguments, "isFavorite") == true ||
            Get<bool?>(arguments, "isPlayed") == true ||
            filters.Any(filter => filter is ItemFilter.IsFavorite or ItemFilter.IsFavoriteOrLikes or ItemFilter.Likes
                or ItemFilter.IsPlayed or ItemFilter.IsResumable);
        int startIndex = Math.Max(0, Get<int?>(arguments, "startIndex") ?? 0);
        if (personalOnly)
        {
            return new QueryResult<BaseItemDto>(startIndex, 0, Array.Empty<BaseItemDto>());
        }

        BaseItemKind[] include = Get<BaseItemKind[]>(arguments, "includeItemTypes") ?? Array.Empty<BaseItemKind>();
        BaseItemKind[] exclude = Get<BaseItemKind[]>(arguments, "excludeItemTypes") ?? Array.Empty<BaseItemKind>();
        string[] genres = Get<string[]>(arguments, "genres") ?? Array.Empty<string>();
        int[] years = Get<int[]>(arguments, "years") ?? Array.Empty<int>();
        string? searchTerm = Get<string>(arguments, "searchTerm");
        string? nameStartsWith = Get<string>(arguments, "nameStartsWith");
        string? nameStartsWithOrGreater = Get<string>(arguments, "nameStartsWithOrGreater");
        string? nameLessThan = Get<string>(arguments, "nameLessThan");

        IEnumerable<BaseItemDto> query = items
            .Where(item => MatchesTypes(item, include, exclude))
            .Where(item => genres.Length == 0 || (item.Genres ?? Array.Empty<string>())
                .Any(genre => genres.Contains(genre, StringComparer.OrdinalIgnoreCase)))
            .Where(item => years.Length == 0 || (item.ProductionYear.HasValue && years.Contains(item.ProductionYear.Value)))
            .Where(item => string.IsNullOrWhiteSpace(searchTerm) ||
                (item.Name ?? string.Empty).Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrEmpty(nameStartsWith) ||
                SortKey(item).StartsWith(nameStartsWith, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrEmpty(nameStartsWithOrGreater) ||
                string.Compare(SortKey(item), nameStartsWithOrGreater, StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(item => string.IsNullOrEmpty(nameLessThan) ||
                string.Compare(SortKey(item), nameLessThan, StringComparison.OrdinalIgnoreCase) < 0);

        BaseItemDto[] matches = Sort(query, Get<ItemSortBy[]>(arguments, "sortBy"), Get<SortOrder[]>(arguments, "sortOrder")).ToArray();
        int? limit = Get<int?>(arguments, "limit");
        BaseItemDto[] page = matches.Skip(startIndex).Take(limit is > 0 ? limit.Value : int.MaxValue).ToArray();
        return new QueryResult<BaseItemDto>(startIndex, matches.Length, page);
    }

    private static IEnumerable<BaseItemDto> Sort(IEnumerable<BaseItemDto> items, ItemSortBy[]? sortBy, SortOrder[]? sortOrder)
    {
        ItemSortBy primary = sortBy is { Length: > 0 } ? sortBy[0] : ItemSortBy.Default;
        bool descending = sortOrder is { Length: > 0 } && sortOrder[0] == SortOrder.Descending;
        if (primary is ItemSortBy.SortName or ItemSortBy.Name)
        {
            return descending
                ? items.OrderByDescending(SortKey, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(SortKey, StringComparer.OrdinalIgnoreCase);
        }

        if (primary == ItemSortBy.Random)
        {
            return items.OrderBy(_ => Random.Shared.Next());
        }

        Func<BaseItemDto, double>? key = primary switch
        {
            ItemSortBy.ProductionYear => item => item.ProductionYear ?? 0,
            ItemSortBy.PremiereDate => item => (item.PremiereDate ?? DateTime.MinValue).Ticks,
            ItemSortBy.CommunityRating => item => item.CommunityRating ?? 0,
            ItemSortBy.CriticRating => item => item.CriticRating ?? 0,
            _ => null
        };

        // Otherwise keep catalog order: it is already "popular first", which suits "latest"/"date added" views.
        return key == null ? items : descending ? items.OrderByDescending(key) : items.OrderBy(key);
    }

    private static string SortKey(BaseItemDto item)
    {
        return item.SortName ?? item.Name ?? string.Empty;
    }

    private static bool MatchesTypes(BaseItemDto item, BaseItemKind[] include, BaseItemKind[] exclude)
    {
        // Clients asking a TV library for episodes or seasons expect the parent series in "Latest" rows.
        BaseItemKind type = item.Type;
        bool included = include.Length == 0 || include.Contains(type) ||
            (type == BaseItemKind.Series && (include.Contains(BaseItemKind.Episode) || include.Contains(BaseItemKind.Season))) ||
            (type == BaseItemKind.Movie && include.Contains(BaseItemKind.Video));
        return included && !exclude.Contains(type);
    }

    private static T? Get<T>(IDictionary<string, object?> arguments, string name)
    {
        return arguments.TryGetValue(name, out object? value) && value is T typed ? typed : default;
    }
}
