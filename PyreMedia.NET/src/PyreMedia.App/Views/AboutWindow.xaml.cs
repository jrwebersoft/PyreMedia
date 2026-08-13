using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using PyreMedia.Core;

// Both WPF and WPF-UI define TextBlock. Everything built here is plain WPF, so
// pin the ambiguous names rather than qualifying every use.
using TextBlock = System.Windows.Controls.TextBlock;
using CornerRadius = System.Windows.CornerRadius;

namespace PyreMedia.App.Views;

/// <summary>
/// Credits, dependencies and where everything lives.
///
/// Deliberately factual rather than decorative: this is where you look when a
/// tool has gone missing, when you want to know which provider named an episode,
/// or when you need to know what the app is allowed to do with someone else's
/// work. Every external tool reports whether it was actually found, so "install
/// mkvmerge" stops being guesswork.
/// </summary>
public partial class AboutWindow
{
    private readonly PyreMediaSettings _settings;
    private readonly StringBuilder _plain = new();

    public AboutWindow(PyreMediaSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        TxtVersion.Text = $"Version {v?.ToString(3) ?? "4.0.0"}   -   .NET {Environment.Version}   -   "
                          + $"{RuntimeInformation()}";

        BuildSources();
        BuildTools();
        BuildLibraries();
        BuildPaths();
    }

    private static string RuntimeInformation() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} "
        + $"({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";

    // ---------------- Sections ----------------

    private void BuildSources()
    {
        Add(PanelSources, "TMDb", "themoviedb.org",
            "https://www.themoviedb.org",
            "Search for both TV and film, all film metadata, and the episode spine in Merged mode. "
            + "Also supplies the TheTVDB id that the legacy episode pipeline is keyed on.");

        Add(PanelSources, "TheTVDB", "thetvdb.com",
            "https://thetvdb.com",
            "Episode data through the legacy XML API. Often carries season-0 specials that TMDb omits, "
            + "and numbers some long-running shows differently - which is why it can be pinned per show.");

        Add(PanelSources, "TVmaze", "tvmaze.com",
            "https://www.tvmaze.com",
            "Free and keyless. Held back as a fallback and selectable in the renumber screen: measured "
            + "across real shows it added nothing over the other two, so it is not queried by default.");

        Add(PanelSources, "fanart.tv", "fanart.tv",
            "https://fanart.tv",
            "Not queried by this app. Listed because artwork URLs in existing Kodi .nfo files often "
            + "point here, and those files are read and preserved.");
    }

    private void BuildTools()
    {
        AddTool(PanelTools, "mkvmerge (MKVToolNix)", _settings.MkvMergePath,
            PyreMediaSettings.MkvToolNixUrl,
            "Preferred engine. Copies tracks without decoding, so Dolby Vision, MVC 3D and PGS "
            + "subtitles survive. Reads MP4, AVI, TS and more; always writes Matroska.");

        AddTool(PanelTools, "ffmpeg", _settings.FfmpegPath,
            PyreMediaSettings.FfmpegUrl,
            "Fallback engine for anything mkvmerge cannot write, and the source of every "
            + "remux that is not Matroska.");

        AddTool(PanelTools, "ffprobe", _settings.FfprobePath,
            PyreMediaSettings.FfmpegUrl,
            "Reads every track, codec, language, HDR format and Dolby Vision profile. Ships with "
            + "ffmpeg. Required - without it nothing can be analysed or verified.");

        AddTool(PanelTools, "dovi_tool", _settings.DoviToolPath,
            PyreMediaSettings.DoviToolUrl,
            "Optional, by quietvoid. For one repair: a file remuxed into MP4 keeps its Dolby "
            + "Vision data but loses the record that declares it, and neither ffmpeg nor mkvmerge "
            + "rebuilds that from the stream - measured. dovi_tool can. There is no winget "
            + "package; it is a single executable from the author's releases.");
    }

    private void BuildLibraries()
    {
        Add(PanelLibs, ".NET 10 / WPF", "Microsoft, MIT",
            "https://dotnet.microsoft.com",
            "Runtime and interface framework.");

        Add(PanelLibs, "WPF-UI 4.3.0", "lepoco, MIT",
            "https://github.com/lepoco/wpfui",
            "Fluent controls, Mica backdrop and theming.");

        Add(PanelLibs, "CommunityToolkit.Mvvm 8.4.2", "Microsoft, MIT",
            "https://github.com/CommunityToolkit/dotnet",
            "Observable properties and commands.");
    }

    private void BuildPaths()
    {
        // Shown as %APPDATA%\... rather than the expanded path. It keeps the
        // account name of whoever is running out of screenshots and out of
        // Copy details, and it is the more truthful answer anyway: these live in
        // the current user's profile, so the same build on another machine writes
        // somewhere else. Explorer and Run expand it, so it still pastes.
        var root = AppPaths.Display(AppPaths.Folder);

        AddPath(PanelPaths, "Settings", Path.Combine(root, "settings.json"),
            "Every option, including your folder list and API keys.");

        AddPath(PanelPaths, "History", Path.Combine(root, "history.jsonl"),
            "Append-only record of every rename, move, delete and remux. This is what Undo reads.");

        AddPath(PanelPaths, "Log", AppPaths.Display(AppLog.FilePath),
            "Diagnostics, rotated at 2 MB.");

        AddPath(PanelPaths, "Archived originals",
            string.IsNullOrWhiteSpace(_settings.ArchiveRootPath)
                ? $"a \"{_settings.ArchiveFolderName}\" folder beside each remuxed file"
                : AppPaths.Display(_settings.ArchiveRootPath),
            "Pre-remux copies, kept so a remux can always be reversed.");
    }

    // ---------------- Row builders ----------------

    private void Add(Panel host, string name, string subtitle, string url, string description)
    {
        _plain.AppendLine($"{name} - {subtitle} - {url}");

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        head.Children.Add(new TextBlock
        {
            Text = "  -  " + subtitle,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush")
        });

        stack.Children.Add(head);
        stack.Children.Add(Link(url));
        stack.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush")
        });

        host.Children.Add(stack);
    }

    /// <summary>
    /// A tool row that says whether it was actually found. A download link is
    /// only useful alongside the answer to "do I already have it?".
    /// </summary>
    private void AddTool(Panel host, string name, string configuredPath, string url, string description)
    {
        var resolved = Resolve(configuredPath);
        var found = resolved is not null;

        _plain.AppendLine($"{name}: {(found ? resolved : "NOT FOUND")}  (configured: {configuredPath})");

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        head.Children.Add(new Border
        {
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
            CornerRadius = new CornerRadius(9),
            Background = new System.Windows.Media.SolidColorBrush(
                found
                    ? System.Windows.Media.Color.FromRgb(0x1E, 0x4D, 0x2B)
                    : System.Windows.Media.Color.FromRgb(0x5A, 0x2A, 0x1E)),
            Child = new TextBlock
            {
                Text = found ? "installed" : "not found",
                FontSize = 10,
                Foreground = System.Windows.Media.Brushes.White
            }
        });

        stack.Children.Add(head);

        stack.Children.Add(new TextBlock
        {
            Text = found ? resolved : $"configured as \"{configuredPath}\" - not on PATH either",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush")
        });

        stack.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush")
        });

        stack.Children.Add(Link(url, found ? "Downloads" : "Download it"));
        host.Children.Add(stack);
    }

    private void AddPath(Panel host, string label, string path, string description)
    {
        _plain.AppendLine($"{label}: {path}");

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorTertiaryBrush")
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush")
        });

        host.Children.Add(stack);
    }

    private TextBlock Link(string url, string? text = null)
    {
        var link = new Hyperlink(new Run(text ?? url)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += OnNavigate;

        return new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 12,
            Inlines = { link }
        };
    }

    /// <summary>Where a configured tool actually resolves to, or null if nowhere.</summary>
    private static string? Resolve(string configured)
    {
        try
        {
            if (File.Exists(configured)) return Path.GetFullPath(configured);

            // A bare name means "look on PATH", which is the default.
            var name = Path.GetFileName(configured);
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;

                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    // ---------------- Actions ----------------

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("About link", ex);
        }

        e.Handled = true;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;

            var text = $"PyreMedia {v?.ToString(3)}\n"
                       + $".NET {Environment.Version}\n"
                       + $"{RuntimeInformation()}\n\n"
                       + _plain;

            Clipboard.SetText(text);
            System.Windows.MessageBox.Show(this, "Copied to the clipboard.", "About",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("About copy", ex);
        }
    }

    /// <summary>
    /// Open the manual.
    ///
    /// There was no way to reach it from inside the program at all - it is one
    /// self-contained HTML page, and knowing that it existed and where it had
    /// been put was left entirely to the reader.
    ///
    /// A copy beside the exe wins, so a newer one can be dropped in without
    /// rebuilding. Otherwise the one carried inside the binary is written out
    /// and opened, which is the path that always works: a single-file build has
    /// its documentation in it and cannot be separated from it.
    /// </summary>
    private void OnOpenManual(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ManualBesideTheExe() ?? UnpackManual();

            if (path is null)
            {
                MessageBox.Show(this,
                    "This build doesn't carry the manual.\n\n"
                    + "It is one file called \"PyreMedia Manual.html\" - put it beside "
                    + "PyreMedia.exe and this button will open it.\n\n"
                    + $"Looked in:\n{AppContext.BaseDirectory}",
                    "Manual not found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open manual", ex);
            MessageBox.Show(this, $"Could not open the manual: {ex.Message}",
                "Manual", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? ManualBesideTheExe()
    {
        var here = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(here, "PyreMedia Manual.html"),
            Path.Combine(here, "PyreMedia-Manual.html"),
            Path.Combine(here, "docs", "PyreMedia-Manual.html"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Write the built-in manual out so a browser can open it, and return where.
    ///
    /// Rewritten whenever the copy on disk is a different size, so upgrading the
    /// program does not leave the old manual being opened for ever. Kept under
    /// the program's own folder rather than the temp directory, because a
    /// browser tab left open for a week should not be pointing at something
    /// Windows has cleaned up underneath it.
    /// </summary>
    private static string? UnpackManual()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("PyreMedia.Manual.html");

        if (stream is null) return null;

        var path = Path.Combine(AppPaths.Folder, "PyreMedia Manual.html");

        if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
        {
            Directory.CreateDirectory(AppPaths.Folder);

            using var file = File.Create(path);
            stream.CopyTo(file);
        }

        return path;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
