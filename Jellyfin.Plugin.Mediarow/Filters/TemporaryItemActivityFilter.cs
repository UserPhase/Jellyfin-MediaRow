using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Mediarow.Filters;

/// <summary>Uses standard native-client requests, without injected client scripts, to renew title activity.</summary>
public sealed class TemporaryItemActivityFilter(
    TemporaryItemActivity activity, ILibraryManager library, DynamicItemCache cache,
    PersistenceService persistence, UserPermissionGuard guard, IUserManager users) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => -1;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var config = MediarowPlugin.Instance?.Configuration;
        bool enabled = config?.Enabled == true &&
            (config.CleanupUnsavedItems || config.CleanupWatchedMovies || config.CleanupCompletedSeries);
        activity.SetEnabled(enabled);
        if (!enabled || context.ActionDescriptor is not ControllerActionDescriptor descriptor || !IsRelevant(descriptor))
        { await next(); return; }

        var requestedUser = Id(context, "userId") ?? Guid.Empty;
        var resolved = guard.Resolve(context.HttpContext.User, requestedUser);
        var user = resolved.Succeeded ? users.GetUserById(resolved.Context!.UserId) : null;
        if (user == null) { await next(); return; } // Native/shared authorization retains control of the response.
        var ids = new[] { "itemId", "routeItemId", "seriesId", "parentId", "seasonId" }
            .Select(key => Id(context, key)).OfType<Guid>().ToList();
        if (context.ActionArguments.Values.OfType<PlaybackProgressInfo>().FirstOrDefault() is { } progress) ids.Add(progress.ItemId);
        if (context.ActionArguments.Values.OfType<PlaybackStopInfo>().FirstOrDefault() is { } stopped) ids.Add(stopped.ItemId);

        var leases = new List<IDisposable>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            foreach (var id in ids.Distinct())
            {
                string? path = null;
                if (library.GetItemById(id) is { } native)
                {
                    if (native.IsVisibleStandalone(user)) path = native.Path;
                }
                else if (cache.GetItem(id) is { } item &&
                    item.Type is BaseItemKind.Movie or BaseItemKind.Series &&
                    guard.CanView(resolved.Context!, item))
                {
                    path = item.Type == BaseItemKind.Movie ? persistence.GetMovieFilePath(item) : persistence.GetSeriesFolderPath(item);
                }
                var title = TemporaryItemActivity.GetTitlePath(config!, path);
                if (title != null && paths.Add(title)) leases.Add(activity.BeginUse(title));
            }
            await next();
        }
        finally
        {
            // Includes Stop and Unfavorite: the complete grace period starts after the interaction finishes.
            foreach (var lease in leases) lease.Dispose();
        }
    }

    private static Guid? Id(ActionExecutingContext context, string key) =>
        context.ActionArguments.TryGetValue(key, out var value) &&
        (value is Guid || value is string) && Guid.TryParse(value.ToString(), out var id) && id != Guid.Empty ? id : null;

    private static bool IsRelevant(ControllerActionDescriptor action) => action.ControllerName switch
    {
        "UserLibrary" => action.ActionName is "GetItem" or "GetItemLegacy" or
            "MarkFavoriteItem" or "MarkFavoriteItemLegacy" or "UnmarkFavoriteItem" or "UnmarkFavoriteItemLegacy",
        "Items" => action.ActionName is "GetItem" or "GetItems" or "UpdateItemUserData" or "UpdateItemUserDataLegacy",
        "TvShows" => action.ActionName is "GetSeasons" or "GetEpisodes" or "GetNextUp",
        "Mediarow" => action.ActionName is "GetItem" or "PersistItem" or "ResolvePlaybackItem",
        "MediaInfo" => action.ActionName is "GetPlaybackInfo" or "GetPostedPlaybackInfo" or "OpenLiveStream",
        "Session" => action.ActionName == "ReportViewing",
        "Playstate" => action.ActionName.StartsWith("ReportPlayback", StringComparison.Ordinal) ||
            action.ActionName.StartsWith("OnPlayback", StringComparison.Ordinal),
        _ => false
    };
}
