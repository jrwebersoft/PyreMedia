using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PyreMedia.Core.Books;

/// <summary>
/// Metron, second in the chain.
///
/// An open comic database with a proper REST API, five thousand questions a day
/// and twenty a minute. Generous enough that a whole collection can be tagged in
/// an evening, and rationed enough to be worth asking after the local dump has
/// had its turn.
///
/// Authenticated with an ordinary username and password over Basic auth rather
/// than a key, which is worth knowing when storing it: this is an account
/// credential, not a token that can be revoked on its own.
/// </summary>
public sealed class MetronProvider(string? user, string? password, HttpClient? http = null) : IComicProvider
{
    private const string Root = "https://metron.cloud/api";

    private readonly HttpClient _http = http ?? Shared;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };

    public string Name => "Metron";

    public bool Ready => !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password);

    public string? NotReadyBecause => Ready ? null : "no metron.cloud account entered in Settings";

    private HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PyreMedia", "1.0"));

        return request;
    }

    public async Task<IReadOnlyList<ComicSeries>> SearchAsync(
        string series, string? year, CancellationToken ct = default)
    {
        if (!Ready || string.IsNullOrWhiteSpace(series)) return [];

        var url = $"{Root}/series/?name={Uri.EscapeDataString(series.Trim())}";
        if (int.TryParse(year, out var y)) url += $"&year_began={y}";

        using var response = await _http.SendAsync(Request(url), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

        var found = new List<ComicSeries>();

        foreach (var r in results.EnumerateArray())
        {
            var display = Str(r, "series") is { Length: > 0 } s ? s : Str(r, "name");

            found.Add(new ComicSeries
            {
                Id = Num(r, "id"),
                Name = display,
                YearBegan = Str(r, "year_began") is { Length: 4 } yb ? yb : YearFrom(display),
                Source = Name
            });
        }

        return found;
    }

    public async Task<IReadOnlyList<ComicIssue>> IssuesAsync(string seriesId, CancellationToken ct = default)
    {
        if (!Ready) return [];

        var issues = new List<ComicIssue>();
        var url = $"{Root}/issue/?series_id={Uri.EscapeDataString(seriesId)}";

        // Paged, and followed sequentially rather than in parallel - twenty a
        // minute is not a budget to spend four at a time.
        while (url.Length > 0 && issues.Count < 2000)
        {
            using var response = await _http.SendAsync(Request(url), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            if (doc.RootElement.TryGetProperty("results", out var results))
            {
                foreach (var r in results.EnumerateArray())
                {
                    var number = Str(r, "number");
                    if (number.Length == 0) continue;

                    issues.Add(new ComicIssue
                    {
                        Number = number,
                        Title = Str(r, "issue") is { Length: > 0 } t ? t : null,
                        Released = Str(r, "cover_date") is { Length: > 0 } d ? d : null
                    });
                }
            }

            url = doc.RootElement.TryGetProperty("next", out var next)
                  && next.ValueKind == JsonValueKind.String
                ? next.GetString() ?? ""
                : "";
        }

        return issues;
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Num(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";

    private static string? YearFrom(string display) =>
        System.Text.RegularExpressions.Regex.Match(display, @"\((19|20)(\d{2})\)") is { Success: true } m
            ? m.Value.Trim('(', ')')
            : null;
}

/// <summary>
/// Comic Vine, last in the chain.
///
/// The largest of the three and the most rationed: two hundred requests an hour
/// per resource, one a second, and a key that is revoked if used commercially.
/// Asked last for that reason alone - it is the best answer to have and the
/// worst budget to spend.
///
/// Two of its terms are worth honouring rather than merely noting. It asks that
/// responses be cached, so a series is not asked about twice in a session. And
/// it asks that anything shown links back, which is why the source of every
/// answer is carried through to the interface rather than quietly absorbed.
/// </summary>
public sealed class ComicVineProvider(string? apiKey, HttpClient? http = null) : IComicProvider
{
    private const string Root = "https://comicvine.gamespot.com/api";

    private readonly HttpClient _http = http ?? Shared;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Answers already given, so the same question is not asked twice.</summary>
    private readonly Dictionary<string, IReadOnlyList<ComicSeries>> _remembered =
        new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Comic Vine";

    public bool Ready => !string.IsNullOrWhiteSpace(apiKey);

    public string? NotReadyBecause => Ready ? null : "no Comic Vine key entered in Settings";

    private HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Comic Vine rejects requests with no user agent outright, and says so
        // in its documentation rather than leaving it to be discovered.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PyreMedia", "1.0"));

        return request;
    }

    public async Task<IReadOnlyList<ComicSeries>> SearchAsync(
        string series, string? year, CancellationToken ct = default)
    {
        if (!Ready || string.IsNullOrWhiteSpace(series)) return [];

        var key = $"{series}|{year}";

        lock (_remembered)
        {
            if (_remembered.TryGetValue(key, out var known)) return known;
        }

        var url = $"{Root}/volumes/?api_key={apiKey}&format=json&limit=40"
                + $"&filter=name:{Uri.EscapeDataString(series.Trim())}";

        using var response = await _http.SendAsync(Request(url), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var found = new List<ComicSeries>();

        if (doc.RootElement.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                var began = Str(r, "start_year");

                if (year is { Length: 4 } && began.Length == 4 && began != year) continue;

                found.Add(new ComicSeries
                {
                    Id = Num(r, "id"),
                    Name = Str(r, "name"),
                    YearBegan = began.Length == 4 ? began : null,
                    IssueCount = r.TryGetProperty("count_of_issues", out var c)
                                 && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null,
                    Publisher = r.TryGetProperty("publisher", out var p)
                                && p.ValueKind == JsonValueKind.Object ? Str(p, "name") : null,
                    Source = Name
                });
            }
        }

        lock (_remembered) _remembered[key] = found;

        return found;
    }

    public async Task<IReadOnlyList<ComicIssue>> IssuesAsync(string seriesId, CancellationToken ct = default)
    {
        if (!Ready) return [];

        var url = $"{Root}/issues/?api_key={apiKey}&format=json&limit=100"
                + $"&filter=volume:{Uri.EscapeDataString(seriesId)}&sort=issue_number:asc";

        using var response = await _http.SendAsync(Request(url), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var issues = new List<ComicIssue>();

        if (doc.RootElement.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                var number = Str(r, "issue_number");
                if (number.Length == 0) continue;

                issues.Add(new ComicIssue
                {
                    Number = number,
                    Title = Str(r, "name") is { Length: > 0 } n ? n : null,
                    Released = Str(r, "cover_date") is { Length: > 0 } d ? d : null
                });
            }
        }

        return issues;
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Num(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";
}
