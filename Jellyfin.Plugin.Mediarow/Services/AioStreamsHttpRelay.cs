using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>Private loopback tickets let Jellyfin probe streams requiring arbitrary HTTP headers.</summary>
public sealed class AioStreamsHttpRelay(IMemoryCache cache, IServerApplicationHost host)
{
    public string BaseUrl => host.GetApiUrlForLocalAccess(IPAddress.Loopback, allowHttps: false).TrimEnd('/');

    public string Register(string url, IReadOnlyDictionary<string, string> headers, bool isManifest = false)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" +
            string.Join("\n", headers.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")))));
        if (!cache.TryGetValue<string>("aio-relay-id:" + identity, out var ticket) || ticket == null)
        {
            ticket = Guid.NewGuid().ToString("N");
            cache.Set("aio-relay-id:" + identity, ticket, TimeSpan.FromHours(6));
        }

        cache.Set("aio-relay:" + ticket, new RelayTarget(url, new Dictionary<string, string>(headers),
            isManifest || new Uri(url).AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)),
            new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(6), AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) });
        var extension = Path.GetExtension(new Uri(url).AbsolutePath);
        if (extension.Length > 12 || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            extension = string.Empty;
        }
        if (isManifest && extension.Length == 0) extension = ".m3u8";
        // FFmpeg validates HLS segment extensions before requesting their content.
        return $"{BaseUrl}/Mediarow/AIOStreams/Source/{ticket}{extension}";
    }

    public RelayTarget? Get(string ticket) => cache.TryGetValue<RelayTarget>("aio-relay:" + ticket.Split('.', 2)[0], out var target) ? target : null;

    public string RewriteManifest(string manifest, Uri origin, IReadOnlyDictionary<string, string> headers)
    {
        string Resolve(string value)
        {
            if (!Uri.TryCreate(origin, value, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException("The HLS manifest contains an unsupported media URI.");
            }
            return Register(uri.AbsoluteUri, headers);
        }

        return string.Join("\n", manifest.Split('\n').Select(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return line;
            return trimmed.StartsWith('#')
                ? Regex.Replace(line, "URI=\"([^\"]+)\"", match => $"URI=\"{Resolve(match.Groups[1].Value)}\"")
                : Resolve(trimmed);
        }));
    }

    public sealed record RelayTarget(string Url, IReadOnlyDictionary<string, string> Headers, bool IsManifest);
}
