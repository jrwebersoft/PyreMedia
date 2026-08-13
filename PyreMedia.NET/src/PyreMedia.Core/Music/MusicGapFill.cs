namespace PyreMedia.Core.Music;

/// <summary>How sure we are that a file elsewhere is the recording a gap wants.</summary>
public enum GapConfidence
{
    /// <summary>
    /// The file carries the MusicBrainz recording id the listing names. Not a
    /// resemblance - the same recording, identified.
    /// </summary>
    Certain,

    /// <summary>
    /// The title matches and the length is what the listing says it should be.
    /// Right nearly always, and the failure mode is a remix of the same length.
    /// </summary>
    Likely,

    /// <summary>
    /// The title matches and the length does not. This is the remix, the live
    /// take, the radio edit. Offered so it can be seen, never filled in
    /// automatically.
    /// </summary>
    Doubtful
}

/// <summary>
/// One recording that could fill a gap, and every file of it already held.
/// </summary>
public sealed record GapCandidate(
    TrackTags Best,
    IReadOnlyList<TrackTags> Copies,
    GapConfidence Confidence,
    string Why)
{
    /// <summary>What copying this would cost on disk.</summary>
    public long Bytes
    {
        get { try { return new FileInfo(Best.Path).Length; } catch { return 0; } }
    }
}

/// <summary>
/// A track an album should have, that the album has not got, and what is held
/// elsewhere that might be it.
/// </summary>
public sealed record Gap(
    AlbumGroup Album,
    NfoTrack Wanted,
    IReadOnlyList<GapCandidate> Candidates)
{
    /// <summary>
    /// Fillable without asking: exactly one recording is a good enough match.
    ///
    /// More than one means the library holds two different recordings of this
    /// song and nothing here can say which one the album wants. That is the
    /// case worth stopping on, not the case worth guessing at.
    /// </summary>
    public GapCandidate? Obvious =>
        Candidates.Count(c => c.Confidence is GapConfidence.Certain) == 1
            ? Candidates.Single(c => c.Confidence is GapConfidence.Certain)
            : Candidates.Count(c => c.Confidence is GapConfidence.Likely) == 1
              && !Candidates.Any(c => c.Confidence is GapConfidence.Certain)
                ? Candidates.Single(c => c.Confidence is GapConfidence.Likely)
                : null;

    public bool NeedsAsking => Obvious is null && Candidates.Count > 0;
}

/// <summary>
/// Fills the holes in an album from the rest of the library.
///
/// A song sits on its album and on a soundtrack, and a library assembled over
/// thirty years routinely holds one but not the other. Where the missing one is
/// already on disk under a different album, it can be copied into place.
///
/// Copied, not linked. A symlink cannot carry its own tags, and the two places
/// need different ones - a different album name and a different track number -
/// because Kodi builds its library from the tags embedded in the file rather
/// than from where the file sits. One file can only be in one album correctly.
/// Symlinks are unusable over a network share in any case: Windows ships with
/// remote-to-remote evaluation disabled, so a link stored on the share is not
/// followed by the machines reading it.
///
/// The hard part is not finding a file with the right title. It is knowing
/// whether that file is the recording the album wants, because a soundtrack
/// version is very often a remix, an edit or a new recording that merely shares
/// its name. Getting that wrong files the wrong take under the right number and
/// it is nearly impossible to notice afterwards. So the bar is deliberately
/// high, and two different recordings of one song is a question rather than a
/// coin toss.
/// </summary>
public static class MusicGapFill
{
    /// <summary>
    /// Every gap in every album, with whatever the library holds that might
    /// fill it. Finds and reports; copies nothing.
    /// </summary>
    /// <param name="audio">
    /// Fingerprints, where they have been taken. Used to tell one recording of
    /// a song from another among the candidates, so two encodings of one rip
    /// are offered as a single choice rather than as a decision between them.
    /// </param>
    public static List<Gap> Find(
        IReadOnlyList<AlbumGroup> albums,
        FingerprintSet? audio = null,
        Func<string, AlbumNfo?>? listing = null)
    {
        audio ??= FingerprintSet.None;
        listing ??= MusicNfoReader.ReadAlbum;

        // Everything held, indexed by title. Built once: the alternative is a
        // scan of the library per gap, and there are thousands of gaps.
        var byTitle = new Dictionary<string, List<TrackTags>>(StringComparer.Ordinal);

        foreach (var track in albums.SelectMany(a => a.Tracks))
        {
            var key = Duplicates.TitleKey(track);
            if (key.Length == 0) continue;

            if (!byTitle.TryGetValue(key, out var held)) byTitle[key] = held = [];
            held.Add(track);
        }

        var gaps = new List<Gap>();

        foreach (var album in albums)
        {
            if (album.SourceFolder is not { } folder) continue;

            var nfo = listing(folder);

            // An album.nfo describing a different release than the one on disk
            // would invent gaps for tracks this album never had. Roughly a third
            // of them do, so this check is doing real work.
            if (nfo is null || !nfo.Corroborates(album.Tracks)) continue;

            var present = album.Tracks
                .Where(t => t.TrackNumber is > 0)
                .Select(t => t.TrackNumber!.Value)
                .ToHashSet();

            // A folder holding one track of a twenty-track listing has not got
            // nineteen gaps in any useful sense - it is an album nobody ripped,
            // and treating it as fillable produces thousands of speculative
            // matches for records the library never had. A gap is only a gap in
            // an album that is mostly here.
            if (nfo.Tracks.Count == 0
                || present.Count < nfo.Tracks.Count * MostlyHere) continue;

            var here = album.Tracks.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var wanted in nfo.Tracks.Where(t => t.Position > 0 && !present.Contains(t.Position)))
            {
                var key = Duplicates.TitleKey(wanted.Title);
                if (key.Length == 0 || !byTitle.TryGetValue(key, out var held)) continue;

                var elsewhere = held
                    .Where(t => !here.Contains(t.Path) && ByTheRightArtist(t, album))
                    .ToList();

                if (elsewhere.Count == 0) continue;

                var candidates = Rank(elsewhere, wanted, album, audio);
                if (candidates.Count > 0) gaps.Add(new Gap(album, wanted, candidates));
            }
        }

        return gaps;
    }

    /// <summary>
    /// How much of an album's listing must already be on disk before the rest
    /// of it counts as gaps rather than as a record nobody ripped.
    /// </summary>
    private const double MostlyHere = 0.5;

    /// <summary>
    /// Whether a file is by the artist whose album has the hole in it.
    ///
    /// The single most important check here, and its absence is not a subtle
    /// failure. Searching on title alone, Citizen King's missing "Blue Monday"
    /// matches Orgy's cover of it - same title, lengths four minutes twenty-five
    /// against four twenty-six - and a different band's recording gets filed
    /// under the right track number where nobody will ever spot it.
    ///
    /// Compilations cannot be checked this way, because every track is by
    /// somebody different and the album artist is the label's word for that.
    /// They are handled by demanding an identifier instead, in Judge.
    /// </summary>
    private static bool ByTheRightArtist(TrackTags file, AlbumGroup album)
    {
        if (album.IsCompilation) return true;

        var wanted = Duplicates.TitleKey(album.DisplayArtist);
        if (wanted.Length == 0) return true;

        // Either credit will do. A guest spot is tagged with the guest as
        // Artist and the headliner as AlbumArtist about as often as the reverse.
        return new[] { file.Artist, file.AlbumArtist }
            .Where(v => v.HasText)
            .Any(v => Duplicates.Alike(Duplicates.TitleKey(v.Text), wanted));
    }

    /// <summary>
    /// Group the files that share a title into distinct recordings, and judge
    /// each against what the listing says it wants.
    /// </summary>
    private static List<GapCandidate> Rank(
        IReadOnlyList<TrackTags> elsewhere, NfoTrack wanted, AlbumGroup album, FingerprintSet audio)
    {
        var recordings = new List<List<TrackTags>>();

        foreach (var file in elsewhere)
        {
            // Same recording as one already grouped? The audio decides where it
            // can. Where it cannot, length does - two files of one rip agree on
            // it, and a remix is what we are trying not to merge.
            var home = recordings.FirstOrDefault(r =>
                audio.Agreement(r[0].Path, file.Path) is { } score
                    ? score >= Fingerprint.SameRecording
                    : r[0].Seconds is > 0 && file.Seconds is > 0
                      && Duplicates.SameLength(r[0].Seconds!.Value, file.Seconds!.Value));

            if (home is null) recordings.Add([file]);
            else home.Add(file);
        }

        var candidates = new List<GapCandidate>();

        foreach (var recording in recordings)
        {
            // The user's rule: of several copies of one recording, the best one
            // is the one that gets copied.
            var best = recording
                .OrderByDescending(Duplicates.Quality)
                .ThenByDescending(t => t.Seconds ?? 0)
                .First();

            var (confidence, why) = Judge(recording, wanted, album);
            candidates.Add(new GapCandidate(best, recording, confidence, why));
        }

        return [.. candidates.OrderBy(c => c.Confidence)];
    }

    private static (GapConfidence, string) Judge(
        IReadOnlyList<TrackTags> recording, NfoTrack wanted, AlbumGroup album)
    {
        // A MusicBrainz recording id is an identity, not a resemblance. When a
        // previous tagger left one and the listing names the same one, there is
        // nothing left to weigh.
        if (!string.IsNullOrWhiteSpace(wanted.MusicBrainzTrackId)
            && recording.Any(t => string.Equals(t.MusicBrainzTrackId, wanted.MusicBrainzTrackId,
                                                StringComparison.OrdinalIgnoreCase)))
            return (GapConfidence.Certain, "MusicBrainz recording id matches the listing");

        // Nothing checked the performer on a compilation, so title and length
        // are all that is left, and on their own they are not enough: every
        // covered song in the library matches every other recording of it. An
        // identifier or a person.
        if (album.IsCompilation)
            return (GapConfidence.Doubtful,
                "the title matches, but on a compilation nothing here can say "
                + "whose recording this is - the listing named no MusicBrainz id");

        var lengths = recording.Where(t => t.Seconds is > 0).Select(t => t.Seconds!.Value).ToList();

        if (wanted.Seconds <= 0 || lengths.Count == 0)
            return (GapConfidence.Doubtful,
                "the title matches, but nothing here knows how long either should be");

        var nearest = lengths.OrderBy(s => Math.Abs(s - wanted.Seconds)).First();

        if (!Duplicates.SameLength(nearest, wanted.Seconds))
            return (GapConfidence.Doubtful,
                $"the title matches but this runs {Minutes(nearest)} against the "
                + $"{Minutes(wanted.Seconds)} the listing wants - likely a different version");

        return (GapConfidence.Likely,
            $"title matches and the length is right at {Minutes(nearest)}");
    }

    private static string Minutes(double s) => $"{(int)s / 60}:{(int)s % 60:00}";
}
