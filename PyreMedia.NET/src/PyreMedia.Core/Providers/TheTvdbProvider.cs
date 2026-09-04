using System.Net;
using System.Xml.Linq;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Providers;

/// <summary>
/// TheTVDB legacy XML API, episode data only.
///
/// Its search endpoint (GetSeries.php) was retired server-side and now always
/// returns an empty document, so searching lives in <see cref="TmdbProvider"/>.
/// The by-id endpoints below still serve live data and are what libraries built
/// with MediaScout 3.x were numbered against, so this stays the default episode
/// source to keep existing libraries consistent.
/// </summary>
public sealed class TheTvdbProvider(HttpClient http, string apiKey) : IEpisodeProvider
{
    private const string Base = "https://www.thetvdb.com/api";

    public async Task<TvShow?> GetShowAsync(ShowSearchResult show, string language, CancellationToken ct = default)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language;
        var url = $"{Base}/{apiKey}/series/{Uri.EscapeDataString(show.TvdbId)}/all/{lang}.xml";

        XDocument doc;
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!resp.IsSuccessStatusCode)
                throw new MetadataException($"TheTVDB returned HTTP {(int)resp.StatusCode} for series {show.TvdbId}.");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MetadataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MetadataException($"TheTVDB request failed: {ex.Message}", ex);
        }

        var seriesEl = doc.Root?.Element("Series");
        if (seriesEl is null)
            return null;

        var result = new TvShow
        {
            Id = show.TvdbId,
            Name = Val(seriesEl, "SeriesName") is { Length: > 0 } n ? n : show.Name,
            Overview = Val(seriesEl, "Overview"),
            Network = Val(seriesEl, "Network"),
            FirstAired = Val(seriesEl, "FirstAired")
        };

        var seasons = new Dictionary<int, Season>();

        foreach (var epEl in doc.Root!.Elements("Episode"))
        {
            if (!int.TryParse(Val(epEl, "SeasonNumber"), out var sn)) continue;
            if (!int.TryParse(Val(epEl, "EpisodeNumber"), out var en)) continue;

            if (!seasons.TryGetValue(sn, out var season))
            {
                season = new Season { Number = sn };
                seasons[sn] = season;
            }

            // Guard against duplicate numbering in the source data.
            if (season.Episodes.Any(e => e.Number == en))
                continue;

            season.Episodes.Add(new Episode
            {
                SeasonNumber = sn,
                Number = en,
                Name = Val(epEl, "EpisodeName"),
                Overview = Val(epEl, "Overview"),
                FirstAired = Val(epEl, "FirstAired"),
                ProductionCode = Val(epEl, "ProductionCode"),
                StillUrl = Still(Val(epEl, "filename")),
                Source = "TheTVDB"
            });
        }

        foreach (var season in seasons.Values.OrderBy(s => s.Number))
        {
            season.Episodes.Sort((a, b) => a.Number.CompareTo(b.Number));
            result.Seasons.Add(season);
        }

        return result;
    }

    private static string Val(XElement parent, string name) =>
        parent.Element(name)?.Value?.Trim() ?? string.Empty;

    /// <summary>
    /// The episode's own frame. TheTVDB stores a path relative to its banner
    /// host, and an absent image is an empty element rather than a missing one -
    /// so an empty string has to mean "none" rather than becoming a url to
    /// nothing.
    /// </summary>
    private static string? Still(string filename) =>
        string.IsNullOrWhiteSpace(filename)
            ? null
            : filename.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? filename
                : "https://artworks.thetvdb.com/banners/" + filename.TrimStart('/');
}
