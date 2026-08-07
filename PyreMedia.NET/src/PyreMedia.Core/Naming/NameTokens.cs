using System.Text.RegularExpressions;

namespace PyreMedia.Core.Naming;

/// <summary>
/// Named placeholders for the filename formats - <c>{title} {season}x{episode}
/// {name}</c> rather than <c>{0} {1}x{3} {2}</c>.
///
/// The numeric form is what .NET's string.Format wants, and it is unreadable:
/// nothing in "{0} {1}x{3} {2}" says which number is the episode and which is
/// its title, and the indices don't even run in order. So the user writes names,
/// and this turns them into indices immediately before formatting.
///
/// Numeric formats are still accepted, unchanged. Anyone who already has one
/// saved keeps their naming exactly as it was, and a format mixing the two works
/// too - the translation only touches the names.
/// </summary>
public static class NameTokens
{
    /// <summary>Placeholders for an episode filename, and the argument each maps to.</summary>
    private static readonly Dictionary<string, int> Episode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = 0, ["series"] = 0, ["show"] = 0, ["showtitle"] = 0, ["seriesname"] = 0,
        ["season"] = 1,
        ["name"] = 2, ["episodetitle"] = 2, ["episodename"] = 2, ["eptitle"] = 2,
        ["episode"] = 3, ["ep"] = 3, ["episodenumber"] = 3,
    };

    /// <summary>Placeholders for a movie or show folder: just a title and a year.</summary>
    private static readonly Dictionary<string, int> TitleYear = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = 0, ["name"] = 0, ["movie"] = 0, ["series"] = 0, ["show"] = 0, ["showtitle"] = 0,
        ["year"] = 1,
    };

    /// <summary>The placeholders an episode format understands, for showing the user.</summary>
    public static string[] EpisodeNames => ["{title}", "{season}", "{episode}", "{name}"];

    /// <summary>The placeholders a movie or folder format understands.</summary>
    public static string[] TitleYearNames => ["{title}", "{year}"];

    public static string ForEpisode(string format) => Translate(format, Episode);

    public static string ForTitleYear(string format) => Translate(format, TitleYear);

    /// <summary>
    /// Replace every recognised name with its index. A name we don't know is left
    /// exactly as written, so string.Format rejects it and the caller falls back
    /// to the shipped default - which is what already happens for a typo like
    /// "{5}", and means one wrong word never silently drops a field.
    /// </summary>
    private static string Translate(string format, Dictionary<string, int> map)
    {
        if (string.IsNullOrEmpty(format) || !format.Contains('{')) return format;

        return Pattern.Replace(format, m =>
            map.TryGetValue(m.Groups[1].Value, out var index)
                ? "{" + index + "}"
                : m.Value);
    }

    // Letters only, so "{0}" and any alignment or format specifier are left alone.
    private static readonly Regex Pattern = new(
        @"\{([A-Za-z]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
}
