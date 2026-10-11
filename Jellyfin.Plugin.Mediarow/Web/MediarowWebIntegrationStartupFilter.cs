using System.Text;
using System.IO.Compression;
using System.Net;
using Jellyfin.Plugin.Mediarow.Filters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Mediarow.Web;

/// <summary>
/// Injects Mediarow's own Home row assets into Jellyfin Web.
/// </summary>
public sealed class MediarowWebIntegrationStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return applicationBuilder =>
        {
            applicationBuilder.Use(async (context, nextMiddleware) =>
            {
                if (!IsWebIndexRequest(context.Request))
                {
                    await nextMiddleware();
                    return;
                }

                Stream originalBody = context.Response.Body;
                string webBasePath = GetWebBasePath(context.Request);
                await using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await nextMiddleware();
                    buffer.Position = 0;
                    string html = await ReadResponseTextAsync(buffer, context.Response.Headers.ContentEncoding);
                    string updatedHtml = InjectAssets(html, webBasePath);
                    context.Response.Body = originalBody;
                    byte[] responseBytes = EncodeResponseText(updatedHtml, context.Response.Headers.ContentEncoding);
                    context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                    context.Response.Headers.Pragma = "no-cache";
                    context.Response.ContentLength = responseBytes.Length;
                    if (updatedHtml.Contains("mediarow-home.js", StringComparison.OrdinalIgnoreCase))
                    {
                        // Marks clients running the injected rows so they do not also get the app libraries.
                        context.Response.Cookies.Append(AppLibraryFilter.WebClientCookie, "1", new CookieOptions
                        {
                            Path = "/",
                            HttpOnly = true,
                            IsEssential = true,
                            SameSite = SameSiteMode.Lax,
                            MaxAge = TimeSpan.FromDays(365)
                        });
                    }

                    await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);
                }
                finally
                {
                    context.Response.Body = originalBody;
                }
            });

            next(applicationBuilder);
        };
    }

    private static bool IsWebIndexRequest(HttpRequest request)
    {
        return request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            (request.Path == "/" ||
             request.Path.Value?.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) == true ||
             request.Path.Value?.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string GetWebBasePath(HttpRequest request)
    {
        // Capture the full path before Jellyfin's BaseUrl mapping temporarily changes PathBase.
        string path = request.PathBase.Add(request.Path).ToUriComponent();
        foreach (string suffix in new[] { "/web/index.html", "/web/", "/index.html" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^suffix.Length];
            }
        }

        return path.TrimEnd('/');
    }

    private static string InjectAssets(string html, string webBasePath)
    {
        const string marker = "</head>";
        if (!html.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            html.Contains("mediarow-home.js", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        string cacheKey = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string assetPath = WebUtility.HtmlEncode(webBasePath + "/Mediarow/Home/");
        string assets = $"<link rel=\"stylesheet\" href=\"{assetPath}mediarow-home.css?v={cacheKey}\" />" +
            $"<script defer src=\"{assetPath}mediarow-home.js?v={cacheKey}\"></script>";
        int index = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return html.Insert(index, assets);
    }

    private static async Task<string> ReadResponseTextAsync(Stream responseBody, string? contentEncoding)
    {
        Stream source = responseBody;
        if (contentEncoding?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true)
        {
            source = new GZipStream(responseBody, CompressionMode.Decompress, leaveOpen: true);
        }
        else if (contentEncoding?.Contains("br", StringComparison.OrdinalIgnoreCase) == true)
        {
            source = new BrotliStream(responseBody, CompressionMode.Decompress, leaveOpen: true);
        }

        using (source)
        {
            using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadToEndAsync();
        }
    }

    private static byte[] EncodeResponseText(string html, string? contentEncoding)
    {
        byte[] text = Encoding.UTF8.GetBytes(html);
        if (string.IsNullOrWhiteSpace(contentEncoding))
        {
            return text;
        }

        using var output = new MemoryStream();
        Stream compressor = contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)
            : contentEncoding.Contains("br", StringComparison.OrdinalIgnoreCase)
                ? new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)
                : output;
        using (compressor)
        {
            compressor.Write(text, 0, text.Length);
        }

        return output.ToArray();
    }
}
