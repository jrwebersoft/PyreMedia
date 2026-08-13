using System.Text;
using System.Text.RegularExpressions;
using PyreMedia.Core.Metadata;

namespace PyreMedia.Core.Music;

/// <summary>What is wrong with a piece of tag text.</summary>
public enum Ailment
{
    /// <summary>The field is empty and something downstream needs it.</summary>
    Missing,

    /// <summary>
    /// A word a program wrote because it had nothing: "Unknown Artist",
    /// "Track 03", "AudioTrack 01". Worse than empty, because it looks like an
    /// answer and every scraper downstream believes it.
    /// </summary>
    Placeholder,

    /// <summary>
    /// UTF-8 bytes that were read as cp1252 and then saved back that way, so
    /// "Björk" is stored as "BjÃ¶rk". Reversible exactly - see Repair.
    /// </summary>
    Mojibake,

    /// <summary>A URL, a release-group tag, or whoever ripped it, written into a field.</summary>
    Spam,

    /// <summary>Leading, trailing or doubled spaces. Invisible, and they break grouping.</summary>
    Whitespace,

    /// <summary>The track number written into the title as well as the number field.</summary>
    NumberInTitle,

    /// <summary>The performer written into the title, where the artist field already has it.</summary>
    ArtistInTitle,

    /// <summary>A year that cannot be right: zero, before recorded sound, or in the future.</summary>
    BadYear,

    /// <summary>A genre that says nothing: "Other", "Unknown", or a bare ID3 number.</summary>
    BadGenre,

    /// <summary>SHOUTING, or all lower case. Cosmetic, and it makes a library look broken.</summary>
    Shouting,

    /// <summary>Two tag blocks in one file that disagree about the same field.</summary>
    Conflict,

    /// <summary>Text that had to be guessed at, or that no encoding read sensibly.</summary>
    Doubtful
}

/// <summary>
/// One thing wrong with one file, and the fix if there is a safe one.
/// </summary>
/// <param name="Suggested">
/// What the value should be. Null when the problem is real but the answer is
/// not derivable from the file - "Unknown Artist" is certainly wrong and
/// nothing here knows who the artist is.
/// </param>
public sealed record Finding(
    TrackTags Track,
    Ailment Ailment,
    string Field,
    string Value,
    string? Suggested,
    string Why)
{
    /// <summary>
    /// True when the fix is derivable from the file itself and reversible in
    /// principle - no lookup, no guess, no network.
    /// </summary>
    public bool Fixable => Suggested is not null && Suggested != Value;
}

/// <summary>
/// Finds tag text that is wrong, and says which of it can be fixed without
/// asking anybody.
///
/// The split matters more than the detection. A library this old has two very
/// different kinds of bad data in it: damage, where the right answer is still
/// in the file and only needs undoing, and absence, where a program wrote a
/// word to fill a hole and the real answer was never there. Mojibake, stray
/// whitespace and a track number duplicated into the title are all damage and
/// all reversible. "Unknown Artist" is absence, and no amount of cleverness
/// recovers it from the file - it needs a lookup or a person, and pretending
/// otherwise is how a library gets confidently mislabelled.
///
/// So everything here reports, and only the reversible half suggests.
/// </summary>
public static class MusicHealth
{
    public static List<Finding> Examine(TrackTags track)
    {
        var findings = new List<Finding>();

        void Look(string field, TagValue value, bool required)
        {
            if (!value.HasText)
            {
                if (required)
                    findings.Add(new(track, Ailment.Missing, field, "", null,
                        $"no {field.ToLowerInvariant()} - this file cannot be filed by tags alone"));
                return;
            }

            var text = value.Text;

            if (value.Confidence is TextConfidence.Guessed or TextConfidence.Unreadable)
                findings.Add(new(track, Ailment.Doubtful, field, text, null,
                    $"the encoding was not declared and had to be {value.Confidence.ToString().ToLowerInvariant()}"));

            if (Repair(text) is { } repaired)
                findings.Add(new(track, Ailment.Mojibake, field, text, repaired,
                    "UTF-8 text stored as cp1252 - the original characters are recoverable exactly"));

            else if (Tidy(text) != text)
                findings.Add(new(track, Ailment.Whitespace, field, text, Tidy(text),
                    "leading, trailing or doubled spaces"));

            if (Placeholder(text))
                findings.Add(new(track, Ailment.Placeholder, field, text, null,
                    "a word a program wrote because it had nothing - worse than empty, "
                    + "because everything downstream believes it"));

            if (Spam.IsMatch(text))
                findings.Add(new(track, Ailment.Spam, field, text, null,
                    "a web address or a ripper's credit written into the tag"));

            if (Shouting(text))
                findings.Add(new(track, Ailment.Shouting, field, text, null,
                    text.Any(char.IsUpper) ? "written in capitals" : "written in lower case"));
        }

        Look("Title", track.Title, required: true);
        Look("Artist", track.Artist, required: true);
        Look("Album", track.Album, required: true);
        Look("AlbumArtist", track.AlbumArtist, required: false);
        Look("Genre", track.Genre, required: false);

        ExamineTitle(track, findings);
        ExamineYear(track, findings);
        ExamineGenre(track, findings);

        foreach (var conflict in track.Conflicts)
            findings.Add(new(track, Ailment.Conflict, "", "", null, conflict));

        return findings;
    }

    /// <summary>
    /// Undo cp1252-vs-UTF-8 damage, or return null if there is none.
    ///
    /// The damage is a pure function and so is its inverse: bytes that were
    /// UTF-8 got read one at a time as cp1252, so writing those characters back
    /// out as cp1252 and reading them as UTF-8 returns the original exactly.
    ///
    /// Every step is checked rather than assumed. The text must round-trip
    /// through cp1252 without loss, the result must be valid UTF-8, it must
    /// actually differ, and re-damaging the result must reproduce the input.
    /// Text that is merely accented - a correctly stored "Björk" - fails the
    /// second test and is left alone, which is the case that matters, because
    /// running this twice on good text would destroy it.
    /// </summary>
    public static string? Repair(string text)
    {
        // The signatures of the damage. Without this gate every plain-ASCII
        // string would be run through the machinery below for nothing.
        if (!text.Any(c => c is >= '' and <= 'ÿ' or '’' or '“'
                                or '”' or '–' or '—' or '…' or '€')) return null;

        try
        {
            LegacyEncodings.Ensure();
            var cp1252 = Encoding.GetEncoding(1252,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

            var bytes = cp1252.GetBytes(text);
            var repaired = utf8.GetString(bytes);

            if (repaired == text) return null;

            // And the other way, to be sure this is the damage and not a
            // coincidence that happens to decode.
            return cp1252.GetString(utf8.GetBytes(repaired)) == text ? repaired : null;
        }
        catch
        {
            // Not representable in cp1252, or not valid UTF-8 afterwards. Either
            // way the text was not damaged this way, so there is nothing to undo.
            return null;
        }
    }

    /// <summary>Trim the ends and collapse runs of whitespace to one space.</summary>
    public static string Tidy(string text) => Spaces.Replace(text, " ").Trim();

    /// <summary>
    /// Whether a value is fit to name a folder or a file.
    ///
    /// Exists because knowing a value is rubbish and then using it anyway is
    /// the worst of both. A real library filed Taio Cruz's "Rokstarr" under an
    /// artist called "(djweetart.com)" - the album artist tag was spam, this
    /// class had already flagged it as spam, and the planner never asked.
    ///
    /// Deliberately narrow. It rejects what nothing could sensibly file under
    /// and nothing else: a shouted name is ugly and still findable, so it
    /// passes.
    /// </summary>
    public static bool Trustworthy(string? text) =>
        !string.IsNullOrWhiteSpace(text) && !Placeholder(text) && !Spam.IsMatch(text);

    /// <summary>
    /// The usable part of a value, with any spam taken out of it.
    ///
    /// Rejecting a whole value is right for a name - an artist called
    /// "(djweetart.com)" has no usable part. It is wrong for a title, and the
    /// first version of this proved it: Crazy Town's "Outro - WWW.Crazytown.Com"
    /// was refused entirely and the track came out named "14.mp3", which is
    /// worse than the spam it was avoiding. "Outro" was there all along.
    ///
    /// Returns empty only when nothing survives, which is when refusing is
    /// right after all.
    /// </summary>
    public static string Salvage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (Placeholder(text)) return "";

        // Whole words, not matched fragments. Spam is a detector - "www\.\w"
        // deliberately consumes the first letter after the dot to be sure it
        // has found an address - so replacing what it matched left debris:
        // "Outro - WWW.Crazytown.Com" came back as "Outro - razytown". A word
        // containing an address is an address.
        var kept = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !Spam.IsMatch(word))
            .ToArray();

        var stripped = kept.Length == text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length
            ? text
            : string.Join(' ', kept);

        // Nothing was spam, so nothing is touched. The first version trimmed
        // punctuation unconditionally and took the closing bracket off every
        // title that ended in one - "One More Time to Pretend (Immuzikation
        // Remix)" lost its ")", and an album called "2008" filed as
        // "2008 (2008)" lost the bracket too, then had the year appended again
        // on the next scan: "2008 (2008 (2008)", growing by one bracket a pass
        // for ever.
        if (stripped == text) return Tidy(text);

        // Spam did come out, so tidy up what it was hanging off - "Outro -"
        // reads as unfinished where "Outro" does not.
        var cleaned = Tidy(stripped).Trim(' ', '-', '_', '~', '|', '(', '[', ')', ']', ',');

        return Placeholder(cleaned) ? "" : Tidy(cleaned);
    }

    /// <summary>
    /// A value that is a program's way of saying it had nothing.
    ///
    /// Deliberately anchored rather than a substring search. A band really is
    /// called Unknown Mortal Orchestra, and an album really is called Untitled.
    /// </summary>
    public static bool Placeholder(string text)
    {
        var t = Tidy(text).ToLowerInvariant().Trim('[', ']', '(', ')', '<', '>', '-', '_', '.');

        return t.Length == 0
            || t is "unknown" or "unknown artist" or "unknown album" or "unknown title"
                 or "various" or "untitled" or "unnamed" or "no artist" or "no album"
                 or "n/a" or "na" or "none" or "null" or "nil" or "?" or "??" or "???"
                 or "new artist" or "new album" or "misc" or "unsorted" or "unclassifiable"
            || Numbered.IsMatch(t);
    }

    /// <summary>SHOUTING or whispering. Only judged on text long enough to mean it.</summary>
    private static bool Shouting(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < 6) return false;

        // A title of initials is not shouting, and neither is one word.
        if (!text.Contains(' ')) return false;

        return letters.All(char.IsUpper) || letters.All(char.IsLower);
    }

    private static void ExamineTitle(TrackTags track, List<Finding> findings)
    {
        if (!track.Title.HasText) return;
        var text = track.Title.Text;

        // "03 - Song Name" in the title field, where the number field already
        // says 3. Only stripped when the two agree - a title that genuinely
        // starts with a number ("99 Problems", "1979") must survive, and it does
        // because its number is not the track number.
        var lead = LeadingNumber.Match(text);
        if (lead.Success && track.TrackNumber is > 0
            && int.TryParse(lead.Groups[1].Value, out var n) && n == track.TrackNumber
            && text[lead.Length..].Trim().Length > 0)
            findings.Add(new(track, Ailment.NumberInTitle, "Title", text,
                Tidy(text[lead.Length..]),
                $"the title repeats track number {n}, which the number field already holds"));

        // "Song Name (Artist Name)" where the artist field says the same thing.
        // Old rippers did this on compilations.
        if (track.Artist.HasText && Trailing.Match(text) is { Success: true } trailing
            && Duplicates.TitleKey(trailing.Groups[1].Value) == Duplicates.TitleKey(track.Artist.Text))
            findings.Add(new(track, Ailment.ArtistInTitle, "Title", text,
                Tidy(text[..trailing.Index]),
                "the title repeats the artist, which has its own field"));
    }

    private static void ExamineYear(TrackTags track, List<Finding> findings)
    {
        if (!track.Year.HasText) return;

        var text = track.Year.Text;
        var digits = FourDigits.Match(text);

        if (!digits.Success || !int.TryParse(digits.Value, out var year))
        {
            findings.Add(new(track, Ailment.BadYear, "Year", text, null,
                "no four-digit year anywhere in this value"));
            return;
        }

        // 1889 is the first commercial recording. Anything before it is a typo
        // or a zero, and anything past next year has not happened.
        if (year is < 1889 || year > DateTime.Now.Year + 1)
            findings.Add(new(track, Ailment.BadYear, "Year", text, null,
                $"{year} cannot be a release year"));

        // "1997-00-00" and "1997/1/1" both mean 1997 and nothing more.
        else if (digits.Value != text.Trim())
            findings.Add(new(track, Ailment.Whitespace, "Year", text, digits.Value,
                "a full date where only the year is meant"));
    }

    private static void ExamineGenre(TrackTags track, List<Finding> findings)
    {
        if (!track.Genre.HasText) return;
        var text = Tidy(track.Genre.Text);

        // "(17)" and "17" are an ID3v1 genre index that nothing translated.
        var index = GenreNumber.Match(text);
        if (index.Success && int.TryParse(index.Groups[1].Value, out var n))
        {
            var named = n >= 0 && n < Id3Reader.Genres.Length ? Id3Reader.Genres[n] : null;
            findings.Add(new(track, Ailment.BadGenre, "Genre", track.Genre.Text, named,
                named is null
                    ? $"genre number {n} is not one the ID3 list defines"
                    : $"an untranslated ID3 genre number - {n} means {named}"));
            return;
        }

        if (text.ToLowerInvariant() is "other" or "unknown" or "genre" or "misc" or "general")
            findings.Add(new(track, Ailment.BadGenre, "Genre", track.Genre.Text, null,
                "a genre that says nothing"));
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex Numbered = new(
        @"^(track|audiotrack|audio track|title|song|pista|piste)\s*\d*$",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex Spam = new(
        @"(https?://|www\.\w|\.com\b|\.net\b|\.org\b|\bripped\s+by\b|\bencoded\s+by\b"
        + @"|\bexact\s+audio\s+copy\b|\btorrent\b|\buploaded\s+by\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex LeadingNumber = new(
        @"^\s*(\d{1,3})\s*[-._)\]]\s*", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex Trailing = new(
        @"\s*[\(\[]([^)\]]+)[\)\]]\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex FourDigits = new(
        @"\d{4}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex GenreNumber = new(
        @"^\(?(\d{1,3})\)?$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
