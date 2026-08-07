using System.Net;
using System.Text.Json;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Providers;

/// <summary>
/// TVmaze. A third, independently-curated episode list - no API key, no signup.
///
/// Its counts are usually lower than TMDb's because it lists broadcast episodes
/// rather than every extra and recap, so it's a useful cross-check: where the
/// others disagree, TVmaze tends to reflect what actually aired.
///
/// Looked up by TheTVDB id, which the TMDb search already gives us, so no
/// separate search step is needed.
/// </summary>
public sealed class TvMazeProvider(HttpClient http) : IEpisodeProvider
{
    private const string Base = "https://api.tvmaze.com";

    public async Task<TvShow?> GetShowAsync(ShowSearchResult show, string language, CancellationToken ct = default)
    {
        // TVmaze is English-only; asking for another language would silently
        // return English anyway, so don't pretend otherwise.
        var showJson = await GetAsync($"{Base}/lookup/shows?thetvdb={Uri.EscapeDataString(show.TvdbId)}", ct)
            .ConfigureAwait(false);

        if (showJson is null) return null;

        var root = showJson.RootElement;
        if (!root.TryGetProperty("id", out var idEl)) return null;

        var mazeId = idEl.GetRawText();

        var epsJson = await GetAsync($"{Base}/shows/{mazeId}/episodes?specials=1", ct).ConfigureAwait(false);
        if (epsJson is null) return null;

        var result = new TvShow
        {
            Id = show.TvdbId,
            Name = Str(root, "name") is { Length: > 0 } n ? n : show.Name,
            Overview = StripHtml(Str(root, "summary")),
            FirstAired = Str(root, "premiered"),
            Network = root.TryGetProperty("network", out var net) && net.ValueKind == JsonValueKind.Object
                ? Str(net, "name")
                : root.TryGetProperty("webChannel", out var web) && web.ValueKind == JsonValueKind.Object
                    ? Str(web, "name")
                    : null
        };

        var bySeason = new Dictionary<int, Season>();

        foreach (var e in epsJson.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("season", out var sEl) || sEl.ValueKind != JsonValueKind.Number) continue;
            if (!e.TryGetProperty("number", out var nEl) || nEl.ValueKind != JsonValueKind.Number) continue;

            var sn = sEl.GetInt32();
            var en = nEl.GetInt32();

            if (!bySeason.TryGetValue(sn, out var season))
                bySeason[sn] = season = new Season { Number = sn };

            if (season.Episodes.Any(x => x.Number == en)) continue;

            season.Episodes.Add(new Episode
            {
                SeasonNumber = sn,
                Number = en,
                Name = Str(e, "name"),
                Overview = StripHtml(Str(e, "summary")),
                FirstAired = Str(e, "airdate"),
                Source = "TVmaze"
            });
        }

        foreach (var s in bySeason.Values.OrderBy(s => s.Number))
        {
            s.Episodes.Sort((a, b) => a.Number.CompareTo(b.Number));
            result.Seasons.Add(s);
        }

        return result;
    }

    /// <summary>TVmaze wraps summaries in HTML.</summary>
    private static string? StripHtml(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", string.Empty).Trim();
    }

    private async Task<JsonDocument?> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);

            // 404 just means TVmaze doesn't know this show - not an error.
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;

            if (resp.StatusCode == (HttpStatusCode)429)
                throw new MetadataException("TVmaze rate limit reached. Try again shortly.");

            if (!resp.IsSuccessStatusCode) return null;

            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (MetadataException) { throw; }
        catch (Exception)
        {
            return null;   // a third opinion failing must not sink the lookup
        }
    }

    private static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
