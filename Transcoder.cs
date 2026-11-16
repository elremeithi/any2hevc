using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Any2Hevc;

public enum JobState { Pending, Skipped, Working, Done, Failed }

public sealed class Job
{
    public required string Path { get; init; }
    /// <summary>The library folder the file was found under; backups and output copies mirror the path below it.</summary>
    public required string Root { get; init; }
    public MediaInfo? Media { get; set; }
    public JobState State { get; set; }
    public Outcome? Outcome { get; set; }
    public string Status { get; set; } = "";
    public string QualityName { get; set; } = "";
    public int? Quality { get; set; }
    public double? Vmaf { get; set; }
    public long? NewSize { get; set; }
    /// <summary>Picture size after removing black bars, e.g. "1920x800"; null when not cropped.</summary>
    public string? Crop { get; set; }
    /// <summary>The library file after the job (the HEVC file when replacing, else the untouched source).</summary>
    public string? NewPath { get; set; }
    /// <summary>Where the original was moved (backup mode), so Revert can put it back.</summary>
    public string? BackupPath { get; set; }
    /// <summary>When the encode finished (local time).</summary>
    public DateTime? Finished { get; set; }
    /// <summary>Found by watch mode; queued ahead of the existing backlog.</summary>
    public bool Arrived { get; set; }
    public ListViewItem? Item { get; set; }
}

/// <summary>
/// Re-encodes one file to HEVC at the highest CRF / CQ whose encode still reaches the target VMAF:
/// short samples spread through the file are test-encoded in a binary search, VMAF-scored against the source
/// samples, and the winner predicts the final size. Only files that shrink enough are fully encoded,
/// verified and swapped in.
/// </summary>
public sealed class Transcoder
{
    readonly AppSettings _s;
    readonly Action<string> _log;
    /// <summary>Status text and fraction done (negative while it can't be known).</summary>
    readonly Action<string, double> _progress;

    const double SampleSeconds = 8;

    public Transcoder(AppSettings settings, Action<string> log, Action<string, double> progress)
    {
        _s = settings;
        _log = log;
        _progress = progress;
    }

    /// <summary>Why a file isn't worth testing at all, or null if it should be queued.</summary>
    public static string? SkipReason(MediaInfo m, AppSettings s)
    {
        if (m.Width == 0) return "No video";
        switch (m.VideoCodec)
        {
            // Re-encoding our own output would only stack quality loss for little gain.
            case "hevc" when m.EncodedByUs: return "Already re-encoded";
            case "hevc" when !s.ReencodeHevc: return "Already HEVC";
            case "av1": return "Already AV1";
            case "vp9": return "Already VP9";
        }
        // 8-bit output can't carry HDR, and Dolby Vision is always HDR.
        if (m.IsHdr || m.DolbyVision) return "HDR";
        if (m.Duration < 60) return "Shorter than 1 min";
        return null;
    }

    public static string QualityNameFor(EncoderKind e) => e == EncoderKind.X265 ? "CRF" : "CQ";

    /// <summary>Search range; higher is smaller and worse.</summary>
    static (int Min, int Max) Range(EncoderKind e) => e == EncoderKind.X265 ? (14, 32) : (16, 38);

    static bool Known(string v) => v.Length > 0 && v != "unknown" && v != "reserved";

    /// <summary>Encoder options for the first output video stream; shared by test samples and the full encode.</summary>
    List<string> VideoArgs(EncoderKind enc, int q, MediaInfo m)
    {
        var a = new List<string>();
        if (_filter is not null) a.AddRange(new[] { "-filter:v:0", _filter });
        if (enc == EncoderKind.X265)
        {
            a.AddRange(new[] { "-c:v:0", "libx265", "-preset:v:0", _s.X265Preset, "-crf:v:0", Str(q),
                "-pix_fmt:v:0", "yuv420p", "-profile:v:0", "main", "-x265-params:v:0", _s.ProtectDarkScenes ? "log-level=error:aq-mode=3" : "log-level=error" });
        }
        else
        {
            a.AddRange(new[] { "-c:v:0", "hevc_nvenc", "-preset:v:0", _s.NvencPreset, "-tune:v:0", "hq",
                "-rc:v:0", "vbr", "-cq:v:0", Str(q), "-b:v:0", "0", "-multipass:v:0", "fullres",
                "-spatial-aq:v:0", "1", "-temporal-aq:v:0", "1", "-rc-lookahead:v:0", "32",
                "-bf:v:0", "4", "-b_ref_mode:v:0", "middle", "-pix_fmt:v:0", "yuv420p", "-profile:v:0", "main" });
            if (_s.ProtectDarkScenes) a.AddRange(new[] { "-aq-strength:v:0", "12" }); // default 8
        }
        // 8-bit Main profile: plays on every HEVC device. 10-bit sources are dithered down.
        if (Known(m.ColorPrimaries)) a.AddRange(new[] { "-color_primaries:v:0", m.ColorPrimaries });
        if (Known(m.ColorTransfer)) a.AddRange(new[] { "-color_trc:v:0", m.ColorTransfer });
        if (Known(m.ColorSpace)) a.AddRange(new[] { "-colorspace:v:0", m.ColorSpace });
        a.AddRange(new[] { "-fps_mode:v:0", "passthrough" });
        return a;
    }

    /// <summary>One output frame per input frame, so VMAF can still pair frames by number.</summary>
    const string Deinterlace = "bwdif=mode=send_frame";

    /// <summary>
    /// 5% unsharp mask on luma only (chroma sharpening just adds colour fringes). The VMAF reference gets it too,
    /// so the search measures compression loss rather than rewarding the sharper picture.
    /// </summary>
    const string Sharpen = "unsharp=luma_msize_x=5:luma_msize_y=5:luma_amount=0.05";

    /// <summary>Deinterlace / crop chain for the current file, or null.</summary>
    string? _filter;

    sealed record Crop(int W, int H, int X, int Y);

    static readonly Regex CropLine = new(@"crop=(\d+):(\d+):(\d+):(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Finds black bars on any side by running cropdetect at 12 points through the file and keeping everything any
    /// point saw as picture. A dark scene, or a wider shot elsewhere (IMAX sequences), is never cut off.
    /// Null when there is nothing to crop.
    /// </summary>
    async Task<Crop?> DetectCropAsync(string src, MediaInfo m, CancellationToken ct)
    {
        _progress("Looking for black bars…", -1);
        const int Points = 12;
        // limit < 1 is a fraction of full scale, so it works for any source bit depth (video black is ~0.063).
        var detect = (m.Interlaced ? Deinterlace + "," : "") + "cropdetect=limit=0.094:round=2:reset=0";
        int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
        for (int i = 0; i < Points; i++)
        {
            double t = m.Duration * (0.05 + 0.9 * (i + 0.5) / Points);
            var r = await Tools.RunAsync(Tools.FFmpeg!, new[] { "-hide_banner", "-nostdin", "-ss", Str(Math.Round(t, 3)),
                "-i", src, "-map", $"0:{m.VideoIndex}", "-frames:v", "24", "-vf", detect, "-f", "null", "-" }, ct, _s.LowPriority);
            var found = CropLine.Matches(r.StdErr);
            if (found.Count == 0) continue;
            var g = found[^1].Groups;
            int w = int.Parse(g[1].Value), h = int.Parse(g[2].Value), x = int.Parse(g[3].Value), y = int.Parse(g[4].Value);
            if (w == 0 || h == 0) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + w);
            bottom = Math.Max(bottom, y + h);
        }
        if (right == 0) return null;

        // 4:2:0 needs even offsets and sizes; round outwards so no picture is lost.
        left &= ~1;
        top &= ~1;
        right = Math.Min(m.Width, right + (right & 1));
        bottom = Math.Min(m.Height, bottom + (bottom & 1));
        int cw = (right - left) & ~1, ch = (bottom - top) & ~1;
        if (cw >= m.Width - 1 && ch >= m.Height - 1) return null;
        // Less than half the frame left means a mostly black video, not bars.
        if (cw < m.Width / 2 || ch < m.Height / 2) return null;
        return new Crop(cw, ch, left, top);
    }

    static string Str(double v) => v.ToString(CultureInfo.InvariantCulture);

    public async Task ProcessAsync(Job job, CancellationToken ct)
    {
        var m = job.Media!;
        var enc = _s.Encoder;
        job.QualityName = QualityNameFor(enc);

        var ext = Path.GetExtension(job.Path).ToLowerInvariant();
        bool mp4 = ext is ".mp4" or ".m4v";
        var outExt = mp4 ? ext : ".mkv";
        var finalPath = _s.Handling == OriginalHandling.WriteToOutputFolder
            ? Path.ChangeExtension(Mirror(_s.OutputFolder, job), outExt)
            : Path.ChangeExtension(job.Path, outExt);
        if (_s.Handling == OriginalHandling.WriteToOutputFolder && _s.OutputFolder.Length == 0)
            throw new InvalidOperationException("Choose an output folder");
        if (_s.Handling == OriginalHandling.MoveToBackup && _s.BackupFolder.Length == 0)
            throw new InvalidOperationException("Choose a backup folder");
        if (!finalPath.Equals(job.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(finalPath))
            throw new InvalidOperationException($"{Path.GetFileName(finalPath)} already exists");

        var outDir = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(outDir);
        var tempOut = Path.Combine(outDir, Path.GetFileNameWithoutExtension(finalPath) + ".any2hevc-tmp" + outExt);
        var work = Path.Combine(Path.GetTempPath(), Tools.AppName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        _log($"{Path.GetFileName(job.Path)}: {m.VideoCodec} {m.Resolution}, " +
             $"{Tools.Size(m.Size)}, {TimeSpan.FromSeconds(m.Duration):h\\:mm\\:ss}, {enc}");
        try
        {
            // Deinterlace before cropping so the fields stay intact, and sharpen last. The same chain is applied to the
            // test encodes, the VMAF reference and the full encode.
            var filters = new List<string>();
            if (m.Interlaced) filters.Add(Deinterlace);
            if (_s.CropBlackBars && await DetectCropAsync(job.Path, m, ct) is { } crop)
            {
                job.Crop = $"{crop.W}x{crop.H}";
                filters.Add($"crop={crop.W}:{crop.H}:{crop.X}:{crop.Y}");
                _log($"  black bars: cropping {m.Resolution} to {job.Crop} (left {crop.X}, top {crop.Y})");
            }
            if (_s.Sharpen)
            {
                filters.Add(Sharpen);
                _log("  sharpening 5%");
            }
            _filter = filters.Count > 0 ? string.Join(",", filters) : null;

            var samples = await MakeSamplesAsync(job.Path, m, work, ct);

            // The full file is predicted from the samples: source video bytes/s vs HEVC bytes/s, other streams
            // unchanged. The search may not pick a setting whose video would outgrow the source's.
            double origVideo = samples.Bytes > 0 ? samples.Bytes / samples.Seconds * m.Duration
                : m.VideoBitrate > 0 ? m.VideoBitrate / 8.0 * m.Duration : m.Size * 0.9;
            origVideo = Math.Min(origVideo, m.Size);
            var best = await SearchAsync(enc, m, samples, work, origVideo / m.Duration * samples.Seconds, ct);
            job.Quality = best.Q;
            job.Vmaf = best.Vmaf;

            double predicted = m.Size - origVideo + best.Bytes / samples.Seconds * m.Duration;
            double change = predicted / m.Size - 1;
            _log($"  chose {job.QualityName} {best.Q} (VMAF {best.Vmaf:0.0}" + (best.Dark is double bd ? $", dark scenes {bd:0.0}" : "") + $"), predicted {Tools.Size((long)predicted)} " +
                 (change <= 0 ? $"= {-change:P0} smaller" : $"= {change:P0} larger"));

            if (Tools.FreeSpace(outDir) is long free && free < predicted * 1.1 + (1L << 30))
                throw new InvalidOperationException($"Not enough free space in {outDir}");

            await EncodeAsync(job, enc, best.Q, mp4, tempOut, ct);

            _progress("Verifying…", -1);
            var result = await MediaInfo.ProbeAsync(tempOut, ct);
            if (result.VideoCodec != "hevc")
                throw new InvalidOperationException("Output is not HEVC");
            if (Math.Abs(result.Duration - m.Duration) > Math.Max(2, m.Duration * 0.01))
                throw new InvalidOperationException($"Output length {result.Duration:0}s differs from source {m.Duration:0}s");
            if (result.AudioStreams != m.AudioStreams)
                throw new InvalidOperationException($"Output has {result.AudioStreams} audio tracks, source has {m.AudioStreams}");

            // Kept even when it saved little or nothing: every file ends up in the same format.
            long newSize = new FileInfo(tempOut).Length;
            double savings = 1 - newSize / (double)m.Size;

            ct.ThrowIfCancellationRequested();
            await Task.Run(() => SwapIn(job, tempOut, finalPath), CancellationToken.None);
            job.NewSize = newSize;
            job.NewPath = _s.Handling == OriginalHandling.WriteToOutputFolder ? job.Path : finalPath;
            job.State = JobState.Done;
            job.Outcome = Outcome.Done;
            job.Status = savings >= 0 ? $"Saved {savings:P0}" : $"Converted ({-savings:P0} larger)";
            _log($"  done: {Tools.Size(m.Size)} -> {Tools.Size(newSize)} " +
                 (savings >= 0 ? $"({savings:P0} smaller)" : $"({-savings:P0} larger)"));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
        }
    }

    /// <summary>The job's path re-rooted under <paramref name="folder"/>, keeping the library folder's name.</summary>
    static string Mirror(string folder, Job job) =>
        Path.Combine(folder, Path.GetFileName(job.Root.TrimEnd('\\', '/')), Path.GetRelativePath(job.Root, job.Path));

    void SwapIn(Job job, string tempOut, string finalPath)
    {
        switch (_s.Handling)
        {
            case OriginalHandling.WriteToOutputFolder:
                File.Move(tempOut, finalPath);
                break;
            case OriginalHandling.MoveToBackup:
                var backup = Mirror(_s.BackupFolder, job);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                for (int i = 2; File.Exists(backup); i++)
                    backup = Path.Combine(Path.GetDirectoryName(backup)!,
                        $"{Path.GetFileNameWithoutExtension(job.Path)} ({i}){Path.GetExtension(job.Path)}");
                _progress("Moving original to backup…", -1);
                File.Move(job.Path, backup);
                job.BackupPath = backup;
                File.Move(tempOut, finalPath);
                break;
            case OriginalHandling.Delete:
                // Same name: one atomic replace. New extension: put the new file in place before deleting the old.
                File.Move(tempOut, finalPath, overwrite: true);
                if (!finalPath.Equals(job.Path, StringComparison.OrdinalIgnoreCase)) File.Delete(job.Path);
                break;
        }
    }

    // ---------- samples ----------

    /// <param name="Bytes">Source video bytes in the samples; 0 when they had to be re-encoded losslessly.</param>
    sealed record Samples(string Path, double Seconds, long Bytes);

    /// <summary>
    /// Cuts 3-8 short clips spread over the file (skipping the first and last 5%: logos, credits) and joins them.
    /// Stream copy keeps the source bitrate measurable; sources that can't be copied cleanly fall back to lossless FFV1.
    /// </summary>
    async Task<Samples> MakeSamplesAsync(string src, MediaInfo m, string work, CancellationToken ct)
    {
        int n = Math.Clamp((int)Math.Round(m.Duration / 600), 3, 8);
        foreach (bool copy in new[] { true, false })
        {
            _progress(copy ? "Cutting test samples…" : "Cutting test samples (lossless)…", -1);
            var list = new StringBuilder();
            bool ok = true;
            for (int i = 0; i < n && ok; i++)
            {
                double t = Math.Max(0, m.Duration * 0.05 + (i + 0.5) * m.Duration * 0.9 / n - SampleSeconds / 2);
                var seg = Path.Combine(work, $"seg{i}.mkv");
                var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-ss", Str(Math.Round(t, 3)), "-i", src, "-t", Str(SampleSeconds), "-map", $"0:{m.VideoIndex}",
                    "-avoid_negative_ts", "make_zero" };
                args.AddRange(copy ? new[] { "-c:v", "copy" } : new[] { "-c:v", "ffv1" });
                args.Add(seg);
                var r = await Tools.RunAsync(Tools.FFmpeg!, args, ct, _s.LowPriority);
                ok = r.ExitCode == 0 && File.Exists(seg) && new FileInfo(seg).Length > 0;
                list.AppendLine($"file 'seg{i}.mkv'");
            }
            if (!ok) continue;

            var listPath = Path.Combine(work, "list.txt");
            await File.WriteAllTextAsync(listPath, list.ToString(), ct);
            var joined = Path.Combine(work, "samples.mkv");
            var c = await Tools.RunAsync(Tools.FFmpeg!, new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", joined }, ct, _s.LowPriority);
            if (c.ExitCode != 0) continue;

            var info = await MediaInfo.ProbeAsync(joined, ct);
            if (info.Duration < SampleSeconds) continue;
            return new(joined, info.Duration, copy ? new FileInfo(joined).Length : 0);
        }
        throw new InvalidOperationException("Could not cut test samples from the file");
    }

    // ---------- quality search ----------

    /// <param name="Dark">Strong dark-scene protection: mean VMAF of the dark frames with their shadows brightened
    /// (null when off, or when the samples have too few dark frames to matter).</param>
    sealed record Trial(int Q, double Vmaf, double? Dark, long Bytes)
    {
        /// <summary>What has to reach the target: the overall VMAF, and the dark frames' too when they count.</summary>
        public double Score => Dark is double d ? Math.Min(Vmaf, d) : Vmaf;
    }

    /// <summary>
    /// Binary search for the highest CRF / CQ that still reaches the target VMAF (about 5 test encodes),
    /// limited to settings whose sample encode is at most <paramref name="maxBytes"/> (the source's size).
    /// </summary>
    async Task<Trial> SearchAsync(EncoderKind enc, MediaInfo m, Samples samples, string work, double maxBytes, CancellationToken ct)
    {
        var (lo, hi) = Range(enc);
        var tried = new Dictionary<int, Trial>();
        var name = QualityNameFor(enc);

        async Task<Trial> Test(int q)
        {
            if (tried.TryGetValue(q, out var t)) return t;
            _progress($"Testing {name} {q} (test {tried.Count + 1})…", -1);
            var encoded = Path.Combine(work, $"test{q}.mkv");
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                "-i", samples.Path, "-map", "0:v:0" };
            args.AddRange(VideoArgs(enc, q, m));
            args.Add(encoded);
            var r = await Tools.RunAsync(Tools.FFmpeg!, args, ct, _s.LowPriority);
            if (r.ExitCode != 0) throw new InvalidOperationException("Test encode failed: " + r.Tail());

            long bytes = new FileInfo(encoded).Length;
            var (vmaf, dark) = await VmafAsync(encoded, samples.Path, m, work, ct);
            File.Delete(encoded);
            t = tried[q] = new Trial(q, vmaf, dark, bytes);
            _log($"  {name} {q}: VMAF {vmaf:0.00}" + (dark is double d ? $", dark scenes {d:0.00}" : "") +
                 (samples.Bytes > 0 ? $", {bytes / (double)samples.Bytes:P0} of source video" : ""));
            return t;
        }

        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if ((await Test(mid)).Score >= _s.TargetVmaf) lo = mid;
            else hi = mid - 1;
        }
        // Even the lowest value can miss the target on very grainy sources; it's still the best on offer.
        var best = await Test(lo);
        if (best.Bytes <= maxBytes) return best;

        // At the target quality the file would grow (a source that is already small). Every file is still
        // converted, for a consistent library, so take the best quality that is no bigger than the source instead.
        int capLo = best.Q + 1, capHi = Range(enc).Max;
        while (capLo < capHi)
        {
            int mid = (capLo + capHi) / 2;
            if ((await Test(mid)).Bytes <= maxBytes) capHi = mid;
            else capLo = mid + 1;
        }
        var capped = await Test(capLo);
        _log($"  {name} {best.Q} would make the file bigger than the source; using {name} {capped.Q} to keep it no larger");
        return capped;
    }

    static readonly Regex VmafScore = new(@"VMAF score: ([\d.]+)", RegexOptions.Compiled);

    // Strong dark-scene protection. A frame is dark when its average luma (8-bit, black = 16) is below this.
    const double DarkLuma = 48;
    // Dark frames only get their own check when they are at least this share of the samples.
    const double MinDarkShare = 0.05;
    // Brightens shadows (both sides) before the dark frames are scored: VMAF hardly notices damage in dark
    // areas, the way a viewer's eye, adjusted to a dark scene, does.
    const string LiftShadows = "eq=gamma=2.0";
    static readonly Regex YAvg = new(@"YAVG=([\d.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Mean VMAF of the encode against the reference. With strong dark-scene protection, a second pass scores
    /// the frames with their shadows brightened and returns the dark frames' mean as well.
    /// </summary>
    async Task<(double Vmaf, double? Dark)> VmafAsync(string encoded, string reference, MediaInfo m, string work, CancellationToken ct)
    {
        // Renumber both streams' frames identically so the comparison pairs frame n with frame n.
        const string Align = "settb=1/1000,setpts=N*40,format=yuv420p";
        var model = m.Width > 2560 ? "vmaf_4k_v0.6.1" : "vmaf_v0.6.1";
        var refChain = (_filter is null ? "" : _filter + ",") + Align;
        var vmafOpts = $"model=version={model}:n_threads={Environment.ProcessorCount}:n_subsample=2";
        var graph = $"[0:v]{Align}[d];[1:v]{refChain}[r];[d][r]libvmaf={vmafOpts}";
        var r = await Tools.RunAsync(Tools.FFmpeg!, new[] { "-hide_banner", "-nostdin", "-i", encoded, "-i", reference,
            "-lavfi", graph, "-f", "null", "-" }, ct, _s.LowPriority);
        var match = VmafScore.Match(r.StdErr);
        if (r.ExitCode != 0 || !match.Success) throw new InvalidOperationException("VMAF failed: " + r.Tail());
        var vmaf = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (_s.DarkScenes != DarkSceneProtection.Strong) return (vmaf, null);

        // Per-frame scores go to a JSON log; the reference's per-frame brightness (before brightening) to a text file.
        var json = Path.Combine(work, "vmaf-dark.json");
        var luma = Path.Combine(work, "luma.txt");
        File.Delete(json);
        File.Delete(luma);
        graph = $"[0:v]{Align},{LiftShadows}[d];" +
                $"[1:v]{refChain},signalstats,metadata=print:key=lavfi.signalstats.YAVG:file='{FilterPath(luma)}',{LiftShadows}[r];" +
                $"[d][r]libvmaf={vmafOpts}:log_fmt=json:log_path='{FilterPath(json)}'";
        r = await Tools.RunAsync(Tools.FFmpeg!, new[] { "-hide_banner", "-nostdin", "-i", encoded, "-i", reference,
            "-lavfi", graph, "-f", "null", "-" }, ct, _s.LowPriority);
        if (r.ExitCode != 0 || !File.Exists(json) || !File.Exists(luma))
            throw new InvalidOperationException("Dark-scene VMAF failed: " + r.Tail());

        var brightness = YAvg.Matches(await File.ReadAllTextAsync(luma, ct))
            .Select(x => double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(json, ct));
        int scored = 0;
        var dark = new List<double>();
        foreach (var f in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            scored++;
            int n = f.GetProperty("frameNum").GetInt32(); // the source frame's number, also with n_subsample
            if (n < brightness.Count && brightness[n] < DarkLuma)
                dark.Add(f.GetProperty("metrics").GetProperty("vmaf").GetDouble());
        }
        return (vmaf, dark.Count > 0 && dark.Count >= scored * MinDarkShare ? dark.Average() : null);
    }

    /// <summary>A file path as a quoted filter option value: forward slashes, the drive colon escaped.</summary>
    static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "");

    // ---------- full encode ----------

    async Task EncodeAsync(Job job, EncoderKind enc, int q, bool mp4, string output, CancellationToken ct)
    {
        var m = job.Media!;
        var name = QualityNameFor(enc);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
            "-i", job.Path, "-map", $"0:{m.VideoIndex}", "-map", "0:a?" };
        if (mp4)
        {
            // MP4 only carries text subtitles as mov_text.
            for (int i = 0; i < m.SubtitleCodecs.Count; i++)
                if (m.SubtitleCodecs[i] == "mov_text") args.AddRange(new[] { "-map", $"0:{m.SubtitleIndexes[i]}" });
        }
        else
        {
            args.AddRange(new[] { "-map", "0:s?", "-map", "0:t?" });
        }
        args.AddRange(new[] { "-map_metadata", "0", "-map_chapters", "0", "-c", "copy" });
        if (!mp4)
        {
            // MKV can't hold mp4-style mov_text subtitles; convert those to SRT.
            for (int i = 0; i < m.SubtitleCodecs.Count; i++)
                if (m.SubtitleCodecs[i] == "mov_text") args.AddRange(new[] { $"-c:s:{i}", "srt" });
        }
        args.AddRange(VideoArgs(enc, q, m));
        args.AddRange(new[] { "-max_muxing_queue_size", "9999", "-metadata", $"{MediaInfo.OurTag}={name} {q}" });
        if (mp4) args.AddRange(new[] { "-tag:v:0", "hvc1", "-movflags", "+faststart+use_metadata_tags" });
        args.Add(output);

        var started = DateTime.UtcNow;
        string speed = "";
        _progress($"Encoding at {name} {q}…", 0);
        var r = await Tools.RunAsync(Tools.FFmpeg!, args, ct, _s.LowPriority, line =>
        {
            if (line.StartsWith("speed=")) speed = line[6..].Trim();
            else if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && us > 0)
            {
                double frac = Math.Clamp(us / 1e6 / m.Duration, 0, 1);
                var elapsed = DateTime.UtcNow - started;
                var eta = frac > 0.005 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - frac) / frac) : (TimeSpan?)null;
                _progress($"Encoding at {name} {q} · {frac:P1} · {speed}" +
                          (eta is { } e ? $" · {(int)e.TotalHours}:{e:mm\\:ss} left" : ""), frac);
            }
        });
        if (r.ExitCode != 0) throw new InvalidOperationException("Encode failed: " + r.Tail());
    }
}
