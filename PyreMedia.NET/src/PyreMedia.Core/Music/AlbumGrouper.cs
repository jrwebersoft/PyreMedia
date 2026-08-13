using System.Text;

namespace PyreMedia.Core.Music;

/// <summary>One album: the tracks, and what they agree they are.</summary>
public sealed class AlbumGroup
{
    public required string Title { get; init; }

    /// <summary>The artist the folder is named for. "Various Artists" for a compilation.</summary>
    public required string DisplayArtist { get; init; }

    /// <summary>Four digits, or null when no track carried a usable one.</summary>
    public string? Year { get; init; }

    public required List<TrackTags> Tracks { get; init; }

    /// <summary>
    /// Tracks by several different artists, gathered under one album. Detected
    /// rather than trusted: a great many compilations are tagged with each
    /// track's own artist as the album artist, which is what shatters a library
    /// into one-track albums.
    /// </summary>
    public bool IsCompilation { get; init; }

    public bool IsMultiDisc { get; init; }

    /// <summary>The folder the tracks are in now, when they all share one.</summary>
    public string? SourceFolder { get; init; }

    /// <summary>Set when something about this album wants a person to look.</summary>
    public string? Caution { get; init; }

    public int TrackCount => Tracks.Count;

    public override string ToString() =>
        $"{DisplayArtist} - {Title}{(Year is null ? "" : $" ({Year})")} [{TrackCount}]";
}

/// <summary>
/// Gathers tracks into albums.
///
/// The hard case, and the one that ruins music libraries, is the compilation. A
/// "Now That's What I Call Music" tagged with each track's own artist as its
/// album artist becomes forty one-track albums - measured on a real library,
/// grouping by track artist rather than album artist tripled the number of
/// albums and left 4,143 of them holding a single track.
///
/// So: album artist first, then the folder the files actually sit in, and a
/// compilation is recognised by the tracks disagreeing about who made them
/// rather than by anyone having said so.
/// </summary>
public static class AlbumGrouper
{
    public const string VariousArtists = "Various Artists";

    public static List<AlbumGroup> Group(IEnumerable<TrackTags> tracks)
    {
        var usable = tracks.Where(t => t.Error is null).ToList();
        var albums = new List<AlbumGroup>();

        // Album artist and album title together, falling back to the containing
        // folder where the album tag is missing - a folder of files with no album
        // tag is still obviously one album.
        foreach (var group in usable.GroupBy(Key))
        {
            var list = group.ToList();
            albums.Add(Build(list));
        }

        return [.. albums.OrderBy(a => a.DisplayArtist, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(a => a.Year ?? "")
                         .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)];

        static (string, string) Key(TrackTags t)
        {
            var artist = t.AlbumArtist.HasText ? t.AlbumArtist.Text
                       : t.Artist.HasText ? t.Artist.Text
                       : "";

            // The same fallback the title uses, and it has to be the same one:
            // if the key kept a trailing "(2008)" that the title strips, one
            // album would key two ways the moment it was filed.
            var album = t.Album.HasText ? t.Album.Text : FolderAsAlbum(t.Path);

            // Every spelling of "this is a compilation" has to key the same, or one
            // soundtrack tagged "Original Soundtrack" on some tracks, "Soundtrack"
            // on others and "Various Artists" on the rest becomes three albums -
            // which all then render to the same folder and fight over every path.
            var key = Normalise(artist);
            if (IsCompilationMarker(key)) key = "various artists";

            return (key, Normalise(StripDisc(album).Title));
        }
    }

    /// <summary>
    /// Pull a disc marker out of an album title.
    ///
    /// "Straight Outta Lynwood Disc 1" and "... Disc 2" are one album in two
    /// parts, not two albums - but tagged that way they group separately and
    /// then collide, because both contain a track 1. The number belongs in the
    /// disc field, where the naming layer already knows what to do with it.
    /// </summary>
    /// <summary>
    /// A folder name read as an album title, with the year this program put
    /// there taken back off.
    ///
    /// Only reached when a file has no album tag at all, and then the folder is
    /// the best evidence there is - but the folder is very often something this
    /// program wrote, in the shape "Album (Year)". Reading that back whole and
    /// appending the year again gives "Album (Year) (Year)", and the next scan
    /// gives "Album (Year) (Year) (Year)". A real library had one such album
    /// and its path grew by four characters every time it was filed.
    ///
    /// Output must never re-enter as input. Anything the pattern is about to
    /// add has to come off first.
    /// </summary>
    internal static string FolderAsAlbum(string path)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        if (folder.Length == 0) return "";

        var trimmed = TrailingYear.Replace(folder, "").Trim();

        // Never leave nothing: a folder called "(2008)" and nothing else is
        // still the only name this album has.
        return trimmed.Length > 0 ? trimmed : folder;
    }

    private static readonly System.Text.RegularExpressions.Regex TrailingYear = new(
        @"\s*\((?:19|20)\d{2}\)\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    internal static (string Title, int? Disc) StripDisc(string album)
    {
        if (string.IsNullOrWhiteSpace(album)) return (album ?? "", null);

        var text = album.Trim();
        int? disc = null;

        // Repeatedly, because taggers stack them: a real library carries
        // "Alle 100 goed Apres Ski CD2 Disc 1". Stripping one marker left
        // "...CD2" in the title, which then became a folder name, which the
        // next scan read as a disc marker all over again - the album moved
        // every time it was filed and never settled.
        while (true)
        {
            var m = DiscSuffix.Match(text);
            if (!m.Success) break;

            var shorter = text[..m.Index].Trim().TrimEnd('-', ':', ',', '(', '[').Trim();

            // Never strip the whole title away: "Disc 2" alone is all we have.
            if (shorter.Length == 0) break;

            // The innermost marker wins - it sits closest to the title and is
            // the one describing this release. In "... CD2 Disc 1" the CD2 is
            // the disc and the trailing "Disc 1" is a tagger's habit.
            if (int.TryParse(m.Groups[1].Value, out var n) && n is > 0 and < 100) disc = n;

            text = shorter;
        }

        return (text, disc);
    }

    /// <summary>
    /// "Volume" is deliberately not here. A disc is part of one release; a volume
    /// is usually a release of its own - "Ryde or Die, Vol. 1" and "Vol. 2" are
    /// two albums a year apart, and folding them together invents collisions
    /// between two records that never shared a track. Leaving a genuine two-volume
    /// set split is the cheaper mistake: it is visible, and it merges nothing.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DiscSuffix = new(
        @"[\s\-,:(\[]+(?:disc|disk|cd)\.?\s*(\d{1,2})\s*[)\]]*\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static AlbumGroup Build(List<TrackTags> tracks)
    {
        // Where the disc lived only in the album title, put it back on the track
        // before anything reads DiscNumber.
        foreach (var t in tracks.Where(t => t.Album.HasText))
        {
            var (_, disc) = StripDisc(t.Album.Text);
            if (disc is not > 0) continue;

            // The title wins over a disc tag of 1. Both discs of a two-disc set
            // are routinely tagged "1 of 1" by whatever ripped them, and only the
            // title distinguishes them - so filling the gap is not enough, the
            // wrong value has to be corrected too. Anything above 1 in the tag is
            // deliberate and left alone.
            if (t.DiscNumber is null or 0 or 1) t.DiscNumber = disc;
        }

        // The disc marker is as likely to be on the folder as in the tag:
        // "Metallica\S&M Disc 1" and "S&M Disc 2" both tag their album "S&M" with
        // no disc at all, so without this they merge and every track number in the
        // shorter disc collides with its opposite number in the longer one.
        foreach (var t in tracks.Where(t => t.DiscNumber is null or 0 or 1))
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(t.Path) ?? "");
            if (folder.Length == 0) continue;

            var (_, disc) = StripDisc(folder);
            if (disc is > 0) t.DiscNumber = disc;
        }

        var folders = tracks
            .Select(t => Path.GetDirectoryName(t.Path) ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var artists = tracks
            .Where(t => t.Artist.HasText)
            .Select(t => Normalise(t.Artist.Text))
            .Distinct()
            .ToList();

        // A compilation either says so, or gives itself away by having tracks
        // from several artists under one album.
        var declared = tracks.Any(t =>
            t.AlbumArtist.HasText && IsCompilationMarker(Normalise(t.AlbumArtist.Text)));

        var mixed = artists.Count > 1 && artists.Count >= Math.Max(3, tracks.Count / 2);
        var compilation = declared || mixed;

        var artist = compilation
            ? VariousArtists
            : Best(tracks.Select(t => t.AlbumArtist.HasText ? t.AlbumArtist.Text : t.Artist.Text));

        var title = Best(tracks.Select(t =>
            StripDisc(t.Album.HasText
                ? t.Album.Text
                : FolderAsAlbum(t.Path)).Title));

        string? caution = null;

        // The same track number twice means either duplicates or two albums that
        // got merged. Worth a look either way, and never worth guessing about.
        var repeated = tracks
            .Where(t => t.TrackNumber is > 0)
            .GroupBy(t => (t.DiscNumber ?? 1, t.TrackNumber!.Value))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Item2)
            .Order()
            .ToList();

        if (repeated.Count > 0)
            caution = $"track {(repeated.Count == 1 ? "number" : "numbers")} "
                      + $"{string.Join(", ", repeated.Take(6))} appear more than once";

        else if (folders.Count > 1)
            caution = $"spread across {folders.Count} folders";

        return new AlbumGroup
        {
            Title = title.Length > 0 ? title : "Unknown Album",
            DisplayArtist = artist.Length > 0 ? artist : "Unknown Artist",
            Year = Year(tracks),
            Tracks = [.. tracks.OrderBy(t => t.DiscNumber ?? 1).ThenBy(t => t.TrackNumber ?? 0)],
            IsCompilation = compilation,
            IsMultiDisc = tracks.Any(t => t.DiscNumber is > 1),
            SourceFolder = folders.Count == 1 ? folders[0] : null,
            Caution = caution
        };
    }

    /// <summary>
    /// The album's year: the earliest four-digit one any track carries.
    ///
    /// Earliest rather than commonest, because where tracks disagree it is
    /// usually a reissue date creeping in on some of them, and the original
    /// release is what belongs on the folder. Dates arrive as "1959",
    /// "24-07-2024" and "2013-02-12T08:00:00Z", so a four-digit run is looked
    /// for rather than the string being truncated.
    /// </summary>
    /// <summary>
    /// The album's year, from whichever tracks are given.
    ///
    /// Internal so the planner can ask again once it knows which tracks are
    /// actually staying: an album named from copies that are about to be
    /// quarantined gets a folder those files will not be in, and the next scan
    /// moves the whole record. Jewel's "Spirit" was filed as "Spirit (1998)"
    /// from duplicates that carried the year, then wanted "Spirit" the moment
    /// they were gone.
    /// </summary>
    internal static string? YearOf(List<TrackTags> tracks) => Year(tracks);

    private static string? Year(List<TrackTags> tracks)
    {
        var years = new List<string>();

        foreach (var t in tracks.Where(t => t.Year.HasText))
        {
            var text = t.Year.Text;

            for (var i = 0; i + 4 <= text.Length; i++)
            {
                var run = text.AsSpan(i, 4);
                if (!run[0].IsDigit() || !run[1].IsDigit() || !run[2].IsDigit() || !run[3].IsDigit()) continue;

                var value = int.Parse(run);
                if (value is >= 1900 and <= 2100) { years.Add(run.ToString()); break; }
            }
        }

        return years.Count == 0 ? null : years.Order().First();
    }

    /// <summary>The tidiest spelling among several: the one a person would have typed.</summary>
    private static string Best(IEnumerable<string> values)
    {
        var options = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (options.Count == 0) return "";

        return options
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.Count(char.IsUpper))
            .ThenBy(g => g.Key.Length)
            .First().Key.Trim();
    }

    /// <summary>
    /// An album-artist value that means "no single artist" rather than naming one.
    /// Taken already normalised - lower case, letters and digits only.
    /// </summary>
    internal static bool IsCompilationMarker(string normalised) => normalised is
        "various artists" or "various" or "va" or "variousartist" or "variousartists"
        or "soundtrack" or "sound track" or "original soundtrack" or "ost"
        or "original motion picture soundtrack" or "motion picture soundtrack"
        or "original cast recording" or "compilation";

    private static bool IsDigit(this char c) => c is >= '0' and <= '9';

    /// <summary>Case, punctuation and spacing are spelling, not identity.</summary>
    private static string Normalise(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == '&') sb.Append("and");
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
        }

        return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
