using System.IO.Compression;
using PyreMedia.Core.History;

namespace PyreMedia.Core.Books;

/// <summary>A folder that is a comic nobody ever packed.</summary>
public sealed record LooseComic
{
    public required string Folder { get; init; }

    /// <summary>The pages, in the order they will be packed.</summary>
    public required List<string> Pages { get; init; }

    /// <summary>Files that are not pages and will travel with them.</summary>
    public List<string> Extras { get; init; } = [];

    public string Name => System.IO.Path.GetFileName(Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    /// <summary>Where it would be packed to.</summary>
    public string Target => Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar) + ".cbz";

    public long Bytes { get; init; }

    public string Describe() =>
        $"{Pages.Count} pages"
        + (Extras.Count > 0 ? $" and {Extras.Count} other file(s)" : "")
        + $", {Bytes / 1024 / 1024} MB";
}

/// <summary>
/// Comics that were never packed into anything.
///
/// A shelf accumulates these: a folder of numbered images that is plainly one
/// issue, sitting beside .cbz files that are the same thing zipped. Nothing in
/// this program could see them - the scanner looks for comic files, and a
/// folder of .jpg is not one - so they were invisible to shelving, to the
/// reader, to page counting, to everything.
///
/// Packed to .cbz and not .cbr. Both are read here, but .cbz is a zip and .cbr
/// is RAR, and RAR cannot be written without a licence to the compressor. A
/// .cbz is also the format every reader handles best. Nothing is lost by the
/// choice: the pages are already compressed images either way.
///
/// The originals are never deleted as part of packing. The new file is written,
/// checked by reading it back, and only then is the folder offered for removal
/// as a separate decision - because a bad pack that also deleted the pages
/// would be unrecoverable.
/// </summary>
public static class LoosePages
{
    /// <summary>Files that belong in the archive beside the pages.</summary>
    private static readonly string[] Worth = [".xml", ".txt", ".nfo", ".json"];

    /// <summary>
    /// Find folders that are comics in all but packaging.
    /// </summary>
    /// <param name="least">
    /// How many pages before a folder counts. Low enough to catch a short
    /// issue, high enough that a folder holding three photographs is not
    /// mistaken for one.
    /// </param>
    public static List<LooseComic> Find(
        IEnumerable<string> roots, int least = 8, CancellationToken ct = default)
    {
        var found = new List<LooseComic>();

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var dir = queue.Dequeue();

                string[] files, subs;

                try
                {
                    files = Directory.GetFiles(dir);
                    subs = Directory.GetDirectories(dir);
                }
                catch (Exception) { continue; }

                foreach (var s in subs) queue.Enqueue(s);

                if (Judge(dir, files, least) is { } comic) found.Add(comic);
            }
        }

        return found;
    }

    /// <summary>
    /// Whether one folder is a loose comic, or null with no explanation - this
    /// is an offer, and a folder that is not one simply is not offered.
    /// </summary>
    public static LooseComic? Judge(string folder, IReadOnlyList<string> files, int least = 8)
    {
        var pages = files.Where(IsPage)
            .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pages.Count < least) return null;

        var rest = files.Except(pages).ToList();

        // A folder holding a book or a comic is that thing's folder, not a
        // loose comic. Calibre keeps a cover.jpg and a metadata.opf beside every
        // book, and packing those into a .cbz would be nonsense.
        if (rest.Any(f => BookFile.IsBook(f) || ComicMatcher.IsComic(f))) return null;

        // Anything substantial that is not a page means this folder is doing
        // something else as well, and packing it would take that with it.
        if (rest.Any(f => !Worth.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
            return null;

        // Already packed beside itself. Offering again would either clash or
        // quietly make a second copy.
        var target = folder.TrimEnd(System.IO.Path.DirectorySeparatorChar) + ".cbz";

        if (File.Exists(target)) return null;

        long size = 0;

        foreach (var p in pages)
        {
            try { size += new FileInfo(p).Length; } catch (Exception) { }
        }

        return new LooseComic { Folder = folder, Pages = pages, Extras = rest, Bytes = size };
    }

    private static bool IsPage(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant()
            is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";

    /// <summary>
    /// Pack one, and check it before saying it worked.
    ///
    /// Built under a temporary name and moved into place only once it has been
    /// read back and found to hold every page. A half-written archive that
    /// looks finished is how somebody deletes the originals and loses a comic.
    /// </summary>
    /// <returns>Null when it worked, or what went wrong.</returns>
    public static string? Pack(LooseComic comic, RenameHistory? history = null)
    {
        if (comic.Pages.Count == 0) return "there are no pages in it";
        if (File.Exists(comic.Target)) return "something is already there under that name";

        var temp = comic.Target + ".packing";

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // Stored rather than deflated: these are already compressed
                // images, and squeezing them again costs time to make a bigger
                // file.
                foreach (var page in comic.Pages)
                    Add(zip, page, CompressionLevel.NoCompression);

                foreach (var extra in comic.Extras)
                    Add(zip, extra, CompressionLevel.Optimal);
            }

            // Read it back before trusting it.
            using (var check = ZipFile.OpenRead(temp))
            {
                var inside = check.Entries.Count(e => IsPage(e.FullName));

                if (inside != comic.Pages.Count)
                    throw new InvalidOperationException(
                        $"packed {inside} pages but the folder holds {comic.Pages.Count}");
            }

            File.Move(temp, comic.Target, overwrite: false);

            history?.Add(HistoryAction.Copy, comic.Folder, comic.Target,
                         comic.Name, null);

            return null;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }

            return ex.Message;
        }

        static void Add(ZipArchive zip, string file, CompressionLevel level)
        {
            var entry = zip.CreateEntry(System.IO.Path.GetFileName(file), level);

            using var from = File.OpenRead(file);
            using var to = entry.Open();

            from.CopyTo(to);
        }
    }
}
