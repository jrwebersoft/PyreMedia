using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core.Music;

namespace PyreMedia.App.Views;

/// <summary>One disagreement, as a row somebody can tick.</summary>
public partial class ConsistencyRow : ObservableObject
{
    public required Inconsistency Finding { get; init; }

    /// <summary>
    /// Off to begin with, every time.
    ///
    /// The obvious alternative - tick everything and let the user clear what
    /// they disagree with - reads as "these are corrections, uncheck the
    /// mistakes", and that is exactly the wrong impression. The majority is
    /// wrong often enough that agreeing has to be a decision rather than the
    /// path of least resistance.
    /// </summary>
    [ObservableProperty]
    public partial bool Approved { get; set; }

    public string Kind => Finding.Kind.ToString();
    public string Field => Finding.Field;
    public int Count => Finding.Odd.Count;
    public string Value => Finding.Value.Length == 0 ? "(nothing)" : Finding.Value;
    public string Suggested => Finding.Suggested ?? "";
    public string Why => Finding.Why;
}

/// <summary>
/// The screen for disagreements between a file and its neighbours.
///
/// A list rather than a summary, and every row unticked, because this is the
/// one kind of finding in the program that cannot be trusted wholesale.
/// Everything else either has an answer inside the file - a stray space, a
/// track number repeated into the title - or refuses to guess. These have an
/// answer only in what the rest of the set says, and the set can be wrong:
/// thirteen files in this library spell a band without an apostrophe and one
/// spells it correctly, and the majority would quietly delete it.
///
/// So the shape of the screen is the argument. No "tick all", the warning
/// above the list rather than buried in a tooltip, and the button says what it
/// will do rather than "OK".
/// </summary>
public partial class ConsistencyWindow
{
    private readonly ObservableCollection<ConsistencyRow> _rows = [];

    /// <summary>What the user ticked. Empty unless they pressed the button.</summary>
    public IReadOnlyList<Inconsistency> Approved { get; private set; } = [];

    public ConsistencyWindow(IReadOnlyList<Inconsistency> findings)
    {
        InitializeComponent();

        foreach (var finding in findings.Where(f => f.Fixable)
                     .OrderBy(f => f.Kind)
                     .ThenBy(f => f.Field))
        {
            var row = new ConsistencyRow { Finding = finding };
            row.PropertyChanged += OnRowChanged;
            _rows.Add(row);
        }

        KindFilter.Items.Add("Everything");
        foreach (var kind in _rows.Select(r => r.Kind).Distinct().OrderBy(k => k))
            KindFilter.Items.Add(kind);

        KindFilter.SelectedIndex = 0;

        TxtCount.Text = $"{_rows.Count} disagreements with something to propose";
        Refresh();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsistencyRow.Approved)) Refresh();
    }

    private void Refresh()
    {
        var ticked = _rows.Count(r => r.Approved);

        TxtTicked.Text = ticked == 0 ? "nothing ticked" : $"{ticked} ticked";
        BtnApply.IsEnabled = ticked > 0;
    }

    private void OnFilter(object sender, SelectionChangedEventArgs e)
    {
        if (Grid is null) return;

        var shown = Shown().ToList();
        Grid.ItemsSource = shown;

        BtnAll.Content = shown.Count == _rows.Count
            ? "Tick all shown"
            : $"Tick these {shown.Count}";
    }

    /// <summary>
    /// Tick everything the filter is currently showing.
    ///
    /// Only what is shown, so it follows the filter beside it - "tick all the
    /// spellings" is a reasonable thing to mean where "tick all eighty-two of
    /// these sight unseen" mostly is not. It is still a proposal being accepted
    /// wholesale, which is why the warning stays above the list.
    /// </summary>
    private void OnAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in Shown()) row.Approved = true;
    }

    private void OnNone(object sender, RoutedEventArgs e)
    {
        // Clears everything, not only what is shown. Somebody reaching for
        // this wants to start again, and leaving ticks hidden behind a filter
        // is how the wrong rows get written.
        foreach (var row in _rows) row.Approved = false;
    }

    private IEnumerable<ConsistencyRow> Shown()
    {
        var wanted = KindFilter.SelectedItem as string;

        return wanted is null or "Everything"
            ? _rows
            : _rows.Where(r => r.Kind == wanted);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Approved = [.. _rows.Where(r => r.Approved).Select(r => r.Finding)];
        DialogResult = true;
    }
}
