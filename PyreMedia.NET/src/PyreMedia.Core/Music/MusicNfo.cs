using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace PyreMedia.Core.Music;

/// <summary>One track as an album.nfo lists it.</summary>
public sealed record NfoTrack(int Position, string Title, double Seconds, string? MusicBrainzTrackId);

/// <summary>
/// What an album.nfo says about an album.
///
/// These are scraped, not authored - a previous library manager wrote them by
/// matching the folder against an online database, and it did not always match
/// the right release. So this is evidence with a provenance, not a source of
/// truth: see <see cref="Corroborates"/> before believing the track listing.
/// </summary>
public sealed class AlbumNfo
{
    public required string Path { get; init; }

    public string? Title { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Year { get; init; }
    public string? Label { get; init; }
    public string? Style { get; init; }

    /// <summary>As the scraper recorded it, where it recorded it at all.</summary>
    public bool? IsCompilation { get; init; }

    public string? MusicBrainzAlbumId { get; init; }
    public string? MusicBrainzArtistId { get; init; }

    public List<NfoTrack> Tracks { get; init; } = [];

    /// <summary>
    /// The share of these files that this listing names, 0 to 1.
    ///
    /// Measured from the files, not from the listing, and the direction is the
    /// whole point. A folder holding one track of a twenty-track album is
    /// completely normal - a library assembled over thirty years is full of them -
    /// and asking "how much of the listing is here?" scores that at 5% and throws
    /// away a perfectly good description. Asking "are the files here in the
    /// listing?" scores it at 100%, which is the truth.
    ///
    /// The reverse case is what needs catching: one Europop folder carries an
    /// album.nfo with the right title and year and a thirteen-track listing
    /// belonging to a remix single. Its files are not in that listing, so it
    /// scores near zero and is not believed.
    /// </summary>
    public double Covers(IEnumerable<TrackTags> files)
    {
        if (Tracks.Count == 0) return 0;

        var listed = Tracks
            .Select(t => Simplify(t.Title))
            .Where(s => s.Length > 0)
            .ToList();

        if (listed.Count == 0) return 0;

        var present = files
            .Select(f => Simplify(f.Title.HasText
                ? f.Title.Text
                : System.IO.Path.GetFileNameWithoutExtension(f.Path)))
            .Where(s => s.Length > 0)
            .ToList();

        if (present.Count == 0) return 0;

        var matched = present.Count(p => listed.Any(l => l.Contains(p) || p.Contains(l)));
        return matched / (double)present.Count;
    }

    /// <summary>
    /// True when the listing describes these files closely enough to be believed
    /// about their order and length. Three quarters is deliberately demanding: a
    /// bonus track or a mis-tagged one may not be named, but if a quarter of what
    /// is here is absent from the listing, the listing is for something else.
    /// </summary>
    public bool Corroborates(IEnumerable<TrackTags> files) => Covers(files) >= 0.75;

    private static string Simplify(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);

        return sb.ToString();
    }
}

/// <summary>Reads the .nfo files a previous library manager left in the folders.</summary>
public static class MusicNfoReader
{
    public const string AlbumFile = "album.nfo";
    public const string ArtistFile = "artist.nfo";

    /// <summary>The album.nfo in a folder, or null if there is none or it will not parse.</summary>
    public static AlbumNfo? ReadAlbum(string folder)
    {
        var path = Path.Combine(folder, AlbumFile);
        if (!File.Exists(path)) return null;

        var root = Load(path);
        if (root is null || root.Name.LocalName != "album") return null;

        return new AlbumNfo
        {
            Path = path,
            Title = Text(root, "title"),
            AlbumArtist = Text(root, "artistdesc") ?? Text(root, "albumartist"),
            Year = FourDigits(Text(root, "year") ?? Text(root, "releasedate")),
            Label = Text(root, "label"),
            Style = Text(root, "style") ?? Text(root, "genre"),
            IsCompilation = Flag(Text(root, "compilation")),
            MusicBrainzAlbumId = Identifier.MusicBrainz(Text(root, "musicBrainzAlbumID")),
            MusicBrainzArtistId = root.Elements("albumArtistCredits")
                .Select(c => Text(c, "musicBrainzArtistID"))
                .FirstOrDefault(v => v is not null),
            Tracks = [.. root.Elements("track").Select(ReadTrack).OfType<NfoTrack>()]
        };
    }

    /// <summary>The MusicBrainz artist id from an artist.nfo, when there is one.</summary>
    public static string? ReadArtistId(string folder)
    {
        var path = Path.Combine(folder, ArtistFile);
        if (!File.Exists(path)) return null;

        var root = Load(path);
        return root is null ? null : Text(root, "musicBrainzArtistID");
    }

    private static NfoTrack? ReadTrack(XElement element)
    {
        var title = Text(element, "title");
        if (title is null) return null;

        var position = int.TryParse(Text(element, "position"), out var p) ? p : 0;

        return new NfoTrack(position, title, Duration(Text(element, "duration")),
            Identifier.MusicBrainz(Text(element, "musicBrainzTrackID")));
    }

    /// <summary>"04:20" or "1:02:33" as seconds. Zero when absent or unreadable.</summary>
    internal static double Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        // Some writers put plain seconds here instead of a clock.
        if (!text.Contains(':'))
            return double.TryParse(text, CultureInfo.InvariantCulture, out var plain) && plain > 0 ? plain : 0;

        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return 0;

        double total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part.Trim(), out var n) || n < 0) return 0;
            total = total * 60 + n;
        }

        return total;
    }

    /// <summary>
    /// Parse tolerantly. These files are thirty years of other people's tools:
    /// some declare UTF-8 and hold cp1252 bytes, some have an unescaped ampersand
    /// in a review. A file we cannot parse is simply one we have no evidence from.
    /// </summary>
    private static XElement? Load(string path)
    {
        try
        {
            return XDocument.Load(path).Root;
        }
        catch
        {
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                return XDocument.Parse(text).Root;
            }
            catch { return null; }
        }
    }

    private static string? Text(XElement parent, string name)
    {
        var value = parent.Element(name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? Flag(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "1" => true,
        "false" or "no" or "0" => false,
        _ => null
    };

    private static string? FourDigits(string? text)
    {
        if (text is null) return null;

        for (var i = 0; i + 4 <= text.Length; i++)
        {
            var run = text.AsSpan(i, 4);
            if (!char.IsAsciiDigit(run[0]) || !char.IsAsciiDigit(run[1])
                || !char.IsAsciiDigit(run[2]) || !char.IsAsciiDigit(run[3])) continue;

            var value = int.Parse(run);
            if (value is >= 1900 and <= 2100) return run.ToString();
        }

        return null;
    }
}
