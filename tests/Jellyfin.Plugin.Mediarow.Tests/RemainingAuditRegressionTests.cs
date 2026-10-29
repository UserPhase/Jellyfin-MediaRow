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
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Querying;
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

[CollectionDefinition("RemainingAuditRegression", DisableParallelization = true)]
public sealed class RemainingAuditRegressionCollection { }

// Regression tests derived from the audit reproductions. No live server or accounts are used.
[Collection("RemainingAuditRegression")]
public class RemainingAuditRegressionTests
{
    [Theory]
    [InlineData("children")]
    [InlineData("nextup")]
    public async Task RestrictedUserCannotReceiveNativeSeriesChildren(string route)
    {
        using var f = new Fixture();
        var series = f.Item(BaseItemKind.Series);
        var season = f.Item(BaseItemKind.Season);
        season.SeriesId = series.Id; season.IndexNumber = 1;
        var episode = f.Item(BaseItemKind.Episode);
        episode.SeriesId = series.Id; episode.ParentIndexNumber = 1; episode.IndexNumber = 1;
        episode.MediaSources = [new() { Id = "private", Path = "https://stream.test/private-token" }];
        f.Cache.StoreItem(series);
        f.Cache.StoreSeasonsForSeries(series.Id, [season]);
        f.Cache.StoreEpisodesForSeries(series.Id, [episode]);
        Assert.False(f.Guard.CanView(f.Guard.Resolve(f.Principal).Context!, series));
        var filter = new SeasonEpisodeFilter(f.Cache, f.Library, f.Users, null!, f.Persistence, null!, f.Guard, NullLogger<SeasonEpisodeFilter>.Instance);
        var ctx = f.Context(route == "children" ? "Items" : "TvShows", route == "children" ? "GetItems" : "GetNextUp",
            new() { [route == "children" ? "parentId" : "seriesId"] = series.Id, ["userId"] = f.User.Id });
        await filter.OnActionExecutionAsync(ctx, () => throw new Exception("Native checks must not be skipped."));
        Assert.IsType<NotFoundResult>(ctx.Result);
    }

    [Theory]
    [InlineData("GetSeasons", "TvShows", "seriesId")]
    [InlineData("GetEpisodes", "TvShows", "seriesId")]
    [InlineData("GetNextUp", "TvShows", "seriesId")]
    [InlineData("GetItems", "Items", "parentId")]
    [InlineData("GetItemsByUserIdLegacy", "Items", "parentId")]
    public async Task AnotherUsersTargetIsForbiddenOnEveryVirtualRoute(string action, string controller, string argument)
    {
        using var f = new Fixture();
        var series = f.Item(BaseItemKind.Series); series.OfficialRating = "PG"; f.Cache.StoreItem(series);
        var filter = new SeasonEpisodeFilter(f.Cache, f.Library, f.Users, null!, f.Persistence, null!, f.Guard, NullLogger<SeasonEpisodeFilter>.Instance);
        var ctx = f.Context(controller, action, new() { [argument] = series.Id, ["userId"] = Guid.NewGuid() });
        await filter.OnActionExecutionAsync(ctx, () => throw new Exception("Must reject the mismatched owner before returning virtual data."));
        Assert.IsType<ForbidResult>(ctx.Result);
    }

    [Fact]
    public async Task AllowedChildrenArePolicyFilteredPagedAndDetached()
    {
        using var f = new Fixture();
        var series = f.Item(BaseItemKind.Series); series.OfficialRating = "PG"; f.Cache.StoreItem(series);
        var season = f.Item(BaseItemKind.Season); season.OfficialRating = null; season.SeriesId = series.Id; season.IndexNumber = 1;
        f.Cache.StoreSeasonsForSeries(series.Id, [season]);
        var inherited = f.Item(BaseItemKind.Episode); inherited.OfficialRating = null; inherited.SeriesId = series.Id; inherited.ParentIndexNumber = 1; inherited.IndexNumber = 1;
        var allowed = f.Item(BaseItemKind.Episode); allowed.OfficialRating = "PG"; allowed.SeriesId = series.Id; allowed.ParentIndexNumber = 1; allowed.IndexNumber = 2;
        var restricted = f.Item(BaseItemKind.Episode); restricted.SeriesId = series.Id; restricted.ParentIndexNumber = 1; restricted.IndexNumber = 3;
        f.Cache.StoreEpisodesForSeries(series.Id, [inherited, allowed, restricted]);
        var filter = new SeasonEpisodeFilter(f.Cache, f.Library, f.Users, null!, f.Persistence, null!, f.Guard, NullLogger<SeasonEpisodeFilter>.Instance);
        var ctx = f.Context("Items", "GetItems", new() { ["parentId"] = season.Id, ["userId"] = f.User.Id, ["startIndex"] = 1, ["limit"] = 1 });
        await filter.OnActionExecutionAsync(ctx, () => throw new Exception("Virtual child route should be handled."));
        var page = Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(ctx.Result).Value);
        Assert.Equal(2, page.TotalRecordCount); Assert.Equal(allowed.Id, Assert.Single(page.Items).Id);
        page.Items[0].Name = "client change";
        Assert.NotEqual("client change", f.Cache.GetItem(allowed.Id)!.Name);
        Assert.Null(inherited.OfficialRating); Assert.Null(season.OfficialRating);
    }

    [Fact]
    public async Task AnonymousVirtualRequestsAreRejected()
    {
        using var f = new Fixture();
        var series = f.Item(BaseItemKind.Series); f.Cache.StoreItem(series);
        var filter = new SeasonEpisodeFilter(f.Cache, f.Library, f.Users, null!, f.Persistence, null!, f.Guard, NullLogger<SeasonEpisodeFilter>.Instance);
        var ctx = f.Context("Items", "GetItems", new() { ["parentId"] = series.Id }); ctx.HttpContext.User = new();
        await filter.OnActionExecutionAsync(ctx, () => throw new Exception("Cannot expose an anonymous cached item."));
        Assert.IsType<UnauthorizedResult>(ctx.Result);
    }

    [Fact]
    public async Task ExplicitPersistRejectsDeniedVisibilityWithoutWritingFiles()
    {
        using var f = new Fixture();
        var movie = f.Item(BaseItemKind.Movie); f.Cache.StoreItem(movie);
        Assert.False(f.Guard.CanView(f.Guard.Resolve(f.Principal).Context!, movie));
        var controller = new MediarowController(f.Cache, null!, f.Persistence, null!, NullLogger<MediarowController>.Instance,
            null!, f.Memory, f.Guard) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = f.Principal } } };
        Assert.IsType<NotFoundResult>(await controller.PersistItem(movie.Id, default));
        Assert.False(File.Exists(f.Persistence.GetMovieFilePath(movie)));
    }

    [Fact]
    public async Task AuthorizedPersistenceStillWorksAndRejectsAnotherUserQuery()
    {
        using var f = new Fixture();
        var movie = f.Item(BaseItemKind.Movie); movie.OfficialRating = "PG"; f.Cache.StoreItem(movie);
        var controller = new MediarowController(f.Cache, null!, f.Persistence, null!, NullLogger<MediarowController>.Instance,
            null!, f.Memory, f.Guard) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = f.Principal } } };
        controller.Request.QueryString = new("?userId=" + Guid.NewGuid());
        Assert.IsType<ForbidResult>(await controller.PersistItem(movie.Id, default));
        Assert.False(File.Exists(f.Persistence.GetMovieFilePath(movie)));
        controller.Request.QueryString = QueryString.Empty;
        Assert.IsType<OkObjectResult>(await controller.PersistItem(movie.Id, default));
        Assert.True(File.Exists(f.Persistence.GetMovieFilePath(movie)));
    }

    [Fact]
    public async Task RatingReevaluationAfterEnrichmentPreventsWritesAndLeavesOriginalDtoUnchanged()
    {
        using var f = new Fixture();
        var movie = f.Item(BaseItemKind.Movie); movie.OfficialRating = "PG"; movie.ProviderIds["Tmdb"] = "123"; f.Cache.StoreItem(movie);
        var details = System.Text.Json.JsonSerializer.Deserialize<TmdbMovieDetails>("""{"imdb_id":"tt7654321","release_dates":{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"R"}]}]}}""")!;
        var tmdb = Proxy<ITmdbClient>.Create((method, _) => method.Name == "GetMovieDetailsAsync"
            ? Task.FromResult<TmdbMovieDetails?>(details) : method.Name == "GetImageBaseUrlAsync" ? Task.FromResult("https://images.test/") : throw new NotSupportedException(method.Name));
        var factory = new SearchResultFactory(tmdb, null!, null!, null!, f.Cache, null!, f.Library, NullLogger<SearchResultFactory>.Instance);
        var controller = new MediarowController(f.Cache, null!, f.Persistence, null!, NullLogger<MediarowController>.Instance,
            null!, f.Memory, f.Guard, factory) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = f.Principal } } };
        Assert.IsType<NotFoundResult>(await controller.PersistItem(movie.Id, default));
        Assert.False(Directory.Exists(f.Config.MoviesLibraryPath));
        Assert.Equal("PG", movie.OfficialRating); Assert.Equal("tt1234567", movie.ProviderIds["Imdb"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveNeverReturnsHiddenNativeItemIdentity(bool hiddenFolder)
    {
        using var f = new Fixture();
        var movie = f.Item(BaseItemKind.Movie); movie.OfficialRating = "PG"; f.Cache.StoreItem(movie);
        Movie hidden = hiddenFolder ? new FolderHiddenMovie { Id = Guid.NewGuid() } : new HiddenMovie { Id = Guid.NewGuid() };
        var library = Proxy<ILibraryManager>.Create((method, args) => method.Name == "GetItemsResult" ? new QueryResult<BaseItem>([hidden])
            : method.Name == "GetItemById" && (Guid)args![0]! == hidden.Id ? hidden : null);
        var persistence = new PersistenceService(library, f.Cache, NullLogger<PersistenceService>.Instance);
        var resolver = new PlaybackItemResolver(null!, persistence, library, f.Cache);
        var guard = new UserPermissionGuard(f.Users, library, Proxy<ILocalizationManager>.Create((_, _) => new ParentalRatingScore(10, null)), f.Cache, f.Memory);
        Assert.True(guard.CanView(guard.Resolve(f.Principal).Context!, movie));
        var controller = new MediarowController(f.Cache, null!, persistence, null!, NullLogger<MediarowController>.Instance,
            resolver, f.Memory, guard, userManager: f.Users, libraryManager: library) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = f.Principal } } };
        Assert.IsType<NotFoundResult>(await controller.ResolvePlaybackItem(movie.Id, default));
    }

    [Fact]
    public async Task CatalogPagingRejectsOverflowWithoutUpstreamRequests()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = new CatalogDiscoveryService(null!, null!, null!, memory, NullLogger<CatalogDiscoveryService>.Instance);
        var page = await service.GetPageAsync([new CatalogDiscoveryRequest()], int.MaxValue - 1, 20, null);
        Assert.Empty(page.Items); Assert.False(page.HasMore);
        foreach (var offset in new[] { -1, int.MaxValue, CatalogDiscoveryService.MaxWindowItems })
        {
            var window = await service.ReadWindowAsync(new(), offset, 40, null);
            Assert.Empty(window.Items); Assert.False(window.HasMore);
        }
        Assert.Empty((await service.GetPageAsync([new()], 50_000_000, 100, null)).Items);
    }

    [Theory]
    [InlineData("disabled-favorite")]
    [InlineData("season-favorite")]
    [InlineData("scan-running")]
    [InlineData("partial-inventory")]
    [InlineData("stale-inventory")]
    [InlineData("wrong-identity")]
    [InlineData("active-lease")]
    [InlineData("complete")]
    [InlineData("scan-starts")]
    [InlineData("config-disabled")]
    [InlineData("new-favorite")]
    [InlineData("active-playback")]
    [InlineData("unwatched")]
    [InlineData("future-episode")]
    [InlineData("failed-inventory")]
    public async Task WatchedCleanupKeepsRetainedOrIncompleteSeries(string scenario)
    {
        using var f = new Fixture();
        f.Config.CleanupCompletedSeries = true;
        var series = new VisibleSeries { Id = Guid.NewGuid(), Path = Path.Combine(f.Config.SeriesLibraryPath, "Show"), Status = SeriesStatus.Ended };
        var season = new Season { Id = Guid.NewGuid() };
        var stream = Path.Combine(series.Path, "Season 01", "S01E01.strm");
        Directory.CreateDirectory(Path.GetDirectoryName(stream)!);
        await File.WriteAllTextAsync(stream, "mediarow://tv/tt1234567/1/1");
        File.SetLastWriteTimeUtc(stream, DateTime.UtcNow.AddHours(-1));
        var episode = new Episode { Id = Guid.NewGuid(), Path = stream, SeriesId = series.Id, PremiereDate = DateTime.UtcNow.AddYears(-1), ParentIndexNumber = 1, IndexNumber = 1 };
        if (scenario == "future-episode") episode.PremiereDate = DateTime.UtcNow.AddDays(1);
        EpisodeSyncService sync;
        {
            var stateFolder = Path.Combine(f.Root, "Jellyfin.Plugin.Mediarow");
            Directory.CreateDirectory(stateFolder);
            await File.WriteAllTextAsync(Path.Combine(stateFolder, "episode-sync-state.json"), System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<Guid, EpisodeSyncEntry> { [series.Id] = new() { Status = "Ended", ConsecutiveFailures = scenario == "failed-inventory" ? 1 : 0, KnownEpisodeCount = scenario == "partial-inventory" ? 2 : 1, LastCheckedUtc = scenario == "stale-inventory" ? DateTime.UtcNow.AddDays(-9) : DateTime.UtcNow } }));
            sync = new EpisodeSyncService(null!, null!, null!, null!, null!, null!, f.Paths, NullLogger<EpisodeSyncService>.Instance);
            var expected = f.Item(BaseItemKind.Episode); expected.ParentIndexNumber = 1; expected.IndexNumber = scenario == "wrong-identity" ? 2 : 1;
            var second = f.Item(BaseItemKind.Episode); second.ParentIndexNumber = 1; second.IndexNumber = 2;
            f.Cache.StoreEpisodesForSeries(series.Id, scenario == "partial-inventory" ? [expected, second] : [expected]);
        }
        var disabled = new User("saved-for-later", "test", "test") { Id = Guid.NewGuid() };
        disabled.SetPermission(PermissionKind.IsDisabled, true);
        bool removed = false;
        bool savedDuringCleanup = false;
        int childReads = 0, scanReads = 0;
        var library = Proxy<ILibraryManager>.Create((method, args) =>
        {
            if (method.Name == "get_IsScanRunning") return scenario == "scan-running" || scenario == "scan-starts" && ++scanReads >= 4;
            if (method.Name == "DeleteItem") { removed = true; return null; }
            var q = Assert.IsType<InternalItemsQuery>(args![0]);
            if (q.ParentId == series.Id && ++childReads == 2)
            {
                if (scenario == "config-disabled") f.Config.CleanupCompletedSeries = false;
                if (scenario == "new-favorite") savedDuringCleanup = true;
            }
            return q.ParentId == series.Id ? new QueryResult<BaseItem>(q.IncludeItemTypes.Contains(BaseItemKind.Season) ? [season, episode] : [episode]) : new QueryResult<BaseItem>([series]);
        });
        var users = Proxy<IUserManager>.Create((_, _) => new[] { f.User, disabled });
        var data = Proxy<IUserDataManager>.Create((_, args) => new UserItemData { Key = "test", Played = scenario != "unwatched",
            IsFavorite = savedDuringCleanup || scenario == "disabled-favorite" && args![0] == disabled || scenario == "season-favorite" && args![1] == season });
        var active = new List<SessionInfo>();
        var sessions = Proxy<ISessionManager>.Create((_, _) => active);
        if (scenario == "active-playback") active.Add(new SessionInfo(sessions, NullLogger.Instance) { NowPlayingItem = new() { Id = episode.Id, SeriesId = series.Id } });
        var activity = new TemporaryItemActivity();
        using var lease = scenario == "active-lease" ? activity.BeginUse(series.Path) : null;
        await new Cleanup(library, users, data, sessions, f.Config, sync, f.Cache, activity).ExecuteAsync(new Progress<double>(), default);
        Assert.Equal(scenario == "complete", removed); Assert.Equal(scenario != "complete", File.Exists(stream));
    }

    [Fact]
    public async Task CompletedAndZeroPositionItemsLeaveContinueWatchingAcrossRestart()
    {
        using var f = new Fixture();
        var movie = f.Item(BaseItemKind.Movie); movie.RunTimeTicks = 100; f.Cache.StoreItem(movie);
        var tracker = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        tracker.SaveResumeItem(f.User.Id, movie, 50);
        var filter = new VirtualPlaybackProgressFilter(f.Cache, tracker, NullLogger<VirtualPlaybackProgressFilter>.Instance);
        var ctx = f.Context("Playstate", "ReportPlaybackStopped", new() { ["info"] = new PlaybackStopInfo { ItemId = movie.Id, PositionTicks = 100 } });
        await filter.OnActionExecutionAsync(ctx, () => Task.FromResult(new ActionExecutedContext(ctx, [], new object()) { Result = new NoContentResult() }));
        Assert.Empty(tracker.GetTrackedItemSnapshots(f.User.Id));
        tracker.SaveResumeItem(f.User.Id, movie, 40);
        tracker.SaveResumeTicks(f.User.Id, movie.Id, 0);
        Assert.Equal(0, tracker.GetResumeTicks(f.User.Id, movie.Id));
        Assert.Empty(new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance).GetTrackedItemSnapshots(f.User.Id));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("failed-action")]
    [InlineData("failed-playback")]
    [InlineData("missing-position")]
    [InlineData("another-user")]
    [InlineData("mark-played")]
    [InlineData("mark-unplayed")]
    public async Task ResumeReportsHonorActionSuccessResetAndAccountScope(string scenario)
    {
        using var f = new Fixture();
        var movie = f.Item(BaseItemKind.Movie); movie.RunTimeTicks = 100; f.Cache.StoreItem(movie);
        var tracker = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        tracker.SaveResumeItem(f.User.Id, movie, 40);
        var other = Guid.NewGuid(); tracker.SaveResumeItem(other, movie, 30);
        var filter = new VirtualPlaybackProgressFilter(f.Cache, tracker, NullLogger<VirtualPlaybackProgressFilter>.Instance);
        string action = scenario == "mark-played" ? "MarkPlayedItem" : scenario == "mark-unplayed" ? "MarkUnplayedItem" : "ReportPlaybackStopped";
        var ctx = f.Context("Playstate", action, new()
        {
            ["itemId"] = movie.Id,
            ["info"] = new PlaybackStopInfo { ItemId = movie.Id, PositionTicks = scenario == "missing-position" ? null : 50, Failed = scenario == "failed-playback" }
        });
        if (scenario == "another-user") ctx.ActionArguments["userId"] = other;
        await filter.OnActionExecutionAsync(ctx, () => Task.FromResult(new ActionExecutedContext(ctx, [], new object())
            { Result = scenario == "failed-action" ? new BadRequestResult() : new NoContentResult() }));
        Assert.Equal(scenario.StartsWith("mark-") ? 0 : scenario == "ordinary" ? 50 : 40, tracker.GetResumeTicks(f.User.Id, movie.Id));
        Assert.Equal(30, tracker.GetResumeTicks(other, movie.Id));
        var restarted = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        Assert.Equal(tracker.GetResumeTicks(f.User.Id, movie.Id), restarted.GetResumeTicks(f.User.Id, movie.Id));
    }

    [Fact]
    public void LegacyResumeMigrationDoesNotResurrectClearedSnapshots()
    {
        using var f = new Fixture();
        var movie = f.Item(BaseItemKind.Movie);
        string folder = Path.Combine(f.Root, "Jellyfin.Plugin.Mediarow"); Directory.CreateDirectory(folder);
        string key = f.User.Id.ToString("N"), itemKey = movie.Id.ToString("N");
        File.WriteAllText(Path.Combine(folder,"virtual-resume.json"), System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, Dictionary<string,long>> { [key] = new() { [itemKey] = 50 } }));
        File.WriteAllText(Path.Combine(folder,"virtual-resume-items.json"), System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, Dictionary<string,VirtualResumeItem>> { [key] = new() { [itemKey] = new() { Item = movie, PlaybackPositionTicks = 50, LastPlayedDate = DateTime.UtcNow } } }));
        var tracker = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        Assert.Single(tracker.GetTrackedItemSnapshots(f.User.Id));
        tracker.RemoveResumeItem(f.User.Id, movie.Id);
        var restarted = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        Assert.Equal(0, restarted.GetResumeTicks(f.User.Id, movie.Id)); Assert.Empty(restarted.GetTrackedItemSnapshots(f.User.Id));
    }

    [Fact]
    public void ResumeSnapshotsDetachNestedSourcesAndProviderIdentities()
    {
        using var f = new Fixture();
        var movie = f.Item(BaseItemKind.Movie);
        movie.MediaSources = [new() { Id = "source", Path = "https://media.test/original" }];
        var tracker = new VirtualResumeTracker(f.Paths, NullLogger<VirtualResumeTracker>.Instance);
        tracker.SaveResumeItem(f.User.Id, movie, 50);
        var response = tracker.GetTrackedItem(f.User.Id, movie.Id)!;
        response.ProviderIds["Imdb"] = "client-change";
        response.MediaSources![0].Path = "client-change";
        Assert.Equal("tt1234567", movie.ProviderIds["Imdb"]);
        Assert.Equal("https://media.test/original", movie.MediaSources[0].Path);
        Assert.Equal("tt1234567", tracker.GetTrackedItem(f.User.Id, movie.Id)!.ProviderIds["Imdb"]);
        Assert.Equal("https://media.test/original", tracker.GetTrackedItem(f.User.Id, movie.Id)!.MediaSources![0].Path);
    }

    [Fact]
    public async Task WatchedOnlyCleanupSharesTheActualPersistenceLease()
    {
        using var f = new Fixture();
        f.Config.CleanupWatchedMovies = true;
        Assert.False(f.Config.CleanupUnsavedItems);
        var activity = new TemporaryItemActivity();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var library = Proxy<ILibraryManager>.Create((method, _) =>
        {
            Assert.Equal("UpdateItemAsync", method.Name);
            entered.TrySetResult();
            return release.Task;
        });
        var persistence = new PersistenceService(library, f.Cache, NullLogger<PersistenceService>.Instance, temporaryActivity: activity);
        var movie = f.Item(BaseItemKind.Movie);
        var native = new Movie { Id = movie.Id, Path = persistence.GetMovieFilePath(movie) };
        string title = Path.GetDirectoryName(native.Path)!;
        var writing = persistence.ApplyCatalogMetadataAsync(native, movie, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool deleted = false;
            Assert.False(activity.TryCleanup(title, DateTime.MinValue, TimeSpan.Zero, () => deleted = true));
            Assert.False(deleted);
        }
        finally { release.TrySetResult(); await writing.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(activity.TryCleanup(title, DateTime.MinValue, TimeSpan.Zero, () => true));
    }

    [Theory]
    [InlineData("watched-movies")]
    [InlineData("completed-series")]
    [InlineData("unsaved")]
    public async Task NativeRequestsAcquireLeasesForEitherCleanupMode(string mode)
    {
        using var f = new Fixture();
        f.Config.CleanupWatchedMovies = mode == "watched-movies";
        f.Config.CleanupCompletedSeries = mode == "completed-series";
        f.Config.CleanupUnsavedItems = mode == "unsaved";
        var movie = f.Item(BaseItemKind.Movie); movie.OfficialRating = "PG";
        f.Cache.StoreItem(movie);
        var activity = new TemporaryItemActivity();
        var filter = new TemporaryItemActivityFilter(activity, f.Library, f.Cache, f.Persistence, f.Guard, f.Users);
        var context = f.Context("Mediarow", "PersistItem", new() { ["itemId"] = movie.Id });
        string title = Path.GetDirectoryName(f.Persistence.GetMovieFilePath(movie))!;
        await filter.OnActionExecutionAsync(context, () =>
        {
            bool deleted = false;
            Assert.False(activity.TryCleanup(title, DateTime.MinValue, TimeSpan.Zero, () => deleted = true));
            Assert.False(deleted);
            return Task.FromResult(new ActionExecutedContext(context, [], new object()) { Result = new NoContentResult() });
        });
        Assert.True(activity.TryCleanup(title, DateTime.MinValue, TimeSpan.Zero, () => true));
    }

    [Fact]
    public void AdminDiscoveryCatalogAppearsInNativeLibraries()
    {
        using var f = new Fixture();
        f.Config.EnableHomeRows = true; f.Config.EnableAppLibraries = true;
        var selection = new HomeCatalogSelection { Id = "audit-catalog", Title = "My discovery" };
        f.Config.HomeCatalogs = [selection];
        var preferences = new UserPreferenceStore(f.Root, NullLogger<UserPreferenceStore>.Instance);
        var manager = new HomeSectionManager(f.Memory, NullLogger<HomeSectionManager>.Instance, preferences);
        var bootstrap = new HomeSectionBootstrapper(manager, null!, f.Cache, null!, NullLogger<HomeSectionBootstrapper>.Instance);
        bootstrap.RegisterConfiguredSections();
        Assert.Contains(manager.GetSections(), s => s is DiscoveryCatalogHomeSection && s.Id == selection.SectionId);
        var app = new AppLibraryService(manager, bootstrap, new UserPreferenceFilter(preferences, null!), null!);
        Assert.Contains(app.GetLibraries(f.User.Id), row => row.SectionId == selection.SectionId);
    }

    [Fact]
    public void CustomCatalogsKeepCategoryOrderVisibilityAndDoNotConsumeGenreSlots()
    {
        using var f = new Fixture();
        f.Config.EnableHomeRows = true; f.Config.EnableAppLibraries = true;
        f.Config.HomeRowContentCategories = ["Movies", "Series"];
        f.Config.HomeRowGenres = Enumerable.Range(1, 8).Select(index => "Genre" + index).ToList();
        var first = new HomeCatalogSelection { Id = "first", Title = "First", MediaType = DiscoveryMediaType.Movie };
        var last = new HomeCatalogSelection { Id = "last", Title = "Last", MediaType = DiscoveryMediaType.Series };
        f.Config.HomeCatalogs = [first, last];
        var preferences = new UserPreferenceStore(f.Root, NullLogger<UserPreferenceStore>.Instance);
        preferences.Update(f.User.Id, new UserPreferencesUpdate { SectionOrder = [last.SectionId, first.SectionId] });
        var manager = new HomeSectionManager(f.Memory, NullLogger<HomeSectionManager>.Instance, preferences);
        var bootstrap = new HomeSectionBootstrapper(manager, null!, f.Cache, null!, NullLogger<HomeSectionBootstrapper>.Instance);
        var app = new AppLibraryService(manager, bootstrap, new UserPreferenceFilter(preferences, null!), null!);
        var rows = app.GetLibraries(f.User.Id);
        Assert.Equal(last.SectionId, rows[0].SectionId); Assert.Equal("Series", rows[0].Category);
        Assert.Equal(first.SectionId, rows[1].SectionId); Assert.Equal("Movies", rows[1].Category);
        Assert.Equal(6, rows.Count(row => row.SectionId.StartsWith("Mediarow_Genre")));
        Assert.Equal(Enumerable.Range(0, rows.Count), rows.Select(row => row.Order));
        preferences.Update(f.User.Id, new UserPreferencesUpdate { HiddenSections = [last.SectionId] });
        Assert.DoesNotContain(app.GetLibraries(f.User.Id), row => row.SectionId == last.SectionId);
    }

    private sealed class VisibleSeries : Series { public override bool IsVisibleStandalone(User user) => true; }
    private sealed class HiddenMovie : Movie { public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => false; }
    private sealed class FolderHiddenMovie : Movie
    {
        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => true;
        public override bool IsVisibleStandalone(User user) => false;
    }
    private sealed class Cleanup(ILibraryManager l, IUserManager u, IUserDataManager d, ISessionManager s, PluginConfiguration c, EpisodeSyncService sync, DynamicItemCache cache, TemporaryItemActivity activity)
        : Jellyfin.Plugin.Mediarow.ScheduledTasks.WatchedItemCleanupTask(l,u,d,s,NullLogger<Jellyfin.Plugin.Mediarow.ScheduledTasks.WatchedItemCleanupTask>.Instance, activity, sync, cache)
    { protected override PluginConfiguration Config => c; }
    private sealed class TestPlugin(IApplicationPaths paths, IXmlSerializer serializer, PluginConfiguration config)
        : MediarowPlugin(paths, serializer, NullLogger<MediarowPlugin>.Instance)
    { public void SetConfig() => Configuration = config; }
    private sealed class Fixture : IDisposable
    {
        private readonly MediarowPlugin? _originalInstance = MediarowPlugin.Instance;
        public readonly string Root = Path.Combine(Path.GetTempPath(), "mediarow-audit-" + Guid.NewGuid().ToString("N"));
        public readonly MemoryCache Memory = new(new MemoryCacheOptions());
        public readonly User User = new("minor", "test", "test") { Id = Guid.NewGuid(), MaxParentalRatingScore = 13 };
        public readonly IApplicationPaths Paths;
        public readonly PluginConfiguration Config;
        public readonly DynamicItemCache Cache;
        public readonly ILibraryManager Library;
        public readonly IUserManager Users;
        public readonly UserPermissionGuard Guard;
        public readonly PersistenceService Persistence;
        public ClaimsPrincipal Principal => new(new ClaimsIdentity([new Claim(UserPermissionGuard.UserIdClaim, User.Id.ToString())], "test"));
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Paths = Proxy<IApplicationPaths>.Create((_, _) => Root);
            Config = new() { EnablePersistence = true, TriggerLibraryScan = false, StreamProvider = StreamProvider.Direct,
                MoviesLibraryPath = Path.Combine(Root,"movies"), SeriesLibraryPath = Path.Combine(Root,"tv"), AnimeLibraryPath = Path.Combine(Root,"anime") };
            new TestPlugin(Paths, Proxy<IXmlSerializer>.Create((_, _) => null), Config).SetConfig();
            User.SetPermission(PermissionKind.EnableAllFolders, true);
            Cache = new(Memory, NullLogger<DynamicItemCache>.Instance);
            Library = Proxy<ILibraryManager>.Create((method, _) => method.Name == "GetItemsResult" ? new QueryResult<BaseItem>([]) : null);
            Users = Proxy<IUserManager>.Create((_, _) => User);
            var localization = Proxy<ILocalizationManager>.Create((_, args) => new ParentalRatingScore((string)args![0]! == "PG" ? 10 : 17, null));
            Guard = new(Users, Library, localization, Cache, Memory);
            Persistence = new(Library, Cache, NullLogger<PersistenceService>.Instance);
        }
        public BaseItemDto Item(BaseItemKind type) => new() { Id = Guid.NewGuid(), Name = "Restricted title", Type = type, OfficialRating = "R",
            PremiereDate = DateTime.UtcNow.AddYears(-1), ProviderIds = new() { ["Mediarow"] = "test", ["Imdb"] = "tt1234567" } };
        public ActionExecutingContext Context(string controller, string action, Dictionary<string, object?> args) => new(
            new ActionContext(new DefaultHttpContext { User = Principal }, new RouteData(), new ControllerActionDescriptor { ControllerName = controller, ActionName = action }), [], args, new object());
        public void Dispose()
        {
            typeof(MediarowPlugin).GetProperty(nameof(MediarowPlugin.Instance))!.SetValue(null, _originalInstance);
            Memory.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    public class Proxy<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[]?, object?> _invoke = null!;
        public static T Create(Func<MethodInfo, object?[]?, object?> fn) { var p = Create<T, Proxy<T>>(); ((Proxy<T>)(object)p)._invoke = fn; return p; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => _invoke(method!, args);
    }
}
