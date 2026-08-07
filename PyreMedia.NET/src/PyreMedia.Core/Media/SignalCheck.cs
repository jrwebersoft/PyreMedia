using System.Text.RegularExpressions;
using PyreMedia.Core.Naming;

namespace PyreMedia.Core.Media;

/// <summary>One thing the filename claims that the file itself doesn't back up.</summary>
public sealed record SignalMismatch(string What, string Detail, bool RemuxMayFix);

/// <summary>
/// Compares what a filename says a file is against what is actually in it.
///
/// The two drift apart in one particular way: a remux that copies the streams
/// but drops the signalling. The picture is untouched and the name still says
/// what it always said, so nothing looks wrong until it plays - Dolby Vision
/// with the wrong colours, or a 3D film that a player shows flat because the
/// stereo flag went missing. Remuxing again with mkvmerge usually puts the
/// signalling back, because the data it is built from is still in the stream.
/// </summary>
public static partial class SignalCheck
{
    /// <summary>
    /// Dolby Vision in a filename. "DV" on its own is the usual spelling, which
    /// makes DVD and DVDRip the obvious trap, and "DVDScr" and "HDV" after that.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:DV|DoVi|Dolby[ ._-]?Vision)(?![A-Za-z0-9])",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DolbyVisionInName();

    /// <summary>
    /// What the name claims but the file doesn't carry.
    /// </summary>
    /// <param name="rpuInStream">
    /// Whether Dolby Vision RPU data was found in the picture - see
    /// <see cref="MediaProbe.HasDolbyVisionRpuAsync"/>. Null when it wasn't
    /// looked for, in which case only the filename can suggest anything.
    /// </param>
    public static List<SignalMismatch> Compare(string fileName, MediaInfo info, bool? rpuInStream = null)
    {
        var found = new List<SignalMismatch>();
        var name = Path.GetFileNameWithoutExtension(fileName);

        var video = info.Video.FirstOrDefault();
        if (video is null) return found;

        // ---- Dolby Vision ----
        var fileHasDv = info.Video.Any(v => v.DvProfile is not null);

        if (!fileHasDv && rpuInStream == true)
        {
            // The data is in the picture; only the configuration record that
            // declares it is gone. This is what remuxing into MP4 leaves behind.
            //
            // A plain remux does NOT put it back - measured, on a profile 5 file:
            // neither ffmpeg nor mkvmerge v100 rebuilds the record from the RPU,
            // and the file comes out of both still undeclared. Saying "remux it"
            // here would be sending someone round in a circle. Rebuilding the
            // record needs a tool that reads the RPU and writes the configuration,
            // or going back to the source.
            found.Add(new SignalMismatch(
                "Dolby Vision",
                "The Dolby Vision data is still in the picture, but the file no longer declares "
                + "it, so players treat it as ordinary HDR. This is what remuxing into MP4 does. "
                + "Remuxing again won't put the declaration back - neither ffmpeg nor mkvmerge "
                + "rebuilds it from the stream. It needs dovi_tool, or the file re-made from its "
                + "source.",
                RemuxMayFix: false));
        }
        else if (!fileHasDv && rpuInStream != true && DolbyVisionInName().IsMatch(name))
        {
            // Only the name says so, and the picture doesn't back it up. Could be
            // a mislabelled file, or one where the data really did go.
            found.Add(new SignalMismatch(
                "Dolby Vision",
                rpuInStream is null
                    ? "The name says Dolby Vision but the file doesn't declare it. Analyse it "
                      + "to find out whether the data is still in the picture."
                    : "The name says Dolby Vision, but neither the declaration nor any Dolby "
                      + "Vision data is in the file. Either it was never there or it has gone "
                      + "for good - a remux can't bring back what isn't in the stream.",
                RemuxMayFix: false));
        }

        // ---- 3D ----
        var tag = Stereo3DTag.Extract(name);

        if (tag is not null)
        {
            var flagged = !string.IsNullOrWhiteSpace(video.Stereo3D) || video.IsMvc;
            var packed = video.PackedLayout is not null;

            if (!flagged && !packed)
            {
                // Half-width layouts are squeezed back to ordinary dimensions, so
                // geometry can't confirm them - the flag is the only signal there
                // is, and without it a player shows two squashed images.
                found.Add(new SignalMismatch(
                    "3D",
                    $"The name says {tag} but the file carries no stereo mode. Players have "
                    + "nothing to go on and will show it flat, or side by side. Remuxing with "
                    + "mkvmerge can write the flag from the name.",
                    RemuxMayFix: true));
            }
            else if (flagged && Disagrees(tag, video.Stereo3D))
            {
                found.Add(new SignalMismatch(
                    "3D",
                    $"The name says {tag} but the file is flagged '{video.Stereo3D}'. "
                    + "One of the two is wrong, and the flag is what a player believes.",
                    RemuxMayFix: false));
            }
        }
        else if (video.IsMvc || !string.IsNullOrWhiteSpace(video.Stereo3D))
        {
            // The other way round is not a fault, but it is worth knowing: the
            // file is 3D and nothing in the name says so.
            found.Add(new SignalMismatch(
                "3D",
                $"The file is 3D ({video.Stereo3D ?? "MVC"}) but the name doesn't say so. "
                + "Turning on 3D tags in Settings adds it when the file is renamed.",
                RemuxMayFix: false));
        }

        return found;
    }

    /// <summary>
    /// Whether a name tag and a container flag describe different layouts. Only
    /// the axis is compared - side-by-side against top-bottom - because half and
    /// full variants share a flag and disagreeing on that would be noise.
    /// </summary>
    private static bool Disagrees(string tag, string? stereoMode)
    {
        if (string.IsNullOrWhiteSpace(stereoMode)) return false;

        var nameSideBySide = tag.Contains("SBS", StringComparison.OrdinalIgnoreCase);
        var nameTopBottom = tag.Contains("TB", StringComparison.OrdinalIgnoreCase)
                            || tag.Contains("OU", StringComparison.OrdinalIgnoreCase);

        var flagSideBySide = stereoMode.Contains("left", StringComparison.OrdinalIgnoreCase)
                             || stereoMode.Contains("right", StringComparison.OrdinalIgnoreCase)
                             || stereoMode.Contains("side", StringComparison.OrdinalIgnoreCase);

        var flagTopBottom = stereoMode.Contains("top", StringComparison.OrdinalIgnoreCase)
                            || stereoMode.Contains("bottom", StringComparison.OrdinalIgnoreCase);

        return (nameSideBySide && flagTopBottom) || (nameTopBottom && flagSideBySide);
    }
}
