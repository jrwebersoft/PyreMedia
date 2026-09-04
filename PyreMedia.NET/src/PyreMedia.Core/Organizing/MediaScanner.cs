using PyreMedia.Core.Models;
using PyreMedia.Core.Naming;

namespace PyreMedia.Core.Organizing;

public enum MediaKind
{
    TvEpisode,
    Movie
}

/// <summary>
/// One scanned item: a folder of video files, or a loose file. Classified as TV
/// or movie from its contents rather than from which folder list it came from,
/// so mixed staging areas work without the user sorting them first.
/// </summary>
public sealed class MediaItem
{
    /// <summary>Folder containing the files (the parent, for a loose file).</summary>
    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Detected kind. Settable so the user can override a bad guess.</summary>
    public required MediaKind Kind { get; set; }

    /// <summary>Video files belonging to this item.</summary>
    public required List<string> Files { get; init; }

    /// <summary>Suggested search term.</summary>
    public required string SearchTitle { get; init; }

    public string? SearchYear { get; init; }

    /// <summary>
    /// The Kodi sidecar, when there is one. Its title, year and provider ids are
    /// authoritative in a way a filename never is - it was written by a scraper
    /// that already identified this exact file.
    /// </summary>
    public Metadata.NfoFile? Nfo { get; set; }

    /// <summary>Largest file - the feature, for movies.</summary>
    public required string MainFile { get; init; }

    /// <summary>True when this is a loose file directly in a scan root.</summary>
    public bool IsLooseFile { get; init; }

    /// <summary>
    /// A folder of titles straight off a disc - "title_t00.mkv" and its
    /// siblings - rather than named files.
    ///
    /// Kept as a fact about the item because everything downstream has to treat
    /// it differently: there is no title in the filename, the folder is named
    /// after the disc's volume label rather than the show, and which title is
    /// which episode is written down nowhere on the disc.
    /// </summary>
    public bool IsDiscRip { get; init; }

    /// <summary>
    /// True when even the folder name is a volume label - "MX2-0N-NW2_DES" -
    /// so there is nothing here to search a provider with.
    /// </summary>
    public bool NameIsDiscLabel { get; init; }

    /// <summary>
    /// The folders this entry was combined from, when several seasons of one
    /// show were sitting side by side. Empty for an ordinary entry.
    /// <para>
    /// A combined entry's Path is the folder those all sat in - usually the scan
    /// root - so it must never be renamed as though it were the show's own
    /// folder. The season folders below it are what get named.
    /// </para>
    /// </summary>
    public List<string> CombinedFrom { get; init; } = [];

    public bool IsCombined => CombinedFrom.Count > 0;

    /// <summary>Audio languages present that aren't the preferred one. Filled by the probe pass.</summary>
    public List<string> ForeignAudio { get; } = [];

    /// <summary>
    /// 3D layout read from the file itself, as a tag like "(3D-SBS)". Filled by
    /// the probe pass, and used only when the filename doesn't already say -
    /// a hand-written tag is the more reliable of the two, since half-width
    /// layouts are indistinguishable from 2D by geometry alone.
    /// </summary>
    public string? Detected3D { get; set; }

    /// <summary>Set once the file has been probed, so unprobed and clean are distinguishable.</summary>
    public bool Probed { get; set; }

    public bool HasForeignAudio => ForeignAudio.Count > 0;

    /// <summary>
    /// How many audio and subtitle tracks the file carries, once probed.
    ///
    /// Counted during the pass that already reads every file for foreign
    /// audio, so it costs nothing extra - the streams were in hand and were
    /// being thrown away. Worth showing because the number is what tells
    /// somebody whether a file is worth remuxing at all: a 2160p feature with
    /// six audio tracks and twenty-five subtitle tracks is carrying several
    /// gigabytes nobody in the house will ever play.
    /// </summary>
    public int AudioTracks { get; set; }

    public int SubtitleTracks { get; set; }

    /// <summary>
    /// Which languages the audio and subtitle tracks are in, in the order the
    /// file lists them, without repeats.
    ///
    /// Counts on their own are not actionable - six audio tracks you cannot
    /// identify tells you a file is large and nothing about whether the extras
    /// matter. Six tracks reading eng, spa, fre, pol, ita, deu tells you at a
    /// glance that five of them will never be played in this house.
    /// </summary>
    public List<string> AudioLanguages { get; } = [];

    public List<string> SubtitleLanguages { get; } = [];

    public string Root { get; init; } = string.Empty;

    public string KindLabel => Kind == MediaKind.TvEpisode ? "TV" : "Movie";
}

public sealed class MediaScanner(PyreMediaSettings settings)
{
    /// <summary>
    /// The folders to scan, reduced to the set that actually needs walking.
    /// <para>
    /// Comparing the configured strings isn't enough: "H:\Done" and "H:\Done\"
    /// are one folder but two strings, and either would be scanned twice - every
    /// show listed twice in the library, and the same file offered for renaming
    /// from two entries. A root nested inside another root is worse, because the
    /// duplicates sit at different paths and so look like separate items.
    /// </para>
    /// </summary>
    public static List<string> ScanRoots(PyreMediaSettings settings)
    {
        var candidates = settings.TvFolders
            .Concat(settings.MovieFolders)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Length)          // parents before their children
            .ToList();

        var kept = new List<string>();

        foreach (var root in candidates)
            if (!kept.Any(k => IsUnder(root, k)))
                kept.Add(root);

        return kept;
    }

    /// <summary>
    /// Whether two paths name the same folder, or the second sits inside the
    /// first. Trailing slashes, casing and relative segments all differ without
    /// meaning anything.
    /// </summary>
    public static bool SameFolder(string a, string b)
    {
        var x = NormalizeRoot(a);
        var y = NormalizeRoot(b);

        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase) || IsUnder(y, x);
    }

    internal static string NormalizeRoot(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception) { full = path; }

        var trimmed = full.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        // "H:\" would trim to "H:", which means the current directory on H:
        // rather than the drive itself. Those keep their separator.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    private static bool IsUnder(string child, string parent)
    {
        var prefix = parent.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? parent
            : parent + System.IO.Path.DirectorySeparatorChar;

        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a directory is a link whose target is the scan root or sits inside
    /// it. Following one of those walks the same files again under a second set of
    /// paths; a link pointing somewhere genuinely separate is fine and is kept.
    /// </summary>
    private static bool PointsBackInto(string dir, string root)
    {
        try
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) return false;

            var resolved = Directory.ResolveLinkTarget(dir, returnFinalTarget: true);
            if (resolved is null) return false;

            var target = NormalizeRoot(resolved.FullName);
            var scanned = NormalizeRoot(root);

            return string.Equals(target, scanned, StringComparison.OrdinalIgnoreCase)
                   || IsUnder(target, scanned);
        }
        catch (Exception)
        {
            // Can't tell what it points at - treat it as ordinary and scan it.
            return false;
        }
    }

    /// <summary>
    /// True when the path sits inside the archive folder. Archived pre-remux
    /// originals live under the library but aren't part of it - scanning them
    /// would double every item and offer to remux the safety copies.
    /// </summary>
    public static bool IsArchivedPath(string path, string archiveFolderName)
    {
        if (string.IsNullOrWhiteSpace(archiveFolderName)) return false;

        var sep = System.IO.Path.DirectorySeparatorChar;
        return path.Contains($"{sep}{archiveFolderName}{sep}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How deep scans enumerate.
    ///
    /// Not SearchOption.AllDirectories: that overload uses
    /// EnumerationOptions.Compatible, where IgnoreInaccessible is false - so a
    /// single permission-denied subfolder threw and abandoned the whole root.
    /// It also recurses into junctions and symlinks without limit, so a reparse
    /// point aimed at an ancestor loops until something gives out. A depth cap
    /// stops that while still following genuine symlinked libraries.
    /// </summary>
    internal static readonly EnumerationOptions DeepScan = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 24
    };

    /// <summary>
    /// Scan every configured root (TV and movie lists both) and classify what's
    /// found. A folder is TV if any video inside parses as a season/episode.
    /// </summary>
    /// <param name="say">
    /// Which folder is being looked at now.
    ///
    /// There is no total to count against here and there should not be one: the
    /// only way to know how many folders a library has is to walk it, and
    /// walking it is the whole job. So this says where it has got to rather
    /// than pretending to a fraction - which is more use anyway when a scan
    /// stalls, because it names the folder that stalled it.
    /// </param>
    public IReadOnlyList<MediaItem> Scan(
        IList<string>? problems = null,
        IProgress<string>? say = null,
        CancellationToken ct = default)
    {
        var found = new List<MediaItem>();

        // What belongs to an item, which is not the same as what can be opened:
        // a disc image is named and filed like any film and never read.
        var exts = settings.ScannedExtensions;
        var subs = settings.SubtitleExtensions;

        bool IsVideo(string f)
        {
            var e = System.IO.Path.GetExtension(f).ToLowerInvariant();
            if (!exts.Contains(e) || subs.Contains(e)) return false;

            // Archived pre-remux originals live under the library but are not
            // part of it - scanning them would double every item and offer to
            // remux the very copies kept as a safety net.
            return !IsArchivedPath(f, settings.ArchiveFolderName);
        }

        var roots = ScanRoots(settings);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                problems?.Add($"Folder not found (skipped): {root}");
                continue;
            }

            say?.Report(root);

            try
            {
                // Loose files sitting directly in the root, gathered by show.
                var loose = new List<MediaItem>();

                foreach (var file in Directory.EnumerateFiles(root).Where(IsVideo))
                {
                    ct.ThrowIfCancellationRequested();
                    loose.Add(Classify(root, System.IO.Path.GetFileName(file), [file], root, loose: true));
                }

                found.AddRange(GatherLooseEpisodes(loose));

                // One entry per subfolder.
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();

                    var name = System.IO.Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;

                    // A junction pointing back into the library would list every
                    // show a second time under a second path, both naming the same
                    // physical files - rename one and the other's source vanishes.
                    // Links out to genuinely separate media are left alone.
                    if (PointsBackInto(dir, root))
                    {
                        problems?.Add($"Skipped '{name}' - it links back into {root}");
                        continue;
                    }

                    // Per-folder guard: one unreadable show folder must not take
                    // the rest of the library down with it.
                    List<string> videos;
                    try
                    {
                        videos = Directory
                            .EnumerateFiles(dir, "*", DeepScan)
                            .Where(IsVideo)
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        problems?.Add($"Could not read '{name}': {ex.Message}");
                        continue;
                    }

                    if (videos.Count == 0) continue;

                    found.Add(Classify(dir, name, videos, root, loose: false));
                }
            }
            catch (Exception ex)
            {
                problems?.Add($"Cannot read {root}: {ex.Message}");
            }
        }

        return [.. found.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Put loose episodes of one show together into a single entry.
    ///
    /// A folder of episodes has always been one item. Loose files were one item
    /// each, so eight episodes of Reacher dropped in a scan root arrived as eight
    /// separate rows, each wanting its own search and its own match - for one
    /// show, whose answer is the same eight times.
    ///
    /// Only television, and only where the parsed show title agrees. Two loose
    /// films are two films however alike their names, and a file whose title
    /// could not be parsed is left on its own rather than swept into whichever
    /// group it most resembles.
    /// </summary>
    private static IEnumerable<MediaItem> GatherLooseEpisodes(List<MediaItem> loose)
    {
        var groups = loose
            .Where(i => i.Kind == MediaKind.TvEpisode
                        && !string.IsNullOrWhiteSpace(i.SearchTitle))
            .GroupBy(i => (i.SearchTitle.Trim(), i.SearchYear ?? ""),
                     TitleYear)
            .ToList();

        var gathered = groups.Where(g => g.Count() > 1).ToList();
        var taken = gathered.SelectMany(g => g).ToHashSet();

        foreach (var item in loose.Where(i => !taken.Contains(i)))
            yield return item;

        foreach (var group in gathered)
        {
            var files = group.SelectMany(i => i.Files).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var first = group.First();

            yield return new MediaItem
            {
                Path = first.Path,               // the scan root, as before
                DisplayName = first.SearchYear is { Length: 4 } y
                    ? $"{first.SearchTitle} ({y})"
                    : first.SearchTitle,
                Kind = MediaKind.TvEpisode,
                Files = files,
                MainFile = files.OrderByDescending(SafeLength).First(),
                SearchTitle = first.SearchTitle,
                SearchYear = first.SearchYear,
                IsLooseFile = true,
                Root = first.Root,
                Nfo = group.Select(i => i.Nfo).FirstOrDefault(n => n is not null)
            };
        }
    }

    /// <summary>Show title and year compared the way a person would read them.</summary>
    private static readonly IEqualityComparer<(string Title, string Year)> TitleYear =
        new TitleYearComparer();

    private sealed class TitleYearComparer : IEqualityComparer<(string Title, string Year)>
    {
        public bool Equals((string Title, string Year) a, (string Title, string Year) b) =>
            string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Year, b.Year, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Title, string Year) v) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(v.Title),
                StringComparer.OrdinalIgnoreCase.GetHashCode(v.Year));
    }

    private MediaItem Classify(string path, string displayName, List<string> videos, string root, bool loose)
    {
        // A "Season 01" or "Specials" folder settles it on its own. This is the
        // only signal that survives files carrying no numbering at all - which
        // is precisely when the episode parse can't help - and it's how badly
        // named rips ("1.mkv", "The Episode Title.mkv") get recognised.
        var inSeasonFolder = videos.Any(v =>
            EpisodeMatcher.ParseSeasonFolder(
                System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(v) ?? ""),
                settings.SeasonFolderName) is not null);

        // Otherwise TV if any video parses as an episode; also accept the folder
        // name itself, since release folders are usually named for the episode
        // they contain.
        var isTv = inSeasonFolder
                   || videos.Any(v => EpisodeMatcher.Parse(System.IO.Path.GetFileName(v)) is not null)
                   || EpisodeMatcher.Parse(displayName) is not null;

        var main = videos.Count == 1
            ? videos[0]
            : videos.OrderByDescending(v => SafeLength(v)).First();

        // A sidecar nfo beats every guess we could make from the name: it was
        // written after something already identified this exact file.
        var nfo = Metadata.NfoFile.ForVideo(main);

        if (nfo is { Kind: Metadata.NfoKind.Movie } && !isTv
            && !string.IsNullOrWhiteSpace(nfo.Title))
        {
            return new MediaItem
            {
                Path = path,
                DisplayName = displayName,
                Kind = MediaKind.Movie,
                Files = videos,
                MainFile = main,
                SearchTitle = nfo.Title!,
                SearchYear = nfo.Year,
                IsLooseFile = loose,
                Root = root,
                Nfo = nfo
            };
        }

        // Straight off a disc. Named for the title index, so no episode parse
        // will ever succeed and no provider search will match the folder.
        if (DiscRip.LooksLikeARip(videos))
        {
            var label = DiscRip.LooksLikeADiscLabel(displayName);

            // A rip of several similar titles is a TV disc; searching it as a
            // film would ask a provider about a folder of nine episodes.
            var (ripTerm, ripYear) = label
                ? ("", (string?)null)
                : NameFormatter.ParseTvName(displayName, settings.SearchTermFilters);

            return new MediaItem
            {
                Path = path,
                DisplayName = displayName,
                Kind = MediaKind.TvEpisode,
                Files = videos,
                MainFile = main,
                SearchTitle = ripTerm,
                SearchYear = ripYear,
                IsLooseFile = loose,
                IsDiscRip = true,
                NameIsDiscLabel = label,
                Root = root,
                Nfo = nfo
            };
        }

        if (isTv)
        {
            // Title is whatever precedes the season/episode marker, with any
            // year lifted out into its own field.
            var (term, year) = NameFormatter.ParseTvName(
                Stereo3DTag.Strip(displayName), settings.SearchTermFilters);

            if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
                (term, year) = NameFormatter.ParseTvName(
                    Stereo3DTag.Strip(System.IO.Path.GetFileName(main)), settings.SearchTermFilters);

            return new MediaItem
            {
                Path = path,
                DisplayName = displayName,
                Kind = MediaKind.TvEpisode,
                Files = videos,
                MainFile = main,
                SearchTitle = term,
                SearchYear = year,
                IsLooseFile = loose,
                Root = root,
                Nfo = nfo
            };
        }

        // Strip a leading 3D tag first - "(3D-SBS) 47 Ronin" must search as
        // "47 Ronin", not carry the marker into the query.
        var parsed = MovieMatcher.Parse(Stereo3DTag.Strip(displayName));
        if (string.IsNullOrWhiteSpace(parsed.Title))
            parsed = MovieMatcher.Parse(Stereo3DTag.Strip(System.IO.Path.GetFileName(main)));

        return new MediaItem
        {
            Path = path,
            DisplayName = displayName,
            Kind = MediaKind.Movie,
            Files = videos,
            MainFile = main,
            SearchTitle = parsed.Title,
            SearchYear = parsed.Year,
            IsLooseFile = loose,
            Root = root,
            Nfo = nfo
        };
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// Folders under a scan root that hold no video anywhere beneath them and
    /// nothing but release junk - the husk a release folder becomes once its
    /// episodes have been gathered elsewhere.
    ///
    /// These never appear in the library, because a folder with no video isn't an
    /// entry, so without this they are invisible and accumulate. A folder is only
    /// listed when every single file in it is on the junk list; one subtitle, one
    /// real .nfo, one stray photograph and it is somebody's folder, not a husk.
    ///
    /// A folder containing a junction or symbolic link is never listed, however
    /// harmless everything on the far side looks. Walking through a link means
    /// judging - and then deleting - files that are not in the library at all,
    /// and the folder being removed wouldn't even be where they live.
    /// </summary>
    public List<string> FindLeftoverFolders()
    {
        var junkExts = settings.JunkExtensionList;
        var found = new List<string>();

        foreach (var root in ScanRoots(settings))
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); }
            catch { continue; }

            found.AddRange(dirs.Where(dir => HoldsNothingWorthKeeping(dir, junkExts, 0)));
        }

        return found;
    }

    /// <summary>
    /// Whether everything in a folder is release junk, or there is nothing at all.
    ///
    /// Walked by hand rather than with RecurseSubdirectories, so that a link is a
    /// hard stop instead of a doorway out of the library. Anything that can't be
    /// read counts as a reason to leave the folder alone: this decides what may be
    /// deleted, so every uncertainty has to resolve to "no".
    /// </summary>
    private static bool HoldsNothingWorthKeeping(string dir, string[] junkExts, int depth)
    {
        if (depth > 24 || IsLink(dir)) return false;

        string[] files, subs;
        try
        {
            files = Directory.GetFiles(dir);
            subs = Directory.GetDirectories(dir);
        }
        catch { return false; }

        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (!junkExts.Contains(ext)) return false;

            // Real Kodi metadata is not junk, whatever its extension.
            if (ext == ".nfo" && MediaPlanner.IsMetadataNfo(f)) return false;

            // Nor is a searchable text file this program wrote, nor a script.
            if (ext == ".txt" && Books.ReadableText.IsSearchableText(f)) return false;
            if (ext == ".nfo" && Books.ScriptNfo.IsScript(f)) return false;
        }

        return subs.All(sub => HoldsNothingWorthKeeping(sub, junkExts, depth + 1));
    }

    /// <summary>A junction, symbolic link or other reparse point.</summary>
    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return true; }             // unreadable counts as "don't touch"
    }

}

/// <summary>
/// Plans renames for a scanned item. In flat mode (the default) files are
/// renamed where they sit - no season folders created, no folder renamed.
/// </summary>
public sealed class MediaPlanner(PyreMediaSettings settings)
{
    public RenamePlan PlanTv(MediaItem item, TvShow show, EpisodeOverrides? overrides = null)
    {
        // Fresh listings: the previous plan's may predate a rename.
        _dirCache.Clear();
        _downloading.Clear();
        _downloadingItem = JobStillRunning(item);

        var plan = new RenamePlan
        {
            ShowFolder = item.Path,
            Show = show,

            // A combined entry always moves its files out of these.
            SourceFoldersToTidy = [.. item.CombinedFrom]
        };
        var claimed = new Dictionary<string, PlannedAction>(StringComparer.OrdinalIgnoreCase);

        // The shift between how the files are numbered and how the metadata
        // numbers them. Looked up per season, below, since each season is
        // numbered - and misnumbered - on its own.

        // Samples must be identified BEFORE episode matching. A file like
        // "sample.mkv" has no SxxExx of its own, so it would otherwise inherit
        // the episode number from the folder name and collide with the real file.
        //
        // The word is a hint and not proof, which is what IsSample says at
        // length and what this used to ignore: an episode called "Free Sample",
        // or a release named "...-sample-fix.mkv", was dropped from the plan
        // outright - no row, no problem, not counted, absent from the preview
        // with nothing anywhere saying why. So size has the final say here too.
        // A real sample clip is a fraction of the episodes it ships beside.
        var biggest = item.Files.Max(SizeOrZero);

        // A folder named Sample is unambiguous - a release puts one there to
        // hold exactly one kind of thing - so that alone is enough. The size
        // guard stays for the filename, where it is earning its keep: a film
        // actually called "Free Sample" sitting on its own is the biggest file
        // there, so it fails the test and gets matched as the feature it is.
        var samples = item.Files
            .Where(f => InSampleFolder(f, item.Path)
                        || (SaysSample(Path.GetFileName(f))
                            && (biggest <= 0 || SizeOrZero(f) * 4 < biggest)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var episodeFiles = item.Files.Where(f => !samples.Contains(f)).ToList();

        // Set aside rather than vanished. Whatever is left out of the matching
        // still gets a row, so the counts add up to what is in the folder.
        foreach (var sample in samples.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            // Offered for removal, not merely noted. "Needs attention" with an
            // empty target was the worst of both: a line in the preview, a tick
            // box that did nothing, and the clip still on disk afterwards - and
            // this row was itself the reason it was never offered properly,
            // because AddCleanup skips any file the plan already has a row for.
            //
            // Unticked, like every deletion here, and only where the option is
            // on. With it off the row stays as it was: said, counted, and
            // nothing done about it.
            plan.Actions.Add(settings.DeleteSamples
                ? new PlannedAction
                {
                    SourcePath = sample,
                    Status = PlanStatus.Delete,
                    DeleteReason = "sample clip",
                    StartsSelected = StartsTicked(sample, item.Path)
                }
                : new PlannedAction
                {
                    SourcePath = sample,
                    Status = PlanStatus.Problem,
                    Problem = "Looks like a sample clip beside the episodes, so it was left alone"
                });
        }

        // The folder name only stands in for a missing episode number when
        // there's exactly one candidate file - otherwise it's ambiguous.
        var allowFolderFallback = episodeFiles.Count == 1;

        foreach (var file in episodeFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);

            // Explicitly excluded on the renumber screen: not a change, not a
            // problem, just left alone.
            if (overrides?.IsSkipped(file) == true)
                continue;

            // A manual assignment replaces parsing and the offset outright -
            // it exists precisely because those got it wrong.
            var manual = overrides?.For(file);

            var parsed = manual is { } mv
                ? new EpisodeRef(mv.Season, mv.Episode)
                : EpisodeMatcher.Parse(fileName)
                  ?? (allowFolderFallback ? EpisodeMatcher.Parse(item.DisplayName) : null);

            if (parsed is null)
            {
                plan.Actions.Add(new PlannedAction
                {
                    SourcePath = file,
                    Status = PlanStatus.Problem,
                    Problem = "No season/episode number in the filename or folder name"
                });
                continue;
            }

            var season = parsed.Value.Season
                         ?? EpisodeMatcher.ParseSeasonFolder(
                                Path.GetFileName(Path.GetDirectoryName(file) ?? ""), settings.SeasonFolderName);

            if (season is null)
            {
                plan.Actions.Add(new PlannedAction
                {
                    SourcePath = file,
                    Parsed = parsed,
                    Status = PlanStatus.Problem,
                    Problem = $"Episode {parsed.Value.Episode} found, but no season number"
                });
                continue;
            }

            // The offset corrects a systematic misread of the regular seasons. A
            // manual assignment is already the final answer, so shifting it would
            // undo the fix - and specials are numbered on their own, unrelated
            // run, so an offset meant for season 1 would quietly misname every one
            // of them.
            var shift = manual is null && season.Value > 0
                ? settings.EpisodeOffset(show.Id, season.Value)
                : 0;

            var wantedEpisode = parsed.Value.Episode + shift;
            var episode = show.FindEpisode(season.Value, wantedEpisode);

            // "Show - 137" reads as either s01e37 or absolute episode 137, and
            // only metadata can tell them apart.
            string? ambiguity = null;
            if (manual is null && parsed.Value.AbsoluteCandidate is { } abs)
            {
                var byAbsolute = ResolveAbsolute(show, abs);

                if (episode is null && byAbsolute is not null)
                {
                    // Split reading doesn't exist, absolute does - take absolute.
                    episode = byAbsolute;
                    season = byAbsolute.SeasonNumber;
                }
                else if (episode is not null && byAbsolute is not null
                         && byAbsolute.SeasonNumber != episode.SeasonNumber)
                {
                    // Both readings are valid. Don't silently pick one - say so,
                    // and let the preview do its job.
                    ambiguity = $"Ambiguous: could also be absolute episode {abs} "
                              + $"({byAbsolute.SeasonNumber}x{byAbsolute.Number:00} \"{byAbsolute.Name}\")";
                }
            }

            if (episode is null)
            {
                plan.Actions.Add(new PlannedAction
                {
                    SourcePath = file,
                    Parsed = parsed,
                    Status = PlanStatus.Problem,
                    Problem = show.HasSeason(season.Value)
                        ? $"Season {season} has no episode {wantedEpisode}"
                          + (shift != 0 ? $" (file says {parsed.Value.Episode}, offset {shift:+#;-#;0})" : "")
                        : $"No season {season} in the metadata"
                });
                continue;
            }

            // Multi-episode files need every episode to exist, or the name would
            // claim content the file doesn't have.
            var extra = new List<Episode>();
            if (parsed.Value.IsMultiEpisode)
            {
                var missing = false;
                foreach (var n in parsed.Value.Episodes.Skip(1))
                {
                    // The same shift the first episode used, so an E01E02 file
                    // doesn't end up straddling two different numberings.
                    var e = show.FindEpisode(season.Value, n + shift);
                    if (e is null) { missing = true; break; }
                    extra.Add(e);
                }

                if (missing)
                {
                    plan.Actions.Add(new PlannedAction
                    {
                        SourcePath = file,
                        Parsed = parsed,
                        Status = PlanStatus.Problem,
                        Problem = $"Looks like a multi-episode file ({parsed}), but not all of those episodes exist"
                    });
                    continue;
                }
            }

            // Honoured, rather than loaded, saved and ignored. Unticking
            // "rename episode files" left every filename being rewritten
            // anyway, which is the one thing it exists to prevent - somebody
            // who wants season folders but their own naming had no way to say
            // so. Off, the file keeps its name and only the folder moves.
            var newName = settings.RenameTvFiles
                ? BuildName(show, episode, extra, parsed.Value) + Path.GetExtension(file)
                : Path.GetFileName(file);

            var target = BuildTarget(item, file, season.Value, newName, plan, show);
            var action = MakeAction(file, target, claimed, parsed, episode, extra);

            // Advisory only - the row stays actionable, but flags the alternative.
            if (ambiguity is not null && action.Status == PlanStatus.Change)
                action.Problem = ambiguity;

            // A Kodi media source is scraped by one scraper at a time, so an
            // episode the merge pulled from elsewhere may show as unmatched even
            // though its filename is right - which looks like a PyreMedia bug
            // and isn't one. Only "may": sources can use different scrapers, and
            // add-ons exist that combine providers. Hence a note, not an error.
            if (action.Status == PlanStatus.Change
                && settings.WarnKodiMismatch
                && episode.Source is { } src
                && !string.Equals(src, settings.KodiScraperSource, StringComparison.OrdinalIgnoreCase))
            {
                var note = $"Only in {src}; if this library's Kodi source scrapes "
                         + $"{settings.KodiScraperSource}, it may not match this one";

                action.Problem = action.Problem is null ? note : action.Problem + ". " + note;
            }

            plan.Actions.Add(action);
        }

        // Independent of MoveTvFiles: naming the containing folder correctly and
        // moving episodes into Season folders are separate decisions, and in flat
        // mode you may well still want the folder named properly.
        //
        // NOT for loose files: their Path is the scan root itself, so renaming
        // would rename the user's whole library folder.
        // Nor for a combined entry: its Path is the folder the seasons sat in,
        // not the show's own, and renaming that would rename the library.
        if (settings.RenameShowFolder && !item.IsLooseFile && !item.IsCombined)
            plan.FolderRename = PlanFolderRename(item, show.Name, show.Year);

        AddCleanup(plan, item);
        return plan;
    }

    public RenamePlan PlanMovie(MediaItem item, Movie movie)
    {
        _dirCache.Clear();
        _downloading.Clear();
        _downloadingItem = JobStillRunning(item);

        var placeholder = new TvShow { Id = movie.TmdbId, Name = movie.Title };
        var plan = new RenamePlan { ShowFolder = item.Path, Show = placeholder };
        var claimed = new Dictionary<string, PlannedAction>(StringComparer.OrdinalIgnoreCase);

        var year = movie.Year ?? string.Empty;

        // Preserve the user's own 3D marker: for half-SBS the filename is the
        // only place that information exists.
        var tag3D = settings.Keep3DTag
            ? Stereo3DTag.Extract(Path.GetFileNameWithoutExtension(item.MainFile))
              ?? Stereo3DTag.Extract(item.DisplayName)
              // Nothing in the name, so fall back to what the file itself says.
              // Only full-frame layouts and MVC are detectable; half-width ones
              // look exactly like 2D, which is why the filename wins when both
              // are available.
              ?? item.Detected3D
            : null;

        // A disc image says so instead of claiming a layout it does not have.
        if (settings.IsDiscImage(item.MainFile)) tag3D = Stereo3DTag.ForDiscImage(tag3D);

        var baseName = NameFormatter.BuildShowFolderName(
            settings.MovieFileFormat, movie.Title, year, settings.FilenameReplaceChar);

        // As with episodes: off means the film keeps the name it has, and only
        // the folder around it changes.
        var newName = settings.RenameMovieFiles
            ? Stereo3DTag.Apply(baseName, tag3D) + Path.GetExtension(item.MainFile)
            : Path.GetFileName(item.MainFile);

        var targetDir = Path.GetDirectoryName(item.MainFile) ?? item.Path;

        // Order matters. The movie folder is decided first, then Extras nests
        // inside it - computing Extras first meant the per-movie folder
        // overwrote it and 3D copies stayed put beside the 2D version.
        //
        // Gated on the movie setting alone. It used to also require MoveTvFiles,
        // so a loose movie silently got no folder because a TV option was off.
        if (settings.MovieFolderPerMovie && item.IsLooseFile)
        {
            var folder = NameFormatter.BuildShowFolderName(
                settings.MovieFolderFormat, movie.Title, year, settings.FilenameReplaceChar);
            targetDir = Path.Combine(item.Path, folder);
            AddFolder(targetDir);
        }

        // Optionally corral 3D copies so they don't sit beside the 2D version.
        //
        // Never nest Extras inside Extras. Re-planning a file that is already
        // filed away appended the folder again, so every apply moved it one
        // level deeper - and the next preview then pointed at a path that no
        // longer existed.
        var alreadyFiled = string.Equals(
            Path.GetFileName(targetDir), settings.ExtrasFolderName,
            StringComparison.OrdinalIgnoreCase);

        if (tag3D is not null && settings.Move3DToExtras && !alreadyFiled)
        {
            targetDir = Path.Combine(targetDir, settings.ExtrasFolderName);
            AddFolder(targetDir);
        }

        void AddFolder(string dir)
        {
            if (!Directory.Exists(dir) && !plan.FoldersToCreate.Contains(dir))
                plan.FoldersToCreate.Add(dir);
        }

        plan.Actions.Add(MakeAction(item.MainFile, Path.Combine(targetDir, newName), claimed, null, null));

        if (settings.RenameShowFolder && !item.IsLooseFile)
            plan.FolderRename = PlanFolderRename(item, movie.Title, year);

        AddCleanup(plan, item);
        return plan;
    }

    /// <summary>
    /// Adds sample clips and leftover scene files as deletion rows. They're
    /// proposed, never automatic - the user ticks them like any other change.
    /// </summary>
    private void AddCleanup(RenamePlan plan, MediaItem item)
    {
        // A combined entry is a special case in both directions. Its own Path is
        // shared with whatever else sits beside it, so that must never be swept -
        // but the release folders it was built from are about to be emptied, and
        // clearing the scene leftovers out of them is the difference between a
        // folder that can be removed and one that lingers holding a .txt.
        //
        // Offered even when junk deletion is switched off globally: combining is
        // an explicit "make these one show", every deletion appears as its own
        // row in the preview, and any of them can be unticked before Apply.
        if (item.IsCombined)
        {
            SweepReleaseFolders(plan, item);
            return;
        }

        if (!settings.DeleteSamples && !settings.DeleteJunkFiles) return;

        // Never sweep a scan root: for a loose file the item's Path IS the root,
        // and a recursive sweep there would reach the entire library.
        if (item.IsLooseFile || IsScanRoot(item.Path)) return;

        var junkExts = settings.JunkExtensionList;
        var videoExts = settings.VideoExtensions;
        var subExts = settings.SubtitleExtensions;

        long mainSize = 0;
        try { mainSize = new FileInfo(item.MainFile).Length; } catch { }

        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(item.Path, "*", MediaScanner.DeepScan).ToList(); }
        catch { return; }

        foreach (var file in all)
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();

            // Never touch the files we're about to rename, or their subtitles.
            if (plan.Actions.Any(a =>
                    string.Equals(a.SourcePath, file, StringComparison.OrdinalIgnoreCase) ||
                    a.Subtitles.Contains(file, StringComparer.OrdinalIgnoreCase)))
                continue;

            if (subExts.Contains(ext)) continue;

            // A download marker is not a leftover - it is a file in use. It
            // outlives the extension list deliberately: adding ".!ut" to that
            // list one day would otherwise start deleting live transfers.
            if (Downloads.IsMarker(name)) continue;

            string? reason = null;

            if (settings.DeleteSamples && IsSample(name, file, ext, videoExts, mainSize, item.Path))
                reason = "sample";

            else if (settings.DeleteJunkFiles && junkExts.Contains(ext))
            {
                // A Kodi .nfo shares its extension with a scene release advert.
                // Offering to delete the user's own library metadata as "junk"
                // is not a mistake worth risking, so check the content.
                if (ext == ".nfo" && IsMetadataNfo(file)) continue;

                // Same trap, one file type along: the searchable text written
                // beside a comic or a book is a .txt, and .txt is on the junk
                // list. Reading a shelf takes minutes; offering to delete the
                // result afterwards would be worse than never writing it.
                if (ext == ".txt" && Books.ReadableText.IsSearchableText(file)) continue;

                // And the script written beside a comic, which is an .nfo but
                // not Kodi's - told apart by its root element, since a scene
                // release advert has the same three letters.
                if (ext == ".nfo" && Books.ScriptNfo.IsScript(file)) continue;

                reason = $"leftover {ext}";
            }

            if (reason is null) continue;

            plan.Actions.Add(new PlannedAction
            {
                SourcePath = file,
                Status = PlanStatus.Delete,
                DeleteReason = reason,
                StartsSelected = StartsTicked(file, item.Path)
            });
        }
    }

    /// <summary>How big a file is, or nothing if it cannot be asked.</summary>
    private static long SizeOrZero(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// Whether a file is a sample clip - the thirty-second teaser scene releases
    /// ship beside the feature.
    /// <para>
    /// Where the release says so, in the filename or in a folder above it, that
    /// is taken at its word. A "Sample" folder is put there to hold exactly one
    /// kind of thing. Second-guessing it with size and duration meant a 77 MB
    /// thirty-second clip in a folder named Sample sat through a rename while
    /// the checks below were still making up their mind.
    /// </para>
    /// <para>
    /// Nothing is deleted on the strength of this. It decides what appears in
    /// the preview as an unticked row, and any file the plan is already renaming
    /// is skipped before this is reached - so a film genuinely called "Free
    /// Sample" is never offered as its own leftover.
    /// </para>
    /// <para>
    /// Where nothing says so, it has to be obvious: small in absolute terms,
    /// small against the feature, and short. Only a video can be a sample at
    /// all - a stray sample.jpg is artwork or noise.
    /// </para>
    /// </summary>
    private bool IsSample(string name, string path, string ext, string[] videoExts,
                          long mainSize, string root)
    {
        if (!videoExts.Contains(ext)) return false;

        if (SaysSample(name) || InSampleFolder(path, root)) return true;

        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception) { return false; }

        var tiny = size < 300L * 1024 * 1024;
        var slither = mainSize > 0 && size < mainSize / 10;

        if (!(tiny && slither)) return false;

        // Size gets a file this far and no further. It decides who is worth
        // probing, not who goes.
        //
        // On its own it condemns the wrong files. Bitrate breaks it in both
        // directions - two minutes of 4K is a few hundred megabytes - and an
        // episode that was hard to find is genuinely a fraction of the ones
        // beside it, because a poor copy was the only copy going. Being small
        // is not evidence of being a clip; being short is.
        var seconds = _probe.Value.QuickDuration(path);

        // No answer is not a yes. This returned true and let size decide alone,
        // which is exactly the case above: one bad-quality episode, no ffprobe,
        // and an offer to delete it. Nothing here is worth guessing at.
        if (seconds is null) return false;

        // Five minutes. A sample clip runs thirty seconds to two; the line has to
        // sit above that and below real content, and real content goes shorter
        // than you would think - plenty of animated shows run eleven minutes, and
        // a genuine short can be three. Erring high would condemn those.
        return seconds < 5 * 60;
    }

    /// <summary>
    /// Whether a filename labels itself a throwaway clip.
    ///
    /// The word on its own is not enough, because it is also a title. "Free
    /// Sample" is a real film, and an episode can be called one. What separates
    /// them is whether the file names itself as a piece of content: something
    /// carrying an episode number, or a year the way a film does, is telling you
    /// what it is - and there the word belongs to the title.
    ///
    /// Those are not waved through, only sent the long way round: they still
    /// face the size and duration checks, which is what catches a genuine
    /// "Show.S01E01.1080p-sample.mkv" while leaving "Show 01x01 Free Sample"
    /// alone. A full-length episode is neither a tenth the size of its
    /// neighbours nor under five minutes.
    /// </summary>
    public static bool SaysSample(string name) =>
        name.Contains("sample", StringComparison.OrdinalIgnoreCase)
        && !NamesItself(name);

    /// <summary>Whether the name identifies a piece of content in its own right.</summary>
    private static bool NamesItself(string name) =>
        Naming.EpisodeMatcher.Parse(name) is not null
        || System.Text.RegularExpressions.Regex.IsMatch(name, @"(?:19|20)\d{2}");

    /// <summary>
    /// Whether any folder between the file and the item it belongs to calls
    /// itself a sample. Bounded by the item so it can never walk out into the
    /// library and find somebody's "Samples" music folder.
    /// </summary>
    public static bool InSampleFolder(string path, string root) =>
        InFolderNamed(path, root, "sample", "samples");

    /// <summary>
    /// Folders whose name says somebody chose to keep what is inside.
    ///
    /// Nothing here is spared deletion - a scene advert in an Extras folder is
    /// still a scene advert. What the name buys is the benefit of the doubt for
    /// anything sizeable: a deleted scene and a sample clip are both short
    /// videos nobody named carefully, and only the folder tells them apart.
    /// </summary>
    private static readonly string[] BonusFolders =
    [
        "extras", "extra", "featurettes", "featurette", "bonus", "bonus features",
        "deleted", "deleted scenes", "behind the scenes", "interviews", "specials",
        "making of", "documentary"
    ];

    /// <summary>Whether any folder between the file and the item is one of these.</summary>
    private static bool InFolderNamed(string path, string root, params string[] names)
    {
        var dir = Path.GetDirectoryName(path);

        // Strictly between the file and the item. The item's own folder is not
        // consulted, or a show called "Sample" would have had every one of its
        // episodes offered for deletion.
        while (!string.IsNullOrEmpty(dir)
               && dir.Length > root.Length
               && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(dir);

            foreach (var name in names)
                if (folder.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    /// <summary>
    /// Whether a deletion should arrive ticked.
    ///
    /// Ticked is the default now, because the point of the list is not to
    /// forget: leftovers were arriving unticked, Apply walked past them without
    /// a word, and the folder came out of a rename still holding them. Every
    /// row is still visible, still says what it will do, still goes to the
    /// Recycle Bin, and can still be cleared before Apply.
    ///
    /// The exception is anything sizeable sitting in a folder that says a
    /// person put it there - Extras, Deleted Scenes, Featurettes. A deleted
    /// scene and a sample clip look alike from the outside, and one of them is
    /// content somebody chose to keep. Those are offered, and left for you.
    /// </summary>
    private bool StartsTicked(string path, string root)
    {
        if (!InFolderNamed(path, root, BonusFolders)
            && !InFolderNamed(path, root, settings.ExtrasFolderName))
            return true;

        // In a bonus folder, only the obviously-disposable is ticked. Ten
        // megabytes is far above any .nfo or .txt and far below anything worth
        // a second look.
        return SizeOrZero(path) <= 10L * 1024 * 1024;
    }

    /// <summary>
    /// Folders a download client is writing into, worked out once each.
    ///
    /// Cleared at the start of every plan rather than cached for the planner's
    /// lifetime: a transfer finishing is exactly the thing this has to notice,
    /// and the planner outlives many plans.
    /// </summary>
    private readonly Dictionary<string, string?> _downloading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once per plan: the client still writing anywhere under this item.</summary>
    private string? _downloadingItem;

    /// <summary>
    /// Whether anything under this item is still arriving.
    ///
    /// Never walks a scan root. A loose file's Path is the root it was found
    /// in, and so is a combined entry's - the folder its parts share. Walking
    /// either reads the whole library to answer a question about one show, and
    /// finds every unrelated download in it: one torrent still running made
    /// every item in the library report itself as busy, and Ghost in the Shell
    /// was refused because Stranger Things had not finished.
    ///
    /// A combined entry is asked about the folders it was actually built from.
    /// A loose file has none, and is covered by the per-file and per-folder
    /// checks instead.
    /// </summary>
    private string? JobStillRunning(MediaItem item)
    {
        if (item.IsLooseFile || IsScanRoot(item.Path))
        {
            foreach (var folder in item.CombinedFrom)
                if (Downloads.ActiveUnder(folder) is { } busy)
                    return busy;

            return null;
        }

        return Downloads.ActiveUnder(item.Path);
    }

    private string? DownloadingInto(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(dir)) return null;

        if (!_downloading.TryGetValue(dir, out var client))
            _downloading[dir] = client = Downloads.ActiveIn(dir);

        return client;
    }

    private readonly Lazy<Media.MediaProbe> _probe =
        new(() => new Media.MediaProbe(settings.FfprobePath));

    /// <summary>
    /// Find an episode by its absolute position across the run, skipping season 0
    /// (specials aren't part of absolute numbering).
    /// </summary>
    private static Episode? ResolveAbsolute(TvShow show, int absolute)
    {
        if (absolute <= 0) return null;

        var ordered = show.Seasons
            .Where(s => s.Number > 0)
            .OrderBy(s => s.Number)
            .SelectMany(s => s.Episodes.OrderBy(e => e.Number))
            .ToList();

        return absolute <= ordered.Count ? ordered[absolute - 1] : null;
    }

    /// <summary>
    /// Filename for one episode, or a joined name for a multi-episode file:
    /// "Show 01x01-02 First & Second".
    /// </summary>
    private string BuildName(TvShow show, Episode first, List<Episode> extra, EpisodeRef parsed)
    {
        if (extra.Count == 0)
        {
            return NameFormatter.BuildEpisodeFileName(
                settings.TvFileFormat, show.Name, first,
                settings.SeasonNumZeroPadding, settings.EpisodeNumZeroPadding,
                settings.FilenameReplaceChar);
        }

        var pad = settings.EpisodeNumZeroPadding;
        var episodeText = first.Number.ToString().PadLeft(pad, '0')
                          + "-" + extra[^1].Number.ToString().PadLeft(pad, '0');

        var titles = string.Join(" & ",
            new[] { first }.Concat(extra)
                           .Select(e => e.Name)
                           .Where(t => !string.IsNullOrWhiteSpace(t)));

        return NameFormatter.BuildEpisodeFileName(
            settings.TvFileFormat,
            show.Name,
            first.SeasonNumber.ToString().PadLeft(settings.SeasonNumZeroPadding, '0'),
            episodeText,
            titles,
            settings.FilenameReplaceChar);
    }

    private string BuildTarget(
        MediaItem item, string file, int season, string newName, RenamePlan plan, TvShow show)
    {
        // Flat: leave the file where it is.
        //
        // Two exceptions, and both are cases where "where it is" is not a place.
        //
        // A combined entry. Treating eight folders as one show is a statement
        // that they belong together, and the only way to carry that out is to
        // gather them into one show folder with a subfolder per season. Renaming
        // them where they sit would leave the split exactly as it was and make
        // combining pointless.
        //
        // And a loose file, which is the case this setting was never about. Flat
        // mode means "do not restructure a library that is already organised" -
        // it assumes the episode is sitting in its show's folder and should stay
        // there. An episode loose in the scan root is in no show's folder, so
        // leaving it there is not preserving an arrangement, it is declining to
        // make one. X-Men '97 came out of a scan correctly renamed and still
        // lying in the root beside six show folders.
        if (!settings.MoveTvFiles && !item.IsCombined && !item.IsLooseFile)
            return Path.Combine(Path.GetDirectoryName(file) ?? item.Path, newName);

        // A loose file has no folder of its own - its Path is the scan root. Give
        // it a new show folder rather than treating the root as the show. Same for
        // a combined entry: its Path is the folder its seasons sat side by side
        // in, so the seasons move into a show folder there instead of the season
        // folders landing loose beside everything else.
        var baseDir = item.Path;
        if (item.IsLooseFile || item.IsCombined || IsScanRoot(item.Path))
        {
            var showFolder = NameFormatter.BuildShowFolderName(
                settings.ShowFolderFormat, show.Name, show.Year, settings.FilenameReplaceChar);

            baseDir = Path.Combine(item.Path, showFolder);
            if (!Directory.Exists(baseDir) && !plan.FoldersToCreate.Contains(baseDir))
                plan.FoldersToCreate.Add(baseDir);
        }

        var seasonFolder = NameFormatter.SeasonFolderName(
            season, settings.SeasonFolderName, settings.SpecialsFolderName, settings.SeasonNumZeroPadding);

        var dir = Path.Combine(baseDir, seasonFolder);
        if (!Directory.Exists(dir) && !plan.FoldersToCreate.Contains(dir))
            plan.FoldersToCreate.Add(dir);

        return Path.Combine(dir, newName);
    }

    private PlannedAction MakeAction(
        string source, string target,
        Dictionary<string, PlannedAction> claimed,
        EpisodeRef? parsed, Episode? episode, IEnumerable<Episode>? extraEpisodes = null)
    {
        var action = new PlannedAction
        {
            SourcePath = source,
            TargetPath = target,
            Parsed = parsed,
            Episode = episode,
            ExtraEpisodes = extraEpisodes is null ? [] : [.. extraEpisodes],
            Status = PlanStatus.Change
        };

        foreach (var sub in FindSubtitles(source))
            action.Subtitles.Add(sub);

        // Still arriving. A download in progress is a file the client is holding
        // open and expects to find where it left it, so renaming it breaks the
        // transfer and usually the client's record of it too - and the result
        // looks like a corrupt file rather than like this program's doing.
        //
        // Found on a real library: forty-two .!ut placeholders beside a part-
        // downloaded Stranger Things. Reported rather than skipped silently,
        // because "it left half my library alone" needs a reason attached.
        if (Downloads.InProgress(source) is { } client)
        {
            action.Status = PlanStatus.Problem;
            action.Problem = $"Still downloading ({client}) - left alone until it finishes";
            return action;
        }

        // Or anywhere under the same download.
        if (_downloadingItem is { } job)
        {
            action.Status = PlanStatus.Problem;
            action.Problem = $"{job} is still downloading this - "
                             + "left alone until the whole thing finishes";
            return action;
        }

        // Or sitting beside something that is.
        //
        // A finished episode carries no marker of its own, so the check above
        // clears it - but moving it out of a folder the client is still writing
        // into breaks the transfer just the same, and renaming it loses the
        // client's record of it. The remux side has refused these since three
        // complete Stranger Things episodes were offered while five beside them
        // were still arriving; this side only ever looked at the one file.
        if (DownloadingInto(source) is { } into)
        {
            action.Status = PlanStatus.Problem;
            action.Problem = $"{into} is still downloading into this folder - "
                             + "left alone until it finishes";
            return action;
        }

        // Ordinal, so a name that differs only in capitalisation still counts as
        // a change: "the office" becomes "The Office" when the provider says so.
        // The executor stages case-only renames through a temporary name, since
        // Windows treats the two as the same file.
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            action.Status = PlanStatus.AlreadyCorrect;
            return action;
        }

        // A case-only difference is a rename and nothing more - it can't collide
        // with anything, because the only file it matches is itself.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return action;

        if (claimed.TryGetValue(target, out var other))
        {
            action.Status = PlanStatus.Problem;
            action.Problem = $"Target collides with '{other.SourceName}'";
            other.Status = PlanStatus.Problem;
            other.Problem ??= $"Target collides with '{action.SourceName}'";
            return action;
        }

        if (File.Exists(target))
        {
            if (!settings.OverwriteFiles)
            {
                action.Status = PlanStatus.Problem;
                action.Problem = "Target exists and overwrite is disabled";
                return action;
            }
            action.WillOverwrite = true;
        }

        claimed[target] = action;
        return action;
    }

    private FolderRename? PlanFolderRename(MediaItem item, string title, string year)
    {
        // Hard stop: a configured scan root is the user's library folder and must
        // never be renamed, whatever else the plan thinks. Belt-and-braces with
        // the IsLooseFile check at the call sites.
        if (IsScanRoot(item.Path)) return null;

        // Equally, never rename a drive root.
        var trimmed = item.Path.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(Path.GetDirectoryName(trimmed))) return null;

        var current = Path.GetFileName(trimmed);
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(parent)) return null;

        var wanted = NameFormatter.BuildShowFolderName(
            item.Kind == MediaKind.Movie ? settings.MovieFolderFormat : settings.ShowFolderFormat,
            title, year, settings.FilenameReplaceChar);

        if (string.IsNullOrWhiteSpace(wanted) || string.Equals(current, wanted, StringComparison.Ordinal))
            return null;

        var target = Path.Combine(parent, wanted);
        var rename = new FolderRename { SourcePath = item.Path, TargetPath = target };

        if (!string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
            rename.Problem = $"A folder named '{wanted}' already exists here";

        // A folder is claimed as a film or a series on the strength of the video
        // in it, and nothing asks what else lives there. A real shelf had
        // "Michael Crichton Collection" - seventy-five books and one stray video
        // file - offered up to be renamed after whatever film that one file
        // turned out to be. The books would have survived; the collection would
        // not have been called anything meaningful again.
        //
        // Said rather than silently skipped, because the user may genuinely want
        // it and is the one who can tell.
        else if (OtherMediaIn(item.Path, item.Files.Count) is { } crowd)
            rename.Problem = crowd;

        return rename;
    }

    /// <summary>
    /// Why this folder does not look like it belongs to the video in it, or null
    /// when it does.
    ///
    /// Counted with a ceiling rather than exhaustively: the question is whether
    /// books and comics outnumber the video, and walking a collection of eleven
    /// hundred files to learn that twice over is waste.
    /// </summary>
    private static string? OtherMediaIn(string folder, int videoCount)
    {
        const int Enough = 200;

        var books = 0;
        var comics = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (Books.BookFile.IsBook(file)) books++;
                else if (Books.ComicMatcher.IsComic(file)) comics++;

                if (books + comics >= Enough) break;
            }
        }
        catch (Exception)
        {
            return null;   // unreadable is not evidence of anything
        }

        var others = books + comics;

        // Comfortably outnumbering the video, and enough of them to be somebody's
        // collection rather than a couple of stray files beside a film.
        if (others < 5 || others < videoCount * 3) return null;

        var what = books > 0 && comics > 0 ? $"{books} book(s) and {comics} comic(s)"
                 : books > 0 ? $"{books} book(s)"
                 : $"{comics} comic(s)";

        return $"This folder holds {what} and only "
             + (videoCount == 1 ? "one video file" : $"{videoCount} video files")
             + " - renaming it after the video would rename somebody's collection. "
             + "Rename it by hand if that is really what you want.";
    }

    /// <summary>True when the path is one of the user's configured scan roots.</summary>
    private bool IsScanRoot(string path)
    {
        var normalized = NormalizeRoot(path);

        return settings.TvFolders.Concat(settings.MovieFolders)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Any(r => string.Equals(NormalizeRoot(r), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRoot(string path) => MediaScanner.NormalizeRoot(path);

    /// <summary>
    /// Files that must travel with the video: subtitles, and Kodi's own .nfo.
    ///
    /// Kodi matches a sidecar .nfo to its video by filename, so leaving one
    /// behind under the old name orphans the metadata - Kodi then re-scrapes and
    /// any hand-edits are lost. Renaming it alongside keeps the pairing.
    /// </summary>
    /// <summary>
    /// One directory listing per folder, reused across every file planned from
    /// it and cleared at the start of each plan.
    ///
    /// Companions used to be probed for by name: roughly seventy-five
    /// File.Exists calls per video, since the artwork check alone is thirteen
    /// suffixes across five extensions, plus a fresh directory enumeration on
    /// top. A twelve-episode folder therefore cost about nine hundred stat calls
    /// and twelve identical enumerations. Barely noticeable on a local disk; on
    /// a NAS, where each one is a round trip, it is seconds per folder.
    /// </summary>
    private readonly Dictionary<string, string[]> _dirCache = new(StringComparer.OrdinalIgnoreCase);

    private string[] FilesIn(string dir)
    {
        if (_dirCache.TryGetValue(dir, out var cached)) return cached;

        try { cached = Directory.GetFiles(dir); }
        catch { cached = []; }

        _dirCache[dir] = cached;
        return cached;
    }

    /// <summary>
    /// Offer the scene leftovers in a combined entry's source folders for
    /// deletion - the .txt advert, the .exe nobody wants to keep, the .sfv.
    ///
    /// Scoped to exactly the folders being emptied and nothing else. Files the
    /// rename is about to move, their companions, and anything that is or could
    /// be real content are all passed over: only the extensions on the junk list
    /// are offered, and an .nfo carrying actual Kodi metadata is never among them
    /// however its extension reads.
    /// </summary>
    private void SweepReleaseFolders(RenamePlan plan, MediaItem item)
    {
        var junkExts = settings.JunkExtensionList;

        // Disc images count as content here whatever the junk list says - .bin
        // is a plausible thing for somebody to add to it, and the file beside
        // the .cue is a film.
        var videoExts = settings.ScannedExtensions;
        var subExts = settings.SubtitleExtensions;

        foreach (var folder in item.CombinedFrom)
        {
            IEnumerable<string> all;
            try { all = Directory.EnumerateFiles(folder, "*", MediaScanner.DeepScan).ToList(); }
            catch { continue; }

            foreach (var file in all)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();

                // Never anything the rename is dealing with, and never a video or
                // subtitle whatever else is true of it.
                if (videoExts.Contains(ext) || subExts.Contains(ext)) continue;

                if (plan.Actions.Any(a =>
                        string.Equals(a.SourcePath, file, StringComparison.OrdinalIgnoreCase) ||
                        a.Subtitles.Contains(file, StringComparer.OrdinalIgnoreCase)))
                    continue;

                if (!junkExts.Contains(ext)) continue;

                // A Kodi .nfo shares its extension with a scene release advert.
                // Offering to delete the user's own library metadata as junk is
                // not a mistake worth risking, so check the content.
                if (ext == ".nfo" && IsMetadataNfo(file)) continue;

                plan.Actions.Add(new PlannedAction
                {
                    SourcePath = file,
                    Status = PlanStatus.Delete,
                    DeleteReason = $"leftover {ext} in a folder being emptied",
                    StartsSelected = true
                });
            }
        }
    }

    private IEnumerable<string> FindSubtitles(string videoFile)
    {
        var dir = Path.GetDirectoryName(videoFile);
        if (dir is null) yield break;

        var stem = Path.GetFileNameWithoutExtension(videoFile);
        var subExts = settings.SubtitleExtensions;

        foreach (var f in FilesIn(dir))
        {
            var name = Path.GetFileName(f);

            // Every companion starts with the video's stem - that is what makes
            // it a companion. Anything else in the folder belongs to another
            // file and must be left alone.
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Length == stem.Length) continue;               // the video itself

            var rest = name[stem.Length..];
            var ext = Path.GetExtension(name).ToLowerInvariant();

            // "Episode.srt", "Episode.eng.srt", "Episode.eng.forced.srt"
            if (rest.StartsWith('.') && subExts.Contains(ext))
            {
                yield return f;
                continue;
            }

            // Kodi's sidecar metadata, as distinct from a scene release advert.
            if (rest.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
            {
                if (IsMetadataNfo(f)) yield return f;
                continue;
            }

            // "Film (2010)-poster.jpg" and the rest of Kodi's per-file artwork.
            if (rest.StartsWith('-') && ArtworkExtensions.Contains(ext))
            {
                var suffix = Path.GetFileNameWithoutExtension(rest)[1..];
                if (ArtworkSuffixes.Contains(suffix, StringComparer.OrdinalIgnoreCase))
                    yield return f;

                continue;
            }

            // The bare "<stem>.tbn" thumbnail, an older Kodi convention.
            if (rest.Equals(".tbn", StringComparison.OrdinalIgnoreCase))
                yield return f;
        }
    }

    /// <summary>Kodi's per-file artwork suffixes.</summary>
    private static readonly string[] ArtworkSuffixes =
    [
        "poster", "fanart", "thumb", "banner", "landscape", "clearlogo", "logo",
        "clearart", "disc", "discart", "keyart", "characterart", "spine"
    ];

    private static readonly string[] ArtworkExtensions = [".jpg", ".jpeg", ".png", ".tbn", ".webp"];

    /// <summary>
    /// Tell a metadata .nfo from a scene release .nfo. They share an extension
    /// and nothing else: Kodi writes XML, scene groups write ASCII-art adverts.
    /// Deleting the first as junk would throw away the user's library metadata.
    /// </summary>
    public static bool IsMetadataNfo(string path)
    {
        try
        {
            using var reader = new StreamReader(path);

            // The root element is within the first few lines of any Kodi nfo.
            for (var i = 0; i < 12; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;

                if (line.Contains("<movie", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("<tvshow", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("<episodedetails", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("<musicvideo", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("themoviedb.org", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("thetvdb.com", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Unreadable: assume it matters rather than assume it's junk.
            return true;
        }

        return false;
    }
}

/// <summary>
/// Recognising a file a download client is still writing.
///
/// Lives on its own rather than on the scanner or the planner because both need
/// it for opposite reasons: the planner must not rename such a file, and the
/// sweep must not delete the marker beside it.
/// </summary>
public static class Downloads
{
    /// <summary>
    /// The download client still writing this file, or null when nothing is.
    ///
    /// Each of these sits beside the real file under the same name with a marker
    /// appended, which is what makes it recognisable without asking any client
    /// anything: "Episode.mkv" plus "Episode.mkv.!ut" means uTorrent has that
    /// file open.
    ///
    /// These are never offered as junk to delete, whatever the extension list
    /// says. Removing one mid-transfer loses the client's place in the file.
    /// </summary>
    public static readonly (string Suffix, string Client)[] Markers =
    [
        (".!ut", "uTorrent"),
        (".part", "a download client"),
        (".!qb", "qBittorrent"),
        (".crdownload", "Chrome"),
    ];

    /// <summary>Whether a filename is itself one of those markers.</summary>
    public static bool IsMarker(string name) =>
        Markers.Any(m => name.EndsWith(m.Suffix, StringComparison.OrdinalIgnoreCase));

    public static string? InProgress(string video)
    {
        var markers = Markers;

        foreach (var (suffix, client) in markers)
        {
            try { if (File.Exists(video + suffix)) return client; }
            catch (Exception) { /* unreadable is not evidence either way */ }
        }

        return null;
    }

    /// <summary>
    /// The client downloading something into this folder, or null when nothing is.
    ///
    /// A finished file carries no marker of its own, so <see cref="InProgress"/>
    /// has nothing to say about it - but a finished file sitting among half a
    /// download is still part of a live transfer, and the client expects to find
    /// it where it left it.
    ///
    /// Folder-level on purpose. What makes the neighbours unsafe is the
    /// transfer, and the transfer belongs to the folder, not to the one file.
    /// Found on a real library: three complete Stranger Things episodes beside
    /// five .!ut placeholders, none of the three carrying a marker.
    /// </summary>
    /// <summary>
    /// The client downloading anywhere beneath this folder, or null.
    ///
    /// A torrent is one job even though its seasons finish at different times.
    /// Checking only the folder a file sits in cleared a completed season one
    /// while four and five were still arriving - and moving season one out from
    /// under the client breaks the transfer for the whole torrent, not only for
    /// what moved.
    /// </summary>
    public static string? ActiveUnder(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);

                foreach (var (suffix, client) in Markers)
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return client;
            }
        }
        catch (Exception) { /* unreadable is not evidence either way */ }

        return null;
    }

    public static string? ActiveIn(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);

                foreach (var (suffix, client) in Markers)
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return client;
            }
        }
        catch (Exception) { /* unreadable is not evidence either way */ }

        return null;
    }
}
