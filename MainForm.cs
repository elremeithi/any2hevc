namespace PlexHevc;

public sealed partial class MainForm : Form
{
    readonly AppSettings _settings;
    readonly History _history;
    readonly List<Job> _jobs = new();
    CancellationTokenSource? _cts;
    bool _running;
    bool _stopAfterCurrent;
    bool _closeWhenStopped;

    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".flv", ".webm", ".divx" };
    static readonly string[] X265Presets = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
    static readonly string[] NvencPresets = { "p1", "p2", "p3", "p4", "p5", "p6", "p7" };

    static readonly (string Text, EncoderKind Value)[] EncoderChoices =
    {
        ("x265 (CPU): smallest files, slow", EncoderKind.X265),
        ("NVENC (GPU): ~10x faster, files ~15% larger", EncoderKind.Nvenc),
    };

    static readonly (string Text, OriginalHandling Value)[] HandlingChoices =
    {
        ("Replace in library, move originals to backup folder", OriginalHandling.MoveToBackup),
        ("Replace in library, delete originals", OriginalHandling.Delete),
        ("Leave library alone, write HEVC copies to output folder", OriginalHandling.WriteToOutputFolder),
    };

    static readonly string LogPath = Path.Combine(AppSettings.DataDir, "log.txt");

    readonly ListBox _folders = new() { Height = 80, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, IntegralHeight = false };
    readonly ComboBox _encoder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    readonly NumericUpDown _vmaf = new() { Minimum = 85, Maximum = 99, DecimalPlaces = 1, Increment = 0.5m, Width = 65 };
    readonly ComboBox _handling = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    readonly Label _handlingFolderLabel = Caption("");
    readonly TextBox _handlingFolder = new() { Width = 340 };
    readonly Button _browse = new() { Text = "Browse…", AutoSize = true };
    readonly CheckBox _cropBars = new() { Text = "Crop black bars (all 4 sides)", AutoSize = true, Margin = new Padding(14, 5, 3, 0) };
    readonly CheckBox _sharpen = new() { Text = "Sharpen 5%", AutoSize = true, Margin = new Padding(10, 5, 3, 0) };
    readonly ComboBox _darkScenes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Margin = new Padding(3, 3, 10, 3) };
    static readonly (string Text, DarkSceneProtection Value)[] DarkSceneChoices =
    {
        ("Off", DarkSceneProtection.Off), ("Protect", DarkSceneProtection.Protect), ("Strong", DarkSceneProtection.Strong),
    };
    readonly CheckBox _reencodeHevc = new() { Text = "Re-encode HEVC files from other sources too", AutoSize = true };
    readonly CheckBox _lowPriority = new() { Text = "Low CPU priority", AutoSize = true };
    readonly CheckBox _keepAwake = new() { Text = "Keep PC awake", AutoSize = true };
    readonly TextBox _plexUrl = new() { Width = 220 };
    readonly TextBox _plexToken = new() { Width = 200, UseSystemPasswordChar = true };
    readonly CheckBox _refreshPlex = new() { Text = "Rescan Plex libraries when the queue finishes", AutoSize = true };

    readonly Button _scan = new() { Text = "Scan library", AutoSize = true };
    readonly Button _start = new() { Text = "Start", AutoSize = true };
    readonly Button _stopAfter = new() { Text = "Stop after this file", AutoSize = true, Enabled = false };
    readonly Button _stopNow = new() { Text = "Stop now", AutoSize = true, Enabled = false };
    readonly Button _revert = new() { Text = "Revert…", AutoSize = true, Margin = new Padding(14, 3, 3, 3) };
    readonly Button _deleteBackups = new() { Text = "Delete backups…", AutoSize = true };
    readonly CheckBox _watch = new() { Text = "Watch for new files", AutoSize = true, Margin = new Padding(14, 7, 3, 0) };
    /// <summary>Status line at the bottom left: file counts, space saved, watch mode.</summary>
    readonly Label _summary = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 0) };

    // ---------- watch mode state ----------
    readonly List<FileSystemWatcher> _watchers = new();
    /// <summary>Files seen arriving: last observed size and time, and since when they have been unchanged.</summary>
    readonly Dictionary<string, (long Size, DateTime Modified, DateTime Since)> _arrivals = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer _watchTimer = new() { Interval = 15_000 };
    DateTime _lastSweep;
    bool _scanning, _scannedOnce, _watchBusy, _restoringWatch;
    /// <summary>Set by Stop (or a setup problem) so watch mode doesn't restart the queue until a new file arrives.</summary>
    bool _autoRunHeld;
    /// <summary>A new file must stay unchanged this long (and not be open for writing) before it's encoded.</summary>
    static readonly TimeSpan Settle = TimeSpan.FromMinutes(1);
    /// <summary>Full re-check of the folders, for events the watchers miss (common on network shares).</summary>
    static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);

    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, ShowItemToolTips = true,
    };
    readonly Label _current = new() { AutoSize = true, Text = "Add your Plex library folders (or drag them onto this window), then Scan.", Margin = new Padding(3, 6, 3, 3) };
    readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 14 };
    readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f), WordWrap = false,
    };
    readonly ToolTip _tips = new() { AutoPopDelay = 20000 };

    public MainForm(AppSettings settings, History history, string? startupCommand = null)
    {
        _settings = settings;
        _history = history;

        Text = "Plex HEVC Re-encoder";
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1180, 860);
        MinimumSize = new Size(950, 650);
        Padding = new Padding(10);
        Icon = LoadAppIcon();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildFolders(), 0, 0);
        root.Controls.Add(BuildSettings(), 0, 1);
        root.Controls.Add(BuildButtons(), 0, 2);
        root.Controls.Add(BuildList(), 0, 3);
        root.Controls.Add(BuildProgress(), 0, 4);
        root.Controls.Add(_log, 0, 5);
        root.Controls.Add(_summary, 0, 6);
        Controls.Add(root);
        EnableFolderDrop(this);

        LoadSettingsIntoControls();
        DarkTheme.Apply(this);
        DarkTheme.Apply(_list.ContextMenuStrip!);
        HandleCreated += (_, _) => DarkTheme.ApplyTitleBar(this);
        _watchTimer.Tick += async (_, _) => await WatchTickAsync();
        Shown += async (_, _) =>
        {
            UpdateSummary();
            if (_settings.Folders.Count > 0) await ScanAsync();
            if (_settings.WatchMode)
            {
                _restoringWatch = true;
                _watch.Checked = true;
                _restoringWatch = false;
            }
            UpdateJumpList();
            if (startupCommand is not null) await RunCommandAsync(startupCommand);
        };
        FormClosing += OnClosing;
        SetupTray();
    }

    // ---------- layout ----------

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(10, 7, 2, 0) };

    static FlowLayoutPanel Line(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 1, 0, 1) };
        p.Controls.AddRange(controls);
        return p;
    }

    static GroupBox Group(string title, Control content)
    {
        content.Dock = DockStyle.Top;
        var g = new GroupBox
        {
            Text = title, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 6),
        };
        g.Controls.Add(content);
        return g;
    }

    Control BuildFolders()
    {
        var add = new Button { Text = "Add folder…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        add.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Pick a Plex library folder (movies, TV…)", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddFolders(new[] { dlg.SelectedPath });
        };
        remove.Click += (_, _) =>
        {
            if (_folders.SelectedItem is not string f) return;
            _settings.Folders.Remove(f);
            _folders.Items.Remove(f);
            _settings.Save();
            if (_settings.WatchMode) StartWatching();
        };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { add, remove });

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.Controls.Add(_folders, 0, 0);
        t.Controls.Add(buttons, 1, 0);
        _folderButtons = new[] { add, remove };
        return Group("Library folders (local or \\\\server\\share paths, searched with all sub-folders; drag folders here from Explorer)", t);
    }

    Button[] _folderButtons = Array.Empty<Button>();

    void AddFolders(IEnumerable<string> paths)
    {
        bool added = false;
        foreach (var raw in paths)
        {
            var path = raw.Length > 3 ? raw.TrimEnd('\\', '/') : raw; // keep "D:\"
            if (_settings.Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            _settings.Folders.Add(path);
            _folders.Items.Add(path);
            added = true;
        }
        if (!added) return;
        _settings.Save();
        if (_settings.WatchMode) StartWatching();
    }

    /// <summary>Folders dragged from Explorer onto any part of the window are added as library folders.</summary>
    void EnableFolderDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += (_, e) => e.Effect = DroppedFolders(e).Count > 0 && _folderButtons[0].Enabled ? DragDropEffects.Link : DragDropEffects.None;
        c.DragDrop += (_, e) =>
        {
            if (_folderButtons[0].Enabled) AddFolders(DroppedFolders(e));
        };
        foreach (Control child in c.Controls) EnableFolderDrop(child);
    }

    static List<string> DroppedFolders(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] items ? items.Where(Directory.Exists).ToList() : new();

    Control BuildSettings()
    {
        var plexTest = new Button { Text = "Test", AutoSize = true };
        plexTest.Click += async (_, _) =>
        {
            try
            {
                var libs = await Plex.LibrariesAsync(_settings.PlexUrl, _settings.PlexToken);
                MessageBox.Show(this, "Connected. Libraries: " + string.Join(", ", libs), "Plex");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not reach Plex: " + ex.Message, "Plex", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var vmafCaption = Caption("Target VMAF:");
        var presetCaption = Caption("Speed preset:");
        _tips.SetToolTip(_vmaf, "Average VMAF the test encodes must reach; the app picks the smallest settings that do.\n" +
            "93 = good, 95 = hard to tell from the source (default), 97 = practically identical. Higher = bigger files.");
        _tips.SetToolTip(vmafCaption, _tips.GetToolTip(_vmaf));
        _tips.SetToolTip(_preset, "x265: slower presets make smaller files at the same quality but take longer; slow is a good balance.\n" +
            "NVENC: p7 is the best quality per bit.");
        _tips.SetToolTip(presetCaption, _tips.GetToolTip(_preset));
        _tips.SetToolTip(_cropBars, "Detects letterbox / pillarbox bars at 12 points through each file and crops them off.\n" +
            "Only bars that are black in every checked scene are removed. Players add bars back automatically.");
        _tips.SetToolTip(_sharpen, "Applies a light 5% unsharp mask to brightness detail (not colour) during the encode.");
        _tips.SetToolTip(_darkScenes, "Shadows and night scenes are where banding and blocking show most, and VMAF under-rates them there.\n\n" +
            "Protect: moves bits from bright, detailed areas to dark, flat ones (x265 aq-mode 3; NVENC stronger\n" +
            "adaptive quantization). Files come out a few percent bigger.\n\n" +
            "Strong: as Protect, and the dark frames of the test samples must reach the target VMAF on their own,\n" +
            "scored with their shadows brightened so the damage counts. Files with many dark scenes get a\n" +
            "higher-quality setting and come out noticeably bigger; mostly bright files are unaffected.");
        _tips.SetToolTip(_reencodeHevc, "HEVC files made elsewhere (remuxes, web rips, 10-bit encodes) are converted like every other file,\n" +
            "so the whole library ends up in the same format. Files this app made are never re-encoded.\n" +
            "Takes effect on the next scan.");
        _tips.SetToolTip(_plexToken, "Your X-Plex-Token (Plex Web: open any item > Get Info > View XML, it's at the end of the URL).");

        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        panel.Controls.AddRange(new Control[]
        {
            Line(Caption("Encoder:"), _encoder, presetCaption, _preset, vmafCaption, _vmaf, _cropBars, _sharpen),
            Line(Caption("Originals:"), _handling, _handlingFolderLabel, _handlingFolder, _browse),
            Line(Caption("Dark scenes:"), _darkScenes, _reencodeHevc, _lowPriority, _keepAwake),
            Line(Caption("Plex URL:"), _plexUrl, Caption("Token:"), _plexToken, plexTest, _refreshPlex),
        });
        return Group("Settings", panel);
    }

    static Control Spacer() => new Label { Width = 4, Margin = Padding.Empty };

    Control BuildButtons()
    {
        _scan.Click += async (_, _) => await ScanAsync();
        _start.Click += async (_, _) => await RunAsync();
        _stopAfter.Click += (_, _) =>
        {
            _stopAfterCurrent = true;
            _autoRunHeld = true;
            _stopAfter.Enabled = false;
            _stopAfter.Text = "Stopping after this file…";
        };
        _stopNow.Click += (_, _) =>
        {
            _autoRunHeld = true;
            _cts?.Cancel();
        };
        _watch.CheckedChanged += async (_, _) => await SetWatchingAsync(_watch.Checked);
        _tips.SetToolTip(_watch, "Watches the library folders (and re-checks them every 10 minutes) and encodes new or\n" +
            "replaced files automatically, once they have stopped changing for a minute. Also works through any\n" +
            "files already waiting. Stop pauses it until the next new file arrives; untick to switch it off.");
        _revert.Click += async (_, _) => await RevertAsync();
        _tips.SetToolTip(_revert, "Undo the encodes: deletes the converted files and moves the originals back from the backup folder.\n" +
            "Asks first and shows what it will do.");
        _deleteBackups.Click += async (_, _) => await DeleteBackupsAsync();
        _tips.SetToolTip(_deleteBackups, "Keep the converted files and permanently delete their originals from the backup folder,\n" +
            "to free the space. Asks twice. Revert is no longer possible for those files afterwards.");
        return Line(_scan, _start, _stopAfter, _stopNow, _watch, _revert, _deleteBackups);
    }

    Control BuildList()
    {
        _list.Columns.Add("File", 370);
        _list.Columns.Add("Codec", 85);
        _list.Columns.Add("Resolution", 150);
        _list.Columns.Add("Size", 80);
        _list.Columns.Add("Setting", 70);
        _list.Columns.Add("VMAF", 55);
        _list.Columns.Add("New size", 80);
        _list.Columns.Add("Finished", 125);
        _list.Columns.Add("Status", 260);
        _list.ColumnClick += (_, e) =>
        {
            if (e.Column is not (ColFile or ColFinished)) return;
            // Each click: ascending, descending, then back to queue order.
            if (e.Column != _sortCol) (_sortCol, _sortDesc) = (e.Column, false);
            else if (!_sortDesc) _sortDesc = true;
            else _sortCol = -1;
            ApplySort();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Skip (never encode)", null, (_, _) => SkipSelected());
        menu.Items.Add("Queue again", null, (_, _) => RetrySelected());
        menu.Items.Add("Open folder", null, (_, _) =>
        {
            if (SelectedJobs().FirstOrDefault() is { } j)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{(File.Exists(j.Path) ? j.Path : j.NewPath ?? j.Path)}\"");
        });
        _list.ContextMenuStrip = menu;
        return _list;
    }

    Control BuildProgress()
    {
        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.Controls.Add(_current, 0, 0);
        t.Controls.Add(_progress, 0, 1);
        return t;
    }

    // ---------- settings ----------

    void LoadSettingsIntoControls()
    {
        var s = _settings;
        foreach (var f in s.Folders) _folders.Items.Add(f);
        _encoder.Items.AddRange(EncoderChoices.Select(c => (object)c.Text).ToArray());
        _encoder.SelectedIndex = Array.FindIndex(EncoderChoices, c => c.Value == s.Encoder);
        FillPresets();
        _vmaf.Value = Math.Clamp((decimal)s.TargetVmaf, _vmaf.Minimum, _vmaf.Maximum);
        _handling.Items.AddRange(HandlingChoices.Select(c => (object)c.Text).ToArray());
        _handling.SelectedIndex = Array.FindIndex(HandlingChoices, c => c.Value == s.Handling);
        ShowHandlingFolder();
        _cropBars.Checked = s.CropBlackBars;
        _sharpen.Checked = s.Sharpen;
        _darkScenes.Items.AddRange(DarkSceneChoices.Select(c => (object)c.Text).ToArray());
        _darkScenes.SelectedIndex = Math.Max(0, Array.FindIndex(DarkSceneChoices, c => c.Value == s.DarkScenes));
        _reencodeHevc.Checked = s.ReencodeHevc;
        _lowPriority.Checked = s.LowPriority;
        _keepAwake.Checked = s.KeepAwake;
        _plexUrl.Text = s.PlexUrl;
        _plexToken.Text = s.PlexToken;
        _refreshPlex.Checked = s.RefreshPlex;

        // Wired after loading so the initial values don't count as edits.
        _encoder.SelectedIndexChanged += (_, _) =>
        {
            s.Encoder = EncoderChoices[_encoder.SelectedIndex].Value;
            FillPresets();
            s.Save();
        };
        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (s.Encoder == EncoderKind.X265) s.X265Preset = (string)_preset.SelectedItem!;
            else s.NvencPreset = (string)_preset.SelectedItem!;
            s.Save();
        };
        _vmaf.ValueChanged += (_, _) => { s.TargetVmaf = (double)_vmaf.Value; s.Save(); };
        _handling.SelectedIndexChanged += (_, _) =>
        {
            s.Handling = HandlingChoices[_handling.SelectedIndex].Value;
            ShowHandlingFolder();
            s.Save();
        };
        _handlingFolder.TextChanged += (_, _) =>
        {
            if (s.Handling == OriginalHandling.MoveToBackup) s.BackupFolder = _handlingFolder.Text.Trim();
            else if (s.Handling == OriginalHandling.WriteToOutputFolder) s.OutputFolder = _handlingFolder.Text.Trim();
            s.Save();
        };
        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _handlingFolder.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _handlingFolder.Text = dlg.SelectedPath;
        };
        _cropBars.CheckedChanged += (_, _) => { s.CropBlackBars = _cropBars.Checked; s.Save(); };
        _sharpen.CheckedChanged += (_, _) => { s.Sharpen = _sharpen.Checked; s.Save(); };
        _darkScenes.SelectedIndexChanged += (_, _) => { s.DarkScenes = DarkSceneChoices[_darkScenes.SelectedIndex].Value; s.Save(); };
        _reencodeHevc.CheckedChanged += (_, _) => { s.ReencodeHevc = _reencodeHevc.Checked; s.Save(); };
        _lowPriority.CheckedChanged += (_, _) => { s.LowPriority = _lowPriority.Checked; s.Save(); };
        _keepAwake.CheckedChanged += (_, _) => { s.KeepAwake = _keepAwake.Checked; s.Save(); };
        _plexUrl.TextChanged += (_, _) => { s.PlexUrl = _plexUrl.Text.Trim(); s.Save(); };
        _plexToken.TextChanged += (_, _) => { s.PlexToken = _plexToken.Text.Trim(); s.Save(); };
        _refreshPlex.CheckedChanged += (_, _) => { s.RefreshPlex = _refreshPlex.Checked; s.Save(); };
    }

    void FillPresets()
    {
        bool x265 = _settings.Encoder == EncoderKind.X265;
        _preset.Items.Clear();
        _preset.Items.AddRange(x265 ? X265Presets : NvencPresets);
        _preset.SelectedItem = x265 ? _settings.X265Preset : _settings.NvencPreset;
        if (_preset.SelectedIndex < 0) _preset.SelectedItem = x265 ? "slow" : "p7";
    }

    void ShowHandlingFolder()
    {
        var h = _settings.Handling;
        bool visible = h != OriginalHandling.Delete;
        _handlingFolderLabel.Visible = _handlingFolder.Visible = _browse.Visible = visible;
        _handlingFolderLabel.Text = h == OriginalHandling.MoveToBackup ? "Backup folder:" : "Output folder:";
        _handlingFolder.Text = h == OriginalHandling.MoveToBackup ? _settings.BackupFolder : _settings.OutputFolder;
    }

    // ---------- scan ----------

    async Task ScanAsync()
    {
        if (_running || _watchBusy) return;
        if (_settings.Folders.Count == 0)
        {
            MessageBox.Show(this, "Add at least one library folder first.", Text);
            return;
        }
        SetBusy(true);
        _scanning = true;
        _jobs.Clear();
        _list.Items.Clear();
        var s = _settings.Clone();
        try
        {
            _current.Text = "Finding video files…";
            _progress.Style = ProgressBarStyle.Marquee;
            foreach (var f in s.Folders.Where(f => !Directory.Exists(f))) Log($"Folder not reachable: {f}");

            var jobs = await Task.Run(() => VideoFiles(s)
                .Select(v => new Job { Path = v.File.FullName, Root = v.Root })
                .DistinctBy(j => j.Path, StringComparer.OrdinalIgnoreCase)
                .ToList());

            int read = 0;
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (job, ct) =>
            {
                try
                {
                    var rec = _history.Get(job.Path, new FileInfo(job.Path));
                    if (rec?.Media is null)
                        rec = _history.Put(job.Path, new FileRecord { Media = await MediaInfo.ProbeAsync(job.Path, ct) });
                    job.Media = rec.Media;
                    Classify(job, rec, s);
                }
                catch (Exception ex)
                {
                    job.State = JobState.Failed;
                    job.Status = "Can't read: " + ex.Message;
                }
                int n = Interlocked.Increment(ref read);
                if (n % 10 == 0) BeginInvoke(() => _current.Text = $"Reading files {n} / {jobs.Count}…");
            });
            _history.Save();

            // Queue order: biggest files first, they save the most.
            _jobs.AddRange(jobs.OrderBy(j => j.State == JobState.Pending ? 0 : 1).ThenByDescending(j => j.Media?.Size ?? 0));
            _list.BeginUpdate();
            foreach (var j in _jobs)
            {
                j.Item = NewItem(j);
                UpdateItem(j);
            }
            _list.Items.AddRange(_jobs.Select(j => j.Item!).ToArray());
            _list.EndUpdate();
            _current.Text = $"Found {_jobs.Count} video files.";
            Log($"Scan: {_jobs.Count} files, {_jobs.Count(j => j.State == JobState.Pending)} to encode");
            _scannedOnce = true;
        }
        catch (Exception ex)
        {
            _current.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _scanning = false;
            SetBusy(false);
            UpdateSummary();
        }
    }

    /// <summary>Every video file under the library folders, minus the backup / output folders and our temp files.</summary>
    static IEnumerable<(string Root, FileInfo File)> VideoFiles(AppSettings s)
    {
        var excluded = ExcludedFolders(s);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in s.Folders.Where(Directory.Exists))
            foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", options))
                if (IsLibraryVideo(fi.FullName, excluded)) yield return (root, fi);
    }

    static List<string> ExcludedFolders(AppSettings s) =>
        new[] { s.BackupFolder, s.OutputFolder }.Where(f => f.Length > 0).ToList();

    static bool IsLibraryVideo(string path, List<string> excluded) =>
        VideoExtensions.Contains(Path.GetExtension(path))
        && !path.Contains(".plexhevc-tmp", StringComparison.OrdinalIgnoreCase)
        && !excluded.Any(e => IsUnder(path, e));

    static bool IsUnder(string path, string folder)
    {
        try
        {
            var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return path.StartsWith(f.EndsWith('\\') ? f : f + '\\', StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // malformed folder setting
        }
    }

    // ---------- watch mode ----------

    async Task SetWatchingAsync(bool on)
    {
        if (on && !_restoringWatch && _settings.Handling == OriginalHandling.Delete && !ConfirmDelete())
        {
            _watch.Checked = false;
            return;
        }
        _settings.WatchMode = on;
        _settings.Save();
        UpdateJumpList();
        if (!on)
        {
            if (_watchers.Count > 0) Log("Watch mode off.");
            StopWatching();
            UpdateSummary();
            return;
        }
        if (!_scannedOnce && _settings.Folders.Count > 0) await ScanAsync();
        StartWatching();
        _autoRunHeld = false;
        UpdateSummary();
        await WatchTickAsync();
    }

    void StartWatching()
    {
        StopWatching();
        foreach (var folder in _settings.Folders.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    SynchronizingObject = this, // events arrive on the UI thread
                };
                w.Created += (_, e) => NoteArrival(e.FullPath);
                w.Changed += (_, e) => NoteArrival(e.FullPath);
                w.Renamed += (_, e) => NoteArrival(e.FullPath);
                w.Error += (_, e) =>
                {
                    Log($"Watcher error on {folder} ({e.GetException().Message}); re-checking the folders.");
                    _lastSweep = DateTime.MinValue;
                };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Log($"Can't watch {folder}: {ex.Message}");
            }
        }
        _lastSweep = DateTime.UtcNow;
        _watchTimer.Start();
        Log($"Watching {_watchers.Count} folder(s) for new files.");
    }

    void StopWatching()
    {
        _watchTimer.Stop();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _arrivals.Clear();
    }

    void NoteArrival(string path)
    {
        if (!IsLibraryVideo(path, ExcludedFolders(_settings))) return;
        if (_jobs.Any(j => j.State == JobState.Working && j.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        // Any change restarts the settle clock.
        _arrivals[path] = (-1, default, DateTime.UtcNow);
    }

    /// <summary>
    /// Every 15 s: queues arrivals that have settled, and starts the queue if it isn't running.
    /// </summary>
    async Task WatchTickAsync()
    {
        if (!_settings.WatchMode || _scanning || _watchBusy) return;
        _watchBusy = true;
        try
        {
            if (DateTime.UtcNow - _lastSweep > SweepEvery) await SweepAsync();

            var now = DateTime.UtcNow;
            var ready = new List<string>();
            foreach (var (path, seen) in _arrivals.ToList())
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) _arrivals.Remove(path);
                else if (fi.Length != seen.Size || fi.LastWriteTimeUtc != seen.Modified)
                    _arrivals[path] = (fi.Length, fi.LastWriteTimeUtc, now);
                else if (now - seen.Since >= Settle)
                {
                    if (IsBeingWritten(path)) _arrivals[path] = (fi.Length, fi.LastWriteTimeUtc, now);
                    else
                    {
                        _arrivals.Remove(path);
                        ready.Add(path);
                    }
                }
            }
            foreach (var path in ready) await AddArrivalAsync(path);

            if (!_running && !_autoRunHeld && _jobs.Any(j => j.State == JobState.Pending))
                _ = RunAsync(unattended: true);
        }
        catch (Exception ex)
        {
            Log("Watch: " + ex.Message);
        }
        finally
        {
            _watchBusy = false;
        }
    }

    /// <summary>Notes every video that isn't in the list yet, or changed since it was listed.</summary>
    async Task SweepAsync()
    {
        _lastSweep = DateTime.UtcNow;
        var s = _settings.Clone();
        var known = new HashSet<string>(_jobs.Select(j => j.Path).Concat(_jobs.Select(j => j.NewPath).OfType<string>()),
            StringComparer.OrdinalIgnoreCase);
        var found = await Task.Run(() => VideoFiles(s)
            .Where(v => !(known.Contains(v.File.FullName) && _history.Get(v.File.FullName, v.File) is not null))
            .Select(v => v.File.FullName)
            .ToList());
        foreach (var path in found) NoteArrival(path);
    }

    /// <summary>True while another program has the file open for writing (a download or copy still going).</summary>
    static bool IsBeingWritten(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Lists a settled new file (or a replaced one, e.g. an upgraded release) and queues it if it needs encoding.</summary>
    async Task AddArrivalAsync(string path)
    {
        var fi = new FileInfo(path);
        bool Same(Job j) => j.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || path.Equals(j.NewPath, StringComparison.OrdinalIgnoreCase);
        var rec = _history.Get(path, fi);
        // Known and unchanged; this includes the HEVC files this app just swapped in.
        if (rec is not null && _jobs.Any(Same)) return;
        var root = _settings.Folders.Where(f => IsUnder(path, f)).MaxBy(f => f.Length);
        if (root is null) return;

        var job = new Job { Path = path, Root = root, Arrived = true };
        try
        {
            if (rec?.Media is null)
                rec = _history.Put(path, new FileRecord { Media = await MediaInfo.ProbeAsync(path, CancellationToken.None) });
            job.Media = rec.Media;
            Classify(job, rec, _settings);
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Status = "Can't read: " + ex.Message;
        }
        _history.Save();

        // A replaced file takes over its old row.
        if (_jobs.FirstOrDefault(Same) is { } old)
        {
            if (old.State == JobState.Working) return;
            _list.Items.Remove(old.Item!);
            _jobs.Remove(old);
        }
        job.Item = NewItem(job);
        UpdateItem(job);
        _jobs.Insert(0, job);
        _list.Items.Insert(0, job.Item);
        if (job.State == JobState.Pending) _autoRunHeld = false;
        Log($"New file: {Path.GetRelativePath(root, path)} ({(job.State == JobState.Pending ? "queued" : job.Status)})");
        UpdateSummary();
    }

    bool ConfirmDelete() => MessageBox.Show(this,
        "Originals will be deleted after each HEVC file is verified. There is no undo.\n\nContinue?",
        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    static void Classify(Job job, FileRecord rec, AppSettings s)
    {
        // Files once skipped for saving too little are converted now too, like everything else.
        if (rec.Outcome == Outcome.NotWorthIt)
        {
            rec.Outcome = null;
            rec.Note = null;
            rec.Quality = null;
            rec.Vmaf = null;
            rec.Crop = null;
        }
        job.Outcome = rec.Outcome;
        job.Status = rec.Note ?? "";
        job.Quality = rec.Quality;
        job.Vmaf = rec.Vmaf;
        job.Crop = rec.Crop;
        job.NewSize = rec.NewSize;
        // Records from before finish times were kept: the encoded file's own timestamp is when it was written.
        // (Not in output-folder mode, where the record is the untouched source: there Size != NewSize.)
        job.Finished = rec.FinishedUtc?.ToLocalTime()
            ?? (rec.Outcome == Outcome.Done && rec.Size == rec.NewSize ? rec.ModifiedUtc.ToLocalTime() : null);
        job.State = rec.Outcome switch
        {
            Outcome.Done => JobState.Done,
            Outcome.Failed => JobState.Failed,
            Outcome.UserSkipped => JobState.Skipped,
            _ => JobState.Pending,
        };
        if (rec.Outcome is null && Transcoder.SkipReason(rec.Media!, s) is { } why)
        {
            job.State = JobState.Skipped;
            job.Status = why;
            if (rec.Media!.EncodedByUs) job.Finished = rec.ModifiedUtc.ToLocalTime(); // ours, but not in this history
        }
    }

    // ---------- encode queue ----------

    /// <summary>
    /// Encodes pending files until none are left. The next file is picked each time round, so files found by
    /// watch mode while it runs join the queue.
    /// </summary>
    /// <param name="unattended">Started by watch mode: no dialogs, problems go to the log.</param>
    async Task RunAsync(bool unattended = false)
    {
        if (_running) return;
        var queue = _jobs.Where(j => j.State == JobState.Pending).ToList();
        if (queue.Count == 0)
        {
            if (!unattended) MessageBox.Show(this, "Nothing to encode. Scan the library first.", Text);
            return;
        }
        string? problem = _settings.Handling switch
        {
            OriginalHandling.MoveToBackup when _settings.BackupFolder.Length == 0 => "Choose a backup folder for the originals.",
            OriginalHandling.WriteToOutputFolder when _settings.OutputFolder.Length == 0 => "Choose an output folder.",
            // Watch mode asked for this when it was switched on.
            OriginalHandling.Delete when !unattended && !ConfirmDelete() => "",
            _ => null,
        };
        if (problem is not null)
        {
            if (unattended)
            {
                Log("Watch mode can't encode: " + problem);
                _autoRunHeld = true;
            }
            else if (problem.Length > 0) MessageBox.Show(this, problem, Text);
            return;
        }
        if (_settings.Encoder == EncoderKind.Nvenc && !await Tools.NvencWorksAsync())
        {
            const string noNvenc = "NVENC isn't available on this PC: it needs an NVIDIA GPU (GTX 10-series or newer) " +
                "with a current driver. Switch the encoder to x265.";
            if (unattended)
            {
                Log("Watch mode can't encode: " + noNvenc);
                _autoRunHeld = true;
            }
            else MessageBox.Show(this, noNvenc, Text);
            return;
        }
        if (_running) return; // started by another caller during the NVENC check

        _cts = new CancellationTokenSource();
        _stopAfterCurrent = false;
        _autoRunHeld = false;
        SetRunning(true);
        if (_settings.KeepAwake) Tools.KeepAwake(true);
        int encoded = 0, failed = 0;
        Log($"Starting: {queue.Count} files, {Tools.Size(queue.Sum(j => j.Media!.Size))}");
        try
        {
            while (!_cts.IsCancellationRequested && !_stopAfterCurrent)
            {
                // Newly arrived files first, then the biggest: they save the most.
                var job = _jobs.Where(j => j.State == JobState.Pending)
                    .OrderByDescending(j => j.Arrived).ThenByDescending(j => j.Media!.Size).FirstOrDefault();
                if (job is null) break;
                if (!File.Exists(job.Path))
                {
                    job.State = JobState.Failed;
                    job.Status = "File no longer exists";
                    UpdateItem(job);
                    continue;
                }

                job.State = JobState.Working;
                job.Status = "Starting…";
                UpdateItem(job);
                job.Item?.EnsureVisible();
                var transcoder = new Transcoder(_settings.Clone(), Log,
                    (text, frac) => BeginInvoke(() => ShowProgress(job, text, frac)));
                try
                {
                    await Task.Run(() => transcoder.ProcessAsync(job, _cts.Token));
                }
                catch (OperationCanceledException)
                {
                    job.State = JobState.Pending;
                    job.Outcome = null;
                    job.Status = "Stopped";
                    job.Quality = null;
                    job.Vmaf = null;
                    job.Crop = null;
                    Log("  stopped");
                }
                catch (Exception ex)
                {
                    job.State = JobState.Failed;
                    job.Outcome = Outcome.Failed;
                    job.Status = ex.Message;
                    Log("  failed: " + ex.Message);
                }
                if (job.State == JobState.Working)
                {
                    // Never leave it Pending, or the loop would pick it again forever.
                    job.State = JobState.Failed;
                    job.Outcome = Outcome.Failed;
                    job.Status = "Finished without a result";
                }
                if (job.State == JobState.Done) job.Finished = DateTime.Now;
                Record(job);
                if (job.State == JobState.Done) encoded++;
                if (job.State == JobState.Failed) failed++;
                UpdateItem(job);
                if (_sortCol == ColFinished) _list.Sort();
                UpdateSummary();
            }
        }
        finally
        {
            Tools.KeepAwake(false);
            SetRunning(false);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            _current.Text = _cts.IsCancellationRequested ? "Stopped." : _stopAfterCurrent ? "Stopped after the current file." : "Queue finished.";
            Log(_current.Text);
            TrayQueueEnded(encoded, failed, _cts.IsCancellationRequested || _stopAfterCurrent);
        }

        if (encoded > 0 && _settings.RefreshPlex && _settings.PlexToken.Length > 0)
        {
            try
            {
                await Plex.RefreshAllAsync(_settings.PlexUrl, _settings.PlexToken);
                Log("Asked Plex to rescan its libraries.");
            }
            catch (Exception ex)
            {
                Log("Plex rescan failed: " + ex.Message);
            }
        }
        if (_closeWhenStopped) Close();
    }

    /// <summary>Saves the job's result so the file is never redone (or retried, if it failed) on later scans.</summary>
    void Record(Job job)
    {
        if (job.Outcome is null) return;
        try
        {
            var rec = new FileRecord
            {
                Media = job.Media, Outcome = job.Outcome, Note = job.Status, Quality = job.Quality, Vmaf = job.Vmaf, Crop = job.Crop,
            };
            var path = job.Path;
            if (job.Outcome == Outcome.Done)
            {
                rec.OriginalSize = job.Media!.Size;
                rec.NewSize = job.NewSize;
                rec.FinishedUtc = job.Finished?.ToUniversalTime();
                rec.OriginalPath = job.Path;
                rec.BackupPath = job.BackupPath;
                path = job.NewPath!;
                if (!path.Equals(job.Path, StringComparison.OrdinalIgnoreCase)) _history.Remove(job.Path);
            }
            _history.Put(path, rec);
            _history.Save();
        }
        catch (Exception ex)
        {
            Log("  could not save history: " + ex.Message);
        }
    }

    IEnumerable<Job> SelectedJobs() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (Job)i.Tag!);

    void SkipSelected()
    {
        foreach (var j in SelectedJobs().Where(j => j.State is JobState.Pending or JobState.Failed))
        {
            j.State = JobState.Skipped;
            j.Outcome = Outcome.UserSkipped;
            j.Status = "Skipped by you";
            Record(j);
            UpdateItem(j);
        }
        UpdateSummary();
    }

    void RetrySelected()
    {
        foreach (var j in SelectedJobs().Where(j => j.Outcome is Outcome.Failed or Outcome.NotWorthIt or Outcome.UserSkipped))
        {
            if (j.Media is null || !File.Exists(j.Path)) continue;
            _history.Put(j.Path, new FileRecord { Media = j.Media });
            Classify(j, new FileRecord { Media = j.Media }, _settings);
            UpdateItem(j);
        }
        _history.Save();
        UpdateSummary();
    }

    // ---------- revert ----------

    /// <summary>
    /// Deletes every converted file and moves its original back from the backup folder, then clears the list.
    /// Shows what it will do and asks first.
    /// </summary>
    async Task RevertAsync()
    {
        if (_running || _scanning) return;
        SetBusy(true);
        _running = true; // keeps watch mode from starting the queue meanwhile
        try
        {
            _current.Text = "Checking converted files and backups…";
            var settings = _settings.Clone();
            var plan = await Task.Run(() => Revert.Prepare(_history, settings));
            _current.Text = "";
            if (plan.Items.Count == 0)
            {
                MessageBox.Show(this, "Nothing to revert: no converted file has its original in the backup folder." +
                    (plan.Skipped.Count > 0 ? $"\n\n{plan.Skipped.Count} converted files were checked; see the log for why each was left." : ""),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                foreach (var s in plan.Skipped) Log("  revert: " + s);
                return;
            }

            long hevc = plan.Items.Sum(i => i.ConvertedSize), originals = plan.Items.Sum(i => i.OriginalSize);
            var msg = $"Revert {plan.Items.Count} converted files?\n\n" +
                      $"• The converted HEVC files ({Tools.Size(hevc)}) are deleted from the library.\n" +
                      $"• Their originals ({Tools.Size(originals)}) are moved back from the backup folder.\n" +
                      "• The file list is cleared; scan again to rebuild it.\n";
            if (plan.Skipped.Count > 0)
                msg += $"\n{plan.Skipped.Count} other converted files can't be reverted and are left as they are (details go to the log).\n";
            if (_settings.WatchMode)
                msg += "\nWatch mode is switched off, or the originals would be encoded again straight away.\n";
            msg += "\nThe converted files can't be recovered afterwards.";
            if (MessageBox.Show(this, msg, "Revert converted files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            if (_watch.Checked) _watch.Checked = false;
            Log($"Revert: {plan.Items.Count} files");
            foreach (var s in plan.Skipped) Log("  left alone: " + s);
            int done = 0, failed = 0;
            _progress.Style = ProgressBarStyle.Continuous;
            for (int i = 0; i < plan.Items.Count; i++)
            {
                var item = plan.Items[i];
                _current.Text = $"Reverting {i + 1} / {plan.Items.Count}: {Path.GetFileName(item.Original)}";
                _progress.Value = (int)(1000.0 * i / plan.Items.Count);
                try
                {
                    // A move between drives copies the whole original, so off the UI thread.
                    await Task.Run(() => Revert.Run(item, settings.BackupFolder));
                    _history.Remove(item.Converted);
                    done++;
                    Log($"  restored {item.Original}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Log($"  could not revert {Path.GetFileName(item.Converted)}: {ex.Message} (converted file kept)");
                }
            }
            _history.Save();
            _progress.Value = 0;

            _jobs.Clear();
            _list.Items.Clear();
            _scannedOnce = false;
            UpdateSummary();
            _current.Text = $"Reverted {done} files" + (failed > 0 ? $", {failed} failed (see the log)" : "") + ". Scan to rebuild the list.";
            Log(_current.Text);
        }
        catch (Exception ex)
        {
            _current.Text = "Revert failed: " + ex.Message;
            Log(_current.Text);
        }
        finally
        {
            _running = false;
            SetBusy(false);
            UpdateTray();
            if (_closeWhenStopped) Close(); // Exit was chosen while reverting
        }
    }

    // ---------- display ----------

    const int ColFile = 0, ColFinished = 7, ColStatus = 8;
    int _sortCol = -1;
    bool _sortDesc;

    static ListViewItem NewItem(Job j) => new(new string[ColStatus + 1]) { Tag = j, ToolTipText = j.Path };

    /// <summary>Sorts the list by the clicked column, or restores queue order (the order of _jobs).</summary>
    void ApplySort()
    {
        _list.Columns[ColFile].Text = "File" + (_sortCol == ColFile ? (_sortDesc ? " ▼" : " ▲") : "");
        _list.Columns[ColFinished].Text = "Finished" + (_sortCol == ColFinished ? (_sortDesc ? " ▼" : " ▲") : "");
        if (_sortCol >= 0)
        {
            _list.ListViewItemSorter = new JobSorter(_sortCol, _sortDesc); // sorts now and on every insert
            return;
        }
        _list.ListViewItemSorter = null;
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Items.AddRange(_jobs.Where(j => j.Item is not null).Select(j => j.Item!).ToArray());
        _list.EndUpdate();
    }

    sealed class JobSorter(int column, bool descending) : System.Collections.IComparer
    {
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b); // Explorer's order: "E2" before "E10"

        public int Compare(object? x, object? y)
        {
            var (a, b) = ((Job)((ListViewItem)x!).Tag!, (Job)((ListViewItem)y!).Tag!);
            if (column == ColFinished)
            {
                // Unfinished files stay at the bottom either way.
                if (a.Finished is null || b.Finished is null)
                    return (a.Finished is null).CompareTo(b.Finished is null);
                int c = a.Finished.Value.CompareTo(b.Finished.Value);
                return descending ? -c : c;
            }
            int n = StrCmpLogicalW(Path.GetRelativePath(a.Root, a.Path), Path.GetRelativePath(b.Root, b.Path));
            return descending ? -n : n;
        }
    }

    static readonly Color DoneColor = Color.FromArgb(120, 200, 130);
    static readonly Color FailedColor = Color.FromArgb(235, 115, 115);
    static readonly Color WorkingColor = Color.FromArgb(110, 175, 255);

    void UpdateItem(Job j)
    {
        if (j.Item is not { } item) return;
        var m = j.Media;
        item.SubItems[0].Text = Path.GetRelativePath(j.Root, j.Path);
        item.SubItems[1].Text = m is null ? "" : m.VideoCodec + (m.IsHdr ? " HDR" : "") + (m.DolbyVision ? " DV" : "");
        item.SubItems[2].Text = (m?.Resolution ?? "") + (j.Crop is { } c ? $" → {c}" : "");
        item.SubItems[3].Text = m is null ? "" : Tools.Size(m.Size);
        item.SubItems[4].Text = j.Quality is { } q ? $"{j.QualityName} {q}".Trim() : "";
        item.SubItems[5].Text = j.Vmaf?.ToString("0.0") ?? "";
        item.SubItems[6].Text = j.NewSize is { } n ? Tools.Size(n) : "";
        item.SubItems[ColFinished].Text = j.Finished?.ToString("yyyy-MM-dd HH:mm") ?? "";
        item.SubItems[ColStatus].Text = j.State == JobState.Pending && j.Status.Length == 0 ? "Queued" : j.Status;
        item.ForeColor = j.State switch
        {
            JobState.Done => DoneColor,
            JobState.Failed => FailedColor,
            JobState.Working => WorkingColor,
            JobState.Skipped => DarkTheme.SubtleText,
            _ => DarkTheme.Text,
        };
    }

    void ShowProgress(Job job, string text, double frac)
    {
        if (job.State != JobState.Working) return;
        job.Status = text;
        if (job.Item is { } item) item.SubItems[ColStatus].Text = text;
        _current.Text = $"{Path.GetFileName(job.Path)}: {text}";
        if (frac < 0) _progress.Style = ProgressBarStyle.Marquee;
        else
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = (int)(frac * 1000);
        }
        TrayProgress(job, text, frac);
    }

    /// <summary>
    /// Keeps the converted files and permanently deletes their originals from the backup folder. Asks twice.
    /// Only backups the history ties to a converted file still in the library (unchanged) are touched.
    /// </summary>
    async Task DeleteBackupsAsync()
    {
        if (_running || _scanning) return;
        SetBusy(true);
        _running = true; // keeps watch mode from starting the queue meanwhile
        try
        {
            _current.Text = "Checking converted files and backups…";
            var settings = _settings.Clone();
            var plan = await Task.Run(() => Revert.Prepare(_history, settings, forDelete: true));
            _current.Text = "";
            if (plan.Items.Count == 0)
            {
                MessageBox.Show(this, "No backups to delete: no converted file has its original in the backup folder." +
                    (plan.Skipped.Count > 0 ? $"\n\n{Plural(plan.Skipped.Count, "converted file")} checked; see the log for why each was left." : ""),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                foreach (var x in plan.Skipped) Log("  delete backups: " + x);
                return;
            }

            long originals = plan.Items.Sum(i => i.OriginalSize), hevc = plan.Items.Sum(i => i.ConvertedSize);
            var n = plan.Items.Count;
            var msg = $"Delete {Plural(n, "backup")} from the backup folder?\n\n" +
                      $"• The originals ({Tools.Size(originals)}) are deleted permanently, not to the Recycle Bin.\n" +
                      $"• The converted files ({Tools.Size(hevc)}) stay in the library as they are.\n" +
                      "• Revert is no longer possible for these files.\n";
            if (plan.Skipped.Count > 0)
                msg += $"\n{plan.Skipped.Count} other converted files have no backup that can safely be deleted; they are left alone (details go to the log).\n";
            msg += "\nCheck a few converted files in Plex before doing this.";
            if (MessageBox.Show(this, msg, "Delete backups", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (MessageBox.Show(this, $"Are you sure? {Plural(n, "original file")} ({Tools.Size(originals)}) will be gone for good.\n\n" +
                    "This can't be undone.", "Delete backups: last check", MessageBoxButtons.YesNo, MessageBoxIcon.Stop,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            Log($"Delete backups: {Plural(n, "file")}, {Tools.Size(originals)}");
            foreach (var x in plan.Skipped) Log("  left alone: " + x);
            int done = 0, failed = 0;
            long freed = 0;
            _progress.Style = ProgressBarStyle.Continuous;
            for (int i = 0; i < plan.Items.Count; i++)
            {
                var item = plan.Items[i];
                _current.Text = $"Deleting backup {i + 1} / {plan.Items.Count}: {Path.GetFileName(item.Backup)}";
                _progress.Value = (int)(1000.0 * i / plan.Items.Count);
                try
                {
                    await Task.Run(() => Revert.DeleteBackup(item, settings.BackupFolder));
                    done++;
                    freed += item.OriginalSize;
                    Log($"  deleted {item.Backup}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Log($"  could not delete {item.Backup}: {ex.Message}");
                }
            }
            _progress.Value = 0;
            _current.Text = $"Deleted {Plural(done, "backup")}, {Tools.Size(freed)} freed" + (failed > 0 ? $"; {failed} failed (see the log)" : "") + ".";
            Log(_current.Text);
        }
        catch (Exception ex)
        {
            _current.Text = "Deleting backups failed: " + ex.Message;
            Log(_current.Text);
        }
        finally
        {
            _running = false;
            SetBusy(false);
            UpdateTray();
            if (_closeWhenStopped) Close(); // Exit was chosen meanwhile
        }
    }

    static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    void UpdateSummary()
    {
        var pending = _jobs.Where(j => j.State == JobState.Pending).ToList();
        var (doneFiles, saved) = _history.Savings();
        _summary.Text = $"{_jobs.Count} files · {pending.Count} to encode ({Tools.Size(pending.Sum(j => j.Media?.Size ?? 0))})" +
                        $" · {doneFiles} re-encoded so far, {Tools.Size(saved)} saved" +
                        (_settings.WatchMode ? " · watching for new files" : "");
        UpdateTray();
    }

    void SetBusy(bool busy)
    {
        _scan.Enabled = _start.Enabled = _revert.Enabled = _deleteBackups.Enabled = !busy;
        foreach (var b in _folderButtons) b.Enabled = !busy;
    }

    void SetRunning(bool running)
    {
        _running = running;
        SetBusy(running);
        _stopNow.Enabled = running;
        _stopAfter.Enabled = running;
        _stopAfter.Text = "Stop after this file";
        if (running) _trayAlert = IconArt.Badge.None;
        UpdateTray();
    }

    void Log(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(line));
            return;
        }
        var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}";
        if (_log.TextLength > 300_000) _log.Text = _log.Text[^150_000..];
        _log.AppendText(stamped + Environment.NewLine);
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(LogPath, stamped + Environment.NewLine);
        }
        catch { }
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        // The close button hides to the tray; Exit (tray menu) and Windows shutting down really close.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (!_running) return;
        e.Cancel = true;
        if (_closeWhenStopped) return;
        if (MessageBox.Show(this, "An encode is running. Stop it and quit? (The original file is kept.)", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _closeWhenStopped = true;
        _cts?.Cancel();
    }
}
