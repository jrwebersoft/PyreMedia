using System.Diagnostics;
using PyreMedia.Core.History;

namespace PyreMedia.Core.Music;

/// <summary>The fields a write may change. Anything null is left as it was.</summary>
public sealed record TagEdit(
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumArtist = null,
    string? Genre = null,
    string? Year = null,
    int? TrackNumber = null,
    int? DiscNumber = null,
    string? MusicBrainzTrackId = null,
    string? MusicBrainzAlbumId = null,

    // ReplayGain. Written as tags and never applied to the audio, so a player
    // evens out the volume at playback and deleting these undoes it entirely.
    string? TrackGain = null,
    string? TrackPeak = null,
    string? AlbumGain = null,
    string? AlbumPeak = null)
{
    public bool Any => Fields().Count > 0;

    /// <summary>The changes, as ffmpeg metadata keys.</summary>
    internal Dictionary<string, string> Fields()
    {
        var fields = new Dictionary<string, string>();

        void Set(string key, string? value)
        {
            if (value is not null) fields[key] = value;
        }

        Set("title", Title);
        Set("artist", Artist);
        Set("album", Album);
        Set("album_artist", AlbumArtist);
        Set("genre", Genre);
        Set("date", Year);
        Set("track", TrackNumber?.ToString());
        Set("disc", DiscNumber?.ToString());
        Set("MusicBrainz Release Track Id", MusicBrainzTrackId);
        Set("MusicBrainz Album Id", MusicBrainzAlbumId);

        // The names every player looks for. Upper case is not cosmetic: the
        // Vorbis comment spec is case-insensitive but a good deal of software
        // reading FLAC compares these bytes exactly.
        Set("REPLAYGAIN_TRACK_GAIN", TrackGain);
        Set("REPLAYGAIN_TRACK_PEAK", TrackPeak);
        Set("REPLAYGAIN_ALBUM_GAIN", AlbumGain);
        Set("REPLAYGAIN_ALBUM_PEAK", AlbumPeak);

        return fields;
    }
}

public sealed record TagWriteResult(bool Written, string Path, string? Error)
{
    public static TagWriteResult Failed(string path, string why) => new(false, path, why);
}

/// <summary>
/// Writes tags back into a file.
///
/// This is what makes everything else count. Kodi builds its music library from
/// the tags inside a file and not from where the file sits, so a plan that
/// moves seventeen thousand files into tidy folders and leaves their tags alone
/// has organised nothing that anybody will see.
///
/// Written through ffmpeg rather than by hand. Four tag formats already have
/// readers here and each took a while to get right; writers are strictly harder,
/// because a reader that misunderstands a file reports nonsense and a writer
/// that misunderstands one destroys it. ffmpeg remuxes with <c>-c copy</c>, so
/// the audio is passed through untouched - the bytes are not re-encoded and
/// nothing is lost - and it already knows every container in the library.
///
/// The file is never written in place. Output goes to a temporary file beside
/// the original, is read back and checked, and only then replaces it. A power
/// cut mid-write costs a temp file rather than a track.
/// </summary>
public sealed class MusicTagWriter(RenameHistory? history = null)
{
    /// <summary>
    /// Apply an edit to one file. Returns what happened; throws nothing.
    /// </summary>
    /// <param name="replaceAll">
    /// Drop every tag the file has and write only what this edit names, rather
    /// than merging into what is there. False for an edit - changing a title
    /// must not discard the comment or the ReplayGain values. True for an undo,
    /// where the recorded state is the whole answer and a field that used to be
    /// empty has to end up empty again. Merging cannot express that: clearing a
    /// tag by naming it with no value is not honoured by every muxer, which is
    /// how three of the four formats here failed to undo before this existed.
    /// </param>
    /// <param name="record">
    /// Whether to log this as its own undoable step. False when the write is
    /// part of a larger action that already has an entry - tagging a
    /// gap-filling copy, say, where undoing the copy deletes the file and a
    /// separate retag entry would be left rewriting something that is gone.
    /// </param>
    public TagWriteResult Write(TrackTags track, TagEdit edit, string? batchId = null,
                                CancellationToken cancel = default, bool replaceAll = false,
                                bool record = true)
    {
        if (!edit.Any) return new TagWriteResult(false, track.Path, null);
        if (!File.Exists(track.Path)) return TagWriteResult.Failed(track.Path, "the file is not there");

        var extension = Path.GetExtension(track.Path).ToLowerInvariant();

        var temp = track.Path + ".pyremedia-tmp" + extension;

        try
        {
            var before = Snapshot(track);

            // WMA is written here rather than through ffmpeg. Its muxer will not
            // write the WM/ names Windows and Kodi read, so a WMA came back
            // apparently written and silently missing its fields; and rewriting
            // only the ASF header copies the audio byte for byte instead of
            // remuxing the whole container.
            var error = extension is ".wma" or ".asf"
                ? AsfWriter.Write(track.Path, temp, edit)
                : Run(track.Path, temp, edit, extension, cancel, replaceAll);

            if (error is not null) return TagWriteResult.Failed(track.Path, error);

            // Read it back before trusting it. ffmpeg reports success on plenty
            // of files it has not actually tagged the way that was asked.
            var check = MusicFileReader.Read(temp);

            if (check.Error is not null)
                return TagWriteResult.Failed(track.Path, $"the rewritten file will not read: {check.Error}");

            if (Missing(check, edit) is { } missing)
                return TagWriteResult.Failed(track.Path, $"{missing} did not survive the write");

            // Audio was copied, not re-encoded, so the result should be within a
            // tag block's worth of the original. An order-of-magnitude change
            // means something other than the tags moved.
            var original = new FileInfo(track.Path).Length;
            var written = new FileInfo(temp).Length;

            if (written < original / 2)
                return TagWriteResult.Failed(track.Path,
                    $"the rewritten file is {written} bytes against {original} - not a tag change");

            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(track.Path));
            File.Move(temp, track.Path, overwrite: true);

            // The old values go on the entry, because once the file has been
            // rewritten nothing else in the world holds them.
            if (record)
                history?.Add(HistoryAction.Retag, track.Path, track.Path,
                    edit.Album ?? track.Album.Text, null, batchId, before);

            Apply(track, edit);
            return new TagWriteResult(true, track.Path, null);
        }
        catch (Exception ex)
        {
            return TagWriteResult.Failed(track.Path, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static string? Run(string source, string temp, TagEdit edit, string extension,
                               CancellationToken cancel, bool replaceAll)
    {
        var psi = new ProcessStartInfo(MusicTools.Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        void Arg(params string[] args) { foreach (var a in args) psi.ArgumentList.Add(a); }

        Arg("-hide_banner", "-v", "error", "-y", "-i", source);

        // Keep every tag the file already had, then override the named ones.
        // Without this an edit to the title would silently drop the comment,
        // the ReplayGain values and anything else a previous tagger left. An
        // undo wants the opposite: nothing carried over, only what was recorded.
        Arg("-map_metadata", replaceAll ? "-1" : "0", "-c", "copy");

        // ID3v2.4 is the newer standard and the worse choice: Windows Explorer
        // and a good deal of hardware still read only 2.3, and a library that
        // has to be readable everywhere is the whole point of tagging it.
        if (extension is ".mp3") Arg("-id3v2_version", "3", "-write_id3v1", "1");

        foreach (var (key, value) in edit.Fields()) Arg("-metadata", $"{key}={value}");

        Arg(temp);

        using var process = Process.Start(psi);
        if (process is null) return "ffmpeg would not start - is it on the path?";

        var errors = process.StandardError.ReadToEndAsync(cancel);

        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "ffmpeg did not finish within two minutes";
        }

        if (process.ExitCode != 0)
        {
            var text = errors.IsCompletedSuccessfully ? errors.Result.Trim() : "";
            return $"ffmpeg failed: {(text.Length > 0 ? text.Split('\n')[^1].Trim() : $"exit code {process.ExitCode}")}";
        }

        return File.Exists(temp) ? null : "ffmpeg reported success but wrote nothing";
    }

    /// <summary>The first field asked for that did not come back, or null.</summary>
    private static string? Missing(TrackTags written, TagEdit edit)
    {
        bool Same(string? wanted, string got) =>
            wanted is null || string.Equals(MusicHealth.Tidy(wanted), MusicHealth.Tidy(got),
                                            StringComparison.OrdinalIgnoreCase);

        if (!Same(edit.Title, written.Title.Text)) return "the title";
        if (!Same(edit.Artist, written.Artist.Text)) return "the artist";
        if (!Same(edit.Album, written.Album.Text)) return "the album";
        if (!Same(edit.AlbumArtist, written.AlbumArtist.Text)) return "the album artist";

        if (edit.TrackNumber is { } n && written.TrackNumber != n) return "the track number";
        if (edit.DiscNumber is { } d && written.DiscNumber != d) return "the disc number";

        return null;
    }

    private static Dictionary<string, string?> Snapshot(TrackTags t) => new()
    {
        ["title"] = t.Title.HasText ? t.Title.Text : null,
        ["artist"] = t.Artist.HasText ? t.Artist.Text : null,
        ["album"] = t.Album.HasText ? t.Album.Text : null,
        ["album_artist"] = t.AlbumArtist.HasText ? t.AlbumArtist.Text : null,
        ["genre"] = t.Genre.HasText ? t.Genre.Text : null,
        ["date"] = t.Year.HasText ? t.Year.Text : null,
        ["track"] = t.TrackNumber?.ToString(),
        ["disc"] = t.DiscNumber?.ToString()
    };

    /// <summary>Bring the in-memory tags in line with what was just written.</summary>
    private static void Apply(TrackTags track, TagEdit edit)
    {
        TagValue Keep(string? value, TagValue current) => value is null
            ? current
            : new TagValue(value, current.Source is TagSource.None ? TagSource.Id3v2 : current.Source,
                           TextConfidence.Declared);

        track.Title = Keep(edit.Title, track.Title);
        track.Artist = Keep(edit.Artist, track.Artist);
        track.Album = Keep(edit.Album, track.Album);
        track.AlbumArtist = Keep(edit.AlbumArtist, track.AlbumArtist);
        track.Genre = Keep(edit.Genre, track.Genre);
        track.Year = Keep(edit.Year, track.Year);

        track.TrackNumber = edit.TrackNumber ?? track.TrackNumber;
        track.DiscNumber = edit.DiscNumber ?? track.DiscNumber;
        track.MusicBrainzTrackId = edit.MusicBrainzTrackId ?? track.MusicBrainzTrackId;
        track.MusicBrainzAlbumId = edit.MusicBrainzAlbumId ?? track.MusicBrainzAlbumId;
    }

    /// <summary>
    /// Put a file's tags back to what a history entry says they were.
    /// Hand this to <c>RevertService.Retag</c>.
    /// </summary>
    public string? Undo(HistoryEntry entry)
    {
        if (entry.Before is not { Count: > 0 } before)
            return "the tags this replaced were not recorded";

        string? Was(string key) => before.GetValueOrDefault(key);

        int? Number(string key) =>
            int.TryParse(Was(key), out var n) ? n : null;

        // An empty string, not null, for a field that used to be empty: null
        // means "leave alone" everywhere else here, and leaving alone is the
        // one thing an undo must not do.
        string Blank(string key) => Was(key) ?? "";

        var result = Write(new TrackTags { Path = entry.NewPath }, new TagEdit(
            Title: Blank("title"),
            Artist: Blank("artist"),
            Album: Blank("album"),
            AlbumArtist: Blank("album_artist"),
            Genre: Blank("genre"),
            Year: Blank("date"),
            TrackNumber: Number("track"),
            DiscNumber: Number("disc")), replaceAll: true);

        return result.Written ? null : result.Error ?? "the tags would not write";
    }

    /// <summary>
    /// The edit that puts right everything MusicHealth found a safe fix for.
    /// Returns an empty edit when there is nothing to do.
    /// </summary>
    public static TagEdit Repairs(IEnumerable<Finding> findings)
    {
        var by = findings.Where(f => f.Fixable)
            .GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Suggested, StringComparer.OrdinalIgnoreCase);

        string? Get(string field) => by.GetValueOrDefault(field);

        return new TagEdit(
            Title: Get("Title"),
            Artist: Get("Artist"),
            Album: Get("Album"),
            AlbumArtist: Get("AlbumArtist"),
            Genre: Get("Genre"),
            Year: Get("Year"));
    }
}
