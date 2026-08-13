namespace PyreMedia.Core.Media;

/// <summary>What would happen to one file.</summary>
public sealed class RemuxFilePlan
{
    public required MediaInfo Info { get; init; }
    public required List<MediaStream> Keep { get; init; }
    public required List<MediaStream> Drop { get; init; }

    /// <summary>Rewrite the container title to match the current filename.</summary>
    public bool SetTitleFromFileName { get; set; } = true;

    /// <summary>Absolute stream index a player should pick by default, per kind.</summary>
    public int? DefaultAudioIndex { get; set; }
    public int? DefaultSubtitleIndex { get; set; }

    /// <summary>
    /// Which subtitle tracks come out marked forced, by absolute stream index.
    ///
    /// Null means "whatever the file already says", which is what every caller
    /// did before this existed. A set - even an empty one - is a decision, and
    /// overrides the file.
    /// <para>
    /// Worth being able to override because the flag is often simply wrong.
    /// A full subtitle track marked forced turns permanent subtitles on for a
    /// film in your own language; a genuine forced track left unmarked means
    /// the one line of alien dialogue goes untranslated. Neither is visible
    /// until you are watching it.
    /// </para>
    /// </summary>
    public HashSet<int>? ForcedSubtitles { get; set; }

    /// <summary>Whether one subtitle stream should be written as forced.</summary>
    public bool IsForced(MediaStream stream) =>
        ForcedSubtitles?.Contains(stream.Index) ?? stream.IsForced;

    /// <summary>
    /// Which video track a player should pick when a file carries more than one
    /// - a Dolby Vision version alongside an HDR10 one, say. Null when there's
    /// only one and the question doesn't arise.
    /// </summary>
    public int? DefaultVideoIndex { get; set; }

    /// <summary>True when the video is 3D MVC, which ffmpeg cannot remux safely.</summary>
    public bool HasMvc => Info.Video.Any(v => v.IsMvc);

    /// <summary>Dolby Vision of any profile.</summary>
    public bool HasDolbyVision => Info.Video.Any(v => v.IsDolbyVision);

    /// <summary>
    /// Dual-layer Dolby Vision (profile 7), where the enhancement layer is a
    /// separate substream - the same shape of risk as MVC. Measured on a real
    /// profile-5 file, ffmpeg's MP4 muxer dropped the DOVI configuration record
    /// outright, so this is not a theoretical concern.
    /// </summary>
    public bool HasDualLayerDv => Info.Video.Any(v => v.IsDualLayerDv);

    /// <summary>
    /// Profile 5 has no HDR10 fallback: lose the DV metadata and it doesn't
    /// merely look flat, it plays with the wrong colours entirely.
    /// </summary>
    public bool HasNonFallbackDv =>
        Info.Video.Any(v => v.IsDolbyVision && v.DvProfile == 5);

    public string? DvLabel => Info.Video.FirstOrDefault(v => v.IsDolbyVision)?.DvLabel;

    /// <summary>
    /// Set when nothing being kept is audibly in your language, which is worth
    /// saying even though the file is perfectly valid.
    ///
    /// The "would remove every audio track" guard only catches the case where
    /// the rules empty a file. It says nothing when the rules keep a track that
    /// happens to be Spanish, or an untagged track that might be anything - and
    /// those are exactly the files you don't want to discover mid-film.
    /// </summary>
    public string? LanguageWarning { get; set; }

    public bool HasLanguageWarning => LanguageWarning is not null;

    /// <summary>
    /// The embedded title, when it doesn't already match the filename - a sign
    /// the file was renamed but still announces its old scene name to players.
    /// </summary>
    public string? StaleTitle { get; set; }

    /// <summary>
    /// Things the filename claims that the file doesn't back up - Dolby Vision
    /// or 3D that the signalling has gone missing from. Usually the mark of an
    /// earlier remux that copied the streams and dropped the flags, and usually
    /// fixable by remuxing again through mkvmerge.
    /// </summary>
    public List<SignalMismatch> Mismatches { get; init; } = [];

    public bool HasMismatch => Mismatches.Count > 0;

    /// <summary>The mismatches worth acting on here, as one line.</summary>
    public string? MismatchSummary => Mismatches.Count == 0
        ? null
        : string.Join("  ", Mismatches.Select(m => $"{m.What}: {m.Detail}"));

    public string FileName => Path.GetFileName(Info.Path);
    public bool HasWork => Drop.Count > 0;

    /// <summary>
    /// Rough saving. Track sizes aren't in the probe, so this is estimated from
    /// duration x a nominal bitrate per codec - indicative, not exact.
    /// </summary>
    public long EstimatedSavingBytes
    {
        get
        {
            long total = 0;
            foreach (var s in Drop)
            {
                // Use the real bitrate when ffprobe reported one; the per-codec
                // guesses below are only a fallback.
                var bps = s.BitRate > 0 ? s.BitRate : s.Kind switch
                {
                    StreamKind.Audio => s.Codec.Contains("truehd", StringComparison.OrdinalIgnoreCase) ? 3_000_000L
                                      : s.Codec.Contains("dts", StringComparison.OrdinalIgnoreCase) ? 1_500_000L
                                      : s.Channels >= 6 ? 640_000L : 192_000L,
                    StreamKind.Subtitle => 2_000L,
                    _ => 0L
                };
                total += (long)(Info.DurationSeconds * bps / 8);
            }
            return total;
        }
    }
}

/// <summary>A set of files that share an identical track layout.</summary>
public sealed class TrackLayoutGroup
{
    public required string Signature { get; init; }
    public required List<RemuxFilePlan> Files { get; init; }

    /// <summary>Representative layout - every file in the group matches it.</summary>
    public MediaInfo Sample => Files[0].Info;

    public int FileCount => Files.Count;
    public int FilesWithWork => Files.Count(f => f.HasWork);
    public long EstimatedSavingBytes => Files.Sum(f => f.EstimatedSavingBytes);
}

public sealed class RemuxPlan
{
    public List<TrackLayoutGroup> Groups { get; init; } = [];
    public List<string> Skipped { get; init; } = [];

    public int TotalFiles => Groups.Sum(g => g.FileCount);
    public int FilesWithWork => Groups.Sum(g => g.FilesWithWork);
    public long EstimatedSavingBytes => Groups.Sum(g => g.EstimatedSavingBytes);
    public bool HasWork => FilesWithWork > 0;
}

public sealed class RemuxPlanner(PyreMediaSettings settings)
{
    /// <summary>
    /// Whether the audio being kept leaves you without your own language, and
    /// why. Null when at least one kept track is in it.
    /// </summary>
    private static string? DescribeLanguageConcern(List<MediaStream> keep, string preferred)
    {
        var audio = keep.Where(s => s.Kind == StreamKind.Audio).ToList();
        if (audio.Count == 0) return null;   // handled by the no-audio guard

        if (audio.Any(a => Models.LanguageCatalog.Same(a.Language, preferred)))
            return null;

        var untagged = audio.Where(a => string.IsNullOrWhiteSpace(a.Language) || a.Language == "und").ToList();

        // Everything untagged: it may well be your language, but nothing in the
        // file says so, so neither can we.
        if (untagged.Count == audio.Count)
        {
            return audio.Count == 1
                ? "Audio has no language tag - it may not be "
                  + $"{Models.LanguageCatalog.NameOf(preferred)}."
                : $"None of the {audio.Count} audio tracks carry a language tag - "
                  + $"they may not be {Models.LanguageCatalog.NameOf(preferred)}.";
        }

        var named = audio
            .Where(a => !string.IsNullOrWhiteSpace(a.Language) && a.Language != "und")
            .Select(a => Models.LanguageCatalog.NameOf(a.Language))
            .Distinct()
            .ToList();

        var list = named.Count switch
        {
            0 => "another language",
            1 => named[0],
            2 => $"{named[0]} and {named[1]}",
            _ => string.Join(", ", named.Take(named.Count - 1)) + $" and {named[^1]}"
        };

        return $"No {Models.LanguageCatalog.NameOf(preferred)} audio - this file is {list}"
               + (untagged.Count > 0 ? ", plus untagged tracks." : ".");
    }

    /// <summary>
    /// Decide what to keep in each file, then group files by identical layout so
    /// the UI can show one row per layout rather than one per file.
    /// </summary>
    /// <param name="rpuByFile">
    /// Whether Dolby Vision RPU data was found in each file's picture, where it
    /// was looked for. Lets a file that has the data but no longer declares it
    /// be told apart from one that never had it.
    /// </param>
    public RemuxPlan Plan(IEnumerable<MediaInfo> files, IReadOnlyDictionary<string, bool>? rpuByFile = null)
    {
        var plan = new RemuxPlan();

        var keepAudio = Parse(settings.KeepAudioLanguages);
        var keepSubs = Parse(settings.KeepSubtitleLanguages);
        var preferred = settings.PreferredLanguage.Trim().ToLowerInvariant();

        var perFile = new List<RemuxFilePlan>();

        foreach (var info in files)
        {
            var keep = new List<MediaStream>();
            var drop = new List<MediaStream>();

            foreach (var s in info.Streams)
            {
                // Video and anything unclassified always survives.
                if (s.Kind is StreamKind.Video or StreamKind.Other) { keep.Add(s); continue; }

                var wanted = s.Kind == StreamKind.Audio ? keepAudio : keepSubs;

                // Matched through the catalogue, not by string equality: a track
                // tagged "fra" and a rule saying "fre" are the same French, and
                // comparing directly would drop it as unwanted.
                var matches = wanted.Count == 0
                              || Models.LanguageCatalog.WantedBy(wanted, s.Language)
                              || (settings.KeepUndeterminedLanguage && s.Language == "und");

                // A forced subtitle carries dialogue the film expects you to read
                // - alien speech, on-screen text - so dropping it on a language
                // rule breaks playback. But only in YOUR language: a forced
                // Spanish track exists for Spanish speakers and is just as
                // unwanted as Spanish audio.
                if (s.Kind == StreamKind.Subtitle
                    && s.IsForced
                    && settings.KeepForcedSubtitles
                    && (Models.LanguageCatalog.Same(s.Language, preferred)
                        || (settings.KeepUndeterminedLanguage && s.Language == "und")))
                {
                    matches = true;
                }

                (matches ? keep : drop).Add(s);
            }

            // Never strip a file down to no audio - better to leave it untouched.
            if (!keep.Any(s => s.Kind == StreamKind.Audio) && info.Audio.Any())
            {
                // Name the languages it does have - "would remove every audio
                // track" says what was refused, not how to fix it.
                var have = string.Join(", ", info.Audio
                    .Select(a => string.IsNullOrWhiteSpace(a.Language) ? "unknown" : a.Language)
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                plan.Skipped.Add(
                    $"{Path.GetFileName(info.Path)} - left as is: none of its audio matches the "
                    + $"language you're keeping. This file has {have}.");
                continue;
            }

            perFile.Add(new RemuxFilePlan
            {
                Info = info,
                // What the name claims the file doesn't carry. The RPU check
                // needs ffprobe and is done by the caller when it can; without
                // it only the filename has anything to say.
                Mismatches = SignalCheck.Compare(Path.GetFileName(info.Path), info, rpuByFile?.GetValueOrDefault(info.Path)),
                Keep = keep,
                Drop = drop,
                LanguageWarning = DescribeLanguageConcern(keep, preferred)
            });
        }

        foreach (var group in perFile.GroupBy(f => f.Info.LayoutSignature))
        {
            plan.Groups.Add(new TrackLayoutGroup
            {
                Signature = group.Key,
                Files = [.. group]
            });
        }

        // Groups needing work first, then largest.
        plan.Groups.Sort((a, b) =>
        {
            var byWork = b.FilesWithWork.CompareTo(a.FilesWithWork);
            return byWork != 0 ? byWork : b.FileCount.CompareTo(a.FileCount);
        });

        return plan;
    }

    private static HashSet<string> Parse(string csv) =>
        [.. csv.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())];
}
