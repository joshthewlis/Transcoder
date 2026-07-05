namespace Transcoder.Server.Services;

public static class LanguageNormalizer
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng", ["eng"] = "eng", ["english"] = "eng",
        ["ja"] = "jpn", ["jp"] = "jpn", ["jpn"] = "jpn", ["japanese"] = "jpn",
        ["de"] = "deu", ["deu"] = "deu", ["ger"] = "deu", ["german"] = "deu",
        ["fr"] = "fra", ["fra"] = "fra", ["fre"] = "fra", ["french"] = "fra",
        ["es"] = "spa", ["spa"] = "spa", ["spanish"] = "spa",
        ["it"] = "ita", ["ita"] = "ita", ["italian"] = "ita",
        ["ko"] = "kor", ["kor"] = "kor", ["korean"] = "kor",
        ["zh"] = "zho", ["zho"] = "zho", ["chi"] = "zho", ["chinese"] = "zho",
        ["pt"] = "por", ["por"] = "por", ["portuguese"] = "por",
        ["nl"] = "nld", ["nld"] = "nld", ["dut"] = "nld", ["dutch"] = "nld",
        ["sv"] = "swe", ["swe"] = "swe", ["swedish"] = "swe",
        ["no"] = "nor", ["nor"] = "nor", ["norwegian"] = "nor",
        ["da"] = "dan", ["dan"] = "dan", ["danish"] = "dan",
        ["fi"] = "fin", ["fin"] = "fin", ["finnish"] = "fin",
        ["pl"] = "pol", ["pol"] = "pol", ["polish"] = "pol",
        ["ru"] = "rus", ["rus"] = "rus", ["russian"] = "rus"
    };

    public static string? Normalize(string? language)
    {
        var value = (language ?? string.Empty).Trim().ToLowerInvariant();
        if (value is "" or "und" or "undefined" or "unknown" or "unk" or "zxx")
            return null;

        if (Map.TryGetValue(value, out var mapped))
            return mapped;

        return value.Length == 2 ? value : value[..Math.Min(3, value.Length)];
    }

    public static string? ToTmdbIso6391(string? language)
    {
        var normalized = Normalize(language);
        return normalized switch
        {
            "eng" => "en",
            "jpn" => "ja",
            "deu" => "de",
            "fra" => "fr",
            "spa" => "es",
            "ita" => "it",
            "kor" => "ko",
            "zho" => "zh",
            "por" => "pt",
            "nld" => "nl",
            "swe" => "sv",
            "nor" => "no",
            "dan" => "da",
            "fin" => "fi",
            "pol" => "pl",
            "rus" => "ru",
            _ => normalized?.Length == 2 ? normalized : null
        };
    }
}
