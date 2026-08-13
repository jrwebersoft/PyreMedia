using System.Text.RegularExpressions;
using PyreMedia.Core.Naming;

namespace PyreMedia.Core.Music;

/// <summary>
/// Builds the path a track should live at.
///
/// The layout is <c>AlbumArtist / (Year) Album / NN Artist - Title</c>. An album
/// is to music what a season is to television, and the same principle applies:
/// the filename says what the file is, so a track separated from its folder is
/// still identifiable.
///
/// The album is deliberately <em>not</em> repeated in the filename. Within one
/// album folder it never varies, so it carries no information there - and
/// measured over a real library it was what pushed 23 paths past Windows'
/// 260-character limit. The artist is repeated, because on a compilation it does
/// vary, and that is exactly the case where a "Various Artists" folder would
/// otherwise hide who is playing.
/// </summary>
public static class MusicNaming
{
    public const string DefaultFolderFormat = "{albumartist}/({year}) {album}";
    public const string DefaultFileFormat = "{track} {artist} - {title}";

    /// <summary>Placeholders, and what each resolves to.</summary>
    private static readonly string[] Known =
    [
        "albumartist", "artist", "album", "title", "year", "track", "disc", "genre"
    ];

    public static string[] Placeholders => [.. Known.Select(k => "{" + k + "}")];

    /// <summary>
    /// Windows will not take a path beyond this without long-path support, and a
    /// library that needs it is a library that breaks in Explorer.
    /// </summary>
    public const int MaxPath = 255;

    /// <summary>
    /// The full path for a track under <paramref name="libraryRoot"/>.
    ///
    /// Where the result would be too long, the title is shortened rather than
    /// anything else: the track number and artist are what make a file
    /// identifiable, and a truncated title is still recognisable where a
    /// truncated artist is not.
    /// </summary>
    public static string PathFor(
        TrackTags track, AlbumGroup album, string libraryRoot,
        string folderFormat = DefaultFolderFormat,
        string fileFormat = DefaultFileFormat,
        char replacement = '-')
    {
        var folder = Expand(folderFormat, track, album, replacement);

        var parts = folder
            .Split('/', '\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => NameFormatter.Sanitize(p, replacement))
            .Where(p => p.Length > 0)
            .ToArray();

        var directory = Path.Combine([libraryRoot, .. parts]);
        var extension = Path.GetExtension(track.Path);

        var name = NameFormatter.Sanitize(Expand(fileFormat, track, album, replacement), replacement);
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(track.Path);

        var full = Path.Combine(directory, name + extension);
        if (full.Length <= MaxPath) return full;

        // Too long. Shorten the title and rebuild, rather than truncating the
        // finished path and producing a name that ends mid-word with no extension.
        var over = full.Length - MaxPath;
        var title = track.Title.Text;
        var shortened = title.Length > over + 4 ? title[..(title.Length - over - 4)].TrimEnd() + "…" : "";

        var retry = NameFormatter.Sanitize(
            Expand(fileFormat, track, album, replacement, titleOverride: shortened), replacement);

        if (retry.Length == 0) retry = $"{track.TrackNumber:00}";

        var attempt = Path.Combine(directory, retry + extension);
        return attempt.Length <= MaxPath ? attempt : Path.Combine(directory, $"{track.TrackNumber:00}{extension}");
    }

    private static string Expand(
        string format, TrackTags t, AlbumGroup album, char replacement, string? titleOverride = null)
    {
        return Token.Replace(format, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "albumartist" => album.DisplayArtist,
            "artist" => t.Artist.HasText ? t.Artist.Text : album.DisplayArtist,
            "album" => album.Title,
            "title" => titleOverride ?? (t.Title.HasText ? t.Title.Text : ""),
            "year" => album.Year ?? "",
            "genre" => t.Genre.Text,
            "disc" => album.IsMultiDisc && t.DiscNumber is > 0 ? $"{t.DiscNumber}-" : "",

            // Multi-disc albums get "1-05" so one folder still sorts correctly,
            // rather than an extra level of nesting for the 14% that need it.
            "track" => album.IsMultiDisc && t.DiscNumber is > 0
                ? $"{t.DiscNumber}-{t.TrackNumber:00}"
                : $"{t.TrackNumber:00}",

            _ => m.Value
        });
    }

    /// <summary>
    /// Tidy an empty "(Year) " prefix away when the year is unknown, so an album
    /// with no date reads "Album" rather than "() Album".
    /// </summary>
    public static string Tidy(string s) =>
        Empties.Replace(s, "").Replace("  ", " ").Trim().Trim('-', '.', ' ');

    private static readonly Regex Token =
        new(@"\{([A-Za-z]+)\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex Empties =
        new(@"\(\s*\)|\[\s*\]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
