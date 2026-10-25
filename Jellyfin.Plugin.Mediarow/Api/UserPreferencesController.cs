using Jellyfin.Plugin.Mediarow.HomeScreen;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Mediarow.Api;

/// <summary>
/// Personal Home controls: row order, hidden rows, Hide watched and the "Not interested" list.
/// Every call is scoped to the authenticated user; administrators may pass another user's id.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AIOStreams")]
[Route("Mediarow/Preferences")]
public sealed class UserPreferencesController : ControllerBase
{
    private readonly UserPreferenceStore _store;
    private readonly HomeSectionManager _manager;
    private readonly HomeSectionBootstrapper _bootstrapper;
    private readonly UserPermissionGuard _guard;
    private readonly DynamicItemCache _itemCache;
    private readonly ILibraryManager _libraryManager;

    public UserPreferencesController(
        UserPreferenceStore store,
        HomeSectionManager manager,
        HomeSectionBootstrapper bootstrapper,
        UserPermissionGuard guard,
        DynamicItemCache itemCache,
        ILibraryManager libraryManager)
    {
        _store = store;
        _manager = manager;
        _bootstrapper = bootstrapper;
        _guard = guard;
        _itemCache = itemCache;
        _libraryManager = libraryManager;
    }

    [HttpGet("UserPreferences")]
    public ActionResult<UserPreferencesResponse> GetPreferences([FromQuery] Guid userId = default)
    {
        UserAccessResult access = _guard.Resolve(User, userId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        Guid effectiveUserId = access.Context!.UserId;
        return BuildResponse(effectiveUserId, _store.Get(effectiveUserId));
    }

    [HttpPost("UserPreferences")]
    public ActionResult<UserPreferencesResponse> UpdatePreferences([FromBody] UserPreferencesUpdate update)
    {
        if (update == null)
        {
            return BadRequest(new { Error = "A preferences body is required." });
        }

        if ((update.SectionOrder?.Count ?? 0) > UserPreferenceStore.MaxSectionIds ||
            (update.HiddenSections?.Count ?? 0) > UserPreferenceStore.MaxSectionIds ||
            (update.RemoveNotInterested?.Count ?? 0) > UserPreferenceStore.MaxNotInterested)
        {
            return BadRequest(new { Error = "Too many entries." });
        }

        UserAccessResult access = _guard.Resolve(User, update.UserId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        Guid effectiveUserId = access.Context!.UserId;
        return BuildResponse(effectiveUserId, _store.Update(effectiveUserId, update));
    }

    [HttpPost("NotInterested/{itemId}")]
    public ActionResult<NotInterestedEntry> AddNotInterested([FromRoute] Guid itemId, [FromQuery] Guid userId = default)
    {
        if (itemId == Guid.Empty)
        {
            return BadRequest(new { Error = "Item id is required." });
        }

        UserAccessResult access = _guard.Resolve(User, userId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        // Provider ids are captured so the title stays hidden when another catalog serves it under a different id.
        NotInterestedEntry entry = UserPreferenceFilter.CreateEntry(itemId, _itemCache.GetItem(itemId) ?? FromLibrary(itemId));
        UserHomePreferences saved = _store.AddNotInterested(access.Context!.UserId, entry);
        return saved.NotInterested.First(existing => existing.ItemId == itemId);
    }

    [HttpDelete("NotInterested/{itemId}")]
    public IActionResult RemoveNotInterested([FromRoute] Guid itemId, [FromQuery] Guid userId = default)
    {
        UserAccessResult access = _guard.Resolve(User, userId);
        if (!access.Succeeded)
        {
            return Deny(access.Failure);
        }

        _store.RemoveNotInterested(access.Context!.UserId, itemId);
        return NoContent();
    }

    private UserPreferencesResponse BuildResponse(Guid userId, UserHomePreferences preferences)
    {
        _bootstrapper.RegisterConfiguredSections();
        UserPreferenceSection[] sections = UserPreferenceFilter
            .Arrange(preferences, _manager.GetSections(), section => section.Id, includeHidden: true)
            .Select((section, index) => new UserPreferenceSection(
                section.Id,
                section.DisplayTitle,
                index,
                !preferences.IsSectionHidden(section.Id),
                GetKind(section)))
            .ToArray();
        return new UserPreferencesResponse
        {
            UserId = userId,
            HideWatched = preferences.HideWatched,
            SectionOrder = preferences.SectionOrder,
            HiddenSections = sections.Where(section => !section.Visible).Select(section => section.Id).ToArray(),
            NotInterested = preferences.NotInterested.AsEnumerable().Reverse().ToArray(),
            Sections = sections,
            UpdatedUtc = preferences.UpdatedUtc
        };
    }

    private static string GetKind(IHomeSection section) => section switch
    {
        VirtualContinueWatchingSection => "ContinueWatching",
        DiscoveryCatalogHomeSection => "Catalog",
        _ when section.Id.Equals(AppLibraryLayout.PopularId, StringComparison.OrdinalIgnoreCase) => "Popular",
        _ => "Genre"
    };

    private BaseItemDto? FromLibrary(Guid itemId)
    {
        BaseItem? item = _libraryManager.GetItemById(itemId);
        return item == null
            ? null
            : new BaseItemDto
            {
                Id = item.Id,
                Name = item.Name,
                Type = item.GetBaseItemKind(),
                ProductionYear = item.ProductionYear,
                ProviderIds = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase)
            };
    }

    private ActionResult Deny(UserAccessFailure failure) => failure switch
    {
        UserAccessFailure.Unauthenticated => Unauthorized(),
        UserAccessFailure.UnknownUser => NotFound(new { Error = "User not found." }),
        _ => Forbid()
    };
}
