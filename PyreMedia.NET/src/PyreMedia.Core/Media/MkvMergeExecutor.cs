using System.Diagnostics;
using System.Text.Json;
using PyreMedia.Core.History;

namespace PyreMedia.Core.Media;

/// <summary>
/// Remux via mkvmerge instead of ffmpeg.
///
/// The reason this exists is MVC. ffmpeg *parses* the video stream and only
/// understands the base view, so remuxing a Blu-ray 3D file hands back a 2D
/// copy with the second eye discarded. mkvmerge copies tracks byte-for-byte
/// without decoding, so the MVC data inside an AVC track survives.
///
/// mkvmerge numbers tracks its own way, so track IDs are resolved with
/// "mkvmerge -J" rather than assumed to match ffprobe's stream indices.
/// </summary>
public sealed class MkvMergeExecutor(PyreMediaSettings settings, RenameHistory? history = null)
{
    private readonly RenameHistory _history = history ?? new RenameHistory();
    private readonly MediaProbe _probe = new(settings.FfprobePath);

    /// <summary>
    /// Inputs mkvmerge can read. It writes Matroska and nothing else, so this
    /// only matters when the output container is MKV - but within that, it reads
    /// far more than it writes.
    ///
    /// Restricting this to .mkv sent MP4 sources to ffmpeg needlessly, which is
    /// the path that drops Dolby Vision.
    /// </summary>
    public static bool CanRead(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        return ext is ".mkv" or ".mk3d" or ".mka" or ".webm"
                   or ".mp4" or ".m4v" or ".mov"
                   or ".avi" or ".ts" or ".m2ts" or ".mts" or ".mpg" or ".mpeg" or ".vob"
                   or ".flv" or ".ogm" or ".ogv" or ".rm" or ".rmvb" or ".wmv";
    }

    /// <summary>
    /// True when mkvmerge can do this job end to end: it must be able to read
    /// the source, and the wanted output must be Matroska.
    /// </summary>
    public static bool CanHandle(string path, bool outputIsMkv)
        => CanRead(path) && (outputIsMkv || Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mk3d");


    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (code, _, _) = await RunAsync(["--version"], ct).ConfigureAwait(false);
            return code == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>One track as mkvmerge sees it.</summary>
    private sealed record MkvTrack(int Id, string Type, string Language, string CodecId, int? StereoMode);

    private async Task<List<MkvTrack>> IdentifyAsync(string file, CancellationToken ct)
    {
        var (code, stdout, _) = await RunAsync(["-J", file], ct).ConfigureAwait(false);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return [];

        var list = new List<MkvTrack>();

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks)) return [];

            foreach (var t in tracks.EnumerateArray())
            {
                var id = t.TryGetProperty("id", out var i) ? i.GetInt32() : -1;
                if (id < 0) continue;

                var props = t.TryGetProperty("properties", out var p) ? p : default;

                list.Add(new MkvTrack(
                    id,
                    t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
                    props.ValueKind == JsonValueKind.Object && props.TryGetProperty("language", out var l)
                        ? l.GetString() ?? "und" : "und",
                    props.ValueKind == JsonValueKind.Object && props.TryGetProperty("codec_id", out var c)
                        ? c.GetString() ?? "" : "",
                    props.ValueKind == JsonValueKind.Object
                        && props.TryGetProperty("stereo_mode", out var sm)
                        && sm.ValueKind == JsonValueKind.Number
                            ? sm.GetInt32() : null));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return list;
    }

    /// <param name="ct">
    /// Abort now. Kills the running mkvmerge; the temp file is deleted and the
    /// original is left exactly as it was.
    /// </param>
    /// <param name="stopAfterCurrent">
    /// Stop cleanly. Checked only between files, so the one in flight finishes,
    /// verifies and is swapped in first.
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
                FileName = plan.FileName, Index = i + 1, Total = todo.Count, Message = "remuxing (mkvmerge)"
            });

            if (!File.Exists(source))
            {
                result.Skipped++;
                result.Errors.Add($"{plan.FileName}: no longer exists");
                continue;
            }

            // Written beside the original before replacing it, so the volume has
            // to hold both at once. Better to say so now than to fail at 90%.
            var noRoom = Storage.VolumeInfo.WhyNoRoomFor(
                Path.GetDirectoryName(source)!, new FileInfo(source).Length);

            if (noRoom is not null)
            {
                result.Skipped++;
                result.Errors.Add($"{plan.FileName}: {noRoom}");
                continue;
            }

            // mkvmerge writes Matroska whatever it read, so an MP4 source comes
            // out as .mkv and the final name changes with it.
            var finalPath = Path.ChangeExtension(source, ".mkv");

            var temp = Path.Combine(
                Path.GetDirectoryName(source)!,
                Path.GetFileNameWithoutExtension(source) + ".msremux.tmp.mkv");

            // Whether the original has been given up yet. Once it has, the temp
            // file is the only copy and the failure paths must leave it alone.
            var retired = false;

            try
            {
                if (File.Exists(temp)) File.Delete(temp);

                var mkvTracks = await IdentifyAsync(source, ct).ConfigureAwait(false);
                if (mkvTracks.Count == 0)
                {
                    result.Failed++;
                    result.Errors.Add($"{plan.FileName}: mkvmerge could not identify the file");
                    continue;
                }

                var sizeBefore = new FileInfo(source).Length;
                var args = BuildArgs(source, temp, plan, mkvTracks);

                // Report every whole percent as mkvmerge works, so a 25 GB file
                // shows real movement instead of sitting still until it's done.
                var lastPct = -1;
                var index = i + 1;

                var (code, _, stderr) = await RunAsync(args, ct, pct =>
                {
                    if (pct == lastPct) return;
                    lastPct = pct;

                    progress?.Report(new RemuxProgress
                    {
                        FileName = plan.FileName,
                        Index = index,
                        Total = todo.Count,
                        Message = $"remuxing (mkvmerge) {pct}%",
                        Percent = pct
                    });
                }).ConfigureAwait(false);

                // mkvmerge uses 1 for "completed with warnings", which is fine.
                if ((code != 0 && code != 1) || !File.Exists(temp))
                {
                    result.Failed++;
                    result.Errors.Add($"{plan.FileName}: mkvmerge exited {code}. {Tail(stderr)}");
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

                // The destination has to be free before anything is given up.
                // File.Move throws when it is not, and that throw used to reach
                // the catch below, which deleted the verified output - after the
                // original had already been archived or recycled.
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

                RetireOriginal(source, batchId);

                // Past here the remuxed file is the only copy.
                retired = true;

                // Archiving usually moves the original away, but if it was only
                // recycled - or the container changed - make sure the old name
                // isn't left sitting beside the new one.
                if (!string.Equals(finalPath, source, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(source))
                {
                    TryDelete(source);
                }

                File.Move(temp, finalPath);

                _history.Add(HistoryAction.Rename, source, finalPath,
                    $"remux (mkvmerge): dropped {plan.Drop.Count} track(s)"
                    + (string.Equals(finalPath, source, StringComparison.OrdinalIgnoreCase)
                        ? "" : $", {Path.GetExtension(source)} -> .mkv"),
                    null, batchId);

                result.Succeeded++;
                result.BytesReclaimed += Math.Max(0, sizeBefore - new FileInfo(finalPath).Length);

                RefreshNfo(finalPath, source, batchId, ct);
            }
            catch (OperationCanceledException)
            {
                if (!retired) TryDelete(temp);
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{plan.FileName}: {ex.Message}");

                if (retired)
                {
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
    /// mkvmerge takes the tracks to KEEP as comma-separated ids, and omitting the
    /// switch entirely keeps all of that kind. Passing an empty list would be
    /// read as "keep none", so the two cases are handled separately.
    /// </summary>
    private static List<string> BuildArgs(
        string source, string temp, RemuxFilePlan plan, List<MkvTrack> mkvTracks)
    {
        var args = new List<string> { "--output", temp };

        // Map ffprobe streams to mkvmerge ids by their position within each kind,
        // which is stable for Matroska.
        List<int> Ids(StreamKind kind, string mkvType)
        {
            var mine = plan.Info.Streams.Where(s => s.Kind == kind).OrderBy(s => s.Index).ToList();
            var theirs = mkvTracks.Where(t => t.Type == mkvType).OrderBy(t => t.Id).ToList();

            var keep = new List<int>();
            for (var i = 0; i < mine.Count && i < theirs.Count; i++)
                if (plan.Keep.Any(k => k.Index == mine[i].Index))
                    keep.Add(theirs[i].Id);

            return keep;
        }

        var audio = Ids(StreamKind.Audio, "audio");
        var subs = Ids(StreamKind.Subtitle, "subtitles");

        if (audio.Count > 0) { args.Add("--audio-tracks"); args.Add(string.Join(",", audio)); }
        else if (plan.Info.Audio.Any()) { args.Add("--no-audio"); }

        if (subs.Count > 0) { args.Add("--subtitle-tracks"); args.Add(string.Join(",", subs)); }
        else if (plan.Info.Subtitles.Any()) { args.Add("--no-subtitles"); }

        var mineVideo = plan.Info.Video.OrderBy(s => s.Index).ToList();
        var theirsVideo = mkvTracks.Where(t => t.Type == "video").OrderBy(t => t.Id).ToList();

        // Video said as explicitly as audio and subtitles, which it was not.
        // Without this switch mkvmerge keeps every video track, so unticking
        // one - the embedded cover art, or the second video track on a
        // DV-plus-HDR10 file - did nothing at all, while the window reported a
        // track removed and the original was archived for a rewrite that
        // changed none of what was asked.
        //
        // Mapped from plan.Info.Video rather than through Ids, because that
        // walks every video-kind stream and cover art is one of those on the
        // ffprobe side while being an attachment rather than a track on
        // mkvmerge's - so the positions would slide by one on any file with an
        // embedded poster, and the wrong track would be kept.
        var video = new List<int>();

        for (var i = 0; i < mineVideo.Count && i < theirsVideo.Count; i++)
            if (plan.Keep.Any(k => k.Index == mineVideo[i].Index))
                video.Add(theirsVideo[i].Id);

        if (video.Count == 0 && mineVideo.Count > 0) args.Add("--no-video");
        else if (video.Count < mineVideo.Count)
        {
            args.Add("--video-tracks");
            args.Add(string.Join(",", video));
        }

        // Attachments are not tracks and are not covered by any of the above.
        // A poster attached to the file survives every track switch, so
        // dropping cover art has to say so separately.
        if (plan.Drop.Any(d => d.IsCoverArt)) args.Add("--no-attachments");

        if (mineVideo.Count > 1)
        {
            for (var i = 0; i < mineVideo.Count && i < theirsVideo.Count; i++)
            {
                var isDefault = plan.DefaultVideoIndex is { } wantV
                    ? mineVideo[i].Index == wantV
                    : mineVideo[i].IsDefault;

                args.Add("--default-track-flag");
                args.Add($"{theirsVideo[i].Id}:{(isDefault ? "1" : "0")}");
            }
        }

        // Default flags, expressed per surviving track id.
        var mineAudio = plan.Info.Audio.OrderBy(s => s.Index).ToList();
        var theirsAudio = mkvTracks.Where(t => t.Type == "audio").OrderBy(t => t.Id).ToList();

        for (var i = 0; i < mineAudio.Count && i < theirsAudio.Count; i++)
        {
            if (!audio.Contains(theirsAudio[i].Id)) continue;

            var isDefault = plan.DefaultAudioIndex is { } want
                ? mineAudio[i].Index == want
                : mineAudio[i].IsDefault;

            args.Add("--default-track-flag");
            args.Add($"{theirsAudio[i].Id}:{(isDefault ? "1" : "0")}");
        }

        var mineSubs = plan.Info.Subtitles.OrderBy(s => s.Index).ToList();
        var theirsSubs = mkvTracks.Where(t => t.Type == "subtitles").OrderBy(t => t.Id).ToList();

        for (var i = 0; i < mineSubs.Count && i < theirsSubs.Count; i++)
        {
            if (!subs.Contains(theirsSubs[i].Id)) continue;

            var forced = plan.IsForced(mineSubs[i]);
            var isDefault = forced || plan.DefaultSubtitleIndex == mineSubs[i].Index;

            args.Add("--default-track-flag");
            args.Add($"{theirsSubs[i].Id}:{(isDefault ? "1" : "0")}");

            if (forced)
            {
                args.Add("--forced-display-flag");
                args.Add($"{theirsSubs[i].Id}:1");
            }
        }

        // Carry the stereo flag across explicitly. mkvmerge normally preserves it,
        // but for MVC it's the only thing telling a player the track holds two
        // eyes - worth stating rather than assuming.
        foreach (var v in mkvTracks.Where(t => t.Type == "video" && t.StereoMode is > 0))
        {
            args.Add("--stereo-mode");
            args.Add($"{v.Id}:{v.StereoMode}");
        }

        if (plan.SetTitleFromFileName)
        {
            args.Add("--title");
            args.Add(Path.GetFileNameWithoutExtension(source));
        }

        args.Add(source);
        return args;
    }

    private async Task<string?> VerifyAsync(RemuxFilePlan plan, string temp, CancellationToken ct)
    {
        var info = await _probe.ProbeAsync(temp, ct).ConfigureAwait(false);
        if (info is null) return "output could not be probed";

        // Cover art is a video stream in the plan but not in Video, so exclude
        // it on both sides - otherwise an embedded poster reads as a lost track.
        var expectedVideo = plan.Keep.Count(s => s.Kind == StreamKind.Video && !s.IsCoverArt);

        // Not fewer than expected, and not more either. Testing only for a
        // shortfall meant a track that was supposed to go and did not passed
        // the check: unticking a second video track or an embedded poster was
        // reported as done, the original was archived, and the file still had
        // everything it started with.
        if (info.Video.Count() != expectedVideo)
            return $"output has {info.Video.Count()} video track(s), expected {expectedVideo}";

        var expectedAudio = plan.Keep.Count(s => s.Kind == StreamKind.Audio);

        if (info.Audio.Count() != expectedAudio)
            return $"output has {info.Audio.Count()} audio track(s), expected {expectedAudio}";

        var expectedCover = plan.Keep.Count(s => s.IsCoverArt);

        if (info.CoverArt.Count() > expectedCover)
            return $"output still carries {info.CoverArt.Count()} embedded image(s), expected {expectedCover}";

        // Measured against the tracks being kept rather than the source
        // container. See DurationCheck - the flat two seconds this used to
        // allow rejected a good remux of a film whose foreign audio ran a
        // minute past the picture.
        if (DurationCheck.Failed(plan.Info, info, plan.Keep) is { } truncated) return truncated;

        if (OutputCheck.TooSmall(temp, plan.Info, plan.HasMvc) is { } small) return small;

        // mkvmerge is the engine that is meant to carry Dolby Vision
        // across, but "meant to" is not "did".
        if (OutputCheck.DolbyVisionLost(plan.Info, info) is { } dv) return dv;

        return null;
    }

    private void RetireOriginal(string path, string batchId)
    {
        if (settings.ArchiveOriginals)
        {
            var dir = Path.GetDirectoryName(path)!;

            var archiveDir = !string.IsNullOrWhiteSpace(settings.ArchiveRootPath)
                ? Path.Combine(settings.ArchiveRootPath,
                               Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)))
                : Path.Combine(dir, settings.ArchiveFolderName);

            Directory.CreateDirectory(archiveDir);

            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var target = Path.Combine(archiveDir, stem + settings.ArchiveSuffix + ext);

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
            return;
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
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Bring the sidecar's track list back in line with the file. Kodi shows
    /// what the .nfo says in preference to re-probing, so after dropping tracks
    /// a stale block advertises audio and subtitles that are no longer there.
    /// Everything else in the .nfo is left exactly as it was.
    /// </summary>
    private void RefreshNfo(string videoPath, string originalPath, string batchId, CancellationToken ct)
    {
        if (!settings.UpdateNfoAfterRemux) return;

        try
        {
            // The .nfo may still sit under the pre-remux name if the container
            // changed, so rename it to follow the video first.
            var wanted = Metadata.NfoWriter.PathFor(videoPath);
            var old = Metadata.NfoWriter.PathFor(originalPath);

            if (!File.Exists(wanted) && File.Exists(old)
                && !string.Equals(old, wanted, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(old, wanted);

                // Recorded like any other move. Without this a revert put the
                // video back under its old name and left the sidecar under the
                // new one, orphaning the metadata.
                _history.Add(HistoryAction.Rename, old, wanted,
                             "remux: sidecar followed the container change", null, batchId);
            }

            if (!File.Exists(wanted)) return;

            var info = _probe.ProbeAsync(videoPath, ct).GetAwaiter().GetResult();
            if (info is null) return;

            Metadata.NfoWriter.UpdateStreamDetails(wanted, info);
        }
        catch
        {
            // A sidecar that can't be refreshed is a cosmetic problem; the
            // remux itself already succeeded and must not be reported failed.
        }
    }

    private static string Tail(string s, int max = 300)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : "..." + s[^max..];
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunAsync(
        List<string> args, CancellationToken ct, Action<int>? onPercent = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = settings.MkvMergePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // --gui-mode makes mkvmerge emit "#GUI#progress 42%" as it works. Without
        // it there is no signal at all between start and finish, which on a 25 GB
        // file means a progress bar that sits still for minutes.
        if (onPercent is not null) psi.ArgumentList.Add("--gui-mode");

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Read stdout line by line rather than to the end, so progress arrives
        // while the process is still running.
        var stdout = new System.Text.StringBuilder();

        var outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                stdout.AppendLine(line);

                if (onPercent is null) continue;

                var i = line.IndexOf("#GUI#progress", StringComparison.Ordinal);
                if (i < 0) continue;

                var pct = line[(i + 13)..].Trim().TrimEnd('%').Trim();
                if (int.TryParse(pct, out var n)) onPercent(Math.Clamp(n, 0, 100));
            }
        }, ct);

        var errTask = proc.StandardError.ReadToEndAsync(ct);

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

        return (proc.ExitCode, stdout.ToString(), await errTask.ConfigureAwait(false));
    }
}
