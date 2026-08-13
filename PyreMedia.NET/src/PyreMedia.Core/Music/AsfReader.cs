using System.Buffers.Binary;
using System.Text;

namespace PyreMedia.Core.Music;

/// <summary>
/// Reads WMA tags - the ASF container's metadata objects.
///
/// ASF is a tree of GUID-identified objects. Two of them carry tags: the Content
/// Description Object, which holds the five fixed fields Windows Media wrote in
/// 2003, and the Extended Content Description Object, which holds arbitrary
/// name/value pairs and is where everything since has gone.
///
/// All text is UTF-16LE by specification, so - as with Vorbis comments and
/// unlike ID3 - there is no encoding to guess.
/// </summary>
public static class AsfReader
{
    private static readonly Guid HeaderObject = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid ContentDescription = new("75B22633-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid ExtendedContent = new("D2D0A440-E307-11D2-97F0-00A0C95EA850");

    private const long MaxHeader = 32 * 1024 * 1024;

    public static void Read(string path, TrackTags into)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var head = new byte[30];
        if (file.Read(head, 0, 30) != 30) return;
        if (new Guid(head.AsSpan(0, 16)) != HeaderObject) return;

        var headerSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16, 8));
        if (headerSize is < 30 or > MaxHeader) return;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(24, 4));
        if (count > 4096) return;

        var found = false;

        for (var i = 0; i < count; i++)
        {
            var objectHeader = new byte[24];
            if (file.Read(objectHeader, 0, 24) != 24) break;

            var id = new Guid(objectHeader.AsSpan(0, 16));
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(objectHeader.AsSpan(16, 8));

            var bodySize = size - 24;
            if (bodySize is < 0 or > MaxHeader) break;

            if (id == ContentDescription || id == ExtendedContent)
            {
                var body = new byte[bodySize];
                if (file.Read(body, 0, (int)bodySize) != bodySize) break;

                if (!found) { into.Sources.Add(TagSource.Asf); found = true; }

                if (id == ContentDescription) Fixed(body, into);
                else Extended(body, into);
            }
            else
            {
                file.Seek(bodySize, SeekOrigin.Current);
            }
        }
    }

    /// <summary>
    /// Five 16-bit lengths, then the five values in that order. Author is the
    /// artist; the rest we mostly don't want.
    /// </summary>
    private static void Fixed(ReadOnlySpan<byte> body, TrackTags into)
    {
        if (body.Length < 10) return;

        Span<int> lengths = stackalloc int[5];
        for (var i = 0; i < 5; i++)
            lengths[i] = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(i * 2, 2));

        var at = 10;
        for (var i = 0; i < 5; i++)
        {
            if (at + lengths[i] > body.Length) return;

            var value = Text(body.Slice(at, lengths[i]));
            at += lengths[i];

            if (value.Length == 0) continue;

            var tag = new TagValue(value, TagSource.Asf, TextConfidence.Declared);

            switch (i)
            {
                case 0: into.Title = tag; break;
                case 1: into.Artist = tag; break;
                // 2 copyright, 3 description, 4 rating - not wanted
            }
        }
    }

    /// <summary>
    /// A count, then name/value pairs. The value carries a type: 0 is a string,
    /// 3 a 32-bit number, 4 a 64-bit one - and taggers use all three for track
    /// numbers, so each has to be handled.
    /// </summary>
    private static void Extended(ReadOnlySpan<byte> body, TrackTags into)
    {
        if (body.Length < 2) return;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(body[..2]);
        var at = 2;

        for (var i = 0; i < count; i++)
        {
            if (at + 2 > body.Length) return;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(at, 2));
            at += 2;

            if (at + nameLength + 4 > body.Length) return;
            var name = Text(body.Slice(at, nameLength));
            at += nameLength;

            var type = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(at, 2));
            var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(at + 2, 2));
            at += 4;

            if (at + valueLength > body.Length) return;
            var raw = body.Slice(at, valueLength);
            at += valueLength;

            var value = type switch
            {
                0 => Text(raw),
                3 when raw.Length >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(raw).ToString(),
                4 when raw.Length >= 8 => BinaryPrimitives.ReadUInt64LittleEndian(raw).ToString(),
                5 when raw.Length >= 2 => BinaryPrimitives.ReadUInt16LittleEndian(raw).ToString(),
                _ => ""
            };

            if (value.Length == 0) continue;

            // WM/ names map onto the same fields the other formats use.
            var key = name.StartsWith("WM/", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;

            VorbisReader.Field(key switch
            {
                "AlbumTitle" => "album",
                "AlbumArtist" => "albumartist",
                "TrackNumber" => "tracknumber",
                "PartOfSet" => "discnumber",
                "Year" => "date",
                "Genre" => "genre",
                "Author" => "artist",
                _ => key
            }, value, into, TagSource.Asf);
        }
    }

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return "";

        var s = Encoding.Unicode.GetString(bytes);

        var end = s.IndexOf('\0');
        if (end >= 0) s = s[..end];

        return s.Trim();
    }
}
