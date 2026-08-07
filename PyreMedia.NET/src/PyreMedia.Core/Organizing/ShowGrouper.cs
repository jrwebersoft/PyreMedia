using System.Text;
using PyreMedia.Core.Naming;

namespace PyreMedia.Core.Organizing;

/// <summary>Items that look like seasons of one show.</summary>
public sealed class ShowGroup
{
    public required string Title { get; init; }

    /// <summary>The year, when every member agrees on one.</summary>
    public string? Year { get; init; }

    public required List<MediaItem> Items { get; init; }

    /// <summary>Season numbers found across the group, in order.</summary>
    public required List<int> Seasons { get; init; }

    /// <summary>
    /// Set when the grouping might be wrong and a person should look. Never a
    /// reason to refuse the group - only to say so.
    /// </summary>
    public string? Caution { get; init; }

    public int FileCount => Items.Sum(i => i.Files.Count);

    public string SeasonSummary => Seasons.Count switch
    {
        0 => "no season numbers",
        1 => $"season {Seasons[0]}",
        _ when Seasons.SequenceEqual(Enumerable.Range(Seasons[0], Seasons.Count))
            => $"seasons {Seasons[0]}-{Seasons[^1]}",
        _ => "seasons " + string.Join(", ", Seasons)
    };

    public string Describe() =>
        $"{Title}{(Year is null ? "" : $" ({Year})")} - {Items.Count} folders, {SeasonSummary}";
}

/// <summary>
/// Spots the same show split across several library entries.
///
/// A season per folder is how most TV arrives - eight folders called
/// "Will.&amp;.Grace.S01..." through "S08" are eight entries to match by hand, and
/// every one of them is the same show. Grouping them means matching once.
///
/// The thing this must not do is merge two shows that share a name. Battlestar
/// Galactica 1978 and Battlestar Galactica 2004 are different series with the
/// same title and overlapping season numbers, and quietly treating them as one
/// would renumber a library wrongly and silently. So: differing years separate
/// outright, and a season number appearing twice is reported rather than
/// smoothed over.
/// </summary>
public static class ShowGrouper
{
    /// <summary>
    /// Groups worth telling the user about - two or more entries that look like
    /// one show. Single entries are not groups and are left out.
    /// </summary>
    public static List<ShowGroup> Group(IEnumerable<MediaItem> items)
    {
        var tv = items.Where(i => i.Kind == MediaKind.TvEpisode && !i.IsLooseFile).ToList();

        var byTitle = tv
            .GroupBy(i => Normalize(i.SearchTitle), StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        var groups = new List<ShowGroup>();

        foreach (var titleGroup in byTitle)
        {
            // A year in the name is the one hard signal that two same-named
            // things are different shows. Entries without a year join the year
            // group only when there is exactly one to join.
            foreach (var yearGroup in SplitByYear(titleGroup))
            {
                if (yearGroup.Items.Count < 2) continue;
                groups.Add(yearGroup);
            }
        }

        return [.. groups.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Replace each confident group with a single entry covering all its files,
    /// so a show split over eight folders is matched once instead of eight times.
    ///
    /// Only groups with nothing to be cautious about are combined. Anything that
    /// might be two shows wearing one name is left exactly as it was and
    /// reported instead - "Dexter" and "Dexter: New Blood" are different series,
    /// as are the two Battlestar Galacticas and every Star Trek after the first.
    /// Getting that wrong would renumber one series against another's episode
    /// list, so the doubt has to resolve to "leave it alone".
    /// </summary>
    public static List<MediaItem> Combine(IReadOnlyList<MediaItem> items, out List<ShowGroup> combined)
    {
        combined = [];

        var groups = Group(items).Where(g => g.Caution is null).ToList();
        if (groups.Count == 0) return [.. items];

        var absorbed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MediaItem>();

        foreach (var g in groups)
        {
            foreach (var i in g.Items) absorbed.Add(i.Path);
            combined.Add(g);
        }

        // Keep everything that wasn't absorbed, in the order it came.
        foreach (var i in items)
            if (!absorbed.Contains(i.Path))
                result.Add(i);

        foreach (var g in groups)
            result.Add(Merge(g));

        return [.. result.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>One entry standing for every folder in the group.</summary>
    private static MediaItem Merge(ShowGroup g)
    {
        var files = g.Items.SelectMany(i => i.Files)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                           .ToList();

        // The folder they all sit in. Not the show's own folder - which is why a
        // combined entry never has its containing folder renamed.
        var parent = g.Items
            .Select(i => Path.GetDirectoryName(i.Path) ?? i.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .First();

        var first = g.Items[0];

        return new MediaItem
        {
            Path = parent,
            DisplayName = g.Title,
            Kind = MediaKind.TvEpisode,
            Files = files,
            SearchTitle = g.Title,
            SearchYear = g.Year,
            MainFile = files.FirstOrDefault() ?? first.MainFile,
            IsLooseFile = false,
            CombinedFrom = [.. g.Items.Select(i => i.Path)],
            Root = first.Root,

            // Any one of them will do - they are the same show, and an id from a
            // sidecar beats anything a filename could offer.
            Nfo = g.Items.Select(i => i.Nfo).FirstOrDefault(n => n?.HasAnyId == true)
                  ?? g.Items.Select(i => i.Nfo).FirstOrDefault(n => n is not null)
        };
    }

    private static List<ShowGroup> SplitByYear(IEnumerable<MediaItem> items)
    {
        var all = items.ToList();
        var years = all.Select(i => i.SearchYear).Where(y => !string.IsNullOrWhiteSpace(y))
                       .Distinct().ToList();

        // Everything agrees, or nothing says: one group.
        if (years.Count <= 1)
            return [Build(all, years.FirstOrDefault())];

        // Two or more years among entries sharing a title - the Battlestar case.
        // Each year is its own show; entries with no year of their own can't be
        // assigned to either, so they stand alone and say why.
        var result = new List<ShowGroup>();

        foreach (var year in years)
        {
            var members = all.Where(i => i.SearchYear == year).ToList();
            if (members.Count > 0) result.Add(Build(members, year));
        }

        var undated = all.Where(i => string.IsNullOrWhiteSpace(i.SearchYear)).ToList();

        if (undated.Count > 1)
        {
            result.Add(Build(undated, null,
                $"There are also entries dated {string.Join(" and ", years)} under this title. "
                + "These carry no year, so which series they belong to can't be told from the name."));
        }

        return result;
    }

    private static ShowGroup Build(List<MediaItem> members, string? year, string? caution = null)
    {
        var seasons = members
            .SelectMany(SeasonsIn)
            .Distinct()
            .Order()
            .ToList();

        // The same season twice under one title is either a duplicate or two
        // different shows. Either way it wants human eyes, not a silent merge.
        var repeated = members
            .SelectMany(m => SeasonsIn(m).Distinct().Select(s => (Item: m, Season: s)))
            .GroupBy(x => x.Season)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order()
            .ToList();

        if (caution is null && repeated.Count > 0)
        {
            caution = repeated.Count == 1
                ? $"Season {repeated[0]} appears in more than one of these - they may be "
                  + "duplicates, or two different series sharing a name."
                : $"Seasons {string.Join(", ", repeated)} each appear more than once - they may "
                  + "be duplicates, or two different series sharing a name.";
        }

        return new ShowGroup
        {
            Title = BestTitle(members),
            Year = year,
            Items = members,
            Seasons = seasons,
            Caution = caution
        };
    }

    /// <summary>The season numbers an entry covers, read from its files.</summary>
    private static IEnumerable<int> SeasonsIn(MediaItem item)
    {
        foreach (var f in item.Files)
        {
            if (EpisodeMatcher.Parse(Path.GetFileName(f)) is { Season: { } s } && s > 0)
                yield return s;
        }
    }

    /// <summary>
    /// The tidiest spelling among the members. They differ in case and
    /// punctuation, and the one to show is the one a person would have typed.
    /// </summary>
    private static string BestTitle(List<MediaItem> members) =>
        members.Select(m => m.SearchTitle)
               .Where(t => !string.IsNullOrWhiteSpace(t))
               .OrderByDescending(t => t.Count(char.IsUpper))
               .ThenBy(t => t.Length)
               .FirstOrDefault()
        ?? members[0].DisplayName;

    /// <summary>
    /// A comparable form of a title: case, punctuation and the difference
    /// between "&amp;" and "and" are all spelling, not identity.
    /// </summary>
    private static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";

        var sb = new StringBuilder(title.Length);

        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == '&') sb.Append("and");
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
            // everything else - dots, dashes, colons, apostrophes - is noise
        }

        // "the" at the front is dropped as often as it is kept.
        var s = string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.StartsWith("the ", StringComparison.Ordinal) ? s[4..] : s;
    }
}
