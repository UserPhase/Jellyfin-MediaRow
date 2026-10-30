using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Parental and library-access rules of one Jellyfin user, captured once per request.
/// </summary>
public sealed record ParentalRuleSet
{
    public static ParentalRuleSet Unrestricted { get; } = new();

    public int? MaxRatingScore { get; init; }

    public int? MaxRatingSubScore { get; init; }

    public IReadOnlySet<UnratedItem> BlockedUnratedTypes { get; init; } = new HashSet<UnratedItem>();

    public IReadOnlySet<string> BlockedTags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> AllowedTags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool CanBrowseVirtualMovies { get; init; } = true;

    public bool CanBrowseVirtualSeries { get; init; } = true;
}

/// <summary>
/// The authenticated caller and the user whose data the request may read.
/// </summary>
public sealed record UserSessionContext(Guid CallerId, Guid UserId, bool IsAdministrator, ParentalRuleSet Rules);

public enum UserAccessFailure
{
    None,
    Unauthenticated,
    Forbidden,
    UnknownUser
}

public readonly record struct UserAccessResult(UserSessionContext? Context, UserAccessFailure Failure)
{
    public bool Succeeded => Context != null;
}

/// <summary>
/// Resolves the caller from Jellyfin's authenticated claims (never from client-supplied ids) and
/// applies the user's parental, tag and library rules to virtual catalog items.
/// </summary>
public sealed class UserPermissionGuard
{
    public const string UserIdClaim = "Jellyfin-UserId";

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly DynamicItemCache _itemCache;

    public UserPermissionGuard(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILocalizationManager localization,
        DynamicItemCache itemCache,
        IMemoryCache rulesCache)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _localization = localization;
        _itemCache = itemCache;
    }

    public static bool TryGetCallerId(ClaimsPrincipal? principal, out Guid callerId)
    {
        callerId = Guid.Empty;
        string? value = principal?.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out callerId) && callerId != Guid.Empty;
    }

    /// <summary>
    /// An empty <paramref name="requestedUserId"/> means "the caller". Another user's id is accepted only for administrators.
    /// </summary>
    public static UserAccessFailure Authorize(Guid callerId, bool callerIsAdministrator, Guid requestedUserId)
    {
        if (callerId == Guid.Empty)
        {
            return UserAccessFailure.Unauthenticated;
        }

        return requestedUserId == Guid.Empty || requestedUserId == callerId || callerIsAdministrator
            ? UserAccessFailure.None
            : UserAccessFailure.Forbidden;
    }

    public UserAccessResult Resolve(ClaimsPrincipal? principal, Guid requestedUserId = default)
    {
        if (principal?.Identity?.IsAuthenticated != true || !TryGetCallerId(principal, out Guid callerId))
        {
            return new UserAccessResult(null, UserAccessFailure.Unauthenticated);
        }

        User? caller = _userManager.GetUserById(callerId);
        if (caller == null)
        {
            return new UserAccessResult(null, UserAccessFailure.Unauthenticated);
        }

        bool isAdministrator = caller.HasPermission(PermissionKind.IsAdministrator);
        if (caller.HasPermission(PermissionKind.IsDisabled) ||
            (!isAdministrator && !caller.IsParentalScheduleAllowed()))
            return new UserAccessResult(null, UserAccessFailure.Forbidden);
        UserAccessFailure failure = Authorize(callerId, isAdministrator, requestedUserId);
        if (failure != UserAccessFailure.None)
        {
            return new UserAccessResult(null, failure);
        }

        User target = caller;
        if (requestedUserId != Guid.Empty && requestedUserId != callerId)
        {
            User? requested = _userManager.GetUserById(requestedUserId);
            if (requested == null)
            {
                return new UserAccessResult(null, UserAccessFailure.UnknownUser);
            }

            target = requested;
        }

        if (target.HasPermission(PermissionKind.IsDisabled) ||
            (!target.HasPermission(PermissionKind.IsAdministrator) && !target.IsParentalScheduleAllowed()))
            return new UserAccessResult(null, UserAccessFailure.Forbidden);

        return new UserAccessResult(
            new UserSessionContext(callerId, target.Id, isAdministrator, BuildRules(target)),
            UserAccessFailure.None);
    }

    public ParentalRuleSet BuildRules(User user)
    {
        bool allFolders = user.HasPermission(PermissionKind.EnableAllFolders);
        bool canMovies = allFolders;
        bool canSeries = allFolders;
        if (!allFolders)
        {
            var config = MediarowPlugin.Instance?.Configuration ?? new PluginConfiguration();
            HashSet<Guid> enabledFolders = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).ToHashSet();
            string[] accessibleLocations = _libraryManager.GetVirtualFolders()
                .Where(folder => Guid.TryParse(folder.ItemId, out Guid id) && enabledFolders.Contains(id))
                .SelectMany(folder => folder.Locations ?? Array.Empty<string>())
                .ToArray();
            canMovies = config.EnablePersistence && CoversPath(accessibleLocations, config.GetPersistencePath("movies"));
            canSeries = config.EnablePersistence &&
                (CoversPath(accessibleLocations, config.GetPersistencePath("tv")) ||
                 CoversPath(accessibleLocations, config.GetPersistencePath("anime")));
        }

        return new ParentalRuleSet
        {
            MaxRatingScore = user.MaxParentalRatingScore,
            MaxRatingSubScore = user.MaxParentalRatingSubScore,
            BlockedUnratedTypes = user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).ToHashSet(),
            BlockedTags = CleanTags(user.GetPreference(PreferenceKind.BlockedTags)),
            AllowedTags = CleanTags(user.GetPreference(PreferenceKind.AllowedTags)),
            CanBrowseVirtualMovies = canMovies,
            CanBrowseVirtualSeries = canSeries
        };
    }

    /// <summary>
    /// True when the user may see <paramref name="item"/>. Real library items use Jellyfin's own visibility rules.
    /// </summary>
    public bool CanView(UserSessionContext context, BaseItemDto item)
    {
        var libraryItem = _libraryManager.GetItemById(item.Id);
        if (libraryItem != null)
        {
            User? user = _userManager.GetUserById(context.UserId);
            return user != null && libraryItem.IsVisible(user, false) && libraryItem.IsVisibleStandalone(user);
        }

        BaseItemDto ratingSource = item;
        if (item.Type is BaseItemKind.Season or BaseItemKind.Episode &&
            item.SeriesId is Guid seriesId && seriesId != Guid.Empty &&
            _itemCache.GetItem(seriesId) is { } series)
        {
            ratingSource = series;
        }

        return IsVirtualItemAllowed(ratingSource, context.Rules, rating => _localization.GetRatingScore(rating, null)) &&
            (ReferenceEquals(ratingSource, item) || IsVirtualItemAllowed(item, context.Rules, rating => _localization.GetRatingScore(rating, null)));
    }

    public IReadOnlyList<BaseItemDto> FilterVisible(UserSessionContext context, IEnumerable<BaseItemDto> items) =>
        items.Where(item => item != null && CanView(context, item)).ToArray();

    /// <summary>
    /// Parental decision for metadata coming from upstream catalogs. Missing or unrecognized ratings fail closed for
    /// users with a maximum rating, because upstream certifications are untrusted and often absent before enrichment.
    /// </summary>
    public static bool IsVirtualItemAllowed(
        BaseItemDto item,
        ParentalRuleSet rules,
        Func<string, ParentalRatingScore?> scoreRating)
    {
        bool isMovie = item.Type is BaseItemKind.Movie;
        if (isMovie ? !rules.CanBrowseVirtualMovies : !rules.CanBrowseVirtualSeries)
        {
            return false;
        }

        if (rules.BlockedTags.Count > 0 || rules.AllowedTags.Count > 0)
        {
            HashSet<string> itemTags = CleanTags(item.Tags ?? Array.Empty<string>());
            if (rules.BlockedTags.Overlaps(itemTags))
            {
                return false;
            }

            if (rules.AllowedTags.Count > 0 && !rules.AllowedTags.Overlaps(itemTags))
            {
                return false;
            }
        }

        string? rating = NormalizeRating(item.CustomRating) ?? NormalizeRating(item.OfficialRating);
        ParentalRatingScore? score = rating == null ? null : scoreRating(rating);
        if (score == null)
        {
            UnratedItem unratedType = isMovie ? UnratedItem.Movie : UnratedItem.Series;
            return !rules.MaxRatingScore.HasValue && !rules.BlockedUnratedTypes.Contains(unratedType);
        }

        if (!rules.MaxRatingScore.HasValue)
        {
            return true;
        }

        if (score.Score != rules.MaxRatingScore.Value)
        {
            return score.Score < rules.MaxRatingScore.Value;
        }

        return !rules.MaxRatingSubScore.HasValue || (score.SubScore ?? 0) <= rules.MaxRatingSubScore.Value;
    }

    private static string? NormalizeRating(string? rating)
    {
        string? trimmed = rating?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static HashSet<string> CleanTags(IEnumerable<string> tags) =>
        tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool CoversPath(IEnumerable<string> locations, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string target = NormalizePath(path);
        return locations.Select(NormalizePath).Any(location =>
            target.Equals(location, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(location + "/", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');
}
