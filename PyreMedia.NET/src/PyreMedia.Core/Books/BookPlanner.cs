namespace PyreMedia.Core.Books;

/// <summary>What a shelf item turned out to be.</summary>
public enum ShelfKind { Comic, Book }

/// <summary>One comic or ebook, as found and as read.</summary>
public sealed record ShelfItem
{
    public required string Path { get; init; }
    public required ShelfKind Kind { get; init; }

    /// <summary>
    /// The best answer available: what the file says of itself, with the
    /// filename filling any gaps.
    /// </summary>
    public ComicRef? Comic { get; init; }

    /// <summary>
    /// What the file said of itself alone, before the filename was folded in -
    /// or null where it said nothing.
    ///
    /// Kept separately because the two are only worth comparing while they are
    /// still two. The agreement check was handed the merged record as "what the
    /// file claims", so for a comic with no metadata it compared the filename
    /// against itself and could only ever agree.
    /// </summary>
    public ComicRef? Inside { get; init; }

    public BookRef? Book { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>What it is called, whatever kind it is.</summary>
    public string Title => Comic is { } c
        ? c.Series + (c.Issue is null ? "" : $" #{c.Issue}")
        : Book?.Title ?? Name;
}

/// <summary>Where one file is going, and why it might not be.</summary>
public sealed record ShelfAction
{
    public required ShelfItem Item { get; init; }

    /// <summary>Null when it cannot be filed - the reason is in <see cref="Held"/>.</summary>
    public string? Target { get; init; }

    /// <summary>Why this one is being left alone.</summary>
    public string? Held { get; init; }

    public bool Moves => Target is { Length: > 0 }
                         && !string.Equals(Target, Item.Path, StringComparison.OrdinalIgnoreCase);
}

public sealed class ShelfPlan
{
    public List<ShelfAction> Actions { get; init; } = [];

    public IEnumerable<ShelfAction> Moving => Actions.Where(a => a.Moves);
    public IEnumerable<ShelfAction> Held => Actions.Where(a => a.Held is not null);

    public int AlreadyRight => Actions.Count(a => a.Held is null && !a.Moves);
}

/// <summary>
/// Finding comics and ebooks, and working out where they should go.
///
/// Deliberately separate from the video scanner. A .cbz is not a film with a
/// different extension: it has no duration, no tracks, no episodes, and the
/// question asked of it - which issue of what - has a different shape. Bolting
/// it onto MediaScanner would mean every video path growing an "unless it is a
/// comic" branch.
///
/// Nothing here writes anything. It reads, parses, and produces a list of where
/// things would go, which is the same promise the rest of the program makes.
/// </summary>
public static class BookPlanner
{
    /// <summary>Every comic and ebook under the given roots.</summary>
    /// <param name="readInside">
    /// Open each file and ask it what it is, rather than reading only its name.
    /// On by default, because it is nearly always right where a filename is
    /// nearly always a guess. Turning it off is for a library on a slow share,
    /// where opening thirty thousand archives is the expensive part.
    /// </param>
    /// <param name="onFolder">Called with each folder as it is walked, before anything is read.</param>
    /// <param name="onCounted">
    /// Called once with how many files there are, as soon as walking has
    /// finished. This is what makes a real progress bar possible: until the
    /// walk is done nobody knows the total, and a bar that cannot say how far
    /// through it is tells you only that the program has not crashed.
    /// </param>
    /// <param name="onRead">Called with how many have been read so far.</param>
    public static List<ShelfItem> Scan(
        IEnumerable<string> comicRoots,
        IEnumerable<string> bookRoots,
        bool readInside = true,
        Action<string>? onFolder = null,
        Action<int>? onCounted = null,
        IProgress<int>? onRead = null,
        CancellationToken ct = default)
    {
        // Walked first, read second. Opening thirty thousand archives is the
        // slow part and it is worth knowing how many there are before starting,
        // even at the cost of walking the tree before reading any of it.
        var comics = new List<string>();
        var books = new List<string>();

        foreach (var root in comicRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            comics.AddRange(Walk(root, ComicMatcher.IsComic, onFolder, ct));

        foreach (var root in bookRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            books.AddRange(Walk(root, BookFile.IsBook, onFolder, ct));

        onCounted?.Invoke(comics.Count + books.Count);

        var found = new List<ShelfItem>(comics.Count + books.Count);
        var done = 0;

        foreach (var file in comics)
        {
            ct.ThrowIfCancellationRequested();

            var name = ComicMatcher.Parse(System.IO.Path.GetFileName(file));

            // The file first, its name second - the same order television
            // uses. Nearly every scanned comic carries ComicInfo.xml, and
            // what it says is a fact where the filename is a guess.
            // Read once and kept both ways: the merged answer for filing, and
            // what the file said on its own for anything that wants to check
            // one against the other.
            var inside = readInside ? ComicInfoFile.Read(file) : null;
            var parsed = inside is not null ? ComicInfoFile.Merge(inside, name) : name;

            found.Add(new ShelfItem
            {
                Path = file, Kind = ShelfKind.Comic, Comic = parsed, Inside = inside
            });

            onRead?.Report(++done);
        }

        foreach (var file in books)
        {
            ct.ThrowIfCancellationRequested();

            // The same shape as the comic branch above: read the inside when
            // asked to, and otherwise fall back to the name rather than to
            // nothing.
            var parsed = readInside
                ? BookFile.Read(file)
                : BookFile.FromNameOnly(file);

            found.Add(new ShelfItem { Path = file, Kind = ShelfKind.Book, Book = parsed });

            onRead?.Report(++done);
        }

        return found;
    }

    private static IEnumerable<string> Walk(
        string root, Func<string, bool> wanted, Action<string>? onFolder, CancellationToken ct)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var dir = queue.Dequeue();
            onFolder?.Invoke(dir);

            string[] files, subs;

            try
            {
                files = Directory.GetFiles(dir);
                subs = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                // One unreadable folder must not take the rest of a library
                // down with it.
                continue;
            }

            foreach (var f in files.Where(wanted)) yield return f;
            foreach (var s in subs) queue.Enqueue(s);
        }
    }

    /// <summary>
    /// Where everything goes.
    /// </summary>
    /// <param name="publisherOf">
    /// The publisher for a comic series, once a provider has been asked. Comics
    /// with no answer still file - just without that folder level, which is what
    /// the optional sections in the pattern are for.
    /// </param>
    public static ShelfPlan Plan(
        IEnumerable<ShelfItem> items,
        string comicRoot,
        string bookRoot,
        string comicPattern,
        string bookPattern,
        Func<ComicRef, string?>? publisherOf = null)
    {
        var plan = new ShelfPlan();
        var all = items as IList<ShelfItem> ?? [.. items];

        // One name per series, decided once for the whole shelf.
        //
        // The shelf view already groups a run together however its copies spell
        // the name, and filing then took each file's own spelling verbatim - so
        // the program showed one run and scattered it. A twenty-issue Mighty
        // Morphin Power Rangers run landed in four folders, and the gap report
        // the user had just read became fiction the moment they pressed Apply.
        var canonical = SeriesNames(all);

        // Two files landing on one name is the failure worth catching before it
        // happens rather than after: the second would overwrite the first.
        var claimed = new Dictionary<string, ShelfItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in all)
        {
            var settled = Settle(item, canonical);

            var (target, held) = Where(settled, comicRoot, bookRoot, comicPattern, bookPattern, publisherOf);

            if (held is not null)
            {
                plan.Actions.Add(new ShelfAction { Item = item, Held = held });
                continue;
            }

            if (target is not null && claimed.TryGetValue(target, out var other)
                && !ReferenceEquals(other, item))
            {
                plan.Actions.Add(new ShelfAction
                {
                    Item = item,
                    Held = $"would land on the same name as {other.Name}"
                });
                continue;
            }

            if (target is not null) claimed[target] = item;

            plan.Actions.Add(new ShelfAction { Item = settled, Target = target });
        }

        return plan;
    }

    /// <summary>
    /// The one spelling each series will be filed under, keyed by the reduced
    /// form the shelf groups on.
    ///
    /// The same choice the shelf makes when it names a group: a name the file
    /// gave of itself beats one taken from a filename, because a filename
    /// cannot hold a colon and the real title often has one; longest wins among
    /// equals, since a truncated name is the commoner failure.
    /// </summary>
    private static Dictionary<string, string> SeriesNames(IEnumerable<ShelfItem> items)
    {
        var best = new Dictionary<string, (string Name, bool FromFile)>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (item.Comic?.Series is not { Length: > 0 } series) continue;

            var key = ComicShelf.Canonical(series);
            var fromFile = item.Inside?.Series is { Length: > 0 };

            if (!best.TryGetValue(key, out var have))
            {
                best[key] = (series, fromFile);
                continue;
            }

            // A name from inside the file outranks one from a filename; between
            // two of the same standing, the longer one.
            var better = (fromFile && !have.FromFile)
                         || (fromFile == have.FromFile && series.Length > have.Name.Length);

            if (better) best[key] = (series, fromFile);
        }

        return best.ToDictionary(p => p.Key, p => p.Value.Name, StringComparer.Ordinal);
    }

    /// <summary>The same item, filed under its series' agreed spelling.</summary>
    private static ShelfItem Settle(ShelfItem item, Dictionary<string, string> canonical)
    {
        if (item.Comic is not { Series: { Length: > 0 } series } comic) return item;
        if (!canonical.TryGetValue(ComicShelf.Canonical(series), out var agreed)) return item;
        if (string.Equals(agreed, series, StringComparison.Ordinal)) return item;

        return item with { Comic = comic with { Series = agreed } };
    }

    private static (string? Target, string? Held) Where(
        ShelfItem item, string comicRoot, string bookRoot,
        string comicPattern, string bookPattern, Func<ComicRef, string?>? publisherOf)
    {
        var extension = System.IO.Path.GetExtension(item.Path);

        if (item.Kind == ShelfKind.Comic)
        {
            if (item.Comic is not { } comic)
                return (null, "nothing in the filename says what series this is");

            // A series with no issue number is filed, but it cannot be filed by
            // issue - and a pattern built around {issue} would put every such
            // file on one name. Better to say so.
            if (comic.Issue is null && comic.Kind == ComicKind.Issue)
                return (null, "no issue number in the filename");

            if (string.IsNullOrWhiteSpace(comicRoot))
                return (null, "no comic library folder has been chosen");

            var relative = BookNaming.Path(comicPattern, comic, publisherOf?.Invoke(comic));

            return relative.Length == 0
                ? (null, "the pattern produced nothing for this file")
                : (System.IO.Path.Combine(comicRoot, relative + extension), null);
        }

        if (item.Book is not { } book)
            return (null, "neither the file nor its name says what book this is");

        if (string.IsNullOrWhiteSpace(bookRoot))
            return (null, "no ebook library folder has been chosen");

        var path = BookNaming.Path(bookPattern, book);

        return path.Length == 0
            ? (null, "the pattern produced nothing for this file")
            : (System.IO.Path.Combine(bookRoot, path + extension), null);
    }
}
