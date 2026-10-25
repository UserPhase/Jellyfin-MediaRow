using System.Reflection;
using System.Security.Claims;
using System.Collections.Immutable;
using MediaBrowser.Model.Dto;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Mediarow.Api;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.ScheduledTasks;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Mediarow.Tests.AioStreamsPlaybackTests;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class ReleaseSafetyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(10000)]
    public void RemovedWatchedMoviesStayHiddenPerUserWithoutPerPosterLibraryQueries(int count)
    {
        var a = new User("a", "Default", "Default") { Id = Guid.NewGuid() };
        var b = new User("b", "Default", "Default") { Id = Guid.NewGuid() };
        var items = Enumerable.Range(0, count).Select(index => new BaseItemDto
        {
            Id = Guid.NewGuid(), Type = BaseItemKind.Movie, ProviderIds = new() { ["Imdb"] = "tt" + (1000000 + index) }
        }).ToArray();
        var library = Proxy<ILibraryManager>.Create((_, _) => throw new InvalidOperationException("No native entries remain; this lookup should use history."));
        var users = Proxy<IUserManager>.Create((_, args) => (Guid)args![0]! == a.Id ? a : b);
        var keys = items.Select(item => "imdb:" + item.ProviderIds["Imdb"]).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var lookup = new LibraryWatchedTitleLookup(library, users, null!, NullLogger<LibraryWatchedTitleLookup>.Instance, new RetainedHistory(a.Id, keys));
        Assert.Equal(count, lookup.GetWatchedItemIds(a.Id, items).Count);
        Assert.Empty(lookup.GetWatchedItemIds(b.Id, items));
    }

    [Fact]
    public void AllStreamRoutesRequireAuthentication() =>
        Assert.NotNull(typeof(StreamController).GetCustomAttribute<AuthorizeAttribute>());

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void StreamRedirectsRespectUserScopeAndNeverLogPrivateUrls(bool authenticated, bool anotherUser)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var user = new User("fixture", "Default", "Default") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableAllFolders, true);
        var movie = new VisibleMovie { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt1234567" } };
        var library = Proxy<ILibraryManager>.Create((method, _) => method.Name == "GetItemsResult"
            ? new QueryResult<BaseItem>([movie]) : null);
        var users = Proxy<IUserManager>.Create((method, args) => method.Name == "GetUserById" && (Guid)args![0]! == user.Id ? user : null);
        var cache = new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance);
        var guard = new UserPermissionGuard(users, library, null!, cache, memory);
        var logger = new CapturingLogger();
        var controller = new TestStreams(library, logger, guard, users)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (authenticated) controller.HttpContext.User = Principal(user.Id);
        if (anotherUser) controller.Request.QueryString = new QueryString("?userId=" + Guid.NewGuid());

        var result = controller.GetMovieStream("tt1234567");
        if (!authenticated) Assert.IsType<UnauthorizedResult>(result);
        else if (anotherUser) Assert.IsType<ForbidResult>(result);
        else Assert.Contains("private-test-token", Assert.IsType<RedirectResult>(result).Url);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("private-test-token", StringComparison.Ordinal));
    }

    [Fact]
    public void PolicyChangesApplyOnTheVeryNextRequest()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var user = new User("fixture", "Default", "Default") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableAllFolders, true);
        var users = Proxy<IUserManager>.Create((_, _) => user);
        var guard = new UserPermissionGuard(users, null!, null!, new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance), memory);
        Assert.Null(guard.Resolve(Principal(user.Id)).Context!.Rules.MaxRatingScore);
        user.MaxParentalRatingScore = 13;
        Assert.Equal(13, guard.Resolve(Principal(user.Id)).Context!.Rules.MaxRatingScore);
    }

    [Fact]
    public async Task CacheCleanupKeepsActiveCapabilitiesAndOtherPluginsEntries()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        for (int index = 0; index < 12; index++) memory.Set("mediarow-relay:" + index, index, TimeSpan.FromHours(1));
        memory.Set("another-plugin:active", "retain", TimeSpan.FromHours(1));
        await new CacheCleanupTask(memory, NullLogger<CacheCleanupTask>.Instance).ExecuteAsync(new Progress<double>(), default);
        for (int index = 0; index < 12; index++) Assert.Equal(index, memory.Get<int>("mediarow-relay:" + index));
        Assert.Equal("retain", memory.Get<string>("another-plugin:active"));
    }

    [Theory]
    [InlineData("en_1")]
    [InlineData("eng_1")]
    public async Task IndexedSubtitleFilesRecoverAfterMemoryCacheExpires(string language)
    {
        var root = Path.Combine(Path.GetTempPath(), "mediarow-subtitle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var memory = new MemoryCache(new MemoryCacheOptions());
            var itemId = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Combine(root, $"{itemId}_en_1.vtt"), "WEBVTT\n\nTrack two");
            var service = new TestSubtitles(new DynamicItemCache(memory, NullLogger<DynamicItemCache>.Instance), root);
            Assert.Contains("Track two", await service.GetSubtitleContentAsync(itemId, language));
            Assert.Null(await service.GetSubtitleContentAsync(itemId, "../../private"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ClaimsPrincipal Principal(Guid id) => new(new ClaimsIdentity([new Claim(UserPermissionGuard.UserIdClaim, id.ToString())], "test"));
    private sealed class VisibleMovie : Movie { public override bool IsVisibleStandalone(User user) => true; }
    private sealed class TestStreams(ILibraryManager library, ILogger<StreamController> logger, UserPermissionGuard guard, IUserManager users)
        : StreamController(library, logger, guard, users)
    {
        protected override PluginConfiguration Config => new() { DirectMovieUrlTemplate = "https://streams.test/private-test-token/{imdb}" };
    }
    private sealed class TestSubtitles(DynamicItemCache cache, string root) : SubtitleService(null!, cache, NullLogger<SubtitleService>.Instance)
    {
        protected override PluginConfiguration Config => new() { SubtitleCachePath = root };
    }
    private sealed class CapturingLogger : ILogger<StreamController>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    private sealed class RetainedHistory(Guid watchedUser, ImmutableHashSet<string> keys) : IMovieWatchHistory
    {
        public Task<MovieWatchSnapshot> GetSnapshotAsync(User user, CancellationToken cancellationToken) =>
            Task.FromResult(new MovieWatchSnapshot(user.Id == watchedUser ? keys : ImmutableHashSet<string>.Empty));
    }
}
