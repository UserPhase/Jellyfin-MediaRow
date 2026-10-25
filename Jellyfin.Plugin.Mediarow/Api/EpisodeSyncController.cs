using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Mediarow.Api;

/// <summary>
/// Administrator controls for the background episode sync.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Mediarow/EpisodeSync")]
public sealed class EpisodeSyncController : ControllerBase
{
    private readonly EpisodeSyncService _sync;
    private readonly ILibraryManager _libraryManager;

    public EpisodeSyncController(EpisodeSyncService sync, ILibraryManager libraryManager)
    {
        _sync = sync;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Checks one series (<paramref name="seriesId"/>) or every tracked series now. <paramref name="force"/> ignores
    /// the due-time policy.
    /// </summary>
    [HttpPost("Run")]
    public async Task<IActionResult> Run([FromQuery] Guid? seriesId, [FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        if (seriesId is { } id && id != Guid.Empty)
        {
            if (_libraryManager.GetItemById(id) is not Series series)
            {
                return NotFound(new { Error = "Series not found." });
            }

            var result = await _sync.SyncSeriesAsync(series, EpisodeSyncTrigger.Manual, cancellationToken);
            return result == null
                ? BadRequest(new { Error = "This series was not added by Mediarow." })
                : Ok(result);
        }

        return Ok(await _sync.SyncAllAsync(null, force, cancellationToken));
    }

    [HttpGet("Status")]
    public IActionResult Status()
    {
        var state = _sync.GetState();
        return Ok(new
        {
            _sync.IsSweeping,
            _sync.LastSweepUtc,
            _sync.LastSummary,
            Series = state
                .Select(pair => new { SeriesId = pair.Key, pair.Value.Name, pair.Value.LastCheckedUtc, pair.Value.LastNewEpisodeUtc, pair.Value.NextAirDateUtc, pair.Value.Status, pair.Value.KnownEpisodeCount, pair.Value.ConsecutiveFailures, pair.Value.LastError })
                .OrderBy(entry => entry.Name)
                .ToList()
        });
    }
}
