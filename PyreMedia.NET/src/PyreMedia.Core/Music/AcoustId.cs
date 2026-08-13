using System.Net.Http;
using System.Text.Json;

namespace PyreMedia.Core.Music;

/// <summary>What AcoustID thinks a file is.</summary>
/// <param name="Score">
/// How sure the service is, 0 to 1. Its own confidence in the fingerprint
/// match, not ours - a high score on a wrong recording is possible when two
/// pressings are genuinely identical audio.
/// </param>
public sealed record AcoustIdMatch(
    double Score,
    string? Title,
    string? Artist,
    string? Album,
    string? RecordingId,
    string? ReleaseGroupId)
{
    public override string ToString() =>
        $"{Artist ?? "?"} - {Title ?? "?"}" + (Album is null ? "" : $" [{Album}]") + $" ({Score:P0})";
}

/// <summary>
/// Asks AcoustID what an untagged file is.
///
/// The only thing in this program that talks to the internet about the user's
/// own library, so it is worth being exact about what leaves the machine: a
/// chromaprint fingerprint and a duration. Not the audio, not the filename, not
/// the path, and nothing that identifies the machine beyond the API key the
/// user chose to enter. A fingerprint is a one-way summary - roughly one 32-bit
/// number per eighth of a second - and no audio can be reconstructed from it.
///
/// Off unless a key is entered, and worth it for one specific case: files with
/// no usable tags at all, where every local rule has already failed. Measured
/// on this library that is 55 of 17,168, so this is a last resort by design
/// rather than a step in the normal pipeline.
/// </summary>
public sealed class AcoustId(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? Shared;

    private static readonly HttpClient Shared = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Where to get a key. Shown beside the box in settings.</summary>
    public const string SignUpUrl = "https://acoustid.org/new-application";

    /// <summary>
    /// The service asks for no more than three requests a second. Honoured here
    /// rather than left to the caller, because exceeding it gets a key blocked
    /// and the caller has no reason to know the number.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _last = DateTime.MinValue;

    /// <summary>
    /// Identify one file. Returns matches best-first, or an empty list when
    /// nothing is known - which is not a failure and is common for anything
    /// homemade, live, or off a small label.
    /// </summary>
    public async Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
        string path, string apiKey, double? seconds = null, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return [];

        var fingerprint = await Task.Run(() => Fingerprint.Base64(path), cancel);
        if (fingerprint.Length == 0) return [];

        // The service matches on fingerprint and duration together, and rejects
        // a request without a duration outright.
        var duration = seconds is > 0
            ? seconds.Value
            : (await MusicDurations.MeasureAsync(path, cancel: cancel)).Seconds;

        if (duration <= 0) return [];

        return await LookupAsync(fingerprint, duration, apiKey, cancel);
    }

    /// <summary>The same, when the fingerprint has already been taken.</summary>
    public async Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
        string fingerprint, double seconds, string apiKey, CancellationToken cancel = default)
    {
        await Gate.WaitAsync(cancel);

        try
        {
            var since = DateTime.UtcNow - _last;
            if (since < TimeSpan.FromMilliseconds(350))
                await Task.Delay(TimeSpan.FromMilliseconds(350) - since, cancel);

            _last = DateTime.UtcNow;

            var url = "https://api.acoustid.org/v2/lookup"
                    + $"?client={Uri.EscapeDataString(apiKey)}"
                    + "&meta=recordings+releasegroups+compress"
                    + $"&duration={(int)Math.Round(seconds)}"
                    + $"&fingerprint={Uri.EscapeDataString(fingerprint)}";

            using var response = await _http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode) return [];

            return Parse(await response.Content.ReadAsStringAsync(cancel));
        }
        catch
        {
            // No network, a blocked key, a service outage. None of those are
            // facts about the music, so they produce no opinion rather than a
            // wrong one.
            return [];
        }
        finally { Gate.Release(); }
    }

    internal static IReadOnlyList<AcoustIdMatch> Parse(string json)
    {
        var matches = new List<AcoustIdMatch>();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("status", out var status)
                || status.GetString() != "ok") return [];

            if (!root.TryGetProperty("results", out var results)) return [];

            foreach (var result in results.EnumerateArray())
            {
                var score = result.TryGetProperty("score", out var s) ? s.GetDouble() : 0;

                // A result with no recordings is a fingerprint the service has
                // seen but nobody has ever named. Real, and no use here.
                if (!result.TryGetProperty("recordings", out var recordings))
                {
                    matches.Add(new AcoustIdMatch(score, null, null, null,
                        Text(result, "id"), null));
                    continue;
                }

                foreach (var recording in recordings.EnumerateArray())
                {
                    string? artist = null;

                    if (recording.TryGetProperty("artists", out var artists))
                        artist = string.Join(", ", artists.EnumerateArray()
                            .Select(a => Text(a, "name"))
                            .Where(n => !string.IsNullOrWhiteSpace(n)));

                    string? album = null;
                    string? releaseGroup = null;

                    if (recording.TryGetProperty("releasegroups", out var groups)
                        && groups.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first)
                    {
                        album = Text(first, "title");
                        releaseGroup = Text(first, "id");
                    }

                    matches.Add(new AcoustIdMatch(
                        score,
                        Text(recording, "title"),
                        string.IsNullOrWhiteSpace(artist) ? null : artist,
                        album,
                        Text(recording, "id"),
                        releaseGroup));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [.. matches.OrderByDescending(m => m.Score)];
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
