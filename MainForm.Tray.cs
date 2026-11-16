namespace Any2Hevc;

/// <summary>
/// Tray icon: shows what the app is doing (a pie that fills while encoding, a spinner while testing quality, a dot
/// for watch mode, a tick or warning when the queue ends unseen). Closing the window hides it here; encoding and
/// watch mode carry on. Exit is in the tray menu.
/// </summary>
public sealed partial class MainForm
{
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _trayAnim = new() { Interval = 120 };
    int _trayFrame;
    double _trayProgress = -1;
    string _trayStatus = "";
    /// <summary>Done or Error once a queue ends; cleared when the window is next looked at.</summary>
    IconArt.Badge _trayAlert = IconArt.Badge.None;
    (IconArt.Badge Badge, int Step)? _trayDrawn;
    bool _exiting, _toldAboutTray;
    ToolStripMenuItem? _trayStart, _trayStopAfter, _trayWatch;

    static Icon LoadAppIcon()
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream("any2hevc.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }

    void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray()).Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        _trayStart = new ToolStripMenuItem("Start", null, async (_, _) => await RunAsync());
        _trayStopAfter = new ToolStripMenuItem("Stop after this file", null, (_, _) => _stopAfter.PerformClick());
        _trayWatch = new ToolStripMenuItem("Watch for new files", null, (_, _) => _watch.Checked = !_watch.Checked);
        menu.Items.AddRange(new ToolStripItem[] { _trayStart, _trayStopAfter, _trayWatch, new ToolStripSeparator() });
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        menu.Opening += (_, _) =>
        {
            _trayStart.Enabled = _start.Enabled && !_running;
            _trayStopAfter.Enabled = _stopAfter.Enabled;
            _trayWatch.Checked = _watch.Checked;
        };
        DarkTheme.Apply(menu);

        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        _trayAnim.Tick += (_, _) => { _trayFrame = (_trayFrame + 1) % 8; UpdateTray(); };
        Activated += (_, _) =>
        {
            if (_trayAlert == IconArt.Badge.None) return;
            _trayAlert = IconArt.Badge.None;
            UpdateTray();
        };
        FormClosed += (_, _) =>
        {
            _trayAnim.Stop();
            _tray.Visible = false;
            _tray.Dispose();
        };
        UpdateTray();
        _tray.Visible = true;
    }

    /// <summary>Redraws the tray icon and tooltip for the current state (only when the picture changes).</summary>
    void UpdateTray()
    {
        if (_tray.ContextMenuStrip is null) return; // not set up yet
        var badge = _running ? IconArt.Badge.Working
            : _trayAlert != IconArt.Badge.None ? _trayAlert
            : _settings.WatchMode ? (_autoRunHeld ? IconArt.Badge.Paused : IconArt.Badge.Watching)
            : IconArt.Badge.None;
        bool spinning = badge == IconArt.Badge.Working && _trayProgress < 0;
        if (spinning != _trayAnim.Enabled) _trayAnim.Enabled = spinning;

        // The pie moves in 1/32 steps, so the icon is redrawn about 32 times per file, not on every progress tick.
        int step = badge != IconArt.Badge.Working ? 0 : spinning ? -1 - _trayFrame : (int)(_trayProgress * 32);
        if (_trayDrawn != (badge, step))
        {
            _trayDrawn = (badge, step);
            using var bmp = IconArt.Draw(SystemInformation.SmallIconSize.Width, badge, spinning ? -1 : step / 32.0, _trayFrame);
            var old = _tray.Icon;
            _tray.Icon = IconArt.ToIcon(bmp);
            old?.Dispose();
        }

        var text = badge switch
        {
            IconArt.Badge.Working => _trayStatus.Length > 0 ? _trayStatus : "Encoding",
            IconArt.Badge.Done => "Queue finished",
            IconArt.Badge.Error => "Queue finished with errors (see the log)",
            IconArt.Badge.Paused => "Watch mode paused until a new file arrives",
            IconArt.Badge.Watching => "Watching for new files",
            _ => "Idle",
        };
        text = "any2hevc: " + text;
        _tray.Text = text.Length > 127 ? text[..126] + "…" : text; // the tooltip limit
    }

    void TrayProgress(Job job, string text, double frac)
    {
        _trayProgress = frac;
        _trayStatus = frac >= 0 ? $"{Path.GetFileName(job.Path)} {frac:P0}" : $"{Path.GetFileName(job.Path)}: {text}";
        UpdateTray();
    }

    /// <summary>Called when a queue ends: badge and a notification if nobody is looking at the window.</summary>
    void TrayQueueEnded(int encoded, int failed, bool stopped)
    {
        _trayProgress = -1;
        _trayStatus = "";
        bool unseen = !Visible || WindowState == FormWindowState.Minimized || !ContainsFocus;
        if (unseen && (encoded > 0 || failed > 0))
        {
            _trayAlert = failed > 0 ? IconArt.Badge.Error : IconArt.Badge.Done;
            if (!stopped)
                _tray.ShowBalloonTip(5000, "any2hevc", $"Queue finished: {encoded} encoded" + (failed > 0 ? $", {failed} failed." : "."),
                    failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        UpdateTray();
    }

    void HideToTray()
    {
        Hide();
        if (_toldAboutTray) return;
        _toldAboutTray = true;
        _tray.ShowBalloonTip(4000, "any2hevc is still running",
            "Encoding and watch mode carry on in the background. Click the tray icon to open it; right-click > Exit to quit.",
            ToolTipIcon.Info);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---------- taskbar jump list ----------

    // Commands a second launch passes on (taskbar jump list, or just starting the exe again: "show").
    public const string CmdShow = "show", CmdStart = "start", CmdStopAfter = "stop-after", CmdStopNow = "stop-now",
        CmdScan = "scan", CmdWatchOn = "watch-on", CmdWatchOff = "watch-off", CmdExit = "exit";

    /// <summary>The right-click menu of the taskbar button. The watch item names what clicking it will do.</summary>
    void UpdateJumpList() => JumpList.SetTasks(new JumpList.Item?[]
    {
        new("Start encoding", CmdStart, "Scan if needed, then encode everything waiting"),
        new("Stop after this file", CmdStopAfter, "Finish the current file, then stop"),
        new("Stop now", CmdStopNow, "Stop at once; the current file keeps its original"),
        null,
        new("Scan library", CmdScan, "Look for new and changed files"),
        _settings.WatchMode
            ? new("Turn off watch mode", CmdWatchOff, "Stop encoding new files automatically")
            : new("Turn on watch mode", CmdWatchOn, "Encode new files automatically as they arrive"),
        null,
        new("Exit", CmdExit, "Quit any2hevc (asks first if an encode is running)"),
    });

    public async Task RunCommandAsync(string command)
    {
        switch (command)
        {
            case CmdStart:
                if (_running || _scanning) return;
                if (!_scannedOnce && _settings.Folders.Count > 0) await ScanAsync();
                await RunAsync();
                break;
            case CmdStopAfter:
                if (_stopAfter.Enabled) _stopAfter.PerformClick();
                break;
            case CmdStopNow:
                if (_stopNow.Enabled) _stopNow.PerformClick();
                break;
            case CmdScan:
                if (!_running && !_scanning) await ScanAsync();
                break;
            case CmdWatchOn:
            case CmdWatchOff:
                _watch.Checked = command == CmdWatchOn;
                break;
            case CmdExit:
                ExitApp();
                break;
            default:
                ShowFromTray();
                break;
        }
    }

    void ExitApp()
    {
        if (_running) ShowFromTray(); // the "stop the encode?" question needs the window
        _exiting = true;
        Close();
        // Answered No: the window closes to the tray again later. (Yes closes once the encode has stopped.)
        if (!IsDisposed && !_closeWhenStopped) _exiting = false;
    }
}
