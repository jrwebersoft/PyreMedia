using System.Collections.Specialized;
using System.IO;
using System.Linq;
using PyreMedia.Core;
using PyreMedia.Core.History;
using PyreMedia.Core.Organizing;
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

        // The audio pane keeps its own state but shares the settings file and
        // the history, so a music run undoes from the same History window.
        MusicPane.Attach(_vm.Settings, _vm.History);
        BooksPane.Attach(_vm.Settings, _vm.History);

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
        //
        // Otherwise scan straight away. Opening on an empty list and waiting to
        // be asked is a step with no decision in it - there is nothing to
        // choose, the folders are already configured, and the answer is always
        // yes. RunSetup does the same scan when setup finishes, so this is the
        // path for every launch after the first.
        Loaded += (_, _) =>
        {
            if (!_vm.Settings.SetupCompleted)
            {
                RunSetup(firstRun: true);
                return;
            }

            if (!_vm.Settings.ScanOnLaunch) return;

            if (_vm.HasFolders && _vm.ScanCommand.CanExecute(null))
                _vm.ScanCommand.Execute(null);
        };

        // Keep the log scrolled to the newest entry.
        ((INotifyCollectionChanged)_vm.Log).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    /// <summary>
    /// Move finished films and episodes out of the staging folders into their
    /// libraries.
    ///
    /// What counts as finished is taken from the history rather than from the
    /// list on screen, and that is deliberate. The video side plans one item at
    /// a time, so nothing here knows the state of the other three hundred; but
    /// a file the history records renaming, which is still sitting where it was
    /// put, has demonstrably been dealt with. It also survives closing the
    /// program, which a list in memory does not.
    /// </summary>
    private void OnMoveCompletedVideo(object sender, RoutedEventArgs e)
    {
        var staging = _vm.Settings.TvFolders.Concat(_vm.Settings.MovieFolders)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (staging.Count == 0)
        {
            System.Windows.MessageBox.Show("No TV or movie folders have been added yet.",
                "Nothing to move", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var video = _vm.Settings.AllowedFileTypes
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().ToLowerInvariant())
            .ToHashSet();

        // Newest entry per path wins: a file renamed twice is finished at
        // whatever it is called now, not at what it was called first.
        var finished = _vm.History.Read()
            .Where(h => h.Action is HistoryAction.Rename or HistoryAction.Move)
            .GroupBy(h => h.NewPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(h => h.Timestamp).First())
            .Where(h => video.Contains(Path.GetExtension(h.NewPath).ToLowerInvariant()))
            .Where(h => File.Exists(h.NewPath))
            .ToList();

        // Grouped by which staging folder they sit under, because each has its
        // own library and the layout below it has to be preserved.
        var byRoot = staging.ToDictionary(
            root => root,
            root => finished
                .Where(h => h.NewPath.StartsWith(
                    root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                .Select(h => h.NewPath)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        var total = byRoot.Sum(kv => kv.Value.Count);

        if (total == 0)
        {
            System.Windows.MessageBox.Show(
                "Nothing has been renamed yet, so nothing is finished. Rename files first - "
                + "a file counts as finished once it has been renamed and is still where "
                + "it was put.",
                "Nothing to move", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Split by what each file is, not by which list its staging folder came
        // from. There is only one folder list now - setup writes MovieFolders
        // empty and everything is added to TvFolders - so asking which list the
        // root belonged to always answered "TV", and every finished film was
        // moved into the TV library. MovieDestination was unreachable.
        //
        // An episode is a file whose name carries a season and episode number,
        // which is the same test the planner uses to decide what it is.
        var groups =
            from pair in byRoot
            where pair.Value.Count > 0
            from file in pair.Value
            group file by (Root: pair.Key,
                           Tv: PyreMedia.Core.Naming.EpisodeMatcher.Parse(
                                   Path.GetFileName(file)) is not null)
            into g
            select g;

        foreach (var group in groups.OrderBy(g => g.Key.Root).ThenByDescending(g => g.Key.Tv))
        {
            var root = group.Key.Root;
            var tv = group.Key.Tv;
            var files = group.ToList();

            var kind = tv ? LibraryKind.Tv : LibraryKind.Movie;
            var destination = _vm.Settings.DestinationFor(kind);

            // Blank means not chosen, not "leave them here". Ask rather than
            // put somebody's library somewhere they did not pick.
            if (string.IsNullOrWhiteSpace(destination))
            {
                var pick = System.Windows.MessageBox.Show(
                    $"No {(tv ? "TV" : "movie")} library folder has been chosen yet.\n\n"
                    + $"{files.Count} finished files are waiting in:\n{root}\n\nChoose where they go?",
                    "Where should finished files go?", MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (pick != MessageBoxResult.OK) continue;

                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = tv ? "Where your TV library lives" : "Where your movie library lives"
                };

                if (dialog.ShowDialog() != true) continue;

                destination = dialog.FolderName;

                if (tv) _vm.Settings.TvDestination = destination;
                else _vm.Settings.MovieDestination = destination;

                _vm.Settings.Save();
            }

            if (System.Windows.MessageBox.Show(
                    $"Move {files.Count} finished files to:\n\n{destination}\n\n"
                    + "Their folder layout is kept as it is now, and this undoes from History.",
                    "Move completed", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK) continue;

            var result = CompletedMover.Move(files, root, destination, _vm.History);

            _vm.Log.Add($"Moved {result.Moved} to {destination}, skipped {result.Skipped}, "
                      + $"failed {result.Failed}. Batch {result.BatchId}.");

            foreach (var error in result.Errors.Take(20)) _vm.Log.Add($"ERROR {error}");

            if (result.EmptyFolders.Count > 0)
                _vm.Log.Add($"{result.EmptyFolders.Count} folders are now empty - none were removed.");
        }
    }

    /// <summary>
    /// All audio / Music / Audiobooks. Sits beside the pane rather than inside
    /// it so it lines up with the video tab's filter, which is the point of
    /// having the same shape on both halves.
    /// </summary>
    private void OnAudioFilter(object sender, RoutedEventArgs e)
    {
        // Fires while the XAML is still being built, before the pane exists.
        if (MusicPane is null) return;

        MusicPane.Show((sender as FrameworkElement)?.Tag as string ?? "all");
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
            inner.ScrollToVerticalOffset(
                inner.VerticalOffset
                + SectionScroll.InnerStep(inner.CanContentScroll, move.By,
                                          SystemParameters.WheelScrollLines));
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

            // Shares of the window, not fixed strips.
            //
            // These are built here rather than taken from the XAML - this method
            // clears the column definitions and rebuilds them - so the widths in
            // the markup are decoration and these are the real ones. At 270 and
            // 330 pixels the library and the match panel stayed those sizes on a
            // three-thousand-pixel monitor while every spare pixel went to the
            // pane that needed it least.
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 220 });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1.3, GridUnitType.Star), MinWidth = 280 });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(2.2, GridUnitType.Star), MinWidth = 360 });

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

        // All of them, then one scan. One command per folder started one scan
        // per folder, and they raced each other into a duplicated library list.
        _vm.AddFolders(dlg.FolderNames);
    }

    /// <summary>
    /// Play the selected item, to see what it actually is.
    ///
    /// The fastest answer to "which episode is this?" for a disc rip, where the
    /// filename is a title index and the metadata is empty. Opens at the
    /// beginning, full screen if that is the preference.
    ///
    /// It hands off to that player rather than embedding one: WPF's MediaElement
    /// goes through Media Foundation, which on a stock Windows install plays
    /// neither Matroska nor HEVC nor DTS - which is to say, not the files you
    /// would most want to look at.
    /// </summary>
    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedItem is not { } item) return;

        Play(item.Media.MainFile);
    }

    /// <summary>Open one file in the player, and say so or say why not.</summary>
    private void Play(string file)
    {
        var settings = _vm.Settings;

        if (PyreMedia.Core.Media.Preview.Open(
                file, settings.PlayerPath, settings.PlayFullScreen) is { } trouble)
        {
            System.Windows.MessageBox.Show(this,
                $"{trouble}\n\n{System.IO.Path.GetFileName(file)}",
                "Could not play it", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _vm.WriteLog($"Playing {System.IO.Path.GetFileName(file)} - "
                     + PyreMedia.Core.Media.Preview.Describe(settings.PlayerPath));
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

    /// <summary>
    /// Remux the one file on this row.
    ///
    /// Per file as well as per item, because a season is twenty files and
    /// wanting to deal with one of them is the normal case - the episode with
    /// the stray commentary track, the one that came from a different source.
    /// Scoping to the whole show meant finding it again in a list of twenty.
    /// </summary>
    private void OnRemuxOne(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PlannedActionItem row) return;

        // Where it is now, not where the plan would put it - nothing has moved
        // yet, and the remux has to open the file that exists.
        OpenRemux([row.Action.SourcePath], row.SourceName);
    }

    /// <summary>Play the one file on this row, to see what it actually is.</summary>
    private void OnPlayOne(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PlannedActionItem row) return;

        Play(row.Action.SourcePath);
    }

    /// <summary>Fetch this item's .nfo and artwork again, renaming nothing.</summary>
    private async void OnRedoSidecars(object sender, RoutedEventArgs e)
    {
        // async void: an unhandled throw here would take the app down.
        try { await _vm.RedoSidecarsAsync(); }
        catch (Exception ex) { AppLog.Error("Redo sidecars", ex); }
    }

    private void OpenRemux(List<string> files, string scope)
    {
        // Disc images are organised, never opened. mkvmerge cannot write one
        // and ffprobe reading one reads a filesystem rather than a stream, so
        // an .iso in this list is a row that can only ever fail. Filtered at the
        // one door all three entry points go through.
        files = [.. files.Where(f => !_vm.Settings.IsDiscImage(f))];

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

        // For a series the artwork covers the whole run, so it goes in the show's
        // folder rather than beside each episode. Same answer the .nfo writer
        // uses, including its refusal to treat a scan root as a show.
        var seriesFolder = isTv ? _vm.ShowFolderFor(files[0]) : null;

        new ArtworkWindow(_vm.Settings, match.Display, files,
                          ct => _vm.GetArtworkAsync(isTv, id, ct),
                          seriesFolder) { Owner = this }.ShowDialog();
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

        // A disc rip has to be read before the window opens, because the reading
        // is what fills it in. It costs a probe per title and a fingerprint for
        // any pair that collide on length, so it happens here rather than during
        // every scan - and only for the item actually being renumbered.
        PyreMedia.Core.Organizing.RipReading? rip = null;

        if (item.Media.IsDiscRip)
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

            try { rip = ReadTheDisc(item.Media, show); }
            finally { Mouse.OverrideCursor = null; }
        }

        var dlg = new EpisodeMapWindow(
            _vm.Settings, item.Media, show, _vm.CurrentEpisodeSource,
            (source, ct) => _vm.GetShowFromAsync(search, source, ct), rip)
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true)
            _vm.ApplyOverrides(dlg.Result, dlg.CurrentShow);
    }

    /// <summary>
    /// Measure a disc rip's titles and work out which are episodes.
    ///
    /// Every title is probed for its length and chapter count, and any pair
    /// that come out within a few seconds of each other are compared by their
    /// audio - because length cannot tell "the disc offers this episode twice"
    /// from "these are two episodes cut to the same broadcast slot", and a real
    /// rip on hand is nine episodes running 21:17 to 21:22.
    /// </summary>
    private PyreMedia.Core.Organizing.RipReading ReadTheDisc(MediaItem media, PyreMedia.Core.Models.TvShow show)
    {
        var probe = new PyreMedia.Core.Media.MediaProbe(_vm.Settings.FfprobePath);
        var titles = new List<PyreMedia.Core.Organizing.RipTitle>();

        foreach (var file in media.Files.Where(PyreMedia.Core.Organizing.DiscRip.IsRipped))
        {
            var info = probe.ProbeAsync(file, CancellationToken.None).GetAwaiter().GetResult();

            long size = 0;
            try { size = new FileInfo(file).Length; } catch (Exception) { }

            titles.Add(new PyreMedia.Core.Organizing.RipTitle(
                file,
                PyreMedia.Core.Organizing.DiscRip.IndexOf(file),
                info?.DurationSeconds ?? 0,
                info?.Chapters ?? 0,
                size));
        }

        // The files may have changed since last time this was opened.
        PyreMedia.Core.Organizing.DiscFootage.Forget();

        var reading = PyreMedia.Core.Organizing.DiscRip.Read(
            titles, PyreMedia.Core.Organizing.DiscFootage.Compare);

        _vm.WriteLog($"Disc rip: {reading.Episodes.Count} episode(s), "
                     + $"{reading.Ignored.Count} title(s) set aside.");

        // Ask the catalogue whether it recognises this disc. Somebody who has
        // ripped it already may have written down which title held which
        // episode, which turns an order into names. Fails quietly: no network,
        // no entry, or a rate limit leaves the local reading standing.
        try
        {
            var match = PyreMedia.Core.Organizing.DiscDb
                .FindAsync(show.Name, show.Year, reading.Episodes)
                .GetAwaiter().GetResult();

            if (match is not null)
            {
                reading.Named = match.Titles;
                reading.Certainty = PyreMedia.Core.Organizing.RipCertainty.Named;
                reading.Caution =
                    $"Matched \"{match.Disc}\" from {match.Release} in TheDiscDb, which "
                    + $"accounted for {match.Matched} of {match.OutOf} titles. These are the "
                    + "episodes somebody who ripped this same disc recorded finding on it, "
                    + "rather than a guess from the order.";

                _vm.WriteLog($"Disc rip: matched {match.Disc} in TheDiscDb.");
            }
        }
        catch (Exception)
        {
            // An improvement on a good answer, never a requirement.
        }

        return reading;
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
        {
            _vm.ReloadServices();

            // The music pattern can be edited in two places, and the audio pane
            // read its copy when the window opened. Without this, changing it in
            // Settings appears to work and is then overwritten by the stale copy
            // the moment the pane saves - which is the whole reason two editors
            // for one value are usually a mistake.
            MusicPane.Attach(_vm.Settings, _vm.History);
            BooksPane.Attach(_vm.Settings, _vm.History);
        }
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
    /// <summary>
    /// Fill in what the .nfo files already on disk are missing.
    ///
    /// Surveyed first and reported before anything is written, because the
    /// interesting number is not how many files there are but how many can be
    /// filled in without guessing - a file whose .nfo does not name its own
    /// title is left alone and listed, never matched on a name that might
    /// belong to something else.
    /// </summary>
    private async void OnFillGaps(object sender, RoutedEventArgs e)
    {
        // async void: an unhandled throw here would take the app down.
        try
        {
            var survey = await Task.Run(() => _vm.SurveyGaps());

            if (survey.Total == 0)
            {
                MessageBox.Show(this,
                    "No video files found in the folders you have added.",
                    "Fill in gaps", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var nl = Environment.NewLine;

            if (survey.Ready.Count == 0)
            {
                MessageBox.Show(this,
                    $"Nothing can be filled in without guessing." + nl + nl
                    + $"{survey.Complete} file(s) already have everything." + nl
                    + $"{survey.Unmatched.Count} are missing something but their .nfo does not "
                    + "name which title they are, so they need matching in the usual way." + nl
                    + $"{survey.NoNfo} have no .nfo at all - a rename writes one.",
                    "Fill in gaps", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sample = string.Join(nl, survey.Ready.Take(8)
                .Select(t => $"   {t.Name}  -  {t.Gaps.Summary}"));

            if (survey.Ready.Count > 8)
                sample += $"{nl}   ...and {survey.Ready.Count - 8} more";

            var answer = MessageBox.Show(this,
                $"{survey.Ready.Count} file(s) can be filled in from the id their .nfo already "
                + "carries, so nothing has to be guessed:" + nl + nl + sample + nl + nl
                + $"{survey.Unmatched.Count} other file(s) are missing something but name no title, "
                + $"and are left alone. {survey.Complete} already have everything." + nl + nl
                + "Existing .nfo files are merged, not replaced: watch state, your own ratings, "
                + "tags and artwork are kept exactly as they are." + nl + nl
                + "Go ahead?",
                "Fill in gaps", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            await _vm.FillGapsAsync(survey.Ready);
        }
        catch (Exception ex)
        {
            AppLog.Error("Fill in gaps", ex);

            MessageBox.Show(this, $"Could not do it.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Fill in gaps", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
