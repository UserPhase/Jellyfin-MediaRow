using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class MovieWatchHistoryTests
{
    [Fact]
    public async Task TenThousandWatchedMoviesNeedOneBootstrapQueryAndMatchDifferentNativeIds()
    {
        using var f = new Fixture();
        f.Movies = Enumerable.Range(1, 10000).Select(number => new Movie
        { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt" + number } }).ToArray();
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.History.GetSnapshotAsync(f.A, default)));
        Assert.Equal(1, f.Queries);
        foreach (var snapshot in snapshots)
        {
            Assert.True(snapshot.IsWatched(Item("tt1")));
            Assert.True(snapshot.IsWatched(Item("tt10000")));
            Assert.False(snapshot.IsWatched(Item("tt10001")));
        }
        await f.History.StopAsync(default);
        var files = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories);
        Assert.Equal("movie-watch-history.json", Path.GetFileName(Assert.Single(files)));
        Assert.True(new FileInfo(files[0]).Length < 2_000_000);
    }

    [Fact]
    public async Task MarkUnwatchedOverridesBootstrapAndSnapshotsRemainIsolated()
    {
        using var f = new Fixture(); var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt7" } };
        f.Movies = [movie];
        f.Save(movie, f.A, false);
        Assert.False((await f.History.GetSnapshotAsync(f.A, default)).IsWatched(Item("tt7")));
        f.Save(movie, f.A, true);
        var before = await f.History.GetSnapshotAsync(f.A, default);
        Assert.True(before.IsWatched(Item("tt7")));
        Assert.False((await f.History.GetSnapshotAsync(f.B, default)).IsWatched(Item("tt7")));
        f.Save(movie, f.A, false);
        Assert.False((await f.History.GetSnapshotAsync(f.A, default)).IsWatched(Item("tt7")));
        Assert.True(before.IsWatched(Item("tt7")));
    }

    [Fact]
    public async Task WatchedAndExplicitUnwatchedSurviveDeletionAndRestartWithoutTitleFiles()
    {
        using var f = new Fixture();
        var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt8", ["Tmdb"] = "42" } };
        var other = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt9" } };
        f.Save(movie, f.A, true); f.Save(other, f.A, true); f.Save(other, f.A, false);
        await f.History.StopAsync(default); f.Movies = [];
        using var restarted = f.CreateHistory();
        var snapshot = await restarted.GetSnapshotAsync(f.A, default);
        Assert.True(snapshot.IsWatched(Item("tt8")));
        Assert.True(snapshot.IsWatched(new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemKind.Movie, ProviderIds = new() { ["Tmdb"] = "42" } }));
        Assert.False(snapshot.IsWatched(Item("tt9")));
        Assert.Single(Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories));
        await restarted.StopAsync(default);
    }

    [Fact]
    public async Task PartialPlaybackAndSeriesDoNotCountAsWatchedMovies()
    {
        using var f = new Fixture();
        f.Save(new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt5" } }, f.A, false, UserDataSaveReason.PlaybackProgress);
        f.Save(new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt6" } }, f.A, true);
        var snapshot = await f.History.GetSnapshotAsync(f.A, default);
        Assert.False(snapshot.IsWatched(Item("tt5"))); Assert.False(snapshot.IsWatched(Item("tt6")));
        await f.History.StopAsync(default);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "Jellyfin.Plugin.Mediarow")));
    }

    private static BaseItemDto Item(string imdb) => new() { Id = Guid.NewGuid(), Type = BaseItemKind.Movie, ProviderIds = new() { ["Imdb"] = imdb } };

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "mediarow-watch-index-" + Guid.NewGuid().ToString("N"));
        public readonly User A = new("A", "test", "test") { Id = Guid.NewGuid() };
        public readonly User B = new("B", "test", "test") { Id = Guid.NewGuid() };
        public readonly MovieWatchHistory History;
        public Movie[] Movies = [];
        public int Queries;
        private readonly ILibraryManager _library;
        private readonly IUserDataManager _data;
        private readonly IApplicationPaths _paths;
        private EventHandler<UserDataSaveEventArgs>? _saved;

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            _paths = AioStreamsPlaybackTests.Proxy<IApplicationPaths>.Create((_, _) => Root);
            _library = AioStreamsPlaybackTests.Proxy<ILibraryManager>.Create((method, args) =>
            {
                if (method.Name != "GetItemsResult") throw new NotSupportedException(method.Name);
                Queries++;
                var query = Assert.IsType<InternalItemsQuery>(args![0]);
                Assert.True(query.IsPlayed); Assert.Equal([BaseItemKind.Movie], query.IncludeItemTypes);
                return new QueryResult<BaseItem>(query.User?.Id == A.Id ? Movies : []);
            });
            _data = AioStreamsPlaybackTests.Proxy<IUserDataManager>.Create((method, args) =>
            {
                if (method.Name == "add_UserDataSaved") _saved += (EventHandler<UserDataSaveEventArgs>)args![0]!;
                else if (method.Name == "remove_UserDataSaved") _saved -= (EventHandler<UserDataSaveEventArgs>)args![0]!;
                else throw new NotSupportedException(method.Name);
                return null;
            });
            History = CreateHistory();
        }

        public MovieWatchHistory CreateHistory() => new(_library, _data, _paths, NullLogger<MovieWatchHistory>.Instance);
        public void Save(BaseItem movie, User user, bool played, UserDataSaveReason reason = UserDataSaveReason.TogglePlayed) =>
            _saved?.Invoke(this, new UserDataSaveEventArgs
            { Item = movie, UserId = user.Id, UserData = new UserItemData { Key = "test", Played = played, PlaybackPositionTicks = played ? 0 : 50 }, SaveReason = reason });
        public void Dispose()
        {
            History.Dispose();
            var folder = Path.Combine(Root, "Jellyfin.Plugin.Mediarow");
            if (Directory.Exists(folder))
            { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
            Directory.Delete(Root);
        }
    }
}
