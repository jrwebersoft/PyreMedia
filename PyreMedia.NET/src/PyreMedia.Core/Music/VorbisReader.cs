using System.Buffers.Binary;
using System.Text;

namespace PyreMedia.Core.Music;

/// <summary>
/// Reads Vorbis comments - the tag format FLAC and Ogg use.
///
/// The easy one. Vorbis comments are defined as UTF-8, so unlike ID3 there is no
/// encoding to guess at and nothing to get wrong: what the bytes say is what was
/// written. Field names are free-form and case-insensitive, which is the only
/// real awkwardness, since taggers disagree about ALBUMARTIST vs ALBUM ARTIST.
/// </summary>
public static class VorbisReader
{
    /// <summary>A comment block bigger than this is corrupt, not ambitious.</summary>
    private const int MaxBlock = 16 * 1024 * 1024;

    public static void ReadFlac(string path, TrackTags into)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var magic = new byte[4];
        if (file.Read(magic, 0, 4) != 4) return;
        if (magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C') return;

        // METADATA_BLOCK_HEADER: 1 bit last-block, 7 bits type, 24 bits length.
        var header = new byte[4];

        while (file.Read(header, 0, 4) == 4)
        {
            var last = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var length = header[1] << 16 | header[2] << 8 | header[3];

            if (length < 0 || length > MaxBlock) return;

            if (type == 4)                                  // VORBIS_COMMENT
            {
                var block = new byte[length];
                if (file.Read(block, 0, length) != length) return;

                into.Sources.Add(TagSource.VorbisComment);
                ParseComments(block, into);
                return;
            }

            if (last) return;
            file.Seek(length, SeekOrigin.Current);
        }
    }

    /// <summary>
    /// The comment block body: a vendor string, a count, then that many
    /// "NAME=value" entries, each with a 32-bit little-endian length.
    ///
    /// Shared with the Ogg reader. The bytes are identical wherever they are
    /// found - only the wrapper differs, a FLAC metadata block in one case and an
    /// Ogg packet in the other.
    /// </summary>
    internal static void ParseComments(ReadOnlySpan<byte> block, TrackTags into)
    {
        var at = 0;

        if (!TakeLength(block, ref at, out var vendorLength)) return;
        at += vendorLength;

        if (!TakeLength(block, ref at, out var count)) return;
        if (count is < 0 or > 10_000) return;

        for (var i = 0; i < count; i++)
        {
            if (!TakeLength(block, ref at, out var length)) return;
            if (length < 0 || at + length > block.Length) return;

            var entry = Encoding.UTF8.GetString(block.Slice(at, length));
            at += length;

            var split = entry.IndexOf('=');
            if (split <= 0) continue;

            Field(entry[..split], entry[(split + 1)..].Trim(), into);
        }

        static bool TakeLength(ReadOnlySpan<byte> block, ref int at, out int value)
        {
            value = 0;
            if (at + 4 > block.Length) return false;

            value = (int)BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(at, 4));
            at += 4;
            return value >= 0;
        }
    }

    /// <summary>
    /// Apply one field. Shared with the ASF reader, since both formats end up as
    /// a name and a UTF-8 value and disagree in the same ways about naming.
    /// </summary>
    internal static void Field(string name, string value, TrackTags into,
                               TagSource source = TagSource.VorbisComment)
    {
        if (value.Length == 0) return;

        var tag = new TagValue(value, source, TextConfidence.Declared);

        switch (name.Trim().ToLowerInvariant().Replace("_", " "))
        {
            case "title": into.Title = tag; break;
            case "artist": into.Artist = tag; break;
            case "album": into.Album = tag; break;
            case "albumartist" or "album artist": into.AlbumArtist = tag; break;
            case "genre": into.Genre = tag; break;
            case "date" or "year" or "originaldate": if (!into.Year.HasText) into.Year = tag; break;

            case "tracknumber" or "track": into.TrackNumber = Number(value) ?? into.TrackNumber; break;
            case "tracktotal" or "totaltracks": into.TrackCount = Number(value) ?? into.TrackCount; break;
            case "discnumber" or "disc": into.DiscNumber = Number(value) ?? into.DiscNumber; break;
            case "disctotal" or "totaldiscs": into.DiscCount = Number(value) ?? into.DiscCount; break;

            case "musicbrainz albumid" or "musicbrainz album id":
                into.MusicBrainzAlbumId = Identifier.Better(into.MusicBrainzAlbumId, value); break;
            case "musicbrainz trackid" or "musicbrainz track id" or "musicbrainz releasetrackid":
                into.MusicBrainzTrackId = Identifier.Better(into.MusicBrainzTrackId, value); break;
            case "musicbrainz artistid" or "musicbrainz artist id" or "musicbrainz albumartistid":
                into.MusicBrainzArtistId ??= value; break;
        }

        // "3/12" turns up here too, despite the format having separate fields.
        if (name.Trim().ToLowerInvariant() is "tracknumber" or "track" && value.Contains('/'))
            into.TrackCount = Number(value[(value.IndexOf('/') + 1)..]) ?? into.TrackCount;

        if (name.Trim().ToLowerInvariant() is "discnumber" or "disc" && value.Contains('/'))
            into.DiscCount = Number(value[(value.IndexOf('/') + 1)..]) ?? into.DiscCount;

        static int? Number(string s)
        {
            var head = s.Split('/', 2)[0].Trim();
            return int.TryParse(head, out var n) && n > 0 ? n : null;
        }
    }
}
