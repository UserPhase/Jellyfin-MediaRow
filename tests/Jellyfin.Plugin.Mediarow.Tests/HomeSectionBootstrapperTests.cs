using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.HomeScreen;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class HomeSectionBootstrapperTests
{
    [Fact]
    public void SavedChanges_RegisterMysteryAndRemoveDisabledAndDeletedRows()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var manager = new HomeSectionManager(cache, NullLogger<HomeSectionManager>.Instance);
        var bootstrapper = new HomeSectionBootstrapper(manager, null!, null!, null!,
            NullLogger<HomeSectionBootstrapper>.Instance);
        var config = new PluginConfiguration
        {
            HomeSections = new()
            {
                new() { Id = "Mediarow_Popular", Order = 0 },
                new() { Id = "Mediarow_Mystery", DisplayTitle = "Mystery", AdditionalData = "Mystery", Order = 1, Enabled = false },
                new() { Id = "Mediarow_ContinueWatching", Enabled = false }
            }
        };
        bootstrapper.RegisterConfiguredSections(config);
        Assert.Equal(new[] { "Mediarow_Popular" }, manager.GetSections().Select(section => section.Id));
        config.HomeSections[1].Enabled = true;
        config.HomeSections[1].Order = -1;
        bootstrapper.RegisterConfiguredSections(config);
        Assert.Equal(new[] { "Mediarow_Mystery", "Mediarow_Popular" },
            manager.GetSections().Select(section => section.Id));
        config.HomeSections.RemoveAt(0);
        bootstrapper.RegisterConfiguredSections(config);
        Assert.Equal(new[] { "Mediarow_Mystery" }, manager.GetSections().Select(section => section.Id));
        config.EnableHomeRows = false;
        bootstrapper.RegisterConfiguredSections(config);
        Assert.Empty(manager.GetSections());
    }
}
