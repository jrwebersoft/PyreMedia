using PyreMedia.Core.Organizing;

namespace PyreMedia.Core.Books;

/// <summary>
/// Release leftovers on a comic or ebook shelf: the "Torrent downloaded
/// from ....txt", the scene .nfo, the .url back to a tracker.
///
/// The video side has swept these since it was written. The books side never
/// did, because it only ever walks folders to find comics - so a shelf keeps
/// its litter for ever.
///
/// This is the most dangerous thing in the books code and it is written that
/// way round: every rule here is a reason NOT to offer a file. Two extensions
/// on the junk list, .txt and .nfo, are also what this program writes beside a
/// comic when it reads the words out of it - and reading a shelf is measured in
/// hours. A sweep that ate its own output would be worse than no sweep at all.
///
/// Nothing here deletes. It returns a list; Tidy shows it with nothing ticked,
/// and what is ticked goes to the Recycle Bin through the same path everything
/// else uses.
/// </summary>
public static class ShelfJunk
{
    /// <summary>What a release leaves behind, by name, whatever its extension.</summary>
    private static readonly string[] Telltale =
    [
        "torrent downloaded from", "downloaded from", "uploaded by",
        "rarbg", "1337x", "yts", "piratebay", "kickass", "limetorrent",
        "extratorrent", "torrentgalaxy", "btarena", "divxhunt",
        "visit us", "read me", "readme"
    ];

    /// <summary>
    /// Names that are never litter whatever else they look like: the artwork a
    /// comic reader looks for, and the sidecars other programs write.
    /// </summary>
    private static readonly string[] Spared =
    [
        "cover", "folder", "poster", "fanart", "banner", "thumb",
        "series", "comicinfo", "metadata"
    ];

    /// <summary>
    /// Litter found under these roots. Read only - nothing is opened for
    /// writing and nothing is removed.
    /// </summary>
    public static List<StrayFile> Find(
        IEnumerable<string> roots,
        PyreMediaSettings settings,
        CancellationToken ct = default)
    {
        var found = new List<StrayFile>();

        if (!settings.DeleteJunkFiles) return found;

        var junk = settings.JunkExtensionList;

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;

            try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                if (Why(file, junk) is not { } because) continue;

                long bytes = 0;
                try { bytes = new FileInfo(file).Length; } catch (Exception) { }

                found.Add(new StrayFile
                {
                    Path = file,
                    Name = System.IO.Path.GetFileName(file),
                    Because = because,
                    Bytes = bytes
                });
            }
        }

        return found;
    }

    /// <summary>
    /// Why this file is litter, or null - which is the answer for everything
    /// this does not positively recognise.
    ///
    /// Internal so the refusals can be tested one at a time, because each of
    /// them is a file somebody would be upset to lose.
    /// </summary>
    internal static string? Why(string file, string[] junkExtensions)
    {
        var name = System.IO.Path.GetFileName(file);
        var stem = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();

        // Only extensions the user has nominated. Everything else is somebody's
        // file and none of this program's business.
        if (!junkExtensions.Contains(ext)) return null;

        // A comic or a book is never litter, whatever it is called. Belt and
        // braces: no reader would put one under these extensions, and the cost
        // of being wrong is somebody's book.
        if (ComicMatcher.IsComic(file) || BookFile.IsBook(file)) return null;

        // This program's own work. The searchable text and the page script are
        // hours of OCR, and both wear an extension on the junk list.
        if (ext == ".txt" && ReadableText.IsSearchableText(file)) return null;
        if (ext == ".nfo" && ScriptNfo.IsScript(file)) return null;

        // Somebody else's metadata, which is not ours to tidy away.
        if (ext == ".nfo" && MediaPlanner.IsMetadataNfo(file)) return null;

        // Artwork and sidecars by name, in case one arrives under an extension
        // that happens to be on the list.
        if (Spared.Any(s => stem.Equals(s, StringComparison.OrdinalIgnoreCase)
                            || stem.StartsWith(s + "-", StringComparison.OrdinalIgnoreCase)
                            || stem.EndsWith("-" + s, StringComparison.OrdinalIgnoreCase)))
            return null;

        // From here on it has to earn being offered, rather than being offered
        // for want of a reason not to be.
        var lower = name.ToLowerInvariant();

        if (Telltale.FirstOrDefault(t => lower.Contains(t)) is { } said)
            return $"a release leftover - the name says \"{said}\"";

        // A scene .nfo is an advert in a fixed-width box. It is not Kodi's -
        // that was ruled out above - and not a comic script.
        if (ext == ".nfo") return "a scene .nfo, not metadata this program or Kodi wrote";

        // The rest of the nominated extensions have no business on a shelf and
        // no other meaning: a checksum, a shortcut, an installer.
        if (ext is ".sfv" or ".md5" or ".par2" or ".url" or ".website" or ".lnk"
                or ".exe" or ".bat" or ".diz")
            return $"a leftover {ext} beside the books";

        // A .txt that says nothing about itself is left alone. Somebody's notes
        // live in one of those.
        return null;
    }
}

/// <summary>One piece of litter, and why it is thought to be litter.</summary>
public sealed record StrayFile
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Because { get; init; }
    public long Bytes { get; init; }
}
