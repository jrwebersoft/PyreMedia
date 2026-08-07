namespace PyreMedia.Core.Models;

/// <summary>One language, as media files label them.</summary>
public sealed record LanguageEntry(string Code, string Name, string? Alt = null)
{
    public string Display => $"{Name} ({Code})";

    /// <summary>True when the text matches the name, the code, or a known alias.</summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        var q = query.Trim();

        return Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || Code.StartsWith(q, StringComparison.OrdinalIgnoreCase)
               || (Alt is not null && Alt.StartsWith(q, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The languages that turn up in media files, by ISO 639-2/B code - which is
/// what ffprobe reports and what Matroska stores.
///
/// Several languages have two ISO 639-2 codes: a bibliographic one (fre, ger)
/// and a terminological one (fra, deu). Files use both, so each is listed as an
/// alias and either will resolve.
/// </summary>
public static class LanguageCatalog
{
    public static readonly IReadOnlyList<LanguageEntry> All =
    [
        new("eng", "English"),
        new("spa", "Spanish"),
        new("fre", "French", "fra"),
        new("ger", "German", "deu"),
        new("ita", "Italian"),
        new("por", "Portuguese"),
        new("dut", "Dutch", "nld"),
        new("rus", "Russian"),
        new("jpn", "Japanese"),
        new("kor", "Korean"),
        new("chi", "Chinese", "zho"),
        new("ara", "Arabic"),
        new("hin", "Hindi"),
        new("pol", "Polish"),
        new("swe", "Swedish"),
        new("nor", "Norwegian"),
        new("dan", "Danish"),
        new("fin", "Finnish"),
        new("ice", "Icelandic", "isl"),
        new("cze", "Czech", "ces"),
        new("slo", "Slovak", "slk"),
        new("hun", "Hungarian"),
        new("rum", "Romanian", "ron"),
        new("bul", "Bulgarian"),
        new("gre", "Greek", "ell"),
        new("tur", "Turkish"),
        new("heb", "Hebrew"),
        new("tha", "Thai"),
        new("vie", "Vietnamese"),
        new("ind", "Indonesian"),
        new("may", "Malay", "msa"),
        new("tgl", "Tagalog"),
        new("ukr", "Ukrainian"),
        new("srp", "Serbian"),
        new("hrv", "Croatian"),
        new("slv", "Slovenian"),
        new("est", "Estonian"),
        new("lav", "Latvian"),
        new("lit", "Lithuanian"),
        new("cat", "Catalan"),
        new("baq", "Basque", "eus"),
        new("glg", "Galician"),
        new("per", "Persian", "fas"),
        new("urd", "Urdu"),
        new("ben", "Bengali"),
        new("tam", "Tamil"),
        new("tel", "Telugu"),
        new("mal", "Malayalam"),
        new("kan", "Kannada"),
        new("mar", "Marathi"),
        new("pan", "Punjabi"),
        new("und", "Untagged / unknown")
    ];

    /// <summary>The entry for a code, matching either ISO 639-2 form.</summary>
    public static LanguageEntry? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var c = code.Trim().ToLowerInvariant();

        return All.FirstOrDefault(l =>
            string.Equals(l.Code, c, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l.Alt, c, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Readable name for a code, falling back to the code itself.</summary>
    public static string NameOf(string? code) =>
        Find(code)?.Name ?? (string.IsNullOrWhiteSpace(code) ? "unknown" : code!);

    public static IEnumerable<LanguageEntry> Search(string query) =>
        All.Where(l => l.Matches(query));

    /// <summary>Split a stored "eng;jpn" list into codes.</summary>
    public static List<string> Parse(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : [.. stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(c => c.ToLowerInvariant())
                 .Distinct()];

    public static string Join(IEnumerable<string> codes) => string.Join(";", codes);

    /// <summary>
    /// Whether two codes name the same language.
    ///
    /// This has to exist because several languages have two ISO 639-2 codes and
    /// files use both: a track tagged "fra" and a rule saying "fre" are the same
    /// French. Comparing the strings directly means the rule silently fails to
    /// match and the track is dropped as unwanted.
    /// </summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        var x = a.Trim();
        var y = b.Trim();

        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;

        // Resolve both to their catalogue entry; unknown codes only ever match
        // themselves, which the check above already covered.
        var ex = Find(x);
        var ey = Find(y);

        return ex is not null && ey is not null
               && string.Equals(ex.Code, ey.Code, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a track's language is in the wanted list, either form.</summary>
    public static bool WantedBy(IEnumerable<string> wanted, string? language)
        => wanted.Any(w => Same(w, language));
}
