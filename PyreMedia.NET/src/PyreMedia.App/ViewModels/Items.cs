using CommunityToolkit.Mvvm.ComponentModel;
using PyreMedia.Core.Models;
using PyreMedia.Core.Organizing;

namespace PyreMedia.App.ViewModels;

/// <summary>
/// A row in the left-hand list. Wraps either a TV show folder or a movie, so
/// the shell doesn't need two list implementations.
/// </summary>
public partial class LibraryItem(MediaItem media) : ObservableObject
{
    public MediaItem Media { get; } = media;

    public string Name => Media.DisplayName;
    public string Path => Media.Path;
    public string SearchTerm => Media.SearchTitle;
    public string? SearchYear => Media.SearchYear;
    public MediaKind Kind => Media.Kind;
    public string KindLabel => Media.KindLabel;
    public bool IsTv => Media.Kind == MediaKind.TvEpisode;

    /// <summary>Languages present beyond the preferred one, e.g. "spa, fre".</summary>
    public string ForeignBadge => string.Join(", ", Media.ForeignAudio);

    public bool HasForeign => Media.HasForeignAudio;

    /// <summary>Called after the background probe pass updates this item.</summary>
    public void RefreshProbeState()
    {
        OnPropertyChanged(nameof(ForeignBadge));
        OnPropertyChanged(nameof(HasForeign));
    }

    /// <summary>Flip the detected kind when auto-detection guessed wrong.</summary>
    public void SetKind(MediaKind kind)
    {
        if (Media.Kind == kind) return;
        Media.Kind = kind;
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IsTv));
    }

    public string Subtitle
    {
        get
        {
            var files = Media.Files.Count == 1 ? "1 file" : $"{Media.Files.Count} files";
            return Media.IsLooseFile ? $"{files} · loose" : files;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial string? MatchedName { get; set; }

    /// <summary>
    /// The one secondary line on a row. Always populated so the row height never
    /// changes - a growing row shifts everything below it and clicks land wrong.
    /// </summary>
    public string StatusLine => string.IsNullOrWhiteSpace(MatchedName) ? Subtitle : MatchedName!;

    [ObservableProperty]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    public partial string? FolderRenamedTo { get; set; }

    public string DisplayName => FolderRenamedTo ?? Name;
}

/// <summary>One row in the preview grid.</summary>
public partial class PlannedActionItem : ObservableObject
{
    public PlannedAction Action { get; }

    public PlannedActionItem(PlannedAction action)
    {
        Action = action;
        IsSelected = action.Status == PlanStatus.Change || action.StartsSelected;
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string SourceName => Action.SourceName;
    public string TargetName => Action.TargetName ?? "-";

    /// <summary>
    /// Target including the folder it lands in, when that differs. Showing the
    /// bare filename made a move look like a no-op: the same name twice with
    /// "move" beside it, and no sign a folder was being created.
    /// </summary>
    public string TargetDisplay
    {
        get
        {
            if (Action.TargetPath is null) return "-";

            // Relative to where the file lives now - never the full path. The
            // scan root is the same for every row, so repeating it says
            // nothing, but everything below it matters: showing only the
            // immediate parent hid the movie folder that "Extras" nests in.
            var from = System.IO.Path.GetDirectoryName(Action.SourcePath);
            if (string.IsNullOrEmpty(from)) return Action.TargetName ?? "-";

            try
            {
                var rel = System.IO.Path.GetRelativePath(from, Action.TargetPath);

                // A move upwards would render as "..\..\x" - useless. Fall back.
                return rel.StartsWith("..", StringComparison.Ordinal)
                    ? Action.TargetName ?? "-"
                    : rel;
            }
            catch
            {
                return Action.TargetName ?? "-";
            }
        }
    }

    /// <summary>
    /// Plain description of what happens to this file. The old labels named the
    /// mechanism ("move") rather than the outcome, so a move into a brand-new
    /// folder read as a no-op when the filename didn't change.
    /// </summary>
    public string ActionLabel
    {
        get
        {
            if (Action.Status == PlanStatus.Delete) return "Delete";
            if (Action.Status == PlanStatus.AlreadyCorrect) return "Already correct";
            if (Action.Status == PlanStatus.Problem) return "Needs attention";

            var intoNewFolder = Action.IsMove && CreatesFolder;

            return (Action.IsRename, Action.IsMove, intoNewFolder) switch
            {
                (true, _, true) => "Rename, into new folder",
                (false, _, true) => "Into new folder",
                (true, true, false) => "Rename and move",
                (false, true, false) => "Move",
                (true, false, false) => "Rename",
                _ => "No change"
            };
        }
    }

    /// <summary>Set by the plan when this file's destination folder doesn't exist yet.</summary>
    public bool CreatesFolder { get; set; }
    public string Description => Action.Description;
    public PlanStatus Status => Action.Status;

    public bool CanSelect => Action.Status == PlanStatus.Change;
    public bool WillOverwrite => Action.WillOverwrite;

    public string EpisodeLabel => Action.Episode is { } e
        ? $"{e.SeasonNumber}x{e.Number:00}"
        : Action.Parsed?.ToString() ?? "";

    public string? Note => Action.Problem;
}

/// <summary>A search result the user can pick. Covers both TV and movies.</summary>
public sealed class SearchResultItem
{
    public ShowSearchResult? Show { get; }
    public MovieSearchResult? MovieResult { get; }

    public SearchResultItem(ShowSearchResult show)
    {
        Show = show;
        Name = show.Name;
        Display = show.Display;
        Year = show.Year;
        PosterUrl = show.PosterUrl;
        Ids = $"tvdb {show.TvdbId}";
        Overview = show.Overview;
    }

    public SearchResultItem(MovieSearchResult movie)
    {
        MovieResult = movie;
        Name = movie.Title;
        Display = movie.Display;
        Year = movie.Year;
        PosterUrl = movie.PosterUrl;
        Ids = $"tmdb {movie.TmdbId}";
        Overview = movie.Overview;
    }

    public string Name { get; }
    public string Display { get; }
    public string? Year { get; }
    public string? PosterUrl { get; }
    public string Ids { get; }
    public string? Overview { get; }

    public string ShortOverview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Overview)) return string.Empty;
            var o = Overview.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return o.Length <= 150 ? o : o[..150] + "...";
        }
    }
}
