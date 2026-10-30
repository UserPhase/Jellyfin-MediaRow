using System.Reflection;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Filters;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

[CollectionDefinition("FinalAuditRegression", DisableParallelization = true)]
public sealed class FinalAuditRegressionCollection { }

// Regression tests derived from the five remaining audit findings.
[Collection("FinalAuditRegression")]
public class FinalAuditRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedLanguageChoicesNeverWriteUserState(bool anotherAccount)
    {
        using var f = new Fixture();
        var victim = new User("victim", "test", "test") { Id = Guid.NewGuid() };
        Guid target = anotherAccount ? victim.Id : f.User.Id;
        var episode = new HiddenEpisode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid(), SeriesName = "Series", OriginalLanguage = "en" };
        f.Config.UserLanguagePreferences = [new() { UserId = target.ToString("N"), PreferOriginalAudio = false, AudioLanguages = ["en"], RememberSeriesChoices = true }];
        var source = new MediaSourceInfo { Id = "source", DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = -1,
            MediaStreams = [new() { Type = MediaStreamType.Audio, Index = 1, Language = "eng" }, new() { Type = MediaStreamType.Audio, Index = 2, Language = "jpn" }] };
        var library = Proxy<ILibraryManager>.Create((_, _) => episode);
        var users = Proxy<IUserManager>.Create((_, args) => (Guid)args![0]! == victim.Id ? victim : f.User);
        var media = Proxy<IMediaSourceManager>.Create((_, _) => new List<MediaSourceInfo> { source });
        int saves = 0;
        var guard = new UserPermissionGuard(users, library, null!, f.Cache, f.Memory);
        var filter = new LanguagePreferenceFilter(library, users, media, null!, f.Memory, NullLogger<LanguagePreferenceFilter>.Instance, guard)
            { Config = f.Config, SaveConfiguration = () => saves++ };
        var context = f.Context("MediaInfo", "GetPostedPlaybackInfo", new()
            { ["itemId"] = episode.Id, ["userId"] = target, ["audioStreamIndex"] = 2 });
        await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object()) { Result = new ForbidResult() }));
        Assert.False(episode.IsVisible(f.User));
        Assert.Equal(0, saves);
        Assert.Empty(f.Config.UserLanguagePreferences[0].SeriesOverrides);
        Assert.Null(f.Memory.Get<TrackDecision>(TrackDecision.CacheKey(target, episode.Id)));
    }

    [Theory]
    [InlineData("own-success", true)]
    [InlineData("admin-success", true)]
    [InlineData("forbidden", false)]
    [InlineData("bad-request", false)]
    [InlineData("negotiation-error", false)]
    [InlineData("exception", false)]
    [InlineData("policy-revoked", false)]
    [InlineData("disabled", false)]
    [InlineData("spoofed-target", false)]
    [InlineData("anonymous", false)]
    public async Task LanguageMemoryRequiresSuccessfulAuthorizedNegotiation(string scenario, bool saved)
    {
        using var f = new Fixture();
        var victim = new User("target", "test", "test") { Id = Guid.NewGuid() };
        victim.SetPermission(PermissionKind.EnableAllFolders, true);
        bool another = scenario is "admin-success" or "spoofed-target";
        if (scenario == "admin-success") f.User.SetPermission(PermissionKind.IsAdministrator, true);
        Guid target = another ? victim.Id : f.User.Id;
        var episode = new VisibleEpisode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid(), SeriesName = "Series", OriginalLanguage = "en" };
        f.Config.UserLanguagePreferences = [new() { UserId = target.ToString("N"), PreferOriginalAudio = false, AudioLanguages = ["en"], RememberSeriesChoices = true }];
        var source = new MediaSourceInfo { Id = "source", DefaultAudioStreamIndex = 1,
            MediaStreams = [new() { Type = MediaStreamType.Audio, Index = 1, Language = "eng" }, new() { Type = MediaStreamType.Audio, Index = 2, Language = "jpn" }] };
        var library = Proxy<ILibraryManager>.Create((_, _) => episode);
        var users = Proxy<IUserManager>.Create((_, args) => (Guid)args![0]! == victim.Id ? victim : f.User);
        var media = Proxy<IMediaSourceManager>.Create((_, _) => new List<MediaSourceInfo> { source });
        int writes = 0;
        var filter = new LanguagePreferenceFilter(library, users, media, null!, f.Memory, NullLogger<LanguagePreferenceFilter>.Instance,
            new UserPermissionGuard(users, library, null!, f.Cache, f.Memory)) { Config = f.Config, SaveConfiguration = () => writes++ };
        var context = f.Context("MediaInfo", "GetPostedPlaybackInfo", new() { ["itemId"] = episode.Id, ["userId"] = target, ["audioStreamIndex"] = 2 });
        if (scenario == "anonymous") context.HttpContext.User = new ClaimsPrincipal();
        await filter.OnActionExecutionAsync(context, () =>
        {
            Assert.Null(f.Memory.Get<TrackDecision>(TrackDecision.CacheKey(target, episode.Id)));
            Assert.Empty(f.Config.UserLanguagePreferences[0].SeriesOverrides);
            if (scenario == "policy-revoked") episode.Allowed = false;
            if (scenario == "disabled") f.User.SetPermission(PermissionKind.IsDisabled, true);
            return Task.FromResult(new ActionExecutedContext(context, [], new object())
            {
                Exception = scenario == "exception" ? new InvalidOperationException("fixture") : null,
                Result = scenario == "forbidden" ? new ForbidResult() : scenario == "bad-request" ? new BadRequestResult() :
                    scenario == "negotiation-error" ? new OkObjectResult(new MediaBrowser.Model.MediaInfo.PlaybackInfoResponse
                        { ErrorCode = MediaBrowser.Model.Dlna.PlaybackErrorCode.NoCompatibleStream }) : new OkResult()
            });
        });
        Assert.Equal(saved ? 1 : 0, writes);
        Assert.Equal(saved, f.Memory.Get<TrackDecision>(TrackDecision.CacheKey(target, episode.Id)) != null);
        Assert.Equal(saved ? 1 : 0, f.Config.UserLanguagePreferences[0].SeriesOverrides.Count);
    }

    [Fact]
    public async Task PersistedAnimeAliasRejectsHiddenEpisodeBeforeLoadingStreams()
    {
        using var f = new Fixture();
        f.Config.EnableAnimeAudioVersions = true; f.Config.AnimeAudioTracks = "sub,dub";
        f.Config.DirectAnimeUrlTemplate = "https://stream.test/private-token/{id}/{episode}/{audio}";
        string path = Path.Combine(f.Root, "episode.strm");
        await File.WriteAllTextAsync(path, "mediarow://anime/123/1");
        var episode = new HiddenEpisode { Id = Guid.NewGuid(), Path = path, Name = "Hidden episode", SeriesName = "Hidden series", RunTimeTicks = 100 };
        string alias = Guid.NewGuid().ToString("N");
        f.Cache.StoreMediaSourceMapping(alias, episode.Id);
        var library = Proxy<ILibraryManager>.Create((method, args) => method.Name == "GetItemById" && (Guid)args![0]! == episode.Id ? episode : null);
        var guard = f.Guard(library);
        Assert.False(guard.CanView(guard.Resolve(f.Principal).Context!, new() { Id = episode.Id, Type = BaseItemKind.Episode }));
        var factory = new SearchResultFactory(null!, null!, null!, null!, f.Cache, null!, library, NullLogger<SearchResultFactory>.Instance);
        var host = Proxy<IServerApplicationHost>.Create((_, _) => "audit");
        var data = Proxy<IUserDataManager>.Create((_, _) => null);
        var filter = new ItemLookupFilter(f.Cache, factory, null!, null!, host, library, data, f.Users, null!,
            NullLogger<ItemLookupFilter>.Instance, null!, null!, null!, guard, null!);
        var context = f.Context("UserLibrary", "GetItem", new() { ["itemId"] = Guid.ParseExact(alias, "N"), ["userId"] = f.User.Id });
        await filter.OnActionExecutionAsync(context, () => throw new Exception("Should have returned control to authorized native action."));
        Assert.IsType<NotFoundResult>(context.Result);
    }

    [Fact]
    public async Task AuthorizedPersistedAnimeAliasStillResolvesTheSelectedVersion()
    {
        using var f = new Fixture();
        f.Config.EnableAnimeAudioVersions = true; f.Config.AnimeAudioTracks = "sub,dub";
        f.Config.DirectAnimeUrlTemplate = "https://stream.test/allowed/{id}/{episode}/{audio}";
        string path = Path.Combine(f.Root, "episode.strm");
        await File.WriteAllTextAsync(path, "mediarow://anime/123/1");
        var episode = new VisibleEpisode { Id = Guid.NewGuid(), Path = path, Name = "Episode", RunTimeTicks = 100 };
        string alias = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"persisted:{episode.Id:N}:sub"))).ToString("N");
        f.Cache.StoreMediaSourceMapping(alias, episode.Id);
        var library = Proxy<ILibraryManager>.Create((method, args) => method.Name == "GetItemById" && (Guid)args![0]! == episode.Id ? episode : null);
        var factory = new SearchResultFactory(null!, null!, null!, null!, f.Cache, null!, library, NullLogger<SearchResultFactory>.Instance);
        var filter = new ItemLookupFilter(f.Cache, factory, null!, null!, Proxy<IServerApplicationHost>.Create((_, _) => "audit"), library,
            Proxy<IUserDataManager>.Create((_, _) => null), f.Users, null!, NullLogger<ItemLookupFilter>.Instance,
            null!, null!, null!, f.Guard(library), null!);
        var context = f.Context("UserLibrary", "GetItem", new() { ["itemId"] = Guid.ParseExact(alias, "N"), ["userId"] = f.User.Id });
        await filter.OnActionExecutionAsync(context, () => throw new Exception("Virtual alias should be resolved."));
        var dto = Assert.IsType<BaseItemDto>(Assert.IsType<OkObjectResult>(context.Result).Value);
        Assert.Equal(alias, Assert.Single(dto.MediaSources!).Id);
    }

    [Fact]
    public void CachedNativeDetailsHonorDeniedLibraryFolders()
    {
        using var f = new Fixture();
        f.User.SetPermission(PermissionKind.EnableAllFolders, false);
        var native = new FolderHiddenMovie { Id = Guid.NewGuid() };
        var dto = f.Movie(); dto.Id = native.Id; f.Cache.StoreItem(dto);
        var library = Proxy<ILibraryManager>.Create((method, _) => method.Name == "GetVirtualFolders" ? new List<VirtualFolderInfo>() : native);
        var guard = f.Guard(library);
        var session = guard.Resolve(f.Principal).Context!;
        Assert.False(session.Rules.CanBrowseVirtualMovies);
        Assert.False(native.IsVisibleStandalone(f.User));
        Assert.False(guard.CanView(session, dto));
        var controller = new MediarowController(f.Cache, null!, null!, null!, NullLogger<MediarowController>.Instance, null!, f.Memory, guard)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = f.Principal } } };
        Assert.IsType<NotFoundResult>(controller.GetItem(dto.Id).Result);
    }

    [Fact]
    public async Task CompletingNativeCopyClearsTheOriginalVirtualResumeIdentity()
    {
        using var f = new Fixture();
        var virtualItem = f.Movie(); f.Cache.StoreItem(virtualItem);
        var tracker = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        tracker.SaveResumeItem(f.User.Id, virtualItem, 50);
        var native = new VisibleMovie { Id = Guid.NewGuid(), RunTimeTicks = 100, ProviderIds = new() { ["Imdb"] = virtualItem.ProviderIds["Imdb"] } };
        var library = Proxy<ILibraryManager>.Create((method, args) => method.Name == "GetItemById" && (Guid)args![0]! == native.Id ? native : null);
        var data = Proxy<IUserDataManager>.Create((_, _) => new UserItemData { Key = "native", Played = true, PlaybackPositionTicks = 0 });
        var filter = new VirtualPlaybackProgressFilter(f.Cache, tracker, NullLogger<VirtualPlaybackProgressFilter>.Instance,
            f.Guard(library), library, f.Users, data);
        var context = f.Context("Playstate", "ReportPlaybackStopped", new() { ["info"] = new PlaybackStopInfo { ItemId = native.Id, PositionTicks = 100 } });
        await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object()) { Result = new NoContentResult() }));
        Assert.Equal(virtualItem.ProviderIds["Imdb"], native.ProviderIds["Imdb"]);
        Assert.Equal(0, tracker.GetResumeTicks(f.User.Id, virtualItem.Id));
        Assert.Empty(tracker.GetTrackedItemSnapshots(f.User.Id));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("zero")]
    [InlineData("mark-played")]
    [InlineData("mark-unplayed")]
    [InlineData("user-data-reset")]
    [InlineData("in-progress")]
    [InlineData("failed-action")]
    [InlineData("failed-playback")]
    public async Task NativeEpisodeUpdatesMatchOnlyThatEpisodeAndAccount(string scenario)
    {
        using var f = new Fixture();
        var tracker = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        var first = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemKind.Episode, ParentIndexNumber = 1, IndexNumber = 1,
            ProviderIds = new() { ["SeriesImdb"] = "tt1234567" }, RunTimeTicks = 100 };
        var sibling = VirtualItemProjection.Clone(first); sibling.Id = Guid.NewGuid(); sibling.IndexNumber = 2;
        var unrelated = VirtualItemProjection.Clone(first); unrelated.Id = Guid.NewGuid(); unrelated.ProviderIds = new() { ["SeriesImdb"] = "tt7654321" };
        var movie = f.Movie();
        var otherUser = Guid.NewGuid();
        tracker.SaveResumeItem(f.User.Id, first, 50); tracker.SaveResumeItem(f.User.Id, sibling, 40);
        tracker.SaveResumeItem(f.User.Id, unrelated, 30); tracker.SaveResumeItem(f.User.Id, movie, 20);
        tracker.SaveResumeItem(otherUser, first, 10);
        var nativeSeries = new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt1234567" } };
        var native = new VisibleEpisode { Id = Guid.NewGuid(), SeriesId = nativeSeries.Id, ParentIndexNumber = 1, IndexNumber = 1, RunTimeTicks = 100 };
        var library = Proxy<ILibraryManager>.Create((method, args) => method.Name == "GetItemById"
            ? (Guid)args![0]! == native.Id ? native : (Guid)args[0]! == nativeSeries.Id ? nativeSeries : null : null);
        var data = Proxy<IUserDataManager>.Create((_, _) => new UserItemData { Key = "native", Played = scenario == "completed", PlaybackPositionTicks = 0 });
        var filter = new VirtualPlaybackProgressFilter(f.Cache, tracker, NullLogger<VirtualPlaybackProgressFilter>.Instance, f.Guard(library), library, f.Users, data);
        string controller = scenario == "user-data-reset" ? "Items" : "Playstate";
        string action = scenario == "mark-played" ? "MarkPlayedItem" : scenario == "mark-unplayed" ? "MarkUnplayedItem" :
            scenario == "user-data-reset" ? "UpdateItemUserData" : "ReportPlaybackStopped";
        var context = f.Context(controller, action, new() { ["itemId"] = native.Id,
            ["info"] = new PlaybackStopInfo { ItemId = native.Id, PositionTicks = scenario == "completed" ? 100 : scenario == "zero" ? 0 : 60, Failed = scenario == "failed-playback" } });
        await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object())
        { Result = scenario == "failed-action" ? new ForbidResult() : scenario == "user-data-reset" ?
            new OkObjectResult(new UserItemDataDto { Key = "test", PlaybackPositionTicks = 0 }) : new NoContentResult() }));
        long expected = scenario == "in-progress" ? 60 : scenario is "failed-action" or "failed-playback" ? 50 : 0;
        Assert.Equal(expected, tracker.GetResumeTicks(f.User.Id, first.Id));
        Assert.Equal(40, tracker.GetResumeTicks(f.User.Id, sibling.Id));
        Assert.Equal(30, tracker.GetResumeTicks(f.User.Id, unrelated.Id));
        Assert.Equal(20, tracker.GetResumeTicks(f.User.Id, movie.Id));
        Assert.Equal(10, tracker.GetResumeTicks(otherUser, first.Id));
        Assert.Equal(expected, new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance).GetResumeTicks(f.User.Id, first.Id));
    }

    [Theory]
    [InlineData("Tmdb")]
    [InlineData("Tvdb")]
    [InlineData("Stremio")]
    public void EpisodeProviderIdentityNeverMatchesAnotherEntityType(string provider)
    {
        using var f = new Fixture();
        var tracker = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        var episode = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemKind.Episode, ProviderIds = new() { [provider] = "123" } };
        var movie = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemKind.Movie, ProviderIds = new() { [provider] = "123" } };
        tracker.SaveResumeItem(f.User.Id, episode, 50); tracker.SaveResumeItem(f.User.Id, movie, 40);
        var nativeIdentity = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemKind.Episode, ProviderIds = new() { [provider] = "123" } };
        var matches = tracker.GetMatchingItemIds(f.User.Id, nativeIdentity);
        Assert.Equal(episode.Id, Assert.Single(matches));
        tracker.RemoveResumeItems(f.User.Id, matches);
        Assert.Equal(40, tracker.GetResumeTicks(f.User.Id, movie.Id));
    }

    [Fact]
    public async Task HomeImmediatelyDropsCompletedTitleAfterTrackerClearsIt()
    {
        using var f = new Fixture();
        var movie = f.Movie(); f.Cache.StoreItem(movie);
        var tracker = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        tracker.SaveResumeItem(f.User.Id, movie, 50);
        var manager = new HomeSectionManager(f.Memory, NullLogger<HomeSectionManager>.Instance);
        var section = new VirtualContinueWatchingSection(f.Cache, tracker,
            new HomeSectionDefinition { Id = "Mediarow_ContinueWatching", DisplayTitle = "Continue Watching", Enabled = true, Limit = 20 });
        manager.Register(section);
        var request = new HomeSectionRequest { UserId = f.User.Id };
        Assert.Single((await manager.ExecuteAsync(section.Id, request)).Items);
        tracker.RemoveResumeItem(f.User.Id, movie.Id);
        Assert.Empty(tracker.GetTrackedItemSnapshots(f.User.Id));
        Assert.Empty((await section.GetResultsAsync(request)).Items);
        Assert.Empty((await manager.ExecuteAsync(section.Id, request)).Items);
    }

    private sealed class HiddenEpisode : Episode
    {
        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => false;
        public override bool IsVisibleStandalone(User user) => false;
    }
    private sealed class VisibleEpisode : Episode
    {
        public bool Allowed = true;
        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => Allowed;
        public override bool IsVisibleStandalone(User user) => Allowed;
    }
    private sealed class VisibleMovie : Movie
    {
        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => true;
        public override bool IsVisibleStandalone(User user) => true;
    }
    private sealed class FolderHiddenMovie : Movie
    {
        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => true;
        public override bool IsVisibleStandalone(User user) => false;
    }
    private sealed class TestPlugin(IApplicationPaths paths, IXmlSerializer serializer, PluginConfiguration config)
        : MediarowPlugin(paths, serializer, NullLogger<MediarowPlugin>.Instance)
    { public void SetConfig() => Configuration = config; }
    private sealed class Fixture : IDisposable
    {
        private readonly MediarowPlugin? _original = MediarowPlugin.Instance;
        public readonly string Root = Path.Combine(Path.GetTempPath(), "mediarow-recheck-" + Guid.NewGuid().ToString("N"));
        public readonly MemoryCache Memory = new(new MemoryCacheOptions());
        public readonly User User = new("caller", "test", "test") { Id = Guid.NewGuid(), MaxParentalRatingScore = 13 };
        public readonly PluginConfiguration Config = new() { StreamProvider = StreamProvider.Direct, HomeRowContentCategories = ["Movies", "Series"] };
        public readonly IApplicationPaths Paths;
        public readonly IUserManager Users;
        public readonly DynamicItemCache Cache;
        public ClaimsPrincipal Principal => new(new ClaimsIdentity([new Claim(UserPermissionGuard.UserIdClaim, User.Id.ToString())], "test"));
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Paths = Proxy<IApplicationPaths>.Create((_, _) => Root);
            new TestPlugin(Paths, Proxy<IXmlSerializer>.Create((_, _) => null), Config).SetConfig();
            User.SetPermission(PermissionKind.EnableAllFolders, true);
            Users = Proxy<IUserManager>.Create((_, _) => User);
            Cache = new(Memory, NullLogger<DynamicItemCache>.Instance);
        }
        public BaseItemDto Movie() => new() { Id = Guid.NewGuid(), Type = BaseItemKind.Movie, Name = "Movie", OfficialRating = "PG", RunTimeTicks = 100,
            ProviderIds = new() { ["Mediarow"] = "test", ["Imdb"] = "tt1234567" } };
        public UserPermissionGuard Guard(ILibraryManager library) => new(Users, library,
            Proxy<ILocalizationManager>.Create((_, _) => new ParentalRatingScore(10, null)), Cache, Memory);
        public ActionExecutingContext Context(string controller, string action, Dictionary<string, object?> args) => new(
            new ActionContext(new DefaultHttpContext { User = Principal }, new RouteData(), new ControllerActionDescriptor { ControllerName = controller, ActionName = action }), [], args, new object());
        public void Dispose()
        {
            typeof(MediarowPlugin).GetProperty(nameof(MediarowPlugin.Instance))!.SetValue(null, _original);
            Memory.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    public class Proxy<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[]?, object?> _invoke = null!;
        public static T Create(Func<MethodInfo, object?[]?, object?> invoke) { var value = Create<T, Proxy<T>>(); ((Proxy<T>)(object)value)._invoke = invoke; return value; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => _invoke(method!, args);
    }
}
