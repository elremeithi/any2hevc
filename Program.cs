using System.IO.Pipes;

namespace PlexHevc;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Taskbar jump-list tasks start the exe with --start, --scan, etc.; a plain start means "show".
        var command = args.FirstOrDefault(a => a.StartsWith("--"))?[2..].ToLowerInvariant() ?? MainForm.CmdShow;

        // One instance per data folder (PLEXHEVC_DATA lets a test copy run beside the real one).
        var key = "PlexHevc_" + AppSettings.DataDir.ToLowerInvariant().Replace('\\', '_');
        using var mutex = new Mutex(true, key, out bool firstInstance);
        if (!firstInstance)
        {
            SendCommand(key, command); // the running copy (maybe hidden in the tray) acts on it
            return;
        }
        // Nothing to stop when the app wasn't running.
        if (command is MainForm.CmdStopAfter or MainForm.CmdStopNow or MainForm.CmdExit) return;

        ApplicationConfiguration.Initialize();
        Tools.EnsureStartMenuShortcut();
        Tools.Prepare();
        if (Tools.FFmpeg is null || Tools.FFprobe is null)
        {
            MessageBox.Show("The built-in ffmpeg could not be unpacked to %LOCALAPPDATA%\\PlexHevc, and ffmpeg.exe / " +
                "ffprobe.exe were not found next to this app or on PATH.\n\n" +
                "Free some disk space, or install them with:  winget install Gyan.FFmpeg", "Plex HEVC",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var form = new MainForm(AppSettings.Load(), History.Load(), command == MainForm.CmdShow ? null : command);
        new Thread(() => ListenForCommands(key, form)) { IsBackground = true }.Start();
        Application.Run(form);
    }

    static void SendCommand(string key, string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", key, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(5000);
            using var w = new StreamWriter(pipe);
            w.WriteLine(command);
        }
        catch { /* the other copy is closing */ }
    }

    static void ListenForCommands(string key, MainForm form)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(key, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                using var r = new StreamReader(pipe);
                if (r.ReadLine() is { Length: > 0 } cmd)
                    form.BeginInvoke(async () => await form.RunCommandAsync(cmd));
            }
            catch { Thread.Sleep(500); } // window not created yet, or closing
        }
    }
}
