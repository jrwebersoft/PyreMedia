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
        // append_to_response, not three more round trips. TMDb returns the
        // credits and the certificate inside the reply it was already sending,
        // so a full cast costs one request rather than three.
        using var doc = await GetJsonAsync(
            $"{BaseUrl}/movie/{Uri.EscapeDataString(tmdbId)}?api_key={apiKey}"
            + $"&language={Uri.EscapeDataString(language)}"
            + "&append_to_response=credits,release_dates", ct).ConfigureAwait(false);

        if (doc is null)
            return null;

        var root = doc.RootElement;
        var release = Str(root, "release_date");
        var credits = root.TryGetProperty("credits", out var cr) ? cr : default;

        return new Movie
        {
            TmdbId = tmdbId,
            ImdbId = Str(root, "imdb_id"),
            Title = Str(root, "title"),
            Overview = Str(root, "overview"),
            ReleaseDate = release,
            Year = release.Length >= 4 ? release[..4] : null,

            Tagline = Blank(Str(root, "tagline")),
            RuntimeMinutes = Int(root, "runtime"),
            Certification = Certificate(root, "release_dates", "release_dates", "certification"),
            Rating = ScoreOf(root, "themoviedb"),

            Genres = Names(root, "genres"),
            Studios = Names(root, "production_companies"),
            Countries = Names(root, "production_countries"),

            Cast = CastOf(credits),
            Directors = CrewOf(credits, DirectorJobs),
            Writers = CrewOf(credits, WriterJobs)
        };
    }

    /// <summary>
    /// The TMDb id for a title known only by somebody else's id.
    ///
    /// Half a real library names its titles with an IMDb or TheTVDB id and no
    /// TMDb one - written by Emby, or by a Kodi old enough to predate
    /// uniqueid. Without this, those files can only be matched by name, which
    /// is the guessing an unattended pass must not do.
    /// </summary>
    /// <param name="source">"imdb_id" or "tvdb_id", as TMDb spells them.</param>
    public async Task<string?> FindByExternalIdAsync(
        string externalId, string source, bool isTv, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;

        using var doc = await GetJsonAsync(
            $"{BaseUrl}/find/{Uri.EscapeDataString(externalId)}?api_key={apiKey}"
            + $"&external_source={Uri.EscapeDataString(source)}", ct).ConfigureAwait(false);

        if (doc is null) return null;

        var bucket = isTv ? "tv_results" : "movie_results";

        if (!doc.RootElement.TryGetProperty(bucket, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;

        // One hit or none. Two titles sharing an external id would mean the
        // other source had merged something, and picking between them here
        // would be the guess this exists to avoid.
        var hits = arr.EnumerateArray().ToList();

        if (hits.Count != 1) return null;

        return hits[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
            ? id.GetInt32().ToString()
            : null;
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

        // aggregate_credits rather than credits: on a long-running series the
        // plain endpoint returns whoever happened to be in the first season,
        // while this one is the cast of the show as a whole, with every
        // character each actor has played folded into one entry.
        using var detail = await GetJsonAsync(
            $"{BaseUrl}/tv/{show.TmdbId}?api_key={apiKey}&language={Uri.EscapeDataString(language)}"
            + "&append_to_response=aggregate_credits,content_ratings", ct)
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
                          ? Str(n, "name") : null,

            Status = Blank(Str(root, "status")),
            Certification = Certificate(root, "content_ratings", null, "rating"),
            Rating = ScoreOf(root, "themoviedb"),

            // A series states a list of typical lengths - a half-hour show that
            // once ran a double episode has both. The first is the usual one.
            RuntimeMinutes = root.TryGetProperty("episode_run_time", out var rts)
                             && rts.ValueKind == JsonValueKind.Array
                             && rts.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } first
                ? first.GetInt32() : null,

            Genres = Names(root, "genres"),
            Studios = Names(root, "networks"),
            Creators = Names(root, "created_by").Select(c => new Person { Name = c }).ToList(),

            Cast = AggregateCast(root)
        };

        foreach (var sn in seasonNumbers)
        {
            using var seasonDoc = await GetJsonAsync(
                $"{BaseUrl}/tv/{show.TmdbId}/season/{sn}?api_key={apiKey}&language={Uri.EscapeDataString(language)}", ct)
                .ConfigureAwait(false);

            if (seasonDoc is null || !seasonDoc.RootElement.TryGetProperty("episodes", out var epsEl))
                continue;

            var season = new Season
            {
                Number = sn,

                // Also already in this reply, like the stills. Most series
                // change their artwork every year, and Kodi shows a season's
                // own poster when the series is opened.
                PosterUrl = seasonDoc.RootElement.TryGetProperty("poster_path", out var sp)
                            && sp.GetString() is { Length: > 0 } sPath
                    ? "https://image.tmdb.org/t/p/w500" + sPath
                    : null
            };

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

                    // Already in this response - the season endpoint returns a
                    // still for every episode, so the picture of the episode
                    // costs nothing beyond reading the field that was always
                    // there.
                    StillUrl = Still(e),

                    Source = "TMDb",

                    // Both already in this reply. The season endpoint carries a
                    // crew and a guest list per episode, so the director of one
                    // episode costs nothing beyond reading the field.
                    GuestStars = CastList(e, "guest_stars"),
                    Directors = CrewIn(e, DirectorJobs),
                    Writers = CrewIn(e, WriterJobs),
                    Rating = ScoreOf(e, "themoviedb")
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

    /// <summary>
    /// An episode's own frame, at a size worth keeping.
    ///
    /// w780 rather than original: a still is a 16:9 frame shown small in a list,
    /// and the original is often a 1920-wide file for something displayed at a
    /// few hundred pixels. Fifty of those per season is a lot of disk for no
    /// visible difference.
    /// </summary>
    private static string? Still(JsonElement episode)
    {
        var path = episode.TryGetProperty("still_path", out var s) ? s.GetString() : null;

        return string.IsNullOrWhiteSpace(path) ? null : "https://image.tmdb.org/t/p/w780" + path;
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

    /// <summary>Jobs that mean "directed it", as TMDb spells them.</summary>
    private static readonly string[] DirectorJobs = ["Director"];

    /// <summary>
    /// Jobs that mean "wrote it". Kodi has one field for all of them, and a
    /// screenplay credit is a writing credit by any reading.
    /// </summary>
    private static readonly string[] WriterJobs = ["Writer", "Screenplay", "Story", "Teleplay"];

    /// <summary>Profile shots, at a size worth caching beside a library.</summary>
    private const string ProfileBase = "https://image.tmdb.org/t/p/w185";

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int? Int(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    /// <summary>Every "name" in an array of objects - genres, networks, countries.</summary>
    private static List<string> Names(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return [.. arr.EnumerateArray()
            .Select(x => Str(x, "name"))
            .Where(n => !string.IsNullOrWhiteSpace(n))];
    }

    /// <summary>
    /// The score, where enough people have voted to make it one.
    ///
    /// A single vote is not a rating, it is somebody's opinion, and writing 10.0
    /// into a library on the strength of one is worse than writing nothing -
    /// nothing at least leaves Kodi's own sorting alone.
    /// </summary>
    private static Rating? ScoreOf(JsonElement root, string source)
    {
        if (!root.TryGetProperty("vote_average", out var v) || v.ValueKind != JsonValueKind.Number)
            return null;

        var value = v.GetDouble();
        var votes = Int(root, "vote_count") ?? 0;

        if (value <= 0 || votes < 2) return null;

        return new Rating { Source = source, Value = value, Votes = votes };
    }

    /// <summary>
    /// A certificate from the viewer's own country, falling back to the US and
    /// then to whatever exists.
    ///
    /// Never averaged and never merged: a rating is a legal judgement made
    /// somewhere specific, and a film rated 12 in one country and R in another
    /// has not been rated "12/R" by anybody.
    /// </summary>
    private static string? Certificate(JsonElement root, string block, string? inner, string field)
    {
        if (!root.TryGetProperty(block, out var b)
            || !b.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            return null;

        var entries = results.EnumerateArray().ToList();

        var preferred = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;

        foreach (var country in new[] { preferred, "US" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hit = entries.FirstOrDefault(e =>
                string.Equals(Str(e, "iso_3166_1"), country, StringComparison.OrdinalIgnoreCase));

            if (hit.ValueKind != JsonValueKind.Object) continue;

            if (Pick(hit) is { } found) return found;
        }

        foreach (var e in entries)
            if (Pick(e) is { } found) return found;

        return null;

        string? Pick(JsonElement entry)
        {
            if (inner is null) return Blank(Str(entry, field));

            if (!entry.TryGetProperty(inner, out var list) || list.ValueKind != JsonValueKind.Array)
                return null;

            return list.EnumerateArray()
                       .Select(r => Str(r, field))
                       .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        }
    }

    private static Person Actor(JsonElement p, string? role, int? order) => new()
    {
        Name = Str(p, "name"),
        Role = Blank(role ?? string.Empty),
        Order = order,
        ThumbUrl = Str(p, "profile_path") is { Length: > 0 } path ? ProfileBase + path : null
    };

    /// <summary>A plain cast array - a film's, or an episode's guest list.</summary>
    private static List<Person> CastList(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return [.. arr.EnumerateArray()
            .Where(c => !string.IsNullOrWhiteSpace(Str(c, "name")))
            .Select(c => Actor(c, Str(c, "character"), Int(c, "order")))];
    }

    private static List<Person> CastOf(JsonElement credits) =>
        credits.ValueKind == JsonValueKind.Object ? CastList(credits, "cast") : [];

    /// <summary>
    /// The cast of a whole series. Each entry carries every character that actor
    /// has played, which for a long run is the honest answer - one actor, three
    /// roles - so they are joined rather than one being picked.
    /// </summary>
    private static List<Person> AggregateCast(JsonElement root)
    {
        if (!root.TryGetProperty("aggregate_credits", out var credits)
            || !credits.TryGetProperty("cast", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return [];

        var people = new List<Person>();

        foreach (var c in arr.EnumerateArray())
        {
            if (string.IsNullOrWhiteSpace(Str(c, "name"))) continue;

            var roles = c.TryGetProperty("roles", out var rs) && rs.ValueKind == JsonValueKind.Array
                ? rs.EnumerateArray().Select(r => Str(r, "character"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList()
                : [];

            people.Add(Actor(c, string.Join(" / ", roles), Int(c, "order")));
        }

        return people;
    }

    private static List<Person> CrewIn(JsonElement root, string[] jobs)
    {
        if (!root.TryGetProperty("crew", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        // Distinct by name: somebody credited as both Writer and Story is one
        // person who wrote it, not two.
        return [.. arr.EnumerateArray()
            .Where(c => jobs.Contains(Str(c, "job"), StringComparer.OrdinalIgnoreCase))
            .Where(c => !string.IsNullOrWhiteSpace(Str(c, "name")))
            .GroupBy(c => Str(c, "name"), StringComparer.OrdinalIgnoreCase)
            .Select(g => Actor(g.First(), Str(g.First(), "job"), null))];
    }

    private static List<Person> CrewOf(JsonElement credits, string[] jobs) =>
        credits.ValueKind == JsonValueKind.Object ? CrewIn(credits, jobs) : [];

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
