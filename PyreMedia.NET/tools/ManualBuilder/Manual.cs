using System.IO;
using System.Text;

namespace ManualBuilder;

/// <summary>
/// The manual itself. One self-contained HTML file with the pictures embedded,
/// so it can be opened from anywhere, copied about, or shipped beside the exe
/// without a folder of images to lose.
/// </summary>
internal static class Manual
{
    public static string Build(Dictionary<string, string> shots)
    {
        var sb = new StringBuilder();

        string Img(string key, string caption)
        {
            // A picture that failed to render is left out rather than replaced with
            // a notice about it. The prose stands on its own, and a bracketed
            // apology in the middle of a manual reads worse than no picture.
            if (!shots.TryGetValue(key, out var path) || !File.Exists(path))
                return "";

            var data = Convert.ToBase64String(File.ReadAllBytes(path));
            return $"""
                    <figure>
                      <img src="data:image/png;base64,{data}" alt="{Escape(caption)}">
                      <figcaption>{Escape(caption)}</figcaption>
                    </figure>
                    """;
        }

        // Not interpolated: the CSS is full of braces, and the picture placeholders
        // are filled in by Replace below once each one has been rendered.
        sb.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>PyreMedia 4 - Manual</title>
            <style>
              :root {
                --bg: #1b1b1f; --panel: #232329; --line: #34343c;
                --text: #e6e6ea; --dim: #a8a8b3; --accent: #7aa7ff; --warn: #ffb454;
              }
              @media (prefers-color-scheme: light) {
                :root {
                  --bg: #ffffff; --panel: #f5f5f7; --line: #e0e0e6;
                  --text: #1b1b1f; --dim: #5c5c68; --accent: #2c5fd0; --warn: #9a5b00;
                }
              }
              * { box-sizing: border-box; }
              body {
                margin: 0; background: var(--bg); color: var(--text);
                font: 16px/1.65 "Segoe UI", system-ui, sans-serif;
              }
              .wrap { max-width: 60rem; margin: 0 auto; padding: 2.5rem 1.25rem 6rem; }
              h1 { font-size: 2.1rem; margin: 0 0 .3rem; letter-spacing: -.02em; }
              .sub { color: var(--dim); margin: 0 0 2.5rem; }
              h2 {
                font-size: 1.4rem; margin: 3.5rem 0 .75rem;
                padding-top: 1.25rem; border-top: 1px solid var(--line);
              }
              h3 { font-size: 1.1rem; margin: 2rem 0 .5rem; }
              p, li { color: var(--text); }
              a { color: var(--accent); }
              code {
                background: var(--panel); border: 1px solid var(--line);
                border-radius: 4px; padding: .1em .35em;
                font: .88em ui-monospace, Consolas, monospace;
              }
              figure { margin: 1.5rem 0 2rem; }
              figure img {
                display: block; width: 100%; height: auto;
                border: 1px solid var(--line); border-radius: 8px; background: var(--panel);
              }
              figcaption { color: var(--dim); font-size: .85rem; margin-top: .5rem; }
              .missing { color: var(--warn); font-style: italic; }
              .note, .warn {
                background: var(--panel); border-left: 3px solid var(--accent);
                border-radius: 0 6px 6px 0; padding: .85rem 1.1rem; margin: 1.25rem 0;
              }
              .warn { border-left-color: var(--warn); }
              .note p:last-child, .warn p:last-child { margin-bottom: 0; }
              .note p:first-child, .warn p:first-child { margin-top: 0; }
              nav { background: var(--panel); border: 1px solid var(--line);
                    border-radius: 8px; padding: 1.1rem 1.4rem; }
              nav ol { margin: .4rem 0 0; padding-left: 1.3rem; }
              nav li { margin: .2rem 0; }
              table { border-collapse: collapse; width: 100%; margin: 1.25rem 0; font-size: .93rem; }
              th, td { border: 1px solid var(--line); padding: .5rem .7rem; text-align: left;
                       vertical-align: top; }
              th { background: var(--panel); font-weight: 600; }
              .path { color: var(--dim); font: .88em ui-monospace, Consolas, monospace; }
              footer { margin-top: 5rem; padding-top: 1.5rem; border-top: 1px solid var(--line);
                       color: var(--dim); font-size: .85rem; }
              @media print {
                body { background: #fff; color: #000; }
                figure img { border-color: #bbb; }
                h2 { break-before: page; }
              }
            </style>
            </head>
            <body>
            <div class="wrap">

            <h1>PyreMedia 4</h1>
            <p class="sub">Renaming and organising a media library, without losing anything.</p>

            <nav>
              <strong>Contents</strong>
              <ol>
                <li><a href="#what">What it does</a></li>
                <li><a href="#first-run">First run</a></li>
                <li><a href="#tour">The main window</a></li>
                <li><a href="#workflow">Renaming, start to finish</a></li>
                <li><a href="#conflicts">When something is already in the way</a></li>
                <li><a href="#split">One show spread over several folders</a></li>
                <li><a href="#renumber">Episodes numbered wrongly</a></li>
                <li><a href="#remux">Remuxing: dropping tracks you don't want</a></li>
                <li><a href="#dv">Dolby Vision</a></li>
                <li><a href="#nfo">.nfo files and Kodi</a></li>
                <li><a href="#history">History and undo</a></li>
                <li><a href="#settings">Settings</a></li>
                <li><a href="#narrow">Using it in a narrow window</a></li>
                <li><a href="#files">Where PyreMedia keeps its own files</a></li>
                <li><a href="#trouble">When something goes wrong</a></li>
              </ol>
            </nav>

            <h2 id="what">What it does</h2>

            <p>PyreMedia looks at the files in your library, works out what each one
            is by asking TheMovieDB, TheTVDB or TVmaze, and renames them into a
            consistent shape that Kodi, Plex and Jellyfin all understand. It can also
            strip audio and subtitle tracks you have no use for, which on a big library
            is a great deal of disk space.</p>

            <p>Two things are true of everything it does:</p>

            <ul>
              <li><strong>Nothing happens until you press Apply.</strong> The whole plan
              is shown first, one row per file, with a tick you can clear.</li>
              <li><strong>Every change is written down.</strong> Renames can be undone
              from History, long after the fact.</li>
            </ul>

            <h2 id="first-run">First run</h2>

            <p>The first launch offers a short setup: it checks for the two external
            tools, lets you pick your folders, and sets the handful of options worth
            deciding up front. You can re-open it any time from the toolbar.</p>

            {{IMG_SETUP}}

            <p>The two tools are separate projects with their own licences, so
            PyreMedia doesn't bundle them - it finds them on your PATH and can install
            them for you through winget, using each project's own package:</p>

            <table>
              <tr><th>Tool</th><th>Who makes it</th><th>What it's for</th></tr>
              <tr><td><code>ffmpeg</code> / <code>ffprobe</code></td>
                  <td>Gyan Doshi's Windows builds of FFmpeg</td>
                  <td>Reading what tracks a file holds, and remuxing anything that
                      isn't Matroska</td></tr>
              <tr><td><code>mkvmerge</code></td>
                  <td>Moritz Bunkus, MKVToolNix</td>
                  <td>Remuxing Matroska. Preferred where it applies - faster, and it
                      writes the stereo flag a 3D file may be missing</td></tr>
              <tr><td><code>dovi_tool</code></td>
                  <td>quietvoid</td>
                  <td><em>Optional.</em> Rebuilds a Dolby Vision declaration that a
                      remux into MP4 stripped. Most libraries never need it</td></tr>
            </table>

            <div class="note">
              <p>Renaming works without any of them. They are only needed for reading
              track details and for remuxing.</p>
              <p>The first three install through winget from the authors' own packages.
              <code>dovi_tool</code> has no winget package, so it is a single executable
              downloaded from its author's releases and pointed at in Settings.</p>
            </div>

            <h2 id="tour">The main window</h2>

            {{IMG_MAIN}}

            <p>Three panels, left to right:</p>

            <ul>
              <li><strong>Library</strong> - everything found in your folders. One row
              per show or film. The tag says whether PyreMedia thinks it's TV or a
              film; you can override that when it guesses wrong.</li>
              <li><strong>Match</strong> - which show or film the selected item
              actually is. It searches automatically from the folder or filename, and
              you can search again by hand with a different title or year.</li>
              <li><strong>Changes</strong> - exactly what would happen on disk, before
              anything does.</li>
            </ul>

            <p>Each row in Changes shows the old name, the new name, and a status.
            The statuses worth knowing:</p>

            <table>
              <tr><th>Status</th><th>Meaning</th></tr>
              <tr><td>Rename</td><td>The file stays where it is and gets a new name.</td></tr>
              <tr><td>Rename, into new folder</td>
                  <td>A folder will be created and the file moved into it.</td></tr>
              <tr><td>Already correct</td>
                  <td>Nothing to do. Shown so you can see it was considered.</td></tr>
              <tr><td>Problem</td>
                  <td>PyreMedia won't touch it, and says why - for example two files
                      that would end up with the same name.</td></tr>
            </table>

            <h2 id="workflow">Renaming, start to finish</h2>

            <ol>
              <li><strong>Add a folder.</strong> Point it at a folder that
              <em>contains</em> your shows and films, not at one show. Adding the same
              folder twice, or a folder inside one you already added, is detected and
              ignored.</li>
              <li><strong>Scan.</strong> Everything found appears in the Library list.</li>
              <li><strong>Pick an item.</strong> PyreMedia searches for it and picks
              a confident match on its own. If it got it wrong, search again in the
              Match box - a year helps a great deal with remakes.</li>
              <li><strong>Read the Changes list.</strong> This is the moment that
              matters. Untick anything you don't want.</li>
              <li><strong>Apply.</strong> The ticked changes happen and are recorded.
              The library is rescanned so what you see stays true.</li>
            </ol>

            <div class="note">
              <p><strong>Move to next automatically</strong>, beside Apply, jumps to
              the next unfinished item after each Apply. With it on you can work
              through a whole folder without touching the Library list.</p>
            </div>

            <h2 id="conflicts">When something is already in the way</h2>

            <p>If a file would land on a name that already exists, PyreMedia stops and
            asks, in the same terms Windows uses - and it asks about the whole batch at
            once rather than once per file.</p>

            {{IMG_CONFLICT}}

            <table>
              <tr><th>Choice</th><th>What happens</th></tr>
              <tr><td>Replace</td>
                  <td>The existing file is removed and the new one takes its name. The
                      removal is recorded, and goes to the Recycle Bin where the drive
                      has one.</td></tr>
              <tr><td>Keep both</td>
                  <td>The incoming file gets a free name - <code>Film (2).mkv</code>.
                      Its artwork and subtitles follow it, so they stay attached to the
                      right file.</td></tr>
              <tr><td>Skip</td>
                  <td>Nothing happens to either file.</td></tr>
            </table>

            <div class="warn">
              <p>Deletions are the one thing undo cannot reverse. On a fixed drive they
              go to the Recycle Bin. On a USB drive or a network share Windows has no
              Recycle Bin at all, and PyreMedia says so plainly rather than implying a
              way back that doesn't exist.</p>
            </div>

            <h2 id="split">One show spread over several folders</h2>

            <p>A season per folder is how most TV arrives. Eight folders called
            <code>Will.&amp;.Grace.S01</code> through <code>S08</code> are eight things to
            match by hand, and every one of them is the same show. PyreMedia recognises
            that and shows them as a single entry - match it once, and all eight seasons
            are named together.</p>

            <p>Renaming a combined entry gathers it: the episodes move into one show
            folder with a <code>Season 01</code> ... <code>Season 08</code> underneath,
            which is the layout Kodi and Plex expect. Their <code>.nfo</code> sidecars,
            subtitles and artwork travel with them. This happens whether or not
            <em>Move episodes into season folders</em> is on - treating eight folders as
            one show is a statement that they belong together, and renaming them where
            they sat would leave the split exactly as it was.</p>

            <p>The thing this must never do is merge two shows that share a name, so it
            steps back whenever there is any doubt:</p>

            <table>
              <tr><th>What it sees</th><th>What it does</th></tr>
              <tr><td>Different years under one title - <em>Battlestar Galactica
                      1978</em> and <em>2004</em></td>
                  <td>Two separate shows. Each is combined on its own; neither is folded
                      into the other.</td></tr>
              <tr><td>The same season number twice - two folders both holding a season 1,
                      as with the British and American <em>Office</em></td>
                  <td>Nothing is combined. The folders stay exactly as they were and the
                      log says why.</td></tr>
              <tr><td>Some folders carry a year and others don't</td>
                  <td>The undated ones aren't assigned to either series - which one they
                      belong to can't be told from the name.</td></tr>
              <tr><td>A subtitle after the name - <em>Dexter</em> against <em>Dexter: New
                      Blood</em>, or <em>Star Trek</em> against <em>Star Trek: Deep Space
                      Nine</em></td>
                  <td>Different titles, so different shows. They are never combined.</td></tr>
            </table>

            <p>Spelling, though, is not identity. <code>Will.&amp;.Grace</code>,
            <code>Will and Grace</code> and <code>will.and.grace</code> are one show:
            case, punctuation, <code>&amp;</code> against <code>and</code>, and a leading
            <em>The</em> are all noise.</p>

            <p>The release leftovers in those folders - the <code>.txt</code> advert, an
            <code>.exe</code>, the <code>.sfv</code> - are offered for deletion at the same
            time, each as its own row in the preview that you can untick. They go to the
            Recycle Bin. This is offered for a combined entry even when junk deletion is
            switched off generally, because combining is an explicit instruction to make
            these folders into one. An <code>.nfo</code> holding real Kodi metadata is
            never among them, however its extension reads - the file is checked, not just
            its name.</p>

            <p>Once everything has moved out, a source folder left completely empty is
            removed. One still holding something is left exactly as it is and named in the
            log, so nothing you didn't ask about is deleted.</p>

            <p>You can switch the whole thing off with <em>Show a series split across
            folders as one entry</em> in Settings, in which case the folders are only
            reported.</p>

            <h2 id="renumber">Episodes numbered wrongly</h2>

            <p>Sometimes a season's files are numbered in an order nobody agrees with -
            a different air order, or specials counted in the run. Renumber shows every
            file beside the episode it would become, so you can fix it in one place
            rather than renaming forty files by hand.</p>

            <ul>
              <li>Files are matched to episodes by title where the filename carries one,
              which usually gets most of a scrambled season right on its own.</li>
              <li>Any row can be pointed at a different episode from a dropdown.</li>
              <li>Rows can be unticked to leave a file alone.</li>
              <li>An offset shifts a whole season at once, for the common case where
              everything is out by the same number.</li>
              <li>The source - TheTVDB, TVmaze or TMDb - can be switched at the top,
              since they disagree about episode order more often than you would hope.</li>
            </ul>

            <h2 id="remux">Remuxing: dropping tracks you don't want</h2>

            <p>A film can easily carry a dozen audio tracks and thirty subtitles in
            languages you will never use. Remuxing rewrites the file with only the
            tracks you keep. It does not re-encode: the video and the audio you keep are
            copied across untouched, so there is no loss of quality and it runs at disk
            speed rather than encoder speed.</p>

            {{IMG_REMUX}}

            <p>Files are grouped by the set of tracks they hold, so a whole season with
            the same layout is one decision rather than twenty. Each group has its own
            tick - it isn't all or nothing.</p>

            <p>Progress is shown per file with an estimate of the time left. There are
            two ways to stop:</p>

            <ul>
              <li><strong>Stop after this file</strong> - lets the file in progress
              finish and swaps it in, then stops. Nothing is left half-written.</li>
              <li><strong>Abort now</strong> - kills the current file as well. The
              part-written temporary file is discarded and the original is left exactly
              as it was.</li>
            </ul>

            <div class="note">
              <p>The original is kept, either beside the file in a
              <code>_originals</code> subfolder or in one folder of your choosing. If a
              remux turns out badly, the original is still there and the swap is in
              History.</p>
            </div>

            <div class="warn">
              <p>PyreMedia will not remux a file down to no audio at all. If your
              language rules would remove every track - because the only tracks are
              tagged <code>und</code>, or the film is in a language you didn't list -
              it says so instead of doing it.</p>
            </div>

            <h2 id="dv">Dolby Vision</h2>

            <p>Dolby Vision is two things: the RPU data carried alongside the picture,
            and a small configuration record in the container that declares it. Players
            need both. They come apart more easily than you would expect, and what
            follows is measured on a real profile 5 file rather than repeated from
            somewhere.</p>

            <table>
              <tr><th>Copying the streams with</th><th>Declaration</th><th>RPU data</th></tr>
              <tr><td>mkvmerge, Matroska out</td><td>kept</td><td>kept</td></tr>
              <tr><td>ffmpeg, Matroska out</td><td>kept</td><td>kept</td></tr>
              <tr><td>ffmpeg, <strong>MP4 out</strong></td><td><strong>lost</strong></td><td>kept</td></tr>
            </table>

            <p>So the danger is the container, not the tool. A file remuxed into MP4
            keeps every byte of its Dolby Vision data and stops declaring it, which
            means players treat it as ordinary HDR - and on profile 5, that means
            visibly wrong colours rather than a flatter picture.</p>

            <div class="warn">
              <p>Remuxing such a file again does <strong>not</strong> put the
              declaration back. Neither ffmpeg nor mkvmerge rebuilds it from the RPU
              still in the stream - both were tried, and both produced a file that still
              didn't declare it. It takes a tool that reads the RPU and writes the
              configuration - <code>dovi_tool</code> - or making the file again from its
              source, which is what re-ripping with MakeMKV amounts to.</p>
            </div>

            <p>PyreMedia spots files already in this state: Dolby Vision data in the
            picture with nothing declaring it. It reads a handful of frames rather than
            the whole file, so the check costs a moment. What it says depends on what it
            finds - that the data is still there and what will actually restore it, or
            that neither the declaration nor the data is present and no remux will help.</p>

            <p>PyreMedia sends Matroska through mkvmerge wherever it can, checks after
            every remux that the Dolby Vision configuration is still present, and
            discards the result if it isn't. It also spots files already in this state:
            RPU data in the picture with nothing declaring it.</p>

            <p>Profiles differ in how they behave on a screen that can't show them:</p>

            <table>
              <tr><th>Profile</th><th>On a non-DV display</th></tr>
              <tr><td>8.1</td><td>Falls back to HDR10 and looks correct.</td></tr>
              <tr><td>7</td><td>Dual layer, from Blu-ray. Has an HDR10 base.</td></tr>
              <tr><td>5</td><td><strong>No fallback.</strong> Without Dolby Vision
                  handling it plays with visibly wrong colours - washed out and green
                  or purple cast - rather than simply looking flat.</td></tr>
            </table>

            <p>Profile 5 files are flagged in the library as DV-only, so you know before
            you copy one to a device that can't display it. HDR10 metadata cannot be
            manufactured from a profile 5 file: the base layer isn't HDR10, so there is
            nothing to fall back to.</p>

            <h2 id="nfo">.nfo files and Kodi</h2>

            <p>An <code>.nfo</code> beside a video is how Kodi stores what it knows
            about it. PyreMedia reads them, writes them, and keeps them attached to
            their file through a rename.</p>

            <ul>
              <li>An existing <code>.nfo</code> is read first when identifying a file.
              A <code>uniqueid</code> in it is trusted over a guess from the
              filename.</li>
              <li>Existing files are preserved by default. Artwork, ratings, cast and
              anything you edited by hand are left exactly as they were.</li>
              <li>After a remux only the <code>streamdetails</code> block is refreshed,
              since that is the only part a remux can invalidate.</li>
              <li>They move and rename with the video, along with subtitles, posters,
              fanart and thumbnails.</li>
            </ul>

            <div class="note">
              <p>Kodi can scrape from several sources through add-ons. Which one you use
              matters for matching: PyreMedia can be told which source your Kodi
              install prefers, so the ids it writes are ones your Kodi will recognise.</p>
            </div>

            <h3>Artwork</h3>

            <p>Poster and fanart can be downloaded beside each file as
            <code>&lt;name&gt;-poster.jpg</code> and <code>&lt;name&gt;-fanart.jpg</code>,
            which is where Kodi looks and which follows the video through a rename.</p>

            <p>Off by default, and separately switchable from the <code>.nfo</code> files
            - a library Kodi already scrapes has artwork of its own, and this is for one
            that doesn't, or for files going to a player that never scrapes. Both
            switches are on the first-run screen and in Settings.</p>

            <p>Existing artwork is left alone unless you say otherwise, on the same
            reasoning as <code>.nfo</code> files: what is on disk may be chosen or
            hand-made. A download that returns something which isn't an image - an error
            page served with a success code, which happens - is refused rather than
            written out as a poster.</p>

            <h3>Choosing which artwork</h3>

            <p>The <strong>Artwork</strong> button, beside Renumber, opens what TMDb
            actually has. For one well-known film that was 241 posters, 108 pieces of
            fanart and 61 logos - so whatever gets picked automatically is very often not
            the one you would have chosen.</p>

            <table>
              <tr><th>Kind</th><th>Written as</th><th>Notes</th></tr>
              <tr><td>Poster</td><td><code>&lt;name&gt;-poster.jpg</code></td>
                  <td>The portrait cover art</td></tr>
              <tr><td>Fanart</td><td><code>&lt;name&gt;-fanart.jpg</code></td>
                  <td>The wide backdrop behind the menus</td></tr>
              <tr><td>Clear logo</td><td><code>&lt;name&gt;-clearlogo.png</code></td>
                  <td>The transparent title treatment. A PNG, and it has to stay one -
                      saved as a JPEG the transparency is flattened onto a background
                      and the whole point of it is gone</td></tr>
            </table>

            <p>Pick one of each and save. Choosing deliberately replaces what is there,
            whatever the preserve setting says - that setting is about not overwriting
            things unasked, and this is very much asked. Which image you chose is
            remembered, so re-opening the picker shows it marked rather than leaving you
            to guess; a <code>.jpg</code> on disk says nothing about which of two hundred
            it was.</p>

            <p>The choice applies to every video in the item, so a whole season gets the
            same poster in one go.</p>

            <div class="note">
              <p>Logos are shown against a light plate in the picker. They are
              transparent, and a white logo on a dark window is invisible.</p>
            </div>

            <h3>When an .nfo is already there</h3>

            <p>Rather than skipping the file or overwriting it, the two are combined.
            Fresh titles, plots, air dates and stream details are taken - that is the
            point of writing it - while everything the scraper has no opinion about is
            kept:</p>

            <ul>
              <li>Play count, last played, resume position, your own rating and the date
                  added. PyreMedia never knows these, so they always win over what it
                  writes. A watched series stays watched.</li>
              <li>Anything hand-added or exported by Kodi that PyreMedia doesn't produce
                  - tags, sets, artwork choices - is carried straight across.</li>
            </ul>

            <p>A file covering more than one episode is merged block by block, matched on
            the season and episode numbers inside rather than the order they appear, so
            two episodes can't end up wearing each other's history. An existing file that
            can't be read - a scene advert in ASCII art, say - simply has nothing to
            contribute, and the new one is written as normal.</p>

            <p><em>Combine with an existing .nfo</em> in Settings turns this off, which
            restores the older behaviour of leaving existing files alone entirely.</p>

            <h2 id="history">History and undo</h2>

            {{IMG_HISTORY}}

            <p>Every rename, move and deletion is recorded with the time, both names,
            and which batch it belonged to. From here you can:</p>

            <ul>
              <li><strong>Revert selected</strong> - put chosen files back.</li>
              <li><strong>Revert whole batch</strong> - undo everything one Apply did,
              including the show folder rename, in the right order.</li>
              <li><strong>Remove from log</strong> - forget rows without touching any
              file. What you lose is the ability to undo them.</li>
              <li><strong>Clear all</strong> - empty the log entirely.</li>
            </ul>

            <p>Rows can be ticked, or picked with Shift and Ctrl the way any list works;
            the ticks and the selection are the same thing. The <em>Can revert</em>
            column says up front whether each row can still be put back - a file that
            has since been moved elsewhere, or a name something else now occupies,
            can't be, and says so rather than failing when you try.</p>

            <div class="warn">
              <p>Deletions are not revertible from here. Where the drive has a Recycle
              Bin, that is where they went.</p>
            </div>

            <h2 id="settings">Settings</h2>

            {{IMG_SETTINGS}}

            <p>The parts worth knowing about:</p>

            <table>
              <tr><th>Setting</th><th>Why you would change it</th></tr>
              <tr><td>Naming formats</td>
                  <td>The shape of the names. A live example updates as you type, so a
                      broken format is obvious before it's used.</td></tr>
              <tr><td>Languages</td>
                  <td>Search and pick; each choice becomes a chip. Used both for
                      flagging foreign audio and for deciding what a remux keeps. The
                      two spellings of a language code - <code>fre</code> and
                      <code>fra</code> - are treated as the same language.</td></tr>
              <tr><td>Keep undetermined</td>
                  <td>Tracks tagged <code>und</code> - very common, and often the only
                      audio. Keeping them is the safe default.</td></tr>
              <tr><td>Keep forced subtitles</td>
                  <td>Forced subtitles translate the odd foreign line in a film that is
                      otherwise in your language. Small, and worth keeping.</td></tr>
              <tr><td>Send deletions to the Recycle Bin</td>
                  <td>On by default. A caution appears if any of your folders sit on a
                      drive where Windows won't honour it.</td></tr>
              <tr><td>Archive originals</td>
                  <td>Where pre-remux originals are kept. Beside the file is instant;
                      another drive means copying every byte.</td></tr>
            </table>

            <p><strong>Reset to defaults</strong> asks first, keeps a copy of your old
            settings, and leaves your folder list alone - losing that would turn a reset
            into a re-setup.</p>

            <h2 id="narrow">Using it in a narrow window</h2>

            <p>Below about 1000 pixels wide the three panels stack instead of sitting
            side by side, and the page scrolls as one. This is what it looks like over
            Remote Desktop from a phone.</p>

            {{IMG_NARROW}}

            <p>Scrolling follows the pointer. A section that isn't fully on screen is
            brought into place first; once it's in view the wheel scrolls inside it; and
            when it has no further to go the page moves on to the next section, so you
            never get stuck.</p>

            <h2 id="files">Where PyreMedia keeps its own files</h2>

            <p>All of it lives in <span class="path">%APPDATA%\PyreMedia</span>:</p>

            <table>
              <tr><th>File</th><th>What it is</th></tr>
              <tr><td><code>settings.json</code></td><td>Everything from Settings.</td></tr>
              <tr><td><code>settings.backup.json</code></td>
                  <td>The previous version, kept on every save.</td></tr>
              <tr><td><code>history.jsonl</code></td>
                  <td>The record of every change, one line each.</td></tr>
              <tr><td><code>pyremedia4.log</code></td>
                  <td>What happened, for when something needs explaining.</td></tr>
            </table>

            <p>Settings are written whole, through a temporary file, so an interrupted
            write leaves the previous version rather than half a file. Only one copy of
            PyreMedia runs at a time - two would each hold their own idea of the
            settings and the last one closed would win.</p>

            <h2 id="trouble">When something goes wrong</h2>

            <h3>It found nothing</h3>
            <p>Check the folder you added contains shows or films, rather than being one
            show. Check the extension is in the allowed list in Settings.</p>

            <h3>It matched the wrong thing</h3>
            <p>Search again in the Match box with a year. If a stale <code>.nfo</code>
            is pinning it to the wrong id, delete that file and rescan.</p>

            <h3>Apply is greyed out</h3>
            <p>Nothing is ticked, or every row is Already correct or Problem. A Problem
            row says what the problem is.</p>

            <h3>A file wouldn't rename</h3>
            <p>Almost always something else has it open - Kodi, Plex, a player, or
            Explorer's preview pane. The other files in the batch still go through, and
            the one that failed is named. Close whatever holds it and Apply again.</p>

            <h3>Remux says there isn't room</h3>
            <p>The new file is written beside the original before replacing it, so the
            drive needs to hold both at once. FAT32 also cannot hold a file of 4 GB or
            more, whatever the free space says.</p>

            <h3>Something looks wrong and I want to see why</h3>
            <p>The log is at <span class="path">%APPDATA%\PyreMedia\pyremedia4.log</span>,
            and About has a button to open it.</p>

            {{IMG_ABOUT}}

            <p>About also lists every source and dependency, with links - what the data
            comes from, what the tools are, and who wrote them.</p>

            <footer>
              <p>PyreMedia 4. This manual is generated from the running application:
              every picture is a real window drawn at build time, so it cannot drift
              away from what the program actually looks like.</p>
              <p>Film and television data from TheMovieDB, TheTVDB and TVmaze.
              PyreMedia is not endorsed by or affiliated with any of them.</p>
            </footer>

            </div>
            </body>
            </html>
            """);

        return sb.ToString()
            .Replace("{{IMG_MAIN}}", Img("main", "The main window: Library, Match and Changes."))
            .Replace("{{IMG_NARROW}}", Img("main-narrow", "The same window narrow, with the panels stacked."))
            .Replace("{{IMG_SETUP}}", Img("setup", "First-run setup: tools, folders and the main options."))
            .Replace("{{IMG_SETTINGS}}", Img("settings", "Settings."))
            .Replace("{{IMG_HISTORY}}", Img("history", "History, with rows ticked ready to revert."))
            .Replace("{{IMG_ABOUT}}", Img("about", "About: versions, sources, dependencies and where files are kept."))
            .Replace("{{IMG_CONFLICT}}", Img("conflict", "Resolving a name that is already taken."))
            .Replace("{{IMG_REMUX}}", Img("remux", "Remux: files grouped by the tracks they hold."));
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
