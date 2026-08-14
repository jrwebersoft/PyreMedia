namespace PyreMedia.Core.Models;

/// <summary>One candidate returned by a metadata search.</summary>
public sealed class ShowSearchResult
{
    public string? TmdbId { get; init; }

    /// <summary>TheTVDB series id. Required - the episode fetch is keyed on it.</summary>
    public required string TvdbId { get; init; }

    public required string Name { get; init; }
    public string? Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }

    public string Display => string.IsNullOrEmpty(Year) ? Name : $"{Name} ({Year})";
}

public sealed class TvShow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Overview { get; init; }
    public string? Network { get; init; }
    public string? FirstAired { get; init; }
    public List<Season> Seasons { get; init; } = [];

    /// <summary>Four-digit première year, or empty when the source didn't give one.</summary>
    public string Year =>
        FirstAired is { Length: >= 4 } fa && fa[..4].All(char.IsDigit) ? fa[..4] : string.Empty;

    public Episode? FindEpisode(int season, int episode) =>
        Seasons.FirstOrDefault(s => s.Number == season)?
               .Episodes.FirstOrDefault(e => e.Number == episode);

    public bool HasSeason(int season) => Seasons.Any(s => s.Number == season);
}

public sealed class Season
{
    public required int Number { get; init; }
    public List<Episode> Episodes { get; init; } = [];

    /// <summary>Season 0 is the specials season by long-standing convention.</summary>
    public bool IsSpecials => Number == 0;
}

public sealed class Episode
{
    public required int SeasonNumber { get; init; }
    public required int Number { get; init; }
    public required string Name { get; init; }
    public string? FirstAired { get; init; }
    public string? Overview { get; init; }
    public string? ProductionCode { get; init; }

    /// <summary>
    /// How long the episode runs, in whole minutes, where the provider says.
    ///
    /// Rounded and sometimes absent, so useless for verifying a file. What it is
    /// good for is telling one disc of a season from another: six consecutive
    /// runtimes of 23, 21, 22, 21, 20, 21 are a recognisable shape even at
    /// minute resolution, and knowing which six of a season you are holding is
    /// the one thing disc order cannot tell you.
    /// </summary>
    public int? RuntimeMinutes { get; init; }

    /// <summary>
    /// Which provider supplied this episode.
    ///
    /// Matters because a Kodi media source is scraped by one scraper at a time.
    /// Different sources can use different scrapers, and add-ons exist that
    /// combine providers - so this is a caution, not a certainty. An episode
    /// only TheTVDB knows about is named correctly on disk but may go unmatched
    /// in a library whose source scrapes TMDb.
    /// </summary>
    public string? Source { get; init; }
}

/// <summary>Season/episode numbers parsed out of a filename.</summary>
public readonly record struct EpisodeRef(int? Season, int Episode)
{
    /// <summary>
    /// Last episode of a multi-episode file, e.g. 2 for "S01E01E02". Null for
    /// an ordinary single episode.
    /// </summary>
    public int? LastEpisode { get; init; }

    /// <summary>
    /// The raw number when the filename could equally be absolute numbering -
    /// "Show - 137" reads as s1e37 under the loose rules, but is far more likely
    /// to be episode 137. The planner resolves it against real metadata.
    /// </summary>
    public int? AbsoluteCandidate { get; init; }

    public bool IsMultiEpisode => LastEpisode is { } l && l > Episode;

    /// <summary>Every episode number this file covers.</summary>
    public IEnumerable<int> Episodes =>
        IsMultiEpisode
            ? Enumerable.Range(Episode, LastEpisode!.Value - Episode + 1)
            : [Episode];

    public override string ToString() =>
        (Season, IsMultiEpisode) switch
        {
            (null, false) => $"e{Episode:00}",
            (null, true) => $"e{Episode:00}-{LastEpisode:00}",
            (_, false) => $"s{Season:00}e{Episode:00}",
            (_, true) => $"s{Season:00}e{Episode:00}-{LastEpisode:00}"
        };
}
