namespace PyreMedia.Core.Music;

/// <summary>
/// Reads whatever tags an audio file carries, whichever format it is in.
///
/// Reads only. Nothing here writes, moves or decides anything - it gathers
/// evidence, and what to do about it is a later question with a person in the
/// middle of it.
/// </summary>
public static class MusicFileReader
{
    /// <summary>
    /// Audio this can read tags from. Deliberately narrow: a format not on this
    /// list is reported as unsupported rather than half-read.
    /// </summary>
    public static readonly string[] Extensions =
    [
        // Read in full.
        ".mp3", ".flac", ".wma", ".asf", ".ogg", ".oga", ".opus",
        ".m4a", ".m4b", ".m4p", ".mp4", ".alac",

        // Audiobooks. .m4b is an .m4a under a different name so players
        // remember your place, and Read() always knew how to handle one - but
        // it was missing from this list, so the scan never offered it a file.
        // Seven Harry Potter books sat in a library completely invisible, and
        // the audiobook feature could not have worked on the one format that
        // identifies a book outright. Everything below is here so that cannot
        // happen again: a file this list omits is not merely untagged, it does
        // not exist as far as the rest of the program is concerned.
        ".aa", ".aax",

        // Uncompressed and lossless. Tags live in a RIFF or IFF chunk, or in
        // an APEv2 block on the end.
        ".wav", ".aif", ".aiff", ".aifc", ".caf", ".au", ".snd",
        ".ape", ".wv", ".tta", ".shn", ".ofr", ".ofs", ".dsf", ".dff",

        // Lossy, including the older ones. .mp2 and .mp1 predate .mp3 and turn
        // up on anything ripped in the nineties; .mpc is Musepack; .ra and .rm
        // are RealAudio, which nobody has encoded since about 2004 and which
        // plenty of libraries still contain.
        ".aac", ".mp2", ".mp1", ".mpc", ".mp+", ".spx", ".ra", ".rm",
        ".amr", ".m4r", ".mka", ".weba"
    ];

    /// <summary>
    /// The formats a reader actually understands. The rest are found, listed
    /// and reported as unsupported - which is the point of listing them.
    /// </summary>
    public static readonly string[] Readable =
    [
        ".mp3", ".flac", ".wma", ".asf", ".ogg", ".oga", ".opus",
        ".m4a", ".m4b", ".m4p", ".mp4", ".alac"
    ];

    public static bool CanReadTags(string path) =>
        Readable.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsAudio(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static TrackTags Read(string path)
    {
        var tags = new TrackTags { Path = path };

        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp3":
                    Id3Reader.Read(path, tags);
                    break;

                case ".flac":
                    VorbisReader.ReadFlac(path, tags);
                    break;

                case ".wma" or ".asf":
                    AsfReader.Read(path, tags);
                    break;

                case ".ogg" or ".oga" or ".opus":
                    OggReader.Read(path, tags);
                    break;

                case ".m4a" or ".m4b" or ".m4p" or ".mp4" or ".alac":
                    Mp4Reader.Read(path, tags);
                    break;

                default:
                    // Found, counted, and named as unsupported. That is the
                    // whole reason these extensions are listed: a file nothing
                    // can read is a gap somebody can see and decide about, and
                    // a file nothing looks for is one they never learn is
                    // there. APEv2 - which would cover .ape, .wv, .mpc and .tta
                    // in one go - is the obvious next reader.
                    tags.Error = $"no reader yet for {Path.GetExtension(path).ToLowerInvariant()}";
                    break;
            }
        }
        catch (Exception ex)
        {
            // One unreadable file in a library of tens of thousands must not stop
            // the scan. Record why and carry on.
            tags.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return tags;
    }

    /// <summary>
    /// Every audio file beneath a folder, read.
    ///
    /// Enumerated lazily so a caller can report progress and so a library too big
    /// to hold in memory at once is still workable. Unreadable folders are
    /// skipped rather than fatal, and links are not followed - a library assembled
    /// from several sources is full of junctions pointing at each other.
    /// </summary>
    public static IEnumerable<TrackTags> ReadFolder(string root, Action<string>? onFolder = null)
    {
        foreach (var file in Walk(root, 0, onFolder))
            yield return Read(file);
    }

    /// <summary>
    /// Every audio file beneath a folder, without reading any of them.
    ///
    /// Separate from <see cref="ReadFolder"/> so a caller can find out how much
    /// work there is before starting it. Reading tags off a library is the
    /// first long phase of a scan and the one somebody watches, and a lazy
    /// enumerable cannot say how far through it is - the count is not known
    /// until it has finished. Walking the directories is a fraction of the cost
    /// of opening every file, so paying it twice buys a progress bar cheaply.
    /// </summary>
    public static IEnumerable<string> Files(string root, Action<string>? onFolder = null) =>
        Walk(root, 0, onFolder);

    private static IEnumerable<string> Walk(string folder, int depth, Action<string>? onFolder)
    {
        if (depth > 32) yield break;

        string[] files, subs;

        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) yield break;

            files = Directory.GetFiles(folder);
            subs = Directory.GetDirectories(folder);
        }
        catch (Exception)
        {
            yield break;                       // unreadable: skip, don't stop
        }

        onFolder?.Invoke(folder);

        foreach (var f in files)
            if (IsAudio(f)) yield return f;

        foreach (var sub in subs)
            foreach (var f in Walk(sub, depth + 1, onFolder))
                yield return f;
    }
}
