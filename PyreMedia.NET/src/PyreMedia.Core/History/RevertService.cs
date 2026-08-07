namespace PyreMedia.Core.History;

public enum RevertState
{
    /// <summary>Can be put back exactly as it was.</summary>
    Ready,

    /// <summary>The file isn't where history says it ended up - moved or renamed since.</summary>
    SourceMissing,

    /// <summary>Something already occupies the original path.</summary>
    TargetOccupied,

    /// <summary>Deletions can't be undone from here - use the Recycle Bin.</summary>
    NotRevertible
}

public sealed class RevertCandidate
{
    public required HistoryEntry Entry { get; init; }
    public required RevertState State { get; init; }
    public string? Reason { get; init; }

    public bool CanRevert => State == RevertState.Ready;

    public string Describe() => State switch
    {
        RevertState.Ready => $"{Entry.NewName}  ->  {Entry.OldName}",
        RevertState.SourceMissing => $"'{Entry.NewName}' is no longer there",
        RevertState.TargetOccupied => $"'{Entry.OldName}' already exists",
        _ => Reason ?? "cannot be reverted"
    };
}

public sealed class RevertResult
{
    public int Reverted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// Puts renamed files and folders back. Works off the history log, so it can
/// undo a mistake discovered long after the fact - which is the case that
/// matters, since a bad match usually isn't obvious until later.
/// </summary>
public sealed class RevertService(RenameHistory history)
{
    /// <summary>
    /// Check what could actually be undone, without touching anything.
    /// <para>
    /// This has to walk the entries in the same order <see cref="Revert"/> will,
    /// because a folder rename undone early moves everything recorded inside it.
    /// A show folder is renamed last, so every file in the batch was logged under
    /// the folder's old name - paths that don't exist on disk right now but will
    /// by the time their turn comes. Checking them where they sit today marks the
    /// whole batch as missing and quietly makes it un-undoable.
    /// </para>
    /// </summary>
    public IReadOnlyList<RevertCandidate> Examine(IEnumerable<HistoryEntry> entries)
    {
        var input = entries.ToList();
        var results = new RevertCandidate[input.Count];

        var order = Enumerable.Range(0, input.Count)
                              .OrderByDescending(i => input[i].Timestamp)
                              .ToList();

        // Folder reverts decided so far: where a path will be once the revert has
        // run (Virtual) against where it physically is at this moment (Physical).
        var applied = new List<(string Virtual, string Physical)>();

        foreach (var i in order)
        {
            var e = input[i];

            if (e.Action == HistoryAction.Delete)
            {
                results[i] = new RevertCandidate
                {
                    Entry = e,
                    State = RevertState.NotRevertible,
                    Reason = "Deleted - restore it from the Recycle Bin"
                };
                continue;
            }

            var isFolder = e.Action == HistoryAction.FolderRename;
            var from = Locate(e.NewPath, applied);
            var to = Locate(e.OldPath, applied);

            var newExists = isFolder ? Directory.Exists(from) : File.Exists(from);
            var oldExists = isFolder ? Directory.Exists(to) : File.Exists(to);

            var state = !newExists ? RevertState.SourceMissing
                      : oldExists ? RevertState.TargetOccupied
                      : RevertState.Ready;

            results[i] = new RevertCandidate { Entry = e, State = state };

            if (state == RevertState.Ready && isFolder)
                applied.Add((e.OldPath, from));
        }

        return results;
    }

    /// <summary>
    /// Where a recorded path physically sits right now, given the folder reverts
    /// that will already have run by the time this entry's turn comes. Walked in
    /// reverse so nested folder renames unwind a layer at a time.
    /// </summary>
    private static string Locate(string path, List<(string Virtual, string Physical)> applied)
    {
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            var (v, p) = applied[i];

            if (path.Equals(v, StringComparison.OrdinalIgnoreCase))
            {
                path = p;
                continue;
            }

            var prefix = v.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(p, path[prefix.Length..]);
        }

        return path;
    }

    /// <summary>
    /// Undo the given changes. Applied newest-first, which puts a folder rename
    /// back before the files that were renamed inside it - those were logged under
    /// the folder's old name, so the folder has to carry that name again first.
    /// </summary>
    public RevertResult Revert(
        IEnumerable<RevertCandidate> candidates,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new RevertResult();
        var batchId = Guid.NewGuid().ToString("N")[..8];

        var ordered = candidates
            .Where(c => c.CanRevert)
            .OrderByDescending(c => c.Entry.Timestamp)
            .ToList();

        // Folder renames still in force - ones this run isn't putting back. A file
        // renamed before its show folder was renamed is logged under the folder's
        // old name, so undoing that one file on its own needs the path shifted to
        // where the file actually sits. Without this, reverting a single row out of
        // a batch silently does nothing.
        var inThisRun = ordered
            .Where(c => c.Entry.Action == HistoryAction.FolderRename)
            .Select(c => $"{c.Entry.OldPath}|{c.Entry.NewPath}|{c.Entry.Timestamp:O}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var standing = history.Read()
            .Where(h => h.Action == HistoryAction.FolderRename)
            .Where(h => !inThisRun.Contains($"{h.OldPath}|{h.NewPath}|{h.Timestamp:O}"))
            .Where(h => Directory.Exists(h.NewPath) && !Directory.Exists(h.OldPath))
            .OrderBy(h => h.Timestamp)
            .Select(h => (Virtual: h.OldPath, Physical: h.NewPath))
            .ToList();

        foreach (var c in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var e = c.Entry;

            try
            {
                var from = Locate(e.NewPath, standing);
                var to = Locate(e.OldPath, standing);

                if (e.Action == HistoryAction.FolderRename)
                {
                    if (!Directory.Exists(from))
                    {
                        result.Skipped++;
                        log?.Report($"Skipped: '{e.NewName}' is no longer there");
                        continue;
                    }

                    Directory.Move(from, to);
                }
                else
                {
                    if (!File.Exists(from))
                    {
                        result.Skipped++;
                        log?.Report($"Skipped: '{e.NewName}' is no longer there");
                        continue;
                    }

                    var dir = Path.GetDirectoryName(to);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    File.Move(from, to);
                }

                // The revert is itself a change worth recording - the log should
                // show the full story, not silently rewind.
                history.Add(e.Action, from, to,
                            e.Title is null ? "revert" : $"revert: {e.Title}",
                            e.MatchId, batchId);

                log?.Report($"Reverted: {e.NewName}  ->  {e.OldName}");
                result.Reverted++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{e.NewName}: {ex.Message}");
                log?.Report($"FAILED to revert {e.NewName} - {ex.Message}");
            }
        }

        result.Skipped += candidates.Count(c => !c.CanRevert);
        return result;
    }
}
