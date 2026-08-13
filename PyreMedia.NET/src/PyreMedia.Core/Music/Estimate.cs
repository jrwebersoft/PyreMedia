namespace PyreMedia.Core.Music;

/// <summary>
/// How long the rest of a long job will take, from how the first part went.
///
/// Kept out of the window because the arithmetic is where these go wrong and
/// it is worth being able to test it. A progress bar that lies is worse than
/// none: somebody who is told four minutes and waits twenty stops believing
/// the number, and then stops believing the one that would have told them
/// something had gone wrong.
///
/// So it declines to guess early, smooths the rate rather than using the last
/// sample, and never counts down past zero.
/// </summary>
public sealed class Estimate(int total)
{
    private readonly List<(DateTime At, int Done)> _samples = [];

    /// <summary>
    /// How many samples before a figure is offered at all.
    ///
    /// The first few items of any batch are unrepresentative - a cold file
    /// cache, a spun-down disk, the first ffmpeg process paying for its own
    /// startup - and extrapolating from two of them produces the wildly wrong
    /// first estimate that everybody has learnt to ignore.
    /// </summary>
    private const int Enough = 8;

    /// <summary>
    /// How far back to measure. Recent work predicts remaining work better
    /// than the whole run does when the rate changes partway - which it does,
    /// because a library is not uniform and one folder of long files will
    /// slow everything down for a while.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    public int Total { get; } = Math.Max(0, total);
    public int Done { get; private set; }

    public double Fraction => Total == 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);

    /// <summary>Record progress. <paramref name="now"/> is for tests.</summary>
    public void Report(int done, DateTime? now = null)
    {
        Done = Math.Clamp(done, 0, Total);
        _samples.Add((now ?? DateTime.UtcNow, Done));

        // Only the window matters, and one sample before it has to survive or
        // a slow patch leaves nothing to measure between.
        var cutoff = (now ?? DateTime.UtcNow) - Window;
        var keep = _samples.FindLastIndex(s => s.At < cutoff);

        if (keep > 0) _samples.RemoveRange(0, keep);
    }

    /// <summary>
    /// How long is left, or null while there is not enough to say.
    ///
    /// Null rather than a guess, so the caller can show "working out how long
    /// this will take" instead of a number that will change by a factor of
    /// five in the next ten seconds.
    /// </summary>
    public TimeSpan? Remaining(DateTime? now = null)
    {
        if (Total == 0 || Done >= Total) return TimeSpan.Zero;
        if (_samples.Count < Enough) return null;

        var first = _samples[0];
        var last = _samples[^1];

        var elapsed = last.At - first.At;
        var moved = last.Done - first.Done;

        if (moved <= 0 || elapsed <= TimeSpan.Zero) return null;

        var perItem = elapsed.TotalSeconds / moved;
        var left = Total - Done;

        return TimeSpan.FromSeconds(Math.Max(0, perItem * left));
    }

    /// <summary>
    /// The estimate as somebody would say it: rounded honestly, and vague
    /// where being precise would be a lie. "About 4 minutes" is true for a
    /// while; "3 minutes 47 seconds" stops being true immediately.
    /// </summary>
    public string Describe(DateTime? now = null)
    {
        if (Total == 0) return "";
        if (Done >= Total) return "done";

        var left = Remaining(now);

        if (left is null) return $"{Done:N0} of {Total:N0}";

        var text = left.Value.TotalSeconds switch
        {
            < 10 => "a few seconds left",
            < 90 => $"about {Math.Round(left.Value.TotalSeconds / 10) * 10:N0} seconds left",
            < 60 * 60 => $"about {Math.Round(left.Value.TotalMinutes):N0} minutes left",
            _ => $"about {left.Value.TotalHours:F1} hours left"
        };

        return $"{Done:N0} of {Total:N0} - {text}";
    }
}
