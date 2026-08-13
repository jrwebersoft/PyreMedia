using System.Diagnostics;
using System.Text.Json;

namespace PyreMedia.Core.Media;

public enum StreamKind { Video, Audio, Subtitle, Other }

public sealed class MediaStream
{
    public required int Index { get; init; }
    public required StreamKind Kind { get; init; }
    public required string Codec { get; init; }

    /// <summary>ISO 639-2 code from tags, or "und" when the file doesn't say.</summary>
    public required string Language { get; init; }

    public string? Title { get; init; }
    public int Channels { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }

    /// <summary>ffprobe's codec profile, e.g. "DTS-HD MA" or "Dolby TrueHD + Atmos".</summary>
    public string? Profile { get; init; }

    /// <summary>e.g. "5.1(side)", "7.1".</summary>
    public string? ChannelLayout { get; init; }

    public long BitRate { get; init; }

    /// <summary>
    /// How long this stream runs, where the container says. Zero when it does
    /// not - MKV often reports only the container's figure.
    ///
    /// Needed because container duration is the longest stream, so dropping
    /// that stream shortens the file legitimately. Verifying against the
    /// container alone called a correct remux truncated: a 2160p film whose
    /// two Spanish audio tracks ran sixty seconds past the picture came out
    /// exactly sixty seconds "short" when they were dropped.
    /// </summary>
    public double Seconds { get; init; }
    public int SampleRate { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>
    /// An embedded poster or thumbnail rather than a second feature. Containers
    /// carry these as video streams, so without this a 600x900 JPEG counts as a
    /// video track and picks up warnings meant for real footage.
    /// </summary>
    public bool IsCoverArt { get; init; }

    /// <summary>Stereoscopic layout, e.g. "side by side" or "left_right". Null for 2D.</summary>
    public string? Stereo3D { get; init; }

    // ---- Dolby Vision ----
    //
    // The RPU that carries the dynamic metadata is inside the HEVC bitstream,
    // so "-c copy" can't damage it. What CAN be lost is the container-level
    // DOVI configuration record - the part that tells a player to engage DV at
    // all. Lose that and the file quietly plays as plain HDR10.

    /// <summary>DV profile: 5 single-layer, 7 dual-layer (Blu-ray), 8 HDR10-compatible.</summary>
    public int? DvProfile { get; init; }

    public int? DvLevel { get; init; }

    /// <summary>An enhancement layer exists - profile 7, and the risky case.</summary>
    public bool DvHasEnhancementLayer { get; init; }

    public bool DvHasRpu { get; init; }

    /// <summary>Base-layer compatibility: 1 = HDR10, 2 = SDR, 4 = HLG.</summary>
    public int? DvCompatibilityId { get; init; }

    public bool IsDolbyVision => DvProfile is not null;

    /// <summary>
    /// Dual-layer DV keeps the enhancement layer as a separate substream, the
    /// same shape of problem as MVC: a muxer that doesn't understand it writes
    /// out the base layer alone and the DV is gone for good.
    /// </summary>
    public bool IsDualLayerDv => DvProfile == 7 || DvHasEnhancementLayer;

    /// <summary>
    /// What a player without Dolby Vision support falls back to, or null when
    /// there is nothing to fall back to.
    ///
    /// Profile 5 is the case that matters: its base layer is encoded in
    /// IPT-PQ-C2 rather than BT.2020 PQ, so a player that ignores the RPU
    /// doesn't show a flat-but-correct HDR10 picture - it misreads the colour
    /// space entirely and the result is visibly wrong, usually green or purple.
    /// Profiles 7 and 8.1 carry a genuine HDR10 base layer, which is why
    /// Blu-rays play correctly on non-DV hardware.
    /// </summary>
    public string? DvFallback => DvProfile switch
    {
        null => null,
        5 => null,
        7 => "HDR10",
        8 when DvCompatibilityId == 1 => "HDR10",
        8 when DvCompatibilityId == 2 => "SDR",
        8 when DvCompatibilityId == 4 => "HLG",
        8 when DvCompatibilityId == 6 => "HDR10",
        _ => HdrFormat
    };

    /// <summary>
    /// Dolby Vision with no usable fallback - it needs a DV-capable player, and
    /// anything else will show the wrong colours rather than merely a duller
    /// picture. Worth knowing before it goes near a device that can't play it.
    /// </summary>
    public bool IsDolbyVisionOnly => IsDolbyVision && DvFallback is null;

    public string? DvLabel => DvProfile switch
    {
        null => null,
        5 => "Dolby Vision P5 (DV only - no fallback)",
        7 => "Dolby Vision P7 (dual layer)",
        8 when DvCompatibilityId == 1 => "Dolby Vision P8.1 (HDR10 compatible)",
        8 when DvCompatibilityId == 2 => "Dolby Vision P8.2 (SDR compatible)",
        8 when DvCompatibilityId == 4 => "Dolby Vision P8.4 (HLG compatible)",
        8 when DvCompatibilityId == 6 => "Dolby Vision P8 (HDR10 compatible)",
        var p => $"Dolby Vision P{p}"
    };

    /// <summary>HDR10 / HDR10+ / HLG, independent of Dolby Vision.</summary>
    public string? HdrFormat { get; init; }

    public bool Is3D => !string.IsNullOrWhiteSpace(Stereo3D);

    /// <summary>
    /// Blu-ray 3D MVC, where the second eye lives in a dependent substream.
    /// ffmpeg doesn't handle MVC, so it sees only the base view - remuxing such
    /// a file would silently discard the 3D.
    /// </summary>
    public bool IsMvc =>
        (Profile?.Contains("Stereo High", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Profile?.Contains("Multiview", StringComparison.OrdinalIgnoreCase) ?? false) ||
        // Matroska "block_lr"/"block_rl" laces both eyes into one block - the
        // packing MakeMKV writes for Blu-ray 3D. ffmpeg reports the base view's
        // profile as plain High, so the profile check alone misses these.
        (Stereo3D?.StartsWith("block", StringComparison.OrdinalIgnoreCase) ?? false) ||
        string.Equals(Stereo3D, "frame alternate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Full side-by-side / over-under carry no metadata flag at all - the only
    /// signal is the frame being twice as wide (or tall) as its aspect implies.
    /// Half-SBS is undetectable this way: it's squeezed back to 1920x1080.
    /// </summary>
    public string? PackedLayout
    {
        get
        {
            if (Kind != StreamKind.Video || Width <= 0 || Height <= 0) return null;

            var ratio = (double)Width / Height;
            if (ratio > 3.0) return "SBS";    // e.g. 3840x1080 - two 16:9 eyes side by side
            if (ratio < 1.1) return "OU";     // e.g. 1920x2160 - stacked
            return null;
        }
    }

    /// <summary>Any 3D signal at all: metadata flag or packed geometry.</summary>
    public bool Is3DAnySignal => Is3D || PackedLayout is not null;

    /// <summary>
    /// Height that isn't one of the broadcast/disc standards - almost always a
    /// cropped scope transfer (1920x800) rather than a corrupt file, but worth
    /// pointing out before the file is rewritten.
    /// </summary>
    public bool IsNonStandardHeight =>
        Kind == StreamKind.Video && Height > 0 &&
        Height is not (2160 or 1440 or 1080 or 720 or 576 or 480 or 360 or 240);

    /// <summary>Per-stream index within its kind - what ffmpeg's -map 0:a:N refers to.</summary>
    public int KindIndex { get; init; }

    public string ChannelLabel => Channels switch
    {
        0 => "",
        1 => "mono",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        _ => $"{Channels}ch"
    };

    /// <summary>
    /// Object audio isn't a codec of its own - it rides inside TrueHD or E-AC3
    /// and ffprobe surfaces it in the profile or the track title, so both are
    /// checked.
    /// </summary>
    public bool IsAtmos =>
        (Profile?.Contains("atmos", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Title?.Contains("atmos", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Profile?.Contains("joc", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool IsDtsX =>
        (Profile?.Contains("dts:x", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Title?.Contains("dts-x", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Title?.Contains("dts:x", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Lossless formats are the ones worth thinking twice about dropping.</summary>
    public bool IsLossless =>
        Codec.Equals("truehd", StringComparison.OrdinalIgnoreCase) ||
        Codec.Equals("flac", StringComparison.OrdinalIgnoreCase) ||
        Codec.Equals("mlp", StringComparison.OrdinalIgnoreCase) ||
        Codec.Equals("pcm_s24le", StringComparison.OrdinalIgnoreCase) ||
        (Profile?.Contains("MA", StringComparison.Ordinal) ?? false);

    /// <summary>Readable codec name - "eac3" means nothing at a glance.</summary>
    public string FormatLabel
    {
        get
        {
            var name = Codec.ToLowerInvariant() switch
            {
                "truehd" => "TrueHD",
                "eac3" => "E-AC3",
                "ac3" => "AC3",
                "dts" => Profile?.Contains("MA", StringComparison.Ordinal) == true ? "DTS-HD MA"
                       : Profile?.Contains("HRA", StringComparison.Ordinal) == true ? "DTS-HD HRA"
                       : "DTS",
                "aac" => "AAC",
                "flac" => "FLAC",
                "opus" => "Opus",
                "mp3" => "MP3",
                "pcm_s16le" or "pcm_s24le" => "PCM",
                "subrip" => "SRT",
                "hdmv_pgs_subtitle" => "PGS",
                "ass" or "ssa" => "ASS",
                "mov_text" => "MP4 text",
                "h264" => "H.264",
                "hevc" => "HEVC",
                "av1" => "AV1",
                _ => Codec.ToUpperInvariant()
            };

            if (IsAtmos) name += " Atmos";
            else if (IsDtsX) name += " DTS:X";

            return name;
        }
    }

    public string BitRateLabel => BitRate > 0
        ? BitRate >= 1_000_000 ? $"{BitRate / 1_000_000.0:F1} Mbps" : $"{BitRate / 1000} kbps"
        : "";

    public string Describe()
    {
        var bits = new List<string> { Language.ToUpperInvariant(), FormatLabel };

        if (Kind == StreamKind.Video && Width > 0)
            bits.Add($"{Width}x{Height}");

        if (Kind == StreamKind.Video)
        {
            // Both, when both are present: a DV file usually carries an HDR10
            // base layer, and which one a player uses depends on the display.
            if (DvLabel is not null) bits.Add(DvLabel);
            if (HdrFormat is not null) bits.Add(HdrFormat);
        }

        if (Kind == StreamKind.Audio)
        {
            var ch = !string.IsNullOrWhiteSpace(ChannelLayout) ? ChannelLayout : ChannelLabel;
            if (!string.IsNullOrEmpty(ch)) bits.Add(ch);
            if (!string.IsNullOrEmpty(BitRateLabel)) bits.Add(BitRateLabel);
            if (IsLossless) bits.Add("lossless");
        }

        if (IsForced) bits.Add("forced");
        if (IsDefault) bits.Add("default");
        if (!string.IsNullOrWhiteSpace(Title)) bits.Add($"\"{Title}\"");

        return string.Join("  -  ", bits);
    }

    /// <summary>Identity for grouping files with the same track layout.</summary>
    public string Signature => $"{Kind}:{Language}:{Codec}:{Channels}:{(IsForced ? "f" : "")}";
}

public sealed class MediaInfo
{
    public required string Path { get; init; }
    public required List<MediaStream> Streams { get; init; }
    public double DurationSeconds { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>Container title tag. Players prefer this over the filename.</summary>
    public string? ContainerTitle { get; init; }

    /// <summary>
    /// True when the embedded title doesn't match the filename - the file was
    /// renamed but still announces its old name.
    /// </summary>
    public bool TitleIsStale =>
        !string.IsNullOrWhiteSpace(ContainerTitle) &&
        !string.Equals(ContainerTitle.Trim(),
                       System.IO.Path.GetFileNameWithoutExtension(Path),
                       StringComparison.OrdinalIgnoreCase);

    public IEnumerable<MediaStream> Audio => Streams.Where(s => s.Kind == StreamKind.Audio);
    public IEnumerable<MediaStream> Subtitles => Streams.Where(s => s.Kind == StreamKind.Subtitle);

    /// <summary>
    /// Real video only. Embedded cover art is carried as a video stream by both
    /// ffprobe and Matroska, so counting it as one made a poster look like a
    /// second feature - and put a "likely cropped" warning on a 600x900 JPEG.
    /// </summary>
    public IEnumerable<MediaStream> Video =>
        Streams.Where(s => s.Kind == StreamKind.Video && !s.IsCoverArt);

    /// <summary>Embedded poster/thumbnail images.</summary>
    public IEnumerable<MediaStream> CoverArt =>
        Streams.Where(s => s.IsCoverArt);

    /// <summary>Files sharing this string have an identical track layout.</summary>
    public string LayoutSignature =>
        string.Join("|", Streams.Where(s => s.Kind != StreamKind.Other).Select(s => s.Signature));
}

/// <summary>Reads track information via ffprobe.</summary>
public sealed class MediaProbe(string? ffprobePath = null)
{
    private readonly string _ffprobe = ffprobePath ?? "ffprobe";

    /// <summary>True when ffprobe can actually be launched.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (code, _, _) = await RunAsync(["-version"], ct).ConfigureAwait(false);
            return code == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// How long a file runs, in seconds, or null if it can't be read. Asks
    /// ffprobe for the duration and nothing else, which is quick enough to use
    /// while planning - a full probe reads every stream and is not.
    /// <para>
    /// Synchronous on purpose: planning is synchronous, and this is only called
    /// for the handful of files that already look like sample clips.
    /// </para>
    /// </summary>
    public double? QuickDuration(string file, int timeoutMs = 4000)
    {
        if (!File.Exists(file)) return null;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(_ffprobe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var a in new[]
                     {
                         "-v", "error",
                         "-show_entries", "format=duration",
                         "-of", "default=noprint_wrappers=1:nokey=1",
                         file
                     })
                psi.ArgumentList.Add(a);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;

            var text = p.StandardOutput.ReadToEnd();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var d)
                   && d > 0
                ? d
                : null;
        }
        catch (Exception)
        {
            // No ffprobe, or it wouldn't run. The caller falls back to size.
            return null;
        }
    }

    /// <summary>
    /// Whether Dolby Vision RPU data is present in the picture itself, as opposed
    /// to the configuration record that points at it.
    /// <para>
    /// The two come apart. Remuxing into MP4 loses the configuration while every
    /// RPU NAL stays exactly where it was - measured, on a profile 5 file - which
    /// is why remuxing such a file back into Matroska brings Dolby Vision back:
    /// the muxer rebuilds the configuration from what is still in the stream.
    /// Without this, a file in that state looks like an ordinary HDR10 file and
    /// there is nothing to suggest it is one remux away from being right.
    /// </para>
    /// <para>
    /// Reads a handful of frames rather than the file, so it costs a moment
    /// rather than a pass over 25 GB.
    /// </para>
    /// </summary>
    public async Task<bool> HasDolbyVisionRpuAsync(
        string file, int frames = 6, CancellationToken ct = default)
    {
        if (!File.Exists(file)) return false;

        try
        {
            var (code, stdout, _) = await RunAsync(
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-read_intervals", $"%+#{frames}",
                "-show_frames",
                "-of", "default=noprint_wrappers=1",
                file
            ], ct).ConfigureAwait(false);

            return code == 0
                   && stdout.Contains("Dolby Vision RPU", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<MediaInfo?> ProbeAsync(string file, CancellationToken ct = default)
    {
        if (!File.Exists(file)) return null;

        var (code, stdout, _) = await RunAsync(
        [
            "-v", "error",
            "-show_streams", "-show_format",
            "-of", "json",
            file
        ], ct).ConfigureAwait(false);

        if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var streams = new List<MediaStream>();
            var counters = new Dictionary<StreamKind, int>();

            if (root.TryGetProperty("streams", out var arr))
            {
                foreach (var s in arr.EnumerateArray())
                {
                    var typeName = Str(s, "codec_type");
                    var kind = typeName switch
                    {
                        "video" => StreamKind.Video,
                        "audio" => StreamKind.Audio,
                        "subtitle" => StreamKind.Subtitle,
                        _ => StreamKind.Other
                    };

                    counters.TryGetValue(kind, out var n);
                    counters[kind] = n + 1;

                    var tags = s.TryGetProperty("tags", out var t) ? t : default;
                    var disp = s.TryGetProperty("disposition", out var d) ? d : default;

                    streams.Add(new MediaStream
                    {
                        Index = s.TryGetProperty("index", out var i) ? i.GetInt32() : 0,
                        KindIndex = n,
                        Kind = kind,
                        Codec = Str(s, "codec_name"),
                        Language = NormalizeLang(tags.ValueKind == JsonValueKind.Object ? Str(tags, "language") : ""),
                        Title = tags.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(tags, "title")) : null,
                        Channels = s.TryGetProperty("channels", out var c) && c.ValueKind == JsonValueKind.Number
                            ? c.GetInt32() : 0,
                        Profile = NullIfEmpty(Str(s, "profile")),
                        ChannelLayout = NullIfEmpty(Str(s, "channel_layout")),
                        BitRate = long.TryParse(Str(s, "bit_rate"), out var br) ? br : 0,
                        Seconds = StreamSeconds(s, tags),
                        SampleRate = int.TryParse(Str(s, "sample_rate"), out var sr) ? sr : 0,
                        Width = s.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number
                            ? w.GetInt32() : 0,
                        Height = s.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number
                            ? h.GetInt32() : 0,
                        // attached_pic is the explicit signal. Some muxers omit
                        // it, so a still-image codec with no duration counts
                        // too - a real feature is never a lone MJPEG frame.
                        IsCoverArt = Flag(disp, "attached_pic")
                                     || IsStillImageCodec(Str(s, "codec_name")),
                        Stereo3D = ReadStereo3D(s, tags),
                        DvProfile = DoviInt(s, "dv_profile"),
                        DvLevel = DoviInt(s, "dv_level"),
                        DvCompatibilityId = DoviInt(s, "dv_bl_signal_compatibility_id"),
                        DvHasEnhancementLayer = DoviInt(s, "el_present_flag") == 1,
                        DvHasRpu = DoviInt(s, "rpu_present_flag") == 1,
                        HdrFormat = ReadHdrFormat(s),
                        IsDefault = Flag(disp, "default"),
                        IsForced = Flag(disp, "forced")
                    });
                }
            }

            double duration = 0;
            long size = 0;
            string? containerTitle = null;
            if (root.TryGetProperty("format", out var fmt))
            {
                if (fmt.TryGetProperty("tags", out var ftags) && ftags.ValueKind == JsonValueKind.Object)
                    containerTitle = NullIfEmpty(Str(ftags, "title"));

                if (double.TryParse(Str(fmt, "duration"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var dur))
                    duration = dur;

                long.TryParse(Str(fmt, "size"), out size);
            }

            return new MediaInfo
            {
                Path = file,
                Streams = streams,
                DurationSeconds = duration,
                SizeBytes = size,
                ContainerTitle = containerTitle
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 3D shows up two ways: a Matroska stereo_mode tag, or a "Stereo 3D"
    /// side-data entry. Check both - files carry one or the other.
    /// </summary>
    private static string? ReadStereo3D(JsonElement stream, JsonElement tags)
    {
        if (tags.ValueKind == JsonValueKind.Object)
        {
            var mode = Str(tags, "stereo_mode");
            if (!string.IsNullOrWhiteSpace(mode) && mode != "mono")
                return mode;
        }

        if (stream.TryGetProperty("side_data_list", out var sd) && sd.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sd.EnumerateArray())
            {
                if (!Str(entry, "side_data_type").Contains("Stereo 3D", StringComparison.OrdinalIgnoreCase))
                    continue;

                var type = Str(entry, "type");
                if (!string.IsNullOrWhiteSpace(type) && type != "2D")
                    return type;
            }
        }

        return null;
    }

    /// <summary>
    /// A field from the "DOVI configuration record" side-data entry. ffprobe
    /// puts these flat inside that entry rather than on the stream itself.
    /// </summary>
    private static int? DoviInt(JsonElement stream, string prop)
    {
        if (!stream.TryGetProperty("side_data_list", out var sd) || sd.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var entry in sd.EnumerateArray())
        {
            if (!Str(entry, "side_data_type").Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = Str(entry, prop);
            if (int.TryParse(raw, out var n)) return n;
        }

        return null;
    }

    /// <summary>
    /// HDR flavour from the transfer characteristics, plus HDR10+ when its
    /// dynamic metadata is present. Separate from DV - a file can carry both.
    /// </summary>
    private static string? ReadHdrFormat(JsonElement stream)
    {
        var transfer = Str(stream, "color_transfer").ToLowerInvariant();

        var hasHdr10Plus = false;
        if (stream.TryGetProperty("side_data_list", out var sd) && sd.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sd.EnumerateArray())
            {
                var t = Str(entry, "side_data_type");
                if (t.Contains("HDR Dynamic Metadata", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("SMPTE2094", StringComparison.OrdinalIgnoreCase))
                {
                    hasHdr10Plus = true;
                }
            }
        }

        return transfer switch
        {
            "smpte2084" => hasHdr10Plus ? "HDR10+" : "HDR10",
            "arib-std-b67" => "HLG",
            _ => null
        };
    }

    /// <summary>
    /// Codecs that only ever appear as embedded artwork in a video file. MJPEG
    /// is the common one - Kodi and Jellyfin both write posters this way.
    /// </summary>
    private static bool IsStillImageCodec(string codec) =>
        codec.ToLowerInvariant() is "mjpeg" or "png" or "bmp" or "gif" or "webp" or "tiff";

    private static string NormalizeLang(string lang) =>
        string.IsNullOrWhiteSpace(lang) ? "und" : lang.Trim().ToLowerInvariant();

    /// <summary>
    /// How long one stream actually runs.
    ///
    /// The obvious field is <c>duration</c>, and in Matroska it is very nearly
    /// useless: it is usually absent, and when it is present it can be the
    /// container's length rather than the stream's. On a real file every stream
    /// reported N/A except one subtitle track, which reported the container -
    /// so the "longest kept stream" came out as the whole container, the check
    /// expected 92:40 from a film whose picture is 89:58, and a good remux was
    /// thrown away as truncated. That is the same fault as before wearing a
    /// different hat: the first two versions measured the container on purpose,
    /// this one measured it by accident.
    ///
    /// Matroska keeps the real figure in a DURATION tag, written per stream as
    /// <c>01:29:58.518000000</c>. Preferred where it exists, because it is the
    /// answer to the question actually being asked.
    /// </summary>
    private static double StreamSeconds(JsonElement stream, JsonElement tags)
    {
        if (tags.ValueKind == JsonValueKind.Object)
        {
            // Tag names vary in case between muxers - DURATION, Duration.
            foreach (var tag in tags.EnumerateObject())
            {
                if (!tag.NameEquals("DURATION")
                    && !string.Equals(tag.Name, "duration", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryHms(tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() : null,
                           out var tagged))
                    return tagged;
            }
        }

        return double.TryParse(Str(stream, "duration"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var plain) ? plain : 0;
    }

    /// <summary>"01:29:58.518000000" as seconds. False for anything else.</summary>
    private static bool TryHms(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(':');
        if (parts.Length != 3) return false;

        var culture = System.Globalization.CultureInfo.InvariantCulture;

        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, culture, out var h)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, culture, out var m)
            || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out var s))
            return false;

        if (h < 0 || m < 0 || s < 0) return false;

        seconds = h * 3600 + m * 60 + s;
        return true;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => ""
            }
            : "";

    private static bool Flag(JsonElement disp, string name) =>
        disp.ValueKind == JsonValueKind.Object &&
        disp.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number &&
        v.GetInt32() == 1;

    private async Task<(int Code, string StdOut, string StdErr)> RunAsync(
        string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }
}
