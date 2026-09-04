using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core;
using PyreMedia.Core.History;
using PyreMedia.Core.Media;
using PyreMedia.Core.Storage;

namespace PyreMedia.App.Views;

/// <summary>One toggleable track row within a layout group.</summary>
public partial class TrackRow : ObservableObject
{
    public required MediaStream Stream { get; init; }

    [ObservableProperty]
    public partial bool Keep { get; set; }

    /// <summary>
    /// Set when the file holds more than one real video track, so choosing
    /// between them is meaningful. With a single video track there is nothing to
    /// choose and dropping it would leave nothing to play.
    /// </summary>
    public bool HasVideoChoice { get; init; }

    /// <summary>
    /// Video is normally not optional. Cover art is carried as video but is just
    /// a poster, and a second video track is a real alternative - both can go.
    /// The "at least one video survives" rule is enforced before remuxing.
    /// </summary>
    public bool CanToggle =>
        Stream.Kind != StreamKind.Video || Stream.IsCoverArt || HasVideoChoice;

    public string KindLabel => Stream.IsCoverArt ? "Cover art" : Stream.Kind.ToString();
    public string Description => Stream.Describe();

    /// <summary>Preferred language, so the note can say whether a forced track is yours.</summary>
    public string Preferred { get; init; } = "eng";

    /// <summary>True when this is the highest-quality audio track in the file.</summary>
    public bool IsBestAudio { get; init; }

    /// <summary>
    /// Which track a player should select automatically. Only meaningful for
    /// audio and subtitles; exactly one audio track carries it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    public bool CanBeDefault =>
        Stream.Kind is StreamKind.Audio or StreamKind.Subtitle
        || (Stream.Kind == StreamKind.Video && HasVideoChoice && !Stream.IsCoverArt);

    /// <summary>
    /// Whether this subtitle track comes out marked forced.
    ///
    /// Starts as whatever the file says and can be changed, because the flag is
    /// often simply wrong and nothing reveals it until you are watching. A full
    /// track marked forced turns permanent subtitles on for a film in your own
    /// language; a genuine forced track left unmarked leaves the one line of
    /// alien dialogue untranslated.
    /// </summary>
    [ObservableProperty]
    public partial bool IsForced { get; set; }

    /// <summary>
    /// Subtitles only. Audio can carry the flag and practically never should -
    /// offering it per audio track invites turning a film's only soundtrack
    /// into something a player treats as an occasional overlay.
    /// </summary>
    public bool CanBeForced => Stream.Kind == StreamKind.Subtitle;

    partial void OnIsForcedChanged(bool value) => OnPropertyChanged(nameof(Note));

    /// <summary>Raised so the owning group can clear the flag from its siblings.</summary>
    public event Action<TrackRow>? DefaultRequested;

    partial void OnIsDefaultChanged(bool value)
    {
        if (value) DefaultRequested?.Invoke(this);
    }

    public string Note
    {
        get
        {
            // Says what the file will end up with rather than what it arrived
            // with, so a flag you have just changed does not keep describing
            // the old answer.
            if (Stream.Kind == StreamKind.Subtitle && IsForced != Stream.IsForced)
            {
                return IsForced
                    ? "will be marked forced"
                    : "forced flag will be removed";
            }

            if (Stream.Kind == StreamKind.Subtitle && IsForced)
            {
                return Stream.Language == Preferred || Stream.Language == "und"
                    ? "forced - usually needed"
                    : "forced, but not your language";
            }

            // Losing the object-audio or lossless track to save a few GB is a
            // decision worth making deliberately, not by accident.
            if (Stream.Kind == StreamKind.Audio && !Keep)
            {
                if (Stream.IsAtmos) return "dropping the Atmos track";
                if (Stream.IsDtsX) return "dropping the DTS:X track";
                if (Stream.IsLossless) return "dropping a lossless track";
                if (IsBestAudio) return "dropping the highest-quality track";
            }

            if (Stream.Kind == StreamKind.Video)
            {
                // MVC is the dangerous one: ffmpeg can't see the dependent view,
                // so a remux keeps the base eye and quietly throws the 3D away.
                if (Stream.IsCoverArt) return "embedded poster image, not a second video";
                if (Stream.IsMvc) return "3D MVC - remuxing would lose the second view";

                // The one worth shouting about: no fallback means a non-DV
                // player shows visibly wrong colours, not just a flatter image.
                if (Stream.IsDolbyVisionOnly)
                    return "Dolby Vision only - no HDR10 fallback, needs a DV-capable player";

                if (Stream.IsDualLayerDv)
                    return $"dual-layer Dolby Vision - falls back to {Stream.DvFallback} without DV";

                if (Stream.IsDolbyVision)
                    return $"Dolby Vision - falls back to {Stream.DvFallback} without DV";

                if (Stream.Is3D) return $"3D ({Stream.Stereo3D})";
                if (Stream.IsNonStandardHeight) return $"non-standard height ({Stream.Height}px) - likely cropped";
            }

            return string.Empty;
        }
    }

    partial void OnKeepChanged(bool value) => OnPropertyChanged(nameof(Note));
}

public sealed partial class LayoutRow : ObservableObject
{
    /// <summary>
    /// Whether this group takes part. Remux used to be all-or-nothing across
    /// every group in the window, so trying one show meant scoping the whole
    /// dialog to it first.
    /// </summary>
    [ObservableProperty]
    public partial bool Include { get; set; } = true;

    public required string Header { get; init; }
    public required string SubHeader { get; init; }

    /// <summary>Total bytes in this group, for byte-weighted progress.</summary>
    public long TotalBytes { get; init; }
    public required ObservableCollection<TrackRow> Tracks { get; init; }
    public required List<MediaInfo> Infos { get; init; }
    public required List<string> FileNames { get; init; }

    public string FilesHeader => FileNames.Count == 1
        ? "1 file"
        : $"{FileNames.Count} files";

    /// <summary>Expanded by default only when there's one group to look at.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>Files still carrying an embedded title that doesn't match their name.</summary>
    public string? TitleWarning { get; set; }

    public bool HasTitleWarning => !string.IsNullOrEmpty(TitleWarning);

    /// <summary>Nothing kept is in your language, or nothing says what it is.</summary>
    public string? LanguageWarning { get; set; }

    public bool HasLanguageWarning => !string.IsNullOrEmpty(LanguageWarning);

    /// <summary>
    /// Something the filename claims that the file doesn't back up - Dolby
    /// Vision or 3D whose signalling has gone. Informational rather than a
    /// warning about this remux: it describes the file as it already is.
    /// </summary>
    public string? MismatchWarning { get; set; }

    public bool HasMismatch => !string.IsNullOrEmpty(MismatchWarning);
}

public partial class RemuxWindow
{
    private readonly PyreMediaSettings _settings;

    /// <summary>
    /// The nearest folder that actually names the title. "Extras", "Specials"
    /// and "Season 01" are containers, not identities - grouping by the
    /// immediate folder listed two different films as "Extras" with nothing to
    /// tell them apart.
    /// </summary>
    private string IdentifyingFolder(string file)
    {
        var dir = Path.GetDirectoryName(file);
        var immediate = Path.GetFileName(dir ?? "");
        var passed = new List<string>();

        // Bounded walk: a title folder is never more than a couple of levels up.
        for (var i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name)) break;

            // Keep the container in the label - the title alone would merge a
            // 3D copy in Extras with the main file sitting beside it.
            if (!IsContainerFolder(name))
                return passed.Count == 0
                    ? name
                    : $"{name}\\{string.Join("\\", passed)}";

            passed.Insert(0, name);
            dir = Path.GetDirectoryName(dir);
        }

        return immediate;
    }

    private bool IsContainerFolder(string name) =>
        string.Equals(name, _settings.ExtrasFolderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, _settings.ArchiveFolderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Extras", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(
            name, @"^(season|series)[\s._-]*\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private readonly RenameHistory _history;
    /// <summary>
    /// The files this window is about. A list rather than a read-only view
    /// because remuxing to MKV changes an extension, and the window has to
    /// follow its files to their new names.
    /// </summary>
    private readonly List<string> _files;
    private readonly MediaProbe _probe;

    private readonly ObservableCollection<LayoutRow> _groups = [];

    private readonly string _scope;

    public RemuxWindow(PyreMediaSettings settings, RenameHistory history,
                       IReadOnlyList<string> files, string scope = "the current scan")
    {
        InitializeComponent();

        _settings = settings;
        _history = history;
        _files = [.. files];
        _scope = scope;
        _probe = new MediaProbe(settings.FfprobePath);

        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 60) Height = Math.Max(MinHeight, work.Height - 60);
        if (Width > work.Width - 60) Width = Math.Max(MinWidth, work.Width - 60);

        PickAudio.Value = settings.KeepAudioLanguages;
        PickSubs.Value = settings.KeepSubtitleLanguages;
        GroupList.ItemsSource = _groups;

        Title = $"Remux - {scope}";
        TxtStatus.Text = $"{files.Count} file(s) from {scope}.";

        // Where the original goes used to be a first-run dialog. It is part of
        // setting the job up, so it lives on the card instead - visible every
        // time, changeable every time, and never popping over the window.
        LoadOriginalChoice();

        Loaded += async (_, _) => await CheckForOrphansAsync();
    }

    /// <summary>
    /// Deal with anything an interrupted run left behind. A crash, a power cut
    /// or the app being closed mid-remux can leave the encoder to finish alone:
    /// the output is often complete and good, but nothing was alive to verify it
    /// and swap it in. Without this it just sits there taking up space.
    /// </summary>
    private async Task CheckForOrphansAsync()
    {
        try
        {
            var exec = new RemuxExecutor(_settings, _history);
            var folders = _files.Select(f => Path.GetDirectoryName(f) ?? "").Where(d => d.Length > 0);

            var orphans = await exec.FindOrphansAsync(folders);
            if (orphans.Count == 0) return;

            var good = orphans.Where(o => o.LooksComplete).ToList();

            // Kept out of "bad" on purpose. These read as video and the file
            // they came from is gone, which makes each one the only copy of
            // its content left - not a broken leftover. They used to be listed
            // as UNUSABLE and deleted outright, with File.Delete rather than
            // the Recycle Bin, on the button that says "recover".
            var lone = orphans.Where(o => !o.LooksComplete && o.OnlyCopy).ToList();
            var bad = orphans.Except(good).Except(lone).ToList();

            var msg = $"Found {orphans.Count} unfinished remux(es) from a run that was interrupted.\n\n";

            foreach (var o in good)
                msg += $"COMPLETE  {Path.GetFileName(o.OriginalPath)}\n"
                       + $"    {o.Reason}\n"
                       + $"    {Human(o.TempBytes)} against the original's {Human(o.OriginalBytes)}\n\n";

            foreach (var o in lone)
                msg += $"ONLY COPY  {Path.GetFileName(o.TempPath)}\n"
                       + $"    {o.Reason}\n"
                       + $"    {Human(o.TempBytes)}. Left alone whatever you choose - "
                       + "rename it yourself once you have looked at it.\n\n";

            foreach (var o in bad)
                msg += $"UNUSABLE  {Path.GetFileName(o.TempPath)}\n    {o.Reason}\n\n";

            if (good.Count > 0)
            {
                msg += $"Yes  -  finish {good.Count} of them: archive the original and put the "
                       + "recovered file in its place (undoable from History)\n"
                       + "No   -  delete the broken leftovers and start again\n"
                       + "Cancel  -  leave everything exactly as it is";
            }
            else if (bad.Count > 0)
            {
                msg += $"Yes  -  delete the {bad.Count} broken one(s)\nNo  -  leave them";
            }
            else
            {
                // Nothing to decide: everything here is either the only copy of
                // something or already fine.
                MessageBox.Show(this, msg, "Unfinished remux found",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                TxtStatus.Text = $"Left {orphans.Count} leftover file(s) alone.";
                return;
            }

            var answer = MessageBox.Show(this, msg, "Unfinished remux found",
                good.Count > 0 ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;

            if (good.Count > 0 && answer == MessageBoxResult.Yes)
            {
                var adopted = 0;
                foreach (var o in good)
                {
                    try
                    {
                        if (exec.AdoptOrphan(o)) adopted++;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"Adopt orphan {o.TempPath}", ex);
                    }
                }

                foreach (var o in bad) TryDeleteOrphan(o.TempPath);

                AppLog.Info($"Recovered {adopted} interrupted remux(es).");
                TxtStatus.Text = $"Recovered {adopted} interrupted remux(es). Press Analyse to refresh.";
                return;
            }

            // With nothing usable the question is only "delete them?", and the
            // button says "No - leave them". It used to leave nothing: every
            // answer that was not Cancel - and Cancel was not offered in that
            // case - fell through to the loop below and deleted exactly the
            // files the button had just promised to keep.
            if (good.Count == 0 && answer != MessageBoxResult.Yes)
            {
                TxtStatus.Text = $"Left {orphans.Count} leftover file(s) alone.";
                return;
            }

            // "No" with usable files, or "Yes" when none were usable: discard
            // the broken ones. Never the only-copy ones - there is nothing to
            // fall back on for those.
            foreach (var o in bad) TryDeleteOrphan(o.TempPath);

            TxtStatus.Text = lone.Count > 0
                ? $"Deleted {bad.Count} leftover file(s), and left {lone.Count} that "
                  + "no longer have an original to fall back on."
                : $"Deleted {bad.Count} leftover file(s).";
        }
        catch (Exception ex)
        {
            AppLog.Error("Orphan check", ex);
        }
    }

    private static void TryDeleteOrphan(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Error($"Delete leftover {path}", ex); }
    }

    /// <summary>Set the radio buttons from the saved choice.</summary>
    private void LoadOriginalChoice()
    {
        _loadingChoice = true;

        if (!_settings.ArchiveOriginals) OptDelete.IsChecked = true;
        else if (!string.IsNullOrWhiteSpace(_settings.ArchiveRootPath)) OptArchive.IsChecked = true;
        else OptKeepBeside.IsChecked = true;

        _loadingChoice = false;
        DescribeOriginalChoice();
    }

    /// <summary>True while the controls are being set from settings, not by the user.</summary>
    private bool _loadingChoice;

    private void OnOriginalChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingChoice) return;

        // Picking the archive option with nowhere to put things is the one case
        // that needs an answer, so ask for it right there rather than storing a
        // choice that can't be carried out.
        if (OptArchive.IsChecked == true && string.IsNullOrWhiteSpace(_settings.ArchiveRootPath)
            && !ChooseArchiveFolder())
        {
            _loadingChoice = true;
            OptKeepBeside.IsChecked = true;
            _loadingChoice = false;
        }

        _settings.ArchiveOriginals = OptDelete.IsChecked != true;
        _settings.ArchiveRootPath = OptArchive.IsChecked == true ? _settings.ArchiveRootPath : "";
        _settings.Save();

        DescribeOriginalChoice();
    }

    private void OnPickArchive(object sender, RoutedEventArgs e)
    {
        if (!ChooseArchiveFolder()) return;

        _loadingChoice = true;
        OptArchive.IsChecked = true;
        _loadingChoice = false;

        _settings.ArchiveOriginals = true;
        _settings.Save();

        DescribeOriginalChoice();
    }

    private bool ChooseArchiveFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder for pre-remux originals",
            InitialDirectory = Directory.Exists(_settings.ArchiveRootPath)
                ? _settings.ArchiveRootPath
                : Path.GetDirectoryName(_files.FirstOrDefault() ?? "") ?? ""
        };

        if (dlg.ShowDialog(this) != true) return false;

        _settings.ArchiveRootPath = dlg.FolderName;
        return true;
    }

    /// <summary>
    /// Say exactly where the file will end up, including the drive - moving to
    /// another volume copies every byte, which on a 20 GB remux is the difference
    /// between instant and several minutes.
    /// </summary>
    private void DescribeOriginalChoice()
    {
        BtnPickArchive.IsEnabled = OptDelete.IsChecked != true;

        if (OptDelete.IsChecked == true)
        {
            TxtArchiveWhere.Text =
                "Goes to the Recycle Bin once the new file has been checked - and only then. "
                + "On a network or removable drive there is no Recycle Bin, so it is gone for good.";
            return;
        }

        if (OptArchive.IsChecked == true)
        {
            var root = _settings.ArchiveRootPath;

            if (string.IsNullOrWhiteSpace(root))
            {
                TxtArchiveWhere.Text = "No folder chosen yet - use Choose...";
                return;
            }

            var sameDrive = _files.Count > 0 && VolumeInfo.SameVolume(_files[0], root);

            TxtArchiveWhere.Text = AppPaths.Display(root)
                + (sameDrive
                    ? "  -  same drive as the media, so moving there is instant."
                    : "  -  a different drive from the media, so every byte is copied. "
                      + "Expect this to take a while on large files.");
            return;
        }

        var where = _files.Select(f => Path.GetDirectoryName(f) ?? "")
                          .Where(d => d.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        TxtArchiveWhere.Text =
            $"Kept in a \"{_settings.ArchiveFolderName}\" subfolder beside each file"
            + (where.Count == 1
                ? " - " + AppPaths.Display(Path.Combine(where[0], _settings.ArchiveFolderName))
                : "")
            + ". Same drive, so it is instant, and it survives emptying the Recycle Bin.";
    }

    private async void OnAnalyse(object sender, RoutedEventArgs e)
    {
        if (!await _probe.IsAvailableAsync())
        {
            MessageBox.Show(this,
                $"ffprobe could not be run.\n\nTried: {_settings.FfprobePath}\n\n" +
                "Install ffmpeg, or set the path in Settings. Remuxing needs both " +
                "ffmpeg and ffprobe.",
                "ffprobe not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.KeepAudioLanguages = PickAudio.Value;
        _settings.KeepSubtitleLanguages = PickSubs.Value;
        _settings.Save();

        BtnAnalyse.IsEnabled = false;
        BtnRemux.IsEnabled = false;
        _groups.Clear();

        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = false;
        Bar.Minimum = 0;
        Bar.Maximum = _files.Count;
        Bar.Value = 0;

        var infos = new List<MediaInfo>();

        try
        {
            // Probing a full season one file at a time is needlessly slow; each
            // call is a separate short-lived process, so a few in flight helps a
            // lot without hammering the disk.
            var done = 0;
            var gate = new SemaphoreSlim(Math.Min(6, Environment.ProcessorCount));
            var results = new MediaInfo?[_files.Count];

            var tasks = _files.Select(async (f, idx) =>
            {
                await gate.WaitAsync();
                try
                {
                    results[idx] = await _probe.ProbeAsync(f);
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    Dispatcher.Invoke(() =>
                    {
                        Bar.Value = n;
                        TxtStatus.Text = $"Reading tracks... {n}/{_files.Count}";
                    });
                }
            });

            await Task.WhenAll(tasks);
            infos.AddRange(results.OfType<MediaInfo>());

            // Only files that ought to be Dolby Vision and aren't. Reading frames
            // costs something, so it is asked of the few that look wrong rather
            // than of everything: HDR10 video with no DV declared is the shape a
            // stripped file takes.
            var suspects = infos
                .Where(i => i.Video.Any(v => v.DvProfile is null)
                            && !i.Video.Any(v => v.DvProfile is not null))
                .ToList();

            var rpu = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var i in suspects)
            {
                TxtStatus.Text = $"Checking for Dolby Vision data... {rpu.Count + 1}/{suspects.Count}";
                rpu[i.Path] = await _probe.HasDolbyVisionRpuAsync(i.Path);
            }

            var plan = new RemuxPlanner(_settings).Plan(infos, rpu);
            BuildRows(plan);

            var saving = plan.EstimatedSavingBytes;
            TxtSummary.Text = plan.HasWork
                ? $"{plan.TotalFiles} file(s), {plan.Groups.Count} distinct layout(s) — "
                  + $"{plan.FilesWithWork} would change, reclaiming roughly {Human(saving)}"
                : $"{plan.TotalFiles} file(s), {plan.Groups.Count} layout(s) — nothing to remove";

            SkippedList.ItemsSource = plan.Skipped.Select(s => $"Skipped - {s}").ToList();
            SkippedList.Visibility = plan.Skipped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Mostly not set here. BuildRows ends by calling UpdateRemuxEnabled,
            // which derives the status and the button from the ticks and keeps
            // deriving them as the user changes their mind - so this only
            // speaks when it found nothing to press. It can find work that is
            // not a dropped track, and telling somebody their files "already
            // match your keep rules" over the top of a live Remux button reads
            // as a refusal.
            if (!plan.HasWork && !BtnRemux.IsEnabled)
                TxtStatus.Text = "Every file already matches your keep rules. "
                                 + "Untick a track below to remove it anyway.";

            // The rules have been answered; give their third of the window to
            // the track list, which is the part that gets scrolled through.
            // The warning has been read by now too.
            if (plan.Groups.Count > 0)
            {
                Rules.IsExpanded = false;
                Warning.IsOpen = false;
            }

            // Only worth offering when there is more than one thing to choose
            // between.
            PickAll.Visibility = plan.Groups.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Error("Remux analyse", ex);
            TxtStatus.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            Bar.Visibility = Visibility.Collapsed;
            BtnAnalyse.IsEnabled = true;
        }
    }

    private void BuildRows(RemuxPlan plan)
    {
        foreach (var g in plan.Groups)
        {
            var sample = g.Sample;
            var keepSet = g.Files[0].Keep.Select(s => s.Index).ToHashSet();

            // Rank audio so the best track can be called out if it's being dropped.
            var best = sample.Audio
                .OrderByDescending(a => a.IsAtmos || a.IsDtsX)
                .ThenByDescending(a => a.IsLossless)
                .ThenByDescending(a => a.Channels)
                .ThenByDescending(a => a.BitRate)
                .FirstOrDefault();

            // A file with two real video tracks - a DV one and an HDR10 one, say -
            // is the only case where choosing between them means anything. With
            // one, the track isn't optional and the flag has nothing to decide.
            var realVideo = sample.Video.Count();

            var tracks = new ObservableCollection<TrackRow>(
                sample.Streams
                      .Where(s => s.Kind != StreamKind.Other)
                      .OrderBy(s => s.Kind)
                      .ThenBy(s => s.Index)
                      .Select(s => new TrackRow
                      {
                          Stream = s,
                          Keep = keepSet.Contains(s.Index),
                          Preferred = _settings.PreferredLanguage.Trim().ToLowerInvariant(),
                          IsBestAudio = best is not null && s.Index == best.Index,
                          HasVideoChoice = realVideo > 1,
                          IsDefault = s.IsDefault,
                          IsForced = s.IsForced
                      }));

            // Seed a default audio track if the file didn't declare one, and keep
            // the choice exclusive within each kind.
            if (!tracks.Any(t => t.Stream.Kind == StreamKind.Audio && t.IsDefault))
            {
                var firstKeptAudio = tracks.FirstOrDefault(t => t.Stream.Kind == StreamKind.Audio && t.Keep);
                if (firstKeptAudio is not null) firstKeptAudio.IsDefault = true;
            }

            foreach (var t in tracks)
            {
                t.DefaultRequested += chosen =>
                {
                    foreach (var other in tracks)
                        if (!ReferenceEquals(other, chosen) && other.Stream.Kind == chosen.Stream.Kind)
                            other.IsDefault = false;
                };
            }

            // Name the group after what it holds - a show name if they share one,
            // otherwise the folder. "1 file(s) with this layout" told you nothing.
            var folders = g.Files
                .Select(f => IdentifyingFolder(f.Info.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var named = folders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

            // Name them. "2 folders" was true and useless: files are grouped by
            // track layout alone, so a group can span shows that have nothing
            // to do with each other, and the header gave no way to tell whose
            // files were about to be rewritten without expanding it. Reported
            // as Strange New Worlds appearing to hold season 1 episodes - they
            // were Stranger Things, sharing a layout and a header.
            var title = named.Count == 1
                ? named[0]
                : g.FileCount == 1
                    ? g.Files[0].FileName
                    : named.Count is > 1 and <= 3
                        ? string.Join(", ", named)
                        : $"{named.Count} folders";

            var stale = g.Files.Where(f => f.Info.TitleIsStale).ToList();

            var row = new LayoutRow
            {
                Header = $"{title}  -  {g.FileCount} file(s), same track layout",
                SubHeader = g.FilesWithWork > 0
                    ? $"{g.FilesWithWork} would change  -  about {Human(g.EstimatedSavingBytes)} reclaimed"
                    : "nothing to remove",

                // Ticked by default even with nothing to remove: a group only
                // contributes work once a track is unticked, and starting it
                // excluded would silently ignore that edit.
                Include = true,
                TotalBytes = g.Files.Sum(f => SafeSize(f.Info.Path)),
                Tracks = tracks,
                Infos = [.. g.Files.Select(f => f.Info)],
                FileNames = [.. g.Files.Select(f => f.FileName)],
                IsExpanded = plan.Groups.Count == 1,
                // Grouped files share a track layout, so they share the concern.
                // Count them so one line covers the lot.
                MismatchWarning = Summarise(g),
                LanguageWarning = g.Files.FirstOrDefault(f => f.HasLanguageWarning) is { } lw
                    ? g.Files.Count(f => f.HasLanguageWarning) == 1
                        ? lw.LanguageWarning
                        : $"{g.Files.Count(f => f.HasLanguageWarning)} file(s): {lw.LanguageWarning}"
                    : null,

                TitleWarning = stale.Count > 0
                    ? $"{stale.Count} file(s) still carry an embedded title from before they were renamed "
                      + $"(e.g. \"{stale[0].Info.ContainerTitle}\"). Players and Kodi show that in preference "
                      + "to the filename. Remuxing will reset it to match the current name."
                    : null
            };

            // Whether there's work to do is decided by the ticks, and the ticks
            // change after Analyse has run. Evaluating it once left Remux dead
            // when a file matched the keep rules but a track was unticked by
            // hand - the exact case for unticking one.
            foreach (var t in row.Tracks) t.PropertyChanged += OnTickChanged;
            row.PropertyChanged += OnTickChanged;

            _groups.Add(row);
        }

        UpdateRemuxEnabled();
    }

    private void OnTickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackRow.Keep) or nameof(LayoutRow.Include))
            UpdateRemuxEnabled();
    }

    /// <summary>
    /// Mirror of the plan-building in OnRemux, so the button and the status line
    /// always agree with what pressing it would actually do.
    /// </summary>
    /// <summary>Take part in the remux, all of them or none.</summary>
    private void OnIncludeAll(object sender, RoutedEventArgs e) => Include(true);

    private void OnIncludeNone(object sender, RoutedEventArgs e) => Include(false);

    private void Include(bool on)
    {
        foreach (var g in _groups) g.Include = on;

        UpdateRemuxEnabled();
    }

    private void UpdateRemuxEnabled()
    {
        var files = 0;
        var drops = 0;
        var titles = 0;
        var blocked = 0;
        var blockedVideo = 0;

        foreach (var row in _groups)
        {
            if (!row.Include) continue;

            var keepIdx = row.Tracks.Where(t => t.Keep).Select(t => t.Stream.Index).ToHashSet();

            foreach (var info in row.Infos)
            {
                var drop = info.Streams.Count(s => s.Kind != StreamKind.Other && !keepIdx.Contains(s.Index));

                // Dropping tracks is not the only thing a remux does. A file
                // whose embedded title is still its old scene name has real
                // work waiting - the window says so, in as many words, right
                // above a Remux button that used to stay dead because nothing
                // was being removed.
                var retitle = info.TitleIsStale;

                if (drop == 0 && !retitle) continue;

                // Never strip a file to no audio at all.
                if (info.Audio.Any() && !info.Audio.Any(a => keepIdx.Contains(a.Index)))
                {
                    blocked++;
                    continue;
                }

                // Nor to no picture. Only reachable now that a second video
                // track can be unticked.
                if (info.Video.Any() && !info.Video.Any(v => keepIdx.Contains(v.Index)))
                {
                    blockedVideo++;
                    continue;
                }

                files++;
                drops += drop;
                if (retitle) titles++;
            }
        }

        BtnRemux.IsEnabled = files > 0;

        TxtStatus.Text = files > 0
            ? drops > 0
                ? $"{files} file(s) would change, {drops} track(s) removed. Press Remux."
                : $"{titles} file(s) would change - no tracks removed, but the embedded title "
                  + "is reset to match the filename. Press Remux."
            : blockedVideo > 0
                ? "That would leave a file with no video at all. Keep at least one video track."
            : blocked > 0
                ? "That would leave a file with no audio at all. Keep at least one audio track."
                : "Nothing is ticked for removal. Untick a track to drop it.";
    }

    /// <summary>
    /// Wrapper, because everything below runs before the try block that used to
    /// be the only protection - and an unhandled throw out of an async void
    /// handler takes the whole app down rather than showing an error.
    /// </summary>
    private async void OnRemux(object sender, RoutedEventArgs e)
    {
        try
        {
            await RemuxAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Remux", ex);
            TxtStatus.Text = $"Failed: {ex.Message}";

            MessageBox.Show(this, $"Remux could not start.\n\n{ex.Message}",
                "Remux", MessageBoxButton.OK, MessageBoxImage.Error);

            BtnRemux.IsEnabled = true;
            BtnAnalyse.IsEnabled = true;
            BtnStopAfter.Visibility = Visibility.Collapsed;
            BtnAbort.Visibility = Visibility.Collapsed;
            BtnClose.IsEnabled = true;
            Bar.Visibility = Visibility.Collapsed;
            TxtEta.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RemuxAsync()
    {
        // Rebuild the plans from the ticks, so manual overrides are honoured.
        var plans = new List<RemuxFilePlan>();

        foreach (var row in _groups)
        {
            if (!row.Include) continue;   // group opted out in the header

            var keepIdx = row.Tracks.Where(t => t.Keep).Select(t => t.Stream.Index).ToHashSet();

            foreach (var info in row.Infos)
            {
                var keep = info.Streams.Where(s => s.Kind == StreamKind.Other || keepIdx.Contains(s.Index)).ToList();
                var drop = info.Streams.Where(s => s.Kind != StreamKind.Other && !keepIdx.Contains(s.Index)).ToList();

                if (drop.Count == 0 && !info.TitleIsStale) continue;

                if (!keep.Any(s => s.Kind == StreamKind.Audio) && info.Audio.Any())
                    continue;   // guarded in the planner too, but never trust the UI alone

                // Same for the picture, now that a second video track can go.
                if (info.Video.Any() && !info.Video.Any(v => keep.Any(k => k.Index == v.Index)))
                    continue;

                plans.Add(new RemuxFilePlan
                {
                    Info = info,
                    Keep = keep,
                    Drop = drop,
                    DefaultAudioIndex = row.Tracks
                        .FirstOrDefault(t => t.IsDefault && t.Stream.Kind == StreamKind.Audio)?.Stream.Index,
                    DefaultSubtitleIndex = row.Tracks
                        .FirstOrDefault(t => t.IsDefault && t.Stream.Kind == StreamKind.Subtitle)?.Stream.Index,
                    ForcedSubtitles = [.. row.Tracks
                        .Where(t => t.IsForced && t.Stream.Kind == StreamKind.Subtitle)
                        .Select(t => t.Stream.Index)],
                    DefaultVideoIndex = row.Tracks
                        .FirstOrDefault(t => t.IsDefault && t.Stream.Kind == StreamKind.Video
                                             && !t.Stream.IsCoverArt)?.Stream.Index
                });
            }
        }

        if (plans.Count == 0)
        {
            MessageBox.Show(this,
                _groups.Any(g => g.Include)
                    ? "Nothing selected to remove. Untick a track in a group to drop it."
                    : "No groups are ticked. Tick at least one group header.",
                "Remux", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // MVC only survives mkvmerge - ffmpeg sees just the base view. Refuse
        // only when mkvmerge isn't available to handle it.
        var mkv = new MkvMergeExecutor(_settings, _history);
        var mkvAvailable = _settings.PreferMkvMerge && await mkv.IsAvailableAsync();

        var mvc = plans.Where(p => p.HasMvc).ToList();
        if (mvc.Count > 0 && !mvc.All(p => mkvAvailable && MkvMergeExecutor.CanHandle(p.Info.Path, _settings.RemuxToMkv)))
        {
            var proceed = MessageBox.Show(this,
                $"{mvc.Count} file(s) are 3D MVC (Blu-ray 3D), where the second eye lives in a "
                + "dependent substream.\n\nffmpeg cannot read that substream - it only sees the base view - "
                + "so remuxing with it would hand back a 2D file and the 3D would be gone for good.\n\n"
                + "mkvmerge copies tracks without decoding and preserves MVC, but it isn't available "
                + "(check the mkvmerge path in Settings).\n\n"
                + "Skip those files and remux the rest?\n\n"
                + "Yes  -  skip the 3D files\n"
                + "No   -  cancel entirely",
                "3D MVC detected", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (proceed != MessageBoxResult.Yes) return;

            plans = [.. plans.Where(p => !p.HasMvc)];
            if (plans.Count == 0)
            {
                MessageBox.Show(this, "Nothing left to remux.", "Remux",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        // Dolby Vision through ffmpeg is a measured risk, not a theoretical one:
        // its MP4 muxer dropped the DOVI configuration record on a real
        // profile-5 file here, leaving a full-length video that plays with the
        // wrong colours. mkvmerge carries it across intact.
        var dv = plans.Where(p => p.HasDolbyVision).ToList();
        var dvViaFfmpeg = dv
            .Where(p => !(mkvAvailable && MkvMergeExecutor.CanHandle(p.Info.Path, _settings.RemuxToMkv)))
            .ToList();

        if (dvViaFfmpeg.Count > 0)
        {
            var worst = dvViaFfmpeg.FirstOrDefault(p => p.HasNonFallbackDv)
                        ?? dvViaFfmpeg.FirstOrDefault(p => p.HasDualLayerDv)
                        ?? dvViaFfmpeg[0];

            var extra = worst.HasNonFallbackDv
                ? "\n\nOne of these is profile 5, which has no HDR10 fallback - if the Dolby "
                  + "Vision metadata is lost it doesn't just look flat, it plays with the wrong "
                  + "colours entirely."
                : worst.HasDualLayerDv
                    ? "\n\nOne of these is profile 7 dual-layer, where the enhancement layer is a "
                      + "separate substream that ffmpeg does not carry across."
                    : "";

            var go = MessageBox.Show(this,
                $"{dvViaFfmpeg.Count} file(s) carry Dolby Vision ({worst.DvLabel}) and would go "
                + "through ffmpeg rather than mkvmerge.\n\n"
                + "ffmpeg can drop the container-level Dolby Vision record on a straight copy. "
                + "Every output is checked afterwards and the original is kept untouched if the "
                + "check fails, so nothing can be lost - but the time spent would be wasted."
                + extra
                + "\n\nSetting the remux container to MKV and installing mkvmerge avoids this.\n\n"
                + "Yes  -  skip these files, remux the rest\n"
                + "No   -  try anyway (the check will catch it)\n"
                + "Cancel  -  stop here",
                "Dolby Vision", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (go == MessageBoxResult.Cancel) return;

            if (go == MessageBoxResult.Yes)
            {
                plans = [.. plans.Except(dvViaFfmpeg)];
                if (plans.Count == 0)
                {
                    MessageBox.Show(this, "Nothing left to remux.", "Remux",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
        }

        // Remuxing stamps the current filename into the container title, so doing
        // it before renaming bakes in the scene name and you'd have to remux
        // again to correct it.
        var unrenamed = plans.Count(p => LooksUnrenamed(p.Info.Path));
        if (unrenamed > 0)
        {
            var ask = MessageBox.Show(this,
                $"{unrenamed} of these {plans.Count} file(s) still look like release names "
                + "rather than tidy episode names.\n\n"
                + "Remuxing writes the current filename into the file's own title, which players "
                + "and Kodi show in preference to the filename. If you rename afterwards, that "
                + "title will still say the old name until you remux again.\n\n"
                + "Rename them first?\n\n"
                + "Yes  -  stop here, go and rename\n"
                + "No   -  remux anyway",
                "Rename before remuxing?", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (ask == MessageBoxResult.Yes) return;
        }

        var totalDrop = plans.Sum(p => p.Drop.Count);
        var archiveWhere = string.IsNullOrWhiteSpace(_settings.ArchiveRootPath)
            ? $"a hidden \"{_settings.ArchiveFolderName}\" folder beside each file"
            : _settings.ArchiveRootPath;

        var confirm = MessageBox.Show(this,
            $"Remux {plans.Count} file(s), removing {totalDrop} track(s)?\n\n"
            + "Each file is rebuilt without those tracks and checked before anything is replaced.\n\n"
            + (_settings.ArchiveOriginals
                ? $"The file as it is now will be kept in {archiveWhere}."
                : "The file as it is now goes to the Recycle Bin.")
            + "\n\nThe removed tracks themselves cannot be recovered separately.",
            "Confirm remux", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        BtnRemux.IsEnabled = false;
        BtnAnalyse.IsEnabled = false;
        BtnStopAfter.Visibility = Visibility.Visible;
        BtnStopAfter.IsEnabled = true;
        BtnAbort.Visibility = Visibility.Visible;
        BtnAbort.IsEnabled = true;
        BtnClose.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = false;
        Bar.Minimum = 0;
        Bar.Maximum = 1000;          // per-mille, so byte weighting has resolution
        Bar.Value = 0;
        TxtEta.Visibility = Visibility.Visible;
        TxtEta.Text = "starting...";

        _abortCts = new CancellationTokenSource();
        _stopCts = new CancellationTokenSource();
        var ct = _abortCts.Token;
        var stopCt = _stopCts.Token;

        // MKV goes through mkvmerge when it's there: faster for Matroska and
        // the only route that keeps MVC. Everything else goes to ffmpeg.
        var viaMkv = mkvAvailable
            ? plans.Where(p => MkvMergeExecutor.CanHandle(p.Info.Path, _settings.RemuxToMkv)).ToList()
            : [];
        var viaFfmpeg = plans.Except(viaMkv).ToList();

        // Progress is reported per batch, each counting from 1. Reported raw,
        // two files split across the two executors both showed "[1/1]".
        // Remap onto the whole run.
        var grandTotal = plans.Count;
        var offset = 0;

        // Weight by size: a 25 GB file is not one step of the same length as a
        // 700 MB one, and a file count alone can't say how long is left.
        //
        // Measured once, up front. Asking the filesystem again as the run went
        // along asked about files the run had already archived or recycled, so
        // every finished file weighed nothing, the total never advanced, and
        // the bar dropped back to zero on each file and never reached the end.
        var sizes = plans.ToDictionary(
            p => p.Info.Path, p => SafeSize(p.Info.Path), StringComparer.OrdinalIgnoreCase);

        long SizeOf(string path) => sizes.TryGetValue(path, out var n) ? n : 0;

        var totalBytes = Math.Max(1, sizes.Values.Sum());
        long bytesDone = 0;
        var started = DateTime.UtcNow;
        var lastIndex = 0;

        try
        {
            var progress = new Progress<RemuxProgress>(p =>
            {
                var globalIndex = offset + p.Index;

                // A new file started: bank the previous one's bytes.
                if (p.Index > lastIndex)
                {
                    if (lastIndex > 0)
                        bytesDone += SizeOf(CurrentBatch()[lastIndex - 1].Info.Path);
                    lastIndex = p.Index;
                }

                TxtStatus.Text = $"[{globalIndex}/{grandTotal}] {p.FileName} - {p.Message}";

                // Count the file in flight at however far through it is, rather
                // than as nothing until it finishes. A single large file used to
                // leave the bar frozen at 0% for its whole duration.
                var currentBytes = SizeOf(CurrentBatch().ElementAtOrDefault(p.Index - 1)?.Info.Path ?? "");
                var partial = p.Percent is { } pct ? currentBytes * pct / 100.0 : 0;

                var fraction = Math.Clamp((bytesDone + partial) / totalBytes, 0, 1);
                Bar.Value = fraction * 1000;
                TxtEta.Text = Eta(started, fraction, grandTotal - globalIndex + 1);

                List<RemuxFilePlan> CurrentBatch() => offset == 0 && viaMkv.Count > 0 ? viaMkv : viaFfmpeg;
            });

            var result = new RemuxResult();

            if (viaMkv.Count > 0)
            {
                AppLog.Info($"Remux: {viaMkv.Count} file(s) via mkvmerge");
                Merge(result, await mkv.ExecuteAsync(viaMkv, progress, ct, stopCt));
                offset = viaMkv.Count;
                lastIndex = 0;
                bytesDone = viaMkv.Sum(p => SizeOf(p.Info.Path));
            }

            if (viaFfmpeg.Count > 0 && !ct.IsCancellationRequested && !stopCt.IsCancellationRequested)
            {
                AppLog.Info($"Remux: {viaFfmpeg.Count} file(s) via ffmpeg");
                Merge(result, await new RemuxExecutor(_settings, _history)
                    .ExecuteAsync(viaFfmpeg, progress, ct, stopCt));
            }

            static void Merge(RemuxResult into, RemuxResult from)
            {
                into.Succeeded += from.Succeeded;
                into.Failed += from.Failed;
                into.Skipped += from.Skipped;
                into.BytesReclaimed += from.BytesReclaimed;
                into.Errors.AddRange(from.Errors);
                into.Stopped |= from.Stopped;
            }

            // "Reclaimed" only when something was actually freed. With the
            // originals archived - which is the default - they are moved to a
            // folder on the same volume, so free space does not go up by the
            // saving; it goes down by the size of the new file. The number is
            // still worth saying, as what deleting the archive would give back.
            var saving = _settings.ArchiveOriginals
                ? $" {Human(result.BytesReclaimed)} will be freed when you delete the archived originals."
                : $" Reclaimed {Human(result.BytesReclaimed)}.";

            var msg = $"Remuxed {result.Succeeded}."
                      + (result.Failed > 0 ? $" Failed {result.Failed}." : "")
                      + (result.Skipped > 0 ? $" Skipped {result.Skipped}." : "")
                      + saving
                      + (result.Stopped || stopCt.IsCancellationRequested
                          ? $"\n\nStopped early - {grandTotal - result.Succeeded - result.Failed} "
                            + "file(s) were left untouched."
                          : "");

            TxtStatus.Text = msg;
            AppLog.Info("Remux: " + msg);

            if (result.Errors.Count > 0)
            {
                foreach (var err in result.Errors) AppLog.Info("Remux error: " + err);
                msg += "\n\n" + string.Join("\n", result.Errors.Take(8));
            }

            MessageBox.Show(this, msg, "Remux complete", MessageBoxButton.OK,
                result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            FollowTheFiles();
            OnAnalyse(this, new RoutedEventArgs());   // refresh from disk
        }
        catch (OperationCanceledException)
        {
            // Only the hard abort throws. The part-written temp is deleted by
            // the executor and the original was never touched.
            TxtStatus.Text = "Aborted. The file being written was discarded; "
                             + "its original is unchanged. Earlier files are kept.";
            AppLog.Info("Remux: aborted by user mid-file.");
            OnAnalyse(this, new RoutedEventArgs());   // refresh so counts are honest
        }
        catch (Exception ex)
        {
            AppLog.Error("Remux", ex);
            TxtStatus.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            Bar.Visibility = Visibility.Collapsed;
            TxtEta.Visibility = Visibility.Collapsed;
            BtnStopAfter.Visibility = Visibility.Collapsed;
            BtnAbort.Visibility = Visibility.Collapsed;
            BtnClose.IsEnabled = true;
            BtnAnalyse.IsEnabled = true;

            _abortCts?.Dispose();
            _stopCts?.Dispose();
            _abortCts = null;
            _stopCts = null;
        }
    }

    /// <summary>Abort now: reaches the running encoder and kills it.</summary>
    private CancellationTokenSource? _abortCts;

    /// <summary>Stop cleanly: checked only between files.</summary>
    private CancellationTokenSource? _stopCts;

    /// <summary>
    /// Let the file in progress finish, verify and be swapped in, then stop.
    /// Nothing already spent on it is wasted.
    /// </summary>
    private void OnStopAfterCurrent(object sender, RoutedEventArgs e)
    {
        _stopCts?.Cancel();
        BtnStopAfter.IsEnabled = false;
        TxtStatus.Text = "Will stop once the current file is finished and saved...";
    }

    /// <summary>
    /// Kill the encoder now. Safe because the original is never touched until
    /// the rebuilt file has been probed and verified - so the half-written temp
    /// is simply deleted and the source stays exactly as it was. The time spent
    /// on that file is lost, which is the trade against stopping cleanly.
    /// </summary>
    private void OnAbortRemux(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(this,
            "Abort the file being written right now?\n\n"
            + "The partly-built file is discarded and the original is left exactly as it is - "
            + "nothing is lost or damaged.\n\n"
            + "The time already spent on this file is wasted. To keep it, use "
            + "\"Finish this file, then stop\" instead.",
            "Abort remux", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (ok != MessageBoxResult.Yes) return;

        _stopCts?.Cancel();    // don't start another file either
        _abortCts?.Cancel();

        BtnAbort.IsEnabled = false;
        BtnStopAfter.IsEnabled = false;
        TxtStatus.Text = "Aborting...";
    }

    /// <summary>
    /// Point the window's file list at where the files are now.
    ///
    /// Remuxing to MKV changes the extension, so the paths this window was
    /// opened with stop existing. Re-analysing them found nothing and emptied
    /// the window, which reads as the remux having destroyed everything.
    /// </summary>
    private void FollowTheFiles()
    {
        for (var i = 0; i < _files.Count; i++)
        {
            var was = _files[i];
            if (File.Exists(was)) continue;

            var now = Path.ChangeExtension(was, ".mkv");
            if (File.Exists(now)) _files[i] = now;
        }
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// Elapsed plus a remaining estimate from the fraction of bytes done. Says
    /// nothing until there's enough signal to be worth trusting.
    /// </summary>
    private static string Eta(DateTime startedUtc, double fraction, int filesLeft)
    {
        var elapsed = DateTime.UtcNow - startedUtc;
        var text = $"{Format(elapsed)} elapsed";

        // Wait for a real sample before quoting a number. Estimating off the
        // first couple of seconds produced wildly wrong figures that then
        // lurched around, which reads as broken rather than approximate.
        if (fraction > 0.03 && elapsed.TotalSeconds > 10)
        {
            var total = TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction);
            var left = total - elapsed;
            if (left > TimeSpan.Zero)
                text += $"  -  about {Format(left)} remaining";
        }
        else
        {
            text += "  -  estimating...";
        }

        return text + $"  -  {filesLeft} file(s) to go";

        static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
            : $"{t.Seconds}s";
    }

    /// <summary>
    /// Rough guess at whether a file still has its release name. Scene names
    /// carry resolution/source/codec tags that a renamed file wouldn't.
    /// </summary>
    private static bool LooksUnrenamed(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);

        return System.Text.RegularExpressions.Regex.IsMatch(
            stem,
            @"\b(?:1080p|2160p|720p|480p|web-?dl|webrip|bluray|brrip|bdrip|hdtv|x26[45]|hevc|"
            + @"ddp?5[\s._-]?1|dts|truehd|amzn|nf|max|hmax|dsnp|atvp|repack|proper)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// One line for whatever the files in a group claim but don't carry. Grouped
    /// by what it is, because a whole season shares the same fault and twenty
    /// copies of the same sentence is noise.
    /// </summary>
    private static string? Summarise(TrackLayoutGroup g)
    {
        var all = g.Files.SelectMany(f => f.Mismatches).ToList();
        if (all.Count == 0) return null;

        var parts = all
            .GroupBy(m => m.Detail)
            .Select(k =>
            {
                var n = k.Count();
                return n == 1 ? k.Key : $"{n} files: {k.Key}";
            });

        return string.Join("  ", parts);
    }

    private static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B"
    };

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
