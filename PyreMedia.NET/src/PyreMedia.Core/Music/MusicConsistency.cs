using System.Text.RegularExpressions;

namespace PyreMedia.Core.Music;

/// <summary>What a group of files disagrees about.</summary>
public enum Disagreement
{
    /// <summary>
    /// A trailing qualifier only some of a set carry - "(Unabridged)",
    /// "[Remastered]", "(Deluxe Edition)". Almost always the shop's doing
    /// rather than a real difference between the records.
    /// </summary>
    OddQualifier,

    /// <summary>The same name spelt differently across a set.</summary>
    Spelling,

    /// <summary>One album whose tracks claim different years.</summary>
    Year,

    /// <summary>One album whose tracks claim different genres.</summary>
    Genre,

    /// <summary>Some tracks of a set name an album artist and others do not.</summary>
    MissingAlbumArtist
}

/// <summary>One thing a group disagrees about, and what the rest of it says.</summary>
public sealed record Inconsistency(
    Disagreement Kind,
    string Field,
    IReadOnlyList<TrackTags> Odd,
    string Value,
    string? Suggested,
    string Why)
{
    /// <summary>
    /// There is a value the rest of the set agrees on. Not the same claim as
    /// "this is the right answer", and the difference matters.
    ///
    /// Everything here works by majority, and the majority is sometimes wrong.
    /// Measured on a real library: thirteen files say "Cherry Poppin Daddies"
    /// and one says "Cherry Poppin' Daddies", and the one is correct - that is
    /// the band's name. Suggesting the majority would quietly delete an
    /// apostrophe from a name that has one.
    ///
    /// So these are proposals for a person to approve, never edits to apply
    /// unattended, and nothing in this class should ever be wired to something
    /// that runs on its own.
    /// </summary>
    public bool Fixable => Suggested is not null && Suggested != Value;
}

/// <summary>
/// Finds the odd one out in a set of files that ought to agree.
///
/// A different question from <see cref="MusicHealth"/>, which examines one file
/// and asks whether it is wrong on its own terms. Nothing is wrong with
/// "Harry Potter and the Philosopher's Stone (Full-Cast Edition) (Unabridged)"
/// read by itself. It is only wrong beside its six siblings, none of which say
/// "(Unabridged)" - and the result is one folder filed away from the rest of
/// the series where nobody looks for it.
///
/// That is the shape of nearly every real inconsistency in a library: not a
/// value that is wrong, but a value that disagrees with its neighbours because
/// two shops tagged two purchases differently. So everything here works by
/// finding what most of a set says and reporting the minority - and only where
/// the minority looks like a variant of the majority rather than a genuinely
/// different thing.
///
/// The majority is not the same as the truth, and this class cannot tell the
/// difference. On a real library thirteen files spell a band "Cherry Poppin
/// Daddies" and one spells it "Cherry Poppin' Daddies"; the one is right.
/// Everything here is a proposal for a person to look at.
/// </summary>
public static class MusicConsistency
{
    /// <summary>
    /// Everything a set of files that ought to match disagrees about.
    /// </summary>
    /// <param name="sets">
    /// Each set is a group expected to agree - the tracks of one album, or the
    /// books of one series. What belongs together is the caller's judgement;
    /// this only compares what it is given.
    /// </param>
    public static List<Inconsistency> Examine(IEnumerable<IReadOnlyList<TrackTags>> sets)
    {
        var found = new List<Inconsistency>();

        foreach (var set in sets.Where(s => s.Count > 1))
        {
            Qualifiers(set, found);
            Spellings(set, found);
            Single(set, found, Disagreement.Year, "Year", t => t.Year.Text);
            Single(set, found, Disagreement.Genre, "Genre", t => t.Genre.Text);
            AlbumArtists(set, found);
        }

        return found;
    }

    /// <summary>
    /// The tag edits a set of approved disagreements amounts to.
    ///
    /// One edit per file rather than per finding, because two findings can
    /// touch the same file - an album whose year and genre both disagree - and
    /// writing it twice would leave two undo entries for one intention and
    /// rewrite the container for no reason.
    ///
    /// Takes only what was approved. Nothing in this class decides on its own
    /// what to write.
    /// </summary>
    public static Dictionary<TrackTags, TagEdit> Edits(IEnumerable<Inconsistency> approved)
    {
        var edits = new Dictionary<TrackTags, TagEdit>();

        foreach (var finding in approved)
        {
            if (finding.Suggested is null) continue;

            foreach (var track in finding.Odd)
            {
                var edit = edits.GetValueOrDefault(track, new TagEdit());

                edits[track] = finding.Field.ToLowerInvariant() switch
                {
                    "album" => edit with { Album = finding.Suggested },
                    "artist" => edit with { Artist = finding.Suggested },
                    "albumartist" => edit with { AlbumArtist = finding.Suggested },
                    "year" => edit with { Year = finding.Suggested },
                    "genre" => edit with { Genre = finding.Suggested },

                    // A field nothing knows how to write is dropped rather than
                    // guessed at. Silently writing the wrong tag because the
                    // name nearly matched is worse than not writing it.
                    _ => edit
                };
            }
        }

        return edits.Where(e => e.Value.Any).ToDictionary(e => e.Key, e => e.Value);
    }

    /// <summary>
    /// A trailing bracket that only a minority of the set carries.
    ///
    /// Compared on what is left once every trailing bracket is stripped, so
    /// "Chamber of Secrets (Full-Cast Edition)" and "Philosopher's Stone
    /// (Full-Cast Edition) (Unabridged)" are recognised as the same shape
    /// carrying a different number of qualifiers.
    /// </summary>
    private static void Qualifiers(IReadOnlyList<TrackTags> set, List<Inconsistency> found)
    {
        var albums = set.Where(t => t.Album.HasText).ToList();
        if (albums.Count < 3) return;

        // How many trailing brackets each title carries, and what they say.
        var shapes = albums
            .Select(t => (Track: t, Text: t.Album.Text, Tail: Tail(t.Album.Text)))
            .ToList();

        var common = shapes
            .SelectMany(s => s.Tail)
            .GroupBy(q => q, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var (qualifier, count) in common)
        {
            // Carried by most of the set: that is the house style, not an
            // anomaly. Carried by one or two: that is the anomaly.
            if (count >= albums.Count * 0.5 || count > 2) continue;

            var odd = shapes.Where(s => s.Tail.Contains(qualifier, StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var (track, text, _) in odd)
                found.Add(new Inconsistency(
                    Disagreement.OddQualifier, "Album", [track], text,
                    Strip(text, qualifier),
                    $"only {count} of {albums.Count} say \"{qualifier}\" - the rest of the set "
                    + "does not, so this one files away from its siblings"));
        }
    }

    /// <summary>
    /// One name spelt two ways across a set: "J.K. Rowling" beside "JK Rowling".
    /// Only reported when the spellings are near-identical - two genuinely
    /// different artists on a compilation are not a disagreement.
    /// </summary>
    private static void Spellings(IReadOnlyList<TrackTags> set, List<Inconsistency> found)
    {
        foreach (var (field, read) in new (string, Func<TrackTags, TagValue>)[]
                 {
                     ("Artist", t => t.Artist),
                     ("AlbumArtist", t => t.AlbumArtist)
                 })
        {
            var named = set.Where(t => read(t).HasText).ToList();
            if (named.Count < 2) continue;

            var spellings = named
                .GroupBy(t => read(t).Text, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (spellings.Count < 2) continue;

            var majority = spellings[0];

            // A tie is not a majority, and proposing one is worse than saying
            // nothing: a real library offered "Daft Punk -> DAFT PUNK" on two
            // files against two, having picked the winner by whichever the
            // sort happened to put first. Year and Genre have always required
            // a clear margin; this did not, and it is the one that names
            // artists.
            if (spellings.Count > 1 && majority.Count() == spellings[1].Count()) continue;

            foreach (var variant in spellings.Skip(1))
            {
                // Near-identical only. "Slayer" and "Disturbed" on one
                // soundtrack disagree about nothing.
                if (!Duplicates.Alike(Duplicates.TitleKey(majority.Key), Duplicates.TitleKey(variant.Key)))
                    continue;

                found.Add(new Inconsistency(
                    Disagreement.Spelling, field, [.. variant], variant.Key, majority.Key,
                    $"{variant.Count()} of {named.Count} spell it \"{variant.Key}\" and "
                    + $"{majority.Count()} spell it \"{majority.Key}\""));
            }
        }
    }

    /// <summary>A field that ought to be the same throughout and is not.</summary>
    private static void Single(
        IReadOnlyList<TrackTags> set, List<Inconsistency> found,
        Disagreement kind, string field, Func<TrackTags, string> read)
    {
        var values = set.Where(t => !string.IsNullOrWhiteSpace(read(t)))
            .GroupBy(read, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (values.Count < 2) return;

        var majority = values[0];

        // A clear majority, or there is nothing to prefer. Two tracks saying
        // one year and two saying another is a question, not a correction.
        if (majority.Count() <= set.Count / 2) return;

        foreach (var odd in values.Skip(1))
            found.Add(new Inconsistency(
                kind, field, [.. odd], odd.Key, majority.Key,
                $"{majority.Count()} of {set.Count} say \"{majority.Key}\""));
    }

    private static void AlbumArtists(IReadOnlyList<TrackTags> set, List<Inconsistency> found)
    {
        var missing = set.Where(t => !t.AlbumArtist.HasText).ToList();
        if (missing.Count == 0 || missing.Count == set.Count) return;

        var named = set.Where(t => t.AlbumArtist.HasText)
            .GroupBy(t => t.AlbumArtist.Text, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First();

        found.Add(new Inconsistency(
            Disagreement.MissingAlbumArtist, "AlbumArtist", missing, "", named.Key,
            $"{missing.Count} of {set.Count} have no album artist while the rest say "
            + $"\"{named.Key}\" - Kodi will split this album in two"));
    }

    /// <summary>Every trailing bracketed qualifier, outermost last.</summary>
    internal static List<string> Tail(string text)
    {
        var qualifiers = new List<string>();

        while (TrailingBracket.Match(text) is { Success: true } match)
        {
            qualifiers.Add(match.Groups[1].Value.Trim());
            text = text[..match.Index];
        }

        return qualifiers;
    }

    /// <summary>
    /// The title without one trailing qualifier, keeping every other.
    ///
    /// The keeping matters. Stripping "(Full-Cast Edition)" from
    /// "Philosopher's Stone (Full-Cast Edition) (Unabridged)" must not take
    /// "(Unabridged)" with it just because that one sat further out - the
    /// first version of this walked inwards and discarded everything it passed.
    /// </summary>
    internal static string Strip(string text, string qualifier)
    {
        var kept = new List<string>();
        var removed = false;

        while (TrailingBracket.Match(text) is { Success: true } match)
        {
            var found = match.Groups[1].Value.Trim();

            // Only the first occurrence goes. A title genuinely saying the same
            // thing twice keeps one of them.
            if (!removed && string.Equals(found, qualifier, StringComparison.OrdinalIgnoreCase))
                removed = true;
            else
                kept.Add(match.Value.Trim());

            text = text[..match.Index];
        }

        // Collected outermost first, so they go back on in reverse.
        kept.Reverse();

        return MusicHealth.Tidy(text + " " + string.Join(" ", kept));
    }

    private static readonly Regex TrailingBracket = new(
        @"\s*[\(\[]([^)\]]+)[\)\]]\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
