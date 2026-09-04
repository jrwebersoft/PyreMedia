using PyreMedia.Core.Models;

namespace PyreMedia.Core.Providers;

/// <summary>
/// Wires search and episode lookup together according to settings, so callers
/// don't care which service answers which half.
/// </summary>
public sealed class MetadataService : IDisposable
{
    private readonly HttpClient _http;
    private readonly TmdbProvider _tmdb;
    private readonly TheTvdbProvider _tvdb;
    private readonly TvMazeProvider _tvmaze;
    private readonly PyreMediaSettings _settings;

    public MetadataService(PyreMediaSettings settings)
    {
        _settings = settings;

        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PyreMedia/4.0");

        _tmdb = new TmdbProvider(_http, settings.TmdbApiKey);
        _tvdb = new TheTvdbProvider(_http, settings.TvdbApiKey);
        _tvmaze = new TvMazeProvider(_http);
    }

    /// <summary>
    /// Search shows. The year narrows results, but release names often carry the
    /// episode's year rather than the show's première year - so a year-filtered
    /// search that finds nothing is retried without it instead of reporting
    /// "no results".
    /// </summary>
    public async Task<IReadOnlyList<ShowSearchResult>> SearchAsync(
        string name, string? year = null, CancellationToken ct = default)
    {
        var results = await _tmdb.SearchAsync(name, year, _settings.Language, ct).ConfigureAwait(false);

        if (results.Count == 0 && !string.IsNullOrWhiteSpace(year))
            results = await _tmdb.SearchAsync(name, null, _settings.Language, ct).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Episode data for a show. In Merged mode both sources are queried in
    /// parallel and combined, so a gap in one doesn't lose the episode.
    /// </summary>
    /// <summary>
    /// Episode data from one named source, ignoring both the global setting and
    /// any per-show pin. Used by the renumber screen, where comparing how each
    /// provider numbers a show is the entire point.
    /// </summary>
    public Task<TvShow?> GetShowFromAsync(
        ShowSearchResult show, EpisodeSource source, CancellationToken ct = default)
        => source switch
        {
            EpisodeSource.Tmdb => _tmdb.GetShowAsync(show, _settings.Language, ct),
            EpisodeSource.TheTvdbLegacy => _tvdb.GetShowAsync(show, _settings.Language, ct),
            EpisodeSource.TvMaze => _tvmaze.GetShowAsync(show, _settings.Language, ct),
            _ => GetShowAsync(show, null, ct)
        };

    public async Task<TvShow?> GetShowAsync(
        ShowSearchResult show,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        // A per-show choice beats the global setting: the user has already looked
        // at this show and decided which source numbers it correctly.
        var effective = _settings.EpisodeSource;

        if (_settings.ShowSourceOverrides.TryGetValue(show.TvdbId, out var pinned))
        {
            effective = pinned.ToLowerInvariant() switch
            {
                "tmdb" => EpisodeSource.Tmdb,
                "thetvdb" or "tvdb" => EpisodeSource.TheTvdbLegacy,
                _ => EpisodeSource.Merged
            };

            log?.Report($"Using pinned source for this show: {pinned}.");
        }

        switch (effective)
        {
            case EpisodeSource.Tmdb:
                return await _tmdb.GetShowAsync(show, _settings.Language, ct).ConfigureAwait(false);

            case EpisodeSource.TheTvdbLegacy:
                return await _tvdb.GetShowAsync(show, _settings.Language, ct).ConfigureAwait(false);

            default:
                // TMDb and TheTVDB in parallel - both are independent, so a slow
                // or broken one costs nothing but its own result.
                //
                // TVmaze is deliberately NOT queried here. Measured across real
                // shows it contributed +0 episodes every time, so making it a
                // third always-on request would be pure latency. It's held back
                // as a fallback below, where it actually earns its keep.
                var tasks = new List<(string Name, Task<TvShow?> Task)>
                {
                    ("TMDb", Safe(() => _tmdb.GetShowAsync(show, _settings.Language, ct))),
                    ("TheTVDB", Safe(() => _tvdb.GetShowAsync(show, _settings.Language, ct)))
                };

                await Task.WhenAll(tasks.Select(t => t.Task)).ConfigureAwait(false);

                var got = tasks
                    .Select(t => (t.Name, Show: t.Task.Result))
                    .Where(t => t.Show is not null)
                    .Select(t => (t.Name, Show: t.Show!))
                    .ToList();

                // Fallback: only when the usual sources gave us nothing usable.
                if (got.Count == 0 || got.All(g => g.Show.Seasons.Sum(s => s.Episodes.Count) == 0))
                {
                    if (_settings.EnableTvMaze)
                    {
                        var maze = await Safe(() => _tvmaze.GetShowAsync(show, _settings.Language, ct))
                            .ConfigureAwait(false);

                        if (maze is not null && maze.Seasons.Sum(s => s.Episodes.Count) > 0)
                        {
                            log?.Report($"Episodes: TMDb and TheTVDB had nothing; TVmaze supplied {Count(maze)}.");
                            return maze;
                        }
                    }

                    if (got.Count == 0) return null;
                }

                // TMDb first when present - it's the richest - then fill gaps.
                var ordered = got.OrderByDescending(g => g.Name == "TMDb")
                                 .ThenByDescending(g => g.Show.Seasons.Sum(s => s.Episodes.Count))
                                 .ToList();

                var merged = ordered[0].Show;
                var contributions = new List<string> { $"{ordered[0].Name} {Count(merged)}" };

                foreach (var (name, other) in ordered.Skip(1))
                {
                    var before = Count(merged);
                    merged = MergeShows(merged, other, null);
                    var added = Count(merged) - before;
                    contributions.Add($"{name} +{added}");
                }

                log?.Report($"Episodes: {string.Join(", ", contributions)} = {Count(merged)} total.");
                return merged;
        }

        static int Count(TvShow s) => s.Seasons.Sum(x => x.Episodes.Count);
    }

    private static async Task<TvShow?> Safe(Func<Task<TvShow?>> f)
    {
        try { return await f().ConfigureAwait(false); }
        catch (Exception) { return null; }   // one source failing must not sink the other
    }

    /// <summary>
    /// Every poster, backdrop and logo TMDb has for a title. Unfiltered and
    /// unsorted beyond popularity - the point is for a person to choose.
    /// </summary>
    public Task<Models.ArtworkSet> GetArtworkAsync(bool isTv, string tmdbId, CancellationToken ct = default) =>
        _tmdb.GetImagesAsync(isTv, tmdbId, ct);

    /// <summary>
    /// Union of both sources, keyed on (season, episode). <paramref name="primary"/>
    /// wins on titles; <paramref name="secondary"/> contributes anything primary lacks.
    /// </summary>
    private static TvShow MergeShows(TvShow primary, TvShow secondary, IProgress<string>? log)
    {
        var merged = new TvShow
        {
            Id = primary.Id,
            Name = string.IsNullOrWhiteSpace(primary.Name) ? secondary.Name : primary.Name,
            Overview = primary.Overview ?? secondary.Overview,
            Network = primary.Network ?? secondary.Network,
            FirstAired = primary.FirstAired ?? secondary.FirstAired
        };

        var bySeason = new Dictionary<int, Dictionary<int, Episode>>();
        var posters = new Dictionary<int, string>();

        void Absorb(TvShow src, bool isPrimary)
        {
            foreach (var season in src.Seasons)
            {
                // First source with a poster for a season keeps it, so the
                // primary wins and the secondary fills what it did not have.
                if (season.PosterUrl is { } art && !posters.ContainsKey(season.Number))
                    posters[season.Number] = art;

                if (!bySeason.TryGetValue(season.Number, out var eps))
                    bySeason[season.Number] = eps = [];

                foreach (var ep in season.Episodes)
                {
                    if (isPrimary || !eps.TryGetValue(ep.Number, out var already))
                    {
                        eps[ep.Number] = ep;
                        continue;
                    }

                    // The primary keeps the episode, but a field it simply does
                    // not have is worth taking from the other. Stills are the
                    // case that matters: TMDb has none for a lot of older shows
                    // and TheTVDB often does, and throwing that away would leave
                    // the episode with no picture for no reason.
                    if (already.StillUrl is null && ep.StillUrl is not null)
                        eps[ep.Number] = already.WithStill(ep.StillUrl);
                }
            }
        }

        Absorb(primary, true);

        var before = bySeason.Sum(s => s.Value.Count);
        Absorb(secondary, false);
        var added = bySeason.Sum(s => s.Value.Count) - before;

        foreach (var (number, eps) in bySeason.OrderBy(kv => kv.Key))
        {
            merged.Seasons.Add(new Season
            {
                Number = number,
                PosterUrl = posters.GetValueOrDefault(number),
                Episodes = [.. eps.Values.OrderBy(e => e.Number)]
            });
        }

        log?.Report(added > 0
            ? $"Episodes: merged TMDb + TheTVDB ({added} extra from TheTVDB)."
            : "Episodes: merged TMDb + TheTVDB (TMDb covered everything).");

        // A merge is a union keyed on episode number, which cannot reconcile two
        // sources that number a season differently - one splitting a two-parter
        // shifts everything after it. The giveaway is the same title appearing
        // twice in a season, and it is worth saying: the numbers are a blend of
        // two schemes, and picking one source or using Renumber is the way out.
        var muddled = merged.Seasons
            .Where(s => s.Number > 0)
            .Select(s => (s.Number, Repeats: s.Episodes
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .GroupBy(e => e.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Count(g => g.Count() > 1)))
            .Where(x => x.Repeats > 0)
            .ToList();

        if (muddled.Count > 0)
        {
            log?.Report(
                $"The two sources number season {string.Join(", ", muddled.Select(m => m.Number))} "
                + "differently - some titles appear twice. Pick a single source at the top of "
                + "Renumber, or map the files there by hand.");
        }

        return merged;
    }

    public async Task<IReadOnlyList<MovieSearchResult>> SearchMoviesAsync(
        string title, string? year = null, CancellationToken ct = default)
    {
        var results = await _tmdb.SearchMoviesAsync(title, year, _settings.Language, ct).ConfigureAwait(false);

        // Same fallback: a wrong year shouldn't mean no results.
        if (results.Count == 0 && !string.IsNullOrWhiteSpace(year))
            results = await _tmdb.SearchMoviesAsync(title, null, _settings.Language, ct).ConfigureAwait(false);

        return results;
    }

    public Task<Movie?> GetMovieAsync(string tmdbId, CancellationToken ct = default)
        => _tmdb.GetMovieAsync(tmdbId, _settings.Language, ct);

    public void Dispose() => _http.Dispose();
}
