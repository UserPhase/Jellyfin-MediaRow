using Jellyfin.Plugin.Mediarow.HomeScreen;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class HomeSectionManagerTests
{
    [Fact]
    public void GetSections_ReturnsEnabledSectionsInStableOrder()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        HomeSectionManager manager = new(cache, NullLogger<HomeSectionManager>.Instance);
        manager.Register(new TestSection("z", 2, 1, Array.Empty<BaseItemDto>()));
        manager.Register(new TestSection("a", 1, 1, Array.Empty<BaseItemDto>()));
        manager.Register(new TestSection("disabled", 0, 1, Array.Empty<BaseItemDto>(), false));

        Assert.Equal(new[] { "a", "z" }, manager.GetSections().Select(section => section.Id));
    }

    [Fact]
    public async Task ExecuteAsync_DeduplicatesAndAppliesLimit()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        HomeSectionManager manager = new(cache, NullLogger<HomeSectionManager>.Instance);
        Guid duplicateId = Guid.NewGuid();
        manager.Register(new TestSection("test", 0, 2, new[]
        {
            new BaseItemDto { Id = duplicateId },
            new BaseItemDto { Id = duplicateId },
            new BaseItemDto { Id = Guid.NewGuid() }
        }));

        QueryResult<BaseItemDto> result = await manager.ExecuteAsync("test", new HomeSectionRequest { UserId = Guid.NewGuid() });

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Items.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public async Task ReplaceSections_RemovesOldRowsAndInvalidatesCachedResults()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        HomeSectionManager manager = new(cache, NullLogger<HomeSectionManager>.Instance);
        var request = new HomeSectionRequest { UserId = Guid.NewGuid() };
        var original = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        manager.ReplaceSections(new[] { new TestSection("test", 0, 1, new[] { new BaseItemDto { Id = original } }) });
        Assert.Equal(original, (await manager.ExecuteAsync("test", request)).Items.Single().Id);
        manager.ReplaceSections(new[]
        {
            new TestSection("test", 1, 1, new[] { new BaseItemDto { Id = replacement } }),
            new TestSection("mystery", 0, 1, Array.Empty<BaseItemDto>())
        });
        Assert.Equal(replacement, (await manager.ExecuteAsync("test", request)).Items.Single().Id);
        manager.ReplaceSections(new[] { new TestSection("mystery", 0, 1, Array.Empty<BaseItemDto>()) });
        Assert.Equal(new[] { "mystery" }, manager.GetSections().Select(section => section.Id));
        Assert.Empty((await manager.ExecuteAsync("test", request)).Items);
    }

    [Fact]
    public async Task ExecuteAsync_CacheSeparatesCategoryViews()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        HomeSectionManager manager = new(cache, NullLogger<HomeSectionManager>.Instance);
        var section = new TestSection("test", 0, 1, new[] { new BaseItemDto { Id = Guid.NewGuid() } });
        manager.Register(section);
        var userId = Guid.NewGuid();
        var movies = new HomeSectionRequest { UserId = userId, Parameters = new Dictionary<string, string> { ["contentCategory"] = "Movies" } };
        var series = new HomeSectionRequest { UserId = userId, Parameters = new Dictionary<string, string> { ["contentCategory"] = "Series" } };
        await manager.ExecuteAsync("test", movies);
        await manager.ExecuteAsync("test", series);
        await manager.ExecuteAsync("test", movies);
        Assert.Equal(2, section.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEmptyWhenProviderFails()
    {
        using MemoryCache cache = new(new MemoryCacheOptions());
        HomeSectionManager manager = new(cache, NullLogger<HomeSectionManager>.Instance);
        manager.Register(new TestSection("failure", 0, 10, Array.Empty<BaseItemDto>(), throws: true));

        QueryResult<BaseItemDto> result = await manager.ExecuteAsync("failure", new HomeSectionRequest { UserId = Guid.NewGuid() });

        Assert.Empty(result.Items);
    }

    private sealed class TestSection : IHomeSection
    {
        private readonly IReadOnlyList<BaseItemDto> _items;
        private readonly bool _throws;

        public TestSection(string id, int order, int limit, IReadOnlyList<BaseItemDto> items, bool enabled = true, bool throws = false)
        {
            Id = id;
            Order = order;
            Limit = limit;
            _items = items;
            Enabled = enabled;
            _throws = throws;
        }

        public string Id { get; }
        public string DisplayTitle => Id;
        public int Order { get; }
        public int Limit { get; }
        public string? Route => null;
        public string? AdditionalData => null;
        public bool Enabled { get; }
        public int Calls { get; private set; }

        public Task<QueryResult<BaseItemDto>> GetResultsAsync(HomeSectionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_throws)
            {
                throw new InvalidOperationException("test provider failure");
            }

            return Task.FromResult(new QueryResult<BaseItemDto>(_items));
        }
    }
}