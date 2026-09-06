using System.Text.RegularExpressions;

namespace PyreMedia.Core.Organizing;

/// <summary>One image file that looks like it came with the release rather than the media.</summary>
public sealed record StrayImage
{
    public required string Path { get; init; }

    /// <summary>Why it looks like litter, in words that can be checked against the file.</summary>
    public required string Because { get; init; }

    public long Bytes { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Images that came with a release rather than with the media.
///
/// Scene releases carry proof shots, screen grabs and group artwork, and those
/// accumulate in a library forever because nothing ever looks at them. They are
/// not on the junk extension list and must not be: the same folder holds
/// cover.jpg, poster.jpg, fanart.jpg, seasonNN-poster.jpg and an episode's own
/// -thumb.jpg, and a rule that deleted images by extension would take every one
/// of them.
///
/// So this recognises the handful of shapes scene tooling actually produces,
/// and refuses everything else. The list of things it will never touch is
/// longer than the list of things it will, deliberately - a false positive here
/// deletes artwork somebody chose, and the only thing worse than clutter is
/// tidying that eats what you wanted.
/// </summary>
public static class SceneImages
{
    private static readonly string[] Pictures = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

    /// <summary>
    /// Artwork, by the names every player looks for. Never offered, whatever
    /// else it looks like.
    /// </summary>
    private static readonly Regex Artwork = new(
        @"^(?:cover|folder|poster|fanart|banner|clearlogo|clearart|logo|thumb|landscape|"
        + @"disc|discart|cdart|keyart|characterart|season[-\w]*|backdrop|artist|album)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The sidecar form: "&lt;video&gt;-poster.jpg", "&lt;video&gt;-thumb.jpg".</summary>
    private static readonly Regex Sidecar = new(
        @"-(?:poster|fanart|thumb|banner|clearlogo|clearart|landscape|logo|disc|discart)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// What scene tooling actually writes. Anchored, because "screen" appears
    /// inside plenty of legitimate names - a film called Screen Test, an artist
    /// folder called Screaming Trees.
    /// </summary>
    private static readonly (Regex Pattern, string Why)[] Litter =
    [
        (new(@"^screens?\d+$", RegexOptions.IgnoreCase), "a numbered screen grab"),
        (new(@"^(?:screen|snap)shot\d*$", RegexOptions.IgnoreCase), "a screenshot"),
        (new(@"^proof\d*$", RegexOptions.IgnoreCase), "a proof shot"),
        (new(@"^sample\d*$", RegexOptions.IgnoreCase), "a sample image"),
        (new(@"^(?:vlcsnap|mpv-shot|snapshot)[-_]?\d*", RegexOptions.IgnoreCase), "a player snapshot"),
        (new(@"^\d{8}[-_]\d{6}$", RegexOptions.IgnoreCase), "a timestamped grab"),
    ];

    /// <summary>
    /// Folders scene releases put their proof in. A whole folder of grabs is a
    /// clearer signal than any one file in it.
    /// </summary>
    private static readonly Regex ProofFolder = new(
        @"^(?:proof|proofs|screens?|screenshots?|sample|samples)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsPicture(string path) =>
        Pictures.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Whether this image is litter, and why - or null when it is not, or when
    /// there is any doubt.
    /// </summary>
    public static string? Judge(string path)
    {
        if (!IsPicture(path)) return null;

        var name = System.IO.Path.GetFileNameWithoutExtension(path);

        // Never artwork. Checked first so no later rule can reach it.
        if (Artwork.IsMatch(name)) return null;
        if (Sidecar.IsMatch(name)) return null;

        // A file sitting in a proof folder is litter whatever it is called -
        // the folder is the statement.
        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "");

        if (ProofFolder.IsMatch(folder)) return $"it is in a folder called {folder}";

        foreach (var (pattern, why) in Litter)
            if (pattern.IsMatch(name)) return why;

        return null;
    }

    /// <summary>
    /// Every stray image under these folders.
    /// </summary>
    /// <param name="skip">
    /// Folders to leave alone entirely. A comic stored as loose pages is a
    /// folder of numbered images and every one of them would look like a grab -
    /// so the caller passes those in rather than this guessing.
    /// </param>
    public static List<StrayImage> Find(
        IEnumerable<string> roots,
        IReadOnlyCollection<string>? skip = null,
        CancellationToken ct = default)
    {
        var found = new List<StrayImage>();
        var avoid = new HashSet<string>(skip ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var dir = queue.Dequeue();

                if (avoid.Contains(dir)) continue;

                string[] files, subs;

                try
                {
                    files = Directory.GetFiles(dir);
                    subs = Directory.GetDirectories(dir);
                }
                catch (Exception)
                {
                    continue;   // one unreadable folder is not the end of a sweep
                }

                // A folder that is nothing but images is somebody's pictures, or
                // a comic stored as loose pages. Either way it is not litter
                // beside media, and taking it apart file by file would be wrong.
                //
                // Unless the folder says outright what it is. A folder called
                // Screens holding nothing but screens is the clearest case
                // there is, and this guard was throwing exactly those away:
                // four grabs were swept and five were not, which is not a
                // distinction anybody could have predicted from the outside.
                var pictures = files.Count(IsPicture);
                var declared = ProofFolder.IsMatch(System.IO.Path.GetFileName(dir));

                if (!declared && pictures > 0 && pictures == files.Length && files.Length > 4)
                {
                    foreach (var s in subs) queue.Enqueue(s);
                    continue;
                }

                foreach (var file in files)
                {
                    if (Judge(file) is not { } why) continue;

                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch (Exception) { }

                    found.Add(new StrayImage { Path = file, Because = why, Bytes = size });
                }

                foreach (var s in subs) queue.Enqueue(s);
            }
        }

        return found;
    }
}
