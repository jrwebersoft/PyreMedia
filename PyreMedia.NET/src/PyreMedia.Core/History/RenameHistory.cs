using System.Text.Json;
using System.Text.Json.Serialization;

namespace PyreMedia.Core.History;

public enum HistoryAction
{
    Rename,
    Move,
    FolderRename,
    Delete,

    // Appended, never reordered: these are serialised by their numeric value
    // and a history file written last month has to keep meaning the same thing.

    /// <summary>
    /// A file duplicated rather than moved - the same recording wanted in two
    /// albums at once. Reversed by deleting the copy, which is safe precisely
    /// because the original was left where it was.
    /// </summary>
    Copy,

    /// <summary>
    /// A redundant file set aside instead of deleted. <see cref="HistoryEntry.NewPath"/>
    /// is where it went, so reverting is a move back rather than a restore from
    /// nowhere. Deleting outright is not undoable and a duplicate detector is
    /// not sure enough to earn that.
    /// </summary>
    Quarantine,

    /// <summary>
    /// Tags rewritten in place, so both paths are the same one. What the fields
    /// held before is in <see cref="HistoryEntry.Before"/>, which is what makes
    /// this undoable at all.
    /// </summary>
    Retag
}

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; }
    public HistoryAction Action { get; set; }

    /// <summary>Groups every change made by a single Apply, so it can be undone as a unit.</summary>
    public string? BatchId { get; set; }

    /// <summary>Title matched at the time, e.g. "Firefly" or "The Matrix".</summary>
    public string? Title { get; set; }

    /// <summary>Provider id used for the match, for tracing a bad match later.</summary>
    public string? MatchId { get; set; }

    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;

    /// <summary>
    /// For a <see cref="HistoryAction.Retag"/>, what the fields held before it
    /// ran - the only record of them once the file has been rewritten. Null on
    /// every other action, and omitted from the file entirely, so history
    /// written before this existed still reads.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Before { get; set; }

    [JsonIgnore] public string OldName => Path.GetFileName(OldPath);
    [JsonIgnore] public string NewName => Path.GetFileName(NewPath);
    [JsonIgnore] public string Folder => Path.GetDirectoryName(OldPath) ?? string.Empty;
}

/// <summary>
/// Append-only record of every change made, stored as JSON Lines.
///
/// One line per change means a crash mid-write can at worst lose the last line,
/// never corrupt the file - which matters for a record whose whole purpose is
/// being trustworthy after something went wrong.
///
/// The motivating case: a show gets renamed against the wrong match, and weeks
/// later you need to know what a file used to be called.
/// </summary>
public sealed class RenameHistory
{
    private readonly string _path;
    private readonly Lock _lock = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public RenameHistory(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public static string DefaultPath => Path.Combine(AppPaths.Folder, "history.jsonl");

    public string FilePath => _path;

    public void Add(HistoryAction action, string oldPath, string newPath,
                    string? title, string? matchId, string? batchId = null,
                    Dictionary<string, string?>? before = null)
    {
        var entry = new HistoryEntry
        {
            Timestamp = DateTime.Now,
            Action = action,
            OldPath = oldPath,
            NewPath = newPath,
            Title = title,
            MatchId = matchId,
            BatchId = batchId,
            Before = before
        };

        var line = JsonSerializer.Serialize(entry, Options) + Environment.NewLine;

        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);

            // A few quick retries: antivirus, a backup agent or a second copy of
            // the app can hold the file open for a moment, and losing an undo
            // record to a transient lock would be a poor trade.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    File.AppendAllText(_path, line);
                    return;
                }
                catch (IOException ex)
                {
                    LastError = ex.Message;
                    Thread.Sleep(25 * (attempt + 1));
                }
                catch (Exception ex)
                {
                    // Permissions or a bad path won't fix themselves.
                    LastError = ex.Message;
                    break;
                }
            }

            // Failing to write history must never break a rename - but it must
            // not pass unnoticed either. The whole point of the record is being
            // there when something needs undoing.
            FailedWrites++;
        }
    }

    /// <summary>
    /// Entries that could not be written. Anything above zero means undo is
    /// incomplete for this session, which the user needs telling about.
    /// </summary>
    public int FailedWrites { get; private set; }

    public string? LastError { get; private set; }

    public void ResetFailureCount()
    {
        lock (_lock)
        {
            FailedWrites = 0;
            LastError = null;
        }
    }

    /// <summary>Newest first. <paramref name="filter"/> matches title or either filename.</summary>
    public IReadOnlyList<HistoryEntry> Read(string? filter = null, int max = 5000)
    {
        if (!File.Exists(_path)) return [];

        var result = new List<HistoryEntry>();

        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                HistoryEntry? entry;
                try { entry = JsonSerializer.Deserialize<HistoryEntry>(line, Options); }
                catch (JsonException) { continue; }   // skip a torn line, keep the rest

                if (entry is null) continue;

                if (!string.IsNullOrWhiteSpace(filter) &&
                    !Contains(entry.Title, filter) &&
                    !Contains(entry.OldPath, filter) &&
                    !Contains(entry.NewPath, filter))
                    continue;

                result.Add(entry);
            }
        }
        catch (IOException)
        {
            return result;
        }

        result.Reverse();
        return result.Count > max ? result[..max] : result;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public int Count()
    {
        try { return File.Exists(_path) ? File.ReadLines(_path).Count(l => !string.IsNullOrWhiteSpace(l)) : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// What makes one entry distinct from another. There is no id in the file, so
    /// this is the whole of what identifies a line - two identical renames a
    /// second apart still differ by timestamp.
    /// </summary>
    private static string KeyOf(HistoryEntry e) =>
        $"{e.Timestamp:O}|{e.Action}|{e.OldPath}|{e.NewPath}";

    /// <summary>
    /// Drop the given entries from the log. Rewritten through a temp file and
    /// swapped, so an interruption leaves the old log rather than half a new one.
    /// Returns how many lines went.
    /// </summary>
    public int Remove(IEnumerable<HistoryEntry> entries)
    {
        var doomed = entries.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        if (doomed.Count == 0) return 0;

        lock (_lock)
        {
            if (!File.Exists(_path)) return 0;

            var kept = new List<string>();
            var removed = 0;

            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                HistoryEntry? entry;
                try { entry = JsonSerializer.Deserialize<HistoryEntry>(line, Options); }
                catch (JsonException) { kept.Add(line); continue; }   // keep what we can't read

                if (entry is not null && doomed.Contains(KeyOf(entry))) { removed++; continue; }

                kept.Add(line);
            }

            if (removed > 0) ReplaceAll(kept);
            return removed;
        }
    }

    /// <summary>
    /// Empty the log completely. The file is left in place but empty, so the next
    /// change appends to it as usual.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(_path)) ReplaceAll([]);
        }
    }

    /// <summary>
    /// Rewrite the log, keeping a .bak of what was there. Deleting history throws
    /// away the only route back for those renames, so one undo of the undo is
    /// worth the disk space.
    /// </summary>
    private void ReplaceAll(List<string> lines)
    {
        var temp = _path + ".tmp";

        try
        {
            File.WriteAllLines(temp, lines);

            try { File.Copy(_path, _path + ".bak", overwrite: true); }
            catch (IOException) { /* a missing backup isn't worth failing the delete for */ }

            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }
}
