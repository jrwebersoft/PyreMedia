using PyreMedia.Core.Models;

namespace PyreMedia.Core.Organizing;

/// <summary>Where in a season a disc's titles sit, and how sure we are.</summary>
public sealed class Placement
{
    /// <summary>The episode the first kept title is, or null when it could not be told.</summary>
    public Episode? StartsAt { get; init; }

    /// <summary>Every episode the disc appears to hold, in order.</summary>
    public List<Episode> Episodes { get; init; } = [];

    /// <summary>How many equally good positions there were. One is the useful answer.</summary>
    public int Candidates { get; init; }

    public bool Certain => StartsAt is not null && Candidates == 1;

    /// <summary>What to say about it, or null when there is nothing to add.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Working out which part of a season a disc holds, from how long its episodes
/// run.
///
/// Disc order tells you the sequence and not where it starts. Rip disc three of
/// four and every title is numbered from episode one, which is wrong in a way
/// that looks entirely right - the files play, the names read properly, and the
/// error is a constant offset nobody notices until they try to watch in order.
///
/// The runtimes give the offset. A season is rarely uniform: 23, 21, 22, 21,
/// 20, 21, 21, 20, 19 is a real TMDb season, and a run of six consecutive
/// values from it is a recognisable shape even rounded to whole minutes.
/// Sliding the disc's own durations along the season and looking for the one
/// place they fit is enough to say which disc you are holding.
///
/// Two things keep it honest. Providers publish whole minutes - TMDb's are real
/// and vary; TVmaze publishes the broadcast slot, so a season of half-hour
/// television reads as 30, 30, 30, 30 and fits everywhere equally. And a series
/// cut to a fixed slot genuinely has no shape to match. Both come out as "more
/// than one position fits", which is the truthful answer and is treated as one:
/// nothing is placed, and the caller is told why.
/// </summary>
public static class DiscPlacement
{
    /// <summary>
    /// How far a title may be from its published runtime and still agree, in
    /// minutes.
    ///
    /// Generous on purpose. The published figure is rounded, a rip includes or
    /// excludes the closing logo depending on the master, and a syndicated cut
    /// runs a minute short of the broadcast one.
    /// </summary>
    private const double Tolerance = 1.5;

    /// <summary>
    /// How many times better than the runner-up the best position must be.
    ///
    /// Relative rather than a fixed margin, which was the first attempt and was
    /// wrong: a margin of half a minute per title widens with the number of
    /// titles, so on a five-title disc the acceptable band was nearly four
    /// minutes wide and swallowed two rival positions that fit far worse. Best
    /// 1.4 against a runner-up of 3.0 is a clear answer; scaled tolerance said
    /// it was a three-way tie.
    /// </summary>
    private const double Decisive = 1.8;

    /// <summary>
    /// Place a disc's titles within a season.
    /// </summary>
    /// <param name="episodes">The season's episodes, in order.</param>
    /// <param name="titles">The titles taken to be episodes, in disc order.</param>
    public static Placement Find(IReadOnlyList<Episode> episodes, IReadOnlyList<RipTitle> titles)
    {
        if (episodes.Count == 0 || titles.Count == 0 || titles.Count > episodes.Count)
            return new Placement { Note = null };

        var published = episodes.Select(e => (double?)e.RuntimeMinutes).ToList();

        if (published.Any(r => r is null or <= 0))
        {
            return new Placement
            {
                Note = "The episode list has no runtimes, so there is no way to tell which "
                     + "part of the season this disc holds. Numbered from the first episode."
            };
        }

        // A season where every episode is published as the same length carries
        // no shape to match - either it really is that uniform, or the provider
        // is publishing the broadcast slot rather than the running time.
        if (published.Select(r => r!.Value).Distinct().Count() == 1)
        {
            return new Placement
            {
                Note = $"Every episode of this season is listed as {published[0]!.Value:0} minutes, "
                     + "so the runtimes cannot say which disc this is. Numbered from the first "
                     + "episode - check that against what you ripped."
            };
        }

        var mine = titles.Select(t => t.Seconds / 60.0).ToList();

        var scores = new List<(int Offset, double Error)>();

        for (var offset = 0; offset + mine.Count <= episodes.Count; offset++)
        {
            var error = 0.0;

            for (var i = 0; i < mine.Count; i++)
                error += Math.Abs(mine[i] - published[offset + i]!.Value);

            scores.Add((offset, error));
        }

        if (scores.Count == 0) return new Placement();

        var ranked = scores.OrderBy(s => s.Error).ToList();
        var best = ranked[0];

        // Every title has to actually agree, not merely agree better than the
        // alternatives. A disc from a season the provider has wrong should find
        // nothing rather than the least bad thing.
        if (best.Error > mine.Count * Tolerance)
        {
            return new Placement
            {
                Note = "These titles do not match the runtimes of any run of episodes in this "
                     + "season. Numbered from the first episode - worth checking, as it may be "
                     + "the wrong season or a disc holding something else."
            };
        }

        // Nothing to be second to: one possible position is the answer by
        // definition, having already passed the test above.
        if (ranked.Count > 1 && ranked[1].Error < best.Error * Decisive)
        {
            var tied = ranked.Count(s => s.Error < best.Error * Decisive);

            return new Placement
            {
                Candidates = tied,
                Note = $"{tied} different runs of episodes fit these runtimes about as well as "
                     + "each other, so which disc this is cannot be told from them. Numbered "
                     + "from the first episode."
            };
        }

        var placed = episodes.Skip(best.Offset).Take(mine.Count).ToList();

        return new Placement
        {
            StartsAt = placed[0],
            Episodes = placed,
            Candidates = 1,
            Note = best.Offset == 0
                ? null
                : $"These runtimes match episodes {placed[0].Number} to {placed[^1].Number} "
                  + "rather than the start of the season, so that is where they have been "
                  + "numbered from."
        };
    }
}
