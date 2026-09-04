namespace PyreMedia.Core.Models;

/// <summary>The kinds of artwork Kodi looks for beside a file.</summary>
public enum ArtKind
{
    Poster,
    Fanart,

    /// <summary>Transparent logo of the title. Always a PNG - the point is the transparency.</summary>
    ClearLogo,

    /// <summary>
    /// A frame from one episode, written as "&lt;video&gt;-thumb.jpg".
    ///
    /// The only per-episode image there is. A poster describes a series and is
    /// the same for all of it; a thumb describes the episode it sits beside. Put
    /// a poster in this slot and every episode in a season looks identical in
    /// the one place a picture was supposed to tell them apart.
    /// </summary>
    EpisodeThumb
}

/// <summary>One image a provider offers, before anything is downloaded.</summary>
public sealed class ArtworkOption
{
    public required ArtKind Kind { get; init; }

    /// <summary>Full-size image, which is what gets written.</summary>
    public required string Url { get; init; }

    /// <summary>Small version, for showing a wall of choices without fetching megabytes.</summary>
    public required string ThumbUrl { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Two-letter code, or null for a language-neutral image.</summary>
    public string? Language { get; init; }

    /// <summary>The provider's own popularity score, used only for ordering.</summary>
    public double Score { get; init; }

    public string Dimensions => Width > 0 && Height > 0 ? $"{Width} x {Height}" : "";

    public string LanguageLabel => string.IsNullOrWhiteSpace(Language) ? "no text" : Language.ToUpperInvariant();
}

/// <summary>Everything a provider has for one title.</summary>
public sealed class ArtworkSet
{
    public List<ArtworkOption> Posters { get; init; } = [];
    public List<ArtworkOption> Fanart { get; init; } = [];
    public List<ArtworkOption> Logos { get; init; } = [];

    public bool IsEmpty => Posters.Count == 0 && Fanart.Count == 0 && Logos.Count == 0;

    public List<ArtworkOption> For(ArtKind kind) => kind switch
    {
        ArtKind.Poster => Posters,
        ArtKind.Fanart => Fanart,
        _ => Logos
    };
}
