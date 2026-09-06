# PyreMedia

A Windows media library organiser. It renames and files TV, film, music,
audiobooks, comics and ebooks, repairs the metadata around them, and strips
audio and subtitle tracks you don't want — without ever writing to disk until you have seen the
change listed and approved it, and without ever making a change you cannot
undo.

Windows · .NET 10 · WPF

---

## Why this exists

If you have ripped your own discs, you know what the shelf looks like
afterwards. MakeMKV hands you `title_t00.mkv` and leaves the naming to you. A
boxset rips one folder per disc, so a four-season set becomes twelve folders
that know nothing about seasons. A UHD disc brings a dozen dubs and thirty
subtitle tracks, several gigabytes of them, for a film you will only ever watch
in one language. CDs ripped across twenty years carry whatever tags whichever
program was installed at the time felt like writing, and half of them disagree
about the album.

None of that is a mistake you made. It is what ripping produces, and it has to
be tidied up afterwards before Kodi or Plex will recognise any of it.

The tools that tidy it up tend to do one of two things: work silently, so you
find out what happened when something is missing, or insist the library is
already in order before they will help — which is the problem.

PyreMedia is built the other way round: **it shows you every change before it
makes one, and records every change it makes so you can take it back.** These
are your discs and your rips; nothing here treats them as disposable.

## What it does

### Renames and files

Episodes and films are renamed to a pattern you set, using readable placeholders
rather than numbered ones:

    {title} {season}x{episode} {name}      ->  Firefly 01x05 Out of Gas
    {title} ({year})                       ->  Blade Runner 2049 (2017)

`{series}`, `{show}`, `{episodetitle}` and others work as synonyms, case is
ignored, and a mistyped placeholder falls back to the default rather than
silently dropping a field. Files can be renamed where they sit, or gathered into
`Show (Year)/Season 01/` — the layout Kodi, Plex and Jellyfin expect.

### Recognises a show split across folders

A boxset rips one folder per disc, so a series arrives as eight or twelve
folders that know nothing about each other. They are recognised as **one
entry**, matched once, and gathered into a single show folder — instead of being
matched a dozen times by hand.

It refuses to guess where guessing is expensive. Differing years separate two
series sharing a name; a season number appearing twice stops the merge and says
why. *Dexter* and *Dexter: New Blood* stay apart, as do the two *Battlestar
Galacticas* and every *Star Trek* after the first.

### Remuxes

Drops audio and subtitle tracks you don't want, using ffmpeg or mkvmerge. No
re-encoding, so quality is untouched and it is fast.

- Keep rules by language, with forced subtitles preserved regardless — they
  carry translated on-screen text, and dropping them silently breaks playback.
- **Dolby Vision and 3D are detected and protected.** Where a remux would risk
  losing the DV configuration record or an MVC 3D layer, it refuses rather than
  produce a file that plays flat.
- The original is never touched until the new file has been written and
  verified. What happens to it then — kept beside the file, moved to an archive
  folder, or recycled — is set on the card before you start, not asked in a
  dialog you can never see again.
- Interrupted runs are detected on the next launch and can be finished or
  cleaned up.

### Metadata and artwork

Writes Kodi-format `.nfo` sidecars and artwork, from TMDb and TheTVDB.

- **Existing `.nfo` files are merged, not overwritten.** Fresh titles, plots and
  stream details are taken; play counts, last-played dates, resume positions,
  your own ratings and anything hand-added survive. A watched series stays
  watched.
- Multi-episode files are merged block by block, matched on the season and
  episode numbers inside rather than their order, so two episodes cannot inherit
  each other's history.
- Poster, fanart and clear logo can be chosen from what the providers offer,
  with a picker rather than a lucky dip. Clear logos are enforced as transparent
  PNG, since that is the only thing Kodi can use.
- **The people, not just the plot.** Cast in billing order with their characters
  and headshots, directors, writers, genres, studios, countries, certificates,
  runtimes and ratings. A rating carries the number of votes behind it, because
  8.4 from eleven people and 8.4 from forty thousand are not the same number. A
  score with fewer than two votes is not written at all - one person's opinion
  is not a rating, and writing it would only distort the sorting.
- **Fills in what is already on disk.** Files whose `.nfo` names its own title -
  by TMDb, IMDb or TheTVDB id, in either the modern or the legacy spelling - can
  be brought up to date without matching them again, because the id says which
  title it is and nothing has to be guessed. A file that names no title is
  listed for you rather than matched on a name that might belong to something
  else.
- Existing `.nfo` files are merged, never replaced. Watch state, your own
  ratings, hand-added tags and artwork paths are carried across untouched;
  fresher facts win only where both files have something to say.

### Organises music and audiobooks

The same idea applied to an audio library: read what is there, show what it
proposes, change nothing until you say so.

- **Files by a pattern you set** — `{albumartist}/{album} ({year})/{track} {title}`
  by default, with optional sections that vanish when a field is empty, so one
  pattern serves a library where some albums have a year and some do not.
- **Tells a duplicate from a different version.** Two files claiming track 4 are
  the same recording, the same song in two versions, or two different songs
  numbered wrongly — and only the first is a duplicate. Where titles and lengths
  disagree it compares the audio itself, using an acoustic fingerprint that needs
  no account and no network. Measured on a real library of seventeen thousand
  files, titles and durations alone were right 95.2% of the time; the fingerprint
  settled the rest, including a pair that would otherwise have had one deleted as
  a copy of something it was not.
- **Finds tag problems worth fixing** — a URL left in a title, a track number
  repeated into it, "Unknown Artist" — and separates them from the ones that are
  only wrong in company, like the one book in a series whose title says
  "(Unabridged)" when its six siblings do not. Those open a list where every row
  starts unticked, because the majority is not always right.
- **Audiobooks are handled as books**, not as albums: chapters kept in order,
  filed under author and title rather than scattered by track number.
- **ReplayGain** can even out volume across a library ripped over thirty years.
  Off by default, tag-only — the audio is never re-encoded.
- Redundant copies are moved to a quarantine folder beside the library, never
  deleted, and all of it undoes from History.

### Organises comics and ebooks

The same again for a shelf rather than a library, and the same rule: nothing is
written until the plan has been read.

- **Files by a pattern you set** — `{series} ({year})/{series} ({year}) #{issue}`
  for comics, `{author}/{title} ({year})` for books, with the same optional
  sections that vanish when a field is empty.
- **Reads what the file already says of itself.** A CBZ or CBR usually carries a
  `ComicInfo.xml` written by whoever scanned it, and an EPUB carries real
  metadata written by the publisher. Where that disagrees with an online source,
  both answers are shown side by side rather than one silently winning.
- **A folder of loose pages is a comic nobody packed.** It is recognised as one,
  and the pages are kept in the order they will be packed, with anything that is
  not a page travelling alongside them.
- **Tells a story page from an advertisement.** Where the scanner already marked
  them, that judgement is used, because a person looking at the page beats a
  guess made from pixels afterwards. Where it did not, the words on the page are
  read and the verdict comes with the evidence, in words you can check against
  the page yourself.
- **Ebook text is read, never guessed at.** An EPUB is a zip of XHTML, so the
  words are already words. No OCR goes anywhere near it — that is the whole
  difference between a book and a comic: a comic has to be looked at, a book only
  has to be opened.
- **A reader**, so you can see what a file actually is before deciding anything
  about it.

### Makes sense of a disc rip

MakeMKV names what it rips after the disc's label and the title's position on
it — `title_t00_new.mkv` in a folder called `MX2-0N-NW2_DES`. Neither has ever
heard of the show, and a Blu-ray records no episode numbers: which playlist is
episode three is written down nowhere on the disc.

- **Recognised as a rip** rather than searched for under its volume label, which
  is not a poor guess but a guess that cannot succeed.
- **Extras are set aside** — a title far shorter than the rest is a trailer, one
  far longer is the "play all" every TV disc carries.
- **The same episode offered twice is reduced to once.** Discs commonly hold a
  chaptered playlist for the menu and a plain one for the "play all" to string
  together.
- **Length nominates a duplicate; the audio decides.** Measured on a real DVD
  rip: nine episodes running 21:17 to 21:22, two identical to the tenth of a
  second, file sizes within half a percent. Television is cut to a broadcast
  slot, so equal length proves nothing — treating it as proof would have thrown
  away seven of those nine.

Which episodes they are is answered three ways, cheapest first: counting a
complete set of ripped discs against the season's episode count; matching the
run of durations against the published runtimes to find which disc of the season
it is; or [TheDiscDb](https://thediscdb.com), where the disc is catalogued.

**When none of them can answer, the rows are left unticked.** The disc's own
order is still shown, because it is usually right — but nothing is applied until
you have said so.

### Plays a file so you can see what it is

Ten seconds of watching answers "which episode is this?" faster than anything
else. Opens in VLC or whatever the system uses; full screen is a setting.

### Undo

Every rename, move, delete and remux is appended to a history file. Any batch
can be reverted, including ones that renamed the containing folder. Deletions go
to the Recycle Bin where the drive has one — and where it does not, the app says
so rather than implying a way back that does not exist.

## The safety model

This is the part worth reading if you are deciding whether to trust it with a
library.

- **Nothing is written until you approve it.** Scanning only reads. Every
  proposed change appears as a row you can untick.
- **Collisions are resolved before anything is written**, not one at a time
  half-way through a batch.
- **Cross-volume, FAT32 and network drives are accounted for** — the 4 GB file
  ceiling, missing Recycle Bins on removable and network drives, and the fact
  that moving between drives copies every byte.
- **Junctions and symlinks are never followed** when deciding what to delete.
  A folder containing one is left alone entirely.
- **A folder is only ever removed when it is genuinely empty.** One stray file
  and it stays, and is named in the log.
- **Reparse loops, permission-denied subfolders and unreadable files** are
  skipped and reported, not fatal.

## Requirements

- Windows 10/11, x64
- A free **TMDb** API key — every search goes through it, for both TV and film
- Optionally a **TheTVDB** key for episode data: it carries season-0 specials
  TMDb often omits, and numbers some long-running shows differently

No key is bundled. A key is registered to an account, and a shared one gets
revoked the moment it is abused — so first-run setup asks for yours and links
to both registration pages.

### External tools

Not bundled; each is a separate project under its own licence. Setup can install
the first three via winget and shows what is missing.

| Tool | Needed for |
|---|---|
| ffprobe | reading tracks, codecs, HDR and Dolby Vision — **required** |
| ffmpeg | remuxing — **required** |
| mkvmerge | remuxing Dolby Vision and 3D safely — recommended |
| dovi_tool | repairing a file that lost its Dolby Vision declaration — rarely |

## Where it keeps things

Everything lives in `%APPDATA%\PyreMedia`, per user, and nothing is written
beside the executable:

| | |
|---|---|
| `settings.json` | every option, including your folder list and API keys |
| `history.jsonl` | append-only record of every change — this is what Undo reads |
| `pyremedia.log` | diagnostics, rotated at 2 MB |

## Building

    dotnet build PyreMedia.NET/PyreMedia.slnx -c Release

A single self-contained executable, no .NET install needed on the target
machine:

    dotnet publish PyreMedia.NET/src/PyreMedia.App -c Release -r win-x64 ^
      --self-contained true -o PyreMedia.NET/publish --nologo ^
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
      -p:EnableCompressionInSingleFile=true -p:DebugType=none

Builds are deterministic — a clean rebuild reproduces the same SHA-256, so a
published checksum verifies the source and not merely the file.

The manual, with screenshots, is generated from the running application:

    dotnet run --project PyreMedia.NET/tools/ManualBuilder -c Release

## Status

Version 2.1.0 — cast, crew and ratings in the `.nfo` files, a pass that fills
those in on media you already have, and release screenshots cleared out with
everything else.

Some things are proven further than others, and it is worth saying which.
Renaming, conflict handling and undo are exercised daily on a large library.
Remuxing is used regularly but on a narrower range of files, and the 3D and
Dolby Vision paths have been tried on few enough files to be worth calling out.
The music side has been run end to end on a library of seventeen thousand files.

The comic and ebook providers are the least proven part of this. Parsing, naming
and the provider chain are covered by tests, but the three clients that fetch
data — Grand Comics Database, Metron and Comic Vine — have never spoken to a
real source, because each needs an account or a six-gigabyte database dump. They
are written from the documentation and from what other tools do, and first
contact with a live source is where any mistake in them will be found. Reading
files that are already on disk does not depend on any of that.

Nothing here deletes anything without showing it first, and renames undo from
History.

## Author

Built by [jrwebersoft](https://github.com/jrwebersoft).

## Licence

[PolyForm Noncommercial 1.0.0](LICENSE.md) — free for anyone to use, change and
share for any **noncommercial** purpose.

That explicitly includes personal use — study, hobby projects, private
entertainment — and use by charities, schools, public research bodies and
government, whatever their funding. Selling it, or building it into something
sold, is not covered.

If you use or build on this, the licence requires this line to travel with it:

    Required Notice: Copyright 2026 jrwebersoft (https://github.com/jrwebersoft)

Worth knowing: a noncommercial restriction means this is not "open source" by
the OSI definition, so GitHub shows the licence as *Other*, and it will not be
accepted into Linux distributions or most package managers.

## Credits

Data from TMDb and TheTVDB. This product uses the TMDb API but is not endorsed
or certified by TMDb.

Inspired by MediaScout 3.x, a Windows renamer unmaintained for years. No code
from it is used here.
