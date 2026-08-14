namespace PyreMedia.Core.Organizing;

/// <summary>One ripped disc within a set, before anything has been probed.</summary>
public sealed record DiscFolder(string Path, int TitleCount, int? Number, DateTime Ripped)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Where one disc sits in a season, worked out from the whole set.</summary>
public sealed class SetPlacement
{
    /// <summary>Episodes on the discs before this one, so the offset to start from.</summary>
    public int Offset { get; init; }

    /// <summary>Which disc of how many, for saying so out loud.</summary>
    public int DiscNumber { get; init; }

    public int DiscCount { get; init; }

    public bool Certain { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// Placing a disc by counting the whole set rather than examining one folder.
///
/// A season ripped disc by disc leaves several folders side by side. Any one of
/// them, on its own, says only "here are five titles in some order" - but taken
/// together they are arithmetic. If a season has twenty-one episodes and five
/// folders hold four, four, four, four and five titles between them, then those
/// twenty-one titles are those twenty-one episodes, and the third folder holds
/// episodes nine to twelve. Nothing needs to be probed or looked up to know it.
///
/// The whole argument rests on the total being exact. Twenty-one titles for
/// twenty-one episodes means every title is an episode and no episode is
/// missing; twenty-three means two extras were ripped as well, and which two is
/// exactly what cannot be inferred by counting. So the arithmetic is offered
/// only when the sum is right, and declined out loud otherwise.
/// </summary>
public static class DiscSet
{
    /// <summary>
    /// The disc number written into a folder name, or null.
    ///
    /// Ripping software names the folder after the disc's volume label, and
    /// mastering houses usually number them: "SEASON_1_DISC_2", "BUFFY_S3_D4".
    /// When they do, that is better evidence of order than any timestamp.
    /// </summary>
    public static int? NumberIn(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;

        var m = System.Text.RegularExpressions.Regex.Match(
            folderName,
            @"(?:^|[^A-Za-z0-9])(?:DISC|DISK|D)[\s_-]?(\d{1,2})(?:$|[^0-9])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is > 0 and < 30
            ? n
            : null;
    }

    /// <summary>
    /// Put the discs of a set in order.
    ///
    /// By the number in the name where every folder has one, because that is
    /// what the disc itself says. Otherwise by when they were ripped, which
    /// assumes somebody worked through the box in order - usually true, and
    /// never treated as more than an assumption.
    /// </summary>
    public static List<DiscFolder> InOrder(IEnumerable<DiscFolder> discs)
    {
        var all = discs.ToList();

        return all.All(d => d.Number is not null)
            ? [.. all.OrderBy(d => d.Number)]
            : [.. all.OrderBy(d => d.Ripped).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Work out where one disc of a set starts.
    /// </summary>
    /// <param name="discs">Every ripped folder of the set, including this one.</param>
    /// <param name="thisDisc">The folder being placed.</param>
    /// <param name="episodesInSeason">How many episodes the season has.</param>
    public static SetPlacement Place(
        IEnumerable<DiscFolder> discs, string thisDisc, int episodesInSeason)
    {
        var ordered = InOrder(discs);

        if (ordered.Count == 0 || episodesInSeason <= 0) return new SetPlacement();

        var here = ordered.FindIndex(d =>
            string.Equals(d.Path, thisDisc, StringComparison.OrdinalIgnoreCase));

        if (here < 0) return new SetPlacement();

        // A single folder is not a set, and counting it proves nothing: five
        // titles out of twenty-one episodes says only that four discs are
        // somewhere else.
        if (ordered.Count == 1)
        {
            return new SetPlacement
            {
                DiscNumber = 1,
                DiscCount = 1,
                Note = ordered[0].TitleCount == episodesInSeason
                    ? null
                    : $"This is the only ripped disc here, holding {ordered[0].TitleCount} of the "
                      + $"season's {episodesInSeason} episodes, so there is no way to tell which "
                      + "part of the season it is. Numbered from the first episode."
            };
        }

        var total = ordered.Sum(d => d.TitleCount);

        if (total != episodesInSeason)
        {
            return new SetPlacement
            {
                DiscNumber = here + 1,
                DiscCount = ordered.Count,
                Note = $"{ordered.Count} ripped discs hold {total} titles between them, and the "
                     + $"season has {episodesInSeason} episodes. "
                     + (total > episodesInSeason
                         ? "The extra titles are trailers or a \"play all\", and which ones cannot "
                           + "be told by counting."
                         : "Some episodes are on a disc that has not been ripped.")
                     + " Numbered from the first episode of this disc's own order."
            };
        }

        var before = ordered.Take(here).Sum(d => d.TitleCount);

        var byName = ordered.All(d => d.Number is not null);

        return new SetPlacement
        {
            Offset = before,
            DiscNumber = here + 1,
            DiscCount = ordered.Count,
            Certain = true,
            Note = $"{ordered.Count} discs hold exactly the season's {episodesInSeason} episodes "
                 + $"between them, so this is disc {here + 1} and starts at episode {before + 1}. "
                 + (byName
                     ? "The discs were ordered by the numbers in their folder names."
                     : "The discs were ordered by when they were ripped, which assumes the box "
                       + "was worked through in order - worth a glance.")
        };
    }
}
