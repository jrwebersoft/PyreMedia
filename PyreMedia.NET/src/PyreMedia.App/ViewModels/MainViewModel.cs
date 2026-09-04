using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyreMedia.Core;
using PyreMedia.Core.History;
using PyreMedia.Core.Models;
using PyreMedia.Core.Naming;
using PyreMedia.Core.Media;
using PyreMedia.Core.Metadata;
using PyreMedia.Core.Organizing;
using PyreMedia.Core.Providers;
using PyreMedia.App;

namespace PyreMedia.App.ViewModels;

/// <summary>What the library list is filtered to. Items are classified automatically.</summary>
public enum KindFilter { All, Tv, Movies }

public partial class MainViewModel : ObservableObject
{
    private readonly PyreMediaSettings _settings;
    private readonly RenameHistory _history = new();

    private MetadataService _metadata;
    private MediaScanner _scanner;
    private MediaPlanner _planner;
    private RenameExecutor _executor;

    private CancellationTokenSource? _cts;
    private RenamePlan? _plan;

    private TvShow? _currentShow;
    private Movie? _currentMovie;

    /// <summary>
    /// Manual numbering from the renumber screen. Scoped to the selected item
    /// and cleared when the selection moves, so a mapping made for one show
    /// can't leak onto the next.
    /// </summary>
    private EpisodeOverrides? _overrides;

    /// <summary>
    /// Asks the user how to resolve file collisions. Supplied by the shell so
    /// the view model stays free of dialogs. Returning null cancels the whole
    /// apply - nothing is written.
    /// </summary>
    public Func<IReadOnlyList<FileConflict>, IReadOnlyList<FileConflict>?>? ResolveConflicts { get; set; }

    public PyreMediaSettings Settings => _settings;
    public RenameHistory History => _history;

    public ObservableCollection<LibraryItem> Items { get; } = [];
    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];
    public ObservableCollection<PlannedActionItem> Preview { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Filtered view over Items, driven by <see cref="Filter"/>.</summary>
    public ICollectionView ItemsView { get; }

    public MainViewModel()
    {
        _settings = PyreMediaSettings.Load();
        _metadata = new MetadataService(_settings);
        _scanner = new MediaScanner(_settings);
        _planner = new MediaPlanner(_settings);
        _executor = new RenameExecutor(_settings, _history);

        AutoAdvance = _settings.AutoAdvanceAfterApply;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is LibraryItem i && Filter switch
        {
            KindFilter.Tv => i.IsTv,
            KindFilter.Movies => !i.IsTv,
            _ => true
        };

        // Say so loudly rather than quietly starting from defaults - a folder
        // list disappearing with no explanation looks like the app lost it.
        if (PyreMediaSettings.LastLoadProblem is { } problem)
        {
            WriteLog(problem);
            AppLog.Info("Settings load: " + problem);
            SetBanner(problem, true);
        }

        // No scan from here. The window decides whether to scan on opening,
        // because that is where the setting is checked and where first-run
        // setup gets a chance to run first.
        //
        // Starting one here as well meant "scan the video folders when the
        // window opens" did nothing in either position - this is a field
        // initialiser, so it ran before the window existed to check the
        // setting, and the check downstream only ever suppressed a second scan
        // that the busy interlock would have blocked anyway. Two scans per
        // launch, and a folder list walked before setup had been shown.
        if (!HasFolders) WriteLog("Welcome. Add a folder to get started.");
    }

    // ---------------- Filter ----------------

    [ObservableProperty]
    public partial KindFilter Filter { get; set; } = KindFilter.All;

    public bool FilterAll => Filter == KindFilter.All;
    public bool FilterTv => Filter == KindFilter.Tv;
    public bool FilterMovies => Filter == KindFilter.Movies;

    partial void OnFilterChanged(KindFilter value)
    {
        OnPropertyChanged(nameof(FilterAll));
        OnPropertyChanged(nameof(FilterTv));
        OnPropertyChanged(nameof(FilterMovies));
        RefreshView();
    }

    /// <summary>
    /// Refresh the filtered view without losing the user's selection. A bare
    /// Refresh() rebuilds the view and drops selection, which reads as the list
    /// jumping on its own.
    /// </summary>
    private void RefreshView()
    {
        var keep = SelectedItem;

        _suppressSelectionWork = true;
        try
        {
            ItemsView.Refresh();

            // Restore only if the item survived the filter.
            //
            // Inside the guard, which is the whole reason the guard exists. It
            // sat outside: the flag was cleared in the finally and then this
            // line assigned, so re-selecting the very same row after a filter
            // change ran the full selection-changed path - clearing the match
            // list, throwing away the built preview, and firing another search
            // for a row that had not moved.
            if (keep is not null && ItemsView.Cast<LibraryItem>().Contains(keep))
                SelectedItem = keep;
        }
        finally
        {
            _suppressSelectionWork = false;
        }
    }

    /// <summary>Set while the view is being rebuilt, so restoring selection doesn't re-search.</summary>
    private bool _suppressSelectionWork;

    [RelayCommand]
    private void SetFilter(string? kind) => Filter = kind?.ToLowerInvariant() switch
    {
        "tv" => KindFilter.Tv,
        "movies" => KindFilter.Movies,
        _ => KindFilter.All
    };

    public string ItemsHeader => $"Library ({Items.Count})";

    /// <summary>The folder list changed outside this view model - setup, say.</summary>
    public void RefreshFolders()
    {
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(CanScan));
        ScanCommand.NotifyCanExecuteChanged();
    }

    // ---------------- Folders ----------------

    /// <summary>One list now - items are classified by content, not by which list they came from.</summary>
    public List<string> Folders => _settings.TvFolders;
    public bool HasFolders => Folders.Count > 0 || _settings.MovieFolders.Count > 0;

    /// <summary>
    /// Take on several folders at once and scan once at the end.
    ///
    /// The Add dialog allows more than one, and adding them one at a time
    /// meant one scan per folder - which either raced and duplicated every
    /// row, or, once the interlock was honoured, quietly dropped every folder
    /// after the first.
    /// </summary>
    public void AddFolders(IEnumerable<string> paths)
    {
        var added = 0;

        foreach (var path in paths)
            if (Remember(path)) added++;

        if (added == 0) return;

        if (ScanCommand.CanExecute(null)) ScanCommand.Execute(null);
    }

    [RelayCommand]
    private void AddFolder(string? path)
    {
        if (!Remember(path)) return;

        if (ScanCommand.CanExecute(null)) ScanCommand.Execute(null);
    }

    /// <summary>Add one folder to the list. True when it was not already there.</summary>
    private bool Remember(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Contains() is an exact, case-sensitive match, so "h:\done" alongside
        // "H:\Done" - or the same path with a trailing slash - both got in, and
        // the folder was then scanned once per entry.
        var already = MediaScanner.ScanRoots(_settings)
            .Any(r => MediaScanner.SameFolder(r, path));

        if (already)
        {
            WriteLog($"Already watching that folder: {path}");
            return false;
        }

        Folders.Add(path);
        _settings.Save();

        OnPropertyChanged(nameof(HasFolders));
        ScanCommand.NotifyCanExecuteChanged();

        WriteLog($"Added folder: {path}");
        return true;
    }

    // ---------------- State ----------------

    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>
    /// CanApply depends on IsBusy, and the plan is built *inside* the busy
    /// window - so the NotifyCanExecuteChanged that follows it evaluates while
    /// IsBusy is still true and latches Apply off. Clearing IsBusy in the
    /// finally never re-asked, leaving a ready plan with a dead button.
    /// </summary>
    partial void OnIsBusyChanged(bool value)
    {
        ApplyCommand.NotifyCanExecuteChanged();

        // Scanning or searching mid-apply would pull the item list out from
        // under the operation that's writing to disk.
        ScanCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();

        UpdateWaitingHint();
    }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial string? BannerText { get; set; }
    [ObservableProperty] public partial bool BannerIsError { get; set; }
    [ObservableProperty] public partial LibraryItem? SelectedItem { get; set; }
    [ObservableProperty] public partial SearchResultItem? SelectedResult { get; set; }
    [ObservableProperty] public partial string PreviewSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string? FolderRenameText { get; set; }
    [ObservableProperty] public partial string? FolderRenameProblem { get; set; }
    [ObservableProperty] public partial bool RenameFolderChecked { get; set; } = true;
    /// <summary>
    /// Mirrors the setting, and writes back to it. The tick box beside Apply is
    /// the same switch as the one in Settings, not a second one that forgets.
    /// </summary>
    [ObservableProperty] public partial bool AutoAdvance { get; set; }
    [ObservableProperty] public partial bool IsComplete { get; set; }
    [ObservableProperty] public partial string CompleteText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? EmptyPreviewText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string SearchTerm { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchYear { get; set; } = string.Empty;

    /// <summary>
    /// What to search as. Seeded from auto-detection; the user can flip it when
    /// the guess is wrong (a movie whose name happens to contain "S01", say).
    /// </summary>
    [ObservableProperty]
    public partial bool SearchAsTv { get; set; } = true;

    public bool SearchAsMovie => !SearchAsTv;

    partial void OnSearchAsTvChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchAsMovie));

        if (SelectedItem is not { } item) return;

        var newKind = value ? MediaKind.TvEpisode : MediaKind.Movie;
        if (item.Kind == newKind) return;

        // Re-kinding can push the item out of the active filter, which would
        // drop the selection onto whatever's next. Widen the filter instead.
        var wouldVanish = (Filter == KindFilter.Tv && newKind != MediaKind.TvEpisode)
                          || (Filter == KindFilter.Movies && newKind != MediaKind.Movie);

        item.SetKind(newKind);

        if (wouldVanish)
            Filter = KindFilter.All;   // OnFilterChanged refreshes and keeps selection
        else
            RefreshView();
    }

    [RelayCommand]
    private async Task SetSearchKind(string? kind)
    {
        var wantTv = !string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase);
        if (wantTv == SearchAsTv) return;

        SearchAsTv = wantTv;

        // Re-derive a sensible term for the other kind, then search again.
        if (SelectedItem is { } item)
        {
            SearchTerm = wantTv
                ? item.SearchTerm
                : PyreMedia.Core.Naming.MovieMatcher.Parse(item.Name).Title;

            if (!wantTv)
            {
                var y = PyreMedia.Core.Naming.MovieMatcher.Parse(item.Name).Year;
                SearchYear = y ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(SearchTerm))
                await SearchAsync();
        }
    }

    /// <summary>
    /// Per-show source pin. Sources disagree on some shows - Battlestar Galactica
    /// being the clear case - and once you've decided which numbers a show
    /// correctly, that sticks.
    /// </summary>
    public string ShowSourcePin
    {
        get
        {
            var id = SelectedResult?.Show?.TvdbId;
            return id is not null && _settings.ShowSourceOverrides.TryGetValue(id, out var v) ? v : "Merged";
        }
    }

    public bool PinIsMerged => ShowSourcePin == "Merged";
    public bool PinIsTmdb => ShowSourcePin == "TMDb";
    public bool PinIsTvdb => ShowSourcePin == "TheTVDB";

    /// <summary>Only meaningful for TV, and only once a show has been matched.</summary>
    public bool CanPinSource => SearchAsTv && SelectedResult?.Show is not null;

    [RelayCommand]
    private async Task PinSource(string? source)
    {
        var id = SelectedResult?.Show?.TvdbId;
        if (id is null || string.IsNullOrWhiteSpace(source)) return;

        if (source.Equals("Merged", StringComparison.OrdinalIgnoreCase))
            _settings.ShowSourceOverrides.Remove(id);
        else
            _settings.ShowSourceOverrides[id] = source;

        _settings.Save();

        OnPropertyChanged(nameof(ShowSourcePin));
        OnPropertyChanged(nameof(PinIsMerged));
        OnPropertyChanged(nameof(PinIsTmdb));
        OnPropertyChanged(nameof(PinIsTvdb));

        WriteLog($"Episode source for this show: {source}.");

        // Re-fetch: numbering may differ, which is the whole point.
        _currentShow = null;
        await BuildPreviewAsync();
    }

    /// <summary>
    /// The seasons the files on screen actually belong to. Offsets are per season,
    /// so the quick +/- has to know which ones it is talking about.
    /// </summary>
    private List<int> SeasonsInView =>
        _plan?.Actions
              .Select(a => a.Parsed?.Season)
              .OfType<int>()
              .Where(s => s > 0)                 // specials are numbered on their own
              .Distinct()
              .Order()
              .ToList() ?? [];

    /// <summary>
    /// Shift between the files' numbering and the metadata's, for what is on
    /// screen. Blank when the seasons in view disagree - there is no single
    /// number to show, and the renumber screen is where that gets sorted out.
    /// </summary>
    public int ShowEpisodeOffset
    {
        get
        {
            var id = SelectedResult?.Show?.TvdbId;
            if (id is null) return 0;

            var values = SeasonsInView.Select(s => _settings.EpisodeOffset(id, s)).Distinct().ToList();
            return values.Count == 1 ? values[0] : 0;
        }
    }

    public string OffsetLabel => ShowEpisodeOffset == 0 ? "0" : ShowEpisodeOffset.ToString("+#;-#;0");
    public bool HasOffset => ShowEpisodeOffset != 0;

    [RelayCommand]
    private async Task AdjustOffset(string? delta)
    {
        var id = SelectedResult?.Show?.TvdbId;
        if (id is null) return;

        var seasons = SeasonsInView;
        if (seasons.Count == 0) return;

        var next = string.Equals(delta, "reset", StringComparison.OrdinalIgnoreCase)
            ? 0
            : ShowEpisodeOffset + (int.TryParse(delta, out var d) ? d : 0);

        // Applies to the seasons on screen, not the whole show. Adjusting season 2
        // used to shift season 1 as well, which is only ever right by accident.
        foreach (var season in seasons)
            _settings.SetEpisodeOffset(id, season, next);

        _settings.Save();

        OnPropertyChanged(nameof(ShowEpisodeOffset));
        OnPropertyChanged(nameof(OffsetLabel));
        OnPropertyChanged(nameof(HasOffset));

        WriteLog(seasons.Count == 1
            ? $"Episode offset for season {seasons[0]}: {next:+#;-#;0}."
            : $"Episode offset for seasons {string.Join(", ", seasons)}: {next:+#;-#;0}.");

        // Local re-plan - no refetch needed, the metadata hasn't changed.
        RebuildPlan();
    }

    /// <summary>Names the scope so it's obvious how many files Tracks would act on.</summary>
    public string TracksButtonText => SelectedItem is { } i
        ? i.Media.Files.Count == 1 ? "Remux (1 file)" : $"Remux ({i.Media.Files.Count} files)"
        : "Remux";

    public bool HasBanner => !string.IsNullOrEmpty(BannerText);
    public bool HasFolderRename => FolderRenameText is not null;
    public bool HasPreview => Preview.Count > 0;
    public bool ShowEmptyPreview => !string.IsNullOrEmpty(EmptyPreviewText);

    /// <summary>
    /// Why Apply is unavailable. A greyed-out button with no explanation reads
    /// as a broken app - the user has no way to tell "nothing to do" from
    /// "you haven't picked a match yet".
    /// </summary>
    [ObservableProperty] public partial string? WaitingHint { get; set; }

    public bool ShowWaitingHint => !string.IsNullOrEmpty(WaitingHint);
    partial void OnWaitingHintChanged(string? value) => OnPropertyChanged(nameof(ShowWaitingHint));

    private void UpdateWaitingHint()
    {
        // Piggy-backed here because this runs at every point the plan state can
        // change: preview built, selection moved, busy flipped.
        OnPropertyChanged(nameof(CanRenumber));

        if (CanApply || IsBusy) { WaitingHint = null; return; }

        if (SelectedItem is null)
        {
            WaitingHint = "Pick something on the left to get started.";
            return;
        }

        // A disc rip has nothing to search with, and saying "no match yet" would
        // send somebody off editing a search term that was never going to work.
        // The folder is the disc's volume label and the files are title indices;
        // neither has ever heard of the show.
        if (SelectedResult is null && SelectedItem.Media.IsDiscRip
            && SelectedItem.Media.NameIsDiscLabel && SearchResults.Count == 0)
        {
            WaitingHint = $"This is a disc rip: {SelectedItem.Media.Files.Count} titles named "
                        + "after their position on the disc, in a folder named after the disc "
                        + "itself. Nothing here says what the show is, so type its name above. "
                        + "Play will show you a title if you are not sure.";
            return;
        }

        if (SelectedResult is null)
        {
            WaitingHint = SearchResults.Count == 0
                ? "No match yet. Edit the search above and press Enter - try adding or "
                  + "removing the year, or hyphenating \"and\"."
                : "Pick the correct match above and the changes will appear here.";
            return;
        }

        if (IsComplete) { WaitingHint = null; return; }

        if (Preview.Count == 0) { WaitingHint = null; return; }   // EmptyPreviewText covers this

        var problems = Preview.Count(p => p.Status == PlanStatus.Problem);
        var correct = Preview.Count(p => p.Status == PlanStatus.AlreadyCorrect);

        WaitingHint =
            problems > 0 && problems + correct == Preview.Count
                ? $"Nothing can be applied: {problems} file(s) need attention - see the note on each row."
            : correct == Preview.Count
                ? "Everything here is already named correctly."
            : "Tick at least one change to enable Apply.";
    }

    partial void OnAutoAdvanceChanged(bool value)
    {
        if (_settings.AutoAdvanceAfterApply == value) return;

        _settings.AutoAdvanceAfterApply = value;
        _settings.Save();
    }

    partial void OnBannerTextChanged(string? value) => OnPropertyChanged(nameof(HasBanner));
    partial void OnFolderRenameTextChanged(string? value) => OnPropertyChanged(nameof(HasFolderRename));
    partial void OnEmptyPreviewTextChanged(string? value) => OnPropertyChanged(nameof(ShowEmptyPreview));

    partial void OnSelectedItemChanged(LibraryItem? value)
    {
        if (_suppressSelectionWork) return;

        SearchResults.Clear();
        ResetPreviewState();

        SelectedResult = null;
        _currentShow = null;
        _currentMovie = null;
        _overrides = null;

        OnPropertyChanged(nameof(TracksButtonText));

        SearchTerm = value?.SearchTerm ?? string.Empty;
        SearchYear = value?.SearchYear ?? string.Empty;

        if (value is null) return;

        SearchAsTv = value.IsTv;

        if (!string.IsNullOrWhiteSpace(SearchTerm))
            _ = SearchAsync();
    }

    partial void OnSelectedResultChanged(SearchResultItem? value)
    {
        OnPropertyChanged(nameof(ShowSourcePin));
        OnPropertyChanged(nameof(PinIsMerged));
        OnPropertyChanged(nameof(PinIsTmdb));
        OnPropertyChanged(nameof(PinIsTvdb));
        OnPropertyChanged(nameof(CanPinSource));
        OnPropertyChanged(nameof(ShowEpisodeOffset));
        OnPropertyChanged(nameof(OffsetLabel));
        OnPropertyChanged(nameof(HasOffset));

        if (value is not null && !IsBusy)
            _ = BuildPreviewAsync();
        else
            UpdateWaitingHint();
    }

    private void ResetPreviewState()
    {
        Preview.Clear();
        _plan = null;
        PreviewSummary = string.Empty;
        FolderRenameText = null;
        FolderRenameProblem = null;
        RenameFolderChecked = true;
        IsComplete = false;
        CompleteText = string.Empty;
        EmptyPreviewText = null;
        BannerText = null;

        OnPropertyChanged(nameof(HasPreview));
        ApplyCommand.NotifyCanExecuteChanged();
        UpdateWaitingHint();
    }

    // ---------------- Scan ----------------

    /// <summary>
    /// Not while something is running. A scan clears and rebuilds the item list,
    /// and an apply in flight holds references into it - rescanning mid-apply
    /// leaves it operating on items no longer in the library.
    /// </summary>
    private bool CanScan => HasFolders && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        BannerText = null;
        StatusText = "Scanning...";
        Items.Clear();

        try
        {
            var problems = new List<string>();
            var found = await Task.Run(() => _scanner.Scan(problems));

            foreach (var m in found) Items.Add(new LibraryItem(m));
            foreach (var p in problems) WriteLog(p);

            var tv = Items.Count(i => i.IsTv);
            var mv = Items.Count - tv;

            OnPropertyChanged(nameof(ItemsHeader));
            RefreshView();

            StatusText = $"{Items.Count} item(s): {tv} TV, {mv} movie(s)";
            SetBanner($"Found {Items.Count} item(s) - {tv} TV, {mv} movie(s). Select one to begin.", false);
            WriteLog(StatusText);

            // A season per folder is how most TV arrives, and every folder is
            // another item to match by hand. Say when several are one show, so
            // eight folders aren't matched eight times.
            ReportShowGroups();
            FindLeftovers();

            // Reading every file is slow, so this runs after the scan is already
            // usable, updating rows as results arrive. A previous pass is called
            // off first - it is working from a list that no longer exists, and on
            // a large library the two would grind over the disk together.
            _flagCts?.Cancel();
            _flagCts?.Dispose();
            _flagCts = null;

            if (_settings.ReadTrackDetails)
            {
                _flagCts = new CancellationTokenSource();
                _ = ReadTrackDetailsAsync(_flagCts.Token);
            }
        }
        catch (Exception ex)
        {
            StatusText = "Scan failed";
            SetBanner($"Scan failed: {ex.Message}", true);
            WriteLog($"Scan failed: {ex.GetType().Name}: {ex.Message}");
            AppLog.Error("Scan", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Probe each item's main file for the two things worth knowing before you
    /// open anything: audio in a language you don't want, and 3D the filename
    /// never mentioned.
    /// </summary>
    /// <summary>
    /// Cancels the track-probing pass. It reads every file in the library, so
    /// on a large one it runs for a long time and has to be stoppable.
    /// </summary>
    private CancellationTokenSource? _flagCts;

    private async Task ReadTrackDetailsAsync(CancellationToken ct)
    {
        var probe = new MediaProbe(_settings.FfprobePath);

        if (!await probe.IsAvailableAsync(ct))
        {
            WriteLog("Track probing skipped: ffprobe not found.");
            return;
        }

        var detect3D = _settings.Keep3DTag;
        var tagged3D = 0;

        var preferred = _settings.PreferredLanguage.Trim().ToLowerInvariant();
        var snapshot = Items.ToList();
        var flagged = 0;

        foreach (var item in snapshot)
        {
            if (ct.IsCancellationRequested) return;

            // Nothing to read inside a disc image, and asking costs a spin-up
            // of whatever drive it lives on for an answer that cannot come.
            if (_settings.IsDiscImage(item.Media.MainFile)) continue;

            try
            {
                var info = await probe.ProbeAsync(item.Media.MainFile, ct);
                item.Media.Probed = true;

                if (info is not null)
                {
                    // Compared through the catalogue: a French track tagged
                    // "fra" against a preference of "fre" is not foreign, and
                    // string equality would flag it as though it were.
                    var foreign = info.Audio
                        .Select(a => a.Language)
                        .Where(l => !LanguageCatalog.Same(l, preferred) && l != "und")
                        .Distinct()
                        .ToList();

                    item.Media.ForeignAudio.Clear();
                    item.Media.ForeignAudio.AddRange(foreign);
                    if (foreign.Count > 0) flagged++;

                    // Free - the streams are already here and were being
                    // discarded. Cover art is a video stream, not a subtitle,
                    // so neither count is affected by it.
                    item.Media.AudioTracks = info.Audio.Count();

                    var subtitles = info.Streams.Where(s => s.Kind == StreamKind.Subtitle).ToList();
                    item.Media.SubtitleTracks = subtitles.Count;

                    // In file order rather than sorted, because the order is
                    // itself information - the first audio track is the one
                    // that plays by default.
                    item.Media.AudioLanguages.Clear();
                    item.Media.AudioLanguages.AddRange(
                        info.Audio.Select(a => a.Language).Distinct(StringComparer.OrdinalIgnoreCase));

                    item.Media.SubtitleLanguages.Clear();
                    item.Media.SubtitleLanguages.AddRange(
                        subtitles.Select(s => s.Language).Distinct(StringComparer.OrdinalIgnoreCase));

                    // 3D the filename never mentioned. Only worth reading when
                    // the name is silent - a hand-written tag is more reliable,
                    // since half-width layouts look exactly like 2D.
                    if (detect3D
                        && Stereo3DTag.Extract(Path.GetFileNameWithoutExtension(item.Media.MainFile)) is null
                        && Stereo3DTag.Extract(item.Media.DisplayName) is null
                        && info.Video.FirstOrDefault(v => v.Is3D || v.IsMvc || v.PackedLayout is not null) is { } v3d)
                    {
                        item.Media.Detected3D = Stereo3DTag.ForDetected(v3d.IsMvc, v3d.PackedLayout);
                        tagged3D++;
                    }
                }

                item.RefreshProbeState();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.Error($"Probe {item.Name}", ex);
            }
        }

        WriteLog(flagged > 0
            ? $"Flagged {flagged} item(s) carrying audio other than '{preferred}'."
            : $"No items carry audio other than '{preferred}'.");

        if (tagged3D > 0)
            WriteLog($"Detected 3D in {tagged3D} item(s) whose filename didn't say so.");
    }

    // ---------------- Search ----------------

    private bool CanSearch => !string.IsNullOrWhiteSpace(SearchTerm) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        // Say why nothing happens, rather than letting it fail somewhere inside
        // an HTTP call as an unauthorised response the user can't interpret.
        if (!_settings.HasTmdbKey)
        {
            SetBanner("No TMDb key, so there is nothing to search. Add one in Settings - "
                      + "it's free, and takes a couple of minutes at "
                      + "themoviedb.org/settings/api.", true);

            WriteLog("Search skipped: no TMDb API key. Settings > Metadata sources.");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        BannerText = null;
        StatusText = $"Searching for \"{SearchTerm}\"...";
        SearchResults.Clear();
        ResetPreviewState();

        SearchResultItem? autoPick = null;

        try
        {
            var year = string.IsNullOrWhiteSpace(SearchYear) ? null : SearchYear;

            // Routed by the (overridable) kind toggle, not by any global mode.

            async Task<List<SearchResultItem>> Ask(string term) => SearchAsTv
                ? [.. (await _metadata.SearchAsync(term, year, ct)).Select(r => new SearchResultItem(r))]
                : [.. (await _metadata.SearchMoviesAsync(term, year, ct)).Select(r => new SearchResultItem(r))];

            var found = await Ask(SearchTerm);
            var searched = SearchTerm;

            // Nothing found, so try it shorter.
            //
            // TMDb matches on every word, not loosely: one word too many
            // returns nothing at all rather than a worse match. "SUICIDE SQUAD
            // EDITION" - what is left of a disc labelled "3D-2D EXTENDED
            // EDITION" once the release noise is stripped - finds nothing,
            // while "Suicide Squad" finds it first time. A name is far more
            // likely to have junk on the end than the beginning, so the end is
            // what gets dropped.
            if (found.Count == 0)
            {
                foreach (var shorter in Shorten(SearchTerm))
                {
                    if (ct.IsCancellationRequested) return;

                    found = await Ask(shorter);
                    if (found.Count == 0) continue;

                    searched = shorter;
                    WriteLog($"Nothing for \"{SearchTerm}\" - searched \"{shorter}\" instead.");
                    break;
                }
            }

            // The selection may have moved while that was in flight.
            //
            // Cancelling the previous token is not enough on its own: a token is
            // only observed at an await, so a search whose results had already
            // come back carried straight on and wrote them - and its confident
            // match - against whatever row was selected by the time it got
            // there. Reported as clicking Strange New Worlds and having
            // Stranger Things chosen instead. They sit next to each other in
            // the list, which is exactly how two searches end up overlapping:
            // click one, then the other.
            if (!ReferenceEquals(SelectedItem, item)) return;

            foreach (var r in found) SearchResults.Add(r);

            // Say so when the answer came from a different question.
            if (SearchResults.Count > 0 && searched != SearchTerm)
                SetBanner($"Nothing matched \"{SearchTerm}\", so these are for \"{searched}\". "
                          + "Check the match before applying.", false);

            if (SearchResults.Count == 0)
            {
                StatusText = "No matches";
                SetBanner($"Nothing found for \"{SearchTerm}\". Try a shorter or corrected term.", true);
                return;
            }

            StatusText = $"{SearchResults.Count} match(es)";

            // Only when asked for. The setting is off by default and said so,
            // and picking regardless meant a single search result was chosen,
            // logged and built into a fully ticked rename plan against a show
            // nobody had agreed to - one click from Apply.
            autoPick = _settings.AutoSelectMatch ? PickConfidentMatch() : null;
        }
        catch (OperationCanceledException) { StatusText = "Cancelled"; }
        catch (MetadataException ex)
        {
            StatusText = "Search failed";
            SetBanner(ex.Message, true);
            WriteLog(ex.Message);
        }
        catch (Exception ex)
        {
            StatusText = "Search failed";
            SetBanner($"Unexpected error: {ex.Message}", true);
            WriteLog($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            AppLog.Error("MainViewModel", ex);
        }
        finally
        {
            IsBusy = false;
        }

        // Outside the busy window: assigning while IsBusy was true made the
        // SelectedResult hook skip building the preview.
        // Still guarded: this is the assignment that puts a show against a row,
        // and it sits outside the try above.
        if (autoPick is not null && ReferenceEquals(SelectedItem, item))
        {
            WriteLog($"Auto-selected '{autoPick.Display}'.");

            // Assigning is enough. The hook on this property starts the preview
            // build, and awaiting a second one started a race with the first:
            // the newcomer cancelled the in-flight token, the metadata service
            // reports a cancelled fetch as no data rather than as cancellation,
            // and the loser put up "No episode data for 'Firefly (2002)'" over
            // a perfectly good change list that the winner had just built. The
            // episode list was also fetched twice for every confident match.
            SelectedResult = autoPick;
        }
    }

    /// <summary>
    /// The same term with trailing words dropped, longest first.
    ///
    /// Never below two words: one word matches half a catalogue, and choosing
    /// from that is worse than finding nothing and being told so. A two-word
    /// term is therefore not shortened at all.
    /// </summary>
    private static IEnumerable<string> Shorten(string term)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var take = words.Length - 1; take >= 2; take--)
            yield return string.Join(' ', words.Take(take));
    }

    private SearchResultItem? PickConfidentMatch()
    {
        if (SearchResults.Count == 0) return null;

        // An id from the sidecar nfo beats any name comparison: something
        // already identified this exact file and wrote the answer down. This is
        // what rescues titles that are ambiguous by name - "300", remakes
        // sharing a title, or a film whose folder was renamed by hand.
        if (SelectedItem?.Media.Nfo is { HasAnyId: true } nfo)
        {
            var byId = SearchResults.FirstOrDefault(r =>
                (nfo.TmdbId is not null && MatchesId(r, nfo.TmdbId, tmdb: true)) ||
                (nfo.TvdbId is not null && MatchesId(r, nfo.TvdbId, tmdb: false)));

            if (byId is not null)
            {
                WriteLog($"Matched by id from {Path.GetFileName(nfo.Path)} ({nfo.IdSummary}).");
                return byId;
            }
        }

        if (SearchResults.Count == 1) return SearchResults[0];

        static string Norm(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

        var target = Norm(SearchTerm);
        var exact = SearchResults.Where(r => Norm(r.Name) == target).ToList();

        if (exact.Count == 1 &&
            (string.IsNullOrWhiteSpace(SearchYear) || exact[0].Year == SearchYear))
            return exact[0];

        return null;
    }

    private static bool MatchesId(SearchResultItem r, string id, bool tmdb)
    {
        var candidate = tmdb
            ? r.MovieResult?.TmdbId ?? r.Show?.TmdbId
            : r.Show?.TvdbId;

        return candidate is not null && string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Preview ----------------

    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        var item = SelectedItem;

        // Held rather than re-read. The selection moves on its own - auto
        // advance after an Apply, the rescan that follows it, a fresh scan
        // clearing the list - and the setter nulls this on the way past. Every
        // line below used to read the property again after an await, so a run
        // whose item had moved on dereferenced null and put "Unexpected error:
        // Object reference not set to an instance of an object" over the
        // incoming item's plan. The real log has it six times.
        var picked = SelectedResult;
        if (picked is null || item is null) return;

        /// <summary>True once the user has moved on and this run is stale.</summary>
        bool MovedOn() => !ReferenceEquals(SelectedResult, picked)
                       || !ReferenceEquals(SelectedItem, item);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        StatusText = "Fetching details...";
        Preview.Clear();
        OnPropertyChanged(nameof(HasPreview));

        try
        {
            if (SearchAsTv)
            {
                if (picked.Show is null) return;

                if (_currentShow is null || _currentShow.Id != picked.Show.TvdbId)
                {
                    _currentShow = await _metadata.GetShowAsync(
                        picked.Show, new Progress<string>(WriteLog), ct);

                    // Nothing here belongs to the item on screen any more.
                    if (MovedOn()) return;

                    if (_currentShow is not null)
                    {
                        var n = _currentShow.Seasons.Sum(s => s.Episodes.Count);
                        WriteLog($"'{_currentShow.Name}': {_currentShow.Seasons.Count} season(s), {n} episode(s).");
                    }
                }

                if (_currentShow is null)
                {
                    StatusText = "No episode data";
                    SetBanner($"No episode data for '{picked.Display}'.", true);
                    return;
                }

                item.MatchedName = _currentShow.Name;
            }
            else
            {
                if (picked.MovieResult is null) return;

                _currentMovie = await _metadata.GetMovieAsync(picked.MovieResult.TmdbId, ct);

                if (MovedOn()) return;

                if (_currentMovie is null)
                {
                    StatusText = "No movie data";
                    SetBanner($"No details for '{picked.Display}'.", true);
                    return;
                }

                item.MatchedName = _currentMovie.Title;
            }

            StatusText = "Working out what would change...";
            RebuildPlan();
        }
        catch (OperationCanceledException) { StatusText = "Cancelled"; }
        catch (MetadataException ex)
        {
            StatusText = "Failed";
            SetBanner(ex.Message, true);
            WriteLog(ex.Message);
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            SetBanner($"Unexpected error: {ex.Message}", true);
            WriteLog($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            AppLog.Error("MainViewModel", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-plan locally from disk. No network, so it's cheap after Apply.</summary>
    private void RebuildPlan()
    {
        var item = SelectedItem;
        if (item is null) return;

        Preview.Clear();

        if (SearchAsTv)
        {
            if (_currentShow is null) return;
            _plan = _planner.PlanTv(item.Media, _currentShow, _overrides);
        }
        else
        {
            if (_currentMovie is null) return;
            _plan = _planner.PlanMovie(item.Media, _currentMovie);
        }

        foreach (var a in _plan.Actions.Where(a => a.Status != PlanStatus.AlreadyCorrect))
        {
            var row = new PlannedActionItem(a)
            {
                CreatesFolder = a.TargetPath is not null
                                && _plan.FoldersToCreate.Any(f => string.Equals(
                                       f, Path.GetDirectoryName(a.TargetPath), StringComparison.OrdinalIgnoreCase))
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlannedActionItem.IsSelected))
                {
                    UpdateSummary();
                    UpdateWaitingHint();
                    ApplyCommand.NotifyCanExecuteChanged();
                }
            };
            Preview.Add(row);
        }

        if (_plan.FolderRename is { } fr)
        {
            FolderRenameText = $"{fr.SourceName}   ->   {fr.TargetName}";
            FolderRenameProblem = fr.Problem;
            if (fr.Problem is not null) RenameFolderChecked = false;
        }
        else
        {
            FolderRenameText = null;
            FolderRenameProblem = null;
        }

        IsComplete = Preview.Count == 0 && _plan.Actions.Count > 0 && _plan.FolderRename is null;

        CompleteText = $"All {_plan.UnchangedCount} file(s) are correctly named.";

        // Don't claim "organised" while the folder is still a release name -
        // say why nothing is being offered.
        if (IsComplete && !_settings.RenameShowFolder && SelectedItem is { } si && !si.Media.IsLooseFile)
        {
            var wanted = PyreMedia.Core.Naming.NameFormatter.BuildShowFolderName(
                si.IsTv ? _settings.ShowFolderFormat : _settings.MovieFolderFormat,
                si.MatchedName ?? string.Empty,
                (si.IsTv ? _currentShow?.Year : _currentMovie?.Year) ?? string.Empty,
                _settings.FilenameReplaceChar);

            var current = Path.GetFileName(si.Media.Path.TrimEnd(Path.DirectorySeparatorChar));

            if (!string.IsNullOrWhiteSpace(wanted) &&
                !string.Equals(current, wanted, StringComparison.Ordinal))
            {
                CompleteText += $"\n\nThe folder is still \"{current}\" - it would become \"{wanted}\", "
                              + "but folder renaming is switched off in Settings.";
            }
        }

        // Only claim there are no files when the plan really found none.
        //
        // IsComplete also insists there is no folder rename outstanding, so a
        // show whose episodes were all correctly named but whose folder was
        // still a release name produced an empty preview with IsComplete false
        // - and this then sent the user to the file-type setting to fix files
        // that had been found and read perfectly well.
        EmptyPreviewText = Preview.Count > 0 || IsComplete ? null
            : _plan is { Actions.Count: > 0 }
                ? "Every file here is already named correctly - only the folder would change."
                : "No video files found here. Check the allowed file types in Settings.";

        UpdateWaitingHint();

        UpdateSummary();
        OnPropertyChanged(nameof(HasPreview));
        ApplyCommand.NotifyCanExecuteChanged();

        StatusText = _plan.HasWork || _plan.FolderRename is not null
            ? _plan.ChangeCount == 1 ? "1 change to review" : $"{_plan.ChangeCount} changes to review"
            : "Nothing to change";

        // Say why a library that looks perfectly correct is full of moves. The
        // filenames are right and every episode is with its show, so without
        // this the plan reads as the program having changed its mind rather
        // than as the season-folder setting doing what it says.
        if (_plan.GainsSeasonFolders)
            StatusText += " - this show keeps its episodes loose, so they are being gathered into season folders";
    }

    // ---------------- Renumber ----------------

    /// <summary>Renumbering only makes sense once we have a show and its files.</summary>
    public bool CanRenumber => SearchAsTv && _currentShow is not null && SelectedItem is not null;

    /// <summary>
    /// Everything TMDb has for a title, for the artwork picker. Goes through the
    /// metadata service so it uses the same client and the same key.
    /// </summary>
    public Task<ArtworkSet> GetArtworkAsync(bool isTv, string tmdbId, CancellationToken ct = default) =>
        _metadata.GetArtworkAsync(isTv, tmdbId, ct);

    public TvShow? CurrentShow => _currentShow;

    public ShowSearchResult? CurrentSearchResult => SelectedResult?.Show;

    public EpisodeSource CurrentEpisodeSource => _settings.EpisodeSource;

    public Task<TvShow?> GetShowFromAsync(ShowSearchResult show, EpisodeSource source, CancellationToken ct)
        => _metadata.GetShowFromAsync(show, source, ct);

    /// <summary>Called by the renumber dialog once the user accepts a mapping.</summary>
    public void ApplyOverrides(EpisodeOverrides? overrides, TvShow? show)
    {
        _overrides = overrides is { IsEmpty: false } ? overrides : null;
        if (show is not null) _currentShow = show;

        RebuildPlan();

        if (_overrides is not null)
        {
            var n = _overrides.Map.Count;
            var skipped = _overrides.Skip.Count;

            WriteLog($"Manual numbering applied to {n} file(s)"
                     + (skipped > 0 ? $", {skipped} excluded." : "."));
        }
    }

    /// <summary>True when the current preview is using a hand-made mapping.</summary>
    public bool HasOverrides => _overrides is not null;

    private void UpdateSummary()
    {
        if (_plan is null) { PreviewSummary = string.Empty; return; }

        var selected = Preview.Count(p => p.IsSelected);
        var parts = new List<string> { $"{selected} selected" };

        // Folder creation was invisible before - a move into a brand-new folder
        // looked identical to no change at all.
        // Any new folder the target sits under, not just its direct parent -
        // "Extras" nests inside a brand-new movie folder, and only the inner
        // one was being reported.
        var newFolders = _plan.FoldersToCreate
            .Where(f => Preview.Any(p => p.IsSelected && p.Action.TargetPath is { } t
                        && t.StartsWith(f + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Length)
            .ToList();

        if (newFolders.Count is 1 or 2)
            parts.Add("creates " + string.Join(" and ",
                newFolders.Select(f => $"\"{Path.GetFileName(f)}\"")));
        else if (newFolders.Count > 2)
            parts.Add($"creates {newFolders.Count} folders");

        if (_plan.UnchangedCount > 0) parts.Add($"{_plan.UnchangedCount} already correct");
        if (_plan.ProblemCount > 0) parts.Add($"{_plan.ProblemCount} need attention");
        // Ticked, not merely found. This counted every deletion the plan had
        // offered whether or not it was going to happen, so a preview about to
        // delete nothing still read "2 to delete" - and the folder came out of
        // Apply still holding the sample clip and the release advert it had
        // just promised to remove. Deletions arrive unticked on purpose; that
        // is worth saying out loud rather than reporting them as done deals.
        if (_plan.DeleteCount > 0)
        {
            var ticked = Preview.Count(p => p.IsSelected && p.Status == PlanStatus.Delete);

            parts.Add(ticked == _plan.DeleteCount ? $"{ticked} to delete"
                    : ticked == 0 ? $"{_plan.DeleteCount} leftover(s) found - none ticked"
                    : $"{ticked} of {_plan.DeleteCount} leftover(s) ticked");
        }

        var overwrites = Preview.Count(p => p.IsSelected && p.WillOverwrite);
        if (overwrites > 0) parts.Add($"{overwrites} will replace an existing file");

        PreviewSummary = string.Join("   |   ", parts);

        OnPropertyChanged(nameof(UntickedJunkCount));
        OnPropertyChanged(nameof(HasUntickedJunk));
        OnPropertyChanged(nameof(UntickedJunkText));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var p in Preview.Where(p => p.CanSelect)) p.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var p in Preview) p.IsSelected = false;
    }

    /// <summary>
    /// Leftovers the plan found and nobody ticked - sample clips, release
    /// adverts, the .nfo a tracker leaves behind.
    ///
    /// They are offered unticked because a deletion is not something to default
    /// into. What was missing was any way to accept them all without also
    /// accepting every rename, and any word afterwards that they had been left:
    /// a folder came out of a rename still holding a 77 MB sample and a scene
    /// .nfo, and the only way to find that out was to open it in Explorer.
    /// </summary>
    public int UntickedJunkCount => Preview.Count(p => p.Status == PlanStatus.Delete && !p.IsSelected);

    public bool HasUntickedJunk => UntickedJunkCount > 0;

    public string UntickedJunkText => $"Leftovers ({UntickedJunkCount})";

    [RelayCommand]
    private void SelectLeftovers()
    {
        foreach (var p in Preview.Where(p => p.Status == PlanStatus.Delete)) p.IsSelected = true;
    }

    // ---------------- Apply ----------------

    private bool CanApply =>
        _plan is not null && !IsBusy &&
        (Preview.Any(p => p.IsSelected) ||
         (RenameFolderChecked && _plan.FolderRename is { CanApply: true }));

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var item = SelectedItem;
        if (_plan is null || item is null) return;

        var chosen = Preview.Where(p => p.IsSelected).Select(p => p.Action).ToList();
        var doFolder = RenameFolderChecked && _plan.FolderRename is { CanApply: true };
        if (chosen.Count == 0 && !doFolder) return;

        // Ask about every collision before anything is written. Answering them
        // one at a time mid-run means the earlier answers are out of sight and
        // half the batch is already applied by the time you change your mind.
        var conflicts = _executor.FindConflicts(chosen);

        if (conflicts.Count > 0)
        {
            var resolved = ResolveConflicts?.Invoke(conflicts);
            if (resolved is null)
            {
                StatusText = "Cancelled";
                WriteLog("Apply cancelled at the conflict prompt - nothing was changed.");
                return;
            }

            conflicts = [.. resolved];
        }

        IsBusy = true;
        BannerText = null;
        StatusText = $"Applying {chosen.Count} change(s)...";

        var rescan = false;
        var plan = _plan;

        // Watch for history writes failing during this apply - undo silently
        // missing entries is worse than the rename failing outright.
        _history.ResetFailureCount();
        var title = SearchAsTv ? _currentShow?.Name : _currentMovie?.Title;
        var matchId = SearchAsTv ? SelectedResult?.Show?.TvdbId : SelectedResult?.MovieResult?.TmdbId;
        var progress = new Progress<string>(WriteLog);

        try
        {
            var result = await Task.Run(() =>
                _executor.Execute(plan, chosen, progress, CancellationToken.None, doFolder, title, matchId,
                                  conflicts));

            foreach (var e in result.Errors) WriteLog($"ERROR: {e}");

            if (result.FolderRenamed is not null && plan.FolderRename is { } fr)
                item.FolderRenamedTo = fr.TargetName;

            var bits = new List<string>();
            if (result.Succeeded > 0) bits.Add($"{result.Succeeded} renamed");
            if (result.Deleted > 0) bits.Add($"{result.Deleted} deleted");
            if (result.FolderRenamed is not null) bits.Add($"folder renamed to \"{result.FolderRenamed}\"");
            if (result.Failed > 0) bits.Add($"{result.Failed} failed");

            var msg = bits.Count > 0 ? "Done - " + string.Join(", ", bits) : "Nothing to do";

            // Leftovers that were offered and not taken. Silence here is how a
            // renamed folder kept a 77 MB sample clip and a tracker's .nfo:
            // they were listed in the preview, unticked as deletions always
            // are, and Apply walked past them without a word. Nothing is
            // deleted on the strength of this - it just stops the answer to
            // "why is this still here?" being a trip to Explorer.
            var skipped = plan.Actions.Count(a => a.Status == PlanStatus.Delete
                                                  && !chosen.Contains(a));

            if (skipped > 0)
                msg += $".  {skipped} leftover file(s) left in place - "
                       + "tick them with Leftovers to remove them.";

            // The files moved either way; what's missing is the ability to put
            // them back, and that has to be said plainly.
            var historyLost = _history.FailedWrites;
            if (historyLost > 0)
            {
                msg += $".  {historyLost} change(s) could not be recorded, so Undo won't cover them"
                       + (_history.LastError is { } why ? $" ({why})" : "");

                AppLog.Error("History", new IOException(_history.LastError ?? "unknown"));
            }

            StatusText = result.Failed == 0 ? "Done" : "Completed with errors";
            SetBanner(msg, result.Failed != 0 || historyLost > 0);
            WriteLog(msg);

            // Sidecars after the move, never before: they have to land beside
            // the file at its final name, and a rename that failed shouldn't
            // leave an .nfo pointing at something that isn't there.
            if (result.Succeeded > 0)
            {
                await WriteNfosAsync(chosen);
                await WriteArtworkAsync(chosen);
            }

            // Whether this item is finished is what the run reported, not what
            // a fresh plan says afterwards.
            //
            // MediaItem.Path and MainFile are fixed at scan time, so once a
            // show folder has been renamed - which is the default - re-planning
            // works from a folder that no longer exists and every episode comes
            // back as still needing a move. The item was therefore never marked
            // done, the green tick never appeared, "move to next automatically"
            // never advanced, and finished items were offered again forever.
            var clean = result.Failed == 0 && result.Errors.Count == 0;

            if (clean) item.IsDone = true;

            // Re-scan this item from disk so the plan reflects reality.
            RefreshSelectedItemFiles();
            RebuildPlan();

            if (clean && AutoAdvance) NextItem();

            // A rename can create folders, move files between them and change
            // what an item even is. Refreshing only the selected item left the
            // rest of the library describing a layout that no longer exists.
            rescan = result.Succeeded > 0 || result.Deleted > 0 || result.FolderRenamed is not null;
        }
        catch (Exception ex)
        {
            StatusText = "Apply failed";
            SetBanner($"Apply failed: {ex.Message}", true);
            WriteLog($"Apply failed: {ex.GetType().Name}: {ex.Message}");
            AppLog.Error("Apply", ex);
        }
        finally
        {
            IsBusy = false;
        }

        // Outside the finally, so IsBusy is already clear and the scan can run.
        if (rescan) await RescanAfterApplyAsync();
    }

    /// <summary>
    /// Rescan the library after files have moved, keeping the user where they
    /// were. A bare scan resets the list and drops you back at the top, which
    /// working through a folder makes intolerable.
    /// </summary>
    private async Task RescanAfterApplyAsync()
    {
        var keepPath = SelectedItem?.Media.Path;
        var keepName = SelectedItem?.DisplayName;
        var done = Items.Where(i => i.IsDone).Select(i => i.Media.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        await ScanAsync();

        // Items that were finished before the scan should stay finished.
        foreach (var i in Items.Where(i => done.Contains(i.Media.Path)))
            i.IsDone = true;

        if (keepPath is null) return;

        // The folder may have been renamed, so fall back to the display name.
        var again = Items.FirstOrDefault(i =>
                        string.Equals(i.Media.Path, keepPath, StringComparison.OrdinalIgnoreCase))
                    ?? Items.FirstOrDefault(i =>
                        string.Equals(i.DisplayName, keepName, StringComparison.OrdinalIgnoreCase));

        // Whether it is finished or not.
        //
        // This used to refuse to reselect a finished item, which was harmless
        // only for as long as nothing ever marked one finished. Once that was
        // fixed, applying a rename left the selection empty - and Play, the
        // per-item Remux and Artwork are all bound to there being a selection,
        // so they vanished from a film the moment it was correctly named. The
        // one thing you most want to do after tidying a file up is look at it.
        //
        // Advancing past a finished item is auto-advance's job, and it has
        // already run by the time this does.
        if (again is not null) SelectedItem = again;
    }

    /// <summary>
    /// Write a Kodi sidecar beside each file that just landed.
    ///
    /// Runs after the rename so the .nfo takes the file's final name - Kodi
    /// pairs them by filename, and writing first would leave the sidecar
    /// orphaned the moment the video moved.
    /// </summary>
    /// <summary>
    /// Write the .nfo and fetch the artwork again for this item, whether or not
    /// anything needs renaming.
    ///
    /// Apply only ever runs on changes, so an item that was already named
    /// correctly had no route to either - and they are the two things most
    /// often worth redoing: a poster you did not want, a sidecar written before
    /// the match was corrected, artwork something else deleted.
    ///
    /// Works from where the files are now, since nothing is moving.
    /// </summary>
    public async Task RedoSidecarsAsync()
    {
        if (_plan is null || SelectedItem is null) return;

        // A plan for files that are staying put: the target is where each one
        // already is, which is what both writers key off. Where the plan named
        // no target - because nothing was going to change - the file's own
        // path is the truthful answer, and saying so is what lets a correctly
        // named file get its sidecars rewritten at all.
        var here = new List<PlannedAction>();

        foreach (var a in _plan.Actions)
        {
            if (a.TargetPath is not { Length: > 0 } t || !File.Exists(t))
            {
                if (!File.Exists(a.SourcePath)) continue;
                a.TargetPath = a.SourcePath;
            }

            here.Add(a);
        }

        if (here.Count == 0)
        {
            SetBanner("Nothing to write - none of this item's files are where the scan left them. "
                      + "Scan again first.", true);
            return;
        }

        IsBusy = true;

        try
        {
            StatusText = "Writing sidecars...";

            await WriteNfosAsync(here);
            await WriteArtworkAsync(here);

            StatusText = "Done";
            SetBanner($"Rewrote the .nfo and artwork for {here.Count} file(s).", false);
        }
        catch (Exception ex)
        {
            SetBanner($"Could not rewrite them: {ex.Message}", true);
            AppLog.Error("Redo sidecars", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task WriteNfosAsync(List<PlannedAction> applied)
    {
        if (!_settings.WriteNfoFiles) return;

        var probe = new MediaProbe(_settings.FfprobePath);
        var written = 0;
        var refreshed = 0;
        var failed = 0;

        foreach (var action in applied)
        {
            if (action.TargetPath is not { } target) continue;

            try
            {
                if (!File.Exists(target)) continue;

                // streamdetails describes the file, so it has to be probed -
                // but a probe failure shouldn't cost us the rest of the nfo.
                MediaInfo? info = null;
                try { info = await probe.ProbeAsync(target, CancellationToken.None); }
                catch { }

                var result = SearchAsTv
                    ? _currentShow is { } show && action.Episode is not null
                        // Every episode the file covers, so a two-episode file
                        // doesn't show up in Kodi as one.
                        ? NfoWriter.WriteEpisodes(target, show, [.. action.AllEpisodes], info, _settings)
                        : null
                    : _currentMovie is { } movie
                        ? NfoWriter.WriteMovie(target, movie, info, _settings)
                        : null;

                switch (result?.Outcome)
                {
                    case NfoWriter.Outcome.Written: written++; break;
                    case NfoWriter.Outcome.Updated: refreshed++; break;
                    case NfoWriter.Outcome.Failed:
                        failed++;
                        WriteLog($"nfo: {Path.GetFileName(target)} - {result.Detail}");
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Error($"nfo for {target}", ex);
            }
        }

        // The show's own tvshow.nfo, once per show rather than per episode.
        // Without it Kodi has nothing to hang the series artwork, plot or rating
        // on and identifies the show by scraping the folder name instead - so a
        // folder it reads differently becomes a second, half-empty series beside
        // the real one.
        if (SearchAsTv && _currentShow is { } tvShow)
        {
            foreach (var folder in ShowFoldersOf(applied))
            {
                try
                {
                    var result = NfoWriter.WriteShow(folder, tvShow, _settings);

                    switch (result.Outcome)
                    {
                        case NfoWriter.Outcome.Written: written++; break;
                        case NfoWriter.Outcome.Updated: refreshed++; break;
                        case NfoWriter.Outcome.Failed:
                            failed++;
                            WriteLog($"nfo: tvshow.nfo - {result.Detail}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Error($"tvshow.nfo in {folder}", ex);
                }
            }
        }

        if (written + refreshed + failed == 0) return;

        var bits = new List<string>();
        if (written > 0) bits.Add($"{written} written");
        if (refreshed > 0) bits.Add($"{refreshed} refreshed");
        if (failed > 0) bits.Add($"{failed} failed");

        WriteLog("nfo: " + string.Join(", ", bits) + ".");
    }

    /// <summary>
    /// The folders that should hold a tvshow.nfo, worked out from where the
    /// episodes actually landed rather than from the plan.
    ///
    /// Taken from the results because that is what survives every arrangement:
    /// a season folder means the show folder is its parent, a flat show folder
    /// means the file's own folder is it, and a combined entry writes several
    /// shows' worth of episodes in one pass. Reading the plan's show folder
    /// instead was right in the ordinary case and wrong in all three of those.
    /// </summary>
    private IEnumerable<string> ShowFoldersOf(List<PlannedAction> applied)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in applied)
        {
            if (action.TargetPath is not { } target) continue;
            if (ShowFolderFor(target) is { } show) folders.Add(show);
        }

        return folders;
    }

    /// <summary>
    /// The show folder holding one episode, or null where there isn't a safe
    /// one.
    ///
    /// Public because the artwork picker needs the same answer: a series poster
    /// goes here, and getting it wrong by one level puts it in the library root
    /// where every show would pick it up.
    /// </summary>
    public string? ShowFolderFor(string episodePath)
    {
        if (Path.GetDirectoryName(episodePath) is not { } dir) return null;

        // A season folder is not the show. Its parent is.
        var isSeason = PyreMedia.Core.Naming.EpisodeMatcher.ParseSeasonFolder(
            Path.GetFileName(dir), _settings.SeasonFolderName) is not null;

        var show = isSeason ? Path.GetDirectoryName(dir) : dir;

        // Never the scan root itself. A loose file that stayed put would put one
        // tvshow.nfo - and now one poster - over a folder holding every show
        // there is, and Kodi would read the whole staging area as a single
        // series.
        if (string.IsNullOrEmpty(show)) return null;
        if (MediaScanner.ScanRoots(_settings).Any(r => MediaScanner.SameFolder(r, show))) return null;

        return show;
    }

    /// <summary>
    /// Artwork where Kodi looks for it. Off unless asked for: it reaches the
    /// network and writes files that have nothing to do with renaming.
    ///
    /// A film is one thing with one poster, so the poster goes beside it.
    ///
    /// A series is not. It has one poster for the whole run and a different
    /// picture for every episode, and those go in different places: the poster
    /// into the show's folder next to tvshow.nfo, and each episode's own frame
    /// beside that episode as "-thumb.jpg". This used to write the show's poster
    /// beside every episode, which gave a season of identical thumbnails in the
    /// one place a picture was meant to tell them apart.
    /// </summary>
    private async Task WriteArtworkAsync(List<PlannedAction> applied)
    {
        if (!_settings.DownloadArtwork) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var written = 0;
        var failed = 0;
        var noPicture = 0;

        async Task Fetch(string path, ArtKind kind, string? url, string what)
        {
            if (string.IsNullOrWhiteSpace(url)) { noPicture++; return; }

            try
            {
                var r = await ArtworkWriter.WriteToAsync(http, path, kind, url, _settings);

                switch (r.Outcome)
                {
                    case ArtworkWriter.Outcome.Written: written++; break;
                    case ArtworkWriter.Outcome.Failed:
                        failed++;
                        WriteLog($"artwork: {what} - {r.Detail}");
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Error($"artwork for {path}", ex);
            }
        }

        if (!SearchAsTv)
        {
            var poster = SelectedResult?.MovieResult?.PosterUrl;

            foreach (var action in applied)
            {
                if (action.TargetPath is not { } target || !File.Exists(target)) continue;

                await Fetch(ArtworkWriter.PathFor(target, ArtKind.Poster),
                            ArtKind.Poster, poster, Path.GetFileName(target));
            }
        }
        else
        {
            // The series poster: once, in the show's folder, beside tvshow.nfo.
            var showPoster = SelectedResult?.Show?.PosterUrl;

            // Which seasons this run actually touched. A season's poster is
            // worth writing only for a season that is here - fetching artwork
            // for eight seasons because the show has eight would download seven
            // images for episodes nobody owns.
            var seasons = applied
                .Select(a => a.Episode?.SeasonNumber)
                .Where(s => s is not null)
                .Select(s => s!.Value)
                .Distinct()
                .ToList();

            foreach (var folder in ShowFoldersOf(applied))
            {
                await Fetch(ArtworkWriter.FolderPathFor(folder, ArtKind.Poster),
                            ArtKind.Poster, showPoster, Path.GetFileName(folder) + " poster");

                // Season posters go in the show folder too, which is the part
                // that catches people out - "season01-poster.jpg" beside
                // tvshow.nfo, not inside the season folder.
                foreach (var number in seasons)
                {
                    var art = _currentShow?.Seasons.FirstOrDefault(s => s.Number == number)?.PosterUrl;

                    await Fetch(ArtworkWriter.SeasonPosterPath(folder, number),
                                ArtKind.Poster, art, $"season {number} poster");
                }
            }

            // Then each episode's own frame beside the episode itself.
            foreach (var action in applied)
            {
                if (action.TargetPath is not { } target || !File.Exists(target)) continue;

                // The episode is already on the action, carrying its own frame.
                await Fetch(ArtworkWriter.PathFor(target, ArtKind.EpisodeThumb),
                            ArtKind.EpisodeThumb, action.Episode?.StillUrl,
                            Path.GetFileName(target));
            }
        }

        if (written + failed + noPicture == 0) return;

        // Counted separately and said plainly. "Nothing happened" reads as a
        // failure, and an episode nobody has photographed is not one - older
        // shows have no stills at all, and that is worth knowing rather than
        // wondering about.
        WriteLog($"artwork: {written} written"
                 + (failed > 0 ? $", {failed} failed" : "")
                 + (noPicture > 0 ? $", {noPicture} had no picture at the source" : "")
                 + ".");
    }


    /// <summary>Files were renamed, so the item's cached file list is stale.</summary>
    private void RefreshSelectedItemFiles()
    {
        var item = SelectedItem;
        if (item is null) return;

        try
        {
            var dir = item.FolderRenamedTo is not null
                ? Path.Combine(Path.GetDirectoryName(item.Media.Path.TrimEnd(Path.DirectorySeparatorChar))!,
                               item.FolderRenamedTo)
                : item.Media.Path;

            if (!Directory.Exists(dir)) return;

            // Same list the scan used, or a disc image would drop out of the
            // item the moment its files were re-read.
            var exts = _settings.ScannedExtensions;
            var subs = _settings.SubtitleExtensions;

            item.Media.Files.Clear();
            item.Media.Files.AddRange(
                Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                         .Where(f =>
                         {
                             var e = Path.GetExtension(f).ToLowerInvariant();
                             return exts.Contains(e) && !subs.Contains(e);
                         }));
        }
        catch (Exception)
        {
            // Non-fatal: the next scan will sort it out.
        }
    }

    [RelayCommand]
    private void NextItem()
    {
        if (Items.Count == 0) return;

        var visible = ItemsView.Cast<LibraryItem>().ToList();
        if (visible.Count == 0) return;

        var start = SelectedItem is null ? -1 : visible.IndexOf(SelectedItem);

        for (var i = 1; i <= visible.Count; i++)
        {
            var candidate = visible[(start + i) % visible.Count];
            if (!candidate.IsDone) { SelectedItem = candidate; return; }
        }

        SetBanner("Everything in this folder is organised.", false);
    }

    // ---------------- Misc ----------------

    public void ReloadServices()
    {
        _metadata.Dispose();
        _metadata = new MetadataService(_settings);
        _scanner = new MediaScanner(_settings);
        _planner = new MediaPlanner(_settings);
        _executor = new RenameExecutor(_settings, _history);
        _settings.Save();

        OnPropertyChanged(nameof(HasFolders));
        ScanCommand.NotifyCanExecuteChanged();
        WriteLog("Settings saved.");
    }

    /// <summary>
    /// Point out entries that look like seasons of one show. Reported, never
    /// acted on - two series can share a name, and the ones that do are exactly
    /// the ones where guessing would be expensive.
    /// </summary>
    /// <summary>
    /// Folders holding nothing but release junk - a release folder whose episodes
    /// have already been gathered elsewhere. They never show up in the library,
    /// since a folder with no video isn't an entry, so without this they simply
    /// accumulate out of sight.
    /// </summary>
    public List<string> LeftoverFolders { get; } = [];

    public bool HasLeftovers => LeftoverFolders.Count > 0;

    public string LeftoverLabel =>
        LeftoverFolders.Count == 1 ? "Clear 1 empty folder" : $"Clear {LeftoverFolders.Count} empty folders";

    private void FindLeftovers()
    {
        LeftoverFolders.Clear();

        try { LeftoverFolders.AddRange(_scanner.FindLeftoverFolders()); }
        catch (Exception ex) { AppLog.Error("Leftover scan", ex); }

        OnPropertyChanged(nameof(HasLeftovers));
        OnPropertyChanged(nameof(LeftoverLabel));

        if (LeftoverFolders.Count == 0) return;

        WriteLog($"{LeftoverFolders.Count} folder(s) hold nothing but release leftovers - no video, "
                 + "no metadata, nothing to lose. Use \"" + LeftoverLabel + "\" to be rid of them:");

        foreach (var f in LeftoverFolders.Take(20))
            WriteLog("   " + Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar)));

        if (LeftoverFolders.Count > 20) WriteLog($"   ...and {LeftoverFolders.Count - 20} more");
    }

    /// <summary>
    /// Remove the leftover folders, contents and all. Everything in them is on the
    /// junk list and the files go to the Recycle Bin where the drive has one, so
    /// this is recoverable - but it is still a deletion, so the caller confirms
    /// first and the log names every folder beforehand.
    /// </summary>
    [RelayCommand]
    private void ClearLeftovers()
    {
        if (LeftoverFolders.Count == 0) return;

        var removed = 0;
        var failed = new List<string>();

        foreach (var dir in LeftoverFolders.ToList())
        {
            try
            {
                // Through the Recycle Bin where the drive has one, so a folder
                // cleared by mistake is still recoverable.
                if (_settings.DeleteToRecycleBin)
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            f,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }

                Directory.Delete(dir, true);
                removed++;
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))}: {ex.Message}");
                AppLog.Error($"Clear leftover {dir}", ex);
            }
        }

        WriteLog($"Cleared {removed} leftover folder(s).");
        foreach (var f in failed) WriteLog("   could not remove " + f);

        SetBanner(failed.Count == 0
            ? $"Cleared {removed} leftover folder(s)."
            : $"Cleared {removed}; {failed.Count} could not be removed - see the log.", failed.Count > 0);

        LeftoverFolders.Clear();
        OnPropertyChanged(nameof(HasLeftovers));
        OnPropertyChanged(nameof(LeftoverLabel));
    }

    /// <summary>
    /// Fold a show split across folders into one row, and say so.
    ///
    /// Eight folders of Will &amp; Grace are one show and should be matched once.
    /// Where there is any doubt that they're the same series the folders are left
    /// alone and the doubt is stated - that is the whole of the caution around
    /// Dexter, Battlestar Galactica and Star Trek.
    /// </summary>
    private void ReportShowGroups()
    {
        var media = Items.Select(i => i.Media).ToList();
        var all = ShowGrouper.Group(media);
        if (all.Count == 0) return;

        var held = all.Where(g => g.Caution is not null).ToList();

        foreach (var g in held)
        {
            WriteLog($"Left separate: {g.Describe()}. {g.Caution}");
        }

        if (!_settings.CombineSplitSeasons)
        {
            foreach (var g in all.Where(g => g.Caution is null))
                WriteLog($"Looks like one show split across folders: {g.Describe()}. "
                         + "Combining is switched off in Settings.");
            return;
        }

        var merged = ShowGrouper.Combine(media, out var combined);
        if (combined.Count == 0)
        {
            if (held.Count > 0)
                SetBanner($"{held.Sum(g => g.Items.Count)} folders share a title but were left "
                          + "separate - see the log.", false);
            return;
        }

        // Rebuild the list around the combined entries. Selection is dropped
        // because the rows it pointed at no longer exist.
        Items.Clear();
        foreach (var m in merged) Items.Add(new LibraryItem(m));

        foreach (var g in combined)
            WriteLog($"Combined into one entry: {g.Describe()}, {g.FileCount} file(s). "
                     + "Match it once and every season is named together.");

        var folders = combined.Sum(g => g.Items.Count);

        OnPropertyChanged(nameof(ItemsHeader));
        RefreshView();

        var tv = Items.Count(i => i.IsTv);
        StatusText = $"{Items.Count} item(s): {tv} TV, {Items.Count - tv} movie(s)";

        SetBanner(
            combined.Count == 1
                ? $"{folders} folders combined into one entry - {combined[0].Describe()}. "
                  + "Match it once."
                : $"{folders} folders combined into {combined.Count} entries. See the log."
                  + (held.Count > 0 ? $" {held.Count} other group(s) left separate." : ""),
            false);
    }

    private void SetBanner(string text, bool isError)
    {
        BannerIsError = isError;
        BannerText = text;
    }

    public void WriteLog(string message)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 500) Log.RemoveAt(0);

        // Mirror to disk: the in-app log is capped and lost on exit, and an error
        // is far easier to diagnose with the run-up to it recorded.
        AppLog.Info(message);
    }
}
