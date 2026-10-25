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
    AioStreamsHttpRelay relay, IHttpClientFactory clientFactory, ILogger<AioStreamsSourceController> logger,
    StreamResolverService? resolver = null) : ControllerBase
{
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(300);

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
        try
        {
            HttpResponseMessage upstream;
            var recovered = false;
            var retried = false;
            while (true)
            {
                using var request = BuildRequest(target);
                try
                {
                    upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (HttpRequestException) when (!retried)
                {
                    retried = true;
                    logger.LogDebug("[Mediarow] Relayed source connection failed; retrying once");
                    await Task.Delay(TransientRetryDelay, cancellationToken);
                    continue;
                }

                var status = (int)upstream.StatusCode;
                if (status is 401 or 403 or 404 or 410 && !recovered && target.Recovery != null && resolver != null)
                {
                    // Debrid links are signed for a limited time; a long pause or seek after expiry gets 403/404.
                    recovered = true;
                    var fresh = await TryRecoverAsync(target.Recovery, cancellationToken);
                    if (fresh != null && !fresh.Url.Equals(target.Url, StringComparison.Ordinal))
                    {
                        upstream.Dispose();
                        target = target with { Url = fresh.Url, Headers = fresh.Headers };
                        relay.Replace(ticket, target);
                        logger.LogInformation("[Mediarow] Relayed source returned HTTP {Status}; switched to a freshly resolved link", status);
                        continue;
                    }
                }
                else if (status is 408 or 429 or >= 500 && !retried)
                {
                    retried = true;
                    upstream.Dispose();
                    await Task.Delay(TransientRetryDelay, cancellationToken);
                    continue;
                }

                break;
            }

            using var response = upstream;
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

    private HttpRequestMessage BuildRequest(AioStreamsHttpRelay.RelayTarget target)
    {
        // An HttpRequestMessage can be sent only once, so every retry builds a new one.
        var request = new HttpRequestMessage(HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get, target.Url);
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
        return request;
    }

    private async Task<StreamCandidate?> TryRecoverAsync(StreamRecoveryKey key, CancellationToken cancellationToken)
    {
        try
        {
            return await resolver!.RecoverAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            logger.LogWarning("[Mediarow] Could not re-resolve an expired relayed source ({ErrorType})", ex.GetType().Name);
            return null;
        }
    }
}
