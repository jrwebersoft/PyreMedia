using System.Net.Http;
using System.Text.Json;

namespace PyreMedia.Core.Music;

/// <summary>One track of a release, as MusicBrainz lists it.</summary>
public sealed record ReleaseTrack(
    int Number, int Disc, string Title, string Artist, double Seconds, string? RecordingId);

/// <summary>A whole release and its track listing.</summary>
public sealed record ReleaseListing(
    string Id, string Title, string Artist, string? Date, IReadOnlyList<ReleaseTrack> Tracks)
{
    public string? Year => Date is { Length: >= 4 } d && d[..4].All(char.IsDigit) ? d[..4] : null;

    public override string ToString() =>
        $"{Artist} - {Title}" + (Year is null ? "" : $" ({Year})") + $", {Tracks.Count} tracks";
}

/// <summary>What a recording actually is, according to MusicBrainz.</summary>
public sealed record Recording(
    string Id,
    string Title,
    string Artist,
    string? ArtistId,
    double? Seconds,
    IReadOnlyList<ReleaseAppearance> Releases)
{
    public override string ToString() => $"{Artist} - {Title}";
}

/// <summary>One release this recording appears on, and where in it.</summary>
public sealed record ReleaseAppearance(
    string Id,
    string Title,
    string? Date,
    int? TrackNumber,
    int? DiscNumber,
    int? TrackCount,
    string? Artist)
{
    /// <summary>Four digits, where the release names a date at all.</summary>
    public string? Year => Date is { Length: >= 4 } d && d[..4].All(char.IsDigit) ? d[..4] : null;
}

/// <summary>
/// Asks MusicBrainz what a recording is, once the audio has said which one.
///
/// This is the half that was missing, and the reason it matters is worth
/// stating plainly. Everything else in this codebase reasons from the tags a
/// file already carries: filenames, folder names, what the neighbours say. All
/// of that is circular. If a shop tagged a purchase wrongly, the wrong value is
/// what the filename says, what the folder says and what the majority of the
/// album says, and no amount of comparing those to each other finds the error -
/// thirteen files spelling a band wrongly outvote the one that has it right.
///
/// The audio cannot lie. Chromaprint says which recording a file holds,
/// AcoustID turns that into a MusicBrainz id, and this turns the id into what
/// the recording is actually called. That chain is the only path in the program
/// from a file to a fact about it that did not come out of the file itself.
///
/// No key needed, unlike AcoustID - MusicBrainz asks only for a User-Agent that
/// identifies the program and a strict one request per second. Both are
/// honoured here rather than left to callers, because exceeding the rate gets
/// an address blocked and a caller has no reason to know the limit.
/// </summary>
public sealed class MusicBrainz(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? Shared;

    private static readonly HttpClient Shared = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Required. MusicBrainz rejects requests from anything that will not
        // say what it is, and a generic agent gets the whole address blocked
        // when somebody else abuses it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PyreMedia/2.0 ( https://github.com/jrwebersoft/PyreMedia )");

        return client;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _last = DateTime.MinValue;

    /// <summary>
    /// What a recording is, by its MusicBrainz id - the id AcoustID returns.
    /// Null when it is unknown or the service cannot be reached, which is not
    /// the same as the recording being wrong.
    /// </summary>
    public async Task<Recording?> RecordingAsync(string id, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var json = await GetAsync(
            $"recording/{Uri.EscapeDataString(id)}?inc=artist-credits+releases+media&fmt=json", cancel);

        return json is null ? null : ParseRecording(json);
    }

    /// <summary>
    /// A whole release by its MusicBrainz id, with its track listing.
    ///
    /// Worth having separately from the recording lookup because it is the id
    /// libraries actually carry. Scraped .nfo files hold release ids in their
    /// thousands and recording ids almost never - measured here, 2,962 against
    /// nil - so this is the lookup that can say anything authoritative about a
    /// library that has never been fingerprinted.
    /// </summary>
    public async Task<ReleaseListing?> ReleaseAsync(string id, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var json = await GetAsync(
            $"release/{Uri.EscapeDataString(id)}?inc=artist-credits+recordings&fmt=json", cancel);

        return json is null ? null : ParseRelease(json);
    }

    internal static ReleaseListing? ParseRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var id = Text(root, "id");
            var title = Text(root, "title");

            if (id is null || title is null) return null;

            var (artist, _) = Credit(root);
            var tracks = new List<ReleaseTrack>();

            if (root.TryGetProperty("media", out var media))
            {
                var disc = 0;

                foreach (var medium in media.EnumerateArray())
                {
                    disc = medium.TryGetProperty("position", out var p)
                        && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : disc + 1;

                    if (!medium.TryGetProperty("tracks", out var list)) continue;

                    foreach (var track in list.EnumerateArray())
                    {
                        var number = track.TryGetProperty("position", out var pos)
                            && pos.ValueKind == JsonValueKind.Number ? pos.GetInt32() : 0;

                        var length = track.TryGetProperty("length", out var ms)
                            && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() / 1000.0 : 0;

                        var (trackArtist, _) = Credit(track);

                        // The recording id hangs off the track, and it is the
                        // one thing here that identifies audio rather than a
                        // position on a record.
                        var recordingId = track.TryGetProperty("recording", out var rec)
                            ? Text(rec, "id") : null;

                        tracks.Add(new ReleaseTrack(number, disc,
                            Text(track, "title") ?? "",
                            string.IsNullOrWhiteSpace(trackArtist) ? artist : trackArtist,
                            length, recordingId));
                    }
                }
            }

            return new ReleaseListing(id, title, artist, Text(root, "date"), tracks);
        }
        catch (JsonException) { return null; }
    }

    private async Task<string?> GetAsync(string query, CancellationToken cancel)
    {
        await Gate.WaitAsync(cancel);

        try
        {
            // One request a second, measured from the last one rather than
            // slept blindly, so a slow reply does not cost the wait twice.
            var since = DateTime.UtcNow - _last;
            if (since < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - since, cancel);

            _last = DateTime.UtcNow;

            using var response = await _http.GetAsync(
                "https://musicbrainz.org/ws/2/" + query, cancel);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancel)
                : null;
        }
        catch { return null; }
        finally { Gate.Release(); }
    }

    internal static Recording? ParseRecording(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var id = Text(root, "id");
            var title = Text(root, "title");

            if (id is null || title is null) return null;

            var (artist, artistId) = Credit(root);
            var length = root.TryGetProperty("length", out var ms) && ms.ValueKind == JsonValueKind.Number
                ? ms.GetDouble() / 1000.0
                : (double?)null;

            var releases = new List<ReleaseAppearance>();

            if (root.TryGetProperty("releases", out var list))
                foreach (var release in list.EnumerateArray())
                    if (Appearance(release) is { } appearance) releases.Add(appearance);

            return new Recording(id, title, artist, artistId, length, releases);
        }
        catch (JsonException) { return null; }
    }

    private static ReleaseAppearance? Appearance(JsonElement release)
    {
        var id = Text(release, "id");
        var title = Text(release, "title");

        if (id is null || title is null) return null;

        int? track = null, disc = null, count = null;

        // The track number is not on the recording - it is on the medium of the
        // release, because one recording sits at a different position on every
        // record it appears on. That is the whole reason a soundtrack and an
        // album disagree about a number that is otherwise "the" track number.
        if (release.TryGetProperty("media", out var media))
        {
            var index = 1;

            foreach (var medium in media.EnumerateArray())
            {
                if (medium.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number)
                    index = p.GetInt32();

                if (medium.TryGetProperty("track-count", out var tc) && tc.ValueKind == JsonValueKind.Number)
                    count = tc.GetInt32();

                if (medium.TryGetProperty("track", out var tracks))
                    foreach (var t in tracks.EnumerateArray())
                        if (t.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number)
                        {
                            track = pos.GetInt32();
                            disc = index;
                        }
            }
        }

        var (artist, _) = Credit(release);

        return new ReleaseAppearance(id, title, Text(release, "date"), track, disc, count,
            string.IsNullOrWhiteSpace(artist) ? null : artist);
    }

    /// <summary>
    /// The artist credit as it should be written, joining phrases included.
    ///
    /// Built from the credit rather than from the artist's name, because the
    /// two differ on purpose: a track credited to "Jay-Z &amp; Linkin Park"
    /// carries two artists and a joiner, and reducing it to the first artist
    /// throws away half of who made it.
    /// </summary>
    private static (string Artist, string? Id) Credit(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credits))
            return ("", null);

        var text = "";
        string? first = null;

        foreach (var credit in credits.EnumerateArray())
        {
            var name = Text(credit, "name")
                ?? (credit.TryGetProperty("artist", out var artist) ? Text(artist, "name") : null);

            text += name ?? "";
            text += Text(credit, "joinphrase") ?? "";

            if (first is null && credit.TryGetProperty("artist", out var a)) first = Text(a, "id");
        }

        return (text.Trim(), first);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
