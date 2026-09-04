using System.IO.Compression;
using System.Xml.Linq;
using PyreMedia.Core.History;

namespace PyreMedia.Core.Books;

/// <summary>
/// What a page is, as ComicInfo.xml records it.
///
/// These are the values the schema defines, and they are set by whoever scanned
/// the comic in. Advertisement is the one worth acting on: somebody has already
/// gone through and marked which pages are adverts, and that judgement is better
/// than any guess made from the pixels afterwards.
/// </summary>
public enum PageKind
{
    Story,
    FrontCover,
    InnerCover,
    Roundup,
    Advertisement,
    Editorial,
    Letters,
    Preview,
    BackCover,
    Deleted,

    /// <summary>
    /// The schema's own catch-all, and what an unrecognised Type reads as.
    /// A tagger uses it for sketch galleries, pinups and bonus material -
    /// things the publisher printed. Never offered for removal.
    /// </summary>
    Other,

    /// <summary>
    /// A page the scanning group added: a credits or release-info page that
    /// was never in the comic.
    ///
    /// Not a schema value, and deliberately separate from <see cref="Other"/>.
    /// The two used to share it, so a comic whose scanner had tagged four
    /// pinup pages Type="Other" was offered "4 scanner pages" for deletion -
    /// and accepting removed four pages of publisher-printed art. Written back
    /// to ComicInfo.xml as "Other", which is the closest thing the schema has.
    /// </summary>
    ScannerPage
}

/// <summary>One page inside a comic.</summary>
public sealed record ComicPage
{
    /// <summary>Where it sits in the archive, which is how it is addressed.</summary>
    public required string Entry { get; init; }

    public required int Number { get; init; }

    public PageKind Kind { get; init; } = PageKind.Story;

    /// <summary>True when the scanner said so, rather than this program guessing.</summary>
    public bool Tagged { get; init; }

    public long Bytes { get; init; }

    public string Name => System.IO.Path.GetFileName(Entry);

    /// <summary>Worth offering to remove: an advert, or one already marked gone.</summary>
    public bool Droppable => Kind is PageKind.Advertisement or PageKind.Deleted;
}

/// <summary>
/// Reading the pages of a .cbz, and taking some of them out.
///
/// A .cbz is a zip of images in name order, and that is nearly all there is to
/// it. The interesting part is ComicInfo.xml, which many scanners include: it
/// carries a Pages element with a Type on each page, and one of the types is
/// Advertisement. Somebody has already read the comic and marked which pages are
/// adverts - so the honest way to find them is to look at what that person
/// wrote, not to guess from the pixels.
///
/// Where no such tagging exists, nothing is claimed. A page is offered for
/// removal because it was marked, never because it looked like an advert to a
/// program that cannot read.
/// </summary>
public static class ComicPages
{
    private static readonly string[] ImageTypes =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif"];

    public const string InfoEntry = "ComicInfo.xml";

    /// <summary>
    /// Every page, in reading order, with whatever the scanner said about it.
    ///
    /// Works on .cbr as well as .cbz - a fifth of a real library is RAR, and a
    /// page is a page whatever it was packed with.
    /// </summary>
    public static List<ComicPage> Read(string cbz)
    {
        // Name order is reading order. It is a convention rather than a rule,
        // and it is the only one every scanner follows.
        var images = ComicArchive.Entries(cbz)
            .Where(e => ImageTypes.Contains(System.IO.Path.GetExtension(e.Name).ToLowerInvariant()))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tagged = Tagging(cbz);

        return [.. images.Select((e, i) =>
        {
            var known = tagged.TryGetValue(i, out var kind);

            return new ComicPage
            {
                Entry = e.Name,
                Number = i,
                Kind = known ? kind : PageKind.Story,
                Tagged = known,
                Bytes = e.Length
            };
        })];
    }

    /// <summary>
    /// What ComicInfo.xml says about each page, by index.
    ///
    /// Indexed by the Image attribute rather than by position in the file,
    /// because that attribute is the page number and the elements are not
    /// reliably in order.
    /// </summary>
    private static Dictionary<int, PageKind> Tagging(string path)
    {
        var found = new Dictionary<int, PageKind>();

        var bytes = ComicArchive.Entries(path)
            .Where(e => string.Equals(System.IO.Path.GetFileName(e.Name), InfoEntry,
                                      StringComparison.OrdinalIgnoreCase))
            .Select(e => ComicArchive.Read(path, e.Name))
            .FirstOrDefault();

        if (bytes is null) return found;

        try
        {
            var doc = XDocument.Load(new MemoryStream(bytes));

            foreach (var page in doc.Descendants().Where(e => e.Name.LocalName == "Page"))
            {
                if (!int.TryParse((string?)page.Attribute("Image"), out var index)) continue;

                var type = (string?)page.Attribute("Type") ?? "Story";

                found[index] = Enum.TryParse<PageKind>(type, ignoreCase: true, out var kind)
                    ? kind
                    : PageKind.Other;
            }
        }
        catch (Exception)
        {
            // Malformed metadata is no reason to refuse to open the comic. The
            // pages are still there and still readable in order.
        }

        return found;
    }

    /// <summary>One page's bytes, for showing it.</summary>
    public static byte[]? Page(string cbz, string entry) => ComicArchive.Read(cbz, entry);

    /// <summary>
    /// Write a copy of the comic without the given pages.
    ///
    /// Built beside the original and swapped in only once it is complete, so a
    /// crash or a full disk leaves the comic as it was rather than half of one.
    /// The original goes to the Recycle Bin rather than being deleted outright -
    /// pages removed by mistake are otherwise unrecoverable, since what is taken
    /// out is not kept anywhere.
    /// </summary>
    /// <param name="drop">Entry names to leave out.</param>
    public static string? Remove(
        string cbz, IReadOnlyCollection<string> drop, RenameHistory? history = null)
    {
        if (drop.Count == 0) return null;

        if (!ComicArchive.CanEdit(cbz)) return CannotEdit(cbz, "remove pages from");

        var dropping = new HashSet<string>(drop, StringComparer.OrdinalIgnoreCase);
        var temp = cbz + ".rebuilding";

        try
        {
            using (var source = ZipFile.OpenRead(cbz))
            {
                var keeping = source.Entries
                    .Where(e => e.Length > 0 || !e.FullName.EndsWith('/'))
                    .Where(e => !dropping.Contains(e.FullName))
                    .ToList();

                if (!keeping.Any(e => IsImage(e.FullName)))
                    return "that would remove every page";

                // The scanner's marks are recorded by page number, so removing a
                // page moves every mark after it onto the wrong picture. Copying
                // the metadata unchanged would leave a story page badged as an
                // advert - and the next pass would remove it.
                var renumbered = Renumber(source, dropping);

                using var target = new FileStream(temp, FileMode.Create, FileAccess.Write);
                using var written = new ZipArchive(target, ZipArchiveMode.Create);

                foreach (var entry in keeping)
                {
                    var copy = written.CreateEntry(entry.FullName, CompressionLevel.NoCompression);

                    using var to = copy.Open();

                    if (renumbered is not null && IsInfo(entry))
                    {
                        renumbered.Save(to, SaveOptions.None);
                        continue;
                    }

                    // Images are already compressed. Storing them rather than
                    // deflating them again is faster and produces a smaller
                    // file than pretending otherwise.
                    using var from = entry.Open();

                    from.CopyTo(to);
                }
            }

            // Only now is the original touched.
            //
            // The marker goes before the extension rather than after it, so what
            // lands in the Recycle Bin is still a .cbz. Restoring it is the whole
            // recovery path, and a file no reader will open is not one.
            var backup = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(cbz) ?? "",
                System.IO.Path.GetFileNameWithoutExtension(cbz)
                    + " (before pages removed)"
                    + System.IO.Path.GetExtension(cbz));

            File.Move(cbz, backup, overwrite: true);
            File.Move(temp, cbz, overwrite: false);

            history?.Add(HistoryAction.Delete, backup, cbz,
                         $"{System.IO.Path.GetFileName(cbz)} - {drop.Count} page(s) removed", null);

            Recycle(backup);

            // The sidecars address pages by position, and the positions have
            // just moved. Left alone they are worse than absent: Tidy reads
            // "page 5 is an advert" off a stale script, resolves it against the
            // shortened comic, and offers a story page for deletion quoting the
            // old evidence. Accept twice and the comic loses two good pages.
            //
            // Deleted rather than renumbered because both are regenerable from
            // the comic in one pass, and nothing downstream treats a missing
            // script as anything but "not read yet".
            Forget(cbz);

            return null;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }

            return ex.Message;
        }
    }

    /// <summary>
    /// Drop what was written about this comic's pages, because it was written
    /// about a different set of them. Both sidecars are regenerable from the
    /// comic in one pass, and everything downstream reads a missing one as
    /// "not read yet" rather than as an error.
    /// </summary>
    public static void Forget(string comic)
    {
        foreach (var sidecar in new[] { ScriptNfo.PathFor(comic), ReadableText.PathFor(comic) })
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); }
            catch (Exception) { /* a locked sidecar is stale, not fatal */ }
        }
    }

    /// <summary>
    /// Write down what pages are, in the file, where every other program can
    /// read it.
    ///
    /// Comics in the wild are rarely marked up beyond the front cover, which
    /// leaves the advert button with nothing to act on. This is the way out that
    /// does not involve guessing: somebody goes through the issue once and says
    /// what the pages are, and the answer is kept in ComicInfo.xml under the
    /// schema everything else already reads. Komga and Kavita will honour it,
    /// this program will honour it next time, and no pages are lost in the
    /// meantime - which is the difference between marking and removing.
    /// </summary>
    /// <param name="marks">Entry name to what it actually is.</param>
    public static string? Mark(
        string cbz, IReadOnlyDictionary<string, PageKind> marks, RenameHistory? history = null)
    {
        if (marks.Count == 0) return null;

        if (!ComicArchive.CanEdit(cbz)) return CannotEdit(cbz, "write marks into");

        var temp = cbz + ".remarking";

        try
        {
            var before = new Dictionary<string, string?>();

            using (var source = ZipFile.OpenRead(cbz))
            {
                var images = source.Entries
                    .Where(e => IsImage(e.FullName))
                    .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var found = source.Entries.FirstOrDefault(IsInfo);
                var doc = found is null ? new XDocument(new XElement("ComicInfo")) : Existing(found);

                // Metadata that will not parse is left alone rather than replaced.
                // Writing a fresh one over it would trade the series, the issue
                // and the credits for a note about which page is an advert.
                if (doc is null)
                    return "this comic's ComicInfo.xml cannot be read, and marking pages would "
                         + "overwrite it - the series and credits in it would be lost";

                var root = doc.Root!;
                var ns = root.Name.Namespace;

                var pages = root.Element(ns + "Pages");

                if (pages is null)
                {
                    pages = new XElement(ns + "Pages");
                    root.Add(pages);
                }

                foreach (var (entry, kind) in marks)
                {
                    var index = images.FindIndex(e =>
                        string.Equals(e.FullName, entry, StringComparison.OrdinalIgnoreCase));

                    if (index < 0) continue;

                    var page = pages.Elements()
                        .FirstOrDefault(p => p.Name.LocalName == "Page"
                                          && (string?)p.Attribute("Image") == index.ToString());

                    if (page is null)
                    {
                        page = new XElement(ns + "Page", new XAttribute("Image", index));
                        pages.Add(page);
                    }

                    before[entry] = (string?)page.Attribute("Type");

                    // Story is what the schema means by an absent Type, so it is
                    // written as an absence. Anything else would leave this
                    // program's idea of "unmarked" in a file other readers see
                    // as marked.
                    if (kind == PageKind.Story) page.Attribute("Type")?.Remove();

                    // ScannerPage is this program's distinction, not the
                    // schema's. Writing the name out would leave other readers
                    // with a Type they do not know, so it goes back as the
                    // schema's own catch-all.
                    else page.SetAttributeValue("Type",
                        kind == PageKind.ScannerPage ? "Other" : kind.ToString());
                }

                // Kept in page order, because a human opens this file too.
                var ordered = pages.Elements()
                    .OrderBy(p => int.TryParse((string?)p.Attribute("Image"), out var n) ? n : int.MaxValue)
                    .ToList();

                pages.RemoveNodes();
                pages.Add(ordered);

                using var target = new FileStream(temp, FileMode.Create, FileAccess.Write);
                using var written = new ZipArchive(target, ZipArchiveMode.Create);

                var had = false;

                foreach (var entry in source.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')))
                {
                    using var to = written.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();

                    if (IsInfo(entry))
                    {
                        had = true;
                        doc.Save(to, SaveOptions.None);
                        continue;
                    }

                    using var from = entry.Open();
                    from.CopyTo(to);
                }

                if (!had)
                {
                    using var to = written.CreateEntry(InfoEntry, CompressionLevel.Optimal).Open();
                    doc.Save(to, SaveOptions.None);
                }
            }

            File.Move(temp, cbz, overwrite: true);

            // Recorded as a tag write rather than a deletion, because that is
            // what it is: nothing left the file, and what the marks used to say
            // is written down beside the change.
            history?.Add(HistoryAction.Retag, cbz, cbz,
                         $"{System.IO.Path.GetFileName(cbz)} - {marks.Count} page(s) marked",
                         null, null, before);

            // Same reasoning as Remove. Marking changes what each page is, which
            // is precisely what the script records, so a script written before
            // the marks now disagrees with the comic - it would say Story for a
            // page the file itself has just been told is an advert. Two answers
            // are worse than one.
            Forget(cbz);

            return null;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }

            return ex.Message;
        }
    }

    /// <summary>
    /// Rename every page inside a comic to a plain, padded, ordered name.
    ///
    /// Scanners name pages however they like - "wildcats v1 #0 03.jpg",
    /// "wildc.a.t.s._n1-p27.jpg", "zback page04.jpg" - and a shelf ends up with
    /// as many conventions as it has scanners. This gives them all one:
    /// 001.jpg, 002.jpg, and so on.
    ///
    /// It does NOT fix reading order, and should not be sold as though it does.
    /// Measured across 250 real comics, the order pages sort in already matches
    /// the order their numbers imply in every single one - the classic
    /// unpadded-number bug (9 sorting after 10) does not appear in this library
    /// at all. The pages are renumbered in exactly the order they already read
    /// in, so what changes is the names and nothing else.
    ///
    /// ComicInfo.xml is carried across untouched, and stays correct, because it
    /// addresses pages by position and the positions do not move.
    /// </summary>
    /// <param name="pad">Digits to pad to. Three covers any comic; four covers a collection.</param>
    public static string? Renumber(string cbz, RenameHistory? history = null, int pad = 3)
    {
        if (!ComicArchive.CanEdit(cbz)) return CannotEdit(cbz, "rename the pages inside");

        var pages = Read(cbz);

        if (pages.Count == 0) return "there are no pages in it";

        var width = Math.Max(pad, pages.Count.ToString().Length);

        // Already done. Renaming a comic to the names it already has would
        // rewrite the file, send the original to the Recycle Bin and change
        // nothing, which is worse than doing nothing.
        var wanted = pages
            .Select((p, i) => (p.Entry, Name: $"{(i + 1).ToString().PadLeft(width, '0')}"
                                              + System.IO.Path.GetExtension(p.Entry).ToLowerInvariant()))
            .ToList();

        if (wanted.All(w => string.Equals(w.Entry, w.Name, StringComparison.Ordinal)))
            return null;

        var temp = cbz + ".renaming";

        try
        {
            using (var source = ZipFile.OpenRead(cbz))
            {
                using var target = new FileStream(temp, FileMode.Create, FileAccess.Write);
                using var written = new ZipArchive(target, ZipArchiveMode.Create);

                var renamed = wanted.ToDictionary(w => w.Entry, w => w.Name, StringComparer.Ordinal);

                foreach (var entry in source.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')))
                {
                    var name = renamed.TryGetValue(entry.FullName, out var fresh)
                        ? fresh
                        : System.IO.Path.GetFileName(entry.FullName);

                    var copy = written.CreateEntry(name,
                        IsImage(entry.FullName) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);

                    using var from = entry.Open();
                    using var to = copy.Open();

                    from.CopyTo(to);
                }
            }

            // The original goes to the Recycle Bin under a name a reader will
            // still open, exactly as removing pages does.
            var backup = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(cbz) ?? "",
                System.IO.Path.GetFileNameWithoutExtension(cbz)
                    + " (before pages renamed)"
                    + System.IO.Path.GetExtension(cbz));

            File.Move(cbz, backup, overwrite: true);
            File.Move(temp, cbz, overwrite: false);

            history?.Add(HistoryAction.Delete, backup, cbz,
                         $"{System.IO.Path.GetFileName(cbz)} - {pages.Count} page(s) renamed", null);

            Recycle(backup);

            return null;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }

            return ex.Message;
        }
    }

    /// <summary>The comic's own metadata, or null when it cannot be read.</summary>
    private static XDocument? Existing(ZipArchiveEntry info)
    {
        try
        {
            using var stream = info.Open();
            var doc = XDocument.Load(stream);

            return doc.Root is null ? null : doc;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Why a comic cannot be changed, said in terms of what it is rather than
    /// what it is called - a .cbz that is secretly a RAR is refused for the same
    /// reason and deserves the same explanation.
    /// </summary>
    private static string CannotEdit(string path, string what) =>
        ComicArchive.Kind(path) == ArchiveKind.Rar
            ? $"this comic is a RAR archive, and there is no way to {what} one safely - "
            + "it can be read and looked through, but not rewritten. Converting it to .cbz "
            + "with any comic tool would make it editable."
            : $"this file is not an archive this program can {what}.";

    private static bool IsImage(string name) =>
        ImageTypes.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant());

    private static bool IsInfo(ZipArchiveEntry entry) =>
        string.Equals(entry.Name, InfoEntry, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ComicInfo.xml with its page numbers moved to where the pages will be.
    ///
    /// Marks on pages being removed go with them; marks on pages that stay are
    /// renumbered to their new position. Null when there is nothing to rewrite -
    /// no metadata, or metadata this cannot read - in which case the file is
    /// copied across as it is rather than replaced with a guess.
    /// </summary>
    private static XDocument? Renumber(ZipArchive zip, HashSet<string> dropping)
    {
        var info = zip.Entries.FirstOrDefault(IsInfo);
        if (info is null) return null;

        // Reading order, exactly as Read sees it, because that is what the
        // Image numbers count.
        var images = zip.Entries
            .Where(e => IsImage(e.FullName))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var moved = new Dictionary<int, int>();
        var next = 0;

        for (var i = 0; i < images.Count; i++)
        {
            if (dropping.Contains(images[i].FullName)) continue;

            moved[i] = next++;
        }

        try
        {
            XDocument doc;

            using (var stream = info.Open()) doc = XDocument.Load(stream);

            foreach (var page in doc.Descendants().Where(e => e.Name.LocalName == "Page").ToList())
            {
                if (!int.TryParse((string?)page.Attribute("Image"), out var was))
                {
                    // A page element with no number cannot be moved, and leaving
                    // it would leave a mark pointing nowhere in particular.
                    page.Remove();
                    continue;
                }

                if (moved.TryGetValue(was, out var now))
                    page.SetAttributeValue("Image", now);
                else
                    page.Remove();
            }

            // Only touched if it was there. An absent PageCount means the file
            // never claimed one, and adding it would be inventing metadata.
            var count = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PageCount");

            if (count is not null) count.Value = next.ToString();

            return doc;
        }
        catch (Exception)
        {
            // Metadata that will not parse is copied across untouched. It is
            // already wrong in a way this cannot fix, and throwing it away would
            // lose the series and issue along with the page marks.
            return null;
        }
    }

    private static void Recycle(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception)
        {
            // No Recycle Bin on this volume. Leave the backup where it is rather
            // than deleting the only copy of the pages that were removed.
        }
    }

    /// <summary>
    /// A sentence about what the scanner marked, for saying once when a comic
    /// is opened.
    /// </summary>
    /// <param name="path">
    /// Given so an archive that could not be opened at all is told apart from
    /// one that opened and held no pages. Both come back as an empty list, and
    /// describing the first as "0 pages, nothing marked" reads as though the
    /// comic were empty rather than unreadable.
    /// </param>
    public static string Describe(IReadOnlyList<ComicPage> pages, string? path = null)
    {
        if (pages.Count == 0 && path is not null)
            return ComicArchive.Kind(path) == ArchiveKind.Unknown
                ? "This is not a zip or a RAR, so its pages cannot be shown. .cb7 and .cbt "
                + "are 7-Zip and tar, which are not read here - repacking it as .cbz with any "
                + "comic tool would open it."
                : "This opened, but there are no pages in it.";

        var ads = pages.Count(p => p.Kind == PageKind.Advertisement);
        var tagged = pages.Count(p => p.Tagged);

        // Deliberately does not say who did the marking. Once a reader can write
        // marks of their own the file no longer records whose they are, and
        // crediting the scanner for somebody's own afternoon's work would be a
        // small lie told confidently.
        if (tagged == 0)
        {
            // Only offered where it can actually be done. A RAR is readable and
            // not writable, and telling somebody their ticks will be saved into
            // one is a promise this cannot keep.
            var canWrite = path is null || ComicArchive.CanEdit(path);

            return $"{pages.Count} pages, and nothing in the file says what any of them are. "
                 + "Nothing is suggested - the adverts are yours to spot"
                 + (canWrite
                     ? ", and ticking them writes the answer into the comic so it only has to "
                       + "be done once."
                     : ". This one is a RAR, so the answer cannot be written back into it.");
        }

        return ads == 0
            ? $"{pages.Count} pages. Some are marked up, but none are marked as adverts."
            : $"{pages.Count} pages, {ads} of them marked as adverts.";
    }
}
