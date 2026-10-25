using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.ScheduledTasks;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class UnsavedItemCleanupTests
{
    [Fact]
    public void DefaultsAreOptInAndGraceIsBounded()
    {
        var config = new PluginConfiguration();
        Assert.False(config.CleanupUnsavedItems); Assert.Equal(5, config.UnsavedItemGraceMinutes);
        config.UnsavedItemGraceMinutes = -100; Assert.Equal(1, config.UnsavedItemGraceMinutes);
        config.UnsavedItemGraceMinutes = int.MaxValue; Assert.Equal(10080, config.UnsavedItemGraceMinutes);
    }

    [Fact]
    public void RequestsImportsAndOverlappingUsesHoldTitleUntilFullGraceAfterRelease()
    {
        var clock = new Clock(); var activity = new TemporaryItemActivity(clock);
        activity.SetEnabled(true);
        var first = activity.BeginUse("title"); var second = activity.BeginUse("title");
        clock.Advance(60);
        Assert.False(activity.TryCleanup("title", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
        first.Dispose(); first.Dispose();
        Assert.False(activity.TryCleanup("title", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
        second.Dispose(); clock.Advance(4);
        Assert.False(activity.TryCleanup("title", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
        clock.Advance(1);
        Assert.True(activity.TryCleanup("title", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
    }

    [Fact]
    public void ReenablingAndRestartingGrantExistingFilesFreshGrace()
    {
        var clock = new Clock(); var activity = new TemporaryItemActivity(clock);
        activity.SetEnabled(true); clock.Advance(6);
        activity.SetEnabled(false);
        Assert.False(activity.TryCleanup("title", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
        activity.SetEnabled(true);
        Assert.False(activity.TryCleanup("title", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
        clock.Advance(5);
        Assert.True(activity.TryCleanup("title", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
        var restarted = new TemporaryItemActivity(clock); restarted.SetEnabled(true);
        Assert.False(restarted.TryCleanup("title", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
    }

    [Fact]
    public async Task CleanupSerializesOneTitleWithoutBlockingOtherTitles()
    {
        var clock = new Clock(); var activity = new TemporaryItemActivity(clock);
        activity.SetEnabled(true); clock.Advance(6);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var cleanup = Task.Run(() => activity.TryCleanup("first", DateTime.MinValue, TimeSpan.FromMinutes(5), () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return true;
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var other = Task.Run(() => { using var use = activity.BeginUse("second"); });
            await other.WaitAsync(TimeSpan.FromSeconds(5));
            release.Set(); Assert.True(await cleanup);
            using var recreated = activity.BeginUse("first");
            Assert.False(activity.TryCleanup("first", DateTime.MinValue, TimeSpan.FromMinutes(5), () => true));
        }
        finally { release.Set(); await cleanup; }
    }

    [Theory]
    [InlineData("movies/Film/Film.strm", "movies/Film")]
    [InlineData("tv/Show/Season 01/S01E01.strm", "tv/Show")]
    [InlineData("anime/Show/Season 01", "anime/Show")]
    [InlineData("../movies/Film/Film.strm", null)]
    [InlineData("movies", null)]
    public void PathsResolveOnlyToConfiguredTitleDirectories(string relative, string? expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "mediarow-path-tests");
        var path = TemporaryItemActivity.GetTitlePath(root, Path.GetFullPath(Path.Combine(root, relative)));
        Assert.Equal(expected == null ? null : Path.GetFullPath(Path.Combine(root, expected)), path);
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    public async Task UnwatchedMoviesExpireButFavoritesPlaybackAndRecentFilesAreRetained(
        bool enabled, bool favorite, bool playing, bool recent, bool removed)
    {
        using var f = new Fixture(); f.Config.CleanupUnsavedItems = enabled;
        f.Favorite = favorite; f.Playing = playing;
        await f.Run(); f.Clock.Advance(6);
        if (recent) File.SetLastWriteTimeUtc(f.Stream, f.Clock.GetUtcNow().UtcDateTime);
        await f.Run();
        Assert.Equal(removed, f.Removed); Assert.Equal(!removed, File.Exists(f.Stream));
        Assert.False(f.Data.Played); // Completion is deliberately irrelevant to this cleanup policy.
    }

    [Fact]
    public async Task CustomGraceAndPosterRevisitRestartTheClock()
    {
        using var f = new Fixture(); f.Config.UnsavedItemGraceMinutes = 17;
        await f.Run(); f.Clock.Advance(16); await f.Run(); Assert.False(f.Removed);
        using (f.Activity.BeginUse(f.Title)) { }
        f.Clock.Advance(16); await f.Run(); Assert.False(f.Removed);
        f.Clock.Advance(1); await f.Run(); Assert.True(f.Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLibraryDestinationsAreCleanedWithoutUsingTheLegacyRoot(bool series)
    {
        using var f = new Fixture(series);
        if (series) f.Config.SeriesLibraryPath = Path.GetDirectoryName(f.Title)!;
        else f.Config.MoviesLibraryPath = Path.GetDirectoryName(f.Title)!;
        f.Config.PersistentLibraryPath = Path.Combine(f.Root, "unused-legacy-root");
        await f.Run(); f.Clock.Advance(6); await f.Run();
        Assert.True(f.Removed);
        Assert.False(File.Exists(f.Stream));
    }

    [Fact]
    public async Task UnfavoriteAndPlaybackStopStartFreshGraceIncludingPausedPlayback()
    {
        using var f = new Fixture(); await f.Run(); f.Clock.Advance(6);
        f.Favorite = true; await f.Run(); Assert.False(f.Removed);
        f.Favorite = false; f.Clock.Advance(4); await f.Run(); Assert.False(f.Removed);
        f.Playing = true; f.Session.PlayState.IsPaused = true;
        f.Clock.Advance(20); await f.Run(); Assert.False(f.Removed);
        f.Playing = false; f.Clock.Advance(4); await f.Run(); Assert.False(f.Removed);
        f.Clock.Advance(1); await f.Run(); Assert.True(f.Removed);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task OngoingSeriesExpireUnlessAChildIsSavedOrPlaying(bool favoriteChild, bool playingChild, bool removed)
    {
        using var f = new Fixture(series: true); f.Favorite = favoriteChild; f.Playing = playingChild;
        await f.Run(); f.Clock.Advance(6); await f.Run();
        Assert.Equal(removed, f.Removed); Assert.Equal(!removed, Directory.Exists(f.Title));
    }

    [Fact]
    public async Task MixedFoldersAndUnmarkedStreamsAreNeverRemoved()
    {
        using var f = new Fixture(); await f.Run(); f.Clock.Advance(6);
        var actualMovie = Path.Combine(f.Title, "movie.mp4");
        File.WriteAllText(actualMovie, "user media");
        await f.Run(); Assert.False(f.Removed); Assert.True(File.Exists(actualMovie));
        File.Delete(actualMovie); File.WriteAllText(f.Stream, "https://ordinary.test/movie.mp4");
        await f.Run(); Assert.False(f.Removed); Assert.True(File.Exists(f.Stream));
    }

    [Fact]
    public async Task InFlightImportPreventsCleanupPastTheConfiguredDeadline()
    {
        using var f = new Fixture(); await f.Run();
        using (f.Activity.BeginUse(f.Title))
        { f.Clock.Advance(30); await f.Run(); Assert.False(f.Removed); }
        await f.Run(); Assert.False(f.Removed);
        f.Clock.Advance(5); await f.Run(); Assert.True(f.Removed);
    }

    [Fact]
    public async Task LibraryScansPreventCleanup()
    {
        using var f = new Fixture(); f.Scanning = true;
        await f.Run(); f.Clock.Advance(10); await f.Run(); Assert.False(f.Removed);
        f.Scanning = false; await f.Run(); Assert.True(f.Removed);
    }

    [Fact]
    public async Task ExistingWatchedTaskDefersToUnsavedGracePolicy()
    {
        await new WatchedTask(new PluginConfiguration { CleanupUnsavedItems = true, CleanupWatchedMovies = true })
            .ExecuteAsync(new Progress<double>(), default);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(1);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int minutes) => _now = _now.AddMinutes(minutes);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "mediarow-unsaved-tests-" + Guid.NewGuid().ToString("N"));
        public readonly Clock Clock = new();
        public readonly TemporaryItemActivity Activity;
        public readonly PluginConfiguration Config;
        public readonly string Title;
        public readonly string Stream;
        public readonly SessionInfo Session;
        public readonly UserItemData Data = new() { Key = "test", PlaybackPositionTicks = 50, Played = false };
        public bool Favorite, Playing, Removed, Scanning;
        private readonly TestTask _task;

        public Fixture(bool series = false)
        {
            Config = new() { PersistentLibraryPath = Root, CleanupUnsavedItems = true };
            Activity = new(Clock);
            Title = Path.Combine(Root, series ? "tv" : "movies", "Title");
            Directory.CreateDirectory(series ? Path.Combine(Title, "Season 01") : Title);
            Stream = series ? Path.Combine(Title, "Season 01", "S01E01.strm") : Path.Combine(Title, "Movie.strm");
            File.WriteAllText(Stream, series ? "mediarow://tv/tt1234/1/1" : "mediarow://movie/tt1234");
            File.SetLastWriteTimeUtc(Stream, DateTime.UtcNow.AddHours(-1));
            BaseItem item = series ? new Series { Id = Guid.NewGuid(), Path = Title, Status = SeriesStatus.Continuing }
                : new Movie { Id = Guid.NewGuid(), Path = Stream };
            var episode = new Episode { Id = Guid.NewGuid(), Path = Stream, SeriesId = item.Id };
            var firstUser = new User("first", "test", "test");
            var secondUser = new User("second", "test", "test");
            var library = AioStreamsPlaybackTests.Proxy<ILibraryManager>.Create((method, args) =>
            {
                if (method.Name == "get_IsScanRunning") return Scanning;
                if (method.Name == "GetItemsResult")
                {
                    var query = (InternalItemsQuery)args![0]!;
                    return new QueryResult<BaseItem>(query.ParentId == item.Id ? [episode] : Removed ? [] : [item]);
                }
                if (method.Name == "DeleteItem")
                {
                    var options = Assert.IsType<DeleteOptions>(args![1]);
                    Assert.False(options.DeleteFileLocation); Assert.False(options.DeleteFromExternalProvider);
                    Removed = true; return null;
                }
                throw new NotSupportedException(method.Name);
            });
            var users = AioStreamsPlaybackTests.Proxy<IUserManager>.Create((method, _) =>
                method.Name == "GetUsers" ? new[] { firstUser, secondUser } : throw new NotSupportedException(method.Name));
            var data = AioStreamsPlaybackTests.Proxy<IUserDataManager>.Create((method, args) =>
            {
                if (method.Name != "GetUserData") throw new NotSupportedException(method.Name);
                return Favorite && args![0] == secondUser && args[1] == (series ? episode : item)
                    ? new UserItemData { Key = "favorite", IsFavorite = true } : Data;
            });
            var sessions = AioStreamsPlaybackTests.Proxy<ISessionManager>.Create((method, _) =>
                method.Name == "get_Sessions" ? Playing ? new[] { Session! } : [] : throw new NotSupportedException(method.Name));
            Session = new SessionInfo(sessions, NullLogger.Instance)
            { NowPlayingItem = new BaseItemDto { Id = series ? episode.Id : item.Id, SeriesId = series ? item.Id : null } };
            _task = new TestTask(library, users, data, sessions, Activity, Config);
        }

        public Task Run() => _task.ExecuteAsync(new Progress<double>(), default);
        public void Dispose()
        {
            // Only this fixture's explicit files/directories are removed, without recursive deletion.
            if (Directory.Exists(Title))
            {
                foreach (var file in Directory.GetFiles(Title, "*", SearchOption.AllDirectories)) File.Delete(file);
                foreach (var directory in Directory.GetDirectories(Title)) Directory.Delete(directory);
                Directory.Delete(Title);
            }
            if (Directory.Exists(Root))
            { foreach (var directory in Directory.GetDirectories(Root)) Directory.Delete(directory); Directory.Delete(Root); }
        }
    }

    private sealed class TestTask(ILibraryManager library, IUserManager users, IUserDataManager data,
        ISessionManager sessions, TemporaryItemActivity activity, PluginConfiguration config)
        : UnsavedItemCleanupTask(library, users, data, sessions, activity, NullLogger<UnsavedItemCleanupTask>.Instance)
    {
        protected override PluginConfiguration Config => config;
    }

    private sealed class WatchedTask(PluginConfiguration config)
        : WatchedItemCleanupTask(null!, null!, null!, null!, NullLogger<WatchedItemCleanupTask>.Instance)
    {
        protected override PluginConfiguration Config => config;
    }
}
