using System.Diagnostics;

namespace PyreMedia.Core.Tools;

/// <summary>How much the app depends on a given tool.</summary>
public enum ToolNeed
{
    /// <summary>Nothing works without it.</summary>
    Required,

    /// <summary>Everything works, but something important is lost without it.</summary>
    Recommended,

    /// <summary>For one particular repair. Most libraries never need it.</summary>
    Optional
}

/// <summary>An external program the app shells out to.</summary>
public sealed class ExternalTool
{
    public required string Name { get; init; }
    /// <summary>
    /// The configured command. Settable because a tool that was just fetched
    /// or browsed to is at a new place, and the row on screen has to be asking
    /// about the new one.
    /// </summary>
    public required string Executable { get; set; }
    public required ToolNeed Need { get; init; }
    public required string Why { get; init; }

    /// <summary>What breaks without it, in plain terms.</summary>
    public required string Without { get; init; }

    /// <summary>
    /// winget package id, when there is one.
    ///
    /// These must be the authors' own packages, never a community repackage:
    /// "Gyan.FFmpeg" is Gyan Doshi, whose builds ffmpeg.org itself links to for
    /// Windows, and "MoritzBunkus.MKVToolNix" is MKVToolNix's own author. Both
    /// install from the authors' release artifacts.
    /// </summary>
    public string? WingetId { get; init; }

    /// <summary>Who publishes it, shown so the source isn't taken on trust.</summary>
    public required string Publisher { get; init; }

    /// <summary>
    /// Where to record this tool's location, once it is known.
    ///
    /// Required, and deliberately so. Setup used to decide this with a switch
    /// on the tool's name that listed three of the five, and the two it missed
    /// were not errors - the Browse dialog opened, a file was chosen, and the
    /// answer went nowhere. Making it part of the tool means the next one
    /// cannot be added without saying where its path belongs.
    /// </summary>
    public required Action<PyreMediaSettings, string> SetPath { get; init; }

    /// <summary>
    /// "owner/repo" when the author publishes Windows builds on GitHub.
    ///
    /// Sending someone to a web page to hunt for the right zip is the worst
    /// step in setup: it is the one that needs judgement, and the judgement -
    /// which of nine assets is the Windows 64-bit one - is exactly what this
    /// program already knows. With a repo here the app fetches the release
    /// itself, from the author's own account, and the page becomes a fallback
    /// rather than the instruction.
    /// </summary>
    public string? GitHubRepo { get; init; }

    /// <summary>
    /// Extra folders to look in, for installers that register the program
    /// properly but never put it on PATH. Environment variables are expanded,
    /// so "%ProgramFiles%\VideoLAN\VLC" is the way to write one.
    /// </summary>
    public string[] AlsoLook { get; init; } = [];

    /// <summary>
    /// Part of the asset's filename that marks it as the Windows 64-bit build.
    /// A release carries a dozen files for a dozen platforms and picking the
    /// wrong one gives a program that will not start, so this is stated per
    /// tool rather than guessed from a general rule.
    /// </summary>
    public string? AssetHint { get; init; }

    /// <summary>
    /// The switch that makes it print its version and exit, or null when
    /// running it is a bad idea.
    ///
    /// Null matters: VLC given an argument it dislikes opens its window. A
    /// setup page that checks which tools are present must not launch one of
    /// them to find out, so VLC's version is read from the file itself.
    /// </summary>
    public string? VersionArg { get; init; } = "-version";

    public required string DownloadUrl { get; init; }

    /// <summary>Where it was found, or null.</summary>
    public string? ResolvedPath { get; set; }

    public bool Found => ResolvedPath is not null;

    /// <summary>Version string as the tool reports it, once found.</summary>
    public string? Version { get; set; }
}

/// <summary>
/// Finds the external tools, and installs them where the platform can.
///
/// Nothing is bundled: ffmpeg and MKVToolNix are separate projects under their
/// own licences, and shipping copies would both bloat the app and create
/// redistribution obligations. Locating them - and offering to install them -
/// is the honest middle ground.
/// </summary>
public static class ToolLocator
{
    public static List<ExternalTool> All(PyreMediaSettings settings) =>
    [
        new()
        {
            Name = "ffprobe",
            Executable = settings.FfprobePath,
            SetPath = (s, p) => s.FfprobePath = p,
            Need = ToolNeed.Required,
            Why = "Reads every track, codec, language, HDR format and Dolby Vision profile.",
            Without = "Nothing can be analysed and no remux can be verified.",
            WingetId = "Gyan.FFmpeg",
            GitHubRepo = "GyanD/codexffmpeg",
            AssetHint = "essentials_build.zip",
            AlsoLook = [@"%ProgramFiles%\ffmpeg\bin", @"%LOCALAPPDATA%\Microsoft\WinGet\Links"],
            Publisher = "Gyan Doshi - one of the two Windows builders listed on ffmpeg.org",
            DownloadUrl = PyreMediaSettings.FfmpegUrl
        },
        new()
        {
            Name = "ffmpeg",
            Executable = settings.FfmpegPath,
            SetPath = (s, p) => s.FfmpegPath = p,
            Need = ToolNeed.Required,
            Why = "Rebuilds files without the tracks you drop.",
            Without = "Remuxing is unavailable. Renaming still works.",
            WingetId = "Gyan.FFmpeg",
            GitHubRepo = "GyanD/codexffmpeg",
            AssetHint = "essentials_build.zip",
            AlsoLook = [@"%ProgramFiles%\ffmpeg\bin", @"%LOCALAPPDATA%\Microsoft\WinGet\Links"],
            Publisher = "Gyan Doshi - one of the two Windows builders listed on ffmpeg.org",
            DownloadUrl = PyreMediaSettings.FfmpegUrl
        },
        new()
        {
            Name = "mkvmerge",
            Executable = settings.MkvMergePath,
            SetPath = (s, p) => s.MkvMergePath = p,
            Need = ToolNeed.Recommended,
            Why = "Copies tracks without decoding, so Dolby Vision and 3D MVC survive.",
            Without = "Remuxing falls back to ffmpeg, which has been measured dropping "
                      + "Dolby Vision signalling. Files are still verified and never damaged, "
                      + "but affected remuxes will refuse to complete.",
            WingetId = "MoritzBunkus.MKVToolNix",

            // MKVToolNix develops on GitLab and publishes its Windows builds on
            // its own site, so there is no GitHub release to fetch. winget is
            // the direct route here, and it is the author's own package.
            AlsoLook = [@"%ProgramFiles%\MKVToolNix", @"%ProgramFiles(x86)%\MKVToolNix"],
            Publisher = "Moritz Bunkus - the author of MKVToolNix",
            DownloadUrl = PyreMediaSettings.MkvToolNixUrl
        },
        new()
        {
            Name = "dovi_tool",
            Executable = settings.DoviToolPath,
            SetPath = (s, p) => s.DoviToolPath = p,
            Need = ToolNeed.Optional,
            Why = "Rebuilds the Dolby Vision declaration for a file that still has the "
                  + "data but no longer announces it.",
            Without = "Such a file can be spotted and reported, but not repaired. Everything "
                      + "else is unaffected - most libraries never need this.",

            // No winget package exists for it - checked, not assumed. A community
            // repackage would be the wrong thing to point at, so this comes
            // from the author's own releases, fetched rather than described.
            WingetId = null,
            GitHubRepo = "quietvoid/dovi_tool",
            AssetHint = "x86_64-pc-windows-msvc",

            // Rust's argument parser, which wants the long form and says so
            // rather than printing a version.
            VersionArg = "--version",
            Publisher = "quietvoid - the author of dovi_tool",
            DownloadUrl = PyreMediaSettings.DoviToolUrl
        },
        new()
        {
            Name = "VLC",
            Executable = settings.PlayerPath,
            SetPath = (s, p) => s.PlayerPath = p,
            Need = ToolNeed.Optional,
            Why = "Plays a file so you can see what it is - the quickest way to tell which "
                  + "episode a disc rip's title_t00.mkv actually holds.",
            Without = "Play still works, opening the file however Windows would open it. "
                      + "What is lost is control: no full screen on request, and no starting "
                      + "part-way in.",
            WingetId = "VideoLAN.VLC",

            // VLC's installer registers the program properly and never touches
            // PATH, so a fully installed copy read as "not found". These are
            // where it actually is; the App Paths key covers the rest.
            AlsoLook = [@"%ProgramFiles%\VideoLAN\VLC", @"%ProgramFiles(x86)%\VideoLAN\VLC"],

            // Not executed to find its version: VLC handed an argument it does
            // not know opens its window, and a page that only means to check
            // what is installed must not start a media player to do it.
            VersionArg = null,
            Publisher = "VideoLAN - the authors of VLC",
            DownloadUrl = PyreMediaSettings.VlcUrl
        }
    ];

    /// <summary>Fill in where each tool resolved to, and what version it reports.</summary>
    public static async Task RefreshAsync(IEnumerable<ExternalTool> tools, CancellationToken ct = default)
    {
        foreach (var t in tools)
        {
            t.ResolvedPath = Resolve(t.Executable, t.AlsoLook);

            t.Version = t.ResolvedPath is null
                ? null
                : t.VersionArg is null
                    ? FileVersion(t.ResolvedPath)
                    : await VersionOfAsync(t.ResolvedPath, t.VersionArg, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Where this app keeps the tools it fetched itself.</summary>
    public static string ToolsFolder => Path.Combine(AppPaths.Folder, "tools");

    /// <summary>
    /// Where a configured command actually resolves to, or null.
    ///
    /// PATH alone is not enough, and VLC is the proof: a fully installed copy
    /// reported "not found as vlc, and not on PATH" while sitting in Program
    /// Files, because VLC's installer registers the program properly and never
    /// touches PATH. So this asks in the order Windows itself would - the exact
    /// path, then PATH, then the App Paths key that Run uses to turn a bare
    /// program name into a location, then the folders these particular
    /// installers are known to use, and finally the tools this app fetched.
    /// </summary>
    public static string? Resolve(string configured, IEnumerable<string>? alsoLook = null)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        try
        {
            if (File.Exists(configured)) return Path.GetFullPath(configured);

            var name = Path.GetFileName(configured);
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;

                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* a malformed PATH entry is not our problem */ }
            }

            if (FromAppPaths(name) is { } registered) return registered;

            foreach (var dir in Places(alsoLook))
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// The location Windows records for a program that registered itself,
    /// which is how Run finds "vlc" without any PATH entry. Both hives, and
    /// both views of the registry, because a 32-bit installer on a 64-bit
    /// machine writes to the other one.
    /// </summary>
    private static string? FromAppPaths(string exeName)
    {
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            foreach (var view in new[] { "", @"WOW6432Node\" })
            {
                try
                {
                    using var key = root.OpenSubKey(
                        $@"SOFTWARE\{view}Microsoft\Windows\CurrentVersion\App Paths\{exeName}");

                    if (key?.GetValue(null) is string s && s.Trim('"') is { Length: > 0 } path && File.Exists(path))
                        return path;
                }
                catch { /* a locked or malformed key is not worth failing over */ }
            }
        }

        return null;
    }

    private static IEnumerable<string> Places(IEnumerable<string>? alsoLook)
    {
        yield return ToolsFolder;

        if (alsoLook is null) yield break;

        foreach (var raw in alsoLook)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var expanded = Environment.ExpandEnvironmentVariables(raw);

            // An unset variable expands to itself, which is not a folder.
            if (!expanded.Contains('%')) yield return expanded;
        }
    }

    /// <summary>
    /// The version the file itself declares, for tools that must not be run
    /// just to be identified.
    /// </summary>
    private static string? FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);

            return string.IsNullOrWhiteSpace(info.ProductVersion)
                ? (string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion)
                : $"{info.ProductName} {info.ProductVersion}".Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First line of the tool's own version output, trimmed to something readable.</summary>
    private static async Task<string?> VersionOfAsync(string path, string arg, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var text = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                text = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var first = text.Split('\n').FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(first) ? null
                 : first.Length <= 90 ? first
                 : first[..90] + "...";
        }
        catch
        {
            return null;
        }
    }

    // ---------------- Installing ----------------

    /// <summary>True when winget is present, which is how installing is offered.</summary>
    public static bool CanInstall => Resolve("winget") is not null;

    /// <summary>
    /// Install through winget, streaming its output back so the user can see
    /// something is happening - these downloads are tens of megabytes.
    /// </summary>
    public static async Task<bool> InstallAsync(
        string wingetId, IProgress<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var a in new[]
                     {
                         "install", "--id", wingetId, "--exact",
                         "--accept-package-agreements", "--accept-source-agreements",
                         "--disable-interactivity"
                     })
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            // winget redraws a progress bar with carriage returns; report only
            // whole lines so the log doesn't fill with partial repaints.
            var reader = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    var clean = line.Split('\r').Last().Trim();
                    if (clean.Length > 0) log?.Report(clean);
                }
            }, ct);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await reader.ConfigureAwait(false);

            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            log?.Report($"Install failed: {ex.Message}");
            return false;
        }
    }

    // ---------------- Fetching from the author's own releases ----------------

    /// <summary>What a fetch did, in terms the window can show without translating.</summary>
    public sealed record Fetched(bool Ok, string Message, string? Path = null);

    /// <summary>True when this tool can be fetched without sending anyone to a web page.</summary>
    public static bool CanFetch(ExternalTool tool) => tool.GitHubRepo is not null;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    /// <summary>A release's archive is tens of megabytes; anything far past that is not one.</summary>
    private const long TooBig = 400L * 1024 * 1024;

    /// <summary>
    /// Fetch a tool straight from its author's latest GitHub release and unpack
    /// it where this app looks.
    ///
    /// The alternative was opening a browser at a releases page, which asks the
    /// user to do the one part that needs judgement - deciding which of a dozen
    /// files is the Windows 64-bit build - while the program that knows the
    /// answer watches. Nothing is run: the archive is opened, the programs
    /// inside are written out by name alone, and the file stays inert until the
    /// user asks for something that uses it.
    /// </summary>
    public static async Task<Fetched> FetchAsync(
        ExternalTool tool, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (tool.GitHubRepo is not { } repo)
            return new Fetched(false, $"{tool.Name} is not published on GitHub, so it cannot be fetched.");

        var wanted = tool.Executable is { Length: > 0 } e ? Path.GetFileName(e) : tool.Name;
        if (!wanted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) wanted += ".exe";

        try
        {
            log?.Report($"Asking github.com for the latest {tool.Name} release...");

            using var ask = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{repo}/releases/latest");

            // GitHub refuses anonymous calls with no User-Agent.
            ask.Headers.UserAgent.ParseAdd("PyreMedia");
            ask.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var reply = await Http.SendAsync(ask, ct).ConfigureAwait(false);

            if (!reply.IsSuccessStatusCode)
                return new Fetched(false, $"github.com answered {(int)reply.StatusCode} for {repo}.");

            using var doc = System.Text.Json.JsonDocument.Parse(
                await reply.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

            if (!doc.RootElement.TryGetProperty("assets", out var assets))
                return new Fetched(false, $"The latest {repo} release lists no files.");

            var (url, assetName) = PickAsset(assets, tool.AssetHint);

            if (url is null)
                return new Fetched(false,
                    $"No Windows build in the latest {tool.Name} release. Use Download instead.");

            log?.Report($"Downloading {assetName} ({tag})...");

            using var got = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                      .ConfigureAwait(false);

            if (!got.IsSuccessStatusCode)
                return new Fetched(false, $"Download of {assetName} answered {(int)got.StatusCode}.");

            if (got.Content.Headers.ContentLength is > TooBig)
                return new Fetched(false, $"{assetName} is far larger than a tool release should be.");

            var temp = Path.Combine(Path.GetTempPath(), $"pyremedia-{Guid.NewGuid():N}.zip");

            try
            {
                await using (var to = File.Create(temp))
                    await got.Content.CopyToAsync(to, ct).ConfigureAwait(false);

                if (new FileInfo(temp).Length > TooBig)
                    return new Fetched(false, $"{assetName} is far larger than a tool release should be.");

                if (!LooksLikeZip(temp))
                    return new Fetched(false,
                        $"{assetName} is not a zip - it may be a .7z, which needs the Download button.");

                log?.Report("Unpacking...");

                var landed = Unpack(temp, wanted);

                if (landed is null)
                    return new Fetched(false, $"{assetName} did not contain {wanted}.");

                log?.Report($"{tool.Name} {tag} is in {AppPaths.Display(ToolsFolder)}.");

                return new Fetched(true, $"{tool.Name} {tag} installed.", landed);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            return new Fetched(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new Fetched(false, $"Could not fetch {tool.Name}: {ex.Message}");
        }
    }

    private static (string? Url, string? Name) PickAsset(System.Text.Json.JsonElement assets, string? hint)
    {
        var zips = new List<(string Url, string Name)>();

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;

            if (name is null || url is null) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

            zips.Add((url, name));
        }

        if (hint is { Length: > 0 })
        {
            foreach (var z in zips)
                if (z.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return z;
        }

        // Without a hint, prefer something that names Windows over something
        // that names nothing - and take nothing at all rather than a build for
        // another operating system.
        foreach (var marker in new[] { "x86_64-pc-windows", "windows", "win64", "x64" })
            foreach (var z in zips)
                if (z.Name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return z;

        return (null, null);
    }

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];

            return s.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4
                && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Write the programs out of the archive into the app's own tools folder.
    ///
    /// Entries are taken by filename alone and the archive's own folders are
    /// discarded, which is deliberate: an entry called "..\..\windows\x.exe"
    /// is a real thing that appears in real archives, and flattening to a bare
    /// name makes it structurally impossible for one to land anywhere but here.
    /// </summary>
    private static string? Unpack(string zip, string wanted)
    {
        Directory.CreateDirectory(ToolsFolder);

        string? landed = null;

        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);

        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName);

            if (name.Length == 0) continue;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            if (entry.Length > TooBig) continue;

            var to = Path.Combine(ToolsFolder, name);

            using (var from = entry.Open())
            using (var into = File.Create(to))
                from.CopyTo(into);

            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) landed = to;
        }

        return landed;
    }

    /// <summary>
    /// A freshly installed tool lands on the machine's PATH, but this process
    /// inherited its environment at launch and won't see it. Re-read PATH so
    /// the tool is usable without restarting the app.
    /// </summary>
    public static void RefreshPathFromEnvironment()
    {
        try
        {
            var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";

            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, machine, user));
        }
        catch { }
    }
}
