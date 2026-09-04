namespace PyreMedia.Core.Books;

/// <summary>One series a provider found.</summary>
public sealed record ComicSeries
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? YearBegan { get; init; }
    public int? IssueCount { get; init; }

    /// <summary>Which provider said so, since they disagree and it matters which.</summary>
    public required string Source { get; init; }

    public string Display => YearBegan is { Length: 4 }
        ? $"{Name} ({YearBegan})" + (Publisher is null ? "" : $" - {Publisher}")
        : Name + (Publisher is null ? "" : $" - {Publisher}");
}

/// <summary>One issue of a series.</summary>
public sealed record ComicIssue
{
    public required string Number { get; init; }
    public string? Title { get; init; }
    public string? Released { get; init; }

    public string? Year => Released is { Length: >= 4 } d && d[..4].All(char.IsDigit) ? d[..4] : null;
}

/// <summary>Somewhere comic data can be asked for.</summary>
public interface IComicProvider
{
    /// <summary>Shown to the user, and written into the log so an answer can be traced.</summary>
    string Name { get; }

    /// <summary>False when it has not been set up - no dump downloaded, no key entered.</summary>
    bool Ready { get; }

    /// <summary>Why it is not ready, for saying so once rather than failing silently.</summary>
    string? NotReadyBecause { get; }

    Task<IReadOnlyList<ComicSeries>> SearchAsync(string series, string? year, CancellationToken ct = default);

    Task<IReadOnlyList<ComicIssue>> IssuesAsync(string seriesId, CancellationToken ct = default);
}

/// <summary>
/// The providers in the order they should be asked.
///
/// Stack-ranked by what a question costs. The Grand Comics Database is a local
/// SQLite file, so asking it is free and unlimited and answers in milliseconds.
/// Metron allows five thousand questions a day. Comic Vine allows two hundred an
/// hour and will revoke a key used commercially. Spending the scarce one first
/// would be the wrong way round.
///
/// It is a fall-through rather than a merge. Comic databases disagree about
/// series names, publisher attribution and which year a run began, and averaging
/// three disagreeing answers produces a fourth that none of them holds. The
/// first provider with a real answer wins, and which one it was is recorded.
/// </summary>
public sealed class ComicProviderChain(IEnumerable<IComicProvider> providers)
{
    private readonly List<IComicProvider> _providers = [.. providers];

    /// <summary>Every provider, in order, whether ready or not.</summary>
    public IReadOnlyList<IComicProvider> All => _providers;

    /// <summary>What was asked, and what answered - for the log.</summary>
    public sealed record Attempt(string Provider, bool Ready, int Results, string? Trouble);

    public sealed record Answer(
        IReadOnlyList<ComicSeries> Series,
        string? From,
        IReadOnlyList<Attempt> Tried);

    public async Task<Answer> SearchAsync(string series, string? year, CancellationToken ct = default)
    {
        var tried = new List<Attempt>();

        foreach (var p in _providers)
        {
            if (!p.Ready)
            {
                tried.Add(new Attempt(p.Name, false, 0, p.NotReadyBecause));
                continue;
            }

            try
            {
                var hits = await p.SearchAsync(series, year, ct).ConfigureAwait(false);

                tried.Add(new Attempt(p.Name, true, hits.Count, null));

                if (hits.Count > 0) return new Answer(hits, p.Name, tried);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A provider being down, rate limited or having changed its
                // shape is a reason to try the next one, not to stop.
                tried.Add(new Attempt(p.Name, true, 0, ex.Message));
            }
        }

        return new Answer([], null, tried);
    }

    /// <summary>Issues from whichever provider supplied the series.</summary>
    public async Task<IReadOnlyList<ComicIssue>> IssuesAsync(
        string source, string seriesId, CancellationToken ct = default)
    {
        var p = _providers.FirstOrDefault(x =>
            string.Equals(x.Name, source, StringComparison.OrdinalIgnoreCase));

        if (p is null || !p.Ready) return [];

        try { return await p.IssuesAsync(seriesId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// One line saying what is available, for the log after a scan.
    ///
    /// Worth saying once: a chain where nothing is set up looks identical to a
    /// chain where nothing matched, and those want different responses from the
    /// person reading it.
    /// </summary>
    public string Readiness()
    {
        var ready = _providers.Where(p => p.Ready).Select(p => p.Name).ToList();

        if (ready.Count == _providers.Count) return $"Comic sources: {string.Join(", ", ready)}.";

        var missing = _providers.Where(p => !p.Ready)
            .Select(p => $"{p.Name} ({p.NotReadyBecause})");

        return ready.Count == 0
            ? $"No comic sources are set up: {string.Join("; ", missing)}."
            : $"Comic sources: {string.Join(", ", ready)}. Not set up: {string.Join("; ", missing)}.";
    }
}
