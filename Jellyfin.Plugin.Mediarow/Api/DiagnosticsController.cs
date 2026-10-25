using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Mediarow.Api;

/// <summary>Administrator-only "Test configuration" endpoints used by the plugin settings page.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Mediarow/Diagnostics")]
public sealed class DiagnosticsController(PluginDiagnosticsService diagnostics) : ControllerBase
{
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<DiagnosticsReport>> Run(
        [FromQuery] bool probeStream = true,
        [FromQuery] string? imdbId = null,
        CancellationToken cancellationToken = default)
    {
        var options = new DiagnosticsOptions { ProbeStream = probeStream };
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            options = options with { ImdbId = imdbId.Trim() };
        }

        var report = await diagnostics.TryRunAsync(options, cancellationToken);
        return report == null
            ? StatusCode(StatusCodes.Status429TooManyRequests, new { Error = "A configuration test is already running." })
            : report;
    }

    [HttpGet("Last")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult<DiagnosticsReport> Last() =>
        diagnostics.LastReport is { } report ? report : NoContent();
}
