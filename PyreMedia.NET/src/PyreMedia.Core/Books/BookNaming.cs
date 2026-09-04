using System.Text;
using System.Text.RegularExpressions;

namespace PyreMedia.Core.Books;

/// <summary>
/// Where a comic or a book goes, as a pattern.
///
/// The same engine as the music side, for the same reason: one pattern has to
/// serve a library where some things have a year and some do not, and the way
/// to do that is optional sections that vanish whole rather than leaving
/// "Saga () #001" behind.
///
/// Publisher is a field for comics and not for books, because that is how the
/// two are actually shelved. Comics are published in runs under an imprint -
/// Image, Vertigo, Dark Horse - and people who collect them think in those
/// terms. Nobody looks for a novel under Hodder. So {publisher} is offered on
/// the comic pattern and is not a field a book pattern knows about at all,
/// which means asking for it there is reported as a mistake rather than
/// silently producing an empty folder.
///
/// Even for comics it stays a field rather than a fixed level. Komga and Kavita
/// expect to find a series folder and do not care what sits above it, so
/// whether to have that level is a preference - exactly the sort of thing that
/// belongs in a pattern rather than in the code.
/// </summary>
public static class BookNaming
{
    /// <summary>Series folder, then issue. What most readers and Mylar expect.</summary>
    public const string ComicDefault = "{series}[ ({year})]/{series}[ ({year})] #{issue:000}";

    /// <summary>The same with the publisher above it.</summary>
    public const string ComicByPublisher = "{publisher}/{series}[ ({year})]/{series}[ ({year})] #{issue:000}";

    /// <summary>Author, then title. What Calibre produces and what most people expect.</summary>
    public const string BookDefault = "{author}/{title}[ ({year})]";

    /// <summary>With the series between them, for anything that has one.</summary>
    public const string BookBySeries = "{author}/[{series}/][{index} - ]{title}[ ({year})]";

    public static readonly IReadOnlyDictionary<string, string> ComicFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["series"] = "The series title.",
            ["issue"] = "Issue number. Pad it: {issue:000}.",
            ["volume"] = "Volume, where the file says - v2, Vol. 3.",
            ["year"] = "The year the series began, not the date on the cover.",
            ["publisher"] = "Who published it, where the comic or a provider says.",
            ["kind"] = "Annual, One-Shot, TPB - empty for an ordinary issue.",
            ["title"] = "The issue's own title, where it has one. Usually only from inside the file.",
            ["coveryear"] = "The year on this issue's cover, which is not the series year.",
        };

    public static readonly IReadOnlyDictionary<string, string> BookFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["author"] = "Who wrote it.",
            ["title"] = "The book's title.",
            ["series"] = "The series, where it belongs to one.",
            ["index"] = "Position in that series. Pad it: {index:00}.",
            ["year"] = "Four digits, where it is known.",
            ["isbn"] = "ISBN, where the file carries one.",
        };

    /// <summary>Where a comic goes, relative to the library root, without an extension.</summary>
    public static string Path(string pattern, ComicRef comic, string? publisher = null) =>
        Build(pattern, name => name switch
        {
            "series" => comic.Series,
            "issue" => comic.Issue ?? "",
            "volume" => comic.Volume?.ToString() ?? "",
            "year" => comic.Year ?? "",

            // The caller's answer wins, because it comes from a provider that
            // was asked about this series. The comic's own is the fallback.
            //
            // And a name rather than nothing when neither knows. An empty
            // component is dropped by Build, so a pattern that begins with
            // {publisher} quietly filed those comics one level higher: on the
            // measured library 872 of 1,935 files landed outside the publisher
            // folders and 58 series were split across both places, with nothing
            // held and nothing said. Named the same way a book with no author
            // is, so the gap is a folder you can work through rather than a
            // library in two halves.
            "publisher" => Given(publisher) ?? Given(comic.Publisher) ?? "Unknown publisher",

            "kind" => comic.Kind == ComicKind.Issue ? "" : Spaced(comic.Kind),
            "title" => comic.Title ?? "",
            "coveryear" => comic.CoverYear ?? "",
            _ => ""
        });

    /// <summary>Where a book goes.</summary>
    public static string Path(string pattern, BookRef book) =>
        Build(pattern, name => name switch
        {
            "author" => book.Author ?? "Unknown author",
            "title" => book.Title,
            "series" => book.Series ?? "",
            "index" => book.SeriesIndex ?? "",
            "year" => book.Year ?? "",
            "isbn" => book.Isbn ?? "",
            _ => ""
        });

    /// <summary>The value, or null where there is nothing usable in it.</summary>
    private static string? Given(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Spaced(ComicKind kind) => kind switch
    {
        ComicKind.OneShot => "One-Shot",
        ComicKind.TradePaperback => "TPB",
        _ => kind.ToString()
    };

    /// <summary>
    /// What is wrong with a pattern, or null. Said before a scan rather than
    /// after one, because a pattern with no issue number in it puts every issue
    /// of a series on the same filename.
    /// </summary>
    public static string? Problems(string pattern, bool comic)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "The pattern is empty.";

        var depth = 0;

        foreach (var c in pattern)
        {
            if (c == '[') depth++;
            if (c == ']') depth--;
            if (depth < 0) return "There is a ] with no [ before it.";
        }

        if (depth != 0) return "There is a [ with no ] after it.";

        var known = comic ? ComicFields : BookFields;

        foreach (Match m in Regex.Matches(pattern, @"\{(\w+)(?::[^}]+)?\}"))
        {
            if (!known.ContainsKey(m.Groups[1].Value))
                return $"There is no field called {{{m.Groups[1].Value}}}.";
        }

        if (comic && !pattern.Contains("{issue", StringComparison.OrdinalIgnoreCase))
            return "Without {issue} every issue of a series lands on the same filename.";

        if (!comic && !pattern.Contains("{title", StringComparison.OrdinalIgnoreCase))
            return "Without {title} every book by an author lands on the same filename.";

        return null;
    }

    // ---------------- The engine ----------------

    private static string Build(string pattern, Func<string, string> value)
    {
        var sb = new StringBuilder();

        // An optional section is kept only if every field inside it produced
        // something. That is what lets one pattern serve a library where half
        // the things have a year.
        var section = new StringBuilder();
        var inSection = false;
        var sectionEmpty = false;

        foreach (var token in Tokenise(pattern))
        {
            if (token == "[") { inSection = true; sectionEmpty = false; section.Clear(); continue; }

            if (token == "]")
            {
                if (!sectionEmpty) sb.Append(section);
                inSection = false;
                continue;
            }

            var target = inSection ? section : sb;

            if (token.StartsWith('{'))
            {
                var text = Field(token, value);

                if (text.Length == 0 && inSection) sectionEmpty = true;

                target.Append(text);
            }
            else target.Append(token);
        }

        // Each path component separately, so a slash in a title becomes a dash
        // rather than an unexpected folder.
        var parts = sb.ToString()
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Safe(p.Trim()))
            .Where(p => p.Length > 0)
            .ToList();

        return string.Join(System.IO.Path.DirectorySeparatorChar, parts);
    }

    private static string Field(string token, Func<string, string> value)
    {
        var inner = token[1..^1];
        var colon = inner.IndexOf(':');
        var name = colon < 0 ? inner : inner[..colon];
        var format = colon < 0 ? null : inner[(colon + 1)..];

        var text = value(name.ToLowerInvariant()).Trim();

        if (text.Length == 0) return "";

        // Padding applies to whole numbers only. "13.1" and "0.5" are issue
        // numbers that would be destroyed by being parsed and reformatted.
        if (format is { Length: > 0 } && int.TryParse(text, out var n))
            return n.ToString(format);

        return Flatten(text);
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

    private static string Flatten(string value) => value.Replace('/', '-').Replace('\\', '-');

    /// <summary>
    /// One path component, made safe for Windows.
    ///
    /// The same rules the music side settled on: replacements chosen to stay
    /// readable rather than reversible, and the reserved device names avoided
    /// entirely - a folder called CON cannot be created at all, and a series
    /// called "Nul" is not impossible.
    /// </summary>
    public static string Safe(string component)
    {
        var sb = new StringBuilder(component.Length);

        foreach (var c in component)
            sb.Append(c switch
            {
                '/' or '\\' => "-",
                ':' => " -",
                '*' => "+",
                '"' => "'",
                '<' => "(",
                '>' => ")",
                '|' => "-",
                '?' => "",
                _ => char.IsControl(c) ? "" : c.ToString()
            });

        var text = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');

        string[] devices =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];

        if (devices.Contains(text, StringComparer.OrdinalIgnoreCase)) text += "_";

        return text;
    }

    /// <summary>
    /// A worked example, to show under the box as it is typed.
    /// </summary>
    /// <param name="known">
    /// Whether the example comic knows its own publisher. Both are worth
    /// seeing where the pattern uses {publisher}: the preview used to hand in
    /// "Image Comics" always, so a pattern that files by publisher looked tidy
    /// in the box whatever it would do to a library where most files carry no
    /// publisher at all.
    /// </param>
    public static string Example(string pattern, bool comic, bool known = true)
    {
        if (Problems(pattern, comic) is not null) return "";

        if (comic)
        {
            return Path(pattern, new ComicRef
            {
                Series = "Saga", Issue = "7", Year = "2012", Volume = 1
            }, known ? "Image Comics" : null) + ".cbz";
        }

        return Path(pattern, new BookRef
        {
            Title = "The Long Way to a Small, Angry Planet",
            Author = "Becky Chambers",
            Series = "Wayfarers",
            SeriesIndex = "1",
            Year = "2014"
        }) + ".epub";
    }
}
