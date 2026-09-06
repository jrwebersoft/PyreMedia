namespace PyreMedia.Core.Metadata;

/// <summary>One file that could be filled in, and what it is short of.</summary>
public sealed record BackfillTarget
{
    public required string VideoPath { get; init; }
    public required string NfoPath { get; init; }
    public required bool IsTv { get; init; }
    public required NfoIds Ids { get; init; }
    public required NfoGaps Gaps { get; init; }

    /// <summary>Where the .nfo names them, so an episode can be looked up.</summary>
    public int? Season { get; init; }
    public int? Episode { get; init; }

    public string Name => Path.GetFileName(VideoPath);

    /// <summary>
    /// Whether this can be looked up without guessing. An episode needs its
    /// numbers as well as the series id - the id says which series, not which
    /// episode, and filling one episode from another is worse than leaving it.
    /// </summary>
    public bool CanLookUp =>
        Ids.Any && (!IsTv || (Season is not null && Episode is not null));
}

/// <summary>What a sweep of the library found, before anything is written.</summary>
public sealed record BackfillSurvey
{
    /// <summary>Missing something, and carrying an id to fetch it with.</summary>
    public List<BackfillTarget> Ready { get; init; } = [];

    /// <summary>Missing something, with nothing to look it up by.</summary>
    public List<BackfillTarget> Unmatched { get; init; } = [];

    /// <summary>Files whose .nfo already answers everything asked of it.</summary>
    public int Complete { get; init; }

    /// <summary>Files with no .nfo at all - a rename writes one, so this is not a gap to fill here.</summary>
    public int NoNfo { get; init; }

    public int Total => Ready.Count + Unmatched.Count + Complete + NoNfo;
}

/// <summary>
/// Find what an unattended pass could fill in.
///
/// Reads and decides; writes nothing. Deciding is the whole difficulty - a
/// backfill that guessed which title a file was would quietly write one film's
/// cast into another's .nfo, and nothing about the result would look wrong
/// until somebody opened it.
/// </summary>
public static class Backfill
{
    /// <summary>
    /// Every video under these folders, paired with the .nfo beside it.
    /// </summary>
    /// <param name="isTv">
    /// Whether these roots hold series rather than films. It decides which
    /// lookup applies, and an episode additionally needs its numbers, which are
    /// taken from the .nfo rather than re-parsed from the filename - the .nfo is
    /// what the library itself believes.
    /// </param>
    public static BackfillSurvey Survey(
        IEnumerable<string> roots,
        PyreMediaSettings settings,
        bool isTv,
        CancellationToken ct = default)
    {
        var ready = new List<BackfillTarget>();
        var unmatched = new List<BackfillTarget>();
        var complete = 0;
        var none = 0;

        var videoExts = settings.VideoExtensions;

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;

            try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                if (!videoExts.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;

                var nfo = Path.ChangeExtension(file, ".nfo");

                if (!File.Exists(nfo)) { none++; continue; }

                var gaps = NfoGaps.Of(nfo);

                if (!gaps.Any) { complete++; continue; }

                var (season, episode) = isTv ? Numbers(nfo) : (null, null);

                var target = new BackfillTarget
                {
                    VideoPath = file,
                    NfoPath = nfo,
                    IsTv = isTv,
                    Ids = NfoIds.Read(nfo),
                    Gaps = gaps,
                    Season = season,
                    Episode = episode
                };

                (target.CanLookUp ? ready : unmatched).Add(target);
            }
        }

        return new BackfillSurvey
        {
            Ready = ready,
            Unmatched = unmatched,
            Complete = complete,
            NoNfo = none
        };
    }

    /// <summary>The season and episode an .nfo states, where it states them.</summary>
    private static (int?, int?) Numbers(string nfoPath)
    {
        try
        {
            var blocks = NfoFile.ReadBlocks(nfoPath);
            if (blocks.Count == 0) return (null, null);

            // The first block. A multi-episode file names several, and the
            // first is the one the show lookup is anchored on.
            var s = blocks[0].Element("season")?.Value;
            var e = blocks[0].Element("episode")?.Value;

            return (int.TryParse(s, out var sn) ? sn : null,
                    int.TryParse(e, out var en) ? en : null);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }
}
