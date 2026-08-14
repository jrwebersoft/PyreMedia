using System.Windows;
using System.Windows.Controls;
using PyreMedia.Core;
using PyreMedia.Core.Models;
using PyreMedia.Core.Naming;

namespace PyreMedia.App.Views;

public partial class SettingsWindow
{
    private readonly PyreMediaSettings _settings;

    public SettingsWindow(PyreMediaSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        // Don't let the dialog overflow a short display.
        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 60) Height = Math.Max(MinHeight, work.Height - 60);
        if (Width > work.Width - 60) Width = Math.Max(MinWidth, work.Width - 60);

        LoadFromSettings();

        TxtFormat.TextChanged += (_, _) => UpdatePreview();
        NumSeasonPad.ValueChanged += (_, _) => UpdatePreview();
        NumEpisodePad.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void LoadFromSettings()
    {
        foreach (var f in _settings.TvFolders)
            FolderList.Items.Add(f);

        PickPreferred.Value = _settings.PreferredLanguage;
        ChkFlagForeign.IsChecked = _settings.ReadTrackDetails;
        PickKeepAudio.Value = _settings.KeepAudioLanguages;
        PickKeepSubs.Value = _settings.KeepSubtitleLanguages;
        ChkKeepUnd.IsChecked = _settings.KeepUndeterminedLanguage;
        ChkKeepForced.IsChecked = _settings.KeepForcedSubtitles;
        ChkArchive.IsChecked = _settings.ArchiveOriginals;
        TxtArchiveRoot.Text = _settings.ArchiveRootPath;
        TxtArchiveFolder.Text = _settings.ArchiveFolderName;
        TxtArchiveSuffix.Text = _settings.ArchiveSuffix;
        TxtArchiveRoot.TextChanged += (_, _) => UpdateArchiveWarning();
        UpdateArchiveWarning();
        TxtFfmpeg.Text = _settings.FfmpegPath;
        TxtFfprobe.Text = _settings.FfprobePath;
        TxtMkvMerge.Text = _settings.MkvMergePath;
        TxtDoviTool.Text = _settings.DoviToolPath;
        ChkPreferMkv.IsChecked = _settings.PreferMkvMerge;

        // Say plainly whether each tool is actually reachable, so a missing one
        // is obvious here rather than as a failure mid-remux.
        ShowToolStatus(TxtFfmpegStatus, LinkFfmpeg, _settings.FfmpegPath, "ffmpeg");
        ShowToolStatus(TxtMkvStatus, LinkMkv, _settings.MkvMergePath, "mkvmerge");
        ShowToolStatus(TxtDoviStatus, LinkDovi, _settings.DoviToolPath, "dovi_tool");

        ChkWriteNfo.IsChecked = _settings.WriteNfoFiles;
        ChkPreserveNfo.IsChecked = _settings.PreserveExistingNfo;
        ChkMergeNfo.IsChecked = _settings.MergeExistingNfo;
        ChkDownloadArt.IsChecked = _settings.DownloadArtwork;
        ChkPreserveArt.IsChecked = _settings.PreserveExistingArtwork;
        ChkUpdateNfoRemux.IsChecked = _settings.UpdateNfoAfterRemux;
        ChkDeleteSamples.IsChecked = _settings.DeleteSamples;
        ChkDeleteJunk.IsChecked = _settings.DeleteJunkFiles;
        ChkRecycleBin.IsChecked = _settings.DeleteToRecycleBin;
        UpdateRecycleWarning();
        TxtJunkExt.Text = _settings.JunkExtensions;
        ChkRenameMovies.IsChecked = _settings.RenameMovieFiles;
        ChkKeep3D.IsChecked = _settings.Keep3DTag;
        ChkMove3D.IsChecked = _settings.Move3DToExtras;
        TxtExtrasFolder.Text = _settings.ExtrasFolderName;
        ChkMovieFolderPer.IsChecked = _settings.MovieFolderPerMovie;
        TxtMovieFileFormat.Text = _settings.MovieFileFormat;
        TxtMovieFolderFormat.Text = _settings.MovieFolderFormat;

        TxtFormat.Text = _settings.TvFileFormat;
        NumSeasonPad.Value = _settings.SeasonNumZeroPadding;
        NumEpisodePad.Value = _settings.EpisodeNumZeroPadding;
        TxtSeasonWord.Text = _settings.SeasonFolderName;
        TxtSpecials.Text = _settings.SpecialsFolderName;
        ChkRenameFolder.IsChecked = _settings.RenameShowFolder;
        TxtFolderFormat.Text = _settings.ShowFolderFormat;

        ChkRename.IsChecked = _settings.RenameTvFiles;
        ChkMove.IsChecked = _settings.MoveTvFiles;
        ChkCombineSeasons.IsChecked = _settings.CombineSplitSeasons;
        ChkFullScreen.IsChecked = _settings.PlayFullScreen;
        ChkScanOnLaunch.IsChecked = _settings.ScanOnLaunch;
        ChkOverwrite.IsChecked = _settings.OverwriteFiles;
        ChkAutoSelect.IsChecked = _settings.AutoSelectMatch;

        TxtVideoExt.Text = _settings.AllowedFileTypes;
        TxtSubExt.Text = _settings.AllowedSubtitles;

        CmbSource.SelectedIndex = _settings.EpisodeSource switch
        {
            EpisodeSource.TheTvdbLegacy => 0,
            EpisodeSource.Tmdb => 1,
            _ => 2
        };
        ChkTvMaze.IsChecked = _settings.EnableTvMaze;
        CmbKodiSource.SelectedIndex =
            _settings.KodiScraperSource.Equals("TheTVDB", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ChkKodiWarn.IsChecked = _settings.WarnKodiMismatch;
        TxtLanguage.Text = _settings.Language;
        TxtTmdbKey.Text = _settings.TmdbApiKey;
        TxtTvdbKey.Text = _settings.TvdbApiKey;
        TxtAcoustIdKey.Text = _settings.AcoustIdApiKey;

        TxtMovieDest.Text = _settings.MovieDestination;
        TxtTvDest.Text = _settings.TvDestination;
        TxtMusicDest.Text = _settings.MusicDestination;
        TxtBookDest.Text = _settings.AudiobookDestination;
        TxtBookFormat.Text = _settings.AudiobookFormat;
        TxtMusicFormat.Text = _settings.MusicFileFormat;
        UpdateMusicExample();
        TxtFilters.Text = _settings.SearchTermFilters;
    }

    private void OnMusicFormatChanged(object sender, TextChangedEventArgs e) => UpdateMusicExample();

    private void OnMusicPreset(object sender, RoutedEventArgs e)
    {
        TxtMusicFormat.Text = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "initial" => PyreMedia.Core.Music.NamingFormat.ByInitial,
            "artisttitle" => PyreMedia.Core.Music.NamingFormat.ArtistTitle,
            _ => PyreMedia.Core.Music.NamingFormat.Default
        };
    }

    /// <summary>
    /// Show what the pattern does, and say what is wrong with it as it is
    /// typed. A pattern is far easier to judge from one worked example than
    /// from reading it, and a mistake named here is a mistake never applied.
    /// </summary>
    private void UpdateMusicExample()
    {
        if (TxtMusicExample is null) return;

        var format = new PyreMedia.Core.Music.NamingFormat(TxtMusicFormat.Text);
        var problems = format.Problems();

        TxtMusicProblem.Text = string.Join("; ", problems);
        TxtMusicProblem.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        try { TxtMusicExample.Text = problems.Count == 0 ? format.Example() : ""; }
        catch { TxtMusicExample.Text = ""; }
    }

    /// <summary>
    /// Choose a library folder. The box stays editable by hand as well - a
    /// path on a drive that is not plugged in cannot be browsed to, and typing
    /// it is the only way to set one up before the disk arrives.
    /// </summary>
    private void OnPickLibrary(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string which }) return;

        var box = which switch
        {
            "movie" => TxtMovieDest,
            "tv" => TxtTvDest,
            "music" => TxtMusicDest,
            _ => TxtBookDest
        };

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Where your {which} library lives",
            InitialDirectory = System.IO.Directory.Exists(box.Text) ? box.Text : ""
        };

        if (dialog.ShowDialog() == true) box.Text = dialog.FolderName;
    }

    /// <summary>Show the user what their format actually produces.</summary>
    private void UpdatePreview()
    {
        if (TxtFormatPreview is null) return;

        try
        {
            var sample = new Episode
            {
                SeasonNumber = 1,
                Number = 5,
                Name = "Out of Gas"
            };

            var name = NameFormatter.BuildEpisodeFileName(
                TxtFormat.Text,
                "Firefly",
                sample,
                (int)(NumSeasonPad.Value ?? 2),
                (int)(NumEpisodePad.Value ?? 2),
                _settings.FilenameReplaceChar);

            TxtFormatPreview.Text = $"Example:  {name}.mkv";
        }
        catch (FormatException)
        {
            TxtFormatPreview.Text = "That format string isn't valid - check the {0}-{3} placeholders.";
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FolderNames)
        {
            // Same folder by a different spelling - trailing slash, casing - or a
            // folder already covered by one in the list. Either way it would be
            // scanned twice and every show in it listed twice.
            var already = FolderList.Items.Cast<string>()
                .Any(existing => PyreMedia.Core.Organizing.MediaScanner.SameFolder(existing, f));

            if (!already) FolderList.Items.Add(f);
        }
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is { } sel)
            FolderList.Items.Remove(sel);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.TvFolders = [.. FolderList.Items.Cast<string>()];

        if (PickPreferred.Value is { Length: > 0 } lang)
            _settings.PreferredLanguage = lang;

        _settings.ReadTrackDetails = ChkFlagForeign.IsChecked == true;
        _settings.KeepAudioLanguages = PickKeepAudio.Value;
        _settings.KeepSubtitleLanguages = PickKeepSubs.Value;
        _settings.KeepUndeterminedLanguage = ChkKeepUnd.IsChecked == true;
        _settings.KeepForcedSubtitles = ChkKeepForced.IsChecked == true;

        _settings.ArchiveOriginals = ChkArchive.IsChecked == true;
        _settings.ArchiveRootPath = TxtArchiveRoot.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(TxtArchiveFolder.Text))
            _settings.ArchiveFolderName = TxtArchiveFolder.Text.Trim();
        if (!string.IsNullOrWhiteSpace(TxtArchiveSuffix.Text))
            _settings.ArchiveSuffix = TxtArchiveSuffix.Text.Trim();

        _settings.PreferMkvMerge = ChkPreferMkv.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(TxtMkvMerge.Text)) _settings.MkvMergePath = TxtMkvMerge.Text.Trim();
        if (!string.IsNullOrWhiteSpace(TxtDoviTool.Text)) _settings.DoviToolPath = TxtDoviTool.Text.Trim();
        if (!string.IsNullOrWhiteSpace(TxtFfmpeg.Text)) _settings.FfmpegPath = TxtFfmpeg.Text.Trim();
        if (!string.IsNullOrWhiteSpace(TxtFfprobe.Text)) _settings.FfprobePath = TxtFfprobe.Text.Trim();

        _settings.WriteNfoFiles = ChkWriteNfo.IsChecked == true;
        _settings.PreserveExistingNfo = ChkPreserveNfo.IsChecked == true;
        _settings.MergeExistingNfo = ChkMergeNfo.IsChecked == true;
        _settings.DownloadArtwork = ChkDownloadArt.IsChecked == true;
        _settings.PreserveExistingArtwork = ChkPreserveArt.IsChecked == true;
        _settings.UpdateNfoAfterRemux = ChkUpdateNfoRemux.IsChecked == true;
        _settings.DeleteSamples = ChkDeleteSamples.IsChecked == true;
        _settings.DeleteJunkFiles = ChkDeleteJunk.IsChecked == true;
        _settings.DeleteToRecycleBin = ChkRecycleBin.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(TxtJunkExt.Text))
            _settings.JunkExtensions = TxtJunkExt.Text.Trim();
        _settings.RenameMovieFiles = ChkRenameMovies.IsChecked == true;
        _settings.Keep3DTag = ChkKeep3D.IsChecked == true;
        _settings.Move3DToExtras = ChkMove3D.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(TxtExtrasFolder.Text))
            _settings.ExtrasFolderName = TxtExtrasFolder.Text.Trim();
        _settings.MovieFolderPerMovie = ChkMovieFolderPer.IsChecked == true;

        if (!string.IsNullOrWhiteSpace(TxtMovieFileFormat.Text))
            _settings.MovieFileFormat = TxtMovieFileFormat.Text.Trim();

        if (!string.IsNullOrWhiteSpace(TxtMovieFolderFormat.Text))
            _settings.MovieFolderFormat = TxtMovieFolderFormat.Text.Trim();

        if (!string.IsNullOrWhiteSpace(TxtFormat.Text))
            _settings.TvFileFormat = TxtFormat.Text;

        _settings.SeasonNumZeroPadding = (int)(NumSeasonPad.Value ?? 2);
        _settings.EpisodeNumZeroPadding = (int)(NumEpisodePad.Value ?? 2);

        if (!string.IsNullOrWhiteSpace(TxtSeasonWord.Text))
            _settings.SeasonFolderName = TxtSeasonWord.Text.Trim();

        if (!string.IsNullOrWhiteSpace(TxtSpecials.Text))
            _settings.SpecialsFolderName = TxtSpecials.Text.Trim();

        _settings.RenameShowFolder = ChkRenameFolder.IsChecked == true;

        if (!string.IsNullOrWhiteSpace(TxtFolderFormat.Text))
            _settings.ShowFolderFormat = TxtFolderFormat.Text.Trim();

        _settings.RenameTvFiles = ChkRename.IsChecked == true;
        _settings.MoveTvFiles = ChkMove.IsChecked == true;
        _settings.CombineSplitSeasons = ChkCombineSeasons.IsChecked == true;
        _settings.PlayFullScreen = ChkFullScreen.IsChecked == true;
        _settings.ScanOnLaunch = ChkScanOnLaunch.IsChecked == true;
        _settings.OverwriteFiles = ChkOverwrite.IsChecked == true;
        _settings.AutoSelectMatch = ChkAutoSelect.IsChecked == true;

        if (!string.IsNullOrWhiteSpace(TxtVideoExt.Text))
            _settings.AllowedFileTypes = TxtVideoExt.Text.Trim();

        if (!string.IsNullOrWhiteSpace(TxtSubExt.Text))
            _settings.AllowedSubtitles = TxtSubExt.Text.Trim();

        _settings.EpisodeSource = CmbSource.SelectedIndex switch
        {
            0 => EpisodeSource.TheTvdbLegacy,
            1 => EpisodeSource.Tmdb,
            _ => EpisodeSource.Merged
        };

        _settings.EnableTvMaze = ChkTvMaze.IsChecked == true;
        _settings.KodiScraperSource = CmbKodiSource.SelectedIndex == 1 ? "TheTVDB" : "TMDb";
        _settings.WarnKodiMismatch = ChkKodiWarn.IsChecked == true;

        if (!string.IsNullOrWhiteSpace(TxtLanguage.Text))
            _settings.Language = TxtLanguage.Text.Trim();

        if (!string.IsNullOrWhiteSpace(TxtTmdbKey.Text))
            _settings.TmdbApiKey = TxtTmdbKey.Text.Trim();

        if (!string.IsNullOrWhiteSpace(TxtTvdbKey.Text))
            _settings.TvdbApiKey = TxtTvdbKey.Text.Trim();

        // Saved even when emptied, unlike the two above. Those are guarded so a
        // blank box cannot wipe a working key by accident - but this is the one
        // key that sends anything about the user's own files anywhere, and
        // clearing the box is how somebody turns that off. A setting you cannot
        // withdraw is not really optional.
        _settings.AcoustIdApiKey = TxtAcoustIdKey.Text.Trim();

        // Saved as typed, blanks included. A library folder somebody has
        // emptied is one they want the button to ask about again, and refusing
        // to clear it would strand a destination on a drive that has gone.
        _settings.MovieDestination = TxtMovieDest.Text.Trim();
        _settings.TvDestination = TxtTvDest.Text.Trim();
        _settings.MusicDestination = TxtMusicDest.Text.Trim();
        _settings.AudiobookDestination = TxtBookDest.Text.Trim();

        if (!string.IsNullOrWhiteSpace(TxtBookFormat.Text))
            _settings.AudiobookFormat = TxtBookFormat.Text.Trim();

        if (!string.IsNullOrWhiteSpace(TxtMusicFormat.Text))
            _settings.MusicFileFormat = TxtMusicFormat.Text.Trim();

        // Checked here rather than silently ignored at scan time. A pattern that
        // won't compile, or one that backtracks its way past a timeout, otherwise
        // just stops working with no sign that it has.
        var filters = TxtFilters.Text ?? string.Empty;

        if (BadFilterPattern(filters) is { } why)
        {
            var keep = MessageBox.Show(this,
                $"The search-term filter can't be used:\n\n{why}\n\n"
                + "Save it anyway? Until it's fixed, folder names will be searched for as they are.",
                "Search-term filter", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (keep != MessageBoxResult.Yes)
            {
                TxtFilters.Focus();
                return;
            }
        }

        _settings.SearchTermFilters = filters;

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Why a search-term filter can't be used, or null if it's fine. Tried against
    /// a name of the shape it will meet, since a pattern can compile perfectly and
    /// still take forever on real input.
    /// </summary>
    private static string? BadFilterPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        const string sample =
            "The.Show.Name.S01E01.2160p.UHD.BluRay.REMUX.HDR.DV.TrueHD.7.1.Atmos-SOMEGROUP [repack]";

        try
        {
            System.Text.RegularExpressions.Regex.Replace(
                sample, pattern, " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(250));

            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return "It takes too long to run on an ordinary filename. "
                   + "Nested repeats like (a+)+ are the usual cause.";
        }
    }

    /// <summary>Resolve a tool on PATH or as a literal path, and report it.</summary>
    private static void ShowToolStatus(TextBlock label, Wpf.Ui.Controls.HyperlinkButton link,
                                       string command, string friendly)
    {
        var found = ResolveTool(command);

        if (found is not null)
        {
            label.Text = $"Found: {found}";
            label.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
            link.Visibility = Visibility.Collapsed;
        }
        else
        {
            label.Text = $"{friendly} not found - remuxing needs it";
            label.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            link.Visibility = Visibility.Visible;
        }
    }

    private static string? ResolveTool(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        try
        {
            if (System.IO.Path.IsPathRooted(command))
                return System.IO.File.Exists(command) ? command : null;

            var exe = command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? command : command + ".exe";

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), exe);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception)
        {
            // A malformed PATH entry shouldn't break the dialog.
        }

        return null;
    }

    private void OnBrowseArchive(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should pre-remux originals be kept?" };
        if (dlg.ShowDialog(this) == true)
            TxtArchiveRoot.Text = dlg.FolderName;
    }

    private void OnClearArchive(object sender, RoutedEventArgs e) => TxtArchiveRoot.Text = "";

    /// <summary>
    /// Warn when the archive root is on a different drive from the media folders:
    /// the move becomes a full copy, which for a 20 GB remux is minutes not
    /// milliseconds, and needs the space twice over.
    /// </summary>
    private void UpdateArchiveWarning()
    {
        if (TxtArchiveWarn is null) return;

        var path = TxtArchiveRoot.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            TxtArchiveWarn.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var archiveRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));

            var different = _settings.TvFolders.Concat(_settings.MovieFolders)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(f)))
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(r => !string.Equals(r, archiveRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (different.Count > 0)
            {
                TxtArchiveWarn.Text =
                    $"This is on {archiveRoot} but some media is on {string.Join(", ", different)}. "
                    + "Archiving across drives copies the whole file instead of moving it - slow for large "
                    + "files, and it needs the space on both drives until the copy finishes.";
                TxtArchiveWarn.Visibility = Visibility.Visible;
                return;
            }
        }
        catch (Exception)
        {
            // A half-typed path isn't worth complaining about.
        }

        TxtArchiveWarn.Visibility = Visibility.Collapsed;
    }

    private void OnRecycleBinChanged(object sender, RoutedEventArgs e) => UpdateRecycleWarning();

    /// <summary>
    /// Ticking "send deletions to the Recycle Bin" promises something Windows
    /// won't deliver on a network share or a removable drive: the delete succeeds
    /// and the file is simply gone. Say so here rather than in the log afterwards.
    /// </summary>
    private void UpdateRecycleWarning()
    {
        if (TxtRecycleWarn is null) return;

        if (ChkRecycleBin.IsChecked != true)
        {
            TxtRecycleWarn.Visibility = Visibility.Collapsed;
            return;
        }

        var unprotected = _settings.TvFolders.Concat(_settings.MovieFolders)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(PyreMedia.Core.Storage.VolumeInfo.For)
            .Where(v => !v.HasRecycleBin)
            .DistinctBy(v => v.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unprotected.Count == 0)
        {
            TxtRecycleWarn.Visibility = Visibility.Collapsed;
            return;
        }

        var where = string.Join(", ", unprotected.Select(v =>
            $"{v.Root.TrimEnd('\\')} ({(v.Type == System.IO.DriveType.Network ? "network" : "removable")})"));

        TxtRecycleWarn.Text =
            $"Windows has no Recycle Bin on {where}, so deletions there are permanent whatever "
            + "this is set to. Renames can still be undone from History - deletions cannot.";
        TxtRecycleWarn.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Put everything back to defaults. Confirmed, backed up, and keeping the
    /// scan folders - those are the one thing here the user chose rather than
    /// accepted, and losing them turns a reset into a re-setup.
    /// </summary>
    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Put every option back to how it shipped?\n\n"
            + "Your scan folders and API keys are kept. Everything else - naming formats, "
            + "languages, keep rules, cleanup, remux and NFO options, tool paths, per-show "
            + "pins and offsets - goes back to default.\n\n"
            + "Your last saved settings are copied to settings.backup.json first, so this can "
            + "be undone by hand if you change your mind. Any unsaved edits on screen are "
            + "discarded.\n\n"
            + "Nothing on disk is touched, and your history is not affected.\n\n"
            + "Yes  -  reset, keeping folders and keys\n"
            + "No   -  reset everything, including folders\n"
            + "Cancel  -  change nothing",
            "Reset to defaults", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel) return;

        var keepFolders = answer == MessageBoxResult.Yes;

        try
        {
            // Back up what's on disk - the last state the user actually saved.
            // Unsaved edits still on screen are discarded, which is what "reset"
            // should mean anyway.
            var backedUp = PyreMediaSettings.Backup();

            _settings.ResetToDefaults(keepFolders, keepApiKeys: keepFolders);
            _settings.Save();

            AppLog.Info($"Settings reset to defaults (folders {(keepFolders ? "kept" : "cleared")}).");

            MessageBox.Show(this,
                "Settings are back to their defaults."
                + (backedUp ? $"\n\nThe previous settings are in:\n{PyreMediaSettings.BackupPath}" : "")
                + "\n\nThe window will close so the new settings take effect.",
                "Reset to defaults", MessageBoxButton.OK, MessageBoxImage.Information);

            // True so the caller reloads its services against the new settings.
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("Reset settings", ex);
            MessageBox.Show(this, $"Could not reset settings.\n\n{ex.Message}",
                "Reset to defaults", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
