using Jellyfin.Plugin.Mediarow.Providers;
using Jellyfin.Plugin.Mediarow.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public class StreamLanguageDetectorTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("eng", "en")]
    [InlineData("EN-us", "en")]
    [InlineData("rus", "ru")]
    [InlineData("fre", "fr")]
    [InlineData("und", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeCode_MapsIsoCodes(string? input, string? expected)
    {
        Assert.Equal(expected, StreamLanguageDetector.NormalizeCode(input));
    }

    [Theory]
    [InlineData("TorBox · 1080p WEB-DL · 🇷🇺", "ru")]
    [InlineData("Smallville.S01E01.1080p.BluRay.RUS.x264", "ru")]
    [InlineData("🗣️ English | 💾 2.1 GB", "en")]
    [InlineData("Smallville S01E01 ITA ENG 1080p", "it,en")]
    [InlineData("🇬🇧 🇷🇺 Dual Audio", "en,ru")]
    [InlineData("Smallville.S01E01.1080p.WEB-DL.TRUEFRENCH", "fr")]
    [InlineData("Smallville 1080p BluRay x265", "")]
    public void Detect_ReadsFlagsNamesAndTags(string text, string expected)
    {
        var languages = StreamLanguageDetector.Detect(text).Languages;
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(), languages.Order());
    }

    [Fact]
    public void Detect_IgnoresSubtitleTagsAndOrdinaryWords()
    {
        Assert.Empty(StreamLanguageDetector.Detect("Movie.2001.1080p.ENG-SUBS.HEVC").Languages);
        Assert.Empty(StreamLanguageDetector.Detect("The Russ Brand Show and an eng-ine").Languages);
        Assert.True(StreamLanguageDetector.Detect("Movie MULTi 1080p").IsMulti);
    }

    [Fact]
    public void OrderByOriginalLanguage_MovesOriginalFirstAndOtherLanguagesLast()
    {
        string[] streams =
        [
            "Smallville S01E01 1080p 🇷🇺",
            "Smallville S01E01 1080p",
            "Smallville S01E01 720p English",
            "Smallville S01E01 MULTi",
            "Smallville S01E01 2160p 🇬🇧"
        ];

        var ordered = StreamLanguageDetector.OrderByOriginalLanguage(streams, stream => stream, "en");

        Assert.Equal(
        [
            "Smallville S01E01 720p English",
            "Smallville S01E01 2160p 🇬🇧",
            "Smallville S01E01 1080p",
            "Smallville S01E01 MULTi",
            "Smallville S01E01 1080p 🇷🇺"
        ], ordered);
    }

    [Fact]
    public void OrderByOriginalLanguage_KeepsOrderWhenLanguageUnknown()
    {
        string[] streams = ["A 🇷🇺", "B English"];
        Assert.Equal(streams, StreamLanguageDetector.OrderByOriginalLanguage(streams, stream => stream, null));
    }

    [Fact]
    public void PreferOriginalAudio_MarksOriginalTrackDefault()
    {
        var source = new MediaSourceInfo
        {
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0 },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "rus", IsDefault = true },
                new MediaStream { Type = MediaStreamType.Audio, Index = 2, Language = "eng" }
            ],
            DefaultAudioStreamIndex = 1
        };

        DynamicAioStreamsMediaSourceProvider.PreferOriginalAudio(source, "en");

        Assert.Equal(2, source.DefaultAudioStreamIndex);
        Assert.False(source.MediaStreams[1].IsDefault);
        Assert.True(source.MediaStreams[2].IsDefault);
    }

    [Fact]
    public void PreferOriginalAudio_LeavesSourceWhenNoTrackMatches()
    {
        var source = new MediaSourceInfo
        {
            MediaStreams = [new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "rus", IsDefault = true }],
            DefaultAudioStreamIndex = 1
        };

        DynamicAioStreamsMediaSourceProvider.PreferOriginalAudio(source, "en");

        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.True(source.MediaStreams[0].IsDefault);
    }
}
