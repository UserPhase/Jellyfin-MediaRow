using Jellyfin.Plugin.Mediarow.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarow;

public class MediarowPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<MediarowPlugin> _logger;

    public MediarowPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<MediarowPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;
        _logger.LogInformation("Mediarow plugin loaded");
    }

    public static MediarowPlugin? Instance { get; private set; }

    public override string Name => "Jellyfin Mediarow";

    public override Guid Id => Guid.Parse("68CFF731-4588-4AB3-AA0D-9CA73648679A");

    public override string Description =>
        "Netflix-style Home screen rows for Jellyfin from Stremio catalogs, with native AIOStreams playback and optional .strm persistence.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = "Jellyfin Mediarow",
            EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html",
        };
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        _logger.LogInformation("Mediarow configuration updated");
    }
}
