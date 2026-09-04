using PyreMedia.Core.Books;
using PyreMedia.Core.History;

namespace PyreMedia.Core.Organizing;

/// <summary>What sort of thing was found.</summary>
public enum TidyKind
{
    /// <summary>Pages inside a comic that are not the comic - adverts, scanner pages.</summary>
    PagesInComic,

    /// <summary>An image a release left behind.</summary>
    StrayImage,

    /// <summary>A folder of pages that was never packed into a comic.</summary>
    UnpackedComic,

    /// <summary>A release leftover beside the books - a tracker .txt, a scene .nfo.</summary>
    ShelfLitter
}

/// <summary>One thing found, and what would be done about it.</summary>
public sealed record TidyFinding
{
    public required TidyKind Kind { get; init; }

    /// <summary>The file or folder this is about.</summary>
    public required string Path { get; init; }

    /// <summary>What it is, in the shortest form that identifies it.</summary>
    public required string What { get; init; }

    /// <summary>The evidence, so the answer can be argued with rather than trusted.</summary>
    public required string Why { get; init; }

    /// <summary>What pressing the button does to it, said before it happens.</summary>
    public required string Will { get; init; }

    /// <summary>Entries to drop, for pages inside a comic.</summary>
    public List<string> Entries { get; init; } = [];

    /// <summary>The folder to pack, for one that was never packed.</summary>
    public LooseComic? Loose { get; init; }

    public long Bytes { get; init; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// Whether doing this removes something. Packing does not, and the two
    /// should not be presented as though they carry the same risk.
    /// </summary>
    public bool Removes =>
        Actionable && Kind is TidyKind.PagesInComic or TidyKind.StrayImage or TidyKind.ShelfLitter;

    /// <summary>
    /// False where this is worth saying but nothing can be done about it.
    ///
    /// A .cbr is the case: pages can be read out of a RAR and never written
    /// back, so an offer to rebuild one without its adverts is an offer that
    /// fails when taken up. 855 of the 1,935 comics measured are RAR, and the
    /// row promised each of them a rebuild.
    /// </summary>
    public bool Actionable { get; init; } = true;
}

public sealed record TidyResult(int Done, int Failed, List<string> Trouble);

/// <summary>
/// One list of things you might want gone, and one of things you might want
/// made.
///
/// Three quite different findings arrive here - pages inside a comic that are
/// not the comic, images a release left behind, and a folder of pages nobody
/// ever packed - and they are shown together because they are all answers to
/// the same question: what in this library is not what it should be? Splitting
/// them across three screens would mean three places to remember to look, and
/// nobody looks in three places.
///
/// Nothing is ticked to begin with. Every row says what it is, what the
/// evidence was, and what pressing the button would do to it, before anything
/// happens. What is removed goes to the Recycle Bin, which is the only way back
/// from a deletion.
/// </summary>
public static class Tidy
{
    /// <summary>
    /// Everything worth offering.
    /// </summary>
    /// <param name="comics">Comic files to look at, which must already have been read.</param>
    /// <param name="roots">Folders to sweep for stray images and unpacked comics.</param>
    /// <param name="say">
    /// What is being looked at now. Three phases of very different lengths -
    /// reading scripts, sweeping for images, and looking for unpacked comics -
    /// so this says which one rather than pretending they are one bar.
    /// </param>
    /// <param name="onComic">How many comics have been checked, against the total.</param>
    /// <param name="settings">
    /// Needed only for the release-leftover sweep, which is the one thing here
    /// that acts on the user's own junk-extension list. Left out, that sweep
    /// does not run at all - deletion is not something to default into.
    /// </param>
    public static List<TidyFinding> Gather(
        IEnumerable<string> comics,
        IEnumerable<string> roots,
        IProgress<string>? say = null,
        IProgress<int>? onComic = null,
        PyreMediaSettings? settings = null,
        CancellationToken ct = default)
    {
        var found = new List<TidyFinding>();
        var rootList = roots.ToList();
        var comicList = comics.ToList();

        say?.Report($"Looking through {comicList.Count:N0} comic(s) that have been read");

        var checkedSoFar = 0;

        // ---- pages inside comics, from the script rather than by reading again ----
        foreach (var comic in comicList)
        {
            ct.ThrowIfCancellationRequested();

            onComic?.Report(++checkedSoFar);

            var script = ScriptNfo.Exists(comic)
                ? ScriptNfo.Read(ScriptNfo.PathFor(comic))
                : null;

            // The archive is addressed by entry name and the script by page
            // number, so the pages are needed either way.
            var pages = ComicPages.Read(comic);

            // Adverts and scanner pages only. A masthead is where the copyright
            // lives and a letters page is somebody's writing - both worth
            // knowing about, neither worth offering to delete.
            //
            // Type="Other" from the file is not on this list. It is the
            // schema's catch-all, and taggers use it for sketch galleries and
            // pinups; it used to share a value with "the scanner added this",
            // so four pages of publisher-printed art were offered for deletion
            // described as pages the ripper had put there.
            //
            // With no script, the comic's own marks answer instead. Somebody
            // ticking pages in the reader writes them into ComicInfo.xml and
            // nothing else, so without this the marks they had just made
            // reached nothing - and a comic that has been read and then marked
            // has its script dropped on purpose, because the marks changed what
            // the script was describing.
            var unwanted = script is not null
                ? script.Pages
                    .Where(p => p.Kind is PageKind.Advertisement or PageKind.ScannerPage)
                    .Select(p => (p.Number, p.Kind, p.Because))
                    .ToList()
                : pages
                    .Where(p => p.Tagged && p.Kind is PageKind.Advertisement)
                    .Select(p => (p.Number, p.Kind, Because: (string?)"the comic itself says so"))
                    .ToList();

            if (unwanted.Count == 0) continue;

            var entries = unwanted
                .Where(u => u.Number >= 1 && u.Number <= pages.Count)
                .Select(u => pages[u.Number - 1].Entry)
                .ToList();

            if (entries.Count == 0 || entries.Count >= pages.Count) continue;

            var kinds = unwanted.GroupBy(p => p.Kind)
                .Select(g => $"{g.Count()} {(g.Key == PageKind.ScannerPage ? "scanner page" : "advert")}"
                             + (g.Count() == 1 ? "" : "s"));

            // A RAR can be read and never written, so this is worth reporting
            // and not worth offering. Said here rather than discovered at the
            // end of Apply, which is where it used to surface: one paragraph
            // about RAR archives per file, in a summary that shows six.
            var editable = Books.ComicArchive.CanEdit(comic);

            found.Add(new TidyFinding
            {
                Kind = TidyKind.PagesInComic,
                Path = comic,
                Actionable = editable,
                What = script?.Series is { Length: > 0 } s
                    ? $"{s}{(script.Issue is { Length: > 0 } i ? $" #{i}" : "")}"
                    : System.IO.Path.GetFileNameWithoutExtension(comic),
                Why = string.Join(", ", kinds) + ": "
                      + string.Join("; ", unwanted.Take(2).Select(u => u.Because ?? "no reason recorded")),
                Will = editable
                    ? $"remove {entries.Count} page(s); the comic is rebuilt without them and "
                      + "the original goes to the Recycle Bin"
                    : $"nothing - this is a RAR (.cbr) and cannot be rewritten. Convert it to "
                      + $".cbz and the {entries.Count} page(s) can be removed then.",
                Entries = entries
            });
        }

        // ---- release leftovers beside the books ----
        //
        // Every one of these is a file somebody could be upset to lose, so the
        // recogniser is written as a list of reasons not to offer a file and
        // this passes it the same junk extensions the video side uses. What it
        // will never offer: a comic, a book, this program's own searchable text
        // or page script, a Kodi .nfo, or anything named like artwork.
        if (settings is not null) say?.Report("Looking for release leftovers beside the books");

        foreach (var litter in settings is null
                     ? []
                     : Books.ShelfJunk.Find(rootList, settings, ct))
        {
            found.Add(new TidyFinding
            {
                Kind = TidyKind.ShelfLitter,
                Path = litter.Path,
                What = litter.Name,
                Why = litter.Because,
                Will = "send it to the Recycle Bin",
                Bytes = litter.Bytes
            });
        }

        // ---- images a release left behind ----
        say?.Report("Sweeping for images a release left behind");

        foreach (var stray in SceneImages.Find(rootList, ct: ct))
            found.Add(new TidyFinding
            {
                Kind = TidyKind.StrayImage,
                Path = stray.Path,
                What = stray.Name,
                Why = stray.Because,
                Will = "send it to the Recycle Bin",
                Bytes = stray.Bytes
            });

        // ---- comics nobody packed ----
        say?.Report("Looking for comics that were never packed");

        foreach (var loose in LoosePages.Find(rootList, ct: ct))
            found.Add(new TidyFinding
            {
                Kind = TidyKind.UnpackedComic,
                Path = loose.Folder,
                What = loose.Name,
                Why = loose.Describe(),
                Will = $"pack it into {System.IO.Path.GetFileName(loose.Target)}; "
                       + "nothing is deleted and the pages stay where they are",
                Loose = loose,
                Bytes = loose.Bytes
            });

        return found;
    }

    /// <summary>Do the chosen ones. Anything not chosen is not touched.</summary>
    public static TidyResult Apply(
        IEnumerable<TidyFinding> chosen,
        PyreMediaSettings settings,
        RenameHistory? history = null,
        IProgress<string>? say = null,
        CancellationToken ct = default)
    {
        var done = 0;
        var failed = 0;
        var trouble = new List<string>();

        foreach (var finding in chosen)
        {
            ct.ThrowIfCancellationRequested();

            say?.Report(finding.What);

            try
            {
                var problem = finding.Kind switch
                {
                    TidyKind.PagesInComic =>
                        ComicPages.Remove(finding.Path, finding.Entries, history),

                    TidyKind.UnpackedComic =>
                        finding.Loose is { } loose ? LoosePages.Pack(loose, history)
                                                   : "the folder is no longer known",

                    TidyKind.StrayImage => Recycle(finding.Path, settings, history),

                    // The same path everything else takes: the Recycle Bin, and
                    // a history entry. Nothing here is deleted outright.
                    TidyKind.ShelfLitter => Recycle(finding.Path, settings, history),

                    _ => "nothing was going to be done about this"
                };

                if (problem is null) done++;
                else { failed++; trouble.Add($"{finding.Name}: {problem}"); }
            }
            catch (Exception ex)
            {
                failed++;
                trouble.Add($"{finding.Name}: {ex.Message}");
            }
        }

        return new TidyResult(done, failed, trouble);
    }

    /// <summary>
    /// To the Recycle Bin, and written down.
    ///
    /// Honours the same setting the rest of the program does. Somebody who has
    /// turned the Recycle Bin off has said what they want, and a tidy-up is not
    /// the place to override it - but it is the place to record what went.
    /// </summary>
    private static string? Recycle(string path, PyreMediaSettings settings, RenameHistory? history)
    {
        if (!File.Exists(path)) return "it is no longer there";

        try
        {
            if (settings.DeleteToRecycleBin)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);

            history?.Add(HistoryAction.Delete, path, path,
                         System.IO.Path.GetFileName(path), null);

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
