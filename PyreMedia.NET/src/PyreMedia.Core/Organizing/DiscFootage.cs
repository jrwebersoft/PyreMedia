using PyreMedia.Core.Music;

namespace PyreMedia.Core.Organizing;

/// <summary>
/// Deciding whether two ripped titles are the same recording, by listening to
/// them.
///
/// The question only arises for titles of near-identical length, and length is
/// exactly what cannot answer it. A real DVD rip on hand holds nine episodes of
/// an animated series running 21:17 to 21:22, two of them identical to the
/// tenth of a second - a series cut to a broadcast slot has episodes that all
/// run to the slot. Their file sizes agree to within half a percent as well.
///
/// Their audio does not. Fingerprinted, two of those identical-length episodes
/// diverge within a few characters, while a title compared with itself agrees
/// completely. So this is the one signal that separates "the disc offered this
/// episode twice" from "these are two different episodes of the same show".
///
/// It is the same acoustic fingerprint the music side uses for the same reason:
/// deciding whether two files are the same recording is the same question
/// whether it is asked of a song or of an episode.
/// </summary>
public static class DiscFootage
{
    /// <summary>
    /// Agreement above which two recordings are taken to be the same.
    ///
    /// The music side measured this: the same recording scores 82-100%,
    /// different ones 47-53%, and nothing was ever observed between 61% and
    /// 81%. The gap is wide enough that the exact threshold hardly matters.
    /// </summary>
    private const double Agrees = 0.70;

    /// <summary>
    /// Fingerprints already taken, so a title compared with three neighbours is
    /// listened to once rather than three times.
    ///
    /// A disc of episodes all cut to the same slot puts nearly every title
    /// within seconds of nearly every other, so the comparisons chain: title
    /// three against two, four against three, and so on. Without this a nine
    /// title disc was fingerprinted fourteen times at four tenths of a second
    /// each, most of it repeating work already done.
    /// </summary>
    private static readonly Dictionary<string, uint[]> Heard = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forget what has been heard - the files may have changed since.</summary>
    public static void Forget()
    {
        lock (Heard) Heard.Clear();
    }

    private static uint[] Listen(string path)
    {
        lock (Heard)
        {
            if (Heard.TryGetValue(path, out var known)) return known;
        }

        var taken = Fingerprint.Of(path);

        lock (Heard) Heard[path] = taken;

        return taken;
    }

    /// <summary>
    /// Whether two titles are the same footage. Null when it cannot be told -
    /// no ffmpeg, an unreadable file, a stream with no audio at all.
    ///
    /// Null matters as much as the answer does. The caller keeps both titles
    /// when it cannot tell, because a kept duplicate wastes disk and a
    /// discarded episode is simply gone.
    /// </summary>
    public static bool? Same(string a, string b)
    {
        if (!Fingerprint.Available()) return null;

        try
        {
            var one = Listen(a);
            var two = Listen(b);

            if (one.Length == 0 || two.Length == 0) return null;

            return Fingerprint.Agreement(one, two) is { } score ? score >= Agrees : null;
        }
        catch (Exception)
        {
            // Unreadable, or ffmpeg went missing between the check and the call.
            // Not knowing is a real answer here and has to travel as one.
            return null;
        }
    }

    /// <summary>The comparer <see cref="DiscRip.Read"/> expects.</summary>
    public static bool? Compare(RipTitle a, RipTitle b) => Same(a.Path, b.Path);
}
