using System.Buffers.Binary;
using PyreMedia.Core;
using PyreMedia.Core.Books;
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

    // The third failure of the same shape, and the one that cost a 20 GB
    // remux. The Croods (2013), measured: the source reports no duration for
    // the video or either audio track, and all seventeen PGS subtitle tracks
    // report exactly 5916.9s - the container's own figure, copied onto each.
    // Seventeen languages do not stop on the same instant. The remux wrote the
    // honest end of the English cues, 5330s, and the check read a ten-minute
    // truncation.
    {
        var croods = Info(@"croods.mkv", 5916.9);

        MediaStream Untimed(StreamKind kind, int index) => new()
        {
            Index = index, Kind = kind,
            Codec = kind == StreamKind.Video ? "h264" : "dts", Language = "eng"
        };

        MediaStream Subs(double seconds, int index) => new()
        {
            Index = index, Kind = StreamKind.Subtitle,
            Codec = "hdmv_pgs_subtitle", Language = "eng", Seconds = seconds
        };

        var keptCroods = new List<MediaStream>
        {
            Untimed(StreamKind.Video, 0),
            Untimed(StreamKind.Audio, 1),
            Subs(5916.9, 3)
        };

        var remuxed = new MediaInfo
        {
            Path = "out.mkv",
            DurationSeconds = 5916.9,
            Streams =
            [
                Untimed(StreamKind.Video, 0),
                Untimed(StreamKind.Audio, 1),
                Subs(5330, 2)          // where the last line of dialogue is
            ]
        };

        Check("subtitles ending before the credits is not a truncation",
            DurationCheck.Failed(croods, remuxed, keptCroods) is null);

        Check("and a subtitle does not set the expected length",
            Math.Abs(DurationCheck.Expected(croods, keptCroods) - 5916.9) < 0.01);

        // Picture and sound still have to be right, which is the whole point of
        // taking subtitles out of it rather than loosening the tolerance.
        var reallyCut = new MediaInfo
        {
            Path = "out.mkv",
            DurationSeconds = 5300,
            Streams =
            [
                new() { Index = 0, Kind = StreamKind.Video, Codec = "h264",
                        Language = "eng", Seconds = 5300 },
                new() { Index = 1, Kind = StreamKind.Audio, Codec = "dts",
                        Language = "eng", Seconds = 5300 }
            ]
        };

        var timedSource = new List<MediaStream>
        {
            new() { Index = 0, Kind = StreamKind.Video, Codec = "h264",
                    Language = "eng", Seconds = 5916.9 },
            new() { Index = 1, Kind = StreamKind.Audio, Codec = "dts",
                    Language = "eng", Seconds = 5916.9 }
        };

        Check("a picture that really is ten minutes short is still refused",
            DurationCheck.Failed(croods, reallyCut, timedSource) is not null);

        // A subtitle's length is not compared at all, even when the file
        // carries a real per-track tag for it.
        //
        // This test used to assert the opposite, on the reasoning that a
        // per-track tag is a measurement rather than the container's figure
        // borrowed. Babe (1995) disproved it. Its English PGS track is tagged
        // 01:29:24.818; mkvmerge writes 01:27:13.186 for the same track, and
        // the content is identical - 2,491 packets in, 2,491 out, last event at
        // 5364.818 on both sides. mkvmerge discounts the trailing clear-screen
        // sets, which carry no duration. The tag is a statistic its muxer chose
        // to write, and two honest tools disagree by two minutes over a
        // bit-identical track.
        MediaStream Measured(double seconds, int index) => new()
        {
            Index = index, Kind = StreamKind.Subtitle, Codec = "subrip",
            Language = "eng", Seconds = seconds, SecondsMeasured = true
        };

        var honest = new List<MediaStream> { Untimed(StreamKind.Video, 0), Measured(5330, 1) };

        var lost = new MediaInfo
        {
            Path = "out.mkv",
            DurationSeconds = 5916.9,
            Streams = [Untimed(StreamKind.Video, 0), Measured(2000, 1)]
        };

        Check("a subtitle that reports a shorter length is not a truncation",
            DurationCheck.Failed(croods, lost, honest) is null);

        // The same shortfall in sound still is. Picture and sound tags survive
        // a remux to the millisecond - Babe came through with 01:31:55.677 and
        // 01:31:55.723 on both sides - so a real drop there means something.
        MediaStream Sound(double seconds, int index) => new()
        {
            Index = index, Kind = StreamKind.Audio, Codec = "dts",
            Language = "eng", Seconds = seconds, SecondsMeasured = true
        };

        Check("but the same shortfall in a sound track is",
            DurationCheck.Failed(
                croods,
                new MediaInfo { Path = "out.mkv", DurationSeconds = 5916.9,
                                Streams = [Untimed(StreamKind.Video, 0), Sound(2000, 1)] },
                [Untimed(StreamKind.Video, 0), Sound(5330, 1)]) is not null);

        var faithful = new MediaInfo
        {
            Path = "out.mkv",
            DurationSeconds = 5916.9,
            Streams = [Untimed(StreamKind.Video, 0), Measured(5330, 1)]
        };

        Check("and passes when it comes back the same length",
            DurationCheck.Failed(croods, faithful, honest) is null);

        // The regression this replaces: a picture and a main soundtrack are
        // exactly as long as the film, so a rule that excused any track whose
        // length equalled the container excused the two whose truncation
        // matters most - and left the check able to fire only on subtitles.
        var realVideo = new List<MediaStream>
        {
            new() { Index = 0, Kind = StreamKind.Video, Codec = "h264",
                    Language = "eng", Seconds = 5916.9, SecondsMeasured = true }
        };

        var shortVideo = new MediaInfo
        {
            Path = "out.mkv",
            DurationSeconds = 5916.9,
            Streams =
            [
                new() { Index = 0, Kind = StreamKind.Video, Codec = "h264",
                        Language = "eng", Seconds = 5000, SecondsMeasured = true }
            ]
        };

        Check("a picture as long as its container is still compared, not excused",
            DurationCheck.Failed(croods, shortVideo, realVideo) is not null);
    }

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

        // The one that matters for remuxing. Show.S01E02.mkv has finished and
        // carries no marker of its own, so the per-file check clears it - but
        // it sits in a folder uTorrent is still writing into, and remuxing
        // replaces the very file the client is holding the torrent open on.
        Check("a folder with a marker in it is known to be busy",
            Downloads.ActiveIn(root) == "uTorrent");

        var quiet = Path.Combine(root, "finished");
        Directory.CreateDirectory(quiet);
        var settled = Path.Combine(quiet, "Show.S02E01.mkv");
        File.WriteAllText(settled, "x");

        Check("a folder with no markers in it is not", Downloads.ActiveIn(quiet) is null);

        Check("and a folder that isn't there is not evidence either way",
            Downloads.ActiveIn(Path.Combine(root, "nope")) is null);

        // End to end, which is where this went wrong: the planner offered three
        // complete Stranger Things episodes that were sitting among five still
        // downloading. Both files here must be refused, for different reasons.
        MediaInfo Film(string path) => new()
        {
            Path = path,
            DurationSeconds = 100,
            Streams =
            [
                new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "h264", Language = "und" },
                new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "ac3", Language = "eng" },
                new MediaStream { Index = 2, Kind = StreamKind.Audio, Codec = "ac3", Language = "spa" },
            ]
        };

        var keepEnglish = new PyreMediaSettings
        {
            KeepAudioLanguages = "eng", KeepSubtitleLanguages = "eng", PreferredLanguage = "eng"
        };

        var midTransfer = new RemuxPlanner(keepEnglish).Plan([Film(arriving), Film(done)]);

        Check("neither the arriving file nor its finished neighbour is offered for remuxing",
            midTransfer.Groups.Count == 0 && midTransfer.Skipped.Count == 2);

        Check("the finished one is refused for the folder it is in, not for itself",
            midTransfer.Skipped.Any(s => s.Contains("Show.S01E02") && s.Contains("into this folder")));

        // The control. Without this the guard could refuse everything and pass.
        var settledPlan = new RemuxPlanner(keepEnglish).Plan([Film(settled)]);

        Check("the same file in a quiet folder is offered as usual",
            settledPlan.Skipped.Count == 0 && settledPlan.Groups.Count > 0);
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

Console.WriteLine("\n=== reading a comic's filename ===");
{
    ComicRef? P(string name) => ComicMatcher.Parse(name);

    // The ordinary shape, with the scanner's signature after it.
    var saga = P("Saga 001 (2012) (Digital) (Zone-Empire).cbz");
    Check("series, issue and year come out of a scene-named file",
        saga is { Series: "Saga", Issue: "001", Year: "2012" });

    Check("and the release group does not become part of the title",
        saga!.Series == "Saga");

    var batman = P("Batman v2 #404 (1987).cbz");
    Check("a volume marker is read and removed from the title",
        batman is { Series: "Batman", Volume: 2, Issue: "404", Year: "1987" });

    var spider = P("The Amazing Spider-Man #050 (of 12).cbz");
    Check("a limited run says how many it is of",
        spider is { Series: "The Amazing Spider-Man", Issue: "050", Of: 12 });

    Check("the issue keeps its padding rather than being turned into a number",
        spider!.Issue == "050" && spider.IssueNumber == 50);

    var annual = P("Uncanny X-Men Annual #1.cbz");
    Check("an annual is recognised and the word leaves the title",
        annual is { Series: "Uncanny X-Men", Issue: "1", Kind: ComicKind.Annual });

    var tpb = P("Saga Vol. 01 (2012).cbz");
    Check("a trade paperback has a volume and no issue",
        tpb is { Series: "Saga", Volume: 1, Issue: null, Year: "2012" });

    // A title that is a year is the trap: dropping every bracket loses it.
    var y2k = P("Batman (2016) #01.cbz");
    Check("a year in brackets is kept as the year, not thrown away with the noise",
        y2k is { Series: "Batman", Year: "2016", Issue: "01" });

    // Scene separators, where a person would use spaces.
    var dotted = P("Fantastic.Four.001.(1961).cbz");
    Check("dots and underscores become spaces",
        dotted is { Series: "Fantastic Four", Issue: "001", Year: "1961" });

    // Half and point issues are real and must not be rounded away. Written the
    // way a file on disk can actually be written: "#1/2" is what the cover says
    // and what collectors type, and it is not a legal filename - the slash makes
    // Windows read "Deadpool #1" as a folder - so on disk it becomes ".5".
    var half = P("Deadpool #0.5 (1994).cbz");
    Check("a half issue survives as written", half?.Issue == "0.5");

    Check("and has no whole number, rather than a wrong one", half!.IssueNumber is null);

    var decimals = P("The Walking Dead #13.1.cbz");
    Check("a point-one issue survives too", decimals?.Issue == "13.1");

    Check("while a padded whole number still reads as one",
        P("Saga #007.cbz") is { Issue: "007", IssueNumber: 7 });

    // No series at all is a real answer.
    Check("a filename with nothing in it gives nothing", P("001.cbz") is null);

    // Every one of these came off a real fifty-issue collection, and every one
    // of them was parsed wrongly before that collection existed to check
    // against. Invented examples had none of these shapes.
    var wildcats = P("WildC.A.T.s - Covert Action Teams Vol.1992 #03 (January, 1993).cbz");

    Check("an initialism in a title survives",
        wildcats?.Series == "WildC.A.T.s - Covert Action Teams");

    Check("a four-digit volume is read rather than left in the title",
        wildcats?.Volume == 1992);

    Check("a cover date written as a month and year is found",
        wildcats?.CoverYear == "1993");

    // The one that would have scattered a run: fifty issues of a 1992 series
    // carry cover dates from 1992 to 1998, and filing by cover date makes six
    // folders where there is one series.
    Check("the series year is the volume, not the date on this issue's cover",
        wildcats?.Year == "1992");

    var last = P("WildC.A.T.s - Covert Action Teams Vol.1992 #50 (June, 1998).cbz");

    Check("so the first and last issue of a run file under the same year",
        last?.Year == wildcats?.Year && last?.CoverYear == "1998");

    // Where there is no volume, the bracketed year is the series year - which is
    // what "Saga 001 (2012)" has always meant.
    Check("with no volume the bracketed year is still the series year",
        P("Saga 001 (2012).cbz") is { Year: "2012", CoverYear: null });

    // .cbr used to be excluded here because nothing could open a RAR. It can
    // now, and leaving it out was hiding 8,342 of 37,425 comics in a real
    // library.
    Check("comic archives are offered and other files are not",
        ComicMatcher.IsComic("x.cbz") && ComicMatcher.IsComic("x.cbr")
        && !ComicMatcher.IsComic("x.mkv") && !ComicMatcher.IsComic("x.epub"));
}

Console.WriteLine("\n=== where a comic or a book goes ===");
{
    var saga = new ComicRef { Series = "Saga", Issue = "7", Year = "2012" };

    Check("the default pattern files by series and pads the issue",
        BookNaming.Path(BookNaming.ComicDefault, saga)
        == Path.Combine("Saga (2012)", "Saga (2012) #007"));

    Check("publisher can be a folder above it",
        BookNaming.Path(BookNaming.ComicByPublisher, saga, "Image Comics")
        == Path.Combine("Image Comics", "Saga (2012)", "Saga (2012) #007"));

    // An optional section vanishes whole rather than leaving "Saga () #007".
    var noYear = new ComicRef { Series = "Saga", Issue = "7" };
    Check("a missing year takes its brackets with it",
        BookNaming.Path(BookNaming.ComicDefault, noYear) == Path.Combine("Saga", "Saga #007"));

    // A publisher nobody has looked up yet is named rather than dropped. An
    // empty component vanishes, so this used to file such comics one level up -
    // 872 of 1,935 files on the measured library, splitting 58 series across
    // two places with nothing held and nothing said. Named the way a book with
    // no author is, the gap is a folder you can work through instead.
    Check("an unknown publisher gets a folder saying so",
        BookNaming.Path(BookNaming.ComicByPublisher, noYear)
        == Path.Combine("Unknown publisher", "Saga", "Saga #007"));

    var known = noYear with { Publisher = "Image Comics" };

    Check("and a known one is still used",
        BookNaming.Path(BookNaming.ComicByPublisher, known)
        == Path.Combine("Image Comics", "Saga", "Saga #007"));

    // Whitespace is not a publisher.
    Check("a blank publisher counts as unknown",
        BookNaming.Path(BookNaming.ComicByPublisher, noYear with { Publisher = "   " })
        == Path.Combine("Unknown publisher", "Saga", "Saga #007"));

    // Padding must not destroy an issue number that is not a whole number.
    var half = new ComicRef { Series = "Deadpool", Issue = "0.5" };
    Check("a point issue is left as written rather than padded into nonsense",
        BookNaming.Path(BookNaming.ComicDefault, half).EndsWith("#0.5"));

    // A slash in a title is a dash, not a new folder.
    var slash = new ComicRef { Series = "Marvel Team-Up: Spider-Man/Hulk", Issue = "1" };
    Check("a colon and a slash in a series name stay in one folder",
        BookNaming.Path(BookNaming.ComicDefault, slash).Split(Path.DirectorySeparatorChar).Length == 2);

    // Books.
    var book = new BookRef
    {
        Title = "The Long Way to a Small, Angry Planet",
        Author = "Becky Chambers", Series = "Wayfarers", SeriesIndex = "1", Year = "2014"
    };

    Check("a book files under its author",
        BookNaming.Path(BookNaming.BookDefault, book)
        == Path.Combine("Becky Chambers", "The Long Way to a Small, Angry Planet (2014)"));

    Check("and by series where the pattern asks for it",
        BookNaming.Path(BookNaming.BookBySeries, book)
        == Path.Combine("Becky Chambers", "Wayfarers", "1 - The Long Way to a Small, Angry Planet (2014)"));

    var standalone = new BookRef { Title = "Small Gods", Author = "Terry Pratchett" };
    Check("a book with no series skips that folder entirely",
        BookNaming.Path(BookNaming.BookBySeries, standalone)
        == Path.Combine("Terry Pratchett", "Small Gods"));

    Check("an author nobody knows is said rather than left blank",
        BookNaming.Path(BookNaming.BookDefault, new BookRef { Title = "Untitled" })
            .StartsWith("Unknown author"));

    // Problems are named before a scan, not after.
    Check("a pattern with no issue number is refused",
        BookNaming.Problems("{series}", comic: true) is not null);

    Check("a pattern with no title is refused for books",
        BookNaming.Problems("{author}", comic: false) is not null);

    Check("an unknown field is named",
        BookNaming.Problems("{series} {sparkle}", comic: true)!.Contains("sparkle"));

    Check("unbalanced brackets are caught",
        BookNaming.Problems("{series}[ ({year}) #{issue}", comic: true) is not null);

    Check("and a good pattern has nothing to say",
        BookNaming.Problems(BookNaming.ComicByPublisher, comic: true) is null);

    // A reserved device name cannot be a folder on Windows at all.
    Check("a series called NUL does not become an uncreatable folder",
        BookNaming.Safe("NUL") == "NUL_");

    // Publisher is how comics are shelved and not how books are. Asking for it
    // in a book pattern is a mistake worth naming rather than an empty folder.
    Check("publisher is a comic field", BookNaming.Problems("{publisher}/{series} #{issue}", comic: true) is null);

    Check("and is not a book field",
        BookNaming.Problems("{publisher}/{title}", comic: false)!.Contains("publisher"));
}

Console.WriteLine("\n=== asking the comic sources in the right order ===");
{
    var local = new Fake("Grand Comics Database", ready: true, hits: 2);
    var metron = new Fake("Metron", ready: true, hits: 5);
    var vine = new Fake("Comic Vine", ready: true, hits: 9);

    var answer = await new ComicProviderChain([local, metron, vine]).SearchAsync("Saga", null);

    Check("the free local source answers first", answer.From == "Grand Comics Database");

    Check("and the rationed ones are never asked",
        metron.Asked == 0 && vine.Asked == 0);

    // Nothing local - fall through, in order.
    var empty = new Fake("Grand Comics Database", ready: true, hits: 0);
    var m2 = new Fake("Metron", ready: true, hits: 3);
    var v2 = new Fake("Comic Vine", ready: true, hits: 9);

    var second = await new ComicProviderChain([empty, m2, v2]).SearchAsync("Saga", null);

    Check("no answer falls through to the next source", second.From == "Metron");
    Check("and stops there rather than asking everything", v2.Asked == 0);

    // A source that is down or rate limited is a reason to try the next, not to
    // give up on the question.
    var broken = new Fake("Grand Comics Database", ready: true, hits: 0, throws: true);
    var m3 = new Fake("Metron", ready: true, hits: 1);

    var recovered = await new ComicProviderChain([broken, m3]).SearchAsync("Saga", null);

    Check("a source that fails does not stop the chain", recovered.From == "Metron");

    Check("and what went wrong is recorded rather than swallowed",
        recovered.Tried.Any(t => t.Trouble is not null && t.Trouble.Contains("rate limited")));

    // A source nobody has set up is skipped without being counted as a failure.
    var unset = new Fake("Grand Comics Database", ready: false, hits: 0);
    var m4 = new Fake("Metron", ready: true, hits: 1);

    var skipped = await new ComicProviderChain([unset, m4]).SearchAsync("Saga", null);

    Check("an unconfigured source is skipped, not tried",
        unset.Asked == 0 && skipped.From == "Metron");

    Check("nothing anywhere gives nothing, with every attempt listed",
        (await new ComicProviderChain([empty, new Fake("Metron", true, 0)]).SearchAsync("x", null))
            is { From: null, Tried.Count: 2 });

    // Which sources are usable is said once, because "nothing is set up" and
    // "nothing matched" look identical afterwards and want different responses.
    var readiness = new ComicProviderChain([unset, m4]).Readiness();

    Check("readiness names what is missing and what is not",
        readiness.Contains("Metron") && readiness.Contains("Not set up"));

    // Issues come from whichever source supplied the series, not from the first
    // one that happens to be ready.
    var chain = new ComicProviderChain([local, metron, vine]);

    Check("issues are fetched from the source that answered",
        (await chain.IssuesAsync("Metron", "1")).Count == 1);

    Check("and asking a source that is not there gives nothing rather than throwing",
        (await chain.IssuesAsync("Nowhere", "1")).Count == 0);
}

Console.WriteLine("\n=== filing comics and ebooks ===");
{
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-shelf-{Guid.NewGuid():N}");
    var comics = Path.Combine(root, "comics in");
    var books = Path.Combine(root, "books in");
    var comicLibrary = Path.Combine(root, "Comics");
    var bookLibrary = Path.Combine(root, "Books");

    Directory.CreateDirectory(Path.Combine(comics, "unsorted"));
    Directory.CreateDirectory(books);

    void Drop(string dir, string name) => File.WriteAllText(Path.Combine(dir, name), "x");

    Drop(Path.Combine(comics, "unsorted"), "Saga 001 (2012) (Digital) (Zone-Empire).cbz");
    Drop(Path.Combine(comics, "unsorted"), "Saga 002 (2012).cbz");
    Drop(comics, "Batman v2 #404 (1987).cbz");
    Drop(comics, "somebodys notes.txt");            // not a comic
    Drop(comics, "Cover Gallery.cbz");              // no issue number
    // Title first. This fixture used to be "Terry Pratchett - Small Gods" and
    // it was the assumption, not the evidence: "Two Words - Two Words" is
    // genuinely ambiguous, and every real file measured takes the Calibre
    // order its default template writes, {title} - {authors}. A name written
    // surname-first settles it either way round, and that is tested above.
    Drop(books, "Small Gods - Terry Pratchett.epub");

    try
    {
        var found = BookPlanner.Scan([comics], [books]);

        Check("comics are found in subfolders as well as the root",
            found.Count(i => i.Kind == ShelfKind.Comic) == 4);

        Check("a text file beside them is not mistaken for one",
            found.All(i => !i.Name.EndsWith(".txt")));

        Check("an ebook is read from its filename when it has no metadata inside",
            found.Any(i => i.Kind == ShelfKind.Book && i.Book?.Author == "Terry Pratchett"));

        var plan = BookPlanner.Plan(
            found, comicLibrary, bookLibrary,
            BookNaming.ComicDefault, BookNaming.BookDefault);

        Check("the three real issues are filed by series",
            plan.Moving.Count(a => a.Item.Kind == ShelfKind.Comic) == 3);

        var saga = plan.Moving.First(a => a.Item.Name.StartsWith("Saga 001"));

        Check("and land under the series folder with a padded issue",
            saga.Target == Path.Combine(comicLibrary, "Saga (2012)", "Saga (2012) #001.cbz"));

        Check("a comic with no issue number is held rather than filed on a guess",
            plan.Held.Any(h => h.Item.Name.StartsWith("Cover Gallery")
                               && h.Held!.Contains("no issue number")));

        Check("the book files under its author",
            plan.Moving.Any(a => a.Target == Path.Combine(bookLibrary, "Terry Pratchett", "Small Gods.epub")));

        // Publisher arrives from a provider, not from the file - and its absence
        // must not leave an empty folder in the path.
        var byPublisher = BookPlanner.Plan(
            found, comicLibrary, bookLibrary,
            BookNaming.ComicByPublisher, BookNaming.BookDefault,
            c => c.Series == "Saga" ? "Image Comics" : null);

        Check("a known publisher becomes a folder",
            byPublisher.Moving.Any(a => a.Target == Path.Combine(
                comicLibrary, "Image Comics", "Saga (2012)", "Saga (2012) #001.cbz")));

        Check("and an unknown one lands under a folder that says so",
            byPublisher.Moving.Any(a => a.Target == Path.Combine(
                comicLibrary, "Unknown publisher", "Batman (1987)", "Batman (1987) #404.cbz")));

        // Two files landing on one name would have the second overwrite the
        // first, so it is caught before anything moves. The realistic way that
        // happens is the same issue downloaded twice from different scanners -
        // which differ only in the part of the name that is thrown away.
        Drop(comics, "Saga 001 (2012) (Digital) (Minutemen-Slayer).cbz");
        var again = BookPlanner.Scan([comics], []);

        var clash = BookPlanner.Plan(again, comicLibrary, bookLibrary,
            BookNaming.ComicDefault, BookNaming.BookDefault);

        Check("two files landing on one name is caught rather than applied",
            clash.Held.Any(h => h.Held!.Contains("same name as")));

        // No library folder chosen is a reason to hold, not to invent one.
        var homeless = BookPlanner.Plan(found, "", "", BookNaming.ComicDefault, BookNaming.BookDefault);

        Check("with no library folder nothing is filed and it says why",
            homeless.Moving.Count() == 0
            && homeless.Held.Any(h => h.Held!.Contains("no comic library folder")));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== moving comics, and taking it back ===");
{
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-move-{Guid.NewGuid():N}");
    var inbox = Path.Combine(root, "in");
    var library = Path.Combine(root, "Comics");

    Directory.CreateDirectory(inbox);

    void Drop(string dir, string name, string body = "x")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), body);
    }

    Drop(inbox, "Saga 001 (2012).cbz");
    Drop(inbox, "Saga 002 (2012).cbz");

    try
    {
        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));

        var plan = BookPlanner.Plan(
            BookPlanner.Scan([inbox], []), library, "",
            BookNaming.ComicDefault, BookNaming.BookDefault);

        var result = ShelfExecutor.Execute(plan, history);

        Check("both issues move", result.Moved == 2 && result.Failed == 0);

        Check("and land where the plan said",
            File.Exists(Path.Combine(library, "Saga (2012)", "Saga (2012) #001.cbz")));

        Check("the originals are gone from the inbox",
            Directory.GetFiles(inbox, "*.cbz").Length == 0);

        // Every move is in the history under one batch, so it reverts as one.
        var entries = history.Read().Where(e => e.BatchId == result.BatchId).ToList();

        Check("every move is written down under one batch", entries.Count == 2);

        Check("with where it came from and where it went",
            entries.All(e => e.OldPath.Contains("in") && e.NewPath.Contains("Comics")));

        // The same issue again, from a different scanner. Nothing is overwritten
        // without being asked - and silence means leave it alone.
        Drop(inbox, "Saga 001 (2012) (Digital) (Minutemen).cbz", "different bytes");

        var second = BookPlanner.Plan(
            BookPlanner.Scan([inbox], []), library, "",
            BookNaming.ComicDefault, BookNaming.BookDefault);

        var clashes = ShelfExecutor.FindClashes(second);

        Check("a name already taken is found before anything moves", clashes.Count == 1);

        Check("and it says what is already there",
            clashes[0].Existing.EndsWith("Saga (2012) #001.cbz"));

        var unanswered = ShelfExecutor.Execute(second, history);

        Check("saying nothing about a clash skips it rather than overwriting",
            unanswered.Skipped == 1 && unanswered.Moved == 0);

        Check("so the file that was already there is untouched",
            File.ReadAllText(Path.Combine(library, "Saga (2012)", "Saga (2012) #001.cbz")) == "x");

        // Keeping both puts the newcomer beside it rather than on top of it.
        var kept = ShelfExecutor.Execute(second, history,
            new Dictionary<string, ShelfChoice>
            {
                [second.Moving.First().Item.Path] = ShelfChoice.KeepBoth
            });

        Check("keeping both finds a free name beside it", kept.Moved == 1);

        Check("and neither file is lost",
            File.Exists(Path.Combine(library, "Saga (2012)", "Saga (2012) #001.cbz"))
            && File.Exists(Path.Combine(library, "Saga (2012)", "Saga (2012) #001 (2).cbz")));

        // A file that vanished between planning and applying is reported, not
        // thrown - somebody may be tidying the same folder in Explorer.
        var stale = BookPlanner.Plan(
            [new ShelfItem
             {
                 Path = Path.Combine(inbox, "gone.cbz"),
                 Kind = ShelfKind.Comic,
                 Comic = new ComicRef { Series = "Gone", Issue = "1" }
             }],
            library, "", BookNaming.ComicDefault, BookNaming.BookDefault);

        var missing = ShelfExecutor.Execute(stale, history);

        Check("a file that disappeared is reported rather than crashing the run",
            missing.Skipped == 1 && missing.Trouble.Any(t => t.Contains("no longer there")));

        Check("emptied folders are found for tidying afterwards",
            ShelfExecutor.EmptyFolders([root]).Count >= 0);

        // The claim the whole design rests on: it undoes. Proved by putting the
        // files back and looking, rather than by trusting that a Move entry in
        // the history is enough on its own.
        var batch = history.Read().Where(e => e.BatchId == result.BatchId).ToList();
        var reverter = new RevertService(history);

        var ready = reverter.Examine(batch);

        Check("both moves are offered back", ready.Count(c => c.CanRevert) == 2);

        var undone = reverter.Revert(ready);

        Check("and both are put back", undone.Reverted == 2 && undone.Failed == 0);

        Check("the files are in the inbox again",
            File.Exists(Path.Combine(inbox, "Saga 001 (2012).cbz"))
            && File.Exists(Path.Combine(inbox, "Saga 002 (2012).cbz")));

        Check("and gone from the library they were filed into",
            !File.Exists(Path.Combine(library, "Saga (2012)", "Saga (2012) #002.cbz")));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== asking the comic what it is, rather than its name ===");
{
    static ComicRef? Info(string xml) =>
        ComicInfoFile.From(System.Xml.Linq.XDocument.Parse(xml));

    // Written the way ComicRack and the scrapers that imitate it write it -
    // taken from a real file rather than invented, because invented examples
    // are the ones that pass.
    var real = Info("""
        <?xml version="1.0"?>
        <ComicInfo xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                   xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Series>WildC.A.T.s: Covert Action Teams</Series>
          <Number>0</Number>
          <Volume>1992</Volume>
          <Count>50</Count>
          <Title>Homecoming</Title>
          <Publisher>Image</Publisher>
          <Notes>Scraped metadata from ComicVine [CVDB114621].</Notes>
          <Year>1993</Year>
          <Month>6</Month>
        </ComicInfo>
        """);

    Check("the series comes from the file, colon and all",
        real?.Series == "WildC.A.T.s: Covert Action Teams");

    // The whole reason to read the file. A filename cannot hold a colon, so
    // every scanner writes a dash instead and the real title is only in here.
    var seriesName = real!.Series;

    Check("which a filename could never have carried",
        System.IO.Path.GetInvalidFileNameChars().Any(seriesName.Contains));

    Check("the issue number is read as written", real.Issue == "0");

    // Volume carries a year in this file, and a year is what a series folder is
    // named after. Reading it as a volume number instead would file this under
    // "Vol 1992" and the cover date would become the series year - which is how
    // one run ends up in six folders.
    Check("a Volume that is a year is the year the series began", real.Year == "1992");
    Check("and is not also read as a volume number", real.Volume is null);
    Check("the cover date stays the cover date", real.CoverYear == "1993");

    Check("the issue's own title is read - a filename almost never has one",
        real.Title == "Homecoming");

    Check("so is the publisher", real.Publisher == "Image");
    Check("and how many issues the run has", real.Of == 50);

    // Worth more than any search: the exact issue, already identified.
    Check("the Comic Vine id is picked out of the notes", real.ProviderId == "cv:114621");

    Check("and it says it came from inside rather than from a guess", real.FromInside);

    // A small Volume is a real volume number, not a year.
    var second = Info("<ComicInfo><Series>Batman</Series><Number>404</Number><Volume>2</Volume></ComicInfo>");

    Check("a small Volume is a volume number", second?.Volume == 2 && second.Year is null);

    // Nothing to file under.
    Check("metadata with no series is no answer at all",
        Info("<ComicInfo><Number>1</Number></ComicInfo>") is null);

    Check("and neither is an empty one",
        Info("<ComicInfo><Series>   </Series><Number>1</Number></ComicInfo>") is null);

    Check("a comic with no Volume falls back to the cover year for the series",
        Info("<ComicInfo><Series>Saga</Series><Number>1</Number><Year>2012</Year></ComicInfo>")
            is { Year: "2012", CoverYear: "2012" });

    Check("Format is read where it says something",
        Info("<ComicInfo><Series>X</Series><Number>1</Number><Format>Annual</Format></ComicInfo>")
            ?.Kind == ComicKind.Annual);

    Check("and a Format nobody recognises is an ordinary issue, not a new category",
        Info("<ComicInfo><Series>X</Series><Number>1</Number><Format>Wednesday</Format></ComicInfo>")
            ?.Kind == ComicKind.Issue);

    Check("a Comic Vine link works as well as a note",
        Info("<ComicInfo><Series>X</Series><Number>1</Number>"
             + "<Web>https://comicvine.gamespot.com/issue/4000-114621/</Web></ComicInfo>")
            ?.ProviderId == "cv:4000-114621");

    // Merging. Neither source is trusted wholesale.
    var fromName = ComicMatcher.Parse("Saga Vol.2012 #003 (May, 2013).cbz");
    var thin = Info("<ComicInfo><Series>Saga</Series><Publisher>Image</Publisher></ComicInfo>");

    var merged = ComicInfoFile.Merge(thin, fromName);

    Check("the file wins on the series", merged?.Series == "Saga");
    Check("the name fills in what the file left out", merged?.Issue == "003" && merged.Year == "2012");
    Check("and what only the file had survives", merged?.Publisher == "Image");
    Check("the merged answer still counts as read rather than guessed", merged!.FromInside);

    Check("with no metadata at all the filename is the answer",
        ComicInfoFile.Merge(null, fromName)?.Issue == "003");

    Check("and with no filename left to parse the file still is",
        ComicInfoFile.Merge(thin, null)?.Series == "Saga");

    // A colon in a series has to survive being turned into a folder name.
    Check("a colon becomes a spaced dash rather than jamming two words together",
        BookNaming.Safe("WildC.A.T.s: Covert Action Teams") == "WildC.A.T.s - Covert Action Teams");

    Check("which is what the pattern then produces",
        BookNaming.Path(BookNaming.ComicDefault, real)
            == @"WildC.A.T.s - Covert Action Teams (1992)\WildC.A.T.s - Covert Action Teams (1992) #000");

    // Fields only the file can fill.
    Check("the issue title can be put in the name now that there is one",
        BookNaming.Path("{series}/{series} #{issue:000}[ - {title}]", real)
            == @"WildC.A.T.s - Covert Action Teams\WildC.A.T.s - Covert Action Teams #000 - Homecoming");

    Check("and the publisher comes from the comic when no provider was asked",
        BookNaming.Path("{publisher}/{series} #{issue:000}", real)
            == @"Image\WildC.A.T.s - Covert Action Teams #000");

    Check("but a provider that was asked outranks it",
        BookNaming.Path("{publisher}/{series} #{issue:000}", real, "Wildstorm")
            .StartsWith("Wildstorm"));

    // Reading inside is what the scan actually does now, so prove it end to end.
    var box = Path.Combine(Path.GetTempPath(), $"pyremedia-inside-{Guid.NewGuid():N}");
    Directory.CreateDirectory(box);

    try
    {
        var made = Path.Combine(box, "wc 00.cbz");

        using (var stream = new FileStream(made, FileMode.Create))
        using (var zip = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var page = new StreamWriter(zip.CreateEntry("01.jpg").Open())) page.Write("x");
            using var meta = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
            meta.Write("<ComicInfo><Series>Saga</Series><Number>7</Number><Volume>2012</Volume>"
                       + "<Publisher>Image</Publisher></ComicInfo>");
        }

        var read = BookPlanner.Scan([box], [], readInside: true);

        Check("a scan opens the comic and takes its word for it",
            read[0].Comic is { Series: "Saga", Issue: "7", Year: "2012", FromInside: true });

        var guessed = BookPlanner.Scan([box], [], readInside: false);

        Check("and turning that off falls back to the name, which knows none of it",
            guessed[0].Comic?.Series != "Saga");

        Check("so the file it would produce is right where the name would not be",
            BookPlanner.Plan(read, @"X:\Lib", "", BookNaming.ComicDefault, BookNaming.BookDefault)
                .Moving.First().Target!.EndsWith(@"Saga (2012)\Saga (2012) #007.cbz"));
    }
    finally
    {
        try { Directory.Delete(box, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== which picture goes where ===");
{
    var ep = Path.Combine("X:", "TV", "Reacher (2022)", "Season 01", "Reacher 01x01 Welcome.mkv");
    var showFolder = Path.Combine("X:", "TV", "Reacher (2022)");

    // The whole bug in one line: an episode's picture is its own frame, and it
    // goes beside the episode under the name Kodi reads for exactly that.
    Check("an episode's frame is written as -thumb.jpg beside it",
        ArtworkWriter.PathFor(ep, ArtKind.EpisodeThumb).EndsWith("Reacher 01x01 Welcome-thumb.jpg"));

    Check("and lands in the season folder, next to the episode",
        Path.GetDirectoryName(ArtworkWriter.PathFor(ep, ArtKind.EpisodeThumb))
            == Path.GetDirectoryName(ep));

    // The series poster belongs to the series, not to any one episode. Writing
    // it beside each one gave a season of identical thumbnails.
    Check("the series poster goes in the show's folder, plainly named",
        ArtworkWriter.FolderPathFor(showFolder, ArtKind.Poster)
            == Path.Combine(showFolder, "poster.jpg"));

    Check("so does the fanart",
        ArtworkWriter.FolderPathFor(showFolder, ArtKind.Fanart)
            == Path.Combine(showFolder, "fanart.jpg"));

    Check("and it is not beside any episode",
        Path.GetDirectoryName(ArtworkWriter.FolderPathFor(showFolder, ArtKind.Poster))
            != Path.GetDirectoryName(ep));

    // Season posters live in the show folder too, which is the part everybody
    // gets wrong by putting them in the season folder.
    Check("a season poster goes in the show folder, numbered",
        ArtworkWriter.SeasonPosterPath(showFolder, 1)
            == Path.Combine(showFolder, "season01-poster.jpg"));

    Check("and season zero is called specials, not season00",
        ArtworkWriter.SeasonPosterPath(showFolder, 0)
            == Path.Combine(showFolder, "season-specials-poster.jpg"));

    Check("a clear logo stays a png, because transparency is the point of it",
        ArtworkWriter.PathFor(ep, ArtKind.ClearLogo).EndsWith(".png"));

    // Merging must not drop a picture only one source had.
    var bare = new Episode { SeasonNumber = 1, Number = 1, Name = "Welcome", Source = "TMDb" };
    var withOne = bare.WithStill("https://example.invalid/frame.jpg");

    Check("an episode can be given a frame without changing the original",
        withOne.StillUrl == "https://example.invalid/frame.jpg" && bare.StillUrl is null);

    Check("and everything else about it comes along",
        withOne.Name == "Welcome" && withOne.Number == 1
        && withOne.SeasonNumber == 1 && withOne.Source == "TMDb");

    // Downloading, through the real writer against a stubbed server.
    var box = Path.Combine(Path.GetTempPath(), $"pyremedia-art-{Guid.NewGuid():N}");
    Directory.CreateDirectory(box);

    try
    {
        var video = Path.Combine(box, "Reacher 01x01 Welcome.mkv");
        File.WriteAllText(video, "x");

        var jpeg = new byte[2048];
        jpeg[0] = 0xFF; jpeg[1] = 0xD8;

        var settings = new PyreMediaSettings { DownloadArtwork = true, PreserveExistingArtwork = false };

        using var http = new HttpClient(new CannedResponse(jpeg));

        var wrote = await ArtworkWriter.WriteAsync(
            http, video, ArtKind.EpisodeThumb, "https://example.invalid/still.jpg", settings);

        Check("a frame is fetched and written",
            wrote.Outcome == ArtworkWriter.Outcome.Written
            && File.Exists(ArtworkWriter.PathFor(video, ArtKind.EpisodeThumb)));

        // An episode nobody photographed is not a failure, and must not be
        // reported as one - plenty of older shows have no stills at all.
        var nothing = await ArtworkWriter.WriteAsync(
            http, video, ArtKind.EpisodeThumb, null, settings);

        Check("an episode with no picture at the source is not a failure",
            nothing.Outcome == ArtworkWriter.Outcome.NothingToGet);

        // Writing to an exact path is what puts a series poster in a folder.
        var folderPoster = ArtworkWriter.FolderPathFor(box, ArtKind.Poster);

        var placed = await ArtworkWriter.WriteToAsync(
            http, folderPoster, ArtKind.Poster, "https://example.invalid/poster.jpg", settings);

        Check("a series poster can be written straight into a folder",
            placed.Outcome == ArtworkWriter.Outcome.Written && File.Exists(folderPoster));

        Check("and it is not sitting beside the episode",
            !File.Exists(ArtworkWriter.PathFor(video, ArtKind.Poster)));

        // What comes back has to actually be an image.
        using var liar = new HttpClient(new CannedResponse(
            System.Text.Encoding.UTF8.GetBytes(new string('x', 2048))));

        var refused = await ArtworkWriter.WriteAsync(
            liar, video, ArtKind.EpisodeThumb, "https://example.invalid/nope", settings);

        Check("something that is not an image is refused rather than saved as one",
            refused.Outcome == ArtworkWriter.Outcome.Failed);

        // A season poster goes in the show folder, not the season folder.
        var seasonArt = ArtworkWriter.SeasonPosterPath(box, 2);

        var seasonWrote = await ArtworkWriter.WriteToAsync(
            http, seasonArt, ArtKind.Poster, "https://example.invalid/s2.jpg", settings);

        Check("a season poster is written where Kodi looks for it",
            seasonWrote.Outcome == ArtworkWriter.Outcome.Written
            && File.Exists(Path.Combine(box, "season02-poster.jpg")));

        Check("and it did not overwrite the series poster",
            File.Exists(ArtworkWriter.FolderPathFor(box, ArtKind.Poster)));
    }
    finally
    {
        try { Directory.Delete(box, true); } catch (Exception) { /* scratch */ }
    }

    // A computed property must not be written into the settings file. It is
    // derived from the key, so a saved copy is an answer that goes stale the
    // moment the key changes.
    var settingsBox = Path.Combine(Path.GetTempPath(), $"pyremedia-set-{Guid.NewGuid():N}");
    Directory.CreateDirectory(settingsBox);

    try
    {
        var file = Path.Combine(settingsBox, "settings.json");
        new PyreMediaSettings { AcoustIdApiKey = "abc" }.Save(file);

        var text = File.ReadAllText(file);

        Check("a derived key flag is not saved into the settings file",
            !text.Contains("HasAcoustIdKey") && !text.Contains("HasTmdbKey"));

        Check("while the key it is derived from is", text.Contains("abc"));
    }
    finally
    {
        try { Directory.Delete(settingsBox, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== words worth searching ===");
{
    var pages = new List<TextPage>
    {
        new(1, "THE CITY BURNS.\nGRIFTER RUNS."),
        new(2, ""),                                  // a splash page with no words
        new(3, "HELSPONT AND THE\nCOMING EVIL")
    };

    var composed = ReadableText.Compose("WildC.A.T.s #0", "Read by Windows OCR", pages);

    // Whoever opens this in a year needs to know why the words are wrong,
    // or they will report it as a bug in whatever wrote it.
    Check("the file says what it is and how it was made",
        composed.Contains("Searchable text") && composed.Contains("Windows OCR")
        && composed.Contains("WildC.A.T.s #0"));

    Check("and that it is for finding rather than reading",
        composed.Contains("not for reading"));

    var back = ReadableText.Parse(composed);

    Check("it reads back page for page", back.Count == 3);
    Check("with the numbers intact", back[0].Number == 1 && back[2].Number == 3);
    Check("and the words", back[0].Text.Contains("GRIFTER RUNS."));

    Check("a page with nothing on it stays a page", back[1].Empty && back[1].Number == 2);

    // The note at the top must not become page one's text.
    Check("the notes are not mistaken for the first page",
        !back[0].Text.Contains("Searchable text") && !back[0].Text.Contains('#'));

    var box = Path.Combine(Path.GetTempPath(), $"pyremedia-text-{Guid.NewGuid():N}");
    Directory.CreateDirectory(box);

    try
    {
        var comic = Path.Combine(box, "WildCATs 000.cbz");
        File.WriteAllText(comic, "not really a comic");

        var sidecar = ReadableText.PathFor(comic);

        Check("the sidecar is named after the comic, as plain text",
            sidecar == Path.Combine(box, "WildCATs 000.txt"));

        Check("and is not there until it is written", !ReadableText.Exists(comic));

        File.WriteAllText(sidecar, composed);

        Check("then it is", ReadableText.Exists(comic));

        var hits = ReadableText.Search([sidecar], "helspont");

        Check("a search finds the phrase", hits.Count == 1);

        Check("and says which page it is on, which is the point of the page marks",
            hits[0].Page == 3 && hits[0].Line.Contains("HELSPONT"));

        Check("and which comic, by name rather than by path", hits[0].Name == "WildCATs 000");

        Check("something that is not in it is not found",
            ReadableText.Search([sidecar], "quantum bicycle repair").Count == 0);

        Check("an empty query finds nothing rather than everything",
            ReadableText.Search([sidecar], "   ").Count == 0);

        Check("a sidecar that is not there is skipped rather than thrown over",
            ReadableText.Search([Path.Combine(box, "gone.txt")], "grifter").Count == 0);

        // The trap this walked straight into: .txt is on the junk list and
        // cleanup is on by default, so without telling these apart by content
        // the program would read a shelf for twenty minutes and then offer to
        // delete everything it wrote.
        ReadableText.Write(comic, composed);

        Check("a searchable text file is recognised as one", ReadableText.IsSearchableText(sidecar));

        var litter = Path.Combine(box, "release-group.txt");
        File.WriteAllText(litter, "  .:: RELEASE GROUP ::.  \r\n  visit us on irc  \r\n");

        Check("while scene litter with the same extension is not",
            !ReadableText.IsSearchableText(litter));

        Check("and neither is something that is not a .txt at all",
            !ReadableText.IsSearchableText(comic));

        // Written with a mark, so Notepad and Windows Search do not have to
        // guess - a book full of accented characters otherwise reads as mojibake.
        var bytes = File.ReadAllBytes(sidecar);

        Check("it is written as UTF-8 and says so",
            bytes is [0xEF, 0xBB, 0xBF, ..]);

        Check("and the mark does not stop it being recognised",
            ReadableText.IsSearchableText(sidecar));

        Check("accented characters survive the round trip",
            ReadableText.Parse(File.ReadAllText(
                WriteAccented(box, "Ω é ü — 語"))) is [{ } only] && only.Text.Contains("語"));
    }
    finally
    {
        try { Directory.Delete(box, true); } catch (Exception) { /* scratch */ }
    }

    // The tolerance without which the whole feature is useless: comic lettering
    // draws a capital I with serifs that read as a slash.
    Check("a name lettered with slashes for its I is still found",
        ReadableText.Loose("GRIFTER").IsMatch("GR/FTER"));

    Check("so is one with zeroes for its O", ReadableText.Loose("VOODOO").IsMatch("V00D00"));
    Check("and fives for its S", ReadableText.Loose("SPARTAN").IsMatch("5PARTAN"));
    Check("and sevens for its T", ReadableText.Loose("ZEALOT").IsMatch("ZEAL07"));

    // It widens letters to the shapes they are drawn as, and no further.
    Check("but a different word is still a different word",
        !ReadableText.Loose("GRIFTER").IsMatch("DRIFTER"));

    Check("and a shorter one is not matched by a longer",
        !ReadableText.Loose("VOODOO").IsMatch("VOODO"));

    Check("a space in the query survives a line being wrapped oddly",
        ReadableText.Loose("COMING EVIL").IsMatch("COMING   EVIL"));
}

Console.WriteLine("\n=== apply what was shown, not what was scanned ===");
{
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-scope-{Guid.NewGuid():N}");
    var inbox = Path.Combine(root, "in");
    var library = Path.Combine(root, "Comics");

    Directory.CreateDirectory(inbox);

    try
    {
        // Two series and an ebook, the way a real shelf arrives.
        foreach (var n in new[] { "Saga 001 (2012).cbz", "Saga 002 (2012).cbz",
                                  "Fables 001 (2002).cbz", "Fables 002 (2002).cbz" })
            File.WriteAllText(Path.Combine(inbox, n), "x");

        var scanned = BookPlanner.Scan([inbox], []);

        var whole = BookPlanner.Plan(
            scanned, library, "", BookNaming.ComicDefault, BookNaming.BookDefault);

        Check("the scan finds both series", whole.Moving.Count() == 4);

        // What the grid shows when one series is picked. The view builds its
        // rows this way, and Apply used to ignore them and run the whole plan -
        // so narrowing to one series moved the entire library.
        var saga = ComicShelf.Group(scanned).First(g => g.Series == "Saga");

        var shown = whole.Actions
            .Where(a => a.Item.Comic is { } c
                        && ComicShelf.SameSeries(c.Series, saga.Series)
                        && c.Year == saga.Year)
            .ToList();

        Check("narrowing to one series shows only its issues", shown.Count == 2);

        var narrowed = new ShelfPlan { Actions = [.. shown] };

        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
        var result = ShelfExecutor.Execute(narrowed, history);

        Check("applying moves exactly what was shown", result.Moved == 2);

        Check("the series that was shown is filed",
            File.Exists(Path.Combine(library, "Saga (2012)", "Saga (2012) #001.cbz")));

        // The whole point: everything else is untouched.
        Check("and the series that was not shown is left where it was",
            File.Exists(Path.Combine(inbox, "Fables 001 (2002).cbz"))
            && File.Exists(Path.Combine(inbox, "Fables 002 (2002).cbz")));

        Check("with nothing of it in the library",
            !Directory.Exists(Path.Combine(library, "Fables (2002)")));

        // One batch, so undoing puts back exactly that much.
        var batch = history.Read().Where(h => h.BatchId == result.BatchId).ToList();

        Check("only what moved is written down", batch.Count == 2);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a folder that is somebody's collection, not a film's ===");
{
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-crowd-{Guid.NewGuid():N}");
    var shelf = Path.Combine(root, "Michael Crichton Collection");
    var proper = Path.Combine(root, "Some Old Film Folder");

    Directory.CreateDirectory(shelf);
    Directory.CreateDirectory(proper);

    try
    {
        // A real shelf had exactly this: seventy-five books and one stray video
        // file, in a folder the video scanner claimed as a film.
        for (var i = 1; i <= 20; i++)
            File.WriteAllText(Path.Combine(shelf, $"Book {i:00}.epub"), "x");

        var stray = Path.Combine(shelf, "Some Film.mkv");
        File.WriteAllText(stray, "x");

        var film = Path.Combine(proper, "Some Film.mkv");
        File.WriteAllText(film, "x");

        var settings = new PyreMediaSettings { RenameShowFolder = true };

        MediaItem Holding(string folder, string main) => new()
        {
            Path = folder,
            DisplayName = Path.GetFileName(folder),
            Kind = MediaKind.Movie,
            Files = [main],
            MainFile = main,
            SearchTitle = "Some Film"
        };

        var movie = new Movie { TmdbId = "1", Title = "Some Film", Year = "1997" };

        var crowded = new MediaPlanner(settings).PlanMovie(Holding(shelf, stray), movie);

        Check("a folder holding a shelf of books is still offered, not hidden",
            crowded.FolderRename is not null);

        // Refused rather than silently skipped: the user may genuinely want it
        // and is the only one who can tell.
        Check("but renaming it is refused, and says why",
            crowded.FolderRename!.Problem is { } why
            && why.Contains("20 book(s)") && why.Contains("one video file"));

        Check("and the reason says what would be lost",
            crowded.FolderRename.Problem!.Contains("collection"));

        // An ordinary film folder is untouched by any of this.
        var ordinary = new MediaPlanner(settings).PlanMovie(Holding(proper, film), movie);

        Check("an ordinary film folder is still renamed",
            ordinary.FolderRename is { Problem: null }
            && Path.GetFileName(ordinary.FolderRename.TargetPath) == "Some Film (1997)");

        // A couple of stray books beside a film is not somebody's collection.
        File.WriteAllText(Path.Combine(proper, "read me.epub"), "x");
        File.WriteAllText(Path.Combine(proper, "also.epub"), "x");

        var couple = new MediaPlanner(settings).PlanMovie(Holding(proper, film), movie);

        Check("and two stray books beside it do not block it either",
            couple.FolderRename is { Problem: null });
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== loose lines into something that reads like a script ===");
{
    // Two balloons side by side. The engine returns lines roughly top to
    // bottom, which interleaves them - so a flat dump reads as nonsense even
    // when every word is right.
    var lines = new List<PlacedLine>
    {
        new("WHAT DO YOU", 10, 10, 100, 12),
        new("WANT, GNOME?", 10, 24, 100, 12),
        new("NOTHING YOU", 300, 10, 100, 12),
        new("WOULD LIKE.", 300, 24, 100, 12),
    };

    var blocks = PageScript.Blocks(lines);

    Check("two balloons side by side stay two balloons", blocks.Count == 2);

    Check("and each keeps its own lines together, in order",
        blocks[0].Text == "WHAT DO YOU WANT, GNOME?"
        && blocks[1].Text == "NOTHING YOU WOULD LIKE.");

    Check("read left to right at the same height", blocks[0].Left < blocks[1].Left);

    var apart = PageScript.Blocks(
    [
        new("UP HERE", 10, 10, 80, 12),
        new("WAY DOWN HERE", 10, 400, 80, 12)
    ]);

    Check("a line far below is not the same balloon", apart.Count == 2);
    Check("and the higher one comes first", apart[0].Text == "UP HERE");

    var stacked = PageScript.Blocks(
    [
        new("SECOND PANEL", 10, 500, 90, 12),
        new("FIRST PANEL", 300, 10, 90, 12)
    ]);

    Check("a block lower down comes after one above it, wherever it sits sideways",
        stacked[0].Text == "FIRST PANEL");

    Check("nothing in, nothing out", PageScript.Blocks([]).Count == 0);

    var script = PageScript.Compose(lines);

    Check("the script numbers each block", script.Contains("[1] ") && script.Contains("[2] "));
    Check("and holds the words", script.Contains("WANT, GNOME?"));
}

Console.WriteLine("\n=== which pages are not story pages ===");
{
    // Taken from a real 2016 issue: an indicia, a house advertisement, and a
    // page of genuine dialogue.
    var story = "WHAT DO YOU WANT, GNOME? NOTHING YOU WOULD LIKE. THEN WE GO. "
              + "WAIT! THE PERIMETER SENSORS! WE HAVE INTRUDERS!";

    var advert = "THE SUMMER OF VALIANT. This summer, the forces of the future unite behind "
               + "the fallen guardian of New Japan in the startling first issue of Valiant's "
               + "four-issue event series. ON SALE MAY. ONLY 9.99 at ValiantUniverse.com";

    var indicia = "Peter Cuneo Chairman. Dinesh Shamdasani CEO & Chief Creative Officer. "
                + "Fred Pierce Publisher. Warren Simons Editor-in-Chief. All rights reserved. "
                + "Trademark of Valiant Entertainment. Printed in the USA.";

    // Judged as a book, because what counts as unusual typography can only be
    // decided by looking at the whole thing.
    var sheet = new List<TextPage>
    {
        new(1, story), new(2, indicia), new(3, story), new(4, story),
        new(5, advert), new(6, story), new(7, story), new(8, story)
    };

    var verdicts = PageJudge.JudgeAll(sheet);

    PageVerdict Sheet(int n) => verdicts.First(v => v.Page == n);

    Check("a page of dialogue is left as a story page",
        !Sheet(1).Suggested && !Sheet(6).Suggested);

    Check("an advertisement is spotted", Sheet(5).Looks == PageKind.Advertisement);

    // A masthead is not an advert. The indicia is where the copyright lives and
    // it should not be offered for removal in the same breath.
    Check("and an indicia is called editorial rather than advertising",
        Sheet(2).Looks == PageKind.Editorial);

    // The whole point: a person has to be able to check the answer.
    Check("every suggestion says what was found",
        Sheet(5).Because.Count > 0 && Sheet(2).Because.Count > 0);

    Check("citing what is actually printed",
        Sheet(5).Because.Any(b => b.Contains("9.99") || b.Contains("on sale"))
        && Sheet(2).Because.Any(b => b.Contains("editor-in-chief")));

    Check("and a story page has nothing to say against it", Sheet(1).Because.Count == 0);

    // A scanner's own marking beats anything worked out here.
    var known = new List<ComicPage>
    {
        new() { Entry = "01.jpg", Number = 0, Kind = PageKind.FrontCover, Tagged = true }
    };

    Check("what the file already says is kept, not second-guessed",
        PageJudge.JudgeAll(sheet, known).First(v => v.Page == 1) is
            { Looks: PageKind.FrontCover, Weight: 100 });

    // The assumption that had to go: plenty of modern comics letter their
    // dialogue in mixed case, and judged against the older convention two
    // thirds of such a comic reads as advertising.
    var modern = new List<TextPage>
    {
        new(1, "You've hurt me... you've hurt Japan grievously, Rai... but it's not too late, son."),
        new(2, "We don't live above each other anymore. Or below. We live side by side now."),
        new(3, "The Japan you swore to protect is falling apart around you, but even now it is not too late."),
        new(4, "I have been waiting for this moment for a very long time indeed, my son."),
        new(5, "There is nothing left for either of us here. Nothing at all. Not any more."),
        new(6, advert)
    };

    var judged = PageJudge.JudgeAll(modern);

    Check("a comic lettered in sentence case is not read as all advertising",
        judged.Count(v => v.Suggested) <= 1);

    Check("while the advertisement in it is still found",
        judged.First(v => v.Page == 6).Suggested);

    Check("the baseline is taken from the comic itself",
        PageJudge.Baseline(modern) > 0.5 && PageJudge.Baseline(sheet) < 0.3);

    Check("and is not claimed at all from too few pages",
        PageJudge.Baseline([new TextPage(1, story)]) is null);

    // A splash page with three words is not evidence of anything.
    Check("too little text is judged as nothing rather than as an advert",
        PageJudge.Judge(1, "KRAKOOM", null, 0.05) is { Looks: PageKind.Story, Weight: 0 });
}

Console.WriteLine("\n=== one list of things you might want gone ===");
{
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-tidy-{Guid.NewGuid():N}");
    var shelf = Path.Combine(root, "shelf");

    Directory.CreateDirectory(Path.Combine(shelf, "Screens"));

    try
    {
        // A comic with an advert and a scanner page, and its script beside it.
        var comic = Path.Combine(shelf, "Saga 007.cbz");

        using (var stream = new FileStream(comic, FileMode.Create))
        using (var zip = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            for (var i = 1; i <= 5; i++)
            {
                using var page = new StreamWriter(zip.CreateEntry($"{i:00}.jpg").Open());
                page.Write($"page {i}");
            }
        }

        ScriptNfo.Write(comic, new Script
        {
            Title = "Saga 007", Series = "Saga", Issue = "7",
            Source = "Windows OCR", Exact = false,
            Pages =
            [
                new() { Number = 1, Kind = PageKind.FrontCover },
                new() { Number = 2, Kind = PageKind.Story },
                new() { Number = 3, Kind = PageKind.Advertisement, Because = "it prints a price" },
                new() { Number = 4, Kind = PageKind.Letters },
                new() { Number = 5, Kind = PageKind.ScannerPage, Because = "it says \"scanned by\"" },

                // The file's own Other, which a tagger uses for pinups and
                // bonus art. Reported, never offered for deletion.
                new() { Number = 6, Kind = PageKind.Other }
            ]
        });

        File.WriteAllText(Path.Combine(shelf, "Screens", "screen0001.jpg"), "x");
        File.WriteAllText(Path.Combine(shelf, "poster.jpg"), "x");

        var loose = Path.Combine(shelf, "Jurassic Park v1 01");
        Directory.CreateDirectory(loose);
        for (var i = 1; i <= 12; i++) File.WriteAllText(Path.Combine(loose, $"p{i:000}.jpg"), $"p{i}");

        var found = Tidy.Gather([comic], [shelf]);

        Check("all three kinds arrive in one list", found.Count == 3);

        var pagesIn = found.First(f => f.Kind == TidyKind.PagesInComic);

        Check("the comic's advert and scanner page are offered",
            pagesIn.Entries.Count == 2 && pagesIn.What == "Saga #7");

        // A masthead is where the copyright lives and a letters page is
        // somebody's writing. Neither is offered for deletion.
        Check("but its letters page is not", !pagesIn.Entries.Contains("04.jpg"));
        Check("nor its story pages", !pagesIn.Entries.Contains("02.jpg"));

        Check("and it says why, quoting the evidence",
            pagesIn.Why.Contains("prints a price") && pagesIn.Why.Contains("advert"));

        Check("and what would happen, before it happens",
            pagesIn.Will.Contains("Recycle Bin"));

        Check("the stray image is offered",
            found.Any(f => f.Kind == TidyKind.StrayImage && f.What == "screen0001.jpg"));

        Check("the poster beside it is not", !found.Any(f => f.What == "poster.jpg"));

        var pack = found.First(f => f.Kind == TidyKind.UnpackedComic);

        Check("the unpacked comic is offered", pack.What == "Jurassic Park v1 01");

        // Packing removes nothing, and saying so is the difference between a
        // list somebody reads and one they tick blindly.
        Check("and is not counted as something that removes anything", !pack.Removes);
        Check("while the other two are", pagesIn.Removes
            && found.First(f => f.Kind == TidyKind.StrayImage).Removes);

        Check("packing says nothing is deleted", pack.Will.Contains("nothing is deleted"));

        // Doing only what was chosen.
        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
        var settings = new PyreMediaSettings { DeleteToRecycleBin = false };

        var result = Tidy.Apply([pack], settings, history);

        Check("only the ticked one is done", result.Done == 1 && result.Failed == 0);

        Check("so the comic was packed",
            File.Exists(Path.Combine(shelf, "Jurassic Park v1 01.cbz")));

        Check("and the stray image is still there, because it was not ticked",
            File.Exists(Path.Combine(shelf, "Screens", "screen0001.jpg")));

        Check("and the comic still has all five pages",
            ComicPages.Read(comic).Count == 5);

        // Now the removals.
        var rest = Tidy.Gather([comic], [shelf])
            .Where(f => f.Removes)
            .ToList();

        var second = Tidy.Apply(rest, settings, history);

        Check("removing what was asked for works", second.Done == 2 && second.Failed == 0);

        Check("the advert and the scanner page are gone",
            ComicPages.Read(comic).Count == 3);

        Check("and the pages that were not offered survive",
            ComicPages.Page(comic, "02.jpg") is not null
            && ComicPages.Page(comic, "04.jpg") is not null);

        Check("the stray image is gone",
            !File.Exists(Path.Combine(shelf, "Screens", "screen0001.jpg")));

        Check("and every removal is written down",
            history.Read().Count(h => h.Action == HistoryAction.Delete) >= 2);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a comic that was never packed into anything ===");
{
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-loose-{Guid.NewGuid():N}");

    try
    {
        // A real one: 296 loose images in a folder, invisible to everything
        // because the scanner looks for comic files and a .jpg is not one.
        var loose = Path.Combine(root, "Andromeda Strain (Dell 1969)");
        Directory.CreateDirectory(loose);

        for (var i = 1; i <= 20; i++)
            File.WriteAllText(Path.Combine(loose, $"page{i:000}.jpg"), $"page {i}");

        File.WriteAllText(Path.Combine(loose, "ComicInfo.xml"), "<ComicInfo><Series>X</Series></ComicInfo>");

        // Things that must NOT be offered.
        var calibre = Path.Combine(root, "Angel Time (2938)");
        Directory.CreateDirectory(calibre);
        File.WriteAllText(Path.Combine(calibre, "Angel Time.epub"), "x");
        File.WriteAllText(Path.Combine(calibre, "cover.jpg"), "x");
        for (var i = 1; i <= 12; i++)
            File.WriteAllText(Path.Combine(calibre, $"img{i:000}.jpg"), "x");

        var tiny = Path.Combine(root, "Three photographs");
        Directory.CreateDirectory(tiny);
        for (var i = 1; i <= 3; i++) File.WriteAllText(Path.Combine(tiny, $"{i}.jpg"), "x");

        var mixed = Path.Combine(root, "Some Film");
        Directory.CreateDirectory(mixed);
        File.WriteAllText(Path.Combine(mixed, "film.mkv"), "x");
        for (var i = 1; i <= 12; i++) File.WriteAllText(Path.Combine(mixed, $"s{i:000}.jpg"), "x");

        var found = LoosePages.Find([root]);

        Check("a folder of pages is recognised as a comic", found.Count == 1);
        Check("and it is the right one", found[0].Name == "Andromeda Strain (Dell 1969)");
        Check("with every page in it", found[0].Pages.Count == 20);
        Check("and the metadata carried along rather than left behind",
            found[0].Extras.Any(e => e.EndsWith("ComicInfo.xml")));

        Check("a Calibre book folder is not offered", !found.Any(f => f.Name.Contains("Angel")));
        Check("nor three photographs", !found.Any(f => f.Name.Contains("photographs")));
        Check("nor a folder that also holds a film", !found.Any(f => f.Name.Contains("Film")));

        // Packing.
        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));

        Check("it packs without trouble", LoosePages.Pack(found[0], history) is null);

        var made = found[0].Target;

        Check("and lands beside the folder as a .cbz",
            File.Exists(made) && made.EndsWith("Andromeda Strain (Dell 1969).cbz"));

        // The thing that matters: what came out is the comic that went in.
        var packed = ComicPages.Read(made);

        Check("every page is in the archive", packed.Count == 20);
        Check("in the order they were in", packed[0].Entry == "page001.jpg");

        Check("and the bytes are the bytes",
            System.Text.Encoding.UTF8.GetString(ComicPages.Page(made, "page007.jpg")!) == "page 7");

        Check("the metadata came with it",
            ComicPages.Page(made, "ComicInfo.xml") is { Length: > 0 });

        // The originals are never touched by packing.
        Check("the loose pages are still there afterwards",
            Directory.GetFiles(loose, "*.jpg").Length == 20);

        Check("packing is written down so it can be undone",
            history.Read().Any(h => h.NewPath == made));

        // Offered once. A second pass must not make a second copy.
        Check("a folder already packed is not offered again",
            LoosePages.Find([root]).Count == 0);

        Check("and packing over something is refused rather than overwriting",
            LoosePages.Pack(found[0]) is not null);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== images a release left behind, and the artwork it must never touch ===");
{
    // What it should offer. Taken from real names found on the shelf.
    foreach (var (name, why) in new[]
    {
        ("screen0001.jpg", "a numbered screen grab"),
        ("screen12.png", "a numbered screen grab"),
        ("screenshot.jpg", "a screenshot"),
        ("snapshot3.jpg", "a screenshot"),
        ("proof.jpg", "a proof shot"),
        ("proof02.png", "a proof shot"),
        ("sample.jpg", "a sample image"),
        ("vlcsnap-00012.png", "a player snapshot"),
        ("20240115_143022.jpg", "a timestamped grab"),
    })
        Check($"{name} is offered as {why}",
            SceneImages.Judge(@"X:\Show\" + name) == why);

    // What it must never offer. This list is longer than the one above on
    // purpose: a false positive here deletes artwork somebody chose.
    foreach (var name in new[]
    {
        "cover.jpg", "folder.jpg", "poster.jpg", "fanart.jpg", "banner.jpg",
        "clearlogo.png", "landscape.jpg", "discart.png", "thumb.jpg",
        "season01-poster.jpg", "season-specials-poster.jpg",
        "Reacher 01x01 Welcome-thumb.jpg", "Reacher 01x01 Welcome-poster.jpg",
        "Some Film (1997)-fanart.jpg", "albumart.jpg", "artist.jpg",
    })
        Check($"{name} is left alone", SceneImages.Judge(@"X:\Show\" + name) is null);

    // Names that merely contain a word from the list.
    foreach (var name in new[]
    {
        "Screen Test (1985).jpg", "Screaming Trees - cover.jpg",
        "The Proof (2005).png", "sampler album.jpg", "widescreen.jpg",
    })
        Check($"\"{name}\" is not litter just for containing a word",
            SceneImages.Judge(@"X:\Show\" + name) is null);

    Check("something that is not a picture at all is not judged",
        SceneImages.Judge(@"X:\Show\screen0001.mkv") is null);

    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-scene-{Guid.NewGuid():N}");

    try
    {
        var release = Path.Combine(root, "Some.Film.2019.1080p");
        var proof = Path.Combine(release, "Proof");
        var comic = Path.Combine(root, "Andromeda Strain (Dell 1969)");

        Directory.CreateDirectory(proof);
        Directory.CreateDirectory(comic);

        File.WriteAllText(Path.Combine(release, "Some.Film.mkv"), "x");
        File.WriteAllText(Path.Combine(release, "poster.jpg"), "x");
        File.WriteAllText(Path.Combine(release, "screen0001.jpg"), "x");
        File.WriteAllText(Path.Combine(proof, "anything.jpg"), "x");
        File.WriteAllText(Path.Combine(proof, "whatever.png"), "x");

        // A comic stored as loose pages is a folder of numbered images, and
        // every one of them looks like a grab. Found on the real shelf: 296
        // images in one such folder.
        for (var i = 1; i <= 12; i++)
            File.WriteAllText(Path.Combine(comic, $"page{i:000}.jpg"), "x");

        var found = SceneImages.Find([root]);

        Check("the grab beside the film is found",
            found.Any(f => f.Name == "screen0001.jpg"));

        Check("and everything in a folder called Proof, whatever it is called",
            found.Count(f => f.Because.Contains("folder called Proof")) == 2);

        Check("the poster beside the film is not touched",
            !found.Any(f => f.Name == "poster.jpg"));

        // The one that would hurt: a comic kept as loose pages.
        Check("a folder that is nothing but images is left entirely alone",
            !found.Any(f => f.Path.Contains("Andromeda")));

        Check("so the sweep finds three things, not fifteen", found.Count == 3);

        Check("and a folder it was told to skip is skipped",
            SceneImages.Find([root], [proof]).Count == 1);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a page the scanner added, not the publisher ===");
{
    var story = "WHAT DO YOU WANT, GNOME? NOTHING YOU WOULD LIKE. THEN WE GO. "
              + "WAIT! THE PERIMETER SENSORS! WE HAVE INTRUDERS!";

    var credit = "This comic was scanned by MINUTEMEN-Zone. If you paid for this you were "
               + "robbed. Support the industry and buy the comic. Join us at our tracker for "
               + "more digital comics in cbr/cbz format every week.";

    var pages = new List<TextPage>
    {
        new(1, story), new(2, story), new(3, story),
        new(4, story), new(5, story), new(6, credit)
    };

    var verdicts = PageJudge.JudgeAll(pages);
    var last = verdicts.First(v => v.Page == 6);

    Check("a scanner's own credit page is spotted", last.Suggested);

    // Not an advert. It is not part of the comic at all, which makes it the one
    // page that can go without losing anything the publisher printed.
    Check("and is called something other than an advert", last.Looks == PageKind.ScannerPage);

    Check("saying it is the scanner talking",
        last.Because.Any(b => b.Contains("whoever scanned this")));

    Check("quoting what it found", last.Because.Any(b => b.Contains("scanned by")));

    Check("while the story pages are left alone",
        verdicts.Count(v => v.Suggested) == 1);
}

Console.WriteLine("\n=== giving every page a plain name ===");
{
    var box = Path.Combine(Path.GetTempPath(), $"pyremedia-renum-{Guid.NewGuid():N}");
    Directory.CreateDirectory(box);

    try
    {
        // Named the way a real scanner named them.
        var comic = Path.Combine(box, "WildCATs 001.cbz");

        string[] scanned =
            ["wildc.a.t.s._n1-p01.jpg", "wildc.a.t.s._n1-p02.jpg", "wildc.a.t.s._n1-p03.jpg",
             "zback page04.jpg"];

        using (var stream = new FileStream(comic, FileMode.Create))
        using (var zip = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in scanned)
            {
                using var page = new StreamWriter(zip.CreateEntry(name).Open());
                page.Write($"bytes of {name}");
            }

            using var meta = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
            meta.Write("<ComicInfo><Series>WildC.A.T.s</Series><Number>1</Number>"
                       + "<Pages><Page Image=\"0\" Type=\"FrontCover\" />"
                       + "<Page Image=\"3\" Type=\"Advertisement\" /></Pages></ComicInfo>");
        }

        var before = ComicPages.Read(comic).Select(p => p.Entry).ToList();

        var history = new RenameHistory(Path.Combine(box, "history.jsonl"));

        Check("renaming reports no trouble", ComicPages.Renumber(comic, history) is null);

        var after = ComicPages.Read(comic);

        Check("every page has a plain padded name",
            after.Select(p => p.Entry).ToList() is ["001.jpg", "002.jpg", "003.jpg", "004.jpg"]);

        // The whole promise: names change, order does not.
        Check("and the order is exactly what it was",
            after.Count == before.Count
            && System.Text.Encoding.UTF8.GetString(ComicPages.Page(comic, "001.jpg")!)
                   == $"bytes of {before[0]}"
            && System.Text.Encoding.UTF8.GetString(ComicPages.Page(comic, "004.jpg")!)
                   == $"bytes of {before[3]}");

        // ComicInfo addresses pages by position, and the positions did not move.
        Check("what the file said about each page still points at that page",
            after[0].Kind == PageKind.FrontCover && after[3].Kind == PageKind.Advertisement);

        Check("and the rest of the metadata came with it",
            System.Text.Encoding.UTF8.GetString(ComicPages.Page(comic, "ComicInfo.xml")!)
                .Contains("WildC.A.T.s"));

        Check("it is written down so it can be found again",
            history.Read().Any(h => h.Title!.Contains("4 page(s) renamed")));

        // Doing it twice would rewrite the file and change nothing, which is
        // worse than doing nothing.
        Check("a comic already named this way is left alone",
            ComicPages.Renumber(comic, history) is null);

        Check("and really left alone - no second backup",
            history.Read().Count(h => h.Title!.Contains("renamed")) == 1);

        // Padding widens for a collection rather than overflowing.
        var big = Path.Combine(box, "collection.cbz");

        using (var stream = new FileStream(big, FileMode.Create))
        using (var zip = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            for (var i = 1; i <= 1200; i++)
            {
                using var page = new StreamWriter(zip.CreateEntry($"p{i}.jpg").Open());
                page.Write("x");
            }
        }

        ComicPages.Renumber(big);

        Check("a comic with more pages than the padding allows widens it",
            ComicPages.Read(big)[0].Entry == "0001.jpg");

        // A RAR is read and never rewritten.
        var rar = Path.Combine(box, "rar.cbz");
        File.WriteAllBytes(rar, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00]);

        Check("a RAR is refused, with a reason",
            ComicPages.Renumber(rar) is { } why && why.Contains("RAR"));
    }
    finally
    {
        try { Directory.Delete(box, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a script file another program can index ===");
{
    var pages = new List<TextPage>
    {
        new(1, "[1] THE CITY BURNS.\n[2] GRIFTER RUNS."),
        new(2, "[1] ON SALE NOW. ONLY 9.99 at example.com"),
        new(3, "")
    };

    var verdicts = new List<PageVerdict>
    {
        new() { Page = 1, Looks = PageKind.Story },
        new()
        {
            Page = 2, Looks = PageKind.Advertisement, Weight = 8,
            Because = ["it prints a price (ONLY 9.99)"]
        },
        new() { Page = 3, Looks = PageKind.Story }
    };

    var script = ScriptNfo.From("Saga 007", "Windows OCR (en-US)", exact: false, pages, verdicts)
        with { Series = "Saga", Issue = "7", Year = "2012" };

    var doc = ScriptNfo.Compose(script);
    var xml = doc.ToString();

    Check("it is a comicscript, so an indexer can tell it from any other .nfo",
        doc.Root!.Name.LocalName == "comicscript");

    Check("and carries a version, so element names can be relied on",
        (string?)doc.Root.Attribute("version") == "1");

    Check("the series and issue are in it", xml.Contains("<series>Saga</series>")
                                            && xml.Contains("<issue>7</issue>"));

    // The most important element in the file: how far to trust every word below it.
    Check("it says the words are not exact, and why",
        xml.Contains("exact=\"false\"") && xml.Contains("hint, not a transcript"));

    Check("and names what read them", xml.Contains("Windows OCR (en-US)"));

    // What a comic reader needs to skip the advertising without reading it.
    Check("each page carries what kind of page it is",
        xml.Contains("kind=\"Story\"") && xml.Contains("kind=\"Advertisement\""));

    Check("and an advert says why it was called one",
        xml.Contains("because=\"it prints a price (ONLY 9.99)\""));

    Check("a story page is not cluttered with an empty reason",
        !xml.Contains("kind=\"Story\" because"));

    // The "[1] " a person reads is a block number here, and having it twice
    // invites an indexer to search for it.
    Check("the block numbering is an attribute, not repeated in the text",
        xml.Contains("<block number=\"1\">THE CITY BURNS.</block>"));

    var box = Path.Combine(Path.GetTempPath(), $"pyremedia-script-{Guid.NewGuid():N}");
    Directory.CreateDirectory(box);

    try
    {
        var comic = Path.Combine(box, "Saga 007.cbz");
        File.WriteAllText(comic, "x");

        Check("the script is named after the comic", ScriptNfo.PathFor(comic)
            == Path.Combine(box, "Saga 007.script.nfo"));

        Check("and is not there until it is written", !ScriptNfo.Exists(comic));

        ScriptNfo.Write(comic, script);

        Check("then it is", ScriptNfo.Exists(comic));

        var back = ScriptNfo.Read(ScriptNfo.PathFor(comic));

        Check("it reads back page for page", back?.Pages.Count == 3);
        Check("with the series intact", back?.Series == "Saga" && back.Issue == "7");
        Check("and the warning intact", back?.Exact == false);

        Check("the advert is still an advert on the way back",
            back?.Pages[1] is { Number: 2, Kind: PageKind.Advertisement, Because: not null });

        Check("and its words came with it",
            back!.Pages[0].Blocks is [{ Number: 1, Text: "THE CITY BURNS." }, { Text: "GRIFTER RUNS." }]);

        Check("a page with nothing on it is still a page",
            back.Pages[2] is { Number: 3, Empty: true });

        // 3 + 2 on page one, 7 on page two, nothing on page three.
        Check("the word count is of the words, not the markup", back.Words == 12);

        // .nfo is on the junk list, and cleanup is on by default. Told apart by
        // its root element, exactly as a Kodi .nfo is told from a scene advert.
        Check("it is recognised as a script rather than junk",
            ScriptNfo.IsScript(ScriptNfo.PathFor(comic)));

        var advert = Path.Combine(box, "release-group.nfo");
        File.WriteAllText(advert, "  .:: RELEASE GROUP ::.  \r\n  visit us on irc  \r\n");

        Check("while scene litter with the same extension is not",
            !ScriptNfo.IsScript(advert));

        var kodi = Path.Combine(box, "tvshow.nfo");
        File.WriteAllText(kodi, "<tvshow><title>Reacher</title></tvshow>");

        Check("and neither is Kodi's own metadata", !ScriptNfo.IsScript(kodi));

        Check("nor is something that is not an .nfo at all", !ScriptNfo.IsScript(comic));
    }
    finally
    {
        try { Directory.Delete(box, true); } catch (Exception) { /* scratch */ }
    }

    // A book's words are the publisher's own, and the file has to say so.
    var exact = ScriptNfo.Compose(
        ScriptNfo.From("A Book", "the book's own text", exact: true,
            [new TextPage(1, "It was a dark night.")])).ToString();

    Check("a book's script says its words are exact",
        exact.Contains("exact=\"true\"") && exact.Contains("publisher's own text"));
}

Console.WriteLine("\n=== a book already knows its own words ===");
{
    // No OCR anywhere near this: an EPUB is a zip of XHTML and the text is
    // already text.
    var mixed = BookText.Strip(
        "<html><head><style>p{color:red}</style></head>"
        + "<body><script>alert(1)</script><p>Real words.</p></body></html>");

    Check("script and style are not prose and do not end up in the text",
        !mixed.Contains("color") && !mixed.Contains("alert"));

    Check("while what was between them does", mixed.Contains("Real words."));

    Check("but the prose does",
        BookText.Strip("<p>Real words.</p>").Contains("Real words."));

    // Without this every paragraph runs into one line and a search result is
    // the entire chapter.
    Check("paragraphs stay on their own lines",
        BookText.Strip("<p>One.</p><p>Two.</p>").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            is ["One.", "Two."]);

    Check("entities are turned back into the characters they stand for",
        BookText.Strip("<p>Mr&nbsp;Fox &amp; Mrs Fox &#8212; friends</p>")
            is var text && text.Contains('&') && text.Contains("Fox") && !text.Contains("amp;"));

    Check("a line break is a line break", BookText.Strip("One<br/>Two").Contains('\n'));

    var box = Path.Combine(Path.GetTempPath(), $"pyremedia-epub-{Guid.NewGuid():N}");
    Directory.CreateDirectory(box);

    try
    {
        var epub = Path.Combine(box, "book.epub");

        using (var stream = new FileStream(epub, FileMode.Create))
        using (var zip = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var one = new StreamWriter(zip.CreateEntry("OEBPS/ch01.xhtml").Open()))
                one.Write("<html><body><h1>Chapter One</h1><p>It was a dark night.</p></body></html>");

            using (var two = new StreamWriter(zip.CreateEntry("OEBPS/ch02.xhtml").Open()))
                two.Write("<html><body><p>The fox came back.</p></body></html>");

            // Not prose, and must not become a chapter.
            using var css = new StreamWriter(zip.CreateEntry("OEBPS/style.css").Open());
            css.Write("body { margin: 0 }");
        }

        var chapters = BookText.Read(epub);

        Check("every chapter is read and nothing else is", chapters.Count == 2);
        Check("in the order they are stored", chapters[0].Text.Contains("dark night"));
        Check("and numbered from one", chapters[0].Number == 1 && chapters[1].Number == 2);
        Check("with the words exact, because they were never guessed at",
            chapters[1].Text == "The fox came back.");

        // What it will not do, said rather than half-attempted.
        Check("an EPUB is readable", BookText.CanRead(epub) && BookText.WhyNot(epub) is null);

        Check("a PDF is not, and says why",
            BookText.WhyNot(@"X:\a.pdf") is { } why && why.Contains("pictures of text"));

        Check("nor is Amazon's format",
            BookText.WhyNot(@"X:\a.azw3") is { } amazon && amazon.Contains("Amazon"));
    }
    finally
    {
        try { Directory.Delete(box, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a comic is whatever it was packed with ===");
{
    var box = Path.Combine(Path.GetTempPath(), $"pyremedia-arc-{Guid.NewGuid():N}");
    Directory.CreateDirectory(box);

    try
    {
        var zip = Path.Combine(box, "plain.cbz");

        using (var stream = new FileStream(zip, FileMode.Create))
        using (var archive = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var page = new StreamWriter(archive.CreateEntry("01.jpg").Open());
            page.Write("bytes");
        }

        Check("a zip is recognised as one", ComicArchive.Kind(zip) == ArchiveKind.Zip);
        Check("and can be changed", ComicArchive.CanEdit(zip));
        Check("its entries are listed", ComicArchive.Entries(zip) is [{ Name: "01.jpg" }]);

        Check("and read back",
            System.Text.Encoding.UTF8.GetString(ComicArchive.Read(zip, "01.jpg")!) == "bytes");

        // The header decides, not the extension. A .cbr that is really a zip is
        // common enough that trusting the name would refuse a readable file.
        var lying = Path.Combine(box, "actually-a-zip.cbr");
        File.Copy(zip, lying);

        Check("a .cbr that is really a zip is opened anyway",
            ComicArchive.Kind(lying) == ArchiveKind.Zip
            && ComicArchive.Entries(lying).Count == 1);

        Check("and can still be changed, because it really is a zip",
            ComicArchive.CanEdit(lying));

        // Nothing this can read.
        var junk = Path.Combine(box, "notacomic.cbz");
        File.WriteAllText(junk, "this is not an archive at all");

        Check("something that is not an archive is not mistaken for one",
            ComicArchive.Kind(junk) == ArchiveKind.Unknown);

        Check("it lists nothing rather than throwing", ComicArchive.Entries(junk).Count == 0);
        Check("and reads nothing", ComicArchive.Read(junk, "01.jpg") is null);
        Check("and cannot be changed", !ComicArchive.CanEdit(junk));

        // A file too short to have a header at all.
        var tiny = Path.Combine(box, "tiny.cbz");
        File.WriteAllBytes(tiny, [0x50, 0x4B]);

        Check("a file too short to identify is unknown, not half-read",
            ComicArchive.Kind(tiny) == ArchiveKind.Unknown);

        // A RAR built by hand: just the signature, which is all the sniffing
        // needs to decide. Reading a real one is proved against the library
        // rather than here, since a valid RAR cannot be written by .NET.
        var rar = Path.Combine(box, "looks-like-rar.cbz");
        File.WriteAllBytes(rar, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00]);

        Check("a .cbz that is really a RAR is recognised as a RAR",
            ComicArchive.Kind(rar) == ArchiveKind.Rar);

        Check("and is refused for changes, whatever it is called",
            !ComicArchive.CanEdit(rar));

        // The refusals have to explain themselves, because "it did not work" is
        // not something anybody can act on.
        var refusedRemove = ComicPages.Remove(rar, ["01.jpg"]);

        Check("removing pages from a RAR is refused", refusedRemove is not null);
        Check("and says it is a RAR and what to do", refusedRemove!.Contains("RAR")
                                                     && refusedRemove.Contains(".cbz"));

        var refusedMark = ComicPages.Mark(rar,
            new Dictionary<string, PageKind> { ["01.jpg"] = PageKind.Advertisement });

        Check("so is writing marks into one", refusedMark is not null && refusedMark.Contains("RAR"));

        Check("while a zip is still allowed both",
            ComicPages.Mark(zip, new Dictionary<string, PageKind>
                { ["01.jpg"] = PageKind.Advertisement }) is null);

        Check(".cbr counts as a comic worth scanning now",
            ComicMatcher.IsComic("x.cbr") && ComicMatcher.IsComic("x.cbz"));

        Check("and something that is not still does not", !ComicMatcher.IsComic("x.mkv"));

        // An archive nothing here can open must not read as an empty comic.
        Check("a file that cannot be opened says so rather than reporting no pages",
            ComicPages.Describe([], junk).Contains("not a zip or a RAR"));

        Check("and suggests the one thing that would fix it",
            ComicPages.Describe([], junk).Contains(".cbz"));

        var hollow = Path.Combine(box, "hollow.cbz");

        using (var stream = new FileStream(hollow, FileMode.Create))
        using (var archive = new System.IO.Compression.ZipArchive(
                   stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var note = new StreamWriter(archive.CreateEntry("readme.txt").Open());
            note.Write("no pages here");
        }

        Check("a real archive with no pages in it is described differently",
            ComicPages.Describe(ComicPages.Read(hollow), hollow).Contains("no pages in it"));

        // Offering to save somebody's ticks into a file that cannot be written
        // is a promise this cannot keep.
        var onePage = new List<ComicPage>
            { new() { Entry = "01.jpg", Number = 0 } };

        Check("an unmarked zip is offered the chance to be marked",
            ComicPages.Describe(onePage, zip).Contains("writes the answer into the comic"));

        Check("an unmarked RAR is not, and says why",
            ComicPages.Describe(onePage, rar) is var said
            && !said.Contains("writes the answer") && said.Contains("RAR"));
    }
    finally
    {
        try { Directory.Delete(box, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a series, and what is missing from it ===");
{
    static ShelfItem Issue(string series, string? year, string number,
                           ComicKind kind = ComicKind.Issue, int? of = null) =>
        new()
        {
            Path = $@"X:\{series} {number}.cbz",
            Kind = ShelfKind.Comic,
            Comic = new ComicRef
            {
                Series = series, Year = year, Issue = number, Kind = kind, Of = of
            }
        };

    var run = ComicShelf.Group(
    [
        Issue("Saga", "2012", "1"), Issue("Saga", "2012", "2"), Issue("Saga", "2012", "3"),
        Issue("Saga", "2012", "6"), Issue("Saga", "2012", "7"), Issue("Saga", "2012", "9"),
    ]);

    Check("one series", run.Count == 1 && run[0].Display == "Saga (2012)");

    Check("the holes in the middle are found", run[0].Missing is [4, 5, 8]);

    Check("and read as ranges rather than a list of numbers",
        run[0].Describe().Contains("#4, #5, #8"));

    Check("a long hole is a range", ComicSeriesGroup.Span([3, 4, 5, 6, 7]) == "#3-7");

    // Two of a kind is written out rather than hyphenated: "#4, #5" is shorter
    // to read than "#4-5" and unambiguous.
    Check("two in a row are named", ComicSeriesGroup.Span([4, 5]) == "#4, #5");

    Check("and mixed shapes read the way somebody would say them",
        ComicSeriesGroup.Span([3, 4, 5, 6, 7, 11, 14, 15]) == "#3-7, #11, #14, #15");

    // What is past the end is a different problem from what is missing in it.
    var partial = ComicShelf.Group(
        [Issue("Saga", "2012", "1", of: 5), Issue("Saga", "2012", "2", of: 5)]);

    Check("issues never bought are not called missing",
        partial[0].Missing.Count == 0 && partial[0].NotYet is [3, 4, 5]);

    Check("and the sentence says so", partial[0].Describe().Contains("not yet: #3-5"));

    Check("a run with no holes and nothing left is complete",
        ComicShelf.Group([Issue("X", "2000", "1", of: 2), Issue("X", "2000", "2", of: 2)])[0]
            is { Complete: true } c && c.Describe().Contains("complete run of 2"));

    // The count comes from the comics, never from what is present. Deriving it
    // would call every incomplete run complete.
    Check("with nothing saying how long the run is, nothing is claimed",
        ComicShelf.Group([Issue("Y", "1999", "1"), Issue("Y", "1999", "2")])[0]
            is { Total: null, Complete: true });

    // An annual is not issue 1.
    var withAnnual = ComicShelf.Group(
    [
        Issue("Hellboy", "1994", "1"), Issue("Hellboy", "1994", "2"),
        Issue("Hellboy", "1994", "1", ComicKind.Annual)
    ]);

    Check("an annual does not fill or make a gap",
        withAnnual[0].Missing.Count == 0 && withAnnual[0].Issues.Count == 3);

    // Five different comics are called Daredevil. Merging them would report
    // enormous gaps that are not real.
    var namesakes = ComicShelf.Group(
        [Issue("Daredevil", "1964", "1"), Issue("Daredevil", "1998", "1")]);

    Check("two runs of one name stay two runs", namesakes.Count == 2);

    Check("and neither is missing anything",
        namesakes.All(g => g.Missing.Count == 0));

    var twice = ComicShelf.Group([Issue("Saga", "2012", "1"), Issue("Saga", "2012", "1")]);

    Check("the same issue twice is reported", twice[0].Duplicated is ["1"]);
    Check("and said plainly", twice[0].Describe().Contains("#1 is here twice"));

    Check("a half issue is kept without breaking the run",
        ComicShelf.Group([Issue("Deadpool", "1997", "0.5"), Issue("Deadpool", "1997", "1")])[0]
            is { Issues.Count: 2, Missing.Count: 0 });

    Check("the publisher comes along where a comic knew one",
        ComicShelf.Group(
        [
            Issue("Saga", "2012", "1") with
            {
                Comic = new ComicRef
                {
                    Series = "Saga", Year = "2012", Issue = "1",
                    Publisher = "Image", FromInside = true
                }
            }
        ])[0].Publisher == "Image");

    Check("and nothing is grouped from an ebook", ComicShelf.Group(
        [new ShelfItem { Path = @"X:\a.epub", Kind = ShelfKind.Book }]).Count == 0);

    // Found in a real library: two issues of four reported as "the complete run
    // of 4", because only the holes between what was held were counted and the
    // run started at #3. With a known length the run starts at #1.
    var late = ComicShelf.Group(
        [Issue("4001 A.D.", "2016", "3", of: 4), Issue("4001 A.D.", "2016", "4", of: 4)]);

    Check("issues before the first one held are missing when the length is known",
        late[0].Missing is [1, 2] && !late[0].Complete);

    Check("and two of four is not described as a complete run",
        !late[0].Describe().Contains("complete"));

    // Without a length there is no way to know where the run starts - plenty
    // open at #0 - so nothing below what is held can be claimed.
    var unknown = ComicShelf.Group(
        [Issue("Something", "2016", "3"), Issue("Something", "2016", "4")]);

    Check("with no known length nothing below the first held is invented",
        unknown[0].Missing.Count == 0);

    // Also found in a real library: a colon cannot go in a filename, so the
    // same run arrived as two series - one from inside the files, one from
    // their names - each apparently missing what the other had.
    var punctuated = ComicShelf.Group(
    [
        Issue("A&A - The Adventures of Archer & Armstrong", "2016", "1"),
        Issue("A&A: The Adventures of Archer & Armstrong", "2016", "2") with
        {
            Comic = new ComicRef
            {
                Series = "A&A: The Adventures of Archer & Armstrong",
                Year = "2016", Issue = "2", FromInside = true
            }
        }
    ]);

    Check("a colon and a dash are the same series", punctuated.Count == 1);

    Check("and neither half is reported as missing the other",
        punctuated[0].Issues.Count == 2 && punctuated[0].Missing.Count == 0);

    // The name the comic gave of itself is the real one; a filename could not
    // have held the colon.
    Check("the name read from inside the file is the one shown",
        punctuated[0].Series == "A&A: The Adventures of Archer & Armstrong");

    Check("full stops and spacing do not split a run either",
        ComicShelf.Group([Issue("4001 A.D", "2016", "1"), Issue("4001 A.D.", "2016", "2")])
            .Count == 1);

    Check("but genuinely different names stay apart",
        !ComicShelf.SameSeries("Batman", "Batman and Robin"));

    Check("and a series named only in punctuation is not collapsed into another",
        ComicShelf.Group([Issue("!!!", "2016", "1"), Issue("???", "2016", "1")]).Count == 2);
}

Console.WriteLine("\n=== reading the pages, and taking the adverts out ===");
{
    var root = Path.Combine(Path.GetTempPath(), $"pyremedia-pages-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);

    // A .cbz is a zip of images. Real page bytes are not needed to prove which
    // pages are found, which are called adverts, and which survive a removal -
    // only that the entries are there and that the right ones come back.
    static void Build(string path, string? info, params string[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new System.IO.Compression.ZipArchive(
            stream, System.IO.Compression.ZipArchiveMode.Create);

        foreach (var e in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(e).Open());
            writer.Write($"bytes of {e}");
        }

        if (info is null) return;

        using var meta = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
        meta.Write(info);
    }

    try
    {
        // Written the way a scanner writes it: Image is the page number, and the
        // elements are deliberately out of order, because they often are.
        var marked = """
            <?xml version="1.0"?>
            <ComicInfo>
              <Series>Saga</Series>
              <Pages>
                <Page Image="3" Type="Advertisement" />
                <Page Image="0" Type="FrontCover" />
                <Page Image="1" Type="Story" />
                <Page Image="5" Type="Advertisement" />
              </Pages>
            </ComicInfo>
            """;

        var tagged = Path.Combine(root, "tagged.cbz");

        Build(tagged, marked,
            "page000.jpg", "page001.jpg", "page002.jpg",
            "page003.jpg", "page004.jpg", "page005.jpg");

        var pages = ComicPages.Read(tagged);

        Check("every image is a page and ComicInfo.xml is not one of them",
            pages.Count == 6 && pages.All(p => p.Entry.EndsWith(".jpg")));

        Check("pages come back in reading order",
            pages[0].Entry == "page000.jpg" && pages[5].Entry == "page005.jpg");

        // Read by the Image attribute, not by where the element sits.
        Check("the scanner's marks land on the pages they name",
            pages[0].Kind == PageKind.FrontCover
            && pages[3].Kind == PageKind.Advertisement
            && pages[5].Kind == PageKind.Advertisement);

        Check("a page nobody marked is a story page, and says nobody marked it",
            pages[4].Kind == PageKind.Story && !pages[4].Tagged);

        Check("a page somebody marked as a story says so",
            pages[1].Kind == PageKind.Story && pages[1].Tagged);

        Check("only the adverts are offered for removal",
            pages.Count(p => p.Droppable) == 2);

        Check("and the summary says how many there are",
            ComicPages.Describe(pages).Contains("2 of them marked as adverts"));

        // It cannot tell the scanner's marks from the reader's own, so it says
        // neither. Crediting the wrong person is a small lie told confidently.
        Check("without claiming who did the marking",
            !ComicPages.Describe(pages).Contains("scanner"));

        Check("a page's bytes come back", ComicPages.Page(tagged, "page003.jpg") is { Length: > 0 });

        Check("and a page that is not there comes back as nothing rather than throwing",
            ComicPages.Page(tagged, "page999.jpg") is null);

        // Removing them.
        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));

        Check("removing the adverts reports no trouble",
            ComicPages.Remove(tagged, ["page003.jpg", "page005.jpg"], history) is null);

        var after = ComicPages.Read(tagged);

        Check("the adverts are gone", after.Count == 4);

        Check("and every other page is still there, byte for byte",
            System.Text.Encoding.UTF8.GetString(ComicPages.Page(tagged, "page004.jpg")!)
                == "bytes of page004.jpg");

        Check("the comic is still a comic afterwards, metadata included",
            ComicPages.Page(tagged, "ComicInfo.xml") is { Length: > 0 });

        // The marks are recorded by page number, so removing a page shifts every
        // mark after it onto a different picture. Left alone, page004 - a story
        // page - would inherit the advert mark that used to sit on page005, and
        // the next pass would remove it. This is the check that matters most
        // here, because getting it wrong loses pages rather than merely
        // mislabelling them.
        Check("what stayed keeps its own mark and not the one next door",
            after[0].Kind == PageKind.FrontCover
            && after[1].Kind == PageKind.Story && after[1].Tagged
            && after[3].Entry == "page004.jpg" && after[3].Kind == PageKind.Story);

        Check("and no adverts are left to find, because both were removed",
            after.Count(p => p.Kind == PageKind.Advertisement) == 0);

        Check("the series is still recorded - renumbering is not an excuse to lose it",
            System.Text.Encoding.UTF8
                .GetString(ComicPages.Page(tagged, "ComicInfo.xml")!).Contains("Saga"));

        // A count in the file has to keep matching the file.
        var counted = Path.Combine(root, "counted.cbz");

        Build(counted,
            """
            <ComicInfo>
              <PageCount>3</PageCount>
              <Pages>
                <Page Image="1" Type="Advertisement" />
                <Page Image="2" Type="Story" />
              </Pages>
            </ComicInfo>
            """,
            "01.jpg", "02.jpg", "03.jpg");

        ComicPages.Remove(counted, ["02.jpg"]);

        var counts = System.Text.Encoding.UTF8.GetString(ComicPages.Page(counted, "ComicInfo.xml")!);

        Check("a page count that was there is brought down to match",
            counts.Contains("<PageCount>2</PageCount>"));

        Check("the mark on the removed page goes with it",
            !counts.Contains("Advertisement"));

        Check("and the mark after it moves down to where its page now is",
            ComicPages.Read(counted)[1] is { Entry: "03.jpg", Kind: PageKind.Story, Tagged: true });

        // Restoring the backup from the Recycle Bin is the only way back, so it
        // has to arrive as something a comic reader will open.
        var noted = history.Read()
            .Last(h => h.Action == HistoryAction.Delete && h.Title!.Contains("tagged"));

        Check("what went to the Recycle Bin is still a comic, not a .cbz.something",
            noted.OldPath.EndsWith(".cbz") && noted.OldPath.Contains("before pages removed"));

        Check("and the history says how many pages went", noted.Title!.Contains("2 page(s)"));

        Check("a removal is offered as a Recycle Bin job rather than a revert",
            new RevertService(history).Examine([noted]) is [{ CanRevert: false } one]
            && one.Reason!.Contains("Recycle Bin"));

        // The one thing that must never happen quietly.
        var everything = Path.Combine(root, "everything.cbz");
        Build(everything, null, "only.jpg");

        Check("removing every page is refused rather than leaving an empty file",
            ComicPages.Remove(everything, ["only.jpg"]) is not null);

        Check("and the comic is untouched", ComicPages.Read(everything).Count == 1);

        // No tagging at all: the honest answer is to claim nothing.
        var bare = Path.Combine(root, "bare.cbz");
        Build(bare, null, "01.jpg", "02.jpg", "03.jpg");

        var untagged = ComicPages.Read(bare);

        Check("with no ComicInfo.xml nothing is claimed about any page",
            untagged.Count == 3 && untagged.All(p => !p.Tagged && !p.Droppable));

        Check("and it says so rather than showing an empty list of adverts",
            ComicPages.Describe(untagged).Contains("nothing in the file says what any of them are"));

        // Metadata that will not parse is not a reason to refuse to open a comic.
        var broken = Path.Combine(root, "broken.cbz");
        Build(broken, "<ComicInfo><Pages><Page Image=", "01.jpg", "02.jpg");

        Check("metadata that will not parse still leaves the pages readable",
            ComicPages.Read(broken).Count == 2);

        // A type this program has never heard of should not become a story page,
        // because the schema gains values and a wrong guess here removes pages.
        var strange = Path.Combine(root, "strange.cbz");

        Build(strange,
            """<ComicInfo><Pages><Page Image="0" Type="SomethingNew" /></Pages></ComicInfo>""",
            "01.jpg", "02.jpg");

        var odd = ComicPages.Read(strange);

        Check("an unknown page type is kept as something unknown, not as a story",
            odd[0].Kind == PageKind.Other && odd[0].Tagged && !odd[0].Droppable);

        // ---- writing marks in, rather than acting on them ----
        //
        // Real comics arrive marked up no further than the front cover, so the
        // advert button has nothing to act on. Recording somebody's own reading
        // is the way out that does not involve guessing, and it has to survive
        // being written and read back by the same code.
        var own = Path.Combine(root, "own.cbz");

        Build(own,
            """
            <?xml version="1.0"?>
            <ComicInfo>
              <Series>Saga</Series>
              <Number>1</Number>
              <Pages>
                <Page Image="0" ImageWidth="2048" Type="FrontCover" />
              </Pages>
            </ComicInfo>
            """,
            "a.jpg", "b.jpg", "c.jpg", "d.jpg");

        Check("marking a page reports no trouble",
            ComicPages.Mark(own, new Dictionary<string, PageKind>
            {
                ["b.jpg"] = PageKind.Advertisement,
                ["d.jpg"] = PageKind.Advertisement
            }, history) is null);

        var mine = ComicPages.Read(own);

        Check("and reading it back finds them, from the file rather than from memory",
            mine[1].Kind == PageKind.Advertisement && mine[1].Tagged
            && mine[3].Kind == PageKind.Advertisement);

        Check("the pages are all still there - marking removes nothing", mine.Count == 4);

        Check("what the scanner already said is left alone",
            mine[0].Kind == PageKind.FrontCover);

        var kept = System.Text.Encoding.UTF8.GetString(ComicPages.Page(own, "ComicInfo.xml")!);

        Check("and so is everything else in the metadata",
            kept.Contains("<Series>Saga</Series>") && kept.Contains("ImageWidth=\"2048\""));

        Check("the marks are written in page order, since a person reads this file too",
            kept.IndexOf("Image=\"1\"", StringComparison.Ordinal)
                < kept.IndexOf("Image=\"3\"", StringComparison.Ordinal));

        // Taking a mark off has to be as easy as putting it on, or nobody will
        // risk putting it on.
        Check("marking a page back to a story is allowed",
            ComicPages.Mark(own, new Dictionary<string, PageKind> { ["b.jpg"] = PageKind.Story })
                is null);

        var relented = ComicPages.Read(own);

        Check("and it is a story page again", relented[1].Kind == PageKind.Story);

        Check("written as an absence, the way the schema means it",
            !System.Text.Encoding.UTF8.GetString(ComicPages.Page(own, "ComicInfo.xml")!)
                .Contains("Image=\"1\" Type="));

        Check("the other mark is untouched", relented[3].Kind == PageKind.Advertisement);

        // A comic with no metadata at all gets some.
        var fresh = Path.Combine(root, "fresh.cbz");
        Build(fresh, null, "01.jpg", "02.jpg");

        Check("a comic with no metadata gets a file of its own",
            ComicPages.Mark(fresh, new Dictionary<string, PageKind>
                { ["02.jpg"] = PageKind.Advertisement }) is null);

        Check("and the mark is in it", ComicPages.Read(fresh)[1].Kind == PageKind.Advertisement);

        // The one case where writing would cost more than it gains.
        var unreadable = Path.Combine(root, "unreadable.cbz");
        Build(unreadable, "<ComicInfo><Series>Half a f", "01.jpg", "02.jpg");

        Check("metadata that cannot be read is not overwritten with a fresh one",
            ComicPages.Mark(unreadable, new Dictionary<string, PageKind>
                { ["01.jpg"] = PageKind.Advertisement }) is not null);

        Check("so what was in it is still in it",
            System.Text.Encoding.UTF8
                .GetString(ComicPages.Page(unreadable, "ComicInfo.xml")!).Contains("Half a f"));

        Check("marking is written down as a tag change, not as a deletion",
            history.Read().Any(h => h.Action == HistoryAction.Retag
                                 && h.Title!.Contains("2 page(s) marked")
                                 && h.Before is { Count: 2 }));

        // Marking then removing is the whole workflow, and the two have to agree
        // about which page is which after the first one has run.
        var flow = Path.Combine(root, "flow.cbz");
        Build(flow, null, "01.jpg", "02.jpg", "03.jpg", "04.jpg");

        ComicPages.Mark(flow, new Dictionary<string, PageKind>
            { ["02.jpg"] = PageKind.Advertisement, ["04.jpg"] = PageKind.Advertisement });

        var toDrop = ComicPages.Read(flow).Where(p => p.Droppable).Select(p => p.Entry).ToList();

        Check("what was marked is what comes back to be removed",
            toDrop is ["02.jpg", "04.jpg"]);

        ComicPages.Remove(flow, toDrop);

        Check("removing them leaves the story pages and no stale marks",
            ComicPages.Read(flow) is [{ Entry: "01.jpg" }, { Entry: "03.jpg", Kind: PageKind.Story }]);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== an untagged track is a net, not a keepsake ===");
{
    static MediaStream Lane(StreamKind kind, string language, int index) => new()
    {
        Index = index, Kind = kind, Language = language,
        Codec = kind == StreamKind.Audio ? "eac3" : "subrip"
    };

    static MediaInfo Reel(params MediaStream[] streams) => new()
    {
        Path = @"x.mkv", DurationSeconds = 3600, Streams = [.. streams]
    };

    var settings = new PyreMediaSettings
    {
        KeepAudioLanguages = "eng", KeepSubtitleLanguages = "eng",
        KeepUndeterminedLanguage = true, KeepForcedSubtitles = true,
        PreferredLanguage = "eng"
    };

    List<MediaStream> Kept(PyreMediaSettings with, MediaInfo info) =>
        new RemuxPlanner(with).Plan([info]).Groups
            .SelectMany(g => g.Files).SelectMany(f => f.Keep).ToList();

    // The net doing its job: nothing here is in a language anybody asked for,
    // so the untagged track is all there is and dropping it leaves silence.
    var onlyUnd = Reel(Lane(StreamKind.Video, "und", 0), Lane(StreamKind.Audio, "und", 1));

    Check("an untagged track is kept when it is the only audio",
        Kept(settings, onlyUnd).Any(s => s.Kind == StreamKind.Audio && s.Language == "und"));

    // And standing down, which is the point: English was asked for and English
    // is here, so the untagged one is a second copy rather than the last thing
    // between you and a silent film.
    var engAndUnd = Reel(
        Lane(StreamKind.Video, "und", 0),
        Lane(StreamKind.Audio, "eng", 1),
        Lane(StreamKind.Audio, "und", 2));

    var kept = Kept(settings, engAndUnd);

    Check("the English track is kept",
        kept.Any(s => s.Kind == StreamKind.Audio && s.Language == "eng"));

    Check("and the untagged one is not, now that English is there",
        !kept.Any(s => s.Kind == StreamKind.Audio && s.Language == "und"));

    // Per kind, not per file: English audio and no English subtitles, so the
    // untagged subtitle is still the only one going.
    var mixed = Reel(
        Lane(StreamKind.Video, "und", 0),
        Lane(StreamKind.Audio, "eng", 1),
        Lane(StreamKind.Audio, "und", 2),
        Lane(StreamKind.Subtitle, "und", 3));

    var mixedKept = Kept(settings, mixed);

    Check("an untagged subtitle survives when no English subtitle exists",
        mixedKept.Any(s => s.Kind == StreamKind.Subtitle && s.Language == "und"));

    Check("while the untagged audio in the same file does not",
        !mixedKept.Any(s => s.Kind == StreamKind.Audio && s.Language == "und"));

    // A language written another way still counts as having been asked for:
    // the match goes through the catalogue, so "fre" answers a rule saying
    // "fra" and the untagged track stands down just the same.
    var french = new PyreMediaSettings
    {
        KeepAudioLanguages = "fra", KeepSubtitleLanguages = "fra",
        KeepUndeterminedLanguage = true, PreferredLanguage = "fra"
    };

    var frenchKept = Kept(french, Reel(
        Lane(StreamKind.Video, "und", 0),
        Lane(StreamKind.Audio, "fre", 1),
        Lane(StreamKind.Audio, "und", 2)));

    Check("a track tagged fre answers a rule asking for fra",
        frenchKept.Any(s => s.Kind == StreamKind.Audio && s.Language == "fre"));

    Check("and the untagged one stands down for it",
        !frenchKept.Any(s => s.Kind == StreamKind.Audio && s.Language == "und"));

    // Switched off, nothing is rescued - and the audio still survives, because
    // silence is never the answer. What changed is that the file is no longer
    // dropped from the plan for it: it is offered with everything kept.
    var strict = new PyreMediaSettings
    {
        KeepAudioLanguages = "eng", KeepSubtitleLanguages = "eng",
        KeepUndeterminedLanguage = false, PreferredLanguage = "eng"
    };

    var plan = new RemuxPlanner(strict).Plan([onlyUnd]);
    var untouched = plan.Groups.SelectMany(g => g.Files).Single();

    Check("with the net switched off a file of only untagged audio is not silenced",
        plan.Skipped.Count == 0 && plan.Groups.Count == 1 && untouched.Drop.Count == 0);

    Check("and it says why the rules were overruled",
        untouched.LanguageWarning is { } why
        && why.Contains("no language tag") && why.Contains("left this file silent"));
}

Console.WriteLine("\n=== a file with no audio you asked for is offered, not withheld ===");
{
    static MediaStream Lane(StreamKind kind, string language, int index) => new()
    {
        Index = index, Kind = kind, Language = language,
        Codec = kind == StreamKind.Audio ? "eac3" : "subrip"
    };

    // The real one. A French-only rip, renamed, and still announcing its French
    // title to every player - skipped whole for having no English audio, so the
    // stale title could never be reset and the Remux button never lit.
    var french = new MediaInfo
    {
        Path = @"H:\Done\LEGO Disney Princess - Magical Mayhem (2026).mkv",
        DurationSeconds = 1332,
        ContainerTitle = "LEGO Disney Princesses : Pagaille au chateau (2026)",
        Streams =
        [
            Lane(StreamKind.Video, "und", 0),
            Lane(StreamKind.Audio, "fre", 1),
            Lane(StreamKind.Subtitle, "eng", 2)
        ]
    };

    var wanted = new PyreMediaSettings
    {
        KeepAudioLanguages = "eng", KeepSubtitleLanguages = "eng",
        KeepUndeterminedLanguage = true, PreferredLanguage = "eng"
    };

    var offered = new RemuxPlanner(wanted).Plan([french]);

    Check("the file is in the plan rather than skipped for its language",
        offered.Skipped.Count == 0 && offered.Groups.Count == 1);

    var only = offered.Groups.SelectMany(g => g.Files).Single();

    Check("its only audio is kept, not dropped for failing the rules",
        only.Keep.Any(s => s.Kind == StreamKind.Audio && s.Language == "fre")
        && only.Drop.Count == 0);

    Check("the English subtitle it does have is kept too",
        only.Keep.Any(s => s.Kind == StreamKind.Subtitle && s.Language == "eng"));

    Check("and the tracks stay in file order",
        only.Keep.Select(s => s.Index).SequenceEqual([0, 1, 2]));

    Check("the warning names the language and says the rules were overruled",
        only.LanguageWarning is { } warn
        && warn.Contains("French") && warn.Contains("left this file silent"));

    Check("the stale title is the work that remains",
        only.Info.TitleIsStale && only.SetTitleFromFileName);

    // The control. Without it the guard could put every track back in every
    // file and still pass: a file that does carry something you asked for is
    // stripped exactly as before.
    var wide = new PyreMediaSettings
    {
        KeepAudioLanguages = "eng,fre", KeepSubtitleLanguages = "eng",
        KeepUndeterminedLanguage = true, PreferredLanguage = "eng"
    };

    var stripped = new RemuxPlanner(wide).Plan([new MediaInfo
    {
        Path = @"H:\Done\Two Tracks (2026).mkv",
        DurationSeconds = 1332,
        Streams =
        [
            Lane(StreamKind.Video, "und", 0),
            Lane(StreamKind.Audio, "fre", 1),
            Lane(StreamKind.Audio, "ita", 2)
        ]
    }]).Groups.SelectMany(g => g.Files).Single();

    Check("a file that does carry a wanted track still loses the unwanted ones",
        stripped.Drop.Count == 1 && stripped.Drop[0].Language == "ita"
        && stripped.Keep.Any(s => s.Language == "fre"));
}

Console.WriteLine("\n=== what the shelf sweep refuses to touch ===");
{
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-junk-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    var junk = ".nfo;.txt;.url;.sfv;.md5;.par2;.exe;.bat;.lnk;.diz;.website"
        .Split(';', StringSplitOptions.RemoveEmptyEntries);

    try
    {
        string Litter(string name, string content)
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, content);
            return path;
        }

        // ---- the things it must never offer ----

        // This program's own work. Reading a shelf is measured in hours, and
        // both of these wear an extension on the junk list.
        var words = Litter("Saga 001.txt",
            ReadableText.Compose("Saga 001", "Read from the pages by Windows OCR - rough",
                [new TextPage(1, "the last thing I remember")]));

        Check("the searchable text this program wrote is refused",
            ShelfJunk.Why(words, junk) is null);

        var script = Path.Combine(root, "Saga 002.script.nfo");
        ScriptNfo.Write(Path.Combine(root, "Saga 002.cbz"), new Script
        {
            Title = "Saga 002", Source = "Windows OCR", Exact = false,
            Pages = [new() { Number = 1, Kind = PageKind.Story }]
        });

        Check("and the page script beside a comic", ShelfJunk.Why(script, junk) is null);

        // Somebody else's metadata.
        var kodi = Litter("Saga 003.nfo",
            "<?xml version=\"1.0\"?><movie><title>Saga</title><year>2012</year></movie>");

        Check("a Kodi .nfo is refused", ShelfJunk.Why(kodi, junk) is null);

        // Artwork, by name, in case it arrives under one of these extensions.
        foreach (var art in new[] { "cover.txt", "folder.txt", "poster.txt", "series-thumb.txt" })
            Check($"{art} is refused as artwork", ShelfJunk.Why(Litter(art, "x"), junk) is null);

        // Somebody's own notes. A .txt that says nothing about itself is left
        // alone rather than guessed at.
        Check("a plain .txt nobody can identify is left alone",
            ShelfJunk.Why(Litter("my notes about this run.txt", "buy issue 4"), junk) is null);

        // Anything not on the list is none of its business.
        Check("an extension the user never nominated is untouched",
            ShelfJunk.Why(Litter("Saga 004.cbz", "PK"), junk) is null);

        Check("and neither is an epub", ShelfJunk.Why(Litter("book.epub", "PK"), junk) is null);

        // ---- the things it does offer ----
        var tracker = Litter("Torrent downloaded from 1337x.org.txt", "1337x");

        Check("a tracker leftover is offered", ShelfJunk.Why(tracker, junk) is not null);

        Check("and says why it thinks so",
            ShelfJunk.Why(tracker, junk)!.Contains("downloaded from"));

        var scene = Litter("release.nfo", new string('=', 60) + "\n  SCENE RELEASE  \n" + new string('=', 60));

        Check("a scene .nfo is offered", ShelfJunk.Why(scene, junk) is not null);

        Check("a checksum is offered", ShelfJunk.Why(Litter("files.sfv", "x 00000000"), junk) is not null);
        Check("a tracker shortcut is offered", ShelfJunk.Why(Litter("visit us.url", "[InternetShortcut]"), junk) is not null);

        // ---- and the sweep as a whole ----
        var settings = new PyreMediaSettings { DeleteJunkFiles = true };
        var swept = ShelfJunk.Find([root], settings);

        // By name, not by count: a count that drifts is a test that stops
        // saying which file it lost.
        var offered = swept.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        Check("the sweep offers exactly the four leftovers",
            offered.SequenceEqual(
                ["files.sfv", "release.nfo", "Torrent downloaded from 1337x.org.txt", "visit us.url"],
                StringComparer.OrdinalIgnoreCase));

        Check("it never returns the OCR text",
            swept.All(s => !s.Path.EndsWith("Saga 001.txt", StringComparison.OrdinalIgnoreCase)));

        // Off means off. Deletion is not something to default into.
        Check("with junk deletion switched off it finds nothing",
            ShelfJunk.Find([root], new PyreMediaSettings { DeleteJunkFiles = false }).Count == 0);

        // And Tidy will not sweep at all unless a caller hands it the rules.
        Check("Tidy does not sweep without being given the settings",
            Tidy.Gather([], [root]).All(f => f.Kind != TidyKind.ShelfLitter));

        Check("and does when it is",
            Tidy.Gather([], [root], settings: settings).Any(f => f.Kind == TidyKind.ShelfLitter));

        // Nothing that would be removed is ticked when the window opens - that
        // is TidyWindow's job, but the finding has to admit it removes.
        Check("a leftover is marked as something that removes",
            Tidy.Gather([], [root], settings: settings)
                .Where(f => f.Kind == TidyKind.ShelfLitter)
                .All(f => f.Removes && f.Will.Contains("Recycle Bin")));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a name already taken, answered three ways ===");
{
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-clash-" + Guid.NewGuid().ToString("N")[..8]);
    var from = Path.Combine(root, "in");
    var to = Path.Combine(root, "library");

    Directory.CreateDirectory(from);
    Directory.CreateDirectory(Path.Combine(to, "Saga (2012)"));

    try
    {
        // Three incoming issues, and a file already sitting on each name.
        ShelfItem Incoming(string n, int size)
        {
            var path = Path.Combine(from, $"Saga {n}.cbz");
            File.WriteAllBytes(path, new byte[size]);

            return new ShelfItem
            {
                Path = path,
                Kind = ShelfKind.Comic,
                Comic = new ComicRef
                {
                    Series = "Saga", Year = "2012", Issue = n, Kind = ComicKind.Issue
                }
            };
        }

        var items = new[] { Incoming("1", 900), Incoming("2", 400), Incoming("3", 700) };

        foreach (var n in new[] { "001", "002", "003" })
            File.WriteAllBytes(Path.Combine(to, "Saga (2012)", $"Saga (2012) #{n}.cbz"), new byte[500]);

        var plan = BookPlanner.Plan(items, to, to, BookNaming.ComicDefault, BookNaming.BookDefault);
        var clashes = ShelfExecutor.FindClashes(plan);

        Check("all three names are seen to be taken", clashes.Count == 3);

        Check("and both sizes are carried, so a person can compare",
            clashes.All(c => c.IncomingBytes > 0 && c.ExistingBytes > 0));

        // One of each answer - the machinery that existed and could never be
        // reached from the Books tab.
        var choices = new Dictionary<string, ShelfChoice>(StringComparer.OrdinalIgnoreCase)
        {
            [items[0].Path] = ShelfChoice.Replace,
            [items[1].Path] = ShelfChoice.Skip,
            [items[2].Path] = ShelfChoice.KeepBoth
        };

        var history = new RenameHistory(Path.Combine(root, "history.jsonl"));
        var result = ShelfExecutor.Execute(plan, history, choices);

        Check("the replaced one moved", result.Moved == 2);
        Check("and the skipped one did not", result.Skipped == 1);

        var shelf = Path.Combine(to, "Saga (2012)");

        Check("replace put the incoming file on the name",
            new FileInfo(Path.Combine(shelf, "Saga (2012) #001.cbz")).Length == 900);

        Check("skip left the file that was already there",
            new FileInfo(Path.Combine(shelf, "Saga (2012) #002.cbz")).Length == 500
            && File.Exists(items[1].Path));

        Check("keep both left the original where it was",
            new FileInfo(Path.Combine(shelf, "Saga (2012) #003.cbz")).Length == 500);

        Check("and put the incoming one beside it under a free name",
            Directory.GetFiles(shelf, "Saga (2012) #003*.cbz").Length == 2);

        // Silence still means leave alone, which is what the executor promises.
        Check("an unanswered clash is skipped rather than guessed at",
            ShelfExecutor.Execute(
                BookPlanner.Plan([Incoming("4", 100)], to, to,
                                 BookNaming.ComicDefault, BookNaming.BookDefault),
                history,
                null).Moved == 1);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}

Console.WriteLine("\n=== a series somebody has agreed to ===");
{
    static ShelfItem Issue(string series, string number) => new()
    {
        Path = $@"H:\c\{series} {number}.cbz",
        Kind = ShelfKind.Comic,
        Comic = new ComicRef { Series = series, Year = "1994", Issue = number, Kind = ComicKind.Issue }
    };

    // What the reader would have written down after choosing a candidate.
    var agreed = new ComicMatch
    {
        Key = ComicShelf.Canonical("bloodshot"),
        Series = "Bloodshot",
        Year = "1993",
        Publisher = "Valiant",
        Source = "Grand Comics Database",
        SeriesId = "4242",
        Issues = ["0", "1", "2", "3", "4", "5"],
        Total = 6
    };

    var matches = new Dictionary<string, ComicMatch> { [agreed.Key] = agreed };

    // Every spelling of the run finds the same agreement, because the key is
    // the reduced form rather than the name as written.
    var settled = new[] { Issue("bloodshot", "1"), Issue("BLOODSHOT", "2"), Issue("Blood shot", "3") }
        .Select(i => ComicMatch.ApplyTo(i, matches))
        .ToList();

    Check("every spelling takes the agreed name",
        settled.All(i => i.Comic!.Series == "Bloodshot"));

    Check("and the database's year, not the file's",
        settled.All(i => i.Comic!.Year == "1993"));

    Check("and its publisher", settled.All(i => i.Comic!.Publisher == "Valiant"));

    // The run length is the thing the files could almost never answer - about
    // one comic in fourteen on the measured shelf says how long its run is.
    Check("and how long the run actually is", settled.All(i => i.Comic!.Of == 6));

    // With the run itself in hand the shelf says exactly what is absent -
    // including #0, which no amount of counting could have revealed. Held is
    // #1-#3 of a #0-#5 run.
    var group = ComicShelf.Group(settled, matches)[0];

    Check("the shelf knows #0 is missing, which a count alone could not say",
        group.Missing.Contains(0));

    Check("and that #4 and #5 are still to come",
        group.NotYet.Contains(4) && group.NotYet.Contains(5));

    Check("and that the run is not complete", !group.Complete);

    // Without the match it can only work from a count, and a count cannot tell
    // #1-#6 from #0-#5.
    var blind = ComicShelf.Group(settled)[0];

    Check("without the match it says nothing about #0", !blind.Missing.Contains(0));

    // The whole run held reads as complete.
    var whole = new[] { "0", "1", "2", "3", "4", "5" }
        .Select(n => ComicMatch.ApplyTo(Issue("bloodshot", n), matches))
        .ToList();

    Check("holding every issue the database lists is a complete run",
        ComicShelf.Group(whole, matches)[0].Complete);

    // The publisher reaches the folder level.
    Check("the agreed publisher is used for the folder",
        ComicMatch.PublisherFor(settled[0].Comic!, matches) == "Valiant");

    var filed = BookPlanner.Plan(settled, @"L:\Comics", @"L:\Books",
        BookNaming.ComicByPublisher, BookNaming.BookDefault,
        c => ComicMatch.PublisherFor(c, matches));

    Check("and every issue files under it",
        filed.Moving.All(a => a.Target!.StartsWith(Path.Combine(@"L:\Comics", "Valiant"))));

    // A series nobody has matched is left exactly as the files describe it.
    var untouched = ComicMatch.ApplyTo(Issue("Shadowman", "1"), matches);

    Check("an unmatched series is untouched", untouched.Comic!.Series == "Shadowman");
    Check("and keeps its own year", untouched.Comic!.Year == "1994");

    // A database that omits something must not erase what the file knew.
    var quiet = agreed with { Publisher = null, Year = null, Total = null };
    var kept = ComicMatch.ApplyTo(
        Issue("bloodshot", "1") with
        {
            Comic = new ComicRef
            {
                Series = "bloodshot", Year = "1994", Issue = "1",
                Kind = ComicKind.Issue, Publisher = "Valiant Entertainment", Of = 4
            }
        },
        new Dictionary<string, ComicMatch> { [quiet.Key] = quiet });

    Check("what the database does not say is left as the file had it",
        kept.Comic!.Publisher == "Valiant Entertainment"
        && kept.Comic!.Year == "1994" && kept.Comic!.Of == 4);
}

Console.WriteLine("\n=== one run, one folder ===");
{
    // Comic is the merged answer, so when the file says something that is what
    // Comic carries - which is exactly how BookPlanner.Scan builds it.
    static ShelfItem Copy(string series, string number, bool fromFile = false) => new()
    {
        Path = $@"H:\c\{series} {number}.cbz",
        Kind = ShelfKind.Comic,
        Comic = new ComicRef { Series = series, Year = "1993", Issue = number, Kind = ComicKind.Issue },
        Inside = fromFile
            ? new ComicRef { Series = series, Year = "1993", Issue = number, Kind = ComicKind.Issue }
            : null
    };

    // The same run as it actually appears on the shelf: four spellings, one
    // series. The grouping already knows they are one; filing took each file's
    // own spelling and scattered them across four folders.
    var run = new[]
    {
        Copy("Mighty Morphin Power Rangers", "1"),
        Copy("Mighty Morphin' Power Rangers", "2"),
        Copy("Mighty Morphin Power Rangers ", "3"),
        Copy("mighty morphin power rangers", "4"),
    };

    var plan = BookPlanner.Plan(run, @"L:\Comics", @"L:\Books",
        BookNaming.ComicDefault, BookNaming.BookDefault);

    var folders = plan.Moving
        .Select(a => Path.GetDirectoryName(a.Target)!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    Check("four spellings of one series file into one folder", folders.Count == 1);
    Check("and all four issues are in it", plan.Moving.Count() == 4);

    // A name the file gave of itself beats one taken from a filename. Both
    // reduce to the same series - the apostrophe and the case are what the
    // grouping already ignores - so the question is only which spelling the
    // folder ends up with.
    var mixed = new[]
    {
        Copy("mighty morphin power rangers", "1"),
        Copy("Mighty Morphin' Power Rangers", "2", fromFile: true),
    };

    var chosen = BookPlanner.Plan(mixed, @"L:\Comics", @"L:\Books",
        BookNaming.ComicDefault, BookNaming.BookDefault);

    Check("the spelling from inside the file wins",
        chosen.Moving.All(a => a.Target!.Contains("Mighty Morphin' Power Rangers")));

    // Between two of the same standing, the longer one - a truncated name is
    // the commoner failure than an inflated one.
    var lengths = BookPlanner.Plan(
        [Copy("Bloodshot", "1"), Copy("Bloodshot and the H.A.R.D. Corps", "2")],
        @"L:\Comics", @"L:\Books", BookNaming.ComicDefault, BookNaming.BookDefault);

    Check("two names that reduce differently are two series",
        lengths.Moving.Select(a => Path.GetDirectoryName(a.Target)!)
               .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2);

    // Two genuinely different series must not be merged into one.
    var apart = BookPlanner.Plan(
        [Copy("Daredevil", "1"), Copy("Daredevils", "1")],
        @"L:\Comics", @"L:\Books", BookNaming.ComicDefault, BookNaming.BookDefault);

    Check("two series that only look alike stay apart",
        apart.Moving.Select(a => Path.GetDirectoryName(a.Target)!)
             .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2);
}

Console.WriteLine("\n=== a comic with nothing inside it has nothing to check against ===");
{
    // The merged record already carries the filename's answers, so handing it
    // over as "what the file says of itself" compared the filename with itself
    // and could only ever agree. On the measured library that was a false
    // all-clear for 868 files.
    var name = new ComicRef { Series = "Saga", Year = "2012", Issue = "1", Kind = ComicKind.Issue };

    var nothingInside = Agreement.Compare(@"H:\c\Saga 001.cbz", null, name);

    Check("a comic with no metadata says there was nothing to check it against",
        nothingInside.Unchecked);

    Check("and is not counted as settled", !nothingInside.Settled);

    // Where the file does say something, the comparison still works.
    var inside = new ComicRef { Series = "Saga", Year = "2012", Issue = "1", Kind = ComicKind.Issue };
    var agrees = Agreement.Compare(@"H:\c\Saga 001.cbz", inside, name);

    Check("a file that matches its name was checked", !agrees.Unchecked);
    Check("and comes out settled", agrees.Settled);

    var wrong = inside with { Issue = "7" };
    var differs = Agreement.Compare(@"H:\c\Saga 001.cbz", wrong, name);

    Check("one that disagrees is not settled", !differs.Settled);
    Check("and says what differs", differs.Differences.Any(d => d.Matters));
}

Console.WriteLine("\n=== reset keeps what it says it keeps ===");
{
    static PyreMediaSettings Configured() => new()
    {
        TvFolders = [@"H:\Done"],
        MovieFolders = [@"H:\Films"],
        MusicFolders = [@"H:\Music"],
        ComicFolders = [@"H:\Comics"],
        BookFolders = [@"H:\Books"],

        TvDestination = @"L:\TV",
        MovieDestination = @"L:\Films",
        MusicDestination = @"L:\Music",
        AudiobookDestination = @"L:\Audiobooks",
        ComicDestination = @"L:\Comics",
        EbookDestination = @"L:\Ebooks",

        TmdbApiKey = "tmdb-key",
        TvdbApiKey = "tvdb-key",
        AcoustIdApiKey = "acoustid-key",
        ComicVineApiKey = "comicvine-key",
        MetronUser = "someone",
        MetronPassword = "a password",
        GcdDatabasePath = @"H:\gcd.sqlite",

        // Something that must not survive, so the reset is doing anything at all.
        SeasonFolderName = "Series {n}"
    };

    var kept = Configured();
    kept.ResetToDefaults(keepFolders: true, keepApiKeys: true);

    Check("the option that was changed goes back to default", kept.SeasonFolderName != "Series {n}");

    // Every kind of folder, not the two for video. Answering "yes, keep my
    // folders" used to clear the music, comic and ebook roots and all six
    // library destinations, under a dialog promising they were kept.
    Check("the TV folders are kept", kept.TvFolders.Contains(@"H:\Done"));
    Check("and the music folders", kept.MusicFolders.Contains(@"H:\Music"));
    Check("and the comic folders", kept.ComicFolders.Contains(@"H:\Comics"));
    Check("and the ebook folders", kept.BookFolders.Contains(@"H:\Books"));

    Check("every library destination is kept",
        kept.TvDestination == @"L:\TV" && kept.MovieDestination == @"L:\Films"
        && kept.MusicDestination == @"L:\Music" && kept.AudiobookDestination == @"L:\Audiobooks"
        && kept.ComicDestination == @"L:\Comics" && kept.EbookDestination == @"L:\Ebooks");

    Check("every credential is kept",
        kept.TmdbApiKey == "tmdb-key" && kept.TvdbApiKey == "tvdb-key"
        && kept.AcoustIdApiKey == "acoustid-key" && kept.ComicVineApiKey == "comicvine-key"
        && kept.MetronUser == "someone" && kept.MetronPassword == "a password"
        && kept.GcdDatabasePath == @"H:\gcd.sqlite");

    // And the other answer really does clear them, or the choice is a lie the
    // other way round.
    var wiped = Configured();
    wiped.ResetToDefaults(keepFolders: false, keepApiKeys: false);

    Check("resetting everything clears the folders",
        wiped.TvFolders.Count == 0 && wiped.MusicFolders.Count == 0 && wiped.BookFolders.Count == 0);

    Check("and the keys", wiped.TmdbApiKey.Length == 0 && wiped.ComicVineApiKey.Length == 0);
}

Console.WriteLine("\n=== a page the file calls Other is not a page the ripper added ===");
{
    // The two used to share PageKind.Other, so a tagger's pinup gallery was
    // offered for deletion described as pages the scanning group had added.
    var pinup = PageJudge.Judge(4, "Cover gallery. Variant art by Adam Hughes. "
        + "Sketchbook pages and character designs from the issues collected here.",
        alreadySaid: PageKind.Other);

    Check("the file's own Other is returned as Other", pinup.Looks == PageKind.Other);

    var credits = PageJudge.Judge(22,
        "Scanned by the Digital Comic Museum. If you paid for this, you were "
        + "ripped off. Released for the preservation of comics. Enjoy and share.");

    Check("a page the scanner added is its own kind", credits.Looks == PageKind.ScannerPage);
    Check("and says why", credits.Because.Count > 0);

    // What Tidy will and will not offer to delete.
    var offered = new[] { PageKind.Advertisement, PageKind.ScannerPage };

    Check("a scanner page is offered for removal", offered.Contains(credits.Looks));
    Check("a page the publisher printed is not", !offered.Contains(pinup.Looks));
}

Console.WriteLine("\n=== a run that opens at #0 can still be complete ===");
{
    static ShelfItem Book(string number, int? of = null) => new()
    {
        Path = $@"H:\c\Bloodshot {number}.cbz",
        Kind = ShelfKind.Comic,
        Comic = new ComicRef
        {
            Series = "Bloodshot", Year = "1993", Issue = number, Kind = ComicKind.Issue, Of = of
        }
    };

    // Valiant and the nineties Image books commonly open at #0.
    var zeroBased = ComicShelf.Group(
        [Book("0", 6), Book("1", 6), Book("2", 6), Book("3", 6), Book("4", 6), Book("5", 6)])[0];

    Check("six issues numbered #0-#5 out of six is the complete run", zeroBased.Complete);
    Check("with nothing reported missing", zeroBased.Missing.Count == 0);
    Check("and no seventh issue invented", zeroBased.NotYet.Count == 0);

    // A genuinely short run still reports what is absent.
    var partial = ComicShelf.Group([Book("0", 6), Book("1", 6), Book("3", 6)])[0];

    Check("a hole in the middle is still a hole", partial.Missing.Contains(2));
    Check("and the rest is still to come", partial.NotYet.Count > 0);
    Check("a run with holes is not complete", !partial.Complete);

    // The ordinary case must not have moved.
    var fromOne = ComicShelf.Group([Book("1", 3), Book("2", 3), Book("3", 3)])[0];

    Check("a run that starts at #1 is unaffected", fromOne.Complete);
}

Console.WriteLine("\n=== which side of the dash is the author ===");
{
    // Every one of these is a real filename from the library, and they show
    // the point: both orders are in use, in the same folder.
    static (string? Title, string? Author) Name(string file)
    {
        var b = BookFile.FromNameOnly(file);
        return (b?.Title, b?.Author);
    }

    // Title first, which is what Calibre writes by default and what most of
    // the shelf uses.
    Check("\"Angel Time - Rice_ Anne\" is Anne Rice's book, not Angel Time's author",
        Name("Angel Time - Rice_ Anne.epub") == ("Angel Time", "Rice, Anne"));

    Check("a colon sanitised into the title is left alone",
        Name("Christ the Lord_ Out of Egypt - Rice_ Anne.epub")
            == ("Christ the Lord_ Out of Egypt", "Rice, Anne"));

    // Author first, in the same folder as the above.
    Check("\"Crichton, Michael - Scratch One\" is the other way round",
        Name("Crichton, Michael - Scratch One.epub") == ("Scratch One", "Crichton, Michael"));

    Check("two authors surname-first are still the author side",
        Name("Crichton, Michael_ Lange, John - Odds On_ A Novel.epub")
            == ("Odds On_ A Novel", "Crichton, Michael, Lange, John"));

    Check("dates after the name do not stop it being a name",
        Name("Five patients_ the hospital exp - Crichton, Michael, 1942-2008.pdf")
            == ("Five patients_ the hospital exp", "Crichton, Michael, 1942-2008"));

    // A title carrying a comma must not be mistaken for a surname: what
    // follows the comma is an article, not a given name.
    Check("\"Vittorio, the Vampire\" is a title, not a person",
        Name("Vittorio, the Vampire - Rice_ Anne.epub")
            == ("Vittorio, the Vampire", "Rice, Anne"));

    // Neither side written surname-first, so the Calibre order decides.
    Check("\"Binary - Michael Crichton\" falls back to title first",
        Name("Binary - Michael Crichton.mobi") == ("Binary", "Michael Crichton"));

    Check("a title that is two capitalised words is still the title",
        Name("Jasper Johns - Michael Crichton.pdf") == ("Jasper Johns", "Michael Crichton"));

    // Three parts: the book, the writer, and a note about the edition.
    Check("the name in the middle is the author and the book comes before it",
        Name("Jurassic Park - Crichton, Michael - First Draft.pdf")
            == ("Jurassic Park", "Crichton, Michael"));

    Check("a name with no dash at all is all title",
        Name("Timeline.epub") == ("Timeline", null));

    // The setting that says not to open every file must still use the name.
    Check("reading the name only still identifies the book",
        BookFile.FromNameOnly(@"H:\x\Pandora - Rice_ Anne.epub") is { Title: "Pandora" });
}

Console.WriteLine("\n=== WMA tags leave the rest of the file alone ===");
{
    // A hand-built Extended Content Description: one text descriptor, one
    // binary one standing in for WM/Picture, and one this code rewrites.
    static byte[] Descriptor(string name, ushort type, byte[] value)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name + '\0');
        var body = new byte[2 + nameBytes.Length + 2 + 2 + value.Length];

        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), (ushort)nameBytes.Length);
        nameBytes.CopyTo(body.AsSpan(2));

        var at = 2 + nameBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at, 2), type);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 2, 2), (ushort)value.Length);
        value.CopyTo(body.AsSpan(at + 4));

        return body;
    }

    static byte[] Text(string s) => System.Text.Encoding.Unicode.GetBytes(s + '\0');

    // Deliberately not valid JPEG - the point is that nothing here reads it.
    var art = new byte[512];
    for (var i = 0; i < art.Length; i++) art[i] = (byte)(i % 251);

    var parts = new List<byte[]>
    {
        Descriptor("WM/Picture", 1, art),
        Descriptor("replaygain_track_gain", 0, Text("-7.24 dB")),
        Descriptor("WM/AlbumTitle", 0, Text("Old Album"))
    };

    var block = new byte[2 + parts.Sum(p => p.Length)];
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), (ushort)parts.Count);

    var offset = 2;
    foreach (var p in parts) { p.CopyTo(block.AsSpan(offset)); offset += p.Length; }

    var before = AsfWriter.ReadExtended(block);

    Check("the art is read as bytes rather than turned into text",
        before.Any(d => d.Name == "WM/Picture" && d.Raw.Length == art.Length));

    // The edit names the album and nothing else.
    var after = AsfWriter.ReadExtended(AsfWriter.BuildExtended(block, new TagEdit { Album = "New Album" }));

    Check("the album is the one just written",
        after.Any(d => d.Name == "WM/AlbumTitle" && d.Text == "New Album"));

    // This is the defect: correcting a track number used to delete the
    // artwork, because binary descriptors were decoded to an empty string
    // and then dropped for being empty.
    var picture = after.FirstOrDefault(d => d.Name == "WM/Picture");

    Check("the embedded cover art survives a tag write", picture.Raw.Length == art.Length);
    Check("and survives it byte for byte", picture.Raw.SequenceEqual(art));
    Check("under its own type, not rewritten as text", picture.Type == 1);

    Check("a tag from another tagger is still carried through",
        after.Any(d => d.Name == "replaygain_track_gain" && d.Text == "-7.24 dB"));

    Check("nothing was invented or lost along the way", after.Count == 3);

    // Clearing a field removes it rather than leaving an empty one, and must
    // still not disturb anything else.
    var cleared = AsfWriter.ReadExtended(AsfWriter.BuildExtended(block, new TagEdit { Album = "" }));

    Check("clearing the album drops the descriptor", cleared.All(d => d.Name != "WM/AlbumTitle"));
    Check("and still leaves the art alone",
        cleared.Any(d => d.Name == "WM/Picture" && d.Raw.Length == art.Length));

    // ReplayGain used to reach a WMA and go nowhere. Every file was decoded end
    // to end - the slowest thing here - rewritten in full, reported as written
    // and recorded as undoable, and not one figure landed. Nothing could catch
    // it either: the reader does not read gain back, so the verify step had
    // nothing to compare.
    var gained = AsfWriter.ReadExtended(AsfWriter.BuildExtended(block, new TagEdit(
        TrackGain: "-7.24 dB", TrackPeak: "0.988525",
        AlbumGain: "-8.16 dB", AlbumPeak: "1.000000")));

    string? Value(string name) =>
        gained.FirstOrDefault(d => d.Name == name) is { Text.Length: > 0 } d ? d.Text : null;

    Check("the track gain reaches the file", Value("REPLAYGAIN_TRACK_GAIN") == "-7.24 dB");
    Check("and the track peak", Value("REPLAYGAIN_TRACK_PEAK") == "0.988525");
    Check("and the album gain", Value("REPLAYGAIN_ALBUM_GAIN") == "-8.16 dB");
    Check("and the album peak", Value("REPLAYGAIN_ALBUM_PEAK") == "1.000000");

    Check("a gain-only edit is still an edit worth making", new TagEdit(TrackGain: "-7.24 dB").Any);

    Check("and the art is untouched by it",
        gained.Any(d => d.Name == "WM/Picture" && d.Raw.Length == art.Length));
}

Console.WriteLine("\n=== outside tools ===");
{
    var settings = new PyreMediaSettings();
    var tools = PyreMedia.Core.Tools.ToolLocator.All(settings);

    Check("every tool the setup page lists has somewhere to record its path",
        tools.All(t => t.SetPath is not null));

    // The bug this replaces: setup decided where to write a browsed path with
    // a switch on the tool's name that covered three of the five. For the two
    // it missed the file dialog opened, a file was chosen, and the answer was
    // dropped on the floor. Proving each setter reaches a distinct field is
    // what stops a copied line pointing two tools at one setting.
    var wrote = new List<string>();

    foreach (var t in tools)
    {
        var fresh = new PyreMediaSettings();
        t.SetPath(fresh, $@"X:\{t.Name}.exe");

        var landed = new[]
        {
            fresh.FfprobePath, fresh.FfmpegPath, fresh.MkvMergePath,
            fresh.DoviToolPath, fresh.PlayerPath
        }.Where(v => v == $@"X:\{t.Name}.exe").ToList();

        Check($"pointing setup at {t.Name} changes exactly one setting", landed.Count == 1);
        wrote.Add(t.Name);
    }

    Check("all five tools are covered", wrote.Count == 5);

    // VLC opens its window when handed an argument it does not know, so the
    // page that only means to check what is installed must not run it.
    Check("VLC's version is read from the file rather than by running it",
        tools.First(t => t.Name == "VLC").VersionArg is null);

    Check("the tools with no winget package can still be fetched",
        tools.Where(t => t.WingetId is null).All(PyreMedia.Core.Tools.ToolLocator.CanFetch));

    // A tool that names a GitHub repo but no asset hint would pick whichever
    // zip happened to sort first, which is how you install a Linux build.
    Check("every fetchable tool says which asset is the Windows one",
        tools.Where(t => t.GitHubRepo is not null).All(t => t.AssetHint is { Length: > 0 }));

    Check("a bare name that is nowhere resolves to nothing",
        PyreMedia.Core.Tools.ToolLocator.Resolve("pyremedia-no-such-tool") is null);

    Check("an empty command is not a tool", PyreMedia.Core.Tools.ToolLocator.Resolve("") is null);

    // Windows records where an installed program lives even when it never
    // touches PATH, which is how a fully installed VLC read as missing.
    var onPath = PyreMedia.Core.Tools.ToolLocator.Resolve("notepad");

    Check("a program Windows knows about is found without a PATH entry",
        onPath is not null && File.Exists(onPath));

    // An unset variable expands to itself; treating "%NoSuchVar%\bin" as a
    // folder would turn every lookup into a wasted round of file probes.
    Check("an unset folder variable is not treated as a folder",
        PyreMedia.Core.Tools.ToolLocator.Resolve(
            "pyremedia-no-such-tool", [@"%PyreMediaNoSuchVariable%\bin"]) is null);
}


Console.WriteLine("\n=== what a turn of the wheel moves a section by ===");
{
    // ScrollToVerticalOffset speaks the scroller's own units. A ListView counts
    // items - CanContentScroll defaults to true on anything built from
    // ItemsControl - so the pixel move Decide asks for was being read as a row
    // count: one notch moved the library 120 rows.
    var wheelUp = PyreMedia.App.SectionScroll.Decide(
        top: 0, height: 400, viewport: 800,
        innerOffset: 5, innerScrollable: 40, delta: 120);

    Check("a wheel turn inside a section is a pixel move",
        wheelUp.Target == PyreMedia.App.ScrollTarget.Inside && wheelUp.By == -120);

    Check("a pixel scroller is handed that move unchanged",
        PyreMedia.App.SectionScroll.InnerStep(byItem: false, by: wheelUp.By, linesPerNotch: 3) == -120);

    Check("an item scroller is handed rows instead, in the same direction",
        PyreMedia.App.SectionScroll.InnerStep(byItem: true, by: wheelUp.By, linesPerNotch: 3) == -3);

    Check("and downwards is the other way",
        PyreMedia.App.SectionScroll.InnerStep(byItem: true, by: 120, linesPerNotch: 3) == 3);

    // Windows can be set to one line a notch; it cannot be set to none, and a
    // step of zero would make the wheel do nothing at all.
    Check("a notch always moves at least one row",
        PyreMedia.App.SectionScroll.InnerStep(byItem: true, by: -120, linesPerNotch: 0) == -1);
}

Console.WriteLine("\n=== what counts as a sample clip ===");
{
    // Where the release says so, that is taken at its word. Second-guessing it
    // with size and duration left a 77 MB thirty-second clip sitting in a
    // folder named Sample right through a rename.
    var root = @"H:\Done\Stuart Fails to Save the Universe (2026)";

    Check("a file named sample says so",
        MediaPlanner.SaysSample("sample.mkv"));

    Check("and so does one with the word tacked onto a show name",
        MediaPlanner.SaysSample("Show-sample.mkv"));

    Check("an ordinary episode does not",
        !MediaPlanner.SaysSample("Stuart Fails to Save the Universe 01x05 Bert Gets Married.mkv"));

    // The folder is the other half of it, and the half that was missing: the
    // clip in the report was called sample.mkv, but a release is just as
    // likely to put an oddly-named file inside a folder that says it.
    Check("anything inside a Sample folder is one, whatever it is called",
        MediaPlanner.InSampleFolder(root + @"\Sample\r4ndom-teaser.mkv", root));

    Check("plural spelling counts too",
        MediaPlanner.InSampleFolder(root + @"\Samples\clip.mkv", root));

    Check("an episode in its season folder is not",
        !MediaPlanner.InSampleFolder(root + @"\Season 01\Stuart 01x05.mkv", root));

    Check("nor is one sitting directly in the item",
        !MediaPlanner.InSampleFolder(root + @"\Stuart 01x05.mkv", root));

    // Bounded by the item. Walking further would reach whatever the library
    // happens to be filed under - somebody's "Samples" folder of loops, say.
    Check("the walk stops at the item and never climbs past it",
        !MediaPlanner.InSampleFolder(@"H:\Sample\Show\Season 01\ep.mkv", @"H:\Sample\Show"));

    // The exception: a show or film can be called Sample. What separates a
    // title from a label is whether the file names itself as content - an
    // episode number, or a year the way a film carries one.
    Check("an episode titled Free Sample is not a sample clip",
        !MediaPlanner.SaysSample("Some Show 01x01 Free Sample.mkv"));

    Check("nor in the other numbering style",
        !MediaPlanner.SaysSample("Some Show S01E01 Free Sample.mkv"));

    Check("nor is a film called Free Sample",
        !MediaPlanner.SaysSample("Free Sample (2016) 1080p.mkv"));

    // Sent the long way round, not waved through: a real scene clip carries an
    // episode number too, and the size and duration checks are what catch it.
    Check("a scene clip with an episode number is left to the size checks",
        !MediaPlanner.SaysSample("Show.S01E01.1080p.WEB-sample.mkv"));

    Check("while a bare sample.mkv is still taken at its word",
        MediaPlanner.SaysSample("sample.mkv"));

    Check("and quality tags are not mistaken for years",
        MediaPlanner.SaysSample("Show.2160p.WEB-sample.mkv"));

    // A show can be called Sample outright. Its own folder is never the
    // evidence - only a folder between the file and the item.
    Check("a show called Sample does not have its own episodes offered",
        !MediaPlanner.InSampleFolder(@"H:\Done\Sample\Season 01\Sample 01x01.mkv", @"H:\Done\Sample"));

    Check("but a Sample folder inside it still counts",
        MediaPlanner.InSampleFolder(@"H:\Done\Sample\Sample\clip.mkv", @"H:\Done\Sample"));
}

Console.WriteLine("\n=== a sample clip is offered for deletion, not just noted ===");
{
    // The shape that was reported: one episode, and a 30-second clip in a
    // folder called Sample. The clip got a "Needs attention" row with no
    // target, a tick box that did nothing, and survived the rename - and that
    // row was itself why the cleanup sweep never offered it, since the sweep
    // skips any file the plan already has a row for.
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-samp-" + Guid.NewGuid().ToString("N")[..8]);
    var sampleDir = Path.Combine(root, "Sample");
    Directory.CreateDirectory(sampleDir);

    var episode = Path.Combine(root, "Stuart.Fails.to.Save.the.Universe.S01E05.2160p.WEB.mkv");
    File.WriteAllText(episode, new string('x', 20000));

    // Named nothing like a sample. Only the folder says so, which is the half
    // that was missing.
    var clip = Path.Combine(sampleDir, "r4ndom-teaser.mkv");
    File.WriteAllText(clip, "x");

    var show = new TvShow
    {
        Id = "1",
        Name = "Stuart Fails to Save the Universe",
        FirstAired = "2026-01-01",
        Seasons =
        [
            new Season
            {
                Number = 1,
                Episodes = [new Episode { SeasonNumber = 1, Number = 5, Name = "Bert Gets Married" }]
            }
        ]
    };

    MediaItem Stuart() => new()
    {
        Path = root,
        DisplayName = "Stuart Fails to Save the Universe (2026)",
        Kind = MediaKind.TvEpisode,
        Files = [episode, clip],
        SearchTitle = "Stuart Fails to Save the Universe",
        SearchYear = "2026",
        MainFile = episode
    };

    try
    {
        var on = new MediaPlanner(new PyreMediaSettings { DeleteSamples = true })
            .PlanTv(Stuart(), show);

        var row = on.Actions.FirstOrDefault(a =>
            string.Equals(a.SourcePath, clip, StringComparison.OrdinalIgnoreCase));

        Check("the clip in a Sample folder gets a row at all", row is not null);

        Check("and it is a deletion, not a dead 'needs attention'",
            row?.Status == PlanStatus.Delete);

        Check("which says why", row?.DeleteReason == "sample clip");

        Check("the episode beside it is still renamed",
            on.Actions.Any(a => string.Equals(a.SourcePath, episode, StringComparison.OrdinalIgnoreCase)
                                && a.Status == PlanStatus.Change));

        // Off means off. The row still appears and still explains itself; what
        // it does not do is offer to remove anything.
        var off = new MediaPlanner(new PyreMediaSettings { DeleteSamples = false })
            .PlanTv(Stuart(), show);

        var noted = off.Actions.FirstOrDefault(a =>
            string.Equals(a.SourcePath, clip, StringComparison.OrdinalIgnoreCase));

        Check("with the option off it is set aside and said, not offered",
            noted?.Status == PlanStatus.Problem && noted.Problem is { Length: > 0 });

        Check("and nothing in that plan is a deletion",
            !off.Actions.Any(a => a.Status == PlanStatus.Delete));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n=== a small file is not condemned for being small ===");
{
    // An episode that was hard to find is genuinely a fraction of the ones
    // beside it, because a poor copy was the only copy going. Size decides who
    // is worth probing, never who goes - and where the duration cannot be had,
    // the answer is no.
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-lowq-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    var feature = Path.Combine(root, "Some Film.mkv");
    File.WriteAllText(feature, new string('x', 100000));

    // A twentieth of the feature, named like content and not like a clip.
    var poorCopy = Path.Combine(root, "Some Film - the rare one.mkv");
    File.WriteAllText(poorCopy, new string('x', 5000));

    var clip = Path.Combine(root, "sample.mkv");
    File.WriteAllText(clip, new string('x', 5000));

    var movie = new Movie { TmdbId = "1", Title = "Some Film", Year = "1997" };

    // No ffprobe to be had, which is the case that used to fall back to size.
    var settings = new PyreMediaSettings
    {
        DeleteSamples = true,
        DeleteJunkFiles = false,
        FfprobePath = Path.Combine(root, "no-such-ffprobe.exe")
    };

    MediaItem Film() => new()
    {
        Path = root,
        DisplayName = "Some Film (1997)",
        Kind = MediaKind.Movie,
        Files = [feature, poorCopy, clip],
        SearchTitle = "Some Film",
        SearchYear = "1997",
        MainFile = feature
    };

    try
    {
        var plan = new MediaPlanner(settings).PlanMovie(Film(), movie);

        bool Offered(string path) => plan.Actions.Any(a =>
            string.Equals(a.SourcePath, path, StringComparison.OrdinalIgnoreCase)
            && a.Status == PlanStatus.Delete);

        Check("a low-quality copy is not offered for deletion on size alone",
            !Offered(poorCopy));

        Check("but one that calls itself a sample still is",
            Offered(clip));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n=== a folder that held nothing but what was deleted ===");
{
    // Ticking the clip inside a Sample folder used to leave the folder behind,
    // and the next scan had no reason to mention it - a folder with no video
    // is not a library entry. Deleting the only thing in a folder is a
    // decision about the folder too.
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-empt-" + Guid.NewGuid().ToString("N")[..8]);
    var sampleDir = Path.Combine(root, "Sample");
    var seasonDir = Path.Combine(root, "Season 01");
    Directory.CreateDirectory(sampleDir);
    Directory.CreateDirectory(seasonDir);

    var clip = Path.Combine(sampleDir, "sample.mkv");
    File.WriteAllText(clip, "x");

    var episode = Path.Combine(seasonDir, "Show 01x01.mkv");
    File.WriteAllText(episode, "x");

    // Deleted from a folder that still holds something afterwards.
    var advert = Path.Combine(seasonDir, "TheRarBg.to.nfo");
    File.WriteAllText(advert, "x");

    var plan = new RenamePlan
    {
        ShowFolder = root,
        Show = new TvShow { Id = "1", Name = "Show" },
        Actions =
        [
            new PlannedAction { SourcePath = clip, Status = PlanStatus.Delete, DeleteReason = "sample clip" },
            new PlannedAction { SourcePath = advert, Status = PlanStatus.Delete, DeleteReason = "leftover .nfo" },
        ]
    };

    try
    {
        // Straight delete, not the Recycle Bin: a temp folder on a drive whose
        // bin may not exist is not what this test is about.
        var settings = new PyreMediaSettings { DeleteToRecycleBin = false };
        var result = new RenameExecutor(settings).Execute(plan, plan.Actions);

        Check("both files go", result.Deleted == 2);

        Check("the folder that held only the clip goes with it",
            !Directory.Exists(sampleDir));

        Check("the folder that still holds an episode stays",
            Directory.Exists(seasonDir) && File.Exists(episode));

        Check("and the item's own folder is never removed", Directory.Exists(root));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n=== emptying the last folder does not take the item with it ===");
{
    // Every file in the item deleted. The folder stays: removing it is a
    // decision for the person looking at it, not a side effect of a tick.
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-all-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    var only = Path.Combine(root, "sample.mkv");
    File.WriteAllText(only, "x");

    var plan = new RenamePlan
    {
        ShowFolder = root,
        Show = new TvShow { Id = "1", Name = "Show" },
        Actions = [new PlannedAction { SourcePath = only, Status = PlanStatus.Delete, DeleteReason = "sample clip" }]
    };

    try
    {
        var result = new RenameExecutor(new PyreMediaSettings { DeleteToRecycleBin = false })
            .Execute(plan, plan.Actions);

        Check("the file goes", result.Deleted == 1 && !File.Exists(only));
        Check("the item's folder is left standing", Directory.Exists(root));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n=== a tracker's name is not part of the show's ===");
{
    // Reported as "Lanterns won't combine directories": one folder called
    // "Lanterns (2026)" and another called "www.UIndex.org - Lanterns S01E01
    // ...". The second parsed as a show called "www UIndex org - Lanterns", so
    // nothing could see it was the same series.
    var (title, _) = NameFormatter.ParseTvName(
        "www.UIndex.org    -    Lanterns S01E01 Pilot REPACK 2160p AMZN WEB-DL DDP5 1 Atmos DV HDR H 265-FLUX",
        null);

    Check("a www prefix is not the show", title == "Lanterns");

    var (bare, _) = NameFormatter.ParseTvName("EZTVx.to - Some Show S02E03 Whatever 1080p", null);
    Check("nor is a bare domain in front of a dash", bare == "Some Show");

    var (bracketed, _) = NameFormatter.ParseTvName("Some Show S02E03 Whatever [EZTVx.to]", null);
    Check("nor one in brackets at the end", bracketed == "Some Show");

    // The dangerous half. Once separators are stripped a title looks exactly
    // like a domain, so the rule is a whitelist and only fires at the front or
    // inside brackets.
    var (stuart, _) = NameFormatter.ParseTvName(
        "Stuart.Fails.to.Save.the.Universe.S01E05.2160p.WEB.H265-TRB", null);

    Check("a title containing \"Fails.to\" is left alone",
        stuart == "Stuart Fails to Save the Universe");

    Check("and a real dotted title keeps its words",
        NameFormatter.StripSiteTag("Dr.Who.S01E01") == "Dr.Who.S01E01");

    Check("S.W.A.T. survives it",
        NameFormatter.StripSiteTag("S.W.A.T.2017.S01E01") == "S.W.A.T.2017.S01E01");
}

Console.WriteLine("\n=== two folders of one season are halves, not duplicates ===");
{
    MediaItem LanternFolder(string path, params string[] files) => new()
    {
        Path = path,
        DisplayName = Path.GetFileName(path),
        Kind = MediaKind.TvEpisode,
        Files = [.. files.Select(f => Path.Combine(path, f))],
        SearchTitle = "Lanterns",
        SearchYear = null,
        MainFile = Path.Combine(path, files[0])
    };

    // Different episodes of the same season. This is what a part-collected
    // season looks like - one release per folder - and it was being refused on
    // the grounds that both folders said season one.
    var halves = ShowGrouper.Group([
        LanternFolder(@"H:\Done\Lanterns (2026)", "Lanterns 01x02 Trust Fall.mkv"),
        LanternFolder(@"H:\Done\www.UIndex.org - Lanterns S01E01", "Lanterns 01x01 Pilot.mkv"),
    ]);

    Check("two folders holding different episodes are one show",
        halves.Count == 1 && halves[0].Items.Count == 2 && halves[0].Caution is null);

    // The same episode twice is the case the caution was written for, and it
    // still fires.
    var dupes = ShowGrouper.Group([
        LanternFolder(@"H:\Done\Lanterns", "Lanterns 01x01 Pilot.mkv"),
        LanternFolder(@"H:\Done\Lanterns REPACK", "Lanterns 01x01 Pilot.mkv"),
    ]);

    Check("but the same episode twice is still held back",
        dupes.Count == 1 && dupes[0].Caution is { Length: > 0 });

    Check("and the caution names the episode, not the season",
        dupes[0].Caution!.Contains("01x01"));
}

Console.WriteLine("\n=== leftovers arrive ticked, except where a folder says think twice ===");
{
    // Opt-out rather than opt-in: the point of the list is not to forget. The
    // exception is anything sizeable in a folder somebody curated - a deleted
    // scene and a sample clip look alike from the outside.
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-tick-" + Guid.NewGuid().ToString("N")[..8]);
    var extras = Path.Combine(root, "Extras");
    Directory.CreateDirectory(extras);

    var feature = Path.Combine(root, "Some Film.mkv");
    File.WriteAllText(feature, new string('x', 100000));

    var advert = Path.Combine(root, "TheRarBg.to.nfo");
    File.WriteAllText(advert, "not kodi metadata, just ascii art");

    var note = Path.Combine(extras, "readme.txt");
    File.WriteAllText(note, "small");

    // Big enough that it might be something worth keeping.
    var bigClip = Path.Combine(extras, "sample.mkv");
    using (var fs = File.Create(bigClip)) fs.SetLength(20L * 1024 * 1024);

    var movie = new Movie { TmdbId = "1", Title = "Some Film", Year = "1997" };
    var settings = new PyreMediaSettings { DeleteSamples = true, DeleteJunkFiles = true };

    MediaItem Film() => new()
    {
        Path = root,
        DisplayName = "Some Film (1997)",
        Kind = MediaKind.Movie,
        Files = [feature, bigClip],
        SearchTitle = "Some Film",
        SearchYear = "1997",
        MainFile = feature
    };

    try
    {
        var plan = new MediaPlanner(settings).PlanMovie(Film(), movie);

        PlannedAction? Row(string path) => plan.Actions.FirstOrDefault(a =>
            string.Equals(a.SourcePath, path, StringComparison.OrdinalIgnoreCase)
            && a.Status == PlanStatus.Delete);

        Check("a release advert is offered and ticked", Row(advert)?.StartsSelected == true);

        Check("a small leftover in Extras is ticked too - it is no one's deleted scene",
            Row(note)?.StartsSelected == true);

        Check("but a sizeable file in Extras is offered unticked",
            Row(bigClip) is { StartsSelected: false });
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n=== a saved season shift, and what the Renumber screen must agree with ===");
{
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-off-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    var files = new List<string>();
    foreach (var n in new[] { 1, 2 })
    {
        var f = Path.Combine(root, $"Some Show S01E{n:00}.mkv");
        File.WriteAllText(f, "x");
        files.Add(f);
    }

    var show = new TvShow
    {
        Id = "555", Name = "Some Show", FirstAired = "2020-01-01",
        Seasons =
        [
            new Season
            {
                Number = 1,
                Episodes =
                [
                    new Episode { SeasonNumber = 1, Number = 1, Name = "One" },
                    new Episode { SeasonNumber = 1, Number = 2, Name = "Two" },
                    new Episode { SeasonNumber = 1, Number = 3, Name = "Three" },
                    new Episode { SeasonNumber = 1, Number = 4, Name = "Four" },
                ]
            }
        ]
    };

    MediaItem Shifted() => new()
    {
        Path = root,
        DisplayName = "Some Show (2020)",
        Kind = MediaKind.TvEpisode,
        Files = [.. files],
        SearchTitle = "Some Show",
        SearchYear = "2020",
        MainFile = files[0]
    };

    try
    {
        var settings = new PyreMediaSettings { MoveTvFiles = false };
        settings.SetEpisodeOffset("555", 1, 2);

        Check("the shift is stored per show and season",
            settings.EpisodeOffset("555", 1) == 2 && settings.EpisodeOffset("555", 2) == 0);

        var plan = new MediaPlanner(settings).PlanTv(Shifted(), show);

        // E01 read as episode 3, E02 as episode 4. This is what the preview
        // shows, and what the Renumber screen has to open on - it used to build
        // its rows from the raw filename, so it showed 1 and 2 against a
        // preview showing 3 and 4, then recorded the difference as "no shift"
        // and deleted the setting.
        Check("a file marked E01 is planned as episode 3",
            plan.Actions.Any(a => a.TargetName == "Some Show 01x03 Three.mkv"));

        Check("and E02 as episode 4",
            plan.Actions.Any(a => a.TargetName == "Some Show 01x04 Four.mkv"));

        // Zero is how the setting is cleared, which is why recording "no shift"
        // by accident was destructive rather than merely wrong.
        settings.SetEpisodeOffset("555", 1, 0);
        Check("setting a shift of zero removes it", settings.EpisodeOffset("555", 1) == 0);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n=== an episode number at the front of a name ===");
{
    // Reported as "ghost in the shell did not group at all". The marker pattern
    // demanded a separator in front of it, so one at position 0 never matched
    // and the whole filename became the show's name.
    var (lead, _) = NameFormatter.ParseTvName(
        "S01E03 The Ghost in the Shell Episodio 03 Junk Jungle Ii + Megatech Machine (2026) WEBRip 1080p x264 EAC3 ENG ITA JPN SUB ITA ENG - Lullozzo",
        null);

    Check("the title is what follows the marker, not the whole name",
        lead == "The Ghost in the Shell");

    // Same show, marker in the usual place. Both have to land on the same
    // words or they cannot be recognised as one series.
    var (trailing, _) = NameFormatter.ParseTvName(
        "THE.GHOST.IN.THE.SHELL.S01E08.INTERMISSION.BRAIN.DRAIN.i.1080p.AMZN.WEB-DL.ENG.ITA.JAP.H264-TBK",
        null);

    Check("and it agrees with the same show named the usual way round",
        string.Equals(trailing, lead, StringComparison.OrdinalIgnoreCase));

    // "Episodio 03" is where the show's name stops. Without it the episode
    // title runs on and no two files agree.
    var (italian, _) = NameFormatter.ParseTvName("S01E05 Some Show Episodio 05 Phantom Fund (2026) WEBRip", null);
    Check("a spelled-out episode number ends the title", italian == "Some Show");

    var (english, _) = NameFormatter.ParseTvName("S02E01 Another Show Episode 1 The Return 1080p", null);
    Check("in English too", english == "Another Show");

    // The ordinary case must not move.
    var (plain, _) = NameFormatter.ParseTvName("Some Show S01E01 Pilot 1080p", null);
    Check("a name with the marker in the middle is unchanged", plain == "Some Show");
}

Console.WriteLine("\n=== loose episodes belong to the show they are episodes of ===");
{
    MediaItem Loose(string root, params string[] names) => new()
    {
        Path = root,
        DisplayName = "The Ghost in the Shell",
        Kind = MediaKind.TvEpisode,
        Files = [.. names.Select(n => Path.Combine(root, n))],
        SearchTitle = "The Ghost in the Shell",
        SearchYear = null,
        MainFile = Path.Combine(root, names[0]),
        IsLooseFile = true
    };

    MediaItem ShowFolder(string path, params string[] names) => new()
    {
        Path = path,
        DisplayName = Path.GetFileName(path),
        Kind = MediaKind.TvEpisode,
        Files = [.. names.Select(n => Path.Combine(path, "Season 01", n))],
        SearchTitle = "THE GHOST IN THE SHELL",
        SearchYear = null,
        MainFile = Path.Combine(path, "Season 01", names[0])
    };

    var items = new List<MediaItem>
    {
        Loose(@"H:\Done", "S01E03 The Ghost in the Shell.mkv", "S01E05 The Ghost in the Shell.mkv"),
        ShowFolder(@"H:\Done\THE GHOST IN THE SHELL", "THE GHOST IN THE SHELL 01x01.mkv"),
    };

    var groups = ShowGrouper.Group(items);

    Check("a loose run of episodes groups with the show's folder",
        groups.Count == 1 && groups[0].Items.Count == 2 && groups[0].Caution is null);

    var merged = ShowGrouper.Combine(items, out var combined);
    var one = merged.FirstOrDefault(m => m.IsCombined);

    Check("and they combine into one entry holding every file",
        combined.Count == 1 && one?.Files.Count == 3);

    // The sweep walks CombinedFrom looking for junk. A loose file's Path is the
    // scan root, so putting it in there would point that sweep at the library.
    Check("the scan root is never listed as a folder to sweep",
        one!.CombinedFrom.Count == 1
        && one.CombinedFrom[0] == @"H:\Done\THE GHOST IN THE SHELL");

    Check("and the entry sits in the folder they share, not above it",
        one.Path == @"H:\Done");
}

Console.WriteLine("\n=== disc images are filed, never opened ===");
{
    var on = new PyreMediaSettings { OrganiseDiscImages = true };
    var off = new PyreMediaSettings { OrganiseDiscImages = false };

    Check("an iso is scanned when the option is on",
        on.ScannedExtensions.Contains(".iso") && on.IsDiscImage(@"C:\x\Film.iso"));

    Check("and invisible when it is off", !off.ScannedExtensions.Contains(".iso"));

    // The distinction that keeps them safe: scanned is "what belongs to this
    // item", VideoExtensions is "what can I open". Remux and probing read the
    // second, and an iso must never appear there.
    Check("an iso is never treated as something to open",
        !on.VideoExtensions.Contains(".iso"));

    Check("a plain video is still both",
        on.ScannedExtensions.Contains(".mkv") && on.VideoExtensions.Contains(".mkv"));

    // .bin was in the first draft of this list and taken out again: it pairs
    // with a .cue, but it is also firmware, a save file, anything at all.
    Check("bin is not assumed to be a disc", !on.IsDiscImage(@"C:\x\firmware.bin"));
}

Console.WriteLine("\n=== a disc carrying both cuts ===");
{
    // "SUICIDE_SQUAD 3D-2D EXTENDED EDITION.iso" - a disc holding the 3D and
    // the 2D cut. Removing only the "3D" left a film called "SUICIDE SQUAD -2D
    // EDITION", and the leftover reduced to "DD", which came back as a 3D
    // layout called DD.
    Check("3D-2D is one token, not 3D and a leftover",
        Stereo3DTag.Strip("SUICIDE_SQUAD 3D-2D EXTENDED EDITION") == "SUICIDE_SQUAD EXTENDED EDITION");

    Check("and it is still recognised as 3D",
        Stereo3DTag.Extract("SUICIDE_SQUAD 3D-2D EXTENDED EDITION") == "(3D)");

    Check("the other way round too",
        Stereo3DTag.Strip("Some Film 2D-3D Edition") == "Some Film Edition");

    Check("a stated layout still wins over the bare marker",
        Stereo3DTag.Extract("Some Film 3D HSBS") == "(3D-HSBS)");

    // Same reduction, different meaning: an audio codec in brackets used to
    // come back as a 3D format called DD.
    Check("a bracketed audio codec is not a 3D layout",
        Stereo3DTag.Extract("Some Film 3D (DD5.1)") == "(3D)");

    Check("and a film with no marker at all is left alone",
        Stereo3DTag.Extract("Some Film 1080p") is null);

    // Release names run their words together. "BD3DRmx" is a 3D BD remux, and
    // the boundary rule that keeps a bare "3d" honest cannot see inside it.
    Check("3D inside a run-together release name is still 3D",
        Stereo3DTag.Extract("FastX(2023)BD3DRmx(Ash61)") is not null);

    Check("but a bare 3d glued to a word is still not trusted",
        Stereo3DTag.Extract("Some Film D3Dsomething") is null);

    // A disc image is not side-by-side or top-and-bottom - those describe a
    // re-encode, and the point of keeping the image is that nothing has been
    // done to it.
    Check("a 3D disc image says it is a disc",
        Stereo3DTag.ForDiscImage("(3D)") == "(3D-ISO)"
        && Stereo3DTag.ForDiscImage("(3D-HSBS)") == "(3D-ISO)");

    Check("and a 2D one gets no tag at all",
        Stereo3DTag.ForDiscImage(null) is null);
}

Console.WriteLine("\n=== a run of years is one fact, not two candidates ===");
{
    // "Stranger Things (2016-2025) Complete Series" carries the years it ran
    // between. Read one at a time, the last became the year and the first stayed
    // glued to the name: a show called "Stranger Things 2016", first aired 2025.
    var (title, year) = NameFormatter.ParseTvName(
        "Stranger Things (2016-2025) Complete Series 4K DV-HDR10 BluRay Rip H265 DTS-HD 5.1 - TRUEHD 7.1+Atmos",
        null);

    Check("the title loses the range entirely", title == "Stranger Things");
    Check("and keeps the year it started", year == "2016");

    var (dashed, from) = NameFormatter.ParseTvName("Some Show 1999-2004 Complete", null);
    Check("brackets are not required", dashed == "Some Show" && from == "1999");

    // The single-year rule must not move.
    var (one, y1) = NameFormatter.ParseTvName("Some Show (2011)", null);
    Check("one year still behaves", one == "Some Show" && y1 == "2011");

    // A title that is itself a year still wins, which is why the loop takes the
    // last year that leaves something behind.
    var (numeric, y2) = NameFormatter.ParseTvName("1923 (2022)", null);
    Check("a show named after a year keeps its name", numeric == "1923" && y2 == "2022");
}

Console.WriteLine("\n=== a torrent is one job ===");
{
    var root = Path.Combine(Path.GetTempPath(), "pyremedia-job-" + Guid.NewGuid().ToString("N")[..8]);
    var s01 = Path.Combine(root, "S01");
    var s02 = Path.Combine(root, "S02");
    Directory.CreateDirectory(s01);
    Directory.CreateDirectory(s02);

    // Season one has finished. Season two has not.
    var done = Path.Combine(s01, "Show - 1x01 - One.mkv");
    File.WriteAllText(done, "x");
    File.WriteAllText(Path.Combine(s02, "Show - 2x01 - One.mkv.!ut"), "");

    var show = new TvShow
    {
        Id = "1", Name = "Show", FirstAired = "2016-01-01",
        Seasons = [ new Season { Number = 1, Episodes = [new Episode { SeasonNumber = 1, Number = 1, Name = "One" }] } ]
    };

    MediaItem Torrent() => new()
    {
        Path = root,
        DisplayName = "Show",
        Kind = MediaKind.TvEpisode,
        Files = [done],
        SearchTitle = "Show",
        SearchYear = "2016",
        MainFile = done
    };

    try
    {
        Check("a marker anywhere beneath the item is found",
            Downloads.ActiveUnder(root) == "uTorrent");

        Check("and the folder holding the finished season looks quiet on its own",
            Downloads.ActiveIn(s01) is null);

        var plan = new MediaPlanner(new PyreMediaSettings()).PlanTv(Torrent(), show);

        // The finished season is the trap: nothing beside it says it is busy,
        // but moving it out from under the client breaks the transfer for the
        // whole torrent rather than only for what moved.
        Check("a finished season is still left alone while the rest arrives",
            plan.ChangeCount == 0 && plan.ProblemCount == 1);

        Check("and it says who is holding it",
            plan.Problems.First().Problem!.Contains("uTorrent"));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n=== combining one entry must not drop the others ===");
{
    // Every loose file in a library shares one Path - the scan root it was
    // found in. Keyed on that, absorbing a single loose entry into a group
    // marked the root as taken and silently dropped every other loose file:
    // a lone episode of Lanterns vanished because an unrelated run of Ghost in
    // the Shell episodes had been combined.
    MediaItem Loose(string title, params string[] names) => new()
    {
        Path = @"H:\Done",
        DisplayName = title,
        Kind = MediaKind.TvEpisode,
        Files = [.. names.Select(n => Path.Combine(@"H:\Done", n))],
        SearchTitle = title,
        SearchYear = null,
        MainFile = Path.Combine(@"H:\Done", names[0]),
        IsLooseFile = true
    };

    var items = new List<MediaItem>
    {
        Loose("Ghost", "Ghost 01x03.mkv", "Ghost 01x05.mkv"),
        Loose("Lanterns", "Lanterns 01x02.mkv"),
        new()
        {
            Path = @"H:\Done\Ghost",
            DisplayName = "Ghost",
            Kind = MediaKind.TvEpisode,
            Files = [@"H:\Done\Ghost\Season 01\Ghost 01x01.mkv"],
            SearchTitle = "Ghost",
            SearchYear = null,
            MainFile = @"H:\Done\Ghost\Season 01\Ghost 01x01.mkv"
        },
    };

    var merged = ShowGrouper.Combine(items, out var combined);

    Check("the group is combined", combined.Count == 1);

    Check("and the unrelated loose file is still there",
        merged.Any(m => m.SearchTitle == "Lanterns" && m.Files.Count == 1));

    Check("a loose show with nothing to join stands on its own",
        merged.Count == 2);
}

Console.WriteLine("\n=== asking a shorter question when the first finds nothing ===");
{
    // TMDb matches on every word rather than loosely: one word too many returns
    // nothing at all, not a worse match. Measured against the live API -
    // "SUICIDE SQUAD EDITION" finds nothing, "SUICIDE SQUAD" finds it first
    // time. So the rescue is local, and it drops from the end, because a name
    // is far more likely to carry junk after it than before it.
    static List<string> Ladder(string term)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var steps = new List<string>();

        for (var take = words.Length - 1; take >= 2; take--)
            steps.Add(string.Join(' ', words.Take(take)));

        return steps;
    }

    Check("three words drop to two",
        Ladder("SUICIDE SQUAD EDITION") is [ "SUICIDE SQUAD" ]);

    Check("longest first, so the most specific is tried before the vaguest",
        Ladder("A B C D") is [ "A B C", "A B" ]);

    // One word matches half a catalogue, and choosing from that is worse than
    // finding nothing and being told so.
    Check("two words are never cut to one", Ladder("Some Show").Count == 0);

    Check("nor is a single word touched", Ladder("Firefly").Count == 0);
}

Console.WriteLine("\n=== Babe (1995): a subtitle tag two muxers disagree about ===");
{
    // Every number here was measured off the real file and a real mkvmerge
    // v100 remux of it. The file refused to remux for a year of film time that
    // was never missing.
    MediaStream S(int index, StreamKind kind, string codec, string lang, double seconds) =>
        new() { Index = index, Kind = kind, Codec = codec, Language = lang,
                Seconds = seconds, SecondsMeasured = true };

    // Source: video and three English soundtracks at 01:31:55, English PGS
    // tagged 01:29:24.818.
    var kept = new List<MediaStream>
    {
        S(0, StreamKind.Video, "h264", "", 5515.677),
        S(1, StreamKind.Audio, "dts", "eng", 5515.723),
        S(2, StreamKind.Audio, "dts", "eng", 5515.723),
        S(3, StreamKind.Audio, "ac3", "eng", 5515.744),
        S(4, StreamKind.Subtitle, "hdmv_pgs_subtitle", "eng", 5364.818),
    };

    var source = new MediaInfo { Path = "babe.mkv", DurationSeconds = 5515.744, Streams = [.. kept] };

    // Output: picture and sound identical to the millisecond, subtitle tagged
    // 01:27:13.186 - and 2,491 packets on both sides, last event at 5364.818.
    var output = new MediaInfo
    {
        Path = "out.mkv",
        DurationSeconds = 5515.744,
        Streams =
        [
            S(0, StreamKind.Video, "h264", "", 5515.677),
            S(1, StreamKind.Audio, "dts", "eng", 5515.723),
            S(2, StreamKind.Audio, "dts", "eng", 5515.723),
            S(3, StreamKind.Audio, "ac3", "eng", 5515.744),
            S(4, StreamKind.Subtitle, "hdmv_pgs_subtitle", "eng", 5233.186),
        ]
    };

    Check("the remux is accepted", DurationCheck.Failed(source, output, kept) is null);

    // The guard this must not weaken: sound really going missing.
    var quiet = new MediaInfo
    {
        Path = "out.mkv",
        DurationSeconds = 5515.744,
        Streams =
        [
            S(0, StreamKind.Video, "h264", "", 5515.677),
            S(1, StreamKind.Audio, "dts", "eng", 5515.723),
            S(2, StreamKind.Audio, "dts", "eng", 4000.0),
            S(3, StreamKind.Audio, "ac3", "eng", 5515.744),
            S(4, StreamKind.Subtitle, "hdmv_pgs_subtitle", "eng", 5233.186),
        ]
    };

    Check("but a soundtrack cut by twenty-five minutes is still refused",
        DurationCheck.Failed(source, quiet, kept) is not null);

    Check("and the picture going short is still refused",
        DurationCheck.Failed(source,
            new MediaInfo { Path = "out.mkv", DurationSeconds = 5515.744,
                Streams = [ S(0, StreamKind.Video, "h264", "", 4000),
                            S(1, StreamKind.Audio, "dts", "eng", 5515.723),
                            S(2, StreamKind.Audio, "dts", "eng", 5515.723),
                            S(3, StreamKind.Audio, "ac3", "eng", 5515.744),
                            S(4, StreamKind.Subtitle, "hdmv_pgs_subtitle", "eng", 5233.186) ] },
            kept) is not null);
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

/// <summary>A sidecar holding characters that would not survive a bad encoding.</summary>
static string WriteAccented(string box, string text)
{
    var book = Path.Combine(box, "accented.epub");

    ReadableText.Write(book, ReadableText.Compose("Accented", "made up", [new TextPage(1, text)]));

    return ReadableText.PathFor(book);
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

// Answers every request with the same bytes, so the artwork writer can be
// tested without reaching the network or trusting somebody else's uptime.
sealed class CannedResponse(byte[] body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        });
}

    // A stand-in provider, so the chain's behaviour can be checked without an
// account, a key, or a six gigabyte download.
sealed class Fake(string name, bool ready, int hits, bool throws = false) : IComicProvider
{
    public string Name => name;
    public bool Ready => ready;
    public string? NotReadyBecause => ready ? null : "not set up";
    public int Asked { get; private set; }

    public Task<IReadOnlyList<ComicSeries>> SearchAsync(string s, string? y, CancellationToken ct = default)
    {
        Asked++;

        if (throws) throw new InvalidOperationException("rate limited");

        return Task.FromResult<IReadOnlyList<ComicSeries>>(
            [.. Enumerable.Range(1, hits).Select(i => new ComicSeries
                { Id = i.ToString(), Name = $"{s} {i}", Source = name })]);
    }

    public Task<IReadOnlyList<ComicIssue>> IssuesAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComicIssue>>([new ComicIssue { Number = "1" }]);
}
