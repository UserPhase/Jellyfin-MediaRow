using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using System.Xml.Serialization;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class HomeRowContentFilterTests
{
    [Fact]
    public void CategoryScope_SeparatesContentAndDoesNotEnableDisabledCategories()
    {
        var config = new PluginConfiguration { HomeRowContentCategories = new[] { "Movies", "Series", "Anime" } };
        Assert.Equal(new[] { "Movies" }, HomeRowContentFilter.GetCategories(config, "Movies"));
        Assert.Equal(new[] { BaseItemKind.Movie }, HomeRowContentFilter.GetContentTypes(config, "Movies"));
        Assert.Equal(new[] { BaseItemKind.Series }, HomeRowContentFilter.GetContentTypes(config, "Series"));
        Assert.True(HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Movie }, config, "Movies"));
        Assert.False(HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Series }, config, "Movies"));
        Assert.False(HomeRowContentFilter.Matches(Anime(BaseItemKind.Movie), config, "Movies"));
        Assert.True(HomeRowContentFilter.Matches(Anime(BaseItemKind.Movie), config, "Anime"));
        config.HomeRowContentCategories = new[] { "Series" };
        Assert.Empty(HomeRowContentFilter.GetContentTypes(config, "Movies"));
        Assert.False(HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Movie }, config, "Movies"));
        Assert.Empty(HomeRowContentFilter.GetCategories(config, "Unknown"));
    }

    [Fact]
    public void Defaults_EnableMoviesAndSeriesButNotAnime()
    {
        var config = new PluginConfiguration();
        Assert.Equal(new[] { "Movies", "Series" }, HomeRowContentFilter.GetCategories(config));
        Assert.True(HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Movie }, config));
        Assert.True(HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Series }, config));
        Assert.False(HomeRowContentFilter.Matches(Anime(BaseItemKind.Movie), config));
        Assert.False(HomeRowContentFilter.Matches(Anime(BaseItemKind.Series), config));
    }

    [Theory]
    [InlineData(HomeRowContentType.Movies, BaseItemKind.Movie, BaseItemKind.Series)]
    [InlineData(HomeRowContentType.Series, BaseItemKind.Series, BaseItemKind.Movie)]
    public void LegacyTypeSelection_IsPreserved(HomeRowContentType legacy, BaseItemKind included, BaseItemKind excluded)
    {
        var config = new PluginConfiguration { HomeRowContentType = legacy };
        Assert.True(HomeRowContentFilter.Matches(new BaseItemDto { Type = included }, config));
        Assert.False(HomeRowContentFilter.Matches(new BaseItemDto { Type = excluded }, config));
    }

    [Fact]
    public void AnimeOnly_FetchesBothTypesButExcludesNonAnime()
    {
        var config = new PluginConfiguration { HomeRowContentCategories = new[] { "Anime" } };
        Assert.Contains(BaseItemKind.Movie, HomeRowContentFilter.GetContentTypes(config));
        Assert.Contains(BaseItemKind.Series, HomeRowContentFilter.GetContentTypes(config));
        Assert.True(HomeRowContentFilter.Matches(Anime(BaseItemKind.Movie), config));
        Assert.True(HomeRowContentFilter.Matches(Anime(BaseItemKind.Series), config));
        Assert.False(HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Movie }, config));
        Assert.False(HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Series }, config));
    }

    [Fact]
    public void AnimationWithoutJapaneseOrigin_RemainsOrdinaryContent()
    {
        var item = new BaseItemDto { Type = BaseItemKind.Movie, Genres = new[] { "Animation" } };
        Assert.True(HomeRowContentFilter.Matches(item, new PluginConfiguration()));
        item.ProductionLocations = new[] { "Japan" };
        Assert.False(HomeRowContentFilter.Matches(item, new PluginConfiguration()));
        Assert.True(HomeRowContentFilter.Matches(item,
            new PluginConfiguration { HomeRowContentCategories = new[] { "Anime" } }));
    }

    [Fact]
    public void NoSelection_ReturnsNoTypesOrMatchingContent()
    {
        var config = new PluginConfiguration { HomeRowContentCategories = Array.Empty<string>() };
        Assert.Empty(HomeRowContentFilter.GetContentTypes(config));
        Assert.False(HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Movie }, config));
        Assert.False(HomeRowContentFilter.Matches(Anime(BaseItemKind.Series), config));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Selections_AreIndependentAndSurviveXmlConfiguration(bool movies, bool series, bool anime)
    {
        var categories = new List<string>();
        if (movies) categories.Add("Movies");
        if (series) categories.Add("Series");
        if (anime) categories.Add("Anime");
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { HomeRowContentCategories = categories.ToArray() });
        using var reader = new StringReader(writer.ToString());
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.Equal(categories, HomeRowContentFilter.GetCategories(config));
        Assert.Equal(movies, HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Movie }, config));
        Assert.Equal(series, HomeRowContentFilter.Matches(new BaseItemDto { Type = BaseItemKind.Series }, config));
        Assert.Equal(anime, HomeRowContentFilter.Matches(Anime(BaseItemKind.Movie), config));
        Assert.Equal(anime, HomeRowContentFilter.Matches(Anime(BaseItemKind.Series), config));
    }

    private static BaseItemDto Anime(BaseItemKind type) => new() { Type = type, Genres = new[] { "Anime" } };

    [Fact]
    public void DefaultSelection_SurvivesXmlConfigurationRoundTrip()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration());
        using var reader = new StringReader(writer.ToString());
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.Equal(new[] { "Movies", "Series" }, HomeRowContentFilter.GetCategories(config));
    }

    [Fact]
    public void LegacyXmlWithoutCategories_PreservesContentTypes()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("<PluginConfiguration><HomeRowContentType>Movies</HomeRowContentType></PluginConfiguration>");
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.Equal(new[] { "Movies" }, HomeRowContentFilter.GetCategories(config));
    }
}
