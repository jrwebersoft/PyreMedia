# PyreMedia

A Windows media library organiser. It renames and files TV and film, repairs the
metadata around them, and strips audio and subtitle tracks you don't want —
without ever writing to disk until you have seen the change listed and approved
it, and without ever making a change you cannot undo.

Windows · .NET 10 · WPF

---

## Why this exists

Media libraries rot. Files arrive named by whoever packaged them, a season
arrives as eight separate folders, metadata sidecars go stale, and remuxes
quietly drop the Dolby Vision layer that made the file worth keeping. Most tools
that fix this either do it silently — you find out what happened afterwards — or
demand the library already be tidy before they will help.

PyreMedia is built the other way round: **it shows you every change before it
makes one, and records every change it makes so you can take it back.**

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

A season per folder is how most TV arrives. Eight folders of the same series are
recognised as **one entry**, matched once, and gathered into a single show
folder — instead of being matched eight times by hand.

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

Version 1.1.0. TV and film are complete and in daily use. Music library
support is being built and is not in this release.

Some things are proven further than others. Renaming, conflict handling and
undo are exercised daily on a large library; remuxing is used regularly but on
a narrower range of files, and the 3D and Dolby Vision paths have been tried on
few enough files to be worth calling out. Nothing here deletes anything without
showing it first, and renames undo from History.

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
