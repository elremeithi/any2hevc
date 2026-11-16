using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PlexHevc;

/// <summary>Finds and runs ffmpeg / ffprobe, plus a few Windows helpers.</summary>
static class Tools
{
    public static string? FFmpeg { get; private set; }
    public static string? FFprobe { get; private set; }

    /// <summary>
    /// Locates ffmpeg / ffprobe: copies next to the exe win (a newer build dropped in by hand), then the ones
    /// embedded in the exe, then PATH. Call once at startup.
    /// </summary>
    public static void Prepare()
    {
        string? unpacked = null;
        try { unpacked = UnpackEmbedded(); }
        catch { /* read-only profile or disk full: fall back to PATH */ }
        FFmpeg = Find("ffmpeg.exe", unpacked);
        FFprobe = Find("ffprobe.exe", unpacked);
    }

    /// <summary>
    /// Brotli-compressed ffmpeg.exe / ffprobe.exe ship inside the exe (see the csproj). They are unpacked once per
    /// app build to %LOCALAPPDATA%\PlexHevc\ffmpeg\&lt;build id&gt;, and older builds' copies are removed.
    /// </summary>
    static string? UnpackEmbedded()
    {
        var asm = typeof(Tools).Assembly;
        var names = asm.GetManifestResourceNames().Where(n => n.EndsWith(".br")).ToList();
        if (names.Count == 0) return null; // built without them

        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlexHevc", "ffmpeg");
        var dir = Path.Combine(parent, asm.ManifestModule.ModuleVersionId.ToString("N"));
        foreach (var name in names)
        {
            var target = Path.Combine(dir, name[..^3]);
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(dir);
            // Unpack to a private name and rename, so a second instance never runs a half-written exe.
            var tmp = $"{target}.{Environment.ProcessId}.tmp";
            using (var src = new System.IO.Compression.BrotliStream(asm.GetManifestResourceStream(name)!,
                       System.IO.Compression.CompressionMode.Decompress))
            using (var dst = File.Create(tmp))
                src.CopyTo(dst);
            try { File.Move(tmp, target); }
            catch (IOException) when (File.Exists(target)) { File.Delete(tmp); } // another instance won
        }

        foreach (var old in Directory.EnumerateDirectories(parent))
            if (!old.Equals(dir, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(old, true); } catch { /* in use by an older copy still running */ }
        return dir;
    }

    static string? Find(string exe, string? unpacked)
    {
        var local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;
        if (unpacked is not null && File.Exists(Path.Combine(unpacked, exe))) return Path.Combine(unpacked, exe);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    public sealed record RunResult(int ExitCode, string StdOut, string StdErr)
    {
        /// <summary>Last few stderr lines, for error messages.</summary>
        public string Tail(int lines = 4) =>
            string.Join(" | ", StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));
    }

    /// <summary>Runs a tool to completion. Cancelling kills it (and waits so its files are unlocked).</summary>
    public static async Task<RunResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct,
        bool lowPriority = false, Action<string>? onStdOut = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        var output = new StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (err)
            {
                err.AppendLine(e.Data);
                if (err.Length > 200_000) err.Remove(0, err.Length - 100_000);
            }
        };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (onStdOut is not null) onStdOut(e.Data);
            else lock (output) output.AppendLine(e.Data);
        };

        p.Start();
        if (lowPriority)
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* already exited */ }
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(10_000); } catch { }
            throw;
        }
        p.WaitForExit(); // drains the async readers
        return new(p.ExitCode, output.ToString(), err.ToString());
    }

    /// <summary>
    /// Keeps a Start menu shortcut pointing at this exe, so the app shows up in Start search and can be pinned to the
    /// taskbar from there (Windows lets only the user pin; an exe on its own offers no "Pin to taskbar"). Updated
    /// when the exe moves. Test copies (PLEXHEVC_DATA) leave it alone.
    /// </summary>
    public static void EnsureStartMenuShortcut()
    {
        if (Environment.GetEnvironmentVariable("PLEXHEVC_DATA") is { Length: > 0 }) return;
        try
        {
            var exe = Environment.ProcessPath!;
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Plex HEVC Re-encoder.lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(lnk);
            if (File.Exists(lnk) && string.Equals((string)link.TargetPath, exe, StringComparison.OrdinalIgnoreCase)) return;
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Re-encode a Plex library to HEVC";
            link.Save();
        }
        catch { /* no Start menu entry; the app works the same */ }
    }

    static Task<bool>? _nvenc;

    /// <summary>Whether this PC can encode with NVENC (NVIDIA GPU and driver present). Tested once, with a tiny encode.</summary>
    public static Task<bool> NvencWorksAsync() => _nvenc ??= Task.Run(async () =>
    {
        try
        {
            var r = await RunAsync(FFmpeg!, new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi",
                "-i", "color=s=256x256:d=0.2", "-c:v", "hevc_nvenc", "-f", "null", "-" }, CancellationToken.None);
            return r.ExitCode == 0;
        }
        catch { return false; }
    });

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForUser, out ulong total, out ulong totalFree);

    /// <summary>Free bytes on the volume holding <paramref name="dir"/> (works for UNC shares); null if unknown.</summary>
    public static long? FreeSpace(string dir)
    {
        if (!dir.EndsWith('\\')) dir += '\\';
        return GetDiskFreeSpaceEx(dir, out var free, out _, out _) ? (long)free : null;
    }

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Stops Windows sleeping while encoding (display may still turn off). Call on the UI thread.</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? 0x80000001u /* CONTINUOUS | SYSTEM_REQUIRED */ : 0x80000000u);

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.00} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        _ => $"{bytes / (double)(1L << 20):0} MB",
    };
}
