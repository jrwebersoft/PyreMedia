namespace PyreMedia.Core.Organizing;

/// <summary>What a volume can and cannot hold.</summary>
/// <param name="FileSystem">As Windows reports it: NTFS, FAT32, exFAT, ReFS.</param>
public sealed record VolumeLimits(string FileSystem, long MaxFileBytes, int MaxPathLength, bool KeepsExactTimes)
{
    /// <summary>
    /// Nothing known - a network share whose backing filesystem Windows will
    /// not name, or a path that could not be resolved. Assumed generous,
    /// because refusing a move on a guess would be worse than the error the
    /// filesystem gives when it genuinely cannot take a file.
    /// </summary>
    public static readonly VolumeLimits Unknown = new("unknown", long.MaxValue, 32_000, true);

    public bool Rejects(long bytes) => bytes > MaxFileBytes;

    /// <summary>
    /// The longest a single folder or file name may be. 255 everywhere that
    /// matters - NTFS, FAT32 and exFAT all agree, because the limit belongs to
    /// the long-filename scheme rather than to any one of them.
    /// </summary>
    public const int MaxComponentLength = 255;
}

/// <summary>
/// What the filesystem underneath a path will put up with.
///
/// This exists because a library that spans several disks eventually meets one
/// that is not NTFS - a USB drive carried between machines is nearly always
/// exFAT, and anything old enough is FAT32. The failures are not graceful. A
/// 5 GB film copied to FAT32 fails partway through with "There is not enough
/// space on the disk", which is untrue and sends people looking at the wrong
/// thing entirely.
///
/// This reads a volume and never writes to one. It asks Windows what the
/// filesystem is called, looks the limits up, and reports them; it does not
/// partition, format, mount or alter a disk in any way, and no code path here
/// leads anywhere that could.
///
/// None of it has been checked against a real FAT32 volume - there is not one
/// on this machine - so the limits come from the specifications and the
/// decision is tested on its own. Said plainly because it is the one part of
/// this that could still be wrong in a way the checks would not catch.
/// </summary>
public static class Volumes
{
    /// <summary>The limits for whatever volume a path lives on.</summary>
    public static VolumeLimits For(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));

            // A UNC path has no DriveInfo. Windows does not report the remote
            // filesystem, and guessing at it would be inventing a limit.
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return VolumeLimits.Unknown;

            return Describe(new DriveInfo(root).DriveFormat);   // the name only; nothing here writes to a volume
        }
        catch { return VolumeLimits.Unknown; }
    }

    /// <summary>The limits implied by a filesystem name.</summary>
    public static VolumeLimits Describe(string fileSystem) => fileSystem.ToUpperInvariant() switch
    {
        // Three limits differ, and only three. The illegal characters are the
        // same set on all of them - \ / : * ? " < > | plus the DOS device names
        // and trailing dots - because Windows applies that rule above the
        // filesystem, not inside it, so NamingFormat.Safe already covers FAT32
        // by covering NTFS. What FAT32 adds is a hard 4 GB ceiling per file,
        // a 260-character path with no way to opt out of it, and timestamps
        // rounded to two seconds.
        "FAT32" or "FAT" => new(fileSystem, (1L << 32) - 1, 260, KeepsExactTimes: false),

        // exFAT lifted the size ceiling and kept the other two.
        "EXFAT" => new(fileSystem, long.MaxValue, 260, KeepsExactTimes: false),

        // NTFS reaches 32,767 only where long paths are enabled, which is off
        // by default on Windows; 260 is what an ordinary install enforces. The
        // higher figure is used here because exceeding it is impossible rather
        // than merely likely to fail, and a warning nobody can act on is noise.
        "NTFS" or "REFS" => new(fileSystem, long.MaxValue, 32_000, KeepsExactTimes: true),

        _ => VolumeLimits.Unknown with { FileSystem = fileSystem }
    };

    /// <summary>
    /// Why these files cannot all go to this destination, or null if they can.
    ///
    /// Checked before anything is written rather than discovered halfway
    /// through, because a bulk move that fails on file four hundred leaves the
    /// library in a state nobody asked for.
    /// </summary>
    public static string? Refuses(IEnumerable<string> files, string destination,
                                  string? stagingRoot = null)
        => Refuses(files, destination, For(destination), stagingRoot);

    /// <summary>
    /// The same decision against limits supplied by the caller.
    ///
    /// Separate from the volume lookup so the rule can be checked without one:
    /// there is no FAT32 disk on this machine to point at, and a test that can
    /// only run on hardware nobody has is a test that never runs.
    /// </summary>
    public static string? Refuses(IEnumerable<string> files, string destination,
                                  VolumeLimits limits, string? stagingRoot = null)
    {
        var toobig = new List<(string Path, long Bytes)>();
        var toolong = new List<string>();

        var root = stagingRoot?.TrimEnd(Path.DirectorySeparatorChar);

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;

                if (limits.Rejects(info.Length)) toobig.Add((file, info.Length));

                // Where it would land, not where it is. A short staging path
                // and a long library path is the whole reason this bites - the
                // file is fine now and unwritable a moment later.
                var landing = root is not null
                    && file.StartsWith(root + Path.DirectorySeparatorChar,
                                       StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(destination, file[(root.Length + 1)..])
                    : Path.Combine(destination, Path.GetFileName(file));

                if (landing.Length > limits.MaxPathLength) toolong.Add(landing);
            }
            catch { }
        }

        if (toolong.Count > 0)
        {
            var longest = toolong.OrderByDescending(p => p.Length).First();

            return $"{toolong.Count} file(s) would end up with a path longer than the "
                 + $"{limits.MaxPathLength} characters a {limits.FileSystem} volume allows. "
                 + $"The longest is {longest.Length} characters. Nothing was moved. A shorter "
                 + "naming pattern, or a destination nearer the top of the drive, would fit.";
        }

        if (toobig.Count == 0) return null;

        var worst = toobig.OrderByDescending(f => f.Bytes).First();

        // Says what is wrong and stops there. Reporting a limit is the whole
        // job; what to do about the drive is the user's business and none of
        // this program's.
        return $"{toobig.Count} file(s) are too big for a {limits.FileSystem} volume, which cannot "
             + $"hold anything over {limits.MaxFileBytes / 1024 / 1024 / 1024} GB. The largest is "
             + $"{Path.GetFileName(worst.Path)} at {worst.Bytes / 1024 / 1024 / 1024.0:F1} GB. "
             + "Nothing was moved. Choose a destination on a different drive, or move these "
             + "files by hand.";
    }
}
