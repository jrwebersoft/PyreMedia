using System.Text.RegularExpressions;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Naming;

/// <summary>
/// Builds output filenames and cleans search terms.
/// The format string keeps the original placeholder contract so existing
/// user formats carry over verbatim:
///   {0} series name   {1} season (padded)   {2} episode name   {3} episode (padded)
/// </summary>
public static class NameFormatter
{
    /// <summary>
    /// Strip a real file extension, leaving dotted names intact.
    ///
    /// Path.GetFileNameWithoutExtension is wrong here: on the folder name
    /// "The.Cat.and.the.Dragon.2026" it treats ".2026" as the extension and
    /// throws the year away. Only a short all-letter suffix counts.
    /// </summary>
    /// <summary>
    /// Extensions we'll actually strip. Matching any short letter suffix was too
    /// eager - it turned "The.Handmaid's.Tale" into "The.Handmaid's" by treating
    /// ".Tale" as an extension. Real titles end in real words far more often than
    /// they end in a file extension, so only known ones count.
    /// </summary>
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mpg", ".mpeg", ".mov", ".wmv", ".flv",
        ".divx", ".xvid", ".ogm", ".ts", ".m2ts", ".vob", ".iso", ".webm", ".rmvb",
        ".dvr-ms", ".srt", ".sub", ".idx", ".ass", ".ssa", ".sup", ".nfo", ".txt"
    };

    public static string StripExtension(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        var dot = name.LastIndexOf('.');
        if (dot <= 0) return name;

        var suffix = name[dot..];
        return KnownExtensions.Contains(suffix) ? name[..dot] : name;
    }

    /// <summary>Characters Windows forbids in a filename, plus the ones the old app stripped.</summary>
    private static readonly char[] Invalid =
        [.. Path.GetInvalidFileNameChars(), ':', '?', '*', '"', '<', '>', '|'];

    public static string BuildEpisodeFileName(
        string format,
        string seriesName,
        Episode episode,
        int seasonPad,
        int episodePad,
        char replacement)
        => BuildEpisodeFileName(
            format, seriesName,
            episode.SeasonNumber.ToString().PadLeft(seasonPad, '0'),
            episode.Number.ToString().PadLeft(episodePad, '0'),
            episode.Name,
            replacement);

    /// <summary>
    /// Explicit-text overload, so a multi-episode file can pass "01-02" as the
    /// episode and a joined title. Substituting into the finished string instead
    /// would hit the season number first when both are "01".
    /// </summary>
    public static string BuildEpisodeFileName(
        string format,
        string seriesName,
        string seasonText,
        string episodeText,
        string episodeName,
        char replacement)
    {
        string name;

        try
        {
            name = string.Format(
                NameTokens.ForEpisode(format), seriesName, seasonText, episodeName, episodeText);
        }
        catch (FormatException)
        {
            // The format is user-editable, so "{5}" or a stray brace is a typo
            // rather than a bug - and throwing here aborts the whole plan with
            // "unexpected error" rather than saying what's wrong. Fall back to
            // the shipped default so the preview still builds and the mistake
            // is visible as a name that isn't what they asked for.
            name = string.Format(
                NameTokens.ForEpisode(DefaultTvFileFormat),
                seriesName, seasonText, episodeName, episodeText);
        }

        var clean = Sanitize(name, replacement);

        // A title of only dots or spaces sanitizes away to nothing, which would
        // produce a hidden, nameless ".mkv". The numbers are always safe, so
        // fall back to those rather than write a file no one can find.
        if (clean.Length == 0)
            clean = Sanitize($"{seriesName} {seasonText}x{episodeText}", replacement);

        if (clean.Length == 0)
            clean = $"{seasonText}x{episodeText}";

        return clean;
    }

    /// <summary>The shipped format, used when a custom one won't format.</summary>
    /// <summary>
    /// Named rather than numbered: "{0} {1}x{3} {2}" said nothing about which
    /// number was the episode, and the indices didn't even run in order. The
    /// numeric form is still accepted for anything already saved.
    /// </summary>
    public const string DefaultTvFileFormat = "{title} {season}x{episode} {name}";

    /// <summary>
    /// Make a string safe as a filename. Invalid characters become
    /// <paramref name="replacement"/>; runs collapse so we don't emit "--".
    /// </summary>
    public static string Sanitize(string value, char replacement)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // "Star Trek: Strange New Worlds" should read "Star Trek - Strange New
        // Worlds", not "Star Trek- Strange New Worlds".
        value = value.Replace(": ", " - ");

        var chars = value.Select(c => Invalid.Contains(c) ? replacement : c).ToArray();
        var result = new string(chars);

        var dup = new string(replacement, 2);
        while (result.Contains(dup))
            result = result.Replace(dup, replacement.ToString());

        result = result.Trim().Trim(replacement).Trim();

        // Windows silently drops a trailing dot rather than refusing it, so
        // "The Show." becomes "The Show" on disk - and the file then never
        // matches the name we planned, which means the same rename is proposed
        // again on every scan. Strip them ourselves so the two agree.
        result = result.TrimEnd('.', ' ');

        if (result.Length == 0)
            return result;

        // MS-DOS device names are reserved as the whole stem and cannot be
        // created at all: a folder named "CON" or a file "NUL.mkv" simply fails.
        if (IsReservedDeviceName(result))
            result += replacement;

        return result;
    }

    /// <summary>
    /// Names Windows will not accept as a file or folder stem, whatever the
    /// extension. Inherited from MS-DOS and still enforced.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// True when this would be a reserved device name. The check is on the stem
    /// before any extension, since "CON.mkv" is refused just as "CON" is.
    /// </summary>
    public static bool IsReservedDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var stem = name;
        var dot = stem.IndexOf('.');
        if (dot > 0) stem = stem[..dot];

        return ReservedNames.Contains(stem.Trim());
    }

    /// <summary>
    /// Show folder name from the canonical series name and year.
    /// Falls back to just the name when the year is unknown, so we never
    /// produce "Firefly ()".
    /// </summary>
    public static string BuildShowFolderName(string format, string seriesName, string year, char replacement)
    {
        string name;
        try
        {
            name = string.IsNullOrWhiteSpace(year)
                ? seriesName
                : string.Format(NameTokens.ForTitleYear(format), seriesName, year);
        }
        catch (FormatException)
        {
            name = seriesName;
        }

        var clean = Sanitize(name, replacement);

        // Never return an empty folder name - Path.Combine would silently give
        // back the parent, and the rename would target the wrong directory.
        return clean.Length > 0 ? clean : "Unnamed";
    }

    public static string SeasonFolderName(int season, string seasonWord, string specialsWord, int pad)
        => season == 0 ? specialsWord : $"{seasonWord} {season.ToString().PadLeft(pad, '0')}";

    /// <summary>
    /// Show name from a release name.
    ///
    /// Everything before the season/episode marker is the title - far more
    /// reliable than blacklisting release tokens, which never ends (MAX, AMZN,
    /// DDP5.1, the file extension...). Falls back to token filtering when
    /// there's no marker to cut at.
    /// </summary>
    /// <summary>
    /// Show title and year from a release name. The year is pulled out rather
    /// than left in the title - searching "The Cat and the Dragon (2026)"
    /// returns nothing, while title + year filter returns the show.
    /// </summary>
    public static (string Title, string? Year) ParseTvName(string rawName, string? filterPattern)
    {
        var title = CleanTvSearchTerm(rawName, filterPattern);
        if (string.IsNullOrWhiteSpace(title)) return (title, null);

        string? year = null;

        // A run of years is one fact, not two candidates.
        //
        // "Stranger Things (2016-2025) Complete Series" carries the years it
        // ran between. Read one at a time, the loop below took the last as the
        // year and left the first glued to the name - a show called "Stranger
        // Things 2016", first aired 2025, which no provider has ever heard of.
        // The first is when it started, which is what a series is indexed
        // under; the second is when it stopped, which is not part of its name.
        var range = Regex.Match(
            title, @"(?<!\d)(?<from>(?:19|20)\d{2})\s*[-–—]\s*(?<to>(?:19|20)\d{2})(?!\d)");

        if (range.Success)
        {
            var upto = title[..range.Index].Trim(' ', '-', '(', '[', '.', '_');

            if (upto.Length > 0)
            {
                title = upto;
                year = range.Groups["from"].Value;
            }
        }

        // Last year that still leaves a title, so "1923 (2022)" keeps "1923".
        foreach (Match m in year is not null
                     ? []
                     : Regex.Matches(title, @"(?<!\d)(?<y>(?:19|20)\d{2})(?!\d)").Reverse())
        {
            var before = title[..m.Index].Trim(' ', '-', '(', '[', '.', '_');
            if (before.Length == 0) continue;

            year = m.Groups["y"].Value;
            title = before;
            break;
        }

        title = Regex.Replace(title, @"[\[\](){}]", " ");
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-', '.');

        return (title, year);
    }

    /// <summary>
    /// Where an episode number sits in a name, and where a title stops.
    ///
    /// "episodio 3" and "episode 3" are in here as markers, not as titles: a
    /// release that leads with SxxExx usually repeats the number in words
    /// straight after the show's name, and that repeat is the only thing saying
    /// where the name ends.
    /// </summary>
    private const string EpisodeMarker =
        @"(?:^|[\s._-])(?:s\d{1,2}[\s._-]*e\d{1,3}|\d{1,2}x\d{1,3}|season[\s._-]*\d{1,2}"
        + @"|episod(?:e|io|es|i)[\s._-]*\d{1,3}|s\d{1,2}(?![\d\w]))";

    public static string CleanTvSearchTerm(string rawName, string? filterPattern)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return string.Empty;

        // Drop an extension first - loose files arrive with one attached.
        var stem = StripSiteTag(StripExtension(rawName));
        if (string.IsNullOrWhiteSpace(stem)) stem = rawName;

        var marker = Regex.Match(stem, EpisodeMarker, RegexOptions.IgnoreCase);

        string head;

        if (!marker.Success)
        {
            head = stem;
        }
        else if (marker.Index > 0)
        {
            head = stem[..marker.Index];
        }
        else
        {
            // The marker is the first thing in the name, so the title is what
            // comes after it rather than before.
            //
            // The pattern used to require a separator in front, which meant a
            // marker at position 0 never matched at all and the entire filename
            // became the title: "S01E03 The Ghost in the Shell Episodio 03 Junk
            // Jungle Ii + Megatech Machine (2026) WEBRip ..." was read as the
            // name of a show, so it could never be recognised as the same
            // series as the folder of it sitting beside it.
            //
            // Cut again at the next marker, which is where the episode's own
            // title begins.
            var rest = stem[marker.Length..];
            var next = Regex.Match(rest, EpisodeMarker, RegexOptions.IgnoreCase);

            head = next.Success && next.Index > 0 ? rest[..next.Index] : rest;
        }

        var cleaned = Regex.Replace(head, @"[\s._]+", " ").Trim(' ', '-', '.');

        // If cutting at the marker left nothing useful, fall back to filtering.
        if (cleaned.Length < 2)
            return CleanSearchTerm(stem, filterPattern);

        return cleaned;
    }

    /// <summary>
    /// Domains that only a tracker puts in a filename.
    ///
    /// A whitelist rather than "any two to four letters", because the dotted
    /// form is also how titles are written once separators are stripped: an
    /// open rule turns "Dr. Who" into "Who". Even so it is only applied at the
    /// front of a name or inside brackets - "Stuart.Fails.to.Save.the.Universe"
    /// contains "Fails.to", and a rule that fired anywhere would eat it.
    /// </summary>
    private const string SiteTlds = "org|com|net|to|io|tv|me|cc|info|xyz|se|nu|eu|ru|is|ag|sx|pw|uk|co";

    /// <summary>
    /// Remove a tracker's own name from the front of a release, or a bracketed
    /// one anywhere in it.
    ///
    /// "www.UIndex.org    -    Lanterns S01E01 Pilot" parsed as a show called
    /// "www UIndex org - Lanterns", which meant it could never be recognised as
    /// the same series as the "Lanterns" folder beside it, so the two were never
    /// offered as one entry and the second folder kept its release name.
    /// </summary>
    public static string StripSiteTag(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        // [EZTVx.to], [rarbg] with a domain in it - brackets make it unambiguous.
        var cut = Regex.Replace(name, @"\[[^\]]*\.(?:" + SiteTlds + @")[^\]]*\]", " ",
                                RegexOptions.IgnoreCase);

        // www.anything.tld at the front, with whatever separator follows it.
        cut = Regex.Replace(cut, @"^\s*www\.[\w-]+\.[a-z]{2,6}\b[\s._-]*", "",
                            RegexOptions.IgnoreCase);

        // A bare domain at the front, but only where a dash separates it from
        // the title - that dash is what makes it a prefix rather than a word.
        cut = Regex.Replace(cut, @"^\s*[\w-]{2,}\.(?:" + SiteTlds + @")\s*[-–]+\s*", "",
                            RegexOptions.IgnoreCase);

        return string.IsNullOrWhiteSpace(cut) ? name : cut;
    }

    /// <summary>
    /// Turn a messy folder name into something worth searching for, by applying
    /// the user's SearchTermFilters regex. Falls back to the raw name if the
    /// filter would leave nothing behind.
    /// </summary>
    public static string CleanSearchTerm(string rawName, string? filterPattern)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return string.Empty;

        var term = StripSiteTag(rawName);

        if (!string.IsNullOrWhiteSpace(filterPattern))
        {
            try
            {
                // With a timeout, because this pattern is the user's to edit and
                // some perfectly innocent-looking ones backtrack catastrophically.
                // Without one a bad pattern doesn't throw - it simply never
                // returns, and the scan hangs on a file with no way to tell why.
                term = Regex.Replace(term, filterPattern, " ", RegexOptions.IgnoreCase,
                                     TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                // A bad user-supplied pattern must not break scanning.
                term = rawName;
            }
            catch (RegexMatchTimeoutException)
            {
                term = rawName;
            }
        }

        term = Regex.Replace(term, @"[\s._]+", " ").Trim();

        return string.IsNullOrWhiteSpace(term) ? rawName.Trim() : term;
    }
}
