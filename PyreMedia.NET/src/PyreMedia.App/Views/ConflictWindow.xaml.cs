using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core.Organizing;

namespace PyreMedia.App.Views;

/// <summary>One collision as a row the user can answer.</summary>
public partial class ConflictRow : ObservableObject
{
    public required FileConflict Conflict { get; init; }

    /// <summary>Unique per row, so the three radio buttons don't group across rows.</summary>
    public required string GroupName { get; init; }

    public string Headline =>
        Conflict.TargetFolder.Length > 0
            ? $"{Conflict.TargetName}   -   in \"{Conflict.TargetFolder}\""
            : Conflict.TargetName;

    public string IncomingDetail => Describe(Conflict.SourceName, Conflict.SourceBytes, Conflict.SourceModified)
                                    + CompanionSuffix;

    public string ExistingDetail => Describe(Conflict.TargetName, Conflict.TargetBytes, Conflict.TargetModified);

    private string CompanionSuffix => Conflict.Companions.Count switch
    {
        0 => "",
        1 => "\n+ 1 companion file",
        var n => $"\n+ {n} companion files"
    };

    /// <summary>
    /// The one thing worth interrupting for: the two files look like the same
    /// file, so replacing gains nothing and risks something.
    /// </summary>
    public string? Note =>
        Conflict.LooksIdentical
            ? "These are the same size - this may already be the same file."
            : null;

    public bool HasNote => Note is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeepBoth))]
    [NotifyPropertyChangedFor(nameof(IsReplace))]
    [NotifyPropertyChangedFor(nameof(IsSkip))]
    [NotifyPropertyChangedFor(nameof(Outcome))]
    public partial ConflictResolution Resolution { get; set; }

    public bool IsKeepBoth
    {
        get => Resolution == ConflictResolution.KeepBoth;
        set { if (value) Resolution = ConflictResolution.KeepBoth; }
    }

    public bool IsReplace
    {
        get => Resolution == ConflictResolution.Replace;
        set { if (value) Resolution = ConflictResolution.Replace; }
    }

    public bool IsSkip
    {
        get => Resolution == ConflictResolution.Skip;
        set { if (value) Resolution = ConflictResolution.Skip; }
    }

    /// <summary>
    /// Plain statement of what this choice does. "Replace" is not self-evident
    /// when the existing file is archived rather than destroyed.
    /// </summary>
    public string Outcome => Resolution switch
    {
        ConflictResolution.KeepBoth =>
            $"Both kept. The incoming file lands as \"{Path.GetFileName(KeepBothName)}\""
            + (Conflict.Companions.Count > 0 ? ", and its companion files follow the same name." : "."),

        ConflictResolution.Replace =>
            "The existing file goes to the Recycle Bin first, so it can be restored.",

        ConflictResolution.Skip =>
            "Nothing happens. Both files stay exactly as they are.",

        _ => "Choose one."
    };

    /// <summary>Resolved lazily so the number shown matches what will be used.</summary>
    public string KeepBothName => Conflict.KeepBothPath ??= UniqueName.For(Conflict.TargetPath);

    private static string Describe(string name, long bytes, DateTime? modified)
    {
        var size = bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N1} GB"
                 : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):N0} MB"
                 : $"{bytes / 1024.0:N0} KB";

        var when = modified?.ToString("d MMM yyyy HH:mm") ?? "unknown date";
        return $"{name}\n{size}   -   {when}";
    }
}

/// <summary>
/// Windows-style conflict resolution: every collision listed together, each
/// answerable individually, with the whole set answerable at once.
///
/// Prompting per file as the run proceeds is worse than it looks - by the third
/// prompt the earlier answers are out of sight and can't be revised, and the
/// run is already half-applied.
/// </summary>
public partial class ConflictWindow
{
    private readonly ObservableCollection<ConflictRow> _rows = [];

    public ConflictWindow(IReadOnlyList<FileConflict> conflicts)
    {
        InitializeComponent();

        for (var i = 0; i < conflicts.Count; i++)
        {
            var row = new ConflictRow
            {
                Conflict = conflicts[i],
                GroupName = $"conflict{i}",

                // Default to the only choice that cannot lose anything.
                Resolution = ConflictResolution.KeepBoth
            };

            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConflictRow.Resolution)) UpdateSummary();
            };

            _rows.Add(row);
        }

        List.ItemsSource = _rows;

        TxtCount.Text = conflicts.Count == 1
            ? "1 file already exists"
            : $"{conflicts.Count} files already exist";

        UpdateSummary();
    }

    /// <summary>The answers, written back onto the conflicts themselves.</summary>
    public IReadOnlyList<FileConflict> Result =>
        [.. _rows.Select(r =>
        {
            r.Conflict.Resolution = r.Resolution;

            if (r.Resolution == ConflictResolution.KeepBoth)
                r.Conflict.KeepBothPath = r.KeepBothName;

            return r.Conflict;
        })];

    private void UpdateSummary()
    {
        var keep = _rows.Count(r => r.Resolution == ConflictResolution.KeepBoth);
        var replace = _rows.Count(r => r.Resolution == ConflictResolution.Replace);
        var skip = _rows.Count(r => r.Resolution == ConflictResolution.Skip);

        var parts = new List<string>();
        if (keep > 0) parts.Add($"{keep} kept alongside");
        if (replace > 0) parts.Add($"{replace} replaced");
        if (skip > 0) parts.Add($"{skip} skipped");

        TxtSummary.Text = parts.Count > 0 ? string.Join(",   ", parts) : "";
        BtnOk.IsEnabled = _rows.All(r => r.Resolution != ConflictResolution.Ask);
    }

    private void SetAll(ConflictResolution r)
    {
        foreach (var row in _rows) row.Resolution = r;
        UpdateSummary();
    }

    private void OnAllKeepBoth(object sender, RoutedEventArgs e) => SetAll(ConflictResolution.KeepBoth);
    private void OnAllReplace(object sender, RoutedEventArgs e) => SetAll(ConflictResolution.Replace);
    private void OnAllSkip(object sender, RoutedEventArgs e) => SetAll(ConflictResolution.Skip);

    /// <summary>
    /// Replace only where the incoming file is actually newer. Anything we can't
    /// compare is skipped rather than guessed at.
    /// </summary>
    private void OnAllNewer(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.Resolution = row.Conflict.SourceIsNewer
                ? ConflictResolution.Replace
                : ConflictResolution.Skip;

        UpdateSummary();
    }

    private void OnAllLarger(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.Resolution = row.Conflict.SourceIsLarger
                ? ConflictResolution.Replace
                : ConflictResolution.Skip;

        UpdateSummary();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var replacing = _rows.Count(r => r.Resolution == ConflictResolution.Replace);

        // Replace is the only answer that can cost you a file, so confirm it
        // once here rather than trusting a radio button clicked in bulk.
        if (replacing > 0)
        {
            var ok = MessageBox.Show(this,
                $"{replacing} existing file(s) will be replaced.\n\n"
                + "Each one goes to the Recycle Bin first and is recorded in History, "
                + "so this can be undone - but the files are no longer where they were.\n\n"
                + "Continue?",
                "Replace existing files", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (ok != MessageBoxResult.OK) return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
