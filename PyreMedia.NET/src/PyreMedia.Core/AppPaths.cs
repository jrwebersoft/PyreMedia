namespace PyreMedia.Core;

/// <summary>
/// Where PyreMedia keeps its own files - settings, history, log. One place
/// decides, so they can't drift apart, and so a tool can point the whole lot at
/// a scratch folder instead of the user's real one.
/// </summary>
public static class AppPaths
{
    private static string? _override;

    /// <summary>The folder holding settings, history and the log.</summary>
    public static string Folder
    {
        get
        {
            if (_override is not null) return _override;

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PyreMedia");

            CarryOverFromOldName(folder);
            return folder;
        }
    }

    private static bool _carriedOver;

    /// <summary>
    /// Bring settings and history across from the folder this app used under its
    /// previous name, once, the first time it runs after the rename.
    ///
    /// Copied rather than moved, and only when nothing is here yet, so the old
    /// folder stays intact as its own fallback and nothing that already exists
    /// can be overwritten. A configured library and a full undo history are worth
    /// too much to lose to a rename, and the alternative is the app opening on an
    /// empty window with no explanation.
    /// </summary>
    private static void CarryOverFromOldName(string folder)
    {
        if (_carriedOver) return;
        _carriedOver = true;

        try
        {
            if (Directory.Exists(folder)) return;

            var previous = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaScout");

            if (!Directory.Exists(previous)) return;

            Directory.CreateDirectory(folder);

            foreach (var file in Directory.EnumerateFiles(previous))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: false);
        }
        catch (Exception)
        {
            // Starting fresh is a worse outcome than starting fresh *and* failing
            // to launch. Setup will ask for a folder like any first run.
        }
    }

    /// <summary>
    /// Send everything to a different folder. For the manual builder and test
    /// harnesses, which construct real windows and would otherwise write over a
    /// configured folder list. Call before anything reads a path.
    /// </summary>
    public static void UseFolder(string path)
    {
        _override = path;
        Directory.CreateDirectory(path);
    }

    /// <summary>True when the paths have been redirected away from the real ones.</summary>
    public static bool IsRedirected => _override is not null;

    /// <summary>
    /// A path as it should be shown to a person: with the user's own folders
    /// written as the variables that stand for them, so
    /// <c>C:\Users\someone\AppData\Roaming\PyreMedia</c> reads
    /// <c>%APPDATA%\PyreMedia</c>.
    ///
    /// Two reasons. It stops a screenshot or a copied diagnostic carrying the
    /// account name of whoever ran it, which is nobody else's business. And it is
    /// the truer answer: the folder is wherever the current user's profile is, not
    /// the one place this machine happens to put it - the same build on another
    /// machine writes somewhere else entirely.
    ///
    /// Still paste-able. Explorer, Run and every shell expand these.
    /// </summary>
    public static string Display(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        // Longest first, or %USERPROFILE% would claim what %APPDATA% should.
        foreach (var (variable, folder) in new[]
                 {
                     ("%APPDATA%", Environment.SpecialFolder.ApplicationData),
                     ("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData),
                     ("%USERPROFILE%", Environment.SpecialFolder.UserProfile),
                 })
        {
            string root;
            try { root = Environment.GetFolderPath(folder); }
            catch { continue; }

            if (string.IsNullOrEmpty(root)) continue;

            root = root.TrimEnd(Path.DirectorySeparatorChar);

            if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return variable + path[root.Length..];

            if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root,
                              StringComparison.OrdinalIgnoreCase))
                return variable;
        }

        return path;
    }
}
