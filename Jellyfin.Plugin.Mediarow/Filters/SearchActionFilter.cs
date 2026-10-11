using Jellyfin.Plugin.Mediarow.Extensions;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Filters;

public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private static readonly TimeSpan CatalogWaitLimit = TimeSpan.FromSeconds(8);
    private readonly MediarowService _mediarowService;
    private readonly VirtualResumeTracker _virtualResumeTracker;
    private readonly ILogger<SearchActionFilter> _logger;

    public SearchActionFilter(
        MediarowService mediarowService,
        VirtualResumeTracker virtualResumeTracker,
        ILogger<SearchActionFilter> logger)
    {
        _mediarowService = mediarowService;
        _virtualResumeTracker = virtualResumeTracker;
        _logger = logger;
    }

    public int Order => 1;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Check if plugin is enabled
        if (!_mediarowService.IsEnabled)
        {
            await next();
            return;
        }

        // Check if this is an Items search. /Search/Hints returns a different result type that Mediarow does not merge.
        if (!context.IsApiSearchAction() || context.GetControllerName() == "Search")
        {
            await next();
            return;
        }

        // Check for searchTerm
        if (!context.TryGetActionArgument<string>("searchTerm", out var searchTerm)
            || string.IsNullOrWhiteSpace(searchTerm))
        {
            await next();
            return;
        }

        // Get requested item types
        var requestedTypes = context.GetRequestedItemTypes();
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        context.TryGetActionArgument("startIndex", out var startIndex, 0);
        var hasLimit = context.TryGetActionArgument("limit", out var limit, 0);
        startIndex = Math.Max(0, startIndex);
        limit = Math.Max(0, limit);

        // Start the catalog request before Jellyfin's local search so the two run concurrently.
        var dynamicSearch = SearchCatalogSafelyAsync(
            searchTerm,
            requestedTypes,
            context.HttpContext.RequestAborted);

        // Jellyfin pages its own results, so request everything through the end of this page and page the merge once.
        context.ActionArguments["startIndex"] = 0;
        if (hasLimit)
        {
            context.ActionArguments["limit"] = startIndex + limit;
        }

        var resultContext = await next();

        // Jellyfin returns ObjectResult, not OkObjectResult.
        if (resultContext.Exception is not null ||
            resultContext.Result is not ObjectResult objectResult ||
            objectResult.Value is not QueryResult<BaseItemDto> originalResults)
        {
            ObserveInBackground(dynamicSearch);
            return;
        }

        var dynamicResults = new List<BaseItemDto>();
        try
        {
            dynamicResults = await dynamicSearch
                .WaitAsync(CatalogWaitLimit, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveInBackground(dynamicSearch);
            _logger.LogInformation(
                "[Mediarow] Catalog search for '{Query}' exceeded {Seconds}s; returning Jellyfin results while it finishes",
                searchTerm,
                CatalogWaitLimit.TotalSeconds);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var filteredDynamicResults = new List<BaseItemDto>();
        try
        {
            filteredDynamicResults = CatalogDuplicateFilter.ExcludeKnown(dynamicResults, originalResults.Items);
            var userId = context.TryGetUserId(out var requestUserId) ? requestUserId : Guid.Empty;
            if (userId != Guid.Empty)
            {
                foreach (var result in filteredDynamicResults)
                {
                    var savedUserData = _virtualResumeTracker.GetUserData(userId, result.Id);
                    if (savedUserData != null)
                    {
                        result.UserData = savedUserData;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error merging dynamic search results for '{Query}'", searchTerm);
            filteredDynamicResults.Clear();
        }

        var mergedItems = originalResults.Items.Concat(filteredDynamicResults);
        var pagedItems = (hasLimit
                ? mergedItems.Skip(startIndex).Take(limit)
                : mergedItems.Skip(startIndex))
            .ToArray();
        var totalCount = Math.Max(originalResults.TotalRecordCount, originalResults.Items.Count) + filteredDynamicResults.Count;

        _logger.LogInformation(
            "Search for '{Query}' merged {OriginalCount} original + {DynamicCount} dynamic results (total: {Total})",
            searchTerm,
            originalResults.Items.Count,
            filteredDynamicResults.Count,
            totalCount);

        resultContext.Result = new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = pagedItems,
            StartIndex = startIndex,
            TotalRecordCount = totalCount
        });
    }

    private static void ObserveInBackground(Task task)
    {
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task<List<BaseItemDto>> SearchCatalogSafelyAsync(
        string searchTerm,
        HashSet<Jellyfin.Data.Enums.BaseItemKind> requestedTypes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _mediarowService.SearchAsync(searchTerm, requestedTypes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new List<BaseItemDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching dynamic catalog for '{Query}'", searchTerm);
            return new List<BaseItemDto>();
        }
    }
}
