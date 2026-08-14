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
            // A picture that could not be taken leaves a slot behind saying what
            // belongs there.
            //
            // It used to be left out entirely, on the reasoning that a bracketed
            // apology mid-chapter reads worse than no picture. That holds for a
            // finished manual and not for this one: the windows can only be
            // photographed from a desktop session, so a build run anywhere else
            // silently produced a manual short of pictures that looked complete.
            // Nothing said which were missing, or that any were.
            //
            // Shaped like the figure it will become, so the page does not reflow
            // when the real one arrives.
            if (!shots.TryGetValue(key, out var path) || !File.Exists(path))
            {
                return $"""
                        <figure>
                          <div class="pending">
                            <strong>Picture to come</strong>
                            <span>{Escape(caption)}</span>
                            <code>{Escape(key)}</code>
                          </div>
                        </figure>
                        """;
            }

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
              .pending {
                border: 1px dashed var(--line); border-radius: 8px; background: var(--panel);
                padding: 2.5rem 1.25rem; text-align: center;
                display: flex; flex-direction: column; gap: .4rem; align-items: center;
              }
              .pending strong { color: var(--warn); font-size: .8rem; letter-spacing: .08em;
                                text-transform: uppercase; }
              .pending span { color: var(--dim); font-size: .9rem; max-width: 34rem; }
              .pending code { font-size: .75rem; color: var(--dim); }
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
                <li><a href="#play">Looking at a file before you name it</a></li>
                <li><a href="#rips">Files straight off a disc</a></li>
                <li><a href="#conflicts">When something is already in the way</a></li>
                <li><a href="#split">One show spread over several folders</a></li>
                <li><a href="#renumber">Episodes numbered wrongly</a></li>
                <li><a href="#remux">Remuxing: dropping tracks you don't want</a></li>
                <li><a href="#dv">Dolby Vision</a></li>
                <li><a href="#audio">Music and audiobooks</a></li>
                <li><a href="#naming">Where music files go</a></li>
                <li><a href="#duplicates">Duplicates, and how it tells them apart</a></li>
                <li><a href="#gaps">Filling an album's gaps from elsewhere</a></li>
                <li><a href="#tags">Tag problems</a></li>
                <li><a href="#books">Audiobooks</a></li>
                <li><a href="#tidy">Cleaning up, and quarantine</a></li>
                <li><a href="#nfo">.nfo files and Kodi</a></li>
                <li><a href="#history">History and undo</a></li>
                <li><a href="#settings">Settings</a></li>
                <li><a href="#narrow">Using it in a narrow window</a></li>
                <li><a href="#files">Where PyreMedia keeps its own files</a></li>
                <li><a href="#trouble">When something goes wrong</a></li>
                <li><a href="#credits">Credits and sources</a></li>
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

            <p>The outside tools are separate projects with their own licences, so
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
              <tr><td>VLC</td>
                  <td>VideoLAN</td>
                  <td><em>Optional.</em> What <strong>Play</strong> opens a file in.
                      Without it a file still plays - Windows opens it however it
                      normally would - but full screen cannot be asked for</td></tr>
            </table>

            <div class="note">
              <p>Renaming works without any of them. They are only needed for reading
              track details and for remuxing.</p>
              <p>ffmpeg, mkvmerge and VLC install through winget from the authors' own
              packages - <code>Gyan.FFmpeg</code>, <code>MoritzBunkus.MKVToolNix</code>
              and <code>VideoLAN.VLC</code>. Never a community repackage: an installer
              nobody named is not one worth running.</p>
              <p><code>dovi_tool</code> has no winget package, so it is a single
              executable downloaded from its author's releases and pointed at in
              Settings.</p>
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
              <li><strong>Scan.</strong> Everything found appears in the Library list.
              This also happens by itself when the window opens, so after the first run
              the list is usually already there - waiting to be told to look at folders
              you have already chosen is a step with no decision in it. Turn it off under
              Settings &rarr; Video if your library sits on a drive that has to spin up or
              a share that has to reconnect. Scanning only reads: nothing is proposed
              until you pick a match, and nothing happens until Apply.</li>
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

            <h2 id="play">Looking at a file before you name it</h2>

            <p><strong>Play</strong> opens the selected item in a media player. It sounds
            slight and it is the fastest answer to the question a disc rip always
            raises: a file called <code>title_t00_new.mkv</code> tells you nothing, its
            metadata tells you less, and ten seconds of watching settles it.</p>

            <p>It opens at the beginning, in a window. Full screen instead is a setting,
            under Video.</p>

            <p>It hands the file to a player rather than playing it here. That is
            deliberate: the playback built into Windows will not open Matroska, HEVC,
            DTS or TrueHD without extra codecs, which is to say it will not open most of
            a ripped library - and a preview that fails on exactly those files would look
            like the files were broken. With <a href="https://www.videolan.org/vlc/">VLC</a>
            installed, PyreMedia can also ask for full screen; without it the file opens
            however Windows would open it, and full screen is not available because
            Windows offers no way to ask for it.</p>

            <h2 id="rips">Files straight off a disc</h2>

            <p>MakeMKV names what it rips after the disc's label and the title's position
            on it - <code>title_t00_new.mkv</code> in a folder called
            <code>MX2-0N-NW2_DES</code>. Neither has ever heard of the show. A Blu-ray
            holds playlists, and which playlist is episode three is written down nowhere
            on the disc.</p>

            <p>So a rip is recognised as one and treated differently. The row says
            <em>9 titles from a disc</em>, and no search term is invented from a volume
            label - asking a provider about <code>MX2-0N-NW2_DES</code> is not a poor
            guess, it is a guess that cannot succeed. Type the show's name; use
            <a href="#play">Play</a> if you are not sure what you ripped.</p>

            <p>Once a show is chosen, <strong>Renumber</strong> opens already filled in.
            Getting there means measuring the disc:</p>

            <ul>
              <li><strong>Extras are set aside.</strong> A title far shorter than the
              others is a trailer or a menu loop; one far longer is the "play all", which
              every TV disc offers and nobody wants as a file.</li>
              <li><strong>The same episode offered twice is reduced to once.</strong>
              Discs commonly carry a chaptered playlist for the menu and a plain one for
              the "play all" to string together. The one with chapter marks is kept.</li>
              <li><strong>What remains is numbered in the disc's own order</strong>, which
              is almost always broadcast order.</li>
            </ul>

            <div class="warn">
              <p><strong>Almost always is not always, and the disc does not say.</strong>
              A wrongly numbered rip looks completely correct afterwards - the files play,
              the names read properly - and the mistake surfaces weeks later when episode
              four turns out to be episode five. The caution above the grid says as much,
              and every row is still a dropdown. Check the first and the last against the
              episode list before applying.</p>
            </div>

            <h3>When it cannot tell, it does not pretend</h3>

            <p>Disc order gives the sequence, not where it starts. Rip disc three of four
            and numbering from episode one is wrong by a constant - the files play, the
            names read properly, and nobody notices until they sit down to watch in
            order. Three things can supply the missing offset, and they are tried
            cheapest first:</p>

            <ol>
              <li><strong>The rest of the set.</strong> If five ripped folders hold exactly
              the season's twenty-one episodes between them, the third starts at episode
              nine. This costs nothing but a directory listing, and where the total is
              exact it is not a guess at all. One title too many means an extra was
              ripped and counting cannot say which - so it is refused rather than
              fudged.</li>
              <li><strong>The runtimes.</strong> A season is rarely uniform, and a run of
              five consecutive lengths is a recognisable shape even at the whole minutes
              providers publish. Sliding the disc's own durations along the season finds
              where they fit.</li>
              <li><strong>The catalogue.</strong> Where the disc is in TheDiscDb, its
              titles are named outright rather than placed.</li>
            </ol>

            <p>When none of them can answer - the disc is not catalogued, it is the only
            one ripped so there is no set to count, and every episode runs to the same
            broadcast slot so the runtimes say nothing - <strong>the rows are left
            unticked</strong>. The numbering shown is the disc's own order starting from
            episode one, which is a guess wearing the clothes of an answer, and Apply does
            nothing until you have ticked the rows yourself.</p>

            <p>Set the first row, use <em>Offset</em> to move the rest with it, and tick
            them once they read correctly. <a href="#play">Play</a> will show you a title
            if you are unsure which episode you are looking at.</p>

            <h3>Why length alone is not enough</h3>

            <p>Two titles of the same length look like the same episode offered twice, and
            sometimes are. Measured on a real DVD rip of an animated series: nine
            episodes running 21:17 to 21:22, two of them identical to the tenth of a
            second, file sizes within half a percent of each other. Television is cut to a
            broadcast slot, so of course they match. Treating equal length as equal
            content would have called seven of those nine duplicates and thrown them
            away.</p>

            <p>So length only nominates a pair, and their audio decides - the same
            acoustic fingerprint the music side uses, because whether two files are the
            same recording is one question whether it is asked of a song or an episode.
            Fingerprinted, those identical-length episodes diverge immediately.</p>

            <p>Without ffmpeg nothing can hear them, and then both titles are kept and the
            reading says why. A duplicate left in place wastes disk; an episode discarded
            is gone.</p>

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

            <p>Those three set one row. Two more buttons decide the whole list at once,
            by asking the same question of every pair rather than applying one answer to
            all of them:</p>

            <table>
              <tr><th>Rule</th><th>What happens</th></tr>
              <tr><td>Keep the newer</td>
                  <td>Replace where the incoming file is newer, skip where it isn't.</td></tr>
              <tr><td>Keep the larger</td>
                  <td>Replace where the incoming file is bigger, skip where it isn't.
                      Bigger usually means a better rip, and does not always - a
                      bloated re-encode is bigger than the remux it came from.</td></tr>
            </table>

            <p>Both leave every row visible with its decision shown, so a rule that got
            one pair wrong can be changed back before anything happens.</p>

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

            <h2 id="audio">Music and audiobooks</h2>

            <p>The window has two tabs. <strong>Video</strong> is everything above:
            films and television. <strong>Audio</strong> is music and audiobooks, and
            works the same way - scan, look at what it proposes, press Apply.</p>

            <p>Inside Audio there is a second row: <em>All audio</em>, <em>Music</em>,
            <em>Audiobooks</em>. It only narrows what the list shows.</p>

            {{IMG_AUDIO}}

            <p>Music needs no accounts. Everything except one optional feature runs on
            your own machine with no network at all. What it does want is
            <strong>ffmpeg</strong>, and it says plainly what stops working without it
            rather than quietly doing less.</p>

            <div class="note">
            <p><strong>A scan takes a few minutes on a large library.</strong> Seventeen
            thousand files is about six seconds of reading tags on a local disk and
            considerably longer over a network share. There is a bar and an estimate; it
            declines to guess the time until it has enough files behind it to say
            something true.</p>
            </div>

            <h3>What a scan does</h3>

            <ol>
              <li>Reads the tags out of every audio file.</li>
              <li>Reads what else the folders say - a scraped <code>album.nfo</code>
                  often knows the year, the exact track lengths and the MusicBrainz
                  identifiers that the files themselves do not.</li>
              <li>Groups the tracks into albums.</li>
              <li>Measures the length of any track whose number collides with another,
                  because that is what tells a duplicate from a different version.</li>
              <li>Compares the audio itself where the tags cannot settle it.</li>
              <li>Works out where every file should go.</li>
            </ol>

            <p>Nothing is written until Apply. Apply then finishes the job: it moves the
            music, writes any tag corrections you asked for, and offers to clear out
            everything that is not music and any folders left empty. Those last two are
            offered rather than done.</p>

            <h2 id="naming">Where music files go</h2>

            <p>The layout is a pattern you can change:</p>

            <pre>{albumartist}/{album}[ ({year})]/[{disc}-]{track:00} {title}</pre>

            <p>Braces are fields. Square brackets are optional sections that disappear
            <em>whole</em> when a field inside them is empty - which is what lets one
            pattern serve a library where some albums have a year and some do not. Disc
            numbers only appear on real multi-disc sets.</p>

            <p>A worked example updates underneath as you type, and problems are named
            before a scan rather than after it: an unknown field, unpaired brackets, or a
            pattern with no title or track number in it, which would land every song on
            an album on the same filename.</p>

            <table>
              <tr><th>Field</th><th>What it is</th></tr>
              <tr><td><code>{albumartist}</code></td><td>Who the album is by. "Various Artists" on a compilation.</td></tr>
              <tr><td><code>{artist}</code></td><td>Who this track is by, which on a compilation is not the album artist.</td></tr>
              <tr><td><code>{album}</code></td><td>The album title.</td></tr>
              <tr><td><code>{title}</code></td><td>The track title.</td></tr>
              <tr><td><code>{year}</code></td><td>Four digits, where the file knows them.</td></tr>
              <tr><td><code>{track}</code>, <code>{disc}</code></td><td>Numbers. Pad them: <code>{track:00}</code>.</td></tr>
              <tr><td><code>{initial}</code></td><td>First letter of the album artist, for A/B/C folders. "The" is ignored, so the Beatles file under B.</td></tr>
              <tr><td><code>{genre}</code></td><td>As tagged. Often empty and often wrong; use with care.</td></tr>
            </table>

            <p>Three presets sit under the box: <em>Artist / Album</em>, <em>Under a
            letter</em> (worth having when one folder would otherwise hold two and a half
            thousand artists), and <em>Artist - Title</em>.</p>

            <div class="note">
            <p><strong>Filing the same library twice changes nothing the second time.</strong>
            That sounds obvious and is not - a rule that reads a folder name it wrote
            itself can move the same album on every scan, for ever. It is checked.</p>
            </div>

            <h2 id="duplicates">Duplicates, and how it tells them apart</h2>

            <p>Two files claiming track 4 of one album are one of three things: the same
            recording twice, the same song in two different versions, or two different
            songs where somebody numbered one wrongly. Only the first is a duplicate, and
            deleting a file on a wrong guess is the mistake this program is built to
            avoid.</p>

            <p>Titles and durations get it right most of the time. Measured against a real
            library of seventeen thousand files, they agreed with the audio 95.2% of the
            time - and the 4.8% that disagreed was not evenly harmless. Seventeen
            duplicates were missed because the titles were spelt differently. Two questions
            were asked that needn't have been. And one pair - two files of identical 4:58
            duration, same title, sharing only 60.5% of their audio - would have had one of
            them deleted as a copy of something it is not.</p>

            <p>So <strong>Compare the audio when in doubt</strong> is on by default. It
            uses an acoustic fingerprint, which is a summary of what the recording
            actually sounds like, and it needs no account and no network. It runs only on
            the files whose track numbers collide, which is about one in twenty, and costs
            roughly four tenths of a second each.</p>

            <p>With it on, that same library had <strong>nothing left to ask</strong>.</p>

            <p>What it finds redundant is <em>never deleted</em>. See
            <a href="#tidy">quarantine</a>.</p>

            <h2 id="gaps">Filling an album's gaps from elsewhere in the library</h2>

            <p>A song sits on its own album and on three soundtracks, and a library
            assembled over thirty years routinely holds one of those and not the other.
            The album is missing track 7; track 7 is on disk, filed under a film.</p>

            <p>Where that is the case the file can be <em>copied</em> in - copied, not
            moved, because the soundtrack is a real album too and taking a track out of
            it to repair another one just moves the hole.</p>

            <table>
              <tr><th>How sure</th><th>What it means</th></tr>
              <tr><td>Certain</td>
                  <td>The file carries the MusicBrainz recording id the listing names.
                      Not a resemblance - the same recording, identified.</td></tr>
              <tr><td>Likely</td>
                  <td>The title matches and the length is what the listing says it
                      should be.</td></tr>
              <tr><td>Doubtful</td>
                  <td>The title matches and the length does not. This is the remix, the
                      live take, the radio edit. Shown so you can see it; never filled
                      in on its own.</td></tr>
            </table>

            <p>Two guards matter more than the confidence:</p>

            <p><strong>It only considers files by the same artist.</strong> Without that
            check, Citizen King's missing <em>Blue Monday</em> matches Orgy's cover of
            it - same title, and the durations are close enough to look like agreement.
            One album would quietly acquire another band's recording.</p>

            <p><strong>An album has to be mostly present before its holes count as
            holes.</strong> If half the listing is missing, that is not an album with
            gaps in it - that is a record nobody ripped, and offering to assemble it out
            of soundtrack appearances would build something that never existed.</p>

            <p>Where two different recordings of the song are both on disk, it stops and
            asks rather than choosing. Two candidates is exactly the case where a guess
            is worth least.</p>

            <h2 id="tags">Tag problems</h2>

            <p>The <strong>Tag problems</strong> button reports two different things, and
            the difference matters more than the total.</p>

            <h3>Wrong on its own terms</h3>

            <p>A file that is wrong however you look at it: a stray URL in the title, a
            track number repeated into the title, "Unknown Artist", doubled spaces,
            SHOUTING. Some of these fix themselves - the answer is still in the file, and
            the repair is exact. Others do not: "Unknown Artist" is certainly wrong and
            nothing in the file knows who it is.</p>

            <p>Tick <strong>Fix tags inside the files</strong> before Apply to write the
            ones that fix themselves. It is off by default, because moving a file is
            undone by moving it back and rewriting one is a bigger promise. Every write
            goes through History like everything else.</p>

            <h3>Wrong only in company</h3>

            <p>Nothing is the matter with <em>Harry Potter and the Philosopher's Stone
            (Full-Cast Edition) (Unabridged)</em> read on its own. It is only wrong beside
            its six siblings, none of which say "(Unabridged)" - and the result is one book
            filed away from the rest of the series where nobody looks for it.</p>

            <p>These open a separate list, one row at a time, and
            <strong>every row starts unticked</strong>. That is deliberate. The suggestion
            is whatever most of the set says, and the majority is not always right: in one
            real library thirteen files spell a band "Cherry Poppin Daddies" and one spells
            it "Cherry Poppin' Daddies". The one is correct.</p>

            {{IMG_CONSISTENCY}}

            <p>Read the rows. Tick the ones you agree with.</p>

            <h3>Identify by ear</h3>

            <p>For files whose tags say nothing usable at all, the audio can be sent to
            AcoustID and the answer looked up in MusicBrainz. This is the one feature here
            that talks to anybody else about your library, so it is worth being exact about
            what leaves the machine: <strong>an acoustic fingerprint and a duration</strong>.
            Not the audio, not the filename, not the path. No audio can be reconstructed
            from a fingerprint.</p>

            <p>It needs a free AcoustID key, entered in Settings. It never writes anything -
            it shows you what it found, including any file where what the audio says
            disagrees with what the tags claim.</p>

            <h2 id="books">Audiobooks</h2>

            <p>A book read into a music library becomes an album by an artist nobody has
            heard of with fifty-seven tracks called "Chapter 1" through "Chapter 57", and
            every rule meant for music then does the wrong thing to it. So books are
            recognised and kept apart.</p>

            <p>An <code>.m4b</code> is a book outright - the format exists so players
            remember your place, and nobody ships an album as one. Several in a folder are
            several books, not one book in pieces. Otherwise it looks for chapter
            numbering, a spoken-word genre, long tracks and words like "unabridged".</p>

            <p>The bar is deliberately high. A film score with movements called
            "Chaconne: Part 1", a cast recording on "Disc 1", and a single whose files are
            named "Track 01" are all <em>not</em> books, and were all wrongly called books
            by an earlier version.</p>

            <p>Books file by author and chapter, not album and track, because a book has
            neither:</p>

            <pre>{author}/{book}[ ({year})]/{chapter:000} {chaptertitle}</pre>

            <p>A book's own tags cannot be trusted for this - one ripped from CDs carries
            the disc as the album and the publisher as the artist - so the author and title
            come from what the whole folder says.</p>

            <h2 id="tidy">Cleaning up, and quarantine</h2>

            <p><strong>Nothing is ever deleted.</strong> Redundant copies and anything the
            clean-up removes are moved to a folder called
            <code>PyreMedia Quarantine</code>, beside your library rather than inside it so
            a later scan does not pick them straight back up. Enough of each file's old path
            is kept that two albums' "01 Intro.mp3" stay apart.</p>

            <p>All of it undoes from History.</p>

            <p><strong>Clean up</strong> takes out everything that is not music: artwork,
            scraped metadata, playlists, system leftovers, ripping logs. On one library that
            was 9,609 files and 2.7 GB.</p>

            <div class="warn">
            <p><strong>It will refuse if the tags have not been written yet.</strong> The
            scraped <code>.nfo</code> files it removes hold the only copy of some of your
            library's information - years, MusicBrainz identifiers, exact track lengths.
            Deleting them before that is written into the audio loses it silently, so the
            program will not do it in that order. Running clean-up from Apply handles this
            for you.</p>
            </div>

            <p>Afterwards it offers to remove folders left holding nothing. That one is not
            undoable from History, and the dialog says so - an empty folder has no contents
            to restore.</p>

            <h3>Evening out the volume</h3>

            <p>A library ripped across thirty years plays at wildly different levels.
            <strong>Even out the volume (ReplayGain)</strong> measures each track and writes
            the figure into its tags; the player does the rest. The audio is never
            re-encoded and deleting the tags undoes it completely.</p>

            <p>It is off by default, and slow - every file has to be decoded once to be
            measured. Albums are measured as albums, so a deliberately quiet track stays
            quiet relative to its record rather than being levelled to match everything
            else.</p>

            <h2 id="nfo">.nfo files and Kodi</h2>

            <p>An <code>.nfo</code> beside a video is how Kodi stores what it knows
            about it. PyreMedia reads them, writes them, and keeps them attached to
            their file through a rename.</p>

            <p>Television gets two kinds. Each episode has its own beside the file, and
            the show has one <code>tvshow.nfo</code> in the show folder, above the
            season folders. Both matter: without the show one, Kodi has nothing to
            attach the series artwork, plot, studio or rating to, and identifies the
            series by scraping the folder name instead - so a folder it reads
            differently becomes a second, half-empty series sitting beside the real
            one.</p>

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

            <p>Settings is grouped into tabs, by what you are trying to do rather
            than by which part of the program reads the value - the libraries sit
            beside the folders they receive from, and the external tools have a
            tab of their own.</p>

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
              <tr><td>Scan when the window opens</td>
                  <td>On. Saves pressing Scan every time. Reading is all it does.</td></tr>
              <tr><td>Play previews full screen</td>
                  <td>Off. A preview is usually a glance to settle which episode a file
                      is; full screen suits a proper look at a transfer. Needs VLC.</td></tr>
              <tr><td>Libraries</td>
                  <td>Where <em>Move completed</em> sends finished files. Four separate
                      folders, because they are not interchangeable - Kodi scans films
                      and television separately, and a book filed among the albums is a
                      book nobody finds again. Blank is fine; the button asks when it
                      needs one.</td></tr>
              <tr><td>Music naming</td>
                  <td>The same box as on the Audio tab, and the same setting - changing
                      it in either place changes it in both.</td></tr>
              <tr><td>AcoustID key</td>
                  <td>Only needed to identify music whose tags say nothing at all. Free,
                      and the only key here used to ask about your own files. Clearing
                      the box turns the feature off.</td></tr>
              <tr><td>Give each film its own folder</td>
                  <td>Off by default, and a real fork in the road: on, every film becomes
                      <em>Arrival (2016)/Arrival (2016).mkv</em>, which is what Kodi and
                      Plex expect and where a poster and subtitles have somewhere to
                      live. Off, films sit loose in one folder. Changing it later moves
                      every film you have.</td></tr>
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

            <h3>It renamed the episodes but made no Season folders</h3>
            <p>That is <em>Move episodes into season folders</em>, in Settings under
            Video. It is on by default, so if you are seeing this it has been turned
            off - with it off the files are renamed exactly where they sit, which on a
            show folder full of loose episodes gives perfect filenames and no
            <code>Season 01</code> beneath them.</p>

            <p>It was off by default once, on the reasoning that renaming inside a
            folder and rearranging a library are different sizes of promise. The result
            was a scan that looked like it had half worked, and was reported as a fault
            more than once. Season folders are also what Kodi, Plex and Jellyfin expect,
            so the default that needed explaining was the one doing less.</p>

            <p>Turning it on also allows the show's own folder to be renamed to the
            canonical title.</p>

            <p>One exception applies either way: an episode sitting loose in a scan root
            is given a show folder and a season folder even with the setting off. It is
            in no show's folder, so leaving it there preserves no arrangement - it
            declines to make one.</p>

            <p>When a scan meets a show keeping its episodes loose, it says so beside the
            change count rather than leaving a list of moves to be puzzled over. A show
            already in season folders is silent, so a tidy library never nags.</p>

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

                        <h2 id="credits">Credits and sources</h2>

            <p>PyreMedia does very little on its own. What follows is what it stands on,
            named so the sources are not taken on trust.</p>

            <h3>Programs it runs</h3>

            <p>None are bundled. Each is a separate project under its own licence, and
            PyreMedia finds it on your PATH and offers to install it from the author's
            own package - never a community repackage, because an installer nobody
            named is not one worth running.</p>

            <table>
              <tr><th>Tool</th><th>By</th><th>Licence</th><th>Where it comes from</th></tr>
              <tr><td>FFmpeg (<code>ffmpeg</code>, <code>ffprobe</code>)</td>
                  <td>The FFmpeg project; Windows builds by Gyan Doshi, one of the two
                      builders <a href="https://ffmpeg.org/download.html">ffmpeg.org</a>
                      links to</td>
                  <td>LGPL-2.1 / GPL, depending on build</td>
                  <td><a href="https://www.gyan.dev/ffmpeg/builds/">gyan.dev</a> ·
                      winget <code>Gyan.FFmpeg</code></td></tr>
              <tr><td>MKVToolNix (<code>mkvmerge</code>)</td>
                  <td>Moritz Bunkus</td>
                  <td>GPL-2.0</td>
                  <td><a href="https://mkvtoolnix.download/">mkvtoolnix.download</a> ·
                      winget <code>MoritzBunkus.MKVToolNix</code></td></tr>
              <tr><td>VLC</td>
                  <td>VideoLAN</td>
                  <td>GPL-2.0</td>
                  <td><a href="https://www.videolan.org/vlc/">videolan.org</a> ·
                      winget <code>VideoLAN.VLC</code></td></tr>
              <tr><td><code>dovi_tool</code></td>
                  <td>quietvoid</td>
                  <td>MIT</td>
                  <td><a href="https://github.com/quietvoid/dovi_tool/releases">its own
                      releases</a> - no winget package exists</td></tr>
            </table>

            <h3>Where the information comes from</h3>

            <table>
              <tr><th>Source</th><th>Used for</th><th>Key needed</th></tr>
              <tr><td><a href="https://www.themoviedb.org/">TMDb</a></td>
                  <td>Searching, film metadata, artwork, the episode spine</td>
                  <td>Yes - free</td></tr>
              <tr><td><a href="https://thetvdb.com/">TheTVDB</a></td>
                  <td>Episode data, especially season-0 specials TMDb omits</td>
                  <td>Yes - free</td></tr>
              <tr><td><a href="https://www.tvmaze.com/">TVmaze</a></td>
                  <td>A third opinion on episode numbering</td>
                  <td>No</td></tr>
              <tr><td><a href="https://musicbrainz.org/">MusicBrainz</a></td>
                  <td>Album and recording data for music</td>
                  <td>No</td></tr>
              <tr><td><a href="https://acoustid.org/">AcoustID</a></td>
                  <td>Identifying music whose tags say nothing at all</td>
                  <td>Yes - free, and optional</td></tr>
              <tr><td><a href="https://fanart.tv/">fanart.tv</a></td>
                  <td>Not queried. Listed because artwork URLs in existing Kodi
                      <code>.nfo</code> files often point there, and those files are
                      read and preserved</td>
                  <td>No</td></tr>
            </table>

            <p>Acoustic fingerprints are made with <a
            href="https://acoustid.org/chromaprint">Chromaprint</a>, which ships inside
            ffmpeg. Comparing two files locally needs no account and no network; only
            asking AcoustID what a recording <em>is</em> reaches out, and that sends a
            fingerprint and a duration - not the audio, not the filename, not the
            path.</p>

            <div class="note">
              <p>TMDb requires this to be said, and it is true of the others as well:
              this product uses the TMDb API but is not endorsed or certified by TMDb.</p>
            </div>

            <h3>What PyreMedia is</h3>

            <p>Written from scratch on .NET 10 - the engine, the interface, the
            providers, the remux pipeline and the metadata handling. It is not a fork, a
            port or a repackaging of anything. It was inspired by MediaScout 3.x, a
            Windows renamer unmaintained for years, and shares no code with it.</p>

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
            .Replace("{{IMG_REMUX}}", Img("remux", "Remux: files grouped by the tracks they hold."))
            .Replace("{{IMG_AUDIO}}", Img("audio", "The Audio tab after a scan: albums grouped, with what it proposes for each file."))
            .Replace("{{IMG_CONSISTENCY}}", Img("consistency", "Files that disagree with the rest of their album. Every row starts unticked."));
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
