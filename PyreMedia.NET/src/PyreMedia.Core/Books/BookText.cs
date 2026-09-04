using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PyreMedia.Core.Books;

/// <summary>
/// The words in an ebook.
///
/// No OCR anywhere near this. An EPUB is a zip of XHTML - the text is already
/// text, written by the publisher, and reading it out is exact rather than a
/// best guess at a photograph of a page. That is the whole difference between
/// this and a comic: a comic has to be looked at, a book only has to be opened.
///
/// Only EPUB. A PDF may be text or may be a scan and there is no way to know
/// without a PDF engine; MOBI and AZW3 are Amazon's, compressed with schemes
/// that would want a library of their own. Those are said plainly rather than
/// half-attempted.
/// </summary>
public static class BookText
{
    /// <summary>Everything between the tags, tags dropped.</summary>
    private static readonly Regex Tags = new(
        @"<[^>]+>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <summary>Anything whose content is not prose.</summary>
    private static readonly Regex NotProse = new(
        @"<(script|style|head)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>Where one block of text ends and the next begins.</summary>
    private static readonly Regex Breaks = new(
        @"</(p|div|h[1-6]|li|br|tr)\s*>|<br\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static readonly Regex Blank = new(
        @"[ \t]*\r?\n(?:[ \t]*\r?\n)+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    public static bool CanRead(string path) =>
        string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The book, one entry per chapter file, in the order the archive holds
    /// them.
    ///
    /// Reading order proper would mean following the OPF spine, and it is worth
    /// saying why that is not done: the spine gives the right order, but this is
    /// for finding a phrase, and a phrase is as findable in one order as
    /// another. Sorting by name keeps chapters roughly right without making the
    /// whole thing depend on an OPF that some books get wrong.
    /// </summary>
    public static List<TextPage> Read(string epub, CancellationToken ct = default)
    {
        var pages = new List<TextPage>();

        using var zip = ZipFile.OpenRead(epub);

        var chapters = zip.Entries
            .Where(e => Path.GetExtension(e.FullName).ToLowerInvariant()
                            is ".xhtml" or ".html" or ".htm")
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var number = 0;

        foreach (var entry in chapters)
        {
            ct.ThrowIfCancellationRequested();

            number++;

            string html;

            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                html = reader.ReadToEnd();
            }
            catch (Exception)
            {
                // One unreadable chapter is not a reason to lose the book.
                pages.Add(new TextPage(number, ""));
                continue;
            }

            pages.Add(new TextPage(number, Strip(html)));
        }

        return pages;
    }

    /// <summary>XHTML to plain words.</summary>
    internal static string Strip(string html)
    {
        var text = NotProse.Replace(html, " ");

        // Turned into line breaks before the tags go, or every paragraph in a
        // chapter runs into one line and a search result is the whole chapter.
        text = Breaks.Replace(text, "\n");
        text = Tags.Replace(text, " ");

        text = WebUtility.HtmlDecode(text);

        // Spaces collapsed per line, blank runs collapsed to one.
        var lines = text.Split('\n')
            .Select(l => Regex.Replace(l, @"[ \t ]+", " ").Trim());

        return Blank.Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    /// <summary>Why a book cannot be read, for saying so instead of failing.</summary>
    public static string? WhyNot(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".epub") return null;

        return ext switch
        {
            ".pdf" => "a PDF may be text or may be pictures of text, and telling them apart "
                    + "needs a PDF engine this does not have",
            ".mobi" or ".azw" or ".azw3" =>
                $"{ext} is Amazon's format and is compressed in a way that needs a library of its own",
            _ => $"{ext} is not a book format this can read"
        };
    }
}
