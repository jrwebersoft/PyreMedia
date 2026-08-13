namespace PyreMedia.Core.Music;

/// <summary>What a file turned out to be, and how sure we are.</summary>
public sealed record Identification(
    TrackTags Track,
    Recording Recording,
    ReleaseAppearance? Release,
    double Score,
    IReadOnlyList<string> Disagrees)
{
    /// <summary>
    /// The tags this file should carry, according to the audio rather than
    /// according to itself.
    /// </summary>
    public TagEdit Tags => new(
        Title: Recording.Title,
        Artist: Recording.Artist,
        Album: Release?.Title,
        AlbumArtist: Release?.Artist ?? Recording.Artist,
        Year: Release?.Year,
        TrackNumber: Release?.TrackNumber,
        DiscNumber: Release?.DiscNumber is > 1 ? Release.DiscNumber : null,
        MusicBrainzTrackId: Recording.Id,
        MusicBrainzAlbumId: Release?.Id);

    /// <summary>Nothing the file says contradicts what the audio says.</summary>
    public bool Agrees => Disagrees.Count == 0;
}

/// <summary>
/// Works out what a file is from the audio, not from what it claims to be.
///
/// The distinction is the entire point. Everything else here reasons from tags
/// the file already carries - its name, its folder, what its neighbours say -
/// and all of that is circular: a purchase tagged wrongly by a shop has the
/// wrong value in its filename, its folder and the majority of its album at
/// once, and comparing those to each other can never find the error. Thirteen
/// files spelling a band wrongly outvote the one that has it right.
///
/// This is the one path that starts somewhere else. Chromaprint says which
/// recording the audio holds, AcoustID turns that into a MusicBrainz id, and
/// MusicBrainz says what that recording is called. No step consults the file's
/// own tags, so the answer is independent of them - which is what makes it
/// worth having and also what makes it worth checking before it is applied.
///
/// It is slower and needs a network, so it is a deliberate act on a selection
/// rather than something the scan does to seventeen thousand files.
/// </summary>
public sealed class Identify(AcoustId? acoustId = null, MusicBrainz? musicBrainz = null)
{
    private readonly AcoustId _acoustId = acoustId ?? new AcoustId();
    private readonly MusicBrainz _musicBrainz = musicBrainz ?? new MusicBrainz();

    /// <summary>
    /// Identify one file. Null when the audio is not in AcoustID, which is
    /// common for anything homemade, live, or off a small label - and is not
    /// evidence that the file's own tags are wrong.
    /// </summary>
    public async Task<Identification?> OfAsync(
        TrackTags track, string apiKey, CancellationToken cancel = default)
    {
        var matches = await _acoustId.LookupAsync(track.Path, apiKey, track.Seconds, cancel);

        var best = matches.FirstOrDefault(m => m.RecordingId is not null);
        if (best?.RecordingId is null) return null;

        var recording = await _musicBrainz.RecordingAsync(best.RecordingId, cancel);
        if (recording is null) return null;

        return new Identification(track, recording,
            Choose(recording, track), best.Score, Disagreements(track, recording));
    }

    /// <summary>
    /// Which of a recording's releases this file most likely came from.
    ///
    /// A popular song appears on the album, three compilations and a
    /// soundtrack, and they disagree about the album name, the year and the
    /// track number. The file's own album tag is the best evidence available
    /// for which one it is - and using it here is not circular, because what is
    /// being decided is which release to believe, not what the recording is.
    /// The identity came from the audio; only the context comes from the tag.
    /// </summary>
    private static ReleaseAppearance? Choose(Recording recording, TrackTags track)
    {
        if (recording.Releases.Count == 0) return null;
        if (recording.Releases.Count == 1) return recording.Releases[0];

        if (track.Album.HasText)
        {
            var wanted = Duplicates.TitleKey(track.Album.Text);

            var named = recording.Releases.FirstOrDefault(r =>
                Duplicates.Alike(Duplicates.TitleKey(r.Title), wanted));

            if (named is not null) return named;
        }

        // Nothing to go on. The earliest release is the least bad default - it
        // is usually the original album rather than a compilation, and a
        // compilation wrongly chosen renames a whole record.
        return recording.Releases
            .OrderBy(r => r.Year ?? "9999", StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// Where the file and the audio disagree, in words.
    ///
    /// Reported rather than silently corrected. A disagreement means one of the
    /// two is wrong and this cannot say which: AcoustID returns the wrong
    /// recording often enough to matter, especially for a remaster that shares
    /// its audio with the original. A person looking at "the file says Oasis,
    /// the audio says Blur" resolves it instantly; a program that just applies
    /// the change does not even leave a trace of the question.
    /// </summary>
    private static List<string> Disagreements(TrackTags track, Recording recording)
    {
        var differences = new List<string>();

        void Compare(string what, string mine, string theirs)
        {
            if (mine.Length == 0 || theirs.Length == 0) return;

            if (!Duplicates.Alike(Duplicates.TitleKey(mine), Duplicates.TitleKey(theirs)))
                differences.Add($"the file says {what} \"{mine}\", the audio says \"{theirs}\"");
        }

        Compare("title", track.Title.Text, recording.Title);
        Compare("artist", track.Artist.Text, recording.Artist);

        return differences;
    }
}
