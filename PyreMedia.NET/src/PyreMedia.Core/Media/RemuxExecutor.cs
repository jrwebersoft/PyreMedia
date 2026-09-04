using System.Diagnostics;
using PyreMedia.Core.History;

namespace PyreMedia.Core.Media;

public sealed class RemuxProgress
{
    public required string FileName { get; init; }
    public required int Index { get; init; }
    public required int Total { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// How far through this one file, 0-100, or null when the engine gives no
    /// signal. Without it a single large file shows no movement for minutes.
    /// </summary>
    public int? Percent { get; init; }
}

public sealed class RemuxResult
{
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long BytesReclaimed { get; set; }
    public List<string> Errors { get; init; } = [];

    /// <summary>Set when a clean stop ended the run before the list was done.</summary>
    public bool Stopped { get; set; }
}

/// <summary>
/// Strips unwanted tracks by remuxing with ffmpeg.
///
/// Stream copy only (-c copy): no re-encoding, so it's disk-speed and lossless.
///
/// The original is never touched until the new file has been probed and checked,
/// because unlike a rename this can't be undone by moving a file back - the
/// dropped tracks are gone.
/// </summary>
public sealed class RemuxExecutor(PyreMediaSettings settings, RenameHistory? history = null)
{
    private readonly RenameHistory _history = history ?? new RenameHistory();
    private readonly MediaProbe _probe = new(settings.FfprobePath);

    /// <param name="ct">
    /// Abort now. Reaches the running ffmpeg process and kills it; the temp
    /// file is deleted and the original is left exactly as it was.
    /// </param>
    /// <param name="stopAfterCurrent">
    /// Stop cleanly. Checked only between files, so the one in flight is
    /// allowed to finish, verify and be swapped in before the run ends.
    /// </param>
    public async Task<RemuxResult> ExecuteAsync(
        IReadOnlyList<RemuxFilePlan> plans,
        IProgress<RemuxProgress>? progress = null,
        CancellationToken ct = default,
        CancellationToken stopAfterCurrent = default)
    {
        var result = new RemuxResult();
        var batchId = Guid.NewGuid().ToString("N")[..8];
        var todo = plans.Where(p => p.HasWork).ToList();

        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Not an exception: a clean stop returns the counts gathered so far.
            if (stopAfterCurrent.IsCancellationRequested)
            {
                result.Stopped = true;
                break;
            }

            var plan = todo[i];
            var source = plan.Info.Path;

            progress?.Report(new RemuxProgress
            {
                FileName = plan.FileName,
                Index = i + 1,
                Total = todo.Count,
                Message = "remuxing"
            });

            if (!File.Exists(source))
            {
                result.Skipped++;
                result.Errors.Add($"{plan.FileName}: no longer exists");
                continue;
            }

            // The remux is written beside the original before replacing it, so the
            // volume has to hold both at once. Worth knowing now: on a 25 GB file
            // the alternative is failing at 90% with the hour already spent.
            var noRoom = Storage.VolumeInfo.WhyNoRoomFor(
                Path.GetDirectoryName(source)!, new FileInfo(source).Length);

            if (noRoom is not null)
            {
                result.Skipped++;
                result.Errors.Add($"{plan.FileName}: {noRoom}");
                continue;
            }

            // Output container. Matroska holds everything the source might
            // carry; MP4 does not, so remuxing into it can lose PGS subtitles
            // or Dolby Vision signalling even on a straight copy.
            var outExt = settings.RemuxToMkv ? ".mkv" : Path.GetExtension(source);
            var finalPath = Path.ChangeExtension(source, outExt);

            // Temp file beside the original: same volume, so the final move is
            // atomic rather than a copy across devices.
            var temp = Path.Combine(
                Path.GetDirectoryName(source)!,
                Path.GetFileNameWithoutExtension(source) + ".msremux.tmp" + outExt);

            // Whether the original has already been given up for this file. Once
            // it has, the temp file is the only copy in existence and the
            // failure paths must not tidy it away.
            var retired = false;

            try
            {
                if (File.Exists(temp)) File.Delete(temp);

                var sizeBefore = new FileInfo(source).Length;

                // Report every whole percent, so a single large file shows real
                // movement rather than sitting still until it completes.
                var lastPct = -1;
                var index = i + 1;

                // Expected output size, for when ffmpeg can't report a timestamp.
                // Estimated saving is approximate, so this is too - but it moves
                // steadily and finishes near 100, which is the point.
                var expected = Math.Max(1, sizeBefore - plan.EstimatedSavingBytes);

                var (code, stderr) = await RunFfmpegAsync(
                    BuildArgs(source, temp, plan), ct,
                    plan.Info.DurationSeconds,
                    pct =>
                    {
                        // Never let the bar go backwards: the two signals can
                        // disagree slightly, and a bar that retreats reads as a
                        // fault rather than an estimate.
                        if (pct <= lastPct) return;
                        lastPct = pct;

                        progress?.Report(new RemuxProgress
                        {
                            FileName = plan.FileName,
                            Index = index,
                            Total = todo.Count,
                            Message = $"remuxing (ffmpeg) {pct}%",
                            Percent = pct
                        });
                    },
                    expected).ConfigureAwait(false);

                if (code != 0 || !File.Exists(temp))
                {
                    result.Failed++;
                    result.Errors.Add($"{plan.FileName}: ffmpeg exited {code}. {Tail(stderr)}");
                    TryDelete(temp);
                    continue;
                }

                progress?.Report(new RemuxProgress
                {
                    FileName = plan.FileName, Index = i + 1, Total = todo.Count, Message = "verifying"
                });

                var problem = await VerifyAsync(plan, temp, ct).ConfigureAwait(false);
                if (problem is not null)
                {
                    result.Failed++;
                    result.Errors.Add($"{plan.FileName}: {problem} - original left untouched");
                    TryDelete(temp);
                    continue;
                }

                var sizeAfter = new FileInfo(temp).Length;

                // Nothing is given up until the place the new file has to go is
                // known to be free. File.Move throws when the destination
                // exists, and that throw used to land in the catch below - which
                // deleted the verified output, seconds after the original had
                // already been archived or recycled. Both copies gone, for a
                // collision that could have been seen a line earlier.
                if (!string.Equals(finalPath, source, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(finalPath))
                {
                    result.Failed++;
                    result.Errors.Add(
                        $"{plan.FileName}: {Path.GetFileName(finalPath)} already exists - "
                        + "original left untouched");

                    TryDelete(temp);
                    continue;
                }

                // Only now is the original expendable. Archived by preference so
                // it can be restored later; recycled only if archiving is off.
                var archived = RetireOriginal(source, batchId);

                // Past this point the original is gone, so the remuxed file is
                // the only copy and must survive any failure below.
                retired = true;

                // Changing container changes the filename, so the old one must
                // be gone before the new lands beside it - otherwise the library
                // ends up holding both.
                if (!string.Equals(finalPath, source, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(source))
                {
                    TryDelete(source);
                }

                File.Move(temp, finalPath);

                _history.Add(HistoryAction.Rename, source, finalPath,
                    $"remux: dropped {plan.Drop.Count} track(s)"
                    + (archived is not null ? $", original archived" : ""),
                    null, batchId);

                result.Succeeded++;
                result.BytesReclaimed += Math.Max(0, sizeBefore - sizeAfter);

                // Kodi trusts the sidecar over re-probing, so a stale track list
                // would advertise audio that no longer exists.
                if (settings.UpdateNfoAfterRemux)
                {
                    try
                    {
                        var wanted = Metadata.NfoWriter.PathFor(finalPath);
                        var old = Metadata.NfoWriter.PathFor(source);

                        if (!File.Exists(wanted) && File.Exists(old)
                            && !string.Equals(old, wanted, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Move(old, wanted);

                            // Recorded like any other move. Without this a revert
                            // put the video back under its old name and left the
                            // sidecar under the new one, orphaning the metadata.
                            _history.Add(HistoryAction.Rename, old, wanted,
                                         "remux: sidecar followed the container change", null, batchId);
                        }

                        if (File.Exists(wanted))
                        {
                            var fresh = await _probe.ProbeAsync(finalPath, ct).ConfigureAwait(false);
                            if (fresh is not null)
                                Metadata.NfoWriter.UpdateStreamDetails(wanted, fresh);
                        }
                    }
                    catch
                    {
                        // Cosmetic - the remux itself succeeded.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Only when the original is still there to fall back on.
                if (!retired) TryDelete(temp);
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{plan.FileName}: {ex.Message}");

                if (retired)
                {
                    // The original has gone and this is the only copy left, so
                    // it stays where it is. Analyse finds it as an unfinished
                    // remux next time and offers to put it in place.
                    result.Errors.Add(
                        $"{plan.FileName}: the remuxed file was kept as {Path.GetFileName(temp)} - "
                        + "the original has already been archived. Press Analyse to finish it.");
                }
                else
                {
                    TryDelete(temp);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Absolute stream indices are used (-map 0:N) rather than per-kind ones, so
    /// there's no ambiguity about which track is being kept.
    /// </summary>
    private static List<string> BuildArgs(string source, string temp, RemuxFilePlan plan)
    {
        // -progress writes machine-readable position to stdout; -nostats stops
        // the human-readable version cluttering stderr, which is where errors
        // need to stay readable.
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats",
            "-progress", "pipe:1",
            "-y", "-i", source
        };

        foreach (var s in plan.Keep.OrderBy(s => s.Index))
        {
            args.Add("-map");
            args.Add($"0:{s.Index}");
        }

        // Attachments (embedded subtitle fonts) aren't in the stream list we
        // filter, and losing them wrecks styled subtitles. '?' makes it optional.
        args.Add("-map");
        args.Add("0:t?");

        args.AddRange(["-c", "copy", "-map_metadata", "0", "-map_chapters", "0"]);

        // Players and Kodi show the container title in preference to the
        // filename, so a renamed file can still display its old scene name
        // forever. Set it to match what the file is now called.
        if (plan.SetTitleFromFileName)
        {
            args.Add("-metadata");
            args.Add($"title={Path.GetFileNameWithoutExtension(source)}");
        }

        // ---- Dispositions ----
        // Stream copy preserves these, but dropping tracks renumbers what's left,
        // and the track that WAS default may be gone. Set them explicitly.

        // Video only needs a default when more than one survives - otherwise the
        // single track is the answer by definition.
        var keptVideo = plan.Keep
            .Where(s => s.Kind == StreamKind.Video && !s.IsCoverArt)
            .OrderBy(s => s.Index)
            .ToList();

        if (keptVideo.Count > 1)
        {
            var defaultVideo = plan.DefaultVideoIndex is { } wantV
                ? keptVideo.FindIndex(s => s.Index == wantV)
                : keptVideo.FindIndex(s => s.IsDefault);

            if (defaultVideo < 0) defaultVideo = 0;

            for (var i = 0; i < keptVideo.Count; i++)
            {
                args.Add($"-disposition:v:{i}");
                args.Add(i == defaultVideo ? "default" : "0");
            }
        }

        var keptAudio = plan.Keep.Where(s => s.Kind == StreamKind.Audio).OrderBy(s => s.Index).ToList();

        // An explicit choice wins; otherwise keep whatever was already default,
        // falling back to the first surviving track.
        var defaultAudio = plan.DefaultAudioIndex is { } wanted
            ? keptAudio.FindIndex(s => s.Index == wanted)
            : keptAudio.FindIndex(s => s.IsDefault);

        if (defaultAudio < 0 && keptAudio.Count > 0) defaultAudio = 0;

        for (var i = 0; i < keptAudio.Count; i++)
        {
            args.Add($"-disposition:a:{i}");
            args.Add(i == defaultAudio ? "default" : "0");
        }

        // A forced subtitle carries dialogue you're meant to read, so it has to
        // be default too or the player won't turn it on and the track may as well
        // not be there. Ordinary subtitle tracks are explicitly NOT default -
        // otherwise subs come on for everything.
        var keptSubs = plan.Keep.Where(s => s.Kind == StreamKind.Subtitle).OrderBy(s => s.Index).ToList();

        for (var i = 0; i < keptSubs.Count; i++)
        {
            var s = keptSubs[i];
            var chosen = plan.DefaultSubtitleIndex is { } wantSub && s.Index == wantSub;

            // The plan's answer, not the file's - the flag can be wrong in the
            // source and is editable before a remux.
            var forced = plan.IsForced(s);

            args.Add($"-disposition:s:{i}");
            args.Add(forced ? "default+forced" : chosen ? "default" : "0");
        }

        args.Add(temp);
        return args;
    }

    /// <summary>The suffix a half-finished remux leaves behind.</summary>
    public const string TempMarker = ".msremux.tmp";

    /// <summary>Original path a temp file belongs to, or null if it isn't one.</summary>
    public static string? OriginalForTemp(string temp)
    {
        var name = Path.GetFileName(temp);
        var i = name.IndexOf(TempMarker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        // "Film.msremux.tmp.mkv" -> "Film" + ".mkv"
        var stem = name[..i];
        var ext = Path.GetExtension(name);
        return Path.Combine(Path.GetDirectoryName(temp)!, stem + ext);
    }

    /// <summary>What an interrupted run left on disk.</summary>
    public sealed class Orphan
    {
        public required string TempPath { get; init; }
        public required string OriginalPath { get; init; }
        public required bool OriginalExists { get; init; }

        /// <summary>Complete and safe to adopt: same duration, still has video and audio.</summary>
        public required bool LooksComplete { get; init; }

        /// <summary>
        /// It reads as a video, and the file it came from is no longer there.
        ///
        /// This is the one state where the leftover is the only copy of the
        /// content in existence - the run died between retiring the original
        /// and moving this into place. It cannot be checked against anything,
        /// which is not the same as being broken, and it used to be listed as
        /// UNUSABLE and deleted outright along with the genuinely broken ones.
        /// </summary>
        public bool OnlyCopy { get; init; }

        public required string Reason { get; init; }
        public long TempBytes { get; init; }
        public long OriginalBytes { get; init; }
    }

    /// <summary>
    /// Find leftovers from a run that died before it could verify and swap.
    ///
    /// A crash, a power cut or the app being killed can leave the encoder to
    /// finish on its own - the output is often complete and perfectly good, but
    /// nothing was alive to check it and put it in place.
    /// </summary>
    public async Task<List<Orphan>> FindOrphansAsync(
        IEnumerable<string> folders, CancellationToken ct = default)
    {
        var found = new List<Orphan>();

        foreach (var dir in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var temp in Directory.GetFiles(dir, "*" + TempMarker + ".*"))
            {
                ct.ThrowIfCancellationRequested();

                var original = OriginalForTemp(temp);
                if (original is null) continue;

                var origExists = File.Exists(original);
                var tempInfo = await _probe.ProbeAsync(temp, ct).ConfigureAwait(false);

                string reason;
                var complete = false;
                var onlyCopy = false;

                if (tempInfo is null)
                {
                    reason = "unreadable - the run was cut short mid-write";
                }
                else if (!origExists)
                {
                    onlyCopy = true;

                    reason = tempInfo.Video.Any()
                        ? $"the original is gone, so this is the only copy - {tempInfo.Streams.Count} "
                          + $"track(s), {tempInfo.DurationSeconds:F0}s. Nothing to check it against."
                        : "the original is gone and this has no video track";

                    // No video and nothing to compare against is the one case
                    // here that is genuinely not worth keeping.
                    if (!tempInfo.Video.Any()) onlyCopy = false;
                }
                else
                {
                    var origInfo = await _probe.ProbeAsync(original, ct).ConfigureAwait(false);

                    if (origInfo is null)
                        reason = "the original could not be read for comparison";
                    else if (!tempInfo.Video.Any())
                        reason = "no video track";
                    else if (origInfo.Audio.Any() && !tempInfo.Audio.Any())
                        reason = "no audio track";
                    else if (origInfo.DurationSeconds > 0 && tempInfo.DurationSeconds > 0
                             && Math.Abs(origInfo.DurationSeconds - tempInfo.DurationSeconds) > 2.0)
                    {
                        reason = $"runs {tempInfo.DurationSeconds:F0}s against the original's "
                                 + $"{origInfo.DurationSeconds:F0}s - truncated";
                    }
                    else if (origInfo.Video.Any(v => v.IsDolbyVision)
                             && !tempInfo.Video.Any(v => v.IsDolbyVision))
                    {
                        // Full length and the right tracks, but the picture is
                        // wrong. Profile 5 in particular is not HDR10-compatible,
                        // so without DV it plays with the wrong colours entirely.
                        var dv = origInfo.Video.First(v => v.IsDolbyVision);
                        reason = $"lost {dv.DvLabel} - complete otherwise, but the picture would be wrong";
                    }
                    else if (origInfo.Video.Any(v => v.IsMvc) && !tempInfo.Video.Any(v => v.IsMvc))
                    {
                        reason = "lost the second 3D view (MVC) - only one eye survived";
                    }
                    else
                    {
                        complete = true;
                        reason = $"complete - same length, {tempInfo.Streams.Count} track(s) "
                                 + $"against the original's {origInfo.Streams.Count}";
                    }
                }

                found.Add(new Orphan
                {
                    TempPath = temp,
                    OriginalPath = original,
                    OriginalExists = origExists,
                    LooksComplete = complete,
                    OnlyCopy = onlyCopy,
                    Reason = reason,
                    TempBytes = SafeLength(temp),
                    OriginalBytes = origExists ? SafeLength(original) : 0
                });
            }
        }

        return found;

        static long SafeLength(string p)
        {
            try { return new FileInfo(p).Length; } catch { return 0; }
        }
    }

    /// <summary>
    /// Finish what the interrupted run started: retire the original and move the
    /// recovered file into its place, recording it in history like any other
    /// remux so it can be reverted.
    /// </summary>
    public bool AdoptOrphan(Orphan orphan)
    {
        if (!orphan.LooksComplete || !orphan.OriginalExists) return false;

        var batchId = Guid.NewGuid().ToString("N")[..8];
        var archived = RetireOriginal(orphan.OriginalPath, batchId);

        File.Move(orphan.TempPath, orphan.OriginalPath);

        _history.Add(HistoryAction.Rename, orphan.OriginalPath, orphan.OriginalPath,
            "remux recovered from an interrupted run"
            + (archived is not null ? ", original archived" : ""),
            null, batchId);

        return true;
    }

    /// <summary>Check the output is sane. Returns a reason, or null when it's good.</summary>
    private async Task<string?> VerifyAsync(RemuxFilePlan plan, string temp, CancellationToken ct)
    {
        var info = await _probe.ProbeAsync(temp, ct).ConfigureAwait(false);
        if (info is null) return "output could not be probed";

        // Cover art is kept as a video stream but excluded from Video, so both
        // sides of this comparison must exclude it or a file with an embedded
        // poster always looks like it lost a video track.
        var expectedVideo = plan.Keep.Count(s => s.Kind == StreamKind.Video && !s.IsCoverArt);
        var expectedAudio = plan.Keep.Count(s => s.Kind == StreamKind.Audio);

        if (info.Video.Count() < expectedVideo)
            return $"output has {info.Video.Count()} video track(s), expected {expectedVideo}";

        if (info.Audio.Count() < expectedAudio)
            return $"output has {info.Audio.Count()} audio track(s), expected {expectedAudio}";

        // A truncated remux is the failure that matters most - it looks fine
        // until you play the end.
        //
        // Tolerance is proportional, not a flat two seconds. Container duration
        // is the longest stream, and subtitle tracks routinely run past the
        // video - dropping them legitimately shortens it. A real 24-minute
        // episode failed this check over 2.9s while being perfectly intact.
        // Truncation is measured in minutes, so 1% still catches it easily.
        if (DurationCheck.Failed(plan.Info, info, plan.Keep) is { } truncated) return truncated;

        if (OutputCheck.TooSmall(temp, plan.Info, plan.HasMvc) is { } small) return small;

        // Dolby Vision is the quiet failure: the picture still plays, in HDR10,
        // and nothing looks broken until you notice DV never engages. The RPU
        // lives in the bitstream and survives -c copy, but the container-level
        // configuration record is what a player looks for, and a muxer that
        // doesn't carry it across drops DV without a word.
        if (OutputCheck.DolbyVisionLost(plan.Info, info) is { } dv) return dv;

        return null;
    }

    /// <summary>
    /// Move the pre-remux file into the archive folder, or recycle it when
    /// archiving is off. Returns the archived path, or null if it was recycled.
    /// </summary>
    private string? RetireOriginal(string path, string batchId)
    {
        if (settings.ArchiveOriginals)
        {
            var dir = Path.GetDirectoryName(path)!;

            string archiveDir;
            if (!string.IsNullOrWhiteSpace(settings.ArchiveRootPath))
            {
                // Keep the containing folder's name under the archive root, so
                // originals from different shows don't pile into one directory.
                var showFolder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                archiveDir = string.IsNullOrEmpty(showFolder)
                    ? settings.ArchiveRootPath
                    : Path.Combine(settings.ArchiveRootPath, showFolder);
            }
            else
            {
                archiveDir = Path.Combine(dir, settings.ArchiveFolderName);
            }

            Directory.CreateDirectory(archiveDir);

            // Hidden so it stays out of the way of media scanners like Kodi.
            try
            {
                var di = new DirectoryInfo(archiveDir);
                if (!di.Attributes.HasFlag(FileAttributes.Hidden))
                    di.Attributes |= FileAttributes.Hidden;
            }
            catch (Exception)
            {
                // Cosmetic only.
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var target = Path.Combine(archiveDir, stem + settings.ArchiveSuffix + ext);

            // Don't clobber a previous archive of the same episode.
            var n = 2;
            while (File.Exists(target))
                target = Path.Combine(archiveDir, $"{stem}{settings.ArchiveSuffix}.{n++}{ext}");

            // An archive root on another drive makes this a copy, so it needs room
            // there. Running out part-way through a 25 GB original used to leave the
            // half-written file sitting in the archive looking like a real one.
            if (!Storage.VolumeInfo.SameVolume(path, target))
            {
                var noRoom = Storage.VolumeInfo.WhyNoRoomFor(archiveDir, new FileInfo(path).Length);
                if (noRoom is not null)
                    throw new IOException($"Can't archive the original - {noRoom}");
            }

            try
            {
                // File.Move copies across volumes by itself and tidies up after a
                // failed copy, which a manual Copy-then-Delete does not.
                File.Move(path, target);
            }
            catch
            {
                try { if (File.Exists(target) && File.Exists(path)) File.Delete(target); } catch { }
                throw;
            }

            _history.Add(HistoryAction.Move, path, target, "remux: original archived", null, batchId);
            return target;
        }

        if (settings.DeleteToRecycleBin)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            File.Delete(path);
        }

        _history.Add(HistoryAction.Delete, path, string.Empty, "remux: original removed", null, batchId);
        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Tail(string s, int max = 300)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : "..." + s[^max..];
    }

    /// <param name="durationSeconds">
    /// Length of the source, for turning ffmpeg's reported position into a
    /// percentage. Zero means no percentage is reported.
    /// </param>
    /// <param name="expectedBytes">
    /// Roughly how big the output will be, used when ffmpeg cannot report a
    /// timestamp. Mapping attachments (-map 0:t?) makes out_time report "N/A" -
    /// attachment streams carry no timestamps - and dropping the mapping isn't
    /// an option because it would lose subtitle fonts. Bytes written is the
    /// signal that survives.
    /// </param>
    private async Task<(int Code, string StdErr)> RunFfmpegAsync(
        List<string> args, CancellationToken ct,
        double durationSeconds = 0, Action<int>? onPercent = null,
        long expectedBytes = 0)
    {
        var psi = new ProcessStartInfo
        {
            FileName = settings.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var errTask = proc.StandardError.ReadToEndAsync(ct);

        // "-progress pipe:1" emits key=value lines roughly twice a second.
        // out_time_us against the source duration gives real position - reading
        // stdout to the end instead meant a 25 GB file showed nothing at all
        // until it finished.
        var outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (onPercent is null) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq];
                var value = line[(eq + 1)..];

                // Position first, bytes as the fallback.
                //
                // Both time fields are MICROSECONDS - "out_time_ms" is a
                // long-standing misnomer reporting the same value as
                // out_time_us, verified against out_time on a real file.
                // Dividing it by 1000 as the name suggests would make a
                // 24-minute episode look like 65 hours.
                //
                // Either can read "N/A": mapping attachments suppresses
                // out_time entirely, which is why total_size is also handled.
                // InvariantCulture because this is machine output, not something
                // the user typed. On a locale using comma as the decimal
                // separator the default parse can reject it outright, and
                // progress would silently never move.
                var inv = System.Globalization.CultureInfo.InvariantCulture;

                if (key is "out_time_us" or "out_time_ms"
                    && durationSeconds > 0
                    && double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out var micros)
                    && micros >= 0)
                {
                    onPercent(Math.Clamp((int)(micros / 1_000_000d / durationSeconds * 100), 0, 100));
                }
                else if (key == "total_size"
                         && expectedBytes > 0
                         && double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out var written)
                         && written >= 0)
                {
                    onPercent(Math.Clamp((int)(written / expectedBytes * 100), 0, 100));
                }
            }
        }, ct);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            await outTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return (proc.ExitCode, await errTask.ConfigureAwait(false));
    }
}
