using System.Diagnostics;

namespace PyreMedia.Core.Media;

/// <summary>
/// Opening a file in whatever will actually play it.
///
/// Deliberately not an embedded player. WPF's MediaElement goes through Media
/// Foundation, which on a stock Windows install will not play Matroska, HEVC,
/// DTS or TrueHD - which is to say, most of a ripped library. A preview window
/// that fails on the files you most need to identify is worse than no preview
/// window, because it fails silently and looks like the file is broken.
///
/// So this hands off, to a player that is an optional install like ffmpeg and
/// MKVToolNix are. Where one is found it is used directly, because those accept
/// switches - full screen, a start position - that Windows offers no general
/// way to ask for. Where none is found the file opens however Windows would
/// open it, and everything still works.
/// </summary>
public static class Preview
{
    /// <summary>
    /// Players that take switches, in the order they are tried.
    ///
    /// VLC is the one offered as an install, being the one most people already
    /// have; the others are recognised because somebody who has installed mpv
    /// or MPC would rather it used those.
    /// </summary>
    private static readonly (string Exe, string FullScreen, string Start)[] Players =
    [
        ("vlc.exe",      "--fullscreen", "--start-time={0}"),
        ("mpv.exe",      "--fullscreen", "--start={0}"),
        ("mpc-hc64.exe", "/fullscreen",  "/start {0}000"),   // milliseconds
        ("mpc-be64.exe", "/fullscreen",  "/start {0}000"),
    ];

    private static readonly string[] SearchPaths =
    [
        @"C:\Program Files\VideoLAN\VLC",
        @"C:\Program Files (x86)\VideoLAN\VLC",
        @"C:\Program Files\mpv",
        @"C:\Program Files\MPC-HC",
        @"C:\Program Files\MPC-BE",
    ];

    /// <summary>
    /// The player that will be used, or null when Windows decides.
    /// </summary>
    /// <param name="configured">
    /// The path from settings, tried first so a chosen player wins over one
    /// that merely happens to be installed.
    /// </param>
    public static string? Player(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)
            && Tools.ToolLocator.Resolve(configured) is { } chosen
            && Known(chosen) is not null)
        {
            return chosen;
        }

        foreach (var (exe, _, _) in Players)
        {
            if (Tools.ToolLocator.Resolve(exe) is { } onPath) return onPath;

            foreach (var dir in SearchPaths)
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full)) return full;
            }
        }

        return null;
    }

    /// <summary>The switch set for a resolved player, or null if it is not one we know.</summary>
    private static (string Exe, string FullScreen, string Start)? Known(string path)
    {
        var name = Path.GetFileName(path);

        foreach (var p in Players)
            if (string.Equals(p.Exe, name, StringComparison.OrdinalIgnoreCase))
                return p;

        return null;
    }

    /// <summary>What to tell the user about how a preview will open.</summary>
    public static string Describe(string? configured = null) =>
        Player(configured) is { } player
            ? $"Opens in {Path.GetFileNameWithoutExtension(player)}."
            : "Opens in whatever Windows uses for the file. Install VLC and PyreMedia can open "
              + "it full screen instead.";

    /// <summary>
    /// Open a file for a look. Returns null on success, or what went wrong.
    /// </summary>
    /// <param name="fullScreen">Open taking the whole screen.</param>
    /// <param name="seconds">
    /// Where to start, in seconds. Zero - the beginning - unless something asks
    /// otherwise. Both are ignored when no player that understands them is
    /// installed, because Windows has no general way to pass either.
    /// </param>
    public static string? Open(
        string path, string? configured = null, bool fullScreen = false, int seconds = 0)
    {
        if (!File.Exists(path)) return "That file is no longer there.";

        try
        {
            if (Player(configured) is { } player && Known(player) is { } switches)
            {
                var psi = new ProcessStartInfo(player) { UseShellExecute = false };

                if (fullScreen) psi.ArgumentList.Add(switches.FullScreen);

                // Split on spaces so "/start 60000" arrives as two arguments
                // rather than one, which is how MPC reads it.
                if (seconds > 0)
                {
                    foreach (var bit in string.Format(switches.Start, seconds).Split(' '))
                        psi.ArgumentList.Add(bit);
                }

                psi.ArgumentList.Add(path);

                Process.Start(psi);
                return null;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
