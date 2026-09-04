using System.Buffers.Binary;
using System.Text;

namespace PyreMedia.Core.Music;

/// <summary>
/// Writes WMA tags, by rewriting the ASF header and copying the audio verbatim.
///
/// Built rather than borrowed, and rewritten rather than remuxed, for the same
/// reason in both cases: this is the narrow half of a format we already parse.
/// <see cref="AsfReader"/> knows where these objects live and what the fields
/// are called; a second tag library beside it could disagree with it about
/// either, and the disagreement would only show up as a file that reads back
/// differently from how it was written. Sharing the GUIDs and the name mapping
/// with the reader makes that impossible by construction.
///
/// Passing a WMA through ffmpeg would remux the whole container. Here the Data
/// Object - the audio - is copied byte for byte and never parsed, so the only
/// bytes that change are the ones holding the tags.
///
/// Three things have to stay consistent or players reject the file: the header
/// object's declared size, its count of child objects, and the File Size field
/// inside the File Properties Object, which counts the header too. The index
/// objects are safe to leave alone - they address packets by number rather than
/// by byte offset, so moving the audio does not invalidate them.
/// </summary>
public static class AsfWriter
{
    private static readonly Guid HeaderObject = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid ContentDescription = new("75B22633-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid ExtendedContent = new("D2D0A440-E307-11D2-97F0-00A0C95EA850");
    private static readonly Guid FileProperties = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    private static readonly Guid Padding = new("1806D474-CADF-4509-A4BA-9AABCB96AAE8");

    private const long MaxHeader = 32 * 1024 * 1024;

    /// <summary>
    /// Apply an edit to a WMA, writing to <paramref name="target"/>. Returns
    /// null on success or a reason it could not be done.
    ///
    /// Never writes over the source, so a caller can verify the result before
    /// deciding to keep it.
    /// </summary>
    public static string? Write(string source, string target, TagEdit edit)
    {
        using var file = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);

        var head = new byte[30];
        if (file.Read(head, 0, 30) != 30) return "the file is too short to be an ASF header";
        if (new Guid(head.AsSpan(0, 16)) != HeaderObject) return "this is not an ASF file";

        var headerSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16, 8));
        if (headerSize is < 30 or > MaxHeader) return $"the header claims to be {headerSize} bytes";

        var count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(24, 4));
        if (count > 4096) return $"the header claims {count} objects";

        // Every child object, read whole. The header is a few kilobytes at most,
        // so holding it in memory costs nothing and makes the rewrite a matter
        // of swapping two entries in a list.
        var objects = new List<(Guid Id, byte[] Body)>();

        for (var i = 0; i < count; i++)
        {
            var objectHeader = new byte[24];
            if (file.Read(objectHeader, 0, 24) != 24) return "the header ended early";

            var id = new Guid(objectHeader.AsSpan(0, 16));
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(objectHeader.AsSpan(16, 8));
            var bodySize = size - 24;

            if (bodySize is < 0 or > MaxHeader) return $"an object claims to be {size} bytes";

            var body = new byte[bodySize];
            if (bodySize > 0 && file.Read(body, 0, (int)bodySize) != bodySize)
                return "an object ended early";

            objects.Add((id, body));
        }

        var existingDescription = objects.FirstOrDefault(o => o.Id == ContentDescription).Body;
        var existingExtended = objects.FirstOrDefault(o => o.Id == ExtendedContent).Body;

        var description = BuildDescription(existingDescription, edit);
        var extended = BuildExtended(existingExtended, edit);

        // Padding exists so tags can change size without moving the audio. We
        // move the audio anyway and fix the one field that cares, so padding is
        // dropped rather than recalculated - it would only be reinstated to a
        // size nothing here can choose meaningfully.
        objects.RemoveAll(o => o.Id is var id
            && (id == ContentDescription || id == ExtendedContent || id == Padding));

        // Order matters to some players even though the specification does not
        // require it: File Properties first, tags near the front.
        objects.Add((ContentDescription, description));
        objects.Add((ExtendedContent, extended));

        var newHeaderSize = 30 + objects.Sum(o => 24L + o.Body.Length);
        var delta = newHeaderSize - headerSize;

        // The File Properties Object records the size of the whole file, header
        // included. Leave it stale and the file plays but reports the wrong
        // length to anything that trusts it.
        var properties = objects.FindIndex(o => o.Id == FileProperties);

        if (properties >= 0 && objects[properties].Body.Length >= 24)
        {
            var body = objects[properties].Body;
            var declared = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(16, 8));

            if (declared > 0)
                BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16, 8),
                    (ulong)Math.Max(0, (long)declared + delta));
        }

        using var output = File.Open(target, FileMode.Create, FileAccess.Write, FileShare.None);

        var newHead = new byte[30];
        HeaderObject.TryWriteBytes(newHead.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(newHead.AsSpan(16, 8), (ulong)newHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(newHead.AsSpan(24, 4), (uint)objects.Count);

        // Reserved, and not free: the specification fixes these at 1 and 2, and
        // Windows Media refuses a file with anything else in them.
        newHead[28] = 0x01;
        newHead[29] = 0x02;

        output.Write(newHead);

        foreach (var (id, body) in objects)
        {
            var objectHeader = new byte[24];
            id.TryWriteBytes(objectHeader.AsSpan(0, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(objectHeader.AsSpan(16, 8), (ulong)(24 + body.Length));

            output.Write(objectHeader);
            output.Write(body);
        }

        // Everything after the header - the Data Object and any index - copied
        // without being looked at. This is the audio, and nothing here has any
        // business interpreting it.
        file.Seek(headerSize, SeekOrigin.Begin);
        file.CopyTo(output);

        return null;
    }

    /// <summary>
    /// The Content Description Object: five 16-bit lengths, then five UTF-16LE
    /// values in that order, each including its null terminator.
    ///
    /// Only the first two are ours. Copyright, description and rating are read
    /// off the existing object and written back untouched, because a tag we do
    /// not use is still somebody's data.
    /// </summary>
    private static byte[] BuildDescription(byte[]? existing, TagEdit edit)
    {
        var values = ReadDescription(existing);

        if (edit.Title is not null) values[0] = edit.Title;
        if (edit.Artist is not null) values[1] = edit.Artist;

        var encoded = values.Select(Encode).ToList();
        var body = new byte[10 + encoded.Sum(v => v.Length)];

        for (var i = 0; i < 5; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(i * 2, 2), (ushort)encoded[i].Length);

        var at = 10;
        foreach (var value in encoded)
        {
            value.CopyTo(body.AsSpan(at));
            at += value.Length;
        }

        return body;
    }

    private static string[] ReadDescription(byte[]? body)
    {
        var values = new[] { "", "", "", "", "" };
        if (body is null || body.Length < 10) return values;

        var lengths = new int[5];
        for (var i = 0; i < 5; i++)
            lengths[i] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i * 2, 2));

        var at = 10;
        for (var i = 0; i < 5; i++)
        {
            if (at + lengths[i] > body.Length) break;
            values[i] = Decode(body.AsSpan(at, lengths[i]));
            at += lengths[i];
        }

        return values;
    }

    /// <summary>
    /// The Extended Content Description Object: a count, then name/value
    /// descriptors. Everything already there survives unless this edit names
    /// it - ReplayGain, encoder settings and MusicBrainz ids from a previous
    /// tagger all live here, and dropping them would be a silent loss.
    /// </summary>
    internal static byte[] BuildExtended(byte[]? existing, TagEdit edit)
    {
        var descriptors = ReadExtended(existing);

        void Set(string name, string? value)
        {
            if (value is null) return;

            descriptors.RemoveAll(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

            // An empty value means the field was cleared, and a descriptor with
            // no value is worse than none - readers report it as an empty tag
            // rather than an absent one.
            if (value.Length > 0) descriptors.Add(new Descriptor(name, 0, value, []));
        }

        // The names Windows Media and Kodi actually read. Deliberately the same
        // set AsfReader maps back, so a write and a read agree by construction.
        Set("WM/AlbumTitle", edit.Album);
        Set("WM/AlbumArtist", edit.AlbumArtist);
        Set("WM/TrackNumber", edit.TrackNumber?.ToString());
        Set("WM/PartOfSet", edit.DiscNumber?.ToString());
        Set("WM/Year", edit.Year);
        Set("WM/Genre", edit.Genre);
        Set("MusicBrainz/Track Id", edit.MusicBrainzTrackId);
        Set("MusicBrainz/Album Id", edit.MusicBrainzAlbumId);

        // ReplayGain, which this writer used to ignore entirely. Every WMA was
        // decoded end to end - the slowest thing the program does - rewritten
        // in full, reported as written and recorded as undoable, and not one
        // gain figure reached the file.
        //
        // These are the names Windows Media and foobar2000 both read. The
        // uppercase form is what every other writer here emits, so a library of
        // mixed formats ends up saying the same thing in the same words.
        Set("REPLAYGAIN_TRACK_GAIN", edit.TrackGain);
        Set("REPLAYGAIN_TRACK_PEAK", edit.TrackPeak);
        Set("REPLAYGAIN_ALBUM_GAIN", edit.AlbumGain);
        Set("REPLAYGAIN_ALBUM_PEAK", edit.AlbumPeak);

        using var memory = new MemoryStream();
        var countBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(countBytes, (ushort)descriptors.Count);
        memory.Write(countBytes);

        foreach (var d in descriptors)
        {
            var nameBytes = Encode(d.Name);

            // Bytes this code does not understand go back exactly as they came,
            // under their own type. Text goes back as type 0: track numbers are
            // written as strings rather than as the DWORD some taggers use,
            // because every reader handles the string and only some handle the
            // number.
            var valueBytes = d.IsBinary ? d.Raw : Encode(d.Text);
            var type = d.IsBinary ? d.Type : (ushort)0;

            var lengthBytes = new byte[2];

            BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)nameBytes.Length);
            memory.Write(lengthBytes);
            memory.Write(nameBytes);

            BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, type);
            memory.Write(lengthBytes);

            BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)valueBytes.Length);
            memory.Write(lengthBytes);
            memory.Write(valueBytes);
        }

        return memory.ToArray();
    }

    internal static List<Descriptor> ReadExtended(byte[]? body)
    {
        var descriptors = new List<Descriptor>();
        if (body is null || body.Length < 2) return descriptors;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0, 2));
        var at = 2;

        for (var i = 0; i < count; i++)
        {
            if (at + 2 > body.Length) break;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at, 2));
            at += 2;

            if (at + nameLength + 4 > body.Length) break;
            var name = Decode(body.AsSpan(at, nameLength));
            at += nameLength;

            var type = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at, 2));
            var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 2, 2));
            at += 4;

            if (at + valueLength > body.Length) break;
            var raw = body.AsSpan(at, valueLength);
            at += valueLength;

            // Numeric descriptors are read to their decimal text and written
            // back as strings. That is a real change to the file and it is the
            // right one: it is the form every reader understands, and keeping
            // the original type would mean carrying four encodings of a value
            // through the rewrite for no gain.
            var value = type switch
            {
                0 => Decode(raw),
                3 when raw.Length >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(raw).ToString(),
                4 when raw.Length >= 8 => BinaryPrimitives.ReadUInt64LittleEndian(raw).ToString(),
                5 when raw.Length >= 2 => BinaryPrimitives.ReadUInt16LittleEndian(raw).ToString(),
                _ => null
            };

            if (name.Length == 0) continue;

            // Type 1 is arbitrary bytes, and that is where WM/Picture - the
            // embedded cover art - lives. It used to be turned into an empty
            // string and then dropped for being empty, so correcting a track
            // number silently destroyed the artwork. Nothing here can read it
            // and nothing here needs to, so it is carried through untouched.
            if (value is null)
            {
                descriptors.Add(new Descriptor(name, type, "", raw.ToArray()));
                continue;
            }

            if (value.Length > 0) descriptors.Add(new Descriptor(name, 0, value, []));
        }

        return descriptors;
    }

    /// <summary>
    /// One entry of the Extended Content Description Object. Either text this
    /// code understands, or bytes it does not and passes through as they were.
    /// </summary>
    internal readonly record struct Descriptor(string Name, ushort Type, string Text, byte[] Raw)
    {
        public bool IsBinary => Raw.Length > 0;
    }

    /// <summary>UTF-16LE with the null terminator the format counts in its lengths.</summary>
    private static byte[] Encode(string text) => Encoding.Unicode.GetBytes(text + '\0');

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return "";

        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');

        return (end >= 0 ? text[..end] : text).Trim();
    }
}
