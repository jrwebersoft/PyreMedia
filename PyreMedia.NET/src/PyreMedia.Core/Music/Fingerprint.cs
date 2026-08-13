using System.Diagnostics;
using System.Numerics;

namespace PyreMedia.Core.Music;

/// <summary>
/// What the audio itself says, when the tags are not enough.
///
/// Chromaprint reduces a recording to a sequence of 32-bit hashes, roughly one
/// every 0.124 seconds, each summarising the spectrum around that moment. Two
/// files of one recording produce near-identical sequences whatever they were
/// encoded with; two different recordings of one song do not, however alike
/// their titles and durations look.
///
/// This exists because titles and durations are not enough, and the gap is not
/// theoretical. Measured against 418 real collisions the tag-and-length rules
/// agreed with the audio 95.2% of the time. The other 4.8% split three ways:
/// 17 duplicates missed because the titles were spelt differently, 2 questions
/// asked that needn't have been, and one pair - Jewel's "Life Uncommon", two
/// files of identical 4:58 duration that share only 60.5% of their audio - where
/// a file would have been deleted as a copy of something it is not.
///
/// No API key and no network: ffmpeg carries the chromaprint muxer, so this is
/// a local computation on a file already on disk.
/// </summary>
public static class Fingerprint
{
    /// <summary>
    /// The share of agreeing bits above which two files are one recording.
    ///
    /// Calibrated on pairs whose answer was already known. Chance is 50%, not
    /// zero - unrelated 32-bit hashes agree on half their bits by definition.
    /// Two encodings of one rip scored 81.8%, 84.7% and 95.0%; two different
    /// songs scored 52.7% and 47.5%. Nothing observed has ever landed between
    /// 61% and 81%, so the exact cut matters far less than it looks: 70% sits in
    /// the middle of an empty gap.
    /// </summary>
    public const double SameRecording = 0.70;

    /// <summary>
    /// Below this a file is too short, too damaged or too silent to have an
    /// opinion worth acting on. 100 hashes is about twelve seconds.
    /// </summary>
    private const int Usable = 100;

    /// <summary>
    /// How far two recordings may be offset and still be compared, in hashes.
    /// 150 is a little under nineteen seconds, which covers the leading silence
    /// and count-ins that differ between one rip and another.
    /// </summary>
    private const int Drift = 150;

    /// <summary>
    /// The fingerprint of one file, or an empty array if it could not be taken.
    ///
    /// Empty means "no opinion", never "different". Every caller must treat it
    /// that way: ffmpeg may be absent, the file may be unreadable, and neither
    /// is evidence about the audio.
    /// </summary>
    public static uint[] Of(string path, TimeSpan? timeout = null)
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
                "-hide_banner", "-v", "error", "-i", path,
                // Fingerprints are only comparable at one sample rate and channel
                // count, so everything is resampled to the same shape first. -vn
                // drops embedded cover art, which would otherwise be decoded as a
                // video stream and fail the muxer.
                "-vn", "-ac", "2", "-ar", "44100", "-af", "aresample=async=1",
                "-f", "chromaprint", "-fp_format", "raw", "-"
            }) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return [];

            using var memory = new MemoryStream();

            // stderr is drained on its own thread throughout. A process whose
            // error pipe fills while nobody is reading it blocks forever, and
            // ffmpeg is talkative about the malformed files in an old library -
            // exactly the ones worth fingerprinting.
            var drain = process.StandardError.ReadToEndAsync();
            process.StandardOutput.BaseStream.CopyTo(memory);

            if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [];
            }

            drain.Wait(TimeSpan.FromSeconds(5));

            var bytes = memory.ToArray();
            var hashes = new uint[bytes.Length / 4];

            for (var i = 0; i < hashes.Length; i++)
                hashes[i] = BitConverter.ToUInt32(bytes, i * 4);

            return hashes;
        }
        catch
        {
            // ffmpeg missing, or the file unreadable. Silence rather than a
            // wrong answer - the tag rules still apply when this says nothing.
            return [];
        }
    }

    /// <summary>
    /// The same fingerprint in the compressed base64 form the AcoustID service
    /// expects, or "" if it could not be taken.
    ///
    /// A different format, not a different fingerprint. The raw hashes above
    /// are what local comparison needs - two sequences lined up and their bits
    /// counted - while AcoustID's index is built on the compressed encoding
    /// that fpcalc emits. Sending it raw returns no matches and no error, which
    /// is the least helpful failure available.
    /// </summary>
    public static string Base64(string path, TimeSpan? timeout = null)
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
                "-hide_banner", "-v", "error", "-i", path,
                "-vn", "-ac", "2", "-ar", "44100", "-af", "aresample=async=1",
                "-f", "chromaprint", "-fp_format", "base64", "-"
            }) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return "";

            var text = process.StandardOutput.ReadToEndAsync();
            var drain = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "";
            }

            drain.Wait(TimeSpan.FromSeconds(5));
            text.Wait(TimeSpan.FromSeconds(5));

            return text.IsCompletedSuccessfully ? text.Result.Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>Whether ffmpeg is on the path and carries the chromaprint muxer.</summary>
    public static bool Available()
    {
        try
        {
            var psi = new ProcessStartInfo(MusicTools.Ffmpeg, "-hide_banner -muxers")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return false;

            var muxers = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);

            return muxers.Contains("chromaprint", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// The share of bits two fingerprints agree on at their best alignment,
    /// from 0 to 1. Returns null when either is too short to judge.
    ///
    /// The alignment search is the point. Two rips of one track rarely start at
    /// the same instant - one has a half-second of leading silence the other
    /// trimmed - and compared head-to-head they would score near chance despite
    /// being the same recording.
    /// </summary>
    public static double? Agreement(uint[] a, uint[] b)
    {
        if (a.Length < Usable || b.Length < Usable) return null;

        var best = 0.0;

        for (var offset = -Drift; offset <= Drift; offset++)
        {
            var aStart = offset < 0 ? -offset : 0;
            var bStart = offset > 0 ? offset : 0;
            var count = Math.Min(a.Length - aStart, b.Length - bStart);

            // Too small an overlap scores highly by luck. A short tail of one
            // track matching the intro of another is not a match.
            if (count < 50) continue;

            var differing = 0;
            for (var i = 0; i < count; i++)
                differing += BitOperations.PopCount(a[aStart + i] ^ b[bStart + i]);

            best = Math.Max(best, 1.0 - differing / (32.0 * count));
        }

        return best;
    }
}

/// <summary>
/// Fingerprints already taken, and what they say about a set of files.
///
/// Kept separate from <see cref="Fingerprint"/> because taking a fingerprint
/// costs about four tenths of a second and reading one costs nothing. The
/// expensive part happens once, in parallel, over only the files that need it;
/// everything downstream consults the result.
/// </summary>
public sealed class FingerprintSet
{
    private readonly Dictionary<string, uint[]> _prints;

    public FingerprintSet(IEnumerable<KeyValuePair<string, uint[]>> prints) =>
        _prints = new Dictionary<string, uint[]>(prints, StringComparer.OrdinalIgnoreCase);

    /// <summary>An empty set, which has an opinion about nothing.</summary>
    public static readonly FingerprintSet None = new([]);

    public int Count => _prints.Count;

    /// <summary>
    /// Take fingerprints of every file given, in parallel, and collect them.
    ///
    /// Skips anything already held, so this can be called repeatedly across a
    /// run without paying twice for the same file.
    /// </summary>
    public static async Task<FingerprintSet> TakeAsync(
        IEnumerable<string> paths,
        FingerprintSet? existing = null,
        IProgress<int>? progress = null,
        CancellationToken cancel = default)
    {
        var prints = existing is null
            ? new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, uint[]>(existing._prints, StringComparer.OrdinalIgnoreCase);

        var todo = paths.Where(p => !prints.ContainsKey(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (todo.Count == 0) return new FingerprintSet(prints);

        var taken = new System.Collections.Concurrent.ConcurrentDictionary<string, uint[]>(
            StringComparer.OrdinalIgnoreCase);
        var done = 0;

        await Parallel.ForEachAsync(todo,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancel },
            async (path, ct) =>
            {
                taken[path] = await Task.Run(() => Fingerprint.Of(path), ct);
                progress?.Report(Interlocked.Increment(ref done));
            });

        foreach (var (path, print) in taken) prints[path] = print;
        return new FingerprintSet(prints);
    }

    /// <summary>
    /// Whether every file in the set is the same recording. Null when any of
    /// them could not be fingerprinted - no opinion, so the tag rules stand.
    /// </summary>
    public bool? SameRecording(IReadOnlyList<TrackTags> files) =>
        Lowest(files) is { } worst ? worst >= Fingerprint.SameRecording : null;

    /// <summary>
    /// The weakest agreement between any pair in the set, or null if any file
    /// is unfingerprinted.
    ///
    /// The weakest and not the average, because a set is only one recording if
    /// every member of it is. Three files where two match perfectly and the
    /// third matches neither is not a set of duplicates.
    /// </summary>
    public double? Lowest(IReadOnlyList<TrackTags> files)
    {
        var worst = 1.0;

        for (var i = 0; i < files.Count; i++)
            for (var j = i + 1; j < files.Count; j++)
            {
                if (Agreement(files[i].Path, files[j].Path) is not { } score) return null;
                worst = Math.Min(worst, score);
            }

        return worst;
    }

    /// <summary>Agreement between two files, or null if either is unavailable.</summary>
    public double? Agreement(string a, string b) =>
        _prints.TryGetValue(a, out var left) && _prints.TryGetValue(b, out var right)
            ? Fingerprint.Agreement(left, right)
            : null;

    /// <summary>True when this file was fingerprinted and the result is usable.</summary>
    public bool Has(string path) => _prints.TryGetValue(path, out var p) && p.Length >= 100;
}
