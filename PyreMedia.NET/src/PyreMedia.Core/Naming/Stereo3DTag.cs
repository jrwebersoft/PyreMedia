using System.Text.RegularExpressions;

namespace PyreMedia.Core.Naming;

/// <summary>
/// Recognises 3D markers in a filename and normalises them to one form:
/// "(3D)", "(3D-SBS)", "(3D-HSBS)", "(3D-TB)", "(3D-HTB)".
///
/// This has to be generous about input and strict about output. Releases write
/// the same thing a dozen ways - "Half-SBS", "h-sbs", "[3D HSBS]", ".HOU." - but
/// the tag we produce should always look the same so a library stays consistent.
///
/// It matters most for half-SBS and half-TB: those are squeezed back to normal
/// dimensions and carry no metadata flag at all, so the filename is the only
/// record that the file is 3D. Lose the tag and the information is gone.
/// </summary>
public static partial class Stereo3DTag
{
    /// <summary>A bracketed group containing "3D", anywhere in the name.</summary>
    [GeneratedRegex(@"[\(\[\{]\s*3[\s._-]*d(?<variant>[^\)\]\}]*)[\)\]\}]", RegexOptions.IgnoreCase)]
    private static partial Regex Bracketed();

    /// <summary>A bare layout token: "HSBS", "Half-SBS", "3D", "HOU", "MVC".</summary>
    /// <remarks>
    /// The paired forms come first, so "3D-2D" is taken as one token rather
    /// than as "3D" followed by something unrecognised. A disc holding both
    /// cuts is labelled that way - "SUICIDE_SQUAD 3D-2D EXTENDED EDITION" - and
    /// removing only the "3D" left a film called "SUICIDE SQUAD -2D EDITION".
    /// </remarks>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:3[\s._-]*d[\s._-]*[-+/][\s._-]*2[\s._-]*d|" +
        @"2[\s._-]*d[\s._-]*[-+/][\s._-]*3[\s._-]*d|" +
        @"3[\s._-]*d|half[\s._-]*sbs|h[\s._-]?sbs|full[\s._-]*sbs|sbs|" +
        @"half[\s._-]*ou|h[\s._-]?ou|hou|ou|half[\s._-]*tab|h[\s._-]?tab|tab|" +
        @"half[\s._-]*tb|h[\s._-]?tb|htb|tb|mvc|bd3d|anaglyph)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex BareToken();

    /// <summary>
    /// Canonical tag for a name, or null when there's no 3D marker.
    /// A bare "TB" or "OU" alone is ignored - too many titles contain those two
    /// letters for it to be safe without "3D" nearby.
    /// </summary>
    public static string? Extract(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var bracketed = Bracketed().Match(name);

        // A bracketed "(3D-HSBS)" states the layout outright.
        if (bracketed.Success)
        {
            var stated = Canonical(bracketed.Groups["variant"].Value);
            if (stated != "(3D)") return stated;
        }

        // Only trust bare layout tokens when "3D" appears too, so an innocent
        // "TB" or "OU" in a title can't masquerade as a 3D marker.
        var says3D = bracketed.Success
            || Regex.IsMatch(name, @"(?<![A-Za-z0-9])3[\s._-]*d(?![A-Za-z0-9])", RegexOptions.IgnoreCase)
            // "BD3DRmx" - release names run their words together, and the
            // boundary rule above cannot see the 3D inside one. "bd3d" is
            // distinctive enough to be trusted without it; a bare "3d" is not,
            // which is why this is spelt out rather than loosening the rule.
            || Regex.IsMatch(name, @"bd[\s._-]*3[\s._-]*d", RegexOptions.IgnoreCase);

        if (!says3D) return null;

        // "[3D] [TAB]" and "3D.HSBS" both put the layout somewhere other than
        // beside the 3D marker, so scan the whole name for one.
        foreach (Match m in BareToken().Matches(name))
        {
            var tag = Canonical(m.Value);
            if (tag != "(3D)") return tag;
        }

        // Also check inside any other bracketed group, e.g. "[TAB]".
        foreach (Match m in Regex.Matches(name, @"[\(\[\{]([^\)\]\}]+)[\)\]\}]"))
        {
            var tag = Canonical(m.Groups[1].Value);
            if (tag != "(3D)") return tag;
        }

        return "(3D)";
    }

    /// <summary>
    /// The tag for a 3D disc image, which is its own thing.
    ///
    /// A raw disc is not side-by-side or top-and-bottom - those describe a
    /// re-encode, and the whole point of keeping the image is that nothing has
    /// been done to it yet. "(3D-ISO)" says what it is: the disc, still 3D,
    /// still whole. Null in, null out - a 2D image gets no tag at all.
    /// </summary>
    public static string? ForDiscImage(string? tag) => tag is null ? null : "(3D-ISO)";

    /// <summary>Map any spelling onto the canonical suffix.</summary>
    private static string Canonical(string variant)
    {
        var v = Regex.Replace(variant ?? "", @"[^A-Za-z]", "").ToUpperInvariant();

        return v switch
        {
            "HSBS" or "HALFSBS" => "(3D-HSBS)",
            "SBS" or "FULLSBS" => "(3D-SBS)",
            "HTB" or "HALFTB" or "HOU" or "HALFOU" or "HTAB" or "HALFTAB" => "(3D-HTB)",
            "TB" or "FULLTB" or "OU" or "TAB" => "(3D-TB)",
            // "D" is what's left of "3D" once digits are stripped - it means the
            // bare marker, not a layout called D. "DD" is what's left of a disc
            // carrying both cuts, "3D-2D", which says nothing about layout
            // either - and it is also what a bracketed "(DD5.1)" reduces to, so
            // this stops an audio codec being read as a 3D format.
            "MVC" or "BD" or "BDD" or "D" or "DD" or "" => "(3D)",
            _ => $"(3D-{v})"      // unrecognised but clearly deliberate - keep it
        };
    }

    /// <summary>The name with every 3D marker removed, ready to search on.</summary>
    public static string Strip(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        var s = Bracketed().Replace(name, " ");

        // Only strip bare tokens when the name really was 3D - otherwise a title
        // containing "Tab" or "Ou" would be mangled.
        if (Extract(name) is not null)
            s = BareToken().Replace(s, " ");

        return Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '.', '_');
    }

    /// <summary>Put the tag back on the front, without doubling it up.</summary>
    public static string Apply(string name, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return name;
        return $"{tag} {Strip(name)}".Trim();
    }

    /// <summary>
    /// Tag for a layout detected from the file itself. Half-variants are never
    /// produced here - they're indistinguishable from 2D by geometry, so they
    /// can only come from a filename.
    /// </summary>
    public static string ForDetected(bool isMvc, string? packedLayout) =>
        isMvc ? "(3D)"
        : packedLayout switch
        {
            "SBS" => "(3D-SBS)",
            "OU" or "TB" => "(3D-TB)",
            _ => "(3D)"
        };
}
