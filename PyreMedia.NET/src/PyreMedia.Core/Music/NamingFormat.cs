using System.Text;
using System.Text.RegularExpressions;

namespace PyreMedia.Core.Music;

/// <summary>
/// Where a file goes, written as a pattern the user can change.
///
/// <code>
///   {albumartist}/{album} ({year})/{disc}{track:00} {title}
/// </code>
///
/// Braces are fields. Square brackets are optional sections that disappear
/// whole if any field inside them is empty, which is what makes one pattern
/// serve a library where some albums have a year and some have not:
///
/// <code>
///   {albumartist}/{album}[ ({year})]/[{disc}-]{track:00} {title}
/// </code>
///
/// Forward slashes separate folders whatever the platform, so a pattern reads
/// the same everywhere and is not quietly broken by being typed on the wrong
/// machine.
/// </summary>
public sealed class NamingFormat
{
    /// <summary>
    /// What most people want, and what the library is being sorted into now.
    /// Compilations are handled by AlbumArtist already reading "Various
    /// Artists", so one pattern covers both cases.
    /// </summary>
    public const string Default = "{albumartist}/{album}[ ({year})]/[{disc}-]{track:00} {title}";

    /// <summary>
    /// The same, under a letter. Worth having on a library of thousands of
    /// artists, where one folder of 2,705 entries is unusable in a file browser.
    /// </summary>
    public const string ByInitial = "{initial}/{albumartist}/{album}[ ({year})]/[{disc}-]{track:00} {title}";

    public const string ArtistTitle = "{albumartist}/{album}[ ({year})]/[{disc}-]{track:00} {artist} - {title}";

    public string Pattern { get; }

    public NamingFormat(string pattern) => Pattern = pattern;

    /// <summary>
    /// Every field name a pattern may use, with a line about each - this is
    /// what a settings screen shows beside the box.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Fields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["albumartist"] = "Who the album is by. \"Various Artists\" on a compilation.",
            ["artist"] = "Who this track is by, which on a compilation is not the album artist.",
            ["album"] = "The album title.",
            ["title"] = "The track title.",
            ["year"] = "Four digits, where the file knows them.",
            ["genre"] = "As tagged. Often empty and often wrong; use with care.",
            ["track"] = "Track number. Pad it with {track:00}.",
            ["disc"] = "Disc number, empty on a single-disc album.",
            ["initial"] = "First letter of the album artist, for A/B/C folders. \"The\" is ignored.",
            ["ext"] = "The file extension, without the dot. Added automatically if you leave it out.",

            // Only meaningful on an audiobook, and harmless elsewhere - they
            // come out empty, so an optional section around one disappears.
            ["author"] = "Audiobooks: who wrote it.",
            ["book"] = "Audiobooks: the title of the book.",
            ["narrator"] = "Audiobooks: who read it, where a tagger said.",
            ["chapter"] = "Audiobooks: position in the book. Pad it with {chapter:000}.",
            ["chaptertitle"] = "Audiobooks: the name of this chapter."
        };

    /// <summary>
    /// The path this track should have, relative to the library root, using the
    /// platform's own separator.
    /// </summary>
    /// <param name="book">
    /// Supplied when the file is a chapter of an audiobook, which is filed by
    /// author and chapter rather than by album and track. A book's own tags
    /// cannot be trusted for this - one ripped from CDs carries the disc as the
    /// album and the publisher as the artist - so the values come from what
    /// <see cref="Audiobooks"/> worked out about the folder as a whole.
    /// </param>
    public string Path(TrackTags track, Audiobook? book = null)
    {
        var built = Build(Pattern, track, book);

        // Each folder and the filename are sanitised separately. Doing it to
        // the whole string at once would turn the separators into underscores.
        var parts = built.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Safe)
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Count == 0) parts.Add(Safe(System.IO.Path.GetFileNameWithoutExtension(track.Path)));

        var extension = System.IO.Path.GetExtension(track.Path).ToLowerInvariant();
        if (!parts[^1].EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            parts[^1] += extension;

        return string.Join(System.IO.Path.DirectorySeparatorChar, parts);
    }

    /// <summary>
    /// What is wrong with a pattern, or an empty list. Run as the user types.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        foreach (Match m in Field.Matches(Pattern))
            if (!Fields.ContainsKey(m.Groups[1].Value))
                problems.Add($"there is no field called \"{m.Groups[1].Value}\"");

        if (Pattern.Count(c => c == '[') != Pattern.Count(c => c == ']'))
            problems.Add("the square brackets do not pair up");

        if (!Field.IsMatch(Pattern))
            problems.Add("a pattern with no fields in it would give every file the same name");

        // A pattern that never varies by track collides every file in an album
        // onto one path. The planner would catch it and hold everything, but
        // saying so here is far more use than a wall of held files later.
        // Something has to vary per file. A book says so with {chapter} and
        // {chaptertitle} rather than {track} and {title}, and rejecting that
        // would make the audiobook pattern permanently invalid.
        else if (!Field.Matches(Pattern).Any(m =>
                     m.Groups[1].Value.ToLowerInvariant()
                         is "title" or "track" or "chapter" or "chaptertitle"))
            problems.Add("without a title or a track number, every file in one album "
                       + "lands on the same filename");

        if (Pattern.TrimEnd().EndsWith('/'))
            problems.Add("the pattern ends with a slash, so there is no filename");

        return problems;
    }

    /// <summary>A worked example, for showing under the box as it is typed.</summary>
    public string Example() => Path(new TrackTags
    {
        Path = @"X:\example.flac",
        Title = new("Sie Liebt Dich", TagSource.VorbisComment, TextConfidence.Declared),
        Artist = new("The Beatles", TagSource.VorbisComment, TextConfidence.Declared),
        AlbumArtist = new("The Beatles", TagSource.VorbisComment, TextConfidence.Declared),
        Album = new("Past Masters", TagSource.VorbisComment, TextConfidence.Declared),
        Year = new("1988", TagSource.VorbisComment, TextConfidence.Declared),
        Genre = new("Rock", TagSource.VorbisComment, TextConfidence.Declared),
        TrackNumber = 9,
        DiscNumber = 1
    });

    private static string Build(string pattern, TrackTags track, Audiobook? book)
    {
        var output = new StringBuilder();
        var section = new StringBuilder();
        var depth = 0;
        var sectionComplete = true;

        foreach (var token in Tokenise(pattern))
        {
            switch (token)
            {
                case "[":
                    // Nested brackets are treated as one section. Simpler to
                    // explain than to nest, and nobody has ever wanted the
                    // difference in a filename.
                    if (depth++ == 0) { section.Clear(); sectionComplete = true; }
                    else section.Append('[');
                    break;

                case "]":
                    if (--depth == 0)
                    {
                        if (sectionComplete) output.Append(section);
                        section.Clear();
                    }
                    else section.Append(']');
                    break;

                default:
                    var text = token.StartsWith('{') ? Value(token, track, book) : token;

                    // An empty field is what collapses the section around it.
                    if (token.StartsWith('{') && text.Length == 0)
                    {
                        if (depth > 0) sectionComplete = false;
                        break;
                    }

                    (depth > 0 ? section : output).Append(text);
                    break;
            }
        }

        // An unclosed bracket: emit what it held rather than silently dropping
        // the tail of the pattern.
        if (depth > 0 && sectionComplete) output.Append(section);

        return output.ToString();
    }

    private static IEnumerable<string> Tokenise(string pattern)
    {
        var literal = new StringBuilder();

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] is '[' or ']')
            {
                if (literal.Length > 0) { yield return literal.ToString(); literal.Clear(); }
                yield return pattern[i].ToString();
            }
            else if (pattern[i] == '{' && pattern.IndexOf('}', i) is var close and >= 0)
            {
                if (literal.Length > 0) { yield return literal.ToString(); literal.Clear(); }
                yield return pattern[i..(close + 1)];
                i = close;
            }
            else literal.Append(pattern[i]);
        }

        if (literal.Length > 0) yield return literal.ToString();
    }

    private static string Value(string token, TrackTags track, Audiobook? book)
    {
        var inner = token[1..^1];
        var colon = inner.IndexOf(':');
        var name = (colon < 0 ? inner : inner[..colon]).ToLowerInvariant();
        var format = colon < 0 ? null : inner[(colon + 1)..];

        string Number(int? n) =>
            n is > 0 ? n.Value.ToString(format ?? "0") : "";

        // A book overrides the fields whose meaning changes. Everything else -
        // year, extension, the padding syntax - works the same either way.
        if (book is not null)
            switch (name)
            {
                case "author" or "albumartist" or "artist":
                    return Flatten(NamingFormat.Text(book.Author ?? "Unknown author"));

                case "title" or "book" or "album":
                    return Flatten(NamingFormat.Text(book.Title));

                case "narrator":
                    return book.Narrator is null ? "" : Flatten(NamingFormat.Text(book.Narrator));

                // Position in the book, which is the track number where there is
                // one and the file's place in the folder where there is not - a
                // book ripped in one pass often has no numbering at all.
                case "chapter" or "track":
                    var at = book.Files.ToList().FindIndex(f => f.Path == track.Path);
                    var number = track.TrackNumber is > 0 ? track.TrackNumber!.Value
                               : at >= 0 ? at + 1 : 0;
                    return number > 0 ? number.ToString(format ?? "0") : "";

                case "chaptertitle":
                    return Flatten(track.Title.HasText
                        ? MusicHealth.Tidy(track.Title.Text)
                        : System.IO.Path.GetFileNameWithoutExtension(track.Path));

                case "initial":
                    return Initial(book.Author ?? "");
            }

        return name switch
        {
            // Falls through rather than filing under a value the program has
            // already decided is rubbish. A spam album artist put Taio Cruz's
            // "Rokstarr" in a folder called "(djweetart.com)" - the tag was
            // flagged as spam by MusicHealth and used to name a folder anyway.
            // Names are rejected outright when they carry spam: an artist
            // called "(djweetart.com)" has no usable part, and salvaging one
            // would leave a folder called "djweetart".
            "albumartist" => First(track.AlbumArtist, track.Artist),
            "artist" => First(track.Artist, track.AlbumArtist),
            "album" => First(track.Album),

            // A title is salvaged instead, because the spam is usually stuck
            // to something real. Crazy Town's "Outro - WWW.Crazytown.Com"
            // rejected whole came out as "14.mp3"; "Outro" was there all along.
            "title" => Flatten(MusicHealth.Salvage(track.Title.Text)),
            "genre" => Flatten(Text(track.Genre)),
            "year" => Year(track),
            "track" => Number(track.TrackNumber),

            // A disc number of 1 on a single-disc album is noise, and the
            // optional section around it is meant to vanish. Only a real
            // multi-disc set reports one.
            "disc" => Number(track.DiscNumber is > 1 ? track.DiscNumber : null),

            "initial" => Initial(track),
            "ext" => System.IO.Path.GetExtension(track.Path).TrimStart('.').ToLowerInvariant(),
            _ => ""
        };
    }

    private static string Text(TagValue value) =>
        value.HasText ? MusicHealth.Tidy(value.Text) : "";

    /// <summary>
    /// The first of these values fit to name a folder, or nothing.
    ///
    /// Nothing rather than a fallback of its own: an empty field collapses the
    /// optional section around it, and where there is no section the planner
    /// holds the file instead of filing it somewhere invented. Refusing is the
    /// behaviour everywhere else here and it belongs here too - a folder called
    /// "Unknown Artist" is a decision, and one nobody asked for.
    /// </summary>
    private static string First(params TagValue[] values)
    {
        foreach (var value in values)
            if (value.HasText && MusicHealth.Trustworthy(value.Text))
                return Flatten(MusicHealth.Tidy(value.Text));

        return "";
    }

    /// <summary>
    /// A field value with its slashes made harmless.
    ///
    /// The pattern uses "/" to mean a folder boundary and a field value is
    /// split on it afterwards, so a slash inside a title used to become one.
    /// A real library has a track called "Polkarama! (The Chicken Dance/Let's
    /// Get It Started/Take Me Out/Beverly Hillbillies)" and it was filed as
    /// four nested folders, the last of them holding the mp3.
    ///
    /// Done here rather than in Safe, which runs on each path component after
    /// the split and therefore too late to tell a separator the pattern meant
    /// from one that arrived inside a value.
    /// </summary>
    private static string Flatten(string value) =>
        value.Replace('/', '-').Replace('\\', '-');

    private static string Text(string value) => MusicHealth.Tidy(value);

    private static string Year(TrackTags track)
    {
        if (!track.Year.HasText) return "";
        var m = Regex.Match(track.Year.Text, @"\d{4}");
        return m.Success ? m.Value : "";
    }

    /// <summary>
    /// The letter an artist files under. "The Beatles" goes under B, as it does
    /// in every record shop, and anything not starting with a letter goes under
    /// "#" rather than making a folder per digit.
    /// </summary>
    private static string Initial(TrackTags track) =>
        Initial(track.AlbumArtist.HasText ? track.AlbumArtist.Text : track.Artist.Text);

    private static string Initial(string artist)
    {
        var name = Article.Replace(artist.Trim(), "");

        var first = name.FirstOrDefault(char.IsLetterOrDigit);
        return first == default ? "" : char.IsLetter(first) ? char.ToUpperInvariant(first).ToString() : "#";
    }

    /// <summary>
    /// One path component, made legal on Windows.
    ///
    /// More to this than stripping the illegal characters. A component named
    /// CON, NUL or COM1 cannot be created at all, extension or not, and the
    /// failure is an unhelpful access-denied rather than anything that names
    /// the cause. A component ending in a dot or a space can be created by the
    /// API and then not opened by Explorer. Both are inherited from DOS and
    /// both still bite.
    /// </summary>
    public static string Safe(string component)
    {
        var sb = new StringBuilder(component.Length);

        foreach (var c in component)
            sb.Append(c switch
            {
                // Replacements chosen to stay readable rather than to be
                // reversible: "AC/DC" should read "AC-DC", not "AC_DC".
                '/' or '\\' => "-",
                ':' => " -",
                '*' => "+",
                '"' => "'",
                '<' => "(",
                '>' => ")",
                '|' => "-",
                '?' => "",
                _ => c < ' ' ? "" : c.ToString()
            });

        var text = MusicHealth.Tidy(sb.ToString()).TrimEnd('.', ' ');

        if (text.Length == 0) return "";

        // The DOS device names, still reserved, still with the extension
        // ignored - "NUL.mp3" is as unusable as "NUL".
        var stem = text.Split('.')[0].ToUpperInvariant();
        if (Reserved.Contains(stem)) text = "_" + text;

        // A single component may not exceed 255 on NTFS. Trimmed from the end
        // so the front of a title survives, which is the part that identifies it.
        return text.Length <= 200 ? text : text[..200].TrimEnd('.', ' ');
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly Regex Field = new(
        @"\{(\w+)(?::[^}]*)?\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex Article = new(
        @"^(the|a|an)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
