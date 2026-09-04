using System.Xml;
using System.Xml.Linq;

namespace PyreMedia.Core.Books;

/// <summary>One block of lettering - a balloon, a caption box, a credit block.</summary>
public sealed record ScriptBlock(int Number, string Text);

/// <summary>One page of a script, with what the page turned out to be.</summary>
public sealed record ScriptPage
{
    public required int Number { get; init; }

    /// <summary>Story, advertisement, editorial - so a reader can skip what is not the comic.</summary>
    public PageKind Kind { get; init; } = PageKind.Story;

    /// <summary>Why it was called that, where it was not simply a story page.</summary>
    public string? Because { get; init; }

    public List<ScriptBlock> Blocks { get; init; } = [];

    public bool Empty => Blocks.Count == 0;
}

/// <summary>A whole book or comic, as script.</summary>
public sealed record Script
{
    public required string Title { get; init; }

    public string? Series { get; init; }
    public string? Issue { get; init; }
    public string? Author { get; init; }
    public string? Year { get; init; }

    /// <summary>
    /// Where the words came from and how much to trust them. "exact" for a book
    /// whose text was read out of it; the engine and language for a comic whose
    /// pages were looked at.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>True where the words are the publisher's own rather than a reading of a picture.</summary>
    public bool Exact { get; init; }

    public List<ScriptPage> Pages { get; init; } = [];

    public int Words => Pages.Sum(p => p.Blocks.Sum(b =>
        b.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
}

/// <summary>
/// The script of a comic or a book, written beside it for another program to
/// index.
///
/// Not a search feature and not a transcript to read. This is a file a comic
/// reader can load to know what is on each page: which pages are story and
/// which are advertising, and what the lettering says, block by block, in
/// reading order. Whatever indexes it later gets to build its own search over
/// the whole library without opening thirty thousand archives again.
///
/// XML, and named .script.nfo, because that is what the rest of this program
/// writes beside media and what every other tool in this corner of the world
/// expects to find. Element names are boring on purpose: an indexer written
/// two years from now should not have to guess.
///
/// The header says how the words were got, and it is the most important part of
/// the file. A book's text is the publisher's own and is exact. A comic's text
/// is a reading of a photograph of hand-drawn lettering, and is wrong often
/// enough that anything built on it must know that before it starts.
/// </summary>
public static class ScriptNfo
{
    /// <summary>Beside the file, under its own name.</summary>
    public static string PathFor(string file) =>
        System.IO.Path.ChangeExtension(file, ".script.nfo");

    public static bool Exists(string file) => File.Exists(PathFor(file));

    /// <summary>The root element, used to tell this from any other .nfo.</summary>
    public const string Root = "comicscript";

    /// <summary>
    /// Whether an .nfo is one of these.
    ///
    /// By its root element, not its extension - .nfo is on the junk list, and a
    /// scene release advert has the same three letters. The same rule already
    /// spares Kodi's own metadata.
    /// </summary>
    public static bool IsScript(string path)
    {
        if (!string.Equals(System.IO.Path.GetExtension(path), ".nfo",
                StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var reader = new StreamReader(path);

            // Read as lines rather than parsed as XML, which matters more than
            // it looks. A scene release advert is plain text, and an XML parser
            // throws on it - so a parser that treats "could not read" as "leave
            // it alone" spares every advert on the disk and quietly undoes the
            // junk cleaning. The same technique the Kodi .nfo check uses.
            for (var i = 0; i < 8; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;

                if (line.Contains('<' + Root, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
        catch (Exception)
        {
            // Genuinely unreadable - locked, or a device that went away. That is
            // a reason to leave it alone, unlike being unparseable.
            return true;
        }
    }

    /// <summary>Write one out.</summary>
    public static void Write(string file, Script script) =>
        Compose(script).Save(PathFor(file));

    /// <summary>The document, separated from the writing so it can be tested.</summary>
    public static XDocument Compose(Script script)
    {
        var root = new XElement(Root,
            new XAttribute("version", "1"),
            new XElement("title", script.Title));

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) root.Add(new XElement(name, value));
        }

        Add("series", script.Series);
        Add("issue", script.Issue);
        Add("author", script.Author);
        Add("year", script.Year);

        // How much to trust every word below it. First, and never optional.
        root.Add(new XElement("text",
            new XAttribute("exact", script.Exact ? "true" : "false"),
            new XAttribute("source", script.Source),
            script.Exact
                ? "The publisher's own text, read out of the file."
                : "Read from pictures of hand-lettered pages. Wrong often enough that "
                  + "anything built on it should treat it as a hint, not a transcript."));

        root.Add(new XElement("pagecount", script.Pages.Count));
        root.Add(new XElement("wordcount", script.Words));

        var pages = new XElement("pages");

        foreach (var page in script.Pages)
        {
            var element = new XElement("page",
                new XAttribute("number", page.Number),
                new XAttribute("kind", page.Kind.ToString()));

            // Only where something was actually decided. An attribute saying
            // "story" on every page is noise an indexer has to skip.
            if (page.Because is { Length: > 0 })
                element.Add(new XAttribute("because", page.Because));

            foreach (var block in page.Blocks)
                element.Add(new XElement("block",
                    new XAttribute("number", block.Number), block.Text));

            pages.Add(element);
        }

        root.Add(pages);

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XComment(" Written by PyreMedia. The script of this file, page by page, for "
                       + "another program to index. Element names will not change without the "
                       + "version attribute above changing with them. "),
            root);
    }

    /// <summary>Read one back.</summary>
    public static Script? Read(string path)
    {
        try
        {
            var doc = XDocument.Load(path);

            if (doc.Root is not { } root
                || !string.Equals(root.Name.LocalName, Root, StringComparison.OrdinalIgnoreCase))
                return null;

            string? Text(string name) =>
                root.Element(name)?.Value is { Length: > 0 } v ? v : null;

            var pages = new List<ScriptPage>();

            foreach (var p in root.Element("pages")?.Elements("page") ?? [])
            {
                var blocks = p.Elements("block")
                    .Select((b, i) => new ScriptBlock(
                        int.TryParse((string?)b.Attribute("number"), out var n) ? n : i + 1,
                        b.Value))
                    .ToList();

                pages.Add(new ScriptPage
                {
                    Number = int.TryParse((string?)p.Attribute("number"), out var num) ? num : pages.Count + 1,
                    Kind = Enum.TryParse<PageKind>((string?)p.Attribute("kind"), true, out var k)
                        ? k : PageKind.Story,
                    Because = (string?)p.Attribute("because"),
                    Blocks = blocks
                });
            }

            return new Script
            {
                Title = Text("title") ?? "",
                Series = Text("series"),
                Issue = Text("issue"),
                Author = Text("author"),
                Year = Text("year"),
                Source = (string?)root.Element("text")?.Attribute("source") ?? "unknown",
                Exact = (string?)root.Element("text")?.Attribute("exact") == "true",
                Pages = pages
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Build a script from pages of text and what was made of them.
    ///
    /// The blocks are already in reading order by the time they arrive here -
    /// grouping loose OCR lines into balloons is <see cref="PageScript"/>'s job,
    /// and a script whose speech is interleaved between two balloons is no use
    /// to anybody.
    /// </summary>
    public static Script From(
        string title,
        string source,
        bool exact,
        IReadOnlyList<TextPage> pages,
        IReadOnlyList<PageVerdict>? verdicts = null)
    {
        var built = new List<ScriptPage>();

        foreach (var page in pages)
        {
            var verdict = verdicts?.FirstOrDefault(v => v.Page == page.Number);

            var blocks = page.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Select(Strip)
                .Select((t, i) => new ScriptBlock(i + 1, t))
                .ToList();

            built.Add(new ScriptPage
            {
                Number = page.Number,
                Kind = verdict?.Looks ?? PageKind.Story,
                Because = verdict is { Suggested: true } ? string.Join("; ", verdict.Because) : null,
                Blocks = blocks
            });
        }

        return new Script
        {
            Title = title,
            Source = source,
            Exact = exact,
            Pages = built
        };
    }

    /// <summary>
    /// Drop the "[1] " a script line carries for a person reading it. The block
    /// number is an attribute here, and having it twice invites an indexer to
    /// search for it.
    /// </summary>
    private static string Strip(string line)
    {
        var close = line.IndexOf("] ", StringComparison.Ordinal);

        return line.StartsWith('[') && close > 0 ? line[(close + 2)..] : line;
    }
}
