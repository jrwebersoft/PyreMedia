using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core;
using PyreMedia.Core.Models;
using PyreMedia.Core.Naming;
using PyreMedia.Core.Organizing;

namespace PyreMedia.App.Views;

/// <summary>One episode as it appears in the per-row dropdown.</summary>
public sealed class EpisodeChoice(Episode episode)
{
    public Episode Episode { get; } = episode;

    public override string ToString() =>
        $"{Episode.SeasonNumber}x{Episode.Number:00}  {Episode.Name}";
}

/// <summary>One file awaiting a number.</summary>
public partial class MapRow : ObservableObject
{
    public required string File { get; init; }
    public required string FileName { get; init; }
    public required string ExtractedTitle { get; init; }

    /// <summary>What the filename itself says, so Reset can go back to it.</summary>
    public EpisodeChoice? Parsed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewName))]
    public partial EpisodeChoice? Chosen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewName))]
    public partial bool Include { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfidenceLabel))]
    [NotifyPropertyChangedFor(nameof(ConfidenceBrush))]
    public partial double Confidence { get; set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlternativeLabel))]
    public partial string? Alternative { get; set; }

    /// <summary>Supplied by the window so the row can render its own result.</summary>
    public Func<Episode, string>? NameBuilder { get; set; }

    public string NewName
    {
        get
        {
            if (!Include) return "(left alone)";
            if (Chosen is null) return "(no episode chosen)";

            var built = NameBuilder?.Invoke(Chosen.Episode) ?? Chosen.Episode.Name;
            return string.Equals(built, FileName, StringComparison.OrdinalIgnoreCase)
                ? "(already correct)"
                : built;
        }
    }

    public string ConfidenceLabel => Confidence switch
    {
        < 0 => "",
        >= 0.85 => "Strong",
        >= 0.60 => "Likely",
        >= 0.35 => "Weak - check",
        _ => "Guess - check"
    };

    public Brush ConfidenceBrush => Confidence switch
    {
        < 0 => Brushes.Gray,
        >= 0.85 => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
        >= 0.60 => new SolidColorBrush(Color.FromRgb(0x8B, 0xC3, 0x4A)),
        >= 0.35 => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)),
        _ => new SolidColorBrush(Color.FromRgb(0xFF, 0x70, 0x43))
    };

    public string? AlternativeLabel =>
        Alternative is null ? null : $"Runner-up: {Alternative}";

    internal void RaiseNewName() => OnPropertyChanged(nameof(NewName));
}

public partial class EpisodeMapWindow
{
    private readonly PyreMediaSettings _settings;
    private readonly MediaItem _item;
    private readonly Func<EpisodeSource, CancellationToken, Task<TvShow?>> _refetch;

    /// <summary>
    /// What was made of a folder of disc titles, when this item is one. Null for
    /// an ordinary folder, where the filenames carry their own numbering.
    /// </summary>
    private readonly PyreMedia.Core.Organizing.RipReading? _rip;

    private TvShow _show;
    private bool _loading;

    public ObservableCollection<MapRow> Rows { get; } = [];
    public ObservableCollection<EpisodeChoice> AllEpisodes { get; } = [];

    /// <summary>Filled in when the user accepts. Null means they cancelled.</summary>
    public EpisodeOverrides? Result { get; private set; }

    /// <summary>The source they settled on, so the caller can keep using it.</summary>
    public EpisodeSource ChosenSource { get; private set; }

    /// <summary>
    /// The show data actually on screen. Switching source replaces it, and the
    /// caller must plan against the same episode list the user just mapped to.
    /// </summary>
    public TvShow CurrentShow => _show;

    public EpisodeMapWindow(
        PyreMediaSettings settings,
        MediaItem item,
        TvShow show,
        EpisodeSource currentSource,
        Func<EpisodeSource, CancellationToken, Task<TvShow?>> refetch,
        PyreMedia.Core.Organizing.RipReading? rip = null)
    {
        InitializeComponent();
        DataContext = this;

        _settings = settings;
        _item = item;
        _show = show;
        _refetch = refetch;
        _rip = rip;
        ChosenSource = currentSource;

        // Through a view, so one season can be worked on at a time without the
        // other rows being lost - they are still there, just not in the way.
        _view = new System.Windows.Data.ListCollectionView(Rows) { Filter = InSelectedSeason };
        Grid.ItemsSource = _view;

        _loading = true;
        CmbSource.ItemsSource = new[]
        {
            "TheTVDB", "TMDb", "TVmaze", "Both merged"
        };
        CmbSource.SelectedIndex = currentSource switch
        {
            EpisodeSource.TheTvdbLegacy => 0,
            EpisodeSource.Tmdb => 1,
            EpisodeSource.TvMaze => 2,
            _ => 3
        };
        _loading = false;

        Rebuild();
    }

    private static EpisodeSource SourceFromIndex(int i) => i switch
    {
        0 => EpisodeSource.TheTvdbLegacy,
        1 => EpisodeSource.Tmdb,
        2 => EpisodeSource.TvMaze,
        _ => EpisodeSource.Merged
    };

    /// <summary>Rebuild the episode list and the rows from the current show data.</summary>
    private void Rebuild()
    {
        AllEpisodes.Clear();
        foreach (var e in _show.Seasons.OrderBy(s => s.Number)
                     .SelectMany(s => s.Episodes.OrderBy(x => x.Number)))
        {
            AllEpisodes.Add(new EpisodeChoice(e));
        }

        var existing = Rows.ToDictionary(r => r.File, StringComparer.OrdinalIgnoreCase);

        foreach (var r in Rows) r.PropertyChanged -= OnRowEdited;
        Rows.Clear();

        foreach (var file in _item.Files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            var parsedRef = EpisodeMatcher.Parse(fileName);

            EpisodeChoice? parsed = null;

            // Where the row starts, which is not the same thing as what the
            // filename says.
            //
            // A saved shift for this season is already applied everywhere else,
            // so building these rows from the raw parse made this screen
            // disagree with the preview behind it by exactly that shift. Worse,
            // Accept then measured chosen-against-parsed, found no difference,
            // and recorded "shifted by 0" - which deletes the entry. Opening
            // Renumber and pressing Accept destroyed the setting the screen
            // exists to edit.
            EpisodeChoice? seeded = null;

            if (parsedRef is { } p && p.Season is { } se)
            {
                parsed = Find(se, p.Episode);

                var shift = se > 0 ? _settings.EpisodeOffset(_show.Id, se) : 0;
                seeded = shift == 0 ? parsed : Find(se, p.Episode + shift) ?? parsed;
            }

            var row = new MapRow
            {
                File = file,
                FileName = fileName,
                ExtractedTitle = TitleMatcher.ExtractTitle(fileName, _show.Name),
                Parsed = parsed,
                NameBuilder = BuildName
            };

            // Switching source shouldn't discard hand-made choices - re-point
            // them at the equivalent episode in the new list.
            if (existing.TryGetValue(file, out var old))
            {
                row.Include = old.Include;
                row.Confidence = old.Confidence;

                if (old.Chosen is { } oc)
                    row.Chosen = Find(oc.Episode.SeasonNumber, oc.Episode.Number) ?? seeded;
                else
                    row.Chosen = seeded;
            }
            else
            {
                row.Chosen = seeded;
            }

            row.PropertyChanged += OnRowEdited;
            Rows.Add(row);
        }

        ApplyRipReading();

        FillSeasons();
        UpdateSummary();
    }

    /// <summary>
    /// Look at the other discs ripped beside this one.
    ///
    /// Only file counts, which cost a directory listing each - no probing, no
    /// network. That is the point: where the sum comes out exactly right the
    /// arithmetic is unarguable and nothing else needs doing.
    /// </summary>
    private PyreMedia.Core.Organizing.SetPlacement CountTheSet(int episodesInSeason)
    {
        try
        {
            var parent = Directory.GetParent(_item.Path)?.FullName;
            if (parent is null) return new PyreMedia.Core.Organizing.SetPlacement();

            var folders = new List<PyreMedia.Core.Organizing.DiscFolder>();

            foreach (var dir in Directory.EnumerateDirectories(parent))
            {
                var ripped = Directory.EnumerateFiles(dir)
                    .Where(PyreMedia.Core.Organizing.DiscRip.IsRipped)
                    .ToList();

                if (ripped.Count < 2) continue;

                folders.Add(new PyreMedia.Core.Organizing.DiscFolder(
                    dir,
                    ripped.Count,
                    PyreMedia.Core.Organizing.DiscSet.NumberIn(Path.GetFileName(dir)),
                    ripped.Min(f => File.GetLastWriteTimeUtc(f))));
            }

            return PyreMedia.Core.Organizing.DiscSet.Place(folders, _item.Path, episodesInSeason);
        }
        catch (Exception)
        {
            // An unreadable sibling folder is not worth failing over - the
            // runtimes are asked next either way.
            return new PyreMedia.Core.Organizing.SetPlacement();
        }
    }

    /// <summary>
    /// Number the rows from a disc reading, where the filenames cannot.
    ///
    /// "title_t00_new.mkv" parses to nothing, so without this every row on a
    /// disc rip opens empty and all nine have to be set by hand. The reading
    /// has already decided which titles are episodes and what order the disc
    /// puts them in; this lays that over the episode list.
    ///
    /// The order is the disc's, which is almost always broadcast order and is
    /// promised nowhere - so the caution above the grid says so, and every row
    /// remains a dropdown.
    /// </summary>
    private void ApplyRipReading()
    {
        if (_rip is null || Rows.Count == 0) return;

        var byFile = Rows.ToDictionary(r => r.File, StringComparer.OrdinalIgnoreCase);

        // Anything the reading set aside starts unticked, with its reason where
        // the row would otherwise offer an alternative.
        foreach (var aside in _rip.Ignored)
        {
            if (!byFile.TryGetValue(aside.Title.Path, out var row)) continue;

            row.Include = false;
            row.Alternative = aside.Why;
        }

        // A catalogued disc names each title outright, so there is nothing to
        // infer: title five is whichever episode somebody who ripped this same
        // disc recorded finding at title five.
        if (_rip.Named is { Count: > 0 } named)
        {
            var placed = 0;

            foreach (var title in _rip.Episodes)
            {
                if (!named.TryGetValue(title.Index, out var entry)) continue;
                if (!byFile.TryGetValue(title.Path, out var row)) continue;

                if (Find(entry.Season, entry.Episode) is not { } choice) continue;

                row.Chosen = choice;
                row.Include = true;
                placed++;
            }

            // Only trust it wholesale if it actually covered the disc. A partial
            // match leaves the rest to the order below rather than half-filling
            // the grid and calling it done.
            if (placed >= _rip.Episodes.Count)
            {
                RipCaution.Text = _rip.Caution ?? "";
                RipCautionBox.Visibility = string.IsNullOrWhiteSpace(_rip.Caution)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                return;
            }
        }

        // The season being worked on, so a disc from the middle of a run lands
        // on the right one rather than always on season one.
        var season = _show.Seasons.Where(x => !x.IsSpecials)
            .OrderBy(x => x.Number)
            .FirstOrDefault();

        if (season is null) return;

        var episodes = season.Episodes.OrderBy(e => e.Number).ToList();

        // Disc order gives the sequence and not where it starts. Rip disc three
        // of four and numbering from episode one is wrong by a constant nobody
        // notices until they try to watch in order.
        //
        // Two ways to find that offset, cheapest first. Counting the whole set
        // needs nothing but the number of files in each sibling folder: if five
        // discs hold exactly the season's twenty-one episodes between them, the
        // third starts at episode nine and no measurement can improve on it.
        // Only when the counting refuses - a title too many, discs missing, one
        // folder on its own - are the runtimes asked instead.
        var counted = CountTheSet(episodes.Count);
        var from = 0;
        var didPlace = false;
        string? placementNote = counted.Note;

        if (counted.Certain)
        {
            from = counted.Offset;
            didPlace = true;
        }
        else
        {
            var byRuntime = PyreMedia.Core.Organizing.DiscPlacement.Find(episodes, _rip.Episodes);

            if (byRuntime.Certain)
            {
                from = episodes.FindIndex(e => e.Number == byRuntime.StartsAt!.Number);
                didPlace = from >= 0;
                placementNote = byRuntime.Note;
            }
            else if (byRuntime.Note is { Length: > 0 })
            {
                // Both declined. Say the counting reason, which is the more
                // concrete of the two, and leave it there.
                placementNote ??= byRuntime.Note;
            }
        }

        if (from < 0 || from >= episodes.Count) { from = 0; didPlace = false; }

        var next = from;

        foreach (var title in _rip.Episodes)
        {
            if (next >= episodes.Count) break;
            if (!byFile.TryGetValue(title.Path, out var row)) continue;

            row.Chosen = Find(episodes[next].SeasonNumber, episodes[next].Number);

            // Ticked only when something actually placed the disc. Where nothing
            // could - not catalogued, one disc with no set to count against, and
            // every episode the same length - the numbering below is the disc's
            // own order starting from episode one, which is a guess wearing the
            // clothes of an answer. Left unticked, so Apply does nothing until
            // somebody has decided it is right.
            row.Include = didPlace;
            next++;
        }

        if (!didPlace)
        {
            placementNote = "Nothing here could tell which episodes these are. "
                          + (placementNote ?? "")
                          + " The order below is the disc's own, which is usually right; the "
                          + "starting episode is not known. Set the first row, then use Offset to "
                          + "move the rest with it - and tick the rows once they read correctly. "
                          + "Play will show you a title if you are unsure.";
        }

        // Whatever the runtimes did or did not settle is worth saying either
        // way: placed at episode six is news, and "these could be any of three
        // discs" is the reason to check before applying.
        if (placementNote is { Length: > 0 } note)
            _rip.Caution = string.IsNullOrWhiteSpace(_rip.Caution) ? note : note + " " + _rip.Caution;

        RipCaution.Text = _rip.Caution ?? "";
        RipCautionBox.Visibility = string.IsNullOrWhiteSpace(_rip.Caution)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private EpisodeChoice? Find(int season, int number) =>
        AllEpisodes.FirstOrDefault(c =>
            c.Episode.SeasonNumber == season && c.Episode.Number == number);

    private string BuildName(Episode e)
        => NameFormatter.BuildEpisodeFileName(
               _settings.TvFileFormat, _show.Name, e,
               _settings.SeasonNumZeroPadding, _settings.EpisodeNumZeroPadding,
               _settings.FilenameReplaceChar)
           + Path.GetExtension(_item.Files.FirstOrDefault() ?? ".mkv");

    /// <summary>
    /// Recount after a row is edited by hand.
    ///
    /// Nothing listened to these. UpdateSummary owns the "N assigned twice"
    /// warning and the OK button, and it ran only on a rebuild or a bulk tool -
    /// so picking an episode in a row's own dropdown, which is the entire point
    /// of the screen, left both stale. Two files could be pointed at one episode
    /// with no complaint and OK still enabled.
    /// </summary>
    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSummary();

    private void OnRowEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapRow.Chosen) or nameof(MapRow.Include))
            UpdateSummary();
    }

    private void UpdateSummary()
    {
        var used = Rows.Count(r => r.Include && r.Chosen is not null);
        var missing = Rows.Count(r => r.Include && r.Chosen is null);
        var excluded = Rows.Count(r => !r.Include);

        var parts = new List<string>
        {
            $"{Rows.Count} file(s)",
            $"{AllEpisodes.Count} episode(s) from {CmbSource.SelectedItem}"
        };

        if (used > 0) parts.Add($"{used} numbered");
        if (missing > 0) parts.Add($"{missing} still need an episode");
        if (excluded > 0) parts.Add($"{excluded} excluded");

        // A duplicate mapping silently loses a file - two rows can't both
        // become the same name.
        var dupes = Rows
            .Where(r => r.Include && r.Chosen is not null)
            .GroupBy(r => (r.Chosen!.Episode.SeasonNumber, r.Chosen.Episode.Number))
            .Count(g => g.Count() > 1);

        if (dupes > 0) parts.Add($"{dupes} episode(s) assigned twice");

        // Which rows the buttons will touch, when that is not all of them.
        var picked = Grid.SelectedItems.OfType<MapRow>().Count();
        if (picked > 1) parts.Add($"tools act on the {picked} selected");

        TxtSummary.Text = string.Join("   |   ", parts);
        TxtShow.Text = _show.Name;
        BtnOk.IsEnabled = used > 0 && dupes == 0;
    }

    // ---------------- Source ----------------

    private async void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbSource.SelectedIndex < 0) return;

        var source = SourceFromIndex(CmbSource.SelectedIndex);
        if (source == ChosenSource) return;

        Spinner.Visibility = Visibility.Visible;
        CmbSource.IsEnabled = false;

        try
        {
            var fresh = await _refetch(source, CancellationToken.None);
            if (fresh is null)
            {
                MessageBox.Show(this,
                    $"{CmbSource.SelectedItem} returned no episode data for this show.",
                    "Renumber", MessageBoxButton.OK, MessageBoxImage.Warning);

                _loading = true;
                CmbSource.SelectedIndex = ChosenSource switch
                {
                    EpisodeSource.TheTvdbLegacy => 0,
                    EpisodeSource.Tmdb => 1,
                    EpisodeSource.TvMaze => 2,
                    _ => 3
                };
                _loading = false;
                return;
            }

            _show = fresh;
            ChosenSource = source;
            Rebuild();
        }
        catch (Exception ex)
        {
            AppLog.Error("EpisodeMap source switch", ex);
            MessageBox.Show(this, ex.Message, "Renumber",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Spinner.Visibility = Visibility.Collapsed;
            CmbSource.IsEnabled = true;
        }
    }

    // ---------------- Season chips ----------------

    /// <summary>One detected season, on or off.</summary>
    public sealed class SeasonChip(int number) : System.ComponentModel.INotifyPropertyChanged
    {
        public int Number { get; } = number;
        public string Label { get; } = number == 0 ? "Specials" : $"Season {number}";

        private bool _isOn = true;
        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value) return;
                _isOn = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsOn)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public ObservableCollection<SeasonChip> Seasons { get; } = [];

    private System.Windows.Data.ListCollectionView? _view;

    private bool InSelectedSeason(object o)
    {
        if (o is not MapRow r) return true;

        // Nothing switched off yet, or a row whose season can't be told - show it
        // rather than hide a file the user then can't find.
        if (Seasons.Count == 0) return true;
        if (SeasonOf(r) is not { } season) return true;

        var chip = Seasons.FirstOrDefault(c => c.Number == season);
        return chip is null || chip.IsOn;
    }

    /// <summary>
    /// The season a row belongs to - what it has been assigned to if anything,
    /// otherwise what its filename says. A file moved into another season follows
    /// the assignment, which is what keeps it findable afterwards.
    /// </summary>
    private static int? SeasonOf(MapRow r) =>
        r.Chosen?.Episode.SeasonNumber ?? r.Parsed?.Episode.SeasonNumber;

    /// <summary>
    /// Rebuild the chips from the seasons actually present, keeping whatever was
    /// switched off. Called after anything that can move a file between seasons.
    /// </summary>
    private void FillSeasons()
    {
        var present = Rows.Select(SeasonOf).OfType<int>().Distinct().Order().ToList();
        var off = Seasons.Where(c => !c.IsOn).Select(c => c.Number).ToHashSet();

        Seasons.Clear();
        foreach (var n in present)
            Seasons.Add(new SeasonChip(n) { IsOn = !off.Contains(n) });

        // Nothing to choose between when there is only one.
        SeasonChips.Visibility = present.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        LblSeasons.Visibility = SeasonChips.Visibility;

        _view?.Refresh();
    }

    private void OnSeasonChipToggled(object sender, RoutedEventArgs e)
    {
        // Never leave every season off - the grid would empty and look broken.
        if (Seasons.Count > 0 && Seasons.All(c => !c.IsOn))
        {
            foreach (var c in Seasons) c.IsOn = true;
        }

        _view?.Refresh();
        UpdateSummary();
    }

    /// <summary>
    /// The rows the bulk tools act on: the ones on screen, in the order they are
    /// on screen. Switching a season on and then shifting everything is the whole
    /// point of switching it on.
    ///
    /// Read from the view, not from Rows. Rows is path order; the view is
    /// whatever the last clicked column header made it. "Fill sequentially",
    /// which describes itself as following the current file order, was filling
    /// against path order while the user read a different one off the screen.
    /// </summary>
    private List<MapRow> Working
    {
        get
        {
            var onScreen = Ordered();

            // A selection means "these ones".
            //
            // Ctrl and Shift already picked rows out of the grid - it has always
            // been Extended - and the bulk tools were the only thing not
            // looking. Two or more, deliberately: clicking a row to reach its
            // dropdown selects it, and a Shift that quietly acted on that one
            // row would be worse than one that acted on everything.
            var picked = Grid.SelectedItems.OfType<MapRow>().ToHashSet();

            return picked.Count > 1
                ? [.. onScreen.Where(picked.Contains)]
                : onScreen;
        }
    }

    /// <summary>The rows on screen, in the order they are on screen.</summary>
    private List<MapRow> Ordered() =>
        _view is null ? [.. Rows.Where(InSelectedSeason)] : [.. _view.Cast<MapRow>()];

    // ---------------- Bulk tools ----------------

    private void OnMatchByTitle(object sender, RoutedEventArgs e)
    {
        // Only the episodes of the seasons on screen, or matching by title while
        // working on season 2 could pull a file into season 1.
        var wanted = Seasons.Where(c => c.IsOn).Select(c => c.Number).ToHashSet();

        var episodes = AllEpisodes
            .Select(c => c.Episode)
            .Where(ep => wanted.Count == 0 || wanted.Contains(ep.SeasonNumber))
            .ToList();

        var included = Working.Where(r => r.Include).ToList();

        var matches = TitleMatcher.MatchAll(
            included.Select(r => r.File), episodes, _show.Name);

        var byFile = matches.ToDictionary(m => m.File, StringComparer.OrdinalIgnoreCase);

        foreach (var row in included)
        {
            if (!byFile.TryGetValue(row.File, out var m) || m.Episode is null) continue;

            row.Chosen = Find(m.Episode.SeasonNumber, m.Episode.Number);
            row.Confidence = m.Confidence;
            row.Alternative = m.Alternative is null
                ? null
                : $"{m.Alternative.SeasonNumber}x{m.Alternative.Number:00} {m.Alternative.Name} "
                  + $"({m.AlternativeConfidence:P0})";
        }

        UpdateSummary();
    }

    /// <summary>
    /// Straight sequential fill, following the order the files are listed in.
    /// The fallback when titles aren't in the filenames at all.
    /// </summary>
    private void OnSequential(object sender, RoutedEventArgs e)
    {
        var included = Working.Where(r => r.Include).ToList();
        if (included.Count == 0 || AllEpisodes.Count == 0) return;

        // Start from the first row's current episode when it has one, so a
        // part-way-correct list keeps its starting point.
        var start = included[0].Chosen is { } c
            ? AllEpisodes.IndexOf(c)
            : 0;

        if (start < 0) start = 0;

        // Stop at the end of the season being filled, not the end of the show.
        // AllEpisodes is every season flattened, so thirteen files against a
        // ten-episode season used to spill into 3x01..3x03 without a word.
        var season = included[0].Chosen?.Episode.SeasonNumber;

        var filled = 0;

        for (var i = 0; i < included.Count && start + i < AllEpisodes.Count; i++)
        {
            var next = AllEpisodes[start + i];
            if (season is { } s && next.Episode.SeasonNumber != s) break;

            included[i].Chosen = next;
            included[i].Confidence = -1;
            included[i].Alternative = null;
            filled++;
        }

        if (filled < included.Count)
        {
            TxtSummary.Text = $"Filled {filled} of {included.Count} - "
                              + $"season {season} has no more episodes to give.";
            return;
        }

        UpdateSummary();
    }

    private void Shift(int by)
    {
        // Inside each row's own season.
        //
        // This used to step through AllEpisodes, which is every season
        // flattened with Specials at the front, so shifting 1x01 down by one
        // landed it on the last special and 2x01 down by one landed on 1x13. A
        // shift is a correction within a season, not a walk through the series.
        var moved = new List<(MapRow Row, EpisodeChoice To)>();

        foreach (var row in Working.Where(r => r.Include && r.Chosen is not null))
        {
            var season = row.Chosen!.Episode.SeasonNumber;

            // Nothing past either end. The bounds check used to be per row, so
            // the row at the edge stayed put while the rest moved - which
            // quietly put two files on one episode, and surfaced only as an OK
            // button that would not enable and no explanation.
            if (Find(season, row.Chosen.Episode.Number + by) is not { } target)
            {
                TxtSummary.Text = $"Shifting by {by:+#;-#;0} would take "
                                  + $"{row.FileName} outside season {season} - nothing moved.";
                return;
            }

            moved.Add((row, target));
        }

        foreach (var (row, to) in moved) row.Chosen = to;

        UpdateSummary();
    }

    /// <summary>
    /// Read the rows on screen as another season, keeping their episode numbers.
    ///
    /// "These are season 2, not season 1" had no expression at all. Shift moves
    /// by one through the flat episode list, so saying it meant clicking
    /// thirteen times and crossing a season boundary on the way - and the
    /// numbering that came out the other side was whatever the walk landed on.
    ///
    /// Nothing is invented: an episode number the target season does not have
    /// leaves its row alone and is reported, rather than being filled with the
    /// nearest thing.
    /// </summary>
    private void OnMoveSeason(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtMoveSeason.Text?.Trim(), out var target) || target < 0)
        {
            TxtSummary.Text = "Give the season to move these into - a number, like 2.";
            return;
        }

        var rows = Working.Where(r => r.Include && r.Chosen is not null).ToList();
        if (rows.Count == 0) return;

        var moved = new List<(MapRow Row, EpisodeChoice To)>();
        var missing = new List<int>();

        foreach (var row in rows)
        {
            var number = row.Chosen!.Episode.Number;

            if (Find(target, number) is { } to) moved.Add((row, to));
            else missing.Add(number);
        }

        foreach (var (row, to) in moved)
        {
            row.Chosen = to;
            row.Confidence = -1;
            row.Alternative = null;
        }

        // The season chips are built from what the rows hold, so they have to be
        // rebuilt or the season these just moved to has no chip and the rows
        // vanish behind the old filter.
        FillSeasons();
        UpdateSummary();

        if (missing.Count > 0)
            TxtSummary.Text = $"Moved {moved.Count} to season {target}. "
                              + $"Season {target} has no episode "
                              + string.Join(", ", missing.Take(5))
                              + (missing.Count > 5 ? $" or {missing.Count - 5} more" : "")
                              + " - those were left as they were.";
    }

    private void OnShiftDown(object sender, RoutedEventArgs e) => Shift(-1);
    private void OnShiftUp(object sender, RoutedEventArgs e) => Shift(1);

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in Working)
        {
            row.Chosen = row.Parsed;
            row.Confidence = -1;
            row.Alternative = null;
        }

        UpdateSummary();
    }

    private void OnIncludeAll(object sender, RoutedEventArgs e) => SetInclude(true);
    private void OnExcludeAll(object sender, RoutedEventArgs e) => SetInclude(false);

    private void SetInclude(bool value)
    {
        foreach (var row in Working) row.Include = value;
        UpdateSummary();
    }

    // ---------------- Finish ----------------

    /// <summary>
    /// When every file moved by the same amount, that's a constant offset, not
    /// a set of individual corrections. Storing it as the show's offset makes it
    /// persist and keep working for files that arrive later - per-file overrides
    /// only ever cover the files in front of us right now.
    /// </summary>
    private Dictionary<int, int> PersistUniformShift()
    {
        var included = Rows.Where(r => r.Include).ToList();
        var saved = new Dictionary<int, int>();

        if (included.Count == 0) return saved;

        // Every row must have both a parsed and a chosen episode in the same
        // season, or "shifted by N" isn't a meaningful description of it.
        // Gathered per season: a run of files can be out by one in season 2 and
        // right in season 1, and one number for the show can't say that.
        var bySeason = new Dictionary<int, List<int>>();

        // Seasons holding a row that "shifted by N" cannot describe. These used
        // to abandon the whole method, so one file with an unreadable name, or
        // one moved to another season, stopped every other season being
        // recorded - including the ones that were perfectly uniform.
        var cannotDescribe = new HashSet<int>();

        foreach (var r in included)
        {
            if (r.Chosen is not { } c || r.Parsed is not { } p)
            {
                if (r.Chosen?.Episode.SeasonNumber is { } orphan) cannotDescribe.Add(orphan);
                continue;
            }

            if (c.Episode.SeasonNumber != p.Episode.SeasonNumber)
            {
                cannotDescribe.Add(c.Episode.SeasonNumber);
                cannotDescribe.Add(p.Episode.SeasonNumber);
                continue;
            }

            var season = c.Episode.SeasonNumber;
            if (season <= 0) continue;              // specials keep their own numbering

            if (!bySeason.TryGetValue(season, out var deltas))
                bySeason[season] = deltas = [];

            deltas.Add(c.Episode.Number - p.Episode.Number);
        }

        var changed = false;

        foreach (var (season, deltas) in bySeason)
        {
            // Only a season where every file moved by the same amount describes a
            // shift. Anything else is a set of individual corrections, and those
            // are recorded as per-file assignments instead.
            if (cannotDescribe.Contains(season)) continue;
            if (deltas.Distinct().Count() != 1) continue;

            _settings.SetEpisodeOffset(_show.Id, season, deltas[0]);
            saved[season] = deltas[0];
            changed = true;
        }

        if (changed) _settings.Save();

        return saved;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        // What was recorded as a whole-season shift. Rows explained by it need
        // no override of their own - see below.
        var shifts = PersistUniformShift();

        var result = new EpisodeOverrides();

        foreach (var row in Rows)
        {
            if (!row.Include)
            {
                result.Skip.Add(row.File);
                continue;
            }

            if (row.Chosen is not { } c) continue;

            // Only what was actually changed.
            //
            // An override is a single season-and-episode pair, so recording one
            // for an untouched row throws away anything the filename said that
            // a pair cannot hold - a file named "S01E01-E02" came back as
            // "S01E01" and the second episode vanished from the name, purely
            // because the screen had been opened and accepted. A row that still
            // agrees with its filename needs no override; the parser will read
            // it the same way next time, span and all.
            if (row.Parsed is { } parsed
                && parsed.Episode.SeasonNumber == c.Episode.SeasonNumber
                && c.Episode.Number - parsed.Episode.Number
                   == (shifts.TryGetValue(c.Episode.SeasonNumber, out var by) ? by : 0))
                continue;

            result.Map[row.File] = (c.Episode.SeasonNumber, c.Episode.Number);
        }

        Result = result;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

}
