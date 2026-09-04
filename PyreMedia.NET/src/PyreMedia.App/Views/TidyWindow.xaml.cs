using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core;
using PyreMedia.Core.History;
using PyreMedia.Core.Organizing;

namespace PyreMedia.App.Views;

/// <summary>One offer in the list.</summary>
public partial class TidyRow : ObservableObject
{
    public required TidyFinding Finding { get; init; }

    [ObservableProperty]
    public partial bool Chosen { get; set; }

    public string What => Finding.What;
    public string Why => Finding.Why;
    public string Will => Finding.Will;

    /// <summary>
    /// Whether this row's tick box does anything. A RAR comic is listed so its
    /// adverts are known about, and cannot be rebuilt - so it is shown and not
    /// offered, rather than offered and then failing.
    /// </summary>
    public bool CanChoose => Finding.Actionable;

    public string KindLabel => Finding.Kind switch
    {
        TidyKind.PagesInComic => "Pages in a comic",
        TidyKind.StrayImage => "Stray image",
        TidyKind.ShelfLitter => "Release leftover",
        _ => "Unpacked comic"
    };

    public string Size => Finding.Bytes <= 0 ? ""
        : Finding.Bytes >= 1024 * 1024 ? $"{Finding.Bytes / 1024 / 1024} MB"
        : $"{Finding.Bytes / 1024} KB";
}

/// <summary>
/// The one place to look at everything in a library that is not what it should
/// be.
///
/// Three findings that would otherwise be three screens: pages inside a comic
/// that are not the comic, images a release left behind, and a folder of pages
/// nobody ever packed. Nobody looks in three places, so they are one list.
///
/// Nothing is ticked when it opens. That is the whole design: this is a list of
/// offers, and an offer that arrives already accepted is not an offer. Each row
/// says what was found, the evidence for it, and what pressing the button would
/// do - before it does anything.
/// </summary>
public partial class TidyWindow
{
    private readonly PyreMediaSettings _settings;
    private readonly RenameHistory _history;
    private readonly ObservableCollection<TidyRow> _rows = [];

    /// <summary>True when something was actually done, so the caller can rescan.</summary>
    public bool Changed { get; private set; }

    public TidyWindow(
        IReadOnlyList<TidyFinding> found, PyreMediaSettings settings, RenameHistory history)
    {
        InitializeComponent();

        _settings = settings;
        _history = history;

        Grid.ItemsSource = _rows;

        foreach (var f in found)
        {
            var row = new TidyRow { Finding = f };
            row.PropertyChanged += OnRowChanged;
            _rows.Add(row);
        }

        Describe(found);
        Tally();
    }

    private void Describe(IReadOnlyList<TidyFinding> found)
    {
        if (found.Count == 0)
        {
            TxtSummary.Text = "Nothing to tidy. No adverts left in any comic that has been read, "
                            + "no stray release images, and no unpacked comics.";
            return;
        }

        var parts = found
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key)
            .Select(g => g.Key switch
            {
                TidyKind.PagesInComic => $"{g.Count()} comic(s) with pages that are not the comic",
                TidyKind.StrayImage => $"{g.Count()} image(s) a release left behind",
                _ => $"{g.Count()} folder(s) of pages that were never packed"
            });

        TxtSummary.Text = string.Join(", ", parts) + ".";
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TidyRow.Chosen)) Tally();
    }

    private void Tally()
    {
        var chosen = _rows.Where(r => r.Chosen).ToList();

        BtnDo.IsEnabled = chosen.Count > 0;

        if (chosen.Count == 0)
        {
            TxtChosen.Text = _rows.Count == 0 ? "" : "Nothing ticked.";
            return;
        }

        // Removing and making are counted apart, because they do not carry the
        // same risk and a single number would hide that.
        var removing = chosen.Count(r => r.Finding.Removes);
        var making = chosen.Count - removing;

        var bits = new System.Collections.Generic.List<string>();

        if (removing > 0) bits.Add($"{removing} to remove");
        if (making > 0) bits.Add($"{making} to pack");

        TxtChosen.Text = string.Join(", ", bits) + ".";
    }

    private void OnTickAll(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;

        foreach (var row in _rows) row.Chosen = box.IsChecked == true;
    }

    private async void OnDo(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Chosen).Select(r => r.Finding).ToList();
        if (chosen.Count == 0) return;

        var removing = chosen.Count(f => f.Removes);

        var ask = MessageBox.Show(this,
            $"Do {chosen.Count} thing(s)?\n\n"
            + (removing > 0
                ? $"{removing} of them remove something. What goes can be got back from the "
                  + "Recycle Bin, and every change is recorded in History.\n\n"
                : "")
            + (chosen.Count - removing > 0
                ? $"{chosen.Count - removing} pack a folder into a comic. Nothing is deleted by "
                  + "that - the pages stay where they are.\n\n"
                : "")
            + "Nothing that is not ticked is touched.",
            "Tidy up", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (ask != MessageBoxResult.OK) return;

        void OnUi(System.Action work) => Dispatcher.Invoke(work);

        _working = true;
        OnUi(() => { Busy.Visibility = Visibility.Visible; BtnDo.IsEnabled = false; });

        try
        {
            var settings = _settings;
            var history = _history;

            var progress = new System.Progress<string>(
                what => OnUi(() => TxtChosen.Text = $"Working on {what}..."));

            var result = await System.Threading.Tasks.Task.Run(
                () => Tidy.Apply(chosen, settings, history, progress));

            OnUi(() =>
            {
                Changed = result.Done > 0;

                // Done rows leave the list. A list that still offers what has
                // already happened invites doing it twice.
                foreach (var row in _rows.Where(r => r.Chosen).ToList())
                    if (!result.Trouble.Any(t => t.StartsWith(row.Finding.Name))) _rows.Remove(row);

                TxtSummary.Text = $"Did {result.Done}"
                                + (result.Failed > 0 ? $", {result.Failed} failed" : "")
                                + (result.Done > 0 ? ". Undo it from History." : ".");

                foreach (var t in result.Trouble.Take(6)) TxtSummary.Text += "\n" + t;

                Tally();
            });
        }
        catch (System.Exception ex)
        {
            OnUi(() => TxtSummary.Text = $"Tidying failed: {ex.Message}");
        }
        finally
        {
            _working = false;
            OnUi(() => { Busy.Visibility = Visibility.Collapsed; BtnDo.IsEnabled = true; });
        }
    }

    /// <summary>True while a run is going, so closing can say what that means.</summary>
    private bool _working;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Closing mid-run looks like cancelling and is not: the work carries on
    /// in the background, and the caller never hears that anything changed, so
    /// its list goes on describing files that have moved.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (!_working) return;

        var go = MessageBox.Show(this,
            "Tidying is still going.\n\n"
            + "Closing this window does not stop it - the files still being worked through "
            + "will be finished either way. Everything done is undoable from History.\n\n"
            + "Close anyway?",
            "Still working", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (go != MessageBoxResult.Yes) e.Cancel = true;
    }
}
