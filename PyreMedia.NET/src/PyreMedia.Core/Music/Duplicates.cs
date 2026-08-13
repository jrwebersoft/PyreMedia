using System.Text;
using System.Text.RegularExpressions;

namespace PyreMedia.Core.Music;

/// <summary>What two files sharing a track number turned out to be.</summary>
public enum DuplicateKind
{
    /// <summary>
    /// Different recordings that happen to carry the same number. Nothing is
    /// duplicated; the numbering is simply wrong and wants correcting.
    /// </summary>
    WrongNumber,

    /// <summary>The same recording, same length, same size. One is a copy of the other.</summary>
    ExactCopy,

    /// <summary>The same recording encoded twice - typically an MP3 and a WMA of one rip.</summary>
    SameRecording,

    /// <summary>
    /// The same title at a materially different length: a live take, a remix, a
    /// radio edit. Only a person can say which one they want.
    /// </summary>
    AlternateVersion,

    /// <summary>
    /// The same title, and no way to tell whether it is the same recording -
    /// nothing measured the lengths. Held rather than guessed at, because the
    /// guess that would otherwise be made is "duplicate", and it discards a file.
    /// </summary>
    Unknown
}

/// <summary>A set of files that claim the same place on an album, and what to do about them.</summary>
public sealed record DuplicateSet(
    DuplicateKind Kind,
    IReadOnlyList<TrackTags> Files,
    TrackTags? Keep,
    string Reason)
{
    /// <summary>True when the rules settle it and nobody needs to be asked.</summary>
    public bool SettledByRule =>
        Kind is not (DuplicateKind.AlternateVersion or DuplicateKind.Unknown);
}

/// <summary>
/// Tells duplicates from alternate versions.
///
/// Measured on a 17,168-file library: of 420 colliding track numbers, 38% were
/// byte-for-byte copies, 32% the same recording in two formats, and 27% were not
/// duplicates at all but different songs carrying the same number. Only the
/// remainder is a question for a person, and the point of this class is to keep
/// that remainder as small as it honestly can be.
/// </summary>
public static class Duplicates
{
    /// <summary>
    /// How far two durations may differ and still be the same recording.
    ///
    /// Not a flat two seconds. An MP3 and a WMA of one rip disagree by more than
    /// that: encoders pad to a whole frame and some decoders report the padding,
    /// so the MP3 reads longer. Across nine tracks of one album the gap ran from
    /// 2.0s to 3.4s, always the same direction - a systematic artefact, not nine
    /// different recordings. Four seconds covers it with room to spare, and the
    /// 2% term keeps that proportional on long tracks without letting a radio
    /// edit (20% shorter, at least) slip through as a duplicate.
    /// </summary>
    public static bool SameLength(double a, double b) =>
        Math.Abs(a - b) <= Math.Max(4.0, Math.Max(a, b) * 0.02);

    /// <summary>
    /// Everything a set of files sharing one track number turns out to be.
    ///
    /// Deliberately not one answer. Three files claiming track 4 are routinely two
    /// copies of one recording plus a differently-titled third, and judging the
    /// whole clash by whether *all* of it agrees leaves the two copies in place to
    /// fight over a path later. Each recording present is classified on its own.
    /// </summary>
    public static List<DuplicateSet> ClassifyAll(
        IReadOnlyList<TrackTags> clash, FingerprintSet? audio = null)
    {
        audio ??= FingerprintSet.None;

        var recordings = Partition(clash, audio);
        var sets = new List<DuplicateSet>();

        if (recordings.Count > 1)
            sets.Add(new(DuplicateKind.WrongNumber, clash, null,
                $"{recordings.Count} different tracks share this number - the numbering is wrong"));

        foreach (var recording in recordings.Where(r => r.Count > 1))
            sets.Add(Classify(recording, audio));

        return sets;
    }

    /// <summary>Split a clash into one list per recording named in it.</summary>
    private static List<List<TrackTags>> Partition(
        IReadOnlyList<TrackTags> clash, FingerprintSet audio)
    {
        var recordings = new List<List<TrackTags>>();

        foreach (var track in clash)
        {
            var home = recordings.FirstOrDefault(r => Together(r[0], track, audio));

            if (home is null) recordings.Add([track]);
            else home.Add(track);
        }

        return recordings;
    }

    /// <summary>
    /// Whether two files are the same recording. The audio answers when it can;
    /// the titles answer when it cannot.
    ///
    /// The audio must go first, because this is where the title rule fails in
    /// both directions. "03 BURN IT DOWN (Swoon Remix)" and "09 Burn It Down"
    /// read as two songs and share 91% of their audio; "Red Pill, Blue Pill [-]"
    /// missed its twin by one character of edit distance.
    /// </summary>
    private static bool Together(TrackTags a, TrackTags b, FingerprintSet audio) =>
        audio.Agreement(a.Path, b.Path) is { } score
            ? score >= Fingerprint.SameRecording
            : Alike(TitleKey(a), TitleKey(b));

    public static DuplicateSet Classify(IReadOnlyList<TrackTags> clash, FingerprintSet? audio = null)
    {
        if (clash.Count < 2)
            return new(DuplicateKind.WrongNumber, clash, clash.FirstOrDefault(), "nothing to compare");

        var heard = (audio ?? FingerprintSet.None).SameRecording(clash);

        // The audio, where there is any, outranks everything below. It is a
        // measurement of the thing being asked about; titles and durations are
        // proxies for it.
        if (heard is false)
            return new(DuplicateKind.WrongNumber, clash, null,
                "the audio differs - these are different recordings however alike "
                + "the titles and lengths look, and nothing is duplicated");

        if (heard is true)
            return Settle(clash, "the audio matches");

        // Different songs first: no length comparison can make two titles one
        // track, and this is where a quarter of all collisions land.
        if (!AllAlike(clash))
            return new(DuplicateKind.WrongNumber, clash, null,
                "different tracks sharing a number - the numbering is wrong, nothing is duplicated");

        var times = clash.Where(t => t.Seconds is > 0).Select(t => t.Seconds!.Value).ToList();

        // Length is the only thing left that separates a second copy from a
        // second version, so without it there is no answer - only a guess that
        // would throw a file away. Say so instead.
        if (times.Count < clash.Count)
            return new(DuplicateKind.Unknown, clash, null,
                "same title, but nothing has measured the lengths or the audio - a "
                + "copy and a remix look identical from here");

        if (!SameLength(times.Min(), times.Max()))
        {
            var range = $"{Minutes(times.Min())} to {Minutes(times.Max())}";
            return new(DuplicateKind.AlternateVersion, clash, null,
                $"same title, lengths from {range} - different versions, not copies");
        }

        return Settle(clash, "same title and length");
    }

    /// <summary>
    /// Which of a set of files to keep, once they are known to be one recording.
    /// </summary>
    private static DuplicateSet Settle(IReadOnlyList<TrackTags> clash, string because)
    {
        var best = clash.OrderByDescending(Quality).ThenByDescending(t => Size(t) ?? 0).First();

        // "Byte-for-byte" is a strong claim, so it is only made on sizes actually
        // read. A file that could not be stat'd reports nothing, and two nothings
        // are not a match - without this a pair of unreadable paths would be
        // declared identical copies and one of them offered for deletion.
        var sizes = clash.Select(Size).ToList();
        var identical = sizes.All(s => s is > 0) && sizes.Distinct().Count() == 1;

        return identical
            ? new(DuplicateKind.ExactCopy, clash, best,
                $"{because}, identical copies at {sizes[0] / 1024 / 1024} MB - keeping one")
            : new(DuplicateKind.SameRecording, clash, best,
                $"{because} - one recording encoded {clash.Count} ways, keeping {Describe(best)}");
    }

    /// <summary>
    /// How good a file is, for choosing between copies of one recording.
    ///
    /// Lossless first whatever the bitrate, then bitrate. A 900 kbps WMA is WMA
    /// Lossless and beats a 320 kbps MP3; a 128 kbps MP3 loses to everything.
    /// </summary>
    public static int Quality(TrackTags t)
    {
        var extension = Path.GetExtension(t.Path).ToLowerInvariant();
        var kbps = t.Kbps ?? 0;

        var lossless = extension is ".flac" or ".wav" or ".ape" or ".alac" or ".aiff" or ".aif"
                    // WMA Lossless is variable and lands around 470-940 kbps. Lossy
                    // WMA tops out near 320, so the gap between them is wide enough
                    // to read from the bitrate alone.
                    || (extension is ".wma" && kbps >= 400);

        return (lossless ? 1_000_000 : 0) + kbps;
    }

    /// <summary>Bytes on disk, or null when the file could not be reached.</summary>
    private static long? Size(TrackTags t)
    {
        try
        {
            var info = new FileInfo(t.Path);
            return info.Exists ? info.Length : null;
        }
        catch { return null; }
    }

    private static string Describe(TrackTags t) =>
        $"{Path.GetExtension(t.Path).TrimStart('.').ToLowerInvariant()}"
        + (t.Kbps is > 0 ? $" at {t.Kbps} kbps" : "");

    private static string Minutes(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

    /// <summary>Every file in the set naming the same recording.</summary>
    private static bool AllAlike(IReadOnlyList<TrackTags> clash)
    {
        var first = TitleKey(clash[0]);
        return clash.Skip(1).All(t => Alike(first, TitleKey(t)));
    }

    /// <summary>
    /// Two titles for one recording.
    ///
    /// Not equality: a library this old carries "Norwegian Wood (The Bird Has
    /// Flown)" beside "(This Bird Has Flown)", and calling those two different
    /// songs would leave a real duplicate in place. Near-identical counts as the
    /// same; anything a person would read as a different song does not.
    /// </summary>
    internal static bool Alike(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;

        var longest = Math.Max(a.Length, b.Length);
        return Distance(a, b) <= Math.Max(1, longest / 8);
    }

    /// <summary>Edit distance, capped by the shorter of the two rows.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// The title as it identifies the recording. Explorer's "(2)" suffix on a
    /// pasted copy and a trailing "(feat. ...)" are spelling, not a different
    /// song - the same track is routinely credited both ways across two rips.
    /// </summary>
    internal static string TitleKey(TrackTags t)
    {
        var text = t.Title.HasText ? t.Title.Text : Path.GetFileNameWithoutExtension(t.Path);

        text = Featuring.Replace(text, "");
        text = CopySuffix.Replace(text, "");

        // Old rippers wrote the performer into the title on compilations, so one
        // soundtrack carries "Under the Gun" beside "Under The Gun (Supreme Beings
        // Of Leisure)". Only stripped when the parenthetical is that track's own
        // artist - a bracket holding anything else is part of the title.
        if (t.Artist.HasText)
        {
            var trailing = Trailing.Match(text);
            if (trailing.Success && Simplify(trailing.Groups[1].Value) == Simplify(t.Artist.Text))
                text = text[..trailing.Index];
        }

        return Simplify(text);
    }

    /// <summary>
    /// The same normalisation applied to a bare title with no file behind it -
    /// a track name out of an album listing, say.
    ///
    /// Separate from the overload above rather than shared with it, because
    /// that one falls back to the filename when a file carries no title tag,
    /// and a filename has an extension to strip. Run a real title through that
    /// path and "Mr. Brightside" becomes "Mr".
    /// </summary>
    internal static string TitleKey(string text) =>
        Simplify(CopySuffix.Replace(Featuring.Replace(text, ""), ""));

    /// <summary>Letters and digits only, lower case. Punctuation is spelling.</summary>
    private static string Simplify(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);

        return sb.ToString();
    }

    private static readonly Regex Featuring = new(
        @"[\(\[]\s*(feat|ft|featuring|with)\b[^)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex CopySuffix = new(
        @"\s*\(\d+\)\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex Trailing = new(
        @"\s*[\(\[]([^)\]]+)[\)\]]\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
