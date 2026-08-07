using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PyreMedia.App.ViewModels;
using PyreMedia.App.Views;
using Wpf.Ui.Appearance;

namespace PyreMedia.App;

public partial class MainWindow
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        FitToWorkArea();
        LogPathText.Text = PyreMedia.Core.AppPaths.Display(AppLog.FilePath);

        // The view model finds the collisions; the shell asks about them.
        _vm.ResolveConflicts = conflicts =>
        {
            var dlg = new ConflictWindow(conflicts) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        };

        // Reflow rather than clip. Remote sessions - a phone over RDP especially -
        // give a viewport far narrower than three fixed columns need.
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) => ApplyResponsiveLayout();

        // First run: offer setup rather than opening on an empty window whose
        // only guidance is a line in a collapsed log.
        Loaded += (_, _) =>
        {
            if (_vm.Settings.SetupCompleted) return;
            RunSetup(firstRun: true);
        };

        // Keep the log scrolled to the newest entry.
        ((INotifyCollectionChanged)_vm.Log).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    private bool? _isNarrow;

    /// <summary>
    /// In the stacked layout the wheel follows the pointer, by the rule in
    /// <see cref="SectionScroll"/>. This part is only the plumbing: work out where
    /// the section sits, ask what should move, and move it.
    /// <para>
    /// It has to be a preview handler. WPF's ScrollViewer marks the wheel handled
    /// even when it is already at its limit and cannot move, so by the time the
    /// event bubbles there is nothing left to decide.
    /// </para>
    /// </summary>
    private void OnSectionWheel(object sender, MouseWheelEventArgs e)
    {
        // Side by side, each section scrolls on its own and the page doesn't move.
        if (_isNarrow != true || sender is not FrameworkElement section) return;
        if (!section.IsVisible || PanelScroll.ViewportHeight <= 0) return;

        double top;
        try
        {
            top = section.TransformToAncestor(PanelScroll).Transform(default).Y;
        }
        catch (InvalidOperationException)
        {
            return;   // not parented yet; let the page have it
        }

        var inner = FindScrollViewer(section);

        var move = SectionScroll.Decide(
            top, section.ActualHeight, PanelScroll.ViewportHeight,
            inner?.VerticalOffset ?? 0, inner?.ScrollableHeight ?? 0, e.Delta);

        if (move.By == 0) return;

        if (move.Target == ScrollTarget.Inside && inner is not null)
            inner.ScrollToVerticalOffset(inner.VerticalOffset + move.By);
        else
            PanelScroll.ScrollToVerticalOffset(PanelScroll.VerticalOffset + move.By);

        e.Handled = true;
    }

    /// <summary>The scrolling control inside a section, wherever its template put it.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// How tall the stacked cards are allowed to get. Enough to browse without
    /// swallowing the page.
    /// <para>
    /// The Match cap is worked out from what has to fit rather than picked as a
    /// round number. A search result is a 96px poster plus padding, and the title
    /// box, year box and Search button above the list eat about 156px, so the old
    /// 280px cap could only ever show one result - which is no use to scroll
    /// through. Three results when the window has the height to spare, two when
    /// it doesn't.
    /// </para>
    /// </summary>
    private void ApplyNarrowHeights()
    {
        const double resultRow = 104;
        const double matchHeader = 156;

        var rows = ActualHeight >= 900 ? 3 : 2;

        CardLibrary.MaxHeight = Math.Max(240, ActualHeight * 0.32);
        CardMatch.MaxHeight = Math.Max(matchHeader + rows * resultRow, ActualHeight * 0.40);
    }

    /// <summary>
    /// Three side-by-side columns need roughly 1000px. Below that they get
    /// squeezed until the rightmost is unusable, so stack them instead and let
    /// the whole thing scroll.
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        const double threshold = 1000;
        var narrow = ActualWidth > 0 && ActualWidth < threshold;

        if (_isNarrow == narrow)
        {
            // Rebuilding the tree on every resize would be wasteful, but the
            // stacked card heights are worked out from the window height, so those
            // do have to follow it - otherwise dragging the window taller leaves
            // the match list the same peephole it was.
            if (narrow) ApplyNarrowHeights();
            return;
        }

        _isNarrow = narrow;

        PanelGrid.ColumnDefinitions.Clear();
        PanelGrid.RowDefinitions.Clear();

        if (narrow)
        {
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Every section sizes to its content and the page scrolls as one.
            // Stretching a section to fill left the mostly-empty Changes card
            // occupying the screen while the library it feeds was squeezed.
            for (var i = 0; i < 3; i++)
                PanelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Place(CardLibrary, 0, 0);
            Place(CardMatch, 1, 0);
            Place(CardChanges, 2, 0);

            ApplyNarrowHeights();
            CardLibrary.Margin = new Thickness(0, 0, 0, 8);
            CardMatch.Margin = new Thickness(0, 0, 0, 8);
            CardChanges.Margin = new Thickness(0, 0, 0, 8);

            PanelScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            // Own row, full width - beside the buttons there was nothing left
            // for it and the text clipped mid-word.
            Grid.SetRow(StatusPanel, 1);
            Grid.SetColumn(StatusPanel, 0);
            Grid.SetColumnSpan(StatusPanel, 4);
            StatusPanel.Margin = new Thickness(0, 8, 0, 0);

            Split1.Visibility = Visibility.Collapsed;
            Split2.Visibility = Visibility.Collapsed;
        }
        else
        {
            PanelScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            CardChanges.Margin = new Thickness(0);

            Grid.SetRow(StatusPanel, 0);
            Grid.SetColumn(StatusPanel, 2);
            Grid.SetColumnSpan(StatusPanel, 1);
            StatusPanel.Margin = new Thickness(20, 0, 0, 0);
            PanelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270), MinWidth = 200 });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330), MinWidth = 260 });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });

            Place(CardLibrary, 0, 0);
            Place(CardMatch, 0, 2);
            Place(CardChanges, 0, 4);

            CardLibrary.MaxHeight = double.PositiveInfinity;
            CardMatch.MaxHeight = double.PositiveInfinity;
            CardLibrary.Margin = new Thickness(0);
            CardMatch.Margin = new Thickness(0);

            Grid.SetRow(Split1, 0); Grid.SetColumn(Split1, 1);
            Grid.SetRow(Split2, 0); Grid.SetColumn(Split2, 3);
            Split1.Visibility = Visibility.Visible;
            Split2.Visibility = Visibility.Visible;
        }

        static void Place(UIElement el, int row, int col)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
        }
    }

    /// <summary>
    /// Shrink and re-centre so the window always fits the usable desktop.
    /// Without this, the default size overflows shorter displays and the title
    /// bar ends up above the top of the screen, where it can't be grabbed.
    /// </summary>
    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;

        // Leave a small margin so the window doesn't sit flush against the edges.
        var maxW = Math.Max(MinWidth, work.Width - 40);
        var maxH = Math.Max(MinHeight, work.Height - 40);

        if (Width > maxW) Width = maxW;
        if (Height > maxH) Height = maxH;

        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;

        // A display shorter than the minimum leaves no good option but to maximise.
        if (work.Height < MinHeight || work.Width < MinWidth)
            WindowState = WindowState.Maximized;
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder containing TV show folders",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) != true)
            return;

        foreach (var path in dlg.FolderNames)
            _vm.AddFolderCommand.Execute(path);
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.IO.File.Exists(AppLog.FilePath))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo { FileName = AppLog.FilePath, UseShellExecute = true });
            else
                MessageBox.Show(this,
                    "No log yet.\n\nIt will be written to:\n"
                    + PyreMedia.Core.AppPaths.Display(AppLog.FilePath),
                    "PyreMedia", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("Open log", ex);
        }
    }

    /// <summary>Toolbar entry: everything scanned.</summary>
    private void OnOpenRemux(object sender, RoutedEventArgs e)
        => OpenRemux(_vm.Items.SelectMany(i => i.Media.Files).Distinct().ToList(), "the whole library");

    /// <summary>Per-item entry: just this show/movie, however many episodes it holds.</summary>
    private void OnOffsetDown(object sender, RoutedEventArgs e) => _vm.AdjustOffsetCommand.Execute("-1");
    private void OnOffsetUp(object sender, RoutedEventArgs e) => _vm.AdjustOffsetCommand.Execute("1");
    private void OnOffsetReset(object sender, RoutedEventArgs e) => _vm.AdjustOffsetCommand.Execute("reset");

    private void OnOpenRemuxForItem(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedItem is not { } sel) return;
        OpenRemux([.. sel.Media.Files], sel.DisplayName);
    }

    private void OpenRemux(List<string> files, string scope)
    {
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Nothing scanned yet. Add a folder and press Scan first.",
                "Tracks", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new RemuxWindow(_vm.Settings, _vm.History, files, scope) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Pick which poster, fanart and clear logo get saved beside this item's
    /// files. TMDb offers dozens of each, so a default choice is often the wrong
    /// one - and once artwork is on disk there is nothing in the file to say
    /// which of them it was.
    /// </summary>
    private void OnOpenArtwork(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedItem is not { } item || _vm.SelectedResult is not { } match)
        {
            MessageBox.Show(this, "Pick a match first - the artwork comes from it.",
                "Artwork", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var isTv = _vm.SearchAsTv;
        var id = isTv ? match.Show?.TmdbId : match.MovieResult?.TmdbId;

        if (string.IsNullOrWhiteSpace(id))
        {
            MessageBox.Show(this, "That match has no TMDb id, so there is no artwork to fetch.",
                "Artwork", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // The videos as they are now, not as the plan would name them - artwork
        // written beside a name that hasn't been applied yet would be orphaned.
        var files = item.Media.Files
            .Where(f => _vm.Settings.VideoExtensions.Contains(
                            System.IO.Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        if (files.Count == 0)
        {
            MessageBox.Show(this, "No video files in this item to put artwork beside.",
                "Artwork", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new ArtworkWindow(_vm.Settings, match.Display, files,
                          ct => _vm.GetArtworkAsync(isTv, id, ct)) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Renumber screen. Only offered for TV once the episode list is in hand -
    /// without it there's nothing to map files onto.
    /// </summary>
    private void OnOpenRenumber(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedItem is not { } item
            || _vm.CurrentShow is not { } show
            || _vm.CurrentSearchResult is not { } search)
        {
            MessageBox.Show(this, "Pick a TV show match first - the episode list comes from it.",
                "Renumber", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new EpisodeMapWindow(
            _vm.Settings, item.Media, show, _vm.CurrentEpisodeSource,
            (source, ct) => _vm.GetShowFromAsync(search, source, ct))
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true)
            _vm.ApplyOverrides(dlg.Result, dlg.CurrentShow);
    }

    /// <summary>
    /// Show setup. On first run the answers are applied and a scan kicked off;
    /// re-run later it behaves the same, so it doubles as a way back to a known
    /// state without hunting through Settings.
    /// </summary>
    private void RunSetup(bool firstRun)
    {
        try
        {
            var dlg = new SetupWindow(_vm.Settings) { Owner = firstRun ? null : this };

            if (dlg.ShowDialog() == true)
            {
                _vm.ReloadServices();
                _vm.RefreshFolders();

                if (_vm.HasFolders && _vm.ScanCommand.CanExecute(null))
                    _vm.ScanCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Setup", ex);
        }
    }

    private void OnOpenSetup(object sender, RoutedEventArgs e) => RunSetup(firstRun: false);

    private void OnOpenAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow(_vm.Settings) { Owner = this }.ShowDialog();
    }

    private void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        new HistoryWindow(_vm.History) { Owner = this }.ShowDialog();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_vm.Settings) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.ReloadServices();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        var next = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        ApplicationThemeManager.Apply(next);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // Commit the pending edit before the command reads the bound value.
        if (sender is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        if (_vm.SearchCommand.CanExecute(null))
            _vm.SearchCommand.Execute(null);
    }

    /// <summary>
    /// Confirm before clearing leftover folders. Everything in them is release
    /// junk and it goes to the Recycle Bin, but it is still a deletion of things
    /// the user never explicitly picked, so it gets asked about - and the log has
    /// already named every folder by the time this appears.
    /// </summary>
    private void OnClearLeftovers(object sender, RoutedEventArgs e)
    {
        var folders = _vm.LeftoverFolders;
        if (folders.Count == 0) return;

        var names = string.Join(Environment.NewLine, folders.Take(12)
            .Select(f => "   " + System.IO.Path.GetFileName(f.TrimEnd(System.IO.Path.DirectorySeparatorChar))));

        if (folders.Count > 12) names += $"{Environment.NewLine}   ...and {folders.Count - 12} more";

        var nl = Environment.NewLine;

        var answer = MessageBox.Show(this,
            $"{folders.Count} folder(s) hold nothing but release leftovers - no video, no "
            + "subtitles, no metadata:" + nl + nl + names + nl + nl
            + "Remove them, contents and all?" + nl + nl
            + (_vm.Settings.DeleteToRecycleBin
                ? "The files go to the Recycle Bin, so this can be undone from there."
                : "Deleting to the Recycle Bin is switched off, so this cannot be undone."),
            "Clear leftover folders", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes) _vm.ClearLeftoversCommand.Execute(null);
    }
}
