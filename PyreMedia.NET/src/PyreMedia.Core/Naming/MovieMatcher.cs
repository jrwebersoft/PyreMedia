using System.Text.RegularExpressions;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Naming;

/// <summary>
/// Guesses a movie's title and year from a file or folder name.
///
/// The year is the strongest signal in a release name: everything before it is
/// almost always the title, and everything after is almost always noise. So we
/// find the year first and cut there, rather than trying to strip junk from a
/// string we don't yet understand.
/// </summary>
public static partial class MovieMatcher
{
    [GeneratedRegex(@"(?<!\d)(?<year>(?:19|20)\d{2})(?!\d)")]
    private static partial Regex Year();

    [GeneratedRegex(
        @"\b(?:480p|540p|720p|1080p|1440p|2160p|4k|8k|x26[45]|h\.?26[45]|hevc|xvid|divx|" +
        @"aac|ac3|dts(?:-hd)?|truehd|atmos|ddp?5[\s._-]?1|5[\s._-]?1|7[\s._-]?1|" +
        @"bluray|blu-ray|brrip|bdrip|dvdrip|dvd|webrip|web-?dl|hdtv|hdrip|remux|cam|ts|" +
        @"proper|repack|internal|extended|uncut|unrated|limited|remastered|directors?[\s._-]?cut|" +
        @"hdr10?|dolby(?:vision)?|10bit|8bit|multi|dual|subbed|dubbed|imax)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Junk();

    /// <summary>
    /// Parse a movie name. Always returns something - unlike episodes, a movie
    /// with no recognisable year is still searchable by title alone.
    /// </summary>
    public static MovieRef Parse(string nameOrPath)
    {
        // NOT GetFileNameWithoutExtension: on "The.Matrix.1999" it would strip
        // ".1999" as an extension and lose the year.
        var stem = NameFormatter.StripExtension(Path.GetFileName(nameOrPath));
        if (string.IsNullOrWhiteSpace(stem))
            return new MovieRef(string.Empty, null);

        // Bracketed years are common: "The Matrix (1999) [1080p]"
        var working = stem.Replace('_', ' ').Replace('.', ' ');

        string? year = null;
        var title = working;

        // Use the LAST year that still leaves a non-empty title. "2012 (2009)"
        // must resolve to title "2012", year 2009 - not the other way round.
        // A number too far in the future isn't a release year - it's part of the
        // title. Without this, "Blade Runner 2049" becomes "Blade Runner" (2049).
        var latestPlausible = DateTime.Now.Year + 2;

        foreach (Match m in Year().Matches(working).Reverse())
        {
            var before = working[..m.Index].Trim(' ', '-', '(', '[', '{');
            if (before.Length == 0)
                continue;

            if (!int.TryParse(m.Groups["year"].Value, out var y) || y > latestPlausible)
                continue;

            year = m.Groups["year"].Value;
            title = before;
            break;
        }

        title = Junk().Replace(title, " ");
        title = Regex.Replace(title, @"[\[\](){}]", " ");
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-');

        // Everything was noise - fall back to the raw stem so the user can edit it.
        if (title.Length == 0)
            title = Regex.Replace(working, @"\s+", " ").Trim();

        return new MovieRef(title, year);
    }
}
