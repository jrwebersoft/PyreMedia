using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.App.Text;
using PyreMedia.Core;
using PyreMedia.Core.Organizing;
using PyreMedia.Core.Books;
using PyreMedia.Core.History;

namespace PyreMedia.App.Views;

/// <summary>One series in the list, with what is held of it.</summary>
public partial class SeriesRow : ObservableObject
{
    public required ComicSeriesGroup Group { get; init; }

    /// <summary>
    /// The cover, once it has been fetched.
    ///
    /// Loaded after the list is on screen rather than while it is being built:
    /// six hundred series means six hundred archives to open, and doing that
    /// before showing anything would be a blank window for half a minute.
    /// </summary>
    [ObservableProperty]
    public partial BitmapSource? Cover { get; set; }

    /// <summary>
    /// Whether the cover is still coming.
    ///
    /// Shown, because six hundred series at a seventh of a second each is a
    /// minute and a half, and a row that is silently empty for a minute looks
    /// like one that failed. An empty box says nothing; a box that says it is
    /// working says the right thing.
    /// </summary>
    [ObservableProperty]
    public partial bool Fetching { get; set; }

    /// <summary>Tried and there was nothing - a broken archive, or a comic with no pages.</summary>
    [ObservableProperty]
    public partial bool NoCover { get; set; }

    public string Title => Group.Display;

    public string Detail => Matched is { } m
        ? Group.Describe() + $"   matched to {m.Display} ({m.Source})"
        : Group.Describe();

    /// <summary>What this series was agreed to be, where somebody has said.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(IsMatched))]
    public partial ComicMatch? Matched { get; set; }

    public bool IsMatched => Matched is not null;

    /// <summary>
    /// A run with holes in it is worth noticing at a glance; one that is merely
    /// unfinished is not a problem, so it stays quiet.
    /// </summary>
    public Brush Tone => Group.Missing.Count > 0 || Group.Duplicated.Count > 0
        ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
        : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
}

/// <summary>One row of the plan.</summary>
public sealed partial class ShelfRow : ObservableObject
{
    public required ShelfAction Action { get; init; }

    /// <summary>
    /// Whether Apply will act on this one.
    ///
    /// The video grid has had this from the start and the comic grid took
    /// everything on screen, so a run of forty-eight where one issue was
    /// obviously misparsed was all-or-nothing: apply the lot, or narrow the
    /// filter until the bad one was elsewhere. Starts ticked for anything that
    /// would move, since agreeing with the plan is the common case.
    /// </summary>
    [ObservableProperty]
    public partial bool Chosen { get; set; }

    /// <summary>
    /// False where ticking would mean nothing: a file already where it belongs,
    /// and one the planner refused to place.
    /// </summary>
    public bool CanChoose => Action.Moves;

    /// <summary>What Apply would do with it, said rather than inferred.</summary>
    public string Status => Action.Held is { Length: > 0 } ? "held"
                          : Action.Moves ? "moves"
                          : "already right";

    public string KindLabel => Action.Item.Kind == ShelfKind.Comic ? "Comic" : "Ebook";
    public string Name => Action.Item.Name;
    public string? Held => Action.Held;

    /// <summary>
    /// The issue on its own, since the series is already named above the list.
    /// Repeating "WildC.A.T.s - Covert Action Teams" on all forty-eight rows
    /// filled the column with the one thing every row had in common.
    /// </summary>
    public string Issue => Action.Item.Comic is { } c
        ? (c.Issue is { Length: > 0 } i ? $"#{i}" : "-")
          + (c.CoverYear is { Length: 4 } y ? $"  ({y})" : "")
        : Path.GetFileNameWithoutExtension(Action.Item.Path);

    /// <summary>Everything the columns no longer have room for, on hovering the row.</summary>
    public string Detail =>
        $"{Name}\n\nRead from {Source}."
        + (Flagged is { Length: > 0 } f ? $"\nFound in it: {f}." : "")
        + (Held is { Length: > 0 } h ? $"\nHeld back: {h}." : "")
        + (Action.Target is { } t ? $"\n\nWould become:\n{t}" : "");

    /// <summary>
    /// What the script beside this file says is in it, or empty where it has
    /// not been read. The flag the whole tidying flow hangs off - and it comes
    /// from the script rather than from reading the pages again, because
    /// reading them took minutes and the answer was written down.
    /// </summary>
    public string Flagged { get; init; } = "";

    /// <summary>
    /// Whether this was read out of the file or guessed from its name. Worth a
    /// column: a wrong name is the usual cause of a wrong answer, and knowing
    /// which rows were guesses says where to look first.
    /// </summary>
    public string Source =>
        Action.Item.Comic?.FromInside == true || Action.Item.Book?.FromInside == true
            ? "the file" : "its name";

    /// <summary>Where it goes, shown relative to the library so the row stays readable.</summary>
    public string Becomes => Action.Target is { } t
        ? string.Join(Path.DirectorySeparatorChar,
            t.Split(Path.DirectorySeparatorChar).TakeLast(3))
        : "";
}

/// <summary>
/// The Books tab: comics and ebooks.
///
/// A pane of its own rather than a filter on the video one. The files are read
/// by different programs, shelved differently, and the question asked of them -
/// which issue of what, or which book by whom - has a different shape.
/// </summary>
public partial class BooksView : UserControl
{
    private PyreMediaSettings _settings = new();
    private RenameHistory _history = new();

    private readonly ObservableCollection<ShelfRow> _rows = [];
    private readonly ObservableCollection<SeriesRow> _series = [];

    private ShelfPlan? _plan;
    private string _showing = "all";

    /// <summary>Which series the grid is narrowed to, or null for everything.</summary>
    private ComicSeriesGroup? _only;

    /// <summary>
    /// What each file's script says is in it, by path.
    ///
    /// Filled once per scan from the scripts already on disk. Working it out
    /// per row would mean opening a file every time the filter changed, and
    /// reading the pages again would mean minutes.
    /// </summary>
    private Dictionary<string, string> _flags = [];

    public BooksView()
    {
        InitializeComponent();

        GridPlan.ItemsSource = _rows;
        SeriesList.ItemsSource = _series;
    }

    /// <summary>Called by the shell once settings and history exist.</summary>
    public void Attach(PyreMediaSettings settings, RenameHistory history)
    {
        _settings = settings;
        _history = history;

        // Which sources are usable, said once. "Nothing is set up" and "nothing
        // matched" look identical afterwards and want different responses.
        Log(Sources().Readiness());
    }

    private ComicProviderChain Sources() => new(
    [
        new GcdProvider(_settings.GcdDatabasePath),
        new MetronProvider(_settings.MetronUser, _settings.MetronPassword),
        new ComicVineProvider(_settings.ComicVineApiKey)
    ]);

    // ---------------- Matching ----------------

    /// <summary>
    /// Agree that the picked run is a particular run in a database.
    ///
    /// The comic answer to the video tab's match column, and the thing the
    /// comic side has never had: the providers were written and wired into
    /// Settings, and the only question ever asked of them was which of them
    /// were configured.
    /// </summary>
    private void OnMatch(object sender, RoutedEventArgs e)
    {
        if (SeriesList.SelectedItem is not SeriesRow row)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Pick a series on the left first. Matching is per series, not per issue - "
                + "the answer to \"which Daredevil is this\" is the same for every issue of it.",
                "Match a series", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var key = ComicShelf.Canonical(row.Group.Series);

        _settings.ComicMatches.TryGetValue(key, out var already);

        // The chain as a factory rather than an instance, so a key entered from
        // inside that window is in use without reopening it.
        var dlg = new ComicMatchWindow(_settings, Sources, row.Group, already)
        {
            Owner = Window.GetWindow(this)
        };

        if (dlg.ShowDialog() != true) return;

        if (dlg.Forgotten)
        {
            _settings.ComicMatches.Remove(key);
            _settings.Save();

            row.Matched = null;
            Log($"{row.Group.Series}: match forgotten - back to what the files say.");
        }
        else if (dlg.Result is { } match)
        {
            _settings.ComicMatches[key] = match;
            _settings.Save();

            row.Matched = match;

            Log($"{row.Group.Series}: matched to {match.Display} from {match.Source}"
                + (match.Total is { } t ? $", a run of {t}" : "") + ".");
        }

        // The plan is built from what the series is called, so a match changes
        // where every issue of it goes.
        Replan();
    }

    /// <summary>
    /// Rebuild the plan and the shelf against the matches now agreed, without
    /// reading every archive again.
    /// </summary>
    private void Replan()
    {
        if (_items is not { Count: > 0 }) return;

        var settled = _items.Select(Apply).ToList();

        _plan = BookPlanner.Plan(
            settled,
            _settings.ComicDestination,
            _settings.EbookDestination,
            _settings.ComicFileFormat,
            _settings.BookFileFormat,
            PublisherOf);

        ShowSeries(settled);
        ApplyFilter();
    }

    /// <summary>The publisher a match supplied, for the {publisher} folder level.</summary>
    private string? PublisherOf(ComicRef comic) =>
        ComicMatch.PublisherFor(comic, _settings.ComicMatches);

    /// <summary>The item as any agreed match describes it.</summary>
    private ShelfItem Apply(ShelfItem item) => ComicMatch.ApplyTo(item, _settings.ComicMatches);
    // ---------------- Scanning ----------------

    private async void OnScan(object sender, RoutedEventArgs e)
    {

        var comicRoots = _settings.ComicFolders.ToList();
        var bookRoots = _settings.BookFolders.ToList();

        if (comicRoots.Count == 0 && bookRoots.Count == 0)
        {
            MessageBox.Show("Add a comic or ebook folder first.",
                "Nothing to scan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Working(true);
        Status("Looking...");

        try
        {
            var settings = _settings;

            var (raw, settled, plan, checks, flags) = await System.Threading.Tasks.Task.Run(() =>
            {
                IProgress<int>? reading = null;

                var items = BookPlanner.Scan(
                    comicRoots, bookRoots, settings.ReadBookMetadata,

                    // While walking there is no total, so this is a count of
                    // folders rather than a fraction of anything.
                    onFolder: folder => Status($"Looking in {Path.GetFileName(folder)}..."),

                    onCounted: total =>
                    {
                        Status(total == 0 ? "Nothing found." : $"Reading {total:N0} file(s)...");
                        reading = Counting(total);
                    },

                    onRead: new Progress<int>(done => reading?.Report(done)));

                // What was read, before any agreed match is folded in - so the
                // shelf can be replanned against a new match without opening
                // every archive again.
                var asRead = items;

                items = [.. items.Select(Apply)];

                var built = BookPlanner.Plan(
                    items,
                    settings.ComicDestination,
                    settings.EbookDestination,
                    settings.ComicFileFormat,
                    settings.BookFileFormat,
                    PublisherOf);

                // What each file claims about itself, checked against a second
                // source. Where they agree there is nothing to say - which on a
                // real shelf is nearly always, and is the point.
                //
                // The inside-only record, not the merged one. Merged already has
                // the filename folded into it, so for a comic carrying no
                // metadata this compared the filename against itself and could
                // only ever agree - and that is the half of a real shelf with
                // nothing to check it against, which is exactly the half worth
                // saying so about. On the test library it reported an all-clear
                // for 868 files it had never actually checked.
                var agreed = items
                    .Where(i => i.Kind == ShelfKind.Comic)
                    .Select(i => Agreement.Compare(
                        i.Path,
                        i.Inside,
                        ComicMatcher.Parse(Path.GetFileName(i.Path))))
                    .ToList();

                // What has already been read, and what it found. Only the files
                // that have a script are opened - the rest cost one existence
                // check each.
                var flagged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items)
                {
                    if (!ScriptNfo.Exists(item.Path)) continue;
                    if (ScriptNfo.Read(ScriptNfo.PathFor(item.Path)) is not { } read) continue;

                    var counts = read.Pages
                        .Where(p => p.Kind is not (PageKind.Story or PageKind.FrontCover))
                        .GroupBy(p => p.Kind)
                        .Select(g => $"{g.Count()} {Describe(g.Key)}")
                        .ToList();

                    if (counts.Count > 0) flagged[item.Path] = string.Join(", ", counts);
                }

                return (asRead, items, built, agreed, flagged);
            });

            _items = raw;
            _plan = plan;
            _flags = flags;

            ShowSeries(settled);
            ApplyFilter();

            var moving = plan.Moving.Count();
            var held = plan.Held.Count();

            Status($"{settled.Count} file(s): {moving} to move, {held} needing you, "
                   + $"{plan.AlreadyRight} already right.");

            Log($"Scanned {settled.Count} file(s) - {moving} would move, {held} held back.");

            // How many answers came from the files rather than from their names,
            // because that is the difference between a fact and a guess and it
            // is worth knowing which this run was built on.
            var inside = settled.Count(f => f.Comic?.FromInside == true || f.Book?.FromInside == true);

            if (settled.Count > 0)
                Log($"{inside} of {settled.Count} were read from inside the file; "
                    + $"the other {settled.Count - inside} from the filename alone.");

            // Only the disagreements. A file whose metadata and filename tell
            // the same story needs nobody, and listing it would bury the
            // handful that do.
            var argued = checks.Where(c => !c.Settled && !c.Unchecked).ToList();

            if (checks.Any(c => !c.Unchecked)) Log(Agreement.Describe(checks));

            foreach (var c in argued.Take(20)) Log($"  {c.Name}: {c.Describe()}");

            foreach (var g in ComicShelf.Group(settled).Where(g => g.Missing.Count > 0))
                Log($"  {g.Display}: {g.Describe()}");

            foreach (var h in plan.Held.Take(20)) Log($"  held: {h.Item.Name} - {h.Held}");

            BtnApply.IsEnabled = moving > 0;
        }
        catch (System.Exception ex)
        {
            Status("Scan failed.");
            Log($"ERROR {ex.Message}");
        }
        finally { Working(false); }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // "All" is ticked in the markup, so this fires while the panel is still
        // being built and the other two buttons do not exist yet. Left alone it
        // throws inside InitializeComponent, which takes the whole window with
        // it - the tab cannot be opened because the shell never starts.
        if (FilterComics is null || FilterBooks is null) return;

        _showing = FilterComics.IsChecked == true ? "comics"
                 : FilterBooks.IsChecked == true ? "books"
                 : "all";

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _rows.Clear();

        if (_plan is null) return;

        foreach (var a in _plan.Actions)
        {
            var isComic = a.Item.Kind == ShelfKind.Comic;

            if (_showing == "comics" && !isComic) continue;
            if (_showing == "books" && isComic) continue;

            if (_only is not null && !InSeries(a, _only)) continue;

            _rows.Add(new ShelfRow
            {
                Action = a,
                Flagged = _flags.GetValueOrDefault(a.Item.Path, "")
            });
        }

        // Anything that would move starts ticked - agreeing with the plan is
        // the common case, and the point of the ticks is being able to
        // disagree with one row rather than having to agree with each.
        foreach (var r in _rows) r.Chosen = r.CanChoose;

        // Apply acts on these rows, so the button has to be about these rows.
        // Its state was set once from the whole scan and never revisited, so
        // narrowing to a series with nothing to move left an enabled Apply
        // that did nothing whatever when pressed.
        BtnApply.IsEnabled = _rows.Any(r => r.CanChoose);
    }

    private void OnTickAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Chosen = r.CanChoose;
    }

    private void OnTickNone(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Chosen = false;
    }

    /// <summary>
    /// Matched on series and year together, reduced exactly as the grouping
    /// reduces them - one file saying "A&amp;A:" and another "A&amp;A -" are the
    /// same series, and comparing the names as written would show an empty list
    /// for half of them. Comparing file paths instead would break the moment a
    /// plan is rebuilt.
    /// </summary>
    private static bool InSeries(ShelfAction a, ComicSeriesGroup group) =>
        a.Item.Comic is { } c
        && ComicShelf.SameSeries(c.Series, group.Series)
        && string.Equals(c.Year, group.Year, StringComparison.OrdinalIgnoreCase);

    private void OnSeriesPicked(object sender, SelectionChangedEventArgs e)
    {
        _only = (SeriesList.SelectedItem as SeriesRow)?.Group;
        ApplyFilter();

        if (_only is not null) Status($"{_only.Display} - {_only.Describe()}");
    }

    private void ShowSeries(IEnumerable<ShelfItem> found)
    {
        _series.Clear();
        _only = null;

        // With the matches, so a series somebody has agreed to is measured
        // against its real run rather than against a count the files mostly
        // do not carry.
        var groups = ComicShelf.Group(found, _settings.ComicMatches);

        foreach (var g in groups)
        {
            _settings.ComicMatches.TryGetValue(ComicShelf.Canonical(g.Series), out var matched);

            _series.Add(new SeriesRow { Group = g, Matched = matched });
        }

        var holed = groups.Count(g => g.Missing.Count > 0);

        TxtSeriesCount.Text = groups.Count switch
        {
            0 => "No series yet.",
            1 => "1 series.",
            _ => $"{groups.Count} series."
        };

        if (holed > 0)
            TxtSeriesCount.Text += holed == 1
                ? " One has a gap in it."
                : $" {holed} have gaps in them.";

        FetchCovers();
    }

    /// <summary>
    /// Everything the last scan read, as it was read - before any agreed match
    /// was folded in. Kept so choosing a match can rebuild the plan without
    /// opening every archive on the shelf again.
    /// </summary>
    private List<ShelfItem> _items = [];

    /// <summary>Which fetch is current, so an older one stops when a new scan starts.</summary>
    private CancellationTokenSource? _covers;

    /// <summary>Rows still wanting a cover, nearest the top of the view first.</summary>
    private readonly List<SeriesRow> _wanted = [];

    private readonly SemaphoreSlim _nudge = new(0);

    /// <summary>
    /// The cover of each series, from page one of the lowest issue held.
    ///
    /// Fetched for what is on screen first, not in list order. Six hundred
    /// series at a seventh of a second each is a minute and a half, so working
    /// straight down the list means everything below the fold sits empty for
    /// most of it while the worker reads archives nobody is looking at.
    ///
    /// Decoded small deliberately. These are drawn at 46 pixels wide and a comic
    /// page is two thousand, so decoding at full size would hold half a gigabyte
    /// of bitmaps to show a column of thumbnails.
    /// </summary>
    private void FetchCovers()
    {
        _covers?.Cancel();
        _covers = new CancellationTokenSource();

        lock (_wanted) _wanted.Clear();

        _done.Clear();

        var token = _covers.Token;

        // Only what is near the view. Everything else waits until it is.
        WantNearby();

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                SeriesRow? row = null;

                lock (_wanted)
                {
                    if (_wanted.Count > 0)
                    {
                        row = _wanted[0];
                        _wanted.RemoveAt(0);
                    }
                }

                if (row is null)
                {
                    // Nothing left. Wait to be told the view moved rather than
                    // spinning, and stop for good when the scan is replaced.
                    try { await _nudge.WaitAsync(token); } catch (OperationCanceledException) { return; }
                    continue;
                }

                var image = Load(row);

                if (token.IsCancellationRequested) return;

                _ = Dispatcher.BeginInvoke(() =>
                {
                    row.Cover = image;
                    row.Fetching = false;
                    row.NoCover = image is null;
                });
            }
        }, token);
    }

    /// <summary>One cover, or null where the comic would not give one up.</summary>
    private static BitmapSource? Load(SeriesRow row)
    {
        // The lowest issue held, because a series is known by its first cover
        // and a run rarely starts at the one that sorts first.
        var first = row.Group.Issues
            .OrderBy(i => i.Sortable ?? int.MaxValue)
            .FirstOrDefault()?.Item.Path;

        if (first is null) return null;

        try
        {
            var pages = ComicPages.Read(first);
            if (pages.Count == 0) return null;

            var bytes = ComicPages.Page(first, pages[0].Entry);
            if (bytes is null) return null;

            var image = new BitmapImage();

            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 92;          // twice the drawn width, for a sharp edge
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            // A comic that will not open has no cover, and the row says so.
            // Nothing here is worth interrupting a list of six hundred for.
            return null;
        }
    }

    /// <summary>Rows already attempted, so scrolling back does not read them again.</summary>
    private readonly HashSet<SeriesRow> _done = [];

    /// <summary>
    /// How far outside the view to fetch.
    ///
    /// Enough that a flick of the wheel lands on covers that are already there,
    /// and few enough that six hundred series do not mean six hundred archives
    /// opened for pictures nobody scrolled to. At a seventh of a second each,
    /// a screenful plus this is under three seconds.
    /// </summary>
    private const int Ahead = 10;

    /// <summary>
    /// Queue the rows near the view, and only those.
    ///
    /// The list virtualises, so the scroll viewer measures in items: the offset
    /// is the index of the first row showing and the viewport is how many fit.
    /// </summary>
    private void WantNearby()
    {
        if (_series.Count == 0) return;

        var first = 0;

        // A screenful, not the whole shelf.
        //
        // This runs before WPF has laid the new rows out, so on the first scan
        // of a session the list has no viewport to report yet - and falling
        // back to the full count queued a cover for every series at once,
        // which is the exact thing the band exists to avoid. Rows are stamped
        // as wanted when they are queued, so the ScrollChanged that arrives
        // after layout had nothing left to narrow.
        var count = 20;

        if (_scroll is { } scroll && scroll.ViewportHeight >= 1)
        {
            first = (int)scroll.VerticalOffset;
            count = (int)scroll.ViewportHeight;
        }

        var from = Math.Max(0, first - Ahead);
        var to = Math.Min(_series.Count - 1, first + count + Ahead);

        var queue = new List<SeriesRow>();

        for (var i = from; i <= to; i++)
        {
            var row = _series[i];

            if (_done.Contains(row)) continue;

            _done.Add(row);
            row.Fetching = true;
            queue.Add(row);
        }

        if (queue.Count == 0) return;

        lock (_wanted) _wanted.AddRange(queue);

        // The worker may be asleep behind an empty queue.
        if (_nudge.CurrentCount == 0) _nudge.Release();
    }

    private ScrollViewer? _scroll;

    private void OnSeriesScrolled(object sender, ScrollChangedEventArgs e)
    {
        // Caught here rather than hunted down the visual tree. The event carries
        // the very scroller that moved, and it fires once when the list is first
        // laid out - which is also when the first band is wanted.
        _scroll ??= e.OriginalSource as ScrollViewer;

        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
            WantNearby();
    }

    /// <summary>
    /// How the list on screen is narrowed, said in the Apply prompt so the
    /// number in it can be checked against what is visible.
    /// </summary>
    private string Narrowed()
    {
        var bits = new List<string>();

        if (_only is not null) bits.Add($"in {_only.Display}");
        else if (_showing == "comics") bits.Add("in comics");
        else if (_showing == "books") bits.Add("in ebooks");

        return bits.Count == 0 ? "" : " " + string.Join(", ", bits);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var comic = GridPlan.SelectedItem is ShelfRow { Action.Item.Kind: ShelfKind.Comic } row
            ? row.Action.Item.Path
            : null;

        BtnRead.IsEnabled = comic is not null;
        BtnPeekOpen.IsEnabled = comic is not null;

        ShowPeek(comic);
    }

    // ---------------- Looking through the issue on the right ----------------

    private string? _peekOf;
    private List<ComicPage> _peekPages = [];
    private int _peekAt;
    private CancellationTokenSource? _peeking;

    /// <summary>The long run that is going, so it can be stopped.</summary>
    private CancellationTokenSource? _cancel;

    /// <summary>
    /// Load the picked comic into the pane on the right.
    ///
    /// Picking a row and being shown nothing but more text is what made this a
    /// spreadsheet. A comic is a picture, and the point of choosing one is to
    /// look at it.
    /// </summary>
    private void ShowPeek(string? comic)
    {
        if (comic == _peekOf) return;

        _peeking?.Cancel();
        _peekOf = comic;
        _peekPages = [];
        _peekAt = 0;

        Peek.Source = null;
        BtnPeekBack.IsEnabled = false;
        BtnPeekForward.IsEnabled = false;

        // Cleared on every path through here, including the ones that return
        // early. It used to be turned off only inside the load's callback,
        // which is the one place a cancelled or superseded load never reaches -
        // so moving off a comic while it was still opening left the ring
        // spinning over an empty pane until something else happened to load.
        PeekBusy.Visibility = Visibility.Collapsed;

        if (comic is null)
        {
            TxtPeekNothing.Visibility = Visibility.Visible;
            TxtPeekNothing.Text = "Pick a comic on the left to look through it here.";
            TxtPeekWhere.Text = "";
            return;
        }

        TxtPeekNothing.Visibility = Visibility.Collapsed;
        PeekBusy.Visibility = Visibility.Visible;
        TxtPeekWhere.Text = Path.GetFileNameWithoutExtension(comic);

        _peeking = new CancellationTokenSource();
        var token = _peeking.Token;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            List<ComicPage> pages;

            try { pages = ComicPages.Read(comic); }
            catch (Exception) { pages = []; }

            if (token.IsCancellationRequested) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested || _peekOf != comic) return;

                PeekBusy.Visibility = Visibility.Collapsed;
                _peekPages = pages;

                if (pages.Count == 0)
                {
                    TxtPeekNothing.Visibility = Visibility.Visible;
                    TxtPeekNothing.Text = "This one could not be opened, so there is nothing to show.";
                    return;
                }

                ShowPeekPage(0);
            });
        }, token);
    }

    private void ShowPeekPage(int index)
    {
        if (_peekOf is not { } comic || _peekPages.Count == 0) return;

        _peekAt = Math.Clamp(index, 0, _peekPages.Count - 1);

        try
        {
            var bytes = ComicPages.Page(comic, _peekPages[_peekAt].Entry);

            if (bytes is null) Peek.Source = null;
            else
            {
                var image = new BitmapImage();

                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;

                // Big enough to fill this pane on any ordinary monitor, and far
                // short of the two thousand a page really is. The reader window
                // is where full size belongs.
                image.DecodePixelWidth = 1000;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();

                Peek.Source = image;
            }
        }
        catch (Exception) { Peek.Source = null; }

        TxtPeekWhere.Text = $"{Path.GetFileNameWithoutExtension(comic)}  -  "
                          + $"page {_peekAt + 1} of {_peekPages.Count}";

        BtnPeekBack.IsEnabled = _peekAt > 0;
        BtnPeekForward.IsEnabled = _peekAt < _peekPages.Count - 1;
    }

    private void OnPeekBack(object sender, RoutedEventArgs e) => ShowPeekPage(_peekAt - 1);
    private void OnPeekForward(object sender, RoutedEventArgs e) => ShowPeekPage(_peekAt + 1);

    /// <summary>Double-clicking the page opens it properly, where it can fill the screen.</summary>
    private void OnPeekClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && BtnPeekOpen.IsEnabled) OnRead(sender, e);
    }

    // ---------------- Reading ----------------

    private void OnRead(object sender, RoutedEventArgs e)
    {
        if (GridPlan.SelectedItem is not ShelfRow row) return;
        if (row.Action.Item.Kind != ShelfKind.Comic) return;

        var reader = new ComicReaderWindow(row.Action.Item.Path, _history)
        {
            Owner = Window.GetWindow(this)
        };

        reader.ShowDialog();

        if (!reader.Changed) return;

        Log($"{row.Name}: {reader.Note}.");

        // The pane on the right is still showing the archive as it was before
        // those pages were removed, and its own "same comic, nothing to do"
        // guard means picking the row again would not reload it.
        _peekOf = null;
        ShowPeek(row.Action.Item.Path);
    }

    // ---------------- Applying ----------------

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;

        // What is on screen, not what was scanned.
        //
        // This used to apply the whole scan while the grid showed a filtered
        // view of it, so narrowing to one series and pressing Apply moved every
        // comic and every ebook in the library. The preview is the promise -
        // a program that acts on more than it showed you has broken the only
        // rule that makes an Apply button safe to press.
        // Ticked, and on screen. Both matter: this used to apply the whole scan
        // while the grid showed a filtered view of it, and then to apply every
        // row of that view whether or not the user agreed with each one.
        var plan = new ShelfPlan { Actions = [.. _rows.Where(r => r.Chosen).Select(r => r.Action)] };

        var moving = plan.Moving.Count();

        if (moving == 0)
        {
            MessageBox.Show(Window.GetWindow(this),
                _rows.Any(r => r.CanChoose)
                    ? "Nothing is ticked. Tick the rows you want moved, or press All."
                    : "Nothing here would move - every file is already where it belongs, "
                      + "or is held back for the reason in its row.",
                "Apply", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        var scope = Narrowed();

        // A name already taken, asked about properly. The executor has always
        // taken Skip, Replace and Keep both, and the clash carries both file
        // sizes - and none of it was reachable: the dialog said the files would
        // be left alone and Execute was called with no answers at all, whose
        // documented default is to skip.
        var clashes = ShelfExecutor.FindClashes(plan);
        Dictionary<string, ShelfChoice>? choices = null;

        if (clashes.Count > 0)
        {
            var dlg = new ShelfConflictWindow(clashes) { Owner = Window.GetWindow(this) };

            if (dlg.ShowDialog() != true) return;

            choices = dlg.Result;
        }
        else if (MessageBox.Show(
                     $"Move {moving} file(s){scope}?\n\nThis undoes from History.",
                     "Apply", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                 != MessageBoxResult.OK)
        {
            return;
        }

        Working(true);

        try
        {
            var history = _history;

            var result = await System.Threading.Tasks.Task.Run(
                () => ShelfExecutor.Execute(plan, history, choices));

            Status($"Moved {result.Moved}, skipped {result.Skipped}, failed {result.Failed}.");

            Log($"Moved {result.Moved} file(s){scope} as batch {result.BatchId}. "
                + "Revert it from History if it is not what you wanted.");

            foreach (var t in result.Trouble.Take(20)) Log("  " + t);

            // The files have moved, so nothing on screen describes the library
            // any more - not the grid, not the shelf, not the pane on the right.
            //
            // Turning the button off here did nothing: the finally below calls
            // Working(false), which recomputes it from the same untouched rows
            // and switched it straight back on, ready to replay a plan whose
            // sources had already gone. Clearing the plan is what makes the
            // button work itself out, and a rescan is the only way back to an
            // Apply.
            _plan = null;
            _rows.Clear();
            _series.Clear();
            _only = null;

            _peekOf = null;
            ShowPeek(null);

            ApplyFilter();
        }
        catch (System.Exception ex)
        {
            Status("Apply failed.");
            Log($"ERROR {ex.Message}");
        }
        finally { Working(false); }

        // Straight back into a scan, so the surface describes what is there
        // now. Outside the try so a failed Apply leaves the old list alone to
        // be looked at.
        if (_plan is null) OnScan(this, new RoutedEventArgs());
    }

    // ---------------- Reading the words ----------------

    /// <summary>
    /// Write a searchable .txt beside every comic and book in the plan.
    ///
    /// Two quite different jobs behind one button. A book already holds its own
    /// text and reading it out is exact. A comic has to be looked at, page by
    /// page, by the OCR engine Windows ships - which is slow, imperfect, and
    /// the only option that does not involve uploading somebody's library.
    /// </summary>
    private async void OnIndex(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _rows.Count == 0)
        {
            MessageBox.Show("Scan first - this works on what the scan found.",
                "Nothing to read", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = _rows.Select(r => r.Action.Item).ToList();

        var comics = files.Count(f => f.Kind == ShelfKind.Comic);
        var books = files.Count(f => f.Kind == ShelfKind.Book);

        if (comics > 0 && !PageOcr.Available)
        {
            MessageBox.Show(
                "Windows has no OCR language installed, so comic pages cannot be read. "
                + "Books would still work.\n\nAdd a language pack under Settings > Time & language.",
                "No OCR on this machine", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var already = files.Count(f => ReadableText.Exists(f.Path));

        var ask = MessageBox.Show(
            $"Read {comics} comic(s) and {books} book(s)?\n\n"
            + (comics > 0
                ? $"Comics are read a page at a time by Windows' own OCR - about a tenth of a "
                  + $"second per page, so roughly {Estimate(comics)} for these. Nothing leaves "
                  + "this machine.\n\nComic lettering is drawn rather than typeset, so the text "
                  + "comes out rough. It is good for finding which issue something happens in, "
                  + "not for reading.\n\n"
                : "")
            + (books > 0 ? "Books carry their own text, so those are exact.\n\n" : "")
            + (already > 0 ? $"{already} already have one and will be skipped.\n\n" : "")
            + "Two files are written beside each one: a .script.nfo for another program to "
            + "index - page by page, saying which pages are advertising - and a plain .txt "
            + "of the same words for anything that just wants text. Nothing else is changed.",
            "Read the words", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (ask != MessageBoxResult.OK) return;

        Working(true);

        // A job the dialog itself measures in hours, with no way to stop it.
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;

        BtnCancel.Visibility = Visibility.Visible;
        BtnCancel.IsEnabled = true;

        var wrote = 0;
        var skipped = 0;
        var failed = 0;
        var done = 0;

        // Position across the shelf. The per-page status says where it is
        // inside one comic and never said which comic of how many.
        var across = Counting(files.Count);

        try
        {
            foreach (var item in files)
            {
                if (token.IsCancellationRequested) break;

                if (ReadableText.Exists(item.Path)) { skipped++; across.Report(++done); continue; }

                Status($"Reading {item.Name} ({done + 1} of {files.Count})...");

                try
                {
                    string? how = null;
                    List<TextPage> pages;
                    List<PageVerdict>? verdicts = null;
                    var exact = false;

                    if (item.Kind == ShelfKind.Book)
                    {
                        if (BookText.WhyNot(item.Path) is { } why)
                        {
                            Log($"  {item.Name}: {why}");
                            skipped++;
                            continue;
                        }

                        pages = await System.Threading.Tasks.Task.Run(() => BookText.Read(item.Path), token);
                        how = "the book's own text";
                        exact = true;
                    }
                    else
                    {
                        if (!PageOcr.Available) { skipped++; continue; }

                        var reading = await PageOcr.ReadAsync(
                            item.Path,
                            (page, total) => Dispatcher.Invoke(() =>
                                Status($"Reading {item.Name} ({done + 1} of {files.Count}) "
                                       + $"- page {page} of {total}")),
                            token);

                        // Every page failing is a fault, not a comic without
                        // words, and writing an empty file over it would hide
                        // that for ever.
                        if (reading.Broken)
                        {
                            Log($"  {item.Name}: no page could be read - {reading.LastTrouble}");
                            failed++;
                            continue;
                        }

                        pages = reading.Pages;
                        how = $"Windows OCR ({PageOcr.Languages})";

                        // What each page turned out to be, carried into the
                        // script so whatever indexes it later can skip the
                        // advertising without reading the words to find out.
                        verdicts = PageJudge.JudgeAll(pages, ComicPages.Read(item.Path));
                    }

                    var name = Path.GetFileNameWithoutExtension(item.Path);

                    // The script, for another program to index.
                    var script = ScriptNfo.From(name, how, exact, pages, verdicts);

                    if (item.Comic is { } comic)
                        script = script with { Series = comic.Series, Issue = comic.Issue, Year = comic.Year };
                    else if (item.Book is { } book)
                        script = script with { Author = book.Author, Year = book.Year, Series = book.Series };

                    ScriptNfo.Write(item.Path, script);

                    // And the plain words, for anything that only wants text.
                    ReadableText.Write(
                        item.Path,
                        ReadableText.Compose(name,
                            exact ? "Taken from the book's own text - exact"
                                  : $"Read from the pages by {how} - rough",
                            pages));

                    wrote++;
                }
                catch (OperationCanceledException) { break; }
                catch (System.Exception ex)
                {
                    failed++;
                    Log($"  {item.Name}: {ex.Message}");
                }

                across.Report(++done);
            }

            var stopped = token.IsCancellationRequested;

            Status($"Read {wrote}, skipped {skipped}"
                   + (failed > 0 ? $", {failed} failed" : "")
                   + (stopped ? " - stopped." : "."));

            Log((stopped ? "Stopped. " : "")
                + $"Wrote {wrote} searchable file(s)"
                + (skipped > 0 ? $", skipped {skipped} that already had one or could not be read" : "")
                + (failed > 0 ? $", {failed} failed" : "")
                + (stopped ? $", {files.Count - done} not reached" : "") + ".");
        }
        finally
        {
            BtnCancel.Visibility = Visibility.Collapsed;
            Working(false);
        }
    }

    /// <summary>A page kind in the words the grid uses.</summary>
    private static string Describe(PageKind kind) => kind switch
    {
        PageKind.Advertisement => "advert",
        PageKind.Editorial => "editorial",
        PageKind.Letters => "letters",
        PageKind.ScannerPage => "scanner page",
        PageKind.Other => "not story",
        PageKind.BackCover => "back cover",
        _ => kind.ToString().ToLowerInvariant()
    };

    /// <summary>Roughly how long a shelf will take, said in units a person uses.</summary>
    private static string Estimate(int comics)
    {
        // About 25 pages an issue at ~120ms a page, measured on a real one.
        var seconds = comics * 25 * 0.12;

        return seconds < 90 ? $"{(int)seconds} seconds"
             : seconds < 5400 ? $"{(int)(seconds / 60)} minutes"
             : $"{seconds / 3600:F1} hours";
    }

    // A search box over the OCR output used to sit here. It was a
    // misreading of what "searchable" meant: the point of reading the pages is
    // to leave a file another program can index, not to put a find box on this
    // screen. ReadableText.Search survives in Core and is still tested - a
    // reader project will want exactly that - it simply is not this tab's job.

    /// <summary>
    /// Everything in the library that is not what it should be, in one list.
    ///
    /// Gathered rather than acted on: the window shows what was found and does
    /// nothing until somebody ticks a row.
    /// </summary>
    private async void OnTidy(object sender, RoutedEventArgs e)
    {
        var roots = _settings.ComicFolders.Concat(_settings.BookFolders).ToList();

        if (roots.Count == 0)
        {
            MessageBox.Show("Add a comic or ebook folder first.",
                "Nothing to look at", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Working(true);
        Status("Looking for things to tidy...");

        try
        {
            var comics = _rows
                .Where(r => r.Action.Item.Kind == ShelfKind.Comic)
                .Select(r => r.Action.Item.Path)
                .ToList();

            var reading = Counting(comics.Count);

            var settings = _settings;

            var found = await System.Threading.Tasks.Task.Run(
                () => Tidy.Gather(
                    comics, roots,
                    say: new Progress<string>(Status),
                    onComic: reading,
                    settings: settings));

            Status(found.Count == 0 ? "Nothing to tidy." : $"{found.Count} thing(s) to look at.");

            var window = new TidyWindow(found, _settings, _history) { Owner = Window.GetWindow(this) };

            window.ShowDialog();

            // Packing makes new comics and removing pages changes existing ones,
            // so what was scanned is no longer what is there.
            if (window.Changed)
            {
                Log("Tidied up - rescanning, because the shelf is not what it was.");
                OnScan(sender, e);
            }
        }
        catch (System.Exception ex)
        {
            Status("Could not look.");
            Log($"ERROR {ex.Message}");
        }
        finally { Working(false); }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = Window.GetWindow(this) };

        // Opened where the book settings are. Landing on Folders and leaving
        // somebody to find the comic sources under a tab called Metadata is how
        // they went unfound.
        dlg.ShowTab("Books");

        if (dlg.ShowDialog() == true) Attach(_settings, _history);
    }

    // ---------------- Odds and ends ----------------

    /// <summary>
    /// Lock the surface while something long is running.
    ///
    /// This used to show the ring and then set IsEnabled = true, which locked
    /// nothing at all - and every long handler here is async void and happily
    /// re-entrant, so Scan could be pressed again mid-scan and "Read the words"
    /// could be started twice over the same shelf.
    /// </summary>
    private void Working(bool busy) => Dispatcher.Invoke(() =>
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        BtnScan.IsEnabled = !busy;
        BtnIndex.IsEnabled = !busy;
        BtnTidy.IsEnabled = !busy;
        BtnMatch.IsEnabled = !busy && SeriesList.SelectedItem is SeriesRow;
        BtnAll.IsEnabled = !busy && _rows.Any(r => r.CanChoose);
        BtnNone.IsEnabled = BtnAll.IsEnabled;
        BtnRead.IsEnabled = !busy && GridPlan.SelectedItem is ShelfRow r
                            && r.Action.Item.Kind == ShelfKind.Comic;
        BtnApply.IsEnabled = !busy && _rows.Any(r => r.CanChoose);

        if (!busy)
        {
            Bar.Visibility = Visibility.Collapsed;
            TxtEta.Text = "";
        }
    });

    /// <summary>Stop the run that is going, after whatever file it is on.</summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        Log("Stopping after this file...");
    }

    /// <summary>
    /// A bar for a job whose size is now known, and how long is left.
    ///
    /// Nothing about painting one is worth losing a scan over, so every handler
    /// swallows its own trouble, and every update is marshalled explicitly:
    /// Progress&lt;T&gt; captures the context where it is built, and these are
    /// built on the thread pool, which has none.
    /// </summary>
    private IProgress<int> Counting(int total)
    {
        var estimate = new PyreMedia.Core.Music.Estimate(total);
        var painted = DateTime.MinValue;

        Dispatcher.Invoke(() =>
        {
            Bar.Value = 0;
            Bar.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEta.Text = total > 0 ? $"0 of {total:N0}" : "";
        });

        return new Progress<int>(done =>
        {
            try
            {
                estimate.Report(done);

                // Thirty thousand files at sixty repaints a second is thirty
                // thousand queued updates behind a window that never catches up.
                var now = DateTime.UtcNow;
                if (done < total && now - painted < TimeSpan.FromMilliseconds(120)) return;

                painted = now;

                var fraction = estimate.Fraction;
                var text = estimate.Describe();

                Dispatcher.BeginInvoke(() =>
                {
                    Bar.Value = fraction;
                    TxtEta.Text = text;
                });
            }
            catch (Exception) { /* a progress bar is not worth a scan */ }
        });
    }

    // Both go through the dispatcher whether or not they are already on it.
    // These are called from async void handlers after an await, and anything
    // that escapes one of those reaches the dispatcher and ends the program
    // rather than the method. Invoking from the UI thread runs inline and costs
    // nothing, so there is no reason to be clever about when to do it.
    private void Status(string text) => Dispatcher.Invoke(() => TxtStatus.Text = text);

    private void Log(string line) => Dispatcher.Invoke(() =>
        TxtLog.AppendText($"{System.DateTime.Now:HH:mm:ss}  {line}{System.Environment.NewLine}"));
}
