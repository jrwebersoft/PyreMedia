using PyreMedia.Core.History;
using PyreMedia.Core.Organizing;

namespace PyreMedia.Core.Music;

/// <summary>What a run of the music plan actually did.</summary>
public sealed class MusicResult
{
    public int Moved { get; set; }
    public int Copied { get; set; }
    public int Quarantined { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>Folders left with nothing in them once their music had moved.</summary>
    public List<string> EmptyFolders { get; } = [];

    public List<string> Errors { get; } = [];

    /// <summary>Groups this run's history entries so it can be undone as a unit.</summary>
    public string BatchId { get; set; } = "";

    public int Changed => Moved + Copied + Quarantined;
}

/// <summary>
/// Carries out a music plan.
///
/// Built to the same rules as the film and television side - every change goes
/// through the shared history so one batch reverts as a unit, and a collision
/// is answered before anything is written rather than resolved by inventing a
/// "(2)". Three things differ, because music needs them:
///
/// A move may cross volumes. A library assembled from several drives has
/// albums on all of them, and File.Move refuses to cross a volume boundary, so
/// those become copy-verify-delete with the delete conditional on the verify.
///
/// A file may be copied rather than moved, because one recording legitimately
/// belongs to two albums at once.
///
/// And nothing is deleted. Redundant copies are moved to a quarantine folder,
/// so a duplicate call that turns out to be wrong costs a move back rather
/// than a re-rip. The classifier agrees with the audio 95.2% of the time,
/// which is good and is not good enough to delete on.
/// </summary>
public sealed class MusicExecutor(RenameHistory? history = null, MusicTagWriter? tagger = null)
{
    /// <summary>
    /// Where redundant files go. Beside the library rather than inside it, so a
    /// later scan does not pick them straight back up.
    /// </summary>
    public const string QuarantineFolder = "PyreMedia Quarantine";

    /// <summary>
    /// Every planned destination that already exists on disk, or that two
    /// actions both want. Answer these before calling <see cref="Execute"/>.
    /// </summary>
    public List<FileConflict> FindConflicts(IEnumerable<MusicAction> approved)
    {
        var conflicts = new List<FileConflict>();
        var wanted = new Dictionary<string, MusicAction>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in approved)
        {
            if (action.Destination is not { } target) continue;

            // Same file, same place - a track already sitting where the plan
            // wants it is not a collision, it is nothing to do.
            if (Same(action.Track.Path, target)) continue;

            if (wanted.TryGetValue(target, out var earlier))
            {
                conflicts.Add(new FileConflict
                {
                    SourcePath = action.Track.Path,
                    TargetPath = target,
                    Reason = $"another file in this run also wants this name "
                           + $"({Path.GetFileName(earlier.Track.Path)})"
                });
                continue;
            }

            wanted[target] = action;

            if (File.Exists(target))
                conflicts.Add(new FileConflict
                {
                    SourcePath = action.Track.Path,
                    TargetPath = target,
                    Reason = "a file of this name is already there"
                });
        }

        return conflicts;
    }

    public MusicResult Execute(
        IEnumerable<MusicAction> approved,
        string libraryRoot,
        IReadOnlyList<FileConflict>? conflicts = null,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new MusicResult { BatchId = Guid.NewGuid().ToString("N")[..8] };
        var list = approved.ToList();

        var decisions = (conflicts ?? [])
            .GroupBy(c => c.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Folders that could not be created, so nothing can land in them. Without
        // this every file bound for one of them fails separately, with an error
        // that describes the move rather than the folder that was the problem.
        var unusable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in list
                     .Where(a => a.Destination is not null)
                     .Select(a => Path.GetDirectoryName(a.Destination!))
                     .OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                result.Errors.Add($"Could not create '{dir}': {ex.Message}");
                unusable.Add(dir);
            }
        }

        foreach (var action in list)
        {
            ct.ThrowIfCancellationRequested();

            if (action.Kind is MusicActionKind.Stay or MusicActionKind.Held) continue;

            var source = action.Track.Path;
            var folder = Path.GetDirectoryName(source);
            if (folder is not null) sourceFolders.Add(folder);

            try
            {
                if (action.Kind is MusicActionKind.Redundant)
                {
                    Quarantine(source, libraryRoot, result, log);
                    continue;
                }

                if (action.Destination is not { } target) continue;
                if (Same(source, target)) continue;

                var dir = Path.GetDirectoryName(target);
                if (dir is not null && unusable.Contains(dir)) { result.Failed++; continue; }

                if (decisions.TryGetValue(source, out var conflict))
                {
                    switch (conflict.Resolution)
                    {
                        case ConflictResolution.Skip:
                            result.Skipped++;
                            log?.Report($"Skipped {Path.GetFileName(source)} - kept the file already there");
                            continue;

                        case ConflictResolution.KeepBoth when conflict.KeepBothPath is { } both:
                            target = both;
                            break;
                    }
                }

                // A destination that still exists at this point was either never
                // reported or was answered with Replace. Either way it is about
                // to be lost, so it is recorded first.
                if (File.Exists(target))
                {
                    Quarantine(target, libraryRoot, result, log, count: false);
                    if (File.Exists(target)) { result.Failed++; continue; }
                }

                if (action.Kind is MusicActionKind.Move)
                {
                    Transfer(source, target, keepSource: false);
                    history?.Add(HistoryAction.Move, source, target,
                        action.Track.Album.Text, null, result.BatchId);
                    result.Moved++;
                }
                else
                {
                    // A copy that cannot be retagged must not be made. It would
                    // carry the source album's name into the target folder, so
                    // Kodi would show it as a second copy of the album it came
                    // from and the gap it was meant to fill would still be
                    // there - a wrong file in a place nobody thinks to look.
                    if (action.Retag is not null && tagger is null)
                    {
                        result.Skipped++;
                        result.Errors.Add(
                            $"{Path.GetFileName(source)}: not copied - it would need its tags "
                            + "rewritten to belong to the album it is filling, and no tag "
                            + "writer was supplied");
                        continue;
                    }

                    Transfer(source, target, keepSource: true);

                    if (action.Retag is { } retag)
                    {
                        // Not recorded separately: the copy's own history entry
                        // covers it, and undoing that deletes the file.
                        var written = tagger!.Write(new TrackTags { Path = target }, retag,
                            result.BatchId, ct, record: false);

                        // Undo the copy rather than leave a mislabelled file
                        // sitting in somebody's album.
                        if (!written.Written)
                        {
                            try { File.Delete(target); } catch { }

                            result.Failed++;
                            result.Errors.Add($"{Path.GetFileName(source)}: copied, but its tags "
                                + $"would not write ({written.Error}) - the copy was removed");
                            continue;
                        }
                    }

                    history?.Add(HistoryAction.Copy, source, target,
                        action.Track.Album.Text, null, result.BatchId);
                    result.Copied++;
                }

                log?.Report($"{action.Kind}: {Path.GetFileName(target)}");
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }

        // Reported, never removed. A folder that held only music now holds
        // nothing, and whether that is rubbish or a place the user keeps things
        // is not a decision this class gets to make.
        foreach (var folder in sourceFolders)
        {
            try
            {
                if (Directory.Exists(folder)
                    && !Directory.EnumerateFileSystemEntries(folder).Any())
                    result.EmptyFolders.Add(folder);
            }
            catch { /* unreadable: not our business either */ }
        }

        return result;
    }

    /// <summary>
    /// Move finished files out of the staging folder into the library.
    ///
    /// Finished means already correctly named where it sits - the music
    /// equivalent of the film side's AlreadyCorrect, which is the same thing
    /// that stops it offering to rename a file twice. Renaming happens in
    /// staging; this is the separate, optional step that relocates the result,
    /// and leaving everything in the staging folder is a perfectly good way to
    /// use the program.
    ///
    /// The layout below the staging root is preserved exactly, because the
    /// rename already put it right. Re-deriving the path here would apply the
    /// pattern twice and could disagree with itself if the pattern changed in
    /// between.
    /// </summary>
    public MusicResult MoveCompleted(
        IEnumerable<MusicAction> actions,
        string stagingRoot,
        string destination,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var finished = actions.Where(a => a.Kind is MusicActionKind.Stay).ToList();

        var moved = CompletedMover.Move(
            finished.Select(a => a.Track.Path),
            stagingRoot, destination, history,
            finished.FirstOrDefault()?.Track.Album.Text, log, ct);

        var result = new MusicResult
        {
            BatchId = moved.BatchId,
            Moved = moved.Moved,
            Skipped = moved.Skipped,
            Failed = moved.Failed
        };

        result.Errors.AddRange(moved.Errors);
        result.EmptyFolders.AddRange(moved.EmptyFolders);

        return result;
    }

    /// <summary>
    /// Every folder under a root that holds nothing at all, deepest first.
    ///
    /// Deepest first because emptying a folder can empty its parent: an album
    /// folder whose music moved out leaves the artist folder holding only that
    /// empty album, and a single pass from the top would miss it.
    /// </summary>
    public static List<string> FindEmptyFolders(string root, CancellationToken ct = default)
    {
        var empty = new List<string>();

        void Walk(string folder)
        {
            ct.ThrowIfCancellationRequested();

            string[] subs;

            try
            {
                // Never through a junction. A library assembled from several
                // sources is full of them, and following one could delete a
                // folder somewhere else entirely.
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
                subs = Directory.GetDirectories(folder);
            }
            catch { return; }

            foreach (var sub in subs) Walk(sub);

            try
            {
                // Counted after the children were walked, so a folder holding
                // only folders that are about to go still reads as empty here.
                if (Directory.GetFiles(folder).Length > 0) return;

                if (Directory.GetDirectories(folder).All(empty.Contains)) empty.Add(folder);
            }
            catch { }
        }

        foreach (var sub in SafeSubdirectories(root)) Walk(sub);

        return empty;
    }

    private static string[] SafeSubdirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return []; }
    }

    /// <summary>
    /// Remove folders that hold nothing.
    ///
    /// Not recorded in the history, and that is worth being plain about rather
    /// than quietly true: there is nothing to record. An empty folder has no
    /// contents to restore, so an undo could only recreate a name, and a
    /// history full of entries that restore nothing makes the ones that matter
    /// harder to find.
    ///
    /// Each one is checked again immediately before it goes, because the list
    /// was gathered earlier and something may have been written into it since.
    /// </summary>
    public static int RemoveEmptyFolders(
        IEnumerable<string> folders, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var removed = 0;

        // Deepest first, so a parent is only considered once its children have
        // actually gone rather than merely been listed.
        foreach (var folder in folders.OrderByDescending(f => f.Length))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!Directory.Exists(folder)) continue;
                if (Directory.EnumerateFileSystemEntries(folder).Any()) continue;

                Directory.Delete(folder);
                removed++;
            }
            catch (Exception ex)
            {
                log?.Report($"Could not remove '{folder}': {ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>
    /// Carry out a sweep: everything the survey found goes to quarantine.
    ///
    /// Not deleted, for the same reason nothing else here is. A sweep removes
    /// thousands of files in one go on the strength of a classifier reading
    /// file extensions, and the one that turns out to matter - a scan of the
    /// sleeve, a text file with the rip notes in it - is indistinguishable from
    /// the six thousand that do not until somebody looks. Quarantine makes
    /// looking possible afterwards instead of only before.
    /// </summary>
    public MusicResult Sweep(
        SweepResult survey,
        string libraryRoot,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new MusicResult { BatchId = Guid.NewGuid().ToString("N")[..8] };

        foreach (var item in survey.Items)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(item.Path)) { result.Skipped++; continue; }
                Quarantine(item.Path, libraryRoot, result, log);
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
            }
        }

        // Reported, not removed - the same rule as after a move.
        foreach (var folder in survey.MusiclessFolders)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    result.EmptyFolders.Add(folder);
            }
            catch { }
        }

        return result;
    }

    /// <summary>
    /// Move a file, or copy it, across volumes if need be.
    ///
    /// File.Move will not cross a volume boundary, and a library spread over
    /// several drives crosses them constantly. The fallback is a copy followed
    /// by a delete, and the delete happens only once the copy has been checked -
    /// otherwise a half-written file on a disk that filled up takes the original
    /// with it.
    /// </summary>
    private static void Transfer(string source, string target, bool keepSource)
    {
        if (!keepSource)
        {
            try
            {
                File.Move(source, target, overwrite: false);
                return;
            }
            catch (IOException) when (!Same(Root(source), Root(target)))
            {
                // Different volumes. Fall through to copy-verify-delete.
            }
        }

        var length = new FileInfo(source).Length;
        File.Copy(source, target, overwrite: false);

        var written = new FileInfo(target);
        if (!written.Exists || written.Length != length)
        {
            try { File.Delete(target); } catch { }
            throw new IOException(
                $"the copy came out {(written.Exists ? written.Length : 0)} bytes "
                + $"against {length} - the original has been left alone");
        }

        // Dates carry meaning in a library this old: when a track was ripped is
        // often the only record of where it came from.
        try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source)); } catch { }

        if (!keepSource) File.Delete(source);
    }

    /// <summary>
    /// Set a file aside rather than delete it, keeping enough of its path to
    /// tell two files of the same name apart.
    /// </summary>
    private void Quarantine(string path, string libraryRoot, MusicResult result,
                            IProgress<string>? log, bool count = true)
    {
        var root = Path.GetDirectoryName(libraryRoot.TrimEnd(Path.DirectorySeparatorChar))
                   ?? libraryRoot;
        var bin = Path.Combine(root, QuarantineFolder);

        // Two albums both hold "01 Intro.mp3"; flattening them into one folder
        // would put the second on top of the first, which is the exact accident
        // quarantine exists to prevent.
        var relative = path.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase)
            ? path[libraryRoot.Length..].TrimStart(Path.DirectorySeparatorChar)
            : Path.GetFileName(path);

        var target = Path.Combine(bin, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target)) target = UniqueName.For(target);

        Transfer(path, target, keepSource: false);
        history?.Add(HistoryAction.Quarantine, path, target, null, null, result.BatchId);

        if (count) result.Quarantined++;
        log?.Report($"Set aside: {Path.GetFileName(path)}");
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Root(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch { return ""; }
    }
}
