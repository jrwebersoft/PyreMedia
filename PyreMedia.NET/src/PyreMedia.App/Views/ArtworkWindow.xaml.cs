using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core;
using PyreMedia.Core.Metadata;
using PyreMedia.Core.Models;

namespace PyreMedia.App.Views;

/// <summary>One image on the wall.</summary>
public partial class ArtTile : ObservableObject
{
    public required ArtworkOption Option { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderBrush))]
    [NotifyPropertyChangedFor(nameof(Badge))]
    public partial bool IsChosen { get; set; }

    /// <summary>True when this is the image already saved beside the file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    public partial bool IsCurrent { get; set; }

    public ImageSource? Thumb { get; set; }

    public string Caption => string.IsNullOrWhiteSpace(Option.Dimensions)
        ? Option.LanguageLabel
        : $"{Option.Dimensions}  ·  {Option.LanguageLabel}";

    /// <summary>
    /// "Saved" for what is already on disk, "Will save" for a fresh choice. The
    /// difference is the whole question the window answers.
    /// </summary>
    public string Badge => IsCurrent ? "✓ saved" : "✓ will save";

    public string Tip => IsCurrent
        ? "This is the image currently saved beside your files."
        : "Click to use this one.";

    public Brush BorderBrush => IsChosen
        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
        : Brushes.Transparent;

    /// <summary>
    /// Logos are transparent PNGs. On a dark window a white logo is invisible,
    /// so they get a light plate behind them.
    /// </summary>
    public Brush Backdrop => Option.Kind == ArtKind.ClearLogo
        ? new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2))
        : Brushes.Transparent;
}

/// <summary>A language filter across the wall.</summary>
public partial class LangChip : ObservableObject
{
    public required string Label { get; init; }

    /// <summary>Null for "everything"; empty for "no text on it".</summary>
    public required string? Code { get; init; }

    [ObservableProperty] public partial bool IsOn { get; set; }
}

/// <summary>One line of "here is what saving would do".</summary>
public sealed class SummaryPill
{
    public required string Text { get; init; }
    public required Brush Brush { get; init; }
}

public partial class ArtworkWindow
{
    private readonly PyreMediaSettings _settings;
    private readonly IReadOnlyList<string> _targets;
    private readonly Func<CancellationToken, Task<ArtworkSet>> _fetch;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<ArtKind, List<ArtTile>> _tiles = new();

    public ObservableCollection<LangChip> Languages { get; } = [];
    public ObservableCollection<SummaryPill> Pills { get; } = [];

    private ArtworkSet? _set;
    private ArtKind _kind = ArtKind.Poster;
    private string? _langFilter;          // null = all, "" = textless
    private bool _loading;

    /// <summary>
    /// Where a series' artwork goes, or null for a film.
    ///
    /// A poster describes a whole series, so for television it belongs in the
    /// show's folder and is written once. Beside each episode it would be the
    /// same picture fifty times over, which is what an episode's own frame is
    /// there to avoid.
    /// </summary>
    private readonly string? _seriesFolder;

    public ArtworkWindow(
        PyreMediaSettings settings,
        string title,
        IReadOnlyList<string> targetFiles,
        Func<CancellationToken, Task<ArtworkSet>> fetch,
        string? seriesFolder = null)
    {
        InitializeComponent();

        _settings = settings;
        _targets = targetFiles;
        _fetch = fetch;
        _seriesFolder = seriesFolder;

        LangChips.ItemsSource = Languages;
        Summary.ItemsSource = Pills;

        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 60) Height = Math.Max(MinHeight, work.Height - 60);
        if (Width > work.Width - 60) Width = Math.Max(MinWidth, work.Width - 60);

        TxtTitle.Text = title;
        TxtScope.Text = targetFiles.Count == 1
            ? Path.GetFileName(targetFiles[0])
            : $"{targetFiles.Count} files";

        foreach (var k in new[] { ArtKind.Poster, ArtKind.Fanart, ArtKind.ClearLogo })
            _tiles[k] = [];

        SetKindLabels(null);
        UpdateKindHelp();
        RefreshPills();

        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _http.Dispose();
    }

    // ---------------- Loading ----------------

    private async Task LoadAsync()
    {
        Spinner.Visibility = Visibility.Visible;
        TxtStatus.Text = "Asking TMDb what artwork it has...";
        TxtEmpty.Visibility = Visibility.Collapsed;

        try
        {
            _set = await _fetch(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Spinner.Visibility = Visibility.Collapsed;
            TxtStatus.Text = $"Couldn't load artwork: {ex.Message}";
            ShowEmpty("Nothing could be loaded. Check the connection and try again.");
            return;
        }

        Spinner.Visibility = Visibility.Collapsed;
        SetKindLabels(_set);

        if (_set.IsEmpty)
        {
            TxtStatus.Text = "";
            ShowEmpty("TMDb has no artwork at all for this title.");
            return;
        }

        BuildTiles();
        ShowKind(_kind);
    }

    /// <summary>Counts on the buttons, so the choice is informed before it's made.</summary>
    private void SetKindLabels(ArtworkSet? set)
    {
        RbPoster.Content = set is null ? "Poster" : $"Poster  ({set.Posters.Count})";
        RbFanart.Content = set is null ? "Fanart" : $"Fanart  ({set.Fanart.Count})";
        RbLogo.Content = set is null ? "Clear logo" : $"Clear logo  ({set.Logos.Count})";
    }

    private void BuildTiles()
    {
        if (_set is null) return;

        foreach (var kind in _tiles.Keys.ToList())
        {
            var list = new List<ArtTile>();
            var current = CurrentlySaved(kind);

            foreach (var option in _set.For(kind))
                list.Add(new ArtTile { Option = option, Thumb = Load(option.ThumbUrl) });

            // Mark what is already on disk, and treat it as the starting choice -
            // so "save" with nothing touched changes nothing.
            if (current is not null)
            {
                var match = list.FirstOrDefault(t => t.Option.Url == current);
                if (match is not null) { match.IsCurrent = true; match.IsChosen = true; }
            }

            _tiles[kind] = list;
        }
    }

    // ---------------- Kind ----------------

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _kind = RbFanart.IsChecked == true ? ArtKind.Fanart
              : RbLogo.IsChecked == true ? ArtKind.ClearLogo
              : ArtKind.Poster;

        _langFilter = null;      // a filter from one kind means nothing in another
        UpdateKindHelp();
        ShowKind(_kind);
    }

    private void UpdateKindHelp() => TxtKindHelp.Text = _kind switch
    {
        ArtKind.Poster =>
            "The upright cover art, shown in lists and on the film's page. "
            + "Saved as <name>-poster.jpg beside each file.",
        ArtKind.Fanart =>
            "The wide background image shown behind the menus while browsing. "
            + "Saved as <name>-fanart.jpg beside each file.",
        _ =>
            "The title written as a transparent graphic, laid over the fanart by most skins. "
            + "Saved as <name>-clearlogo.png - a PNG, because the transparency is the point."
    };

    private void ShowKind(ArtKind kind)
    {
        BuildLangChips(kind);
        ApplyFilter();
    }

    // ---------------- Filtering ----------------

    /// <summary>
    /// Chips for the languages actually present, commonest first. Built from the
    /// data rather than a fixed list, so there is no mapping to get wrong and
    /// nothing offered that would return nothing.
    /// </summary>
    private void BuildLangChips(ArtKind kind)
    {
        Languages.Clear();

        var tiles = _tiles[kind];
        if (tiles.Count == 0) { FilterBar.Visibility = Visibility.Collapsed; return; }

        Languages.Add(new LangChip { Label = $"All  ({tiles.Count})", Code = null, IsOn = _langFilter is null });

        var textless = tiles.Count(t => string.IsNullOrWhiteSpace(t.Option.Language));
        if (textless > 0)
        {
            Languages.Add(new LangChip
            {
                Label = $"No text  ({textless})",
                Code = "",
                IsOn = _langFilter == ""
            });
        }

        var byLang = tiles
            .Where(t => !string.IsNullOrWhiteSpace(t.Option.Language))
            .GroupBy(t => t.Option.Language!)
            .OrderByDescending(g => g.Count())
            .Take(8);

        foreach (var g in byLang)
        {
            Languages.Add(new LangChip
            {
                Label = $"{g.Key.ToUpperInvariant()}  ({g.Count()})",
                Code = g.Key,
                IsOn = _langFilter == g.Key
            });
        }

        // One chip is not a choice.
        FilterBar.Visibility = Languages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLangChip(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleButton { Tag: LangChip chip }) return;

        _langFilter = chip.Code;

        foreach (var c in Languages) c.IsOn = c.Code == _langFilter;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var tiles = _tiles[_kind];

        var shown = _langFilter is null
            ? tiles
            : [.. tiles.Where(t => _langFilter.Length == 0
                                   ? string.IsNullOrWhiteSpace(t.Option.Language)
                                   : t.Option.Language == _langFilter)];

        Gallery.ItemsSource = shown;

        if (shown.Count == 0)
        {
            ShowEmpty(tiles.Count == 0
                ? _kind switch
                {
                    ArtKind.ClearLogo => "TMDb has no clear logo for this title. Plenty of films don't have one.",
                    ArtKind.Fanart => "TMDb has no fanart for this title.",
                    _ => "TMDb has no posters for this title."
                }
                : "Nothing in that language. Try another chip, or All.");
        }
        else
        {
            TxtEmpty.Visibility = Visibility.Collapsed;
        }

        RefreshPills();
    }

    private void ShowEmpty(string message)
    {
        TxtEmpty.Text = message;
        TxtEmpty.Visibility = Visibility.Visible;
        Gallery.ItemsSource = null;
    }

    // ---------------- Choosing ----------------

    private void OnPick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ArtTile tile }) return;

        // Clicking the one already chosen clears it - that is how you say
        // "leave this kind alone" once you've picked something.
        var wasChosen = tile.IsChosen;

        foreach (var t in _tiles[_kind]) t.IsChosen = false;
        tile.IsChosen = !wasChosen;

        RefreshPills();
    }

    /// <summary>
    /// What saving would do, per kind, said before the button is pressed rather
    /// than reported after.
    /// </summary>
    private void RefreshPills()
    {
        Pills.Clear();

        var dim = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

        var pending = 0;

        foreach (var (kind, name) in new[]
                 {
                     (ArtKind.Poster, "Poster"),
                     (ArtKind.Fanart, "Fanart"),
                     (ArtKind.ClearLogo, "Clear logo")
                 })
        {
            var pick = _tiles[kind].FirstOrDefault(t => t.IsChosen);

            var (text, brush) = pick switch
            {
                null when _tiles[kind].Any(t => t.IsCurrent) => ($"{name}: keeping what's saved", dim),
                null => ($"{name}: none", dim),
                { IsCurrent: true } => ($"{name}: unchanged", dim),
                _ => ($"{name}: will be saved", accent)
            };

            if (pick is { IsCurrent: false }) pending++;

            Pills.Add(new SummaryPill { Text = text, Brush = brush });
        }

        BtnApply.IsEnabled = pending > 0;

        TxtStatus.Text = pending == 0
            ? "Nothing to save yet - click an image above."
            : _seriesFolder is { } folder
                ? $"{pending} image(s) will be written into {Path.GetFileName(folder)}, "
                  + "where they cover the whole series."
                : $"{pending} image(s) will be written beside "
                  + (_targets.Count == 1 ? "this file." : $"all {_targets.Count} files.");
    }

    /// <summary>
    /// Every place one kind of image should be written.
    ///
    /// For a series that is one file in the show's folder. For a film it is one
    /// beside each video, which is how a folder holding two films keeps two
    /// posters.
    /// </summary>
    private IEnumerable<string> Destinations(ArtKind kind) =>
        _seriesFolder is { } folder
            ? [ArtworkWriter.FolderPathFor(folder, kind)]
            : _targets.Select(t => ArtworkWriter.PathFor(t, kind));

    /// <summary>
    /// Whatever is saved already, if it was chosen here. It cannot be read back
    /// out of the image, so it is remembered when written.
    /// </summary>
    private string? CurrentlySaved(ArtKind kind)
    {
        var path = Destinations(kind).FirstOrDefault();
        if (path is null) return null;

        return File.Exists(path) ? _settings.ChosenArtwork.GetValueOrDefault(path) : null;
    }

    private static BitmapImage? Load(string url)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(url);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 360;      // the tile is 184 wide; decoding full size is waste
            img.EndInit();
            return img;
        }
        catch (Exception)
        {
            return null;   // a thumbnail that won't load isn't worth failing over
        }
    }

    // ---------------- Saving ----------------

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        BtnApply.IsEnabled = false;
        Spinner.Visibility = Visibility.Visible;
        _loading = true;

        var written = 0;
        var failed = 0;

        // Guarded because this is an async void handler: an exception escaping
        // one does not reach a caller, it reaches the dispatcher, and an
        // unhandled exception there ends the process. Downloading artwork
        // touches the network and the disk, so it has two good reasons to
        // throw, and losing the whole program to a failed download would be a
        // poor trade. The finally matters as much - without it a throw leaves
        // the spinner turning and Apply disabled for ever.
        try
        {

        // A deliberate choice replaces what is there. Preserve-existing is about
        // not overwriting things unasked, and this was asked.
        var forced = new PyreMediaSettings
        {
            DownloadArtwork = true,
            PreserveExistingArtwork = false
        };

            foreach (var (kind, tiles) in _tiles)
            {
                var pick = tiles.FirstOrDefault(t => t.IsChosen && !t.IsCurrent);
                if (pick is null) continue;

                // A series has one poster and it goes in the show's folder. A
                // film's goes beside the film, which is where a film's belongs.
                foreach (var path in Destinations(kind))
                {
                    TxtStatus.Text = $"Saving {kind}  -  {Path.GetFileName(path)}";

                    var r = await ArtworkWriter.WriteToAsync(_http, path, kind, pick.Option.Url, forced);

                    if (r.Outcome == ArtworkWriter.Outcome.Written)
                    {
                        written++;
                        _settings.ChosenArtwork[r.Path] = pick.Option.Url;
                    }
                    else if (r.Outcome == ArtworkWriter.Outcome.Failed)
                    {
                        failed++;
                        AppLog.Info($"artwork: {Path.GetFileName(path)} {kind} - {r.Detail}");
                    }
                }

                // What was chosen is now what's saved.
                foreach (var t in tiles) t.IsCurrent = false;
                pick.IsCurrent = true;
            }

            _settings.Save();

            TxtStatus.Text = failed == 0
                ? $"Saved {written} image(s)."
                : $"Saved {written}, {failed} failed - see the log for why.";
        }
        catch (Exception ex)
        {
            AppLog.Info($"artwork: apply failed - {ex.Message}");

            TxtStatus.Text = written > 0
                ? $"Saved {written} image(s), then stopped: {ex.Message}"
                : $"Could not save: {ex.Message}";
        }
        finally
        {
            _loading = false;
            Spinner.Visibility = Visibility.Collapsed;
            BtnApply.IsEnabled = true;
            RefreshPills();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
