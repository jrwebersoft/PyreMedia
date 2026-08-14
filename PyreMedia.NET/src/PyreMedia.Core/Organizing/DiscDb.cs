using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PyreMedia.Core.Organizing;

/// <summary>What a catalogued disc says one of its titles is.</summary>
public sealed record DiscEntry(int Index, double Seconds, int Season, int Episode, string Title);

/// <summary>A disc that matched, and what it says each title is.</summary>
public sealed class DiscMatch
{
    public required string Release { get; init; }
    public required string Disc { get; init; }

    /// <summary>By MakeMKV title index.</summary>
    public required Dictionary<int, DiscEntry> Titles { get; init; }

    /// <summary>How many of the ripped titles this accounted for.</summary>
    public required int Matched { get; init; }

    public required int OutOf { get; init; }
}

/// <summary>
/// Looking a disc up in TheDiscDb, so its titles can be named rather than
/// merely ordered.
///
/// A Blu-ray records no episode numbers, so working them out locally can only
/// ever produce an order and a caution. TheDiscDb is people who have already
/// ripped that exact disc writing down which title held which episode -
/// contributed data, MIT licensed, kept as plain JSON.
///
/// The awkward part is identifying the disc after the fact. TheDiscDb keys
/// discs by a content hash MakeMKV computes from the disc itself, which is no
/// use once the disc is back on the shelf and only the files remain. What does
/// survive is the shape of the rip: a sequence of durations, to the second,
/// which is distinctive enough to pick one disc out of a catalogue.
///
/// Everything here fails quietly. It is an improvement on a good answer, not a
/// requirement - no network, no match, or a rate limit simply leaves the local
/// reading standing.
/// </summary>
public static class DiscDb
{
    private const string Api = "https://api.github.com/repos/TheDiscDb/data/contents";
    private const string Raw = "https://raw.githubusercontent.com/TheDiscDb/data/main";

    /// <summary>Durations this close are the same title. Rips vary by a frame or two.</summary>
    private const double Close = 2.5;

    /// <summary>
    /// How much of a rip a disc must account for before it is believed.
    ///
    /// Not all of it: a rip made with a minimum-length filter has fewer titles
    /// than the catalogue lists, and one made without has more. Three quarters
    /// agreeing to the second is far beyond coincidence.
    /// </summary>
    private const double Enough = 0.75;

    private static readonly HttpClient Http = Make();

    private static HttpClient Make()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // GitHub refuses anonymous requests with no User-Agent, and asking
        // politely costs nothing.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PyreMedia", "1.0"));

        return http;
    }

    /// <summary>
    /// Find the disc these titles came from, or null.
    /// </summary>
    /// <param name="show">The show as the provider named it, e.g. "30 Rock".</param>
    /// <param name="year">First-aired year, which the catalogue folders carry.</param>
    public static async Task<DiscMatch?> FindAsync(
        string show, string? year, IReadOnlyList<RipTitle> titles, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(show) || titles.Count == 0) return null;

        try
        {
            var folder = await SeriesFolderAsync(show, year, ct).ConfigureAwait(false);
            if (folder is null) return null;

            foreach (var release in await ListAsync($"{Api}/data/series/{Esc(folder)}", "dir", ct)
                         .ConfigureAwait(false))
            {
                var discs = await ListAsync(
                    $"{Api}/data/series/{Esc(folder)}/{Esc(release)}", "file", ct).ConfigureAwait(false);

                foreach (var disc in discs.Where(d =>
                             d.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                {
                    var url = $"{Raw}/data/series/{Esc(folder)}/{Esc(release)}/{Esc(disc)}";

                    if (await MatchAsync(url, release, titles, ct).ConfigureAwait(false) is { } hit)
                        return hit;
                }
            }
        }
        catch (Exception)
        {
            // No network, a rate limit, a layout change upstream. None of it is
            // worth interrupting somebody over: the local reading still stands.
        }

        return null;
    }

    /// <summary>The catalogue folder for a show, e.g. "30 Rock (2006)".</summary>
    private static async Task<string?> SeriesFolderAsync(string show, string? year, CancellationToken ct)
    {
        var all = await ListAsync($"{Api}/data/series", "dir", ct).ConfigureAwait(false);

        // "30 Rock (2006)" exactly, when the year is known.
        if (!string.IsNullOrWhiteSpace(year))
        {
            var exact = all.FirstOrDefault(f =>
                string.Equals(f, $"{show} ({year})", StringComparison.OrdinalIgnoreCase));

            if (exact is not null) return exact;
        }

        // Otherwise the name with any year at all. A show is not in there twice
        // under different years often enough to be worth agonising over, and
        // the duration match still has to agree afterwards.
        return all.FirstOrDefault(f =>
            f.StartsWith(show + " (", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, show, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Names of the entries of one kind in a catalogue folder.</summary>
    private static async Task<List<string>> ListAsync(string url, string kind, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        return [.. doc.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == kind)
            .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
            .Where(n => n.Length > 0)];
    }

    /// <summary>Compare one catalogued disc against the rip.</summary>
    private static async Task<DiscMatch?> MatchAsync(
        string url, string release, IReadOnlyList<RipTitle> titles, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("Titles", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;

        var entries = new List<DiscEntry>();

        foreach (var t in list.EnumerateArray())
        {
            if (!t.TryGetProperty("Item", out var item) || item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("Type", out var type)
                && !string.Equals(type.GetString(), "Episode", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Hms(Str(t, "Duration"), out var seconds)) continue;

            entries.Add(new DiscEntry(
                t.TryGetProperty("Index", out var i) && i.ValueKind == JsonValueKind.Number
                    ? i.GetInt32() : -1,
                seconds,
                Number(item, "Season"),
                Number(item, "Episode"),
                Str(item, "Title")));
        }

        if (entries.Count == 0) return null;

        // Match by duration rather than by index. MakeMKV's numbering shifts
        // with its minimum-length setting and its version, so the index is
        // corroboration rather than a key - but a run of durations agreeing to
        // the second is the disc.
        var taken = new HashSet<DiscEntry>();
        var mapped = new Dictionary<int, DiscEntry>();

        foreach (var title in titles.OrderBy(t => t.Index))
        {
            var hit = entries
                .Where(e => !taken.Contains(e) && Math.Abs(e.Seconds - title.Seconds) <= Close)
                .OrderBy(e => Math.Abs(e.Index - title.Index))      // index breaks ties
                .ThenBy(e => Math.Abs(e.Seconds - title.Seconds))
                .FirstOrDefault();

            if (hit is null) continue;

            taken.Add(hit);
            mapped[title.Index] = hit;
        }

        if (mapped.Count < titles.Count * Enough) return null;

        return new DiscMatch
        {
            Release = release,
            Disc = Str(root, "Name"),
            Titles = mapped,
            Matched = mapped.Count,
            OutOf = titles.Count
        };
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Number(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v)
        && int.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString(), out var n)
            ? n : 0;

    /// <summary>"0:21:35" as seconds.</summary>
    private static bool Hms(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length != 3) return false;

        var c = System.Globalization.CultureInfo.InvariantCulture;

        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, c, out var h)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, c, out var m)
            || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, c, out var s))
            return false;

        seconds = h * 3600 + m * 60 + s;
        return true;
    }

    private static string Esc(string segment) => Uri.EscapeDataString(segment);
}
