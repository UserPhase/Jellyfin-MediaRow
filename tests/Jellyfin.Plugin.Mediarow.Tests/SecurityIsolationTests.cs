using System.Reflection;
using System.Security.Claims;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class SecurityIsolationTests
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // US-style scores, matching Jellyfin's rating tables closely enough for the comparison logic.
    private static ParentalRatingScore? Score(string rating) => rating.ToUpperInvariant() switch
    {
        "G" => new ParentalRatingScore(0, null),
        "PG" => new ParentalRatingScore(10, null),
        "PG-13" => new ParentalRatingScore(13, null),
        "R" => new ParentalRatingScore(17, null),
        "TV-MA" => new ParentalRatingScore(17, 1),
        _ => null
    };

    private static BaseItemDto Movie(string? rating = null, params string[] tags) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Catalog movie",
        Type = BaseItemKind.Movie,
        OfficialRating = rating,
        Tags = tags
    };

    [Fact]
    public void Caller_id_comes_only_from_the_jellyfin_claim()
    {
        var spoofed = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("userId", UserB.ToString()) }, "test"));
        var real = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(UserPermissionGuard.UserIdClaim, UserA.ToString()) }, "test"));

        Assert.False(UserPermissionGuard.TryGetCallerId(spoofed, out _));
        Assert.False(UserPermissionGuard.TryGetCallerId(null, out _));
        Assert.True(UserPermissionGuard.TryGetCallerId(real, out Guid caller));
        Assert.Equal(UserA, caller);
    }

    [Theory]
    [InlineData(false, "00000000-0000-0000-0000-000000000000", UserAccessFailure.None)]
    [InlineData(false, "aaaaaaaa-0000-0000-0000-000000000001", UserAccessFailure.None)]
    [InlineData(false, "bbbbbbbb-0000-0000-0000-000000000002", UserAccessFailure.Forbidden)]
    [InlineData(true, "bbbbbbbb-0000-0000-0000-000000000002", UserAccessFailure.None)]
    public void Another_users_id_requires_administrator(bool isAdmin, string requested, UserAccessFailure expected)
    {
        Assert.Equal(expected, UserPermissionGuard.Authorize(UserA, isAdmin, Guid.Parse(requested)));
    }

    [Fact]
    public void Missing_caller_is_unauthenticated()
    {
        Assert.Equal(UserAccessFailure.Unauthenticated, UserPermissionGuard.Authorize(Guid.Empty, true, UserB));
    }

    [Theory]
    [InlineData("PG", true)]
    [InlineData("PG-13", true)]
    [InlineData("R", false)]
    [InlineData("TV-MA", false)]
    [InlineData("  pg  ", true)]
    public void Ratings_above_the_users_maximum_are_hidden(string rating, bool expected)
    {
        var rules = new ParentalRuleSet { MaxRatingScore = 13 };
        Assert.Equal(expected, UserPermissionGuard.IsVirtualItemAllowed(Movie(rating), rules, Score));
    }

    [Fact]
    public void Sub_score_is_compared_when_scores_tie()
    {
        var rules = new ParentalRuleSet { MaxRatingScore = 17, MaxRatingSubScore = 0 };
        Assert.True(UserPermissionGuard.IsVirtualItemAllowed(Movie("R"), rules, Score));
        Assert.False(UserPermissionGuard.IsVirtualItemAllowed(Movie("TV-MA"), rules, Score));
    }

    [Fact]
    public void Custom_rating_overrides_official_rating()
    {
        var item = Movie("R");
        item.CustomRating = "PG";
        Assert.True(UserPermissionGuard.IsVirtualItemAllowed(item, new ParentalRuleSet { MaxRatingScore = 13 }, Score));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NR-Unknown-42")]
    public void Missing_or_malformed_ratings_fail_closed_for_restricted_users(string? rating)
    {
        Assert.False(UserPermissionGuard.IsVirtualItemAllowed(Movie(rating), new ParentalRuleSet { MaxRatingScore = 13 }, Score));
        Assert.True(UserPermissionGuard.IsVirtualItemAllowed(Movie(rating), ParentalRuleSet.Unrestricted, Score));
    }

    [Fact]
    public void Unrated_blocking_applies_per_item_type()
    {
        var rules = new ParentalRuleSet { BlockedUnratedTypes = new HashSet<UnratedItem> { UnratedItem.Movie } };
        var series = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemKind.Series };

        Assert.False(UserPermissionGuard.IsVirtualItemAllowed(Movie(), rules, Score));
        Assert.True(UserPermissionGuard.IsVirtualItemAllowed(series, rules, Score));
    }

    [Fact]
    public void Blocked_and_allowed_tags_are_enforced_case_insensitively()
    {
        var blocked = new ParentalRuleSet { BlockedTags = new HashSet<string>(new[] { "horror" }, StringComparer.OrdinalIgnoreCase) };
        var allowed = new ParentalRuleSet { AllowedTags = new HashSet<string>(new[] { "kids" }, StringComparer.OrdinalIgnoreCase) };

        Assert.False(UserPermissionGuard.IsVirtualItemAllowed(Movie("PG", " Horror "), blocked, Score));
        Assert.True(UserPermissionGuard.IsVirtualItemAllowed(Movie("PG", "comedy"), blocked, Score));
        Assert.True(UserPermissionGuard.IsVirtualItemAllowed(Movie("PG", "KIDS"), allowed, Score));
        Assert.False(UserPermissionGuard.IsVirtualItemAllowed(Movie("PG"), allowed, Score));
    }

    [Fact]
    public void Users_without_library_access_do_not_see_virtual_catalog_types()
    {
        var rules = new ParentalRuleSet { CanBrowseVirtualMovies = false, CanBrowseVirtualSeries = true };
        Assert.False(UserPermissionGuard.IsVirtualItemAllowed(Movie("G"), rules, Score));
        Assert.True(UserPermissionGuard.IsVirtualItemAllowed(new BaseItemDto { Type = BaseItemKind.Series, OfficialRating = "G" }, rules, Score));
    }

    [Fact]
    public void Projection_never_mutates_or_shares_user_state_with_the_source()
    {
        var shared = Movie("PG");
        var userData = new UserItemDataDto { Key = "a", PlaybackPositionTicks = 500, IsFavorite = true };

        BaseItemDto forA = VirtualItemProjection.ForUser(shared, userData);
        forA.MediaSources = [];
        userData.PlaybackPositionTicks = 999;
        BaseItemDto forB = VirtualItemProjection.ForUser(shared, null);

        Assert.Null(shared.UserData);
        Assert.Null(shared.MediaSources);
        Assert.NotSame(shared, forA);
        Assert.Equal(500, forA.UserData!.PlaybackPositionTicks);
        Assert.True(forA.UserData.IsFavorite);
        Assert.Null(forB.UserData);
        Assert.Equal(shared.Name, forB.Name);
    }

    [Fact]
    public void Cache_strips_user_state_and_hands_out_isolated_copies()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance);
        var item = Movie("PG");
        item.UserData = new UserItemDataDto { Key = "leak", PlaybackPositionTicks = 42 };

        cache.StoreItem(item);
        BaseItemDto forA = cache.GetItemForUser(item.Id, new UserItemDataDto { Key = "a", PlaybackPositionTicks = 100 })!;
        BaseItemDto forB = cache.GetItemForUser(item.Id, null)!;

        Assert.Null(cache.GetItem(item.Id)!.UserData);
        Assert.Equal(100, forA.UserData!.PlaybackPositionTicks);
        Assert.Null(forB.UserData);
    }

    [Fact]
    public void Saving_resume_progress_does_not_mutate_the_shared_cached_item()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mediarow-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tracker = new VirtualResumeTracker(TestPaths.Create(directory), NullLogger<VirtualResumeTracker>.Instance);
            using var memory = new MemoryCache(new MemoryCacheOptions());
            var cache = new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance);
            var item = Movie("PG");
            cache.StoreItem(item);
            BaseItemDto shared = cache.GetItem(item.Id)!;

            tracker.SaveResumeItem(UserA, shared, 12_000);

            Assert.Null(shared.UserData);
            Assert.Null(cache.GetItem(item.Id)!.UserData);
            Assert.Equal(12_000, tracker.GetResumeTicks(UserA, item.Id));
            Assert.Equal(0, tracker.GetResumeTicks(UserB, item.Id));
            Assert.Empty(tracker.GetResumeTicksForUser(UserB));
            Assert.Equal(12_000, tracker.GetTrackedItemSnapshots(UserA).Single().Item.UserData!.PlaybackPositionTicks);
            Assert.Empty(tracker.GetTrackedItemSnapshots(UserB));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    public class TestPaths : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;

        public static IApplicationPaths Create(string root)
        {
            IApplicationPaths proxy = Create<IApplicationPaths, TestPaths>();
            ((TestPaths)(object)proxy).Root = root;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(string) ? Root : null;
    }
}
