using System.Xml.Linq;

namespace PyreMedia.Core.Metadata;

public enum NfoKind { Unknown, Movie, TvShow, Episode }

/// <summary>
/// A Kodi sidecar .nfo, read for what it can tell us that a filename can't.
///
/// The valuable part is <c>&lt;uniqueid&gt;</c>: an exact TMDb/TVDB/IMDb id that
/// removes the guesswork from matching entirely. A file called "300.mkv" or
/// "The Cat and the Dragon.mkv" is ambiguous by name and unambiguous by id.
///
/// Read-only and defensive - a malformed or hand-edited nfo must never stop a
/// scan, so every failure degrades to "no information" rather than throwing.
/// </summary>
public sealed class NfoFile
{
    public required string Path { get; init; }
    public required NfoKind Kind { get; init; }

    public string? Title { get; init; }
    public string? OriginalTitle { get; init; }
    public string? Year { get; init; }

    public string? TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public string? TvdbId { get; init; }

    /// <summary>Season/episode when this describes a single episode.</summary>
    public int? Season { get; init; }
    public int? Episode { get; init; }

    /// <summary>Collection name, e.g. "Ant-Man Collection".</summary>
    public string? Set { get; init; }

    /// <summary>True when the file carries a fileinfo/streamdetails block.</summary>
    public bool HasStreamDetails { get; init; }

    /// <summary>What streamdetails currently claims, for staleness checks.</summary>
    public string? VideoCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? HdrType { get; init; }
    public string? StereoMode { get; init; }
    public int AudioTrackCount { get; init; }
    public int SubtitleTrackCount { get; init; }

    public bool HasAnyId => !string.IsNullOrWhiteSpace(TmdbId)
                            || !string.IsNullOrWhiteSpace(TvdbId)
                            || !string.IsNullOrWhiteSpace(ImdbId);

    public string IdSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(TmdbId)) parts.Add($"tmdb {TmdbId}");
            if (!string.IsNullOrWhiteSpace(TvdbId)) parts.Add($"tvdb {TvdbId}");
            if (!string.IsNullOrWhiteSpace(ImdbId)) parts.Add(ImdbId!);
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Drop the XML declaration - legal only at the very start of a document, so
    /// it can't survive being wrapped in another element.
    /// </summary>
    private static string StripDeclaration(string xml)
    {
        var start = xml.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return xml;

        var end = xml.IndexOf("?>", start, StringComparison.Ordinal);
        return end < 0 ? xml : xml[..start] + xml[(end + 2)..];
    }

    /// <summary>Parse, or null if it isn't a metadata nfo we understand.</summary>
    /// <summary>
    /// Every root block in an .nfo, in file order.
    ///
    /// A multi-episode .nfo is several <c>episodedetails</c> elements one after
    /// another, which is Kodi's format and not a document XDocument will load, so
    /// it is read under a wrapper. Used when rewriting a file, to carry across
    /// what the new metadata doesn't know.
    /// </summary>
    public static List<XElement> ReadBlocks(string path)
    {
        LegacyEncodings.Ensure();

        var wrapped = XDocument.Parse(
            "<msroot>" + StripDeclaration(File.ReadAllText(path)) + "</msroot>", LoadOptions.None);

        return [.. wrapped.Root!.Elements()];
    }

    public static NfoFile? Read(string path)
    {
        try
        {
            // No DTD processing: these files come from the internet by way of a
            // scraper, and we only ever read them.
            //
            // Parsed under a wrapper because a multi-episode .nfo has one
            // episodedetails block per episode and so several root elements -
            // Kodi's format, and not a document XDocument.Load will accept. The
            // first block is the one that identifies the file.
            LegacyEncodings.Ensure();

            var wrapped = XDocument.Parse(
                "<msroot>" + StripDeclaration(File.ReadAllText(path)) + "</msroot>", LoadOptions.None);

            var root = wrapped.Root!.Elements().FirstOrDefault();
            if (root is null) return null;

            var kind = root.Name.LocalName.ToLowerInvariant() switch
            {
                "movie" => NfoKind.Movie,
                "tvshow" => NfoKind.TvShow,
                "episodedetails" => NfoKind.Episode,
                _ => NfoKind.Unknown
            };

            if (kind == NfoKind.Unknown) return null;

            var stream = root.Element("fileinfo")?.Element("streamdetails");
            var video = stream?.Element("video");

            return new NfoFile
            {
                Path = path,
                Kind = kind,
                Title = Text(root, "title"),
                OriginalTitle = Text(root, "originaltitle"),
                Year = YearFrom(root),
                TmdbId = UniqueId(root, "tmdb") ?? TextIfNumeric(root, "id"),
                ImdbId = UniqueId(root, "imdb"),
                TvdbId = UniqueId(root, "tvdb"),
                Season = Int(root, "season"),
                Episode = Int(root, "episode"),
                Set = root.Element("set")?.Element("name")?.Value.Trim()
                      ?? (root.Element("set")?.HasElements == false
                          ? root.Element("set")?.Value.Trim()
                          : null),
                HasStreamDetails = stream is not null,
                VideoCodec = Text(video, "codec"),
                Width = Int(video, "width"),
                Height = Int(video, "height"),
                HdrType = Text(video, "hdrtype"),
                StereoMode = Text(video, "stereomode"),
                AudioTrackCount = stream?.Elements("audio").Count() ?? 0,
                SubtitleTrackCount = stream?.Elements("subtitle").Count() ?? 0
            };
        }
        catch
        {
            // Malformed, locked, or not XML at all. Not our problem to fix.
            return null;
        }
    }

    private static string? Text(XElement? parent, string name)
    {
        var v = parent?.Element(name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static int? Int(XElement? parent, string name)
        => int.TryParse(Text(parent, name), out var n) ? n : null;

    /// <summary>
    /// The old &lt;id&gt; element predates &lt;uniqueid&gt; and holds whatever the
    /// scraper of the day used. Only trust it when it's numeric, since an IMDb
    /// "tt..." there would be read as a TMDb id.
    /// </summary>
    private static string? TextIfNumeric(XElement root, string name)
    {
        var v = Text(root, name);
        return v is not null && v.All(char.IsDigit) ? v : null;
    }

    private static string? UniqueId(XElement root, string type)
    {
        var match = root.Elements("uniqueid")
            .FirstOrDefault(e => string.Equals(
                (string?)e.Attribute("type"), type, StringComparison.OrdinalIgnoreCase));

        var v = match?.Value.Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>
    /// Year, from whichever element carries it. Kodi writes &lt;year&gt; on older
    /// files and &lt;premiered&gt;/&lt;aired&gt; on newer ones, and a movie nfo may
    /// have only the full date.
    /// </summary>
    private static string? YearFrom(XElement root)
    {
        var y = Text(root, "year");
        if (y is { Length: 4 }) return y;

        foreach (var name in new[] { "premiered", "aired", "releasedate" })
        {
            var v = Text(root, name);
            if (v is { Length: >= 4 } && v[..4].All(char.IsDigit))
                return v[..4];
        }

        return null;
    }

    /// <summary>
    /// The nfo Kodi would look for alongside a video, in its own order of
    /// preference: "Film (2023).nfo" beside "Film (2023).mkv", then "movie.nfo"
    /// in the same folder.
    /// </summary>
    public static NfoFile? ForVideo(string videoPath)
    {
        var dir = System.IO.Path.GetDirectoryName(videoPath);
        if (dir is null) return null;

        var stem = System.IO.Path.GetFileNameWithoutExtension(videoPath);

        foreach (var candidate in new[]
                 {
                     System.IO.Path.Combine(dir, stem + ".nfo"),
                     System.IO.Path.Combine(dir, "movie.nfo"),
                     System.IO.Path.Combine(dir, "tvshow.nfo")
                 })
        {
            if (!File.Exists(candidate)) continue;

            var nfo = Read(candidate);
            if (nfo is not null) return nfo;
        }

        return null;
    }
}
