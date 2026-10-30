using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Mediarow.Configuration;
using Jellyfin.Plugin.Mediarow.Filters;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Mediarow.Tests.AioStreamsPlaybackTests;

namespace Jellyfin.Plugin.Mediarow.Tests;

public class TrackLanguageSelectorTests
{
    private static MediaStream Audio(int index, string? language, int channels = 2, string? title = null, bool isOriginal = false)
        => new() { Index = index, Type = MediaStreamType.Audio, Language = language, Channels = channels, Title = title, IsOriginal = isOriginal };

    private static MediaStream Subtitle(int index, string? language, string? title = null, bool forced = false)
        => new() { Index = index, Type = MediaStreamType.Subtitle, Language = language, Title = title, IsForced = forced };

    private static LanguagePreference Preference(string[] audio, string[] subtitles, bool original = true)
        => new(audio, subtitles, original, true, true, true);

    [Fact]
    public void OriginalAudioWinsAndPicksMostChannels()
    {
        MediaStream[] streams = [Audio(1, "rus", 6), Audio(2, "eng", 2), Audio(3, "eng", 6)];
        var selection = TrackLanguageSelector.Select(streams, Preference(["ru"], []), "en");
        Assert.Equal(3, selection.AudioStreamIndex);
    }

    [Fact]
    public void PriorityListIsUsedWhenOriginalIsMissingOrDisabled()
    {
        MediaStream[] streams = [Audio(1, "fre"), Audio(2, "ger"), Audio(3, "eng")];
        Assert.Equal(2, TrackLanguageSelector.Select(streams, Preference(["de", "en"], []), "ja").AudioStreamIndex);
        Assert.Equal(2, TrackLanguageSelector.Select(streams, Preference(["deu", "eng"], [], original: false), "en").AudioStreamIndex);
    }

    [Fact]
    public void ContainerFlagAndTitleIdentifyOriginalVersion()
    {
        Assert.Equal(2, TrackLanguageSelector.Select([Audio(1, "eng"), Audio(2, "fre", isOriginal: true)], Preference([], []), "en").AudioStreamIndex);
        Assert.Equal(2, TrackLanguageSelector.Select([Audio(1, "eng"), Audio(2, "fre", title: "Original")], Preference([], []), "en").AudioStreamIndex);
    }

    [Fact]
    public void UntaggedAudioUsesTrackTitle()
    {
        MediaStream[] streams = [Audio(1, null, title: "Russian 5.1"), Audio(2, null, title: "English 5.1")];
        Assert.Equal(2, TrackLanguageSelector.Select(streams, Preference([], []), "en").AudioStreamIndex);
    }

    [Fact]
    public void AudioInSubtitleLanguageShowsForcedSubtitlesOrNone()
    {
        MediaStream[] forced = [Audio(1, "eng"), Subtitle(2, "eng"), Subtitle(3, "eng", "Forced")];
        Assert.Equal(3, TrackLanguageSelector.Select(forced, Preference(["en"], ["en"]), "en").SubtitleStreamIndex);

        MediaStream[] none = [Audio(1, "eng"), Subtitle(2, "eng"), Subtitle(3, "fre")];
        Assert.Equal(TrackLanguageSelector.SubtitlesOff, TrackLanguageSelector.Select(none, Preference(["en"], ["en"]), "en").SubtitleStreamIndex);
    }

    [Fact]
    public void ManualAudioPickDrivesSubtitleChoice()
    {
        MediaStream[] streams = [Audio(1, "eng"), Audio(2, "jpn"), Subtitle(3, "eng"), Subtitle(4, "eng", forced: true)];
        Assert.Equal(new TrackSelection(1, 4), TrackLanguageSelector.Select(streams, Preference(["en"], ["en"], original: false), "en"));
        Assert.Equal(new TrackSelection(2, 3), TrackLanguageSelector.Select(streams, Preference(["en"], ["en"], original: false), "en", manualAudioIndex: 2));
    }

    [Fact]
    public void SubtitleModesCoverAlwaysForcedOnlyOffAndDefault()
    {
        MediaStream[] streams = [Audio(1, "eng"), Audio(2, "jpn"), Subtitle(3, "eng"), Subtitle(4, "eng", forced: true), Subtitle(5, "dut")];
        LanguagePreference Mode(SubtitleMode mode, string[] subtitles) => new(["en"], subtitles, false, true, true, true, mode);

        // English audio: "when needed" shows only forced English; "always" shows full English anyway.
        Assert.Equal(4, TrackLanguageSelector.Select(streams, Mode(SubtitleMode.WhenNeeded, ["en"]), "en").SubtitleStreamIndex);
        Assert.Equal(3, TrackLanguageSelector.Select(streams, Mode(SubtitleMode.Always, ["en"]), "en").SubtitleStreamIndex);
        Assert.Equal(5, TrackLanguageSelector.Select(streams, Mode(SubtitleMode.Always, ["nl", "en"]), "en").SubtitleStreamIndex);
        Assert.Equal(4, TrackLanguageSelector.Select(streams, Mode(SubtitleMode.ForcedOnly, []), "en").SubtitleStreamIndex);
        Assert.Equal(TrackLanguageSelector.SubtitlesOff,
            TrackLanguageSelector.Select([Audio(1, "eng"), Subtitle(3, "eng")], Mode(SubtitleMode.ForcedOnly, ["en"]), "en").SubtitleStreamIndex);
        Assert.Equal(TrackLanguageSelector.SubtitlesOff, TrackLanguageSelector.Select(streams, Mode(SubtitleMode.Off, ["en"]), "en").SubtitleStreamIndex);
        Assert.Null(TrackLanguageSelector.Select(streams, Mode(SubtitleMode.Default, ["en"]), "en").SubtitleStreamIndex);
    }

    [Fact]
    public void TwoUsersGetTheirOwnTracks()
    {
        var originalFan = Guid.NewGuid();
        var dubFan = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserLanguagePreferences =
            [
                new UserLanguagePreference { UserId = originalFan.ToString("N"), PreferOriginalAudio = true, SubtitleLanguages = ["eng"], SubtitleMode = SubtitleMode.Always },
                new UserLanguagePreference { UserId = dubFan.ToString(), PreferOriginalAudio = false, AudioLanguages = ["ger"], SubtitleMode = SubtitleMode.Off },
            ],
        };
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie();
        MediaStream[] streams = [Audio(1, "ger"), Audio(2, "jpn"), Subtitle(3, "eng")];

        var first = TrackLanguageSelector.Resolve(config, originalFan, movie, false)!;
        var second = TrackLanguageSelector.Resolve(config, dubFan, movie, false)!;
        Assert.Equal(new TrackSelection(2, 3), TrackLanguageSelector.Select(streams, first, "ja"));
        Assert.Equal(new TrackSelection(1, TrackLanguageSelector.SubtitlesOff), TrackLanguageSelector.Select(streams, second, "ja"));
        Assert.Null(TrackLanguageSelector.Resolve(config, Guid.NewGuid(), movie, false));
    }

    [Fact]
    public void RememberedSeriesChoiceAppliesToTheNextEpisodeForThatUserOnly()
    {
        var sam = Guid.NewGuid();
        var other = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserLanguagePreferences =
            [
                new UserLanguagePreference { UserId = sam.ToString("N"), PreferOriginalAudio = true, SubtitleLanguages = ["eng"] },
                new UserLanguagePreference { UserId = other.ToString("N"), PreferOriginalAudio = true, SubtitleLanguages = ["eng"] },
            ],
        };
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { SeriesId = seriesId, SeriesName = "Smallville" };
        MediaStream[] streams = [Audio(1, "eng"), Audio(2, "rus"), Subtitle(3, "eng"), Subtitle(4, "dut")];

        // Sam switches to the Russian dub with Dutch subtitles during episode 1.
        Assert.True(SeriesLanguageMemory.Remember(config, sam, episode, streams, 2, 4, true, true));
        var learned = Assert.Single(config.UserLanguagePreferences[0].SeriesOverrides);
        Assert.True(learned.Learned);
        Assert.Equal("Smallville", learned.SeriesName);
        Assert.False(SeriesLanguageMemory.Remember(config, sam, episode, streams, 2, 4, true, true));

        var next = new MediaBrowser.Controller.Entities.TV.Episode { SeriesId = seriesId };
        Assert.Equal(new TrackSelection(2, 4), TrackLanguageSelector.Select(streams, TrackLanguageSelector.Resolve(config, sam, next, false)!, "en"));
        Assert.Equal(new TrackSelection(1, TrackLanguageSelector.SubtitlesOff),
            TrackLanguageSelector.Select(streams, TrackLanguageSelector.Resolve(config, other, next, false)!, "en"));

        // Turning subtitles off is remembered too; a movie or a user who opted out is never changed.
        Assert.True(SeriesLanguageMemory.Remember(config, sam, episode, streams, null, TrackLanguageSelector.SubtitlesOff, false, true));
        Assert.Equal(new TrackSelection(2, TrackLanguageSelector.SubtitlesOff), TrackLanguageSelector.Select(streams, TrackLanguageSelector.Resolve(config, sam, next, false)!, "en"));
        Assert.False(SeriesLanguageMemory.Remember(config, sam, new MediaBrowser.Controller.Entities.Movies.Movie(), streams, 2, 4, true, true));
        config.UserLanguagePreferences[1].RememberSeriesChoices = false;
        Assert.False(SeriesLanguageMemory.Remember(config, other, episode, streams, 2, 4, true, true));
        Assert.Empty(config.UserLanguagePreferences[1].SeriesOverrides);
    }

    [Fact]
    public void ForeignAudioPrefersFullSubtitlesInPriorityOrder()
    {
        MediaStream[] streams = [Audio(1, "jpn"), Subtitle(2, "eng", "Signs & Songs"), Subtitle(3, "eng", "Full Subtitles"), Subtitle(4, "dut")];
        Assert.Equal(3, TrackLanguageSelector.Select(streams, Preference([], ["en", "nl"]), "ja").SubtitleStreamIndex);
        Assert.Equal(4, TrackLanguageSelector.Select(streams, Preference([], ["nl", "en"]), "ja").SubtitleStreamIndex);
    }

    [Fact]
    public void LowerPriorityAudioStillGetsBetterSubtitles()
    {
        // Dutch speaker who also understands English: English audio + Dutch subtitles.
        MediaStream[] streams = [Audio(1, "eng"), Subtitle(2, "eng"), Subtitle(3, "dut")];
        Assert.Equal(3, TrackLanguageSelector.Select(streams, Preference([], ["nl", "en"]), "en").SubtitleStreamIndex);
    }

    [Theory]
    [InlineData("Forced", true)]
    [InlineData("Signs & Songs", true)]
    [InlineData("S&S", true)]
    [InlineData("Full + Signs", false)]
    [InlineData("Non-Forced", false)]
    [InlineData("Designs", false)]
    public void DetectsForcedSubtitleTitles(string title, bool expected)
        => Assert.Equal(expected, TrackLanguageSelector.IsForcedSubtitle(Subtitle(1, "eng", title)));

    [Fact]
    public void ResolveAppliesSeriesOverrideAndGlobalFallback()
    {
        var user = Guid.NewGuid();
        var series = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserLanguagePreferences =
            [
                new UserLanguagePreference
                {
                    UserId = user.ToString("D"), AudioLanguages = ["en"], SubtitleLanguages = ["nl"],
                    SeriesOverrides = [new SeriesLanguageOverride { SeriesId = series.ToString("N"), AudioLanguages = ["ja"] }]
                }
            ]
        };

        var preference = TrackLanguageSelector.Resolve(config, user, new Episode { SeriesId = series }, false)!;
        Assert.Equal(["ja"], preference.AudioLanguages);
        Assert.Equal(["nl"], preference.SubtitleLanguages);
        Assert.Equal(["en"], TrackLanguageSelector.Resolve(config, user, new Movie(), false)!.AudioLanguages);

        Assert.Null(TrackLanguageSelector.Resolve(config, Guid.NewGuid(), new Movie(), false));
        var fallback = TrackLanguageSelector.Resolve(config, Guid.NewGuid(), new Movie(), true)!;
        Assert.False(fallback.IsUserSpecific);
        Assert.True(fallback.PreferOriginalAudio);
        config.PreferOriginalLanguageStreams = false;
        Assert.Null(TrackLanguageSelector.Resolve(config, Guid.NewGuid(), new Movie(), true));
    }

    [Fact]
    public void ReleasesAreRankedByUserLanguageOrder()
    {
        string[] releases = ["Movie 1080p RUS", "Movie 1080p", "Movie 1080p ENG", "Movie 1080p GER"];
        var ordered = StreamLanguageDetector.OrderByLanguages(releases, release => release, ["de", "en"]);
        Assert.Equal(["Movie 1080p GER", "Movie 1080p ENG", "Movie 1080p", "Movie 1080p RUS"], ordered);
    }

    [Fact]
    public async Task FilterInjectsPreferredTracksUnlessClientPickedManually()
    {
        var user = new User("viewer", "test", "test") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableAllFolders, true);
        var movie = new VisibleLanguageMovie { Id = Guid.NewGuid(), Name = "Smallville", OriginalLanguage = "en" };
        var source = new MediaSourceInfo
        {
            Id = movie.Id.ToString("N"),
            MediaStreams = [Audio(1, "rus"), Audio(2, "eng"), Subtitle(3, "eng"), Subtitle(4, "rus")],
            DefaultAudioStreamIndex = 1,
            DefaultSubtitleStreamIndex = -1
        };
        var config = new PluginConfiguration
        {
            UserLanguagePreferences = [new UserLanguagePreference { UserId = user.Id.ToString("N"), SubtitleLanguages = ["en"] }]
        };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var library = Proxy<ILibraryManager>.Create((method, _) => method.Name == "GetItemById" ? movie : null);
        var users = Proxy<IUserManager>.Create((method, _) => method.Name == "GetUserById" ? user : null);
        var guard = new UserPermissionGuard(users, library, null!, new DynamicItemCache(cache, NullLogger<DynamicItemCache>.Instance), cache);
        var filter = new LanguagePreferenceFilter(
            library, users,
            Proxy<IMediaSourceManager>.Create((method, _) => method.Name == "GetStaticMediaSources" ? new List<MediaSourceInfo> { source } : null),
            null!, cache, NullLogger<LanguagePreferenceFilter>.Instance, guard) { Config = config };

        async Task<IDictionary<string, object?>> Run(int? clientAudio)
        {
            var arguments = new Dictionary<string, object?> { ["itemId"] = movie.Id, ["userId"] = user.Id };
            if (clientAudio.HasValue)
            {
                arguments["audioStreamIndex"] = clientAudio;
            }

            var action = new ActionContext(new DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(UserPermissionGuard.UserIdClaim, user.Id.ToString())], "test")) }, new RouteData(),
                new ControllerActionDescriptor { ControllerName = "MediaInfo", ActionName = "GetPostedPlaybackInfo" });
            var context = new ActionExecutingContext(action, [], arguments, new object());
            await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(action, [], new object())));
            return context.ActionArguments;
        }

        var automatic = await Run(null);
        Assert.Equal(2, automatic["audioStreamIndex"]);
        Assert.Equal(TrackLanguageSelector.SubtitlesOff, automatic["subtitleStreamIndex"]);
        Assert.Equal(source.Id, automatic["mediaSourceId"]);

        Assert.Equal(2, (await Run(1))["audioStreamIndex"]);
        var manual = await Run(4);
        Assert.Equal(4, manual["audioStreamIndex"]);
        Assert.True(cache.Get<TrackDecision>(TrackDecision.CacheKey(user.Id, movie.Id))!.AudioManual);
    }

    private sealed class VisibleLanguageMovie : Movie
    {
        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => true;
        public override bool IsVisibleStandalone(User user) => true;
    }

    [Fact]
    public async Task PlaybackStartOnlyCorrectsTracksThatDiffer()
    {
        var user = new User("viewer", "test", "test");
        var movie = new Movie { Id = Guid.NewGuid() };
        var sent = new List<GeneralCommand>();
        var sessions = Proxy<ISessionManager>.Create((method, args) =>
        {
            sent.Add((GeneralCommand)args![2]!);
            return Task.CompletedTask;
        });
        var session = new SessionInfo(sessions, NullLogger.Instance)
        {
            Id = "session",
            NowPlayingItem = new BaseItemDto { Id = movie.Id },
            PlayState = new PlayerStateInfo { AudioStreamIndex = 2, SubtitleStreamIndex = 3 },
            Capabilities = new ClientCapabilities { SupportedCommands = [GeneralCommandType.SetAudioStreamIndex, GeneralCommandType.SetSubtitleStreamIndex] }
        };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var consumer = new LanguagePlaybackStartConsumer(cache, sessions, null!, NullLogger<LanguagePlaybackStartConsumer>.Instance)
        {
            PlayerInitDelay = TimeSpan.Zero, BetweenCommandsDelay = TimeSpan.Zero
        };
        var playback = new PlaybackStartEventArgs { Item = movie, Users = [user], Session = session };

        cache.Set(TrackDecision.CacheKey(user.Id, movie.Id), new TrackDecision(2, -1, false, false));
        await consumer.CorrectAsync(playback, new PluginConfiguration());
        var command = Assert.Single(sent);
        Assert.Equal(GeneralCommandType.SetSubtitleStreamIndex, command.Name);
        Assert.Equal("-1", command.Arguments["Index"]);

        sent.Clear();
        cache.Set(TrackDecision.CacheKey(user.Id, movie.Id), new TrackDecision(1, 3, true, false));
        await consumer.CorrectAsync(playback, new PluginConfiguration());
        Assert.Empty(sent);
    }
}
