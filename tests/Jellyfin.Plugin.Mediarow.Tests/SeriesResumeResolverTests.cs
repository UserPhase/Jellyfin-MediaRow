using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class SeriesResumeResolverTests
{
    private static readonly User TestUser = new("tester", "Default", "Default");

    [Fact]
    public void ReturnsInProgressEpisodeWhenNothingWasFinished()
    {
        var episode5 = new Episode { Id = Guid.NewGuid(), Name = "E5" };
        var resolver = CreateResolver(episode5, null, new Dictionary<BaseItem, DateTime>());

        Assert.Same(episode5, resolver.FindResumeEpisode(TestUser, new Series { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void ReturnsInProgressEpisodeWhenItWasWatchedAfterTheLastFinishedEpisode()
    {
        var finished = new Episode { Id = Guid.NewGuid(), Name = "E1" };
        var inProgress = new Episode { Id = Guid.NewGuid(), Name = "E5" };
        var resolver = CreateResolver(inProgress, finished, new Dictionary<BaseItem, DateTime>
        {
            [finished] = new(2026, 10, 1),
            [inProgress] = new(2026, 10, 2)
        });

        Assert.Same(inProgress, resolver.FindResumeEpisode(TestUser, new Series { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void KeepsJellyfinNextUpWhenAnEpisodeWasFinishedMoreRecently()
    {
        var finished = new Episode { Id = Guid.NewGuid(), Name = "E6" };
        var stale = new Episode { Id = Guid.NewGuid(), Name = "E2" };
        var resolver = CreateResolver(stale, finished, new Dictionary<BaseItem, DateTime>
        {
            [finished] = new(2026, 10, 3),
            [stale] = new(2026, 10, 2)
        });

        Assert.Null(resolver.FindResumeEpisode(TestUser, new Series { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void ReturnsNullWhenNoEpisodeIsInProgress()
    {
        var resolver = CreateResolver(null, new Episode { Id = Guid.NewGuid() }, new Dictionary<BaseItem, DateTime>());

        Assert.Null(resolver.FindResumeEpisode(TestUser, new Series { Id = Guid.NewGuid() }));
    }

    private static SeriesResumeResolver CreateResolver(BaseItem? resumable, BaseItem? played, Dictionary<BaseItem, DateTime> playedDates)
    {
        var library = AioStreamsPlaybackTests.Proxy<ILibraryManager>.Create((method, args) =>
        {
            Assert.Equal(nameof(ILibraryManager.GetItemList), method.Name);
            var query = (InternalItemsQuery)args![0]!;
            Assert.Equal(1, query.Limit);
            var item = query.IsResumable == true ? resumable : played;
            return item is null ? new List<BaseItem>() : new List<BaseItem> { item };
        });
        var userData = AioStreamsPlaybackTests.Proxy<IUserDataManager>.Create((method, args) =>
        {
            Assert.Equal(nameof(IUserDataManager.GetUserData), method.Name);
            var item = (BaseItem)args![1]!;
            return new UserItemData
            {
                Key = item.Id.ToString("N"),
                LastPlayedDate = playedDates.TryGetValue(item, out var date) ? date : null
            };
        });

        return new SeriesResumeResolver(library, userData);
    }
}
