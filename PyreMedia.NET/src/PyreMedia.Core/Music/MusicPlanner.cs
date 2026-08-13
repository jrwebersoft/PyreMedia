namespace PyreMedia.Core.Music;

public enum MusicActionKind
{
    /// <summary>Move the file to a new path under the library root.</summary>
    Move,

    /// <summary>Already where it should be.</summary>
    Stay,

    /// <summary>A redundant copy of a track kept elsewhere in this plan.</summary>
    Redundant,

    /// <summary>Left alone: something about it needs a person first.</summary>
    Held,

    /// <summary>
    /// Duplicate the file into a second album, leaving the original where it is.
    ///
    /// A song sits on its own album and on a soundtrack, and both want it. This
    /// cannot be a link: the two copies need different album names and different
    /// track numbers embedded in them, and one file carries one set of tags.
    /// </summary>
    Copy
}

public sealed record MusicAction(
    TrackTags Track,
    MusicActionKind Kind,
    string? Destination,
    string? Note)
{
    public string Source => Track.Path;

    /// <summary>
    /// Tags the copy must carry once it is made, on a <see cref="MusicActionKind.Copy"/>.
    ///
    /// Not optional in practice, and the reason is the whole point of the
    /// action. Kodi builds its library from the tags inside a file, so a track
    /// copied into a second album while still carrying the first album's name
    /// and track number does not appear in the second album at all - it appears
    /// as another copy of the first, and the gap it was meant to fill is still
    /// there. A copy that cannot be retagged is worse than no copy, so the
    /// executor refuses one rather than making it.
    /// </summary>
    public TagEdit? Retag { get; init; }
}

/// <summary>A question only a person can answer, with everything needed to answer it.</summary>
public sealed record MusicDecision(
    AlbumGroup Album,
    int Track,
    DuplicateSet Candidates,
    string Question);

public sealed class MusicPlan
{
    public List<MusicAction> Actions { get; } = [];
    public List<MusicDecision> Decisions { get; } = [];

    public IEnumerable<MusicAction> Moves => Actions.Where(a => a.Kind is MusicActionKind.Move);
    public IEnumerable<MusicAction> Redundant => Actions.Where(a => a.Kind is MusicActionKind.Redundant);
    public IEnumerable<MusicAction> Held => Actions.Where(a => a.Kind is MusicActionKind.Held);

    public long RedundantBytes { get; set; }
}

/// <summary>
/// Works out where every track should go, and refuses to produce a plan in which
/// two files want the same path.
///
/// That refusal is the whole point. A move that silently overwrites is the one
/// mistake a library tool cannot walk back, so anything this class cannot settle
/// by rule is held where it is and raised as a question instead. Measured on a
/// 17,168-file library, that leaves two questions.
/// </summary>
public static class MusicPlanner
{
    public static MusicPlan Plan(
        IEnumerable<AlbumGroup> albums,
        string libraryRoot,
        string folderFormat = MusicNaming.DefaultFolderFormat,
        string fileFormat = MusicNaming.DefaultFileFormat,
        FingerprintSet? audio = null,
        NamingFormat? naming = null,
        IReadOnlyList<Gap>? gaps = null)
    {
        var plan = new MusicPlan();
        var albumList = albums as IReadOnlyList<AlbumGroup> ?? [.. albums];
        albums = albumList;

        foreach (var album in albums)
        {
            FillGapsFromFilenames(album);
            RenumberFromFilenames(album);

            var held = new HashSet<TrackTags>();
            var redundant = new HashSet<TrackTags>();

            foreach (var clash in Clashes(album))
                foreach (var set in Duplicates.ClassifyAll([.. clash], audio))
                {
                    if (set.Kind is DuplicateKind.AlternateVersion)
                    {
                        foreach (var t in set.Files) held.Add(t);
                        plan.Decisions.Add(new(album, clash.Key.Track, set,
                            $"{set.Files.Count} files claim track {clash.Key.Track} "
                            + $"of {album.Title}: {set.Reason}"));
                        continue;
                    }

                    // WrongNumber says the numbering is suspect, not that anything
                    // is duplicated. The files differ in title so they do not
                    // collide on disk, and they are filed as they stand.
                    if (set.Kind is DuplicateKind.WrongNumber) continue;

                    foreach (var t in set.Files.Where(t => t != set.Keep)) redundant.Add(t);
                }

            // What the album looks like once the duplicates have gone. Asked
            // after held and redundant are decided, because naming an album
            // from files that will not be there is what stopped it settling.
            var survivors = album.Tracks
                .Where(t => !held.Contains(t) && !redundant.Contains(t))
                .ToList();

            var survivingYear = AlbumGrouper.YearOf(survivors.Count > 0 ? survivors : album.Tracks);

            foreach (var track in album.Tracks)
            {
                if (held.Contains(track))
                {
                    plan.Actions.Add(new(track, MusicActionKind.Held, null, "waiting on a decision"));
                    continue;
                }

                if (redundant.Contains(track))
                {
                    plan.Actions.Add(new(track, MusicActionKind.Redundant, null,
                        "a better copy of this track is being kept"));
                    plan.RedundantBytes += Length(track.Path);
                    continue;
                }

                // One pattern, when the caller has given one; the older pair of
                // format strings otherwise, so nothing that called this before
                // has to change.
                var destination = naming is null
                    ? MusicNaming.PathFor(track, album, libraryRoot, folderFormat, fileFormat)
                    : Path.Combine(libraryRoot, naming.Path(AsFiledUnder(track, album, survivingYear)));

                plan.Actions.Add(string.Equals(destination, track.Path, StringComparison.OrdinalIgnoreCase)
                    ? new(track, MusicActionKind.Stay, destination, null)
                    : new(track, MusicActionKind.Move, destination, null));
            }
        }

        if (gaps is not null) FillGaps(plan, gaps, libraryRoot, folderFormat, fileFormat, naming);

        HoldAnyRemainingCollisions(plan);
        return plan;
    }

    /// <summary>
    /// Turn the gaps somebody has approved into copies.
    ///
    /// Only the ones a rule settled. A gap whose candidates split into two
    /// different recordings is a question, and answering it by picking one is
    /// exactly the mistake that files a remix under the album version's number.
    /// </summary>
    private static void FillGaps(
        MusicPlan plan, IReadOnlyList<Gap> gaps, string libraryRoot,
        string folderFormat, string fileFormat, NamingFormat? naming)
    {
        foreach (var gap in gaps)
        {
            if (gap.Obvious is not { } candidate) continue;

            var source = candidate.Best;

            // Where it goes is worked out as though it already belonged to the
            // album with the hole in it - that album's name, that album's
            // artist, and the position the listing says is missing. Naming it
            // from the source's own tags would file it back beside itself.
            var asIfHere = new TrackTags
            {
                Path = source.Path,
                Title = source.Title.HasText
                    ? source.Title
                    : new TagValue(gap.Wanted.Title, TagSource.Nfo, TextConfidence.Declared),
                Artist = source.Artist,
                Album = new TagValue(gap.Album.Title, TagSource.Nfo, TextConfidence.Declared),
                AlbumArtist = new TagValue(gap.Album.DisplayArtist, TagSource.Nfo, TextConfidence.Declared),
                Genre = source.Genre,
                Year = gap.Album.Year is { } y
                    ? new TagValue(y, TagSource.Nfo, TextConfidence.Ascii)
                    : source.Year,
                TrackNumber = gap.Wanted.Position,
                DiscNumber = null
            };

            var destination = naming is null
                ? MusicNaming.PathFor(asIfHere, gap.Album, libraryRoot, folderFormat, fileFormat)
                : Path.Combine(libraryRoot, naming.Path(asIfHere));

            // The tags the copy has to end up with. Same values the path was
            // built from, so the file and its folder agree.
            var retag = new TagEdit(
                Title: asIfHere.Title.HasText ? asIfHere.Title.Text : null,
                Album: gap.Album.Title,
                AlbumArtist: gap.Album.DisplayArtist,
                Year: gap.Album.Year,
                TrackNumber: gap.Wanted.Position);

            plan.Actions.Add(new MusicAction(source, MusicActionKind.Copy, destination,
                $"fills track {gap.Wanted.Position} of {gap.Album.Title} - {candidate.Why}")
            {
                Retag = retag
            });
        }
    }

    /// <summary>
    /// A track as its album says it should be filed, rather than as its own
    /// tags say.
    ///
    /// Grouping decides an album has one artist, one title and one year, and
    /// then naming asked each file separately - so an album whose tags
    /// disagreed was split across folders by the very disagreement the
    /// grouping had already resolved. Measured on a real library: 569 files
    /// wanted to move a second time immediately after being filed, because
    /// scanning them again re-derived the same per-file differences. One
    /// Beatles record was heading for "Beatles\Past Masters (1994)" while its
    /// siblings sat in "The Beatles\Past Masters (1988)", and a Creed album
    /// lost its year on the tracks that had never carried one.
    ///
    /// The track keeps its own title and number - those genuinely differ per
    /// file. Only what makes a folder comes from the album.
    /// </summary>
    private static TrackTags AsFiledUnder(TrackTags track, AlbumGroup album, string? year)
    {
        var artist = MusicHealth.Salvage(album.DisplayArtist);
        var title = MusicHealth.Salvage(album.Title);

        return new TrackTags
        {
            Path = track.Path,

            Title = track.Title,
            Artist = track.Artist,
            Genre = track.Genre,
            TrackNumber = track.TrackNumber,
            DiscNumber = track.DiscNumber,
            Seconds = track.Seconds,
            Kbps = track.Kbps,

            AlbumArtist = artist.Length > 0
                ? new TagValue(artist, TagSource.None, TextConfidence.Declared)
                : track.AlbumArtist,

            Album = title.Length > 0
                ? new TagValue(title, TagSource.None, TextConfidence.Declared)
                : track.Album,

            // The year of the tracks that are actually staying, with no
            // fallback to the file's own. Falling back is what made this
            // unstable: an album whose year lived only on the copies about to
            // be quarantined got a folder those files would not be in, and the
            // next scan moved the whole record.
            Year = year is { Length: > 0 }
                ? new TagValue(year, TagSource.None, TextConfidence.Ascii)
                : TagValue.Empty
        };
    }

    /// <summary>
    /// The last guard: whatever the rules concluded, no two files may be sent to
    /// one path. Anything still contending is held rather than disambiguated -
    /// inventing "name (2)" would file a duplicate as though it were a track, and
    /// the library would carry the mistake for ever.
    /// </summary>
    private static void HoldAnyRemainingCollisions(MusicPlan plan)
    {
        var contested = plan.Actions
            .Where(a => a.Destination is not null && a.Kind is MusicActionKind.Move or MusicActionKind.Stay)
            .GroupBy(a => a.Destination!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in contested)
            foreach (var action in group)
            {
                var index = plan.Actions.IndexOf(action);
                plan.Actions[index] = action with
                {
                    Kind = MusicActionKind.Held,
                    Note = $"{group.Count()} files want {group.Key}"
                };
            }
    }

    /// <summary>
    /// Where two tracks claim one number and their filenames disagree with their
    /// tags, believe the filenames.
    ///
    /// A folder holding "01 It Was Onyx (Skit).wma" and "02 Raze It Up.wma" where
    /// both tags say track 1 has already told us the order - somebody named those
    /// files from the sleeve. Applied only when the filenames resolve the clash
    /// completely, so a folder that is genuinely ambiguous stays ambiguous.
    /// </summary>
    private static void RenumberFromFilenames(AlbumGroup album)
    {
        foreach (var clash in Clashes(album))
        {
            var files = clash.ToList();
            if (files.Select(Duplicates.TitleKey).Distinct().Count() == 1) continue;

            var fromNames = files.Select(t => (Track: t, Number: NumberFromFilename(t.Path))).ToList();
            if (fromNames.Any(x => x.Number is null)) continue;
            if (fromNames.Select(x => x.Number).Distinct().Count() != files.Count) continue;

            // Only if the new numbers do not walk into somebody else's place.
            var occupied = album.Tracks.Except(files)
                .Where(t => (t.DiscNumber ?? 1) == clash.Key.Disc)
                .Select(t => t.TrackNumber)
                .ToHashSet();

            if (fromNames.Any(x => occupied.Contains(x.Number))) continue;

            foreach (var (track, number) in fromNames) track.TrackNumber = number;
        }
    }

    /// <summary>
    /// Where a tag is missing, take what the filename says.
    ///
    /// Without this a folder of files with no title tag names every one of them
    /// the same thing - the format renders "00 Queen - " for all fifteen, which
    /// is not empty, so no fallback fires and fifteen files contend for one path.
    /// The filename is the only surviving record of what those tracks are.
    /// </summary>
    private static void FillGapsFromFilenames(AlbumGroup album)
    {
        foreach (var track in album.Tracks)
        {
            var (number, title) = MusicEvidence.SplitFilename(track.Path);

            if (track.TrackNumber is null or 0 && number is > 0)
                track.TrackNumber = number;

            if (!track.Title.HasText && title.Length > 0)
                track.Title = new TagValue(title, TagSource.None, TextConfidence.Ascii);
        }
    }

    /// <summary>The leading number in a filename, when it has one.</summary>
    internal static int? NumberFromFilename(string path) => MusicEvidence.SplitFilename(path).Number;

    internal static (int? Number, string Title) SplitFilename(string path) =>
        MusicEvidence.SplitFilename(path);

    private static List<IGrouping<(int Disc, int Track), TrackTags>> Clashes(AlbumGroup album) =>
        [.. album.Tracks
            .Where(t => t.TrackNumber is > 0)
            .GroupBy(t => (Disc: t.DiscNumber ?? 1, Track: t.TrackNumber!.Value))
            .Where(g => g.Count() > 1)];

    private static long Length(string path)
    {
        try { var i = new FileInfo(path); return i.Exists ? i.Length : 0; } catch { return 0; }
    }
}
