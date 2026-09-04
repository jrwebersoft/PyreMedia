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

    /// <summary>
    /// What the file carries, e.g. "6 audio - 25 subs".
    ///
    /// Blank until the probe has run and blank when there is nothing to say -
    /// one audio track and no subtitles is the ordinary case and printing it
    /// on every row would bury the files that are worth looking at.
    /// </summary>
    public string TrackBadge
    {
        get
        {
            if (!Media.Probed) return "";

            var parts = new List<string>(2);

            // Counts only. The languages used to be here too and did not fit -
            // on a row carrying a title and a foreign-audio badge as well, it
            // clipped to "2 audio (hin, e...", which is worse than not saying
            // it. The row's job is to show there is something here; what it is
            // is spelt out beside the Remux button, and named in full in the
            // tooltip.
            if (Media.AudioTracks > 1) parts.Add($"{Media.AudioTracks} audio");

            if (Media.SubtitleTracks > 0)
                parts.Add($"{Media.SubtitleTracks} sub{(Media.SubtitleTracks == 1 ? "" : "s")}");

            return string.Join("   ", parts);
        }
    }

    /// <summary>
    /// Every language spelt out, for the tooltip. "spa" is not something most
    /// people read fluently, and the badge has no room for "Spanish".
    /// </summary>
    public string TrackTooltip
    {
        get
        {
            if (!Media.Probed) return "";

            string Named(List<string> codes) => string.Join(", ", codes
                .Select(c => c is null or "" or "und" ? "unnamed" : LanguageCatalog.NameOf(c))
                .Distinct());

            var lines = new List<string>(3);

            if (Media.AudioTracks > 0)
                lines.Add($"Audio ({Media.AudioTracks}): {Named(Media.AudioLanguages)}");

            if (Media.SubtitleTracks > 0)
                lines.Add($"Subtitles ({Media.SubtitleTracks}): {Named(Media.SubtitleLanguages)}");

            if (lines.Count == 0) return "";

            lines.Add("Use Remux to drop the ones you'll never play.");
            return string.Join("\n", lines);
        }
    }

    public bool HasTrackBadge => TrackBadge.Length > 0;

    /// <summary>
    /// The same thing said beside the Remux button, where there is room to say
    /// it properly.
    ///
    /// The badge on the row has to fit a narrow list and gets clipped mid-word
    /// on a long one - "2 audio (hin, e..." - and it sits nowhere near the
    /// button that acts on it. Here the languages are spelt out, because "hin"
    /// is a code most people have to look up, and it reads into the button
    /// rather than away from it: this is what the file carries, and that is the
    /// thing that drops what you don't want.
    /// </summary>
    public string TrackCallout
    {
        get
        {
            if (!Media.Probed) return "";

            string Named(List<string> codes)
            {
                var names = codes
                    .Where(c => !string.IsNullOrWhiteSpace(c) && c != "und")
                    .Select(LanguageCatalog.NameOf)
                    .Distinct()
                    .ToList();

                return names.Count == 0 ? "" : $" ({string.Join(", ", names)})";
            }

            var parts = new List<string>(2);

            if (Media.AudioTracks > 1)
                parts.Add($"{Media.AudioTracks} audio{Named(Media.AudioLanguages)}");

            if (Media.SubtitleTracks > 0)
                parts.Add($"{Media.SubtitleTracks} subtitle{(Media.SubtitleTracks == 1 ? "" : "s")}{Named(Media.SubtitleLanguages)}");

            return string.Join("   ", parts);
        }
    }

    public bool HasTrackCallout => TrackCallout.Length > 0;

    /// <summary>Called after the background probe pass updates this item.</summary>
    public void RefreshProbeState()
    {
        OnPropertyChanged(nameof(ForeignBadge));
        OnPropertyChanged(nameof(HasForeign));
        OnPropertyChanged(nameof(TrackBadge));
        OnPropertyChanged(nameof(TrackTooltip));
        OnPropertyChanged(nameof(HasTrackBadge));
        OnPropertyChanged(nameof(TrackCallout));
        OnPropertyChanged(nameof(HasTrackCallout));
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

            // A disc rip says so on the row, because "9 files" invites you to
            // wonder why it will not match anything. The reason is that the
            // filenames are title indices and the folder is a volume label -
            // which is a thing to be told, not to work out.
            if (Media.IsDiscRip)
            {
                return Media.NameIsDiscLabel
                    ? $"{Media.Files.Count} titles from a disc · needs a name"
                    : $"{Media.Files.Count} titles from a disc";
            }

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
            // A deletion has no target, and a dash for one told nobody what
            // was about to happen. Say where it goes instead - these rows sit
            // among renames and read as one until they are read closely.
            if (Action.Status == PlanStatus.Delete)
                return Action.DeleteReason is { Length: > 0 } why
                    ? $"to the Recycle Bin - {why}"
                    : "to the Recycle Bin";

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

    /// <summary>
    /// Whether the tick box for this row does anything.
    ///
    /// Deletions belong here too. Restricting it to renames disabled the box
    /// on every Delete row, in both directions: the sample and junk files the
    /// scan offered to remove could never be ticked, so a folder of nothing but
    /// those left Apply permanently greyed out under a hint saying to tick
    /// something; and a deletion that arrived pre-ticked could never be
    /// refused, which is the opposite of what the plan promises.
    /// </summary>
    public bool CanSelect => Action.Status is PlanStatus.Change or PlanStatus.Delete;
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
