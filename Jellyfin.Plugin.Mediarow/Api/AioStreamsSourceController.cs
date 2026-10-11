using System.Net;
using Jellyfin.Plugin.Mediarow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow.Api;

[ApiController]
[Route("Mediarow/AIOStreams/Source")]
public sealed class AioStreamsSourceController(
    AioStreamsHttpRelay relay, IHttpClientFactory clientFactory, ILogger<AioStreamsSourceController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("{ticket}")]
    [HttpHead("{ticket}")]
    public async Task<IActionResult> GetSource(string ticket, CancellationToken cancellationToken)
    {
        var address = HttpContext.Connection.RemoteIpAddress;
        if (address == null || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) ||
            !Guid.TryParseExact(ticket.Split('.', 2)[0], "N", out _))
        {
            return NotFound();
        }

        var target = relay.Get(ticket);
        if (target == null) return NotFound();
        using var client = clientFactory.CreateClient(nameof(AioStreamsSourceController));
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get, target.Url);
        foreach (var header in target.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (!target.IsManifest && Request.Headers.TryGetValue("Range", out var range))
        {
            request.Headers.Remove("Range");
            request.Headers.TryAddWithoutValidation("Range", range.ToString());
        }
        if (Request.Headers.TryGetValue("If-Range", out var ifRange))
        {
            request.Headers.Remove("If-Range");
            request.Headers.TryAddWithoutValidation("If-Range", ifRange.ToString());
        }
        try
        {
            using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var contentType = upstream.Content.Headers.ContentType?.MediaType;
            var isManifest = target.IsManifest ||
                contentType is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl";
            if (!HttpMethods.IsHead(Request.Method) && upstream.IsSuccessStatusCode && isManifest)
            {
                var manifest = await upstream.Content.ReadAsStringAsync(cancellationToken);
                var rewritten = relay.RewriteManifest(manifest, upstream.RequestMessage?.RequestUri ?? new Uri(target.Url), target.Headers);
                return Content(rewritten, "application/vnd.apple.mpegurl");
            }
            Response.StatusCode = (int)upstream.StatusCode;
            foreach (var name in new[] { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified" })
            {
                if (isManifest && name == "Content-Length") continue;
                if (upstream.Content.Headers.TryGetValues(name, out var values) || upstream.Headers.TryGetValues(name, out values))
                {
                    Response.Headers[name] = string.Join(", ", values);
                }
            }

            if (!upstream.IsSuccessStatusCode)
            {
                logger.LogWarning("[Mediarow] Header-dependent source returned HTTP {Status}", (int)upstream.StatusCode);
            }
            if (!HttpMethods.IsHead(Request.Method))
            {
                await upstream.Content.CopyToAsync(Response.Body, cancellationToken);
            }
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("[Mediarow] Header-dependent source relay failed");
            if (Response.HasStarted)
            {
                HttpContext.Abort();
                return new EmptyResult();
            }
            return StatusCode(502);
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("[Mediarow] Header-dependent HLS manifest could not be rewritten");
            return StatusCode(502);
        }
    }
}
