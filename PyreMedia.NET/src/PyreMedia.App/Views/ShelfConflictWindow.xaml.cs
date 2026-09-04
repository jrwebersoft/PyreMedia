using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core.Books;

namespace PyreMedia.App.Views;

/// <summary>One name already taken, and what to do about it.</summary>
public sealed partial class ClashRow : ObservableObject
{
    public required ShelfClash Clash { get; init; }

    /// <summary>
    /// Radio buttons in a DataGrid share a group across every row unless each
    /// row has its own name, which makes forty rows behave as one answer.
    /// </summary>
    public string Group { get; } = "clash" + Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkip))]
    [NotifyPropertyChangedFor(nameof(IsReplace))]
    [NotifyPropertyChangedFor(nameof(IsKeepBoth))]
    public partial ShelfChoice Choice { get; set; } = ShelfChoice.Skip;

    public bool IsSkip
    {
        get => Choice == ShelfChoice.Skip;
        set { if (value) Choice = ShelfChoice.Skip; }
    }

    public bool IsReplace
    {
        get => Choice == ShelfChoice.Replace;
        set { if (value) Choice = ShelfChoice.Replace; }
    }

    public bool IsKeepBoth
    {
        get => Choice == ShelfChoice.KeepBoth;
        set { if (value) Choice = ShelfChoice.KeepBoth; }
    }

    public string Incoming => Clash.Action.Item.Name;
    public string Existing => Path.GetFileName(Clash.Existing);

    public string IncomingSize => Size(Clash.IncomingBytes);
    public string ExistingSize => Size(Clash.ExistingBytes);

    /// <summary>True when the file coming in is the bigger of the two.</summary>
    public bool IncomingIsBigger => Clash.IncomingBytes > Clash.ExistingBytes;

    private static string Size(long bytes) =>
        bytes <= 0 ? "?"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024:N0} MB"
        : $"{bytes / 1024:N0} KB";
}

/// <summary>
/// What to do where a comic or book would land on a name already taken.
///
/// The executor has always understood Skip, Replace and Keep both, and the
/// clash it hands over already carries both file sizes - and none of it could
/// be reached. The Books tab found the clashes only to count them, said "those
/// will be left alone", and called Execute with no answers, whose documented
/// default is to skip. So the one case where a person most wants a say - the
/// same issue arriving twice from two scanners - was decided for them.
/// </summary>
public partial class ShelfConflictWindow
{
    private readonly ObservableCollection<ClashRow> _rows = [];

    /// <summary>The answers, by source path, in the shape Execute wants.</summary>
    public Dictionary<string, ShelfChoice> Result { get; private set; } = [];

    public ShelfConflictWindow(IEnumerable<ShelfClash> clashes)
    {
        InitializeComponent();

        foreach (var c in clashes) _rows.Add(new ClashRow { Clash = c });

        Grid.ItemsSource = _rows;

        TxtWhat.Text = _rows.Count == 1
            ? "One file would land on a name that is already taken."
            : $"{_rows.Count} files would land on names that are already taken.";

        foreach (var r in _rows) r.PropertyChanged += (_, _) => Tally();

        Tally();
    }

    private void Tally()
    {
        var skip = _rows.Count(r => r.Choice == ShelfChoice.Skip);
        var replace = _rows.Count(r => r.Choice == ShelfChoice.Replace);
        var both = _rows.Count(r => r.Choice == ShelfChoice.KeepBoth);

        var bits = new List<string>();

        if (skip > 0) bits.Add($"{skip} left alone");
        if (replace > 0) bits.Add($"{replace} replaced");
        if (both > 0) bits.Add($"{both} kept alongside");

        TxtSummary.Text = string.Join(", ", bits) + ".";
    }

    private void Set(ShelfChoice choice)
    {
        foreach (var r in _rows) r.Choice = choice;
    }

    private void OnAllSkip(object sender, RoutedEventArgs e) => Set(ShelfChoice.Skip);
    private void OnAllReplace(object sender, RoutedEventArgs e) => Set(ShelfChoice.Replace);
    private void OnAllKeepBoth(object sender, RoutedEventArgs e) => Set(ShelfChoice.KeepBoth);

    /// <summary>
    /// The commonest real answer: the same issue from two scanners, and the
    /// bigger file is usually the better scan. Says nothing about which is
    /// actually better, which is why it is one button among four rather than
    /// the default.
    /// </summary>
    private void OnAllBigger(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
            r.Choice = r.IncomingIsBigger ? ShelfChoice.Replace : ShelfChoice.Skip;
    }

    private void OnGo(object sender, RoutedEventArgs e)
    {
        Result = _rows.ToDictionary(r => r.Clash.Action.Item.Path, r => r.Choice,
                                    StringComparer.OrdinalIgnoreCase);

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
