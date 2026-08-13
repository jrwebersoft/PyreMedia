namespace PyreMedia.Core.Music;

/// <summary>
/// Reads tags from an Ogg file - Vorbis, Opus or FLAC inside an Ogg container.
///
/// The tags are ordinary Vorbis comments, the same bytes FLAC carries; the work
/// is getting at them. Ogg is a page format, and a packet is free to straddle
/// several pages, so the comment header has to be reassembled from the segment
/// tables rather than read where it appears to start.
///
/// Which codec is inside only changes the few bytes in front of the comments:
/// Vorbis writes 0x03 "vorbis", Opus writes "OpusTags", and FLAC-in-Ogg wraps
/// its usual metadata blocks. All three end at the same place.
/// </summary>
public static class OggReader
{
    /// <summary>
    /// The comment header is the second packet of a stream. Reading far past that
    /// means the file is not what it claims, so the search stops rather than
    /// walking a gigabyte of audio looking for something that is not there.
    /// </summary>
    private const int MaxPages = 64;

    private const int MaxPacket = 8 * 1024 * 1024;

    public static void Read(string path, TrackTags into)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var packet = new MemoryStream();

        for (var page = 0; page < MaxPages; page++)
        {
            if (!ReadPage(file, out var payload, out var continued, out var endsPacket)) return;

            // A page that continues the previous packet appends to it; one that
            // does not starts afresh, so a truncated packet cannot bleed into the
            // next and be parsed as though it were whole.
            if (!continued) packet.SetLength(0);

            if (packet.Length + payload.Length > MaxPacket) return;
            packet.Write(payload);

            if (!endsPacket) continue;

            if (TryComments(packet.GetBuffer().AsSpan(0, (int)packet.Length), into)) return;
            packet.SetLength(0);
        }
    }

    /// <summary>
    /// One page: the 27-byte header, a segment table, and the segments it counts.
    /// </summary>
    private static bool ReadPage(Stream file, out byte[] payload, out bool continued, out bool endsPacket)
    {
        payload = [];
        continued = false;
        endsPacket = false;

        var header = new byte[27];
        if (!Fill(file, header)) return false;

        if (header[0] != 'O' || header[1] != 'g' || header[2] != 'g' || header[3] != 'S') return false;
        if (header[4] != 0) return false;

        continued = (header[5] & 0x01) != 0;

        var segments = header[26];
        var table = new byte[segments];
        if (!Fill(file, table)) return false;

        var length = 0;
        foreach (var b in table) length += b;

        payload = new byte[length];
        if (!Fill(file, payload)) return false;

        // A packet ends on the page where its last segment is shorter than 255.
        endsPacket = segments == 0 || table[segments - 1] != 255;
        return true;
    }

    /// <summary>Recognise a comment header and hand its body to the Vorbis parser.</summary>
    private static bool TryComments(ReadOnlySpan<byte> packet, TrackTags into)
    {
        // Vorbis: packet type 3, then "vorbis".
        if (packet.Length > 7 && packet[0] == 3 && Matches(packet[1..], "vorbis"))
        {
            into.Sources.Add(TagSource.VorbisComment);
            VorbisReader.ParseComments(packet[7..], into);
            return true;
        }

        // Opus.
        if (packet.Length > 8 && Matches(packet, "OpusTags"))
        {
            into.Sources.Add(TagSource.VorbisComment);
            VorbisReader.ParseComments(packet[8..], into);
            return true;
        }

        // FLAC inside Ogg: 0x7F "FLAC", version, header count, then "fLaC" and the
        // usual metadata blocks. The comments are one of those blocks.
        if (packet.Length > 13 && packet[0] == 0x7F && Matches(packet[1..], "FLAC"))
            return FlacBlocks(packet[13..], into);

        return false;
    }

    /// <summary>FLAC metadata blocks carried in an Ogg packet.</summary>
    private static bool FlacBlocks(ReadOnlySpan<byte> data, TrackTags into)
    {
        var at = 0;

        while (at + 4 <= data.Length)
        {
            var last = (data[at] & 0x80) != 0;
            var type = data[at] & 0x7F;
            var length = data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3];

            at += 4;
            if (length < 0 || at + length > data.Length) return false;

            if (type == 4)
            {
                into.Sources.Add(TagSource.VorbisComment);
                VorbisReader.ParseComments(data.Slice(at, length), into);
                return true;
            }

            if (last) return false;
            at += length;
        }

        return false;
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, string text)
    {
        if (bytes.Length < text.Length) return false;

        for (var i = 0; i < text.Length; i++)
            if (bytes[i] != text[i]) return false;

        return true;
    }

    private static bool Fill(Stream file, Span<byte> buffer)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var n = file.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }

        return true;
    }
}
