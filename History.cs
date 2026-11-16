using System.Text.Json;

namespace PlexHevc;

public enum Outcome
{
    /// <summary>Re-encoded. Keyed by the file now in the library (the HEVC one, unless writing to an output folder).</summary>
    Done,
    /// <summary>No longer produced (every file is converted now); old records are put back in the queue.</summary>
    NotWorthIt,
    /// <summary>Skipped by the user from the list.</summary>
    UserSkipped,
    Failed,
}

/// <summary>One file as last seen. Size + modified time tell whether it changed since.</summary>
public sealed class FileRecord
{
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public MediaInfo? Media { get; set; }
    public Outcome? Outcome { get; set; }
    public string? Note { get; set; }
    public int? Quality { get; set; }
    public double? Vmaf { get; set; }
    /// <summary>Picture size after removing black bars, if any.</summary>
    public string? Crop { get; set; }
    /// <summary>Source and HEVC sizes (Done only).</summary>
    public long? OriginalSize { get; set; }
    public long? NewSize { get; set; }
    /// <summary>When the encode finished (Done only).</summary>
    public DateTime? FinishedUtc { get; set; }
    /// <summary>Where the source was, and where it was moved to in backup mode (Done only; Revert uses them).</summary>
    public string? OriginalPath { get; set; }
    public string? BackupPath { get; set; }
}

/// <summary>Probe cache and results of every file processed, so rescans are quick and work is never repeated.</summary>
public sealed class History
{
    public Dictionary<string, FileRecord> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    static readonly string FilePath = Path.Combine(AppSettings.DataDir, "history.json");
    readonly object _lock = new();

    /// <summary>The record for this exact file version, or null if new or changed since.</summary>
    public FileRecord? Get(string path, FileInfo fi)
    {
        lock (_lock)
            return Files.TryGetValue(path, out var r) && r.Size == fi.Length && r.ModifiedUtc == fi.LastWriteTimeUtc ? r : null;
    }

    public FileRecord Put(string path, FileRecord record)
    {
        var fi = new FileInfo(path);
        record.Size = fi.Length;
        record.ModifiedUtc = fi.LastWriteTimeUtc;
        lock (_lock) Files[path] = record;
        return record;
    }

    public void Remove(string path)
    {
        lock (_lock) Files.Remove(path);
    }

    public (int Files, long Saved) Savings()
    {
        lock (_lock)
        {
            var done = Files.Values.Where(r => r.Outcome == PlexHevc.Outcome.Done && r.OriginalSize is not null && r.NewSize is not null).ToList();
            return (done.Count, done.Sum(r => r.OriginalSize!.Value - r.NewSize!.Value));
        }
    }

    /// <summary>Snapshot of every finished encode, keyed by the converted file's path.</summary>
    public List<(string Path, FileRecord Record)> Done()
    {
        lock (_lock)
            return Files.Where(f => f.Value.Outcome == PlexHevc.Outcome.Done).Select(f => (f.Key, f.Value)).ToList();
    }

    public static History Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var h = JsonSerializer.Deserialize<History>(File.ReadAllText(FilePath)) ?? new();
                h.Files = new(h.Files, StringComparer.OrdinalIgnoreCase);
                return h;
            }
        }
        catch { /* corrupt file: start over, files just get re-probed */ }
        return new();
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
