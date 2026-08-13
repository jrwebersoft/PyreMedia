using PyreMedia.Core.Tools;

namespace PyreMedia.Core.Music;

/// <summary>What a missing tool costs.</summary>
/// <param name="Name">The tool, as somebody would go looking for it.</param>
/// <param name="Present">Whether it was found.</param>
/// <param name="Lost">What stops working without it, in words.</param>
public sealed record Capability(string Name, bool Present, string Lost);

/// <summary>
/// Where the external tools are, and what the music side loses without them.
///
/// Two problems, one cause. The music classes each said "ffmpeg" and left it
/// to the PATH, while the film side has had a configurable path and a locator
/// since the remux work - so somebody who pointed the program at a specific
/// build got that build for remuxing and whatever the PATH happened to hold
/// for everything here, or nothing at all.
///
/// And nothing said what was missing. A library scanned without ffmpeg still
/// produces a plan, still moves files, and quietly falls back to comparing
/// titles and durations - which is the 95.2% path rather than the right one.
/// Working less well with no sign of it is the worst way for a dependency to
/// be absent.
/// </summary>
public static class MusicTools
{
    private static string _ffmpeg = "ffmpeg";
    private static string _ffprobe = "ffprobe";

    /// <summary>
    /// The command to run for ffmpeg - resolved from settings where the user
    /// set one, and a bare "ffmpeg" otherwise, which means the PATH.
    /// </summary>
    public static string Ffmpeg => _ffmpeg;

    public static string Ffprobe => _ffprobe;

    /// <summary>
    /// Point the music side at the same tools the rest of the program uses.
    /// Called once, when settings are loaded.
    /// </summary>
    public static void Use(PyreMediaSettings settings)
    {
        // Resolve() turns a bare name into a full path by walking the PATH, and
        // returns null when nothing is there. Falling back to the bare name in
        // that case is deliberate: the process start will fail with a message
        // naming the command, which is more use than failing here with a
        // message naming a setting.
        _ffmpeg = ToolLocator.Resolve(settings.FfmpegPath) ?? settings.FfmpegPath;
        _ffprobe = ToolLocator.Resolve(settings.FfprobePath) ?? settings.FfprobePath;
    }

    /// <summary>
    /// What works and what does not, ready to show somebody.
    ///
    /// Written as what is lost rather than what is missing. "ffmpeg not found"
    /// tells a user nothing about whether they need it; "duplicate detection
    /// falls back to comparing titles and durations, which is right 95% of the
    /// time instead of always" tells them exactly how much they care.
    /// </summary>
    public static List<Capability> Check(PyreMediaSettings settings)
    {
        Use(settings);

        var ffmpeg = ToolLocator.Resolve(settings.FfmpegPath) is not null;
        var ffprobe = ToolLocator.Resolve(settings.FfprobePath) is not null;
        var chromaprint = ffmpeg && Fingerprint.Available();

        return
        [
            new("ffmpeg", ffmpeg,
                "Without it: no tag writing, no loudness, and no comparing the audio. "
                + "Reading tags, grouping albums, planning and moving all still work."),

            new("ffmpeg's chromaprint", chromaprint, ffmpeg
                ? "This build of ffmpeg has no chromaprint muxer, so duplicates are judged "
                  + "on titles and durations alone - which agreed with the audio 95.2% of the "
                  + "time on a measured library, and deleted one file that was not a copy."
                : "Comes with ffmpeg."),

            new("ffprobe", ffprobe,
                "Without it: track lengths cannot be measured, so two files claiming one "
                + "track number cannot be told apart and are held for a person instead."),

            new("AcoustID key", settings.HasAcoustIdKey,
                "Only needed to identify files whose tags say nothing at all. Free, and "
                + $"optional - on a measured library of 17,175 files it had nothing to do. "
                + $"From {AcoustId.SignUpUrl}"),

            // Listed so its absence from the list of things to install is
            // itself information: people assume anything that names a service
            // needs an account.
            new("MusicBrainz", true,
                "Needs no key and no account - only a User-Agent, which is built in.")
        ];
    }
}
