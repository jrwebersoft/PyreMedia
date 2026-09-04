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
///
/// Every kept track is then asked whether it still runs as long as it did,
/// which is the question worth asking and catches a single truncated stream in
/// a file that is otherwise the right length. It is only asked of tracks whose
/// stated length before the remux was measured rather than inherited from the
/// container - see <see cref="Inherited"/>, which is where a good remux was
/// lost. Only picture and sound are allowed to define the expected length,
/// since a subtitle ends when the talking does.
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
        var mine = kept.Where(Comparable).ToList();
        var theirs = output.Streams.Where(Comparable).ToList();

        if (mine.Count == 0 || mine.Count != theirs.Count) return null;

        for (var i = 0; i < mine.Count; i++)
        {
            var before = mine[i];
            var after = theirs[i];

            if (before.Kind != after.Kind) return null;      // not the pairing we assumed
            if (before.Seconds <= 0 || after.Seconds <= 0) continue;

            // Only where the number is a description of that track rather than
            // the container's figure wearing its name. See Believable.
            if (!Believable(before)) continue;

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
    /// Subtitles are excluded for a different reason, and it cost a real file.
    /// A subtitle track's duration is not a length of content: before a remux
    /// it is usually the container's figure copied onto every track, and after
    /// one it is the timestamp of the last cue - which is legitimately minutes
    /// before the end, because nobody speaks over the credits. The Croods is
    /// the case that proved it. Its source reports no duration at all for the
    /// video or either audio track, and all seventeen PGS tracks report exactly
    /// 5,916.9 seconds, to the decimal, which is the container's own figure -
    /// seventeen languages do not end on the same instant. The remux wrote the
    /// honest 88:50 for the English track, the check read a ten-minute
    /// shortfall, and a good 20 GB remux was refused. Worse, since the picture
    /// and sound said nothing measurable, subtitles were the only thing being
    /// compared: the verdict rested entirely on the one stream that cannot give
    /// it.
    ///
    /// Data and attachment streams are excluded for the same reason. Only
    /// something that plays over time, and whose end is the end of the film,
    /// can say how long a file is.
    /// </summary>
    private static bool Timed(MediaStream stream) =>
        !stream.IsCoverArt
        && stream.Kind is StreamKind.Video or StreamKind.Audio;

    /// <summary>
    /// Whether a stream can be compared against its own self after a remux.
    ///
    /// Wider than <see cref="Timed"/> on purpose. Asking whether each track
    /// still runs as long as it did is the right question and a subtitle is
    /// entitled to be asked it - what a subtitle cannot do is define how long
    /// the film is, which is what Timed is for.
    /// </summary>
    /// <summary>
    /// Whether a track's length can be compared across a remux at all.
    ///
    /// Picture and sound only. A subtitle's DURATION is not a measurement of
    /// its content - it is a statistic whoever muxed the file wrote down, and
    /// muxers disagree about it even when not a byte has changed.
    ///
    /// Measured on Babe (1995), which refused to remux for a year of film time
    /// that was never missing. Its English PGS track carries a real per-track
    /// tag of 01:29:24.818, so the tag test below trusted it; mkvmerge wrote
    /// 01:27:13.186 for the same track. The content was identical - 2,491
    /// packets in, 2,491 packets out, last event at 5364.818 in both - because
    /// mkvmerge discounts the trailing PGS clear-screen sets, which carry no
    /// duration of their own. Two honest tools, one bit-identical track, two
    /// minutes of disagreement.
    ///
    /// Picture and sound have no such ambiguity: the same file came through
    /// with 01:31:55.677 and 01:31:55.723 on both sides, to the millisecond.
    /// Losing a subtitle track outright is still caught, by the stream and
    /// codec checks that run beside this one.
    /// </summary>
    private static bool Comparable(MediaStream stream) =>
        !stream.IsCoverArt
        && stream.Kind is StreamKind.Video or StreamKind.Audio;

    /// <summary>
    /// Whether a stream's stated length can be compared with a straight face.
    ///
    /// The question is where the number came from, not what it equals. ffprobe
    /// fills a stream's duration in from the container when the track carries
    /// no figure of its own, and on Matroska it does that for subtitles - so a
    /// PGS track claims the whole film while the picture beside it claims
    /// nothing. Comparing that against a remux, which writes real tags, reads
    /// as a truncation. The Croods lost a good 20 GB file to it: seventeen PGS
    /// tracks all reporting the container's 5916.9 seconds against an honest
    /// 88:50, and no other track reporting anything at all.
    ///
    /// The first attempt at this test asked whether the value equalled the
    /// container, which was wrong in the worst possible direction: an honest
    /// picture and an honest main soundtrack are exactly as long as the film,
    /// so it excused the two tracks whose truncation matters most and left the
    /// check able to fire only on subtitles - the opposite of the intent, and
    /// on any file carrying real tags a live regression.
    ///
    /// So it asks the probe instead. A figure that came from the file's own
    /// per-track tag is a measurement of that track and is compared, whatever
    /// it equals. A figure ffprobe supplied is only believed for picture and
    /// sound, where the container's length is a fair description of the track;
    /// for a subtitle it says nothing, because a subtitle ends when the talking
    /// does.
    /// </summary>
    private static bool Believable(MediaStream before) =>
        before.Kind is StreamKind.Video or StreamKind.Audio || before.SecondsMeasured;

    private static string Minutes(double seconds) =>
        $"{(int)seconds / 60}:{(int)seconds % 60:00}";
}
