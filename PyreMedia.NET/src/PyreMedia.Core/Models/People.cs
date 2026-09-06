namespace PyreMedia.Core.Models;

/// <summary>
/// One person a provider named, and what they did.
///
/// One type for cast and crew alike, because the only thing that differs is
/// what <see cref="Role"/> holds - a character for an actor, a job for anyone
/// else - and splitting it into two nearly identical records buys nothing.
/// </summary>
public sealed record Person
{
    public required string Name { get; init; }

    /// <summary>
    /// The character for an actor, the job for anyone else. Null where the
    /// source named the person but not what they did, which happens more often
    /// than you would expect on older titles.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Billing order, where the source gives one. Kodi sorts the cast list on
    /// it, and a cast written in the wrong order reads as a different film.
    /// </summary>
    public int? Order { get; init; }

    /// <summary>A headshot, where there is one. Kodi caches it beside the library.</summary>
    public string? ThumbUrl { get; init; }
}

/// <summary>
/// A score from one source, with the weight behind it.
///
/// The vote count travels with the value because it is the difference between
/// a film and an opinion: 8.4 from eleven people and 8.4 from forty thousand
/// are not the same number, and Kodi shows both.
/// </summary>
public sealed record Rating
{
    /// <summary>Which source said so - "themoviedb", "thetvdb".</summary>
    public required string Source { get; init; }

    public required double Value { get; init; }

    public int? Votes { get; init; }

    /// <summary>The top of the scale this value is on. Ten everywhere so far.</summary>
    public double Max { get; init; } = 10;
}
