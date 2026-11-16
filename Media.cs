using System.Globalization;
using System.Text.Json;

namespace Any2Hevc;

/// <summary>What ffprobe says about a file; cached in the history so rescans don't re-probe.</summary>
public sealed class MediaInfo
{
    public double Duration { get; set; }
    public long Size { get; set; }
    /// <summary>Absolute stream index of the main video (cover art is skipped).</summary>
    public int VideoIndex { get; set; }
    public string VideoCodec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string PixFmt { get; set; } = "";
    public string ColorPrimaries { get; set; } = "";
    public string ColorTransfer { get; set; } = "";
    public string ColorSpace { get; set; } = "";
    public bool Interlaced { get; set; }
    public bool DolbyVision { get; set; }
    /// <summary>Source video bitrate in bits/s, 0 when the container doesn't say.</summary>
    public long VideoBitrate { get; set; }
    public int AudioStreams { get; set; }
    /// <summary>Codec of each subtitle stream, in order.</summary>
    public List<string> SubtitleCodecs { get; set; } = new();
    /// <summary>Absolute stream index of each subtitle stream, matching <see cref="SubtitleCodecs"/>.</summary>
    public List<int> SubtitleIndexes { get; set; } = new();

    /// <summary>Carries this app's container tag, i.e. it is one of our own encodes.</summary>
    public bool EncodedByUs { get; set; }
    /// <summary>Container tag written on every encode (value: the CRF / CQ used).</summary>
    public const string OurTag = "ANY2HEVC";
    /// <summary>All tags that mark our encodes: this app was called PlexHevc before, and its encodes say so.</summary>
    static readonly string[] OurTags = { OurTag, "PLEXHEVC" };

    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
    public string Resolution => Width == 0 ? "" : $"{Width}x{Height}";

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var r = await Tools.RunAsync(Tools.FFprobe!,
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("ffprobe: " + r.Tail(2));

        using var doc = JsonDocument.Parse(r.StdOut);
        var root = doc.RootElement;
        var m = new MediaInfo();
        if (root.TryGetProperty("format", out var fmt))
        {
            m.Duration = Num(fmt, "duration");
            m.Size = (long)Num(fmt, "size");
            if (fmt.TryGetProperty("tags", out var ftags))
                m.EncodedByUs = ftags.EnumerateObject().Any(t => OurTags.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
        }

        bool haveVideo = false;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            switch (Str(s, "codec_type"))
            {
                case "video" when !haveVideo && !IsCoverArt(s):
                    haveVideo = true;
                    m.VideoIndex = (int)Num(s, "index");
                    m.VideoCodec = Str(s, "codec_name");
                    m.Width = (int)Num(s, "width");
                    m.Height = (int)Num(s, "height");
                    m.PixFmt = Str(s, "pix_fmt");
                    m.ColorPrimaries = Str(s, "color_primaries");
                    m.ColorTransfer = Str(s, "color_transfer");
                    m.ColorSpace = Str(s, "color_space");
                    m.Interlaced = Str(s, "field_order") is "tt" or "bb" or "tb" or "bt";
                    m.VideoBitrate = (long)Num(s, "bit_rate");
                    if (m.VideoBitrate == 0 && s.TryGetProperty("tags", out var tags))
                        m.VideoBitrate = (long)(Num(tags, "BPS") is > 0 and var bps ? bps : Num(tags, "BPS-eng"));
                    if (s.TryGetProperty("side_data_list", out var sd))
                        m.DolbyVision = sd.EnumerateArray().Any(d => Str(d, "side_data_type").Contains("DOVI"));
                    if (m.Duration == 0) m.Duration = Num(s, "duration");
                    break;
                case "audio":
                    m.AudioStreams++;
                    break;
                case "subtitle":
                    m.SubtitleCodecs.Add(Str(s, "codec_name"));
                    m.SubtitleIndexes.Add((int)Num(s, "index"));
                    break;
            }
        }
        if (m.Size == 0) m.Size = new FileInfo(path).Length;
        return m;
    }

    static bool IsCoverArt(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
