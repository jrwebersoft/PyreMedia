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

        // --words writes the manual without taking any pictures.
        //
        // Photographing the windows takes over the screen for a minute, which is
        // not something to do to somebody mid-task just because a paragraph
        // changed. The text is most of the manual and changes far more often
        // than the windows do, so it is worth being able to rebuild on its own.
        // Every picture then leaves its labelled "picture to come" slot, which
        // is the same thing that happens when a capture fails - so a manual
        // built this way is visibly short of pictures rather than quietly so.
        var wordsOnly = args.Contains("--words");

        if (wordsOnly)
        {
            Console.WriteLine("Building the manual, words only - no pictures will be taken.");
            Console.WriteLine("Run without --words to photograph the windows.");
        }
        else
        {
            Console.WriteLine("Building the manual. Each window is shown on screen for a few");
            Console.WriteLine("seconds to be photographed - WPF renders nothing for a window that");
            Console.WriteLine("is hidden or off the desktop. Run this when the machine is free.");
        }

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
                var comics = BuildComicShelf();
                var settings = PrepareSettings(library, music, comics);

                var shots = wordsOnly
                    ? []
                    : CaptureAll(settings, library, music, comics);

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
    /// A small comic shelf whose files are real archives with real pages.
    ///
    /// Empty files would do for the video pictures because nothing reads inside
    /// them. The Books tab does: it opens each archive, counts the pages, reads
    /// ComicInfo.xml and draws page one as the cover. Photographed against empty
    /// files it is a picture of an empty grid.
    ///
    /// So these are generated - a few coloured pages each, zipped, with the
    /// sidecar a scanner would have written. Public domain titles, since these
    /// pictures get published.
    /// </summary>
    private static string BuildComicShelf()
    {
        var shelf = Path.Combine(_work, "Comics");
        Directory.CreateDirectory(shelf);

        // Deliberately not tidy, for the same reason the music fixture is not:
        // a shelf already in order shows nothing of what the screen is for. So
        // there is a run with a gap in it, an issue with no number at all, and
        // one comic carrying a page its scanner marked as an advert.
        //
        // Public domain titles only, and chosen carefully: a lapsed copyright
        // is not a lapsed trademark, so the long-running house names are out
        // even where their earliest issues are free. Eastern Color's Famous
        // Funnies and the Centaur catalogue are the safe ones - Centaur folded
        // in 1942 and its books are the stock in trade of the free comic
        // archives.
        (string Name, string Series, string? Issue, int Pages, int Total, int? Advert)[] books =
        [
            ("Famous Funnies v1 001 (1934).cbz", "Famous Funnies", "1", 8, 4, null),
            ("Famous Funnies v1 002 (1934).cbz", "Famous Funnies", "2", 8, 4, 5),
            ("Famous Funnies v1 004 (1934).cbz", "Famous Funnies", "4", 8, 4, null),
            ("Amazing Mystery Funnies 007 (1938).cbz", "Amazing Mystery Funnies", "7", 6, 0, null),
            ("Cover Gallery.cbz", "Famous Funnies", null, 3, 0, null),
        ];

        foreach (var b in books)
        {
            var path = Path.Combine(shelf, b.Name);

            using var file = File.Create(path);
            using var zip = new System.IO.Compression.ZipArchive(
                file, System.IO.Compression.ZipArchiveMode.Create);

            for (var p = 1; p <= b.Pages; p++)
            {
                var entry = zip.CreateEntry($"{p:000}.png");
                using var to = entry.Open();
                var png = Page(p, b.Series, b.Issue);
                to.Write(png, 0, png.Length);
            }

            // What a scanner would have left inside. The Volume field holds the
            // year the series began, which is how these are written in the wild
            // whatever the field is called.
            var info = new System.Text.StringBuilder();
            info.Append("<?xml version=\"1.0\"?><ComicInfo>");
            info.Append($"<Series>{b.Series}</Series>");
            if (b.Issue is not null) info.Append($"<Number>{b.Issue}</Number>");
            if (b.Total > 0) info.Append($"<Count>{b.Total}</Count>");
            info.Append("<Volume>1934</Volume><Year>1934</Year><Publisher>Eastern Color</Publisher>");
            info.Append($"<PageCount>{b.Pages}</PageCount>");

            if (b.Advert is { } ad)
                info.Append($"<Pages><Page Image=\"{ad - 1}\" Type=\"Advertisement\" /></Pages>");

            info.Append("</ComicInfo>");

            var xml = zip.CreateEntry("ComicInfo.xml");
            using var xw = new StreamWriter(xml.Open());
            xw.Write(info.ToString());
        }

        // A folder of loose images, which is what Tidy offers to pack.
        var loose = Path.Combine(shelf, "Amazing Mystery Funnies 008");
        Directory.CreateDirectory(loose);

        for (var p = 1; p <= 6; p++)
            File.WriteAllBytes(Path.Combine(loose, $"p{p:000}.png"),
                               Page(p, "Amazing Mystery Funnies", "8"));

        return shelf;
    }

    /// <summary>
    /// One page, as a PNG. Drawn rather than copied, so the manual carries no
    /// artwork belonging to anybody.
    /// </summary>
    private static byte[] Page(int number, string series, string? issue)
    {
        var visual = new System.Windows.Media.DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var w = 600.0;
            var h = 900.0;

            // A different hue per page, so a row of them reads as a comic
            // rather than as one picture repeated.
            var hue = (number * 47) % 360;

            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(FromHue(hue, 0.30, 0.86)),
                             null, new Rect(0, 0, w, h));

            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(FromHue(hue, 0.55, 0.55)),
                             null, new Rect(40, 40, w - 80, 260));

            for (var i = 0; i < 3; i++)
            {
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(FromHue(hue, 0.20, 0.95)),
                                 null, new Rect(40, 340 + i * 180, w - 80, 150));
            }

            var caption = number == 1
                ? $"{series}{(issue is null ? "" : $"  #{issue}")}"
                : $"page {number}";

            var text = new System.Windows.Media.FormattedText(
                caption, System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI"), number == 1 ? 44 : 28,
                System.Windows.Media.Brushes.White, 96);

            dc.DrawText(text, new System.Windows.Point(60, number == 1 ? 130 : 100));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            600, 900, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

        bitmap.Render(visual);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using var ms = new MemoryStream();
        encoder.Save(ms);

        return ms.ToArray();
    }

    private static System.Windows.Media.Color FromHue(double hue, double sat, double val)
    {
        var c = val * sat;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = val - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return System.Windows.Media.Color.FromRgb(
            (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
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

    private static PyreMediaSettings PrepareSettings(string library, string? music, string? comics = null)
    {
        var s = PyreMediaSettings.Load();     // scratch folder, so this is fresh
        s.TvFolders.Add(library);
        s.SetupCompleted = true;               // Remux no longer opens a modal

        if (comics is not null)
        {
            s.ComicFolders.Add(comics);
            s.ComicDestination = Path.Combine(_work, "Comic library");
        }

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
        PyreMediaSettings settings, string library, string? music, string? comics = null)
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

        // ---- the comic side ----
        //
        // Before the audio block, which returns early when there is no ffmpeg -
        // the comic pictures need none and should not be lost with it.
        if (comics is not null) CaptureComics(shots, comics, Shot);

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

    /// <summary>
    /// The comic side: the Books tab after a scan, the reader open on a real
    /// archive, and the tidy-up screen.
    ///
    /// Like the Audio tab, the Books tab is a pane inside the shell rather than
    /// a window, so this photographs the shell with that tab chosen.
    /// </summary>
    private static void CaptureComics(
        Dictionary<string, string> shots, string comics,
        Action<string, Func<Window>, int, int, double, Func<Window, bool>?> shot)
    {
        var started = false;

        bool ScannedBooks(Window w)
        {
            if (w.FindName("Tabs") is not System.Windows.Controls.TabControl tabs) return false;
            if (w.FindName("BooksPane") is not System.Windows.Controls.UserControl pane) return false;

            tabs.SelectedIndex = 2;

            var grid = pane.FindName("GridPlan") as System.Windows.Controls.DataGrid;
            var scan = pane.FindName("BtnScan") as System.Windows.Controls.Button;

            _lastState = $" [rows={grid?.Items.Count} scan-enabled={scan?.IsEnabled}]";

            if (!started && scan is { IsEnabled: true })
            {
                started = true;
                scan.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return false;
            }

            return started && scan is { IsEnabled: true } && grid is { Items.Count: > 0 };
        }

        shot("books", () => new PyreMedia.App.MainWindow(), 1280, 860, 60, ScannedBooks);
        _lastState = "";

        // The reader, opened on a generated archive. Its pages are real images
        // in a real zip, so this is the reader doing its actual job rather than
        // a picture of an empty frame.
        var one = Path.Combine(comics, "Famous Funnies v1 002 (1934).cbz");

        if (File.Exists(one))
        {
            shot("reader", () => new ComicReaderWindow(one, new RenameHistory()),
                 1100, 900, 4, null);
        }
        else
        {
            Log.Add("SKIPPED reader - the fixture comic was not written");
        }

        // Tidy, against the same shelf: it finds the folder of loose images and
        // the advert page the fixture's own ComicInfo.xml declares.
        shot("tidy", () =>
        {
            var found = PyreMedia.Core.Organizing.Tidy.Gather([], [comics]);
            return new TidyWindow(found, PyreMediaSettings.Load(), new RenameHistory());
        }, 1000, 700, 3, null);
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
