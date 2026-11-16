using System.Text.Json;

namespace Any2Hevc;

public enum EncoderKind { X265, Nvenc }

public enum DarkSceneProtection { Off, Protect, Strong }

/// <summary>What happens to the source file once its HEVC copy has been verified.</summary>
public enum OriginalHandling
{
    /// <summary>Replace in the library; the original moves to the backup folder (same sub-folders).</summary>
    MoveToBackup,
    /// <summary>Replace in the library; the original is deleted.</summary>
    Delete,
    /// <summary>Library untouched; the HEVC copy is written to the output folder (same sub-folders).</summary>
    WriteToOutputFolder,
}

public sealed class AppSettings
{
    public List<string> Folders { get; set; } = new();

    public EncoderKind Encoder { get; set; } = EncoderKind.X265;
    public string X265Preset { get; set; } = "slow";
    public string NvencPreset { get; set; } = "p7";
    /// <summary>Mean VMAF the encode must reach on the test samples. 95 is hard to tell apart from the source on a TV.</summary>
    public double TargetVmaf { get; set; } = 95;
    /// <summary>Remove black bars (letterbox / pillarbox) from all four sides; players add them back as needed.</summary>
    public bool CropBlackBars { get; set; } = true;
    /// <summary>Light 5% unsharp mask on the luma, for slightly crisper detail.</summary>
    public bool Sharpen { get; set; }
    /// <summary>
    /// Off; Protect: give dark, flat areas more bits (adaptive quantization) so shadows and night scenes don't band
    /// or block; Strong: also make the dark frames reach the target VMAF, scored with the shadows brightened.
    /// </summary>
    public DarkSceneProtection DarkScenes { get; set; }

    /// <summary>Older settings files: the on/off switch that became <see cref="DarkScenes"/>.</summary>
    public bool ProtectDarkScenes
    {
        get => DarkScenes != DarkSceneProtection.Off;
        set { if (value && DarkScenes == DarkSceneProtection.Off) DarkScenes = DarkSceneProtection.Protect; }
    }

    public OriginalHandling Handling { get; set; } = OriginalHandling.MoveToBackup;
    public string BackupFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";

    /// <summary>Also convert HEVC files made elsewhere, so the whole library ends up in one format.</summary>
    public bool ReencodeHevc { get; set; } = true;
    public bool LowPriority { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    /// <summary>Watch the library folders and encode new files automatically.</summary>
    public bool WatchMode { get; set; }

    public string PlexUrl { get; set; } = "http://localhost:32400";
    public string PlexToken { get; set; } = "";
    public bool RefreshPlex { get; set; }

    // Before Dir: static fields initialise in order and PickDataDir reads this.
    static readonly string[] DataFiles = { "settings.json", "history.json", "log.txt" };

    static readonly string Dir = PickDataDir();
    public static string DataDir => Dir;

    /// <summary>
    /// The exe's own folder, so settings and history travel with it (e.g. survive reinstalling Windows). Falls back to
    /// %APPDATA%\any2hevc when that folder isn't writable (Program Files). ANY2HEVC_DATA overrides both, e.g. for testing.
    /// </summary>
    static string PickDataDir()
    {
        if (Tools.DataOverride is { } custom) return custom;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appData = Path.Combine(roaming, Tools.AppName);
        var oldAppData = Path.Combine(roaming, Tools.OldAppName); // when the app was called PlexHevc
        var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var dir = CanWrite(exeDir) ? exeDir : appData;

        // Earlier versions kept everything in %APPDATA%\PlexHevc: bring it over once.
        foreach (var from in new[] { appData, oldAppData })
        {
            if (from.Equals(dir, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in DataFiles)
            {
                var src = Path.Combine(from, name);
                var dst = Path.Combine(dir, name);
                try
                {
                    if (!File.Exists(src) || File.Exists(dst)) continue;
                    Directory.CreateDirectory(dir);
                    File.Move(src, dst);
                }
                catch { /* left where it was; this file starts fresh */ }
            }
        }
        return dir;
    }

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".any2hevc-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Snapshot taken per file, so edits in the window apply from the next file on.</summary>
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}
