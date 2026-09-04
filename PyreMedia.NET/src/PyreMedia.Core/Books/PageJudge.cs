using System.Text.RegularExpressions;

namespace PyreMedia.Core.Books;

/// <summary>What the words on a page suggest it is, and why.</summary>
public sealed record PageVerdict
{
    public required int Page { get; init; }

    /// <summary>Story where nothing said otherwise.</summary>
    public required PageKind Looks { get; init; }

    /// <summary>
    /// What was actually found, in words a person can check against the page.
    ///
    /// The whole point. A score on its own is unarguable and therefore useless -
    /// somebody deciding whether to delete a page needs to see that it says
    /// "ONLY 9.99" and carries a web address, so they can look and disagree.
    /// </summary>
    public List<string> Because { get; init; } = [];

    /// <summary>How much the evidence adds up to. Nothing is acted on by this alone.</summary>
    public int Weight { get; init; }

    public bool Suggested => Looks != PageKind.Story;
}

/// <summary>
/// Reading a page's words and saying what kind of page it looks like.
///
/// This is not image analysis and deliberately so. Guessing "advert" from
/// pixels is the sort of confident wrongness that deletes somebody's story
/// pages. What this does instead is read what is printed and cite it: a page
/// saying ON SALE and carrying a price and a web address is making a claim
/// about itself.
///
/// Typography is the other signal, and it has to be measured against the comic
/// it came from rather than against an idea of comics. Older books are lettered
/// in capitals, so an editorial column set in sentence case stands out - in a
/// 1992 issue the story pages are almost entirely capitals while the house ad
/// and the letters page read as prose. But plenty of modern comics letter their
/// dialogue in mixed case: 4001 A.D. says "You've hurt me... you've hurt Japan
/// grievously, Rai..." in an ordinary sentence, and judged against the older
/// convention two thirds of that comic was called advertising.
///
/// So the baseline is the comic's own. Whatever most of a book looks like is
/// that book's lettering, and only a page that departs from it says anything.
/// Where a comic is mixed-case throughout, typography contributes nothing and
/// the verdict rests on what the page actually says.
///
/// It does not attempt previews. A preview of another series is story art with
/// lettered dialogue and looks exactly like the comic around it - there is no
/// honest way to tell them apart from words alone, so it is not claimed.
/// </summary>
public static class PageJudge
{
    /// <summary>Money, in the shapes comics print it.</summary>
    private static readonly Regex Price = new(
        @"(?<![A-Z0-9])(?:\$\s?\d|\d+\.\d{2}\s*(?:US|CAN|EACH)?\b|ONLY\s+\d+\.\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex Web = new(
        @"(?:www\.[a-z0-9-]+\.[a-z]{2,}|[a-z0-9-]+\.(?:com|net|org|co\.uk)\b|@[A-Za-z][A-Za-z0-9_]{3,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Words that only appear when something is being sold.</summary>
    private static readonly string[] Selling =
    [
        "on sale", "in stores", "coming soon", "pre-order", "preorder", "order now",
        "available now", "subscribe", "subscription", "trade paperback", "isbn",
        "collecting", "in comic shops", "advertisement",
        "allow 6-8 weeks", "money order", "offer expires", "while supplies last",
        "send to", "p.o. box", "catalog", "satisfaction guaranteed", "plus shipping",

        // Teasers for the next issue or another title. House advertising rather
        // than something being posted to you, but a page of it all the same -
        // and "next issue" in particular used to be filed under letters pages,
        // which put a house ad in the one category nobody removes.
        "next issue", "next month", "coming from", "coming in", "on sale now",
        "digital exclusive", "variant cover", "cover gallery"
    ];

    /// <summary>Words that mean a masthead rather than a story.</summary>
    private static readonly string[] Masthead =
    [
        "editor-in-chief", "publisher", "chief operating officer", "chief creative officer",
        "all rights reserved", "trademark", "published monthly", "printed in the usa",
        "no similarity between", "is coincidental", "director of marketing",
        "editorial assistant", "licensing manager", "vp operations"
    ];

    /// <summary>Words that mean a letters column.</summary>
    private static readonly string[] Letters =
    [
        "dear ", "c/o ", "letters to", "write to us", "letter column"
    ];

    /// <summary>
    /// Words put there by whoever scanned the comic, rather than by anybody who
    /// made it.
    ///
    /// A page the ripper added is not part of the book at all, which makes it
    /// the one category that can be removed without losing anything the
    /// publisher printed. Recognised by what such a page says about itself -
    /// these are the phrases scanning groups actually put on their credit
    /// pages.
    /// </summary>
    private static readonly string[] Scanner =
    [
        "scanned by", "scan by", "scanning by", "ripped by", "rip by",
        "if you paid for this", "this comic was", "presented by",
        "join us at", "release group", "for free distribution",
        "support the industry", "buy the comic", "digital comics",
        "cbr/cbz", "dcp ", "minutemen-", "zone-empire"
    ];

    /// <summary>
    /// Judge one page's text.
    /// </summary>
    /// <param name="alreadySaid">
    /// What the file already claims this page is. A scanner's own marking beats
    /// anything guessed here and is returned unchanged - somebody read the comic
    /// to write that.
    /// </param>
    /// <param name="baseline">
    /// How much mixed-case text this comic's ordinary pages carry, from
    /// <see cref="Baseline"/>. Null means it could not be worked out, and then
    /// typography is not used at all rather than guessed at.
    /// </param>
    public static PageVerdict Judge(
        int page, string text, PageKind? alreadySaid = null, double? baseline = null)
    {
        if (alreadySaid is { } said && said != PageKind.Story)
            return new PageVerdict
            {
                Page = page,
                Looks = said,
                Weight = 100,
                Because = [$"the file already says this page is {said.ToString().ToLowerInvariant()}"]
            };

        var why = new List<string>();
        var weight = 0;

        var words = Words(text);

        // Too little to judge. A splash page with three words on it is not
        // evidence of anything, and calling it an advert on one match would be
        // the worst kind of wrong.
        if (words.Count < 12)
            return new PageVerdict { Page = page, Looks = PageKind.Story, Weight = 0 };

        var lower = text.ToLowerInvariant();

        // ---- typography, measured against this comic's own lettering ----
        //
        // Only a departure from the book's own style says anything. Judged
        // against a fixed idea of how comics are lettered, a mixed-case modern
        // comic reads as two thirds advertising.
        if (baseline is { } normal)
        {
            var prose = ProseRatio(words);
            var above = prose - normal;

            if (above >= 0.40)
            {
                weight += 5;
                why.Add($"{prose:P0} of it is sentence-case prose against {normal:P0} "
                        + "on this comic's ordinary pages, so it is typeset rather than lettered");
            }
            else if (above >= 0.25)
            {
                weight += 2;
                why.Add($"it carries more sentence-case prose ({prose:P0}) than this comic's "
                        + $"ordinary pages ({normal:P0})");
            }
        }

        // ---- what it says about itself ----
        var selling = Selling.Where(s => lower.Contains(s)).Take(3).ToList();

        if (selling.Count > 0)
        {
            weight += 2 * selling.Count;
            why.Add("it says " + string.Join(", ", selling.Select(s => $"\"{s.Trim()}\"")));
        }

        if (Price.Match(text) is { Success: true } price)
        {
            weight += 3;
            why.Add($"it prints a price ({price.Value.Trim()})");
        }

        if (Web.Matches(text) is { Count: > 0 } web)
        {
            weight += web.Count >= 2 ? 3 : 2;
            why.Add($"it carries {(web.Count == 1 ? "a web address" : $"{web.Count} web addresses")} "
                    + $"({web[0].Value})");
        }

        var masthead = Masthead.Where(s => lower.Contains(s)).Take(3).ToList();

        if (masthead.Count > 0)
        {
            weight += 3 * masthead.Count;
            why.Add("it reads like a masthead: " + string.Join(", ", masthead.Select(s => $"\"{s.Trim()}\"")));
        }

        var letters = Letters.Where(s => lower.Contains(s)).ToList();

        if (letters.Count > 0)
        {
            weight += 2;
            why.Add("it says " + string.Join(", ", letters.Select(s => $"\"{s.Trim()}\"")));
        }

        // A page the scanner added is the one thing on this list that is not
        // part of the comic at all, so it is worth reaching the threshold on
        // its own - nothing the publisher printed is lost by removing it.
        var ripper = Scanner.Where(s => lower.Contains(s)).Take(2).ToList();

        if (ripper.Count > 0)
        {
            weight += 6;
            why.Add("it says " + string.Join(", ", ripper.Select(s => $"\"{s.Trim()}\""))
                    + ", which is whoever scanned this talking rather than the comic");
        }

        // ---- what all that adds up to ----
        //
        // A masthead is not an advert and should not be offered for removal in
        // the same breath - the indicia is where the copyright lives. A page the
        // scanner added outranks everything, because it is the only one that is
        // not part of the comic.
        var kind = ripper.Count > 0 ? PageKind.ScannerPage
                 : weight < 5 ? PageKind.Story
                 : masthead.Count > 0 ? PageKind.Editorial
                 : letters.Count > 0 && selling.Count == 0 ? PageKind.Letters
                 : PageKind.Advertisement;

        return new PageVerdict
        {
            Page = page,
            Looks = kind,
            Weight = weight,
            Because = kind == PageKind.Story ? [] : why
        };
    }

    /// <summary>
    /// Every page of a comic, judged together.
    ///
    /// Together rather than one at a time on purpose: what counts as unusual
    /// typography can only be decided by looking at the whole book.
    /// </summary>
    public static List<PageVerdict> JudgeAll(
        IReadOnlyList<TextPage> pages, IReadOnlyList<ComicPage>? known = null)
    {
        var baseline = Baseline(pages);
        var verdicts = new List<PageVerdict>(pages.Count);

        foreach (var page in pages)
        {
            var said = known?.FirstOrDefault(k => k.Number == page.Number - 1) is { Tagged: true } k
                ? k.Kind
                : (PageKind?)null;

            verdicts.Add(Judge(page.Number, page.Text, said, baseline));
        }

        return verdicts;
    }

    /// <summary>
    /// How much mixed-case text this comic's ordinary pages carry.
    ///
    /// The median rather than the mean, because a handful of dense advertising
    /// pages would drag an average up and quietly raise the bar for spotting
    /// the rest of them. Null where there are too few pages with enough words
    /// to say anything, in which case typography is simply not used.
    /// </summary>
    public static double? Baseline(IReadOnlyList<TextPage> pages)
    {
        var ratios = pages
            .Select(p => Words(p.Text))
            .Where(w => w.Count >= 12)
            .Select(ProseRatio)
            .OrderBy(r => r)
            .ToList();

        if (ratios.Count < 5) return null;

        return ratios[ratios.Count / 2];
    }

    /// <summary>
    /// How much of the page is written the way prose is written, rather than the
    /// way comic lettering is.
    ///
    /// Counted per word: a word with a lowercase letter in it that is not merely
    /// a capitalised name. Comic balloons are set in capitals, so lowercase is
    /// what typesetting looks like. OCR does misread a capital as lowercase now
    /// and then, which is why this is a proportion over a whole page rather than
    /// a verdict on any one word.
    /// </summary>
    internal static double ProseRatio(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return 0;

        var lowerish = words.Count(w =>
            w.Length >= 3 && w.Skip(1).Any(char.IsLower) && w.Any(char.IsLetter));

        return (double)lowerish / words.Count;
    }

    private static List<string> Words(string text) =>
        [.. text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
               .Select(w => w.Trim('.', ',', '!', '?', '"', '\'', '(', ')', '-', ':', ';'))
               .Where(w => w.Length > 0 && w.Any(char.IsLetter))];
}
