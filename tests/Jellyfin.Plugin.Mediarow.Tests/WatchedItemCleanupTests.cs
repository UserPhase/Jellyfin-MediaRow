using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Services;
using Jellyfin.Plugin.Mediarow.ScheduledTasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class WatchedItemCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mediarow-cleanup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _files = [];
    private readonly List<string> _directories = [];

    [Fact]
    public void CleanupIsOptInAndWaitsForEveryViewer()
    {
        var config = new PluginConfiguration();
        Assert.False(config.CleanupWatchedMovies);
        Assert.False(config.CleanupCompletedSeries);
        Assert.False(WatchedItemCleanup.CanRemoveMovie(false, [true], false, false));
        Assert.False(WatchedItemCleanup.CanRemoveMovie(true, [], false, false));
        Assert.False(WatchedItemCleanup.CanRemoveMovie(true, [true, false], false, false));
        Assert.False(WatchedItemCleanup.CanRemoveMovie(true, [true], true, false));
        Assert.False(WatchedItemCleanup.CanRemoveMovie(true, [true], false, true));
        Assert.True(WatchedItemCleanup.CanRemoveMovie(true, [true, true], false, false));
    }

    [Fact]
    public void SeriesMustBeEndedFullyWatchedAndNotInUse()
    {
        Assert.True(WatchedItemCleanup.CanRemoveSeries(true, SeriesStatus.Ended, [true, true], false, false, false));
        Assert.False(WatchedItemCleanup.CanRemoveSeries(false, SeriesStatus.Ended, [true], false, false, false));
        Assert.False(WatchedItemCleanup.CanRemoveSeries(true, null, [true], false, false, false));
        Assert.False(WatchedItemCleanup.CanRemoveSeries(true, SeriesStatus.Continuing, [true], false, false, false));
        Assert.False(WatchedItemCleanup.CanRemoveSeries(true, SeriesStatus.Ended, [], false, false, false));
        Assert.False(WatchedItemCleanup.CanRemoveSeries(true, SeriesStatus.Ended, [true, false], false, false, false));
        Assert.False(WatchedItemCleanup.CanRemoveSeries(true, SeriesStatus.Ended, [true], true, false, false));
        Assert.False(WatchedItemCleanup.CanRemoveSeries(true, SeriesStatus.Ended, [true], false, true, false));
        Assert.False(WatchedItemCleanup.CanRemoveSeries(true, SeriesStatus.Ended, [true], false, false, true));
    }

    [Fact]
    public void MovieDeletesOnlyMarkedStreamAndCompanionNfo()
    {
        string stream = Write("movies", "Movie", "Movie.strm", "mediarow://movie/tt1234");
        string title = Path.GetDirectoryName(stream)!;
        string nfo = Write("movies", "Movie", "Movie.nfo", "<movie/>");
        string artwork = Write("movies", "Movie", "poster.jpg", "keep");
        WatchedItemCleanup.DeleteOwnedFiles(_root, title, false, [stream]);
        Assert.False(File.Exists(stream));
        Assert.False(File.Exists(nfo));
        Assert.True(File.Exists(artwork));
        Assert.True(Directory.Exists(title));
    }

    [Fact]
    public void CompletedSeriesDeletesIndexedStreamsAndNfosAndEmptyTitleFolders()
    {
        string first = Write("tv", "Series", Path.Combine("Season 01", "S01E01.strm"), "mediarow://tv/tt1234/1/1");
        string second = Write("tv", "Series", Path.Combine("Season 01", "S01E02.strm"), "dynamiclibrary://tv/tt1234/1/2");
        Write("tv", "Series", "tvshow.nfo", "<tvshow/>");
        Write("tv", "Series", Path.Combine("Season 01", "season.nfo"), "<season/>");
        Write("tv", "Series", Path.Combine("Season 01", "S01E01.nfo"), "<episodedetails/>");
        string title = Path.Combine(_root, "tv", "Series");
        WatchedItemCleanup.DeleteOwnedFiles(_root, title, true, [first, second]);
        Assert.False(Directory.Exists(title));
        Assert.True(Directory.Exists(Path.Combine(_root, "tv")));
    }

    [Fact]
    public void UnmarkedMixedAndUnindexedFoldersAreRefused()
    {
        string stream = Write("movies", "Movie", "Movie.strm", "https://video.example/movie.mp4");
        string title = Path.GetDirectoryName(stream)!;
        Assert.Empty(WatchedItemCleanup.GetOwnedFiles(_root, title, false, [stream]));
        File.WriteAllText(stream, "mediarow://movie/tt1234");
        string realMedia = Write("movies", "Movie", "Movie.mp4", "real media");
        Assert.Empty(WatchedItemCleanup.GetOwnedFiles(_root, title, false, [stream]));
        File.Delete(realMedia);
        Write("movies", "Movie", "Extra.strm", "mediarow://movie/tt5678");
        Assert.Empty(WatchedItemCleanup.GetOwnedFiles(_root, title, false, [stream]));
        Assert.Throws<InvalidOperationException>(() => WatchedItemCleanup.DeleteOwnedFiles(_root, title, false, [stream]));
        Assert.True(File.Exists(stream));
    }

    [Fact]
    public void RootCategoryAndOutsideDirectoriesCannotBeDeleted()
    {
        string stream = Write("outside", "Movie", "Movie.strm", "mediarow://movie/tt1234");
        Assert.Empty(WatchedItemCleanup.GetOwnedFiles(_root, _root, false, [stream]));
        Assert.Empty(WatchedItemCleanup.GetOwnedFiles(_root, Path.Combine(_root, "movies"), false, [stream]));
        Assert.Empty(WatchedItemCleanup.GetOwnedFiles(_root, Path.GetDirectoryName(stream)!, false, [stream]));
        Assert.Empty(WatchedItemCleanup.GetOwnedFiles("relative", Path.GetDirectoryName(stream)!, false, [stream]));
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    public async Task ScheduledCleanupHonorsViewerPlaybackFavoriteAndRecentFileGuards(
        bool secondUserWatched, bool inUse, bool favorite, bool recent, bool expectedRemoval)
    {
        string stream = Write("movies", "Movie", "Movie.strm", "mediarow://movie/tt1234");
        File.SetLastWriteTimeUtc(stream, recent ? DateTime.UtcNow : DateTime.UtcNow.AddHours(-1));
        var movie = new VisibleMovie { Id = Guid.NewGuid(), Path = stream };
        var users = new[] { new User("first", "test", "test"), new User("second", "test", "test") };
        bool removed = false;
        var library = AioStreamsPlaybackTests.Proxy<ILibraryManager>.Create((method, args) =>
        {
            if (method.Name == "GetItemsResult") return new QueryResult<BaseItem>([movie]);
            if (method.Name == "DeleteItem")
            {
                Assert.Equal(movie, args![0]);
                var options = Assert.IsType<DeleteOptions>(args[1]);
                Assert.False(options.DeleteFileLocation);
                Assert.False(options.DeleteFromExternalProvider);
                removed = true;
                return null;
            }
            throw new NotSupportedException(method.Name);
        });
        var userManager = AioStreamsPlaybackTests.Proxy<IUserManager>.Create((method, _) =>
            method.Name == "GetUsers" ? users : throw new NotSupportedException(method.Name));
        var data = AioStreamsPlaybackTests.Proxy<IUserDataManager>.Create((method, args) =>
            method.Name == "GetUserData"
                ? new UserItemData { Key = "cleanup-test", Played = args![0] == users[0] || secondUserWatched, IsFavorite = favorite }
                : throw new NotSupportedException(method.Name));
        var activeSessions = new List<SessionInfo>();
        var sessions = AioStreamsPlaybackTests.Proxy<ISessionManager>.Create((method, _) =>
            method.Name == "get_Sessions" ? activeSessions : throw new NotSupportedException(method.Name));
        if (inUse)
        {
            activeSessions.Add(new SessionInfo(sessions, NullLogger.Instance)
            {
                NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = movie.Id }
            });
        }
        var task = new TestTask(library, userManager, data, sessions,
            new PluginConfiguration
            {
                PersistentLibraryPath = Path.Combine(_root, "unused"),
                MoviesLibraryPath = Path.Combine(_root, "movies"), CleanupWatchedMovies = true
            });
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal(expectedRemoval, removed);
        Assert.Equal(!expectedRemoval, File.Exists(stream));
    }

    private sealed class VisibleMovie : Movie
    {
        public override bool IsVisibleStandalone(User user) => true;
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task ScheduledSeriesCleanupRequiresEndedStatusAndEveryEpisode(bool ended, bool lastWatched, bool expectedRemoval)
    {
        string first = Write("tv", "Series", Path.Combine("Season 01", "S01E01.strm"), "mediarow://tv/tt1234/1/1");
        string last = Write("tv", "Series", Path.Combine("Season 01", "S01E02.strm"), "mediarow://tv/tt1234/1/2");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(last, DateTime.UtcNow.AddHours(-1));
        var series = new VisibleSeries
        {
            Id = Guid.NewGuid(), Path = Path.Combine(_root, "tv", "Series"),
            Status = ended ? SeriesStatus.Ended : SeriesStatus.Continuing
        };
        var episodes = new[]
        {
            new Episode { Id = Guid.NewGuid(), Path = first, PremiereDate = DateTime.UtcNow.AddDays(-1) },
            new Episode { Id = Guid.NewGuid(), Path = last, PremiereDate = DateTime.UtcNow.AddDays(-1) }
        };
        var user = new User("viewer", "test", "test");
        bool removed = false;
        var library = AioStreamsPlaybackTests.Proxy<ILibraryManager>.Create((method, args) =>
        {
            if (method.Name == "GetItemsResult")
            {
                var query = Assert.IsType<InternalItemsQuery>(args![0]);
                return query.ParentId == series.Id
                    ? new QueryResult<BaseItem>(episodes)
                    : new QueryResult<BaseItem>([series]);
            }
            if (method.Name == "DeleteItem")
            {
                Assert.Equal(series, args![0]);
                removed = true;
                return null;
            }
            throw new NotSupportedException(method.Name);
        });
        var users = AioStreamsPlaybackTests.Proxy<IUserManager>.Create((method, _) =>
            method.Name == "GetUsers" ? new[] { user } : throw new NotSupportedException(method.Name));
        var data = AioStreamsPlaybackTests.Proxy<IUserDataManager>.Create((method, args) =>
            method.Name == "GetUserData"
                ? new UserItemData { Key = "cleanup-test", Played = args![1] != episodes[1] || lastWatched }
                : throw new NotSupportedException(method.Name));
        var sessions = AioStreamsPlaybackTests.Proxy<ISessionManager>.Create((method, _) =>
            method.Name == "get_Sessions" ? Array.Empty<SessionInfo>() : throw new NotSupportedException(method.Name));
        await new TestTask(library, users, data, sessions,
            new PluginConfiguration
            {
                PersistentLibraryPath = Path.Combine(_root, "unused"),
                SeriesLibraryPath = Path.Combine(_root, "tv"),
                AnimeLibraryPath = Path.Combine(_root, "anime"), CleanupCompletedSeries = true
            })
            .ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Equal(expectedRemoval, removed);
        Assert.Equal(!expectedRemoval, Directory.Exists(series.Path));
    }

    private sealed class VisibleSeries : Series
    {
        public override bool IsVisibleStandalone(User user) => true;
    }

    private sealed class TestTask(
        ILibraryManager library, IUserManager users, IUserDataManager data, ISessionManager sessions, PluginConfiguration config)
        : WatchedItemCleanupTask(library, users, data, sessions, NullLogger<WatchedItemCleanupTask>.Instance)
    {
        protected override PluginConfiguration Config => config;
    }

    private string Write(string category, string title, string file, string content)
    {
        string path = Path.Combine(_root, category, title, file);
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (directory != null && directory.FullName.StartsWith(_root, StringComparison.Ordinal))
        {
            if (!_directories.Contains(directory.FullName)) _directories.Add(directory.FullName);
            directory = directory.Parent;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string file in _files)
        {
            if (File.Exists(file)) File.Delete(file);
        }
        foreach (string directory in _directories.OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
