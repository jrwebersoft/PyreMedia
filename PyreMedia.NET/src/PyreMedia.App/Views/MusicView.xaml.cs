using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PyreMedia.Core;
using PyreMedia.Core.History;
using PyreMedia.Core.Music;
using PyreMedia.Core.Organizing;

namespace PyreMedia.App.Views;

/// <summary>One proposed change, as the grid shows it.</summary>
public sealed class MusicRow
{
    public required MusicAction Action { get; init; }

    /// <summary>True when this file is a chapter of a book rather than a track.</summary>
    public bool IsBook { get; init; }

    public string SourceName => Path.GetFileName(Action.Track.Path);
    public string? Note => Action.Note;

    /// <summary>
    /// Which album or book this row belongs under.
    ///
    /// Taken from where the file is going rather than from its tags. The whole
    /// point of the plan is that the tags are often wrong, so grouping by them
    /// would scatter an album across four headings for the four spellings of
    /// its artist - which is exactly the mess being cleaned up. The
    /// destination folder is what the plan has decided, and files that will
    /// end up together belong together on screen.
    /// </summary>
    public string Group
    {
        get
        {
            if (Action.Destination is { } target
                && Path.GetDirectoryName(target) is { Length: > 0 } folder)
            {
                // Two levels, as the pattern lays them out: artist and album.
                var parts = folder.Split(Path.DirectorySeparatorChar);

                return parts.Length >= 2
                    ? $"{parts[^2]}  •  {parts[^1]}"
                    : parts[^1];
            }

            // Held and redundant files have no destination, so they sit under
            // where they are now - which is where somebody would go to look.
            var here = Path.GetDirectoryName(Action.Track.Path) ?? "";
            var name = Path.GetFileName(here);

            return name.Length > 0 ? $"{name}   (staying put)" : "(nowhere yet)";
        }
    }

    public string KindLabel => Action.Kind switch
    {
        MusicActionKind.Move => "Move",
        MusicActionKind.Copy => "Copy",
        MusicActionKind.Redundant => "Set aside",
        MusicActionKind.Held => "Needs you",
        _ => "Stays"
    };

    /// <summary>
    /// Where it lands, shortened to the part that differs. A full path per row
    /// is unreadable at seventeen thousand rows and the root is the same on all
    /// of them.
    /// </summary>
    public string TargetLabel
    {
        get
        {
            if (Action.Destination is not { } target) return "";

            var parts = target.Split(Path.DirectorySeparatorChar);
            return parts.Length <= 3 ? target : string.Join(Path.DirectorySeparatorChar, parts[^3..]);
        }
    }
}

/// <summary>
/// The music half of the audio tab.
///
/// Laid out to match the remux window deliberately - same cards, same warning
/// bar, same grid of proposed changes - because the two do the same kind of
/// thing and a tool that looks different in each half reads as two tools.
///
/// Nothing here touches a file until Apply, and Apply goes through the same
/// conflict dialog and the same history the film side uses, so a run undoes
/// from the existing History window with no special case for music.
/// </summary>
public partial class MusicView : UserControl
{
    private PyreMediaSettings _settings = new();
    private RenameHistory? _history;

    private readonly ObservableCollection<string> _folders = [];
    private readonly List<MusicRow> _rows = [];

    private MusicPlan? _plan;
    private List<Finding> _findings = [];

    /// <summary>
    /// Every file the last scan read. Kept because the sweep needs it: whether
    /// the .nfo files are safe to remove is a question about what was gathered
    /// out of them, not about what is on disk now.
    /// </summary>
    private List<TrackTags> _scanned = [];

    /// <summary>Books found by the last scan, kept out of the album grouping.</summary>
    private List<Audiobook> _books = [];

    /// <summary>Which half of the audio tab is on show. Set by the host.</summary>
    private string _showing = "all";
    private CancellationTokenSource? _cancel;

    public MusicView()
    {
        InitializeComponent();
        ListFolders.ItemsSource = _folders;
    }

    /// <summary>Called by the host once settings and history exist.</summary>
    public void Attach(PyreMediaSettings settings, RenameHistory history)
    {
        _settings = settings;
        _history = history;

        // Point the music side at the same ffmpeg the rest of the program
        // uses. Every class here said "ffmpeg" and left it to the PATH, so a
        // configured build was honoured for remuxing and ignored for this.
        MusicTools.Use(settings);

        _folders.Clear();
        foreach (var f in settings.MusicFolders) _folders.Add(f);

        TxtPattern.Text = string.IsNullOrWhiteSpace(settings.MusicFileFormat)
            ? NamingFormat.Default
            : settings.MusicFileFormat;

        ChkFingerprint.IsChecked = settings.UseFingerprinting;
        ChkWriteTags.IsChecked = settings.WriteMusicTags;
        ChkReplayGain.IsChecked = settings.WriteReplayGain;

        RefreshFolders();
        UpdatePreview();
    }

    /// <summary>
    /// Which kind of audio to list: "all", "music" or "books". Called by the
    /// shell, because the buttons live beside the video tab's filter and the
    /// two are meant to look and behave alike.
    /// </summary>
    public void Show(string what)
    {
        _showing = what;
        ApplyFilter();
    }

    /// <summary>
    /// Write the settings out.
    ///
    /// The shell saves at its own moments - a scan, a rename - and none of them
    /// are reached by anything in this pane, so a folder added here and the app
    /// closed was simply lost.
    /// </summary>
    private void Persist()
    {
        _settings.MusicFolders = [.. _folders];
        _settings.MusicFileFormat = TxtPattern.Text;
        _settings.UseFingerprinting = ChkFingerprint.IsChecked == true;
        _settings.WriteMusicTags = ChkWriteTags.IsChecked == true;
        _settings.WriteReplayGain = ChkReplayGain.IsChecked == true;

        try { _settings.Save(); }
        catch (Exception ex) { Log($"Could not save settings: {ex.Message}"); }
    }

    private void RefreshFolders() =>
        TxtNoFolders.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Where your music lives" };
        if (dialog.ShowDialog() != true) return;

        if (_folders.Contains(dialog.FolderName)) return;

        _folders.Add(dialog.FolderName);
        RefreshFolders();
        Persist();
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string folder }) return;

        _folders.Remove(folder);
        RefreshFolders();
        Persist();
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        TxtPattern.Text = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "initial" => NamingFormat.ByInitial,
            "artisttitle" => NamingFormat.ArtistTitle,
            _ => NamingFormat.Default
        };
    }

    private void OnPatternChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    /// <summary>
    /// Show what the pattern does as it is typed. A pattern is far easier to
    /// judge from one worked example than from reading it.
    /// </summary>
    private void UpdatePreview()
    {
        if (TxtPreview is null) return;

        var format = new NamingFormat(TxtPattern.Text);
        var problems = format.Problems();

        TxtPatternProblem.Text = string.Join("; ", problems);
        TxtPatternProblem.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        try { TxtPreview.Text = problems.Count == 0 ? format.Example() : ""; }
        catch { TxtPreview.Text = ""; }

    }

    private void Log(string line) => Dispatcher.Invoke(() =>
    {
        TxtLog.AppendText(line + "\r\n");
        TxtLog.ScrollToEnd();
    });

    private void Status(string text) => Dispatcher.Invoke(() => TxtStatus.Text = text);

    /// <summary>
    /// Start measuring a phase, or clear the bar when there is nothing to
    /// measure. Returns something to report progress to.
    ///
    /// Only the phases that take real time get one. Reading tags off seventeen
    /// thousand files is six seconds; probing durations and fingerprinting are
    /// minutes, and those are what somebody is actually waiting through.
    /// </summary>
    private IProgress<int> Measure(string what, int total)
    {
        var estimate = new Estimate(total);

        Dispatcher.Invoke(() =>
        {
            TxtStatus.Text = what;
            Bar.Value = 0;
            TxtEta.Text = total > 0 ? $"0 of {total:N0}" : "";
            ProgressRow.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        });

        var lastPainted = DateTime.MinValue;

        return new Progress<int>(done =>
        {
            // Nothing about drawing a progress bar is worth losing a scan
            // over, and an exception on a thread pool thread cannot be caught
            // anywhere else - it takes the process with it, which is exactly
            // what happened here.
            try { Paint(done); } catch { }
        });

        void Paint(int done)
        {
            estimate.Report(done);

            // Repainting per file would spend more time on the bar than on the
            // work. Four times a second is smooth to a person and free.
            var now = DateTime.UtcNow;
            if (now - lastPainted < TimeSpan.FromMilliseconds(250) && done < total) return;

            lastPainted = now;

            var fraction = estimate.Fraction;
            var text = estimate.Describe();

            // Marshalled explicitly, because Progress<T> cannot do it here.
            // It captures the synchronisation context where it is constructed,
            // and this is constructed inside the background scan - a thread
            // pool thread, which has none. So the handler ran on the pool and
            // wrote straight to a control owned by the UI thread, and WPF
            // killed the process for it.
            //
            // BeginInvoke rather than Invoke: the scan must not wait on the UI
            // thread to paint, and a queue of stale progress updates behind a
            // busy window is worse than dropping them.
            Dispatcher.BeginInvoke(() =>
            {
                Bar.Value = fraction;
                TxtEta.Text = text;
            });
        }
    }

    private void ClearProgress() => Dispatcher.Invoke(() =>
    {
        ProgressRow.Visibility = Visibility.Collapsed;
        TxtEta.Text = "";
        Bar.Value = 0;
    });

    private void OnCancel(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0)
        {
            System.Windows.MessageBox.Show("Add the folder your music lives in first.",
                "Nothing to scan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Persist();

        // Said once, before a scan that would otherwise quietly do less. A
        // library scanned without ffmpeg still produces a plan and still moves
        // files - it just judges duplicates on titles and durations, which was
        // right 95.2% of the time on a measured library rather than always.
        var missing = MusicTools.Check(_settings).Where(c => !c.Present).ToList();

        if (missing.Any(m => m.Name != "AcoustID key"))
        {
            var lost = string.Join("\n\n", missing
                .Where(m => m.Name != "AcoustID key")
                .Select(m => $"{m.Name} - not found\n{m.Lost}"));

            if (System.Windows.MessageBox.Show(
                    lost + "\n\nScan anyway?",
                    "Some tools are missing", MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        }

        _cancel = new CancellationTokenSource();
        Busy(true);

        try
        {
            var roots = _folders.ToList();
            var pattern = TxtPattern.Text;
            var fingerprint = ChkFingerprint.IsChecked == true;
            var token = _cancel.Token;

            var (plan, findings, books, scanned) = await Task.Run(
                () => Survey(roots, pattern, fingerprint, token), token);

            _plan = plan;
            _findings = findings;
            _scanned = scanned;
            _books = books;

            _rows.Clear();
            _rows.AddRange(plan.Actions
                .Where(a => a.Kind is not MusicActionKind.Stay)
                .Select(a => new MusicRow { Action = a }));

            // Books are filed by author and chapter, so they get their own
            // pattern and their own rows rather than being squeezed through the
            // album one.
            var bookFormat = new NamingFormat(
                string.IsNullOrWhiteSpace(_settings.AudiobookFormat)
                    ? Audiobooks.DefaultFormat
                    : _settings.AudiobookFormat);

            foreach (var book in _books)
                foreach (var file in book.Files)
                {
                    var target = Path.Combine(_folders[0], bookFormat.Path(file, book));

                    // Already where it belongs, so it is not part of the plan.
                    // The music rows leave those out and the book rows did not,
                    // which meant one list answered two different questions
                    // depending on which filter was showing.
                    if (string.Equals(target, file.Path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    _rows.Add(new MusicRow
                    {
                        IsBook = true,
                        Action = new MusicAction(file, MusicActionKind.Move,
                            target,
                            $"{book.Title} - {book.Why}")
                    });
                }

            ApplyFilter();

            var held = plan.Actions.Count(a => a.Kind is MusicActionKind.Held);

            Status($"{plan.Actions.Count:N0} files - {plan.Moves.Count():N0} to move, "
                 + $"{plan.Redundant.Count():N0} redundant, {held} need you"
                 + (_books.Count > 0
                     ? $"; {_books.Count} audiobook(s), {_books.Sum(b => b.Files.Count)} chapters"
                     : ""));

            BtnApply.IsEnabled = _rows.Count > 0;
            BtnHealth.IsEnabled = _findings.Count > 0;
            BtnSweep.IsEnabled = true;
            BtnIdentify.IsEnabled = _findings.Any(f =>
                f.Ailment is Ailment.Placeholder or Ailment.Missing
                && f.Field is "Title" or "Artist");
            BtnMove.IsEnabled = plan.Actions.Any(a => a.Kind is MusicActionKind.Stay);
        }
        catch (OperationCanceledException) { Status("Stopped."); }
        catch (Exception ex)
        {
            Status("Scan failed.");
            Log($"ERROR {ex.Message}");
        }
        finally { Busy(false); ClearProgress(); }
    }

    /// <summary>
    /// The whole read-only half of the job, off the UI thread.
    ///
    /// Fingerprints are taken only for files whose track numbers collide -
    /// about one in twenty - because a fingerprint costs four tenths of a
    /// second and taking one for every file would turn a six second scan into
    /// an hour to answer a question about a few hundred of them.
    /// </summary>
    private (MusicPlan Plan, List<Finding> Findings, List<Audiobook> Books, List<TrackTags> Scanned) Survey(
        List<string> roots, string pattern, bool fingerprint, CancellationToken token)
    {
        // Counted before it is read. Walking the directories costs a fraction
        // of opening every file, and without it this phase - the longest one
        // somebody actually watches - cannot say how far through it is, because
        // a lazy enumerable does not know its own length until it ends.
        var found = new List<string>();

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();

            var folders = 0;

            Status($"Looking through {root}...");

            found.AddRange(MusicFileReader.Files(root, _ =>
            {
                if (++folders % 25 == 0) Status($"Looking through {root} - {folders:N0} folders...");
            }));
        }

        var reading = Measure($"Reading tags from {found.Count:N0} files...", found.Count);
        var tracks = new List<TrackTags>(found.Count);

        foreach (var path in found)
        {
            token.ThrowIfCancellationRequested();

            tracks.Add(MusicFileReader.Read(path));
            reading.Report(tracks.Count);
        }

        ClearProgress();

        // Reads an .nfo per folder, so it is measured by folder rather than by
        // file - a hundred folders is a hundred files opened, not seventeen
        // thousand.
        // Same comparer Gather uses internally, so the grouping here and the
        // grouping there cannot disagree about what one folder is.
        var byFolder = tracks
            .GroupBy(t => Path.GetDirectoryName(t.Path) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
        var evidence = Measure($"Reading what else the folders say...", byFolder.Count);
        var seen = 0;

        foreach (var folder in byFolder)
        {
            token.ThrowIfCancellationRequested();
            MusicEvidence.Gather(folder);
            evidence.Report(++seen);
        }

        ClearProgress();

        // Books are not albums and every rule below would mangle them.
        var books = Audiobooks.Find(tracks).Where(b => b.Confidence == SpokenWord.Yes).ToList();
        var spoken = books.SelectMany(b => b.Files).ToHashSet();

        foreach (var b in books) Log($"Audiobook: {b} - {b.Why}");

        var music = tracks.Where(t => !spoken.Contains(t)).ToList();
        var albums = AlbumGrouper.Group(music);

        // How many files the duration probe will actually touch, so the bar
        // measures the work rather than the library.
        var contestedCount = albums
            .SelectMany(a => a.Tracks.Where(t => t.TrackNumber is > 0)
                .GroupBy(t => (t.DiscNumber ?? 1, t.TrackNumber!.Value))
                .Where(g => g.Count() > 1)
                .SelectMany(g => g))
            .Count(t => t.Seconds is null or <= 0);

        MusicDurations.FillContestedAsync(albums,
            progress: Measure($"{albums.Count:N0} albums. Measuring the ones that disagree...",
                              contestedCount),
            cancel: token).GetAwaiter().GetResult();

        ClearProgress();

        FingerprintSet? audio = null;

        if (fingerprint && Fingerprint.Available())
        {
            var contested = albums
                .SelectMany(a => a.Tracks.Where(t => t.TrackNumber is > 0)
                    .GroupBy(t => (t.DiscNumber ?? 1, t.TrackNumber!.Value))
                    .Where(g => g.Count() > 1)
                    .SelectMany(g => g))
                .Select(t => t.Path)
                .Distinct()
                .ToList();

            if (contested.Count > 0)
            {
                audio = FingerprintSet.TakeAsync(contested,
                    progress: Measure("Comparing the audio of the files that disagree...",
                                      contested.Count),
                    cancel: token).GetAwaiter().GetResult();

                ClearProgress();
            }
        }

        // Which albums are missing a track the library already holds elsewhere.
        // Only the ones a rule settles become copies; the rest are questions and
        // stay questions.
        Status("Looking for gaps other albums could fill...");

        // Two passes, because judging a candidate needs its length and nothing
        // has measured it. FillContestedAsync only probes files whose track
        // numbers collide, and a gap candidate by definition collides with
        // nothing - so a first pass finds the handful of files worth measuring,
        // and the second pass can actually decide. Without this every gap comes
        // back "nothing here knows how long either should be" and the feature
        // never fires at all.
        var rough = MusicGapFill.Find(albums);

        var candidates = rough
            .SelectMany(g => g.Candidates)
            .SelectMany(c => c.Copies)
            .Where(t => t.Seconds is null or <= 0)
            .Distinct()
            .ToList();

        if (candidates.Count > 0)
        {
            MusicDurations.FillAsync(candidates,
                progress: Measure("Measuring files that might fill a gap...", candidates.Count),
                cancel: token).GetAwaiter().GetResult();

            ClearProgress();
        }

        var gaps = MusicGapFill.Find(albums, audio);

        var fillable = gaps.Where(g => g.Obvious is not null).ToList();
        var asking = gaps.Count(g => g.NeedsAsking);

        if (gaps.Count > 0)
            Log($"{gaps.Count} gaps: {fillable.Count} fillable, {asking} holding two "
              + "different recordings of the song and left alone.");

        Status("Working out where everything goes...");

        var format = new NamingFormat(pattern);
        var plan = MusicPlanner.Plan(albums, roots[0], audio: audio, naming: format,
            gaps: fillable);

        var findings = music.SelectMany(MusicHealth.Examine).ToList();
        Log($"{findings.Count} tag problems, {findings.Count(f => f.Fixable)} fixable without asking");

        return (plan, findings, books, tracks);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (GridPlan is null) return;

        // Two filters, not one: which kind of audio, then what is happening to
        // it. They compose - "audiobooks that need you" is a real question.
        var kind = _rows.Where(r => _showing switch
        {
            "music" => !r.IsBook,
            "books" => r.IsBook,
            _ => true
        });

        var shown = kind.Where(r =>
            FilterMoves.IsChecked == true ? r.Action.Kind is MusicActionKind.Move or MusicActionKind.Copy
          : FilterDupes.IsChecked == true ? r.Action.Kind is MusicActionKind.Redundant
          : FilterHeld.IsChecked == true ? r.Action.Kind is MusicActionKind.Held
          : true).ToList();

        // Grouped by album. A view is rebuilt per filter rather than kept and
        // refiltered, because the grouping has to be rebuilt anyway and a
        // stale view is how a grid ends up showing rows the filter excluded.
        var view = new System.Windows.Data.CollectionViewSource { Source = shown };
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(MusicRow.Group)));

        GridPlan.ItemsSource = view.View;

        TxtStatus.Text = shown.Count == 0
            ? "Nothing to show with this filter."
            : $"{shown.Count:N0} files in "
              + $"{shown.Select(r => r.Group).Distinct().Count():N0} albums";
    }

    /// <summary>
    /// Offer to take everything that is not music out of the library.
    ///
    /// Shared by the Clean up button and by Apply, so the two cannot drift -
    /// and so Apply can finish the job. The guard is the reason this was ever
    /// a separate step: the .nfo files hold the only copy of some of the
    /// library's metadata, and deleting them before it is written into the
    /// audio loses it silently. Running it from Apply, after the tags have been
    /// written, means nobody has to know that.
    /// </summary>
    /// <param name="ask">
    /// False when the user pressed Clean up and has already said what they
    /// want; true when this is Apply offering to carry on, where saying no is
    /// a reasonable answer.
    /// </param>
    private async Task<bool> OfferToCleanUp(string root, CancellationToken token, bool ask = true)
    {
        if (_history is null) return false;

        if (MusicSweep.Blocked(_scanned) is { } blocked)
        {
            // Only worth saying when somebody asked for the sweep. After an
            // Apply that did not write tags it is the expected state, not a
            // problem, and a warning would read as one.
            if (!ask)
                System.Windows.MessageBox.Show(
                    blocked + "\n\nTick \"Fix tags inside the files\" and apply first, "
                    + "and this will unblock itself.",
                    "Not yet", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                Log($"Clean up skipped: {blocked}");

            return false;
        }

        var survey = await Task.Run(() => MusicSweep.Survey(root, MusicSweep.FullRefresh), token);

        if (survey.Items.Count == 0)
        {
            if (!ask)
                System.Windows.MessageBox.Show("Nothing to clean up - the library is all music.",
                    "Clean up", MessageBoxButton.OK, MessageBoxImage.Information);

            return false;
        }

        var breakdown = string.Join("\n", survey.ByCategory
            .OrderByDescending(g => g.Sum(i => i.Bytes))
            .Select(g => $"  {g.Key,-18} {g.Count(),7:N0} files  {g.Sum(i => i.Bytes) / 1024 / 1024,7:N0} MB"));

        var confirm = System.Windows.MessageBox.Show(
            (ask ? "The music is filed. What is left is everything that is not music:\n\n" : "")
            + $"{survey.Items.Count:N0} files, {survey.Bytes / 1024 / 1024:N0} MB.\n\n"
            + breakdown
            + "\n\nAll of it moves to a folder beside your library rather than being "
            + "deleted, and undoes from History.",
            "Clean up", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return false;

        var executor = new MusicExecutor(_history);

        var result = await Task.Run(
            () => executor.Sweep(survey, root, new Progress<string>(Log), token), token);

        Status($"Set aside {result.Quarantined:N0} files, skipped {result.Skipped:N0}, "
             + $"failed {result.Failed:N0}. Batch {result.BatchId}.");

        foreach (var error in result.Errors.Take(50)) Log($"ERROR {error}");
        return true;
    }

    /// <summary>
    /// Offer to remove folders left holding nothing.
    ///
    /// Offered rather than done. Moving music out of a library leaves the
    /// folders it came from behind, and after a sweep takes the artwork with
    /// it there are thousands of them - but whether an empty folder is rubbish
    /// or somewhere the user keeps things is not this program's call to make
    /// silently. "Reported, never removed" was the rule and it does not scale
    /// past a thousand; asking does.
    /// </summary>
    private async Task OfferToRemoveEmptyFolders(string root, CancellationToken token)
    {
        var empty = await Task.Run(
            () => MusicExecutor.FindEmptyFolders(root, token), token);

        if (empty.Count == 0) return;

        var sample = string.Join("\n", empty
            .OrderBy(f => f.Length)
            .Take(6)
            .Select(f => "  " + (f.Length > root.Length ? f[(root.Length + 1)..] : f)));

        var confirm = System.Windows.MessageBox.Show(
            $"{empty.Count:N0} folders are left holding nothing at all.\n\n{sample}"
            + (empty.Count > 6 ? $"\n  ... and {empty.Count - 6:N0} more" : "")
            + "\n\nRemove them?\n\nThis one is not undoable from History - an empty folder "
            + "has no contents to put back, so there would be nothing to restore.",
            "Empty folders", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var removed = await Task.Run(
            () => MusicExecutor.RemoveEmptyFolders(empty, new Progress<string>(Log), token), token);

        Log($"Removed {removed:N0} empty folders.");
        Status($"Removed {removed:N0} empty folders.");
    }

    /// <summary>
    /// Work out what a file is from the audio, for the ones whose tags say
    /// nothing useful.
    ///
    /// Deliberately not part of the scan. It needs a key, a network and about
    /// a second and a half per file, and it is the only thing here that asks
    /// anybody else about the user's library - so it runs on a handful of
    /// files somebody chose, never on seventeen thousand as a side effect.
    ///
    /// The handful is picked by what the local rules already gave up on: a
    /// placeholder like "Unknown Artist", or a missing title or artist. Those
    /// are exactly the files that no amount of comparing tags to each other
    /// can help, which is what makes them worth a network call.
    /// </summary>
    private async void OnIdentify(object sender, RoutedEventArgs e)
    {
        var nameless = _findings
            .Where(f => f.Ailment is Ailment.Placeholder or Ailment.Missing)
            .Where(f => f.Field is "Title" or "Artist")
            .Select(f => f.Track)
            .Distinct()
            .ToList();

        if (nameless.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "Every file here already has a title and an artist that say something. "
                + "This is for the ones that do not, so there is nothing for it to do.",
                "Nothing to identify", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.AcoustIdApiKey))
        {
            System.Windows.MessageBox.Show(
                $"{nameless.Count} files have no usable title or artist.\n\n"
                + "Identifying them by ear needs an AcoustID key, which is free. Settings has "
                + "the box and a link to where it comes from.\n\n"
                + "What would leave this machine is an acoustic fingerprint and a duration - "
                + "not the audio, not the filename, not the path.",
                "No AcoustID key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Ask AcoustID and MusicBrainz about {nameless.Count} files?\n\n"
            + "A fingerprint and a duration leave this machine for each one - not the audio, "
            + "not the filename, not the path.\n\n"
            + "Nothing is written; you will see what it found and decide afterwards.",
            "Identify by ear", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        _cancel = new CancellationTokenSource();
        Busy(true);

        try
        {
            var token = _cancel.Token;
            var key = _settings.AcoustIdApiKey;
            var identifier = new Identify();
            var found = new List<Identification>();
            var looked = 0;

            foreach (var track in nameless)
            {
                token.ThrowIfCancellationRequested();

                if (await identifier.OfAsync(track, key, token) is { } id) found.Add(id);

                Status($"Looked at {++looked:N0} of {nameless.Count:N0}, "
                     + $"recognised {found.Count:N0}...");
            }

            foreach (var i in found)
            {
                Log($"{Path.GetFileName(i.Track.Path)} -> {i.Recording}"
                  + (i.Release is null ? "" : $"  [{i.Release.Title}]") + $"  {i.Score:P0}");

                foreach (var d in i.Disagrees) Log($"      {d}");
            }

            var report = found.Count == 0
                ? "Nothing was recognised. That is common for anything homemade, live, or off "
                  + "a small label, and it is not evidence that these files are wrong."
                : string.Join("\n", found.Take(20).Select(i =>
                    $"  {i.Recording}" + (i.Release is null ? "" : $"  [{i.Release.Title}]")));

            Status($"Recognised {found.Count:N0} of {nameless.Count:N0}.");

            System.Windows.MessageBox.Show(
                $"{found.Count} of {nameless.Count} recognised.\n\n{report}\n\n"
                + "Nothing has been written. The log has the full list, including any file "
                + "where what the audio says disagrees with what the tags claim.",
                "Identify by ear", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { Status("Stopped."); }
        catch (Exception ex)
        {
            Status("Identifying failed.");
            Log($"ERROR {ex.Message}");
        }
        finally { Busy(false); }
    }

    /// <summary>
    /// Everything in the library that is not music, offered for removal.
    ///
    /// Guarded twice over. The scraped .nfo files here are the only place a
    /// good deal of the library's metadata exists, so the sweep will not run
    /// while any of it is still only in memory; and nothing is deleted, only
    /// set aside, because a classifier reading file extensions cannot tell a
    /// scan of the sleeve from the six thousand cover thumbnails around it.
    /// </summary>
    private async void OnSweep(object sender, RoutedEventArgs e)
    {
        if (_history is null || _folders.Count == 0) return;

        _cancel = new CancellationTokenSource();
        Busy(true);

        try
        {
            var root = _folders[0];

            // ask: false - the button is somebody saying what they want, so a
            // guard that stops it has to explain itself rather than go to a log.
            if (await OfferToCleanUp(root, _cancel.Token, ask: false))
                await OfferToRemoveEmptyFolders(root, _cancel.Token);
        }
        catch (OperationCanceledException) { Status("Stopped."); }
        catch (Exception ex)
        {
            Status("Clean up failed.");
            Log($"ERROR {ex.Message}");
        }
        finally { Busy(false); ClearProgress(); }
    }


    /// <summary>
    /// Move what is finished out of the staging folder and into the library.
    ///
    /// Finished means already correctly named where it sits, which is the same
    /// flag that stops the film side offering to rename a file twice. Renaming
    /// happens in staging; this is the separate step that relocates the result,
    /// and never doing it is a perfectly good way to use the program.
    /// </summary>
    private async void OnMoveCompleted(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _history is null || _folders.Count == 0) return;

        var books = _showing == "books";
        var kind = books ? LibraryKind.Audiobook : LibraryKind.Music;
        var destination = _settings.DestinationFor(kind);

        // Blank means not chosen, not "the same place". Moving finished files
        // somewhere nobody picked is worse than asking.
        if (string.IsNullOrWhiteSpace(destination))
        {
            var pick = System.Windows.MessageBox.Show(
                $"No {(books ? "audiobook" : "music")} library folder has been chosen yet.\n\n"
                + "Choose one now?",
                "Where should finished files go?", MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (pick != MessageBoxResult.OK) return;

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = books ? "Where your audiobooks live" : "Where your music library lives"
            };

            if (dialog.ShowDialog() != true) return;

            destination = dialog.FolderName;

            if (books) _settings.AudiobookDestination = destination;
            else _settings.MusicDestination = destination;

            Persist();
        }

        var staging = _folders[0];

        if (string.Equals(Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(
                "The library folder is the same as the folder being scanned, so there is "
                + "nowhere to move anything to.",
                "Already there", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Books carry their own rows; the plan only covers music.
        var finished = books
            ? _rows.Where(r => r.IsBook && r.Action.Kind is MusicActionKind.Stay)
                   .Select(r => r.Action).ToList()
            : _plan.Actions.Where(a => a.Kind is MusicActionKind.Stay).ToList();

        if (finished.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "Nothing is finished yet. Apply the plan first - a file counts as finished "
                + "once it is already named correctly where it sits.",
                "Nothing to move", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Move {finished.Count:N0} finished files to:\n\n{destination}\n\n"
            + "Their folder layout is kept exactly as it is now, and this undoes from History.",
            "Move completed", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        _cancel = new CancellationTokenSource();
        Busy(true);

        try
        {
            var token = _cancel.Token;
            var executor = new MusicExecutor(_history);

            var result = await Task.Run(() => executor.MoveCompleted(
                finished, staging, destination, new Progress<string>(Log), token), token);

            Status($"Moved {result.Moved:N0} to the library, skipped {result.Skipped:N0}, "
                 + $"failed {result.Failed:N0}. Batch {result.BatchId}.");

            foreach (var error in result.Errors.Take(50)) Log($"ERROR {error}");
        }
        catch (OperationCanceledException) { Status("Stopped."); }
        catch (Exception ex)
        {
            Status("Move failed.");
            Log($"ERROR {ex.Message}");
        }
        finally { Busy(false); }
    }

    /// <summary>
    /// What is wrong with the tags, in two kinds.
    ///
    /// A file can be wrong on its own terms - a placeholder, a stray URL, a
    /// track number repeated into the title - and MusicHealth finds those. It
    /// can also be wrong only in company: nothing is the matter with
    /// "...(Full-Cast Edition) (Unabridged)" until you see the six siblings
    /// that do not say it, and this one files away from all of them. Those
    /// need the whole set to see, which is why they are counted apart.
    /// </summary>
    private void OnShowHealth(object sender, RoutedEventArgs e)
    {
        var fixable = _findings.Count(f => f.Fixable);
        var files = _findings.Select(f => f.Track).Distinct().Count();

        var perFile = string.Join("\n", _findings
            .GroupBy(f => f.Ailment)
            .OrderByDescending(g => g.Count())
            .Select(g => $"  {g.Key,-16} {g.Select(f => f.Track).Distinct().Count(),6} files"
                       + $"  {g.Count(f => f.Fixable)} fixable"));

        // Sets that ought to agree: the tracks bound for one folder, and the
        // books sharing one.
        var sets = new List<IReadOnlyList<TrackTags>>();

        if (_plan is not null)
            sets.AddRange(_plan.Actions
                .Where(a => a.Destination is not null)
                .GroupBy(a => Path.GetDirectoryName(a.Destination!) ?? "")
                .Where(g => g.Count() > 1)
                .Select(g => (IReadOnlyList<TrackTags>)g.Select(a => a.Track).ToList()));

        sets.AddRange(_books
            .GroupBy(b => Path.GetDirectoryName(b.Files[0].Path)!)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<TrackTags>)g.SelectMany(b => b.Files).ToList()));

        var disagreements = MusicConsistency.Examine(sets);

        var perSet = disagreements.Count == 0
            ? "  none"
            : string.Join("\n", disagreements
                .GroupBy(d => d.Kind)
                .OrderByDescending(g => g.Count())
                .Select(g => $"  {g.Key,-20} {g.Count(),5}"));

        System.Windows.MessageBox.Show(
            $"WRONG ON ITS OWN TERMS - {_findings.Count} findings across {files} files\n\n"
            + perFile
            + $"\n\n{fixable} can be put right from the file itself. The rest name something "
            + "no cleverness recovers back: \"Unknown Artist\" is certainly wrong and nothing "
            + "here knows who it is.\n\n"
            + $"WRONG ONLY IN COMPANY - {disagreements.Count} across {sets.Count} sets\n\n"
            + perSet
            + "\n\nThese are files that disagree with their neighbours. Proposals rather "
            + "than corrections: the majority is not always right, and following it here would "
            + "delete the apostrophe from a band that has one.\n\n"
            + "Tick \"Fix tags inside the files\" to write the first kind.\n\n"
            + (disagreements.Count > 0
                ? "Press OK to look through the second kind one row at a time."
                : ""),
            "Tag problems", MessageBoxButton.OK, MessageBoxImage.Information);

        if (disagreements.Count == 0 || _history is null) return;

        // A list, not a summary, and every row unticked. This is the one kind
        // of finding here that cannot be trusted wholesale.
        var review = new ConsistencyWindow(disagreements) { Owner = Window.GetWindow(this) };
        if (review.ShowDialog() != true || review.Approved.Count == 0) return;

        WriteApproved(review.Approved);
    }

    /// <summary>
    /// Write the disagreements a person actually ticked.
    ///
    /// One edit per file rather than per finding, because two findings can
    /// touch the same file - an album whose year and genre both disagree - and
    /// rewriting it twice would leave two undo entries for one intention.
    /// </summary>
    private async void WriteApproved(IReadOnlyList<Inconsistency> approved)
    {
        var writer = new MusicTagWriter(_history);
        var batch = Guid.NewGuid().ToString("N")[..8];

        var edits = MusicConsistency.Edits(approved);

        Busy(true);

        try
        {
            var written = 0;
            var failed = 0;

            // Constructed here, on the UI thread, before the work is handed to
            // the pool. Measure marshals its own updates now either way, but
            // building it outside the background work is the habit that stops
            // this class of mistake.
            var progress = Measure($"Writing tags into {edits.Count:N0} files...", edits.Count);
            var done = 0;

            await Task.Run(() =>
            {
                foreach (var (track, edit) in edits)
                {
                    progress.Report(++done);

                    if (!edit.Any) continue;

                    var result = writer.Write(track, edit, batch);

                    if (result.Written) written++;
                    else { failed++; Log($"Tags: {result.Error}"); }
                }
            });

            ClearProgress();

            Status($"Wrote {written:N0} files, {failed:N0} refused. Batch {batch} - "
                 + "undo it from History.");
        }
        catch (Exception ex)
        {
            Status("Writing tags failed.");
            Log($"ERROR {ex.Message}");
        }
        finally { Busy(false); }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _history is null) return;

        var doing = _plan.Actions
            .Where(a => a.Kind is MusicActionKind.Move or MusicActionKind.Copy or MusicActionKind.Redundant)
            .ToList();

        // A gap-filling copy has to be retagged to belong to the album it is
        // filling, and that needs ffmpeg. Better to say so now than to make
        // every copy and delete it again.
        var copies = doing.Count(a => a.Kind is MusicActionKind.Copy);

        if (copies > 0 && !Fingerprint.Available())
        {
            System.Windows.MessageBox.Show(
                $"{copies} of these are copies filling a gap in another album, and each one "
                + "has to have its album and track number rewritten to belong there. "
                + "That needs ffmpeg, which is not on the path.\n\n"
                + "Install it, or the copies will be skipped.",
                "ffmpeg needed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var executor = new MusicExecutor(_history, new MusicTagWriter(_history));
        var conflicts = executor.FindConflicts(doing);

        // The same dialog the film side uses. A collision is a collision.
        if (conflicts.Count > 0 && new ConflictWindow(conflicts) { Owner = Window.GetWindow(this) }
                .ShowDialog() != true)
            return;

        var redundant = doing.Count(a => a.Kind is MusicActionKind.Redundant);

        var confirm = System.Windows.MessageBox.Show(
            $"{doing.Count(a => a.Kind is MusicActionKind.Move):N0} files will move, "
            + $"{doing.Count(a => a.Kind is MusicActionKind.Copy):N0} will be copied, and "
            + $"{redundant:N0} redundant ones will be set aside in a folder beside your library "
            + "rather than deleted.\n\nAll of it can be undone from History.",
            "Apply", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        _cancel = new CancellationTokenSource();
        Busy(true);

        try
        {
            var root = _folders[0];
            var token = _cancel.Token;
            var writeTags = ChkWriteTags.IsChecked == true;
            var replayGain = ChkReplayGain.IsChecked == true;

            var result = await Task.Run(() =>
            {
                var r = executor.Execute(doing, root, conflicts,
                    new Progress<string>(Log), token);

                if (writeTags) WriteTags(r.BatchId, token);
                if (replayGain) WriteGains(r.BatchId, token);
                return r;
            }, token);

            Status($"Moved {result.Moved:N0}, copied {result.Copied:N0}, "
                 + $"set aside {result.Quarantined:N0}, skipped {result.Skipped:N0}, "
                 + $"failed {result.Failed:N0}. Batch {result.BatchId}.");

            foreach (var error in result.Errors.Take(50)) Log($"ERROR {error}");

            BtnApply.IsEnabled = false;

            // Apply finishes the job rather than doing a third of it and
            // leaving two more buttons and an ordering rule to remember.
            //
            // The order matters and is the reason this was ever separate: the
            // sweep deletes .nfo files that hold the only copy of some of the
            // library's metadata, so the tags have to be written first. Doing
            // it here means nobody has to know that.
            await OfferToCleanUp(root, token);

            // Last, because clearing the artwork out of an album empties the
            // folder it was in - running this before the sweep would find a
            // fraction of what running it after does.
            await OfferToRemoveEmptyFolders(root, token);
        }
        catch (OperationCanceledException) { Status("Stopped."); }
        catch (Exception ex)
        {
            Status("Apply failed.");
            Log($"ERROR {ex.Message}");
        }
        finally { Busy(false); }
    }

    /// <summary>
    /// Write the fixes that are derivable from the file itself. Only ever the
    /// reversible half - a placeholder is not guessed at here.
    /// </summary>
    private void WriteTags(string batchId, CancellationToken token)
    {
        var writer = new MusicTagWriter(_history);
        var written = 0;
        var failed = 0;

        var groups = _findings.Where(f => f.Fixable).GroupBy(f => f.Track).ToList();
        var progress = Measure($"Putting right {groups.Count:N0} files...", groups.Count);
        var done = 0;

        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(++done);

            var result = writer.Write(group.Key, MusicTagWriter.Repairs(group), batchId, token);

            if (result.Written) written++;
            else if (result.Error is not null) { failed++; Log($"Tags: {result.Error}"); }
        }

        ClearProgress();
        Log($"Tags written into {written} files, {failed} refused.");
    }

    /// <summary>
    /// Measure every track and write the loudness into its tags.
    ///
    /// Album by album, because album gain is the whole reason to bother: it is
    /// what keeps a deliberately quiet track quiet relative to the rest of its
    /// record instead of levelling every song to the same volume. Measuring
    /// tracks in isolation and stopping there would throw that away.
    ///
    /// This decodes every file once, so it is by far the slowest thing here.
    /// </summary>
    private void WriteGains(string batchId, CancellationToken token)
    {
        if (_plan is null) return;

        var writer = new MusicTagWriter(_history);
        var done = 0;
        var failed = 0;

        var albums = _plan.Actions
            .Where(a => a.Kind is MusicActionKind.Move or MusicActionKind.Stay)
            .GroupBy(a => Path.GetDirectoryName(a.Destination ?? a.Track.Path) ?? "")
            .ToList();

        // Counted over every file rather than per album, or the bar restarts
        // at each record and says nothing about the hour still to go. Each
        // file is decoded once to be measured and written once after, so the
        // work is two passes and the count reflects that.
        var total = albums.Sum(a => a.Count()) * 2;
        var progress = Measure($"Measuring loudness across {albums.Count:N0} albums...", total);
        var steps = 0;

        foreach (var album in albums)
        {
            token.ThrowIfCancellationRequested();

            var measured = new List<(MusicAction Action, Loudness L)>();

            foreach (var action in album)
            {
                token.ThrowIfCancellationRequested();

                var path = action.Destination ?? action.Track.Path;
                if (!File.Exists(path)) continue;

                if (ReplayGain.Measure(path, token) is { } loudness)
                    measured.Add((action, loudness));

                progress.Report(++steps);
            }

            if (measured.Count == 0) continue;

            var albumGain = ReplayGain.Album(
                measured.Select(m => (m.L, m.Action.Track.Seconds ?? 1.0)));

            foreach (var (action, loudness) in measured)
            {
                var path = action.Destination ?? action.Track.Path;
                var result = writer.Write(new TrackTags { Path = path },
                    ReplayGain.Tags(loudness, albumGain), batchId, token);

                if (result.Written) done++;
                else { failed++; Log($"ReplayGain: {result.Error}"); }

                progress.Report(++steps);
            }
        }

        ClearProgress();

        Log($"ReplayGain written into {done} files across {albums.Count} albums, {failed} refused.");
    }

    private void Busy(bool busy)
    {
        BtnScan.IsEnabled = !busy;
        BtnApply.IsEnabled = !busy && _rows.Count > 0;
        BtnSweep.IsEnabled = !busy && _scanned.Count > 0;
        BtnIdentify.IsEnabled = !busy && _findings.Any(f =>
            f.Ailment is Ailment.Placeholder or Ailment.Missing
            && f.Field is "Title" or "Artist");
        BtnMove.IsEnabled = !busy && _plan is not null
            && _plan.Actions.Any(a => a.Kind is MusicActionKind.Stay);
        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
