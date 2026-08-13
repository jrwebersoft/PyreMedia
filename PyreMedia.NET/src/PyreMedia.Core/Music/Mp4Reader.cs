using System.Buffers.Binary;
using System.Text;

namespace PyreMedia.Core.Music;

/// <summary>
/// Reads tags from MP4 and its audio-only spellings - .m4a, .m4b, ALAC.
///
/// The file is a tree of boxes, each a big-endian length and a four-character
/// name, and the tags live a long way down it: moov > udta > meta > ilst. Every
/// tag is then itself a box whose name is the field - "©nam" for the title, the
/// copyright sign being iTunes' marker for a standard field - holding a "data"
/// box that says what type its bytes are.
///
/// Nothing here decodes by guesswork. The data box declares UTF-8 or UTF-16, and
/// track and disc numbers are binary rather than text, so unlike ID3 there is no
/// encoding to get wrong.
/// </summary>
public static class Mp4Reader
{
    /// <summary>A tag value longer than this is a corrupt length, not a long title.</summary>
    private const int MaxValue = 1024 * 1024;

    public static void Read(string path, TrackTags into)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (!Find(file, file.Length, "moov", out var moov)) return;
        if (!Find(file, moov.End, "udta", out var udta)) return;
        if (!Find(file, udta.End, "meta", out var meta)) return;

        // meta is a full box: four bytes of version and flags before its children.
        // Some writers leave them out, so rather than trust the spec, look at what
        // is actually there - a box name where the version should be means there
        // is no version.
        file.Position = meta.Start;
        var peek = new byte[8];

        if (file.Read(peek, 0, 8) != 8) return;
        var skipsVersion = !IsBoxName(peek.AsSpan(4, 4));

        var children = skipsVersion ? meta.Start + 4 : meta.Start;
        file.Position = children;

        if (!Find(file, meta.End, "ilst", out var ilst)) return;

        into.Sources.Add(TagSource.Mp4);
        ReadItems(file, ilst, into);
    }

    /// <summary>Each child of ilst is one tag, named for the field it holds.</summary>
    private static void ReadItems(Stream file, Box ilst, TrackTags into)
    {
        var at = ilst.Start;

        while (at < ilst.End)
        {
            file.Position = at;
            if (!ReadHeader(file, ilst.End, out var name, out var box)) return;

            if (name == "----") ReadFreeform(file, box, into);
            else if (Find(file, box.End, "data", out var data)) ReadValue(file, name, data, into);

            at = box.End;
        }
    }

    /// <summary>
    /// A data box: four bytes saying what the value is, four of locale, then the
    /// value itself.
    /// </summary>
    private static void ReadValue(Stream file, string name, Box data, TrackTags into)
    {
        var length = (int)(data.End - data.Start);
        if (length is < 8 or > MaxValue) return;

        var bytes = new byte[length];
        file.Position = data.Start;
        if (file.Read(bytes, 0, length) != length) return;

        var kind = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)) & 0xFFFFFF;
        var body = bytes.AsSpan(8);

        switch (name)
        {
            // Binary pairs: two reserved bytes, the number, then the total.
            case "trkn" when body.Length >= 6:
                into.TrackNumber = Positive(BinaryPrimitives.ReadUInt16BigEndian(body[2..]));
                into.TrackCount = Positive(BinaryPrimitives.ReadUInt16BigEndian(body[4..]));
                return;

            case "disk" when body.Length >= 6:
                into.DiscNumber = Positive(BinaryPrimitives.ReadUInt16BigEndian(body[2..]));
                into.DiscCount = Positive(BinaryPrimitives.ReadUInt16BigEndian(body[4..]));
                return;

            // The old numeric genre: an index into the ID3v1 list, one-based here
            // where ID3 itself counts from zero.
            case "gnre" when body.Length >= 2 && !into.Genre.HasText:
                var index = BinaryPrimitives.ReadUInt16BigEndian(body) - 1;
                if (index >= 0 && index < Id3Reader.Genres.Length)
                    into.Genre = new TagValue(Id3Reader.Genres[index], TagSource.Mp4, TextConfidence.Declared);
                return;
        }

        var text = Decode(body, kind);
        if (text.Length == 0) return;

        var tag = new TagValue(text, TagSource.Mp4, TextConfidence.Declared);

        switch (name)
        {
            case "©nam": into.Title = tag; break;
            case "©ART": into.Artist = tag; break;
            case "aART": into.AlbumArtist = tag; break;
            case "©alb": into.Album = tag; break;
            case "©gen": into.Genre = tag; break;
            case "©day": if (!into.Year.HasText) into.Year = tag; break;

            // Some taggers write the numbers as text after all.
            case "©trk": into.TrackNumber ??= Number(text); break;
            case "©dis": into.DiscNumber ??= Number(text); break;
        }
    }

    /// <summary>
    /// A "----" box is a freeform tag: a mean box naming the writer, a name box
    /// naming the field, and the data. This is where MusicBrainz ids live.
    /// </summary>
    private static void ReadFreeform(Stream file, Box box, TrackTags into)
    {
        string? field = null;
        var at = box.Start;

        while (at < box.End)
        {
            file.Position = at;
            if (!ReadHeader(file, box.End, out var name, out var child)) return;

            var length = (int)(child.End - child.Start);

            if (name == "name" && length is > 4 and < 256)
            {
                var bytes = new byte[length];
                file.Position = child.Start;

                if (file.Read(bytes, 0, length) == length)
                    field = Encoding.UTF8.GetString(bytes.AsSpan(4)).Trim();
            }
            else if (name == "data" && field is not null && length is >= 8 and <= MaxValue)
            {
                var bytes = new byte[length];
                file.Position = child.Start;

                if (file.Read(bytes, 0, length) == length)
                {
                    var value = Encoding.UTF8.GetString(bytes.AsSpan(8)).Trim();
                    if (value.Length > 0) Freeform(field, value, into);
                }

                field = null;
            }

            at = child.End;
        }
    }

    private static void Freeform(string field, string value, TrackTags into)
    {
        switch (field.ToLowerInvariant())
        {
            case "musicbrainz album id": into.MusicBrainzAlbumId = Identifier.Better(into.MusicBrainzAlbumId, value); break;
            case "musicbrainz track id" or "musicbrainz release track id":
                into.MusicBrainzTrackId = Identifier.Better(into.MusicBrainzTrackId, value); break;
            case "musicbrainz artist id" or "musicbrainz album artist id":
                into.MusicBrainzArtistId = Identifier.Better(into.MusicBrainzArtistId, value); break;
        }
    }

    /// <summary>
    /// The data box says which encoding it used: 1 is UTF-8, 2 is UTF-16. Anything
    /// else holding text is treated as UTF-8, which is what writers that leave the
    /// field at 0 almost always mean.
    /// </summary>
    private static string Decode(ReadOnlySpan<byte> body, uint kind) => kind switch
    {
        2 => Encoding.BigEndianUnicode.GetString(body).Trim('\0', ' '),
        _ => Encoding.UTF8.GetString(body).Trim('\0', ' ')
    };

    private readonly record struct Box(long Start, long End);

    /// <summary>Scan boxes from the current position for one with this name.</summary>
    private static bool Find(Stream file, long end, string wanted, out Box box)
    {
        box = default;

        while (file.Position < end)
        {
            var at = file.Position;
            if (!ReadHeader(file, end, out var name, out var found)) return false;

            if (name == wanted) { box = found; file.Position = found.Start; return true; }
            if (found.End <= at) return false;

            file.Position = found.End;
        }

        return false;
    }

    /// <summary>
    /// A box header: a 32-bit length that includes the header, then the name. A
    /// length of 1 means a 64-bit length follows; 0 means the box runs to the end.
    /// </summary>
    private static bool ReadHeader(Stream file, long end, out string name, out Box box)
    {
        name = "";
        box = default;

        var start = file.Position;
        if (start + 8 > end) return false;

        var header = new byte[8];
        if (file.Read(header, 0, 8) != 8) return false;

        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        name = Encoding.Latin1.GetString(header, 4, 4);

        var headerLength = 8L;

        if (size == 1)
        {
            var extended = new byte[8];
            if (file.Read(extended, 0, 8) != 8) return false;

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(extended);
            headerLength = 16;
        }
        else if (size == 0)
        {
            size = end - start;
        }

        if (size < headerLength || start + size > end) return false;

        box = new Box(start + headerLength, start + size);
        return true;
    }

    /// <summary>Four printable characters, which is what a box name always is.</summary>
    private static bool IsBoxName(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b is < 0x20 or > 0x7E && b != 0xA9) return false;

        return true;
    }

    private static int? Positive(int value) => value > 0 ? value : null;

    private static int? Number(string text) =>
        int.TryParse(text.Split('/', 2)[0].Trim(), out var n) && n > 0 ? n : null;
}
