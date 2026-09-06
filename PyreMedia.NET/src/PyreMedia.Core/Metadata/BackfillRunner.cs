using PyreMedia.Core.Media;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Metadata;

/// <summary>What became of one file.</summary>
public sealed record BackfillOutcome(string Name, bool Filled, string Detail);

public sealed record BackfillResult
{
    public int Filled { get; init; }
    public int Unchanged { get; init; }
    public int Failed { get; init; }
    public List<BackfillOutcome> Trouble { get; init; } = [];
}

/// <summary>
/// Fill the gaps in .nfo files that already name their own title.
///
/// Every lookup is by id, so nothing here decides what a file is - that was
/// settled by whoever wrote the id, and a file carrying none never reaches this
/// class. Writing goes through <see cref="NfoWriter"/> exactly as a rename
/// does, so the same merge applies: fresh data wins, and watch state, hand
/// edits and artwork are carried across untouched.
///
/// The providers arrive as functions rather than as a service so this can be
/// exercised without a network - the interesting behaviour is the id
/// resolution, the caching and the failure handling, none of which needs TMDb
/// to be reachable to be got wrong.
/// </summary>
public sealed class BackfillRunner(
    Func<string, CancellationToken, Task<Movie?>> movieById,
    Func<string, string, CancellationToken, Task<TvShow?>> showById,
    Func<string, string, bool, CancellationToken, Task<string?>> resolveExternalId,
    Func<string, CancellationToken, Task<MediaInfo?>> probe,
    PyreMediaSettings settings)
{
    /// <summary>
    /// Series already fetched, by TMDb id.
    ///
    /// A season of twenty episodes is twenty files naming the same series, and
    /// fetching a show costs one request per season. Without this a single
    /// season would be four hundred requests to learn the same thing twenty
    /// times, which is how an unattended sweep gets an API key rate-limited.
    /// </summary>
    private readonly Dictionary<string, TvShow?> _shows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>External ids already resolved, for the same reason.</summary>
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public async Task<BackfillResult> RunAsync(
        IReadOnlyList<BackfillTarget> targets,
        IProgress<string>? log = null,
        IProgress<int>? done = null,
        CancellationToken ct = default)
    {
        var filled = 0;
        var unchanged = 0;
        var failed = 0;
        var trouble = new List<BackfillOutcome>();

        var n = 0;

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            done?.Report(++n);

            try
            {
                var outcome = await OneAsync(target, ct).ConfigureAwait(false);

                if (outcome.Filled)
                {
                    filled++;
                    log?.Report($"{target.Name} - filled in {target.Gaps.Summary}");
                }
                else
                {
                    unchanged++;
                    trouble.Add(outcome);
                    log?.Report($"{target.Name} - {outcome.Detail}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One title that cannot be fetched is not a reason to abandon
                // the rest of a library.
                failed++;
                trouble.Add(new BackfillOutcome(target.Name, false, ex.Message));
                log?.Report($"{target.Name} - {ex.Message}");
            }
        }

        return new BackfillResult
        {
            Filled = filled,
            Unchanged = unchanged,
            Failed = failed,
            Trouble = trouble
        };
    }

    private async Task<BackfillOutcome> OneAsync(BackfillTarget target, CancellationToken ct)
    {
        if (!File.Exists(target.VideoPath))
            return new BackfillOutcome(target.Name, false, "the file is no longer there");

        var tmdbId = await TmdbIdFor(target, ct).ConfigureAwait(false);

        if (tmdbId is null)
            return new BackfillOutcome(target.Name, false,
                "its id could not be matched to a TMDb title");

        // Probed only where the .nfo has no track details to begin with.
        //
        // Not an optimisation so much as the difference between a job that can
        // be run and one that cannot. Surveying a real library here found
        // 39,276 files to fill in, of which all but a few hundred already
        // carried streamdetails - probing every one would have been 39,276
        // ffprobe launches to re-learn what the file already said, hours of
        // work for nothing, on top of the fetching this is actually for.
        //
        // Where they are missing they are read and written. Where they are
        // present the merge carries the existing block across untouched, which
        // is the same answer a probe would have produced for a file nothing has
        // changed. A remux is what invalidates them, and a remux refreshes them
        // itself.
        MediaInfo? info = null;

        if (target.Gaps.StreamDetails)
        {
            try { info = await probe(target.VideoPath, ct).ConfigureAwait(false); }
            catch (Exception) { }
        }

        if (!target.IsTv)
        {
            var movie = await movieById(tmdbId, ct).ConfigureAwait(false);

            if (movie is null)
                return new BackfillOutcome(target.Name, false, $"TMDb has no film {tmdbId}");

            var written = NfoWriter.WriteMovie(target.VideoPath, movie, info, settings);

            return Judge(target, written);
        }

        var show = await ShowFor(tmdbId, target.Ids.TvdbId ?? tmdbId, ct).ConfigureAwait(false);

        if (show is null)
            return new BackfillOutcome(target.Name, false, $"TMDb has no series {tmdbId}");

        var episode = show.FindEpisode(target.Season!.Value, target.Episode!.Value);

        if (episode is null)
            return new BackfillOutcome(target.Name, false,
                $"{show.Name} has no episode {target.Season}x{target.Episode}");

        var epWritten = NfoWriter.WriteEpisode(target.VideoPath, show, episode, info, settings);

        return Judge(target, epWritten);
    }

    private static BackfillOutcome Judge(BackfillTarget target, NfoWriter.Result result) =>
        result.Changed
            ? new BackfillOutcome(target.Name, true, result.Outcome.ToString())
            : new BackfillOutcome(target.Name, false,
                result.Detail ?? result.Outcome.ToString());

    /// <summary>
    /// The TMDb id, whether the file named one or named somebody else's.
    /// </summary>
    private async Task<string?> TmdbIdFor(BackfillTarget target, CancellationToken ct)
    {
        if (target.Ids.TmdbId is { } direct) return direct;

        // IMDb before TheTVDB for a film and the other way round for a series,
        // because that is the id each source is actually authoritative for.
        var order = target.IsTv
            ? new[] { (target.Ids.TvdbId, "tvdb_id"), (target.Ids.ImdbId, "imdb_id") }
            : [(target.Ids.ImdbId, "imdb_id"), (target.Ids.TvdbId, "tvdb_id")];

        foreach (var (value, source) in order)
        {
            if (value is null) continue;

            var key = $"{source}:{value}:{target.IsTv}";

            if (!_resolved.TryGetValue(key, out var found))
                _resolved[key] = found = await resolveExternalId(value, source, target.IsTv, ct)
                    .ConfigureAwait(false);

            if (found is not null) return found;
        }

        return null;
    }

    private async Task<TvShow?> ShowFor(string tmdbId, string tvdbId, CancellationToken ct)
    {
        if (_shows.TryGetValue(tmdbId, out var cached)) return cached;

        return _shows[tmdbId] = await showById(tmdbId, tvdbId, ct).ConfigureAwait(false);
    }
}
