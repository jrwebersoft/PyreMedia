using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.App.Text;
using PyreMedia.Core.Books;
using PyreMedia.Core.History;

namespace PyreMedia.App.Views;

/// <summary>One page in the list beside the reader.</summary>
public partial class PageRow : ObservableObject
{
    public required ComicPage Page { get; init; }

    [ObservableProperty]
    public partial bool Drop { get; set; }

    public string Label => $"{Page.Number + 1}. {Page.Name}";

    /// <summary>Only shown where the scanner actually said something.</summary>
    public bool ShowKind => Page.Tagged && Page.Kind != PageKind.Story;

    public string KindLabel => Page.Kind switch
    {
        PageKind.Advertisement => "advert",
        PageKind.FrontCover => "cover",
        PageKind.BackCover => "back",
        PageKind.Deleted => "deleted",
        _ => Page.Kind.ToString().ToLowerInvariant()
    };
}

/// <summary>
/// Looking through a comic, and taking the adverts out.
///
/// The arrows and the page list are the whole interface, because that is the
/// whole job: see what a page is, decide whether it belongs, move on.
///
/// Adverts are found by reading what the person who scanned the comic wrote,
/// not by looking at the pixels. ComicInfo.xml has a Type on each page and
/// Advertisement is one of the values, so where a scanner has marked them up
/// the answer is already in the file and is better than any guess. Where nobody
/// marked anything, nothing is suggested and the window says so - inventing
/// adverts from image analysis would be confident and wrong, and the pages it
/// removed would not be recoverable.
/// </summary>
public partial class ComicReaderWindow
{
    private readonly string _path;
    private readonly RenameHistory _history;
    private readonly ObservableCollection<PageRow> _pages = [];

    private int _at;
    private bool _syncing;

    /// <summary>What reading the pages turned up, kept so each page can show its own reasons.</summary>
    private List<PageVerdict> _found = [];

    /// <summary>True when the file on disk was written to, so the caller can say so.</summary>
    public bool Changed { get; private set; }

    /// <summary>What was done, in the caller's words rather than a guess.</summary>
    public string? Note { get; private set; }

    public ComicReaderWindow(string path, RenameHistory history)
    {
        InitializeComponent();

        _path = path;
        _history = history;

        Title = Path.GetFileName(path);
        PageList.ItemsSource = _pages;

        Load();
    }

    private void Load()
    {
        _pages.Clear();

        try
        {
            var read = ComicPages.Read(_path);

            TxtSummary.Text = ComicPages.Describe(read, _path);

            foreach (var p in read)
            {
                var row = new PageRow { Page = p };
                row.PropertyChanged += OnRowChanged;
                _pages.Add(row);
            }

            BtnMarkAds.IsEnabled = read.Any(p => p.Kind == PageKind.Advertisement);

            Show(0);
        }
        catch (System.Exception ex)
        {
            TxtSummary.Text = $"This file could not be opened: {ex.Message}";
            BtnMarkAds.IsEnabled = false;
        }

        Tally();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PageRow.Drop)) return;

        Tally();

        // The page on screen should show its own state, not only the list.
        if (sender is PageRow row && _pages.IndexOf(row) == _at) Veil(row);
    }

    private void Tally()
    {
        var marked = _pages.Count(p => p.Drop);

        // A RAR can be read but not rewritten. Saying so once, and leaving the
        // buttons off, beats letting somebody tick forty pages and only then
        // find out.
        var editable = ComicArchive.CanEdit(_path);

        TxtMarked.Text = !editable
            ? "This one can be read but not changed - it is a RAR."
            : marked == 0 ? "Nothing ticked. Space ticks the page you are on."
            : marked == 1 ? "1 page ticked."
            : $"{marked} pages ticked.";

        BtnDrop.IsEnabled = editable && marked > 0 && marked < _pages.Count;
        BtnRecord.IsEnabled = editable && marked > 0;
    }

    // ---------------- Moving about ----------------

    private void Show(int index)
    {
        if (_pages.Count == 0) return;

        _at = System.Math.Clamp(index, 0, _pages.Count - 1);

        var row = _pages[_at];

        try
        {
            var bytes = ComicPages.Page(_path, row.Page.Entry);

            if (bytes is null) { PageImage.Source = null; }
            else
            {
                var image = new BitmapImage();

                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;   // so the stream can close
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();

                PageImage.Source = image;

                // The new page has its own size, so whatever fit is in force has
                // to be worked out again for it.
                Redraw();
            }
        }
        catch (System.Exception)
        {
            // A page that will not decode is still a page - show nothing rather
            // than stopping the reader.
            PageImage.Source = null;
        }

        TxtWhere.Text = $"Page {_at + 1} of {_pages.Count}";

        // Said twice, because in full screen the ordinary panel is not on
        // screen and a page being judged is exactly when the reason matters.
        TxtFullWhere.Text = $"Page {_at + 1} of {_pages.Count}"
                          + (row.Drop ? "  -  ticked for removal" : "");

        // Whatever was found on this page, on this page. A verdict listed
        // somewhere else is one nobody checks against the picture.
        if (_found.FirstOrDefault(v => v.Page == _at + 1) is { } verdict)
        {
            var said = $"Reads like {verdict.Looks.ToString().ToLowerInvariant()}: "
                     + string.Join("; ", verdict.Because);

            TxtWhy.Text = said;
            TxtWhy.Visibility = Visibility.Visible;

            TxtFullWhy.Text = said;
            TxtFullWhy.Visibility = Visibility.Visible;
        }
        else
        {
            TxtWhy.Visibility = Visibility.Collapsed;
            TxtFullWhy.Visibility = Visibility.Collapsed;
        }

        BtnBack.IsEnabled = _at > 0;
        BtnForward.IsEnabled = _at < _pages.Count - 1;

        Veil(row);

        // Keeping the list in step would otherwise come straight back here and
        // decode the same page a second time.
        if (!ReferenceEquals(PageList.SelectedItem, row))
        {
            _syncing = true;
            PageList.SelectedItem = row;
            PageList.ScrollIntoView(row);
            _syncing = false;
        }
    }

    private void Veil(PageRow row) =>
        DroppedVeil.Visibility = row.Drop ? Visibility.Visible : Visibility.Collapsed;

    private void OnBack(object sender, RoutedEventArgs e) => Show(_at - 1);
    private void OnForward(object sender, RoutedEventArgs e) => Show(_at + 1);

    /// <summary>
    /// Keep the page list from taking over a narrow window.
    ///
    /// The list is wide by default because scanner page names are long, but a
    /// fixed 320 pixels is most of a small window - at 760 wide the list was
    /// broader than the page, which is the wrong way round in a window whose
    /// job is showing the page. Held to three tenths, and only ever reduced, so
    /// dragging the splitter narrower is still obeyed.
    ///
    /// Three and not four: the arrows, the margins and two scrollbars take
    /// something like two hundred pixels before either pane sees any, so at
    /// two fifths of a small window the list was still the wider of the two.
    /// </summary>
    private void OnSized(object sender, SizeChangedEventArgs e)
    {
        if (_full || ListColumn.Width.Value <= 0) return;

        var most = ActualWidth * 0.3;

        if (ListColumn.Width.IsAbsolute && ListColumn.Width.Value > most && most > 60)
            ListColumn.Width = new GridLength(most);
    }

    /// <summary>
    /// Fit-to-width has to be worked out again whenever the space it fits into
    /// changes.
    ///
    /// It is a fixed pixel width set once from the viewport, and horizontal
    /// scrolling is off while it applies - so making the window narrower left
    /// the page at its old width with the right-hand edge cut off and no way to
    /// reach it. This covers the window, the splitter and anything else that
    /// moves the divider, because it listens to the pane itself.
    /// </summary>
    private void OnPageAreaSized(object sender, SizeChangedEventArgs e)
    {
        if (_fit == Fit.Width && e.WidthChanged) Redraw();
    }

    // ---------------- How big the page is drawn ----------------

    /// <summary>
    /// Whole page, fit to width, or actual size.
    ///
    /// Fit-page is the wrong default for judging a page and the right one for
    /// finding it: a comic page is roughly 2000 by 3000, so even filling a
    /// monitor is a three-times reduction and the lettering on an advert is
    /// unreadable. Fit-width is what makes a page checkable, and actual size is
    /// for the cases where even that is not enough.
    /// </summary>
    private enum Fit { Page, Width, Actual }

    private Fit _fit = Fit.Page;

    private void OnZoom(object sender, RoutedEventArgs e) =>
        SetFit(_fit switch
        {
            Fit.Page => Fit.Width,
            Fit.Width => Fit.Actual,
            _ => Fit.Page
        });

    private void SetFit(Fit fit)
    {
        _fit = fit;

        BtnZoom.Content = fit switch
        {
            Fit.Page => "Fit width",
            Fit.Width => "Actual size",
            _ => "Whole page"
        };

        Redraw();
    }

    /// <summary>Apply the current fit to whatever page is on screen.</summary>
    private void Redraw()
    {
        if (PageImage.Source is not System.Windows.Media.Imaging.BitmapSource bitmap)
            return;

        switch (_fit)
        {
            case Fit.Page:
                PageImage.Stretch = Stretch.Uniform;
                PageImage.Width = double.NaN;
                PageImage.Height = double.NaN;
                PageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                break;

            case Fit.Width:
                // The width of the space it has, less whatever the vertical bar
                // will take - otherwise adding the bar shrinks the viewport and
                // brings in a horizontal one as well.
                PageImage.Stretch = Stretch.Uniform;
                PageImage.Width = System.Math.Max(1, PageScroll.ActualWidth - 18);
                PageImage.Height = double.NaN;
                PageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                break;

            default:
                PageImage.Stretch = Stretch.None;
                PageImage.Width = bitmap.PixelWidth;
                PageImage.Height = bitmap.PixelHeight;
                PageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                break;
        }

        PageScroll.ScrollToTop();
    }

    /// <summary>
    /// The wheel scrolls, unless Control is held, in which case it changes how
    /// big the page is drawn - which is what every other picture viewer does.
    /// </summary>
    private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control)
            return;

        SetFit(e.Delta > 0
            ? _fit == Fit.Page ? Fit.Width : Fit.Actual
            : _fit == Fit.Actual ? Fit.Width : Fit.Page);

        e.Handled = true;
    }

    // ---------------- Filling the screen ----------------

    private bool _full;
    private WindowState _wasState;
    private WindowStyle _wasStyle;
    private ResizeMode _wasResize;

    /// <summary>
    /// The page, and nothing else.
    ///
    /// A comic page in a window sharing space with a list and two toolbars is
    /// about a third of the width of a monitor, which is not enough to read the
    /// lettering on an advert or tell a house ad from a splash page. These pages
    /// get removed on the strength of that look, so being able to actually see
    /// one is not a luxury.
    /// </summary>
    private void OnFullScreen(object sender, RoutedEventArgs e) => Fill(!_full);

    private void OnPageClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Fill(!_full);
    }

    private void Fill(bool on)
    {
        if (on == _full) return;

        _full = on;

        if (on)
        {
            _wasState = WindowState;
            _wasStyle = WindowStyle;
            _wasResize = ResizeMode;

            Bar.Visibility = Visibility.Collapsed;
            Notes.Visibility = Visibility.Collapsed;
            Bottom.Visibility = Visibility.Collapsed;

            // The arrows go too. They are a mouse convenience, and the keys do
            // the same job without covering any of the page.
            BackColumn.Width = new GridLength(0);
            ForwardColumn.Width = new GridLength(0);
            ListColumn.Width = new GridLength(0);

            Middle.Margin = new Thickness(0);
            PageFrame.Margin = new Thickness(0);
            PageFrame.CornerRadius = new CornerRadius(0);

            FullNotes.Visibility = Visibility.Visible;

            // Restored to Normal first, or a window that was already maximised
            // stays the size of the work area with the taskbar still over it.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            Bar.Visibility = Visibility.Visible;
            Notes.Visibility = Visibility.Visible;
            Bottom.Visibility = Visibility.Visible;

            BackColumn.Width = GridLength.Auto;
            ForwardColumn.Width = GridLength.Auto;
            ListColumn.Width = new GridLength(230);

            Middle.Margin = new Thickness(16, 0, 16, 8);
            PageFrame.Margin = new Thickness(10, 0, 10, 0);
            PageFrame.CornerRadius = new CornerRadius(6);

            FullNotes.Visibility = Visibility.Collapsed;

            WindowStyle = _wasStyle;
            ResizeMode = _wasResize;
            WindowState = _wasState;
        }

        // Going full screen to look at a page and being given the same
        // unreadable whole-page view is the wrong answer to the request. Fitting
        // the width is what a bigger screen is for.
        if (on && _fit == Fit.Page) SetFit(Fit.Width);

        Show(_at);

        // After the layout has settled, or fit-to-width measures against the
        // size the panel was before everything around it was hidden.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new System.Action(Redraw));
    }

    private void OnPagePicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        if (PageList.SelectedItem is PageRow row) Show(_pages.IndexOf(row));
    }

    /// <summary>
    /// Arrow keys, because that is how anybody reads a comic. Space marks the
    /// page, which turns going through an issue into one hand's work.
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Show(_at - 1); e.Handled = true; break;
            case Key.Right: Show(_at + 1); e.Handled = true; break;
            case Key.Home: Show(0); e.Handled = true; break;
            case Key.End: Show(_pages.Count - 1); e.Handled = true; break;

            case Key.Space:
                if (_pages.Count > 0) _pages[_at].Drop = !_pages[_at].Drop;
                e.Handled = true;
                break;

            case Key.F11: Fill(!_full); e.Handled = true; break;

            case Key.Z: OnZoom(this, e); e.Handled = true; break;

            // Only when it would do something. Escape on an ordinary window
            // should close it, and swallowing that would be a small surprise
            // every time.
            case Key.Escape when _full: Fill(false); e.Handled = true; break;
        }
    }

    // ---------------- Marking and removing ----------------

    private void OnMarkAds(object sender, RoutedEventArgs e)
    {
        var marked = 0;

        foreach (var row in _pages.Where(p => p.Page.Kind == PageKind.Advertisement))
        {
            row.Drop = true;
            marked++;
        }

        TxtSummary.Text = $"Ticked the {marked} page(s) the file calls adverts. "
                        + "Look through them before removing anything - a mark is somebody's "
                        + "judgement about a page, not a fact about it.";
    }

    /// <summary>
    /// Read every page and tick the ones whose words say they are not story.
    ///
    /// Nearly no comic is marked up beyond its front cover, so the button above
    /// has nothing to act on. This reads what is printed instead - a page
    /// saying ON SALE, carrying a price and a web address is making a claim
    /// about itself - and shows the evidence for each one. Nothing is removed;
    /// the ticks are a suggestion to look at.
    /// </summary>
    private async void OnSuggest(object sender, RoutedEventArgs e)
    {
        if (!PageOcr.Available)
        {
            MessageBox.Show(this,
                "Windows has no OCR language installed, so the pages cannot be read.\n\n"
                + "Add one under Settings > Time & language.",
                "Nothing to read with", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnSuggest.IsEnabled = false;

        // Everything that touches a control goes through here, whether or not
        // the await came back on the UI thread. It usually does - but this
        // handler is async void, so anything that escapes it reaches the
        // dispatcher and ends the program rather than the method, and this
        // exact exception has been in the log once already.
        void OnUi(System.Action work) => Dispatcher.Invoke(work);

        try
        {
            TxtSummary.Text = "Reading the pages...";

            var reading = await PageOcr.ReadAsync(
                _path,
                (page, total) => OnUi(() =>
                    TxtSummary.Text = $"Reading page {page} of {total}..."));

            if (reading.Broken)
            {
                OnUi(() => TxtSummary.Text = $"No page could be read - {reading.LastTrouble}");
                return;
            }

            OnUi(() =>
            {
                var verdicts = PageJudge.JudgeAll(reading.Pages, [.. _pages.Select(p => p.Page)]);

                _found = verdicts.Where(v => v.Suggested && v.Weight < 100).ToList();

                foreach (var v in _found)
                {
                    var row = _pages.FirstOrDefault(p => p.Page.Number == v.Page - 1);

                    // Only adverts are ticked. An indicia is where the copyright
                    // lives and a letters page is somebody's writing - both worth
                    // pointing at, neither worth ticking for deletion.
                    if (row is not null && v.Looks == PageKind.Advertisement) row.Drop = true;
                }

                var ads = _found.Count(v => v.Looks == PageKind.Advertisement);
                var other = _found.Count - ads;

                TxtSummary.Text = _found.Count == 0
                    ? "Read every page and found nothing that reads like an advert. "
                      + "That is a real answer - plenty of scans have had the adverts taken out already."
                    : $"Ticked {ads} page(s) that read like adverts"
                      + (other > 0
                          ? $", and found {other} more that look like a masthead or a letters page - "
                            + "those are listed but not ticked, since they are not adverts."
                          : ".")
                      + " Each page in the list says what was found on it. Look before removing anything.";

                Show(_at);
            });
        }
        catch (System.Exception ex)
        {
            OnUi(() => TxtSummary.Text = $"Reading the pages failed: {ex.Message}");
        }
        finally { OnUi(() => BtnSuggest.IsEnabled = true); }
    }

    /// <summary>
    /// Write the ticks into the comic instead of acting on them.
    ///
    /// Most comics arrive marked up no further than the front cover, so the
    /// button above has nothing to act on and there is no honest way to find the
    /// adverts by machine. This is the answer: go through it once, and the
    /// answer stays in the file - readable by Komga, by Kavita, and by this
    /// program the next time. Nothing is removed, so it costs nothing to be
    /// wrong.
    /// </summary>
    private void OnRecord(object sender, RoutedEventArgs e)
    {
        var ticked = _pages.Where(p => p.Drop).ToList();
        if (ticked.Count == 0) return;

        var trouble = ComicPages.Mark(
            _path,
            ticked.ToDictionary(t => t.Page.Entry, _ => PageKind.Advertisement),
            _history);

        if (trouble is not null)
        {
            MessageBox.Show(this, trouble, "Nothing was changed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Changed = true;
        Note = $"{ticked.Count} page(s) marked as adverts in the file";

        Load();

        // Re-ticked, because they are still the pages somebody was working
        // through and losing the ticks would mean starting again.
        foreach (var row in _pages.Where(p => p.Page.Kind == PageKind.Advertisement))
            row.Drop = true;

        TxtSummary.Text = $"{ticked.Count} page(s) are now recorded as adverts inside the comic "
                        + "itself. Nothing was removed. Any reader that understands ComicInfo.xml "
                        + "will see the same thing, and reopening this shows them already ticked.";
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var dropping = _pages.Where(p => p.Drop).ToList();

        if (dropping.Count == 0) return;

        if (MessageBox.Show(
                $"Remove {dropping.Count} page(s) from {Path.GetFileName(_path)}?\n\n"
                + "The comic is rebuilt without them and the original goes to the Recycle Bin, "
                + "which is the only way back - the pages themselves are not kept anywhere.",
                "Remove pages", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
        {
            return;
        }

        var trouble = ComicPages.Remove(
            _path, [.. dropping.Select(d => d.Page.Entry)], _history);

        if (trouble is not null)
        {
            MessageBox.Show(this, trouble, "Nothing was changed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Changed = true;
        Note = $"{dropping.Count} page(s) removed - the original is in the Recycle Bin";

        Load();
    }

    /// <summary>
    /// Give every page a plain numbered name.
    ///
    /// Tidiness, and said as tidiness. It does not fix reading order: across
    /// 250 real comics the order pages sort in already matched the order their
    /// numbers imply in every one, so there is no ordering bug here to fix and
    /// claiming otherwise would be selling a rewrite of somebody's archive on a
    /// promise it does not keep.
    /// </summary>
    private void OnRenumber(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0) return;

        if (!ComicArchive.CanEdit(_path))
        {
            MessageBox.Show(this,
                "This one is a RAR, and there is no way to rewrite one safely. "
                + "Converting it to .cbz with any comic tool would make it editable.",
                "Nothing was changed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var first = _pages[0].Page.Name;

        if (MessageBox.Show(this,
                $"Rename all {_pages.Count} pages to 001, 002 and so on?\n\n"
                + $"They are currently named like \"{first}\".\n\n"
                + "The order they read in does not change - only the names. The comic is "
                + "rebuilt and the original goes to the Recycle Bin.",
                "Rename the pages", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
        {
            return;
        }

        var trouble = ComicPages.Renumber(_path, _history);

        if (trouble is not null)
        {
            MessageBox.Show(this, trouble, "Nothing was changed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Changed = true;
        Note = $"{_pages.Count} page(s) renamed";

        var wasAt = _at;

        Load();
        Show(wasAt);

        TxtSummary.Text = $"Every page is now named 001 upwards. The order is exactly what it was.";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
