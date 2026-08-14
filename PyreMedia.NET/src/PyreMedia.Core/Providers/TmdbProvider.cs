using System.Net;
using System.Text.Json;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Providers;

/// <summary>
/// TMDb v3. Used for name-to-id search (TheTVDB retired its GetSeries.php search
/// endpoint - it returns HTTP 200 with an empty &lt;Data/&gt; body for every query),
/// and optionally for episode lists.
/// </summary>
public sealed class TmdbProvider(HttpClient http, string apiKey) : IShowSearchProvider, IEpisodeProvider
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/w185";

    public async Task<IReadOnlyList<ShowSearchResult>> SearchAsync(
        string name, string? year, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return [];

        var url = $"{BaseUrl}/search/tv?api_key={apiKey}"
                + $"&query={Uri.EscapeDataString(name)}"
                + $"&language={Uri.EscapeDataString(language)}";

        if (!string.IsNullOrWhiteSpace(year))
            url += $"&first_air_date_year={Uri.EscapeDataString(year)}";

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var results))
            return [];

        // Resolve external ids in parallel - the episode fetch is keyed on tvdb_id.
        var candidates = results.EnumerateArray()
            .Where(r => r.TryGetProperty("id", out _))
            .Select(r => new
            {
                TmdbId = r.GetProperty("id").GetRawText(),
                Name = Str(r, "name"),
                Overview = Str(r, "overview"),
                FirstAir = Str(r, "first_air_date"),
                Poster = Str(r, "poster_path")
            })
            .ToList();

        var resolved = await Task.WhenAll(candidates.Select(async c =>
        {
            var tvdbId = await GetTvdbIdAsync(c.TmdbId, ct).ConfigureAwait(false);
            if (tvdbId is null)
                return null;

            return new ShowSearchResult
            {
                TmdbId = c.TmdbId,
                TvdbId = tvdbId,
                Name = c.Name,
                Overview = c.Overview,
                Year = c.FirstAir.Length >= 4 ? c.FirstAir[..4] : null,
                PosterUrl = string.IsNullOrEmpty(c.Poster) ? null : ImageBase + c.Poster
            };
        })).ConfigureAwait(false);

        return [.. resolved.OfType<ShowSearchResult>()];
    }

    // ---------------- Movies ----------------

    public async Task<IReadOnlyList<MovieSearchResult>> SearchMoviesAsync(
        string title, string? year, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var url = $"{BaseUrl}/search/movie?api_key={apiKey}"
                + $"&query={Uri.EscapeDataString(title)}"
                + $"&language={Uri.EscapeDataString(language)}";

        if (!string.IsNullOrWhiteSpace(year))
            url += $"&year={Uri.EscapeDataString(year)}";

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var results))
            return [];

        var list = new List<MovieSearchResult>();

        foreach (var r in results.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var idEl))
                continue;

            var release = Str(r, "release_date");
            var poster = Str(r, "poster_path");

            list.Add(new MovieSearchResult
            {
                TmdbId = idEl.GetRawText(),
                Title = Str(r, "title"),
                Overview = Str(r, "overview"),
                Year = release.Length >= 4 ? release[..4] : null,
                PosterUrl = string.IsNullOrEmpty(poster) ? null : ImageBase + poster
            });
        }

        return list;
    }

    public async Task<Movie?> GetMovieAsync(string tmdbId, string language, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{BaseUrl}/movie/{Uri.EscapeDataString(tmdbId)}?api_key={apiKey}"
            + $"&language={Uri.EscapeDataString(language)}", ct).ConfigureAwait(false);

        if (doc is null)
            return null;

        var root = doc.RootElement;
        var release = Str(root, "release_date");

        return new Movie
        {
            TmdbId = tmdbId,
            ImdbId = Str(root, "imdb_id"),
            Title = Str(root, "title"),
            Overview = Str(root, "overview"),
            ReleaseDate = release,
            Year = release.Length >= 4 ? release[..4] : null
        };
    }

    // ---------------- Shared ----------------

    public async Task<string?> GetTvdbIdAsync(string tmdbId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{BaseUrl}/tv/{Uri.EscapeDataString(tmdbId)}/external_ids?api_key={apiKey}", ct)
            .ConfigureAwait(false);

        if (doc is null || !doc.RootElement.TryGetProperty("tvdb_id", out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.String => el.GetString(),
            _ => null
        };
    }

    public async Task<TvShow?> GetShowAsync(ShowSearchResult show, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(show.TmdbId))
            return null;

        using var detail = await GetJsonAsync(
            $"{BaseUrl}/tv/{show.TmdbId}?api_key={apiKey}&language={Uri.EscapeDataString(language)}", ct)
            .ConfigureAwait(false);

        if (detail is null)
            return null;

        var root = detail.RootElement;
        var seasonNumbers = root.TryGetProperty("seasons", out var seasonsEl)
            ? seasonsEl.EnumerateArray()
                .Where(s => s.TryGetProperty("season_number", out _))
                .Select(s => s.GetProperty("season_number").GetInt32())
                .ToList()
            : [];

        var result = new TvShow
        {
            Id = show.TvdbId,
            Name = Str(root, "name"),
            Overview = Str(root, "overview"),
            FirstAired = Str(root, "first_air_date"),
            Network = root.TryGetProperty("networks", out var nets)
                      && nets.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } n
                          ? Str(n, "name") : null
        };

        foreach (var sn in seasonNumbers)
        {
            using var seasonDoc = await GetJsonAsync(
                $"{BaseUrl}/tv/{show.TmdbId}/season/{sn}?api_key={apiKey}&language={Uri.EscapeDataString(language)}", ct)
                .ConfigureAwait(false);

            if (seasonDoc is null || !seasonDoc.RootElement.TryGetProperty("episodes", out var epsEl))
                continue;

            var season = new Season { Number = sn };

            foreach (var e in epsEl.EnumerateArray())
            {
                if (!e.TryGetProperty("episode_number", out var numEl))
                    continue;

                season.Episodes.Add(new Episode
                {
                    SeasonNumber = sn,
                    Number = numEl.GetInt32(),
                    Name = Str(e, "name"),
                    Overview = Str(e, "overview"),
                    FirstAired = Str(e, "air_date"),
                    RuntimeMinutes = e.TryGetProperty("runtime", out var rt)
                                     && rt.ValueKind == JsonValueKind.Number
                        ? rt.GetInt32() : null,
                    Source = "TMDb"
                });
            }

            result.Seasons.Add(season);
        }

        return result;
    }

    /// <summary>
    /// Every image TMDb has for a title - posters, backdrops and logos.
    ///
    /// Deliberately unfiltered by language: a logo with no text is often the one
    /// you want, and an English poster isn't automatically better than the
    /// original. Sorted by the provider's own score so the popular ones come
    /// first, and the caller shows the lot.
    /// </summary>
    public async Task<ArtworkSet> GetImagesAsync(bool isTv, string id, CancellationToken ct = default)
    {
        var kind = isTv ? "tv" : "movie";

        // No include_image_language filter: everything, and let the user choose.
        var url = $"{BaseUrl}/{kind}/{id}/images?api_key={apiKey}";

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (doc is null) return new ArtworkSet();

        var root = doc.RootElement;

        return new ArtworkSet
        {
            Posters = Read(root, "posters", ArtKind.Poster, "w342"),
            Fanart = Read(root, "backdrops", ArtKind.Fanart, "w300"),
            Logos = Read(root, "logos", ArtKind.ClearLogo, "w300")
        };

        static List<ArtworkOption> Read(JsonElement root, string property, ArtKind kind, string thumbSize)
        {
            if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<ArtworkOption>();

            foreach (var e in arr.EnumerateArray())
            {
                var path = e.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
                if (string.IsNullOrWhiteSpace(path)) continue;

                // A clear logo is a transparent PNG, and only a PNG. TMDb does
                // carry the odd SVG, which neither WPF can show nor Kodi can use,
                // and writing one out under a .png name would be a broken file
                // that looks like a working one.
                if (kind == ArtKind.ClearLogo
                    && !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    continue;

                list.Add(new ArtworkOption
                {
                    Kind = kind,

                    // "original" keeps a logo's transparency and a poster's detail;
                    // the thumbnail is what the picker actually loads.
                    Url = "https://image.tmdb.org/t/p/original" + path,
                    ThumbUrl = $"https://image.tmdb.org/t/p/{thumbSize}{path}",

                    Width = e.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? wi : 0,
                    Height = e.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi) ? hi : 0,
                    Language = e.TryGetProperty("iso_639_1", out var l) ? l.GetString() : null,
                    Score = e.TryGetProperty("vote_average", out var v) && v.TryGetDouble(out var vd) ? vd : 0
                });
            }

            return [.. list.OrderByDescending(i => i.Score).ThenByDescending(i => i.Width)];
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new MetadataException("TMDb rejected the API key (HTTP 401). Check the key in Settings.");

            if (resp.StatusCode == (HttpStatusCode)429)
                throw new MetadataException("TMDb rate limit reached (HTTP 429). Try again shortly.");

            if (!resp.IsSuccessStatusCode)
                throw new MetadataException($"TMDb returned HTTP {(int)resp.StatusCode} for {url}");

            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
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
            throw new MetadataException($"TMDb request failed: {ex.Message}", ex);
        }
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
