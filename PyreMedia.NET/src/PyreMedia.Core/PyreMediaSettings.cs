using System.Text.Json;
using System.Text.Json.Serialization;

namespace PyreMedia.Core;

public enum EpisodeSource
{
    /// <summary>
    /// TheTVDB's legacy XML API only. Still serving current data - it carries
    /// recent seasons fine - but tends to list fewer episodes per season than
    /// TMDb, and its search endpoint is retired (searching always uses TMDb).
    /// </summary>
    TheTvdbLegacy,

    /// <summary>TMDb v3 only. Usually the richer episode list, but can miss specials.</summary>
    Tmdb,

    /// <summary>
    /// Both, merged. TMDb supplies the spine; anything TheTVDB has that TMDb
    /// doesn't - typically season-0 specials - is filled in rather than lost.
    /// Measured on a sample library this was strictly better than either alone.
    /// </summary>
    Merged,

    /// <summary>
    /// TVmaze only. Keyless and free. It contributed nothing over TMDb+TheTVDB
    /// in testing, so it isn't in the merge - but it numbers some shows
    /// differently again, which is worth having when the other two disagree.
    /// Appended last: the values above are already persisted in settings.json.
    /// </summary>
    TvMaze
}

/// <summary>
/// User settings. Defaults are seeded from the long-standing MediaScout 3.0.4
/// configuration so behaviour matches what the user already expects.
/// Persisted as JSON under %AppData%\PyreMedia\settings.json.
/// </summary>
public sealed class PyreMediaSettings
{
    // ---- Folders (intentionally empty; chosen on first run) ----
    public List<string> TvFolders { get; set; } = [];
    public List<string> MovieFolders { get; set; } = [];
    public List<string> MusicFolders { get; set; } = [];

    /// <summary>Where comics and ebooks are looked for. Separate, because a
    /// .cbz and an .epub are read by different programs and shelved
    /// differently - and because somebody may keep only one of the two.</summary>
    public List<string> ComicFolders { get; set; } = [];

    public List<string> BookFolders { get; set; } = [];

    // ---- Where finished media goes ----
    //
    // A staging folder is where things arrive and a library is where they live,
    // and they are rarely the same disk. One destination per kind because they
    // are not interchangeable: Kodi scans a film library and a television
    // library separately, and a book filed among the albums is a book nobody
    // will find again.
    //
    // Blank means not chosen yet rather than "the same place". Moving finished
    // files into an unset destination would put them somewhere nobody picked,
    // so the button asks instead.

    public string MovieDestination { get; set; } = "";
    public string TvDestination { get; set; } = "";
    public string MusicDestination { get; set; } = "";
    public string AudiobookDestination { get; set; } = "";
    public string ComicDestination { get; set; } = "";
    public string EbookDestination { get; set; } = "";

    /// <summary>The destination for a kind of library, or "" if none is set.</summary>
    public string DestinationFor(LibraryKind kind) => kind switch
    {
        LibraryKind.Movie => MovieDestination,
        LibraryKind.Tv => TvDestination,
        LibraryKind.Music => MusicDestination,
        LibraryKind.Audiobook => AudiobookDestination,
        LibraryKind.Comic => ComicDestination,
        LibraryKind.Ebook => EbookDestination,
        _ => ""
    };

    // ---- Music ----

    /// <summary>
    /// Where a track goes, as a pattern. See <c>NamingFormat</c> for the fields
    /// and for what the square brackets do.
    /// </summary>
    public string MusicFileFormat { get; set; } = "{albumartist}/{album}[ ({year})]/[{disc}-]{track:00} {title}";

    /// <summary>
    /// Where an audiobook goes. Separate from the music pattern because a book
    /// has no album and no track number worth the name - filing one by them
    /// gives a folder called "Unknown Album" holding fifty-seven files called
    /// "Track 04".
    /// </summary>
    public string AudiobookFormat { get; set; } = "{author}/{book}[ ({year})]/{chapter:000} {chaptertitle}";

    // ---- Comics and ebooks ----

    /// <summary>
    /// Where a comic goes. See <c>BookNaming</c> for the fields and for what
    /// the square brackets do.
    ///
    /// {publisher} is available here and deliberately not in the ebook pattern:
    /// comics are collected by imprint and novels are not.
    /// </summary>
    public string ComicFileFormat { get; set; } = Books.BookNaming.ComicDefault;

    /// <summary>Where an ebook goes.</summary>
    public string BookFileFormat { get; set; } = Books.BookNaming.BookDefault;

    /// <summary>
    /// Open comics and ebooks and ask what they are, rather than guessing from
    /// their filenames.
    ///
    /// On by default and cheap - both are zips with an XML file inside. An EPUB
    /// knows its own title, author and publisher; a comic's ComicInfo.xml knows
    /// its series, issue, publisher and usually the exact issue's id at the
    /// source it was scraped from. Measured over a real library of 29,083 .cbz
    /// files, a sample of 400 found 396 carrying that metadata. The filename is
    /// the fallback, not the source.
    ///
    /// Turning it off is for a library on a share slow enough that opening every
    /// archive is the expensive part of a scan.
    /// </summary>
    public bool ReadBookMetadata { get; set; } = true;

    // ---- Comic sources ----
    //
    // Asked in this order, and the order is by what a question costs rather than
    // by which database is best. The GCD dump is a local file: free, unlimited,
    // instant. Metron allows five thousand a day. Comic Vine allows two hundred
    // an hour. Spending the scarce one first would be the wrong way round.

    /// <summary>
    /// The Grand Comics Database dump, downloaded from comics.org.
    ///
    /// Not bundled and could not be - it is six gigabytes and CC BY-SA 4.0,
    /// which carries attribution and share-alike obligations. Fetched from its
    /// own authors, exactly as ffmpeg is.
    /// </summary>
    public string GcdDatabasePath { get; set; } = "";

    /// <summary>metron.cloud account. Basic auth, so this is a real password.</summary>
    public string MetronUser { get; set; } = "";

    public string MetronPassword { get; set; } = "";

    /// <summary>
    /// Comic Vine key. Free, and non-commercial only - it is revoked for
    /// commercial use, which suits this program and is worth knowing anyway.
    /// </summary>
    public string ComicVineApiKey { get; set; } = "";

    /// <summary>
    /// Compare the audio itself when tags and durations cannot separate a copy
    /// from a different version. Costs about four tenths of a second per file
    /// and only runs on files whose track numbers collide, but it is the
    /// difference between 95% right and right.
    /// </summary>
    public bool UseFingerprinting { get; set; } = true;

    /// <summary>
    /// Measure loudness and write ReplayGain tags, so players even out a
    /// library ripped over thirty years at wildly different levels.
    ///
    /// Off by default under the standing rule that anything which could degrade
    /// a file is opt-in - though this one cannot. ReplayGain is a pair of
    /// numbers in the tags telling the player how much to turn a track up or
    /// down; the audio is never re-encoded and deleting the tags undoes it
    /// completely. What it does cost is time: every file has to be decoded once
    /// to measure it.
    /// </summary>
    public bool WriteReplayGain { get; set; } = false;

    /// <summary>
    /// Write corrected tags into the files as well as moving them. Off by
    /// default: moving a file is undone by moving it back, and rewriting one
    /// is a bigger promise.
    /// </summary>
    public bool WriteMusicTags { get; set; } = false;

    // ---- Movies ----
    public bool RenameMovieFiles { get; set; } = true;

    // ---- 3D ----

    /// <summary>
    /// Carry a "(3D)" / "(3D-SBS)" prefix through renaming. Essential for
    /// half-SBS, where the filename is the only record that a file is 3D.
    /// </summary>
    public bool Keep3DTag { get; set; } = true;

    /// <summary>Move 3D files into a subfolder so they don't clutter the main library.</summary>
    public bool Move3DToExtras { get; set; } = false;

    public string ExtrasFolderName { get; set; } = "Extras";

    /// <summary>Give each movie its own folder. Kodi's preferred layout.</summary>
    public bool MovieFolderPerMovie { get; set; } = true;

    /// <summary>Placeholders: {title}, {year}.</summary>
    public string MovieFileFormat { get; set; } = "{title} ({year})";

    public string MovieFolderFormat { get; set; } = "{title} ({year})";

    // ---- Naming ----
    /// <summary>
    /// Placeholders: {title}, {season}, {episode}, {name} - the last being the
    /// episode's own title. Season and episode arrive already zero-padded.
    /// The old numeric form still works for anything already saved.
    /// </summary>
    public string TvFileFormat { get; set; } = "{title} {season}x{episode} {name}";

    public int SeasonNumZeroPadding { get; set; } = 2;
    public int EpisodeNumZeroPadding { get; set; } = 2;
    public string SeasonFolderName { get; set; } = "Season";
    public string SpecialsFolderName { get; set; } = "Specials";
    public char FilenameReplaceChar { get; set; } = '-';

    /// <summary>
    /// Rename the containing folder to the canonical title. Only applies when
    /// <see cref="MoveTvFiles"/> is on - in flat mode nothing structural changes.
    /// </summary>
    public bool RenameShowFolder { get; set; } = true;

    /// <summary>Placeholders: {title}, {year}. "Title (Year)" is Kodi's recommended form.</summary>
    public string ShowFolderFormat { get; set; } = "{title} ({year})";

    // ---- Behaviour ----
    public bool RenameTvFiles { get; set; } = true;

    /// <summary>
    /// Move episodes into Season folders, and allow the containing folder to be
    /// renamed to the canonical title.
    ///
    /// On by default. It was off, on the reasoning that renaming inside a folder
    /// and rearranging a library are different sizes of promise - but the result
    /// was a scan that produced perfect filenames and no Season folder, which
    /// reads as a fault rather than as a choice, and was reported as one.
    /// <para>
    /// Season folders are also what Kodi, Plex and Jellyfin expect, so the
    /// default that needed explaining was the one that did less. Nothing moves
    /// without the plan being shown and Apply pressed, and it all reverts from
    /// History, so the cost of the livelier default is a plan to look at rather
    /// than a surprise.
    /// </para>
    /// </summary>
    public bool MoveTvFiles { get; set; } = true;

    /// <summary>
    /// Scan the video folders as soon as the window opens.
    ///
    /// On by default. Opening on an empty list and waiting to be told to scan is
    /// a step with no decision in it: the folders are already configured and the
    /// answer is always yes. Scanning only reads - nothing is proposed until a
    /// match is chosen, and nothing happens until Apply.
    /// </summary>
    public bool ScanOnLaunch { get; set; } = true;

    public bool OverwriteFiles { get; set; } = true;

    /// <summary>
    /// Show a series split across several folders as one entry.
    ///
    /// A season per folder is how most TV arrives, and matching the same show
    /// eight times is pure repetition. Only done where there is no doubt: a year
    /// that disagrees, or a season number appearing twice, leaves the folders
    /// exactly as they were - "Dexter" and "Dexter: New Blood" are different
    /// series, and so are the two Battlestar Galacticas.
    /// </summary>
    public bool CombineSplitSeasons { get; set; } = true;

    /// <summary>Never auto-pick a search result; the user chooses.</summary>
    public bool AutoSelectMatch { get; set; } = false;

    // ---- File types ----
    public string AllowedFileTypes { get; set; } =
        ".avi;.mkv;.mp4;.mpg;.mpeg;.m4v;.ogm;.wmv;.divx;.dvr-ms";

    public string AllowedSubtitles { get; set; } = ".sub;.idx;.srt";

    /// <summary>
    /// Whether a disc image is something to organise.
    ///
    /// On: a folder of .iso files is a shelf of films with nothing tidying
    /// them, and naming and filing one needs nothing more than reading its
    /// name. Off, they are invisible to the scan exactly as they were.
    /// </summary>
    public bool OrganiseDiscImages { get; set; } = true;

    /// <summary>
    /// Disc images: named and filed, never opened.
    ///
    /// Kept apart from the video list rather than added to it, because
    /// everything downstream of that list assumes it can read the file.
    /// Remuxing an .iso is not a thing that can happen - mkvmerge cannot write
    /// one and would not be asked to - and probing one for its tracks reads a
    /// filesystem, not a stream. So these are scanned, matched, renamed and
    /// moved like any film, and left out of everything that opens a file.
    /// </summary>
    public string DiscImageTypes { get; set; } = ".iso;.img;.mdf;.nrg";

    // ---- Cleanup ----
    // On by default, and the word that carries the weight is "offer". Nothing
    // here deletes anything: it adds a row to the preview, ticked, which can be
    // cleared like any other, and deletions go to the Recycle Bin where the
    // drive has one.
    //
    // They were off, on the reasoning that deletion is the one thing here that
    // renaming back cannot undo. True, and it argued for the confirmation rather
    // than for the default - off meant the release adverts and sample clips that
    // come with every download simply stayed, and the setting that would have
    // dealt with them was one nobody knew to look for.
    //
    // Two things are never offered whatever these say. A .nfo holding real Kodi
    // metadata is checked by content, not extension, because a scraped sidecar
    // and a scene advert share a suffix. And a download marker beside a file
    // still arriving is a file in use rather than a leftover.

    /// <summary>
    /// Move the selection to the next unfinished item after a successful Apply.
    ///
    /// Off. It used to be on and was not a setting at all - a field on the
    /// window that reset to on every launch - so the item you had just worked
    /// on left the screen before you could see what happened to it. The .nfo
    /// and the artwork are written straight after the rename, and the row that
    /// reports them is the one it had already moved off.
    /// </summary>
    public bool AutoAdvanceAfterApply { get; set; }

    /// <summary>Offer to delete sample clips (name contains "sample", or tiny beside the feature).</summary>
    public bool DeleteSamples { get; set; } = true;

    /// <summary>Offer to delete leftover scene files - .txt, .sfv, .url and friends.</summary>
    public bool DeleteJunkFiles { get; set; } = true;

    public string JunkExtensions { get; set; } =
        ".nfo;.txt;.url;.sfv;.md5;.par2;.exe;.bat;.lnk;.diz;.website";

    /// <summary>Delete files sent to the Recycle Bin rather than removed outright.</summary>
    public bool DeleteToRecycleBin { get; set; } = true;

    // ---- Remux (ffmpeg) ----
    // Rewriting media files is a bigger commitment than renaming them, so this is
    // a separate, explicitly-confirmed action and off unless asked for.

    /// <summary>
    /// Your language, as an ISO 639-2 code. Seeds the keep rules and decides
    /// what counts as "foreign" when flagging files in the library list.
    /// </summary>
    public string PreferredLanguage { get; set; } = "eng";

    /// <summary>
    /// After a scan, read each item's tracks: how many audio and subtitle
    /// streams it carries, what languages they are in, whether any of them is
    /// foreign, and whether the file is 3D without saying so in its name.
    ///
    /// On by default. It was off and named for one of those four things, which
    /// is how the track counts and languages on the cards came to be switched
    /// off by a setting called "flag foreign audio" - a file with a Hindi track
    /// and two English subtitle tracks showed nothing at all, and nothing said
    /// why.
    ///
    /// It costs a probe per item, not per file - the main file stands for the
    /// folder. It runs after the scan is already usable, fills rows in as
    /// answers arrive, and is called off the moment another scan starts.
    /// <para>
    /// The stored name is kept as it was so that turning it off stays turned
    /// off across this rename. What it is called in the file is not worth
    /// silently discarding somebody's choice over.
    /// </para>
    /// </summary>
    [JsonPropertyName("FlagForeignAudio")]
    public bool ReadTrackDetails { get; set; } = true;

    /// <summary>Comma-separated ISO 639-2 codes. Empty keeps everything.</summary>
    public string KeepAudioLanguages { get; set; } = "eng";

    public string KeepSubtitleLanguages { get; set; } = "eng";

    /// <summary>
    /// Keep tracks tagged "und", where nothing else in the file would do.
    ///
    /// Many rips do not tag language at all, and dropping those would throw
    /// away the only audio. That is the whole of the reason - so it applies
    /// only while it is true. A file that already carries a track in a
    /// language you asked for has given you what you wanted, and its untagged
    /// track is a second copy, a commentary or a stray rather than the last
    /// thing standing between you and a silent film.
    ///
    /// Judged per file and per kind: a file can hold English audio and no
    /// English subtitles, and the untagged subtitle is still worth rescuing
    /// there.
    /// </summary>
    public bool KeepUndeterminedLanguage { get; set; } = true;

    /// <summary>
    /// Keep forced subtitles regardless of language - they carry translated
    /// on-screen text and dropping them silently breaks playback.
    /// </summary>
    public bool KeepForcedSubtitles { get; set; } = true;

    /// <summary>
    /// Keep the pre-remux file instead of recycling it. Safer than the Recycle
    /// Bin: it survives emptying, isn't subject to a size cap, and stays next to
    /// the media it belongs to.
    /// </summary>
    public bool ArchiveOriginals { get; set; } = true;

    /// <summary>
    /// Absolute path to keep pre-remux originals in. Empty means "beside the
    /// file", which is instant because it stays on the same volume - a root on
    /// another drive has to copy every byte of a 20 GB remux. Chosen on first run.
    /// </summary>
    public string ArchiveRootPath { get; set; } = "";

    /// <summary>
    /// Subfolder name used when no <see cref="ArchiveRootPath"/> is set, created
    /// beside the file.
    /// </summary>
    public string ArchiveFolderName { get; set; } = "_originals";

    // ---- Artwork ----

    /// <summary>
    /// Download poster and fanart beside the media, the way Kodi looks for them.
    ///
    /// Off by default. It reaches the network and writes files that are nothing
    /// to do with renaming, and a library scraped by Kodi already has its own -
    /// this is for one that isn't, or for files that travel to a player which
    /// doesn't scrape at all.
    /// </summary>
    public bool DownloadArtwork { get; set; } = false;

    /// <summary>
    /// Leave artwork that is already there. The same reasoning as .nfo files:
    /// what's on disk may be chosen or hand-made, and replacing it silently is
    /// the one thing that can't be undone from History.
    /// </summary>
    public bool PreserveExistingArtwork { get; set; } = true;

    /// <summary>
    /// Which image was saved where, keyed by the artwork file's path.
    ///
    /// Only so the picker can show what is already chosen: a .jpg on disk says
    /// nothing about which of a hundred posters it was, and re-opening the
    /// picker to a wall with nothing marked is a guessing game.
    /// </summary>
    public Dictionary<string, string> ChosenArtwork { get; set; } = [];


    /// <summary>Suffix added before the extension, e.g. "Show 01x01.original.mkv".</summary>
    public string ArchiveSuffix { get; set; } = ".original";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string MkvMergePath { get; set; } = "mkvmerge";

    /// <summary>
    /// dovi_tool, for rebuilding a Dolby Vision declaration that a remux into
    /// MP4 stripped while leaving the RPU data in the picture. Nothing else
    /// uses it, and most libraries never will.
    /// </summary>
    public string DoviToolPath { get; set; } = "dovi_tool";

    /// <summary>
    /// The player Play uses. Optional like the rest of the outside tools -
    /// without one the file opens however Windows would open it.
    /// </summary>
    public string PlayerPath { get; set; } = "vlc";

    /// <summary>
    /// Open a preview full screen.
    ///
    /// Off by default. A preview is usually a glance to answer "which episode
    /// is this", and a window you can dismiss suits that better than one that
    /// takes the screen - but on a television, or for a proper look at a
    /// transfer, full screen is the point.
    /// </summary>
    public bool PlayFullScreen { get; set; } = false;

    /// <summary>
    /// Use mkvmerge for MKV sources when it's installed. It copies tracks without
    /// decoding, so MVC survives - ffmpeg would silently discard the second view.
    /// </summary>
    public bool PreferMkvMerge { get; set; } = true;

    /// <summary>
    /// Container to remux into: "mkv", or "keep" to leave each file in whatever
    /// it already uses. Matroska is the safer target - it carries Dolby Vision,
    /// MVC, PGS subtitles, chapters and per-track flags that MP4 either can't
    /// hold or holds badly - and it's the only container mkvmerge writes, so
    /// choosing it routes everything through the safer of the two engines.
    /// </summary>
    public string RemuxContainer { get; set; } = "mkv";

    // ---- NFO sidecars ----

    /// <summary>
    /// Fetch metadata and write a Kodi-format .nfo beside each matched file.
    /// Kodi can scrape for itself, so this exists for libraries kept offline or
    /// on players that only read sidecars - hence a switch rather than always.
    /// </summary>
    public bool WriteNfoFiles { get; set; } = true;

    /// <summary>
    /// First-run setup has been seen. Set whether it was completed or skipped -
    /// a wizard that reappears because you declined it is a wizard you come to
    /// resent.
    /// </summary>
    public bool SetupCompleted { get; set; }

    /// <summary>
    /// Never overwrite an existing .nfo. Hand-edited files and Kodi's own
    /// exports carry ratings, tags and artwork choices we don't reproduce, so
    /// replacing one silently would lose work.
    /// </summary>
    public bool PreserveExistingNfo { get; set; } = true;

    /// <summary>
    /// Fold an existing .nfo into the one being written instead of skipping it.
    ///
    /// Better than leaving the file alone, which is what PreserveExistingNfo did
    /// on its own: the reason for preserving was that a Kodi export holds ratings,
    /// tags and watch state we don't reproduce, and merging keeps every one of
    /// those while still taking the fresh title, plot and stream details.
    ///
    /// Watch state - playcount, last played, resume position - always wins over
    /// what we write, because we never know it. Everything else we do produce
    /// wins, since that is the point of rewriting the file.
    /// </summary>
    public bool MergeExistingNfo { get; set; } = true;

    /// <summary>
    /// Refresh the fileinfo/streamdetails block after a remux. The rest of the
    /// file is left exactly as it was - dropping tracks makes only that block
    /// wrong, and Kodi shows it in preference to re-probing.
    /// </summary>
    public bool UpdateNfoAfterRemux { get; set; } = true;

    [JsonIgnore]
    public bool RemuxToMkv => string.Equals(RemuxContainer, "mkv", StringComparison.OrdinalIgnoreCase);

    public const string MkvToolNixUrl = "https://mkvtoolnix.download/downloads.html";
    public const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/";
    public const string DoviToolUrl = "https://github.com/quietvoid/dovi_tool/releases";

    /// <summary>VideoLAN's own download page, not a mirror.</summary>
    public const string VlcUrl = "https://www.videolan.org/vlc/";

    [JsonIgnore]
    public string[] JunkExtensionList =>
        [.. JunkExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())];

    // ---- Metadata ----
    public string Language { get; set; } = "en";

    /// <summary>
    /// Merged by default: best coverage. Either source alone has gaps - the
    /// legacy TVDB endpoint is missing recent seasons, and TMDb occasionally
    /// lacks specials that TVDB catalogued years ago.
    /// </summary>
    public EpisodeSource EpisodeSource { get; set; } = EpisodeSource.Merged;

    /// <summary>
    /// Include TVmaze in Merged mode. Free and keyless, and independently
    /// curated - it lists what actually aired, so it's a useful cross-check
    /// against TMDb's more inclusive lists.
    /// </summary>
    public bool EnableTvMaze { get; set; } = true;

    /// <summary>
    /// Which source your Kodi library scrapes. Kodi ships the TMDb scrapers and
    /// binds a scraper per source path, so TheTVDB means a separately-installed
    /// addon. Episodes that exist only outside this source get flagged: the file
    /// will be named correctly but Kodi won't match it.
    /// </summary>
    public string KodiScraperSource { get; set; } = "TMDb";

    /// <summary>Warn when an episode only exists outside the Kodi scraper's source.</summary>
    public bool WarnKodiMismatch { get; set; } = true;

    /// <summary>
    /// Per-show source override, keyed by TheTVDB id.
    ///
    /// Sources genuinely disagree on some shows and neither is "right" globally.
    /// Battlestar Galactica is the clear case: TheTVDB puts the Miniseries at
    /// 0x01-0x02, TMDb doesn't, so every special after it is shifted. Once you've
    /// decided which is correct for a show, that decision is remembered.
    /// </summary>
    public Dictionary<string, string> ShowSourceOverrides { get; set; } = [];

    /// <summary>
    /// Comic series a person has matched against a database, keyed by the
    /// reduced form of the series name.
    ///
    /// The same reasoning as the show overrides above: the agreement is the
    /// valuable part, not the query. Comics are worse for it than television -
    /// the free source is a six gigabyte download and the others are rationed
    /// by the hour - so an answer that was agreed to once is kept rather than
    /// asked for again on every scan.
    /// </summary>
    public Dictionary<string, Books.ComicMatch> ComicMatches { get; set; } = [];

    /// <summary>
    /// Episode renumbering, keyed by show id and season - see <see cref="OffsetKey"/>.
    ///
    /// When files were numbered against one source's scheme and the metadata uses
    /// another's, every episode is out by a constant - Battlestar's miniseries
    /// counting as two episodes shifts everything after it by two. Rather than
    /// renaming by hand, shift the parsed number before it's looked up.
    ///
    /// Per season, not per show: seasons are numbered independently and are wrong
    /// independently too. One shift across a whole show fixes the season you were
    /// looking at and breaks the ones you weren't.
    /// </summary>
    public Dictionary<string, int> ShowEpisodeOffsets { get; set; } = [];

    private static string OffsetKey(string showId, int season) => $"{showId}:s{season}";

    /// <summary>The shift to apply to a parsed episode number, or 0 for none.</summary>
    public int EpisodeOffset(string? showId, int season)
    {
        if (string.IsNullOrEmpty(showId)) return 0;

        return ShowEpisodeOffsets.TryGetValue(OffsetKey(showId, season), out var v) ? v : 0;
    }

    /// <summary>Set or clear the shift for one season. Zero removes the entry.</summary>
    public void SetEpisodeOffset(string? showId, int season, int offset)
    {
        if (string.IsNullOrEmpty(showId)) return;

        var key = OffsetKey(showId, season);

        if (offset == 0) ShowEpisodeOffsets.Remove(key);
        else ShowEpisodeOffsets[key] = offset;
    }

    /// <summary>Seasons of this show that carry a shift, for showing what is set.</summary>
    public IEnumerable<(int Season, int Offset)> EpisodeOffsetsFor(string? showId)
    {
        if (string.IsNullOrEmpty(showId)) yield break;

        var prefix = showId + ":s";

        foreach (var (key, value) in ShowEpisodeOffsets)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(key[prefix.Length..], out var season)) yield return (season, value);
        }
    }

    /// <summary>
    /// Regex of noise tokens stripped from a folder name to build a search term.
    /// Carried over from the 3.0.4 SearchTermFilters value.
    /// </summary>
    public string SearchTermFilters { get; set; } =
        // season/episode markers
        @"\bS\d{1,2}E\d{1,3}\b|\b\d{1,2}x\d{1,3}\b|\bseason\s*\d{1,2}\b|\bS\d{1,2}\b|" +
        // release/quality noise
        @"\b(?:480p|540p|720p|1080p|1440p|2160p|4k|8k)\b|" +
        @"\b(?:x26[45]|h\.?26[45]|hevc|xvid|divx|10bit|8bit)\b|" +
        @"\b(?:aac|ac3|dts(?:-hd)?|truehd|atmos|ddp?5[\s.]?1|5[\s.]?1|7[\s.]?1)\b|" +
        @"\b(?:bluray|blu-ray|brrip|bdrip|dvdrip|dvd|webrip|web-?dl|hdtv|pdtv|hdrip|remux)\b|" +
        @"\b(?:complete|proper|repack|internal|extended|uncut|unrated|limited|upscaled)\b|" +
        @"\b(?:hdr10?|dolby(?:vision)?|multi|dual|subbed|dubbed|LOL)\b|" +
        // years and bracketed groups
        @"\b(?:19|20)\d{2}\b|\[[^\]]*\]|\([^)]*\)|\{[^}]*\}|" +
        // separators
        @"[._]";

    // ---- API keys ----
    // Empty by design. A key is registered to an account, so shipping one in the
    // program would be handing out someone's credential - and it would be
    // revoked the moment it was abused, breaking the app for everyone. Setup
    // asks for these on first run and links to where to get them.
    public string TmdbApiKey { get; set; } = "";
    public string TvdbApiKey { get; set; } = "";

    /// <summary>
    /// AcoustID, for identifying music files that carry no usable tags at all.
    ///
    /// The only key here that is used to ask about the user's own library
    /// rather than about a film or a series. What leaves the machine is a
    /// chromaprint fingerprint and a duration - not the audio, not the
    /// filename, not the path. Free, from acoustid.org/new-application.
    /// </summary>
    public string AcoustIdApiKey { get; set; } = "";

    /// <summary>
    /// Ignored like its siblings. Derived from the key above, so writing it into
    /// the settings file records an answer that is already there and would go
    /// stale the moment the key changed.
    /// </summary>
    [JsonIgnore]
    public bool HasAcoustIdKey => !string.IsNullOrWhiteSpace(AcoustIdApiKey);

    /// <summary>
    /// Nothing can be searched for without this: every lookup, TV or film, goes
    /// through TMDb.
    /// </summary>
    [JsonIgnore]
    public bool HasTmdbKey => !string.IsNullOrWhiteSpace(TmdbApiKey);

    /// <summary>Episode data only, and optional - TMDb alone works.</summary>
    [JsonIgnore]
    public bool HasTvdbKey => !string.IsNullOrWhiteSpace(TvdbApiKey);

    [JsonIgnore]
    public string[] VideoExtensions =>
        [.. AllowedFileTypes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())];

    /// <summary>Disc image extensions, whether or not they are being organised.</summary>
    [JsonIgnore]
    public string[] DiscImageExtensions =>
        [.. DiscImageTypes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())];

    /// <summary>
    /// Everything the scan should pick up: videos, plus disc images when those
    /// are being organised.
    ///
    /// This is the list for "what files belong to this item". VideoExtensions
    /// remains the list for "what can I open", and the two are deliberately not
    /// the same.
    /// </summary>
    [JsonIgnore]
    public string[] ScannedExtensions =>
        OrganiseDiscImages ? [.. VideoExtensions, .. DiscImageExtensions] : VideoExtensions;

    /// <summary>Whether a path is a disc image - something to file, never to open.</summary>
    public bool IsDiscImage(string path) =>
        DiscImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    [JsonIgnore]
    public string[] SubtitleExtensions =>
        [.. AllowedSubtitles.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())];

    // ---- Persistence ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultPath => Path.Combine(AppPaths.Folder, "settings.json");

    /// <summary>Where the pre-reset copy is kept, so a reset is never final.</summary>
    public static string BackupPath => Path.Combine(AppPaths.Folder, "settings.backup.json");

    /// <summary>
    /// The backup belonging to a particular settings file. It has to follow the
    /// file, not be a fixed location: a save aimed anywhere else would otherwise
    /// overwrite the real backup with unrelated content, and a failed load would
    /// fall back to it and quietly mix two different configurations. The default
    /// file keeps its established name.
    /// </summary>
    private static string BackupFor(string path) =>
        string.Equals(path, DefaultPath, StringComparison.OrdinalIgnoreCase)
            ? BackupPath
            : path + ".backup";

    /// <summary>
    /// Put every option back to its shipped default, in place.
    ///
    /// Copies from a fresh instance by reflection rather than assigning each
    /// property by hand: the option list grows steadily, and a hand-written
    /// reset silently stops covering whatever was added last.
    /// </summary>
    /// <param name="keepFolders">
    /// Keep the scan roots and the libraries they file into. They are the one
    /// thing here the user chose rather than accepted, and losing them turns a
    /// reset into a re-setup.
    ///
    /// Every kind of folder, not only the two for video. This used to carry TV
    /// and films alone, so answering "yes, keep my folders" still cleared the
    /// music, comic and ebook roots and all six destinations - and the dialog
    /// promising they were kept is what stopped anybody checking.
    /// </param>
    /// <param name="keepApiKeys">
    /// Keep any credential entered by hand: every key, not just the two for
    /// video metadata.
    /// </param>
    public void ResetToDefaults(bool keepFolders = true, bool keepApiKeys = true)
    {
        var tv = new List<string>(TvFolders);
        var movies = new List<string>(MovieFolders);
        var music = new List<string>(MusicFolders);
        var comics = new List<string>(ComicFolders);
        var books = new List<string>(BookFolders);

        var movieTo = MovieDestination;
        var tvTo = TvDestination;
        var musicTo = MusicDestination;
        var audiobookTo = AudiobookDestination;
        var comicTo = ComicDestination;
        var ebookTo = EbookDestination;

        var tmdb = TmdbApiKey;
        var tvdb = TvdbApiKey;
        var acoustId = AcoustIdApiKey;
        var comicVine = ComicVineApiKey;
        var metronUser = MetronUser;
        var metronPassword = MetronPassword;
        var gcd = GcdDatabasePath;

        var fresh = new PyreMediaSettings();

        foreach (var p in typeof(PyreMediaSettings).GetProperties())
        {
            if (!p.CanRead || !p.CanWrite) continue;

            // Computed views over other settings - nothing of their own to reset.
            if (p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length > 0) continue;

            try { p.SetValue(this, p.GetValue(fresh)); }
            catch { /* indexer or otherwise not a plain setting */ }
        }

        if (keepFolders)
        {
            TvFolders = tv;
            MovieFolders = movies;
            MusicFolders = music;
            ComicFolders = comics;
            BookFolders = books;

            MovieDestination = movieTo;
            TvDestination = tvTo;
            MusicDestination = musicTo;
            AudiobookDestination = audiobookTo;
            ComicDestination = comicTo;
            EbookDestination = ebookTo;
        }

        if (keepApiKeys)
        {
            TmdbApiKey = tmdb;
            TvdbApiKey = tvdb;
            AcoustIdApiKey = acoustId;
            ComicVineApiKey = comicVine;
            MetronUser = metronUser;
            MetronPassword = metronPassword;
            GcdDatabasePath = gcd;
        }
    }

    /// <summary>
    /// Copy the current file aside before overwriting it. A reset that can't be
    /// undone is a worse trap than the over-configured state it fixes.
    /// </summary>
    public static bool Backup(string? path = null)
    {
        try
        {
            var source = path ?? DefaultPath;
            if (!File.Exists(source)) return false;

            File.Copy(source, BackupFor(source), overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Set when the last Load couldn't read the settings file. The caller
    /// surfaces this - settings vanishing without a word is how a folder list
    /// disappears and nobody knows why.
    /// </summary>
    public static string? LastLoadProblem { get; private set; }

    /// <summary>
    /// Where this instance came from. An object that was never loaded from disk
    /// has no business becoming the source of truth for one - see <see cref="Save"/>.
    /// </summary>
    [JsonIgnore]
    private string? _origin;

    /// <summary>
    /// A file of API keys sitting beside the program, used to fill in any key
    /// the settings do not already have.
    ///
    /// This exists so a working build can carry its owner's keys without them
    /// being typed in again after every clean install - and, more to the point,
    /// without them being in the source. A key in a source file is a key in the
    /// repository, and a key in the repository is public the moment the
    /// repository is, whatever is deleted afterwards.
    ///
    /// So it is read and never written, and the filename is in .gitignore. A
    /// published build simply has no such file, and asks for keys the ordinary
    /// way.
    /// </summary>
    public const string LocalKeysFile = "keys.local.json";

    /// <summary>Where the keys file would be, beside the running program.</summary>
    public static string LocalKeysPath =>
        Path.Combine(AppContext.BaseDirectory, LocalKeysFile);

    /// <summary>
    /// Fill in any key this instance has not got from the local keys file.
    ///
    /// Only fills gaps. A key entered in Settings is the user's decision and
    /// outranks a file they may have forgotten is there - and clearing a key
    /// must stay cleared, or turning AcoustID off would silently undo itself
    /// on the next start.
    /// </summary>
    /// <summary>
    /// Rewrite any saved format that still uses numbers into names.
    ///
    /// Both forms have always worked, which is why this went unnoticed for so
    /// long: nothing was broken, the settings box simply showed
    /// "{0} {1}x{3} {2}" to anybody whose settings predated named
    /// placeholders. Nothing in the numbers says which is the episode and
    /// which is its title, and they do not even run in order.
    ///
    /// The stored value is only a record; how it is written down is
    /// presentation, and the readable spelling is the one to keep.
    /// </summary>
    private void UseNamedPlaceholders()
    {
        TvFileFormat = Naming.NameTokens.NameEpisode(TvFileFormat);

        MovieFileFormat = Naming.NameTokens.NameTitleYear(MovieFileFormat);
        MovieFolderFormat = Naming.NameTokens.NameTitleYear(MovieFolderFormat);
        ShowFolderFormat = Naming.NameTokens.NameTitleYear(ShowFolderFormat);
    }

    private void SeedKeysFromLocalFile()
    {
        try
        {
            if (!File.Exists(LocalKeysPath)) return;

            var seed = JsonSerializer.Deserialize<PyreMediaSettings>(
                File.ReadAllText(LocalKeysPath), JsonOptions);

            if (seed is null) return;

            if (string.IsNullOrWhiteSpace(TmdbApiKey)) TmdbApiKey = seed.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(TvdbApiKey)) TvdbApiKey = seed.TvdbApiKey;
            if (string.IsNullOrWhiteSpace(AcoustIdApiKey)) AcoustIdApiKey = seed.AcoustIdApiKey;

            // The comic sources travel the same way. Metron's is an account
            // password rather than a token, which is a reason to keep it out of
            // the source tree rather than a reason to treat it differently.
            if (string.IsNullOrWhiteSpace(ComicVineApiKey)) ComicVineApiKey = seed.ComicVineApiKey;
            if (string.IsNullOrWhiteSpace(MetronUser)) MetronUser = seed.MetronUser;
            if (string.IsNullOrWhiteSpace(MetronPassword)) MetronPassword = seed.MetronPassword;
        }
        catch
        {
            // A malformed keys file is not worth stopping the program for. The
            // keys simply stay empty and the user is asked, which is what would
            // happen without the file at all.
        }
    }

    public static PyreMediaSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        LastLoadProblem = null;

        if (!File.Exists(path))
        {
            var fresh = new PyreMediaSettings { _origin = path };
            fresh.SeedKeysFromLocalFile();
            return fresh;
        }

        // The main file first, then the backup written before the last save.
        foreach (var (candidate, label) in new[] { (path, "settings"), (BackupFor(path), "the backup") })
        {
            if (!File.Exists(candidate)) continue;

            try
            {
                var json = File.ReadAllText(candidate);
                var loaded = JsonSerializer.Deserialize<PyreMediaSettings>(json, JsonOptions);

                if (loaded is null) throw new InvalidDataException("empty document");

                if (candidate != path)
                    LastLoadProblem = $"Settings were unreadable, so {label} was used instead.";

                loaded._origin = path;
                loaded.SeedKeysFromLocalFile();
                loaded.UseNamedPlaceholders();
                return loaded;
            }
            catch (Exception ex)
            {
                if (candidate != path) continue;

                // Keep the unreadable file rather than letting the next save
                // overwrite it - it may still be recoverable by hand.
                try
                {
                    var kept = path + ".corrupt";
                    File.Copy(path, kept, overwrite: true);
                    LastLoadProblem = $"Settings could not be read ({ex.Message}). "
                                      + $"The file was kept as {Path.GetFileName(kept)}.";
                }
                catch
                {
                    LastLoadProblem = $"Settings could not be read ({ex.Message}).";
                }
            }
        }

        LastLoadProblem ??= "Settings could not be read; defaults are in use.";
        return new PyreMediaSettings { _origin = path };
    }

    /// <summary>
    /// Write atomically: a full file appears, or the previous one survives
    /// untouched.
    ///
    /// WriteAllText truncates before it writes, so a crash - or the process
    /// being killed - during that window leaves a half-written file. Load then
    /// falls back to defaults, and the next save makes the loss permanent.
    /// That is exactly how a configured folder list disappears.
    /// </summary>
    public void Save(string? path = null)
    {
        // Write back to wherever this came from. A default-constructed instance
        // has nowhere of its own, and letting it fall through to the real settings
        // file is how a throwaway object - a test fixture, a preview, a dialog
        // handed a scratch copy - silently replaces a configured folder list with
        // an empty one. That happened, and cost a real folder list twice.
        if (path is null && _origin is null && File.Exists(DefaultPath))
        {
            throw new InvalidOperationException(
                "These settings were never loaded from disk, so saving them would "
                + "overwrite the real settings file with defaults. Load() them first, "
                + "or pass an explicit path.");
        }

        path ??= _origin ?? DefaultPath;
        _origin ??= path;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);

        // Sanity check before anything is replaced: never let a serialisation
        // that produced nothing useful overwrite a good file.
        if (string.IsNullOrWhiteSpace(json) || json.Length < 2)
            throw new InvalidOperationException("Refusing to save empty settings.");

        var temp = path + ".tmp";
        File.WriteAllText(temp, json);

        // Keep the last known-good copy, so even a corrupt main file is
        // recoverable on the next load.
        try
        {
            if (File.Exists(path)) File.Copy(path, BackupFor(path), overwrite: true);
        }
        catch { /* a missing backup is not worth failing the save for */ }

        File.Move(temp, path, overwrite: true);
    }
}
