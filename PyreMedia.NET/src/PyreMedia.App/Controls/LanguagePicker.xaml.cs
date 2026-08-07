using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PyreMedia.Core.Models;

namespace PyreMedia.App.Controls;

/// <summary>
/// Pick languages by searching and collecting them as chips.
///
/// These were free-text fields holding "eng;jpn" - which required knowing the
/// ISO code for your own language, offered no way to discover one, and gave no
/// sense of what was actually selected. Typing a code by hand still works,
/// because no list covers everything.
/// </summary>
public partial class LanguagePicker : UserControl
{
    private readonly ObservableCollection<LanguageEntry> _chosen = [];
    private readonly ObservableCollection<LanguageEntry> _matches = [];

    public LanguagePicker()
    {
        InitializeComponent();

        ChipList.ItemsSource = _chosen;
        Suggestions.ItemsSource = _matches;

        _chosen.CollectionChanged += (_, _) =>
        {
            TxtEmpty.Visibility = _chosen.Count == 0 && AllowEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Whether an empty selection is meaningful. For keep-rules it means "keep
    /// everything"; where exactly one language is wanted, it isn't offered.
    /// </summary>
    public bool AllowEmpty { get; set; } = true;

    /// <summary>Only one at a time - for "your language" rather than a keep list.</summary>
    public bool SingleSelect { get; set; }

    public event EventHandler? SelectionChanged;

    /// <summary>The selection as stored: "eng;jpn".</summary>
    public string Value
    {
        get => LanguageCatalog.Join(_chosen.Select(c => c.Code));
        set
        {
            _chosen.Clear();

            foreach (var code in LanguageCatalog.Parse(value))
            {
                // An unknown code is still valid - show it as itself rather
                // than dropping the user's setting on the floor.
                _chosen.Add(LanguageCatalog.Find(code) ?? new LanguageEntry(code, code));
            }

            TxtEmpty.Visibility = _chosen.Count == 0 && AllowEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    // ---------------- Searching ----------------

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refilter();
    private void OnSearchFocus(object sender, RoutedEventArgs e) => Refilter();

    private void Refilter()
    {
        var query = TxtSearch.Text?.Trim() ?? "";

        _matches.Clear();

        foreach (var l in LanguageCatalog.Search(query).Where(l => !Has(l.Code)).Take(40))
            _matches.Add(l);

        // Only take up space while there's something to choose.
        SuggestBox.Visibility = _matches.Count > 0 && TxtSearch.IsKeyboardFocusWithin
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                // Take the highlighted suggestion, or whatever was typed - a
                // code we don't list is still a valid code.
                if (Suggestions.SelectedItem is LanguageEntry picked) Add(picked);
                else if (_matches.Count > 0) Add(_matches[0]);
                else AddRaw(TxtSearch.Text);

                e.Handled = true;
                break;

            case Key.Down when _matches.Count > 0:
                Suggestions.SelectedIndex = Math.Min(Suggestions.SelectedIndex + 1, _matches.Count - 1);
                e.Handled = true;
                break;

            case Key.Up when _matches.Count > 0:
                Suggestions.SelectedIndex = Math.Max(Suggestions.SelectedIndex - 1, 0);
                e.Handled = true;
                break;

            case Key.Escape:
                TxtSearch.Text = "";
                SuggestBox.Visibility = Visibility.Collapsed;
                e.Handled = true;
                break;

            // Backspace on an empty box removes the last chip, the way tag
            // fields behave everywhere else.
            case Key.Back when TxtSearch.Text.Length == 0 && _chosen.Count > 0:
                _chosen.RemoveAt(_chosen.Count - 1);
                Refilter();
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionPicked(object sender, MouseButtonEventArgs e)
    {
        if (Suggestions.SelectedItem is LanguageEntry l) Add(l);
    }

    /// <summary>Single click is enough - a double click to add a tag is a surprise.</summary>
    private void OnSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!SuggestBox.IsVisible) return;
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not LanguageEntry l) return;

        // Keyboard navigation also raises this, so only act on a real click.
        if (Mouse.LeftButton != MouseButtonState.Released) return;
        if (!Suggestions.IsMouseOver) return;

        Add(l);
    }

    // ---------------- Selection ----------------

    private bool Has(string code) =>
        _chosen.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

    private void Add(LanguageEntry entry)
    {
        if (SingleSelect) _chosen.Clear();

        if (!Has(entry.Code)) _chosen.Add(entry);

        TxtSearch.Text = "";
        SuggestBox.Visibility = Visibility.Collapsed;
        TxtSearch.Focus();
    }

    /// <summary>A code typed by hand, for anything the catalogue doesn't list.</summary>
    private void AddRaw(string? text)
    {
        var code = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(code)) return;

        // Only plausible codes - stops a half-typed language name becoming one.
        if (code.Length is < 2 or > 3 || !code.All(char.IsLetter)) return;

        Add(LanguageCatalog.Find(code) ?? new LanguageEntry(code, code));
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LanguageEntry l)
        {
            _chosen.Remove(l);
            Refilter();
        }
    }
}
