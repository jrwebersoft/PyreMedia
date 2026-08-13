using System.IO;
using System.Windows;
using System.Windows.Threading;
using PyreMedia.App.Views;
using PyreMedia.Core;
using PyreMedia.Core.History;
using PyreMedia.Core.Models;
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

        // Every window here is opened, photographed and closed, so between any
        // two captures there is a moment with no windows open - which is the
        // condition WPF shuts an application down on by default. When it fires,
        // every capture after the first fails with "the Application object is
        // being shut down" and the manual is written with no pictures in it and
        // no complaint. This tool decides when it is finished.
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
                var settings = PrepareSettings(library);
                var shots = CaptureAll(settings, library);

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

    private static PyreMediaSettings PrepareSettings(string library)
    {
        var s = PyreMediaSettings.Load();     // scratch folder, so this is fresh
        s.TvFolders.Add(library);
        s.SetupCompleted = true;               // Remux no longer opens a modal
        s.Save();
        return s;
    }

    // ---------------- Captures ----------------

    private static Dictionary<string, string> CaptureAll(PyreMediaSettings settings, string library)
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
            catch (Exception) { /* expected to be blank the first time or two */ }
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
        Shot("settings", () => new SettingsWindow(settings), 900, 820);
        Shot("history", () => new HistoryWindow(history), 1180, 700);
        Shot("about", () => new AboutWindow(settings), 900, 780, 6);   // checks for tools
        Shot("conflict", () => new ConflictWindow(SampleConflicts(library)), 900, 640);

        Shot("remux", () =>
        {
            var files = Directory.GetFiles(library, "*.mkv", SearchOption.AllDirectories).Take(4).ToList();
            return new RemuxWindow(settings, history, files, "The Cisco Kid");
        }, 1100, 800, 4);

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
