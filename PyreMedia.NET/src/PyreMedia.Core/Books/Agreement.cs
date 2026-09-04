namespace PyreMedia.Core.Books;

/// <summary>Where two sources were asked the same question and said different things.</summary>
public sealed record Mismatch
{
    /// <summary>Series, issue, year - what they disagreed about.</summary>
    public required string About { get; init; }

    /// <summary>What the file says of itself.</summary>
    public required string Claimed { get; init; }

    /// <summary>What the other source says.</summary>
    public required string Found { get; init; }

    /// <summary>Which other source - the filename, a sidecar, a database.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Whether this changes where the file goes.
    ///
    /// A publisher spelt two ways is a disagreement and is not a problem. An
    /// issue number that differs is the file being filed as the wrong issue.
    /// Only the second is worth anybody's afternoon.
    /// </summary>
    public required bool Matters { get; init; }

    public string Describe() =>
        $"{About}: the file says \"{Claimed}\", {Source} says \"{Found}\"";
}

/// <summary>What came of checking one file against another source.</summary>
public sealed record Checked
{
    public required string Path { get; init; }

    public List<Mismatch> Differences { get; init; } = [];

    /// <summary>What was compared, so "nothing to report" can be told from "nothing was asked".</summary>
    public required string Against { get; init; }

    /// <summary>True when nothing was compared at all - no second opinion existed.</summary>
    public bool Unchecked => Against.Length == 0;

    /// <summary>
    /// Nothing here needs a person. The ordinary case, and the point of the
    /// whole exercise: a library where most files are silent.
    /// </summary>
    public bool Settled => !Unchecked && !Differences.Any(d => d.Matters);

    public string Name => System.IO.Path.GetFileName(Path);

    public string Describe() =>
        Unchecked ? "nothing to check it against"
        : Settled && Differences.Count == 0 ? $"agrees with {Against}"
        : Settled ? $"agrees with {Against} on everything that decides where it goes"
        : string.Join("; ", Differences.Where(d => d.Matters).Select(d => d.Describe()));
}

/// <summary>
/// Asking a file what it is, asking somewhere else the same question, and
/// reporting only where they disagree.
///
/// This is the whole idea, and it is not the same as identifying a file. A
/// television episode called "S01E04.mkv" knows nothing about itself, so it has
/// to be searched for. A comic usually carries its series, its issue number and
/// the id of the exact issue at the database it was scraped from; an EPUB
/// carries its title and author. For a library like that, "search for a match"
/// is a ritual performed on files that already know the answer.
///
/// So the question worth asking is not "what is this?" but "does what it says
/// about itself hold up?" Where two sources agree there is nothing to do and
/// nothing to show. Where they disagree, that is the work - and it is a handful
/// of files rather than tens of thousands.
///
/// The comparison is only as good as its idea of what counts as different. A
/// filename cannot hold a colon, so "WildC.A.T.s: Covert Action Teams" and
/// "WildC.A.T.s - Covert Action Teams" are the same series and reporting them
/// would bury the real disagreements in noise. Getting that wrong in the other
/// direction is worse: two genuinely different things called the same is how a
/// wrong rename gets waved through.
/// </summary>
public static class Agreement
{
    /// <summary>
    /// Check a comic's own metadata against what its filename says.
    ///
    /// Two sources that cost nothing and need no account: they are both already
    /// in hand by the time a scan has finished.
    /// </summary>
    public static Checked Compare(string path, ComicRef? inside, ComicRef? fromName)
    {
        if (inside is null || fromName is null)
            return new Checked { Path = path, Against = "" };

        var found = new List<Mismatch>();

        // The series decides the folder, so a real difference here scatters a
        // run. Compared on letters and digits alone, because punctuation is how
        // the same name is written differently rather than a different name.
        if (!ComicShelf.SameSeries(inside.Series, fromName.Series))
            found.Add(new Mismatch
            {
                About = "series",
                Claimed = inside.Series,
                Found = fromName.Series,
                Source = "the filename",
                Matters = true
            });

        // The issue number decides the filename. A file claiming to be issue 3
        // whose name says 4 is one of them being wrong, and it is worth knowing
        // which before either is trusted.
        if (Both(inside.Issue, fromName.Issue, out var a, out var b)
            && !SameIssue(a, b))
            found.Add(new Mismatch
            {
                About = "issue number",
                Claimed = a,
                Found = b,
                Source = "the filename",
                Matters = true
            });

        // The series year decides the folder too.
        if (Both(inside.Year, fromName.Year, out var y1, out var y2) && y1 != y2)
            found.Add(new Mismatch
            {
                About = "the year the series began",
                Claimed = y1,
                Found = y2,
                Source = "the filename",
                Matters = true
            });

        // Recorded but not raised. A cover date differing from a series year is
        // usually the filename carrying the wrong one of the two, which is worth
        // seeing and is not worth stopping for.
        if (Both(inside.CoverYear, fromName.CoverYear, out var c1, out var c2) && c1 != c2)
            found.Add(new Mismatch
            {
                About = "the date on the cover",
                Claimed = c1,
                Found = c2,
                Source = "the filename",
                Matters = false
            });

        return new Checked { Path = path, Against = "its filename", Differences = found };
    }

    /// <summary>
    /// Check a book's own metadata against the Calibre sidecar beside it.
    ///
    /// The sidecar is a second opinion that needs no network and no key, and it
    /// is the only one available for the PDFs and the MOBIs, which carry nothing
    /// this can read.
    /// </summary>
    public static Checked Compare(string path, BookRef? inside, BookRef? sidecar, string sourceName)
    {
        if (inside is null || sidecar is null)
            return new Checked { Path = path, Against = "" };

        var found = new List<Mismatch>();

        // The title decides the filename.
        if (!SameText(inside.Title, sidecar.Title))
            found.Add(new Mismatch
            {
                About = "title",
                Claimed = inside.Title,
                Found = sidecar.Title,
                Source = sourceName,
                Matters = true
            });

        // The author decides the folder, and is the field most often written two
        // ways - "Anne Rice" against "Rice, Anne" is one author, not two.
        if (Both(inside.Author, sidecar.Author, out var a1, out var a2) && !SamePerson(a1, a2))
            found.Add(new Mismatch
            {
                About = "author",
                Claimed = a1,
                Found = a2,
                Source = sourceName,
                Matters = true
            });

        if (Both(inside.Series, sidecar.Series, out var s1, out var s2) && !SameText(s1, s2))
            found.Add(new Mismatch
            {
                About = "series",
                Claimed = s1,
                Found = s2,
                Source = sourceName,
                Matters = true
            });

        // A year differing by a year or two is a reprint date against a first
        // publication date, which is a fact about publishing rather than a
        // mistake. Worth showing, not worth stopping for.
        if (Both(inside.Year, sidecar.Year, out var y1, out var y2) && y1 != y2)
            found.Add(new Mismatch
            {
                About = "year",
                Claimed = y1,
                Found = y2,
                Source = sourceName,
                Matters = false
            });

        return new Checked { Path = path, Against = sourceName, Differences = found };
    }

    /// <summary>Only compare where both actually said something.</summary>
    private static bool Both(string? left, string? right, out string a, out string b)
    {
        a = left?.Trim() ?? "";
        b = right?.Trim() ?? "";

        return a.Length > 0 && b.Length > 0;
    }

    /// <summary>
    /// "007" and "7" are one issue. "1/2" and "0.5" are not compared as numbers
    /// because they are written as they are for a reason and the text is what
    /// ends up in the filename.
    /// </summary>
    private static bool SameIssue(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        var left = a.TrimStart('0');
        var right = b.TrimStart('0');

        if (left.Length == 0) left = "0";
        if (right.Length == 0) right = "0";

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Letters and digits only - punctuation is how one name is written two ways.</summary>
    private static bool SameText(string a, string b) =>
        ComicShelf.Canonical(a) == ComicShelf.Canonical(b);

    /// <summary>
    /// One person written two ways.
    ///
    /// "Rice, Anne" and "Anne Rice" are the same author; a library that files
    /// them separately has two folders for one person. Compared as a set of
    /// name parts rather than by reordering, because reordering has to decide
    /// which part is the surname and that is exactly what cannot be known -
    /// "Ludwig van Beethoven", "Hilary Mantel" and "Kim Stanley Robinson" all
    /// break a rule built for "Surname, First".
    /// </summary>
    private static bool SamePerson(string a, string b)
    {
        if (SameText(a, b)) return true;

        var left = Parts(a);
        var right = Parts(b);

        return left.Count > 0 && left.SetEquals(right);

        static HashSet<string> Parts(string name) =>
        [
            .. name.Split([' ', ',', '.', ';'], StringSplitOptions.RemoveEmptyEntries)
                   .Select(p => ComicShelf.Canonical(p))
                   .Where(p => p.Length > 1)
        ];
    }

    /// <summary>
    /// A sentence about a whole shelf, for saying once rather than per file.
    ///
    /// Written so that the good outcome reads as the good outcome. A library
    /// where nothing disagrees should not produce a screen full of green ticks
    /// to scroll past.
    /// </summary>
    public static string Describe(IReadOnlyList<Checked> all)
    {
        if (all.Count == 0) return "Nothing to check.";

        var unchecked_ = all.Count(c => c.Unchecked);
        var settled = all.Count(c => c.Settled);
        var trouble = all.Count(c => !c.Unchecked && !c.Settled);

        var parts = new List<string>();

        if (settled > 0)
            parts.Add(settled == all.Count
                ? $"All {settled} agree with what was checked against them"
                : $"{settled} agree");

        if (trouble > 0)
            parts.Add(trouble == 1
                ? "1 does not, and is worth a look"
                : $"{trouble} do not, and are worth a look");

        if (unchecked_ > 0)
            parts.Add($"{unchecked_} had nothing to check against");

        return string.Join(", ", parts) + ".";
    }
}
