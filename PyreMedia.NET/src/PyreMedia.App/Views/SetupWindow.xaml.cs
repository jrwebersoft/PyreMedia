using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core;
using PyreMedia.Core.Tools;

namespace PyreMedia.App.Views;

/// <summary>One tool as a card on the first page.</summary>
public partial class ToolRow : ObservableObject
{
    public required ExternalTool Tool { get; init; }

    public string Name => Tool.Name;
    public string Why => Tool.Why;
    public bool IsRequired => Tool.Need == ToolNeed.Required;
    public bool IsOptional => Tool.Need == ToolNeed.Optional;

    /// <summary>
    /// The optional one not being there is the normal state, not a problem, so
    /// it must not read as a warning next to the two that matter.
    /// </summary>
    public string NeedLabel => Tool.Need switch
    {
        ToolNeed.Required => "required",
        ToolNeed.Recommended => "recommended",
        _ => "only for repairs"
    };

    /// <summary>
    /// Named up front, because "let this app install software for you" deserves
    /// to say whose software. Both are the upstream authors' own packages.
    /// </summary>
    public string Publisher => Tool.WingetId is null
        ? Tool.Publisher
        : $"{Tool.Publisher}  -  installs via winget ({Tool.WingetId})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(ShowWithout))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(ShowDownload))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool Busy { get; set; }

    /// <summary>Bumped after every check so the bindings re-read the tool.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(ShowWithout))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(ShowDownload))]
    public partial int Revision { get; set; }

    public string StatusText => Busy ? "installing" : Tool.Found ? "installed" : "not found";

    public Brush StatusBrush => Busy
        ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x6B))
        : Tool.Found
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4D, 0x2B))
            : IsRequired
                ? new SolidColorBrush(Color.FromRgb(0x6B, 0x22, 0x1E))
                : new SolidColorBrush(Color.FromRgb(0x5A, 0x40, 0x1E));

    public string Detail => Tool.Found
        ? $"{PyreMedia.Core.AppPaths.Display(Tool.ResolvedPath ?? "")}\n{Tool.Version}"
        : $"not found as \"{Tool.Executable}\", and not on PATH";

    /// <summary>The consequence only matters while it's actually missing.</summary>
    public bool ShowWithout => !Tool.Found && !Busy;
    public string Without => Tool.Without;

    public bool CanInstall => !Tool.Found && !Busy && Tool.WingetId is not null && ToolLocator.CanInstall;
    public bool ShowDownload => !Tool.Found && !Busy;
    public bool IsIdle => !Busy;

    public void Refresh()
    {
        Revision++;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(Detail));
    }
}

/// <summary>
/// First-run setup.
///
/// Before this, a fresh install opened on an empty window whose only guidance
/// was a line in a collapsed log, and never checked that ffprobe existed even
/// though nothing works without it. The first thing a new user saw was silence.
///
/// Deliberately skippable: everything here is also in Settings, and someone who
/// knows what they're doing shouldn't be made to click through four pages.
/// </summary>
public partial class SetupWindow
{
    private readonly PyreMediaSettings _settings;
    private readonly ObservableCollection<ToolRow> _tools = [];

    /// <summary>
    /// Small numbers read better as words in a sentence, and this one is always
    /// small - it is how many outside programs the app can use.
    /// </summary>
    private static string Spell(int n) => n switch
    {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five",
        6 => "six", 7 => "seven", 8 => "eight", _ => n.ToString()
    };
    private readonly ObservableCollection<string> _folders = [];

    private int _step;
    private const int LastStep = 4;

    public SetupWindow(PyreMediaSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var t in ToolLocator.All(settings))
            _tools.Add(new ToolRow { Tool = t });

        ToolList.ItemsSource = _tools;
        FolderList.ItemsSource = _folders;

        foreach (var f in settings.TvFolders.Concat(settings.MovieFolders).Distinct(StringComparer.OrdinalIgnoreCase))
            _folders.Add(f);

        TxtSetupTmdb.Text = settings.TmdbApiKey;
        TxtSetupTvdb.Text = settings.TvdbApiKey;
        TxtSetupTmdb.TextChanged += (_, _) => UpdateWarning();

        PickLang.Value = settings.PreferredLanguage;
        PickAudio.Value = settings.KeepAudioLanguages;
        PickSubs.Value = settings.KeepSubtitleLanguages;

        // Keep the rules in step with the chosen language while they still
        // match it. A deliberate "eng;jpn" is left alone.
        PickLang.SelectionChanged += (_, _) =>
        {
            var code = PickLang.Value;
            if (string.IsNullOrWhiteSpace(code)) return;

            if (!PickAudio.Value.Contains(';')) PickAudio.Value = code;
            if (!PickSubs.Value.Contains(';')) PickSubs.Value = code;
        };

        ChkKeepUnd.IsChecked = settings.KeepUndeterminedLanguage;
        ChkKeepForced.IsChecked = settings.KeepForcedSubtitles;

        ChkMkv.IsChecked = settings.RemuxToMkv;
        ChkArchive.IsChecked = settings.ArchiveOriginals;
        TxtArchive.Text = settings.ArchiveRootPath;
        ChkMoveTv.IsChecked = settings.MoveTvFiles;
        ChkMovieFolder.IsChecked = settings.MovieFolderPerMovie;
        ChkNfo.IsChecked = settings.WriteNfoFiles;
        ChkArt.IsChecked = settings.DownloadArtwork;

        ShowStep(0);
        Loaded += async (_, _) => await RecheckAsync();

        // Closing with the X counts as having seen it. Otherwise the wizard
        // reappears on every launch until you happen to press the right button,
        // which is how a helpful thing becomes an irritating one. It stays
        // re-openable from the toolbar.
        Closed += (_, _) =>
        {
            if (_settings.SetupCompleted) return;

            _settings.SetupCompleted = true;
            _settings.Save();
        };
    }

    // ---------------- Navigation ----------------

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, LastStep);
        Steps.Value = _step;

        PageTools.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageKeys.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageFolders.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageOptions.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageDone.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        (TxtStepTitle.Text, TxtStepBlurb.Text) = _step switch
        {
            // Counted rather than written down, because the written-down number
            // was wrong within a day of a fifth tool being added and nothing
            // pointed it out. A sentence that says "four" while the list below
            // shows five undermines every other number in the window.
            0 => ("Tools",
                  $"PyreMedia uses {Spell(_tools.Count)} outside tools - ffmpeg and ffprobe "
                  + "ship together. None are bundled here, being separate projects under "
                  + "their own licences. Most install from winget, from each author's own "
                  + "package. Only the first two are needed at all: the rest each buy you "
                  + "one thing, and the window says which."),
            1 => ("Metadata keys",
                  "Where the titles, episode lists and artwork come from. Both services are "
                  + "free; the keys are registered to you rather than shipped with the "
                  + "program, because a key belongs to the account that created it."),
            2 => ("Folders",
                  "Where your TV and films live. Scanning only reads; nothing is changed without "
                  + "you approving it first."),
            3 => ("Options",
                  "The few settings worth deciding up front. Everything else has a sensible "
                  + "default and lives in Settings."),
            _ => ("Done", "")
        };

        BtnBack.IsEnabled = _step > 0;
        BtnNext.Content = _step == LastStep ? "Start using PyreMedia" : "Next";

        if (_step == LastStep) BuildSummary();
        UpdateWarning();
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == LastStep)
        {
            Save();
            DialogResult = true;
            return;
        }

        // Leaving the tools page without ffprobe is allowed, but not silently -
        // renaming will work and nothing else will.
        if (_step == 0 && _tools.Any(t => t.IsRequired && !t.Tool.Found))
        {
            var go = MessageBox.Show(this,
                "ffmpeg isn't installed yet.\n\n"
                + "Renaming and organising will work without it. Analysing tracks and remuxing "
                + "won't, and neither will the checks that protect Dolby Vision.\n\n"
                + "You can install it later from Settings.\n\nCarry on anyway?",
                "Tools missing", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (go != MessageBoxResult.Yes) return;
        }

        ShowStep(_step + 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        // Still record that setup was seen, or it reappears every launch.
        _settings.SetupCompleted = true;
        _settings.Save();

        DialogResult = false;
    }

    private void UpdateWarning()
    {
        // Name the tools that are actually missing. This used to say "mkvmerge"
        // whichever one it was, so a missing dovi_tool - which is the normal
        // state, and which the page itself labels "only for repairs" - produced
        // a warning about a tool sitting there installed.
        var missingRequired = _tools.Where(t => t.IsRequired && !t.Tool.Found)
                                    .Select(t => t.Name).ToList();

        var missingAdvised = _tools.Where(t => !t.IsRequired && !t.IsOptional && !t.Tool.Found)
                                   .Select(t => t.Name).ToList();

        TxtWarn.Text = _step switch
        {
            0 when missingRequired.Count > 0 =>
                $"{Join(missingRequired)} missing - remuxing will be unavailable.",
            0 when missingAdvised.Count > 0 =>
                $"{Join(missingAdvised)} missing - remuxes of Dolby Vision or 3D files will refuse "
                + "rather than risk them.",
            1 when string.IsNullOrWhiteSpace(TxtSetupTmdb.Text) =>
                "Without a TMDb key nothing can be searched for. You can add it later in "
                + "Settings, but matching won't work until you do.",
            2 when _folders.Count == 0 =>
                "No folders yet. You can add them later, but there'll be nothing to scan.",
            _ => ""
        };

        static string Join(List<string> names) => names.Count switch
        {
            1 => names[0] + " is",
            2 => $"{names[0]} and {names[1]} are",
            _ => string.Join(", ", names[..^1]) + $" and {names[^1]} are"
        };
    }

    // ---------------- Tools ----------------

    private async Task RecheckAsync()
    {
        await ToolLocator.RefreshAsync(_tools.Select(t => t.Tool));
        foreach (var t in _tools) t.Refresh();
        UpdateWarning();

        // Say something even when nothing changed. With every tool already
        // present the button looked inert - there was no way to tell a
        // completed check from a dead button.
        var found = _tools.Count(t => t.Tool.Found);
        TxtChecked.Text = $"Checked at {DateTime.Now:HH:mm:ss} - {found} of {_tools.Count} found.";
    }

    private async void OnRecheck(object sender, RoutedEventArgs e)
    {
        BtnRecheck.IsEnabled = false;
        TxtChecked.Text = "Checking...";

        try
        {
            // Re-read PATH first: a tool installed since this app launched
            // won't be visible in the environment it inherited.
            ToolLocator.RefreshPathFromEnvironment();
            await RecheckAsync();
        }
        finally
        {
            BtnRecheck.IsEnabled = true;
        }
    }

    private async void OnInstallTool(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ToolRow row) return;
        if (row.Tool.WingetId is not { } id) return;

        row.Busy = true;
        ToolLog.Text = $"Installing {row.Name}...";

        try
        {
            var log = new Progress<string>(line =>
            {
                // Keep the last few lines only - winget is verbose and the
                // interesting part is always the end.
                var lines = (ToolLog.Text + "\n" + line).Split('\n');
                ToolLog.Text = string.Join("\n", lines.TakeLast(6));
            });

            var ok = await ToolLocator.InstallAsync(id, log);

            // A fresh install lands on the machine PATH, which this process
            // inherited at launch and won't otherwise see.
            ToolLocator.RefreshPathFromEnvironment();
            await RecheckAsync();

            ToolLog.Text += ok
                ? $"\n{row.Name}: installed."
                : $"\n{row.Name}: winget reported a problem. Use Download instead.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Install {id}", ex);
            ToolLog.Text += $"\nFailed: {ex.Message}";
        }
        finally
        {
            row.Busy = false;
            row.Refresh();
            UpdateWarning();
        }
    }

    private void OnOpenDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ToolRow row) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = row.Tool.DownloadUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open download", ex);
        }
    }

    /// <summary>Point at an executable directly, for installs that never touch PATH.</summary>
    private async void OnBrowseTool(object sender, RoutedEventArgs e)
    {
        // async void: an unhandled throw here would take the app down rather
        // than reporting a failure.
        try
        {
            if ((sender as FrameworkElement)?.Tag is not ToolRow row) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Where is {row.Name}?",
                Filter = $"{row.Name}|{row.Name}.exe|Programs|*.exe|All files|*.*"
            };

            if (dlg.ShowDialog(this) != true) return;

            switch (row.Name)
            {
                case "ffprobe": _settings.FfprobePath = dlg.FileName; break;
                case "ffmpeg": _settings.FfmpegPath = dlg.FileName; break;
                case "mkvmerge": _settings.MkvMergePath = dlg.FileName; break;
            }

            // The row holds the old command, so rebuild against the new settings.
            var fresh = ToolLocator.All(_settings).First(t => t.Name == row.Name);
            row.Tool.ResolvedPath = ToolLocator.Resolve(fresh.Executable);

            await RecheckAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Browse for tool", ex);
            MessageBox.Show(this, $"Could not use that file.\n\n{ex.Message}",
                "Setup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------- Folders ----------------

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where do your TV shows and films live?",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FolderNames)
            if (!_folders.Contains(f, StringComparer.OrdinalIgnoreCase))
                _folders.Add(f);

        UpdateWarning();
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is string s) _folders.Remove(s);
        UpdateWarning();
    }

    private void OnBrowseArchive(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should originals be kept?" };
        if (dlg.ShowDialog(this) == true) TxtArchive.Text = dlg.FolderName;
    }

    // ---------------- Finish ----------------

    private void BuildSummary()
    {
        var missing = _tools.Where(t => !t.Tool.Found).Select(t => t.Name).ToList();

        var bits = new List<string>
        {
            _folders.Count == 1 ? "1 folder to scan" : $"{_folders.Count} folders to scan"
        };

        bits.Add(missing.Count == 0
            ? "all tools installed"
            : $"missing {string.Join(" and ", missing)}");

        if (ChkMkv.IsChecked == true) bits.Add("remuxing to MKV");
        if (ChkArchive.IsChecked == true) bits.Add("originals kept");

        TxtSummary.Text = string.Join(",   ", bits) + ".";
    }

    private void Save()
    {
        // One list. Items are classified by content, so a separate movie list
        // would only be another thing to keep in sync.
        _settings.TvFolders = [.. _folders];
        _settings.MovieFolders = [];

        if (PickLang.Value is { Length: > 0 } lang)
            _settings.PreferredLanguage = lang;

        _settings.KeepAudioLanguages = PickAudio.Value;
        _settings.KeepSubtitleLanguages = PickSubs.Value;
        _settings.KeepUndeterminedLanguage = ChkKeepUnd.IsChecked == true;
        _settings.KeepForcedSubtitles = ChkKeepForced.IsChecked == true;

        _settings.RemuxContainer = ChkMkv.IsChecked == true ? "mkv" : "keep";
        _settings.ArchiveOriginals = ChkArchive.IsChecked == true;
        _settings.TmdbApiKey = TxtSetupTmdb.Text?.Trim() ?? "";
        _settings.TvdbApiKey = TxtSetupTvdb.Text?.Trim() ?? "";
        _settings.ArchiveRootPath = TxtArchive.Text?.Trim() ?? "";
        _settings.MoveTvFiles = ChkMoveTv.IsChecked == true;
        _settings.MovieFolderPerMovie = ChkMovieFolder.IsChecked == true;
        _settings.WriteNfoFiles = ChkNfo.IsChecked == true;
        _settings.DownloadArtwork = ChkArt.IsChecked == true;

        // Both answered here, so neither prompts again.
        _settings.SetupCompleted = true;

        _settings.Save();
        AppLog.Info($"First-run setup completed: {_folders.Count} folder(s).");
    }
}
