using System.Xml.Linq;

namespace PyreMedia.Core.Metadata;

/// <summary>
/// The provider ids an .nfo already carries, and what it is still missing.
///
/// This is what makes an unattended backfill honest. A file whose .nfo names
/// the title - by TMDb, IMDb or TheTVDB id - can be looked up with no guessing
/// at all, and a file that does not is left alone rather than matched on a
/// name that might belong to something else.
/// </summary>
public sealed record NfoIds
{
    public string? TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public string? TvdbId { get; init; }

    /// <summary>Whether anything here is enough to look the title up.</summary>
    public bool Any => TmdbId is not null || ImdbId is not null || TvdbId is not null;

    /// <summary>
    /// Read the ids out of an .nfo, in both the shapes libraries actually
    /// contain.
    ///
    /// Kodi writes &lt;uniqueid type="tmdb"&gt;. Older Kodi, Emby and Jellyfin
    /// write &lt;tmdbid&gt;, &lt;imdbid&gt; and &lt;tvdbid&gt; as plain
    /// elements, and a real shelf holds both - of 57 files sampled here, the
    /// ones carrying an id carried it in the legacy form. Reading only the
    /// modern spelling would have found nothing and reported the library
    /// unmatchable.
    /// </summary>
    public static NfoIds Read(string nfoPath)
    {
        try
        {
            var blocks = NfoFile.ReadBlocks(nfoPath);
            return blocks.Count == 0 ? new NfoIds() : From(blocks[0]);
        }
        catch (Exception)
        {
            return new NfoIds();   // unreadable is simply "no ids", not a failure
        }
    }

    public static NfoIds From(XElement root)
    {
        string? Unique(string type) => root
            .Elements("uniqueid")
            .FirstOrDefault(u => string.Equals((string?)u.Attribute("type"), type,
                                               StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() is { Length: > 0 } v ? v : null;

        string? Legacy(string name) =>
            root.Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        // An imdb id is the one with a shape worth checking: "tt" and digits.
        // The others are bare numbers and there is nothing to verify.
        var imdb = Unique("imdb") ?? Legacy("imdbid") ?? Legacy("imdb_id");

        return new NfoIds
        {
            TmdbId = Digits(Unique("tmdb") ?? Legacy("tmdbid")),
            TvdbId = Digits(Unique("tvdb") ?? Legacy("tvdbid")),
            ImdbId = imdb is not null && imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                     && imdb.Length > 2 ? imdb : null
        };
    }

    /// <summary>
    /// Only a genuine number. Emby writes an empty &lt;tmdbid /&gt; on titles
    /// it never matched, and asking a provider for title "" is a request that
    /// can only fail.
    /// </summary>
    private static string? Digits(string? value) =>
        value is { Length: > 0 } v && v.All(char.IsDigit) ? v : null;
}

/// <summary>What an existing .nfo has no answer for yet.</summary>
public sealed record NfoGaps
{
    public bool Cast { get; init; }
    public bool Rating { get; init; }
    public bool Genre { get; init; }
    public bool Crew { get; init; }
    public bool Certificate { get; init; }
    public bool Plot { get; init; }
    public bool StreamDetails { get; init; }

    public bool Any => Cast || Rating || Genre || Crew || Certificate || Plot || StreamDetails;

    /// <summary>The gaps in words, for a report somebody reads before agreeing to it.</summary>
    public string Summary
    {
        get
        {
            var bits = new List<string>();
            if (Cast) bits.Add("cast");
            if (Crew) bits.Add("director");
            if (Genre) bits.Add("genre");
            if (Rating) bits.Add("rating");
            if (Certificate) bits.Add("certificate");
            if (Plot) bits.Add("plot");
            if (StreamDetails) bits.Add("track details");

            return bits.Count == 0 ? "nothing missing" : string.Join(", ", bits);
        }
    }

    public static NfoGaps Of(string nfoPath)
    {
        try
        {
            var blocks = NfoFile.ReadBlocks(nfoPath);
            return blocks.Count == 0 ? Everything : Of(blocks[0]);
        }
        catch (Exception)
        {
            return Everything;
        }
    }

    /// <summary>A file that cannot be read is missing all of it, by definition.</summary>
    private static NfoGaps Everything => new()
    {
        Cast = true, Rating = true, Genre = true, Crew = true,
        Certificate = true, Plot = true, StreamDetails = true
    };

    public static NfoGaps Of(XElement root) => new()
    {
        Cast = !root.Elements("actor").Any(),

        // Either spelling counts as having one. A library holding a legacy
        // <rating> is not missing a rating just because it predates <ratings>.
        Rating = root.Element("ratings") is null && root.Element("rating") is null,

        Genre = !root.Elements("genre").Any(),
        Crew = !root.Elements("director").Any() && !root.Elements("credits").Any(),
        Certificate = root.Element("mpaa") is null,
        Plot = string.IsNullOrWhiteSpace(root.Element("plot")?.Value),
        StreamDetails = root.Element("fileinfo")?.Element("streamdetails") is null
    };
}
