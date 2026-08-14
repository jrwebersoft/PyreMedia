using System.Text.RegularExpressions;

namespace PyreMedia.Core.Organizing;

/// <summary>How far the reading can be trusted without you checking it.</summary>
public enum RipCertainty
{
    /// <summary>A catalogued disc matched. Each file is named, not guessed at.</summary>
    Named,

    /// <summary>
    /// Nothing matched, but the titles fall into an obvious shape - a run of
    /// similar lengths with the odd extra around them. The order is the disc's
    /// own, which is almost always broadcast order.
    /// </summary>
    Ordered,

    /// <summary>
    /// The shape is not obvious and guessing would be worse than asking. The
    /// files are still listed, with what was noticed about them.
    /// </summary>
    NeedsYou
}

/// <summary>One .mkv that came off a disc.</summary>
public sealed record RipTitle(string Path, int Index, double Seconds, int Chapters, long Bytes)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Length => $"{(int)Seconds / 60}:{(int)Seconds % 60:00}";
}

/// <summary>A title left out of the episode list, and why.</summary>
public sealed record SetAside(RipTitle Title, string Why);

/// <summary>What was made of a folder of ripped titles.</summary>
public sealed class RipReading
{
    /// <summary>The titles taken to be episodes, in the order they should be numbered.</summary>
    public List<RipTitle> Episodes { get; init; } = [];

    /// <summary>Everything else, each with the reason it was left out.</summary>
    public List<SetAside> Ignored { get; init; } = [];

    public RipCertainty Certainty { get; set; }

    /// <summary>
    /// What a catalogued disc says each title is, by title index. Null when no
    /// disc matched, which is the ordinary case - the catalogue is contributed
    /// and covers a fraction of what exists.
    /// </summary>
    public Dictionary<int, DiscEntry>? Named { get; set; }

    /// <summary>
    /// What to tell the user, or null when there is nothing worth saying.
    ///
    /// Always shown when the certainty is not <see cref="RipCertainty.Named"/>,
    /// because a wrong episode order is not visible afterwards: the files play,
    /// the names look right, and the mistake surfaces weeks later when episode
    /// four turns out to be episode five.
    /// </summary>
    public string? Caution { get; set; }
}

/// <summary>
/// Making sense of a folder of freshly ripped disc titles.
///
/// MakeMKV names its output after the disc label and the title index -
/// "30 Rock_t00.mkv" - because that is all it knows. The disc itself carries no
/// episode numbers: a Blu-ray holds playlists, and which playlist is episode
/// three is not written down anywhere on it.
///
/// So this does two separate jobs, and keeps them separate on purpose. It works
/// out which titles are episodes at all - discarding the trailer, the
/// behind-the-scenes reel, the "play all" that is every episode end to end, and
/// the second copy of each episode that many discs carry as a chapterless
/// playlist. Then it puts what remains in the disc's own order, which is
/// almost always broadcast order.
///
/// What it will not do is pretend to know more than it does. Where the shape of
/// the disc is not obvious it says so and leaves the numbering to you, because
/// a rip numbered wrongly looks completely correct until the day somebody
/// watches it.
/// </summary>
public static class DiscRip
{
    /// <summary>
    /// MakeMKV's output pattern: anything, then _t and the title index, then
    /// whatever else it felt like adding.
    ///
    /// The trailing part is not decoration to be ignored - a real rip on hand
    /// is named "title_t00_new.mkv", and a pattern anchored to "_t00.mkv"
    /// matched none of the nine files in it. The version, the settings and the
    /// disc all move that suffix about.
    /// </summary>
    private static readonly Regex Ripped =
        new(@"_t(\d{1,3})(?:[_-][A-Za-z0-9]+)*\.mkv$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Whether a filename looks like it came straight off a disc.</summary>
    public static bool IsRipped(string path) => Ripped.IsMatch(path);

    /// <summary>The title index in the name, or -1.</summary>
    public static int IndexOf(string path) =>
        Ripped.Match(path) is { Success: true } m ? int.Parse(m.Groups[1].Value) : -1;

    /// <summary>
    /// True when a set of files is a disc rip rather than an ordinary folder.
    /// Two is the threshold: one file called something_t00.mkv is a film, and
    /// there is nothing to work out.
    /// </summary>
    public static bool LooksLikeARip(IEnumerable<string> files) =>
        files.Count(IsRipped) >= 2;

    /// <summary>
    /// Whether a folder name is a disc's volume label rather than a title.
    ///
    /// MakeMKV names its output folder after the label burnt into the disc, and
    /// those are written for a filesystem rather than for a person:
    /// "MX2-0N-NW2_DES" is a real one, holding nine episodes of an animated
    /// series whose name appears nowhere in it.
    ///
    /// Worth telling apart because a label makes a hopeless search term. Asked
    /// to identify "MX2-0N-NW2_DES" every provider returns nothing, and the
    /// scan reports a folder it could not match rather than a disc it needs a
    /// name for - which is a different problem with a different answer.
    /// </summary>
    public static bool LooksLikeADiscLabel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // A title has spaces. Volume labels on optical media cannot: the
        // formats that allow them are not what mastering houses use.
        if (name.Contains(' ')) return false;

        // And a title has lower case in it somewhere. "MX2-0N-NW2_DES" does
        // not, nor does "SEASON_1_DISC_2".
        if (name.Any(char.IsLower)) return false;

        // Something machine-made about it: a digit, or a separator standing in
        // for a space. Without this, a title someone wrote in capitals - "TENET"
        // - would be mistaken for a label.
        return name.Any(char.IsDigit) || name.Contains('_');
    }

    // A title shorter than this fraction of the typical one is not an episode.
    // Trailers, logos and menu loops sit far below; a genuine short episode of a
    // half-hour show does not.
    private const double TooShort = 0.45;

    // And one this much longer is the "play all" - every episode joined end to
    // end, which every TV disc offers and nobody wants as a file.
    private const double TooLong = 1.75;

    // Close enough in length to be worth *asking* whether two titles are the
    // same footage. Not close enough to conclude it - see SameFootage below.
    private const double WorthComparing = 3.0;

    /// <summary>
    /// Read a folder of ripped titles.
    /// </summary>
    /// <param name="sameFootage">
    /// Whether two titles of near-identical length are actually the same
    /// recording. True, false, or null for "could not tell".
    /// <para>
    /// This exists because length cannot answer it, and assuming otherwise
    /// destroys episodes. A real DVD rip of an animated series has nine
    /// episodes running 21:17 to 21:22, two of them identical to the tenth of a
    /// second - television is cut to a broadcast slot, so of course it does.
    /// Treating equal length as equal content collapsed those nine into two and
    /// discarded the rest. File size is no better: they were all within half a
    /// percent of each other.
    /// </para>
    /// <para>
    /// What does answer it is the audio, through the same acoustic fingerprint
    /// the music side uses. When nothing can answer it - no ffmpeg - both
    /// titles are kept and the reading says so. Keeping a duplicate wastes
    /// disk; dropping an episode loses it.
    /// </para>
    /// </param>
    public static RipReading Read(
        IReadOnlyList<RipTitle> titles,
        Func<RipTitle, RipTitle, bool?>? sameFootage = null)
    {
        if (titles.Count == 0)
            return new RipReading { Certainty = RipCertainty.NeedsYou, Caution = "Nothing to read." };

        var timed = titles.Where(t => t.Seconds > 0).ToList();

        if (timed.Count == 0)
        {
            return new RipReading
            {
                Ignored = [.. titles.Select(t => new SetAside(t, "no length could be read"))],
                Certainty = RipCertainty.NeedsYou,
                Caution = "None of these files would report a length, so there is nothing to "
                        + "compare them by. Name them yourself, or check ffprobe is installed."
            };
        }

        var typical = Median(timed.Select(t => t.Seconds));

        var ignored = new List<SetAside>();
        var kept = new List<RipTitle>();

        foreach (var t in titles.OrderBy(t => t.Index))
        {
            if (t.Seconds <= 0)
            {
                ignored.Add(new SetAside(t, "no length could be read"));
            }
            else if (t.Seconds < typical * TooShort)
            {
                ignored.Add(new SetAside(t, $"{t.Length} - too short beside the usual {Length(typical)}, "
                                          + "so an extra rather than an episode"));
            }
            else if (t.Seconds > typical * TooLong)
            {
                ignored.Add(new SetAside(t, $"{t.Length} - long enough to be every episode joined "
                                          + "together, which is what a disc's \"play all\" is"));
            }
            else
            {
                kept.Add(t);
            }
        }

        // The same episode offered as more than one playlist - common on discs,
        // which carry a chaptered version for the menu and a plain one for the
        // "play all" to string together.
        //
        // Length only nominates a pair. Something that can hear the audio has
        // to confirm it, because a set of episodes cut to the same broadcast
        // slot all have the same length and none of them are copies.
        var deduped = new List<RipTitle>();
        var unsure = 0;

        foreach (var t in kept)
        {
            var twin = deduped.FirstOrDefault(k => Math.Abs(k.Seconds - t.Seconds) <= WorthComparing);

            if (twin is null) { deduped.Add(t); continue; }

            var verdict = sameFootage?.Invoke(twin, t);

            if (verdict != true)
            {
                // Not the same, or nothing could tell. Either way it stays: a
                // duplicate costs disk, a discarded episode is gone.
                if (verdict is null) unsure++;
                deduped.Add(t);
                continue;
            }

            var (keep, drop) = Better(twin, t);

            if (!ReferenceEquals(keep, twin))
            {
                deduped[deduped.IndexOf(twin)] = keep;
            }

            ignored.Add(new SetAside(drop,
                $"the same recording as {keep.Name} - the disc offers this episode twice, "
                + (keep.Chapters > drop.Chapters
                    ? "and this copy has no chapter marks"
                    : "and this is the later of the two")));
        }

        var (certainty, caution) = Judge(deduped, ignored, typical);

        if (unsure > 0)
        {
            caution = $"{unsure} pair(s) of titles are the same length, and without ffmpeg there is "
                    + "no way to tell whether they are the same episode twice or two episodes that "
                    + "simply run to the same slot. Both were kept. "
                    + (caution ?? "");

            certainty = RipCertainty.NeedsYou;
        }

        return new RipReading
        {
            Episodes = [.. deduped.OrderBy(t => t.Index)],
            Ignored = ignored,
            Certainty = certainty,
            Caution = caution
        };
    }

    /// <summary>
    /// Which of two copies of the same episode to keep. Chapters first, then the
    /// larger file, then the earlier title - in that order, because each is a
    /// weaker signal than the one before it.
    /// </summary>
    private static (RipTitle Keep, RipTitle Drop) Better(RipTitle a, RipTitle b)
    {
        if (a.Chapters != b.Chapters) return a.Chapters > b.Chapters ? (a, b) : (b, a);
        if (a.Bytes != b.Bytes) return a.Bytes > b.Bytes ? (a, b) : (b, a);
        return a.Index <= b.Index ? (a, b) : (b, a);
    }

    private static (RipCertainty, string?) Judge(
        List<RipTitle> kept, List<SetAside> ignored, double typical)
    {
        if (kept.Count == 0)
        {
            return (RipCertainty.NeedsYou,
                "Nothing here looks like an episode - every title was either far shorter or far "
                + "longer than the rest. If this disc is a film rather than a series, that is "
                + "expected; name it as a film instead.");
        }

        if (kept.Count == 1)
        {
            return (RipCertainty.NeedsYou,
                "Only one title looks like an episode. That is normal for a film, and unusual for "
                + "a TV disc - worth a look before anything is renamed.");
        }

        // A disc of episodes has episodes of much the same length. A wide spread
        // means the filter has probably kept something that is not an episode,
        // and numbering in order would then be numbering the wrong things.
        var shortest = kept.Min(t => t.Seconds);
        var longest = kept.Max(t => t.Seconds);

        if (longest > shortest * 1.5)
        {
            return (RipCertainty.NeedsYou,
                $"These run from {Length(shortest)} to {Length(longest)}, which is a wider spread "
                + "than a set of episodes usually has. One of them may be an extra that has been "
                + "counted as an episode. Check the order before applying it.");
        }

        var note = $"Read {kept.Count} episodes from the disc, in title order"
                 + (ignored.Count > 0 ? $", setting aside {ignored.Count} other title(s)." : ".")
                 + " Disc order is almost always broadcast order, but the disc does not actually "
                 + "say so - check the first and last against the episode list before applying.";

        return (RipCertainty.Ordered, note);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;

        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static string Length(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
}
