namespace PyreMedia.Core.Music;

/// <summary>What a non-music file in a music folder is.</summary>
public enum SweepCategory
{
    /// <summary>folder.jpg, fanart.jpg, cdart.png, extrafanart. Nearly all of the weight.</summary>
    Art,

    /// <summary>album.nfo and artist.nfo, written by whatever managed the library before.</summary>
    ScrapedMetadata,

    Playlists,

    /// <summary>desktop.ini, Thumbs.db, AlbumArtSmall.jpg - written by Windows and its players.</summary>
    SystemLeftovers,

    Documents,
    Programs,
    Video,

    /// <summary>Cue sheets, rip logs, checksums, lyrics.</summary>
    RipArtefacts,

    Other
}

public sealed record SweepItem(string Path, SweepCategory Category, long Bytes);

public sealed class SweepResult
{
    public List<SweepItem> Items { get; } = [];

    /// <summary>Folders holding no music at any depth below them.</summary>
    public List<string> MusiclessFolders { get; } = [];

    public long Bytes => Items.Sum(i => i.Bytes);

    public IEnumerable<IGrouping<SweepCategory, SweepItem>> ByCategory =>
        Items.GroupBy(i => i.Category).OrderByDescending(g => g.Sum(i => i.Bytes));
}

/// <summary>
/// Finds everything in a music library that is not music.
///
/// For a full refresh: the art and the scraped .nfo files are a previous
/// manager's work, they are stale where they are not simply wrong, and they can
/// be fetched again from better sources. Measured on a 17,168-file library
/// that is 9,609 files and 2,751 MB - almost exactly the weight of the redundant
/// audio copies.
///
/// The .nfo files are worth harvesting before they go, though. They carry track
/// lengths and MusicBrainz ids for about 40% of the library, which is evidence
/// the tags themselves do not have - see <see cref="MusicEvidence"/>. Sweeping
/// first and gathering afterwards throws that away.
///
/// This only ever reports. Nothing is removed without going through the same
/// approval as any other deletion.
/// </summary>
public static class MusicSweep
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wma", ".m4a", ".ogg", ".oga", ".opus", ".wav", ".aac",
        ".ape", ".wv", ".aiff", ".aif", ".alac", ".mpc", ".dsf", ".dff", ".m4b"
    };

    /// <summary>
    /// Walk a library and list what is not music.
    ///
    /// Reparse points are never followed. A junction pointing outside the library
    /// would otherwise put files nobody meant to touch onto a deletion list, and
    /// the same care is taken here as on the video side.
    /// </summary>
    /// <summary>
    /// Why a sweep must not run yet, or null if it may.
    ///
    /// The scraped .nfo files this removes are the only place a good deal of
    /// the library's metadata exists - MusicBrainz ids and exact track lengths
    /// that nothing here can derive. MusicEvidence reads them into memory and
    /// the sweep deletes them, so running in that order loses the lot without
    /// a single error message. Write the harvest into the audio files first.
    /// </summary>
    public static string? Blocked(IEnumerable<TrackTags> tracks, IEnumerable<SweepCategory>? only = null)
    {
        var categories = only?.ToList() ?? [.. FullRefresh];
        if (!categories.Contains(SweepCategory.ScrapedMetadata)) return null;

        // Only what came out of the .nfo files. A title read off a filename is
        // also held nowhere but outside the audio, and is also worth writing in
        // - but it is a rename that would lose it, not this, and blocking a
        // sweep over it means the warning is wrong about its own cause.
        var pending = tracks.Count(t =>
            t.Title.Source is TagSource.Nfo
            || t.Album.Source is TagSource.Nfo
            || t.AlbumArtist.Source is TagSource.Nfo
            || t.Year.Source is TagSource.Nfo
            || t.Gathered.Count > 0);

        return pending == 0
            ? null
            : $"{pending:N0} files still carry information that came from the .nfo files "
            + "this would delete. Write it into the audio first, or that information is gone.";
    }

    public static SweepResult Survey(
        string root,
        IEnumerable<SweepCategory>? only = null,
        int maxDepth = 32)
    {
        var wanted = only is null ? null : new HashSet<SweepCategory>(only);
        var result = new SweepResult();

        Walk(new DirectoryInfo(root), 0);
        return result;

        // Returns true when this folder, or anything below it, holds music.
        bool Walk(DirectoryInfo folder, int depth)
        {
            if (depth > maxDepth) return false;

            FileInfo[] files;
            DirectoryInfo[] children;

            try
            {
                files = folder.GetFiles();
                children = folder.GetDirectories();
            }
            catch
            {
                // Unreadable is not empty. Say nothing about it rather than
                // reporting a folder as safe to remove because we could not look.
                return true;
            }

            var holdsMusic = files.Any(f => AudioExtensions.Contains(f.Extension));

            foreach (var child in children)
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (Walk(child, depth + 1)) holdsMusic = true;
            }

            foreach (var file in files)
            {
                if (AudioExtensions.Contains(file.Extension)) continue;

                var category = Classify(file);
                if (wanted is not null && !wanted.Contains(category)) continue;

                result.Items.Add(new(file.FullName, category, file.Length));
            }

            if (!holdsMusic) result.MusiclessFolders.Add(folder.FullName);

            return holdsMusic;
        }
    }

    public static SweepCategory Classify(FileInfo file)
    {
        var extension = file.Extension.ToLowerInvariant();

        // Explorer's copy suffix, so "desktop (2).ini" is recognised as the same
        // leftover "desktop.ini" is. A library merged from several sources is full
        // of them.
        var name = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();
        var bracket = name.LastIndexOf(" (", StringComparison.Ordinal);

        if (bracket > 0 && name.EndsWith(')')
            && name[(bracket + 2)..^1].All(char.IsAsciiDigit)
            && bracket + 2 < name.Length - 1)
            name = name[..bracket];

        name += extension;

        // Named leftovers first: AlbumArtSmall.jpg is an image, but it is Windows
        // Media Player's cache rather than art anybody chose.
        if (name is "desktop.ini" or "thumbs.db" or ".ds_store" or "albumartsmall.jpg"
            || name.StartsWith("albumart_{"))
            return SweepCategory.SystemLeftovers;

        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tbn" or ".webp"
                => SweepCategory.Art,

            ".nfo" => SweepCategory.ScrapedMetadata,

            ".m3u" or ".m3u8" or ".pls" or ".wpl" or ".asx" or ".xspf"
                => SweepCategory.Playlists,

            ".txt" or ".log" or ".doc" or ".docx" or ".pdf" or ".rtf" or ".html" or ".htm" or ".url"
                => SweepCategory.Documents,

            ".exe" or ".msi" or ".zip" or ".rar" or ".7z" or ".cab" or ".bat" or ".cmd" or ".scr"
                => SweepCategory.Programs,

            ".mkv" or ".mp4" or ".avi" or ".wmv" or ".mov" or ".flv" or ".mpg" or ".mpeg"
                => SweepCategory.Video,

            ".cue" or ".md5" or ".sfv" or ".accurip" or ".lrc" or ".ffp"
                => SweepCategory.RipArtefacts,

            _ => SweepCategory.Other
        };
    }

    /// <summary>
    /// The categories a full refresh removes by default.
    ///
    /// Video is not among them. A music folder holding an .mkv is as likely to be
    /// a concert film somebody wants as it is to be junk, and nothing here knows
    /// which - so it is listed and left for a person.
    /// </summary>
    public static readonly SweepCategory[] FullRefresh =
    [
        SweepCategory.Art,
        SweepCategory.ScrapedMetadata,
        SweepCategory.SystemLeftovers,
        SweepCategory.Playlists,
        SweepCategory.RipArtefacts,
        SweepCategory.Documents
    ];
}
