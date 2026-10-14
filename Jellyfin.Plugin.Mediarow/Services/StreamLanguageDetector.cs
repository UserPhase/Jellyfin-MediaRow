using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Mediarow.Services;

/// <summary>
/// Reads audio languages from AIOStreams result text (flags, names and release tags)
/// so releases in a title's original language can be preferred.
/// </summary>
public static partial class StreamLanguageDetector
{
    private sealed record Language(string Code, string[] Flags, string[] Names, string[] Tags);

    // Tags are matched case-sensitively in upper case (or as written) to avoid hits on ordinary words.
    private static readonly Language[] Languages =
    [
        new("en", ["GB", "US", "AU", "CA", "IE", "NZ"], ["english"], ["ENG", "EN"]),
        new("ru", ["RU"], ["russian"], ["RUS", "Rus"]),
        new("uk", ["UA"], ["ukrainian"], ["UKR", "Ukr"]),
        new("fr", ["FR"], ["french", "truefrench"], ["FRE", "FRA", "FR", "VFF", "VFQ", "VF", "VF2", "VFI", "VOF"]),
        new("de", ["DE", "AT"], ["german"], ["GER", "DEU"]),
        new("es", ["ES", "MX", "AR"], ["spanish", "castellano", "latino"], ["SPA", "ESP", "LAT"]),
        new("it", ["IT"], ["italian"], ["ITA"]),
        new("pt", ["PT", "BR"], ["portuguese"], ["POR", "PTBR"]),
        new("ja", ["JP"], ["japanese"], ["JPN", "JAP"]),
        new("ko", ["KR"], ["korean"], ["KOR"]),
        new("zh", ["CN", "TW", "HK"], ["chinese", "mandarin", "cantonese"], ["CHI", "CHN", "ZHO"]),
        new("hi", ["IN"], ["hindi"], ["HIN"]),
        new("ta", [], ["tamil"], ["TAM"]),
        new("te", [], ["telugu"], ["TEL"]),
        new("pl", ["PL"], ["polish"], ["POL", "PL"]),
        new("nl", ["NL", "BE"], ["dutch"], ["DUT", "NLD"]),
        new("tr", ["TR"], ["turkish"], ["TUR"]),
        new("ar", ["SA", "AE", "EG"], ["arabic"], ["ARA"]),
        new("cs", ["CZ"], ["czech"], ["CZE", "CES"]),
        new("hu", ["HU"], ["hungarian"], ["HUN"]),
        new("el", ["GR"], ["greek"], ["GRE", "ELL"]),
        new("sv", ["SE"], ["swedish"], ["SWE"]),
        new("no", ["NO"], ["norwegian"], ["NOR"]),
        new("da", ["DK"], ["danish"], ["DAN"]),
        new("fi", ["FI"], ["finnish"], ["FIN"]),
        new("ro", ["RO"], ["romanian"], ["ROM", "RUM", "RON"]),
        new("he", ["IL"], ["hebrew"], ["HEB"]),
        new("th", ["TH"], ["thai"], ["THA"]),
        new("vi", ["VN"], ["vietnamese"], ["VIE"]),
        new("id", ["ID"], ["indonesian"], ["IND"]),
        new("fa", ["IR"], ["persian", "farsi"], ["PER", "FAS"]),
        new("bg", ["BG"], ["bulgarian"], ["BUL"]),
        new("hr", ["HR"], ["croatian"], ["HRV", "CRO"]),
        new("sr", ["RS"], ["serbian"], ["SRP"]),
        new("lt", ["LT"], ["lithuanian"], ["LIT"]),
        new("lv", ["LV"], ["latvian"], ["LAV"]),
        new("et", ["EE"], ["estonian"], ["EST"])
    ];

    private static readonly Dictionary<string, string> FlagToLanguage = Languages
        .SelectMany(language => language.Flags.Select(flag => (flag, language.Code)))
        .ToDictionary(pair => pair.flag, pair => pair.Code, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> NameToLanguage = Languages
        .SelectMany(language => language.Names.Select(name => (name, language.Code)))
        .ToDictionary(pair => pair.name, pair => pair.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> TagToLanguage = Languages
        .SelectMany(language => language.Tags.Select(tag => (tag, language.Code)))
        .ToDictionary(pair => pair.tag, pair => pair.Code, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> IsoCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["rus"] = "ru", ["ukr"] = "uk", ["fre"] = "fr", ["fra"] = "fr", ["ger"] = "de", ["deu"] = "de",
        ["spa"] = "es", ["ita"] = "it", ["por"] = "pt", ["jpn"] = "ja", ["kor"] = "ko", ["chi"] = "zh", ["zho"] = "zh",
        ["hin"] = "hi", ["tam"] = "ta", ["tel"] = "te", ["pol"] = "pl", ["dut"] = "nl", ["nld"] = "nl", ["tur"] = "tr",
        ["ara"] = "ar", ["cze"] = "cs", ["ces"] = "cs", ["hun"] = "hu", ["gre"] = "el", ["ell"] = "el", ["swe"] = "sv",
        ["nor"] = "no", ["nob"] = "no", ["nno"] = "no", ["dan"] = "da", ["fin"] = "fi", ["rum"] = "ro", ["ron"] = "ro",
        ["heb"] = "he", ["tha"] = "th", ["vie"] = "vi", ["ind"] = "id", ["per"] = "fa", ["fas"] = "fa", ["bul"] = "bg",
        ["hrv"] = "hr", ["srp"] = "sr", ["lit"] = "lt", ["lav"] = "lv", ["est"] = "et", ["cmn"] = "zh", ["yue"] = "zh"
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^[\s._-]*(?:soft)?subs?(?:bed|titles?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubtitleSuffixRegex();

    [GeneratedRegex(@"\b(?:multi(?:-?audio|-?lang)?|dual(?:[\s.-]?audio)?)\b|🌐", RegexOptions.IgnoreCase)]
    private static partial Regex MultiRegex();

    /// <summary>
    /// Converts an ISO 639-1/639-2 code (e.g. "en", "eng") to a two-letter code, or null when unknown.
    /// </summary>
    public static string? NormalizeCode(string? language)
    {
        var value = language?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // Region suffixes such as "en-US" or "pt_BR".
        value = value.Split('-', '_')[0];
        if (value.Length == 2 && value.All(char.IsAsciiLetter))
        {
            return value.ToLowerInvariant();
        }

        return IsoCodes.TryGetValue(value, out var code) ? code : null;
    }

    /// <summary>
    /// Returns the languages a stream advertises and whether it is labeled as multi-language.
    /// </summary>
    public static (IReadOnlySet<string> Languages, bool IsMulti) Detect(string? text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (found, false);
        }

        AddFlagLanguages(text, found);
        foreach (Match match in WordRegex().Matches(text))
        {
            var word = match.Value;
            // "ENG-SUBS" or "English Subtitles" describe subtitles, not audio.
            if ((NameToLanguage.TryGetValue(word, out var named) || TagToLanguage.TryGetValue(word, out named)) &&
                !SubtitleSuffixRegex().IsMatch(text.AsSpan(match.Index + match.Length).ToString()))
            {
                found.Add(named);
            }
        }

        return (found, MultiRegex().IsMatch(text));
    }

    /// <summary>
    /// Ranks a stream for the original language: 0 = labeled with it, 1 = unlabeled or multi-language,
    /// 2 = labeled with other languages only.
    /// </summary>
    public static int Rank(string? text, string originalLanguage)
    {
        var (languages, isMulti) = Detect(text);
        if (languages.Contains(originalLanguage))
        {
            return 0;
        }

        return languages.Count == 0 || isMulti ? 1 : 2;
    }

    /// <summary>
    /// Stable-sorts streams so releases in the original language come first.
    /// </summary>
    public static List<T> OrderByOriginalLanguage<T>(IEnumerable<T> streams, Func<T, string?> describe, string? originalLanguage)
        => OrderByLanguages(streams, describe, NormalizeCode(originalLanguage) is { } code ? [code] : []);

    /// <summary>
    /// Stable-sorts streams by the first preferred language they advertise; unlabeled or multi-language
    /// releases follow, and releases only in other languages go last.
    /// </summary>
    public static List<T> OrderByLanguages<T>(IEnumerable<T> streams, Func<T, string?> describe, IReadOnlyList<string> languages)
    {
        var codes = languages.Select(NormalizeCode).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var list = streams.ToList();
        if (codes.Count == 0)
        {
            return list;
        }

        return list.Select((stream, index) => (stream, index, rank: Rank(describe(stream), codes)))
            .OrderBy(entry => entry.rank)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.stream)
            .ToList();
    }

    private static int Rank(string? text, List<string> codes)
    {
        var (found, isMulti) = Detect(text);
        var position = codes.FindIndex(found.Contains);
        if (position >= 0)
        {
            return position;
        }

        return found.Count == 0 || isMulti ? codes.Count : codes.Count + 1;
    }

    /// <summary>
    /// Finds the first audio track in the original language.
    /// </summary>
    public static MediaStream? FindOriginalAudio(IEnumerable<MediaStream> streams, string? originalLanguage)
    {
        var code = NormalizeCode(originalLanguage);
        if (code == null)
        {
            return null;
        }

        var audio = streams.Where(stream => stream.Type == MediaStreamType.Audio).ToList();
        return audio.FirstOrDefault(stream => NormalizeCode(stream.Language) == code)
            ?? audio.FirstOrDefault(stream => NormalizeCode(stream.Language) == null &&
                (Detect(stream.Title).Languages.Contains(code) || Detect(stream.DisplayTitle).Languages.Contains(code)));
    }

    private static void AddFlagLanguages(string text, HashSet<string> found)
    {
        // Flags are pairs of regional indicator symbols (U+1F1E6..U+1F1FF), two UTF-16 chars each.
        for (var i = 0; i + 3 < text.Length; i++)
        {
            if (!char.IsSurrogatePair(text[i], text[i + 1]) || !char.IsSurrogatePair(text[i + 2], text[i + 3]))
            {
                continue;
            }

            var first = char.ConvertToUtf32(text[i], text[i + 1]) - 0x1F1E6;
            var second = char.ConvertToUtf32(text[i + 2], text[i + 3]) - 0x1F1E6;
            if (first is < 0 or > 25 || second is < 0 or > 25)
            {
                continue;
            }

            var country = new string([(char)('A' + first), (char)('A' + second)]);
            if (FlagToLanguage.TryGetValue(country, out var language))
            {
                found.Add(language);
            }

            i += 3;
        }
    }
}
