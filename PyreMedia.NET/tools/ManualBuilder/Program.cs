using System.IO;
using System.Windows;
using System.Windows.Threading;
using PyreMedia.App.Views;
using PyreMedia.Core;
using PyreMedia.Core.History;
using PyreMedia.Core.Models;
using PyreMedia.Core.Music;
using PyreMedia.Core.Organizing;

namespace ManualBuilder;

/// <summary>
/// Builds the user manual, pictures and all.
///
/// The pictures are the real windows rendered to bitmaps, not mock-ups and not
/// screen grabs, so they can't drift out of date without the build noticing:
/// change a dialog and the next run shows the change.
///
/// Everything happens against a scratch app-data folder and a throwaway library
/// of empty files. It never reads or writes the real settings, history or media.
/// </summary>
internal static class Program
{
    private static string _lastState = "";
    private static string _work = "";
    private static string _shots = "";
    private static readonly List<string> Log = [];

    [STAThread]
    private static int Main(string[] args)
    {
        var given = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        var outPath = given
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
                            "docs", "PyreMedia-Manual.html");

        outPath = Path.GetFullPath(outPath);

        Console.WriteLine("Building the manual. Each window is shown on screen for a few");
        Console.WriteLine("seconds to be photographed - WPF renders nothing for a window that");
        Console.WriteLine("is hidden or off the desktop. Run this when the machine is free.");
        Console.WriteLine();

        // Not the temp folder. Every path under it carries the account name -
        // "C:\Users\someone\AppData\Local\Temp\..." - and the settings window
        // photographed for this manual shows its media folders, so the account
        // name went into a picture, into the repository, and would have gone
        // out with the published build. Text searches never found it because it
        // was pixels.
        //
        // ProgramData has no user in the path. If it cannot be written the run
        // stops rather than quietly falling back to somewhere that can, because
        // the fallback is exactly the thing being avoided.
        _work = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PyreMedia-Manual");
        _shots = Path.Combine(_work, "shots");

        if (Directory.Exists(_work)) TryDelete(_work);
        Directory.CreateDirectory(_shots);

        // Before anything touches a path: settings, history and the log all go to
        // the scratch folder. The user's real ones are never opened.
        AppPaths.UseFolder(Path.Combine(_work, "appdata"));

        var app = new PyreMedia.App.App();
        app.InitializeComponent();

        // Every window here is opened, photographed and closed, so between any two
        // captures there is a moment with no windows open at all - which is the
        // condition WPF shuts an application down on by default. It does not fire
        // today, because that path runs from Application.Run and this tool drives
        // the dispatcher itself, but the tool's lifetime should not rest on that.
        // It decides when it is finished.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // App.OnStartup does this, but that only runs under Application.Run, and
        // this tool drives the dispatcher itself so as not to launch the real app.
        // Without it the theme brushes never resolve and every caption renders
        // near-white on near-white.
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);

        var exit = 0;
        var dispatcher = Dispatcher.CurrentDispatcher;

        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                var library = BuildLibrary();
                var music = BuildMusicLibrary();
                var settings = PrepareSettings(library, music);
                var shots = CaptureAll(settings, library, music);

                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllText(outPath, Manual.Build(shots), new System.Text.UTF8Encoding(false));

                Console.WriteLine();
                foreach (var l in Log) Console.WriteLine("  " + l);
                Console.WriteLine();
                Console.WriteLine($"Manual written to {outPath}");
                Console.WriteLine($"  {shots.Count} picture(s), {new FileInfo(outPath).Length / 1024} KB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex}");
                exit = 1;
            }

            dispatcher.InvokeShutdown();
        }));

        Dispatcher.Run();

        // --keep leaves the rendered PNGs behind so they can be looked at; the
        // pictures are the part most likely to come out wrong.
        if (args.Contains("--keep"))
            Console.WriteLine($"Pictures kept in {_shots}");
        else
            TryDelete(_work);

        return exit;
    }

    // ---------------- Fixtures ----------------

    /// <summary>
    /// A small library of empty files - enough for the windows to have something
    /// real to show. No media is copied and nothing of the user's is touched.
    /// </summary>
    private static string BuildLibrary()
    {
        var library = Path.Combine(_work, "Library");

        // Public domain throughout, titles included. These pictures go into a
        // manual that gets published, and there is no reason for it to carry
        // other people's films even as filenames.
        var show = Path.Combine(library, "cisco kid", "Season 01");
        Directory.CreateDirectory(show);

        foreach (var (n, _) in new[]
                 {
                     ("The.Cisco.Kid.S01E01.480p.WEB.x264-GROUP.mkv", 0),
                     ("The.Cisco.Kid.S01E02.480p.WEB.x264-GROUP.mkv", 0),
                     ("The.Cisco.Kid.S01E03.480p.WEB.x264-GROUP.mkv", 0),
                     ("The.Cisco.Kid.S01E04.480p.WEB.x264-GROUP.mkv", 0),
                 })
            File.WriteAllText(Path.Combine(show, n), "");

        File.WriteAllText(Path.Combine(show, "The.Cisco.Kid.S01E01.480p.WEB.x264-GROUP.eng.srt"), "");

        var movies = Path.Combine(library, "Movies");
        Directory.CreateDirectory(movies);

        foreach (var n in new[]
                 {
                     "Night.of.the.Living.Dead.1968.1080p.BluRay.x264.mkv",
                     "Metropolis (1927) 2160p HDR.mkv",
                     "nosferatu.1922.mkv",
                 })
            File.WriteAllText(Path.Combine(movies, n), "");

        return library;
    }

    /// <summary>
    /// A small music library that is actually playable.
    ///
    /// The video fixtures are empty files, which is enough because nothing reads
    /// inside them. The music side does: it reads tags, measures durations and
    /// listens to the audio, and an empty .mp3 has none of that - the Audio tab
    /// photographed against one is a picture of an empty grid.
    ///
    /// So these are generated: three seconds of silence each, tagged with ffmpeg.
    /// Returns null when ffmpeg cannot be found, and the audio pictures are then
    /// skipped rather than the run failing.
    /// </summary>
    private static string? BuildMusicLibrary()
    {
        var ffmpeg = Which("ffmpeg");
        if (ffmpeg is null) return null;

        var music = Path.Combine(_work, "Music");

        // Deliberately not tidy. A manual picture of a library that is already
        // in order shows nothing of what the screen is for, so this carries one
        // of each thing the scan reports: a duplicate, a title with a spam URL
        // in it, and one file that spells its album differently from its five
        // siblings.
        (string Folder, string File, string Title, string Artist, string Album, string Year, int Track)[] tracks =
        [
            ("Beethoven - Symphony No 5", "01 Allegro con brio.mp3", "I. Allegro con brio", "Ludwig van Beethoven", "Symphony No. 5", "1808", 1),
            ("Beethoven - Symphony No 5", "02 Andante con moto.mp3", "II. Andante con moto", "Ludwig van Beethoven", "Symphony No. 5", "1808", 2),
            ("Beethoven - Symphony No 5", "03 Scherzo.mp3", "III. Scherzo. Allegro", "Ludwig van Beethoven", "Symphony No. 5", "1808", 3),
            ("Beethoven - Symphony No 5", "04 Allegro.mp3", "IV. Allegro", "Ludwig van Beethoven", "Symphony No. 5", "1808", 4),
            ("Beethoven - Symphony No 5", "05 Coda.mp3", "IV. Allegro - Coda", "Ludwig van Beethoven", "Symphony No. 5", "1808", 5),

            // Same album, spelt with a suffix nothing else in the set has. This
            // is the "wrong only in company" case, and the Consistency picture.
            ("Beethoven - Symphony No 5", "06 Finale.mp3", "IV. Finale", "Ludwig van Beethoven", "Symphony No. 5 (Remastered)", "1808", 6),

            ("Vivaldi/The Four Seasons", "01 Spring I.mp3", "Spring - I. Allegro", "Antonio Vivaldi", "The Four Seasons", "1725", 1),
            ("Vivaldi/The Four Seasons", "02 Spring II.mp3", "Spring - II. Largo", "Antonio Vivaldi", "The Four Seasons", "1725", 2),

            // A title carrying a release-site advert - repairable exactly, and
            // what lights up the Tag problems button.
            ("Vivaldi/The Four Seasons", "03 Summer I.mp3", "Summer - I. Allegro WWW.EXAMPLE-RIPS.COM", "Antonio Vivaldi", "The Four Seasons", "1725", 3),

            ("Vivaldi/The Four Seasons", "04 Autumn I.mp3", "Autumn - I. Allegro", "Antonio Vivaldi", "The Four Seasons", "1725", 4),
            ("Vivaldi/The Four Seasons", "05 Winter I.mp3", "Winter - I. Allegro non molto", "Antonio Vivaldi", "The Four Seasons", "1725", 5),

            // The same recording twice, in two places. One of them has to go,
            // and which one is the question the duplicate rules answer.
            ("Loose ends", "spring i.mp3", "Spring - I. Allegro", "Antonio Vivaldi", "The Four Seasons", "1725", 1),
        ];

        foreach (var t in tracks)
        {
            var dir = Path.Combine(music, t.Folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);

            Run(ffmpeg,
                "-hide_banner -loglevel error -y "
                + "-f lavfi -i anullsrc=r=44100:cl=stereo -t 3 "
                + $"-metadata title=\"{t.Title}\" "
                + $"-metadata artist=\"{t.Artist}\" "
                + $"-metadata album=\"{t.Album}\" "
                + $"-metadata album_artist=\"{t.Artist}\" "
                + $"-metadata date=\"{t.Year}\" "
                + $"-metadata track=\"{t.Track}\" "
                + $"-q:a 9 \"{Path.Combine(dir, t.File)}\"");
        }

        return music;
    }

    /// <summary>Where a tool is, or null. The music scan prompts when one is missing.</summary>
    private static string? Which(string exe)
    {
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);

        return onPath
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.Combine(d.Trim(), exe + ".exe"))
            .FirstOrDefault(File.Exists);
    }

    private static void Run(string exe, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;

        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    private static PyreMediaSettings PrepareSettings(string library, string? music)
    {
        var s = PyreMediaSettings.Load();     // scratch folder, so this is fresh
        s.TvFolders.Add(library);
        s.SetupCompleted = true;               // Remux no longer opens a modal

        if (music is not null)
        {
            s.MusicFolders.Add(music);

            // The scan asks before running without these, and a modal in a build
            // tool is a hang rather than a question. Point it at what was found.
            s.FfmpegPath = Which("ffmpeg") ?? s.FfmpegPath;
            s.FfprobePath = Which("ffprobe") ?? s.FfprobePath;
        }

        s.Save();
        return s;
    }

    // ---------------- Captures ----------------

    private static Dictionary<string, string> CaptureAll(
        PyreMediaSettings settings, string library, string? music)
    {
        var shots = new Dictionary<string, string>();

        void Shot(string key, Func<Window> make, int w, int h, double settleSeconds = 2,
                  Func<Window, bool>? readyWhen = null)
        {
            try
            {
                var path = Path.Combine(_shots, key + ".png");
                Shots.Capture(make(), path, w, h, TimeSpan.FromSeconds(settleSeconds), readyWhen);
                shots[key] = path;

                // Say so rather than quietly shipping a picture of a spinner.
                if (Shots.NotReady)
                {
                    // Say what it was actually doing, rather than only that it
                    // wasn't finished - the difference between a slow scan and a
                    // broken one is the whole diagnosis.
                    Log.Add($"captured {key} - BUT IT NEVER FINISHED LOADING{_lastState}");
                }
                else Log.Add($"captured {key}");
            }
            catch (Exception ex)
            {
                Log.Add($"SKIPPED {key} - {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The first windows of a run come out blank: WPF's render thread and the
        // compositor need a window or two before RenderTargetBitmap returns
        // anything. Burn a throwaway one rather than losing real pictures to it.
        for (var i = 0; i < 2; i++)
        {
            try
            {
                var warm = new Window
                {
                    Content = new System.Windows.Controls.TextBlock { Text = "warm-up" },
                    Background = System.Windows.Media.Brushes.DarkSlateBlue
                };
                Shots.Capture(warm, Path.Combine(_shots, "_warmup.png"), 400, 300, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                // Blank or zero-sized is the expected outcome for the first one or
                // two. Said out loud rather than swallowed, because "laid out at
                // 0x0" for every warm-up means there is no interactive desktop -
                // and that diagnosis is worth more than the silence was.
                Console.WriteLine($"  warm-up {i + 1}: {ex.Message}");
            }
        }

        File.Delete(Path.Combine(_shots, "_warmup.png"));

        var history = SampleHistory(library);
        _lastState = "";


        // The main window scans on open and looks up what it finds, so it needs
        // longer than a dialog that only has to lay itself out.
        // Wait for the scan to actually finish rather than for a fixed nine
        // seconds, which produced a picture of the app mid-scan with an empty
        // library - the one window in the manual that most needs to show content.
        bool Scanned(Window w)
        {
            if (w.DataContext is not PyreMedia.App.ViewModels.MainViewModel vm) return false;

            _lastState = $" [busy={vm.IsBusy} items={vm.Items.Count} status=\"{vm.StatusText}\""
                         + $" log={string.Join(" | ", vm.Log.TakeLast(3))}]";

            return !vm.IsBusy && vm.Items.Count > 0;
        }

        Shot("main", () => new PyreMedia.App.MainWindow(), 1280, 860, 45, Scanned);
        Shot("main-narrow", () => new PyreMedia.App.MainWindow(), 720, 1000, 45, Scanned);
        Shot("setup", () => new SetupWindow(settings), 900, 700, 6);   // checks for tools
        // Settings is photographed as a fresh install rather than with the
        // fixture library loaded. It is the one window that displays folder
        // paths, and a picture of somebody's folder list is a picture of how
        // their disk is arranged - which is theirs, not documentation. A blank
        // canvas is also what a new reader actually sees on opening it.
        Shot("settings", () => new SettingsWindow(new PyreMediaSettings()), 900, 820);
        Shot("history", () => new HistoryWindow(history), 1180, 700);
        Shot("about", () => new AboutWindow(settings), 900, 780, 6);   // checks for tools
        Shot("conflict", () => new ConflictWindow(SampleConflicts(library)), 900, 640);

        Shot("remux", () =>
        {
            var files = Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories).Take(4).ToList();
            return new RemuxWindow(settings, history, files, "The Cisco Kid");
        }, 1100, 800, 4);

        // ---- the audio side ----
        //
        // Skipped rather than failed when there is no ffmpeg to generate a
        // library with: the manual is still worth building without these two.
        if (music is null)
        {
            Log.Add("SKIPPED audio + consistency - no ffmpeg, so there is no music to photograph");
            return shots;
        }

        // The Audio tab is a pane inside the shell, not a window of its own, so
        // this photographs the whole shell with that tab selected - which is
        // what the reader is actually looking at.
        //
        // The scan cannot be started before the window is shown: an unshown
        // window has no size and generates no rows. So it is kicked off on the
        // first readiness poll, and the polls after that wait for it.
        var started = false;

        bool ScannedMusic(Window w)
        {
            if (w.FindName("Tabs") is not System.Windows.Controls.TabControl tabs) return false;
            if (w.FindName("MusicPane") is not System.Windows.Controls.UserControl pane) return false;

            tabs.SelectedIndex = 1;

            var grid = pane.FindName("GridPlan") as System.Windows.Controls.DataGrid;
            var scan = pane.FindName("BtnScan") as System.Windows.Controls.Button;
            var status = pane.FindName("TxtStatus") as System.Windows.Controls.TextBlock;

            _lastState = $" [rows={grid?.Items.Count} scan-enabled={scan?.IsEnabled}"
                         + $" status=\"{status?.Text}\"]";

            if (!started && scan is { IsEnabled: true })
            {
                started = true;
                scan.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return false;
            }

            // Enabled again means the scan has finished, not merely that rows
            // have begun to appear.
            return started && scan is { IsEnabled: true } && grid is { Items.Count: > 0 };
        }

        Shot("audio", () => new PyreMedia.App.MainWindow(), 1280, 860, 90, ScannedMusic);
        _lastState = "";

        // The consistency screen is built from the real examination of the real
        // fixture files, not from hand-written rows - if the rules stop finding
        // the disagreement planted in the library, this picture goes missing and
        // says so, which is the point of generating the manual at all.
        Shot("consistency", () =>
        {
            // By folder, not by album tag. Grouping on the tag would put the one
            // file that says "Dummy (Remastered)" in a set of its own, where it
            // agrees with everything - and the disagreement this screen exists
            // to show would vanish exactly when it matters.
            var albums = MusicFileReader.ReadFolder(music)
                .GroupBy(t => Path.GetDirectoryName(t.Path) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => (IReadOnlyList<TrackTags>)g.ToList())
                .ToList();

            return new ConsistencyWindow(MusicConsistency.Examine(albums));
        }, 900, 620);

        return shots;
    }

    private static RenameHistory SampleHistory(string library)
    {
        var h = new RenameHistory();
        var season = Path.Combine(library, "cisco kid", "Season 01");

        var batch = "a1b2c3d4";
        for (var i = 1; i <= 4; i++)
        {
            h.Add(HistoryAction.Rename,
                  Path.Combine(season, $"The.Cisco.Kid.S01E0{i}.480p.WEB.x264-GROUP.mkv"),
                  Path.Combine(season, $"The Cisco Kid 01x0{i} Episode {i}.mkv"),
                  "The Cisco Kid", "72004", batch);
        }

        h.Add(HistoryAction.FolderRename,
              Path.Combine(library, "cisco kid"),
              Path.Combine(library, "The Cisco Kid (1950)"),
              "The Cisco Kid", "72004", batch);

        return h;
    }

    private static List<FileConflict> SampleConflicts(string library)
    {
        var movies = Path.Combine(library, "Movies");
        var existing = Path.Combine(movies, "Night of the Living Dead (1968).mkv");
        File.WriteAllText(existing, "");

        return
        [
            new FileConflict
            {
                SourcePath = Path.Combine(movies, "Night.of.the.Living.Dead.1968.1080p.BluRay.x264.mkv"),
                TargetPath = existing
            }
        ];
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception) { /* scratch space; a leftover is harmless */ }
    }
}
