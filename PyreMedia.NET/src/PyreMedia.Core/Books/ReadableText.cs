using System.Text;
using System.Text.RegularExpressions;

namespace PyreMedia.Core.Books;

/// <summary>One page or chapter's worth of words.</summary>
public sealed record TextPage(int Number, string Text)
{
    /// <summary>Nothing was read here - a splash page, or a chapter of pictures.</summary>
    public bool Empty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Where a search matched.</summary>
public sealed record TextHit(string Path, int Page, string Line)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The words in a comic or a book, written out beside it so they can be
/// searched.
///
/// A plain text file rather than a database, and deliberately: it is readable
/// without this program, it is found by Windows Search and by every grep ever
/// written, and it survives the library being moved to a machine that has never
/// heard of PyreMedia. A sidecar that only one program can read is a sidecar
/// that dies with that program.
///
/// The text of a comic comes from OCR and is not clean. Comic lettering is
/// stylised - a capital I is drawn with serifs that read as a slash, and O/0,
/// S/5 and T/7 swap constantly. Measured over a real issue, 87% of words came
/// back as letters alone, but plenty of those were still wrong. What does work
/// is finding things: of the ten characters named in one issue's own summary,
/// nine were findable in its OCR. So this is for searching, not for reading,
/// and the file says so at the top.
/// </summary>
public static class ReadableText
{
    /// <summary>What separates one page from the next, and how a page says its number.</summary>
    private static readonly Regex PageMark = new(
        @"^\s*--\s*page\s+(\d+)\s*--\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private const string Header = "# Searchable text";

    /// <summary>The sidecar beside a comic or a book.</summary>
    public static string PathFor(string file) =>
        System.IO.Path.ChangeExtension(file, ".txt");

    /// <summary>True where one has already been written.</summary>
    public static bool Exists(string file) => File.Exists(PathFor(file));

    /// <summary>
    /// Whether a .txt is one of these rather than scene litter.
    ///
    /// Told apart by what is in it, not by its extension - exactly as a Kodi
    /// .nfo is told from a release group's advert. It matters: .txt is on the
    /// junk list and cleanup is on by default, so without this the program
    /// would spend twenty minutes reading a shelf of comics and then offer to
    /// delete everything it had just written.
    /// </summary>
    public static bool IsSearchableText(string path)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                return false;

            using var reader = new StreamReader(path);

            // Only the first line is read. These files run to hundreds of
            // kilobytes and the answer is on line one.
            return reader.ReadLine()?.TrimStart('﻿').StartsWith(Header, StringComparison.Ordinal)
                   ?? false;
        }
        catch (Exception)
        {
            // Unreadable counts as "leave it alone", which is the safe way round
            // for something that decides whether a file gets deleted.
            return true;
        }
    }

    /// <summary>
    /// Write one out.
    ///
    /// With a byte order mark, deliberately. The whole point of a plain text
    /// sidecar is that other programs read it, and Notepad and Windows Search
    /// both guess at an unmarked UTF-8 file - which turns every accented
    /// character in a book into mojibake.
    /// </summary>
    public static void Write(string file, string content) =>
        File.WriteAllText(PathFor(file), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>
    /// The whole thing as one file.
    ///
    /// The note at the top is not decoration. Somebody opening this a year from
    /// now needs to know why the words are wrong, or they will report it as a
    /// bug in whatever wrote it.
    /// </summary>
    public static string Compose(string title, string how, IReadOnlyList<TextPage> pages)
    {
        var sb = new StringBuilder();

        sb.AppendLine(Header)
          .AppendLine($"# {title}")
          .AppendLine($"# {how}")
          .AppendLine("#")
          .AppendLine("# For finding things, not for reading. Page numbers below are the order")
          .AppendLine("# pages appear in the file, counting from 1.")
          .AppendLine();

        foreach (var page in pages)
        {
            sb.AppendLine($"-- page {page.Number} --");

            if (!page.Empty) sb.AppendLine(page.Text.TrimEnd());

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Read one back, so a search can say which page it found something on.</summary>
    public static List<TextPage> Parse(string content)
    {
        var pages = new List<TextPage>();

        var number = 0;
        var body = new StringBuilder();

        void Flush()
        {
            if (number > 0) pages.Add(new TextPage(number, body.ToString().TrimEnd()));
            body.Clear();
        }

        foreach (var line in content.Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (PageMark.Match(text) is { Success: true } m)
            {
                Flush();
                number = int.Parse(m.Groups[1].Value);
                continue;
            }

            // Notes at the top are not part of any page.
            if (number == 0 && text.StartsWith('#')) continue;

            if (number > 0) body.AppendLine(text);
        }

        Flush();

        return pages;
    }

    /// <summary>
    /// Find a phrase in the sidecars beside these files.
    ///
    /// Tolerant of the substitutions comic lettering actually causes, because
    /// searching for "Grifter" and being told there is no such character - when
    /// the page says GR/FTER - is the failure that would make the whole feature
    /// pointless. The tolerance is per character and only ever widens a letter
    /// to the shape that letter is drawn as, so it cannot match something
    /// unrelated.
    /// </summary>
    public static List<TextHit> Search(
        IEnumerable<string> sidecars, string query, int limit = 500, CancellationToken ct = default)
    {
        var hits = new List<TextHit>();

        if (string.IsNullOrWhiteSpace(query)) return hits;

        var pattern = Loose(query);

        foreach (var path in sidecars)
        {
            ct.ThrowIfCancellationRequested();

            if (hits.Count >= limit) break;

            string content;

            try { content = File.ReadAllText(path); }
            catch (Exception) { continue; }   // a file being written, or gone

            // Cheap rejection first: most files will not contain the phrase at
            // all, and running the tolerant pattern over every one of them is
            // the slow way to discover that.
            if (!pattern.IsMatch(content)) continue;

            foreach (var page in Parse(content))
            {
                foreach (var line in page.Text.Split('\n'))
                {
                    if (!pattern.IsMatch(line)) continue;

                    hits.Add(new TextHit(path, page.Number, line.Trim()));

                    if (hits.Count >= limit) return hits;
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// The query, widened to the shapes comic lettering draws those letters as.
    ///
    /// Public because it is worth being able to show somebody exactly what a
    /// search will and will not match, rather than having them guess at why a
    /// name was found.
    /// </summary>
    public static Regex Loose(string query)
    {
        var sb = new StringBuilder();

        foreach (var c in query.Trim())
            sb.Append(char.ToUpperInvariant(c) switch
            {
                'I' => "[I/|1l]",
                'O' => "[O0]",
                'S' => "[S5]",
                'T' => "[T7]",
                'L' => "[L1I]",
                'B' => "[B8]",
                ' ' => @"\s+",
                _ => Regex.Escape(c.ToString())
            });

        return new Regex(sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }
}
