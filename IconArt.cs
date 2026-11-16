using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Any2Hevc;

/// <summary>
/// The app's icon, drawn in code so the tray can show the same artwork with a status badge. Also writes the
/// .ico the exe is built with (see the csproj).
/// </summary>
static class IconArt
{
    public enum Badge { None, Watching, Working, Paused, Done, Error }

    static readonly Color Top = Color.FromArgb(14, 165, 233);     // sky blue
    static readonly Color Bottom = Color.FromArgb(79, 70, 229);   // indigo

    /// <param name="progress">Working: 0..1 fills the badge like a pie; below 0 a segment spins (with <paramref name="frame"/>).</param>
    public static Bitmap Draw(int size, Badge badge = Badge.None, double progress = -1, int frame = 0)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = size;

        // Tile: rounded square with a diagonal gradient.
        var tile = new RectangleF(s * 0.03f, s * 0.03f, s * 0.94f, s * 0.94f);
        using (var path = Rounded(tile, s * 0.22f))
        using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Top, Bottom))
            g.FillPath(fill, path);

        // Picture frame with open corners: the picture being squeezed. Too fine to see below 32 px.
        if (size >= 32)
        {
            using var pen = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1.5f, s * 0.035f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float a = s * 0.17f, b = s * 0.83f, l = s * 0.14f;
            g.DrawLines(pen, new[] { new PointF(a, a + l), new PointF(a, a), new PointF(a + l, a) });
            g.DrawLines(pen, new[] { new PointF(b - l, a), new PointF(b, a), new PointF(b, a + l) });
            g.DrawLines(pen, new[] { new PointF(a, b - l), new PointF(a, b), new PointF(a + l, b) });
            g.DrawLines(pen, new[] { new PointF(b - l, b), new PointF(b, b), new PointF(b, b - l) });
        }

        // Play symbol, nudged right so it looks centred.
        float k = size >= 32 ? 1f : 1.25f; // bigger when there's no frame
        var c = new PointF(s * 0.53f, s * 0.5f);
        var tri = new[]
        {
            new PointF(c.X - s * 0.15f * k, c.Y - s * 0.19f * k),
            new PointF(c.X - s * 0.15f * k, c.Y + s * 0.19f * k),
            new PointF(c.X + s * 0.19f * k, c.Y),
        };
        using (var white = new SolidBrush(Color.White))
        using (var join = new Pen(Color.White, s * 0.05f) { LineJoin = LineJoin.Round })
        {
            g.FillPolygon(white, tri);
            g.DrawPolygon(join, tri); // rounds the corners
        }

        if (badge != Badge.None) DrawBadge(g, s, badge, progress, frame);
        return bmp;
    }

    static void DrawBadge(Graphics g, float s, Badge badge, double progress, int frame)
    {
        float d = s * 0.52f;
        var r = new RectangleF(s - d, s - d, d, d);
        using (var ring = new SolidBrush(Color.FromArgb(32, 32, 32)))
            g.FillEllipse(ring, r);
        var inner = RectangleF.Inflate(r, -d * 0.12f, -d * 0.12f);

        if (badge == Badge.Working)
        {
            using var track = new SolidBrush(Color.FromArgb(70, 70, 70));
            using var fill = new SolidBrush(Color.FromArgb(74, 222, 128));
            g.FillEllipse(track, inner);
            if (progress >= 0)
                g.FillPie(fill, inner.X, inner.Y, inner.Width, inner.Height, -90, (float)(360 * Math.Clamp(progress, 0.02, 1)));
            else
                g.FillPie(fill, inner.X, inner.Y, inner.Width, inner.Height, frame * 45 - 90, 110);
            return;
        }

        var color = badge switch
        {
            Badge.Watching => Color.FromArgb(56, 189, 248),
            Badge.Paused => Color.FromArgb(251, 191, 36),
            Badge.Done => Color.FromArgb(74, 222, 128),
            _ => Color.FromArgb(248, 113, 113),
        };
        using (var b = new SolidBrush(color))
            g.FillEllipse(b, inner);
        if (s < 24) return; // just a coloured dot at tray size on standard DPI

        using var pen = new Pen(Color.FromArgb(30, 30, 30), inner.Width * 0.16f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x = inner.X, y = inner.Y, w = inner.Width;
        switch (badge)
        {
            case Badge.Done:
                g.DrawLines(pen, new[] { new PointF(x + w * 0.27f, y + w * 0.52f), new PointF(x + w * 0.44f, y + w * 0.68f), new PointF(x + w * 0.74f, y + w * 0.34f) });
                break;
            case Badge.Paused:
                g.DrawLine(pen, x + w * 0.38f, y + w * 0.3f, x + w * 0.38f, y + w * 0.7f);
                g.DrawLine(pen, x + w * 0.62f, y + w * 0.3f, x + w * 0.62f, y + w * 0.7f);
                break;
            case Badge.Error:
                g.DrawLine(pen, x + w * 0.5f, y + w * 0.27f, x + w * 0.5f, y + w * 0.55f);
                g.DrawLine(pen, x + w * 0.5f, y + w * 0.74f, x + w * 0.5f, y + w * 0.745f);
                break;
            case Badge.Watching:
                using (var dark = new SolidBrush(Color.FromArgb(30, 30, 30)))
                    g.FillEllipse(dark, x + w * 0.33f, y + w * 0.33f, w * 0.34f, w * 0.34f);
                break;
        }
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>An Icon that owns its own copy, so the GDI handle can be freed straight away.</summary>
    public static Icon ToIcon(Bitmap bmp)
    {
        var h = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(h);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    /// <summary>Writes a multi-size .ico with PNG images (fine for Windows Vista and later).</summary>
    public static void SaveIco(string path, params int[] sizes)
    {
        var images = sizes.Select(sz =>
        {
            using var bmp = Draw(sz);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return (Size: sz, Png: ms.ToArray());
        }).ToList();

        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0);
        w.Write((short)1); // icon
        w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);  // palette
            w.Write((byte)0);
            w.Write((short)1); // planes
            w.Write((short)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
