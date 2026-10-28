using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Mediarow.Tests.AioStreamsPlaybackTests;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class WatchedSeriesVisibilityTests
{
    [Theory]
    [InlineData("finished", true)]
    [InlineData("continuing", false)]
    [InlineData("unknown-status", false)]
    [InlineData("unwatched-episode", false)]
    [InlineData("missing-episode", false)]
    [InlineData("future-episode", false)]
    [InlineData("unknown-airdate", false)]
    [InlineData("unknown-number", false)]
    [InlineData("empty", false)]
    [InlineData("stale", false)]
    [InlineData("failed", false)]
    [InlineData("no-inventory", false)]
    [InlineData("upcoming", false)]
    [InlineData("duplicate-version", true)]
    [InlineData("unwatched-special", true)]
    public void OnlyVerifiedCompletedEndedSeriesAreHidden(string scenario, bool hidden)
    {
        using var fixture = new Fixture();
        var show = fixture.AddSeries();
        switch (scenario)
        {
            case "continuing": show.Status = SeriesStatus.Continuing; break;
            case "unknown-status": show.Status = null; break;
            case "unwatched-episode": fixture.Unwatched.Add(fixture.Episodes[1].Id); break;
            case "missing-episode": fixture.Episodes.RemoveAt(1); break;
            case "future-episode": fixture.Episodes[1].PremiereDate = DateTime.UtcNow.AddDays(1); break;
            case "unknown-airdate": fixture.Episodes[1].PremiereDate = null; break;
            case "unknown-number": fixture.Episodes[1].IndexNumber = null; break;
            case "empty": fixture.Episodes.Clear(); break;
            case "stale": fixture.Inventory[show.Id].LastCheckedUtc = DateTime.UtcNow.AddDays(-9); break;
            case "failed": fixture.Inventory[show.Id].ConsecutiveFailures = 1; break;
            case "no-inventory": fixture.Inventory.Clear(); break;
            case "upcoming": fixture.Inventory[show.Id].NextAirDateUtc = DateTime.UtcNow.AddDays(1); break;
            case "duplicate-version": fixture.AddEpisode(show, 2, watched: false); break;
            case "unwatched-special": fixture.AddEpisode(show, 1, watched: false, season: 0); break;
        }
        var lookup = fixture.CreateLookup();
        var item = fixture.ToDto(show);
        Assert.Equal(hidden, lookup.GetWatchedItemIds(fixture.A.Id, [item]).Contains(item.Id));
        // A's completion cannot hide a series for B.
        Assert.Empty(lookup.GetWatchedItemIds(fixture.B.Id, [item]));
    }

    [Fact]
    public void SeriesLookupBatchesEpisodeAndUserDataQueriesForTheWholeRow()
    {
        using var fixture = new Fixture();
        for (int index = 0; index < 100; index++) fixture.AddSeries();
        var lookup = fixture.CreateLookup();
        Assert.Equal(100, lookup.GetWatchedItemIds(fixture.A.Id, fixture.Shows.Select(fixture.ToDto).ToArray()).Count);
        Assert.Equal(2, fixture.LibraryQueries); // title IDs + all episodes, no per-poster queries
        Assert.Equal(2, fixture.DataQueries);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mediarow-series-visibility-" + Guid.NewGuid().ToString("N"));
        public readonly User A = new("A", "test", "test") { Id = Guid.NewGuid() };
        public readonly User B = new("B", "test", "test") { Id = Guid.NewGuid() };
        public readonly List<Series> Shows = [];
        public readonly List<Episode> Episodes = [];
        public readonly HashSet<Guid> Unwatched = [];
        public readonly Dictionary<Guid, EpisodeSyncEntry> Inventory = [];
        public int LibraryQueries;
        public int DataQueries;

        public Series AddSeries()
        {
            var show = new Series { Id = Guid.NewGuid(), Status = SeriesStatus.Ended };
            Shows.Add(show);
            Inventory[show.Id] = new() { Status = "Ended", KnownEpisodeCount = 2, LastCheckedUtc = DateTime.UtcNow.AddMinutes(-1) };
            AddEpisode(show, 1);
            AddEpisode(show, 2);
            return show;
        }

        public void AddEpisode(Series show, int number, bool watched = true, int season = 1)
        {
            var episode = new Episode
            {
                Id = Guid.NewGuid(), SeriesId = show.Id, ParentIndexNumber = season,
                IndexNumber = number, PremiereDate = DateTime.UtcNow.AddYears(-1)
            };
            Episodes.Add(episode);
            if (!watched) Unwatched.Add(episode.Id);
        }

        public BaseItemDto ToDto(Series show) => new() { Id = show.Id, Type = BaseItemKind.Series };

        public LibraryWatchedTitleLookup CreateLookup()
        {
            string folder = Path.Combine(_root, "Jellyfin.Plugin.Mediarow");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "episode-sync-state.json"), JsonSerializer.Serialize(Inventory));
            var paths = Proxy<IApplicationPaths>.Create((_, _) => _root);
            var sync = new EpisodeSyncService(null!, null!, null!, null!, null!, null!, paths, NullLogger<EpisodeSyncService>.Instance);
            var library = Proxy<ILibraryManager>.Create((method, args) =>
            {
                Assert.Equal("GetItemList", method.Name);
                LibraryQueries++;
                var query = Assert.IsType<InternalItemsQuery>(args![0]);
                if (query.IncludeItemTypes.Contains(BaseItemKind.Episode))
                    return Episodes.Where(episode => query.AncestorIds.Contains(episode.SeriesId)).Cast<BaseItem>().ToList();
                return Shows.Where(show => query.ItemIds.Contains(show.Id)).Cast<BaseItem>().ToList();
            });
            var data = Proxy<IUserDataManager>.Create((method, args) =>
            {
                Assert.Equal("GetUserDataBatch", method.Name);
                DataQueries++;
                var items = Assert.IsAssignableFrom<IReadOnlyList<BaseItem>>(args![0]);
                var user = Assert.IsType<User>(args[1]);
                return items.ToDictionary(item => item.Id, item => new UserItemData
                {
                    Key = "test", Played = user.Id == A.Id && !Unwatched.Contains(item.Id)
                });
            });
            var users = Proxy<IUserManager>.Create((_, args) => (Guid)args![0]! == A.Id ? A : B);
            return new(library, users, data, NullLogger<LibraryWatchedTitleLookup>.Instance, episodeSync: sync);
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
