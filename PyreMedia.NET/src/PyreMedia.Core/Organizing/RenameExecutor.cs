using PyreMedia.Core.History;

namespace PyreMedia.Core.Organizing;

/// <summary>
/// Applies an approved plan. Only acts on the actions the user left selected,
/// and re-checks the filesystem as it goes - the plan may be minutes old.
/// Every change is written to <see cref="RenameHistory"/>.
/// </summary>
public sealed class RenameExecutor(PyreMediaSettings settings, RenameHistory? history = null)
{
    private readonly RenameHistory _history = history ?? new RenameHistory();

    /// <summary>
    /// Everything that would land on a file which already exists, gathered
    /// before anything is written.
    ///
    /// Asking per file as you go is the wrong shape: by the time the third
    /// prompt appears you have already committed to two answers you can no
    /// longer see. Collecting them first means the whole list can be reviewed
    /// and answered together.
    /// </summary>
    public List<FileConflict> FindConflicts(IEnumerable<PlannedAction> approved)
    {
        var conflicts = new List<FileConflict>();

        // Names claimed earlier in this same batch count as taken, even though
        // nothing has been written yet.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in approved.Where(a => a.Status == PlanStatus.Change && a.TargetPath is not null))
        {
            var target = a.TargetPath!;

            var videoClash = File.Exists(target)
                             && !string.Equals(a.SourcePath, target, StringComparison.OrdinalIgnoreCase);

            // A companion collision matters just as much: it is how artwork and
            // language-tagged subtitles get lost.
            var companionClash = a.Subtitles.Any(c =>
            {
                var ct = UniqueName.CompanionFor(c, a.SourcePath, target);
                return File.Exists(ct) && !string.Equals(c, ct, StringComparison.OrdinalIgnoreCase);
            });

            if (!videoClash && !companionClash && !claimed.Contains(target)) continue;

            conflicts.Add(new FileConflict
            {
                SourcePath = a.SourcePath,
                TargetPath = target,
                Companions = [.. a.Subtitles]
            });

            claimed.Add(target);
        }

        return conflicts;
    }

    public ExecutionResult Execute(
        RenamePlan plan,
        IEnumerable<PlannedAction> approved,
        IProgress<string>? log = null,
        CancellationToken ct = default,
        bool renameShowFolder = false,
        string? title = null,
        string? matchId = null,
        IReadOnlyList<FileConflict>? conflicts = null)
    {
        var result = new ExecutionResult();
        var batchId = Guid.NewGuid().ToString("N")[..8];
        result.BatchId = batchId;
        var list = approved.ToList();

        var moves = list.Where(a => a.Status == PlanStatus.Change && a.TargetPath is not null).ToList();
        var deletes = list.Where(a => a.Status == PlanStatus.Delete).ToList();

        // How each collision was answered, by source path.
        var decisions = (conflicts ?? [])
            .GroupBy(c => c.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Names taken by earlier files in this batch, so two "keep both" files
        // can't both be handed the same "(2)".
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Create target folders first, only the ones actually needed.
        // Folders that couldn't be made, so nothing can land in them.
        var unusableDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in moves
                     .Select(a => Path.GetDirectoryName(a.TargetPath!))
                     .OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    log?.Report($"Created folder: {dir}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Could not create '{dir}': {ex.Message}");
                unusableDirs.Add(dir);
            }
        }

        foreach (var action in moves)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var target = action.TargetPath!;

                // Its folder couldn't be made, so moving into it can only fail -
                // and fail obscurely, with "The parameter is incorrect" landing on
                // top of the message that actually explained the problem.
                var dir = Path.GetDirectoryName(target);
                if (dir is not null && unusableDirs.Contains(dir))
                {
                    result.Failed++;
                    continue;
                }

                // Honour the answer given for this collision, if there was one.
                if (decisions.TryGetValue(action.SourcePath, out var conflict))
                {
                    switch (conflict.Resolution)
                    {
                        case ConflictResolution.Skip:
                            result.Skipped++;
                            log?.Report($"Skipped {action.SourceName} - kept the existing file");
                            continue;

                        case ConflictResolution.KeepBoth:
                            // The whole set follows the video into its new name,
                            // or the artwork ends up orphaned from it.
                            target = conflict.KeepBothPath ?? UniqueName.For(target, claimed);
                            log?.Report($"Keeping both - {action.SourceName} -> {Path.GetFileName(target)}");
                            break;

                        case ConflictResolution.Replace:
                            // MoveOne already retires whatever is there.
                            break;

                        case ConflictResolution.Ask:
                            result.Skipped++;
                            result.Errors.Add($"{action.SourceName}: conflict was never answered");
                            continue;
                    }
                }

                claimed.Add(target);

                // Where the file really went, recorded on the action itself.
                //
                // "Keep both" resolved into this local and stopped there, so
                // the action went on naming the colliding path - the file the
                // user had just chosen to preserve. Everything downstream reads
                // TargetPath, so the .nfo and the artwork were written against
                // that file with the incoming match's data, while the file that
                // actually moved got neither and stayed invisible to Kodi.
                action.TargetPath = target;

                MoveOne(action.SourcePath, target, log, title, matchId, batchId);
                _history.Add(action.IsMove ? HistoryAction.Move : HistoryAction.Rename,
                             action.SourcePath, target, title, matchId, batchId);

                foreach (var sub in action.Subtitles)
                {
                    if (!File.Exists(sub)) continue;

                    var subTarget = UniqueName.CompanionFor(sub, action.SourcePath, target);

                    // Nothing to do at all.
                    if (string.Equals(sub, subTarget, StringComparison.Ordinal)) continue;

                    // Same file, different capitalisation: follow the video's
                    // recasing rather than skipping, or the artwork keeps the old
                    // spelling while the video gets the new one. It cannot
                    // collide - the only file matching it is itself - so the
                    // free-name logic below must be bypassed.
                    var recaseOnly = string.Equals(sub, subTarget, StringComparison.OrdinalIgnoreCase);

                    // Companions inherit the video's answer. Under Replace the
                    // existing one is retired; otherwise take a free name rather
                    // than overwrite - losing artwork silently is the bug this
                    // whole path exists to prevent.
                    if (!recaseOnly && (File.Exists(subTarget) || claimed.Contains(subTarget)))
                    {
                        var replacing = decisions.TryGetValue(action.SourcePath, out var d)
                                        && d.Resolution == ConflictResolution.Replace;

                        if (!replacing) subTarget = UniqueName.For(subTarget, claimed);
                    }

                    claimed.Add(subTarget);

                    try
                    {
                        MoveOne(sub, subTarget, log, title, matchId, batchId);
                        _history.Add(HistoryAction.Rename, sub, subTarget, title, matchId, batchId);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Companion '{Path.GetFileName(sub)}': {ex.Message}");
                    }
                }

                result.Succeeded++;
            }
            catch (Exception ex)
            {

                result.Failed++;
                result.Errors.Add($"{action.SourceName}: {ex.Message}");
                log?.Report($"FAILED {action.SourceName} - {ex.Message}");
            }
        }

        foreach (var action in deletes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                DeleteOne(action.SourcePath);
                _history.Add(HistoryAction.Delete, action.SourcePath, string.Empty, title, matchId, batchId);
                log?.Report($"{action.SourceName} - "
                            + Storage.VolumeInfo.For(action.SourcePath)
                                     .DeleteWording(settings.DeleteToRecycleBin));
                result.Deleted++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Delete '{action.SourceName}': {ex.Message}");
            }
        }

        // A folder that held nothing but what was just deleted.
        //
        // The Sample folder is the case that prompted this: tick the clip
        // inside it and what is left is an empty folder called Sample, which
        // the next scan has no reason to mention because a folder with no video
        // is not a library entry. Deleting the only thing in a folder is a
        // decision about the folder too.
        //
        // Only ever inside the item, only when the folder is genuinely empty,
        // and only after the deletion it followed actually happened.
        foreach (var folder in EmptiedByDeleting(deletes, plan.ShowFolder))
        {
            try
            {
                DeleteFolder(folder);
                log?.Report($"{Path.GetFileName(folder)} - empty after that, removed");
            }
            catch (Exception ex)
            {
                // Not a failed apply. The file went; the folder is just still
                // sitting there, which is what it was doing before.
                log?.Report($"Left \"{Path.GetFileName(folder)}\" - {ex.Message}");
            }
        }

        // Folder rename LAST - doing it earlier invalidates every path above.
        if (renameShowFolder && plan.FolderRename is { CanApply: true } fr)
        {
            try
            {
                RenameFolder(fr.SourcePath, fr.TargetPath);
                _history.Add(HistoryAction.FolderRename, fr.SourcePath, fr.TargetPath, title, matchId, batchId);
                result.FolderRenamed = fr.TargetName;
                log?.Report($"Folder: {fr.SourceName}  ->  {fr.TargetName}");

                // Every target above was worked out under the folder's old name,
                // and the folder has just moved out from under all of them. The
                // caller writes .nfo sidecars from these paths afterwards, and was
                // finding nothing there - so an apply that renamed the show folder
                // silently wrote no sidecars at all. Point them where the files
                // actually went.
                Rebase(list, fr.SourcePath, fr.TargetPath);
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"Folder rename '{fr.SourceName}': {ex.Message}");
            }
        }

        TidySourceFolders(plan, result, log);

        return result;
    }

    /// <summary>
    /// Move every planned target from under one folder to under another, after
    /// that folder has been renamed. Only the paths are rewritten - nothing on
    /// disk is touched, it has already moved.
    /// </summary>
    private static void Rebase(IEnumerable<PlannedAction> actions, string oldFolder, string newFolder)
    {
        var prefix = oldFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var a in actions)
        {
            if (a.TargetPath is not { } t) continue;
            if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            a.TargetPath = Path.Combine(newFolder, t[prefix.Length..]);
        }
    }

    /// <summary>
    /// New name for a companion file, keeping whatever sits between the stem and
    /// the extension.
    ///
    /// Renaming on extension alone collapsed every companion sharing one:
    /// "-poster.jpg", "-fanart.jpg" and "-thumb.jpg" all became "&lt;name&gt;.jpg"
    /// and overwrote each other, and "Show.eng.srt" lost its language. The middle
    /// part is what distinguishes them, so it has to survive.
    /// </summary>
    private static string CompanionName(string companion, string oldStem, string newStem)
    {
        var name = Path.GetFileName(companion);

        // "Tron (2010)-poster.jpg" with stem "Tron (2010)" -> keep "-poster.jpg"
        if (name.StartsWith(oldStem, StringComparison.OrdinalIgnoreCase))
            return newStem + name[oldStem.Length..];

        // Doesn't start with the stem - keep the whole original name rather than
        // invent one, so nothing can silently collide.
        return name;
    }

    private void MoveOne(string source, string target, IProgress<string>? log,
                         string? title = null, string? matchId = null, string? batchId = null)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("Source no longer exists (moved or deleted since the preview).");

        // Same file, different capitalisation. Windows compares names case
        // insensitively, so File.Exists(target) is true here even though there
        // is only one file - and the overwrite branch below would delete the
        // source, then fail to move it. Two steps through a temporary name is
        // the only safe way, exactly as RenameFolder already does.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(source, target, StringComparison.Ordinal))
        {
            var staging = source + ".msrename";

            var n = 2;
            while (File.Exists(staging)) staging = $"{source}.msrename{n++}";

            File.Move(source, staging);

            try
            {
                File.Move(staging, target);
            }
            catch
            {
                // Put it back rather than leave the file under a temp name.
                try { if (File.Exists(staging)) File.Move(staging, source); } catch { }
                throw;
            }

            log?.Report($"{Path.GetFileName(source)}  ->  {Path.GetFileName(target)}");
            return;
        }

        // Landing on another drive is a copy-then-delete, not a rename: it needs
        // room at the far end and takes real time on a 25 GB file. Failing after
        // most of that has been copied is the outcome worth avoiding.
        if (!Storage.VolumeInfo.SameVolume(source, target))
        {
            var size = new FileInfo(source).Length;
            var noRoom = Storage.VolumeInfo.WhyNoRoomFor(Path.GetDirectoryName(target)!, size);

            if (noRoom is not null)
                throw new IOException($"Can't move to another drive - {noRoom}");

            log?.Report($"Copying {Path.GetFileName(source)} to another drive "
                        + $"({size / 1024 / 1024 / 1024.0:F1} GB) - this takes a while");
        }

        if (File.Exists(target))
        {
            if (!settings.OverwriteFiles)
                throw new IOException("Target already exists and overwrite is disabled.");

            // Overwriting destroys a file the user never explicitly chose to
            // delete, so it goes through the same recycle-and-record path as an
            // ordinary deletion. Previously this was a bare File.Delete: gone for
            // good, and absent from the history.
            DeleteOne(target);

            _history.Add(HistoryAction.Delete, target, string.Empty,
                         title is null ? "overwritten" : $"overwritten by {title}", matchId, batchId);

            log?.Report($"Replaced existing: {Path.GetFileName(target)} - old copy "
                        + Storage.VolumeInfo.For(target).DeleteWording(settings.DeleteToRecycleBin));
        }

        File.Move(source, target);
        log?.Report($"{Path.GetFileName(source)}  ->  {Path.GetFileName(target)}");
    }

    private void DeleteOne(string path)
    {
        if (!File.Exists(path)) return;

        if (settings.DeleteToRecycleBin)
        {
            // Recoverable by default - deletion is the one action renaming back can't fix.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            File.Delete(path);
        }
    }

    private void DeleteFolder(string path)
    {
        if (!Directory.Exists(path)) return;

        if (settings.DeleteToRecycleBin)
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Directory.Delete(path);
    }

    /// <summary>
    /// Folders left empty by a set of deletions, innermost first.
    ///
    /// Walks up from each deleted file for as long as the folder is empty and
    /// still inside <paramref name="root"/>. The root itself is never returned:
    /// an item whose every file was deleted keeps its own folder, because
    /// removing that is a decision for the person looking at it rather than a
    /// side effect of ticking a row.
    /// </summary>
    private static List<string> EmptiedByDeleting(IEnumerable<PlannedAction> deletes, string root)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Prefix alone would let "Show Extras" pass for a child of "Show".
        static bool Inside(string dir, string root) =>
            dir.Length > root.Length
            && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (dir[root.Length] == Path.DirectorySeparatorChar
                || dir[root.Length] == Path.AltDirectorySeparatorChar);

        foreach (var action in deletes)
        {
            var dir = Path.GetDirectoryName(action.SourcePath);

            while (!string.IsNullOrEmpty(dir) && Inside(dir, root) && seen.Add(dir))
            {
                bool empty;
                try { empty = !Directory.EnumerateFileSystemEntries(dir).Any(); }
                catch (Exception) { break; }

                if (!empty) break;

                found.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        return found;
    }

    private static void RenameFolder(string source, string target)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("Source folder no longer exists.");

        // Case-only rename needs two steps: Windows treats the paths as identical.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(source, target, StringComparison.Ordinal))
        {
            var temp = source + "__msrename";
            Directory.Move(source, temp);
            Directory.Move(temp, target);
            return;
        }

        if (Directory.Exists(target))
            throw new IOException("A folder with that name already exists.");

        Directory.Move(source, target);
    }

    /// <summary>
    /// Remove the season folders a combined entry emptied, so eight husks aren't
    /// left behind where one show folder now stands.
    ///
    /// Only ever removes a folder holding nothing at all - not a file, not a
    /// subfolder. Anything still in there is something the rename didn't account
    /// for (a stray sidecar, a subtitle folder, a file the user skipped), and a
    /// folder with contents is never worth guessing about. Those are named in the
    /// log and left exactly where they are.
    /// </summary>
    /// <summary>
    /// Whether a folder holds no files at all, however deep.
    ///
    /// Asking whether it has any entries at all is the obvious question and the
    /// wrong one: a release folder whose grabs have just been deleted still
    /// holds the empty Screens folder they were in, so it read as occupied and
    /// was left standing in the library root with nothing in it - and had to be
    /// removed by hand, which is how it was noticed at all.
    ///
    /// A tree of empty folders is empty in the only sense that matters here -
    /// there is nothing in it to lose. Unreadable is not empty: if the question
    /// cannot be answered the folder stays.
    /// </summary>
    private static bool HoldsNoFiles(string dir)
    {
        try { return !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch (Exception) { return false; }
    }

    private static void TidySourceFolders(RenamePlan plan, ExecutionResult result, IProgress<string>? log)
    {
        if (plan.SourceFoldersToTidy.Count == 0) return;

        var removed = 0;
        var kept = new List<string>();

        foreach (var dir in plan.SourceFoldersToTidy)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                if (!HoldsNoFiles(dir))
                {
                    kept.Add(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)));
                    continue;
                }

                // Recursive, because what is left may be a tree of empty
                // folders rather than nothing at all.
                Directory.Delete(dir, true);
                removed++;
            }
            catch (Exception ex)
            {
                // Never a reason to call the run a failure - the files arrived.
                kept.Add(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)));
                result.Errors.Add($"Could not remove empty folder "
                                  + $"'{Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))}': {ex.Message}");
            }
        }

        if (removed > 0)
            log?.Report($"Removed {removed} empty folder(s) the seasons came from.");

        if (kept.Count > 0)
        {
            log?.Report($"{kept.Count} of the old season folder(s) still hold files that weren't part "
                        + "of the rename - sidecars, subtitles or artwork - so they were left alone. "
                        + "Nothing there is a video file; move or delete them yourself when you're ready: "
                        + string.Join(", ", kept));
        }
    }
}
