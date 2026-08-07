using System.Text;
using System.Text.RegularExpressions;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Naming;

/// <summary>
/// Matches files to episodes by their <em>title</em> rather than their number.
///
/// Some rips are numbered wrongly from end to end - the numbers in the
/// filenames bear no relation to the numbering the metadata uses - but the
/// episode title is usually still in the filename somewhere. Matching on that
/// recovers the correct numbering without hand-editing every file.
/// </summary>
public static partial class TitleMatcher
{
    /// <summary>Everything that is packaging rather than title.</summary>
    [GeneratedRegex(
        @"\b(?:480p|540p|720p|1080p|1440p|2160p|4k|8k|x26[45]|h\.?26[45]|hevc|xvid|divx|" +
        @"aac|ac3|eac3|dts(?:-hd)?(?:[\s._-]?ma)?|truehd|atmos|flac|mp3|opus|" +
        @"ddp?5[\s._-]?1|5[\s._-]?1|7[\s._-]?1|2[\s._-]?0|" +
        @"bluray|blu-ray|brrip|bdrip|bdrmux|dvdrip|dvd[59]?|webrip|web-?dl|web|hdtv|pdtv|hdrip|remux|" +
        @"proper|repack|internal|extended|uncut|unrated|limited|remastered|hdr10?\+?|dolby(?:vision)?|dv|" +
        @"10bit|8bit|multi|dual|subbed|dubbed|complete|season|series|disc\d*|" +
        @"amzn|nf|hmax|max|dsnp|atvp|hulu|pcok|stan|itunes)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Packaging();

    /// <summary>Season/episode markers in any of the forms we recognise.</summary>
    [GeneratedRegex(
        @"(?:\bs\d{1,2}[\s._-]*e\d{1,3}(?:[\s._-]*(?:-[\s._-]*e?|e)\d{1,3})*\b" +
        @"|\b\d{1,2}x\d{1,3}\b" +
        @"|\bseason[\s._-]*\d{1,2}\b|\bepisode[\s._-]*\d{1,3}\b|\bep?[\s._-]*\d{1,3}\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeMarker();

    /// <summary>
    /// A release group suffix: "[GROUP]" or "-GROUP" at the very end. The
    /// hyphen form requires a separator before it, so a hyphenated last word in
    /// a real title ("Part-Two") isn't mistaken for one.
    /// </summary>
    [GeneratedRegex(@"(?:\[[A-Za-z0-9]{2,12}\]|(?<=[\s._])-\s*[A-Za-z0-9]{2,12})\s*$")]
    private static partial Regex ReleaseGroup();

    [GeneratedRegex(@"[^a-z0-9 ]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Noise words that carry no matching signal.</summary>
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "in", "on", "at", "to", "for",
        "part", "pt", "vol", "episode", "ep"
    };

    /// <summary>
    /// Best guess at the episode title inside a filename: strip the extension,
    /// the show name, the season/episode marker and all the release packaging,
    /// and whatever survives is usually the title.
    /// </summary>
    public static string ExtractTitle(string fileName, string? showName)
    {
        var s = Path.GetFileNameWithoutExtension(fileName);

        // The show name is repeated in almost every filename and would otherwise
        // dominate the score, making every episode look equally good.
        //
        // Anchored to the start, because that's where it sits as a prefix.
        // Removing it everywhere also ate episode titles that repeat it -
        // "The Cat and the Dragon 01x01 The Cat and the Dragon" reduced to
        // nothing at all.
        if (!string.IsNullOrWhiteSpace(showName))
        {
            var loose = Regex.Escape(showName).Replace("\\ ", "[\\s._-]+");
            s = Regex.Replace(s, @"^[\s._-]*" + loose, " ", RegexOptions.IgnoreCase);
        }

        s = EpisodeMarker().Replace(s, " ");
        s = Packaging().Replace(s, " ");
        s = ReleaseGroup().Replace(s, " ");

        // Separators only become spaces after the patterns above have run -
        // they rely on dots and underscores being intact.
        s = s.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

        var title = Whitespace().Replace(s, " ").Trim();

        // Stripping can legitimately consume the whole name. Retrying without
        // the show-name removal beats showing the user a blank cell.
        if (title.Length == 0 && !string.IsNullOrWhiteSpace(showName))
            return ExtractTitle(fileName, null);

        return title;
    }

    /// <summary>Lower-cased, punctuation-free, stop-words removed.</summary>
    private static List<string> Tokens(string value)
    {
        var flat = NonAlphanumeric().Replace(value.ToLowerInvariant(), " ");

        return [.. Whitespace().Split(flat)
            .Where(t => t.Length > 0 && !Stop.Contains(t))];
    }

    /// <summary>
    /// 0-1 similarity. Token overlap is the backbone - it survives reordering
    /// and missing words - with a bonus when one string contains the other,
    /// which catches "Pilot" against "The Pilot".
    /// </summary>
    public static double Score(string fileTitle, string episodeTitle)
    {
        if (string.IsNullOrWhiteSpace(fileTitle) || string.IsNullOrWhiteSpace(episodeTitle))
            return 0;

        var a = Tokens(fileTitle);
        var b = Tokens(episodeTitle);
        if (a.Count == 0 || b.Count == 0) return 0;

        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        var setB = new HashSet<string>(b, StringComparer.Ordinal);

        var overlap = setA.Count(t => setB.Contains(t));

        // Divide by the smaller set: a filename carrying extra words shouldn't
        // be punished when it contains the whole episode title.
        var baseScore = (double)overlap / Math.Min(setA.Count, setB.Count);

        // Penalise a short match against a long title - one shared word out of
        // six is coincidence, not a match.
        var coverage = (double)overlap / Math.Max(setA.Count, setB.Count);
        var score = baseScore * 0.7 + coverage * 0.3;

        var flatA = string.Concat(a);
        var flatB = string.Concat(b);
        if (flatA.Contains(flatB, StringComparison.Ordinal) ||
            flatB.Contains(flatA, StringComparison.Ordinal))
        {
            score = Math.Max(score, 0.9);
        }

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>One file's best candidate.</summary>
    public sealed class TitleMatch
    {
        public required string File { get; init; }
        public required string ExtractedTitle { get; init; }
        public Episode? Episode { get; set; }
        public double Confidence { get; set; }

        /// <summary>Runner-up, so the UI can say when it was a close call.</summary>
        public Episode? Alternative { get; set; }
        public double AlternativeConfidence { get; set; }
    }

    /// <summary>
    /// Assign each file its best-matching episode, strongest match first, with
    /// no episode used twice. Greedy rather than optimal: a confident match
    /// should never be displaced by a weak one competing for the same episode.
    /// </summary>
    public static List<TitleMatch> MatchAll(
        IEnumerable<string> files, IReadOnlyList<Episode> episodes, string? showName)
    {
        var results = files
            .Select(f => new TitleMatch
            {
                File = f,
                ExtractedTitle = ExtractTitle(Path.GetFileName(f), showName)
            })
            .ToList();

        // Score every pair once.
        var scored = new List<(TitleMatch Match, Episode Episode, double Score)>();
        foreach (var r in results)
            foreach (var e in episodes)
            {
                var s = Score(r.ExtractedTitle, e.Name);
                if (s > 0) scored.Add((r, e, s));
            }

        var takenEpisodes = new HashSet<Episode>();
        var takenFiles = new HashSet<TitleMatch>();

        foreach (var (match, episode, score) in scored.OrderByDescending(x => x.Score))
        {
            if (takenFiles.Contains(match) || takenEpisodes.Contains(episode))
            {
                // Not the winner, but worth showing as the runner-up.
                if (takenFiles.Contains(match) && match.Alternative is null
                    && !ReferenceEquals(match.Episode, episode))
                {
                    match.Alternative = episode;
                    match.AlternativeConfidence = score;
                }
                continue;
            }

            match.Episode = episode;
            match.Confidence = score;
            takenFiles.Add(match);
            takenEpisodes.Add(episode);
        }

        return results;
    }
}
