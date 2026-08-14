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
    public required string Executable { get; init; }
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
            Need = ToolNeed.Required,
            Why = "Reads every track, codec, language, HDR format and Dolby Vision profile.",
            Without = "Nothing can be analysed and no remux can be verified.",
            WingetId = "Gyan.FFmpeg",
            Publisher = "Gyan Doshi - one of the two Windows builders listed on ffmpeg.org",
            DownloadUrl = PyreMediaSettings.FfmpegUrl
        },
        new()
        {
            Name = "ffmpeg",
            Executable = settings.FfmpegPath,
            Need = ToolNeed.Required,
            Why = "Rebuilds files without the tracks you drop.",
            Without = "Remuxing is unavailable. Renaming still works.",
            WingetId = "Gyan.FFmpeg",
            Publisher = "Gyan Doshi - one of the two Windows builders listed on ffmpeg.org",
            DownloadUrl = PyreMediaSettings.FfmpegUrl
        },
        new()
        {
            Name = "mkvmerge",
            Executable = settings.MkvMergePath,
            Need = ToolNeed.Recommended,
            Why = "Copies tracks without decoding, so Dolby Vision and 3D MVC survive.",
            Without = "Remuxing falls back to ffmpeg, which has been measured dropping "
                      + "Dolby Vision signalling. Files are still verified and never damaged, "
                      + "but affected remuxes will refuse to complete.",
            WingetId = "MoritzBunkus.MKVToolNix",
            Publisher = "Moritz Bunkus - the author of MKVToolNix",
            DownloadUrl = PyreMediaSettings.MkvToolNixUrl
        },
        new()
        {
            Name = "dovi_tool",
            Executable = settings.DoviToolPath,
            Need = ToolNeed.Optional,
            Why = "Rebuilds the Dolby Vision declaration for a file that still has the "
                  + "data but no longer announces it.",
            Without = "Such a file can be spotted and reported, but not repaired. Everything "
                      + "else is unaffected - most libraries never need this.",

            // No winget package exists for it - checked, not assumed. A community
            // repackage would be the wrong thing to point at, so this is a
            // download from the author's own releases.
            WingetId = null,
            Publisher = "quietvoid - the author of dovi_tool",
            DownloadUrl = PyreMediaSettings.DoviToolUrl
        },
        new()
        {
            Name = "VLC",
            Executable = settings.PlayerPath,
            Need = ToolNeed.Optional,
            Why = "Plays a file so you can see what it is - the quickest way to tell which "
                  + "episode a disc rip's title_t00.mkv actually holds.",
            Without = "Play still works, opening the file however Windows would open it. "
                      + "What is lost is control: no full screen on request, and no starting "
                      + "part-way in.",
            WingetId = "VideoLAN.VLC",
            Publisher = "VideoLAN - the authors of VLC",
            DownloadUrl = PyreMediaSettings.VlcUrl
        }
    ];

    /// <summary>Fill in where each tool resolved to, and what version it reports.</summary>
    public static async Task RefreshAsync(IEnumerable<ExternalTool> tools, CancellationToken ct = default)
    {
        foreach (var t in tools)
        {
            t.ResolvedPath = Resolve(t.Executable);
            t.Version = t.ResolvedPath is null ? null : await VersionOfAsync(t.ResolvedPath, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Where a configured command actually resolves to, or null. A bare name
    /// means "look on PATH", which is the default and what most installers set
    /// up.
    /// </summary>
    public static string? Resolve(string configured)
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
        }
        catch { }

        return null;
    }

    /// <summary>First line of the tool's own --version output, trimmed to something readable.</summary>
    private static async Task<string?> VersionOfAsync(string path, CancellationToken ct)
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
            psi.ArgumentList.Add("-version");

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
