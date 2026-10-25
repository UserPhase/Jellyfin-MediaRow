using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using Jellyfin.Plugin.Mediarow.Models;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class UserPreferencesTests : IDisposable
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mediarow-prefs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private UserPreferenceStore NewStore() => new(_directory, NullLogger<UserPreferenceStore>.Instance);

    private sealed class FakeWatched : IWatchedTitleLookup
    {
        public HashSet<(Guid User, Guid Item)> Played { get; } = new();

        public int Calls { get; private set; }

        public IReadOnlySet<Guid> GetWatchedItemIds(Guid userId, IReadOnlyList<BaseItemDto> items)
        {
            Calls++;
            return items.Where(item => Played.Contains((userId, item.Id))).Select(item => item.Id).ToHashSet();
        }
    }

    private sealed record Section(string Id, string DisplayTitle, int Order, string? AdditionalData = null) : IHomeSection
    {
        public int Limit => 20;
        public string? Route => null;
        public bool Enabled => true;

        public Task<QueryResult<BaseItemDto>> GetResultsAsync(HomeSectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueryResult<BaseItemDto>(new[] { Movie("Row item") }));
    }

    private static BaseItemDto Movie(string name, string? imdb = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = BaseItemKind.Movie,
        ProviderIds = imdb == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["Imdb"] = imdb }
    };

    [Fact]
    public void Store_PersistsPerUser_AndReturnsDetachedCopies()
    {
        var store = NewStore();
        store.Update(UserA, new UserPreferencesUpdate
        {
            SectionOrder = new() { "Mediarow_Drama", "Mediarow_Popular", "Mediarow_Drama", " " },
            HiddenSections = new() { "Mediarow_Horror" },
            HideWatched = true
        });

        UserHomePreferences snapshot = store.Get(UserA);
        snapshot.HiddenSections.Add("Mediarow_Popular");
        snapshot.NotInterested.Add(new NotInterestedEntry { ItemId = Guid.NewGuid() });

        var reloaded = NewStore().Get(UserA);
        Assert.Equal(new[] { "Mediarow_Drama", "Mediarow_Popular" }, reloaded.SectionOrder);
        Assert.Equal(new[] { "Mediarow_Horror" }, reloaded.HiddenSections);
        Assert.True(reloaded.HideWatched);
        Assert.Empty(reloaded.NotInterested);

        var other = store.Get(UserB);
        Assert.False(other.HideWatched);
        Assert.Empty(other.HiddenSections);
        Assert.False(File.Exists(Path.Combine(_directory, UserPreferenceStore.FileName + ".tmp")));
    }

    [Fact]
    public void Store_PartialUpdateKeepsUnspecifiedFields()
    {
        var store = NewStore();
        store.Update(UserA, new UserPreferencesUpdate { HiddenSections = new() { "Mediarow_Horror" }, HideWatched = true });
        store.Update(UserA, new UserPreferencesUpdate { SectionOrder = new() { "Mediarow_Popular" } });

        var preferences = store.Get(UserA);
        Assert.True(preferences.HideWatched);
        Assert.Equal(new[] { "Mediarow_Horror" }, preferences.HiddenSections);
        Assert.Equal(new[] { "Mediarow_Popular" }, preferences.SectionOrder);
    }

    [Fact]
    public void Store_MigratesLegacyEnabledSections_UntilTheUserSavesHiddenRows()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, UserPreferenceStore.LegacyFileName),
            JsonSerializer.Serialize(new Dictionary<Guid, string[]> { [UserA] = new[] { "Mediarow_Popular" } }));

        var store = NewStore();
        Assert.False(store.Get(UserA).IsSectionHidden("Mediarow_Popular"));
        Assert.True(store.Get(UserA).IsSectionHidden("Mediarow_Action"));
        Assert.False(store.Get(UserB).IsSectionHidden("Mediarow_Action"));
        Assert.True(File.Exists(Path.Combine(_directory, UserPreferenceStore.FileName)));

        store.SetEnabledSections(UserA, new[] { "Mediarow_Popular", "Mediarow_Action" }, new[] { "Mediarow_Popular", "Mediarow_Action", "Mediarow_Drama" });
        var preferences = store.Get(UserA);
        Assert.Null(preferences.LegacyEnabledSections);
        Assert.Equal(new[] { "Mediarow_Drama" }, preferences.HiddenSections);
    }

    [Fact]
    public void Store_NotInterestedIsCappedDeduplicatedAndUndoable()
    {
        var store = NewStore();
        Guid first = Guid.NewGuid();
        store.AddNotInterested(UserA, new NotInterestedEntry { ItemId = first, Name = "First" });
        for (int index = 0; index < UserPreferenceStore.MaxNotInterested; index++)
        {
            store.AddNotInterested(UserA, new NotInterestedEntry { ItemId = Guid.NewGuid() });
        }

        var preferences = store.Get(UserA);
        Assert.Equal(UserPreferenceStore.MaxNotInterested, preferences.NotInterested.Count);
        Assert.DoesNotContain(preferences.NotInterested, entry => entry.ItemId == first);

        Guid last = preferences.NotInterested[^1].ItemId;
        store.AddNotInterested(UserA, new NotInterestedEntry { ItemId = last });
        Assert.Single(store.Get(UserA).NotInterested, entry => entry.ItemId == last);

        store.RemoveNotInterested(UserA, last);
        Assert.DoesNotContain(store.Get(UserA).NotInterested, entry => entry.ItemId == last);
        Assert.Throws<ArgumentException>(() => store.AddNotInterested(UserA, new NotInterestedEntry()));
    }

    [Fact]
    public void Arrange_PutsUserOrderFirst_KeepsNewRowsInServerOrder_AndDropsHiddenRows()
    {
        var sections = new[] { "Mediarow_ContinueWatching", "Mediarow_Popular", "Mediarow_Action", "Mediarow_Drama", "Mediarow_Horror" };
        var preferences = new UserHomePreferences
        {
            SectionOrder = new() { "Mediarow_Drama", "Mediarow_Unknown", "Mediarow_Popular" },
            HiddenSections = new() { "mediarow_horror" }
        };

        Assert.Equal(
            new[] { "Mediarow_Drama", "Mediarow_Popular", "Mediarow_ContinueWatching", "Mediarow_Action" },
            UserPreferenceFilter.Arrange(preferences, sections, id => id, includeHidden: false));
        Assert.Equal(
            "Mediarow_Horror",
            UserPreferenceFilter.Arrange(preferences, sections, id => id, includeHidden: true)[^1]);
    }

    [Fact]
    public void NotInterested_HidesTitleForThatUserOnly_ByIdAndByProviderId()
    {
        var store = NewStore();
        var filter = new UserPreferenceFilter(store, new FakeWatched());
        BaseItemDto heat = Movie("Heat", "tt0113277");
        BaseItemDto heatFromOtherCatalog = Movie("Heat (1995)", "tt0113277");
        BaseItemDto ronin = Movie("Ronin", "tt0122690");
        var row = new[] { heat, ronin, heatFromOtherCatalog };
        string before = JsonSerializer.Serialize(row);

        store.AddNotInterested(UserA, UserPreferenceFilter.CreateEntry(heat.Id, heat));

        Assert.Equal(new[] { ronin.Id }, filter.FilterItems(UserA, row).Select(item => item.Id));
        Assert.Equal(row.Select(item => item.Id), filter.FilterItems(UserB, row).Select(item => item.Id));
        Assert.Equal(before, JsonSerializer.Serialize(row));
    }

    [Fact]
    public void HideWatched_RemovesOnlyThatUsersFinishedTitles_AndSkipsLookupWhenOff()
    {
        var store = NewStore();
        var watched = new FakeWatched();
        var filter = new UserPreferenceFilter(store, watched);
        BaseItemDto finished = Movie("Finished");
        BaseItemDto unwatched = Movie("Unwatched");
        watched.Played.Add((UserA, finished.Id));
        var row = new[] { finished, unwatched };

        Assert.Equal(2, filter.FilterItems(UserA, row).Count);
        Assert.Equal(0, watched.Calls);

        store.Update(UserA, new UserPreferencesUpdate { HideWatched = true });
        store.Update(UserB, new UserPreferencesUpdate { HideWatched = true });
        Assert.Equal(new[] { unwatched.Id }, filter.FilterItems(UserA, row).Select(item => item.Id));
        Assert.Equal(2, filter.FilterItems(UserB, row).Count);
    }

    [Fact]
    public async Task HiddenSection_ReturnsNothingForThatUser_ButStillServesOthers()
    {
        var store = NewStore();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var manager = new HomeSectionManager(cache, NullLogger<HomeSectionManager>.Instance, store);
        manager.Register(new Section("Mediarow_Action", "Action", 1, "Action"));
        store.Update(UserA, new UserPreferencesUpdate { HiddenSections = new() { "Mediarow_Action" } });

        var hidden = await manager.ExecuteAsync("Mediarow_Action", new HomeSectionRequest { UserId = UserA });
        var visible = await manager.ExecuteAsync("Mediarow_Action", new HomeSectionRequest { UserId = UserB });

        Assert.Empty(hidden.Items!);
        Assert.Single(visible.Items!);
    }

    [Fact]
    public void AppLibraries_FollowTheUsersRowOrder()
    {
        IHomeSection[] sections =
        {
            new Section("Mediarow_Popular", "Popular", 0),
            new Section("Mediarow_Action", "Action", 1, "Action"),
            new Section("Mediarow_Drama", "Drama", 2, "Drama"),
        };
        var preferences = new UserHomePreferences { SectionOrder = new() { "Mediarow_Drama" }, HiddenSections = new() { "Mediarow_Popular" } };
        var arranged = UserPreferenceFilter.Arrange(preferences, sections, section => section.Id, includeHidden: false);

        var libraries = AppLibraryLayout.Project(arranged, new[] { "Movies", "Series" }, preserveOrder: true);

        Assert.Equal(new[] { "Drama Movies", "Action Series" }, libraries.Select(library => library.Name));
    }
}
