using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.Filters;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class AppLibraryTests
{
    private sealed record Section(string Id, string DisplayTitle, int Order, string? AdditionalData = null) : IHomeSection
    {
        public int Limit => 20;
        public string? Route => null;
        public bool Enabled => true;

        public Task<QueryResult<BaseItemDto>> GetResultsAsync(HomeSectionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>()));
        }
    }

    private static readonly IHomeSection[] Sections =
    {
        new Section("Mediarow_ContinueWatching", "Continue Watching", -1),
        new Section("Mediarow_Popular", "Popular", 0),
        new Section("Mediarow_Action", "Action", 1, "Action"),
        new Section("Mediarow_Comedy", "Comedy", 2, "Comedy"),
        new Section("Mediarow_Drama", "Drama", 3, "Drama"),
        new Section("Mediarow_Horror", "Horror", 4, "Horror"),
        new Section("Mediarow_Anime", "Anime", 5, "Anime"),
        new Section("Mediarow_Crime", "Crime", 6, "Crime"),
        new Section("Mediarow_Romance", "Romance", 7, "Romance"),
        new Section("Mediarow_Fantasy", "Fantasy", 8, "Fantasy"),
    };

    [Fact]
    public void Layout_MirrorsWebHomeRows()
    {
        var libraries = AppLibraryLayout.Project(Sections, new[] { "Movies", "Series" });

        Assert.Equal(
            new[] { "Popular Movies", "Popular Series", "Action Movies", "Comedy Series", "Drama Movies", "Horror Series", "Crime Movies", "Romance Series" },
            libraries.Select(library => library.Name));
        Assert.DoesNotContain(libraries, library => library.SectionId == "Mediarow_ContinueWatching");
        Assert.Equal(CollectionType.movies, libraries[0].CollectionType);
        Assert.Equal(CollectionType.tvshows, libraries[1].CollectionType);
        Assert.Equal(Enumerable.Range(0, libraries.Count), libraries.Select(library => library.Order));
        Assert.Equal(libraries.Count, libraries.Select(library => library.Id).Distinct().Count());
    }

    [Fact]
    public void Layout_ShowsAnimeOnlyWhenEnabled_AndHandlesEmptyCategories()
    {
        var libraries = AppLibraryLayout.Project(Sections, new[] { "Movies", "Anime" });
        var anime = Assert.Single(libraries, library => library.SectionId == "Mediarow_Anime");
        Assert.Equal("Anime", anime.Name);
        Assert.Equal(CollectionType.tvshows, anime.CollectionType);
        Assert.Contains(libraries, library => library.Name == "Popular Anime");
        Assert.Empty(AppLibraryLayout.Project(Sections, Array.Empty<string>()));
    }

    [Fact]
    public void LibraryIds_AreStableAcrossRestartsAndCase()
    {
        Assert.Equal(AppLibraryLayout.CreateId("Mediarow_Popular", "Movies"), AppLibraryLayout.CreateId("mediarow_popular", "movies"));
        Assert.NotEqual(AppLibraryLayout.CreateId("Mediarow_Popular", "Movies"), AppLibraryLayout.CreateId("Mediarow_Popular", "Series"));
    }

    private static readonly BaseItemDto[] Items =
    {
        new() { Id = Guid.NewGuid(), Name = "Zulu", Type = BaseItemKind.Movie, ProductionYear = 2001, Genres = new[] { "Action" } },
        new() { Id = Guid.NewGuid(), Name = "Alpha", Type = BaseItemKind.Series, ProductionYear = 2020, Genres = new[] { "Drama" } },
        new() { Id = Guid.NewGuid(), Name = "Mike", Type = BaseItemKind.Movie, ProductionYear = 2010, Genres = new[] { "Drama" } },
    };

    [Fact]
    public void QueryItems_FiltersSortsAndPages()
    {
        var result = AppLibraryFilter.QueryItems(Items, new Dictionary<string, object?>
        {
            ["includeItemTypes"] = new[] { BaseItemKind.Movie },
            ["sortBy"] = new[] { ItemSortBy.SortName },
            ["sortOrder"] = new[] { SortOrder.Ascending },
            ["startIndex"] = (int?)1,
            ["limit"] = (int?)5
        });

        Assert.Equal(2, result.TotalRecordCount);
        Assert.Equal("Zulu", Assert.Single(result.Items).Name);

        var drama = AppLibraryFilter.QueryItems(Items, new Dictionary<string, object?>
        {
            ["genres"] = new[] { "drama" },
            ["sortBy"] = new[] { ItemSortBy.ProductionYear },
            ["sortOrder"] = new[] { SortOrder.Descending }
        });
        Assert.Equal(new[] { "Alpha", "Mike" }, drama.Items.Select(item => item.Name));
    }

    [Fact]
    public void QueryItems_ReturnsNothingForPersonalFilters()
    {
        Assert.Equal(0, AppLibraryFilter.QueryItems(Items, new Dictionary<string, object?> { ["isFavorite"] = (bool?)true }).TotalRecordCount);
        Assert.Empty(AppLibraryFilter.QueryItems(Items, new Dictionary<string, object?> { ["filters"] = new[] { ItemFilter.IsResumable } }).Items);
    }

    [Fact]
    public void Latest_KeepsCatalogOrderAndMapsEpisodeRequestsToSeries()
    {
        var latest = AppLibraryFilter.GetLatest(Items, new Dictionary<string, object?>
        {
            ["includeItemTypes"] = new[] { BaseItemKind.Episode },
            ["limit"] = 16
        });
        Assert.Equal("Alpha", Assert.Single(latest).Name);
        Assert.Equal(2, AppLibraryFilter.GetLatest(Items, new Dictionary<string, object?> { ["limit"] = 2 }).Length);
        Assert.Empty(AppLibraryFilter.GetLatest(Items, new Dictionary<string, object?> { ["isPlayed"] = (bool?)true }));
    }
}
