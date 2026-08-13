using PyreMedia.Core.Models;

namespace PyreMedia.Core.Organizing;

public enum PlanStatus
{
    /// <summary>File will be renamed and/or moved.</summary>
    Change,

    /// <summary>Already correct - nothing to do.</summary>
    AlreadyCorrect,

    /// <summary>Cannot act: unparseable name, no metadata match, or a conflict.</summary>
    Problem,

    /// <summary>File will be deleted (sample or leftover junk).</summary>
    Delete
}

public sealed class PlannedAction
{
    public required string SourcePath { get; init; }
    public string? TargetPath { get; set; }
    public required PlanStatus Status { get; set; }

    public EpisodeRef? Parsed { get; init; }
    public Episode? Episode { get; init; }

    /// <summary>
    /// The further episodes a multi-episode file also covers - the E02 of an
    /// S01E01E02. Kept so the .nfo can describe all of them; Kodi shows only what
    /// the .nfo mentions, so writing one block for a two-episode file loses the
    /// second episode from the library.
    /// </summary>
    public List<Episode> ExtraEpisodes { get; init; } = [];

    /// <summary>Every episode this file covers, in order.</summary>
    public IEnumerable<Episode> AllEpisodes =>
        Episode is null ? [] : [Episode, .. ExtraEpisodes];

    /// <summary>Why this file can't be acted on, when Status is Problem.</summary>
    public string? Problem { get; set; }

    /// <summary>Target exists and will be replaced.</summary>
    public bool WillOverwrite { get; set; }

    /// <summary>Companion subtitle files that move with this video.</summary>
    public List<string> Subtitles { get; init; } = [];

    public string SourceName => Path.GetFileName(SourcePath);
    public string? TargetName => TargetPath is null ? null : Path.GetFileName(TargetPath);

    public bool IsMove =>
        TargetPath is not null &&
        !string.Equals(Path.GetDirectoryName(SourcePath), Path.GetDirectoryName(TargetPath),
                       StringComparison.OrdinalIgnoreCase);

    public bool IsRename =>
        TargetPath is not null &&
        !string.Equals(SourceName, Path.GetFileName(TargetPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a file is proposed for deletion, when Status is Delete.</summary>
    public string? DeleteReason { get; set; }

    /// <summary>
    /// Whether this row starts ticked in the preview.
    ///
    /// Renames always do. A deletion normally does not - offering to delete
    /// something is not the same as deciding to - with one exception: the
    /// leftovers in a folder a combined show is emptying. Clearing those is the
    /// point of the operation, and leaving them unticked means the folders can
    /// never empty and so can never be removed, which is what the user asked for
    /// in the first place. Still a tick box; still refusable.
    /// </summary>
    public bool StartsSelected { get; init; }

    public string Description => Status switch
    {
        PlanStatus.AlreadyCorrect => "no change",
        PlanStatus.Problem => Problem ?? "problem",
        PlanStatus.Delete => DeleteReason ?? "delete",
        _ => (IsRename, IsMove) switch
        {
            (true, true) => "rename + move",
            (true, false) => "rename",
            (false, true) => "move",
            _ => "no change"
        }
    };
}

/// <summary>Renaming the show folder itself, e.g. "Firefly.2002.1080p" -> "Firefly (2002)".</summary>
public sealed class FolderRename
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }

    public string SourceName => Path.GetFileName(SourcePath);
    public string TargetName => Path.GetFileName(TargetPath);

    /// <summary>Set when the rename can't proceed (target exists, etc).</summary>
    public string? Problem { get; set; }

    public bool CanApply => Problem is null;
}

public sealed class RenamePlan
{
    public required string ShowFolder { get; init; }
    public required TvShow Show { get; init; }
    public List<PlannedAction> Actions { get; init; } = [];

    /// <summary>Pending rename of the show folder, or null when it already matches.</summary>
    public FolderRename? FolderRename { get; set; }

    /// <summary>Season folders that must be created before anything moves.</summary>
    public List<string> FoldersToCreate { get; init; } = [];

    /// <summary>
    /// This show keeps its episodes loose in the show folder, and the plan is
    /// about to gather them into Season folders.
    ///
    /// Worth saying out loud rather than leaving to be inferred from a list of
    /// moves. A library organised the flat way looks completely correct - the
    /// filenames are right, every episode is where its show is - so a plan full
    /// of moves against it is surprising until you know that a setting decided
    /// it. This is what lets the scan say so in the same breath.
    /// </summary>
    public bool GainsSeasonFolders =>
        FoldersToCreate.Count > 0
        && Changes.Any(a => a.TargetPath is { } t
                            && Path.GetDirectoryName(a.SourcePath) == ShowFolder
                            && Path.GetDirectoryName(t) != ShowFolder);

    /// <summary>
    /// The per-season folders a combined entry was assembled from. Once its files
    /// have moved into one show folder these are left standing and empty, so they
    /// are offered for tidying - and only removed if genuinely empty.
    /// </summary>
    public List<string> SourceFoldersToTidy { get; init; } = [];

    public IEnumerable<PlannedAction> Changes => Actions.Where(a => a.Status == PlanStatus.Change);
    public IEnumerable<PlannedAction> Problems => Actions.Where(a => a.Status == PlanStatus.Problem);
    public IEnumerable<PlannedAction> Deletions => Actions.Where(a => a.Status == PlanStatus.Delete);

    public int ChangeCount => Changes.Count();
    public int ProblemCount => Problems.Count();
    public int DeleteCount => Deletions.Count();
    public int UnchangedCount => Actions.Count(a => a.Status == PlanStatus.AlreadyCorrect);
    public bool HasWork => ChangeCount > 0 || DeleteCount > 0;
}

public sealed class ExecutionResult
{
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Deleted { get; set; }

    /// <summary>Files left alone because a conflict was answered with Skip.</summary>
    public int Skipped { get; set; }

    /// <summary>New show-folder name, when the folder was renamed.</summary>
    public string? FolderRenamed { get; set; }

    /// <summary>Groups this run's history entries so it can be reverted as a unit.</summary>
    public string? BatchId { get; set; }

    public List<string> Errors { get; init; } = [];
}
