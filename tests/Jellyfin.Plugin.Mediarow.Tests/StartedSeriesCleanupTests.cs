using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.ScheduledTasks;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Mediarow.Tests.AioStreamsPlaybackTests;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class StartedSeriesCleanupTests
{
    [Theory]
    [InlineData("episode-one-finished")]
    [InlineData("partial-episode")]
    [InlineData("caught-up-continuing")]
    [InlineData("unknown-status")]
    [InlineData("ended-but-unfinished")]
    [InlineData("other-account")]
    [InlineData("disabled-account")]
    [InlineData("only-play-count")]
    [InlineData("only-play-date")]
    [InlineData("all-removal-settings")]
    [InlineData("anime")]
    public async Task StartedSeriesStayBetweenEpisodesPastTheGracePeriod(string scenario)
    {
        using var f = new Fixture(anime: scenario == "anime");
        int user = scenario is "other-account" or "disabled-account" ? 1 : 0;
        var first = f.Data(user, 0);
        first.Played = true;
        if (scenario == "partial-episode") { first.Played = false; first.PlaybackPositionTicks = 50; }
        if (scenario == "only-play-count") { first.Played = false; first.PlayCount = 1; }
        if (scenario == "only-play-date") { first.Played = false; first.LastPlayedDate = DateTime.UtcNow; }
        if (scenario == "disabled-account") f.Users[1].SetPermission(PermissionKind.IsDisabled, true);
        if (scenario == "caught-up-continuing") f.Data(user, 1).Played = true;
        if (scenario == "unknown-status") f.Show.Status = null;
        if (scenario == "ended-but-unfinished") f.SetEndedInventory();
        if (scenario == "all-removal-settings")
        { f.Config.CleanupWatchedMovies = true; f.Config.CleanupCompletedSeries = true; }

        await f.Run(); f.Clock.AdvanceDays(2); await f.Run();
        Assert.False(f.Removed);
        Assert.All(f.Episodes, episode => Assert.True(File.Exists(episode.Path)));
    }

    [Fact]
    public async Task ProtectionUsesSavedJellyfinHistoryAcrossTaskAndActivityRestarts()
    {
        using var f = new Fixture(); f.Data(0, 0).Played = true;
        await f.Run(); f.Clock.AdvanceDays(1); await f.Run(); Assert.False(f.Removed);
        f.Restart(); await f.Run(); f.Clock.AdvanceDays(2); await f.Run();
        Assert.False(f.Removed);
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("failed")]
    [InlineData("partial-import")]
    [InlineData("wrong-identity")]
    [InlineData("future-episode")]
    [InlineData("other-account-unfinished")]
    [InlineData("rewatching")]
    [InlineData("favorite")]
    public async Task FinishedEndedSeriesExpireOnlyWithVerifiedInventoryAndEveryStartedAccountFinished(string scenario)
    {
        using var f = new Fixture();
        foreach (var episode in Enumerable.Range(0, f.Episodes.Length)) f.Data(0, episode).Played = true;
        f.SetEndedInventory(scenario);
        if (scenario == "other-account-unfinished") f.Data(1, 0).Played = true;
        if (scenario == "rewatching") f.Data(0, 1).PlaybackPositionTicks = 50;
        if (scenario == "favorite") f.Data(1, 0).IsFavorite = true;
        if (scenario == "future-episode") f.Episodes[1].PremiereDate = DateTime.UtcNow.AddDays(1);
        await f.Run(); f.Clock.AdvanceDays(2); await f.Run();
        Assert.Equal(scenario == "fresh", f.Removed);
    }

    [Fact]
    public async Task MerelyOpeningSeriesPosterStillAllowsUnsavedCleanup()
    {
        using var f = new Fixture();
        await f.Run(); f.Clock.AdvanceDays(2); await f.Run();
        Assert.True(f.Removed);
    }

    [Fact]
    public async Task ClearingPlaybackHistoryReleasesProtectionAfterAFreshGracePeriod()
    {
        using var f = new Fixture(); f.Data(0, 0).Played = true;
        await f.Run(); f.Clock.AdvanceDays(1); await f.Run(); Assert.False(f.Removed);
        f.Data(0, 0).Played = false;
        await f.Run(); Assert.False(f.Removed);
        f.Clock.AdvanceMinutes(5); await f.Run(); Assert.True(f.Removed);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(1);
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceDays(int days) => _now = _now.AddDays(days);
        public void AdvanceMinutes(int minutes) => _now = _now.AddMinutes(minutes);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mediarow-started-series-" + Guid.NewGuid().ToString("N"));
        private readonly MemoryCache _memory = new(new MemoryCacheOptions());
        private readonly Dictionary<(Guid User, Guid Item), UserItemData> _data = [];
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IUserDataManager _userData;
        private readonly ISessionManager _sessions;
        private readonly DynamicItemCache _cache;
        private EpisodeSyncService? _sync;
        private TemporaryItemActivity _activity;
        public readonly Clock Clock = new();
        public readonly User[] Users = [new("first", "test", "test") { Id = Guid.NewGuid() }, new("second", "test", "test") { Id = Guid.NewGuid() }];
        public readonly PluginConfiguration Config;
        public readonly Series Show;
        public readonly Episode[] Episodes;
        public bool Removed;

        public Fixture(bool anime = false)
        {
            Config = new() { PersistentLibraryPath = _root, CleanupUnsavedItems = true };
            Show = new Series { Id = Guid.NewGuid(), Path = Path.Combine(_root, anime ? "anime" : "tv", "Show"), Status = SeriesStatus.Continuing };
            var season = Path.Combine(Show.Path, "Season 01"); Directory.CreateDirectory(season);
            Episodes = Enumerable.Range(1, 2).Select(number => new Episode
            {
                Id = Guid.NewGuid(), SeriesId = Show.Id, ParentIndexNumber = 1, IndexNumber = number,
                PremiereDate = DateTime.UtcNow.AddYears(-1), Path = Path.Combine(season, $"S01E{number:D2}.strm")
            }).ToArray();
            foreach (var episode in Episodes)
            {
                File.WriteAllText(episode.Path, $"mediarow://tv/tt1234567/1/{episode.IndexNumber}");
                File.SetLastWriteTimeUtc(episode.Path, DateTime.UtcNow.AddHours(-1));
            }
            _activity = new(Clock);
            _cache = new(_memory, NullLogger<DynamicItemCache>.Instance);
            _library = Proxy<ILibraryManager>.Create((method, args) => method.Name switch
            {
                "get_IsScanRunning" => false,
                "GetItemsResult" => new QueryResult<BaseItem>(((InternalItemsQuery)args![0]!).ParentId == Show.Id ? Episodes : Removed ? [] : [Show]),
                "DeleteItem" => Delete(),
                _ => throw new NotSupportedException(method.Name)
            });
            _users = Proxy<IUserManager>.Create((_, _) => Users);
            _userData = Proxy<IUserDataManager>.Create((_, args) => GetData((User)args![0]!, (BaseItem)args[1]!));
            _sessions = Proxy<ISessionManager>.Create((_, _) => Array.Empty<SessionInfo>());
        }

        private object? Delete() { Removed = true; return null; }
        private UserItemData GetData(User user, BaseItem item)
        {
            var key = (user.Id, item.Id);
            if (!_data.TryGetValue(key, out var data)) _data[key] = data = new() { Key = "test" };
            return data;
        }
        public UserItemData Data(int user, int episode) => GetData(Users[user], Episodes[episode]);
        public void Restart() => _activity = new(Clock);
        public Task Run() => new Cleanup(_library, _users, _userData, _sessions, _activity, Config, _sync, _cache)
            .ExecuteAsync(new Progress<double>(), default);

        public void SetEndedInventory(string scenario = "fresh")
        {
            Show.Status = SeriesStatus.Ended;
            if (scenario == "missing") return;
            var folder = Path.Combine(_root, "Jellyfin.Plugin.Mediarow"); Directory.CreateDirectory(folder);
            var known = Episodes.Select(episode => new BaseItemDto
            {
                Id = episode.Id, Type = BaseItemKind.Episode, ParentIndexNumber = 1,
                IndexNumber = scenario == "wrong-identity" ? episode.IndexNumber + 10 : episode.IndexNumber,
                PremiereDate = episode.PremiereDate
            }).ToList();
            if (scenario == "partial-import") known.Add(new() { Id = Guid.NewGuid(), Type = BaseItemKind.Episode, ParentIndexNumber = 1, IndexNumber = 3, PremiereDate = DateTime.UtcNow.AddYears(-1) });
            _cache.StoreEpisodesForSeries(Show.Id, known);
            File.WriteAllText(Path.Combine(folder, "episode-sync-state.json"), JsonSerializer.Serialize(new Dictionary<Guid, EpisodeSyncEntry>
            {
                [Show.Id] = new() { Status = "Ended", KnownEpisodeCount = known.Count,
                    LastCheckedUtc = DateTime.UtcNow.AddDays(scenario == "stale" ? -9 : -1), ConsecutiveFailures = scenario == "failed" ? 1 : 0 }
            }));
            var paths = Proxy<IApplicationPaths>.Create((_, _) => _root);
            _sync = new(null!, null!, null!, null!, null!, null!, paths, NullLogger<EpisodeSyncService>.Instance);
        }

        public void Dispose()
        {
            _memory.Dispose();
            if (!Directory.Exists(_root)) return;
            foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) Directory.Delete(directory);
            Directory.Delete(_root);
        }
    }

    private sealed class Cleanup(ILibraryManager library, IUserManager users, IUserDataManager data,
        ISessionManager sessions, TemporaryItemActivity activity, PluginConfiguration config, EpisodeSyncService? sync, DynamicItemCache cache)
        : UnsavedItemCleanupTask(library, users, data, sessions, activity, NullLogger<UnsavedItemCleanupTask>.Instance, sync, cache)
    { protected override PluginConfiguration Config => config; }
}
