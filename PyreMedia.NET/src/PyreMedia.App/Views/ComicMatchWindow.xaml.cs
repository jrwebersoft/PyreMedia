using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PyreMedia.Core;
using PyreMedia.Core.Books;

namespace PyreMedia.App.Views;

/// <summary>One candidate from a database, as the list shows it.</summary>
public sealed class MatchRow
{
    public required ComicSeries Series { get; init; }

    public string Display => Series.Display;

    public string Detail
    {
        get
        {
            var bits = new List<string> { Series.Source };

            if (Series.IssueCount is { } n) bits.Add(n == 1 ? "1 issue" : $"{n} issues");
            if (Series.YearBegan is { Length: 4 } y) bits.Add($"began {y}");

            return string.Join("  ·  ", bits);
        }
    }
}

/// <summary>
/// Agreeing that a run on the shelf is a particular run in a database.
///
/// The comic answer to the video tab's match column. Comics needed it more and
/// had it less: the providers were written, tested and wired into Settings, and
/// nothing ever asked one a question - the whole chain existed to print a line
/// saying which sources were configured. A comic was filed on whatever its
/// tagger had written, and where nobody had written anything - 38% of a real
/// shelf - on whatever could be read out of the filename.
///
/// Per series rather than per file, because that is the unit the question has:
/// a shelf holds a few hundred series and tens of thousands of issues, and the
/// answer to "which Daredevil is this" is the same for every issue of it.
/// </summary>
public partial class ComicMatchWindow
{
    private readonly PyreMediaSettings _settings;

    /// <summary>
    /// Built on demand rather than held, so a key entered from this window is
    /// in use the moment it is saved. A chain captured once would go on saying
    /// nothing is set up after the user had just set something up.
    /// </summary>
    private readonly Func<ComicProviderChain> _build;

    private ComicProviderChain _sources;
    private readonly ComicSeriesGroup _group;
    private readonly string _key;

    private readonly ObservableCollection<MatchRow> _hits = [];
    private CancellationTokenSource? _work;

    private ComicSeries? _picked;
    private List<string> _issues = [];

    /// <summary>What was agreed to, or null where nothing was.</summary>
    public ComicMatch? Result { get; private set; }

    /// <summary>True when the user asked for an existing match to be dropped.</summary>
    public bool Forgotten { get; private set; }

    public ComicMatchWindow(
        PyreMediaSettings settings, Func<ComicProviderChain> build,
        ComicSeriesGroup group, ComicMatch? already)
    {
        InitializeComponent();

        _settings = settings;
        _build = build;
        _sources = build();
        _group = group;
        _key = ComicShelf.Canonical(group.Series);

        Hits.ItemsSource = _hits;

        TxtShelf.Text = group.Series + (group.Year is { Length: 4 } y ? $" ({y})" : "");

        var held = group.Issues.Count == 1 ? "1 issue held" : $"{group.Issues.Count} issues held";

        TxtHeld.Text = group.Total is { } t
            ? $"{held}, of {t}."
            : $"{held}. The files do not say how long the run is, which is why this is worth asking.";

        TxtTerm.Text = group.Series;
        TxtYear.Text = group.Year ?? "";

        // Say up front which databases will answer, rather than after a search
        // that returns nothing.
        Ready();

        if (already is not null)
        {
            BtnForget.Visibility = Visibility.Visible;

            TxtStatus.Text = $"Currently matched to {already.Display}"
                             + (already.Agreed is { Length: > 0 } when ? $", agreed {when}" : "") + ".";
        }

        TxtNothing.Text = BtnSearch.IsEnabled
            ? "Press Search to look this series up."
            : "";

        Loaded += (_, _) => TxtTerm.Focus();
    }

    private void OnTermKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BtnSearch.IsEnabled) OnSearch(sender, new RoutedEventArgs());
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        // async void: an unhandled throw here would take the app down.
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;

        _hits.Clear();
        Chose(null);

        TxtNothing.Text = "";
        Busy.Visibility = Visibility.Visible;
        BtnSearch.IsEnabled = false;

        try
        {
            var term = TxtTerm.Text.Trim();
            var year = TxtYear.Text.Trim();

            if (term.Length == 0) { TxtNothing.Text = "Type a series name to look for."; return; }

            var answer = await _sources.SearchAsync(term, year.Length == 4 ? year : null, ct);

            if (ct.IsCancellationRequested) return;

            foreach (var s in answer.Series) _hits.Add(new MatchRow { Series = s });

            // Every provider's part in it, so an empty result can be told from
            // an unconfigured one.
            var tried = answer.Tried
                .Select(t => t.Ready
                    ? $"{t.Provider}: {t.Results} result(s)" + (t.Trouble is null ? "" : $" - {t.Trouble}")
                    : $"{t.Provider}: not set up" + (t.Trouble is null ? "" : $" - {t.Trouble}"));

            TxtStatus.Text = string.Join("   ", tried);

            TxtNothing.Text = _hits.Count > 0 ? "" :
                $"Nothing came back for \"{term}\"" + (year.Length == 4 ? $" ({year})" : "")
                + ". Try a shorter name, or drop the year - databases disagree about which "
                + "year a run began more often than they disagree about its name.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("Comic search", ex);
            TxtNothing.Text = $"The search failed: {ex.Message}";
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            BtnSearch.IsEnabled = _sources.All.Any(p => p.Ready);
        }
    }

    private async void OnPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        try
        {
            if (Hits.SelectedItem is not MatchRow row) { Chose(null); return; }

            Chose(row.Series);

            // The run itself, which is the thing the shelf could never know.
            // A comic says how long its run is only when somebody wrote it
            // down, which on the measured library was about one file in
            // fourteen - so "which issues am I missing" was answered from
            // nothing at all for nearly every series.
            _issues = [];
            RunBox.Visibility = Visibility.Collapsed;

            if (row.Series.Id is not { Length: > 0 }) return;

            _work?.Cancel();
            _work = new CancellationTokenSource();
            var ct = _work.Token;

            Busy.Visibility = Visibility.Visible;
            TxtRun.Text = "Asking for the issue list...";
            RunBox.Visibility = Visibility.Visible;
            TxtGaps.Text = "";

            var issues = await _sources.IssuesAsync(row.Series.Source, row.Series.Id, ct);

            if (ct.IsCancellationRequested || !ReferenceEquals(Hits.SelectedItem, row)) return;

            _issues = [.. issues.Select(i => i.Number).Where(n => n.Length > 0)];

            Describe();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("Comic issues", ex);
            TxtRun.Text = $"The issue list could not be fetched: {ex.Message}";
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>What the run is, and what of it is on the shelf.</summary>
    private void Describe()
    {
        if (_issues.Count == 0)
        {
            TxtRun.Text = "The database lists no issues for this series.";
            TxtGaps.Text = "";
            return;
        }

        TxtRun.Text = _issues.Count == 1
            ? "1 issue in the run."
            : $"{_issues.Count} issues in the run.";

        // Compared as written, so #0, #1/2 and #100.1 line up rather than being
        // rounded into each other.
        var held = _group.Issues
            .Select(i => i.Item.Comic?.Issue)
            .Where(n => n is { Length: > 0 })
            .Select(n => Tidy(n!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var absent = _issues.Where(n => !held.Contains(Tidy(n))).ToList();

        TxtGaps.Text = absent.Count == 0
            ? ""
            : absent.Count <= 12
                ? $"Missing from the shelf: #{string.Join(", #", absent)}"
                : $"Missing {absent.Count} of them, starting #{string.Join(", #", absent.Take(10))}...";

        if (absent.Count == 0) TxtRun.Text += " Every one of them is here.";
    }

    /// <summary>"007" and "7" are one issue; so are "1.MU" and "1.mu".</summary>
    private static string Tidy(string number) =>
        number.TrimStart('0') is { Length: > 0 } t ? t : number;

    private void Chose(ComicSeries? series)
    {
        _picked = series;
        BtnUse.IsEnabled = series is not null;

        if (series is null)
        {
            TxtChosen.Text = "Nothing chosen yet.";
            TxtChosenWhere.Text = "";
            TxtNote.Text = "";
            RunBox.Visibility = Visibility.Collapsed;
            return;
        }

        TxtChosen.Text = series.Display;

        TxtChosenWhere.Text = $"{series.Series()}\\...";

        TxtNote.Text = "Every issue of this series on the shelf files under this name, whatever "
                       + "its own file happens to say. The choice is remembered, so a rescan does "
                       + "not ask again - and it can be undone here.";
    }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        if (_picked is not { } series) return;

        Result = new ComicMatch
        {
            Key = _key,
            Series = series.Name,
            Year = series.YearBegan,
            Publisher = series.Publisher,
            Source = series.Source,
            SeriesId = series.Id,
            Issues = _issues,
            Total = _issues.Count > 0 ? _issues.Count : series.IssueCount,
            Agreed = DateTime.Now.ToString("yyyy-MM-dd")
        };

        DialogResult = true;
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        Forgotten = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Straight to where the key goes, once they have been to fetch one.
    /// Closing this window to go hunting for the right tab is the step that
    /// loses people.
    /// </summary>
    private void OnSettings(object sender, RoutedEventArgs e)
    {
        // The live settings, not a fresh copy - a copy would take the key and
        // leave the rest of the program still not knowing about it.
        var dlg = new SettingsWindow(_settings) { Owner = this };
        dlg.ShowTab("Books");

        if (dlg.ShowDialog() != true) return;

        // Rebuilt, so a key just entered is in use without closing anything.
        _sources = _build();

        Ready();

        TxtStatus.Text = _sources.All.Any(p => p.Ready)
            ? "Set up. Press Search."
            : "Still nothing set up - the key or the dump path has not taken.";
    }

    /// <summary>Say which databases will answer, and offer the links when none will.</summary>
    private void Ready()
    {
        var ready = _sources.All.Where(p => p.Ready).Select(p => p.Name).ToList();

        TxtSources.Text = ready.Count > 0
            ? "Will ask: " + string.Join(", ", ready)
              + ". The cheapest is asked first and the first real answer wins."
            : "No comic database is set up yet, so there is nothing to search. Any one of "
              + "these is enough - the first is free and needs no key at all:";

        // The links only where they are the answer. On a machine that is set
        // up they are clutter over the thing being done.
        Signup.Visibility = ready.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        BtnSettings.Visibility = Signup.Visibility;

        BtnSearch.IsEnabled = ready.Count > 0;

        if (ready.Count > 0 && TxtNothing.Text.StartsWith("No comic database", StringComparison.Ordinal))
            TxtNothing.Text = "Press Search to look this series up.";
    }

    protected override void OnClosed(EventArgs e)
    {
        _work?.Cancel();
        _work?.Dispose();

        base.OnClosed(e);
    }
}

internal static class MatchNames
{
    /// <summary>The folder this series would be filed under, for the preview.</summary>
    public static string Series(this ComicSeries s) =>
        s.YearBegan is { Length: 4 } y ? $"{s.Name} ({y})" : s.Name;
}
