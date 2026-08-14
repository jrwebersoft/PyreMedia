using PyreMedia.Core;
using PyreMedia.Core.History;
using PyreMedia.Core.Metadata;
using PyreMedia.Core.Models;
using PyreMedia.Core.Media;
using PyreMedia.Core.Music;
using PyreMedia.Core.Naming;
using PyreMedia.Core.Organizing;

// Checks, run with `dotnet run --project src/PyreMedia.Tests`. Exits non-zero on
// any failure, so it works as a build gate.
//
// Every duration here was measured by ffprobe on a real 17,168-file library
// rather than invented, because the rules they pin down were all got wrong first
// by reasoning about what the numbers ought to be. The MP3-versus-WMA gaps in
// particular look like different recordings until you see nine of them in a row
// leaning the same way.

var failures = 0;

void Check(string what, bool ok)
{
    Console.WriteLine($"   {(ok ? "ok  " : "FAIL")}  {what}");
    if (!ok) failures++;
}

Console.WriteLine("=== duration tolerance ===");

// Nickelback, Silver Side Up: the mp3 and wma of one rip, measured.
Check("Never Again mp3 262.90 vs wma 259.46 is one recording",
    Duplicates.SameLength(262.896327, 259.461));
Check("Woke Up This Morning 232.31 vs 229.07 is one recording",
    Duplicates.SameLength(232.306939, 229.074));
Check("Hollywood 186.93 vs 184.06 is one recording",
    Duplicates.SameLength(186.932245, 184.063));

// Eiffel 65: a radio cut tagged as the extended mix. Must stay a question.
Check("Blue 3:30 vs 4:46 is not one recording",
    !Duplicates.SameLength(210, 286));
Check("a six-second gap on a 3:40 track is not one recording",
    !Duplicates.SameLength(222, 216));

Console.WriteLine("\n=== title matching ===");

Check("Norwegian Wood, The vs This Bird Has Flown, is one song",
    Duplicates.Alike("norwegianwoodthebirdhasflown", "norwegianwoodthisbirdhasflown"));
Check("Fergalicious is not London Bridge",
    !Duplicates.Alike("fergalicious", "londonbridge"));
Check("Jump is not Girl Gone Bad",
    !Duplicates.Alike("jump", "girlgonebad"));
Check("Enter Sandman matches itself",
    Duplicates.Alike("entersandman", "entersandman"));
Check("Blue Da Ba Dee is not Blue Da Ba Dee Extended Mix",
    !Duplicates.Alike("bluedabadee", "bluedabadeeextendedmix"));

Console.WriteLine("\n=== quality ranking ===");

Check("WMA Lossless at 920 beats MP3 at 330",
    Duplicates.Quality(At(@"c:\a\01 x.wma", 920)) > Duplicates.Quality(At(@"c:\a\01 x.mp3", 330)));
Check("FLAC beats MP3 at 320",
    Duplicates.Quality(At(@"c:\a\01 x.flac", 900)) > Duplicates.Quality(At(@"c:\a\01 x.mp3", 320)));
Check("lossy WMA at 129 loses to MP3 at 192",
    Duplicates.Quality(At(@"c:\a\01 x.wma", 129)) < Duplicates.Quality(At(@"c:\a\01 x.mp3", 192)));
Check("MP3 at 320 beats MP3 at 128",
    Duplicates.Quality(At(@"c:\a\01 x.mp3", 320)) > Duplicates.Quality(At(@"c:\a\01 x.mp3", 128)));

Console.WriteLine("\n=== classification ===");

Check("two formats of one rip is SameRecording",
    Duplicates.Classify([Track(@"c:\a\02 Mama.mp3", "Mama", 119.0, 322),
                         Track(@"c:\a\02 Mama.wma", "Mama", 119.0, 1045)]).Kind
    == DuplicateKind.SameRecording);

Check("different songs on one number is WrongNumber",
    Duplicates.Classify([Track(@"c:\a\01 Fergalicious.mp3", "Fergalicious", 292.0, 320),
                         Track(@"c:\a\01 London Bridge.mp3", "London Bridge", 241.0, 320)]).Kind
    == DuplicateKind.WrongNumber);

Check("same title at very different lengths is AlternateVersion",
    Duplicates.Classify([Track(@"c:\a\14 Blue.mp3", "Blue (Da Ba Dee) [Extended Mix]", 210.0, 188),
                         Track(@"c:\a\14 Blue.wma", "Blue (Da Ba Dee) [Extended Mix]", 286.0, 329)]).Kind
    == DuplicateKind.AlternateVersion);

Check("a feat. credit on one side only is still one recording",
    Duplicates.Classify([Track(@"c:\a\02 Go Kindergarten (feat. Robyn).mp3", "Go Kindergarten (feat. Robyn)", 148.0, 325),
                         Track(@"c:\a\02 Go Kindergarten.wma", "Go Kindergarten", 147.0, 1048)]).Kind
    == DuplicateKind.SameRecording);

Check("an Explorer (2) copy is still one recording",
    Duplicates.Classify([Track(@"c:\a\01 Ultra Mega (2).wma", "Ultra Mega (2)", 210.0, 355),
                         Track(@"c:\a\01 Ultra Mega.wma", "Ultra Mega", 210.0, 355)]).Kind
    is DuplicateKind.ExactCopy or DuplicateKind.SameRecording);

Check("the lossless side is the one kept",
    Duplicates.Classify([Track(@"c:\a\02 Mama.mp3", "Mama", 119.0, 322),
                         Track(@"c:\a\02 Mama.wma", "Mama", 119.0, 1045)]).Keep!.Path.EndsWith(".wma"));

Check("an alternate version keeps nothing on its own",
    Duplicates.Classify([Track(@"c:\a\14 Blue.mp3", "Blue [Extended Mix]", 210.0, 188),
                         Track(@"c:\a\14 Blue.wma", "Blue [Extended Mix]", 286.0, 329)]).Keep is null);

Console.WriteLine("\n=== a clash holding more than one recording ===");
{
    // The Animatrix: two copies of one track plus a third that names the artist
    // in its title. Judging the whole clash at once left the two copies in place.
    var clash = new[]
    {
        Named(@"c:\a\04 Under the Gun (2).wma", "Under the Gun", "Supreme Beings of Leisure", 222.0, 320),
        Named(@"c:\a\04 Under the Gun.wma", "Under The Gun (Supreme Beings Of Leisure)", "Supreme Beings of Leisure", 222.0, 320),
        Named(@"c:\a\4 Under the Gun.wma", "Under the Gun", "Supreme Beings of Leisure", 222.0, 320)
    };

    var sets = Duplicates.ClassifyAll(clash);

    Check("the artist in a title does not make it another song",
        sets.Count == 1 && sets[0].Kind is not DuplicateKind.WrongNumber);
    Check("all three are treated as one recording",
        sets.Count == 1 && sets[0].Files.Count == 3);
    Check("two of the three are redundant",
        sets.Count == 1 && sets[0].Keep is not null);
}
{
    // Two copies of one track, plus a genuinely different song on the same number.
    var clash = new[]
    {
        Named(@"c:\a\01 Ultra Mega.wma", "Ultra Mega", "Powerman 5000", 210.0, 355),
        Named(@"c:\a\01 Ultra Mega (2).wma", "Ultra Mega", "Powerman 5000", 210.0, 355),
        Named(@"c:\a\01 Bloodline.wma", "Bloodline", "Slayer", 201.0, 353)
    };

    var sets = Duplicates.ClassifyAll(clash);

    Check("the odd song out is reported as a numbering fault",
        sets.Any(s => s.Kind is DuplicateKind.WrongNumber));
    Check("the two real copies are still resolved",
        sets.Any(s => s.Kind is DuplicateKind.ExactCopy or DuplicateKind.SameRecording
                      && s.Files.Count == 2 && s.Keep is not null));
}

Console.WriteLine("\n=== every spelling of a compilation groups together ===");
{
    var tracks = new List<TrackTags>
    {
        Comp(@"E:\m\Dracula 2000\01 Ultra Mega.wma", "Soundtrack", "Powerman 5000", 1),
        Comp(@"E:\m\Dracula 2000\02 A Welcome Burden.wma", "Original Soundtrack", "Disturbed", 2),
        Comp(@"E:\m\Dracula 2000\03 Bloodline.wma", "Various Artists", "Slayer", 3),
        Comp(@"E:\m\Dracula 2000\04 Metro.wma", "OST", "System of a Down", 4)
    };

    var albums = AlbumGrouper.Group(tracks);
    Check("four spellings make one album", albums.Count == 1);
    Check("and it is a compilation", albums[0].IsCompilation);
}

Console.WriteLine("\n=== track numbers and titles from filenames ===");

Check("01 White & Nerdy is track 1",
    MusicPlanner.SplitFilename(@"c:\a\01 White & Nerdy.mp3") == (1, "White & Nerdy"));
Check("00 01 One Vision steps over the leading zeros",
    MusicPlanner.SplitFilename(@"c:\a\00 01 One Vision.mp3") == (1, "One Vision"));
Check("00 00 Da Funk Music keeps its title and takes no number",
    MusicPlanner.SplitFilename(@"c:\a\00 00 Da Funk Music.mp3") == (null, "Da Funk Music"));
Check("1984 is not track 198",
    MusicPlanner.SplitFilename(@"c:\a\1984.mp3") == (null, "1984"));
Check("a bare title takes no number",
    MusicPlanner.SplitFilename(@"c:\a\Enter Sandman.mp3") == (null, "Enter Sandman"));
Check("07-Money Bought splits on the dash",
    MusicPlanner.SplitFilename(@"c:\a\07-Money Bought.mp3") == (7, "Money Bought"));

Console.WriteLine("\n=== when nothing measured the lengths ===");
{
    // ffprobe missing, or a format it cannot read. The tempting answer is
    // "duplicate", and it deletes somebody's extended mix.
    var unmeasured = new[]
    {
        new TrackTags { Path = @"c:\a\14 Blue.mp3", Kbps = 188,
            Title = new TagValue("Blue [Extended Mix]", TagSource.Id3v2, TextConfidence.Declared) },
        new TrackTags { Path = @"c:\a\14 Blue.wma", Kbps = 329,
            Title = new TagValue("Blue [Extended Mix]", TagSource.Asf, TextConfidence.Declared) }
    };

    var set = Duplicates.Classify(unmeasured);

    Check("an unmeasured pair is Unknown, not a duplicate", set.Kind is DuplicateKind.Unknown);
    Check("and nothing is chosen to discard", set.Keep is null);
    Check("and it is not settled by rule", !set.SettledByRule);
}

Console.WriteLine("\n=== reading an album.nfo ===");
{
    var folder = Path.Combine(Path.GetTempPath(), "pyremedia-nfo-check");
    Directory.CreateDirectory(folder);

    File.WriteAllText(Path.Combine(folder, "album.nfo"), """
        <?xml version="1.0" encoding="UTF-8" standalone="yes" ?>
        <album>
            <title>Silver Side Up</title>
            <artistdesc>Nickelback</artistdesc>
            <compilation>false</compilation>
            <year>2001</year>
            <label>Roadrunner Records</label>
            <albumArtistCredits>
                <artist>Nickelback</artist>
                <musicBrainzArtistID>bc710bcf-8815-42cf-bad2-3f1d12246aeb</musicBrainzArtistID>
            </albumArtistCredits>
            <track><title>Never Again</title><position>1</position><duration>04:20</duration></track>
            <track><title>How You Remind Me</title><position>2</position><duration>03:43</duration></track>
            <track><title>Woke Up This Morning</title><position>3</position><duration>03:50</duration></track>
        </album>
        """);

    var nfo = MusicNfoReader.ReadAlbum(folder);

    Check("it parses", nfo is not null);
    Check("title, year and label come through",
        nfo!.Title == "Silver Side Up" && nfo.Year == "2001" && nfo.Label == "Roadrunner Records");
    Check("the compilation flag is read as false", nfo.IsCompilation is false);
    Check("the MusicBrainz artist id comes through",
        nfo.MusicBrainzArtistId == "bc710bcf-8815-42cf-bad2-3f1d12246aeb");
    Check("an empty musicBrainzAlbumID reads as absent", nfo.MusicBrainzAlbumId is null);
    Check("three tracks with positions and lengths",
        nfo.Tracks.Count == 3 && nfo.Tracks[0].Position == 1 && nfo.Tracks[0].Seconds == 260);

    // The listing describes one track of three. That is a normal library.
    var one = new[] { Track(Path.Combine(folder, "02 How You Remind Me.mp3"), "How You Remind Me", 223.4, 320) };
    Check("a folder holding one of three tracks is still described", nfo.Corroborates(one));

    // Same title, same year, listing for a different release.
    var elsewhere = new[]
    {
        Track(Path.Combine(folder, "01 Photograph.mp3"), "Photograph", 259.0, 320),
        Track(Path.Combine(folder, "02 Far Away.mp3"), "Far Away", 238.0, 320)
    };
    Check("a listing for another release is not believed", !nfo.Corroborates(elsewhere));

    Directory.Delete(folder, true);
}

Console.WriteLine("\n=== nfo durations ===");

Check("04:20 is 260 seconds", MusicNfoReader.Duration("04:20") == 260);
Check("1:02:33 is 3753 seconds", MusicNfoReader.Duration("1:02:33") == 3753);
Check("plain seconds are taken as seconds", MusicNfoReader.Duration("260") == 260);
Check("nothing is zero", MusicNfoReader.Duration("") == 0 && MusicNfoReader.Duration(null) == 0);
Check("rubbish is zero", MusicNfoReader.Duration("about four minutes") == 0);

Console.WriteLine("\n=== reading an MP4 (.m4a) ===");
{
    // Built by hand rather than with ffmpeg, so the check needs nothing installed
    // and exercises the box walk itself: moov > udta > meta > ilst > tag > data.
    var ilst = Cat(
        Item("©nam", Data(1, Utf8("Get Lucky"))),
        Item("©ART", Data(1, Utf8("Daft Punk"))),
        Item("aART", Data(1, Utf8("Daft Punk"))),
        Item("©alb", Data(1, Utf8("Random Access Memories"))),
        Item("©day", Data(1, Utf8("2013"))),
        Item("trkn", Data(0, [0, 0, 0, 8, 0, 13, 0, 0])),
        Item("disk", Data(0, [0, 0, 0, 1, 0, 2])),
        Item("gnre", Data(0, [0, 53])),
        Item("----", Cat(
            Box("mean", Cat([0, 0, 0, 0], Utf8("com.apple.iTunes"))),
            Box("name", Cat([0, 0, 0, 0], Utf8("MusicBrainz Album Id"))),
            Data(1, Utf8("4a8e0d61-c5b3-4f7f-9b12-000000000000")))));

    var file = Box("moov", Box("udta", Box("meta",
        Cat([0, 0, 0, 0], Box("hdlr", new byte[16]), Box("ilst", ilst)))));

    var path = Path.Combine(Path.GetTempPath(), "pyremedia-check.m4a");
    File.WriteAllBytes(path, file);

    var t = MusicFileReader.Read(path);

    Check("no error", t.Error is null);
    Check("the source is recorded as MP4", t.Sources.Contains(TagSource.Mp4));
    Check("title, artist, album", t.Title.Text == "Get Lucky"
        && t.Artist.Text == "Daft Punk" && t.Album.Text == "Random Access Memories");
    Check("album artist", t.AlbumArtist.Text == "Daft Punk");
    Check("year", t.Year.Text == "2013");
    Check("trkn is read as binary, 8 of 13", t.TrackNumber == 8 && t.TrackCount == 13);
    Check("disk is read as binary, 1 of 2", t.DiscNumber == 1 && t.DiscCount == 2);
    Check("the numeric genre is one-based, unlike ID3's own",
        t.Genre.Text == Id3Reader.Genres[52]);
    Check("a freeform MusicBrainz id is picked up",
        t.MusicBrainzAlbumId == "4a8e0d61-c5b3-4f7f-9b12-000000000000");
    Check("MP4 text is declared, never guessed", t.Title.Confidence == TextConfidence.Declared);

    File.Delete(path);
}
{
    // Some writers leave the four version bytes off the meta box.
    var ilst = Item("©nam", Data(1, Utf8("No Version Box")));
    var file = Box("moov", Box("udta", Box("meta",
        Cat(Box("hdlr", new byte[16]), Box("ilst", ilst)))));

    var path = Path.Combine(Path.GetTempPath(), "pyremedia-check2.m4a");
    File.WriteAllBytes(path, file);

    var t = MusicFileReader.Read(path);
    Check("a meta box with no version bytes still reads", t.Title.Text == "No Version Box");

    File.Delete(path);
}
{
    var path = Path.Combine(Path.GetTempPath(), "pyremedia-check3.m4a");
    File.WriteAllBytes(path, Utf8("this is not an mp4 at all, not even close"));

    var t = MusicFileReader.Read(path);
    Check("rubbish is not mistaken for tags", !t.HasAnyText && t.Error is null);

    File.Delete(path);
}

Console.WriteLine("\n=== reading an Ogg ===");
{
    var comments = Cat(
        [3], Utf8("vorbis"),
        Length(Utf8("test")), Utf8("test"),
        Length32(4),
        Entry("TITLE=Old Polina"),
        Entry("ARTIST=Great Big Sea"),
        Entry("ALBUM=The Hard And The Easy"),
        Entry("TRACKNUMBER=2/12"),
        [1]);

    var path = Path.Combine(Path.GetTempPath(), "pyremedia-check.ogg");
    File.WriteAllBytes(path, OggPage(comments));

    var t = MusicFileReader.Read(path);

    Check("no error", t.Error is null);
    Check("the source is recorded as Vorbis", t.Sources.Contains(TagSource.VorbisComment));
    Check("title and artist", t.Title.Text == "Old Polina" && t.Artist.Text == "Great Big Sea");
    Check("2/12 gives both number and total", t.TrackNumber == 2 && t.TrackCount == 12);
}
{
    // Opus carries the same comments behind a different marker.
    var comments = Cat(
        Utf8("OpusTags"),
        Length(Utf8("test")), Utf8("test"),
        Length32(1),
        Entry("TITLE=An Opus Track"));

    var path = Path.Combine(Path.GetTempPath(), "pyremedia-check.opus");
    File.WriteAllBytes(path, OggPage(comments));

    var t = MusicFileReader.Read(path);
    Check("OpusTags is read the same way", t.Title.Text == "An Opus Track");
}
{
    // A packet split across two pages: the reader has to reassemble it before
    // the comments make any sense.
    // The vendor string is padded so the packet is long enough to break on a
    // 255-byte boundary, which is the only place Ogg allows it to break.
    var vendor = Utf8(new string('v', 300));

    var comments = Cat(
        [3], Utf8("vorbis"),
        Length(vendor), vendor,
        Length32(1),
        Entry("TITLE=Split Across Pages"),
        [1]);

    var path = Path.Combine(Path.GetTempPath(), "pyremedia-split.ogg");

    File.WriteAllBytes(path, Cat(
        OggPartialPage(comments[..255]),
        OggPage(comments[255..], continued: true)));

    var t = MusicFileReader.Read(path);
    Check("a packet spanning two pages is reassembled", t.Title.Text == "Split Across Pages");

    File.Delete(path);
}

Console.WriteLine("\n=== sorting out what is not music ===");
{
    static SweepCategory Of(string name) => MusicSweep.Classify(new FileInfo(@"c:\a\" + name));

    Check("folder.jpg is art", Of("folder.jpg") == SweepCategory.Art);
    Check("cdart.png is art", Of("cdart.png") == SweepCategory.Art);
    Check("album.nfo is scraped metadata", Of("album.nfo") == SweepCategory.ScrapedMetadata);
    Check("desktop.ini is a leftover", Of("desktop.ini") == SweepCategory.SystemLeftovers);
    Check("desktop (2).ini is the same leftover", Of("desktop (2).ini") == SweepCategory.SystemLeftovers);
    Check("Thumbs (3).db is the same leftover", Of("Thumbs (3).db") == SweepCategory.SystemLeftovers);
    Check("AlbumArtSmall.jpg is a leftover, not art",
        Of("AlbumArtSmall.jpg") == SweepCategory.SystemLeftovers);
    Check("AlbumArt_{GUID}_Large.jpg is a leftover",
        Of("AlbumArt_{F4BE81E3-9848-43A2-9303-76AAEA5D0ADF}_Large.jpg") == SweepCategory.SystemLeftovers);
    Check("a concert film is video", Of("live at wembley.mkv") == SweepCategory.Video);
    Check("video is not swept by default", !MusicSweep.FullRefresh.Contains(SweepCategory.Video));
    Check("an album named with brackets is not mistaken for a copy",
        Of("Disc (Live).nfo") == SweepCategory.ScrapedMetadata);
}
{
    // A folder holding only art, beside one holding music.
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-sweep-check");
    if (Directory.Exists(root)) Directory.Delete(root, true);

    Directory.CreateDirectory(Path.Combine(root, "Artist", "extrafanart"));
    Directory.CreateDirectory(Path.Combine(root, "Artist", "Album"));

    File.WriteAllText(Path.Combine(root, "Artist", "artist.nfo"), "<artist/>");
    File.WriteAllBytes(Path.Combine(root, "Artist", "extrafanart", "fanart1.jpg"), new byte[16]);
    File.WriteAllBytes(Path.Combine(root, "Artist", "Album", "01 Track.mp3"), new byte[32]);
    File.WriteAllBytes(Path.Combine(root, "Artist", "Album", "folder.jpg"), new byte[16]);

    var result = MusicSweep.Survey(root);

    Check("the music file is not listed",
        result.Items.All(i => !i.Path.EndsWith(".mp3")));
    Check("art and metadata are listed", result.Items.Count == 3);
    Check("the art-only folder is reported as holding no music",
        result.MusiclessFolders.Any(f => f.EndsWith("extrafanart")));
    Check("the folder holding the music is not",
        !result.MusiclessFolders.Any(f => f.EndsWith("Album")));
    Check("nor is its parent, which holds music below it",
        !result.MusiclessFolders.Any(f => f.EndsWith("Artist")));

    var art = MusicSweep.Survey(root, [SweepCategory.Art]);
    Check("asking only for art leaves the .nfo alone",
        art.Items.All(i => i.Category == SweepCategory.Art) && art.Items.Count == 2);

    Directory.Delete(root, true);
}

Console.WriteLine("\n=== disc markers ===");

Check("Disc 2 comes out of an album title", AlbumGrouper.StripDisc("S&M Disc 2") == ("S&M", 2));
Check("(Cd1) comes out", AlbumGrouper.StripDisc("Tron- Legacy (Cd1)") == ("Tron- Legacy", 1));
Check("Vol. 2 stays put", AlbumGrouper.StripDisc("Ryde or Die, Vol. 2") == ("Ryde or Die, Vol. 2", null));
Check("Vol. 13 stays but Disc 1 goes",
    AlbumGrouper.StripDisc("Sunshine Live, Vol. 13 Disc 1") == ("Sunshine Live, Vol. 13", 1));
Check("a title that is only a disc marker survives",
    AlbumGrouper.StripDisc("Disc 2").Title.Length > 0);

Console.WriteLine("\n=== discs taken from the folder name ===");
{
    // Metallica S&M: both folders tag the album "S&M" with no disc at all.
    var tracks = new List<TrackTags>();
    for (var n = 1; n <= 11; n++) tracks.Add(Folder(@"E:\m\Metallica\S&M Disc 1", n, "S&M"));
    for (var n = 1; n <= 10; n++) tracks.Add(Folder(@"E:\m\Metallica\S&M Disc 2", n, "S&M"));

    var album = AlbumGrouper.Group(tracks).Single();

    Check("both discs land in one album", album.TrackCount == 21);
    Check("it is marked multi-disc", album.IsMultiDisc);
    Check("the folder supplied the disc numbers",
        album.Tracks.Count(t => t.DiscNumber == 1) == 11 && album.Tracks.Count(t => t.DiscNumber == 2) == 10);
    Check("no track number collides any more", album.Caution is null || !album.Caution.Contains("more than once"));
}

Console.WriteLine("\n=== volumes stay separate ===");
{
    var tracks = new List<TrackTags>();
    for (var n = 1; n <= 5; n++) tracks.Add(Folder(@"E:\m\Ruff Ryders\Ryde or Die, Vol. 1", n, "Ryde or Die, Vol. 1"));
    for (var n = 1; n <= 5; n++) tracks.Add(Folder(@"E:\m\Ruff Ryders\Ryde or Die, Vol. 2", n, "Ryde or Die, Vol. 2"));

    var albums = AlbumGrouper.Group(tracks);
    Check("two volumes are two albums", albums.Count == 2);
}

// The audio outranks the tags. Each of these is a real case the tag rules got
// wrong, measured against fingerprints on the live library.
Console.WriteLine("\n=== the audio settles it ===");
{
    // Linkin Park, Recharged: "03 BURN IT DOWN (Swoon Remix)" beside
    // "09 Burn It Down". Two titles, 91% the same audio - one recording.
    var a = Track(@"E:\m\a\03 BURN IT DOWN (Swoon Remix).mp3", "BURN IT DOWN (Swoon Remix)", 228, 320);
    var b = Track(@"E:\m\a\09 Burn It Down.mp3", "Burn It Down", 228, 192);

    Check("titles that differ are one recording when the audio agrees",
        Duplicates.Classify([a, b], Prints((a, 0u), (b, 3u))).Kind == DuplicateKind.SameRecording);

    Check("and the better file is the one kept",
        Duplicates.Classify([a, b], Prints((a, 0u), (b, 3u))).Keep == a);

    Check("without the audio they read as two different songs",
        Duplicates.Classify([a, b]).Kind == DuplicateKind.WrongNumber);
}
{
    // Jewel, Spirit, track 11. Both files 4:58, both titled "Life Uncommon",
    // and only 60.5% of the audio in common. The tag rules called this a
    // duplicate and offered one of them for deletion.
    var a = Track(@"E:\m\Jewel\Spirit\11 Life Uncommon.mp3", "Life Uncommon", 298, 192);
    var b = Track(@"E:\m\Jewel\Spirit\11 Life Uncommon.wma", "Life Uncommon", 298, 128);

    Check("same title and length is NOT a duplicate when the audio differs",
        Duplicates.Classify([a, b], Prints((a, 0u), (b, 0xFFFFu))).Kind == DuplicateKind.WrongNumber);

    Check("nothing is offered for deletion in that case",
        Duplicates.Classify([a, b], Prints((a, 0u), (b, 0xFFFFu))).Keep is null);

    Check("the tag rules alone would have discarded one of them",
        Duplicates.Classify([a, b]).Kind == DuplicateKind.SameRecording);
}
{
    // Held as a question because the lengths disagreed by more than the
    // encoder-padding allowance. The audio says one recording, so there is
    // nothing to ask.
    var a = Track(@"E:\m\b\04 Song.flac", "Song", 240, 900);
    var b = Track(@"E:\m\b\04 Song.mp3", "Song", 262, 320);

    Check("a length gap is not a question when the audio agrees",
        Duplicates.Classify([a, b], Prints((a, 0u), (b, 3u))).Kind == DuplicateKind.SameRecording);

    Check("the lossless file wins",
        Duplicates.Classify([a, b], Prints((a, 0u), (b, 3u))).Keep == a);

    Check("without the audio it is held as an alternate version",
        Duplicates.Classify([a, b]).Kind == DuplicateKind.AlternateVersion);
}
{
    // Nothing measured the durations. That is Unknown on tags alone; the audio
    // answers it outright without any probing.
    var a = Track(@"E:\m\c\07 Track.mp3", "Track", 0, 192);
    var b = Track(@"E:\m\c\07 Track.wma", "Track", 0, 128);

    Check("unmeasured lengths are still settled by the audio",
        Duplicates.Classify([a, b], Prints((a, 0u), (b, 0u))).Kind == DuplicateKind.SameRecording);

    Check("and are Unknown without it",
        Duplicates.Classify([a, b]).Kind == DuplicateKind.Unknown);
}
{
    // One file ffmpeg could not read. A missing fingerprint is no opinion, not
    // a verdict - the tag rules must take over rather than the pair being
    // declared different.
    var a = Track(@"E:\m\d\01 Thing.mp3", "Thing", 200, 192);
    var b = Track(@"E:\m\d\01 Thing.wma", "Thing", 200, 128);
    var half = new FingerprintSet([new(a.Path, Print(0u))]);

    Check("one missing fingerprint means no opinion", half.SameRecording([a, b]) is null);
    Check("so the tag rules still decide",
        Duplicates.Classify([a, b], half).Kind == DuplicateKind.SameRecording);

    var tooShort = new FingerprintSet([new(a.Path, Print(0u, 40)), new(b.Path, Print(0u, 40))]);
    Check("a fingerprint too short to judge is also no opinion",
        tooShort.SameRecording([a, b]) is null);
}
{
    // Three files, two of them one recording. The odd one out must not be
    // dragged in by the pair that matches.
    var a = Track(@"E:\m\e\05 One.mp3", "One", 200, 320);
    var b = Track(@"E:\m\e\05 One.wma", "One", 200, 128);
    var c = Track(@"E:\m\e\05 Other.mp3", "Other", 200, 320);
    var prints = Prints((a, 0u), (b, 3u), (c, 0xFFFFu));

    var sets = Duplicates.ClassifyAll([a, b, c], prints);

    Check("a mixed clash is reported as wrongly numbered",
        sets.Any(s => s.Kind == DuplicateKind.WrongNumber));
    Check("and the matching pair is still deduplicated",
        sets.Any(s => s.Kind == DuplicateKind.SameRecording && s.Files.Count == 2));
    Check("the weakest pair decides a set, not the average",
        prints.SameRecording([a, b, c]) is false);
}

// Filling a hole in an album from the rest of the library.
Console.WriteLine("\n=== gaps filled from elsewhere ===");
{
    // The album has 3 of its 4 tracks; the fourth is on another record by the
    // same artist, at the length the listing wants.
    var album = Album("Yellow Submarine", "The Beatles",
        Named(@"E:\m\Beatles\Yellow Submarine\01 A.mp3", "A", "The Beatles", 120, 320),
        Named(@"E:\m\Beatles\Yellow Submarine\02 B.mp3", "B", "The Beatles", 120, 320),
        Named(@"E:\m\Beatles\Yellow Submarine\03 C.mp3", "C", "The Beatles", 120, 320));

    var other = Album("Past Masters", "The Beatles",
        Named(@"E:\m\Beatles\Past Masters\09 Sie Liebt Dich.mp3", "Sie Liebt Dich", "The Beatles", 140, 320));

    var listing = Listing(("A", 120), ("B", 120), ("C", 120), ("Sie liebt dich", 144));

    var gaps = MusicGapFill.Find([album, other], null, _ => listing);

    Check("the missing track is found on another album by the same artist", gaps.Count == 1);
    Check("and it is fillable without asking", gaps[0].Obvious is not null);
    Check("from the file that is actually there",
        gaps[0].Obvious!.Best.Path.EndsWith("Sie Liebt Dich.mp3"));

    // Citizen King's "Blue Monday" is not Orgy's cover of it. Same title, one
    // second apart, a different band - and on titles alone this filled the gap.
    var orgy = Album("Candyass", "Orgy",
        Named(@"E:\m\Orgy\Candyass\07 Blue Monday.mp3", "Blue Monday", "Orgy", 265, 320));

    var king = Album("Mobile Estates", "Citizen King",
        Named(@"E:\m\Citizen King\Mobile Estates\01 A.mp3", "A", "Citizen King", 120, 320),
        Named(@"E:\m\Citizen King\Mobile Estates\02 B.mp3", "B", "Citizen King", 120, 320));

    var kingList = Listing(("A", 120), ("B", 120), ("Blue Monday", 266));

    Check("another band's recording of the same song is not a candidate",
        MusicGapFill.Find([king, orgy], null, f => f.Contains("Citizen King") ? kingList : null).Count == 0);
}
{
    // A live album missing its live take, with the studio version on disk. The
    // lengths disagree, so it is offered and never filled.
    var live = Album("Anywhere But Home", "Evanescence",
        Named(@"E:\m\Ev\Anywhere But Home\02 Going Under.mp3", "Going Under", "Evanescence", 240, 320),
        Named(@"E:\m\Ev\Anywhere But Home\03 Taking Over Me.mp3", "Taking Over Me", "Evanescence", 240, 320));

    var studio = Album("Fallen", "Evanescence",
        Named(@"E:\m\Ev\Fallen\05 Haunted.wma", "Haunted", "Evanescence", 186, 128));

    var listing = Listing(("Going Under", 240), ("Taking Over Me", 240), ("Haunted", 244));
    var gaps = MusicGapFill.Find([live, studio], null, f => f.Contains("Anywhere") ? listing : null);

    Check("a different version is found but doubted", gaps.Count == 1);
    Check("and is never filled in automatically", gaps.All(g => g.Obvious is null));
    Check("it is put to a person instead", gaps.All(g => g.NeedsAsking));
    Check("with the reason given",
        gaps.SelectMany(g => g.Candidates).All(c => c.Confidence == GapConfidence.Doubtful));
}
{
    // One track present, twenty in the listing. That is a record nobody ripped,
    // not nineteen gaps.
    var thin = Album("Greatest Hits", "Club Nouveau",
        Named(@"E:\m\CN\Greatest Hits\01 A.mp3", "A", "Club Nouveau", 120, 320));

    var elsewhere = Album("Other", "Club Nouveau",
        Named(@"E:\m\CN\Other\02 B.mp3", "B", "Club Nouveau", 130, 320));

    var listing = Listing([("A", 120), ("B", 130),
        .. Enumerable.Range(3, 18).Select(n => ($"Track {n}", 200.0))]);

    Check("a mostly-missing album does not claim gaps",
        MusicGapFill.Find([thin, elsewhere], null, f => f.Contains("Greatest") ? listing : null).Count == 0);
}
{
    // Two encodings of one recording are one choice, not two, and the better
    // file is the one offered.
    var album = Album("Record", "Band",
        Named(@"E:\m\Band\Record\01 A.mp3", "A", "Band", 120, 320),
        Named(@"E:\m\Band\Record\02 B.mp3", "B", "Band", 120, 320));

    var flac = Named(@"E:\m\Band\Other\05 Wanted.flac", "Wanted", "Band", 200, 900);
    var mp3 = Named(@"E:\m\Band\Other2\05 Wanted.mp3", "Wanted", "Band", 201, 192);
    var other = Album("Other", "Band", flac, mp3);

    var listing = Listing(("A", 120), ("B", 120), ("Wanted", 200));
    var gaps = MusicGapFill.Find([album, other], null, f => f.EndsWith("Record") ? listing : null);

    Check("two encodings of one recording are a single candidate",
        gaps.Count == 1 && gaps[0].Candidates.Count == 1);
    Check("holding both files", gaps[0].Candidates[0].Copies.Count == 2);
    Check("and the lossless one is what gets copied", gaps[0].Obvious?.Best == flac);
}
{
    // A compilation has no artist to check against, so a title and a length
    // that happen to line up are not enough to act on.
    var comp = Compilation("Soundtrack", "Various Artists",
        Named(@"E:\m\VA\Soundtrack\01 A.mp3", "A", "Someone", 120, 320),
        Named(@"E:\m\VA\Soundtrack\02 B.mp3", "B", "Someone Else", 120, 320));

    var other = Album("Record", "A Band",
        Named(@"E:\m\Band\Record\04 Wanted.mp3", "Wanted", "A Band", 200, 320));

    var listing = Listing(("A", 120), ("B", 120), ("Wanted", 200));
    var gaps = MusicGapFill.Find([comp, other], null, f => f.Contains("Soundtrack") ? listing : null);

    Check("a compilation gap is not filled on title and length alone",
        gaps.Count == 1 && gaps[0].Obvious is null);
}

Console.WriteLine("\n=== undoing encoding damage ===");
{
    // UTF-8 read as cp1252 and saved back that way. Both directions checked,
    // because running a repair over already-good text would destroy it.
    Check("Björk comes back", MusicHealth.Repair("BjÃ¶rk") == "Björk");
    // U+2019, the typographic apostrophe - not the ASCII one. Repair restores
    // what was written, it does not also normalise the punctuation.
    Check("a smart apostrophe comes back", MusicHealth.Repair("Donâ€™t") == "Don’t");
    Check("correct text is left alone", MusicHealth.Repair("Björk") is null);
    Check("plain ASCII is left alone", MusicHealth.Repair("Untitled") is null);
    Check("repairing twice changes nothing",
        MusicHealth.Repair(MusicHealth.Repair("BjÃ¶rk")!) is null);
    Check("Hasta Mañana is not damage", MusicHealth.Repair("Hasta Mañana") is null);
}

Console.WriteLine("\n=== bad tag text ===");
{
    Check("Unknown Artist is a placeholder", MusicHealth.Placeholder("Unknown Artist"));
    Check("Track 14 is a placeholder", MusicHealth.Placeholder("Track 14"));
    Check("[unknown] is a placeholder", MusicHealth.Placeholder("[unknown]"));
    Check("Unknown Mortal Orchestra is a band",
        !MusicHealth.Placeholder("Unknown Mortal Orchestra"));
    Check("Untitled Rebel Song is a song", !MusicHealth.Placeholder("Untitled Rebel Song"));
    Check("doubled spaces collapse", MusicHealth.Tidy("Technologic  Altar") == "Technologic Altar");

    var numbered = Track(@"E:\m\a\03 Song.mp3", "03 - Song", 200, 320);
    numbered.TrackNumber = 3;
    Check("a track number repeated in the title is fixable",
        MusicHealth.Examine(numbered).Any(f => f.Ailment == Ailment.NumberInTitle
                                            && f.Suggested == "Song"));

    var real = Track(@"E:\m\a\05 99 Problems.mp3", "99 Problems", 200, 320);
    real.TrackNumber = 5;
    Check("a title that starts with a different number is left alone",
        !MusicHealth.Examine(real).Any(f => f.Ailment == Ailment.NumberInTitle));

    var genre = Track(@"E:\m\a\01 X.mp3", "X", 200, 320);
    genre.Genre = new TagValue("(80)", TagSource.Id3v2, TextConfidence.Ascii);
    Check("ID3 genre 80 is Folk",
        MusicHealth.Examine(genre).Any(f => f.Ailment == Ailment.BadGenre && f.Suggested == "Folk"));

    var year = Track(@"E:\m\a\01 Y.mp3", "Y", 200, 320);
    year.Year = new TagValue("0000", TagSource.Id3v2, TextConfidence.Ascii);
    Check("a zero year is reported",
        MusicHealth.Examine(year).Any(f => f.Ailment == Ailment.BadYear));
}

Console.WriteLine("\n=== the naming pattern ===");
{
    var beatles = Named(@"E:\m\x.flac", "Sie Liebt Dich", "The Beatles", 140, 900);
    beatles.Album = new TagValue("Past Masters", TagSource.VorbisComment, TextConfidence.Declared);
    beatles.AlbumArtist = beatles.Artist;
    beatles.Year = new TagValue("1988", TagSource.VorbisComment, TextConfidence.Declared);
    beatles.TrackNumber = 9;

    var format = new NamingFormat(NamingFormat.Default);
    Check("a full track lays out as expected",
        format.Path(beatles) == @"The Beatles\Past Masters (1988)\09 Sie Liebt Dich.flac");

    beatles.Year = TagValue.Empty;
    Check("the year section vanishes when there is no year",
        format.Path(beatles) == @"The Beatles\Past Masters\09 Sie Liebt Dich.flac");

    beatles.DiscNumber = 1;
    Check("disc 1 is not written out",
        format.Path(beatles) == @"The Beatles\Past Masters\09 Sie Liebt Dich.flac");

    beatles.DiscNumber = 2;
    Check("disc 2 is",
        format.Path(beatles) == @"The Beatles\Past Masters\2-09 Sie Liebt Dich.flac");

    Check("The is ignored when filing under a letter",
        new NamingFormat("{initial}/{title}").Path(beatles) == @"B\Sie Liebt Dich.flac");

    Check("an example is produced for the settings screen",
        new NamingFormat(NamingFormat.ByInitial).Example().StartsWith(@"B\The Beatles\"));
}
{
    Check("a slash in a name becomes a dash", NamingFormat.Safe("AC/DC") == "AC-DC");
    Check("a colon keeps the spacing readable",
        NamingFormat.Safe("Vol. 1: Learning") == "Vol. 1 - Learning");
    Check("a question mark goes", NamingFormat.Safe("Whut? Thee Album") == "Whut Thee Album");
    Check("a trailing dot goes", NamingFormat.Safe("Album.") == "Album");
    Check("a DOS device name is escaped", NamingFormat.Safe("CON") == "_CON");
    Check("even with an extension on it", NamingFormat.Safe("NUL.mp3") == "_NUL.mp3");
    Check("a name that is only illegal characters comes back empty",
        NamingFormat.Safe("?") == "");
    Check("a very long name is trimmed to fit",
        NamingFormat.Safe(new string('x', 400)).Length == 200);
}
{
    Check("an unknown field is reported",
        new NamingFormat("{albumartist}/{nonsense}").Problems()
            .Any(p => p.Contains("nonsense")));
    Check("unpaired brackets are reported",
        new NamingFormat("{album}[ ({year})").Problems().Any(p => p.Contains("bracket")));
    Check("a pattern with no fields is reported",
        new NamingFormat("music").Problems().Count > 0);
    Check("a pattern that would collide every track is reported",
        new NamingFormat("{albumartist}/{album}").Problems()
            .Any(p => p.Contains("same filename")));
    Check("a good pattern has nothing wrong with it",
        new NamingFormat(NamingFormat.Default).Problems().Count == 0);
}

// Real files in a temp folder. Mocking the filesystem here would test the mock:
// the whole point of this class is that File.Move will not cross a volume and
// that a half-written copy must not take the original with it.
Console.WriteLine("\n=== carrying out a music plan ===");
{
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);
    var library = Path.Combine(root, "Music");

    try
    {
        var source = Make(library, @"Unsorted\track.mp3", "audio");
        var target = Path.Combine(library, "Band", "Album", "01 Song.mp3");

        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
        var executor = new MusicExecutor(history);
        var move = new MusicAction(Tag(source), MusicActionKind.Move, target, null);

        Check("nothing collides in a clean plan", executor.FindConflicts([move]).Count == 0);

        var result = executor.Execute([move], library);

        Check("the file moved", result.Moved == 1 && File.Exists(target) && !File.Exists(source));
        Check("its folder is reported as empty", result.EmptyFolders.Count == 1);
        Check("but the folder is still there", Directory.Exists(Path.GetDirectoryName(source)!));

        // And back again, through the same service the film side uses.
        var revert = new RevertService(history);
        var undone = revert.Revert(revert.Examine(history.Read()));

        Check("reverting puts it back", undone.Reverted == 1
            && File.Exists(source) && !File.Exists(target));
    }
    finally { Clean(root); }
}
{
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);
    var library = Path.Combine(root, "Music");

    try
    {
        // A copy: one recording wanted by two albums at once.
        var source = Make(library, @"Soundtrack\05 Song.mp3", "audio");
        var target = Path.Combine(library, "Band", "Album", "03 Song.mp3");

        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
        var executor = new MusicExecutor(history);

        var result = executor.Execute(
            [new MusicAction(Tag(source), MusicActionKind.Copy, target, null)], library);

        Check("the copy is made", result.Copied == 1 && File.Exists(target));
        Check("and the original is still there", File.Exists(source));

        var revert = new RevertService(history);
        var candidates = revert.Examine(history.Read());

        Check("a copy is revertible even though the original is in place",
            candidates.All(c => c.CanRevert));

        revert.Revert(candidates);
        Check("undoing a copy removes only the copy", !File.Exists(target) && File.Exists(source));
    }
    finally { Clean(root); }
}
{
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);
    var library = Path.Combine(root, "Music");

    try
    {
        // The dangerous one: the original has gone, so this copy is the last of
        // the recording and deleting it would lose the audio outright.
        var source = Make(library, @"Soundtrack\05 Song.mp3", "audio");
        var target = Path.Combine(library, "Band", "Album", "03 Song.mp3");

        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
        new MusicExecutor(history).Execute(
            [new MusicAction(Tag(source), MusicActionKind.Copy, target, null)], library);

        File.Delete(source);

        var revert = new RevertService(history);
        var result = revert.Revert(revert.Examine(history.Read()));

        Check("the last copy of a recording is not deleted by an undo",
            File.Exists(target) && result.Reverted == 0 && result.Skipped == 1);
    }
    finally { Clean(root); }
}
{
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);
    var library = Path.Combine(root, "Music");

    try
    {
        // Redundant files are set aside, never deleted - a duplicate call that
        // agrees with the audio 95% of the time has not earned a delete.
        var extra = Make(library, @"Band\Album\01 Song (2).mp3", "audio");

        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
        var result = new MusicExecutor(history).Execute(
            [new MusicAction(Tag(extra), MusicActionKind.Redundant, null, null)], library);

        var bin = Path.Combine(root, MusicExecutor.QuarantineFolder);

        Check("a redundant file is set aside", result.Quarantined == 1 && !File.Exists(extra));
        Check("into a folder outside the library", Directory.Exists(bin));
        Check("keeping enough path to tell two copies apart",
            File.Exists(Path.Combine(bin, "Band", "Album", "01 Song (2).mp3")));

        var revert = new RevertService(history);
        revert.Revert(revert.Examine(history.Read()));
        Check("and it comes back", File.Exists(extra));
    }
    finally { Clean(root); }
}
{
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);
    var library = Path.Combine(root, "Music");

    try
    {
        var a = Make(library, @"One\track.mp3", "first");
        var b = Make(library, @"Two\track.mp3", "second");
        var occupied = Make(library, @"Band\Album\01 Song.mp3", "already here");
        var target = Path.Combine(library, "Band", "Album", "01 Song.mp3");

        var executor = new MusicExecutor();
        var conflicts = executor.FindConflicts([
            new MusicAction(Tag(a), MusicActionKind.Move, target, null),
            new MusicAction(Tag(b), MusicActionKind.Move, target, null)
        ]);

        Check("both kinds of collision are found", conflicts.Count == 2);
        Check("and each says which it is",
            conflicts.Any(c => c.Reason!.Contains("already there"))
            && conflicts.Any(c => c.Reason!.Contains("also wants")));

        conflicts[0].Resolution = ConflictResolution.Skip;
        conflicts[1].Resolution = ConflictResolution.Skip;

        var result = executor.Execute([
            new MusicAction(Tag(a), MusicActionKind.Move, target, null),
            new MusicAction(Tag(b), MusicActionKind.Move, target, null)
        ], library, conflicts);

        Check("skipping leaves everything where it was",
            result.Skipped == 2 && File.Exists(a) && File.Exists(b)
            && File.ReadAllText(occupied) == "already here");
    }
    finally { Clean(root); }
}

// Real audio, written by ffmpeg and read back by our own readers. A mocked
// tag writer would prove only that the mock agrees with itself, and the whole
// question here is whether a file survives being rewritten.
Console.WriteLine("\n=== writing tags back ===");
if (!Fingerprint.Available())
    Console.WriteLine("   skipped - ffmpeg is not on the path");
else
{
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    try
    {
        foreach (var extension in new[] { ".mp3", ".flac", ".m4a", ".ogg", ".wma" })
        {
            var path = Path.Combine(root, "tone" + extension);

            if (!Tone(path)) { Console.WriteLine($"   skipped {extension} - ffmpeg would not make one"); continue; }

            var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
            var writer = new MusicTagWriter(history);

            var before = MusicFileReader.Read(path);
            var original = new FileInfo(path).Length;

            // Captured as a string, not held as a reference: Write updates the
            // tags it is handed so the caller's copy stays true to the file, so
            // reading this back off `before` afterwards would compare the new
            // value against itself and pass no matter what the undo did.
            var wasTitled = before.Title.Text;

            var written = writer.Write(before, new TagEdit(
                Title: "Sie Liebt Dich", Artist: "The Beatles", Album: "Past Masters",
                AlbumArtist: "The Beatles", Year: "1988", TrackNumber: 9, DiscNumber: 2));

            if (!written.Written) Console.WriteLine($"      ffmpeg said: {written.Error}");
            Check($"{extension} tags write", written.Written);

            var after = MusicFileReader.Read(path);
            Check($"{extension} title comes back", after.Title.Text == "Sie Liebt Dich");
            Check($"{extension} album comes back", after.Album.Text == "Past Masters");
            Check($"{extension} track number comes back", after.TrackNumber == 9);
            Check($"{extension} disc number comes back", after.DiscNumber == 2);

            // -c copy, so the audio is passed through rather than re-encoded.
            Check($"{extension} the audio is not re-encoded",
                Math.Abs(new FileInfo(path).Length - original) < original / 2);

            var revert = new RevertService(history) { Retag = writer.Undo };
            var candidates = revert.Examine(history.Read());

            Check($"{extension} the retag is revertible", candidates.All(c => c.CanRevert));

            revert.Revert(candidates);
            Check($"{extension} the old tags come back",
                MusicFileReader.Read(path).Title.Text == wasTitled);
        }

        // A retag recorded before the old values were kept must not offer an
        // undo that silently does nothing.
        var bare = new RenameHistory(Path.Combine(root, "bare.jsonl"));
        var file = Path.Combine(root, "tone.mp3");
        bare.Add(HistoryAction.Retag, file, file, null, null);

        var noValues = new RevertService(bare) { Retag = _ => null }.Examine(bare.Read());
        Check("a retag with no recorded values is not offered as revertible",
            noValues.All(c => !c.CanRevert));
    }
    finally { Clean(root); }
}

Console.WriteLine("\n=== books, and things that are not books ===");
{
    // A format that only holds books settles it on its own.
    Check("an .m4b is a book",
        Audiobooks.Judge([Track(@"E:\b\Neuromancer\book.m4b", "Neuromancer", 30000, 64)], @"E:\b\Neuromancer")
            .Confidence == SpokenWord.Yes);

    var chapters = Enumerable.Range(1, 12)
        .Select(n => Track($@"E:\b\Dune\{n:00} Chapter {n}.mp3", $"Chapter {n}", 1800, 64))
        .ToList();

    foreach (var c in chapters)
        c.Genre = new TagValue("Audiobook", TagSource.Id3v2, TextConfidence.Ascii);

    Check("chapters plus an audiobook genre is a book",
        Audiobooks.Judge(chapters, @"E:\b\Dune").Confidence == SpokenWord.Yes);

    Check("and it is found and named",
        Audiobooks.Find(chapters) is [{ Title: "Dune", Files.Count: 12 }]);

    // Every one of these was reported as a book by the first version of this,
    // in a library holding no books at all.
    var violin = Enumerable.Range(1, 20)
        .Select(n => Track($@"E:\m\The Red Violin\{n:00} Chaconne Part {n}.mp3",
                           $"Chaconne: Part {n}", 200, 320)).ToList();

    Check("a film score with movements is not a book",
        Audiobooks.Judge(violin, @"E:\m\The Red Violin").Confidence == SpokenWord.No);

    var phantom = Enumerable.Range(1, 14)
        .Select(n => Track($@"E:\m\Phantom [Original Cast] Disc 1\{n:00} Song.mp3",
                           $"Song {n}", 240, 320)).ToList();

    Check("a cast recording on Disc 1 is not a book",
        Audiobooks.Judge(phantom, @"E:\m\Phantom [Original Cast] Disc 1").Confidence == SpokenWord.No);

    var nickelback = Enumerable.Range(1, 5)
        .Select(n => Track($@"E:\m\Nickelback\Leader of Men\Track {n:00}.mp3",
                           $"Track {n:00}", 220, 320)).ToList();

    Check("files called Track 01 are not chapters",
        Audiobooks.Judge(nickelback, @"E:\m\Nickelback\Leader of Men").Confidence == SpokenWord.No);

    // Records do not number themselves "3 of 12".
    var parts = Enumerable.Range(1, 12)
        .Select(n => Track($@"E:\b\Ulysses\{n:00}.mp3", $"Part {n} of 12", 2400, 64)).ToList();

    Check("Part 3 of 12 does count as a chapter",
        Audiobooks.Judge(parts, @"E:\b\Ulysses").Confidence != SpokenWord.No);
}

Console.WriteLine("\n=== moving finished files into a library ===");
{
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);
    var staging = Path.Combine(root, "Staging");
    var library = Path.Combine(root, "Library");

    try
    {
        var a = Make(staging, @"Band\Album\01 One.mp3", "one");
        var b = Make(staging, @"Band\Album\02 Two.mp3", "two");
        var outside = Make(root, @"Elsewhere\03 Three.mp3", "three");

        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
        var result = CompletedMover.Move([a, b, outside], staging, library, history);

        Check("finished files move", result.Moved == 2);
        Check("keeping the layout below the staging folder",
            File.Exists(Path.Combine(library, "Band", "Album", "01 One.mp3")));
        Check("a file outside the staging folder is left alone",
            result.Skipped == 1 && File.Exists(outside));
        Check("the emptied folder is reported", result.EmptyFolders.Count > 0);
        Check("and not removed", Directory.Exists(Path.GetDirectoryName(a)!));

        // A name already in the library is a real question - the same episode at
        // a better quality, most often - not something to answer silently.
        var again = Make(staging, @"Band\Album\01 One.mp3", "a different rip");
        var second = CompletedMover.Move([again], staging, library, history);

        Check("a name already in the library is left for a person",
            second.Skipped == 1 && second.Moved == 0 && File.Exists(again));
        Check("and the file already there is untouched",
            File.ReadAllText(Path.Combine(library, "Band", "Album", "01 One.mp3")) == "one");

        Check("no destination is refused rather than guessed",
            CompletedMover.Move([again], staging, "", history).Errors.Count == 1);

        var revert = new RevertService(history);
        revert.Revert(revert.Examine(history.Read()));
        Check("moving into the library undoes", File.Exists(a) && File.Exists(b));
    }
    finally { Clean(root); }
}

Console.WriteLine("\n=== loudness ===");
{
    // A real ebur128 summary block, as ffmpeg prints it.
    const string report = """
[Parsed_ebur128_0 @ 000001] Summary:

  Integrated loudness:
    I:         -23.4 LUFS
    Threshold: -33.6 LUFS

  Loudness range:
    LRA:         5.2 LU

  True peak:
    Peak:       -1.5 dBFS
""";

    var measured = ReplayGain.Parse(report);

    Check("the integrated loudness is read", measured is not null
        && Math.Abs(measured.Lufs - -23.4) < 0.01);

    // -18 is the ReplayGain 2.0 reference, so a track at -23.4 wants turning up.
    Check("a quiet track gets positive gain", measured!.Gain > 0);
    Check("the gain is the distance from the reference",
        Math.Abs(measured.Gain - 5.4) < 0.01);
    Check("the gain is tagged in dB", measured.GainTag == "5.40 dB");

    // -1.5 dBFS is a fraction of full scale, not a fraction of one.
    Check("the true peak is converted out of decibels",
        Math.Abs(measured.Peak - 0.841) < 0.005);

    Check("a report with no summary yields nothing",
        ReplayGain.Parse("no loudness here") is null);

    // Absent peak is 1.0, not 0: a zero would have players turn a track down
    // for no reason.
    Check("a missing peak is assumed to be full scale",
        ReplayGain.Parse("Integrated loudness:\n    I:  -10.0 LUFS")?.Peak == 1.0);
}
{
    // Album gain has to average energy, not decibels. Ten seconds at -10 beside
    // ten seconds at -30 is far closer to -13 than to the -20 that averaging
    // the numbers would give - one loud track dominates, which is the point.
    var album = ReplayGain.Album([
        (new Loudness(-10, 0.9), 10.0),
        (new Loudness(-30, 0.2), 10.0)]);

    Check("album loudness is averaged in the linear domain",
        album is not null && album.Lufs > -14 && album.Lufs < -12);

    Check("the album peak is the loudest track's", album!.Peak == 0.9);

    Check("a longer track counts for more",
        ReplayGain.Album([(new Loudness(-10, 0.5), 1.0), (new Loudness(-30, 0.5), 100.0)])!.Lufs
        < album.Lufs);

    Check("nothing measured gives no album figure", ReplayGain.Album([]) is null);
}

Console.WriteLine("\n=== AcoustID replies ===");
{
    // The shape the service actually returns, trimmed.
    const string reply = """
    {"status":"ok","results":[
      {"id":"9ff43b6a","score":0.98,"recordings":[
        {"id":"cd1b8f0e","title":"Sie Liebt Dich",
         "artists":[{"id":"b10bbb","name":"The Beatles"}],
         "releasegroups":[{"id":"rg-1","title":"Past Masters"}]}]},
      {"id":"11111111","score":0.42,"recordings":[
        {"id":"cd222222","title":"Something Else",
         "artists":[{"id":"x","name":"Someone"},{"id":"y","name":"Another"}]}]}]}
    """;

    var matches = AcoustId.Parse(reply);

    Check("both results are read", matches.Count == 2);
    Check("the best score comes first", matches[0].Score > matches[1].Score);
    Check("the title is read", matches[0].Title == "Sie Liebt Dich");
    Check("the artist is read", matches[0].Artist == "The Beatles");
    Check("the album comes from the release group", matches[0].Album == "Past Masters");
    Check("the MusicBrainz recording id is kept", matches[0].RecordingId == "cd1b8f0e");
    Check("several artists are joined", matches[1].Artist == "Someone, Another");
    Check("a recording with no release group has no album", matches[1].Album is null);

    // A fingerprint the service knows but nobody has ever named. Real, and no
    // use - it must not read as a match.
    Check("a result with no recordings names nothing",
        AcoustId.Parse("""{"status":"ok","results":[{"id":"a","score":0.9}]}""")
            is [{ Title: null, Artist: null }]);

    Check("an error reply yields nothing",
        AcoustId.Parse("""{"status":"error","error":{"message":"invalid API key"}}""").Count == 0);
    Check("no results yields nothing",
        AcoustId.Parse("""{"status":"ok","results":[]}""").Count == 0);
    Check("malformed JSON yields nothing rather than throwing",
        AcoustId.Parse("not json at all").Count == 0);
    Check("an empty key never reaches the network",
        new AcoustId().LookupAsync("fp", 200, "").GetAwaiter().GetResult().Count == 0);
}

Console.WriteLine("\n=== what a volume will take ===");
{
    // There is no FAT32 volume on this machine to check against, so the limits
    // come from the specifications and the decision is tested on its own.
    var fat = Volumes.Describe("FAT32");
    var exfat = Volumes.Describe("exFAT");
    var ntfs = Volumes.Describe("NTFS");

    Check("FAT32 stops just under 4 GB", fat.MaxFileBytes == (1L << 32) - 1);
    Check("a 3 GB file fits on FAT32", !fat.Rejects(3L * 1024 * 1024 * 1024));
    Check("a 5 GB file does not", fat.Rejects(5L * 1024 * 1024 * 1024));
    Check("exFAT lifted the limit", !exfat.Rejects(50L * 1024 * 1024 * 1024));
    Check("NTFS takes anything here", !ntfs.Rejects(50L * 1024 * 1024 * 1024));

    // Timestamps land on a two-second boundary on both FAT variants, which is
    // why nothing compares them to decide whether a copy worked.
    Check("FAT32 does not keep exact times", !fat.KeepsExactTimes);
    Check("exFAT does not either", !exfat.KeepsExactTimes);
    Check("NTFS does", ntfs.KeepsExactTimes);

    // An unnamed filesystem - a network share, most often - is assumed
    // generous. Refusing a move on a guess would be worse than the error the
    // filesystem gives when it genuinely cannot take a file.
    Check("an unknown filesystem is not refused",
        !Volumes.Describe("something new").Rejects(50L * 1024 * 1024 * 1024));

    Check("a path on this machine reports a real filesystem",
        Volumes.For(Path.GetTempPath()).FileSystem.Length > 0);

    // The refusal names the problem and stops there.
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);

    try
    {
        var file = Make(root, "big.mkv", "pretend");
        Check("nothing is refused when the destination is roomy",
            Volumes.Refuses([file], root) is null);

        // The path that matters is where a file would land, not where it is:
        // a short staging folder and a deep library is exactly how this bites.
        var deep = Path.Combine(root, new string('x', 200), new string('y', 200));

        Check("a path too long for the destination is refused before anything moves",
            Volumes.Refuses([file], deep, Volumes.Describe("FAT32"), root)?
                .Contains("longer than") == true);

        Check("and nothing is written when it is refused", !Directory.Exists(deep));

        Check("the same path is fine on a volume that allows it",
            Volumes.Refuses([file], deep, Volumes.Describe("NTFS"), root) is null);

        Check("a name longer than any filesystem allows is caught",
            VolumeLimits.MaxComponentLength == 255);
    }
    finally { Clean(root); }
}

Console.WriteLine("\n=== formats the scan will find ===");
{
    // The .m4b bug: Read() knew the format, IsAudio did not, so the scan never
    // offered it a file. Seven audiobooks were invisible in a real library.
    Check(".m4b is found", MusicFileReader.IsAudio(@"x\book.m4b"));
    Check(".m4b can be read", MusicFileReader.CanReadTags(@"x\book.m4b"));

    foreach (var ext in Audiobooks.BookFormats)
        Check($"{ext} is found", MusicFileReader.IsAudio("x" + ext));

    // Every format with a reader must also be findable, or the reader is dead
    // code. This is the check that would have caught .m4b.
    foreach (var ext in MusicFileReader.Readable)
        Check($"{ext} is both readable and findable", MusicFileReader.IsAudio("x" + ext));

    // Older formats. No reader for these yet - they are listed so they are
    // found and reported rather than silently absent.
    foreach (var ext in new[] { ".asf", ".aiff", ".mp2", ".mpc", ".ra", ".tta", ".dsf" })
        Check($"{ext} is found", MusicFileReader.IsAudio("x" + ext));

    Check(".asf reads as ASF, like .wma",
        MusicFileReader.CanReadTags(@"x\y.asf"));

    Check("an unreadable format says so rather than looking untagged",
        MusicFileReader.Read(Path.Combine(Path.GetTempPath(), "nope.mpc")).Error is not null);

    Check("a video file is not audio", !MusicFileReader.IsAudio(@"x\film.mkv"));
    Check("nor is a picture", !MusicFileReader.IsAudio(@"x\cover.jpg"));
}

Console.WriteLine("\n=== the odd one out in a set ===");
{
    // The real case: seven audiobooks, one of which carries an extra
    // qualifier and so files away from its siblings.
    var books = new[]
    {
        "Philosopher's Stone (Full-Cast Edition) (Unabridged)",
        "Chamber of Secrets (Full-Cast Edition)",
        "Prisoner of Azkaban (Full-Cast Edition)",
        "Goblet of Fire (Full-Cast Edition)",
        "Order of the Phoenix (Full-Cast Edition)"
    }.Select((title, n) =>
    {
        var t = Track($@"E:\b\{n}.m4b", title, 30000, 64);
        t.Album = new TagValue(title, TagSource.Mp4, TextConfidence.Declared);
        t.Artist = new TagValue("J.K. Rowling", TagSource.Mp4, TextConfidence.Declared);
        return t;
    }).ToList();

    var found = MusicConsistency.Examine([books]);
    var odd = found.Where(f => f.Kind == Disagreement.OddQualifier).ToList();

    Check("the one extra qualifier is found", odd.Count == 1);
    Check("and only that one is trimmed",
        odd[0].Suggested == "Philosopher's Stone (Full-Cast Edition)");
    Check("the qualifier the whole set shares is left alone",
        !found.Any(f => f.Suggested?.Contains("Full-Cast") == false));

    // Stripping an inner qualifier must not take the outer ones with it.
    Check("removing one qualifier keeps the others",
        MusicConsistency.Strip("A Title (Full-Cast Edition) (Unabridged)", "Full-Cast Edition")
        == "A Title (Unabridged)");
    Check("removing the outer one keeps the inner",
        MusicConsistency.Strip("A Title (Full-Cast Edition) (Unabridged)", "Unabridged")
        == "A Title (Full-Cast Edition)");
}
{
    // Capitalisation drift within one artist's work.
    var tracks = Enumerable.Range(1, 10)
        .Select(n => Named($@"E:\m\{n}.mp3", $"Song {n}", n > 8 ? "DAFT PUNK" : "Daft Punk", 200, 320))
        .ToList();

    var spelling = MusicConsistency.Examine([tracks])
        .Where(f => f.Kind == Disagreement.Spelling).ToList();

    Check("a shouted spelling is reported against the majority", spelling.Count == 1);
    Check("with the majority as the suggestion", spelling[0].Suggested == "Daft Punk");

    // Two genuinely different artists on a compilation disagree about nothing.
    var comp = new[]
    {
        Named(@"E:\m\1.mp3", "A", "Slayer", 200, 320),
        Named(@"E:\m\2.mp3", "B", "Disturbed", 200, 320),
        Named(@"E:\m\3.mp3", "C", "Powerman 5000", 200, 320)
    };

    Check("different artists are not a disagreement",
        !MusicConsistency.Examine([comp]).Any(f => f.Kind == Disagreement.Spelling));
}
{
    // A field that should be uniform and is not - but only where there is a
    // clear majority to prefer.
    var album = Enumerable.Range(1, 5)
        .Select(n => Track($@"E:\m\a\{n}.mp3", $"Song {n}", 200, 320))
        .ToList();

    foreach (var t in album)
        t.Year = new TagValue("2002", TagSource.Id3v2, TextConfidence.Ascii);

    album[4].Year = new TagValue("2000", TagSource.Id3v2, TextConfidence.Ascii);

    var years = MusicConsistency.Examine([album])
        .Where(f => f.Kind == Disagreement.Year).ToList();

    Check("one track with the wrong year is found", years.Count == 1);
    Check("and the album's year is suggested", years[0].Suggested == "2002");

    // An even split is a question, not a correction.
    var split = Enumerable.Range(1, 4)
        .Select(n => Track($@"E:\m\b\{n}.mp3", $"Song {n}", 200, 320))
        .ToList();

    for (var i = 0; i < 4; i++)
        split[i].Year = new TagValue(i < 2 ? "2001" : "2002", TagSource.Id3v2, TextConfidence.Ascii);

    Check("an even split is not corrected either way",
        !MusicConsistency.Examine([split]).Any(f => f.Kind == Disagreement.Year));
}
{
    // Kodi splits an album in two when some tracks name an album artist and
    // others do not, so it is worth reporting even though nothing is "wrong".
    var album = Enumerable.Range(1, 4)
        .Select(n => Named($@"E:\m\c\{n}.mp3", $"Song {n}", "Band", 200, 320))
        .ToList();

    foreach (var t in album.Take(3))
        t.AlbumArtist = new TagValue("Band", TagSource.Id3v2, TextConfidence.Ascii);

    var missing = MusicConsistency.Examine([album])
        .Where(f => f.Kind == Disagreement.MissingAlbumArtist).ToList();

    Check("a partly-set album artist is reported", missing.Count == 1);
    Check("naming the one the rest use", missing[0].Suggested == "Band");
}

Console.WriteLine("\n=== what the audio says it is ===");
{
    // A real MusicBrainz recording reply, trimmed to the parts that matter.
    const string reply = """
    {"id":"cd1b8f0e","title":"Sie Liebt Dich","length":142000,
     "artist-credit":[{"name":"The Beatles","joinphrase":"",
                       "artist":{"id":"b10bbb","name":"The Beatles"}}],
     "releases":[
       {"id":"rel-1","title":"Past Masters, Volume One","date":"1988-03-07",
        "artist-credit":[{"name":"The Beatles","joinphrase":""}],
        "media":[{"position":1,"track-count":18,"track":[{"position":9}]}]},
       {"id":"rel-2","title":"The Beatles Collection","date":"1994",
        "media":[{"position":2,"track-count":14,"track":[{"position":1}]}]}]}
    """;

    var recording = MusicBrainz.ParseRecording(reply);

    Check("the recording is read", recording is not null);
    Check("its title comes from the service", recording!.Title == "Sie Liebt Dich");
    Check("and its artist", recording.Artist == "The Beatles");
    Check("the length is seconds, not milliseconds",
        recording.Seconds is not null && Math.Abs(recording.Seconds.Value - 142) < 0.5);
    Check("both releases are listed", recording.Releases.Count == 2);

    // The track number belongs to the release, not the recording - which is
    // exactly why an album and a soundtrack disagree about it.
    Check("the track number comes off the release", recording.Releases[0].TrackNumber == 9);
    Check("and a different release gives a different number",
        recording.Releases[1].TrackNumber == 1);
    Check("the disc number comes off the medium", recording.Releases[1].DiscNumber == 2);
    Check("the year is four digits of the date", recording.Releases[0].Year == "1988");

    Check("a reply that is not a recording yields nothing",
        MusicBrainz.ParseRecording("""{"error":"Not Found"}""") is null);
    Check("malformed JSON yields nothing rather than throwing",
        MusicBrainz.ParseRecording("{{{") is null);
}
{
    // A credit with a joining phrase must survive whole. Reducing it to the
    // first artist throws away half of who made the record.
    const string collab = """
    {"id":"x","title":"Numb / Encore",
     "artist-credit":[{"name":"Jay-Z","joinphrase":" & ",
                       "artist":{"id":"a1","name":"JAY-Z"}},
                      {"name":"Linkin Park","joinphrase":"",
                       "artist":{"id":"a2","name":"Linkin Park"}}],
     "releases":[]}
    """;

    Check("a joint credit is kept whole",
        MusicBrainz.ParseRecording(collab)?.Artist == "Jay-Z & Linkin Park");
}

Console.WriteLine("\n=== identifiers are checked before they are believed ===");
{
    Check("a dashed UUID is an id",
        Identifier.MusicBrainz("cd1b8f0e-1111-2222-3333-444455556666") is not null);
    Check("an undashed one is too",
        Identifier.MusicBrainz("cd1b8f0e111122223333444455556666") is not null);

    // The exact damage found in 231 files: every TXXX value written as its own
    // field name shifted one character.
    Check("\"usicBrainz Album Id\" is not an id",
        Identifier.MusicBrainz("usicBrainz Album Id") is null);
    Check("nor is \"CRIP\"", Identifier.MusicBrainz("CRIP") is null);
    Check("nor is empty", Identifier.MusicBrainz("  ") is null);
    Check("nor is a title", Identifier.MusicBrainz("Hunting High and Low") is null);

    // Rejecting a bad value must not discard a good one already held.
    Check("a good id survives a bad one after it",
        Identifier.Better("cd1b8f0e-1111-2222-3333-444455556666", "rubbish")
        == "cd1b8f0e-1111-2222-3333-444455556666");
    Check("and a good one replaces nothing",
        Identifier.Better(null, "cd1b8f0e-1111-2222-3333-444455556666") is not null);
    Check("two bad ones leave nothing", Identifier.Better(null, "rubbish") is null);
}

Console.WriteLine("\n=== only what was approved gets written ===");
{
    var album = Enumerable.Range(1, 4)
        .Select(n => Named($@"E:\m\a\{n}.mp3", $"Song {n}", "Band", 200, 320))
        .ToList();

    foreach (var t in album)
    {
        t.Album = new TagValue("Record", TagSource.Id3v2, TextConfidence.Ascii);
        t.Year = new TagValue("2001", TagSource.Id3v2, TextConfidence.Ascii);
        t.Genre = new TagValue("Rock", TagSource.Id3v2, TextConfidence.Ascii);
    }

    // One track disagreeing about two things at once.
    album[3].Year = new TagValue("1999", TagSource.Id3v2, TextConfidence.Ascii);
    album[3].Genre = new TagValue("Pop", TagSource.Id3v2, TextConfidence.Ascii);

    var found = MusicConsistency.Examine([album]);
    Check("both disagreements are found", found.Count(f => f.Fixable) == 2);

    // Two findings, one file: one edit, or the file is rewritten twice and
    // leaves two undo entries for one intention.
    var edits = MusicConsistency.Edits(found);
    Check("two findings on one file make one edit", edits.Count == 1);
    Check("carrying both changes",
        edits.Values.Single().Year == "2001" && edits.Values.Single().Genre == "Rock");
    Check("and touching nothing else", edits.Values.Single().Album is null);

    // The whole point of the review screen: nothing ticked, nothing written.
    Check("approving nothing writes nothing", MusicConsistency.Edits([]).Count == 0);

    var oneOnly = MusicConsistency.Edits(found.Where(f => f.Field == "Year"));
    Check("approving one field writes only that field",
        oneOnly.Values.Single().Year == "2001" && oneOnly.Values.Single().Genre is null);

    // A field nothing knows how to write is dropped rather than guessed at.
    var unknown = new Inconsistency(Disagreement.Spelling, "Comment",
        [album[0]], "a", "b", "");

    Check("an unwritable field produces no edit", MusicConsistency.Edits([unknown]).Count == 0);

    // A missing album artist has an empty current value; it still writes.
    var half = Enumerable.Range(1, 4)
        .Select(n => Named($@"E:\m\d\{n}.mp3", $"Song {n}", "Band", 200, 320))
        .ToList();

    foreach (var t in half.Take(3))
        t.AlbumArtist = new TagValue("Band", TagSource.Id3v2, TextConfidence.Ascii);

    var filled = MusicConsistency.Edits(MusicConsistency.Examine([half]));
    Check("filling in a missing album artist is a real edit",
        filled.Count == 1 && filled.Values.Single().AlbumArtist == "Band");
}

Console.WriteLine("\n=== how long is left ===");
{
    var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // Half a second a file, 100 files.
    var e = new Estimate(100);
    for (var n = 1; n <= 20; n++) e.Report(n, start.AddSeconds(n * 0.5));

    Check("progress is a fraction", Math.Abs(e.Fraction - 0.2) < 0.001);

    var left = e.Remaining(start.AddSeconds(10));
    Check("80 files at half a second is about 40 seconds",
        left is not null && Math.Abs(left.Value.TotalSeconds - 40) < 2);

    // The first few files of any batch are unrepresentative - a cold cache, a
    // spun-down disk, ffmpeg paying its own startup - so no figure is offered
    // until there is enough to say.
    var early = new Estimate(1000);
    for (var n = 1; n <= 3; n++) early.Report(n, start.AddSeconds(n));

    Check("too early to say means null, not a wild guess", early.Remaining(start.AddSeconds(3)) is null);
    Check("and it says the count instead of a time",
        early.Describe(start.AddSeconds(3)) == "3 of 1,000");

    // Never counts past the end, however the caller reports.
    var over = new Estimate(10);
    over.Report(50, start);
    Check("done never exceeds the total", over.Done == 10);
    Check("and finished means zero left", over.Remaining(start) == TimeSpan.Zero);
    Check("described as done", over.Describe(start) == "done");

    Check("nothing to do is not a division by zero",
        new Estimate(0).Remaining(start) == TimeSpan.Zero);

    // Vague where being precise would be a lie: "3 minutes 47 seconds" stops
    // being true immediately, "about 4 minutes" is true for a while.
    var slow = new Estimate(1000);
    for (var n = 1; n <= 20; n++) slow.Report(n, start.AddSeconds(n * 0.5));

    var text = slow.Describe(start.AddSeconds(10));
    Check("a long wait is rounded to minutes", text.Contains("minutes left"));
    Check("and still says where it is", text.StartsWith("20 of 1,000"));

    var nearly = new Estimate(100);
    for (var n = 1; n <= 99; n++) nearly.Report(n, start.AddSeconds(n * 0.1));
    Check("a few seconds is said as a few seconds",
        nearly.Describe(start.AddSeconds(9.9))!.Contains("few seconds"));

    // A rate that changes partway is followed, not averaged over the whole run.
    var uneven = new Estimate(200);
    for (var n = 1; n <= 100; n++) uneven.Report(n, start.AddSeconds(n * 0.01));
    var at = start.AddSeconds(1);
    for (var n = 101; n <= 120; n++) uneven.Report(n, at.AddSeconds((n - 100) * 1.0));

    var after = uneven.Remaining(at.AddSeconds(20));
    Check("a slowdown is believed rather than averaged away",
        after is not null && after.Value.TotalSeconds > 40);
}

Console.WriteLine("\n=== was the remux truncated ===");
{
    // The Roses (2025), measured. Container 6385.312s, because two Spanish
    // audio tracks run sixty seconds past a 6321.333s picture. Dropping them
    // makes the file exactly sixty seconds shorter without losing a frame, and
    // the old flat check called that truncation and threw the remux away.
    MediaInfo Info(string path, double seconds) =>
        new() { Path = path, DurationSeconds = seconds, Streams = [] };

    MediaStream Stream(StreamKind kind, double seconds, int index) => new()
    {
        Index = index, Kind = kind, Codec = kind == StreamKind.Video ? "hevc" : "eac3",
        Language = "eng", Seconds = seconds
    };

    var source = Info(@"x.mp4", 6385.312);

    var kept = new List<MediaStream>
    {
        Stream(StreamKind.Video, 6321.333, 0),
        Stream(StreamKind.Audio, 6325.280, 1),
        Stream(StreamKind.Audio, 6325.312, 3)
    };

    var output = Info(@"y.mkv", 6325.312);

    Check("dropping the longest track is not truncation",
        DurationCheck.Failed(source, output, kept) is null);

    Check("the expected length is the longest track kept",
        Math.Abs(DurationCheck.Expected(source, kept) - 6325.312) < 0.01);

    // Real truncation still has to be caught, and it is the failure that
    // matters most: the picture plays and the last ten minutes are gone.
    var cut = Info(@"y.mkv", 5725.312);
    Check("losing ten minutes is still caught",
        DurationCheck.Failed(source, cut, kept) is not null);

    var barely = Info(@"y.mkv", 6325.312 - 30);
    Check("losing half a minute is caught too",
        DurationCheck.Failed(source, barely, kept) is not null);

    var rounding = Info(@"y.mkv", 6325.312 - 1.5);
    Check("container rounding is not", DurationCheck.Failed(source, rounding, kept) is null);

    // Longer than expected is not truncation. Muxers pad to a whole frame, and
    // refusing on that would reject good files.
    var longer = Info(@"y.mkv", 6400);
    Check("a longer output is not a truncation", DurationCheck.Failed(source, longer, kept) is null);

    // MKV often reports no per-stream duration. There is then nothing better
    // than the container, so the tolerance widens rather than guessing.
    var silent = kept.Select(s => new MediaStream
    {
        Index = s.Index, Kind = s.Kind, Codec = s.Codec, Language = s.Language
    }).ToList();

    Check("with no stream lengths the container is the only guide",
        Math.Abs(DurationCheck.Expected(source, silent) - 6385.312) < 0.01);
    Check("and a small drift is tolerated",
        DurationCheck.Failed(source, Info("y", 6385.312 - 60), silent) is null);
    Check("while a large one is not",
        DurationCheck.Failed(source, Info("y", 4000), silent) is not null);

    // A stream claiming to be longer than its own container is a broken
    // header, not a promise the output has to keep.
    var lying = new List<MediaStream> { Stream(StreamKind.Audio, 99999, 0) };
    Check("a stream longer than its container is not believed",
        Math.Abs(DurationCheck.Expected(source, lying) - 6385.312) < 0.01);
    // The second failure of the same file. Cover art is one JPEG that ffprobe
    // reports as spanning the whole container, so a kept poster claimed 6385s
    // and became the target the honest 6325s output was measured against.
    var poster = new MediaStream
    {
        Index = 33, Kind = StreamKind.Video, Codec = "mjpeg",
        Language = "und", Seconds = 6385.312, IsCoverArt = true
    };

    var withPoster = new List<MediaStream>(kept) { poster };

    Check("a kept poster does not set the expected length",
        Math.Abs(DurationCheck.Expected(source, withPoster) - 6325.312) < 0.01);

    Check("and the remux passes with one attached",
        DurationCheck.Failed(source, output, withPoster) is null);

    // A data stream spans the file for the same reason and means as little.
    var data = new MediaStream
    {
        Index = 32, Kind = StreamKind.Other, Codec = "bin_data",
        Language = "und", Seconds = 6385.312
    };

    Check("nor does a data stream",
        DurationCheck.Failed(source, output, new List<MediaStream>(kept) { data }) is null);

    // Truncation is still caught when a poster is present - the check must not
    // have been disabled by excluding it.
    Check("real truncation is still caught alongside a poster",
        DurationCheck.Failed(source, cut, withPoster) is not null);


    Check("an unmeasurable file is not judged",
        DurationCheck.Failed(source, Info("y", 0), kept) is null);
}

Console.WriteLine("\n=== rubbish never names a folder ===");
{
    var t = Named(@"E:\m\x.mp3", "Break Your Heart", "Taio Cruz", 200, 320);
    t.Album = new TagValue("Rokstarr", TagSource.Id3v2, TextConfidence.Declared);
    t.TrackNumber = 1;

    // The real case: spam in the album artist put Rokstarr under a folder
    // called "(djweetart.com)". MusicHealth flagged it and nothing asked.
    t.AlbumArtist = new TagValue("(djweetart.com)", TagSource.Id3v2, TextConfidence.Declared);

    var format = new NamingFormat("{albumartist}/{album}/{track:00} {title}");

    Check("spam is not used as an artist folder",
        !format.Path(t).Contains("djweetart"));
    Check("and the track artist is used instead",
        format.Path(t).StartsWith("Taio Cruz"));

    Check("MusicHealth agrees it is unusable",
        !MusicHealth.Trustworthy("(djweetart.com)"));
    Check("a placeholder is unusable too",
        !MusicHealth.Trustworthy("Unknown Artist"));
    Check("an ordinary name is fine", MusicHealth.Trustworthy("Taio Cruz"));

    // Shouting is ugly and still findable, so it must not be rejected -
    // rejecting it would empty the folder name over a cosmetic complaint.
    Check("a shouted name is still usable", MusicHealth.Trustworthy("DAFT PUNK"));

    // Both spelt rubbish: nothing invented, the field comes out empty and the
    // planner holds the file rather than filing it somewhere made up.
    t.Artist = new TagValue("Unknown Artist", TagSource.Id3v2, TextConfidence.Declared);
    Check("with nothing usable the field is left empty",
        !format.Path(t).StartsWith("Unknown") && !format.Path(t).Contains("djweetart"));
}

Console.WriteLine("\n=== folders left holding nothing ===");
{
    var root = Path.Combine(Path.GetTempPath(), "PyreMediaTests", Guid.NewGuid().ToString("N")[..8]);

    try
    {
        // An artist folder whose only album emptied: both should go, and the
        // parent only because the child did.
        Directory.CreateDirectory(Path.Combine(root, "Band", "Album"));
        Directory.CreateDirectory(Path.Combine(root, "Other", "Record"));
        Make(root, @"Other\Record\01 Song.mp3", "audio");

        var empty = MusicExecutor.FindEmptyFolders(root);

        Check("an emptied album is found", empty.Any(f => f.EndsWith("Album")));
        Check("and the artist folder above it", empty.Any(f => f.EndsWith("Band")));
        Check("a folder with music is left alone", !empty.Any(f => f.EndsWith("Record")));
        Check("as is its parent", !empty.Any(f => f.EndsWith("Other")));

        var removed = MusicExecutor.RemoveEmptyFolders(empty);

        Check("both are removed", removed == 2);
        Check("the empty ones are gone", !Directory.Exists(Path.Combine(root, "Band")));
        Check("the one with music survives",
            File.Exists(Path.Combine(root, "Other", "Record", "01 Song.mp3")));

        // Something written into a folder between the listing and the removal
        // must save it - the list is gathered before a person is asked.
        Directory.CreateDirectory(Path.Combine(root, "Late"));
        var stale = MusicExecutor.FindEmptyFolders(root);
        Make(root, @"Late\appeared.mp3", "audio");

        Check("a folder that filled up in the meantime is kept",
            MusicExecutor.RemoveEmptyFolders(stale) == 0
            && Directory.Exists(Path.Combine(root, "Late")));
    }
    finally { Clean(root); }
}

Console.WriteLine("\n=== a tie is not a majority ===");
{
    // Reported from a real review screen: "Daft Punk -> DAFT PUNK, 2 of 5
    // against 2 of 5". No majority at all - the winner was whichever the sort
    // happened to put first. Year and Genre had always required a margin.
    var even = new List<TrackTags>
    {
        Named(@"E:\m\1.mp3", "A", "Daft Punk", 200, 320),
        Named(@"E:\m\2.mp3", "B", "Daft Punk", 200, 320),
        Named(@"E:\m\3.mp3", "C", "DAFT PUNK", 200, 320),
        Named(@"E:\m\4.mp3", "D", "DAFT PUNK", 200, 320)
    };

    Check("an even split proposes nothing",
        !MusicConsistency.Examine([even]).Any(f => f.Kind == Disagreement.Spelling));

    // One more on either side and there is something to say again.
    even.Add(Named(@"E:\m\5.mp3", "E", "Daft Punk", 200, 320));

    var decided = MusicConsistency.Examine([even])
        .Where(f => f.Kind == Disagreement.Spelling).ToList();

    Check("a real majority still proposes", decided.Count == 1);
    Check("and proposes the majority", decided[0].Suggested == "Daft Punk");
}

Console.WriteLine("\n=== what a remux must still be ===");
{
    MediaInfo Info(string path, long bytes, params MediaStream[] streams) =>
        new() { Path = path, DurationSeconds = 100, SizeBytes = bytes, Streams = [.. streams] };

    // IsDolbyVision and DvLabel are derived from DvProfile, so setting the
    // profile is what makes a stream Dolby Vision.
    MediaStream Dv(bool enhancement, int profile) => new()
    {
        Index = 0, Kind = StreamKind.Video, Codec = "hevc", Language = "und",
        DvProfile = profile, DvHasEnhancementLayer = enhancement
    };

    MediaStream Plain() => new()
    {
        Index = 0, Kind = StreamKind.Video, Codec = "hevc", Language = "und"
    };

    var withDv = Info("a.mkv", 1000, Dv(false, 8));

    Check("losing Dolby Vision is caught",
        OutputCheck.DolbyVisionLost(withDv, Info("b.mkv", 900, Plain())) is not null);

    Check("keeping it is not",
        OutputCheck.DolbyVisionLost(withDv, Info("b.mkv", 900, Dv(false, 8))) is null);

    Check("a changed profile is caught",
        OutputCheck.DolbyVisionLost(withDv, Info("b.mkv", 900, Dv(false, 5))) is not null);

    Check("a lost enhancement layer is caught",
        OutputCheck.DolbyVisionLost(Info("a.mkv", 1000, Dv(true, 7)),
                                    Info("b.mkv", 900, Dv(false, 7))) is not null);

    Check("a source without it is not judged",
        OutputCheck.DolbyVisionLost(Info("a.mkv", 1000, Plain()),
                                    Info("b.mkv", 900, Plain())) is null);
}

Console.WriteLine("\n=== the plan has to settle ===");
{
    // Every one of these was found by planning a real library twice and
    // watching what refused to stop moving.

    // A folder this program wrote is read back as an album title when the file
    // has no album tag. Leaving the year on it and then appending the year
    // again grew one album's path by four characters every single scan:
    // "(2008) (2008)" then "(2008) (2008) (2008)".
    Check("a folder name gives up the year this program added",
        AlbumGrouper.FolderAsAlbum(@"E:\m\Band\Rumours (1977)\01 x.mp3") == "Rumours");

    Check("a year in the middle is left alone",
        AlbumGrouper.FolderAsAlbum(@"E:\m\Band\1999 (1982)\01 x.mp3") == "1999");

    Check("a folder that is only a year still names the album",
        AlbumGrouper.FolderAsAlbum(@"E:\m\Band\(2008)\01 x.mp3") == "(2008)");

    Check("something that is not a year stays",
        AlbumGrouper.FolderAsAlbum(@"E:\m\Band\Album (Deluxe)\01 x.mp3") == "Album (Deluxe)");

    // Taggers stack disc markers. Stripping only the last one left "CD2" in
    // the title, which became a folder name, which the next scan read as a
    // disc marker all over again.
    Check("stacked disc markers all come off",
        AlbumGrouper.StripDisc("Alle 100 goed Apres Ski CD2 Disc 1").Title == "Alle 100 goed Apres Ski");

    Check("and the innermost one is the disc",
        AlbumGrouper.StripDisc("Alle 100 goed Apres Ski CD2 Disc 1").Disc == 2);

    Check("a single marker still works",
        AlbumGrouper.StripDisc("Straight Outta Lynwood Disc 1") is { Title: "Straight Outta Lynwood", Disc: 1 });

    Check("a title that is only a disc marker survives",
        AlbumGrouper.StripDisc("Disc 2").Title == "Disc 2");
}

Console.WriteLine("\n=== salvage only touches what it changed ===");
{
    // The first version trimmed punctuation unconditionally and took the
    // closing bracket off every title that ended in one.
    Check("a bracketed title is left whole",
        MusicHealth.Salvage("One More Time to Pretend (Immuzikation Remix)")
        == "One More Time to Pretend (Immuzikation Remix)");

    Check("a bracketed album is left whole",
        MusicHealth.Salvage("2008 (2008)") == "2008 (2008)");

    // Where spam did come out, the punctuation it hung off goes too.
    Check("spam is removed and the dangling dash with it",
        MusicHealth.Salvage("Outro - WWW.Crazytown.Com") == "Outro");

    Check("a value that is only spam comes back empty",
        MusicHealth.Salvage("www.example.com") == "");

    Check("a placeholder comes back empty", MusicHealth.Salvage("Unknown Artist") == "");
    Check("an ordinary title is untouched", MusicHealth.Salvage("Hey Jude") == "Hey Jude");
}

Console.WriteLine("\n=== saved formats show as words ===");
{
    // Both spellings have always worked, which is why this went unnoticed:
    // nothing was broken, the settings box simply showed "{0} {1}x{3} {2}" to
    // anybody whose settings predated named placeholders.
    Check("an episode format comes back in words",
        NameTokens.NameEpisode("{0} {1}x{3} {2}") == "{title} {season}x{episode} {name}");

    Check("a title and year format too",
        NameTokens.NameTitleYear("{0} ({1})") == "{title} ({year})");

    Check("a format already in words is untouched",
        NameTokens.NameEpisode("{title} {season}x{episode}") == "{title} {season}x{episode}");

    // A numeric token carrying a format specifier is doing something a name
    // cannot express, so it stays as it is rather than losing the specifier.
    // Only the token carrying the specifier is left alone - the plain one
    // beside it is still renamed, which is the point.
    Check("a numeric token with a specifier is left alone",
        NameTokens.NameEpisode("{0} {1:00}") == "{title} {1:00}");

    Check("an index nothing maps to is left alone",
        NameTokens.NameTitleYear("{0} {7}") == "{title} {7}");

    Check("empty stays empty", NameTokens.NameEpisode("") == "");

    // And the round trip still formats: names in, indices out, same as before.
    Check("words translate back to indices for formatting",
        NameTokens.ForEpisode(NameTokens.NameEpisode("{0} {1}x{3} {2}")) == "{0} {1}x{3} {2}");
}

Console.WriteLine("\n=== a build carries its keys beside it ===");
{
    // The mechanism that lets a private build keep its owner's keys out of the
    // source. Never had a test, and it is the kind that fails silently: the
    // keys simply do not appear, and the app asks for them as though there had
    // never been a file - which is exactly what it does when working correctly
    // on a published build.
    var seeded = Path.Combine(AppContext.BaseDirectory, PyreMediaSettings.LocalKeysFile);
    var existed = File.Exists(seeded);
    var original = existed ? File.ReadAllText(seeded) : null;

    var scratch = Path.Combine(Path.GetTempPath(), $"pyremedia-keys-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratch);

    try
    {
        File.WriteAllText(seeded, """
            { "TmdbApiKey": "tmdb-from-file", "TvdbApiKey": "tvdb-from-file",
              "AcoustIdApiKey": "acoustid-from-file" }
            """);

        var fresh = PyreMediaSettings.Load(Path.Combine(scratch, "settings.json"));

        Check("a first run with no settings takes every key from the file",
            fresh.TmdbApiKey == "tmdb-from-file"
            && fresh.TvdbApiKey == "tvdb-from-file"
            && fresh.AcoustIdApiKey == "acoustid-from-file");

        // A key typed in Settings is a decision. The file fills gaps and must
        // not overrule one, or editing a key would not survive a restart.
        var saved = Path.Combine(scratch, "kept.json");
        new PyreMediaSettings { TmdbApiKey = "typed-by-hand" }.Save(saved);

        var reloaded = PyreMediaSettings.Load(saved);

        Check("a key already set outranks the file",
            reloaded.TmdbApiKey == "typed-by-hand");

        Check("and the gaps beside it are still filled",
            reloaded.TvdbApiKey == "tvdb-from-file");

        // Clearing a key has to stay cleared, or turning AcoustID off would
        // undo itself on the next start.
        File.Delete(seeded);

        var withoutFile = PyreMediaSettings.Load(Path.Combine(scratch, "none.json"));

        Check("no file means no keys, not a failure",
            withoutFile.TmdbApiKey.Length == 0 && withoutFile.AcoustIdApiKey.Length == 0);

        // A published build must not be stopped by a corrupt one.
        File.WriteAllText(seeded, "{ this is not json");

        var broken = PyreMediaSettings.Load(Path.Combine(scratch, "broken.json"));

        Check("a malformed keys file is ignored rather than fatal",
            broken.TmdbApiKey.Length == 0);
    }
    finally
    {
        if (original is not null) File.WriteAllText(seeded, original);
        else File.Delete(seeded);

        try { Directory.Delete(scratch, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== season folders, and the setting that decides them ===");
{
    // Reported against a real library: The Lady Grace Mysteries came out of a
    // scan correctly renamed and sitting straight in the show folder, with no
    // Season 01 beneath it.
    //
    // The layout is the ordinary one - a show folder holding loose episodes -
    // so this pins down what each setting does with it, and that the answer
    // does not depend on the episodes already being in a season folder.
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-season-{Guid.NewGuid():N}");
    var showDir = Path.Combine(root, "The Lady Grace Mysteries (2026)");
    Directory.CreateDirectory(showDir);

    var episodes = new List<string>();
    for (var n = 1; n <= 3; n++)
    {
        var f = Path.Combine(showDir, $"The Lady Grace Mysteries 01x{n:00} Episode {n}.mkv");
        File.WriteAllText(f, "");
        episodes.Add(f);
    }

    var show = new TvShow
    {
        Id = "1",
        Name = "The Lady Grace Mysteries",
        FirstAired = "2026-01-01",
        Seasons =
        [
            new Season
            {
                Number = 1,
                Episodes =
                [
                    new Episode { SeasonNumber = 1, Number = 1, Name = "Episode 1" },
                    new Episode { SeasonNumber = 1, Number = 2, Name = "Episode 2" },
                    new Episode { SeasonNumber = 1, Number = 3, Name = "Episode 3" },
                ]
            }
        ]
    };

    MediaItem LadyGrace() => new()
    {
        Path = showDir,
        DisplayName = "The Lady Grace Mysteries (2026)",
        Kind = MediaKind.TvEpisode,
        Files = [.. episodes],
        SearchTitle = "The Lady Grace Mysteries",
        SearchYear = "2026",
        MainFile = episodes[0]
    };

    try
    {
        // On: episodes gather under Season 01. This is what the checkbox says.
        var moving = new PyreMediaSettings { MoveTvFiles = true };
        var withFolders = new MediaPlanner(moving).PlanTv(LadyGrace(), show);

        var wanted = Path.Combine(showDir, "Season 01");

        Check("with the setting on, every episode lands in Season 01",
            withFolders.Actions.Count == 3
            && withFolders.Actions.All(a => Path.GetDirectoryName(a.TargetPath) == wanted));

        Check("and the Season 01 folder is planned rather than assumed",
            withFolders.FoldersToCreate.Contains(wanted));

        // Off: left where they are. Not a failure to create the folder - a
        // decision not to restructure a library that is already arranged.
        var flat = new PyreMediaSettings { MoveTvFiles = false };
        var inPlace = new MediaPlanner(flat).PlanTv(LadyGrace(), show);

        Check("with the setting off, episodes stay in the show folder",
            inPlace.Actions.Count == 3
            && inPlace.Actions.All(a => Path.GetDirectoryName(a.TargetPath) == showDir));

        Check("and no season folder is created behind your back",
            !inPlace.FoldersToCreate.Any(f => f.Contains("Season", StringComparison.OrdinalIgnoreCase)));

        // The exception that fixed X-Men '97: a file loose in the scan root is
        // in no show's folder, so leaving it there preserves no arrangement.
        // It gets a show folder and a season folder even with moving off.
        var loose = Path.Combine(root, "The Lady Grace Mysteries 01x01 Episode 1.mkv");
        File.WriteAllText(loose, "");

        var looseItem = new MediaItem
        {
            Path = root,
            DisplayName = "The Lady Grace Mysteries 01x01 Episode 1.mkv",
            Kind = MediaKind.TvEpisode,
            Files = [loose],
            SearchTitle = "The Lady Grace Mysteries",
            SearchYear = "2026",
            MainFile = loose,
            IsLooseFile = true
        };

        var rescued = new MediaPlanner(flat).PlanTv(looseItem, show);

        Check("a loose episode is still given a show and season folder",
            rescued.Actions.Count == 1
            && Path.GetDirectoryName(rescued.Actions[0].TargetPath)
               == Path.Combine(root, "The Lady Grace Mysteries (2026)", "Season 01"));

        // On by default now. It was off, and a scan that produced perfect
        // filenames and no Season folder read as a fault rather than a choice.
        Check("season folders are the default", new PyreMediaSettings().MoveTvFiles);

        // The detection. These filenames are already exactly right, which is the
        // case that could quietly do nothing: if "already correct" were judged on
        // the name alone, turning the setting on would find no work to do and the
        // show would stay flat for ever.
        Check("a correctly named show with no season folders is still detected",
            withFolders.GainsSeasonFolders && withFolders.ChangeCount == 3);

        Check("and a show already in season folders is not reported as gaining them",
            !inPlace.GainsSeasonFolders);

        // Once the episodes are in Season 01, a second pass has nothing to say.
        // Without this the notice would fire on every scan of a tidy library.
        var tidyDir = Path.Combine(showDir, "Season 01");
        Directory.CreateDirectory(tidyDir);

        var tidyFiles = episodes
            .Select(e => Path.Combine(tidyDir, Path.GetFileName(e)))
            .ToList();

        foreach (var f in tidyFiles) File.WriteAllText(f, "");

        var settled = new MediaPlanner(moving).PlanTv(new MediaItem
        {
            Path = showDir,
            DisplayName = "The Lady Grace Mysteries (2026)",
            Kind = MediaKind.TvEpisode,
            Files = [.. tidyFiles],
            SearchTitle = "The Lady Grace Mysteries",
            SearchYear = "2026",
            MainFile = tidyFiles[0]
        }, show);

        Check("an already gathered show is left alone and says nothing",
            !settled.GainsSeasonFolders && settled.ChangeCount == 0);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== loose episodes of one show are one entry ===");
{
    // Eight episodes of Reacher dropped in a scan root arrived as eight separate
    // rows, each wanting its own search and its own match, for one show whose
    // answer is the same eight times. A folder of episodes was always one item;
    // loose files were one item each.
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-loose-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);

    void Drop(string name) => File.WriteAllText(Path.Combine(root, name), "x");

    Drop("Reacher.S04E01.1080p.WEB.h264-GRP.mkv");
    Drop("Reacher.S04E02.1080p.WEB.h264-GRP.mkv");
    Drop("Reacher.S04E03.1080p.WEB.h264-GRP.mkv");

    // A different show, so grouping cannot simply sweep everything together.
    Drop("Slow.Horses.S01E01.1080p.WEB.h264-GRP.mkv");

    // Two films whose names look alike. Never grouped - two loose films are two
    // films however similar they read.
    Drop("Dune.2021.1080p.BluRay.x264.mkv");
    Drop("Dune Part Two 2024 1080p BluRay x264.mkv");

    try
    {
        var settings = new PyreMediaSettings();
        settings.TvFolders.Add(root);

        var found = new MediaScanner(settings).Scan();

        var reacher = found.Where(i => i.SearchTitle.Contains("Reacher", StringComparison.OrdinalIgnoreCase)).ToList();

        Check("three loose Reacher episodes make one entry", reacher.Count == 1);
        Check("and that entry holds all three files", reacher.Count == 1 && reacher[0].Files.Count == 3);
        Check("still marked loose, so it is given a show and season folder",
            reacher.Count == 1 && reacher[0].IsLooseFile);

        Check("a single episode of another show stays its own entry",
            found.Count(i => i.SearchTitle.Contains("Slow Horses", StringComparison.OrdinalIgnoreCase)) == 1);

        Check("two similarly named films are not gathered together",
            found.Count(i => i.Kind == MediaKind.Movie) == 2);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a file still downloading is left alone ===");
{
    // Found on a real library: forty-two .!ut placeholders beside a part-
    // downloaded Stranger Things. Renaming a file a client is still writing
    // breaks the transfer, and the result looks like a corrupt file rather than
    // like this program's doing.
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-dl-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);

    var arriving = Path.Combine(root, "Show.S01E01.mkv");
    File.WriteAllText(arriving, "x");
    File.WriteAllText(arriving + ".!ut", "");

    var done = Path.Combine(root, "Show.S01E02.mkv");
    File.WriteAllText(done, "x");

    try
    {
        Check("a video with a .!ut beside it is recognised as arriving",
            Downloads.InProgress(arriving) == "uTorrent");

        Check("and one without is not", Downloads.InProgress(done) is null);

        // The marker must never be offered as junk, whatever the extension list
        // grows to say. Deleting one mid-transfer loses the client's place.
        Check("the marker itself is never junk",
            Downloads.IsMarker("Show.S01E01.mkv.!ut")
            && Downloads.IsMarker("film.mkv.part")
            && !Downloads.IsMarker("Show.S01E01.mkv"));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== the show gets an .nfo of its own ===");
{
    // Every episode got one and the show got none. Not cosmetic: without it
    // Kodi has nothing to attach series artwork, plot or rating to, and falls
    // back to scraping the folder name.
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-tvnfo-{Guid.NewGuid():N}");
    var showDir = Path.Combine(root, "Reacher (2022)");
    Directory.CreateDirectory(showDir);

    var show = new TvShow
    {
        Id = "350665",
        Name = "Reacher",
        FirstAired = "2022-02-04",
        Overview = "Jack Reacher gets off a bus.",
        Network = "Prime Video",
        Seasons =
        [
            new Season { Number = 0, Episodes = [new Episode { SeasonNumber = 0, Number = 1, Name = "Special" }] },
            new Season { Number = 1, Episodes = [
                new Episode { SeasonNumber = 1, Number = 1, Name = "Welcome to Margrave" },
                new Episode { SeasonNumber = 1, Number = 2, Name = "First Dance" }] },
            new Season { Number = 2, Episodes = [
                new Episode { SeasonNumber = 2, Number = 1, Name = "ATM" }] },
        ]
    };

    try
    {
        var settings = new PyreMediaSettings { WriteNfoFiles = true };
        var written = NfoWriter.WriteShow(showDir, show, settings);

        var path = Path.Combine(showDir, "tvshow.nfo");

        Check("tvshow.nfo is written into the show folder",
            written.Outcome == NfoWriter.Outcome.Written && File.Exists(path));

        var xml = File.ReadAllText(path);

        Check("it names the show and carries the plot",
            xml.Contains("<title>Reacher</title>") && xml.Contains("Jack Reacher gets off a bus."));

        Check("and the id Kodi matches episodes through",
            xml.Contains("350665") && xml.Contains("tvdb"));

        // Specials are season 0 and are not one of the seasons a series "has".
        Check("specials are not counted as a season",
            xml.Contains("<season>2</season>") && xml.Contains("<episode>3</episode>"));

        // Off means off, for this as much as for the episode files.
        var quiet = NfoWriter.WriteShow(showDir, show, new PyreMediaSettings { WriteNfoFiles = false });

        Check("nothing is written when .nfo files are turned off",
            quiet.Outcome == NfoWriter.Outcome.SkippedDisabled);

        // An existing one may hold artwork and hand edits nothing here can
        // reproduce, and it has no stream details to refresh.
        var kept = NfoWriter.WriteShow(showDir, show,
            new PyreMediaSettings { WriteNfoFiles = true, PreserveExistingNfo = true, MergeExistingNfo = false });

        Check("an existing tvshow.nfo is left alone when it is being preserved",
            kept.Outcome == NfoWriter.Outcome.SkippedExisting);

        // The one that would hurt: cleanup is on by default now, .nfo is in the
        // junk list, and the file this feature just wrote has that extension.
        // It survives because these are told apart by content, not by suffix.
        Check("the tvshow.nfo it just wrote is not treated as junk",
            MediaPlanner.IsMetadataNfo(path));

        var advert = Path.Combine(showDir, "release-group.nfo");
        File.WriteAllText(advert, "  .:: RELEASE GROUP ::.  \r\n  visit us on irc  \r\n");

        Check("while a scene advert with the same extension is",
            !MediaPlanner.IsMetadataNfo(advert));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== track details survive their setting being renamed ===");
{
    // The counts and languages on the cards were gated behind a setting called
    // "flag foreign audio", which is one of the four things that pass does. A
    // file with a Hindi track and two English subtitle tracks showed nothing at
    // all, because the setting was off and named for something else.
    //
    // Renamed to say what it does. The stored key is deliberately unchanged: a
    // rename that quietly discards somebody's "off" is worse than a bad name.
    var scratch = Path.Combine(Path.GetTempPath(), $"pyremedia-tracks-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratch);

    try
    {
        Check("reading track details is on by default", new PyreMediaSettings().ReadTrackDetails);

        var path = Path.Combine(scratch, "settings.json");
        new PyreMediaSettings { ReadTrackDetails = false }.Save(path);

        Check("it is still stored under the name it always had",
            File.ReadAllText(path).Contains("\"FlagForeignAudio\""));

        Check("and an existing off stays off across the rename",
            !PyreMediaSettings.Load(path).ReadTrackDetails);

        // Settings written before the rename must keep working.
        var older = Path.Combine(scratch, "older.json");
        File.WriteAllText(older, "{ \"FlagForeignAudio\": true, \"PreferredLanguage\": \"eng\" }");

        Check("a settings file from before the rename is read correctly",
            PyreMediaSettings.Load(older).ReadTrackDetails);
    }
    finally
    {
        try { Directory.Delete(scratch, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== the output is judged on what it kept, track by track ===");
{
    // Minions & Monsters (2026), measured. Every number here came off the real
    // file, because the reasoning about it was wrong twice.
    //
    //   video      1:29:58.518   kept
    //   audio hin  1:32:39.648   DROPPED, and the longest thing in the file
    //   audio eng  1:29:57.504   kept
    //   sub   eng  1:29:52.870   kept
    //   container  1:32:40       - which is to say, the Hindi track
    MediaInfo Info(double seconds, params MediaStream[] streams) =>
        new() { Path = "x.mkv", DurationSeconds = seconds, Streams = [.. streams] };

    MediaStream S(StreamKind kind, double seconds, string lang = "eng", int index = 0) => new()
    {
        Index = index, Kind = kind,
        Codec = kind == StreamKind.Video ? "hevc" : kind == StreamKind.Audio ? "eac3" : "subrip",
        Language = lang, Seconds = seconds
    };

    var kept = new List<MediaStream>
    {
        S(StreamKind.Video, 5398.518, "und", 0),
        S(StreamKind.Audio, 5397.504, "eng", 2),
        S(StreamKind.Subtitle, 5392.870, "eng", 4),
    };

    // The source container says 92:40 because of a track that is being dropped.
    var source = Info(5560.656, kept[0], S(StreamKind.Audio, 5559.648, "hin", 1), kept[1], kept[2]);

    // What a good remux actually produces: every kept track its own length, and
    // a container that is now as long as the picture rather than the Hindi.
    var good = Info(5398.518,
        S(StreamKind.Video, 5398.518, "und", 0),
        S(StreamKind.Audio, 5397.504, "eng", 1),
        S(StreamKind.Subtitle, 5392.870, "eng", 2));

    Check("a good remux is not called truncated because a longer track was dropped",
        DurationCheck.Failed(source, good, kept) is null);

    // One track cut short while the others are intact. The container is still
    // 89:58, so nothing about the file's overall length gives this away.
    var lostAudio = Info(5398.518,
        S(StreamKind.Video, 5398.518, "und", 0),
        S(StreamKind.Audio, 4800.000, "eng", 1),
        S(StreamKind.Subtitle, 5392.870, "eng", 2));

    var complaint = DurationCheck.Failed(source, lostAudio, kept);

    Check("a single truncated track is caught even when the file looks the right length",
        complaint is not null);

    Check("and the complaint names which track it was",
        complaint is not null && complaint.Contains("audio") && complaint.Contains("eng"));

    // Rounding is not truncation.
    var rounded = Info(5398.518,
        S(StreamKind.Video, 5398.518 - 1.2, "und", 0),
        S(StreamKind.Audio, 5397.504, "eng", 1),
        S(StreamKind.Subtitle, 5392.870, "eng", 2));

    Check("a second and a half of rounding is not truncation",
        DurationCheck.Failed(source, rounded, kept) is null);

    // Matroska often reports no per-stream duration at all. The track-by-track
    // check has nothing to say then, and must not invent an answer - the
    // whole-file check still runs behind it.
    var silent = new List<MediaStream>
    {
        S(StreamKind.Video, 0, "und", 0),
        S(StreamKind.Audio, 0, "eng", 2),
    };

    // The Hindi track is in the source and not in the kept list - so the file
    // is legitimately shorter, and nothing in it can say by how much.
    var quietSource = Info(5560.656, silent[0], S(StreamKind.Audio, 0, "hin", 1), silent[1]);
    var quietOutput = Info(5398.518, S(StreamKind.Video, 0, "und", 0), S(StreamKind.Audio, 0, "eng", 1));

    Check("no per-stream durations and a dropped track means no verdict, not a false one",
        DurationCheck.Failed(quietSource, quietOutput, silent) is null);

    // But with nothing dropped there is a real expectation again, and a file
    // that came up short against it is still caught.
    var whole = new List<MediaStream> { S(StreamKind.Video, 0, "und", 0), S(StreamKind.Audio, 0, "eng", 1) };
    var wholeSource = Info(5560.656, whole[0], whole[1]);

    Check("with nothing dropped, an unmeasurable file is still checked as a whole",
        DurationCheck.Failed(wholeSource, Info(4800.0, whole[0], whole[1]), whole) is not null);
}

Console.WriteLine("\n=== the forced flag is the plan's to decide ===");
{
    // The flag is often wrong in the file, and nothing reveals it until you are
    // watching: a full track marked forced turns permanent subtitles on for a
    // film in your own language, and a genuine forced track left unmarked
    // leaves the one line of alien dialogue untranslated.
    MediaStream Sub(int index, bool forced) => new()
    {
        Index = index, Kind = StreamKind.Subtitle, Codec = "subrip",
        Language = "eng", IsForced = forced
    };

    var plain = Sub(3, forced: false);
    var marked = Sub(4, forced: true);

    var info = new MediaInfo { Path = "x.mkv", Streams = [plain, marked], DurationSeconds = 100 };

    // Nothing said: the file's own flags stand, which is what every caller did
    // before this existed.
    var untouched = new RemuxFilePlan { Info = info, Keep = [plain, marked], Drop = [] };

    Check("with no decision recorded the file's own flags stand",
        !untouched.IsForced(plain) && untouched.IsForced(marked));

    // Turned on for a track the file did not mark.
    var added = new RemuxFilePlan { Info = info, Keep = [plain, marked], Drop = [], ForcedSubtitles = [3, 4] };

    Check("a track can be marked forced when the file did not", added.IsForced(plain));

    // And off for one it did - an empty set is a decision, not an absence.
    var cleared = new RemuxFilePlan { Info = info, Keep = [plain, marked], Drop = [], ForcedSubtitles = [] };

    Check("and the flag can be taken off one the file did mark", !cleared.IsForced(marked));

    Check("an empty set means none forced, not no opinion",
        !cleared.IsForced(plain) && !cleared.IsForced(marked));
}

Console.WriteLine("\n=== reading a disc rip ===");
{
    // MakeMKV names its output after the disc label and the title index,
    // because that is all it knows: a Blu-ray holds playlists, and which
    // playlist is episode three is written down nowhere on the disc.
    RipTitle T(int i, double mins, int chapters = 5, long gb = 5) =>
        new($@"C:\rip\30 Rock_t{i:00}.mkv", i, mins * 60, chapters, gb * 1_000_000_000);

    Check("a folder of _t00 files is recognised as a rip",
        DiscRip.LooksLikeARip([@"C:\rip\Show_t00.mkv", @"C:\rip\Show_t01.mkv"]));

    Check("one such file is not - that is a film, and there is nothing to work out",
        !DiscRip.LooksLikeARip([@"C:\rip\Film_t00.mkv"]));

    Check("the title index is read from the name", DiscRip.IndexOf(@"C:\rip\Show_t07.mkv") == 7);

    // The real folder name from a MakeMKV rip of an animated series. It names
    // the disc, not the show, and no provider will ever match it.
    Check("a volume label is recognised as one", DiscRip.LooksLikeADiscLabel("MX2-0N-NW2_DES"));
    Check("and so is the other common shape", DiscRip.LooksLikeADiscLabel("SEASON_1_DISC_2"));

    Check("a real title is not", !DiscRip.LooksLikeADiscLabel("The Cisco Kid (1950)"));
    Check("nor is one written in capitals", !DiscRip.LooksLikeADiscLabel("TENET"));
    Check("nor one with a year in it", !DiscRip.LooksLikeADiscLabel("Metropolis 1927"));

    // An ordinary TV disc: four episodes, a trailer, and the "play all".
    var ordinary = DiscRip.Read([
        T(0, 22.0), T(1, 21.4), T(2, 21.6), T(3, 21.7),
        T(4, 2.5, chapters: 0, gb: 1),            // trailer
        T(5, 86.7, chapters: 20, gb: 20),         // play all
    ]);

    Check("the four episodes are kept", ordinary.Episodes.Count == 4);

    Check("the trailer is set aside as too short",
        ordinary.Ignored.Any(i => i.Title.Index == 4 && i.Why.Contains("too short")));

    Check("the play-all is recognised for what it is",
        ordinary.Ignored.Any(i => i.Title.Index == 5 && i.Why.Contains("joined together")));

    Check("and they come back in disc order",
        ordinary.Episodes.Select(e => e.Index).SequenceEqual([0, 1, 2, 3]));

    Check("with a caution, because disc order is not promised anywhere",
        ordinary.Certainty == RipCertainty.Ordered && ordinary.Caution is not null);

    // The case worth getting right: the same episode offered twice, once with
    // chapter marks and once without.
    // Odd index is the chapterless twin of the one before it.
    bool? Twins(RipTitle a, RipTitle b) => Math.Abs(a.Index - b.Index) == 1
                                           && Math.Min(a.Index, b.Index) % 2 == 0;

    var twinned = DiscRip.Read([
        T(0, 22.0, chapters: 6), T(1, 22.0, chapters: 0),
        T(2, 21.5, chapters: 6), T(3, 21.5, chapters: 0),
    ], Twins);

    Check("a chapterless twin of the same length is dropped", twinned.Episodes.Count == 2);

    Check("and the copy with chapters is the one kept",
        twinned.Episodes.All(e => e.Chapters > 0));

    Check("the reason says the disc offers it twice",
        twinned.Ignored.All(i => i.Why.Contains("offers this episode twice")));

    // The case a real rip exposed. Nine episodes of an animated series, all
    // 21:17 to 21:22 and two identical to the tenth of a second, because
    // television is cut to a broadcast slot. Length alone would have called
    // them copies of each other and thrown seven episodes away.
    var slotted = DiscRip.Read([
        T(0, 21.37), T(1, 21.30), T(2, 21.32), T(3, 21.32), T(4, 21.31),
        T(5, 21.30), T(6, 21.31), T(7, 21.30), T(8, 21.32),
    ], (_, _) => false);

    Check("episodes of identical length are not mistaken for copies",
        slotted.Episodes.Count == 9);

    // And with nothing able to hear the audio, they are all still kept - it
    // says it cannot tell rather than choosing.
    var deaf = DiscRip.Read([
        T(0, 21.37), T(1, 21.30), T(2, 21.32), T(3, 21.32),
    ], (_, _) => null);

    Check("with no way to compare audio, nothing is discarded",
        deaf.Episodes.Count == 4);

    Check("and it says why it cannot be sure",
        deaf.Certainty == RipCertainty.NeedsYou && deaf.Caution!.Contains("same length"));

    // A spread this wide means something that is not an episode has been kept,
    // and numbering in order would number the wrong things.
    var uneven = DiscRip.Read([T(0, 20.0), T(1, 28.0), T(2, 34.0)]);

    Check("a wide spread of lengths asks rather than guesses",
        uneven.Certainty == RipCertainty.NeedsYou && uneven.Caution!.Contains("wider spread"));

    // A film rip: one long title and some extras.
    var film = DiscRip.Read([T(0, 142.0, chapters: 24, gb: 30), T(1, 3.0, chapters: 0, gb: 1)]);

    Check("a single feature is not silently numbered as episode one",
        film.Certainty == RipCertainty.NeedsYou && film.Caution!.Contains("film"));

    // Nothing measurable is not the same as nothing there.
    var blind = DiscRip.Read([
        new(@"C:\rip\Show_t00.mkv", 0, 0, 0, 0),
        new(@"C:\rip\Show_t01.mkv", 1, 0, 0, 0),
    ]);

    Check("files with no readable length are reported, not guessed at",
        blind.Certainty == RipCertainty.NeedsYou && blind.Episodes.Count == 0
        && blind.Caution!.Contains("ffprobe"));
}

Console.WriteLine("\n=== which disc of the season is this ===");
{
    // The Big Bang Theory season 1, runtimes as TMDb actually publishes them.
    // Rounded to whole minutes, and varied enough that a run of five is a shape.
    int[] published = [23, 21, 22, 21, 20, 21, 21, 20, 19, 21, 22, 20, 21, 22, 20, 21, 22];

    List<Episode> Season(int[] runtimes) =>
        [.. runtimes.Select((r, i) => new Episode
        {
            SeasonNumber = 1, Number = i + 1, Name = $"Episode {i + 1}", RuntimeMinutes = r
        })];

    RipTitle T(int i, double minutes) =>
        new($@"C:\rip\t{i:00}.mkv", i, minutes * 60, 5, 5_000_000_000);

    var season = Season(published);

    // Disc two: episodes 6 to 10, ripped. Durations are the real thing, so they
    // sit a little either side of the published minute.
    var discTwo = new List<RipTitle>
    {
        T(0, 21.3), T(1, 21.1), T(2, 20.4), T(3, 19.2), T(4, 21.4)
    };

    var placed = DiscPlacement.Find(season, discTwo);

    Check("a disc from the middle of a season is placed by its runtimes",
        placed.Certain && placed.StartsAt!.Number == 6);

    Check("and it says so, since numbering from episode one would be wrong",
        placed.Note is not null && placed.Note.Contains("6"));

    // The first disc needs no explanation - that is where it would have gone.
    var discOne = DiscPlacement.Find(season, [T(0, 22.8), T(1, 21.2), T(2, 21.9), T(3, 21.1)]);

    Check("the first disc is placed at episode one", discOne.Certain && discOne.StartsAt!.Number == 1);
    Check("with nothing to explain", discOne.Note is null);

    // A series cut to a fixed slot has no shape. This is the X-Men case, and
    // what TVmaze publishes for everything.
    var flat = DiscPlacement.Find(Season([30, 30, 30, 30, 30, 30, 30, 30]),
                                  [T(0, 21.3), T(1, 21.3), T(2, 21.3)]);

    Check("a season listed as all one length cannot place anything",
        !flat.Certain && flat.Note!.Contains("30 minutes"));

    // Runtimes missing entirely.
    var blind = DiscPlacement.Find(
        [.. Enumerable.Range(1, 6).Select(n => new Episode
            { SeasonNumber = 1, Number = n, Name = $"E{n}" })],
        [T(0, 21.3), T(1, 21.1)]);

    Check("no runtimes means no placement, and it says why",
        !blind.Certain && blind.Note!.Contains("no runtimes"));

    // Titles that match nothing in the season - wrong season, or a disc of
    // extras. Better to find nothing than the least bad thing.
    var wrong = DiscPlacement.Find(season, [T(0, 48.0), T(1, 47.5), T(2, 49.0)]);

    Check("titles matching nothing are not forced into the closest gap",
        !wrong.Certain && wrong.Note!.Contains("do not match"));
}

Console.WriteLine("\n=== counting a whole set of discs ===");
{
    DiscFolder D(string name, int titles, DateTime? when = null) =>
        new($@"H:\rips\{name}", titles, DiscSet.NumberIn(name),
            when ?? new DateTime(2026, 1, 1));

    Check("a disc number in the folder name is read",
        DiscSet.NumberIn("SEASON_1_DISC_2") == 2 && DiscSet.NumberIn("BUFFY_S3_D4") == 4);

    Check("and a folder without one gives nothing",
        DiscSet.NumberIn("MX2-0N-NW2_DES") is null);

    Check("a year is not a disc number", DiscSet.NumberIn("METROPOLIS_1927") is null);

    // Twenty-one episodes across five discs: 4, 4, 4, 4, 5. The totals agree,
    // so the third disc is episodes nine to twelve and nothing had to be
    // probed or looked up to know it.
    var set = new[]
    {
        D("SEASON_1_DISC_1", 4), D("SEASON_1_DISC_2", 4), D("SEASON_1_DISC_3", 4),
        D("SEASON_1_DISC_4", 4), D("SEASON_1_DISC_5", 5),
    };

    var third = DiscSet.Place(set, @"H:\rips\SEASON_1_DISC_3", 21);

    Check("a disc is placed by counting the ones before it",
        third.Certain && third.Offset == 8 && third.DiscNumber == 3);

    Check("and it says which disc of how many",
        third.Note!.Contains("disc 3") && third.Note.Contains("episode 9"));

    var first = DiscSet.Place(set, @"H:\rips\SEASON_1_DISC_1", 21);
    Check("the first disc starts at nothing", first.Certain && first.Offset == 0);

    var last = DiscSet.Place(set, @"H:\rips\SEASON_1_DISC_5", 21);
    Check("the last starts after all the others", last.Certain && last.Offset == 16);

    // One title too many somewhere. Counting cannot say which disc carries the
    // extra, so it declines rather than shifting everything by one.
    var withExtra = new[]
    {
        D("SEASON_1_DISC_1", 4), D("SEASON_1_DISC_2", 5), D("SEASON_1_DISC_3", 4),
        D("SEASON_1_DISC_4", 4), D("SEASON_1_DISC_5", 5),
    };

    var uncertain = DiscSet.Place(withExtra, @"H:\rips\SEASON_1_DISC_3", 21);

    Check("one extra title anywhere and the arithmetic is refused",
        !uncertain.Certain && uncertain.Note!.Contains("22 titles"));

    // Discs missing from the set - some episodes are still in the box.
    var partial = new[] { D("SEASON_1_DISC_1", 4), D("SEASON_1_DISC_2", 4) };
    var incomplete = DiscSet.Place(partial, @"H:\rips\SEASON_1_DISC_2", 21);

    Check("a set that does not add up says episodes are missing",
        !incomplete.Certain && incomplete.Note!.Contains("has not been ripped"));

    // One folder on its own is not a set. This is the X-Men case.
    var alone = DiscSet.Place([D("MX2-0N-NW2_DES", 9)], @"H:\rips\MX2-0N-NW2_DES", 13);

    Check("a single disc cannot be placed by counting",
        !alone.Certain && alone.Note!.Contains("only ripped disc"));

    // Unnumbered folders fall back to when they were ripped, and say so.
    var byDate = new[]
    {
        D("VOL_A", 4, new DateTime(2026, 1, 3)),
        D("VOL_B", 4, new DateTime(2026, 1, 1)),
        D("VOL_C", 5, new DateTime(2026, 1, 2)),
    };

    var dated = DiscSet.Place(byDate, @"H:\rips\VOL_C", 13);

    Check("unnumbered discs are ordered by when they were ripped",
        dated.Certain && dated.Offset == 4 && dated.DiscNumber == 2);

    Check("and it admits that is an assumption",
        dated.Note!.Contains("worked through in order"));
}

Console.WriteLine($"\n{(failures == 0 ? "all passed" : $"{failures} FAILED")}");
return failures;

/// <summary>Two seconds of a sine wave, so there is a real file to tag.</summary>
static bool Tone(string path)
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };

        foreach (var a in new[] { "-hide_banner", "-v", "error", "-y",
                                  "-f", "lavfi", "-i", "sine=frequency=440:duration=2", path })
            psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);

        return File.Exists(path) && new FileInfo(path).Length > 0;
    }
    catch { return false; }
}

static string Make(string root, string relative, string content)
{
    var path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    return path;
}

static TrackTags Tag(string path) => new() { Path = path };

static void Clean(string root)
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static AlbumNfo Listing(params (string Title, double Seconds)[] tracks) => new()
{
    Path = @"E:\m\album.nfo",
    Tracks = [.. tracks.Select((t, i) => new NfoTrack(i + 1, t.Title, t.Seconds, null))]
};

static AlbumGroup Album(string title, string artist, params TrackTags[] tracks) =>
    Grouped(title, artist, false, tracks);

static AlbumGroup Compilation(string title, string artist, params TrackTags[] tracks) =>
    Grouped(title, artist, true, tracks);

static AlbumGroup Grouped(string title, string artist, bool compilation, params TrackTags[] tracks)
{
    for (var i = 0; i < tracks.Length; i++) tracks[i].TrackNumber ??= i + 1;

    return new AlbumGroup
    {
        Title = title,
        DisplayArtist = artist,
        IsCompilation = compilation,
        Tracks = [.. tracks],
        SourceFolder = Path.GetDirectoryName(tracks[0].Path)
    };
}

// A fingerprint of one repeated hash. Constant sequences make the comparison
// deterministic at every alignment, so these test the arbitration rather than
// chromaprint: 0 against 3 differs in 2 bits of 32 (93.8%, one recording), 0
// against 0xFFFF differs in 16 (50.0%, which is chance - different recordings).
static uint[] Print(uint value, int length = 300) => [.. Enumerable.Repeat(value, length)];

static FingerprintSet Prints(params (TrackTags Track, uint Value)[] files) =>
    new(files.Select(f => new KeyValuePair<string, uint[]>(f.Track.Path, Print(f.Value))));

static TrackTags At(string path, int kbps) => new() { Path = path, Kbps = kbps };

static TrackTags Track(string path, string title, double seconds, int kbps) => new()
{
    Path = path,
    Title = new TagValue(title, TagSource.Id3v2, TextConfidence.Declared),
    Seconds = seconds,
    Kbps = kbps
};

// --- fixture builders: MP4 boxes and Ogg pages, assembled by hand ---

static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);

static byte[] Cat(params byte[][] parts)
{
    var all = new byte[parts.Sum(p => p.Length)];
    var at = 0;
    foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
    return all;
}

/// <summary>An MP4 box: big-endian length including the header, then the name.</summary>
static byte[] Box(string name, params byte[][] body)
{
    var content = Cat(body);
    var size = 8 + content.Length;

    return Cat(
        [(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size],
        System.Text.Encoding.Latin1.GetBytes(name),
        content);
}

static byte[] Item(string name, params byte[][] body) => Box(name, body);

/// <summary>A data box: the value's type, four bytes of locale, then the bytes.</summary>
static byte[] Data(uint kind, byte[] value) => Box("data",
    [(byte)(kind >> 24), (byte)(kind >> 16), (byte)(kind >> 8), (byte)kind],
    [0, 0, 0, 0],
    value);

static byte[] Length32(int n) => [(byte)n, (byte)(n >> 8), (byte)(n >> 16), (byte)(n >> 24)];
static byte[] Length(byte[] b) => Length32(b.Length);
static byte[] Entry(string text) => Cat(Length(Utf8(text)), Utf8(text));

/// <summary>
/// One Ogg page. The segment table has to describe the payload exactly - a run of
/// 255s and then a shorter one, whose shortness is what marks the packet's end.
/// A payload that is an exact multiple of 255 therefore needs a trailing zero.
/// </summary>
static byte[] OggPage(byte[] payload, bool continued = false)
{
    var segments = new List<byte>();
    var left = payload.Length;

    while (left >= 255) { segments.Add(255); left -= 255; }
    segments.Add((byte)left);

    return OggPageRaw(payload, segments, continued);
}

/// <summary>
/// A page whose packet is deliberately unfinished: every segment full, so the
/// reader must wait for the next page before it has anything to parse.
/// </summary>
static byte[] OggPartialPage(byte[] payload, bool continued = false)
{
    if (payload.Length % 255 != 0)
        throw new ArgumentException("an unfinished packet must end on a full 255-byte segment");

    var segments = Enumerable.Repeat((byte)255, payload.Length / 255).ToList();
    return OggPageRaw(payload, segments, continued);
}

static byte[] OggPageRaw(byte[] payload, List<byte> segments, bool continued) => Cat(
    Utf8("OggS"),
    [0],                                     // version
    [(byte)(continued ? 0x01 : 0x00)],       // header type
    new byte[8],                             // granule position
    new byte[4],                             // bitstream serial
    new byte[4],                             // page sequence
    new byte[4],                             // checksum, not verified here
    [(byte)segments.Count],
    [.. segments],
    payload);

static TrackTags Named(string path, string title, string artist, double seconds, int kbps) => new()
{
    Path = path,
    Title = new TagValue(title, TagSource.Asf, TextConfidence.Declared),
    Artist = new TagValue(artist, TagSource.Asf, TextConfidence.Declared),
    Seconds = seconds,
    Kbps = kbps
};

static TrackTags Comp(string path, string albumArtist, string artist, int number) => new()
{
    Path = path,
    Album = new TagValue("Dracula 2000", TagSource.Asf, TextConfidence.Declared),
    AlbumArtist = new TagValue(albumArtist, TagSource.Asf, TextConfidence.Declared),
    Artist = new TagValue(artist, TagSource.Asf, TextConfidence.Declared),
    Title = new TagValue($"Track {number}", TagSource.Asf, TextConfidence.Declared),
    TrackNumber = number
};

static TrackTags Folder(string folder, int number, string album) => new()
{
    Path = Path.Combine(folder, $"{number:00} Track.mp3"),
    Album = new TagValue(album, TagSource.Id3v2, TextConfidence.Declared),
    AlbumArtist = new TagValue("Metallica", TagSource.Id3v2, TextConfidence.Declared),
    Title = new TagValue($"Track {number}", TagSource.Id3v2, TextConfidence.Declared),
    TrackNumber = number
};
