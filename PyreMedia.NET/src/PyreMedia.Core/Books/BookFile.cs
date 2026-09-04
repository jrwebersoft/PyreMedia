using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PyreMedia.Core.Books;

/// <summary>What a book says about itself, or what its filename suggests.</summary>
public sealed record BookRef
{
    public required string Title { get; init; }

    public string? Author { get; init; }
    public string? Series { get; init; }

    /// <summary>Position in a series, where it is known. "2", "2.5" - both occur.</summary>
    public string? SeriesIndex { get; init; }

    public string? Year { get; init; }
    public string? Publisher { get; init; }
    public string? Isbn { get; init; }
    public string? Language { get; init; }

    /// <summary>True when this came out of the file's own metadata rather than its name.</summary>
    public bool FromInside { get; init; }
}

/// <summary>
/// Reading an ebook.
///
/// An EPUB is a zip with an OPF inside it, and that OPF is Dublin Core: title,
/// creator, publisher, date, identifier, language - written by whoever made the
/// book rather than by whoever named the file. So unlike a comic, most ebooks
/// know what they are, and the filename is the fallback rather than the source.
///
/// That ordering matters. "9780140328721.epub" is a perfectly common filename
/// and says nothing; the OPF inside it says "Fantastic Mr Fox, Roald Dahl,
/// Puffin, 1988". Guessing from the name when the answer is inside the file
/// would be choosing the worse evidence.
/// </summary>
public static class BookFile
{
    public static readonly string[] Extensions = [".epub", ".mobi", ".azw3", ".azw", ".pdf", ".fb2"];

    /// <summary>Formats whose metadata can actually be read, as opposed to merely filed.</summary>
    public static readonly string[] Readable = [".epub"];

    public static bool IsBook(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool CanReadInside(string path) =>
        Readable.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Read a book, from the inside where possible and from the name otherwise.
    /// </summary>
    public static BookRef? Read(string path)
    {
        if (CanReadInside(path))
        {
            try
            {
                if (FromEpub(path) is { } inside) return inside;
            }
            catch (Exception)
            {
                // A malformed zip, a missing OPF, an encrypted book. The
                // filename is still there and still says something.
            }
        }

        return FromName(Path.GetFileName(path));
    }

    /// <summary>
    /// Read a book from its name alone, without opening it.
    ///
    /// For the setting that says not to open every file. That used to be done
    /// by calling <see cref="Read"/> and discarding the answer when it came
    /// from inside - which opened the archive anyway, two or three times over,
    /// and then filed the book as unidentifiable because the name had never
    /// been consulted. The setting made the scan slower and its results worse.
    /// </summary>
    public static BookRef? FromNameOnly(string path) => FromName(Path.GetFileName(path));

    // ---------------- Inside the file ----------------

    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";

    private static BookRef? FromEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        // The spec puts a pointer at META-INF/container.xml rather than fixing
        // the OPF's location, and publishers use it: OEBPS/content.opf,
        // EPUB/package.opf and plain content.opf are all in the wild.
        var pointer = zip.GetEntry("META-INF/container.xml");
        if (pointer is null) return null;

        string opfPath;

        using (var s = pointer.Open())
        {
            opfPath = XDocument.Load(s)
                .Descendants(Container + "rootfile")
                .Select(r => (string?)r.Attribute("full-path"))
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
        }

        if (opfPath.Length == 0) return null;

        var opf = zip.GetEntry(opfPath);
        if (opf is null) return null;

        using var os = opf.Open();
        var doc = XDocument.Load(os);

        string? One(string name) => doc.Descendants(Dc + name)
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);

        var title = One("title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Calibre writes series into a meta element rather than Dublin Core,
        // because Dublin Core has nowhere to put it. Almost every EPUB in a
        // personal library has been through Calibre at some point.
        string? Meta(string name) => doc.Descendants(Opf + "meta")
            .Where(m => string.Equals((string?)m.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))
            .Select(m => (string?)m.Attribute("content"))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var isbn = doc.Descendants(Dc + "identifier")
            .Select(e => e.Value.Trim())
            .Select(v => Regex.Match(v, @"\d{13}|\d{9}[\dXx]"))
            .Where(m => m.Success)
            .Select(m => m.Value)
            .FirstOrDefault();

        return new BookRef
        {
            Title = title!,
            Author = One("creator"),
            Publisher = One("publisher"),
            Year = One("date") is { } d && Regex.Match(d, @"(19|20)\d{2}") is { Success: true } ym
                ? ym.Value
                : null,
            Language = One("language"),
            Isbn = isbn,
            Series = Meta("calibre:series"),
            SeriesIndex = Meta("calibre:series_index") is { } ix ? Trim(ix) : null,
            FromInside = true
        };
    }

    /// <summary>"3.0" is book three, not book three point zero.</summary>
    private static string Trim(string index) =>
        index.EndsWith(".0", StringComparison.Ordinal) ? index[..^2] : index;

    // ---------------- The filename ----------------

    /// <summary>
    /// A person written surname-first: "Rice, Anne", "Crichton, Michael,
    /// 1942-2008", or the same with the comma sanitised to an underscore by
    /// whatever wrote the file.
    ///
    /// The capital after the separator is what makes this work. "Vittorio, the
    /// Vampire" and "Dealing_ or The Berkeley-to-Boston Blues" are titles that
    /// also carry a comma, and both are rejected because what follows it is a
    /// lower-case article rather than a given name.
    /// </summary>
    private static readonly Regex SurnameFirst = new(
        @"^\p{Lu}[\p{L}'’.\-]+[,_]\s+\p{Lu}", RegexOptions.Compiled);

    /// <summary>"Title (Series #2)" or "Title (Series 02)".</summary>
    private static readonly Regex SeriesInBrackets = new(
        @"\((?<series>[^)]+?)[\s#]+(?<index>\d{1,3}(?:\.\d+)?)\)", RegexOptions.Compiled);

    private static readonly Regex YearInBrackets = new(@"\((19|20)\d{2}\)", RegexOptions.Compiled);

    private static BookRef? FromName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var year = YearInBrackets.Match(name) is { Success: true } y ? y.Value.Trim('(', ')') : null;
        var work = YearInBrackets.Replace(name, " ");

        string? series = null, index = null;

        if (SeriesInBrackets.Match(work) is { Success: true } s)
        {
            series = s.Groups["series"].Value.Trim();
            index = s.Groups["index"].Value;
            work = work.Remove(s.Index, s.Length);
        }

        work = Regex.Replace(work, @"\s+", " ").Trim(' ', '-', '_');

        var (title, author) = SplitNameAndAuthor(work);

        if (title.Length == 0) return null;

        return new BookRef
        {
            Title = title,
            Author = author,
            Series = series,
            SeriesIndex = index,
            Year = year,
            FromInside = false
        };
    }

    /// <summary>
    /// Which side of the dash is the book and which is the person.
    ///
    /// This used to be settled by assumption - the whole shape was called
    /// "Author - Title" - and the assumption was backwards for most files.
    /// Both orders are real and they sit in the same folder: "Angel Time -
    /// Rice_ Anne" is title-first, "Crichton, Michael - Scratch One" is
    /// author-first. Guessing one way filed 28 Anne Rice novels under folders
    /// named after the books, each holding a file named after the writer.
    ///
    /// So the side that looks like a person decides, and only a surname-first
    /// name counts as looking like one - it is the one shape a title almost
    /// never takes. When neither side says, the order is the one Calibre
    /// writes by default, which is {title} - {authors}.
    /// </summary>
    private static (string Title, string? Author) SplitNameAndAuthor(string work)
    {
        var parts = work.Split(" - ", StringSplitOptions.TrimEntries)
                        .Where(p => p.Length > 0)
                        .ToArray();

        if (parts.Length < 2) return (work, null);

        var person = Array.FindIndex(parts, p => SurnameFirst.IsMatch(p));

        if (person >= 0)
        {
            // The title is whatever sits before the name, or after it when the
            // name came first. "Jurassic Park - Crichton, Michael - First
            // Draft" is the book, the writer, and a note about the edition.
            var titleAt = person > 0 ? person - 1 : 1;

            return (parts[titleAt], Restore(parts[person]));
        }

        // Neither side is written surname-first: "Binary - Michael Crichton".
        return (parts[0], string.Join(" - ", parts[1..]));
    }

    /// <summary>
    /// Put back the comma a sanitiser turned into an underscore. "Rice_ Anne"
    /// is a filename's way of writing "Rice, Anne", and only in a name is that
    /// unambiguous - a title's underscore just as often stands for a colon,
    /// which no Windows filename could hold anyway.
    /// </summary>
    private static string Restore(string author) =>
        Regex.Replace(author, @"_(\s)", ",$1").Trim();
}
