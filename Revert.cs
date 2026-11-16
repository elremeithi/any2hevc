namespace Any2Hevc;

/// <summary>
/// Undoes encodes made in backup mode: deletes the HEVC file and moves the original back from the backup folder.
/// A file is only touched when everything checks out: the converted file is still exactly the one the history
/// recorded, the backup is exactly the original's size, and nothing else sits where the original goes back.
/// </summary>
static class Revert
{
    /// <param name="Converted">The HEVC file in the library.</param>
    /// <param name="Backup">The original in the backup folder.</param>
    /// <param name="Original">Where the original goes back to.</param>
    public sealed record Item(string Converted, string Backup, string Original, long ConvertedSize, long OriginalSize);

    public sealed record Plan(List<Item> Items, List<string> Skipped);

    /// <summary>
    /// Works out what can be reverted (or, with <paramref name="forDelete"/>, which backups can be deleted: then
    /// it doesn't matter whether the original's old place is free). Touches nothing.
    /// </summary>
    public static Plan Prepare(History history, AppSettings s, bool forDelete = false)
    {
        var items = new List<Item>();
        var skipped = new List<string>();
        foreach (var (path, rec) in history.Done())
        {
            var name = Path.GetFileName(path);
            // Output-folder mode: the library file is the untouched source, there is nothing to revert.
            if (rec.NewSize is null || rec.OriginalSize is null || rec.Size != rec.NewSize) continue;

            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                skipped.Add($"{name}: converted file no longer exists");
                continue;
            }
            if (fi.Length != rec.Size || fi.LastWriteTimeUtc != rec.ModifiedUtc)
            {
                skipped.Add($"{name}: file changed since it was converted, left alone");
                continue;
            }
            var backup = rec.BackupPath is { } b && Matches(b, rec.OriginalSize.Value) ? b : FindBackup(path, rec, s);
            if (backup is null)
            {
                skipped.Add($"{name}: no backup found (original size {Tools.Size(rec.OriginalSize.Value)})");
                continue;
            }
            var original = rec.OriginalPath
                ?? Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + Path.GetExtension(backup));
            if (!forDelete && !original.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(original))
            {
                skipped.Add($"{name}: another file is already at {original}");
                continue;
            }
            items.Add(new Item(path, backup, original, fi.Length, rec.OriginalSize.Value));
        }
        return new Plan(items, skipped);
    }

    static bool Matches(string path, long size)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.Length == size;
    }

    /// <summary>
    /// For encodes recorded before backup paths were kept: the original sits in the mirrored folder under the
    /// backup folder, named like the converted file (any extension, maybe with " (2)"), and exactly its size.
    /// </summary>
    static string? FindBackup(string converted, FileRecord rec, AppSettings s)
    {
        if (s.BackupFolder.Length == 0) return null;
        var root = s.Folders.FirstOrDefault(f => converted.StartsWith(Path.TrimEndingDirectorySeparator(f) + "\\", StringComparison.OrdinalIgnoreCase));
        if (root is null) return null;
        var mirrored = Path.Combine(s.BackupFolder, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
            Path.GetRelativePath(root, converted));
        var dir = Path.GetDirectoryName(mirrored)!;
        if (!Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(converted);
        try
        {
            return Directory.EnumerateFiles(dir, stem + "*").FirstOrDefault(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f);
                return (n.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                        System.Text.RegularExpressions.Regex.IsMatch(n, "^" + System.Text.RegularExpressions.Regex.Escape(stem) + @" \(\d+\)$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                       && new FileInfo(f).Length == rec.OriginalSize;
            });
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Puts one original back. The converted file is first renamed aside, so if the original can't be moved
    /// back, it is renamed back and the library is as before.
    /// </summary>
    public static void Run(Item item, string backupRoot)
    {
        var aside = item.Converted + ".any2hevc-revert";
        File.Move(item.Converted, aside);
        try
        {
            File.Move(item.Backup, item.Original);
        }
        catch
        {
            File.Move(aside, item.Converted);
            throw;
        }
        File.Delete(aside);
        RemoveEmptyFolders(Path.GetDirectoryName(item.Backup)!, backupRoot);
    }

    /// <summary>
    /// Deletes one backup for good, keeping the converted file. Checked again right before: the converted file
    /// must still be there and the backup still exactly the original's size.
    /// </summary>
    public static void DeleteBackup(Item item, string backupRoot)
    {
        if (!File.Exists(item.Converted)) throw new IOException("the converted file is gone, backup kept");
        if (!Matches(item.Backup, item.OriginalSize)) throw new IOException("the backup changed, kept");
        File.Delete(item.Backup);
        RemoveEmptyFolders(Path.GetDirectoryName(item.Backup)!, backupRoot);
    }

    /// <summary>Removes folders emptied in the backup tree, up to (not including) the backup folder itself.</summary>
    static void RemoveEmptyFolders(string dir, string backupRoot)
    {
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupRoot));
        try
        {
            for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            {
                if (Path.TrimEndingDirectorySeparator(d.FullName).Equals(stop, StringComparison.OrdinalIgnoreCase)) break;
                if (!d.FullName.StartsWith(stop + "\\", StringComparison.OrdinalIgnoreCase)) break;
                if (d.EnumerateFileSystemInfos().Any()) break;
                d.Delete();
            }
        }
        catch { /* leftover empty folders are harmless */ }
    }
}
