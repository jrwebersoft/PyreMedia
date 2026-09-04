namespace PyreMedia.Core.Music;

/// <summary>Where a piece of tag text came from, and how much it can be trusted.</summary>
public enum TagSource
{
    None,

    /// <summary>
    /// The 128 bytes on the end of an MP3. No encoding field, no length beyond
    /// 30 characters, no disc number. The oldest thing in a library and the
    /// least reliable, but often the only thing a 1998 rip has.
    /// </summary>
    Id3v1,

    /// <summary>ID3v2.2, .3 or .4 at the front of the file.</summary>
    Id3v2,

    /// <summary>FLAC and Ogg. Always UTF-8, so text from here is never in doubt.</summary>
    VorbisComment,

    /// <summary>WMA and other ASF. Always UTF-16LE.</summary>
    Asf,

    /// <summary>APEv2, as Monkey's Audio and some MP3 taggers write.</summary>
    Ape,

    /// <summary>
    /// The iTunes-style metadata boxes in an MP4: .m4a, .m4b, ALAC. Every value
    /// declares its own encoding, so nothing here is ever guessed at.
    /// </summary>
    Mp4,

    /// <summary>
    /// Read off the filename, because the file's own tags were silent. Good
    /// evidence - somebody named that file from the sleeve - but it lives
    /// nowhere except the name, and a rename would lose it.
    /// </summary>
    Filename,

    /// <summary>
    /// Taken from a scraped album.nfo sitting beside the file.
    ///
    /// The most perishable thing in a library. These files hold MusicBrainz
    /// ids and exact track lengths that nothing else here can derive, and the
    /// sweep deletes them - so anything sourced this way has to be written
    /// into the audio file before that happens or it is gone for good.
    /// </summary>
    Nfo
}

/// <summary>
/// One text value, with where it came from and how confident the decoding is.
///
/// The confidence matters because a library assembled over thirty years has
/// tags written by programs that disagreed about encoding, and a value that had
/// to be guessed at should not be written back over the top of one that didn't.
/// </summary>
public sealed record TagValue(string Text, TagSource Source, TextConfidence Confidence)
{
    public static readonly TagValue Empty = new("", TagSource.None, TextConfidence.None);

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public override string ToString() => Text;
}

/// <summary>How sure we are that the characters are the ones originally written.</summary>
public enum TextConfidence
{
    None,

    /// <summary>
    /// The encoding was declared by the format and honoured - UTF-8, UTF-16, or
    /// an ID3v2 frame that said which it was. The text is what was written.
    /// </summary>
    Declared,

    /// <summary>
    /// Pure ASCII. No encoding question arises, whatever the format claims.
    /// </summary>
    Ascii,

    /// <summary>
    /// Non-ASCII bytes in a format that does not record its encoding, decoded by
    /// the best guess available. Probably right, worth a human glance before it
    /// is written anywhere.
    /// </summary>
    Guessed,

    /// <summary>
    /// Non-ASCII bytes that no candidate encoding read sensibly. Shown so the
    /// file can be found, never written back.
    /// </summary>
    Unreadable
}

/// <summary>
/// Values with a known shape, checked before they are believed.
///
/// Written because a tagger loose in this library corrupted every TXXX frame
/// it touched, writing each value as its own field name shifted one character:
/// "MusicBrainz Album Id" came back as "usicBrainz Album Id", "SCRIPT" as
/// "CRIP". 231 files carry that damage. The readers were parsing it perfectly
/// and storing nonsense, and nothing downstream could tell - an id is just a
/// string, so a wrong one looks exactly like a right one until it is looked up
/// and returns somebody else's record.
///
/// A field with a known shape should refuse anything that is not that shape.
/// It costs nothing and it turns a silent wrong answer into an absent one,
/// which every rule here already knows how to handle.
/// </summary>
public static class Identifier
{
    /// <summary>
    /// A MusicBrainz id if it is one, or null. They are UUIDs, always, and
    /// dashes are optional in the wild so both forms are accepted.
    /// </summary>
    /// <summary>
    /// Keep <paramref name="current"/> unless <paramref name="value"/> is a
    /// valid id. Rejecting a bad value must not also discard a good one read a
    /// moment earlier - a file with two frames for the same field would
    /// otherwise end up with whichever came last, valid or not.
    /// </summary>
    public static string? Better(string? current, string? value) =>
        MusicBrainz(value) ?? current;

    public static string? MusicBrainz(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();

        // Guid.TryParse also accepts braces and parentheses, which no tagger
        // writes and no service returns; the exact forms are spelt out so a
        // "{...}" cannot round-trip into a lookup that will not match.
        return Guid.TryParseExact(text, "D", out _) || Guid.TryParseExact(text, "N", out _)
            ? text
            : null;
    }
}

/// <summary>
/// Everything read from one audio file: what the tags say, and what the file
/// itself is. Deliberately separate from any judgement about what it *should*
/// say - this is evidence, not a decision.
/// </summary>
public sealed class TrackTags
{
    /// <summary>
    /// Where the file is now.
    ///
    /// Settable because a file that has just been moved is somewhere else, and
    /// everything holding one of these needs to know. Fixed at scan time, it
    /// meant "fix the tags inside the files" ran after Apply had moved them and
    /// failed on every single one with "the file is not there" - the count said
    /// zero written and nothing said why.
    /// </summary>
    public required string Path { get; set; }

    public TagValue Title { get; set; } = TagValue.Empty;
    public TagValue Artist { get; set; } = TagValue.Empty;
    public TagValue Album { get; set; } = TagValue.Empty;
    public TagValue AlbumArtist { get; set; } = TagValue.Empty;
    public TagValue Genre { get; set; } = TagValue.Empty;

    /// <summary>Year as written. Kept as text: "1959", "1959-08-17" and "59" all turn up.</summary>
    public TagValue Year { get; set; } = TagValue.Empty;

    public int? TrackNumber { get; set; }
    public int? TrackCount { get; set; }
    public int? DiscNumber { get; set; }
    public int? DiscCount { get; set; }

    /// <summary>
    /// MusicBrainz ids, when a previous tagger left them. Gold when present -
    /// and worthless when wrong, which is why nothing reaches these fields
    /// without being a UUID first. See <see cref="Identifier"/>.
    /// </summary>
    public string? MusicBrainzTrackId { get; set; }
    public string? MusicBrainzAlbumId { get; set; }
    public string? MusicBrainzArtistId { get; set; }

    /// <summary>
    /// How long the audio runs, once something has measured it. Not read from the
    /// tags - it costs a probe, so it is filled in only for the files where it
    /// settles a question, which in practice means the ones whose track numbers
    /// collide.
    /// </summary>
    public double? Seconds { get; set; }

    /// <summary>Kilobits per second, from the same probe as <see cref="Seconds"/>.</summary>
    public int? Kbps { get; set; }

    /// <summary>Every tag block found, in the order they were read.</summary>
    public List<TagSource> Sources { get; } = [];

    /// <summary>
    /// Fields filled from outside the file - a filename or a scraped .nfo -
    /// rather than read out of it.
    ///
    /// Needed because the identifiers below are plain strings with nowhere to
    /// record where they came from, unlike the text fields which carry a
    /// TagSource. Without this the harvest counts every id a file already had
    /// embedded as being at risk, and nags about data that is already safe.
    /// </summary>
    public HashSet<string> Gathered { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a file carries more than one tag block that disagree.</summary>
    public List<string> Conflicts { get; } = [];

    /// <summary>Set when the file could not be read at all.</summary>
    public string? Error { get; set; }

    public bool HasAnyText =>
        Title.HasText || Artist.HasText || Album.HasText || AlbumArtist.HasText;

    /// <summary>
    /// Nothing usable to identify this file by. These are the ones that need
    /// fingerprinting or a human, and counting them is the first thing worth
    /// knowing about a library.
    /// </summary>
    public bool IsUntagged => Error is null && !HasAnyText;

    /// <summary>Any text that had to be guessed at, or could not be decoded.</summary>
    public bool HasDoubtfulText =>
        new[] { Title, Artist, Album, AlbumArtist, Genre }
            .Any(v => v.Confidence is TextConfidence.Guessed or TextConfidence.Unreadable);

    public override string ToString() =>
        Error is not null
            ? $"{System.IO.Path.GetFileName(Path)}: {Error}"
            : $"{Artist} - {Album} - {TrackNumber:00} {Title}";
}
