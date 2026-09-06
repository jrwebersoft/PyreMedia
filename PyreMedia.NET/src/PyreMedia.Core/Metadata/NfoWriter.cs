using System.Xml.Linq;
using PyreMedia.Core.Media;
using PyreMedia.Core.Models;

namespace PyreMedia.Core.Metadata;

/// <summary>
/// Writes Kodi-format sidecar .nfo files.
///
/// Two jobs, deliberately separate:
///
/// <b>Create</b> a new .nfo for a file that has none. Only fields we actually
/// know are written - inventing empty ratings or placeholder artwork would make
/// a scraper's job harder, not easier.
///
/// <b>Update</b> the streamdetails inside an .nfo that already exists. This is
/// surgical: everything else in the file - artwork, ratings, tags, actors, hand
/// edits - is left byte-for-byte alone. A remux only invalidates the track list,
/// and Kodi trusts the .nfo over re-probing the file, so a stale block means the
/// library advertises tracks that are no longer there.
/// </summary>
public static class NfoWriter
{
    /// <summary>What happened, for honest reporting rather than silent success.</summary>
    public enum Outcome { Written, Updated, SkippedExisting, SkippedDisabled, Failed, NoChange }

    public sealed record Result(Outcome Outcome, string Path, string? Detail = null)
    {
        public bool Changed => Outcome is Outcome.Written or Outcome.Updated;
    }

    /// <summary>The .nfo Kodi expects beside a video: same name, .nfo extension.</summary>
    public static string PathFor(string videoPath) => Path.ChangeExtension(videoPath, ".nfo");

    // ---------------- Create ----------------

    public static Result WriteMovie(
        string videoPath, Movie movie, MediaInfo? info, PyreMediaSettings settings)
    {
        if (!settings.WriteNfoFiles)
            return new Result(Outcome.SkippedDisabled, PathFor(videoPath));

        var path = PathFor(videoPath);

        if (File.Exists(path) && settings.PreserveExistingNfo && !settings.MergeExistingNfo)
        {
            // An existing file may hold artwork and hand edits we can't
            // reproduce. Refresh only what a remux can invalidate.
            return settings.UpdateNfoAfterRemux && info is not null
                ? UpdateStreamDetails(path, info)
                : new Result(Outcome.SkippedExisting, path, "already exists");
        }

        var root = new XElement("movie",
            El("title", movie.Title),
            El("originaltitle", movie.Title),
            El("plot", movie.Overview),
            El("tagline", movie.Tagline),
            El("year", movie.Year),
            El("premiered", movie.ReleaseDate),
            El("mpaa", movie.Certification),
            El("runtime", movie.RuntimeMinutes?.ToString()));

        root.Add(Ratings(movie.Rating));
        root.Add(Many("genre", movie.Genres));
        root.Add(Many("country", movie.Countries));
        root.Add(Many("studio", movie.Studios));
        root.Add(Crew("director", movie.Directors));
        root.Add(Crew("credits", movie.Writers));
        root.Add(Actors(movie.Cast));

        if (!string.IsNullOrWhiteSpace(movie.TmdbId))
        {
            root.Add(new XElement("uniqueid",
                new XAttribute("type", "tmdb"),
                new XAttribute("default", "true"),
                movie.TmdbId));
        }

        if (!string.IsNullOrWhiteSpace(movie.ImdbId))
            root.Add(new XElement("uniqueid", new XAttribute("type", "imdb"), movie.ImdbId));

        if (info is not null) root.Add(BuildFileInfo(info));

        var one = new List<XElement> { root };
        Merge(path, one);

        return Save(path, one[0]);
    }

    public static Result WriteEpisode(
        string videoPath, TvShow show, Episode episode, MediaInfo? info, PyreMediaSettings settings)
    {
        if (!settings.WriteNfoFiles)
            return new Result(Outcome.SkippedDisabled, PathFor(videoPath));

        var path = PathFor(videoPath);

        if (File.Exists(path) && settings.PreserveExistingNfo && !settings.MergeExistingNfo)
        {
            return settings.UpdateNfoAfterRemux && info is not null
                ? UpdateStreamDetails(path, info)
                : new Result(Outcome.SkippedExisting, path, "already exists");
        }

        return Write(path, show, [episode], info);
    }

    /// <summary>
    /// The .nfo for a file covering more than one episode - an S01E01E02.
    /// <para>
    /// Kodi's format for these is one <c>episodedetails</c> block per episode,
    /// one after another in the same file. That is deliberately not a single XML
    /// document: it has several root elements. Writing just the first block, which
    /// is what happened before, left the second episode out of the library
    /// entirely.
    /// </para>
    /// </summary>
    public static Result WriteEpisodes(
        string videoPath, TvShow show, IReadOnlyList<Episode> episodes,
        MediaInfo? info, PyreMediaSettings settings)
    {
        if (!settings.WriteNfoFiles)
            return new Result(Outcome.SkippedDisabled, PathFor(videoPath));

        if (episodes.Count == 0)
            return new Result(Outcome.Failed, PathFor(videoPath), "no episodes to write");

        var path = PathFor(videoPath);

        if (File.Exists(path) && settings.PreserveExistingNfo && !settings.MergeExistingNfo)
        {
            return settings.UpdateNfoAfterRemux && info is not null
                ? UpdateStreamDetails(path, info)
                : new Result(Outcome.SkippedExisting, path, "already exists");
        }

        return Write(path, show, episodes, info);
    }

    /// <summary>
    /// The show's own <c>tvshow.nfo</c>, written in the show folder rather than
    /// beside any one video.
    ///
    /// This was simply missing. Every episode got its .nfo and the show got
    /// none, which is not a cosmetic gap: without it Kodi has nothing to attach
    /// the series artwork, plot, studio or rating to, and identifies the show by
    /// scraping the folder name - so a folder it reads differently becomes a
    /// second, half-empty series sitting beside the real one.
    /// <para>
    /// Its path is fixed. Kodi looks for exactly <c>tvshow.nfo</c> in the folder
    /// holding the season folders, so this takes the show folder rather than a
    /// video path like the others.
    /// </para>
    /// </summary>
    public static Result WriteShow(string showFolder, TvShow show, PyreMediaSettings settings)
    {
        var path = Path.Combine(showFolder, "tvshow.nfo");

        if (!settings.WriteNfoFiles) return new Result(Outcome.SkippedDisabled, path);

        if (!Directory.Exists(showFolder))
            return new Result(Outcome.Failed, path, "no show folder");

        // Unlike an episode file, this one carries artwork and hand edits that
        // nothing here can reproduce, and it has no stream details to refresh -
        // so when it is being preserved there is genuinely nothing to do.
        if (File.Exists(path) && settings.PreserveExistingNfo && !settings.MergeExistingNfo)
            return new Result(Outcome.SkippedExisting, path, "already exists");

        var root = new XElement("tvshow",
            El("title", show.Name),
            El("originaltitle", show.Name),
            El("showtitle", show.Name),
            El("plot", show.Overview),
            El("premiered", show.FirstAired),
            El("year", show.Year),
            El("studio", show.Network),
            El("status", show.Status),
            El("mpaa", show.Certification),
            El("runtime", show.RuntimeMinutes?.ToString()));

        root.Add(Ratings(show.Rating));
        root.Add(Many("genre", show.Genres));

        // The network is already written above as the first studio; the rest
        // are the companies behind it, and repeating the first would give Kodi
        // the same name twice.
        root.Add(Many("studio", show.Studios.Where(x =>
            !string.Equals(x, show.Network, StringComparison.OrdinalIgnoreCase))));

        root.Add(Crew("credits", show.Creators));
        root.Add(Actors(show.Cast));

        // Kodi shows this on the series page and uses it to decide whether a
        // series is complete. Specials are season 0 and are not counted.
        var seasons = show.Seasons.Where(s => !s.IsSpecials).ToList();

        if (seasons.Count > 0)
        {
            root.Add(El("season", seasons.Count.ToString()));
            root.Add(El("episode", seasons.Sum(s => s.Episodes.Count).ToString()));
        }

        if (!string.IsNullOrWhiteSpace(show.Id))
        {
            root.Add(new XElement("uniqueid",
                new XAttribute("type", "tvdb"),
                new XAttribute("default", "true"),
                show.Id));
        }

        var one = new List<XElement> { root };
        Merge(path, one);

        return Save(path, one[0]);
    }

    private static Result Write(string path, TvShow show, IReadOnlyList<Episode> episodes, MediaInfo? info)
    {
        var blocks = new List<XElement>();

        foreach (var episode in episodes)
        {
            var root = new XElement("episodedetails",
                El("title", episode.Name),
                El("showtitle", show.Name),
                El("season", episode.SeasonNumber.ToString()),
                El("episode", episode.Number.ToString()),
                El("plot", episode.Overview),
                El("aired", episode.FirstAired),
                El("productioncode", episode.ProductionCode),
                El("runtime", episode.RuntimeMinutes?.ToString()));

            root.Add(Ratings(episode.Rating));
            root.Add(Crew("director", episode.Directors));
            root.Add(Crew("credits", episode.Writers));

            // Guests only. The regular cast belongs to the series and Kodi
            // merges the two itself; writing it into all fifty episodes as well
            // would treble the size of a season's metadata to say nothing new.
            root.Add(Actors(episode.GuestStars));

            // The show id, not the episode's - Kodi matches episodes through their
            // series, and TheTVDB is what the legacy pipeline is keyed on.
            if (!string.IsNullOrWhiteSpace(show.Id))
                root.Add(new XElement("uniqueid", new XAttribute("type", "tvdb"), show.Id));

            // The stream details describe the file, and the file is the same one
            // for every episode in it, so each block carries them.
            if (info is not null) root.Add(BuildFileInfo(info));

            blocks.Add(root);
        }

        Merge(path, blocks);

        return blocks.Count == 1 ? Save(path, blocks[0]) : SaveBlocks(path, blocks);
    }

    /// <summary>
    /// Everything in an .nfo that belongs to the person watching, not to the
    /// provider. Fresh metadata knows none of it, so overwriting a file without
    /// carrying these across marks a watched series unwatched and loses part-way
    /// positions - the kind of damage that isn't noticed until much later.
    /// </summary>
    private static readonly string[] WatchState =
    [
        "playcount", "lastplayed", "watched", "userrating", "resume", "dateadded", "votes"
    ];

    /// <summary>
    /// Fold what's already on disk into what's about to be written.
    ///
    /// New information wins wherever both files have something to say - that is
    /// the point of rewriting. Anything the new data has no opinion about is kept
    /// rather than dropped: watch state always, and any element we don't produce
    /// ourselves, which is how a hand-added <c>tag</c> or a Kodi export's
    /// <c>set</c> survives.
    ///
    /// A file that can't be read is not an error. It means there is nothing to
    /// carry across, and the new .nfo is written as it would have been anyway.
    /// </summary>
    private static void Merge(string path, List<XElement> blocks)
    {
        if (blocks.Count == 0 || !File.Exists(path)) return;

        List<XElement> old;
        try { old = NfoFile.ReadBlocks(path); }
        catch { return; }

        if (old.Count == 0) return;

        for (var i = 0; i < blocks.Count; i++)
        {
            // Blocks line up by position - a multi-episode file writes its
            // episodes in the same order every time. If the old file had fewer,
            // the extra new ones have nothing to inherit.
            var previous = MatchingBlock(old, blocks[i], i);
            if (previous is null) continue;

            foreach (var e in previous.Elements())
            {
                var name = e.Name.LocalName;

                var isWatchState = WatchState.Contains(name, StringComparer.OrdinalIgnoreCase);
                var alreadyWritten = blocks[i].Element(e.Name) is not null;

                // Watch state replaces whatever we wrote; everything else only
                // fills a gap, so a stale plot can't outrank a fresh one.
                if (isWatchState)
                {
                    blocks[i].Elements(e.Name).Remove();
                    blocks[i].Add(new XElement(e));
                }
                else if (!alreadyWritten)
                {
                    blocks[i].Add(new XElement(e));
                }
            }
        }
    }

    /// <summary>
    /// The old block describing the same episode. Season and episode numbers are
    /// what identify it - position is only a fallback, for a file that never had
    /// them.
    /// </summary>
    private static XElement? MatchingBlock(List<XElement> old, XElement fresh, int index)
    {
        // Only ever from a block of the same kind. A film sitting where an
        // episode's .nfo used to be is not a continuation of it, and taking that
        // file's contents would give the movie a season, an episode number, a
        // show title and somebody else's play count.
        var same = old.Where(b => b.Name == fresh.Name).ToList();
        if (same.Count == 0) return null;

        var season = fresh.Element("season")?.Value;
        var number = fresh.Element("episode")?.Value;

        if (season is not null && number is not null)
        {
            var byNumber = same.FirstOrDefault(b =>
                b.Element("season")?.Value == season && b.Element("episode")?.Value == number);

            if (byNumber is not null) return byNumber;

            // The right kind of file but not this episode. Falling back to
            // position here would hand one episode another's history, so the
            // numbers are the only answer once both files carry them.
            if (same.Any(b => b.Element("episode") is not null)) return null;
        }

        return index < same.Count ? same[index] : null;
    }

    // ---------------- Update in place ----------------

    /// <summary>
    /// Replace only fileinfo/streamdetails, preserving everything else exactly.
    /// Called after a remux, where the tracks changed and nothing else did.
    /// </summary>
    public static Result UpdateStreamDetails(string nfoPath, MediaInfo info)
    {
        try
        {
            if (!File.Exists(nfoPath))
                return new Result(Outcome.Failed, nfoPath, "no .nfo to update");

            LegacyEncodings.Ensure();

            // A multi-episode .nfo has several root elements, which is valid to
            // Kodi and not a document to XDocument. Parse under a wrapper so both
            // shapes read the same way, and remember which it was.
            var raw = File.ReadAllText(nfoPath);
            var wrapped = XDocument.Parse("<msroot>" + StripDeclaration(raw) + "</msroot>",
                                          LoadOptions.PreserveWhitespace);

            var roots = wrapped.Root!.Elements().ToList();
            if (roots.Count == 0)
                return new Result(Outcome.Failed, nfoPath, "empty document");

            var changed = false;

            // Every block describes the same file, so every block's stream details
            // are refreshed.
            foreach (var root in roots)
            {
                var fresh = BuildFileInfo(info);
                var existing = root.Element("fileinfo");

                if (existing is not null)
                {
                    if (XNode.DeepEquals(Normalize(existing), Normalize(fresh))) continue;
                    existing.ReplaceWith(fresh);
                }
                else
                {
                    root.Add(fresh);
                }

                changed = true;
            }

            // Nothing to do if it already said the right thing - rewriting the
            // file would churn its timestamp for no reason.
            if (!changed) return new Result(Outcome.NoChange, nfoPath);

            if (roots.Count == 1)
            {
                SaveDocument(nfoPath, new XDocument(
                    new XDeclaration("1.0", "UTF-8", "yes"), roots[0]));

                return new Result(Outcome.Updated, nfoPath);
            }

            var saved = SaveBlocks(nfoPath, roots);
            return saved.Outcome == Outcome.Written
                ? new Result(Outcome.Updated, nfoPath)
                : saved;
        }
        catch (Exception ex)
        {
            return new Result(Outcome.Failed, nfoPath, ex.Message);
        }
    }

    /// <summary>
    /// Drop the XML declaration. It is only legal at the very start of a
    /// document, so it can't survive being wrapped in another element.
    /// </summary>
    private static string StripDeclaration(string xml)
    {
        var start = xml.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return xml;

        var end = xml.IndexOf("?>", start, StringComparison.Ordinal);
        return end < 0 ? xml : xml[..start] + xml[(end + 2)..];
    }

    /// <summary>Whitespace-insensitive copy, for comparing two blocks.</summary>
    private static XElement Normalize(XElement e) => XElement.Parse(e.ToString(SaveOptions.DisableFormatting));

    // ---------------- Shared ----------------

    /// <summary>
    /// The fileinfo/streamdetails block, in the shape and element order Kodi
    /// writes it. Values come from ffprobe, so they describe the file as it is
    /// now rather than as the scraper found it.
    /// </summary>
    public static XElement BuildFileInfo(MediaInfo info)
    {
        var details = new XElement("streamdetails");

        foreach (var v in info.Video)
        {
            var video = new XElement("video",
                El("codec", v.Codec),
                El("aspect", Aspect(v)),
                El("width", v.Width > 0 ? v.Width.ToString() : null),
                El("height", v.Height > 0 ? v.Height.ToString() : null),
                El("durationinseconds", info.DurationSeconds > 0
                    ? ((int)Math.Round(info.DurationSeconds)).ToString()
                    : null));

            // Kodi always emits stereomode, empty for 2D, so a reader can tell
            // "not 3D" from "not recorded".
            video.Add(new XElement("stereomode", KodiStereoMode(v.Stereo3D) ?? ""));

            var hdr = KodiHdrType(v);
            if (hdr is not null) video.Add(new XElement("hdrtype", hdr));

            details.Add(video);
        }

        foreach (var a in info.Audio)
        {
            details.Add(new XElement("audio",
                El("codec", a.Codec),
                El("language", a.Language),
                El("channels", a.Channels > 0 ? a.Channels.ToString() : null)));
        }

        foreach (var s in info.Subtitles)
            details.Add(new XElement("subtitle", El("language", s.Language)));

        return new XElement("fileinfo", details);
    }

    /// <summary>
    /// Kodi's hdrtype vocabulary. Dolby Vision wins when both are present: it's
    /// what a capable display will actually use.
    /// </summary>
    private static string? KodiHdrType(MediaStream v)
    {
        if (v.IsDolbyVision) return "dolbyvision";

        return v.HdrFormat switch
        {
            "HDR10" => "hdr10",
            "HDR10+" => "hdr10plus",
            "HLG" => "hlg",
            _ => null
        };
    }

    /// <summary>
    /// Kodi's stereomode vocabulary, which is not ffprobe's. Anything we can't
    /// map confidently is left empty rather than guessed - a wrong 3D mode makes
    /// a player show two squashed images side by side.
    /// </summary>
    private static string? KodiStereoMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;

        return mode.ToLowerInvariant() switch
        {
            "side by side" or "left_right" or "sbs" => "left_right",
            "right_left" => "right_left",
            "top bottom" or "top_bottom" or "tb" => "top_bottom",
            "bottom_top" => "bottom_top",
            "block_lr" => "block_lr",
            "block_rl" => "block_rl",
            "frame alternate" or "frame_alternate" => "row_interleaved_lr",
            _ => null
        };
    }

    private static string? Aspect(MediaStream v)
        => v.Width > 0 && v.Height > 0
            ? ((double)v.Width / v.Height).ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>An element, or nothing at all when there's no value to write.</summary>
    private static XElement? El(string name, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);

    /// <summary>
    /// How many of a cast list to write.
    ///
    /// TMDb's aggregate credits for a long-running series run to several
    /// hundred people, most of them one-line parts from a single episode.
    /// Kodi shows the top of the list and nobody scrolls a cast of four
    /// hundred, so the tail is weight in every .nfo for no reading anybody
    /// does. Billing order decides who stays, which is the order the credits
    /// themselves chose.
    /// </summary>
    private const int MaxCast = 30;

    private static IEnumerable<XElement> Many(string name, IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .Select(v => new XElement(name, v));

    /// <summary>Crew as Kodi writes them - the name alone, one element each.</summary>
    private static IEnumerable<XElement> Crew(string name, IEnumerable<Models.Person> people) =>
        Many(name, people.Select(p => p.Name));

    /// <summary>
    /// Kodi's actor block. Order is written where the source gave one, because
    /// Kodi sorts on it and a cast in the wrong order reads as a different film.
    /// </summary>
    private static IEnumerable<XElement> Actors(IEnumerable<Models.Person> cast)
    {
        var ordered = cast.Where(p => !string.IsNullOrWhiteSpace(p.Name))
                          .OrderBy(p => p.Order ?? int.MaxValue)
                          .Take(MaxCast)
                          .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];

            yield return new XElement("actor",
                new XElement("name", p.Name),
                El("role", p.Role),
                new XElement("order", (p.Order ?? i).ToString()),
                El("thumb", p.ThumbUrl));
        }
    }

    /// <summary>
    /// The ratings block in the form Kodi has used since v17.
    ///
    /// Named and attributed rather than a bare number, because a library can
    /// hold scores from several sources and a value with no source attached
    /// cannot be updated later without guessing whose it was.
    /// </summary>
    private static XElement? Ratings(Models.Rating? rating)
    {
        if (rating is null) return null;

        var one = new XElement("rating",
            new XAttribute("name", rating.Source),
            new XAttribute("max", ((int)rating.Max).ToString()),
            new XAttribute("default", "true"),
            new XElement("value", rating.Value.ToString("0.0",
                System.Globalization.CultureInfo.InvariantCulture)));

        if (rating.Votes is { } votes and > 0)
            one.Add(new XElement("votes", votes.ToString()));

        return new XElement("ratings", one);
    }

    /// <summary>
    /// Several root elements in one file, which is what Kodi wants for a
    /// multi-episode .nfo and what no XML writer will produce on its own. Written
    /// through the same temp-and-swap as everything else.
    /// </summary>
    private static Result SaveBlocks(string path, List<XElement> blocks)
    {
        var temp = path + ".tmp";

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");

            foreach (var b in blocks)
                sb.AppendLine(b.ToString(SaveOptions.None));

            File.WriteAllText(temp, sb.ToString(), new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);

            return new Result(Outcome.Written, path);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return new Result(Outcome.Failed, path, ex.Message);
        }
    }

    private static Result Save(string path, XElement root)
    {
        try
        {
            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
            SaveDocument(path, doc);
            return new Result(Outcome.Written, path);
        }
        catch (Exception ex)
        {
            return new Result(Outcome.Failed, path, ex.Message);
        }
    }

    /// <summary>
    /// Write via a temp file and swap. A half-written .nfo is worse than none:
    /// Kodi would fail to parse it and fall back to the filename, and the
    /// original would already be gone.
    /// </summary>
    private static void SaveDocument(string path, XDocument doc)
    {
        var temp = path + ".tmp";

        var settings = new System.Xml.XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            Encoding = new System.Text.UTF8Encoding(false),
            OmitXmlDeclaration = false
        };

        try
        {
            using (var writer = System.Xml.XmlWriter.Create(temp, settings))
                doc.Save(writer);

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A read-only .nfo fails at the swap, which would otherwise strand a
            // .nfo.tmp beside the video for good - the caller only sees Failed and
            // has no idea there is anything to clean up.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }
}
