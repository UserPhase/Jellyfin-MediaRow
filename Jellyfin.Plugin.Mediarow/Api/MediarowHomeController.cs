using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Mediarow.Api;

/// <summary>
/// Server-owned Home section API for the Mediarow web client.
/// </summary>
[ApiController]
[Authorize]
[Route("Mediarow/Home")]
public sealed class MediarowHomeController : ControllerBase
{
    private readonly HomeSectionManager _manager;
    private readonly HomeSectionUserSettingsStore _userSettings;
    private readonly HomeSectionBootstrapper _bootstrapper;

    public MediarowHomeController(
        HomeSectionManager manager,
        HomeSectionUserSettingsStore userSettings,
        HomeSectionBootstrapper bootstrapper)
    {
        _manager = manager;
        _userSettings = userSettings;
        _bootstrapper = bootstrapper;
    }

    [AllowAnonymous]
    [HttpGet("mediarow-home.js")]
    public IActionResult GetHomeScript()
    {
        return GetEmbeddedAsset("mediarow-home.js", "application/javascript");
    }

    [AllowAnonymous]
    [HttpGet("mediarow-home.css")]
    public IActionResult GetHomeStyles()
    {
        string css = ReadEmbeddedText("mediarow-home.css") ?? string.Empty;
        if (MediarowPlugin.Instance?.Configuration.NetflixStylePlayerButtons ?? true)
        {
            css += "\n" + ReadEmbeddedText("mediarow-player.css");
        }

        return Content(css, "text/css");
    }

    private static string? ReadEmbeddedText(string assetName)
    {
        using Stream? stream = typeof(MediarowHomeController).Assembly.GetManifestResourceStream($"Jellyfin.Plugin.Mediarow.Web.{assetName}");
        if (stream == null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [HttpGet("Sections")]
    public HomeSectionsResult GetSections([FromQuery] Guid userId)
    {
        _bootstrapper.RegisterConfiguredSections();
        IReadOnlySet<string>? enabled = _userSettings.Get(userId);
        HomeSectionInfo[] sections = _manager.GetSections()
            .Where(section => enabled == null || enabled.Contains(section.Id))
            .Select(section => new HomeSectionInfo
            {
                Id = section.Id,
                DisplayTitle = section.DisplayTitle,
                Order = section.Order,
                Limit = section.Limit,
                Route = section.Route,
                AdditionalData = section.AdditionalData
            })
            .OrderBy(section => section.Order)
            .ToArray();
        return new HomeSectionsResult(sections)
        {
            ContentCategories = HomeRowContentFilter.GetCategories(
                MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration())
        };
    }

    [HttpGet("Section/{id}")]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetSection(
        [FromRoute] string id,
        [FromQuery] Guid userId,
        [FromQuery] string? language = null,
        [FromQuery] string? contentCategory = null,
        CancellationToken cancellationToken = default)
    {
        if (contentCategory != null && contentCategory is not ("Movies" or "Series" or "Anime"))
        {
            return BadRequest(new { Error = "Content category must be Movies, Series, or Anime." });
        }
        _bootstrapper.RegisterConfiguredSections();
        IReadOnlyDictionary<string, string> parameters = Request.Query
            .Where(pair => !pair.Key.Equals("userId", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Equals("language", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return await _manager.ExecuteAsync(id, new HomeSectionRequest
        {
            UserId = userId,
            Language = language,
            Parameters = parameters
        }, cancellationToken);
    }

    [HttpGet("UserSettings")]
    public object GetUserSettings([FromQuery] Guid userId)
    {
        _bootstrapper.RegisterConfiguredSections();
        IReadOnlySet<string>? saved = _userSettings.Get(userId);
        return new
        {
            UserId = userId,
            EnabledSections = saved?.ToArray() ?? _manager.GetSections().Select(section => section.Id).ToArray()
        };
    }

    [HttpPost("UserSettings")]
    public IActionResult UpdateUserSettings([FromBody] HomeSectionUserSettings settings)
    {
        _userSettings.Set(settings.UserId, settings.EnabledSections);
        return Ok();
    }

    private IActionResult GetEmbeddedAsset(string assetName, string contentType)
    {
        string resourceName = $"Jellyfin.Plugin.Mediarow.Web.{assetName}";
        Stream? stream = typeof(MediarowHomeController).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, contentType);
    }

    public sealed class HomeSectionInfo
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayTitle { get; init; } = string.Empty;
        public int Order { get; init; }
        public int Limit { get; init; }
        public string? Route { get; init; }
        public string? AdditionalData { get; init; }
    }

    public sealed class HomeSectionsResult : QueryResult<HomeSectionInfo>
    {
        public HomeSectionsResult(IReadOnlyList<HomeSectionInfo> items) : base(items)
        {
        }

        public IReadOnlyList<string> ContentCategories { get; init; } = Array.Empty<string>();
    }

    public sealed class HomeSectionUserSettings
    {
        public Guid UserId { get; init; }
        public IReadOnlyList<string> EnabledSections { get; init; } = Array.Empty<string>();
    }
}
