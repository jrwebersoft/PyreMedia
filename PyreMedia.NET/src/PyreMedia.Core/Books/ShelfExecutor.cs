using PyreMedia.Core.History;

namespace PyreMedia.Core.Books;

/// <summary>What happened when a shelf plan was applied.</summary>
public sealed class ShelfResult
{
    public int Moved { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>Groups this run's history entries so it reverts as one thing.</summary>
    public string? BatchId { get; set; }

    public List<string> Trouble { get; init; } = [];
}

/// <summary>A file already sitting where something else wants to go.</summary>
public sealed record ShelfClash(ShelfAction Action, string Existing)
{
    public long ExistingBytes { get; init; }
    public long IncomingBytes { get; init; }
}

/// <summary>What to do about one.</summary>
public enum ShelfChoice { Skip, Replace, KeepBoth }

/// <summary>
/// Moving comics and ebooks where the plan says.
///
/// The same promises the rest of the program makes, for the same reasons. Every
/// move is written to the history before the next one starts, so a run
/// interrupted half way is still a run that can be undone. Nothing is
/// overwritten without being asked. A file that is already where it should be is
/// left alone rather than moved onto itself.
/// </summary>
public static class ShelfExecutor
{
    /// <summary>
    /// Names already taken at the destination, before anything moves.
    ///
    /// Found up front and asked about once, rather than one dialog per file
    /// half way through a hundred moves. A comic collection is exactly where
    /// this happens: the same issue arrives twice from different scanners and
    /// the names collapse to one.
    /// </summary>
    public static List<ShelfClash> FindClashes(ShelfPlan plan)
    {
        var clashes = new List<ShelfClash>();

        foreach (var action in plan.Moving)
        {
            if (action.Target is not { } target || !File.Exists(target)) continue;

            long incoming = 0, existing = 0;

            try { incoming = new FileInfo(action.Item.Path).Length; } catch (Exception) { }
            try { existing = new FileInfo(target).Length; } catch (Exception) { }

            clashes.Add(new ShelfClash(action, target)
            {
                IncomingBytes = incoming,
                ExistingBytes = existing
            });
        }

        return clashes;
    }

    /// <summary>
    /// Apply the plan.
    /// </summary>
    /// <param name="choices">
    /// What to do about each clash, by source path. Anything not answered for is
    /// skipped - silence means leave it alone, never overwrite.
    /// </param>
    public static ShelfResult Execute(
        ShelfPlan plan,
        RenameHistory history,
        IReadOnlyDictionary<string, ShelfChoice>? choices = null,
        bool recycleReplaced = true,
        Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ShelfResult { BatchId = Guid.NewGuid().ToString("N")[..8] };

        var moving = plan.Moving.ToList();
        var done = 0;

        foreach (var action in moving)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Invoke(++done, moving.Count);

            if (action.Target is not { } target) continue;

            try
            {
                if (!File.Exists(action.Item.Path))
                {
                    result.Skipped++;
                    result.Trouble.Add($"{action.Item.Name} is no longer there");
                    continue;
                }

                var finalTarget = target;

                if (File.Exists(target))
                {
                    var choice = choices is not null
                                 && choices.TryGetValue(action.Item.Path, out var c)
                        ? c
                        : ShelfChoice.Skip;

                    switch (choice)
                    {
                        case ShelfChoice.Skip:
                            result.Skipped++;
                            continue;

                        case ShelfChoice.KeepBoth:
                            finalTarget = FreeName(target);
                            break;

                        case ShelfChoice.Replace:
                            Remove(target, recycleReplaced, history, result.BatchId);
                            break;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(finalTarget)!);

                Transfer(action.Item.Path, finalTarget);

                // Written before the next move begins, so a run stopped half way
                // is still a run that undoes.
                history.Add(HistoryAction.Move, action.Item.Path, finalTarget,
                            action.Item.Title, null, result.BatchId);

                // The sidecars follow the comic.
                //
                // "Read the words" writes a script and a text file beside each
                // one, and reading a shelf is measured in hours. Moving only the
                // comic left both behind in the source folder, so the work was
                // orphaned - and the next scan of the old folder found scripts
                // with nothing to belong to.
                Companions(action.Item.Path, finalTarget, history, result.BatchId);

                result.Moved++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Failed++;
                result.Trouble.Add($"{action.Item.Name}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Move whatever was written beside a comic along with it.
    ///
    /// Recorded under the same batch as the comic, so the whole move still
    /// undoes as one thing. A sidecar that will not move is not worth failing
    /// the file over - the comic is where it should be, and the script can be
    /// written again.
    /// </summary>
    private static void Companions(string from, string to, RenameHistory history, string batchId)
    {
        foreach (var beside in new[]
                 {
                     (Was: ScriptNfo.PathFor(from), Now: ScriptNfo.PathFor(to)),
                     (Was: ReadableText.PathFor(from), Now: ReadableText.PathFor(to))
                 })
        {
            try
            {
                if (!File.Exists(beside.Was)) continue;
                if (File.Exists(beside.Now)) continue;

                File.Move(beside.Was, beside.Now);

                history.Add(HistoryAction.Move, beside.Was, beside.Now,
                            Path.GetFileName(beside.Now), null, batchId);
            }
            catch (Exception) { /* the comic moved; the script can be rewritten */ }
        }
    }

    /// <summary>"Saga (2012) #001 (2).cbz" - a free name beside the one taken.</summary>
    private static string FreeName(string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new IOException($"nowhere free beside {Path.GetFileName(target)}");
    }

    private static void Remove(string path, bool recycle, RenameHistory history, string batch)
    {
        if (recycle)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                history.Add(HistoryAction.Delete, path, "", Path.GetFileName(path), null, batch);
                return;
            }
            catch (Exception)
            {
                // No Recycle Bin on this volume - network shares and removable
                // drives have none. Fall through and say so by recording it.
            }
        }

        File.Delete(path);
        history.Add(HistoryAction.Delete, path, "", Path.GetFileName(path), null, batch);
    }

    /// <summary>
    /// Move a file, crossing volumes where it has to.
    ///
    /// File.Move refuses a volume boundary, and a comic library on its own drive
    /// crosses one every time. The fallback is copy, check, then delete - the
    /// delete only once the copy is known to be the right length, so a disk that
    /// filled up half way does not take the original with it.
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
            // Different drives. Copy and verify instead.
        }

        var length = new FileInfo(source).Length;

        File.Copy(source, target, overwrite: false);

        var written = new FileInfo(target);

        if (!written.Exists || written.Length != length)
        {
            try { File.Delete(target); } catch (Exception) { }
            throw new IOException($"the copy of {Path.GetFileName(source)} came out the wrong size");
        }

        File.Delete(source);
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(a)),
                Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Folders the move emptied, for offering to tidy afterwards.
    ///
    /// Only ones holding nothing at all - not "nothing of ours". A folder with a
    /// stray cover image or a note in it is a folder somebody put something in.
    /// </summary>
    public static List<string> EmptyFolders(IEnumerable<string> roots)
    {
        var empty = new List<string>();

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) empty.Add(dir);
                }
                catch (Exception) { /* unreadable is not empty */ }
            }
        }

        return empty;
    }
}
