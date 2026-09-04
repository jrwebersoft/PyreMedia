using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PyreMedia.Core.History;

namespace PyreMedia.App.Views;

public partial class HistoryWindow
{
    private readonly RenameHistory _history;
    private readonly RevertService _revert;

    public HistoryWindow(RenameHistory history)
    {
        InitializeComponent();
        _history = history;
        // Tag writes are offered here as undoable, so something has to be able
        // to undo them. Without this the grid showed Retag rows ready to
        // revert, enabled the button, and then skipped every one of them
        // silently - the reason went to a log the caller never passed.
        _revert = new RevertService(history)
        {
            Retag = new PyreMedia.Core.Music.MusicTagWriter(history).Undo
        };

        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 60) Height = Math.Max(MinHeight, work.Height - 60);
        if (Width > work.Width - 60) Width = Math.Max(MinWidth, work.Width - 60);

        TxtPath.Text = PyreMedia.Core.AppPaths.Display(_history.FilePath);
        Reload();
    }

    private void Reload()
    {
        var filter = TxtSearch.Text;
        var entries = _history.Read(string.IsNullOrWhiteSpace(filter) ? null : filter);

        // Examine live, so the grid shows whether each row can actually be undone.
        Grid.ItemsSource = _revert.Examine(entries);

        var total = _history.Count();
        TxtCount.Text = string.IsNullOrWhiteSpace(filter)
            ? $"{total} change(s) recorded"
            : $"{entries.Count} of {total} shown";

        UpdateSelectionState();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Reload();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void UpdateSelectionState()
    {
        var selected = Grid.SelectedItems.Cast<RevertCandidate>().ToList();
        var revertible = selected.Count(c => c.CanRevert);
        var shown = (Grid.ItemsSource as IReadOnlyList<RevertCandidate>)?.Count ?? 0;

        BtnRevert.IsEnabled = revertible > 0;
        BtnRevertBatch.IsEnabled = selected.Any(c => !string.IsNullOrEmpty(c.Entry.BatchId));
        BtnForget.IsEnabled = selected.Count > 0;
        BtnClearAll.IsEnabled = shown > 0 || _history.Count() > 0;

        // The header tick follows the rows rather than driving them, so it can't
        // disagree with what is actually selected.
        if (ChkAll is not null)
        {
            _syncingHeaderTick = true;
            ChkAll.IsChecked = shown > 0 && selected.Count == shown ? true
                             : selected.Count == 0 ? false
                             : null;
            _syncingHeaderTick = false;
        }

        TxtSelection.Text = selected.Count == 0
            ? "Tick rows, or click and use Shift or Ctrl to pick several."
            : $"{selected.Count} selected, {revertible} revertible";
    }

    private bool _syncingHeaderTick;

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        if (_syncingHeaderTick || !IsLoaded) return;

        if (ChkAll.IsChecked == true) Grid.SelectAll();
        else Grid.UnselectAll();
    }

    /// <summary>
    /// Drive the row's selection straight from the tick and stop the click there.
    /// Left to itself the grid treats it as a plain row click and clears every
    /// other selected row, so ticking a fifth row would silently drop the first four.
    /// </summary>
    private void OnTickClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not CheckBox cb) return;

        if (ItemsControl.ContainerFromElement(Grid, cb) is DataGridRow row)
            row.IsSelected = cb.IsChecked != true;

        e.Handled = true;
    }

    private void OnRevertSelected(object sender, RoutedEventArgs e)
    {
        var selected = Grid.SelectedItems.Cast<RevertCandidate>().ToList();
        RevertThese(selected);
    }

    private void OnRevertBatch(object sender, RoutedEventArgs e)
    {
        var batches = Grid.SelectedItems.Cast<RevertCandidate>()
            .Select(c => c.Entry.BatchId)
            .Where(b => !string.IsNullOrEmpty(b))
            .Distinct()
            .ToHashSet();

        if (batches.Count == 0) return;

        var all = _revert.Examine(_history.Read())
            .Where(c => batches.Contains(c.Entry.BatchId))
            .ToList();

        RevertThese(all);
    }

    private void RevertThese(IReadOnlyList<RevertCandidate> candidates)
    {
        var doable = candidates.Where(c => c.CanRevert).ToList();
        if (doable.Count == 0)
        {
            MessageBox.Show(this,
                "None of the selected entries can be reverted.\n\n" +
                "Files may have been moved since, something may already occupy the original name, " +
                "or the entry is a deletion - those are restored from the Recycle Bin.",
                "Nothing to revert", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var blocked = candidates.Count - doable.Count;

        var confirm = MessageBox.Show(this,
            $"Put {doable.Count} item(s) back to their original names?" +
            (blocked > 0 ? $"\n\n{blocked} selected item(s) cannot be reverted and will be skipped." : "") +
            "\n\nThis moves files on disk and is recorded in the history.",
            "Revert changes", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        var result = _revert.Revert(doable);

        var msg = $"Reverted {result.Reverted}."
                  + (result.Skipped > 0 ? $" Skipped {result.Skipped}." : "")
                  + (result.Failed > 0 ? $" Failed {result.Failed}." : "");

        if (result.Errors.Count > 0)
            msg += "\n\n" + string.Join("\n", result.Errors.Take(10));

        MessageBox.Show(this, msg, "Revert complete",
            MessageBoxButton.OK,
            result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        Reload();
    }

    /// <summary>
    /// Drop the selected rows from the log. This does not touch a single file - it
    /// only throws away the record, and with it the ability to undo those renames.
    /// </summary>
    private void OnForgetSelected(object sender, RoutedEventArgs e)
    {
        var selected = Grid.SelectedItems.Cast<RevertCandidate>().ToList();
        if (selected.Count == 0) return;

        var revertible = selected.Count(c => c.CanRevert);

        var confirm = MessageBox.Show(this,
            $"Remove {selected.Count} row(s) from the log?\n\n"
            + "Your files are not touched and nothing is renamed. What goes is the record "
            + "of the change"
            + (revertible > 0
                ? $" - including the ability to undo {revertible} of them, which will be gone for good."
                : ".")
            + $"\n\nThe previous log is kept as {Path.GetFileName(_history.FilePath)}.bak.",
            "Remove from history", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            var gone = _history.Remove(selected.Select(c => c.Entry));
            Reload();
            TxtSelection.Text = $"Removed {gone} row(s) from the log.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not rewrite the log.\n\n{ex.Message}",
                "Remove failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Empty the log. Same deal - the files are untouched, the record isn't.</summary>
    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var total = _history.Count();
        if (total == 0) return;

        var confirm = MessageBox.Show(this,
            $"Clear all {total} recorded change(s)?\n\n"
            + "No files are touched and nothing is renamed back. You are throwing away the "
            + "record itself, so nothing in it can be undone afterwards.\n\n"
            + $"The current log is kept as {Path.GetFileName(_history.FilePath)}.bak.",
            "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _history.Clear();
            Reload();
            TxtSelection.Text = "History cleared.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not clear the log.\n\n{ex.Message}",
                "Clear failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(_history.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
