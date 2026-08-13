namespace PyreMedia.Core.Organizing;

/// <summary>What to do when a rename would land on a file that already exists.</summary>
public enum ConflictResolution
{
    /// <summary>Not decided yet - the executor must not proceed on this.</summary>
    Ask,

    /// <summary>Retire what's there (archive or recycle) and take its place.</summary>
    Replace,

    /// <summary>Land beside it under a free name: "Film (2).mkv".</summary>
    KeepBoth,

    /// <summary>Leave both files exactly as they are.</summary>
    Skip
}

/// <summary>
/// One collision, with enough detail to choose between the two files without
/// leaving the dialog - which is the whole point of showing a list rather than
/// a bare "overwrite?" prompt.
/// </summary>
public sealed class FileConflict
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }

    /// <summary>Companions that must move with the source and share its fate.</summary>
    public List<string> Companions { get; init; } = [];

    /// <summary>
    /// Why these two are in each other's way, in words. "A file of this name is
    /// already there" and "another file in this run wants the same name" need
    /// opposite answers, and a dialog that shows two paths without saying which
    /// case it is makes the user work that out for themselves.
    /// </summary>
    public string? Reason { get; init; }

    public ConflictResolution Resolution { get; set; } = ConflictResolution.Ask;

    /// <summary>The name this would take under KeepBoth, filled in when resolved.</summary>
    public string? KeepBothPath { get; set; }

    public string SourceName => Path.GetFileName(SourcePath);
    public string TargetName => Path.GetFileName(TargetPath);
    public string TargetFolder => Path.GetFileName(Path.GetDirectoryName(TargetPath) ?? "");

    public long SourceBytes => Size(SourcePath);
    public long TargetBytes => Size(TargetPath);
    public DateTime? SourceModified => Modified(SourcePath);
    public DateTime? TargetModified => Modified(TargetPath);

    /// <summary>
    /// True when the two are plausibly the same file already - same size to the
    /// byte. Worth calling out, because replacing one with an identical copy is
    /// pure risk for no gain.
    /// </summary>
    public bool LooksIdentical => SourceBytes > 0 && SourceBytes == TargetBytes;

    /// <summary>Which is newer, for the "keep the better one" judgement.</summary>
    public bool SourceIsNewer =>
        SourceModified is { } a && TargetModified is { } b && a > b;

    public bool SourceIsLarger => SourceBytes > TargetBytes;

    private static long Size(string p)
    {
        try { return new FileInfo(p).Length; } catch { return 0; }
    }

    private static DateTime? Modified(string p)
    {
        try { return File.Exists(p) ? new FileInfo(p).LastWriteTime : null; }
        catch { return null; }
    }
}

/// <summary>Finds a free name, the way Windows does.</summary>
public static class UniqueName
{
    /// <summary>
    /// "Film.mkv" -> "Film (2).mkv" -> "Film (3).mkv", skipping anything taken.
    ///
    /// <paramref name="alsoTaken"/> covers names claimed earlier in the same
    /// batch but not yet written: two files resolving to the same target within
    /// one apply would otherwise both be handed the same "(2)".
    /// </summary>
    public static string For(string path, ISet<string>? alsoTaken = null)
    {
        if (!Exists(path, alsoTaken)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        // Don't stack suffixes: "Film (2).mkv" colliding becomes "Film (3).mkv",
        // never "Film (2) (2).mkv".
        //
        // Two digits at most. Films are named "Film (2010)", and treating that
        // year as a copy counter silently produced "Film (2011)" - a wrong year
        // on the most common naming convention there is. A real copy counter
        // never reaches three digits.
        var match = System.Text.RegularExpressions.Regex.Match(stem, @"^(?<base>.+) \((?<n>\d{1,2})\)$");
        var start = 2;

        if (match.Success && int.TryParse(match.Groups["n"].Value, out var existing) && existing >= 2)
        {
            stem = match.Groups["base"].Value;
            start = existing + 1;
        }

        for (var i = start; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Exists(candidate, alsoTaken)) return candidate;
        }

        // Vanishingly unlikely, but never return a name we know is taken.
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>
    /// The companion's name once its video has been renamed to
    /// <paramref name="newVideoPath"/>. Keeps whatever distinguishes it -
    /// "-poster", ".eng" - and follows the video into any "(2)" it took, so the
    /// set never ends up split between the original name and the new one.
    /// </summary>
    public static string CompanionFor(string companion, string oldVideoPath, string newVideoPath)
    {
        var oldStem = Path.GetFileNameWithoutExtension(oldVideoPath);
        var newStem = Path.GetFileNameWithoutExtension(newVideoPath);
        var dir = Path.GetDirectoryName(newVideoPath) ?? "";
        var name = Path.GetFileName(companion);

        return name.StartsWith(oldStem, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(dir, newStem + name[oldStem.Length..])
            : Path.Combine(dir, name);
    }

    private static bool Exists(string path, ISet<string>? alsoTaken)
        => File.Exists(path) || (alsoTaken?.Contains(path) ?? false);
}
