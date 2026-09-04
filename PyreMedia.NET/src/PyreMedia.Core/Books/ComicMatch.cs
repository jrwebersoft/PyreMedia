namespace PyreMedia.Core.Books;

/// <summary>
/// A series a person has agreed to, and what the database said about it.
///
/// Kept because the answer is worth more than the lookup. A comic shelf is
/// asked about once and filed many times, the free provider is a six gigabyte
/// download and the others are rationed, and the user's agreement to "this run
/// is that run" is a judgement no amount of re-querying reproduces. So it is
/// recorded against the series rather than the file, and survives a rescan, a
/// restart and a rename.
/// </summary>
public sealed record ComicMatch
{
    /// <summary>
    /// The series as the shelf reduces it - letters and digits only.
    ///
    /// Keyed this way and not by the name as written, because the name as
    /// written is the thing being corrected: four spellings of one run all have
    /// to find the same match.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>The name to file under, from the database rather than a filename.</summary>
    public required string Series { get; init; }

    public string? Year { get; init; }
    public string? Publisher { get; init; }

    /// <summary>Which database answered, since they disagree and it matters which.</summary>
    public required string Source { get; init; }

    /// <summary>That database's own id for the series, so the issues can be asked for again.</summary>
    public string? SeriesId { get; init; }

    /// <summary>
    /// Every issue number the run has, as the database gives them.
    ///
    /// Text and not numbers: real runs carry #0, #1/2, #100.1 and #1.MU, and
    /// turning those into integers loses exactly the issues that are hard to
    /// find. Empty where the issues were never fetched.
    /// </summary>
    public List<string> Issues { get; init; } = [];

    /// <summary>
    /// How long the run is, where it is known - the count of issues the
    /// database lists, or what it said outright.
    /// </summary>
    public int? Total { get; init; }

    /// <summary>When it was agreed to, so a stale answer can be told from a fresh one.</summary>
    public string? Agreed { get; init; }

    public string Display => Year is { Length: 4 }
        ? $"{Series} ({Year})" + (Publisher is null ? "" : $" - {Publisher}")
        : Series + (Publisher is null ? "" : $" - {Publisher}");

    /// <summary>
    /// The item as the agreed match describes it: the database's name for the
    /// series, its year, its publisher, and how long the run really is.
    ///
    /// The run length is the one the files could almost never answer. A comic
    /// says how long its run is only where somebody wrote it down, which on a
    /// measured shelf of 1,935 was about one file in fourteen - so "which
    /// issues am I missing" was being answered from nothing at all for nearly
    /// every series.
    ///
    /// Anything the match does not know is left as the file had it. A database
    /// that omits a publisher should not erase one the file carried.
    /// </summary>
    public static ShelfItem ApplyTo(ShelfItem item, IReadOnlyDictionary<string, ComicMatch> matches)
    {
        if (item.Comic is not { Series: { Length: > 0 } series } comic) return item;
        if (!matches.TryGetValue(ComicShelf.Canonical(series), out var m)) return item;

        return item with
        {
            Comic = comic with
            {
                Series = m.Series,
                Year = m.Year ?? comic.Year,
                Publisher = m.Publisher ?? comic.Publisher,
                Of = m.Total ?? comic.Of
            }
        };
    }

    /// <summary>The publisher an agreed match supplies, for the folder level.</summary>
    public static string? PublisherFor(
        ComicRef comic, IReadOnlyDictionary<string, ComicMatch> matches) =>
        comic.Series is { Length: > 0 } s
        && matches.TryGetValue(ComicShelf.Canonical(s), out var m)
            ? m.Publisher
            : null;
}
