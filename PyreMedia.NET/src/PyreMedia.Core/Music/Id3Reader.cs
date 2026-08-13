using System.Text;

namespace PyreMedia.Core.Music;

/// <summary>
/// Reads ID3 tags from the bytes of an MP3.
///
/// Both kinds, because thirty years of taggers wrote both and they disagree: the
/// 128-byte ID3v1 block on the end, and ID3v2.2/.3/.4 at the front. Where a file
/// has both, v2 wins on every field it carries and v1 fills the gaps - v2 is
/// newer, longer and actually records its encoding.
/// </summary>
public static class Id3Reader
{
    /// <summary>How much of a file to pull in for the v2 header and frames.</summary>
    private const int MaxTagBytes = 4 * 1024 * 1024;

    public static void Read(string path, TrackTags into)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // v1 first, so v2 can overwrite it field by field.
        ReadV1(file, into);
        ReadV2(file, into);
    }

    // ---------------- ID3v1 ----------------

    private static void ReadV1(FileStream file, TrackTags into)
    {
        if (file.Length < 128) return;

        var block = new byte[128];
        file.Seek(-128, SeekOrigin.End);
        if (file.ReadExactlyOrZero(block) != 128) return;

        if (block[0] != 'T' || block[1] != 'A' || block[2] != 'G') return;

        into.Sources.Add(TagSource.Id3v1);

        into.Title = Field(block, 3, 30);
        into.Artist = Field(block, 33, 30);
        into.Album = Field(block, 63, 30);
        into.Year = Field(block, 93, 4);

        // ID3v1.1 stole the last two bytes of the comment for a track number.
        // Only valid when the byte before it is zero - otherwise it is comment.
        if (block[125] == 0 && block[126] != 0) into.TrackNumber = block[126];

        var genre = block[127];
        if (genre < Genres.Length) into.Genre = new TagValue(Genres[genre], TagSource.Id3v1, TextConfidence.Ascii);

        static TagValue Field(byte[] block, int offset, int length)
        {
            var span = block.AsSpan(offset, length);

            // Trim NUL and space padding before decoding - trailing rubbish
            // skews the codepage guess.
            var end = span.Length;
            while (end > 0 && (span[end - 1] == 0 || span[end - 1] == 0x20)) end--;
            if (end == 0) return TagValue.Empty;

            var (text, confidence) = TextDecoding.Guess(span[..end]);
            return text.Length == 0 ? TagValue.Empty : new TagValue(text, TagSource.Id3v1, confidence);
        }
    }

    // ---------------- ID3v2 ----------------

    private static void ReadV2(FileStream file, TrackTags into)
    {
        if (file.Length < 10) return;

        file.Seek(0, SeekOrigin.Begin);
        var header = new byte[10];
        if (file.ReadExactlyOrZero(header) != 10) return;

        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return;

        var major = header[3];
        if (major is < 2 or > 4) return;                  // not a version we know

        var size = SyncSafe(header, 6);
        if (size <= 0 || size > MaxTagBytes) return;

        var body = new byte[size];
        if (file.ReadExactlyOrZero(body) != size) return;

        into.Sources.Add(TagSource.Id3v2);

        // An unsynchronised v2.3 tag has 0xFF00 stuffed through it. v2.4 does it
        // per frame, which we don't unpick - those are rare and the frames we
        // want are text, which rarely contains 0xFF.
        if (major == 3 && (header[5] & 0x80) != 0) body = Unsynchronise(body);

        var extended = (header[5] & 0x40) != 0;
        var at = 0;

        if (extended && major >= 3)
        {
            if (body.Length < 4) return;
            var extendedSize = major == 4 ? SyncSafe(body, 0) : BeInt(body, 0) + 4;
            at = Math.Clamp(extendedSize, 0, body.Length);
        }

        var idLength = major == 2 ? 3 : 4;
        var sizeLength = major == 2 ? 3 : 4;

        while (at + idLength + sizeLength <= body.Length)
        {
            var id = Encoding.ASCII.GetString(body, at, idLength);

            // Padding: the rest of the tag is zeroes.
            if (id[0] == '\0') break;

            int frameSize;
            if (major == 2) frameSize = (body[at + 3] << 16) | (body[at + 4] << 8) | body[at + 5];
            else if (major == 4) frameSize = SyncSafe(body, at + 4);
            else frameSize = BeInt(body, at + 4);

            var headerLength = major == 2 ? 6 : 10;
            var start = at + headerLength;

            if (frameSize <= 0 || start + frameSize > body.Length) break;

            Frame(id, body.AsSpan(start, frameSize), into);
            at = start + frameSize;
        }
    }

    private static void Frame(string id, ReadOnlySpan<byte> data, TrackTags into)
    {
        // TXXX carries a name/value pair - MusicBrainz ids live here.
        if (id is "TXXX" or "TXX")
        {
            UserText(data, into);
            return;
        }

        if (id.Length == 0 || id[0] != 'T') return;       // only text frames

        var value = Text(data, out var confidence);
        if (value.Length == 0) return;

        var tag = new TagValue(value, TagSource.Id3v2, confidence);

        switch (id)
        {
            case "TIT2" or "TT2": into.Title = tag; break;
            case "TPE1" or "TP1": into.Artist = tag; break;
            case "TALB" or "TAL": into.Album = tag; break;
            case "TPE2" or "TP2": into.AlbumArtist = tag; break;
            case "TCON" or "TCO": into.Genre = new TagValue(Genre(value), TagSource.Id3v2, confidence); break;

            case "TYER" or "TYE" or "TDRC" or "TDRL":
                if (!into.Year.HasText || into.Year.Source == TagSource.Id3v1) into.Year = tag;
                break;

            case "TRCK" or "TRK":
                (into.TrackNumber, into.TrackCount) = Pair(value, into.TrackNumber, into.TrackCount);
                break;

            case "TPOS" or "TPA":
                (into.DiscNumber, into.DiscCount) = Pair(value, into.DiscNumber, into.DiscCount);
                break;
        }
    }

    private static void UserText(ReadOnlySpan<byte> data, TrackTags into)
    {
        if (data.Length < 2) return;

        var encoding = data[0];
        var rest = data[1..];

        // description NUL value, with the terminator's width set by the encoding
        var wide = encoding is 1 or 2;
        var split = FindTerminator(rest, wide);
        if (split < 0) return;

        var name = Decode(encoding, rest[..split], out _);
        var after = split + (wide ? 2 : 1);
        if (after >= rest.Length) return;

        var value = Decode(encoding, rest[after..], out _);
        if (value.Length == 0) return;

        switch (name.Trim().ToLowerInvariant())
        {
            case "musicbrainz release id" or "musicbrainz album id":
                into.MusicBrainzAlbumId = Identifier.Better(into.MusicBrainzAlbumId, value); break;
            case "musicbrainz track id" or "musicbrainz release track id":
                into.MusicBrainzTrackId = Identifier.Better(into.MusicBrainzTrackId, value); break;
            case "musicbrainz artist id":
                into.MusicBrainzArtistId = Identifier.Better(into.MusicBrainzArtistId, value); break;
            case "albumartist" when !into.AlbumArtist.HasText:
                into.AlbumArtist = new TagValue(value, TagSource.Id3v2, TextConfidence.Declared); break;
        }
    }

    private static string Text(ReadOnlySpan<byte> data, out TextConfidence confidence)
    {
        confidence = TextConfidence.None;
        if (data.Length < 2) return "";

        return Decode(data[0], data[1..], out confidence);
    }

    private static string Decode(byte encoding, ReadOnlySpan<byte> bytes, out TextConfidence confidence)
    {
        switch (encoding)
        {
            case 1:     // UTF-16 with BOM
            {
                var (t, c) = TextDecoding.Declared(bytes, Encoding.Unicode);
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                    (t, c) = TextDecoding.Declared(bytes[2..], Encoding.Unicode);
                else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                    (t, c) = TextDecoding.Declared(bytes[2..], Encoding.BigEndianUnicode);

                confidence = c;
                return t;
            }

            case 2:     // UTF-16BE, no BOM
            {
                var (t, c) = TextDecoding.Declared(bytes, Encoding.BigEndianUnicode);
                confidence = c;
                return t;
            }

            case 3:     // UTF-8
            {
                var (t, c) = TextDecoding.Declared(bytes, new UTF8Encoding(false, false));
                confidence = c;
                return t;
            }

            default:
                // 0 means Latin-1 by the spec, and by the spec alone: taggers
                // wrote their local codepage into this and called it Latin-1.
                // So it gets the same treatment as ID3v1 - guessed, and labelled
                // as such.
            {
                var (t, c) = TextDecoding.Guess(bytes);
                confidence = c;
                return t;
            }
        }
    }

    // ---------------- helpers ----------------

    /// <summary>"3/12" and "3" both turn up. Existing values are not overwritten with nothing.</summary>
    private static (int?, int?) Pair(string value, int? number, int? count)
    {
        var parts = value.Split('/', 2);

        if (int.TryParse(parts[0].Trim(), out var n) && n > 0) number = n;
        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var c) && c > 0) count = c;

        return (number, count);
    }

    /// <summary>ID3v2 genres can be "(17)", "17" or the name itself.</summary>
    private static string Genre(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('(') && trimmed.IndexOf(')') > 1)
        {
            var inner = trimmed[1..trimmed.IndexOf(')')];
            if (int.TryParse(inner, out var i) && i < Genres.Length) return Genres[i];
        }

        if (int.TryParse(trimmed, out var n) && n < Genres.Length) return Genres[n];
        return trimmed;
    }

    private static int FindTerminator(ReadOnlySpan<byte> data, bool wide)
    {
        if (!wide) return data.IndexOf((byte)0);

        for (var i = 0; i + 1 < data.Length; i += 2)
            if (data[i] == 0 && data[i + 1] == 0) return i;

        return -1;
    }

    private static int SyncSafe(byte[] b, int at) =>
        (b[at] & 0x7F) << 21 | (b[at + 1] & 0x7F) << 14 | (b[at + 2] & 0x7F) << 7 | (b[at + 3] & 0x7F);

    private static int BeInt(byte[] b, int at) =>
        b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

    private static byte[] Unsynchronise(byte[] body)
    {
        var output = new List<byte>(body.Length);

        for (var i = 0; i < body.Length; i++)
        {
            output.Add(body[i]);
            if (body[i] == 0xFF && i + 1 < body.Length && body[i + 1] == 0x00) i++;
        }

        return [.. output];
    }

    /// <summary>
    /// The ID3v1 genre list, by its numeric id.
    ///
    /// The specification defines 0-79. Everything past that is Winamp's, added
    /// in two batches, and it is not optional trivia: 359 files in one measured
    /// library carry a bare "(80)", which is Folk, and stopping at the spec
    /// leaves every one of them reading as an unknown number.
    ///
    /// Index 133 is a racial slur in the original list. It is not reproduced
    /// here. Files carrying it will read as "Afro-Punk", which is what the
    /// genre came to be called, and which is a better answer than the number.
    /// </summary>
    internal static readonly string[] Genres =
    [
        "Blues","Classic Rock","Country","Dance","Disco","Funk","Grunge","Hip-Hop","Jazz","Metal",
        "New Age","Oldies","Other","Pop","R&B","Rap","Reggae","Rock","Techno","Industrial",
        "Alternative","Ska","Death Metal","Pranks","Soundtrack","Euro-Techno","Ambient","Trip-Hop",
        "Vocal","Jazz+Funk","Fusion","Trance","Classical","Instrumental","Acid","House","Game",
        "Sound Clip","Gospel","Noise","Alt. Rock","Bass","Soul","Punk","Space","Meditative",
        "Instrumental Pop","Instrumental Rock","Ethnic","Gothic","Darkwave","Techno-Industrial",
        "Electronic","Pop-Folk","Eurodance","Dream","Southern Rock","Comedy","Cult","Gangsta Rap",
        "Top 40","Christian Rap","Pop/Funk","Jungle","Native American","Cabaret","New Wave",
        "Psychedelic","Rave","Showtunes","Trailer","Lo-Fi","Tribal","Acid Punk","Acid Jazz","Polka",
        "Retro","Musical","Rock & Roll","Hard Rock",

        // Winamp's extension, 80-147.
        "Folk","Folk-Rock","National Folk","Swing","Fast Fusion","Bebob","Latin","Revival","Celtic",
        "Bluegrass","Avantgarde","Gothic Rock","Progressive Rock","Psychedelic Rock",
        "Symphonic Rock","Slow Rock","Big Band","Chorus","Easy Listening","Acoustic","Humour",
        "Speech","Chanson","Opera","Chamber Music","Sonata","Symphony","Booty Bass","Primus",
        "Porn Groove","Satire","Slow Jam","Club","Tango","Samba","Folklore","Ballad","Power Ballad",
        "Rhythmic Soul","Freestyle","Duet","Punk Rock","Drum Solo","A Cappella","Euro-House",
        "Dance Hall","Goa","Drum & Bass","Club-House","Hardcore","Terror","Indie","BritPop",
        "Afro-Punk","Polsk Punk","Beat","Christian Gangsta Rap","Heavy Metal","Black Metal",
        "Crossover","Contemporary Christian","Christian Rock","Merengue","Salsa","Thrash Metal",
        "Anime","JPop","Synthpop",

        // Winamp 5.6, 148-191.
        "Abstract","Art Rock","Baroque","Bhangra","Big Beat","Breakbeat","Chillout","Downtempo",
        "Dub","EBM","Eclectic","Electro","Electroclash","Emo","Experimental","Garage","Global",
        "IDM","Illbient","Industro-Goth","Jam Band","Krautrock","Leftfield","Lounge","Math Rock",
        "New Romantic","Nu-Breakz","Post-Punk","Post-Rock","Psytrance","Shoegaze","Space Rock",
        "Trop Rock","World Music","Neoclassical","Audiobook","Audio Theatre","Neue Deutsche Welle",
        "Podcast","Indie Rock","G-Funk","Dubstep","Garage Rock","Psybient"
    ];

    private static int ReadExactlyOrZero(this FileStream file, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = file.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
