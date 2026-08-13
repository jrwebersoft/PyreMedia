using PyreMedia.Core.History;

namespace PyreMedia.Core.Organizing;

/// <summary>What moving a batch of finished files into a library did.</summary>
public sealed class MoveResult
{
    public int Moved { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>Folders left with nothing in them. Reported, never removed.</summary>
    public List<string> EmptyFolders { get; } = [];

    public List<string> Errors { get; } = [];

    public string BatchId { get; set; } = "";
}

/// <summary>
/// Moves finished files out of a staging folder and into a library.
///
/// One implementation for films, television, music and books, because the job
/// is identical in all four and the differences - what counts as finished, and
/// which destination applies - are decided before anything gets here. Writing
/// it twice would mean two places to get the cross-volume case wrong.
///
/// Renaming has already happened by this point, in the staging folder, so the
/// layout below the staging root is copied across exactly as it stands.
/// Recomputing the path here would apply the naming pattern a second time and
/// could disagree with the first if the pattern changed in between.
/// </summary>
public static class CompletedMover
{
    public static MoveResult Move(
        IEnumerable<string> files,
        string stagingRoot,
        string destination,
        RenameHistory? history = null,
        string? title = null,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new MoveResult { BatchId = Guid.NewGuid().ToString("N")[..8] };

        if (string.IsNullOrWhiteSpace(destination))
        {
            result.Errors.Add("no destination folder has been chosen for this kind of media");
            return result;
        }

        var all = files as IReadOnlyCollection<string> ?? [.. files];

        // Asked before anything is written. A bulk move that fails on file four
        // hundred because the destination is FAT32 leaves the library in a state
        // nobody asked for, and the error the filesystem gives - "not enough
        // space on the disk" - is untrue and sends people looking elsewhere.
        if (Volumes.Refuses(all, destination, stagingRoot) is { } refusal)
        {
            result.Errors.Add(refusal);
            return result;
        }

        var root = stagingRoot.TrimEnd(Path.DirectorySeparatorChar);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in all.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(source)) { result.Skipped++; continue; }

                if (Path.GetDirectoryName(source) is { } folder) folders.Add(folder);

                // Anything outside the staging root has no meaningful place
                // below the destination, and flattening it into the top of the
                // library would be worse than leaving it alone.
                if (!source.StartsWith(root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Skipped++;
                    log?.Report($"Left {Path.GetFileName(source)} - it is not under the scanned folder");
                    continue;
                }

                var target = Path.Combine(destination, source[(root.Length + 1)..]);

                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target),
                        StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // A file of that name already in the library is a real question -
                // the same episode at a different quality, most often - and not
                // one to answer silently in a bulk move.
                if (File.Exists(target))
                {
                    result.Skipped++;
                    log?.Report($"Left {Path.GetFileName(source)} - already in the library");
                    continue;
                }

                Transfer(source, target);
                history?.Add(HistoryAction.Move, source, target, title, null, result.BatchId);

                result.Moved++;
                log?.Report($"Moved {Path.GetFileName(source)}");
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }

        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    result.EmptyFolders.Add(folder);
            }
            catch { /* unreadable: not our business */ }
        }

        return result;
    }

    /// <summary>
    /// Move a file, falling back to copy-verify-delete across a volume boundary.
    ///
    /// A staging folder and a library are very often on different disks - that
    /// is most of the reason for having both - so this path is the normal case
    /// here rather than the exception. The delete happens only once the copy has
    /// been checked, or a disk that filled up mid-write takes the original too.
    /// </summary>
    private static void Transfer(string source, string target)
    {
        try
        {
            File.Move(source, target, overwrite: false);
            return;
        }
        catch (IOException) when (!SameVolume(source, target))
        {
        }

        var length = new FileInfo(source).Length;
        File.Copy(source, target, overwrite: false);

        var written = new FileInfo(target);

        if (!written.Exists || written.Length != length)
        {
            try { File.Delete(target); } catch { }
            throw new IOException(
                $"the copy came out {(written.Exists ? written.Length : 0)} bytes against "
                + $"{length} - the original has been left alone");
        }

        // Best effort. FAT32 and exFAT stamp times to the nearest two seconds,
        // so the value that comes back is not always the one that went in -
        // which is why nothing downstream compares timestamps to decide whether
        // a copy worked.
        try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source)); } catch { }

        File.Delete(source);
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)),
                                 Path.GetPathRoot(Path.GetFullPath(b)),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
