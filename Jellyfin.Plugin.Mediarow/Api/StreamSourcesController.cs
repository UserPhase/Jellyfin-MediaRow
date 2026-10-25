using Jellyfin.Plugin.Mediarow.Providers;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Api;

public sealed record StreamSourceSummary(string Id, string Name);

public sealed record StreamRefreshResponse(Guid ItemId, int Count, DateTimeOffset RefreshedAt, IReadOnlyList<StreamSourceSummary> Sources);

/// <summary>"Refresh Sources": asks AIOStreams again for a title, bypassing the cached (possibly expired) stream list.</summary>
[ApiController]
[Authorize]
[Route("Mediarow/Streams")]
public sealed class StreamSourcesController(
    DynamicAioStreamsMediaSourceProvider provider,
    ILibraryManager libraryManager,
    IUserManager userManager,
    UserPermissionGuard guard,
    ILogger<StreamSourcesController> logger) : ControllerBase
{
    [HttpPost("{itemId:guid}/Refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StreamRefreshResponse>> Refresh(Guid itemId, CancellationToken cancellationToken)
    {
        var access = guard.Resolve(User);
        if (!access.Succeeded)
        {
            return access.Failure == UserAccessFailure.Unauthenticated ? Unauthorized() : Forbid();
        }

        var user = userManager.GetUserById(access.Context!.UserId);
        var item = libraryManager.GetItemById(itemId);
        // Hidden items answer like missing ones, so the endpoint cannot be used to probe another user's library.
        if (user == null || item == null || !item.IsVisibleStandalone(user) || !provider.IsMediarowItem(item))
        {
            return NotFound(new { Error = "Title not found." });
        }

        try
        {
            var sources = (await provider.GetMediaSourcesAsync(item, true, cancellationToken)).ToList();
            return new StreamRefreshResponse(
                item.Id,
                sources.Count,
                DateTimeOffset.UtcNow,
                sources.Select(source => new StreamSourceSummary(source.Id, source.Name)).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            logger.LogWarning("[Mediarow] Refreshing sources failed for item {ItemId} ({ErrorType})", itemId, ex.GetType().Name);
            return StatusCode(StatusCodes.Status502BadGateway, new { Error = "AIOStreams could not refresh the streams. Retry later." });
        }
    }
}
