using System.Text.RegularExpressions;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Naming;

/// <summary>
/// Extracts season/episode numbers from a filename.
///
/// Ported from the original GetID.GetSeasonAndEpisodeIDFromFile, with one
/// important correction: the original ran a bare <c>(\d{1,2})(\d{2})</c> pattern
/// against the raw filename, so "Show.1080p.mkv" matched as season 10 episode 80
/// and "Show.2019.x264" could match off the year. Junk tokens are now stripped
/// before the loose numeric fallbacks run, and those fallbacks are only tried
/// after every explicit pattern has failed.
/// </summary>
public static partial class EpisodeMatcher
{
    // Explicit, unambiguous forms - tried first, in order.
    // Covers S01E05, S1.EP3, S1 Ep 3, s03e11, plus double/triple episodes:
    // S01E01E02, S01E01-E02, S01E01-02. Without the trailing group the second
    // episode was silently dropped and the file mislabelled as just the first.
    // The continuation MUST be introduced by 'e' or '-', never a bare number.
    // Allowing a bare number let "S01E01E02.1080p" pick up ".1080" as a third
    // episode, which then failed the sanity check and lost the range entirely.
    [GeneratedRegex(
        @"s(?<se>\d{1,2})[\s._-]*e(?:p|pisode)?[\s._-]*(?<ep>\d{1,3})" +
        @"(?:[\s._-]*(?:-[\s._-]*e?|e)(?<ep2>\d{1,3}))*",
        RegexOptions.IgnoreCase)]
    private static partial Regex SxxExx();

    [GeneratedRegex(@"(?<se>\d{1,2})x(?<ep>\d{1,3})(?:[\s._-]*(?:x|-)(?<ep2>\d{1,3}))*", RegexOptions.IgnoreCase)]
    private static partial Regex NxNN();

    [GeneratedRegex(@"season[\s._-]*(?<se>\d{1,2})[\s._-]*episode[\s._-]*(?<ep>\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeWords();

    // Loose fallbacks - only after junk removal, and only if nothing above hit.
    [GeneratedRegex(@"^(?<se>\d{1,2})(?<ep>\d{2})(?!\d)")]
    private static partial Regex LeadingSEE();

    [GeneratedRegex(@"(?<!\d)(?<se>\d{1,2})(?<ep>\d{2})(?!\d)")]
    private static partial Regex AnywhereSEE();

    /// <summary>
    /// A leading number is an episode index only when it is shaped like one:
    /// zero-padded ("01 Pilot"), followed by a separator ("3 - Title", "137 -
    /// Title"), or a 1-2 digit number standing alone ("7"). A number followed
    /// by a word is part of the title - "47 Ronin", "12 Monkeys", "13 Reasons
    /// Why" are not episode 47, 12, 13. Three digits standing entirely alone is
    /// a title too: "300" is the film, not episode 300, and real absolute
    /// numbering always carries a show name in front of it.
    /// </summary>
    [GeneratedRegex(
        @"^(?:(?<ep>0\d{1,2})(?!\d)" +
        @"|(?<ep>\d{1,3})(?!\d)\s*[-_.]" +
        @"|(?<ep>\d{1,2})(?!\d)\s*$)")]
    private static partial Regex LeadingEpisodeOnly();

    /// <summary>
    /// A bare year in parentheses at the end is a film/series title convention,
    /// never an episode filename - a real episode would have matched one of the
    /// explicit patterns above. Blocks the loose guesses so "47 Ronin (2013)"
    /// stops being read as episode 47.
    /// </summary>
    [GeneratedRegex(@"\((?:19|20)\d{2}\)\s*$")]
    private static partial Regex TrailingYearInParens();

    /// <summary>
    /// Episode number with no season: "Episode 3", "Ep 3", "e04". Common inside
    /// a Season folder, where the folder supplies the season.
    /// </summary>
    [GeneratedRegex(@"\be(?:p|pisode)?[\s._-]*(?<ep>\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWordOnly();

    /// <summary>
    /// Tokens that produce false episode matches. Removed before the loose
    /// numeric fallbacks run. Deliberately does NOT touch the explicit patterns.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:480p|540p|720p|1080p|1440p|2160p|4k|8k|x26[45]|h\.?26[45]|hevc|xvid|divx|" +
        @"aac|ac3|dts(?:-hd)?|truehd|atmos|ddp?5[\s._-]?1|5[\s._-]?1|7[\s._-]?1|" +
        @"bluray|blu-ray|brrip|bdrip|dvdrip|dvd|webrip|web-?dl|hdtv|pdtv|hdrip|remux|" +
        @"proper|repack|internal|extended|uncut|unrated|limited|hdr10?|dolby(?:vision)?|" +
        @"10bit|8bit|multi|dual|subbed|dubbed)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex JunkTokens();

    // A 4-digit year in 1900-2099 - never an episode number.
    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?!\d)")]
    private static partial Regex YearToken();

    /// <summary>
    /// Parse season/episode out of a filename. Returns null when nothing
    /// recognisable is found - the caller should surface that rather than guess.
    /// </summary>
    public static EpisodeRef? Parse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var stem = Path.GetFileNameWithoutExtension(fileName);

        foreach (var rx in new[] { SxxExx(), SeasonEpisodeWords(), NxNN() })
        {
            var m = rx.Match(stem);
            if (m.Success
                && int.TryParse(m.Groups["se"].Value, out var se)
                && int.TryParse(m.Groups["ep"].Value, out var ep))
            {
                // Captures repeat for E01E02E03; the last one is the range end.
                int? last = null;
                var g2 = m.Groups["ep2"];
                if (g2.Success && g2.Captures.Count > 0
                    && int.TryParse(g2.Captures[^1].Value, out var e2)
                    && e2 > ep && e2 - ep < 20)   // a huge span means we misread something
                {
                    last = e2;
                }

                return new EpisodeRef(se, ep) { LastEpisode = last };
            }
        }

        // Everything below is a guess. "Title (2013)" is a film convention, so
        // refuse to guess at all rather than turn a numeric title into an
        // episode number. Explicit patterns already had their chance above.
        if (TrailingYearInParens().IsMatch(stem))
            return null;

        // Strip the things that masquerade as episode numbers before guessing.
        var cleaned = YearToken().Replace(JunkTokens().Replace(stem, " "), " ");

        foreach (var rx in new[] { LeadingSEE(), AnywhereSEE() })
        {
            var m = rx.Match(cleaned);
            // Episode 0 doesn't exist (season 0 does - that's Specials). A split
            // that produces one means we misread a title: "300" is not season 3
            // episode 00.
            if (m.Success
                && int.TryParse(m.Groups["se"].Value, out var se)
                && int.TryParse(m.Groups["ep"].Value, out var ep)
                && ep > 0)
            {
                // "Show - 137" splits to s1e37 under these rules, but for anime
                // and long-running shows it's far more likely to be absolute
                // episode 137. Carry the raw number so the planner can decide
                // against real metadata instead of us guessing here.
                int? absolute = null;
                var whole = m.Groups["se"].Value + m.Groups["ep"].Value;
                if (whole.Length >= 3 && int.TryParse(whole, out var abs))
                    absolute = abs;

                return new EpisodeRef(se, ep) { AbsoluteCandidate = absolute };
            }
        }

        var only = LeadingEpisodeOnly().Match(cleaned.TrimStart());
        if (only.Success && int.TryParse(only.Groups["ep"].Value, out var epOnly))
            return new EpisodeRef(null, epOnly);

        // "Episode 3", "Ep 3", "e04" - no season, so the folder has to supply it.
        var word = EpisodeWordOnly().Match(cleaned);
        if (word.Success && int.TryParse(word.Groups["ep"].Value, out var epWord))
            return new EpisodeRef(null, epWord);

        return null;
    }

    /// <summary>Season number from a folder name like "Season 03" / "season.3".</summary>
    public static int? ParseSeasonFolder(string folderName, string seasonFolderWord)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return null;

        if (folderName.Trim().Equals("Specials", StringComparison.OrdinalIgnoreCase))
            return 0;

        var rx = new Regex(Regex.Escape(seasonFolderWord) + @"[\s._-]*(\d{1,3})",
                           RegexOptions.IgnoreCase);
        var m = rx.Match(folderName);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }
}
