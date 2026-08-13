using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PyreMedia.Core.Music;

/// <summary>How loud one file is, and what gain would even it out.</summary>
/// <param name="Lufs">Integrated loudness, in LUFS. Quiet is more negative.</param>
/// <param name="Peak">True peak as a fraction of full scale; 1.0 is 0 dBFS.</param>
public sealed record Loudness(double Lufs, double Peak)
{
    /// <summary>
    /// The gain that brings this file to the reference level, in dB.
    ///
    /// ReplayGain 2.0 references -18 LUFS, which is what every player written
    /// since about 2010 expects. The older ReplayGain 1.0 used 89 dB SPL and a
    /// different loudness model entirely; the tag names are the same, which is
    /// unhelpful, but the difference in practice is small and consistent.
    /// </summary>
    public double Gain => Reference - Lufs;

    public const double Reference = -18.0;

    public string GainTag => $"{Gain.ToString("0.00", CultureInfo.InvariantCulture)} dB";
    public string PeakTag => Peak.ToString("0.000000", CultureInfo.InvariantCulture);
}

/// <summary>
/// Measures loudness so players can even out a library ripped over thirty years
/// at wildly different levels.
///
/// Nothing here touches the audio. ReplayGain is a pair of numbers written into
/// the tags saying how much quieter or louder a file is than the reference, and
/// the player applies that at playback - so it is exactly reversible by
/// deleting the tags, and no sample is ever re-encoded. The other approach,
/// which some tools take, rewrites the audio itself; that one really can
/// degrade a file and is not implemented here on purpose.
///
/// Measured with ffmpeg's ebur128 filter, which implements the same standard
/// the broadcast world uses, so the numbers are comparable with anything else
/// that quotes LUFS.
/// </summary>
public static class ReplayGain
{
    /// <summary>
    /// Measure one file. Null when ffmpeg is missing or the file will not
    /// decode - no opinion, never a guess.
    /// </summary>
    public static Loudness? Measure(string path, CancellationToken cancel = default)
    {
        try
        {
            var psi = new ProcessStartInfo(MusicTools.Ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in new[]
            {
                "-hide_banner", "-nostats", "-i", path,
                // peak=true asks for the true-peak figure as well as the
                // loudness; without it the summary has no peak line at all.
                "-af", "ebur128=peak=true",
                "-f", "null", "-"
            }) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return null;

            // ebur128 reports on stderr, which has to be drained while the
            // process runs or a long track fills the pipe and deadlocks.
            var output = process.StandardError.ReadToEndAsync(cancel);

            if (!process.WaitForExit(600_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            output.Wait(TimeSpan.FromSeconds(5), cancel);
            return Parse(output.IsCompletedSuccessfully ? output.Result : "");
        }
        catch { return null; }
    }

    /// <summary>
    /// The album figure for a set of tracks already measured.
    ///
    /// An approximation, and worth being plain about which one. Done properly,
    /// album gain is the gated loudness of every track played end to end, which
    /// needs a second pass over the whole album - an hour of decoding to refine
    /// a number by a fraction of a decibel. This averages the tracks' energy
    /// weighted by their length instead, which is what most taggers do and lands
    /// within a few tenths of a dB of the exact figure. The point of album gain
    /// is that a quiet track on a loud record stays quiet, and this preserves
    /// that exactly.
    /// </summary>
    public static Loudness? Album(IEnumerable<(Loudness Loudness, double Seconds)> tracks)
    {
        var measured = tracks.Where(t => t.Seconds > 0).ToList();
        if (measured.Count == 0) return null;

        var total = measured.Sum(t => t.Seconds);

        // Loudness is logarithmic, so the averaging happens in the linear domain
        // and comes back afterwards. Averaging the decibel figures directly
        // would let one very quiet track drag the whole album down.
        var energy = measured.Sum(t => Math.Pow(10, t.Loudness.Lufs / 10) * t.Seconds) / total;

        return new Loudness(10 * Math.Log10(energy), measured.Max(t => t.Loudness.Peak));
    }

    /// <summary>The tags for a track, given its own figure and its album's.</summary>
    public static TagEdit Tags(Loudness track, Loudness? album) => new(
        TrackGain: track.GainTag,
        TrackPeak: track.PeakTag,
        AlbumGain: album?.GainTag,
        AlbumPeak: album?.PeakTag);

    internal static Loudness? Parse(string report)
    {
        // The summary block at the end, not the running per-second lines. Both
        // carry an "I:" figure and only the last one is the whole file.
        var summary = report.LastIndexOf("Integrated loudness", StringComparison.Ordinal);
        if (summary < 0) return null;

        var tail = report[summary..];

        var lufs = Number(IntegratedLine, tail);
        if (lufs is null) return null;

        // dBTP as a fraction of full scale. A missing peak is not fatal - the
        // gain is the useful half - but a wrong one would have a player
        // needlessly turn a track down, so absent means 1.0 rather than 0.
        var peakDb = Number(PeakLine, tail);
        var peak = peakDb is null ? 1.0 : Math.Pow(10, peakDb.Value / 20);

        return new Loudness(lufs.Value, peak);
    }

    private static double? Number(Regex pattern, string text)
    {
        var match = pattern.Match(text);

        return match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                               CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static readonly Regex IntegratedLine = new(
        @"I:\s*(-?\d+(?:\.\d+)?)\s*LUFS",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex PeakLine = new(
        @"Peak:\s*(-?\d+(?:\.\d+)?)\s*dBFS",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
