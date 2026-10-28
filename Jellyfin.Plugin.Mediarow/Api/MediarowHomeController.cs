using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using Jellyfin.Plugin.Mediarow.Models;
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
    private readonly UserPreferenceStore _preferenceStore;
    private readonly UserPreferenceFilter _preferences;
    private readonly HomeSectionBootstrapper _bootstrapper;
    private readonly UserPermissionGuard _guard;
    private readonly DynamicItemCache _itemCache;
    private readonly VirtualResumeTracker _resumeTracker;
    private readonly CatalogDiscoveryService _discovery;

    public MediarowHomeController(
        HomeSectionManager manager,
        UserPreferenceStore preferenceStore,
        UserPreferenceFilter preferences,
        HomeSectionBootstrapper bootstrapper,
        UserPermissionGuard guard,
        DynamicItemCache itemCache,
        VirtualResumeTracker resumeTracker,
        CatalogDiscoveryService discovery)
    {
        _manager = manager;
        _preferenceStore = preferenceStore;
        _preferences = preferences;
        _bootstrapper = bootstrapper;
        _guard = guard;
        _itemCache = itemCache;
        _resumeTracker = resumeTracker;
        _discovery = discovery;
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
    public ActionResult<HomeSectionsResult> GetSections([FromQuery] Guid userId)
    {
        UserAccessResult access = _guard.Resolve(User, userId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        _bootstrapper.RegisterConfiguredSections();
        // Hidden rows are removed and Order is rewritten to the user's position, so clients that sort by Order follow it.
        HomeSectionInfo[] sections = _preferences
            .ArrangeSections(access.Context!.UserId, _manager.GetSections(), section => section.Id)
            .Select((section, index) => new HomeSectionInfo
            {
                Id = section.Id,
                DisplayTitle = section.DisplayTitle,
                Order = index,
                Limit = section.Limit,
                Route = section.Route,
                AdditionalData = section.AdditionalData
            })
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

        UserAccessResult access = _guard.Resolve(User, userId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        UserSessionContext session = access.Context!;
        _bootstrapper.RegisterConfiguredSections();
        IReadOnlyDictionary<string, string> parameters = Request.Query
            .Where(pair => !pair.Key.Equals("userId", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Equals("language", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        QueryResult<BaseItemDto> result = await _manager.ExecuteAsync(id, new HomeSectionRequest
        {
            UserId = session.UserId,
            Language = language,
            Parameters = parameters
        }, cancellationToken);

        // Section results are cached; project per request so the cached instances stay untouched.
        IReadOnlyDictionary<Guid, long> resumeTicks = _resumeTracker.GetResumeTicksForUser(session.UserId);
        HomeItemContext itemContext = id.Equals("Mediarow_ContinueWatching", StringComparison.OrdinalIgnoreCase)
            ? HomeItemContext.ContinueWatching : HomeItemContext.DiscoveryRow;
        BaseItemDto[] items = _preferences.FilterItems(session.UserId, _guard.FilterVisible(session, result.Items ?? Array.Empty<BaseItemDto>()), itemContext)
            .Select(item => ProjectForUser(item, resumeTicks))
            .ToArray();
        return new QueryResult<BaseItemDto>(items);
    }

    /// <summary>
    /// One page of a Home row's catalog for "See All": offset = pageIndex * pageSize.
    /// <paramref name="catalogId"/> is the row's section id (Popular, a genre row or an admin catalog row).
    /// </summary>
    [HttpGet("Catalog/{catalogId}/Page")]
    public async Task<ActionResult<CatalogPageResult>> GetCatalogPage(
        [FromRoute] string catalogId,
        [FromQuery] Guid userId,
        [FromQuery] int pageIndex = 0,
        [FromQuery] int pageSize = 40,
        [FromQuery] string? contentCategory = null,
        CancellationToken cancellationToken = default)
    {
        if (contentCategory != null && contentCategory is not ("Movies" or "Series" or "Anime"))
        {
            return BadRequest(new { Error = "Content category must be Movies, Series, or Anime." });
        }

        if (pageIndex < 0 || pageSize < 1 || pageSize > CatalogDiscoveryService.MaxPageSize)
        {
            return BadRequest(new { Error = $"pageIndex must be 0 or more and pageSize 1-{CatalogDiscoveryService.MaxPageSize}." });
        }

        UserAccessResult access = _guard.Resolve(User, userId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        _bootstrapper.RegisterConfiguredSections();
        IHomeSection? section = _manager.GetSections()
            .FirstOrDefault(candidate => candidate.Id.Equals(catalogId, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<CatalogDiscoveryRequest>? requests = section switch
        {
            DiscoveryCatalogHomeSection catalog => _discovery.BuildRequests(catalog.Selection, contentCategory),
            VirtualCatalogHomeSection row => _discovery.BuildRequests(
                row.Id.Equals("Mediarow_Popular", StringComparison.OrdinalIgnoreCase) ? null : row.AdditionalData ?? row.DisplayTitle,
                contentCategory),
            _ => null
        };
        if (section == null)
        {
            return NotFound(new { Error = "Catalog not found." });
        }

        if (requests == null)
        {
            return BadRequest(new { Error = "This row has no catalog to page through." });
        }

        var (pageItems, hasMore) = await _discovery.GetPageAsync(requests, pageIndex, pageSize, contentCategory, cancellationToken);
        UserSessionContext session = access.Context!;
        IReadOnlyDictionary<Guid, long> resumeTicks = _resumeTracker.GetResumeTicksForUser(session.UserId);
        BaseItemDto[] items = _preferences.FilterItems(session.UserId, _guard.FilterVisible(session, pageItems), HomeItemContext.DiscoveryRow)
            .Select(item => ProjectForUser(item, resumeTicks))
            .ToArray();
        return new CatalogPageResult(items, pageIndex, pageSize, hasMore)
        {
            CatalogId = section.Id,
            DisplayTitle = section.DisplayTitle
        };
    }

    /// <summary>
    /// Catalogs and genres an administrator can add to Home.
    /// </summary>
    [HttpGet("Catalogs")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<CatalogOptions>> GetAvailableCatalogs(CancellationToken cancellationToken = default)
    {
        return await _discovery.GetAvailableCatalogsAsync(cancellationToken);
    }

    private BaseItemDto ProjectForUser(BaseItemDto item, IReadOnlyDictionary<Guid, long> resumeTicks)
    {
        if (!_itemCache.HasItem(item.Id))
        {
            // Library DTOs are built by Jellyfin for this user; untracked virtual snapshots are already per-user copies.
            return item;
        }

        // Virtual items carry user state only from this user's resume tracker.
        UserItemDataDto? userData = resumeTicks.TryGetValue(item.Id, out long ticks)
            ? new UserItemDataDto
            {
                Key = item.Id.ToString("N"),
                PlaybackPositionTicks = ticks,
                LastPlayedDate = item.UserData?.LastPlayedDate
            }
            : null;

        return VirtualItemProjection.ForUser(item, userData);
    }

    private ActionResult Deny(UserAccessFailure failure) => failure switch
    {
        UserAccessFailure.Unauthenticated => Unauthorized(),
        UserAccessFailure.UnknownUser => NotFound(new { Error = "User not found." }),
        _ => Forbid()
    };

    [HttpGet("UserSettings")]
    public ActionResult<object> GetUserSettings([FromQuery] Guid userId)
    {
        UserAccessResult access = _guard.Resolve(User, userId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        Guid effectiveUserId = access.Context!.UserId;
        _bootstrapper.RegisterConfiguredSections();
        UserHomePreferences saved = _preferenceStore.Get(effectiveUserId);
        return new
        {
            UserId = effectiveUserId,
            EnabledSections = _manager.GetSections()
                .Where(section => !saved.IsSectionHidden(section.Id))
                .Select(section => section.Id)
                .ToArray()
        };
    }

    [HttpPost("UserSettings")]
    public IActionResult UpdateUserSettings([FromBody] HomeSectionUserSettings settings)
    {
        UserAccessResult access = _guard.Resolve(User, settings.UserId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        _bootstrapper.RegisterConfiguredSections();
        _preferenceStore.SetEnabledSections(
            access.Context!.UserId,
            settings.EnabledSections,
            _manager.GetSections().Select(section => section.Id));
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
