using PyreMedia.Core.Models;

namespace PyreMedia.Core.Metadata;

/// <summary>
/// Downloads poster and fanart and puts them where Kodi looks.
///
/// Kodi finds artwork by name, beside the video: "&lt;video&gt;-poster.jpg" and
/// "&lt;video&gt;-fanart.jpg", or plain "poster.jpg" / "fanart.jpg" when a film has
/// a folder to itself. Both forms are read; the sidecar form is written, because
/// it survives a folder holding more than one film and it travels with the video
/// through a rename - the companion rules already carry "-poster" and "-fanart"
/// along with everything else.
///
/// Nothing here is required. A library Kodi scrapes already has artwork of its
/// own, and the point of this is the library that doesn't - or files copied to a
/// player that never scrapes.
/// </summary>
public static class ArtworkWriter
{
    public enum Outcome { Written, SkippedExisting, SkippedDisabled, NothingToGet, Failed }

    public sealed record Result(Outcome Outcome, string Path, string? Detail = null)
    {
        public bool Changed => Outcome == Outcome.Written;
    }

    /// <summary>Where Kodi would look for a poster beside this video.</summary>
    public static string PosterPathFor(string videoPath) => PathFor(videoPath, ArtKind.Poster);

    /// <summary>Where Kodi would look for fanart beside this video.</summary>
    public static string FanartPathFor(string videoPath) => PathFor(videoPath, ArtKind.Fanart);

    /// <summary>
    /// Where Kodi looks for one kind of artwork beside a video.
    ///
    /// A clear logo is a PNG and has to stay one - it is transparent, and
    /// writing it as .jpg would flatten it onto a background and defeat the
    /// entire point of it.
    /// </summary>
    public static string PathFor(string videoPath, ArtKind kind)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(videoPath);

        var (name, ext) = kind switch
        {
            ArtKind.Poster => ("poster", ".jpg"),
            ArtKind.Fanart => ("fanart", ".jpg"),
            _ => ("clearlogo", ".png")
        };

        return Path.Combine(dir, $"{stem}-{name}{ext}");
    }

    /// <summary>
    /// Fetch one image to its place beside the video.
    /// </summary>
    /// <param name="url">Absolute image url, or null when the provider had none.</param>
    public static async Task<Result> WriteAsync(
        HttpClient http,
        string videoPath,
        ArtKind kind,
        string? url,
        PyreMediaSettings settings,
        CancellationToken ct = default)
    {
        var path = PathFor(videoPath, kind);

        if (!settings.DownloadArtwork)
            return new Result(Outcome.SkippedDisabled, path);

        if (string.IsNullOrWhiteSpace(url))
            return new Result(Outcome.NothingToGet, path, "the provider had no image");

        // What's already there may be chosen, hand-made, or simply better.
        if (File.Exists(path) && settings.PreserveExistingArtwork)
            return new Result(Outcome.SkippedExisting, path, "already there");

        var temp = path + ".tmp";

        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // A truncated or empty download is worse than none: it looks like
            // artwork to every player and shows as a broken image.
            if (bytes.Length < 1024 || !LooksLikeImage(bytes))
                return new Result(Outcome.Failed, path, "what came back wasn't an image");

            // The clear logo slot is written with a .png name because it has to
            // be transparent. Anything else arriving here would be saved under a
            // name that lies about it.
            if (kind == ArtKind.ClearLogo && !IsPng(bytes))
                return new Result(Outcome.Failed, path,
                    "a clear logo has to be a transparent PNG, and this wasn't one");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Temp and swap, so an interrupted download can't leave half a file
            // where a whole one is expected.
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            return new Result(Outcome.Written, path);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return new Result(Outcome.Failed, path, ex.Message);
        }
    }

    /// <summary>
    /// A JPEG or PNG by its first bytes. An error page returned with a 200 is a
    /// real thing, and writing it out as poster.jpg helps nobody.
    /// </summary>
    private static bool LooksLikeImage(byte[] bytes)
    {
        if (bytes.Length < 4) return false;

        var jpeg = bytes[0] == 0xFF && bytes[1] == 0xD8;
        return jpeg || IsPng(bytes);
    }

    /// <summary>The PNG signature. Transparency is the whole point of a logo.</summary>
    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 4
        && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
}
