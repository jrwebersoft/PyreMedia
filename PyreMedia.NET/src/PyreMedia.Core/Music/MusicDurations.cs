using System.Diagnostics;
using System.Globalization;

namespace PyreMedia.Core.Music;

/// <summary>
/// Measures how long tracks actually run.
///
/// Only ever asked about files whose track numbers collide. Length is what
/// separates a second copy from a second version, and that question arises for
/// about 5% of a library - probing the other 95% would cost an hour to learn
/// nothing. On the 17,168-file sample this reads 883 files in eight seconds.
/// </summary>
public static class MusicDurations
{
    /// <summary>
    /// Fill <see cref="TrackTags.Seconds"/> and <see cref="TrackTags.Kbps"/> for
    /// every track in an album whose number is claimed by more than one file.
    /// </summary>
    public static async Task FillContestedAsync(
        IEnumerable<AlbumGroup> albums,
        string? ffprobePath = null,
        IProgress<int>? progress = null,
        CancellationToken cancel = default)
    {
        var contested = albums
            .SelectMany(a => a.Tracks
                .Where(t => t.TrackNumber is > 0)
                .GroupBy(t => (t.DiscNumber ?? 1, t.TrackNumber!.Value))
                .Where(g => g.Count() > 1)
                .SelectMany(g => g))
            .Where(t => t.Seconds is null)
            .Distinct()
            .ToList();

        await FillAsync(contested, ffprobePath, progress, cancel);
    }

    public static async Task FillAsync(
        IReadOnlyList<TrackTags> tracks,
        string? ffprobePath = null,
        IProgress<int>? progress = null,
        CancellationToken cancel = default)
    {
        if (tracks.Count == 0) return;

        var done = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
            CancellationToken = cancel
        };

        await Parallel.ForEachAsync(tracks, options, async (track, token) =>
        {
            var (seconds, kbps) = await MeasureAsync(track.Path, ffprobePath, token);

            if (seconds > 0) track.Seconds = seconds;
            if (kbps > 0) track.Kbps = kbps;

            progress?.Report(Interlocked.Increment(ref done));
        });
    }

    /// <summary>
    /// Duration and bitrate for one file, or zeroes if ffprobe is not there.
    ///
    /// A missing ffprobe is not an error to raise: the caller's own rules already
    /// treat an unmeasured length as a question for a person, which is the right
    /// outcome when nothing can measure it.
    /// </summary>
    public static async Task<(double Seconds, int Kbps)> MeasureAsync(
        string path, string? ffprobePath = null, CancellationToken cancel = default)
    {
        try
        {
            // Null means "whatever the settings resolved to", which is the
            // normal case; a caller passing one explicitly is a test.
            var psi = new ProcessStartInfo(ffprobePath ?? MusicTools.Ffprobe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration,bit_rate");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=nw=1:nk=1");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process is null) return (0, 0);

            var output = await process.StandardOutput.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var seconds = lines.Length > 0
                && double.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, out var d) ? d : 0;

            var kbps = lines.Length > 1
                && int.TryParse(lines[1].Trim(), out var r) ? r / 1000 : 0;

            return (seconds, kbps);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (0, 0); }
    }
}
