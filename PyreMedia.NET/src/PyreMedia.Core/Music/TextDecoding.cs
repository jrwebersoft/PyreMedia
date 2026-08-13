using System.Text;
using PyreMedia.Core.Metadata;

namespace PyreMedia.Core.Music;

/// <summary>
/// Turns tag bytes into text, and says how much it had to guess.
///
/// ID3v1 records no encoding at all, and ID3v2.3's default is Latin-1 in a world
/// where taggers wrote whatever their machine's codepage was. So for anything
/// that isn't plain ASCII there is no declared answer, only a best reading - and
/// the caller needs to know which of those it got. Writing a guess back over a
/// correct tag is how a library gets worse instead of better.
///
/// This is the reason ffprobe cannot be used for text: it decodes such bytes as
/// UTF-8, fails, and substitutes U+FFFD. Measured - "Björk" in cp1252 comes back
/// "Bj�rk", and the original byte is gone before we ever see it.
/// </summary>
public static class TextDecoding
{
    /// <summary>
    /// Codepages tried for undeclared text, best-first. Latin-1 is not among the
    /// candidates because it decodes every byte without complaint and so would
    /// always "win" - it is the fallback instead.
    /// </summary>
    private static readonly int[] Candidates =
    [
        1252,   // Western European - by far the commonest in Western libraries
        1251,   // Cyrillic
        932,    // Shift-JIS
        936,    // GBK
        949,    // Korean
        1250,   // Central European
        1253,   // Greek
        1254,   // Turkish
    ];

    /// <summary>
    /// Decode bytes whose encoding the format declared. No guessing involved,
    /// so the result is <see cref="TextConfidence.Declared"/> - or Ascii when
    /// there was nothing outside ASCII to be wrong about.
    /// </summary>
    public static (string Text, TextConfidence Confidence) Declared(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var text = Clean(encoding.GetString(bytes));
        if (text.Length == 0) return ("", TextConfidence.None);

        return (text, IsAscii(bytes) ? TextConfidence.Ascii : TextConfidence.Declared);
    }

    /// <summary>
    /// Decode bytes whose encoding nobody recorded - ID3v1, or an ID3v2.3 frame
    /// marked Latin-1 that plainly isn't.
    ///
    /// Pure ASCII needs no decision. Otherwise each candidate codepage is scored
    /// on how plausible the result looks as human text, and the best is taken.
    /// If nothing scores, the bytes are reported unreadable rather than
    /// silently mangled.
    /// </summary>
    public static (string Text, TextConfidence Confidence) Guess(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return ("", TextConfidence.None);

        if (IsAscii(bytes))
        {
            var ascii = Clean(Encoding.ASCII.GetString(bytes));
            return (ascii, ascii.Length == 0 ? TextConfidence.None : TextConfidence.Ascii);
        }

        // Valid UTF-8 in a field that never promised UTF-8 is almost always
        // actually UTF-8 - the byte patterns are too structured to arise by
        // accident from a single-byte codepage.
        if (LooksLikeUtf8(bytes))
        {
            var utf8 = Clean(new UTF8Encoding(false, false).GetString(bytes));
            if (utf8.Length > 0) return (utf8, TextConfidence.Declared);
        }

        LegacyEncodings.Ensure();

        var bestText = "";
        var bestScore = double.MinValue;

        foreach (var codepage in Candidates)
        {
            string candidate;
            try { candidate = Encoding.GetEncoding(codepage).GetString(bytes); }
            catch (Exception) { continue; }

            var score = Plausibility(candidate);
            if (score > bestScore) (bestScore, bestText) = (score, candidate);
        }

        bestText = Clean(bestText);

        if (bestText.Length == 0 || bestScore <= 0)
        {
            // Nothing read sensibly. Latin-1 at least round-trips the bytes, so
            // the file can still be found by whatever it does show.
            var raw = Clean(Encoding.Latin1.GetString(bytes));
            return (raw, raw.Length == 0 ? TextConfidence.None : TextConfidence.Unreadable);
        }

        return (bestText, TextConfidence.Guessed);
    }

    /// <summary>
    /// How much a decoded string looks like something a person wrote.
    ///
    /// Letters, digits and ordinary punctuation count for it; control
    /// characters, replacement characters and unassigned code points count
    /// heavily against. Runs of accented capitals in the middle of words are the
    /// classic signature of the wrong codepage and are penalised.
    /// </summary>
    private static double Plausibility(string s)
    {
        if (s.Length == 0) return double.MinValue;

        double score = 0;
        var previousWasLetter = false;

        foreach (var c in s)
        {
            if (c == '�' || char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
            {
                score -= 8;
                previousWasLetter = false;
                continue;
            }

            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c))
            {
                score += 1;

                // "Ã©" and friends: an accented capital immediately after a
                // lowercase letter is mojibake far more often than a name.
                if (char.IsUpper(c) && c > 127 && previousWasLetter) score -= 3;

                previousWasLetter = char.IsLetter(c);
                continue;
            }

            if (char.IsSymbol(c)) { score += 0.25; previousWasLetter = false; continue; }

            score -= 4;                       // unassigned or private use
            previousWasLetter = false;
        }

        return score + ScriptCoherence(s);
    }

    /// <summary>
    /// Latin text that is nearly all accented letters is mojibake, not a name.
    ///
    /// This is what separates cp1251 from cp1252 for the same bytes. Cyrillic
    /// "Пикник" read as cp1252 gives "Ïèêíèê" - six accented Latin letters and
    /// not one plain one. Real Western text is overwhelmingly ASCII with the odd
    /// accent: "Björk" is four plain letters and one accented. Scoring letters
    /// alone cannot tell those apart, because both are letters all the way.
    ///
    /// Only Latin is judged this way. In Cyrillic, Greek or CJK every letter is
    /// non-ASCII by nature, so the ratio means nothing there.
    /// </summary>
    private static double ScriptCoherence(string s)
    {
        int plainLatin = 0, accentedLatin = 0, otherScript = 0;

        foreach (var c in s)
        {
            if (!char.IsLetter(c)) continue;

            if (c < 128) plainLatin++;
            else if (c is >= 'À' and <= 'ɏ') accentedLatin++;
            else otherScript++;
        }

        var letters = plainLatin + accentedLatin + otherScript;
        if (letters == 0) return 0;

        // A non-Latin reading is only coherent if it is non-Latin throughout.
        // One Cyrillic letter sitting among four ASCII ones is not Russian - it
        // is "Björk" read with the wrong codepage, which is precisely the trap
        // the previous rule fell into.
        if (otherScript > 0)
        {
            var share = otherScript / (double)letters;
            return share > 0.6 ? otherScript * 1.5 : -5.0 * otherScript;
        }

        if (accentedLatin == 0) return 0;

        // The giveaway: accents outnumbering plain letters.
        var ratio = accentedLatin / (double)(plainLatin + accentedLatin);
        return ratio switch
        {
            > 0.7 => -6.0 * accentedLatin,
            > 0.4 => -2.0 * accentedLatin,
            _ => 0
        };
    }

    private static bool IsAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) if (b > 0x7F) return false;
        return true;
    }

    /// <summary>
    /// Whether the bytes form well-shaped UTF-8 with at least one multi-byte
    /// sequence. Strict: a single malformed sequence disqualifies the whole run.
    /// </summary>
    private static bool LooksLikeUtf8(ReadOnlySpan<byte> bytes)
    {
        var multiByte = false;

        for (var i = 0; i < bytes.Length;)
        {
            var b = bytes[i];

            if (b < 0x80) { i++; continue; }

            var length = b switch
            {
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 0
            };

            if (length == 0 || i + length > bytes.Length) return false;

            for (var k = 1; k < length; k++)
                if ((bytes[i + k] & 0xC0) != 0x80) return false;

            multiByte = true;
            i += length;
        }

        return multiByte;
    }

    /// <summary>
    /// Trim padding and strip the control characters taggers leave behind. Old
    /// tags are full of trailing NULs, and some write a stray BOM into every
    /// field.
    /// </summary>
    private static string Clean(string s)
    {
        if (s.Length == 0) return "";

        var end = s.IndexOf('\0');
        if (end >= 0) s = s[..end];

        s = s.TrimStart('﻿');

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsControl(c) || c == '\t') sb.Append(c);

        return sb.ToString().Trim();
    }
}
