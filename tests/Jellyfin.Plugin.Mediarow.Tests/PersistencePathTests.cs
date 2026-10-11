using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class PersistencePathTests
{
    [Fact]
    public void LegacyConfigurationKeepsExistingDestinations()
    {
        var config = new PluginConfiguration { PersistentLibraryPath = Path.Combine(Path.GetTempPath(), "legacy") };
        Assert.Equal(Path.Combine(config.PersistentLibraryPath, "movies"), config.GetPersistencePath("movies"));
        Assert.Equal(Path.Combine(config.PersistentLibraryPath, "tv"), config.GetPersistencePath("tv"));
        Assert.Equal(Path.Combine(config.PersistentLibraryPath, "anime"), config.GetPersistencePath("anime"));
        Assert.Throws<ArgumentOutOfRangeException>(() => config.GetPersistencePath("invalid"));
    }

    [Fact]
    public void ExplicitPathsAreUsedWithoutAppendingCategoryFolders()
    {
        var config = new PluginConfiguration
        {
            MoviesLibraryPath = Path.Combine(Path.GetTempPath(), "films"),
            SeriesLibraryPath = Path.Combine(Path.GetTempPath(), "shows"),
            AnimeLibraryPath = Path.Combine(Path.GetTempPath(), "animation")
        };
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance);
        var library = AioStreamsPlaybackTests.Proxy<ILibraryManager>.Create((method, _) =>
            throw new NotSupportedException(method.Name));
        var persistence = new TestPersistence(library, cache, config);
        var movie = new BaseItemDto { Name = "Film", Type = BaseItemKind.Movie, ProductionYear = 2020 };
        var series = new BaseItemDto { Name = "Show", Type = BaseItemKind.Series, ProductionYear = 2020 };
        Assert.Equal(config.MoviesLibraryPath, Path.GetDirectoryName(Path.GetDirectoryName(persistence.GetMovieFilePath(movie))));
        Assert.Equal(config.SeriesLibraryPath, Path.GetDirectoryName(persistence.GetSeriesFolderPath(series)));
        series.Genres = ["Anime"];
        Assert.Equal(config.AnimeLibraryPath, Path.GetDirectoryName(persistence.GetSeriesFolderPath(series)));
    }

    private sealed class TestPersistence(ILibraryManager library, DynamicItemCache cache, PluginConfiguration config)
        : PersistenceService(library, cache, NullLogger<PersistenceService>.Instance)
    {
        protected override PluginConfiguration Config => config;
    }
}
