namespace PyreMedia.Core.Storage;

/// <summary>
/// What the volume under a path will and won't do. Media libraries sprawl across
/// internal NTFS drives, USB drives formatted exFAT or FAT32, and network shares,
/// and those differ in ways that matter before anything is deleted or moved.
/// </summary>
public sealed record VolumeInfo
{
    public required string Root { get; init; }

    /// <summary>NTFS, exFAT, FAT32, ReFS - or empty if it couldn't be read.</summary>
    public required string Format { get; init; }

    public required DriveType Type { get; init; }
    public required bool IsReady { get; init; }

    /// <summary>
    /// Free space in bytes right now, or -1 if it couldn't be read. Deliberately
    /// not cached with the rest: a batch of remuxes eats into it as it runs, and
    /// a stale figure would wave through the file that fills the drive.
    /// </summary>
    public long FreeBytes
    {
        get
        {
            try { return new DriveInfo(Root).AvailableFreeSpace; }
            catch (Exception) { return -1; }
        }
    }

    public bool IsFat => Format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
                         || Format.Equals("exFAT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// FAT32 cannot hold a file of 4 GiB or more - a single movie routinely
    /// exceeds that, so a copy or remux fails part-way with a bare disk error.
    /// </summary>
    public long MaxFileSize =>
        Format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? 4L * 1024 * 1024 * 1024 - 1
        : Format.Equals("FAT16", StringComparison.OrdinalIgnoreCase) ? 2L * 1024 * 1024 * 1024 - 1
        : long.MaxValue;

    /// <summary>
    /// Whether a deleted file can actually be recovered. Windows does not recycle
    /// from network shares, and deletes from removable drives outright - the call
    /// still succeeds, so "sent to the Recycle Bin" would be a comfortable lie.
    /// </summary>
    public bool HasRecycleBin => Type is DriveType.Fixed or DriveType.Ram;

    /// <summary>How to describe a deletion on this volume, truthfully.</summary>
    public string DeleteWording(bool recycleRequested) =>
        !recycleRequested ? "deleted"
        : HasRecycleBin ? "sent to the Recycle Bin"
        : Type == DriveType.Network ? "deleted permanently - network shares have no Recycle Bin"
        : "deleted permanently - removable drives have no Recycle Bin";

    private static readonly Dictionary<string, VolumeInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The volume a path sits on. Cached: this is called per file during an apply
    /// and DriveInfo hits the filesystem every time.
    /// </summary>
    public static VolumeInfo For(string path)
    {
        var root = RootOf(path);

        lock (Cache)
        {
            if (Cache.TryGetValue(root, out var hit)) return hit;
        }

        var info = Read(root);

        lock (Cache)
        {
            Cache[root] = info;
        }

        return info;
    }

    /// <summary>Forget what was cached - after a drive is plugged in or mapped.</summary>
    public static void Forget()
    {
        lock (Cache) Cache.Clear();
    }

    private static VolumeInfo Read(string root)
    {
        // A UNC path has no DriveInfo at all; treat the share as the volume.
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new VolumeInfo
            {
                Root = root,
                Format = "",
                Type = DriveType.Network,
                IsReady = Directory.Exists(root)
            };
        }

        try
        {
            var d = new DriveInfo(root);
            var ready = d.IsReady;

            return new VolumeInfo
            {
                Root = root,
                Format = ready ? d.DriveFormat : "",
                Type = d.DriveType,
                IsReady = ready
            };
        }
        catch (Exception)
        {
            // An unmapped letter or a drive that vanished mid-scan.
            return new VolumeInfo { Root = root, Format = "", Type = DriveType.Unknown, IsReady = false };
        }
    }

    /// <summary>
    /// Why a file of this size can't be written into <paramref name="folder"/>, or
    /// null if it can. Checked before a long job starts rather than discovered at
    /// 90% with an hour of copying already burned.
    /// </summary>
    public static string? WhyNoRoomFor(string folder, long bytes)
    {
        var v = For(folder);

        if (!v.IsReady)
            return $"{v.Root} is not available";

        if (bytes > v.MaxFileSize)
            return $"{v.Format} can't hold a file of {Gb(bytes)} GB "
                   + $"- its limit is {Gb(v.MaxFileSize)} GB";

        // The new file is written beside the original, so for a while both exist.
        var free = v.FreeBytes;
        if (free >= 0 && free < bytes)
            return $"only {Gb(free)} GB free on {v.Root}, and this needs about {Gb(bytes)} GB";

        return null;
    }

    private static string Gb(long bytes) =>
        (bytes / 1024.0 / 1024 / 1024).ToString("F1", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Two paths on the same volume - a rename; different ones - a copy.</summary>
    public static bool SameVolume(string a, string b) =>
        string.Equals(RootOf(a), RootOf(b), StringComparison.OrdinalIgnoreCase);

    private static string RootOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);

            // \\server\share\... - the share is the unit, not \\server.
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = full.TrimStart('\\').Split(Path.DirectorySeparatorChar);
                return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : full;
            }

            return Path.GetPathRoot(full) ?? full;
        }
        catch (Exception)
        {
            return path;
        }
    }
}
