namespace PyreMedia.Core.Media;

/// <summary>
/// Whether a remuxed file was truncated.
///
/// Truncation is the failure that matters most - the picture plays, nothing
/// looks wrong, and the last ten minutes are missing. So this check has to be
/// strict. It also has to be right, and the obvious version is not.
///
/// A container's duration is the length of its longest stream. Drop that
/// stream and the file gets shorter without losing a frame of anything kept.
/// Comparing the source container against the output container therefore
/// measures the wrong thing, and it failed on a real file: a 2160p feature
/// whose two Spanish audio tracks ran sixty seconds past the picture came out
/// exactly sixty seconds "short" once they were dropped, and a perfectly good
/// remux was thrown away.
///
/// What should be compared is the longest stream that was kept. Where the
/// probe reports per-stream durations that is exact and the tolerance can stay
/// tight. Where it does not - MKV frequently reports only the container figure
/// - there is nothing better than the container, and the tolerance widens to
/// cover the streams that might have been defining it.
/// </summary>
public static class DurationCheck
{
    /// <summary>
    /// Why the output looks truncated, or null if it does not.
    /// </summary>
    /// <param name="kept">The streams the plan decided to keep.</param>
    public static string? Failed(MediaInfo source, MediaInfo output, IReadOnlyList<MediaStream> kept)
    {
        if (source.DurationSeconds <= 0 || output.DurationSeconds <= 0) return null;

        // Track by track first, which is the question actually worth asking:
        // does each thing that survived still run as long as it used to? A
        // dropped track cannot affect that answer however long it was, and a
        // single truncated stream is caught even when the file as a whole still
        // looks the right length.
        if (PerStream(source, output, kept) is { } stream) return stream;

        // Nothing measurable, and something timed was dropped: this cannot tell
        // a file that is shorter because a long track left from a file that is
        // shorter because it was cut off. The container it would compare
        // against is partly the length of the track that is gone.
        //
        // No verdict rather than a guess. A guess here has been wrong twice on
        // real files and each time it destroyed a good remux, which is a worse
        // outcome than the truncation it was guarding against - the streams,
        // codecs, Dolby Vision and file size are all still checked, and the
        // original is not removed until those pass.
        var measurable = kept.Any(s => Timed(s) && s.Seconds > 0);
        var dropped = source.Streams.Count(Timed) > kept.Count(Timed);

        if (!measurable && dropped) return null;

        var expected = Expected(source, kept);

        // Exact when the streams told us their own lengths, loose when they did
        // not. Three seconds covers container rounding and the last frame of a
        // GOP; truncation is measured in minutes and is caught either way.
        var known = kept.Count > 0 && kept.Any(s => s.Seconds > 0);
        var allowed = known ? 3.0 : Math.Max(3.0, source.DurationSeconds * 0.02);

        var drift = Math.Abs(expected - output.DurationSeconds);
        if (drift <= allowed) return null;

        // Longer than expected is not truncation. It happens when a muxer pads
        // to a whole frame or writes a duration from a stream that was not
        // measurable, and refusing on it would reject good files.
        if (output.DurationSeconds > expected) return null;

        return $"runs {Minutes(output.DurationSeconds)} against the "
             + $"{Minutes(expected)} expected from the tracks being kept - truncated";
    }

    /// <summary>
    /// Each kept track against its own length in the output, or null when
    /// nothing can be compared that way.
    ///
    /// The streams the remux wrote are the kept ones in the order they were
    /// given, so they line up position by position. Only pairs that agree on
    /// kind are compared, so a mismatch in that order refuses to guess rather
    /// than reporting a subtitle against a video.
    ///
    /// Silent when the durations are not known. Where a container reports none
    /// - which Matroska frequently does not, per stream - this has nothing to
    /// say and the whole-file check still runs behind it.
    /// </summary>
    private static string? PerStream(MediaInfo source, MediaInfo output, IReadOnlyList<MediaStream> kept)
    {
        var mine = kept.Where(Timed).ToList();
        var theirs = output.Streams.Where(Timed).ToList();

        if (mine.Count == 0 || mine.Count != theirs.Count) return null;

        for (var i = 0; i < mine.Count; i++)
        {
            var before = mine[i];
            var after = theirs[i];

            if (before.Kind != after.Kind) return null;      // not the pairing we assumed
            if (before.Seconds <= 0 || after.Seconds <= 0) continue;

            // Three seconds covers container rounding and the last frame of a
            // GOP. Truncation is measured in minutes.
            if (before.Seconds - after.Seconds <= 3.0) continue;

            var name = string.IsNullOrWhiteSpace(before.Language)
                ? before.Kind.ToString().ToLowerInvariant()
                : $"{before.Kind.ToString().ToLowerInvariant()} ({before.Language})";

            return $"the {name} track runs {Minutes(after.Seconds)} against the "
                 + $"{Minutes(before.Seconds)} it ran before - truncated";
        }

        return null;
    }

    /// <summary>
    /// How long the output should be: the longest stream being kept.
    ///
    /// Falls back to the source container when no kept stream reports a
    /// length, which is the best available answer rather than a good one.
    /// </summary>
    public static double Expected(MediaInfo source, IReadOnlyList<MediaStream> kept)
    {
        var longest = kept.Where(Timed)
            .Where(s => s.Seconds > 0)
            .Select(s => s.Seconds)
            .DefaultIfEmpty(0)
            .Max();

        // Never expect more than the source had. A stream whose reported
        // duration exceeds its own container is a broken header, not a promise.
        return longest > 0 ? Math.Min(longest, source.DurationSeconds) : source.DurationSeconds;
    }

    /// <summary>
    /// Whether a stream's length means anything.
    ///
    /// An embedded poster is one JPEG, and ffprobe reports its duration as the
    /// whole file - so a 105-minute film with cover art has a "video" stream
    /// claiming 105 minutes that contains a single frame. Letting that set the
    /// expected length is how the second version of this check still failed
    /// The Roses: the poster inherited the container's 106:25 from the audio
    /// tracks being dropped, and the output's honest 105:25 read as a minute
    /// missing.
    ///
    /// Data and attachment streams are excluded for the same reason. Only
    /// something that actually plays over time can say how long a file is.
    /// </summary>
    private static bool Timed(MediaStream stream) =>
        !stream.IsCoverArt
        && stream.Kind is StreamKind.Video or StreamKind.Audio or StreamKind.Subtitle;

    private static string Minutes(double seconds) =>
        $"{(int)seconds / 60}:{(int)seconds % 60:00}";
}
