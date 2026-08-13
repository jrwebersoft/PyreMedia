namespace PyreMedia.Core.Media;

/// <summary>
/// What a remuxed file must still be, whichever tool wrote it.
///
/// Shared between the ffmpeg and mkvmerge executors because the question is
/// identical and writing it twice is how it drifts. That is not a theory here:
/// the duration check had already been fixed in one and left wrong in the
/// other, and the stale copy rejected a correct remux of a real film. The same
/// audit found this pair identical today and a size floor present in one and
/// missing from the other.
/// </summary>
public static class OutputCheck
{
    /// <summary>
    /// Whether Dolby Vision survived, or why it did not.
    ///
    /// The quiet failure. The picture still plays, in HDR10, and nothing looks
    /// broken until somebody notices DV never engages. The RPU lives in the
    /// bitstream and survives a stream copy, but the container-level
    /// configuration record is what a player actually looks for, and a muxer
    /// that does not carry it across drops DV without a word.
    /// </summary>
    public static string? DolbyVisionLost(MediaInfo source, MediaInfo output)
    {
        var before = source.Video.FirstOrDefault(v => v.IsDolbyVision);
        if (before is null) return null;

        var after = output.Video.FirstOrDefault(v => v.IsDolbyVision);

        if (after is null)
            return $"Dolby Vision was lost ({before.DvLabel} in the original, none in the output)";

        if (before.DvProfile != after.DvProfile)
            return $"Dolby Vision profile changed from {before.DvProfile} to {after.DvProfile}";

        if (before.DvHasEnhancementLayer && !after.DvHasEnhancementLayer)
            return "the Dolby Vision enhancement layer was lost";

        return null;
    }

    /// <summary>
    /// Whether the output is implausibly small for what was kept.
    ///
    /// Two separate floors, and the general one was missing from the mkvmerge
    /// path entirely - a truncated or empty output would have been caught only
    /// by the duration check, and only if the container still declared one.
    ///
    /// MVC gets a stricter floor because losing the dependent view roughly
    /// halves the video payload while leaving a file that plays perfectly well
    /// in 2D, which is the same shape of silent loss as Dolby Vision.
    /// </summary>
    public static string? TooSmall(string path, MediaInfo source, bool hasMvc)
    {
        long size;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "the output is not there";
            size = info.Length;
        }
        catch { return null; }

        if (size < 1024) return "output is suspiciously small";

        if (source.SizeBytes <= 0) return null;

        // Dropping tracks is the point, so a smaller file is expected and only
        // a collapse is evidence of anything. A tenth of the original means
        // the video went, whatever the track counts say.
        var floor = hasMvc ? 0.55 : 0.10;

        return size < source.SizeBytes * floor
            ? $"output is {size * 100 / source.SizeBytes}% of the original"
              + (hasMvc ? " - the MVC view may have been lost" : " - the video may have been lost")
            : null;
    }
}
