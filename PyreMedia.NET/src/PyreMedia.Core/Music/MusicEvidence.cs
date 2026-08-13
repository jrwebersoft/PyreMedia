using System.Text;

namespace PyreMedia.Core.Music;

/// <summary>What one pass of evidence-gathering found.</summary>
public sealed record EvidenceTally(
    int Folders,
    int FoldersWithNfo,
    int FoldersBelieved,
    int TitlesFilled,
    int NumbersFilled,
    int LengthsFilled,
    int YearsFilled,
    int CompilationsDeclared,
    int MusicBrainzIdsFound)
{
    public override string ToString() =>
        $"{FoldersBelieved}/{FoldersWithNfo} nfo believed; filled {TitlesFilled} titles, "
        + $"{NumbersFilled} numbers, {LengthsFilled} lengths, {YearsFilled} years";
}

/// <summary>
/// Gathers everything a folder knows about its own music before anything is
/// grouped or named.
///
/// Four sources, none of them sufficient alone: the embedded tags, the filename,
/// the folder structure, and any .nfo a previous library manager left behind. A
/// thirty-year-old library has all four in varying states of decay, and the one
/// that is missing is never the same one twice - so they are read together and
/// each fills the others' gaps.
///
/// Nothing here overwrites a value that is already present. Evidence fills
/// silence; it does not argue with what the file says about itself.
/// </summary>
public static class MusicEvidence
{
    public static EvidenceTally Gather(IEnumerable<TrackTags> tracks)
    {
        int folders = 0, withNfo = 0, believed = 0;
        int titles = 0, numbers = 0, lengths = 0, years = 0, compilations = 0, ids = 0;

        var byFolder = tracks
            .Where(t => t.Error is null)
            .GroupBy(t => Path.GetDirectoryName(t.Path) ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0);

        foreach (var folder in byFolder)
        {
            folders++;
            var files = folder.ToList();

            // The filename first: it is always there, and it is often the only
            // record of a track's number and name that survived.
            foreach (var track in files)
            {
                var (number, title) = SplitFilename(track.Path);

                if (track.TrackNumber is null or 0 && number is > 0) { track.TrackNumber = number; numbers++; }

                if (!track.Title.HasText && title.Length > 0)
                {
                    track.Title = new TagValue(title, TagSource.Filename, TextConfidence.Ascii);
                    titles++;
                }
            }

            var nfo = MusicNfoReader.ReadAlbum(folder.Key);
            if (nfo is null) continue;
            withNfo++;

            // A scraped listing that does not describe these files describes some
            // other release, and believing it would renumber the album into
            // nonsense. Measured: about a third of them are for the wrong record.
            if (!nfo.Corroborates(files)) continue;
            believed++;

            foreach (var track in files)
            {
                var listed = Match(nfo, track);

                if (listed is not null)
                {
                    if (!track.Title.HasText)
                    {
                        track.Title = new TagValue(listed.Title, TagSource.Nfo, TextConfidence.Declared);
                        titles++;
                    }

                    if (track.TrackNumber is null or 0 && listed.Position > 0)
                    {
                        track.TrackNumber = listed.Position;
                        numbers++;
                    }

                    // The length is the prize. It is what separates a second copy
                    // from a second version, and having it here means not running
                    // ffprobe over the file at all.
                    if (track.Seconds is null && listed.Seconds > 0)
                    {
                        track.Seconds = listed.Seconds;
                        lengths++;
                    }

                    if (track.MusicBrainzTrackId is null && listed.MusicBrainzTrackId is not null)
                    {
                        track.MusicBrainzTrackId = listed.MusicBrainzTrackId;
                        track.Gathered.Add("musicbrainztrackid");
                        ids++;
                    }
                }

                if (!track.Year.HasText && nfo.Year is not null)
                {
                    track.Year = new TagValue(nfo.Year, TagSource.Nfo, TextConfidence.Ascii);
                    years++;
                }

                if (track.MusicBrainzAlbumId is null && nfo.MusicBrainzAlbumId is not null)
                {
                    track.MusicBrainzAlbumId = nfo.MusicBrainzAlbumId;
                    track.Gathered.Add("musicbrainzalbumid");
                    ids++;
                }

                if (track.MusicBrainzArtistId is null && nfo.MusicBrainzArtistId is not null)
                {
                    track.MusicBrainzArtistId = nfo.MusicBrainzArtistId;
                    ids++;
                }

                // A declared compilation beats guessing at one. Written only where
                // the file itself is silent, and only as the marker the grouper
                // already understands - so a soundtrack whose tracks each name
                // their own performer still gathers into one album.
                if (nfo.IsCompilation is true && !track.AlbumArtist.HasText)
                {
                    track.AlbumArtist = new TagValue(
                        AlbumGrouper.VariousArtists, TagSource.Nfo, TextConfidence.Ascii);
                    compilations++;
                }
            }
        }

        return new(folders, withNfo, believed, titles, numbers, lengths, years, compilations, ids);
    }

    /// <summary>
    /// The listed track this file is. By title first, because a library that needs
    /// this help is a library whose track numbers are already wrong - the numbers
    /// are what we are trying to repair, so they cannot also be the key.
    /// </summary>
    private static NfoTrack? Match(AlbumNfo nfo, TrackTags track)
    {
        var name = Simplify(track.Title.HasText
            ? track.Title.Text
            : Path.GetFileNameWithoutExtension(track.Path));

        if (name.Length > 0)
        {
            var byTitle = nfo.Tracks.FirstOrDefault(t =>
            {
                var listed = Simplify(t.Title);
                return listed.Length > 0 && (listed == name || listed.Contains(name) || name.Contains(listed));
            });

            if (byTitle is not null) return byTitle;
        }

        return track.TrackNumber is > 0
            ? nfo.Tracks.FirstOrDefault(t => t.Position == track.TrackNumber)
            : null;
    }

    /// <summary>
    /// A filename split into its track number and its title.
    ///
    /// Leading zero-groups are stepped over rather than taken: files turn up named
    /// "00 01 One Vision" where the first pair is a disc or a rip artefact and the
    /// second is the track. The first non-zero group is the number, and whatever
    /// follows the numbers is the title.
    /// </summary>
    public static (int? Number, string Title) SplitFilename(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Trim();

        int? number = null;
        var i = 0;

        while (i < name.Length)
        {
            var start = i;
            while (i < name.Length && char.IsAsciiDigit(name[i])) i++;

            var length = i - start;
            if (length is 0 or > 3) { i = start; break; }

            // A number has to be separated from what follows, or "1984" reads as
            // track 198 and the title becomes "4".
            if (i < name.Length && !char.IsWhiteSpace(name[i]) && name[i] is not ('-' or '.' or '_'))
            {
                i = start;
                break;
            }

            if (int.TryParse(name.AsSpan(start, length), out var n) && n is > 0 and < 200)
            {
                number = n;
                while (i < name.Length && (char.IsWhiteSpace(name[i]) || name[i] is '-' or '.' or '_')) i++;
                break;
            }

            while (i < name.Length && (char.IsWhiteSpace(name[i]) || name[i] is '-' or '.' or '_')) i++;
            if (i == start) break;
        }

        return (number, name[i..].Trim());
    }

    private static string Simplify(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);

        return sb.ToString();
    }
    /// <summary>
    /// Everything gathered from outside the audio files that is not yet inside
    /// them, as an edit per file.
    ///
    /// This exists because of an ordering trap that destroys data silently.
    /// Gather() reads album.nfo files for MusicBrainz ids, exact track lengths,
    /// years and titles - 5,926 ids and 6,861 lengths on one measured library,
    /// none of which anything here can derive on its own. It puts them in
    /// memory. The sweep then deletes the .nfo files as scraped rubbish, which
    /// they are, and the harvest goes with them.
    ///
    /// So the harvest has to be written into the audio before the sweep runs,
    /// and <see cref="MusicSweep"/> refuses to remove scraped metadata while
    /// this returns anything.
    ///
    /// Filename-sourced values are included for the same reason on a smaller
    /// scale: a title that exists only in the filename does not survive being
    /// renamed to a pattern built from tags.
    /// </summary>
    public static List<(TrackTags Track, TagEdit Edit)> Harvest(IEnumerable<TrackTags> tracks)
    {
        var harvest = new List<(TrackTags, TagEdit)>();

        foreach (var track in tracks)
        {
            // Only what came from outside. A value the file already carried is
            // already safe and rewriting it would be churn.
            string? Gathered(TagValue v) =>
                v.Source is TagSource.Nfo or TagSource.Filename && v.HasText ? v.Text : null;

            var edit = new TagEdit(
                Title: Gathered(track.Title),
                Album: Gathered(track.Album),
                AlbumArtist: Gathered(track.AlbumArtist),
                Year: Gathered(track.Year),
                MusicBrainzTrackId: track.Gathered.Contains("musicbrainztrackid")
                    ? track.MusicBrainzTrackId : null,
                MusicBrainzAlbumId: track.Gathered.Contains("musicbrainzalbumid")
                    ? track.MusicBrainzAlbumId : null);

            if (edit.Any) harvest.Add((track, edit));
        }

        return harvest;
    }
}
