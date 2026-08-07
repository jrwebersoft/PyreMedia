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
        Func<EpisodeSource, CancellationToken, Task<TvShow?>> refetch)
    {
        InitializeComponent();
        DataContext = this;

        _settings = settings;
        _item = item;
        _show = show;
        _refetch = refetch;
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
        Rows.Clear();

        foreach (var file in _item.Files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            var parsedRef = EpisodeMatcher.Parse(fileName);

            EpisodeChoice? parsed = null;
            if (parsedRef is { } p && p.Season is { } se)
                parsed = Find(se, p.Episode);

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
                    row.Chosen = Find(oc.Episode.SeasonNumber, oc.Episode.Number) ?? parsed;
                else
                    row.Chosen = parsed;
            }
            else
            {
                row.Chosen = parsed;
            }

            Rows.Add(row);
        }

        FillSeasons();
        UpdateSummary();
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
    /// The rows the bulk tools act on: the ones on screen. Switching a season on
    /// and then shifting everything is the whole point of switching it on.
    /// </summary>
    private List<MapRow> Working => Rows.Where(r => InSelectedSeason(r)).ToList();

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

        for (var i = 0; i < included.Count && start + i < AllEpisodes.Count; i++)
        {
            included[i].Chosen = AllEpisodes[start + i];
            included[i].Confidence = -1;
            included[i].Alternative = null;
        }

        UpdateSummary();
    }

    private void Shift(int by)
    {
        foreach (var row in Working.Where(r => r.Include && r.Chosen is not null))
        {
            var idx = AllEpisodes.IndexOf(row.Chosen!) + by;
            if (idx >= 0 && idx < AllEpisodes.Count)
                row.Chosen = AllEpisodes[idx];
        }

        UpdateSummary();
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
    private void PersistUniformShift()
    {
        var included = Rows.Where(r => r.Include).ToList();
        if (included.Count == 0) return;

        // Every row must have both a parsed and a chosen episode in the same
        // season, or "shifted by N" isn't a meaningful description of it.
        // Gathered per season: a run of files can be out by one in season 2 and
        // right in season 1, and one number for the show can't say that.
        var bySeason = new Dictionary<int, List<int>>();

        foreach (var r in included)
        {
            if (r.Chosen is not { } c || r.Parsed is not { } p) return;
            if (c.Episode.SeasonNumber != p.Episode.SeasonNumber) return;

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
            if (deltas.Distinct().Count() != 1) continue;

            _settings.SetEpisodeOffset(_show.Id, season, deltas[0]);
            changed = true;
        }

        if (changed) _settings.Save();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        PersistUniformShift();

        var result = new EpisodeOverrides();

        foreach (var row in Rows)
        {
            if (!row.Include)
            {
                result.Skip.Add(row.File);
                continue;
            }

            if (row.Chosen is { } c)
                result.Map[row.File] = (c.Episode.SeasonNumber, c.Episode.Number);
        }

        Result = result;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

}
