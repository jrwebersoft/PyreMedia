using System.Text.RegularExpressions;

namespace PyreMedia.Core.Books;

/// <summary>What a comic's filename says about it, before anything is looked up.</summary>
public sealed record ComicRef
{
    public required string Series { get; init; }

    /// <summary>Issue number as written, so "007" and "7" are not confused, and half-issues survive.</summary>
    public string? Issue { get; init; }

    /// <summary>Volume, where the name carries one - "v2", "Vol. 3".</summary>
    public int? Volume { get; init; }

    /// <summary>
    /// The year the series began - what a series folder should be called.
    ///
    /// Not the same as the date on the cover, and conflating them is how a run
    /// gets scattered: fifty issues of a 1992 series carry cover dates from 1992
    /// to 1998, so filing by cover date makes six folders where there is one
    /// series.
    /// </summary>
    public string? Year { get; init; }

    /// <summary>The date on this issue's cover, where the name carries one.</summary>
    public string? CoverYear { get; init; }

    /// <summary>How many issues the run has, from "(of 6)".</summary>
    public int? Of { get; init; }

    /// <summary>Annual, one-shot, trade paperback and so on, where it says.</summary>
    public ComicKind Kind { get; init; } = ComicKind.Issue;

    /// <summary>
    /// The issue's own title, which a filename almost never carries and the
    /// file's own metadata often does. The nearest thing a comic has to an
    /// episode name.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>Who published it, where something has said so.</summary>
    public string? Publisher { get; init; }

    /// <summary>
    /// This exact issue at whichever source named it - "cv:114621". Present on
    /// most scanned comics, and worth more than any search: it is the answer
    /// rather than a candidate.
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// True when this was read out of the comic rather than guessed from its
    /// name. The same flag <see cref="BookRef"/> carries, and for the same
    /// reason: a guess and a fact should not look alike.
    /// </summary>
    public bool FromInside { get; init; }

    /// <summary>The issue as a number, for sorting and padding. Null for "1/2" and the like.</summary>
    public int? IssueNumber =>
        Issue is not null && int.TryParse(Issue.TrimStart('0') is "" ? "0" : Issue.TrimStart('0'), out var n)
            ? n
            : null;
}

public enum ComicKind { Issue, Annual, OneShot, TradePaperback, Special }

/// <summary>
/// Reading a comic's filename.
///
/// The shapes are as varied as television's ever were, and the same approach
/// works: recognise the parts that are actually written down, and refuse to
/// invent the rest.
/// <code>
///   Saga 001 (2012) (Digital) (Zone-Empire).cbz
///   Batman v2 #404 (1987).cbz
///   The Amazing Spider-Man #050 (of 12).cbz
///   Uncanny X-Men Annual #1.cbz
///   Saga Vol. 01 (2012).cbz
/// </code>
/// The hard part is that a comic filename is mostly parentheses, and only some
/// of them mean anything. A year does; "(Digital)", "(Zone-Empire)", "(F)" and
/// "(c2c)" are the scanner's signature and say nothing about the comic. Getting
/// that wrong turns a release group into a series title.
/// </summary>
public static class ComicMatcher
{
    /// <summary>
    /// Extensions worth reading.
    ///
    /// .cbz is a zip and .cbr is a RAR, and both are opened by what is actually
    /// inside them rather than by the name - misnamed files are common enough
    /// that a header is the only reliable answer. .cb7 and .cbt are 7-Zip and
    /// tar; they are listed so such a file is at least seen and filed by its
    /// name, but its pages cannot be shown.
    /// </summary>
    public static readonly string[] Extensions = [".cbz", ".cbr", ".cb7", ".cbt"];

    /// <summary>
    /// Parenthesised things that are never part of a title.
    ///
    /// Deliberately a list of what scanners write rather than a rule like "drop
    /// every bracket": <em>Batman (2016)</em> needs its year, and
    /// <em>Marvel Team-Up (1972)</em> is not improved by losing it.
    /// </summary>
    private static readonly Regex ScannerNoise = new(
        @"\((?:digital|webrip|scan(?:lation)?|c2c|f|fiche|noads|no\s*ads|empire|minutemen|"
        + @"zone-empire|dcp|the\s+last\s+kryptonian|glorith|phillip[s]?|bchry|"
        + @"covers?|re-?edit|repack|inc\.?|incomplete|\d+\s*of\s*\d+\s*covers?)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Anything in square brackets. Scanners use those and titles do not.</summary>
    private static readonly Regex Bracketed = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>
    /// A year in brackets, with or without the month a cover date carries.
    ///
    /// "(1992)" and "(June, 1993)" both mean the same thing to a reader and only
    /// the first was being read - a real collection on hand writes every one of
    /// its fifty issues the second way, so every year was being thrown out with
    /// the noise.
    /// </summary>
    private static readonly Regex Year = new(
        @"\(\s*(?:[A-Za-z]+\.?,?\s*)?((?:19|20)\d{2})\s*\)", RegexOptions.Compiled);

    /// <summary>A dot acting as a separator - one that is not inside a number.</summary>
    private static readonly Regex SeparatorDots = new(@"(?<!\d)\.|\.(?!\d)", RegexOptions.Compiled);

    /// <summary>
    /// An initialism: two or more single letters each followed by a dot, and
    /// whatever letters trail it. "C.A.T.s", "S.H.I.E.L.D.", "A.X.E."
    ///
    /// Protected before separators are flattened, because a rule that turns
    /// every dot into a space turns WildC.A.T.s into "WildC A T s" - which is
    /// what a real collection of fifty issues was being renamed to.
    /// </summary>
    private static readonly Regex Initialism = new(
        @"(?:[A-Za-z]\.){2,}[A-Za-z]*", RegexOptions.Compiled);

    /// <summary>A character no filename holds, to stand in while dots are flattened.</summary>
    private const char Shield = '';

    private static readonly Regex OfCount = new(@"\(\s*of\s+(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A volume marker. Up to four digits, because a great many taggers write
    /// the year the run began as its volume - "Vol.1992" - and three digits
    /// left that stranded in the series name.
    /// </summary>
    private static readonly Regex VolumeMark = new(
        @"(?:^|\s|\.)(?:v|vol\.?|volume)\s*\.?\s*(\d{1,4})(?=\s|$|#|\.)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The issue number: a # and digits, or digits standing alone at the end.
    ///
    /// Half issues and decimals are real - #0, #1/2, #13.1 all exist - so the
    /// number is kept as written rather than parsed to an int and rounded into
    /// something else.
    /// </summary>
    private static readonly Regex Hashed = new(
        @"#\s*(\d{1,4}(?:\.\d+)?(?:/\d+)?)", RegexOptions.Compiled);

    private static readonly Regex Trailing = new(@"(?:^|\s)(\d{1,4}(?:\.\d+)?)\s*$", RegexOptions.Compiled);

    public static bool IsComic(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Read a filename, or null when there is not even a series in it.</summary>
    public static ComicRef? Parse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var name = Path.GetFileNameWithoutExtension(fileName);

        // Year first, before the noise is stripped - it is the one parenthesis
        // worth keeping and it is easiest to find while everything is intact.
        var year = Year.Match(name) is { Success: true } y ? y.Groups[1].Value : null;

        var of = OfCount.Match(name) is { Success: true } o ? int.Parse(o.Groups[1].Value) : (int?)null;

        var work = OfCount.Replace(name, " ");
        work = Year.Replace(work, " ");
        work = ScannerNoise.Replace(work, " ");
        work = Bracketed.Replace(work, " ");

        // Whatever is left in brackets is unknown rather than known-noise, so it
        // goes too - but only after the known things have been taken, so a year
        // is never lost to it.
        work = Regex.Replace(work, @"\([^)]*\)", " ");

        // Scene separators become spaces here rather than at the end, because the
        // issue is found by looking for a number with space around it and
        // "Fantastic.Four.001" has no space anywhere in it.
        //
        // A dot between two digits is left alone: #13.1 is a real issue number,
        // and turning it into "13 1" invents an issue 13 and loses the point one.
        // Initialisms first, out of harm's way, then put back afterwards.
        var initialisms = new List<string>();

        work = Initialism.Replace(work, m =>
        {
            initialisms.Add(m.Value);
            return Shield + (initialisms.Count - 1).ToString() + Shield;
        });

        work = SeparatorDots.Replace(work, " ").Replace('_', ' ');

        for (var i = 0; i < initialisms.Count; i++)
            work = work.Replace($"{Shield}{i}{Shield}", initialisms[i]);

        var kind = KindOf(work);
        work = Regex.Replace(work, @"(?i)\b(annual|one[-\s]?shot|tpb|trade\s+paperback|hc|hardcover|special)\b", " ");

        int? volume = null;
        if (VolumeMark.Match(work) is { Success: true } v)
        {
            volume = int.Parse(v.Groups[1].Value);
            work = work.Remove(v.Index, v.Length).Insert(v.Index, " ");
        }

        string? issue = null;

        if (Hashed.Match(work) is { Success: true } h)
        {
            issue = h.Groups[1].Value.Replace(" ", "");
            work = work.Remove(h.Index, h.Length).Insert(h.Index, " ");
        }
        else if (Trailing.Match(work) is { Success: true } t)
        {
            issue = t.Groups[1].Value;
            work = work.Remove(t.Index, t.Length);
        }

        var series = Tidy(work);

        if (series.Length == 0) return null;

        // Which of the two numbers is the series year.
        //
        // "Vol.1992" is how a great many taggers write the year a run began, and
        // where it is present the bracketed date is this issue's cover date
        // rather than the series'. Where there is no volume, the bracketed year
        // is the series year by convention - "Saga 001 (2012)" means the 2012
        // Saga, not an issue that happened to come out that year.
        var seriesYear = volume is >= 1900 and <= 2100 ? volume.ToString() : year;
        var coverYear = volume is >= 1900 and <= 2100 ? year : null;

        return new ComicRef
        {
            Series = series,
            Issue = issue,
            Volume = volume,
            Year = seriesYear,
            CoverYear = coverYear,
            Of = of,
            Kind = kind
        };
    }

    private static ComicKind KindOf(string text)
    {
        if (Regex.IsMatch(text, @"(?i)\bannual\b")) return ComicKind.Annual;
        if (Regex.IsMatch(text, @"(?i)\bone[-\s]?shot\b")) return ComicKind.OneShot;
        if (Regex.IsMatch(text, @"(?i)\b(tpb|trade\s+paperback|hc|hardcover)\b")) return ComicKind.TradePaperback;
        if (Regex.IsMatch(text, @"(?i)\bspecial\b")) return ComicKind.Special;
        return ComicKind.Issue;
    }

    /// <summary>
    /// Tidy what is left into a series name.
    ///
    /// Separators become spaces, because scene-style names use dots and
    /// underscores where a person would use neither - and a title that really
    /// contains a full stop, like "S.H.I.E.L.D.", loses nothing by being spaced
    /// out for searching, since the provider is asked by name rather than by
    /// exact string.
    /// </summary>
    private static string Tidy(string value)
    {
        var text = value.Replace('_', ' ');

        text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '_', '.', ',');

        return text;
    }
}
