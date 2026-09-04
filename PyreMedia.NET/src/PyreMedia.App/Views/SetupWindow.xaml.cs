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
    [NotifyPropertyChangedFor(nameof(CanFetch))]
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
    [NotifyPropertyChangedFor(nameof(CanFetch))]
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

    /// <summary>
    /// Offered when there is no winget package but the author publishes builds
    /// on GitHub, so the answer to "how do I get this" is a button rather than
    /// a web page and a decision about which of a dozen files to take.
    /// </summary>
    public bool CanFetch => !Tool.Found && !Busy && ToolLocator.CanFetch(Tool) && !CanInstall;

    public string FetchLabel => $"Get {Tool.Name}";

    public bool ShowDownload => !Tool.Found && !Busy;
    public bool IsIdle => !Busy;

    public void Refresh()
    {
        Revision++;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(CanFetch));
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

    /// <summary>So closing the window after Next or Skip doesn't save twice.</summary>
    private bool _saved;

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

        TxtSetupGcd.Text = settings.GcdDatabasePath;
        TxtSetupMetronUser.Text = settings.MetronUser;
        TxtSetupMetronPass.Password = settings.MetronPassword;
        TxtSetupComicVine.Text = settings.ComicVineApiKey;

        // Say whether a path already recorded still holds a real dump - a file
        // moved or half-unpacked since it was chosen is otherwise only
        // discovered at the first search.
        if (settings.GcdDatabasePath is { Length: > 0 }) SayAboutGcd(settings.GcdDatabasePath);

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

        // Closing with the X counts as having seen it, and keeps what was
        // answered. Otherwise the wizard reappears on every launch until you
        // happen to press the right button, which is how a helpful thing
        // becomes an irritating one - and the key you had already pasted went
        // with it. It stays re-openable from the toolbar.
        Closed += (_, _) =>
        {
            if (_saved) return;

            try { Save(); }
            catch (Exception ex) { AppLog.Error("Saving setup on close", ex); }
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
                  "Where the titles, episode lists and artwork come from. Every service here is "
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

    /// <summary>
    /// Leave the wizard early, keeping whatever was answered on the way.
    ///
    /// This used to throw it all away. Someone who pasted a TMDb key, added
    /// their library folder, then pressed Skip on the next page lost both, and
    /// the program opened as though they had never typed anything - which
    /// reads as the app losing your work, not as a shortcut. Every control was
    /// filled from the settings to begin with, so saving the untouched ones
    /// writes back exactly what was already there.
    /// </summary>
    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Save();
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

    /// <summary>
    /// Fetch a tool from its author's own releases, for the ones no package
    /// manager carries. The user's part is one button; picking the Windows
    /// build out of a release page is not a decision worth handing over.
    /// </summary>
    private async void OnFetchTool(object sender, RoutedEventArgs e)
    {
        // async void: an unhandled throw here would take the app down.
        ToolRow? row = null;

        try
        {
            if ((sender as FrameworkElement)?.Tag is not ToolRow r) return;
            row = r;

            row.Busy = true;
            ToolLog.Text = $"Getting {row.Name}...";

            var log = new Progress<string>(line =>
            {
                var lines = (ToolLog.Text + "\n" + line).Split('\n');
                ToolLog.Text = string.Join("\n", lines.TakeLast(6));
            });

            var got = await ToolLocator.FetchAsync(row.Tool, log);

            // Remember where it landed, so it is still found next launch
            // without anything being on PATH.
            if (got is { Ok: true, Path: { } landed })
            {
                row.Tool.SetPath(_settings, landed);

                // ffmpeg and ffprobe arrive in the same archive, so a fetch of
                // one has quietly supplied the other. Every row whose tool is
                // now sitting in the fetched-tools folder is pointed at it, so
                // both cards go green from the one download.
                foreach (var other in _tools)
                {
                    if (ReferenceEquals(other, row)) continue;

                    var beside = System.IO.Path.Combine(
                        ToolLocator.ToolsFolder, System.IO.Path.GetFileName(other.Tool.Executable));

                    if (!beside.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) beside += ".exe";

                    if (System.IO.File.Exists(beside)) other.Tool.SetPath(_settings, beside);
                }

                Rebuild();
            }

            ToolLog.Text += "\n" + got.Message;

            await RecheckAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Fetch tool", ex);
            ToolLog.Text += $"\nFailed: {ex.Message}";
        }
        finally
        {
            if (row is not null) row.Busy = false;
            row?.Refresh();
            UpdateWarning();
        }
    }

    /// <summary>
    /// Rebuild each row's tool against the current settings, after something
    /// changed where a tool is expected to be.
    /// </summary>
    private void Rebuild()
    {
        var fresh = ToolLocator.All(_settings);

        foreach (var row in _tools)
        {
            if (fresh.FirstOrDefault(f => f.Name == row.Name) is { } f)
                row.Tool.Executable = f.Executable;
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

            // Every tool the page offers Browse on, because the tool itself
            // says where its path is kept. This was a switch on the name that
            // listed three of the five, and for the other two the dialog
            // opened, a file was chosen, and nothing whatever happened.
            row.Tool.SetPath(_settings, dlg.FileName);

            // And the row has to ask about the new location, or the recheck
            // immediately below re-resolves the old bare name, finds nothing -
            // which is why Browse was needed - and paints the card "not found"
            // over the path just chosen.
            Rebuild();

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

    /// <summary>
    /// Point at the downloaded Grand Comics Database dump, and say straight
    /// away whether it is one.
    ///
    /// The check has always existed and was never called from anywhere - its
    /// own summary says it is there so a wrong choice is caught when it is made
    /// rather than at the first search, which is exactly what was happening.
    /// </summary>
    private void OnPickGcd(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Where is the Grand Comics Database dump?",
            Filter = "SQLite database|*.db;*.sqlite;*.sqlite3|All files|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;

        TxtSetupGcd.Text = dlg.FileName;
        SayAboutGcd(dlg.FileName);
    }

    /// <summary>Whether that file is the database it is meant to be.</summary>
    private void SayAboutGcd(string path)
    {
        var trouble = PyreMedia.Core.Books.GcdProvider.Check(path);

        TxtGcdSays.Visibility = Visibility.Visible;

        TxtGcdSays.Text = trouble
            ?? "That is the Grand Comics Database. Comics can be matched without any key.";

        TxtGcdSays.Foreground = trouble is null
            ? (Brush)FindResource("SystemFillColorSuccessBrush")
            : (Brush)FindResource("SystemFillColorCautionBrush");
    }

    private void OnBrowseArchive(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should originals be kept?" };
        if (dlg.ShowDialog(this) == true) TxtArchive.Text = dlg.FolderName;
    }

    // ---------------- Finish ----------------

    private void BuildSummary()
    {
        // Only the tools that matter. Listing the optional ones as "missing"
        // contradicted the page that had just called their absence normal, and
        // ended a wizard with a green tick over a complaint about dovi_tool.
        var missing = _tools.Where(t => !t.Tool.Found && !t.IsOptional).Select(t => t.Name).ToList();

        var bits = new List<string>
        {
            _folders.Count == 1 ? "1 folder to scan" : $"{_folders.Count} folders to scan"
        };

        bits.Add(missing.Count == 0
            ? "every tool that matters is installed"
            : $"missing {string.Join(" and ", missing)}");

        if (ChkMkv.IsChecked == true) bits.Add("remuxing to MKV");
        if (ChkArchive.IsChecked == true) bits.Add("originals kept");

        TxtSummary.Text = string.Join(",   ", bits) + ".";

        // "Ready" over an empty key box was the wizard agreeing that nothing
        // could be searched for.
        var noKey = string.IsNullOrWhiteSpace(TxtSetupTmdb.Text);

        TxtDoneTitle.Text = noKey ? "Almost ready" : "Ready";

        TxtDoneWarn.Text = noKey
            ? "There is no TMDb key yet, so nothing can be looked up and no file can be matched. "
              + "Renaming by hand still works. Add the key in Settings whenever you like - "
              + "step two of this wizard has the link."
            : "";

        TxtDoneWarn.Visibility = noKey ? Visibility.Visible : Visibility.Collapsed;
        IconDone.Foreground = noKey
            ? (Brush)FindResource("SystemFillColorCautionBrush")
            : (Brush)FindResource("SystemFillColorSuccessBrush");
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

        _settings.GcdDatabasePath = TxtSetupGcd.Text?.Trim() ?? "";
        _settings.MetronUser = TxtSetupMetronUser.Text?.Trim() ?? "";
        _settings.MetronPassword = TxtSetupMetronPass.Password ?? "";
        _settings.ComicVineApiKey = TxtSetupComicVine.Text?.Trim() ?? "";
        _settings.ArchiveRootPath = TxtArchive.Text?.Trim() ?? "";
        _settings.MoveTvFiles = ChkMoveTv.IsChecked == true;
        _settings.MovieFolderPerMovie = ChkMovieFolder.IsChecked == true;
        _settings.WriteNfoFiles = ChkNfo.IsChecked == true;
        _settings.DownloadArtwork = ChkArt.IsChecked == true;

        // Both answered here, so neither prompts again.
        _settings.SetupCompleted = true;

        _settings.Save();
        _saved = true;

        AppLog.Info($"First-run setup completed: {_folders.Count} folder(s).");
    }
}
