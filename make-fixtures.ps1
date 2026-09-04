# Builds a throwaway library of dummy files covering every naming case that has
# caused a real bug. Files are a few KB, so a full scan-rename-verify cycle is
# instant and nothing valuable is at risk.
#
#   .\make-fixtures.ps1              -> H:\_MediaScout-Fixtures
#   .\make-fixtures.ps1 -Root D:\tmp -> somewhere else

param([string]$Root = 'H:\_MediaScout-Fixtures')

$ErrorActionPreference = 'Stop'

if (Test-Path $Root) {
    Get-ChildItem $Root -Recurse -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $Root | Out-Null
}

function Vid([string]$rel, [int]$mb = 2) {
    $full = Join-Path $Root $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
    $fs = [IO.File]::Create($full)
    $fs.SetLength($mb * 1MB)
    $fs.Close()
}

function Txt([string]$rel, [string]$body) {
    $full = Join-Path $Root $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
    Set-Content -LiteralPath $full -Value $body -Encoding utf8
}

# ---------------------------------------------------------------- movies

# The Tron case: companions sharing an extension, distinguished only by the
# segment between stem and dot. Contents differ so a collision is provable.
$tron = 'Tron Legacy (2010)'
Vid "$tron\$tron.mkv" 4
foreach ($a in 'poster','fanart','thumb','banner','landscape','keyart') {
    Txt "$tron\$tron-$a.jpg" $a.ToUpper()
}
foreach ($a in 'clearlogo','clearart','disc','discart') {
    Txt "$tron\$tron-$a.png" $a.ToUpper()
}
Txt "$tron\$tron.srt"      'PLAIN-SUB'
Txt "$tron\$tron.eng.srt"  'ENG-SUB'
Txt "$tron\$tron.eng.forced.srt" 'ENG-FORCED-SUB'
Txt "$tron\$tron.nfo" @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>TRON: Legacy</title>
  <year>2010</year>
  <uniqueid type="tmdb" default="true">20526</uniqueid>
  <uniqueid type="imdb">tt1104001</uniqueid>
</movie>
'@
# Folder-level art belongs to the folder, not the file - must NOT be renamed.
Txt "$tron\banner.jpg"   'FOLDER-BANNER'
Txt "$tron\clearart.png" 'FOLDER-CLEARART'
Txt "$tron\movie.nfo"    '<movie><title>TRON: Legacy</title></movie>'

# Pre-existing targets, so a conflict happens on every run. Distinct contents,
# so "keep both" vs "replace" is provable by what survives rather than assumed.
Txt "$tron\TRON - Legacy (2010).mkv"        'EXISTING-VIDEO'
Txt "$tron\TRON - Legacy (2010)-poster.jpg" 'EXISTING-POSTER'

# A second conflict in a folder whose name already ends in a year - this is what
# made a copy counter increment the year instead of appending one.
$blade = 'Blade Runner 2049 (2017)'
Vid "$blade\Blade Runner 2049.mkv"
Txt "$blade\Blade Runner 2049 (2017).mkv" 'EXISTING-BLADE'

# Numeric titles - these were read as episode numbers.
Vid '47 Ronin (2013)\47 Ronin (2013).mkv'
Vid '300 (2006)\300 (2006).mkv'
Vid '12 Monkeys (1995)\12 Monkeys (1995).mkv'
Vid '1917 (2019)\1917 (2019).mkv'
Vid '2012 (2009)\2012 (2009).mkv'

# 3D tag variants, and a loose file that needs a folder creating.
Vid '(3D-SBS) Blade Runner 2049.mkv'
Vid '(3D-HSBS) Secret of the Wings.mkv'

# Scene-named loose film with junk beside it.
Vid 'Some.Film.2019.1080p.BluRay.x264-GRP.mkv'
Txt 'grp-somefilm.nfo' '  ___  SCENE RELEASE ADVERT  ___'
Txt 'Some.Film.2019.1080p.BluRay.x264-GRP.sample.txt' 'junk'

# ------------------------------------------------------------------- tv

# Season folder with no numbering in the filenames at all.
Vid 'The Cat and the Dragon (2026)\Season 01\Uncle Wings.mkv'
Vid 'The Cat and the Dragon (2026)\Season 01\The Hatching.mkv'

# Bare numbers inside a Season folder.
Vid 'Some Show (2001)\Season 02\1.mkv'
Vid 'Some Show (2001)\Season 02\2.mkv'
Vid 'Some Show (2001)\Season 02\3.mkv'

# Ordinary and multi-episode naming, plus a sample that must not be matched.
Vid 'Firefly (2002)\Season 01\Firefly S01E01 Serenity.mkv'
Vid 'Firefly (2002)\Season 01\Firefly S01E02E03.mkv'
Vid 'Firefly (2002)\Season 01\sample.mkv' 1
Txt 'Firefly (2002)\Season 01\Firefly S01E01 Serenity.eng.srt' 'FIREFLY-ENG'
Txt 'Firefly (2002)\Season 01\Firefly S01E01 Serenity.nfo' `
    '<episodedetails><title>Serenity</title><season>1</season><episode>1</episode></episodedetails>'

# Specials.
Vid 'Firefly (2002)\Specials\Behind the Scenes.mkv'

# Apostrophe and "and" in the title - both historically awkward to search.
Vid "The Handmaid's Tale (2017)\Season 04\The Handmaid's Tale S04E03.mkv"
Vid 'Last Man Standing (2011)\Season 01\Last Man Standing S01E01.mkv'

$n = (Get-ChildItem $Root -Recurse -File).Count
Write-Host ""
Write-Host "Created $n dummy file(s) under $Root" -ForegroundColor Green
Write-Host "Add it as a folder in MediaScout and scan. Nothing here is real - "
Write-Host "delete the whole tree when you're done."
