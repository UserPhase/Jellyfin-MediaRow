using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class CatalogDuplicateFilterTests
{
    [Fact]
    public void ExcludeKnown_MatchesImdbAcrossTypesButTmdbAndTvdbOnlyWithinType()
    {
        var results = new[]
        {
            Dto("Shared IMDb", BaseItemKind.Series, ("Imdb", "tt1")),
            Dto("Movie TMDB", BaseItemKind.Movie, ("Tmdb", "10")),
            Dto("Series same TMDB", BaseItemKind.Series, ("Tmdb", "10")),
            Dto("Series TVDB", BaseItemKind.Series, ("Tvdb", "20"))
        };
        var known = new[]
        {
            Dto("Library movie", BaseItemKind.Movie, ("Imdb", "TT1"), ("Tmdb", "10")),
            Dto("Library series", BaseItemKind.Series, ("Tvdb", "20"))
        };

        var remaining = CatalogDuplicateFilter.ExcludeKnown(results, known);

        Assert.Equal("Series same TMDB", Assert.Single(remaining).Name);
    }

    [Fact]
    public void ExcludeLibraryItems_UsesOneBatchedProviderQuery()
    {
        InternalItemsQuery? captured = null;
        var calls = 0;
        var library = AioStreamsPlaybackTests.Proxy<ILibraryManager>.Create((method, args) =>
        {
            Assert.Equal(nameof(ILibraryManager.GetItemList), method.Name);
            calls++;
            captured = (InternalItemsQuery)args![0]!;
            return new List<BaseItem>
            {
                new Movie { Name = "Owned", ProviderIds = new() { ["Imdb"] = "tt1" } },
                new Series { Name = "Owned series", ProviderIds = new() { ["Tmdb"] = "30" } }
            };
        });
        var results = new[]
        {
            Dto("Owned", BaseItemKind.Movie, ("Imdb", "tt1")),
            Dto("Movie with series TMDB", BaseItemKind.Movie, ("Tmdb", "30")),
            Dto("New", BaseItemKind.Series, ("Tvdb", "40"))
        };

        var remaining = CatalogDuplicateFilter.ExcludeLibraryItems(library, results, NullLogger.Instance);

        Assert.Equal(["Movie with series TMDB", "New"], remaining.Select(item => item.Name));
        Assert.Equal(1, calls);
        Assert.NotNull(captured);
        Assert.Equal([BaseItemKind.Movie, BaseItemKind.Series], captured.IncludeItemTypes);
        Assert.Equal(["tt1"], captured.HasAnyProviderIds!["Imdb"]);
        Assert.Equal(["30"], captured.HasAnyProviderIds["Tmdb"]);
        Assert.Equal(["40"], captured.HasAnyProviderIds["Tvdb"]);
    }

    private static BaseItemDto Dto(string name, BaseItemKind type, params (string Provider, string Value)[] ids)
    {
        return new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = type,
            ProviderIds = ids.ToDictionary(id => id.Provider, id => id.Value)
        };
    }
}
