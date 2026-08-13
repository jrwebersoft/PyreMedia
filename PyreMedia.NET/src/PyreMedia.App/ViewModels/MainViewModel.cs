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

        if (HasFolders)
            _ = ScanAsync();
        else
            WriteLog("Welcome. Add a folder to get started.");
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
        }
        finally
        {
            _suppressSelectionWork = false;
        }

        // Restore only if the item survived the filter.
        if (keep is not null && ItemsView.Cast<LibraryItem>().Contains(keep))
            SelectedItem = keep;
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

    [RelayCommand]
    private void AddFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Contains() is an exact, case-sensitive match, so "h:\done" alongside
        // "H:\Done" - or the same path with a trailing slash - both got in, and
        // the folder was then scanned once per entry.
        var already = MediaScanner.ScanRoots(_settings)
            .Any(r => MediaScanner.SameFolder(r, path));

        if (already)
        {
            WriteLog($"Already watching that folder: {path}");
            return;
        }

        Folders.Add(path);
        _settings.Save();

        OnPropertyChanged(nameof(HasFolders));
        ScanCommand.NotifyCanExecuteChanged();

        WriteLog($"Added folder: {path}");
        _ = ScanAsync();
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
    [ObservableProperty] public partial bool AutoAdvance { get; set; } = true;
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

            if (SearchAsTv)
            {
                foreach (var r in await _metadata.SearchAsync(SearchTerm, year, ct))
                    SearchResults.Add(new SearchResultItem(r));
            }
            else
            {
                foreach (var r in await _metadata.SearchMoviesAsync(SearchTerm, year, ct))
                    SearchResults.Add(new SearchResultItem(r));
            }

            if (SearchResults.Count == 0)
            {
                StatusText = "No matches";
                SetBanner($"Nothing found for \"{SearchTerm}\". Try a shorter or corrected term.", true);
                return;
            }

            StatusText = $"{SearchResults.Count} match(es)";
            autoPick = PickConfidentMatch();
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
        if (autoPick is not null)
        {
            WriteLog($"Auto-selected '{autoPick.Display}'.");
            SelectedResult = autoPick;
            if (Preview.Count == 0 && !IsComplete)
                await BuildPreviewAsync();
        }
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
        if (SelectedResult is null || item is null) return;

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
                if (SelectedResult.Show is null) return;

                if (_currentShow is null || _currentShow.Id != SelectedResult.Show.TvdbId)
                {
                    _currentShow = await _metadata.GetShowAsync(
                        SelectedResult.Show, new Progress<string>(WriteLog), ct);

                    if (_currentShow is not null)
                    {
                        var n = _currentShow.Seasons.Sum(s => s.Episodes.Count);
                        WriteLog($"'{_currentShow.Name}': {_currentShow.Seasons.Count} season(s), {n} episode(s).");
                    }
                }

                if (_currentShow is null)
                {
                    StatusText = "No episode data";
                    SetBanner($"No episode data for '{SelectedResult.Display}'.", true);
                    return;
                }

                item.MatchedName = _currentShow.Name;
            }
            else
            {
                if (SelectedResult.MovieResult is null) return;

                _currentMovie = await _metadata.GetMovieAsync(SelectedResult.MovieResult.TmdbId, ct);
                if (_currentMovie is null)
                {
                    StatusText = "No movie data";
                    SetBanner($"No details for '{SelectedResult.Display}'.", true);
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

        EmptyPreviewText = Preview.Count == 0 && !IsComplete
            ? "No video files found here. Check the allowed file types in Settings."
            : null;

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
        if (_plan.DeleteCount > 0) parts.Add($"{_plan.DeleteCount} to delete");

        var overwrites = Preview.Count(p => p.IsSelected && p.WillOverwrite);
        if (overwrites > 0) parts.Add($"{overwrites} will replace an existing file");

        PreviewSummary = string.Join("   |   ", parts);
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

            // Re-scan this item from disk so the plan reflects reality.
            RefreshSelectedItemFiles();
            RebuildPlan();

            var clean = result.Failed == 0 && Preview.Count == 0;
            if (clean) item.IsDone = true;
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

        if (again is not null && !again.IsDone)
            SelectedItem = again;
    }

    /// <summary>
    /// Write a Kodi sidecar beside each file that just landed.
    ///
    /// Runs after the rename so the .nfo takes the file's final name - Kodi
    /// pairs them by filename, and writing first would leave the sidecar
    /// orphaned the moment the video moved.
    /// </summary>
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
            if (Path.GetDirectoryName(target) is not { } dir) continue;

            // A season folder is not the show. Its parent is.
            var isSeason = PyreMedia.Core.Naming.EpisodeMatcher.ParseSeasonFolder(
                Path.GetFileName(dir), _settings.SeasonFolderName) is not null;

            var show = isSeason ? Path.GetDirectoryName(dir) : dir;

            // Never the scan root itself. A loose file that stayed put would put
            // one tvshow.nfo over a folder holding every show there is, and Kodi
            // would read the whole staging area as a single series.
            if (string.IsNullOrEmpty(show)) continue;
            if (MediaScanner.ScanRoots(_settings).Any(r => MediaScanner.SameFolder(r, show))) continue;

            folders.Add(show);
        }

        return folders;
    }

    /// <summary>
    /// Poster and fanart beside each renamed file, where Kodi looks for them.
    /// Off unless asked for: it reaches the network and writes files that have
    /// nothing to do with renaming.
    /// </summary>
    private async Task WriteArtworkAsync(List<PlannedAction> applied)
    {
        if (!_settings.DownloadArtwork) return;

        var poster = SearchAsTv ? SelectedResult?.Show?.PosterUrl : SelectedResult?.MovieResult?.PosterUrl;
        if (string.IsNullOrWhiteSpace(poster)) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var written = 0;
        var failed = 0;

        // One image per file. For a season that means the same poster beside
        // every episode, which is what Kodi expects of the sidecar form.
        foreach (var action in applied)
        {
            if (action.TargetPath is not { } target || !File.Exists(target)) continue;

            try
            {
                var r = await ArtworkWriter.WriteAsync(http, target, ArtKind.Poster, poster, _settings);

                switch (r.Outcome)
                {
                    case ArtworkWriter.Outcome.Written: written++; break;
                    case ArtworkWriter.Outcome.Failed:
                        failed++;
                        WriteLog($"artwork: {Path.GetFileName(target)} - {r.Detail}");
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Error($"artwork for {target}", ex);
            }
        }

        if (written + failed == 0) return;

        WriteLog($"artwork: {written} written"
                 + (failed > 0 ? $", {failed} failed." : "."));
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

            var exts = _settings.VideoExtensions;
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
