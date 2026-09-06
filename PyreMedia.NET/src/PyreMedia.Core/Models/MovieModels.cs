namespace PyreMedia.Core.Models;

public sealed class MovieSearchResult
{
    public required string TmdbId { get; init; }
    public required string Title { get; init; }
    public string? Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }

    public string Display => string.IsNullOrEmpty(Year) ? Title : $"{Title} ({Year})";
}

public sealed class Movie
{
    public required string TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public required string Title { get; init; }
    public string? Overview { get; init; }
    public string? ReleaseDate { get; init; }

    /// <summary>The line on the poster, where the source has one.</summary>
    public string? Tagline { get; init; }

    /// <summary>Running time in whole minutes, as the provider states it.</summary>
    public int? RuntimeMinutes { get; init; }

    /// <summary>
    /// The certificate - "PG-13", "15". Taken from one country's board rather
    /// than averaged, because a rating is a legal judgement made somewhere
    /// specific and blending two of them produces a rating nobody issued.
    /// </summary>
    public string? Certification { get; init; }

    public Rating? Rating { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Studios { get; init; } = [];
    public IReadOnlyList<string> Countries { get; init; } = [];

    /// <summary>Billed cast, in billing order.</summary>
    public IReadOnlyList<Person> Cast { get; init; } = [];

    public IReadOnlyList<Person> Directors { get; init; } = [];
    public IReadOnlyList<Person> Writers { get; init; } = [];

    private readonly string? _year;

    /// <summary>
    /// Release year. Falls back to the front of <see cref="ReleaseDate"/> when it
    /// wasn't set separately - the two have to agree, and a movie whose year goes
    /// missing is named "The Matrix" instead of "The Matrix (1999)", which is both
    /// wrong and liable to collide with another film of the same name.
    /// <see cref="TvShow"/> derives its year the same way.
    /// </summary>
    public string? Year
    {
        get => !string.IsNullOrWhiteSpace(_year) ? _year
             : ReleaseDate is { Length: >= 4 } d && d[..4].All(char.IsDigit) ? d[..4]
             : null;
        init => _year = value;
    }
}

/// <summary>Title and year guessed from a movie filename or folder name.</summary>
public readonly record struct MovieRef(string Title, string? Year)
{
    public override string ToString() => Year is null ? Title : $"{Title} ({Year})";
}
