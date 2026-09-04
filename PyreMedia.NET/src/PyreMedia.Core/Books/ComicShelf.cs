namespace PyreMedia.Core.Books;

/// <summary>One issue, and the file it came from.</summary>
public sealed record HeldIssue
{
    public required ShelfItem Item { get; init; }

    /// <summary>The issue number as written, so "1/2" and "007" survive.</summary>
    public required string Number { get; init; }

    /// <summary>The same as a whole number, where it is one. Null for "1/2".</summary>
    public int? Sortable { get; init; }

    public ComicKind Kind => Item.Comic?.Kind ?? ComicKind.Issue;
}

/// <summary>
/// A series and what is held of it.
///
/// The comic answer to a television series: a name, a year, and a run with
/// gaps in it. The gaps are the point - a shelf of files says what is there
/// and nothing about what is not, and "which issues am I missing" is the
/// question anybody with a long run actually asks.
/// </summary>
public sealed record ComicSeriesGroup
{
    public required string Series { get; init; }

    /// <summary>The year the series began, where anything said so.</summary>
    public string? Year { get; init; }

    public string? Publisher { get; init; }

    public List<HeldIssue> Issues { get; init; } = [];

    /// <summary>
    /// How many issues the run has, where the comics say. From ComicInfo's
    /// Count or a filename's "(of 6)" - never guessed from what is held, since
    /// a run is usually incomplete and that is the thing being measured.
    /// </summary>
    public int? Total { get; init; }

    /// <summary>Issue numbers between the lowest and highest held that are not held.</summary>
    public List<int> Missing { get; init; } = [];

    /// <summary>
    /// Numbers past the highest held, up to <see cref="Total"/>. Kept apart from
    /// <see cref="Missing"/> because a hole in the middle and an unfinished run
    /// are different problems: one is a bad download, the other is just where
    /// you got to.
    /// </summary>
    public List<int> NotYet { get; init; } = [];

    /// <summary>Issue numbers held more than once.</summary>
    public List<string> Duplicated { get; init; } = [];

    /// <summary>True when every issue this program knows about is here.</summary>
    public bool Complete => Missing.Count == 0 && NotYet.Count == 0;

    public string Display => Year is null ? Series : $"{Series} ({Year})";

    /// <summary>A sentence about the run, for showing beside its name.</summary>
    public string Describe()
    {
        var held = Issues.Count == 1 ? "1 issue" : $"{Issues.Count} issues";

        if (Total is { } total && Complete) return $"{held} - the complete run of {total}.";

        var parts = new List<string> { held };

        if (Total is { } t) parts.Add($"of {t}");

        if (Missing.Count > 0)
            parts.Add(Missing.Count == 1
                ? $"missing #{Missing[0]}"
                : $"missing {Missing.Count}: {Span(Missing)}");

        if (NotYet.Count > 0) parts.Add($"not yet: {Span(NotYet)}");

        if (Duplicated.Count > 0)
            parts.Add(Duplicated.Count == 1
                ? $"#{Duplicated[0]} is here twice"
                : $"{Duplicated.Count} issues here more than once");

        return string.Join(", ", parts) + ".";
    }

    /// <summary>
    /// "3-7, 11, 14-15" rather than every number. A run with forty holes is
    /// unreadable listed out, and the shape of the gap is what tells you
    /// whether an arc is missing or a single issue is.
    /// </summary>
    internal static string Span(IReadOnlyList<int> numbers)
    {
        if (numbers.Count == 0) return "";

        var parts = new List<string>();
        var start = numbers[0];
        var last = start;

        for (var i = 1; i <= numbers.Count; i++)
        {
            if (i < numbers.Count && numbers[i] == last + 1) { last = numbers[i]; continue; }

            parts.Add(start == last ? $"#{start}"
                    : last == start + 1 ? $"#{start}, #{last}"
                    : $"#{start}-{last}");

            if (i < numbers.Count) { start = numbers[i]; last = start; }
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Gathering loose comics into the series they belong to.
///
/// Grouping is by series and year together, because two unrelated runs share a
/// name often enough to matter - there are five different comics called
/// Daredevil - and merging them would report enormous fictional gaps.
/// </summary>
public static class ComicShelf
{
    /// <summary>Every series among these files, with what is held and what is not.</summary>
    /// <param name="matches">
    /// Series somebody has agreed to, keyed by <see cref="Canonical"/>.
    ///
    /// Where one exists its issue list is the run, which is the only way this
    /// can be right: a comic states how long its run is only when somebody
    /// wrote it down, and on a measured shelf of 1,935 that was about one file
    /// in fourteen. A count alone is not enough either - it says a run is six
    /// long without saying whether those six are #1-#6 or #0-#5.
    /// </param>
    public static List<ComicSeriesGroup> Group(
        IEnumerable<ShelfItem> items,
        IReadOnlyDictionary<string, ComicMatch>? matches = null)
    {
        var groups = items
            .Where(i => i.Kind == ShelfKind.Comic && i.Comic is not null)
            .GroupBy(i => (Series: Canonical(i.Comic!.Series), i.Comic.Year));

        return
        [
            .. groups
                .Select(g => Build(g, Agreed(matches, g.Key.Series)))
                .OrderBy(g => g.Display, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// A series name reduced to what it is, for comparing only.
    ///
    /// A colon cannot go in a filename, so every scanner writes a dash instead.
    /// That means the same run arrives under two names the moment some copies
    /// carry metadata and others do not - and a real library had exactly that:
    /// "A&amp;A: The Adventures of Archer &amp; Armstrong" from inside the files
    /// beside "A&amp;A - The Adventures of Archer &amp; Armstrong" from the
    /// names, listed as two series, each apparently missing what the other had.
    ///
    /// Punctuation and spacing are dropped rather than mapped, because the
    /// number of ways to write a separator is not worth enumerating. What is
    /// left is the letters and digits, which is what people mean by the title.
    /// </summary>
    /// <summary>Whether two series names mean the same series.</summary>
    public static bool SameSeries(string a, string b) => Canonical(a) == Canonical(b);

    /// <summary>
    /// Public because it is the key everything about a series hangs off - the
    /// grouping, the folder it files into, and the match somebody agreed to.
    /// Those three have to agree, or a run is grouped one way and filed another.
    /// </summary>
    public static string Canonical(string series)
    {
        var sb = new System.Text.StringBuilder(series.Length);

        foreach (var c in series)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));

        // Nothing but punctuation is not a name to reduce - keep it as it came
        // so two such series do not collapse into one.
        return sb.Length > 0 ? sb.ToString() : series.Trim().ToLowerInvariant();
    }

    /// <summary>The agreed match for a reduced series name, where there is one.</summary>
    private static ComicMatch? Agreed(
        IReadOnlyDictionary<string, ComicMatch>? matches, string key) =>
        matches is not null && matches.TryGetValue(key, out var m) ? m : null;

    /// <summary>
    /// Issue numbers as whole numbers, for the ones that are.
    ///
    /// A run carries #1/2, #100.1 and #1.MU as well, and those have no place on
    /// a number line - they are dropped from the gap arithmetic rather than
    /// rounded into a neighbour they would then hide.
    /// </summary>
    private static List<int> Numbers(IEnumerable<string> issues)
    {
        var found = new List<int>();

        foreach (var raw in issues)
        {
            var digits = new string([.. raw.TakeWhile(char.IsDigit)]);

            if (digits.Length > 0 && digits.Length == raw.Trim().Length
                && int.TryParse(digits, out var n))
            {
                found.Add(n);
            }
        }

        return found;
    }

    private static ComicSeriesGroup Build(
        IGrouping<(string Series, string? Year), ShelfItem> group, ComicMatch? agreed = null)
    {
        var issues = group
            .Where(i => i.Comic!.Issue is not null)
            .Select(i => new HeldIssue
            {
                Item = i,
                Number = i.Comic!.Issue!,
                Sortable = i.Comic.IssueNumber
            })
            .OrderBy(h => h.Sortable ?? int.MaxValue)
            .ThenBy(h => h.Number, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only ordinary issues count towards a run. An annual is not issue 1,
        // and counting it as one invents a gap where the real #1 sits.
        var numbered = issues
            .Where(h => h.Kind == ComicKind.Issue && h.Sortable is not null)
            .Select(h => h.Sortable!.Value)
            .ToList();

        var held = numbered.ToHashSet();

        // Taken from what the comics say, not from what is present. Deriving it
        // from the highest issue held would declare every incomplete run
        // complete, which is exactly backwards.
        var total = group.Select(i => i.Comic!.Of).FirstOrDefault(o => o is > 0);

        var missing = new List<int>();
        var notYet = new List<int>();

        if (held.Count > 0)
        {
            var highest = held.Max();

            // Where the run's length is known, it starts at #1 and everything
            // below the highest held that is absent is missing - including
            // issues before the lowest held. Counting only the holes between
            // them called two issues of four "the complete run of 4", which is
            // both wrong and exactly the sort of wrong nobody checks.
            //
            // Where the length is not known, the start is not known either:
            // plenty of series open at #0 and plenty of collections begin part
            // way in. Only the holes between what is held can be claimed.
            //
            // A known length still does not mean the run starts at #1. Plenty
            // open at #0 - Valiant and the nineties Image books especially -
            // and assuming otherwise told the owner of a complete six-issue
            // #0-#5 run that a seventh was still to come, and left it forever
            // one short of complete.
            // An agreed match knows the run outright, so nothing has to be
            // inferred: every number it lists that is not here is missing,
            // whether it sits above, below or between what is held. This is
            // where a run that opens at #0 answers correctly without anybody
            // having guessed that it does.
            var run = agreed is null ? [] : Numbers(agreed.Issues);

            if (run.Count > 0)
            {
                foreach (var n in run.Where(n => !held.Contains(n)).Distinct().Order())
                    (n <= highest ? missing : notYet).Add(n);
            }
            else
            {
                var from = total is not null ? Math.Min(1, held.Min()) : held.Min();

                for (var n = from; n <= highest; n++)
                    if (!held.Contains(n)) missing.Add(n);

                // Only as many as are actually still outstanding. Counting up
                // to the total regardless invents an issue for every one the
                // run started early.
                if (total is { } t)
                    for (var n = highest + 1; held.Count + notYet.Count < t && n <= t; n++)
                        notYet.Add(n);
            }
        }
        else if (total is { } t)
        {
            for (var n = 1; n <= t; n++) notYet.Add(n);
        }

        var duplicated = issues
            .GroupBy(h => h.Number, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ComicSeriesGroup
        {
            // The name a comic gave of itself beats one taken from a filename,
            // because a filename cannot hold a colon and the real title often
            // has one. Longest wins among equals, since a truncated name is a
            // more common failure than an inflated one.
            Series = group.Select(i => i.Comic!)
                          .OrderByDescending(c => c.FromInside)
                          .ThenByDescending(c => c.Series.Length)
                          .First().Series.Trim(),

            Year = group.Key.Year,

            // Any of them will do - they are the same series - but a comic that
            // was read from inside is worth preferring over one that was not.
            Publisher = group.Select(i => i.Comic!)
                             .OrderByDescending(c => c.FromInside)
                             .Select(c => c.Publisher)
                             .FirstOrDefault(p => p is not null),

            Issues = issues,
            Total = total,
            Missing = missing,
            NotYet = notYet,
            Duplicated = duplicated
        };
    }

}
