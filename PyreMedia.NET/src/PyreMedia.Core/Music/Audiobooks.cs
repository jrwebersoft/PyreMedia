namespace PyreMedia.Core.Music;

/// <summary>How sure we are that a folder holds a book rather than a record.</summary>
public enum SpokenWord
{
    /// <summary>Music. Nothing here suggests otherwise.</summary>
    No,

    /// <summary>Signs of a book, but not enough of them to file it as one.</summary>
    Maybe,

    /// <summary>A book. The format or the tags say so outright.</summary>
    Yes
}

/// <summary>A book, its author, and the files it is spread across.</summary>
public sealed record Audiobook(
    string Title,
    string? Author,
    string? Narrator,
    string? Series,
    IReadOnlyList<TrackTags> Files,
    SpokenWord Confidence,
    string Why)
{
    public double Hours => Files.Sum(f => f.Seconds ?? 0) / 3600.0;

    public override string ToString() =>
        $"{Author ?? "Unknown author"} - {Title} ({Files.Count} files, {Hours:F1}h)";
}

/// <summary>
/// Tells a book from a record.
///
/// Worth separating for a reason that has nothing to do with tidiness. An
/// audiobook read into a music library becomes an album by an artist nobody has
/// heard of with fifty-seven tracks called "Chapter 1" through "Chapter 57", and
/// every rule elsewhere in this codebase then does the wrong thing to it - the
/// duplicate finder sees identical durations, the gap filler sees missing track
/// numbers, and the album grouper tries to work out which of eleven files is the
/// title track. None of that is recoverable afterwards by looking at the result.
///
/// The evidence is deliberately weighted. One signal is a guess; a format that
/// exists only for books, or a tag that names the genre outright, is not.
/// </summary>
public static class Audiobooks
{
    /// <summary>
    /// Formats that exist to hold books. .m4b is an .m4a with a different
    /// extension precisely so players know to remember where you were, and
    /// nobody has ever shipped an album as one.
    /// </summary>
    public static readonly string[] BookFormats = [".m4b", ".aa", ".aax"];

    /// <summary>
    /// Formats that hold an entire book in one file, chapters and all. Several
    /// of them in a folder is several books, not one book in pieces.
    /// </summary>
    private static readonly HashSet<string> SelfContained =
        new(BookFormats, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Roughly how long a track has to run before its length is itself
    /// evidence. Songs over twenty minutes exist - prog rock, live sets, DJ
    /// mixes - so this is a signal and never a verdict on its own.
    /// </summary>
    private const double LongTrack = 20 * 60;

    /// <summary>
    /// Every book found among a set of files, grouped by the folder they sit in.
    ///
    /// Folder and not tags, because the tags are exactly what is unreliable
    /// here: a book ripped from CDs routinely carries the disc as the album and
    /// the publisher as the artist, and grouping on that scatters one book
    /// across fourteen "albums".
    /// </summary>
    public static List<Audiobook> Find(IEnumerable<TrackTags> tracks)
    {
        var books = new List<Audiobook>();

        foreach (var folder in tracks.GroupBy(t => Path.GetDirectoryName(t.Path) ?? ""))
        {
            var files = folder.ToList();

            // A whole book in one file, which is what .m4b exists for. Seven of
            // them share a folder in a real library - the complete Harry Potter
            // - and grouping by folder would have called that one book with
            // seven chapters. Each self-contained file is its own book.
            var selfContained = files
                .Where(f => SelfContained.Contains(Path.GetExtension(f.Path).ToLowerInvariant()))
                .ToList();

            if (selfContained.Count > 0)
            {
                foreach (var single in selfContained)
                    books.Add(new Audiobook(
                        Title: BookTitle([single], folder.Key),
                        Author: Author([single]),
                        Narrator: Narrator([single]),
                        Series: null,
                        Files: [single],
                        Confidence: SpokenWord.Yes,
                        Why: "a whole book in one file"));

                files = files.Except(selfContained).ToList();
                if (files.Count == 0) continue;
            }
            var (confidence, why) = Judge(files, folder.Key);

            if (confidence is SpokenWord.No) continue;

            books.Add(new Audiobook(
                Title: BookTitle(files, folder.Key),
                Author: Author(files),
                Narrator: Narrator(files),
                Series: null,
                Files: [.. files.OrderBy(f => f.TrackNumber ?? int.MaxValue)
                                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)],
                Confidence: confidence,
                Why: why));
        }

        return books;
    }

    /// <summary>
    /// Whether the files in one folder are a book, and what made us think so.
    /// </summary>
    public static (SpokenWord Confidence, string Why) Judge(
        IReadOnlyList<TrackTags> files, string folder)
    {
        if (files.Count == 0) return (SpokenWord.No, "");

        // A format that only holds books settles it on its own.
        if (files.Any(f => BookFormats.Contains(Path.GetExtension(f.Path).ToLowerInvariant())))
            return (SpokenWord.Yes, "the files are in a format made for books");

        var reasons = new List<string>();
        var weight = 0;

        // ID3 genre 183 is Audiobook and 101 is Speech. Both are somebody
        // saying outright what this is, which beats anything inferred.
        if (files.Any(f => f.Genre.HasText && SpokenGenre(f.Genre.Text)))
        {
            weight += 2;
            reasons.Add("the genre says so");
        }

        // Chapter numbering. One or two chapter-named tracks on a record is a
        // coincidence; most of a folder named that way is not.
        var chapters = files.Count(f => Chaptered(Title(f)));
        if (chapters >= Math.Max(3, files.Count * 0.6))
        {
            weight += 2;
            reasons.Add($"{chapters} of {files.Count} files are named as chapters");
        }

        var long_ = files.Count(f => f.Seconds is > LongTrack);
        if (long_ >= Math.Max(2, files.Count / 2))
        {
            weight++;
            reasons.Add($"{long_} files run over twenty minutes");
        }

        // "unabridged", "narrated by", "read by" anywhere in the tags.
        if (files.Any(f => Spoken(Title(f)) || Spoken(f.Album.Text) || Spoken(f.Artist.Text))
            || Spoken(Path.GetFileName(folder)))
        {
            weight += 2;
            reasons.Add("the tags describe a reading");
        }

        return weight switch
        {
            >= 3 => (SpokenWord.Yes, string.Join(", ", reasons)),
            2 => (SpokenWord.Maybe, string.Join(", ", reasons)),
            _ => (SpokenWord.No, "")
        };
    }

    /// <summary>
    /// Where a book goes. Not the music pattern: a book has no album and no
    /// track number worth the name, and filing one by them produces a folder
    /// called "Unknown Album" holding fifty-seven files called "Track 04".
    /// </summary>
    /// <remarks>
    /// Kept identical to <c>PyreMediaSettings.AudiobookFormat</c>, which is
    /// what actually gets used - this is only the fallback when that is blank.
    /// The two had drifted: this said <c>{title}</c> where the setting said
    /// <c>{book}</c>. They mean the same thing on a book, so nothing was
    /// visibly wrong, which is exactly how a fallback that differs from the
    /// default survives long enough to matter.
    /// </remarks>
    public const string DefaultFormat = "{author}/{book}[ ({year})]/{chapter:000} {chaptertitle}";

    private static bool SpokenGenre(string genre)
    {
        var g = genre.Trim().ToLowerInvariant().Trim('(', ')');

        return g is "audiobook" or "audio book" or "speech" or "spoken" or "spoken word"
                 or "spoken & audio" or "books" or "book" or "podcast" or "audio theatre"
                 or "audio drama" or "lecture" or "183" or "101" or "186" or "184";
    }

    /// <summary>
    /// Named as a chapter of a book.
    ///
    /// Deliberately narrow, and it was not narrow enough first time. Matching
    /// "track", "disc", "part" and " of " found four books in a library with
    /// none in it: a film score with movements called "Chaconne: Part 1", both
    /// discs of a Phantom of the Opera cast recording, and a Nickelback single
    /// whose files were called "Track 01". Every one of those words is ordinary
    /// music. Only chapter language is evidence of a book, and "Part 3 of 12" -
    /// with the total spelled out - only counts because records do not number
    /// themselves that way.
    /// </summary>
    private static bool Chaptered(string text)
    {
        var t = text.ToLowerInvariant();

        return t.Contains("chapter") || t.Contains("chap.")
            || PartOf.IsMatch(t);
    }

    private static readonly System.Text.RegularExpressions.Regex PartOf = new(
        @"\bpart\s+\d+\s+of\s+\d+\b",
        System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static bool Spoken(string text)
    {
        var t = text.ToLowerInvariant();

        return t.Contains("unabridged") || t.Contains("abridged") || t.Contains("narrated")
            || t.Contains("narrator") || t.Contains("read by") || t.Contains("audiobook")
            || t.Contains("audio book");
    }

    private static string Title(TrackTags t) =>
        t.Title.HasText ? t.Title.Text : Path.GetFileNameWithoutExtension(t.Path);

    /// <summary>
    /// The book's title. The album tag is right more often than not, but a book
    /// ripped from CDs puts the disc there - "Neuromancer Disc 4" - so a
    /// title shared across the folder beats one that varies with the disc.
    /// </summary>
    private static string BookTitle(IReadOnlyList<TrackTags> files, string folder)
    {
        // A single self-contained book takes its name from its own tags or its
        // own filename - never from the folder, which it may share with the
        // rest of a series.
        if (files.Count == 1 && SelfContained.Contains(Path.GetExtension(files[0].Path).ToLowerInvariant()))
            return files[0].Album.HasText && !MusicHealth.Placeholder(files[0].Album.Text)
                ? files[0].Album.Text
                : files[0].Title.HasText && !MusicHealth.Placeholder(files[0].Title.Text)
                    ? files[0].Title.Text
                    : Path.GetFileNameWithoutExtension(files[0].Path);

        var albums = files.Where(f => f.Album.HasText)
            .Select(f => f.Album.Text)
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (albums.Count == 1) return albums[0].Key;

        // Several album values, or none. The folder is the better witness.
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        return name.Length > 0 ? name : albums.FirstOrDefault()?.Key ?? "Unknown book";
    }

    /// <summary>
    /// The author. On a book the artist field usually holds them, but a
    /// publisher's rip puts the narrator there instead, and the album artist is
    /// the more reliable of the two when they disagree.
    /// </summary>
    private static string? Author(IReadOnlyList<TrackTags> files)
    {
        var candidates = files.Select(f => f.AlbumArtist.HasText ? f.AlbumArtist : f.Artist)
            .Where(v => v.HasText && !MusicHealth.Placeholder(v.Text))
            .Select(v => v.Text)
            .ToList();

        if (candidates.Count == 0) return null;

        return candidates.GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First().Key;
    }

    /// <summary>
    /// The narrator, where a tagger wrote one. Only taken when the artist and
    /// album artist disagree, which is the shape that means one is the author
    /// and the other is whoever read it.
    /// </summary>
    private static string? Narrator(IReadOnlyList<TrackTags> files)
    {
        var first = files.FirstOrDefault(f =>
            f.Artist.HasText && f.AlbumArtist.HasText
            && !string.Equals(f.Artist.Text, f.AlbumArtist.Text, StringComparison.OrdinalIgnoreCase));

        return first?.Artist.Text;
    }
}
