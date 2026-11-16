using System.Runtime.InteropServices;

namespace Any2Hevc;

/// <summary>Dark colors for the WinForms window, its list, the tray menu and the title bar.</summary>
static class DarkTheme
{
    public static readonly Color Background = Color.FromArgb(32, 32, 32);
    public static readonly Color Surface = Color.FromArgb(43, 43, 43);
    public static readonly Color Border = Color.FromArgb(64, 64, 64);
    public static readonly Color Text = Color.FromArgb(240, 240, 240);
    public static readonly Color SubtleText = Color.FromArgb(160, 160, 160);
    public static readonly Color Highlight = Color.FromArgb(62, 62, 66);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Dark title bar (Windows 10 2004+ / 11). Call once the handle exists.</summary>
    public static void ApplyTitleBar(Form form)
    {
        int on = 1;
        if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));     // pre-20H1 value
    }

    /// <summary>Recolors a control tree. Controls that set their own colors (ON / Apply buttons) are tagged to skip.</summary>
    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls) Apply(c);
        if (root.Tag as string == KeepColors) return;

        switch (root)
        {
            case ListView lv:
                lv.BackColor = Surface;
                lv.ForeColor = Text;
                lv.BorderStyle = BorderStyle.FixedSingle;
                lv.OwnerDraw = true;
                lv.DrawColumnHeader += DrawHeader;
                lv.DrawItem += (_, e) => e.DrawDefault = true;
                lv.DrawSubItem += (_, e) => e.DrawDefault = true;
                lv.HandleCreated += (_, _) => SetWindowTheme(lv.Handle, "DarkMode_Explorer", null);
                if (lv.IsHandleCreated) SetWindowTheme(lv.Handle, "DarkMode_Explorer", null);
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = Surface;
                cb.ForeColor = Text;
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Highlight;
                b.BackColor = Surface;
                b.ForeColor = Text;
                break;
            case TextBoxBase or NumericUpDown or ListBox:
                root.BackColor = Surface;
                root.ForeColor = Text;
                break;
            case Label l:
                l.BackColor = Color.Transparent;
                l.ForeColor = l.Tag as string == Subtle ? SubtleText : Text;
                break;
            default:
                root.BackColor = Background;
                root.ForeColor = Text;
                break;
        }
    }

    public const string KeepColors = "theme:keep";
    public const string Subtle = "theme:subtle";

    static void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(Color.FromArgb(50, 50, 50));
        e.Graphics.FillRectangle(bg, e.Bounds);
        using var pen = new Pen(Border);
        e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var r = e.Bounds;
        r.Inflate(-6, 0);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, e.Font, r, Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    public static void Apply(ToolStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        foreach (ToolStripItem item in menu.Items) item.ForeColor = Text;
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Highlight;
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemSelectedGradientBegin => Highlight;
        public override Color MenuItemSelectedGradientEnd => Highlight;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
