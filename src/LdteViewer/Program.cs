using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LdteViewer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--check")
        {
            try
            {
                var doc = LdteReader.Read(args[1]);
                var sb = new StringBuilder();
                sb.AppendLine("frames=" + doc.Frames);
                sb.AppendLine("channels=" + doc.Channels);
                sb.AppendLine("duration=" + doc.Duration.ToString("0.00"));
                sb.AppendLine("segments=" + doc.Segments.Count);
                sb.AppendLine("tracks=" + doc.Tracks.Count);
                foreach (var t in doc.Tracks.Take(3)) sb.AppendLine("track " + t.Key + " | " + t.Name + " | " + t.Seconds + "s");
                File.WriteAllText(args[2], sb.ToString(), Encoding.UTF8);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(args[2], "ERROR: " + ex.Message, Encoding.UTF8); } catch { }
                Environment.Exit(2);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Length > 0 ? args[0] : ""));
    }
}

internal static class LdteCrypto
{
    public static byte[] FromB64Url(string s)
    {
        s = (s ?? "").Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("密钥长度不正确");
        }
        return Convert.FromBase64String(s);
    }

    public static byte[] ParseBat1(string token)
    {
        var p = (token ?? "").Trim().Split('.');
        if (p.Length != 4 || p[0] != "BAT1") throw new FormatException("格式不是 BAT1 离线密钥");
        var key = FromB64Url(p[1]);
        if (key.Length != 32) throw new FormatException("密钥长度不正确");
        if (!long.TryParse(p[2], out long exp)) throw new FormatException("到期时间无效");
        using var sha = SHA256.Create();
        string fp = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(p[1] + "." + p[2]))).Substring(0, 16).ToLowerInvariant();
        if (fp != p[3].ToLowerInvariant()) throw new FormatException("密钥校验失败");
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) throw new FormatException("密钥已过期");
        return key;
    }

    public static byte[] DecryptClip(byte[] bytes, byte[] master)
    {
        if (bytes.Length < 101) throw new InvalidDataException("片段文件过短");
        if (Encoding.ASCII.GetString(bytes, 0, 5) != "LDTC1") throw new InvalidDataException("不是 LDTC1 加密片段");
        if (bytes[5] != 1) throw new InvalidDataException("片段版本不支持");
        int off = 6;
        var nonce = Slice(bytes, ref off, 12);
        var tag = Slice(bytes, ref off, 16);
        var wnonce = Slice(bytes, ref off, 12);
        var wtag = Slice(bytes, ref off, 16);
        var wkey = Slice(bytes, ref off, 32);
        long len = BitConverter.ToInt64(bytes, off);
        off += 8;
        if (len <= 0 || off + len > bytes.Length) throw new InvalidDataException("片段长度损坏");
        var ct = Slice(bytes, ref off, (int)len);
        using var mk = new AesGcm(master, 16);
        var dk = new byte[32];
        mk.Decrypt(wnonce, wkey, wtag, dk);
        using var ak = new AesGcm(dk, 16);
        var plain = new byte[len];
        ak.Decrypt(nonce, ct, tag, plain);
        return plain;
    }

    private static byte[] Slice(byte[] src, ref int off, int len)
    {
        var b = new byte[len];
        Buffer.BlockCopy(src, off, b, 0, len);
        off += len;
        return b;
    }

    public static string LdteFileName(string key)
    {
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? ""));
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}

internal sealed class ChannelData
{
    public string Name;
    public string Title;
    public string Desc;
    public float[] Bins;
    public float Max;
    public float Avg;
    public Color Color;
}

internal sealed class SegmentRow
{
    public double T0;
    public double T1;
    public double Peak;
    public float RmsMean;
    public float HighMean;
    public float LowMean;
}

internal sealed class TrackInfo
{
    public string Key = "";
    public string Name = "";
    public double Seconds;
    public double Frames;
    public double Peak;
    public double Rms;
    public double Crest;
    public double Lufs;
    public string Updated = "";
}

internal sealed class LdteDoc
{
    public string Path = "";
    public string Key = "";
    public string Name = "";
    public string Updated = "";
    public bool System;
    public uint TrackCount;
    public int Version;
    public int FrameMs;
    public uint Frames;
    public uint Channels;
    public uint SampleRate;
    public uint SegCount;
    public uint Passes;
    public uint Flags;
    public uint HeaderLen;
    public long FileBytes;
    public double Duration;
    public string HeaderJson = "";
    public Dictionary<string, object> Stats = new();
    public Dictionary<string, object> Limit = new();
    public Dictionary<string, Dictionary<string, object>> Profiles = new();
    public List<ChannelData> Cols = new();
    public List<SegmentRow> Segments = new();
    public List<TrackInfo> Tracks = new();
}

internal static class LdteReader
{
    private static readonly string[] Names =
        { "peak", "rms", "zcr", "flux", "bright", "low", "mid", "high", "width", "corr", "trans", "clip" };
    private static readonly string[] Titles =
        { "峰值", "有效值", "过零率", "动态指数", "亮度", "低频", "中频", "高频", "立体声宽度", "相关性", "瞬态", "削波" };
    private static readonly string[] Descs =
    {
        "帧内瞬时振幅", "帧内有效电平", "辅助音高与静音检测", "RMS 帧间变化", "高频占比（简单谱心代理）",
        "<250Hz 能量", "250–2500Hz 能量", ">2500Hz 能量", "左右声道差异", "声道相关性",
        "帧间突变强度", "过载削波计数"
    };
    private static readonly Color[] Palette =
    {
        Color.FromArgb(235, 120, 120), Color.FromArgb(235, 175, 95), Color.FromArgb(205, 205, 100),
        Color.FromArgb(150, 205, 110), Color.FromArgb(110, 215, 160), Color.FromArgb(100, 195, 225),
        Color.FromArgb(110, 165, 245), Color.FromArgb(150, 135, 245), Color.FromArgb(205, 135, 235),
        Color.FromArgb(235, 135, 205), Color.FromArgb(120, 205, 225), Color.FromArgb(225, 165, 120)
    };

    public static LdteDoc Read(string path, int bins = 2200)
    {
        var buf = File.ReadAllBytes(path);
        if (buf.Length < 20) throw new InvalidDataException("档案过短");
        if (Encoding.ASCII.GetString(buf, 0, 4) != "LDTE") throw new InvalidDataException("不是 LDTE 档案");
        int off = 4;
        int ver = BitConverter.ToUInt16(buf, off); off += 2;
        int frameMs = BitConverter.ToUInt16(buf, off); off += 2;
        uint passes = BitConverter.ToUInt32(buf, off); off += 4;
        uint frames = BitConverter.ToUInt32(buf, off); off += 4;
        uint jsonLen = BitConverter.ToUInt32(buf, off); off += 4;
        uint channels = 4, rate = 48000, flags = 0, segCount = 0;
        if (ver >= 2)
        {
            channels = BitConverter.ToUInt32(buf, off); off += 4;
            rate = BitConverter.ToUInt32(buf, off); off += 4;
            flags = BitConverter.ToUInt32(buf, off); off += 4;
            segCount = BitConverter.ToUInt32(buf, off); off += 4;
            off += 12;
        }
        if (ver == 0 || ver > 2) throw new InvalidDataException("版本不支持：" + ver);
        if (channels == 0 || channels > 64) throw new InvalidDataException("通道数异常");
        if (frames > 20000000) throw new InvalidDataException("帧数异常");
        if (jsonLen > 4 * 1024 * 1024 || off + jsonLen > buf.Length) throw new InvalidDataException("头部异常");
        string json = Encoding.UTF8.GetString(buf, off, (int)jsonLen);
        off += (int)jsonLen;

        var doc = new LdteDoc
        {
            Path = path,
            Version = ver,
            FrameMs = frameMs,
            Frames = frames,
            Channels = channels,
            SampleRate = rate,
            SegCount = segCount,
            Passes = passes,
            Flags = flags,
            HeaderLen = jsonLen,
            FileBytes = buf.Length,
            Duration = frames * frameMs / 1000.0,
            HeaderJson = json
        };

        int chCount = (int)Math.Min(channels, Names.Length);
        for (int c = 0; c < chCount; c++)
        {
            var cd = new ChannelData
            {
                Name = Names[c],
                Title = Titles[c],
                Desc = Descs[c],
                Bins = new float[bins],
                Color = Palette[c]
            };
            long baseOff = off + (long)c * frames * 4;
            if (baseOff + (long)frames * 4 > buf.Length) throw new InvalidDataException("数据区不足");
            double sum = 0;
            for (int i = 0; i < frames; i++)
            {
                float v = BitConverter.ToSingle(buf, (int)(baseOff + (long)i * 4));
                if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
                if (v > cd.Max) cd.Max = v;
                sum += v;
                int bi = frames > 0 ? (int)((long)i * bins / frames) : 0;
                if (bi < 0) bi = 0;
                if (bi >= bins) bi = bins - 1;
                float a = Math.Abs(v);
                if (a > cd.Bins[bi]) cd.Bins[bi] = a;
            }
            cd.Avg = frames > 0 ? (float)(sum / frames) : 0;
            doc.Cols.Add(cd);
        }

        try
        {
            using var jd = JsonDocument.Parse(json);
            var r = jd.RootElement;
            if (r.TryGetProperty("key", out var k)) doc.Key = k.GetString() ?? "";
            if (r.TryGetProperty("name", out var n)) doc.Name = n.GetString() ?? "";
            if (r.TryGetProperty("updated", out var upd)) doc.Updated = upd.GetString() ?? "";
            if (r.TryGetProperty("system", out var sysEl) && sysEl.ValueKind == JsonValueKind.True) doc.System = true;
            if (r.TryGetProperty("trackCount", out var tce) && tce.ValueKind == JsonValueKind.Number) doc.TrackCount = (uint)tce.GetDouble();
            ReadDict(r, "stats", doc.Stats);
            ReadDict(r, "limit", doc.Limit);
            if (r.TryGetProperty("profiles", out var profs) && profs.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in profs.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    var pd = new Dictionary<string, object>();
                    ReadDictFlat(p.Value, pd, "");
                    doc.Profiles[p.Name] = pd;
                }
            }
            if (r.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in tr.EnumerateObject())
                {
                    var ti = new TrackInfo { Key = p.Name };
                    if (p.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (p.Value.TryGetProperty("name", out var tn)) ti.Name = tn.GetString() ?? "";
                        if (p.Value.TryGetProperty("seconds", out var ts) && ts.ValueKind == JsonValueKind.Number) ti.Seconds = ts.GetDouble();
                        if (p.Value.TryGetProperty("frames", out var tf) && tf.ValueKind == JsonValueKind.Number) ti.Frames = tf.GetDouble();
                        if (p.Value.TryGetProperty("peak", out var tp) && tp.ValueKind == JsonValueKind.Number) ti.Peak = tp.GetDouble();
                        if (p.Value.TryGetProperty("rms", out var trm) && trm.ValueKind == JsonValueKind.Number) ti.Rms = trm.GetDouble();
                        if (p.Value.TryGetProperty("crest", out var tc) && tc.ValueKind == JsonValueKind.Number) ti.Crest = tc.GetDouble();
                        if (p.Value.TryGetProperty("lufs", out var tl) && tl.ValueKind == JsonValueKind.Number) ti.Lufs = tl.GetDouble();
                        if (p.Value.TryGetProperty("updated", out var tu)) ti.Updated = tu.GetString() ?? "";
                    }
                    doc.Tracks.Add(ti);
                }
                doc.Tracks = doc.Tracks.OrderByDescending(t => t.Seconds).ToList();
            }
            if (r.TryGetProperty("segments", out var sg) && sg.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sg.EnumerateArray())
                {
                    double t0 = s.TryGetProperty("t0", out var a) ? a.GetDouble() : 0;
                    double t1 = s.TryGetProperty("t1", out var b) ? b.GetDouble() : 0;
                    double peak = s.TryGetProperty("peak", out var c2) ? c2.GetDouble() : 0;
                    if (t1 < t0) continue;
                    doc.Segments.Add(new SegmentRow
                    {
                        T0 = t0,
                        T1 = t1,
                        Peak = peak,
                        RmsMean = MeanOf(doc, "rms", t0, t1),
                        HighMean = MeanOf(doc, "high", t0, t1),
                        LowMean = MeanOf(doc, "low", t0, t1)
                    });
                }
            }
        }
        catch { }

        return doc;
    }

    private static void ReadDict(JsonElement r, string name, Dictionary<string, object> into)
    {
        if (!r.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return;
        ReadDictFlat(el, into, "");
    }

    private static void ReadDictFlat(JsonElement el, Dictionary<string, object> into, string prefix)
    {
        foreach (var p in el.EnumerateObject())
        {
            string key = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
            if (p.Value.ValueKind == JsonValueKind.Number) into[key] = p.Value.GetDouble();
            else if (p.Value.ValueKind == JsonValueKind.String) into[key] = p.Value.GetString() ?? "";
            else if (p.Value.ValueKind == JsonValueKind.True || p.Value.ValueKind == JsonValueKind.False) into[key] = p.Value.GetBoolean();
            else if (p.Value.ValueKind == JsonValueKind.Object) ReadDictFlat(p.Value, into, key);
        }
    }

    private static float MeanOf(LdteDoc doc, string name, double t0, double t1)
    {
        var col = doc.Cols.FirstOrDefault(c => c.Name == name);
        if (col == null || doc.Duration <= 0) return 0;
        int bins = col.Bins.Length;
        int a = (int)(t0 / doc.Duration * bins);
        int b = (int)(t1 / doc.Duration * bins);
        a = Math.Max(0, Math.Min(bins - 1, a));
        b = Math.Max(a + 1, Math.Min(bins, b));
        double sum = 0;
        for (int i = a; i < b; i++) sum += col.Bins[i];
        return (float)(sum / (b - a));
    }

    public static string DescribeFlags(uint flags)
    {
        var parts = new List<string>();
        if ((flags & 1) != 0) parts.Add("16ms");
        if ((flags & 2) != 0) parts.Add("响度");
        if ((flags & 4) != 0) parts.Add("立体声");
        return parts.Count > 0 ? string.Join("/", parts) : "0x" + flags.ToString("x");
    }
}

internal sealed class ChannelChart : Panel
{
    public ChannelData Data;
    public double Duration;
    private double _pos;

    public ChannelChart()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(12, 19, 34);
        ResizeRedraw = true;
        Margin = new Padding(0, 0, 0, 8);
    }

    public void SetPosition(double fraction)
    {
        _pos = fraction;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Data == null) return;
        int pad = 12;
        using (var br = new SolidBrush(Data.Color))
            g.DrawString(Data.Title + "  " + Data.Name, Font, br, pad, 6);
        if (Width > 460)
            using (var br2 = new SolidBrush(Color.FromArgb(130, 155, 190)))
                g.DrawString(Data.Desc, Font, br2, pad + 130, 6);
        if (Width > 620)
        {
            string right = "max " + Data.Max.ToString("0.000") + "  ·  avg " + Data.Avg.ToString("0.000");
            var sz = g.MeasureString(right, Font);
            using var br3 = new SolidBrush(Color.FromArgb(160, 185, 220));
            g.DrawString(right, Font, br3, Width - sz.Width - pad, 6);
        }

        int top = 26, h = Height - top - 22, w = Width - pad * 2;
        if (w < 10 || h < 10) return;
        var rect = new Rectangle(pad, top, w, h);
        using (var bg = new SolidBrush(Color.FromArgb(9, 14, 24))) g.FillRectangle(bg, rect);
        using (var grid = new Pen(Color.FromArgb(26, 38, 58)))
        {
            for (int i = 1; i < 4; i++)
            {
                int y = rect.Top + rect.Height * i / 4;
                g.DrawLine(grid, rect.Left, y, rect.Right, y);
            }
        }

        var bins = Data.Bins;
        if (bins != null && bins.Length > 0)
        {
            using var pen = new Pen(Data.Color, 1.2f);
            var pts = new List<PointF>(w);
            for (int x = 0; x < w; x++)
            {
                int a = (int)((long)x * bins.Length / w);
                int b = (int)((long)(x + 1) * bins.Length / w);
                float m = 0;
                for (int i = a; i < b && i < bins.Length; i++) if (bins[i] > m) m = bins[i];
                pts.Add(new PointF(rect.Left + x, rect.Top + rect.Height - Math.Min(1f, m) * (rect.Height - 2)));
            }
            if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
        }

        int px = rect.Left + (int)(Math.Max(0, Math.Min(1, _pos)) * (w - 1));
        using (var play = new Pen(Color.FromArgb(255, 205, 120), 1.5f)) g.DrawLine(play, px, rect.Top, px, rect.Bottom);
        using (var br4 = new SolidBrush(Color.FromArgb(120, 145, 180)))
        {
            g.DrawString("0s", Font, br4, pad, Height - 18);
            string t = fmt(Duration);
            var ts = g.MeasureString(t, Font);
            g.DrawString(t, Font, br4, Width - pad - ts.Width, Height - 18);
        }
    }

    private static string fmt(double s)
    {
        if (s < 60) return s.ToString("0.0") + "s";
        return (s / 60).ToString("0.0") + "m";
    }
}

internal sealed class SegmentBar : Panel
{
    public List<SegmentRow> Segments = new();
    public double Duration;

    public SegmentBar()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(10, 16, 28);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Duration <= 0) return;
        int w = Width, h = Height;
        foreach (var s in Segments)
        {
            int x0 = (int)(Math.Max(0, Math.Min(1, s.T0 / Duration)) * w);
            int x1 = (int)(Math.Max(0, Math.Min(1, s.T1 / Duration)) * w);
            if (x1 <= x0) x1 = x0 + 1;
            int alpha = 90 + (int)(120 * Math.Min(1, s.Peak));
            using var br = new SolidBrush(Color.FromArgb(Math.Min(235, alpha), 228, 96, 96));
            g.FillRectangle(br, x0, 2, Math.Max(1, x1 - x0), h - 4);
        }
    }
}

internal sealed class ClipRef
{
    public string Id = "";
    public string File = "";
    public double Start;
    public double End;
    public bool Full;
    public string Label => (Full ? "全录制" : "片段") + " " + Start.ToString("0.0") + "s–" + End.ToString("0.0") + "s";
}

internal static class WavUtil
{
    public static (int Channels, int Rate, double Duration, float[] Env) Parse(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new InvalidDataException("不是有效 WAV");
        int off = 12, channels = 0, rate = 0, bits = 0, dataOff = -1, dataLen = 0;
        while (off + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, off, 4);
            int sz = BitConverter.ToInt32(wav, off + 4);
            off += 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToUInt16(wav, off + 2);
                rate = BitConverter.ToInt32(wav, off + 4);
                bits = BitConverter.ToUInt16(wav, off + 14);
            }
            else if (id == "data")
            {
                dataOff = off;
                dataLen = Math.Min(sz, wav.Length - off);
                break;
            }
            if (sz < 0) break;
            off += sz + (sz % 2);
        }
        if (dataOff < 0 || channels <= 0 || rate <= 0 || bits != 16) throw new InvalidDataException("仅支持 16 位 PCM WAV");
        int frameBytes = channels * 2;
        int frames = dataLen / frameBytes;
        var env = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float m = 0;
            int basePos = dataOff + i * frameBytes;
            for (int c = 0; c < channels; c++)
            {
                short v = BitConverter.ToInt16(wav, basePos + c * 2);
                float a = Math.Abs(v / 32768f);
                if (a > m) m = a;
            }
            env[i] = m;
        }
        return (channels, rate, frames / (double)rate, env);
    }

    public static float[] Bin(float[] env, int bins)
    {
        var outArr = new float[bins];
        if (env == null || env.Length == 0) return outArr;
        for (int x = 0; x < bins; x++)
        {
            int a = (int)((long)x * env.Length / bins);
            int b = (int)((long)(x + 1) * env.Length / bins);
            float m = 0;
            for (int i = a; i < b && i < env.Length; i++) if (env[i] > m) m = env[i];
            outArr[x] = m;
        }
        return outArr;
    }
}

internal sealed class MainForm : Form
{
    private readonly Label _splashText = new();
    private readonly Panel _splash = new();

    private readonly Label _topInfo = new();
    private readonly TextBox _dirBox = new();
    private readonly ListView _list = new();
    private readonly Label _scanInfo = new();
    private readonly Button _listBack = new();

    private readonly TextBox _key = new();
    private readonly Label _keyState = new();
    private readonly ComboBox _audioSelect = new();
    private readonly Button _playBtn = new();
    private readonly Button _stopBtn = new();
    private readonly Button _openAudioBtn = new();

    private readonly Button[] _tabBtns = new Button[4];
    private readonly Panel[] _tabPages = new Panel[4];
    private readonly Panel _tabHost = new();

    private readonly TextBox _col1 = new();
    private readonly TextBox _col2 = new();
    private readonly TextBox _col3 = new();
    private readonly Label _trackInfo = new();

    private readonly FlowLayoutPanel _charts = new();
    private readonly SegmentBar _segBar = new();
    private readonly ListView _segments = new();
    private readonly RichTextBox _headerBox = new();

    private readonly Label _status = new();

    private string _dir = "";
    private LdteDoc _doc;
    private LdteDoc _sysDoc;
    private readonly List<ClipRef> _clips = new();
    private readonly List<ChannelChart> _chartList = new();

    private byte[] _master;
    private dynamic _wmp;
    private string _tempWav;
    private double _clipStart;
    private double _clipEnd;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private bool _playing;

    private string _clipFailedPath = "";
    private string _cloudBase = "";
    private string _cloudSecret = "";
    private string _cloudPages = "";
    private string _cloudToken = "";
    private string _cloudTokenBase = "";
    private long _cloudTokenExpires;
    private int _cloudHours = 23;
    private string _cloudNote = "";
    private string _srcAudioPath = "";
    private string _keySource = "";
    private long _batExp;

    private bool _trackMode;
    private bool _clipMode;
    private string _clipFile = "";
    private long _clipEncBytes;
    private long _clipPlainBytes;
    private string _clipHow = "";
    private string _clipError = "";
    private int _clipChannels;
    private int _clipRate;
    private int _clipFrames;
    private string _trackInfoOverride = "";
    private bool _suppressListOpen;

    private const long CloudMaxBytes = 9L * 1024 * 1024;

    public MainForm(string initialFile)
    {
        Text = "LDTE 全音频训练档案";
        Width = 1260;
        Height = 860;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 18, 30);
        ForeColor = Color.FromArgb(220, 230, 245);
        Font = new Font("Microsoft YaHei UI", 9f);
        ClampToScreen();

        BuildSplash();
        BuildUi();
        Controls.Add(_splash);
        _splash.Dock = DockStyle.Fill;
        _splash.BringToFront();

        _timer.Tick += (_, _) => UpdatePlayback();
        FormClosing += (_, _) => CleanupAudio();

        Shown += (_, _) =>
        {
            BeginInvoke(new Action(async () =>
            {
                _dir = FindDir();
                _dirBox.Text = _dir;
                LoadDefaultKey();
                await ScanAsync();
                _splash.Visible = false;
                if (!string.IsNullOrEmpty(initialFile) && File.Exists(initialFile)) OpenPath(initialFile);
            }));
        };
    }

    private void ClampToScreen()
    {
        try
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int maxW = Math.Max(760, wa.Width - 24);
            int maxH = Math.Max(520, wa.Height - 48);
            Width = Math.Min(Width, maxW);
            Height = Math.Min(Height, maxH);
            MinimumSize = new Size(Math.Min(900, Width), Math.Min(600, Height));
        }
        catch { }
    }

    private void BuildSplash()
    {
        var box = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 18, 30) };
        var t = new Label
        {
            Text = "LDTE 全音频训练档案",
            Dock = DockStyle.Top,
            Height = 150,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 220, 245)
        };
        _splashText.Text = "正在检测 LDTE训练 目录…";
        _splashText.Dock = DockStyle.Fill;
        _splashText.TextAlign = ContentAlignment.MiddleCenter;
        _splashText.ForeColor = Color.FromArgb(150, 175, 210);
        box.Controls.Add(_splashText);
        box.Controls.Add(t);
        _splash.Controls.Add(box);
    }

    private void BuildUi()
    {
        var left = new Panel { Dock = DockStyle.Left, Width = 460, BackColor = Color.FromArgb(12, 18, 30) };
        var lblDir = new Label { Text = "训练目录", Left = 12, Top = 10, Width = 90, ForeColor = Color.FromArgb(150, 175, 210) };
        _dirBox.Left = 12; _dirBox.Top = 32; _dirBox.Width = 340; _dirBox.ReadOnly = true;
        _dirBox.BackColor = Color.FromArgb(10, 16, 28); _dirBox.ForeColor = Color.FromArgb(190, 210, 235);
        var pick = new Button { Text = "…", Left = 360, Top = 30, Width = 30, Height = 26, FlatStyle = FlatStyle.Flat };
        pick.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK) { _dir = dlg.SelectedPath; _dirBox.Text = _dir; LoadDefaultKey(); _ = ScanAsync(); }
        };
        var refresh = new Button { Text = "刷新", Left = 396, Top = 30, Width = 50, Height = 26, FlatStyle = FlatStyle.Flat };
        refresh.Click += (_, _) => _ = ScanAsync();

        _scanInfo.Left = 12; _scanInfo.Top = 62; _scanInfo.Width = 340; _scanInfo.Height = 20;
        _scanInfo.AutoSize = false;
        _scanInfo.AutoEllipsis = true;
        _scanInfo.ForeColor = Color.FromArgb(140, 165, 200);
        _scanInfo.Text = "—";

        _listBack.Text = "← 文件";
        _listBack.Left = 360; _listBack.Top = 58; _listBack.Width = 86; _listBack.Height = 24;
        _listBack.FlatStyle = FlatStyle.Flat;
        _listBack.Visible = false;
        _listBack.Click += (_, _) => ShowFileList();

        _list.Left = 12; _list.Top = 88; _list.Width = 436; _list.Height = Height - 140;
        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.Columns.Add("文件 / 曲目", 150);
        _list.Columns.Add("修改时间", 74);
        _list.Columns.Add("大小", 78);
        _list.BackColor = Color.FromArgb(10, 16, 28); _list.ForeColor = Color.FromArgb(210, 225, 245);
        _list.Font = new Font("Microsoft YaHei UI", 8.5f);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        _list.OwnerDraw = true;
        _list.DrawColumnHeader += DarkHeader;
        _list.DrawItem += DarkItem;
        _list.DrawSubItem += DarkSubItem;
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressListOpen || !_list.Focused || _list.SelectedItems.Count == 0) return;
            OpenTag(_list.SelectedItems[0].Tag as string);
        };
        _list.MouseClick += (_, e) =>
        {
            if (_suppressListOpen) return;
            var item = _list.GetItemAt(e.X, e.Y);
            if (item == null) return;
            if (item.Selected) { OpenTag(item.Tag as string); return; }
            item.Selected = true;
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || _suppressListOpen) return;
            if (_list.SelectedItems.Count != 1) return;
            OpenTag(_list.SelectedItems[0].Tag as string);
        };
        _list.Resize += (_, _) => FitListColumns();

        left.Controls.AddRange(new Control[] { lblDir, _dirBox, pick, refresh, _scanInfo, _listBack, _list });

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(16, 22, 38), Padding = new Padding(12, 9, 12, 9) };
        _topInfo.Dock = DockStyle.Fill;
        _topInfo.AutoSize = false;
        _topInfo.AutoEllipsis = true;
        _topInfo.TextAlign = ContentAlignment.MiddleLeft;
        _topInfo.ForeColor = Color.FromArgb(170, 195, 225);
        _topInfo.Text = "未打开档案";
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0)
        };
        Button ToolBtn(string text, EventHandler click, int w = 86)
        {
            var b = new Button { Text = text, Width = w, Height = 28, FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 0, 3, 0) };
            b.Click += click;
            return b;
        }
        tools.Controls.Add(ToolBtn("导出摘要", (_, _) => ExportSummary()));
        tools.Controls.Add(ToolBtn("导出 CSV", (_, _) => ExportCsv()));
        tools.Controls.Add(ToolBtn("刷新", (_, _) => _ = ScanAsync(), 62));
        tools.Controls.Add(ToolBtn("选择目录", (_, _) => pick.PerformClick()));
        tools.Controls.Add(ToolBtn("打开文件", (_, _) => PickFiles()));
        toolbar.Controls.Add(_topInfo);
        toolbar.Controls.Add(tools);

        var audioBar = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Color.FromArgb(14, 20, 33), Padding = new Padding(12, 6, 12, 6) };
        var keyRow = new Panel { Dock = DockStyle.Top, Height = 34 };
        var playRow = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        var lblKey = new Label { Text = "密钥", Dock = DockStyle.Left, Width = 40, ForeColor = Color.FromArgb(150, 175, 210), TextAlign = ContentAlignment.MiddleLeft };
        _key.Dock = DockStyle.Fill;
        _key.BorderStyle = BorderStyle.FixedSingle;
        _key.BackColor = Color.FromArgb(10, 16, 28); _key.ForeColor = Color.FromArgb(160, 205, 255);
        _key.PlaceholderText = "粘贴 BAT1 离线密钥或 CFK1 云端密钥，留空则使用默认配置";
        var bVerify = new Button { Text = "验证", Dock = DockStyle.Right, Width = 62, FlatStyle = FlatStyle.Flat };
        bVerify.Click += (_, _) => VerifyKey();
        _keyState.Dock = DockStyle.Right; _keyState.Width = 260; _keyState.AutoSize = false;
        _keyState.AutoEllipsis = true;
        _keyState.TextAlign = ContentAlignment.MiddleLeft;
        _keyState.ForeColor = Color.FromArgb(150, 175, 210);
        _keyState.Text = "未验证";
        keyRow.Controls.Add(_key);
        keyRow.Controls.Add(_keyState);
        keyRow.Controls.Add(bVerify);
        keyRow.Controls.Add(lblKey);

        _audioSelect.Dock = DockStyle.Fill;
        _audioSelect.DropDownStyle = ComboBoxStyle.DropDownList;
        _audioSelect.BackColor = Color.FromArgb(10, 16, 28); _audioSelect.ForeColor = Color.FromArgb(220, 230, 245);
        _playBtn.Text = "▶ 试听"; _playBtn.Width = 76; _playBtn.Height = 26; _playBtn.FlatStyle = FlatStyle.Flat; _playBtn.Dock = DockStyle.Right;
        _playBtn.Click += (_, _) => TogglePlay();
        _stopBtn.Text = "■ 停止"; _stopBtn.Width = 72; _stopBtn.Height = 26; _stopBtn.FlatStyle = FlatStyle.Flat; _stopBtn.Dock = DockStyle.Right;
        _stopBtn.Click += (_, _) => StopAudio();
        _openAudioBtn.Text = "打开音频"; _openAudioBtn.Width = 84; _openAudioBtn.Height = 26; _openAudioBtn.FlatStyle = FlatStyle.Flat; _openAudioBtn.Dock = DockStyle.Right;
        _openAudioBtn.Click += (_, _) => OpenLocalAudio();
        var lblAudio = new Label { Text = "音频", Dock = DockStyle.Left, Width = 40, ForeColor = Color.FromArgb(150, 175, 210), TextAlign = ContentAlignment.MiddleLeft };
        playRow.Controls.Add(_audioSelect);
        playRow.Controls.Add(_openAudioBtn);
        playRow.Controls.Add(_stopBtn);
        playRow.Controls.Add(_playBtn);
        playRow.Controls.Add(lblAudio);

        audioBar.Controls.Add(keyRow);
        audioBar.Controls.Add(playRow);

        var tabBar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Color.FromArgb(16, 22, 38) };
        string[] names = { "概览", "通道图表", "峰值区段", "头部数据" };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            var b = new Button
            {
                Text = names[i],
                Left = 14 + i * 92,
                Top = 6,
                Width = 86,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = i == 0 ? Color.FromArgb(37, 62, 122) : Color.FromArgb(20, 28, 44),
                ForeColor = Color.FromArgb(215, 230, 250)
            };
            b.Click += (_, _) => SelectTab(idx);
            _tabBtns[i] = b;
            tabBar.Controls.Add(b);
        }

        _tabHost.Dock = DockStyle.Fill;
        _tabHost.BackColor = Color.FromArgb(13, 18, 30);

        var overview = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 18, 30), Padding = new Padding(14, 10, 14, 12) };
        _trackInfo.Dock = DockStyle.Top;
        _trackInfo.Height = 24;
        _trackInfo.AutoSize = false;
        _trackInfo.AutoEllipsis = true;
        _trackInfo.TextAlign = ContentAlignment.MiddleLeft;
        _trackInfo.ForeColor = Color.FromArgb(170, 200, 235);
        _trackInfo.Text = "点击左侧档案查看详情";
        var cols = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 4, 0, 0) };
        for (int i = 0; i < 3; i++) cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        _col1.Multiline = true; _col1.ReadOnly = true; _col1.WordWrap = false; _col1.ScrollBars = ScrollBars.Both; _col1.Dock = DockStyle.Fill; _col1.Margin = new Padding(4);
        _col2.Multiline = true; _col2.ReadOnly = true; _col2.WordWrap = false; _col2.ScrollBars = ScrollBars.Both; _col2.Dock = DockStyle.Fill; _col2.Margin = new Padding(4);
        _col3.Multiline = true; _col3.ReadOnly = true; _col3.WordWrap = false; _col3.ScrollBars = ScrollBars.Both; _col3.Dock = DockStyle.Fill; _col3.Margin = new Padding(4);
        foreach (var tb in new[] { _col1, _col2, _col3 })
        {
            tb.BackColor = Color.FromArgb(11, 17, 28);
            tb.ForeColor = Color.FromArgb(195, 215, 240);
            tb.Font = new Font("Consolas", 9.5f);
            tb.BorderStyle = BorderStyle.None;
            tb.TabStop = false;
        }
        cols.Controls.Add(_col1, 0, 0);
        cols.Controls.Add(_col2, 1, 0);
        cols.Controls.Add(_col3, 2, 0);
        overview.Controls.Add(cols);
        overview.Controls.Add(_trackInfo);

        var chPage = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 18, 30) };
        _charts.Dock = DockStyle.Fill;
        _charts.FlowDirection = FlowDirection.TopDown;
        _charts.WrapContents = false;
        _charts.AutoScroll = true;
        _charts.BackColor = Color.FromArgb(13, 18, 30);
        _charts.Padding = new Padding(10, 6, 10, 6);
        _charts.SizeChanged += (_, _) =>
        {
            foreach (var ch in _chartList) ch.Width = Math.Max(300, _charts.ClientSize.Width - 30);
        };
        chPage.Controls.Add(_charts);

        var segPage = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 18, 30), Padding = new Padding(14, 10, 14, 12) };
        _segments.Dock = DockStyle.Fill;
        _segments.View = View.Details; _segments.FullRowSelect = true; _segments.MultiSelect = false;
        _segments.Columns.Add("开始", 90);
        _segments.Columns.Add("时长", 80);
        _segments.Columns.Add("结束", 90);
        _segments.Columns.Add("峰值", 90);
        _segments.Columns.Add("RMS", 90);
        _segments.Columns.Add("高频均值", 100);
        _segments.Columns.Add("低频均值", 100);
        _segments.BackColor = Color.FromArgb(10, 16, 28); _segments.ForeColor = Color.FromArgb(210, 225, 245);
        _segments.OwnerDraw = true;
        _segments.DrawColumnHeader += DarkHeader;
        _segments.DrawItem += DarkItem;
        _segments.DrawSubItem += DarkSubItem;
        _segments.DoubleClick += (_, _) => PlaySegment();
        _segments.Resize += (_, _) => FitSegmentColumns();
        _segBar.Dock = DockStyle.Top;
        _segBar.Height = 56;
        var segGap = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.FromArgb(13, 18, 30) };
        segPage.Controls.Add(_segments);
        segPage.Controls.Add(segGap);
        segPage.Controls.Add(_segBar);

        var headPage = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 18, 30) };
        _headerBox.Dock = DockStyle.Fill;
        _headerBox.ReadOnly = true;
        _headerBox.BackColor = Color.FromArgb(10, 16, 28);
        _headerBox.ForeColor = Color.FromArgb(190, 215, 240);
        _headerBox.Font = new Font("Consolas", 9.5f);
        _headerBox.TabStop = false;
        headPage.Controls.Add(_headerBox);

        _tabPages[0] = overview; _tabPages[1] = chPage; _tabPages[2] = segPage; _tabPages[3] = headPage;
        foreach (var p in _tabPages) { p.Dock = DockStyle.Fill; p.Visible = false; _tabHost.Controls.Add(p); }
        _tabPages[0].Visible = true;

        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 18, 30) };
        right.Controls.Add(_tabHost);
        right.Controls.Add(tabBar);
        right.Controls.Add(audioBar);
        right.Controls.Add(toolbar);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 24;
        _status.BackColor = Color.FromArgb(11, 16, 26);
        _status.ForeColor = Color.FromArgb(140, 165, 200);
        _status.AutoEllipsis = true;
        _status.Text = "就绪";

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(_status);
    }

    private void DarkHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var bg = new SolidBrush(Color.FromArgb(18, 26, 42))) e.Graphics.FillRectangle(bg, e.Bounds);
        using (var pen = new Pen(Color.FromArgb(36, 48, 70)))
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var lv = sender as ListView ?? _list;
        var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 8), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Header.Text ?? "", lv.Font, r, Color.FromArgb(150, 175, 210),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private void DarkItem(object sender, DrawListViewItemEventArgs e)
    {
    }

    private void DarkSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        var lv = sender as ListView;
        bool sel = e.Item.Selected;
        Color bg = sel ? Color.FromArgb(37, 62, 122)
            : (e.ItemIndex % 2 == 0 ? Color.FromArgb(12, 19, 32) : Color.FromArgb(10, 16, 27));
        using (var br = new SolidBrush(bg)) e.Graphics.FillRectangle(br, e.Bounds);
        var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 8), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text ?? "", lv?.Font ?? Font, r,
            sel ? Color.White : Color.FromArgb(205, 222, 244),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private void FitListColumns()
    {
        try
        {
            if (_list == null || !_list.IsHandleCreated) return;
            if (_list.Columns.Count < 3) return;
            int w = _list.ClientSize.Width;
            if (w < 200) return;
            int t = 112;
            int s = 92;
            _list.Columns[0].Width = Math.Max(150, w - t - s - 4);
            _list.Columns[1].Width = t;
            _list.Columns[2].Width = s;
        }
        catch { }
    }

    private void FitSegmentColumns()
    {
        try
        {
            if (_segments == null || !_segments.IsHandleCreated) return;
            int w = _segments.ClientSize.Width;
            if (w < 300 || _segments.Columns.Count < 7) return;
            double[] pct = { 0.12, 0.11, 0.12, 0.13, 0.13, 0.195, 0.195 };
            for (int i = 0; i < 7; i++)
                _segments.Columns[i].Width = Math.Max(48, (int)(w * pct[i]));
        }
        catch { }
    }

    private void SetListHeaders(string a, string b, string c)
    {
        if (_list.Columns.Count < 3) return;
        _list.Columns[0].Text = a;
        _list.Columns[1].Text = b;
        _list.Columns[2].Text = c;
        _list.Columns[0].Width = Math.Max(170, _list.ClientSize.Width - 208);
        _list.Columns[1].Width = 112;
        _list.Columns[2].Width = 92;
        FitListColumns();
        try { _list.BeginInvoke(new Action(FitListColumns)); } catch { }
        _list.Invalidate(true);
    }

    private void SelectTab(int idx)
    {
        for (int i = 0; i < 4; i++)
        {
            _tabPages[i].Visible = i == idx;
            _tabBtns[i].BackColor = i == idx ? Color.FromArgb(37, 62, 122) : Color.FromArgb(20, 28, 44);
        }
        if (idx == 0) RenderOverview();
    }

    private void ShowFileList()
    {
        _trackMode = false;
        _sysDoc = null;
        _listBack.Visible = false;
        _ = ScanAsync();
        Log("已返回文件列表");
    }

    private void Log(string msg)
    {
        _status.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
    }

    private string FindDir()
    {
        var probes = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "LDTE训练"),
            Path.Combine(AppContext.BaseDirectory, "app", "LDTE训练"),
            Path.Combine(Environment.CurrentDirectory, "LDTE训练")
        };
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 4 && d != null; i++)
            {
                probes.Add(Path.Combine(d.FullName, "LDTE训练"));
                d = d.Parent;
            }
        }
        catch { }
        foreach (var c in probes)
        {
            try
            {
                string full = Path.GetFullPath(c);
                if (!Directory.Exists(full)) continue;
                if (Directory.Exists(Path.Combine(full, "_keys")) || Directory.GetFiles(full, "*.ldte").Length > 0)
                    return full;
            }
            catch { }
        }
        foreach (var c in probes)
        {
            try { if (Directory.Exists(Path.GetFullPath(c))) return Path.GetFullPath(c); } catch { }
        }
        return "";
    }

    private async Task ScanAsync()
    {
        _list.Items.Clear();
        _trackMode = false;
        _listBack.Visible = false;
        _scanInfo.Text = "正在扫描…";
        if (_dir.Length == 0 || !Directory.Exists(_dir)) { _scanInfo.Text = "未找到 LDTE训练 目录"; return; }

        var clips = new Dictionary<string, (string Name, double Start, double End, bool Full)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string idx = Path.Combine(_dir, "clips", "index.json");
            if (File.Exists(idx))
            {
                using var jd = JsonDocument.Parse(File.ReadAllText(idx));
                if (jd.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in jd.RootElement.EnumerateArray())
                    {
                        string file = it.TryGetProperty("file", out var f) ? (f.GetString() ?? "") : "";
                        if (file.Length == 0) continue;
                        string name = it.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
                        double s0 = it.TryGetProperty("start", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : 0;
                        double s1 = it.TryGetProperty("end", out var ev) && ev.ValueKind == JsonValueKind.Number ? ev.GetDouble() : 0;
                        bool full = it.TryGetProperty("full", out var fl) && fl.ValueKind == JsonValueKind.True;
                        clips[Path.GetFileName(file)] = (name, s0, s1, full);
                    }
                }
            }
        }
        catch { }

        var files = await Task.Run(() =>
        {
            var list = new List<string>();
            try
            {
                list.AddRange(Directory.GetFiles(_dir, "*.ldte", SearchOption.AllDirectories));
                list.AddRange(Directory.GetFiles(_dir, "*.ldtc", SearchOption.AllDirectories));
            }
            catch { }
            return list.ToArray();
        });
        var ordered = files.Select(f => new FileInfo(f))
            .OrderBy(f => f.Extension.Equals(".ldtc", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(f => f.LastWriteTime)
            .ToList();

        _suppressListOpen = true;
        try
        {
            foreach (var fi in ordered)
            {
                string label = fi.Name;
                if (fi.Extension.Equals(".ldtc", StringComparison.OrdinalIgnoreCase) && clips.TryGetValue(fi.Name, out var c))
                {
                    string title = c.Name.Length > 0 ? c.Name : Path.GetFileNameWithoutExtension(fi.Name);
                    label = "片段 · " + title + (c.Full ? "（全录制）" : "");
                }
                var it = new ListViewItem(label);
                it.SubItems.Add(FormatTime(fi.LastWriteTime));
                it.SubItems.Add(SizeText(fi.Length));
                it.Tag = fi.FullName;
                _list.Items.Add(it);
            }
            _list.SelectedItems.Clear();
        }
        finally { _suppressListOpen = false; }

        SetListHeaders("文件 / 曲目", "修改时间", "大小");
        _scanInfo.Text = "档案 " + ordered.Count(x => !x.Extension.Equals(".ldtc", StringComparison.OrdinalIgnoreCase))
            + " 个 · 加密片段 " + ordered.Count(x => x.Extension.Equals(".ldtc", StringComparison.OrdinalIgnoreCase)) + " 个";
    }

    private void PopulateTracks(LdteDoc doc)
    {
        _trackMode = doc.Tracks.Count > 0;
        if (!_trackMode) return;
        _sysDoc = doc;
        _suppressListOpen = true;
        try
        {
            _list.Items.Clear();
            foreach (var t in doc.Tracks)
            {
                var it = new ListViewItem(string.IsNullOrEmpty(t.Name) ? t.Key : t.Name);
                it.SubItems.Add(TimeText(t.Seconds));
                double bytes = t.Frames > 0 ? t.Frames * doc.Channels * 4 : t.Seconds * 1000 / Math.Max(1, doc.FrameMs) * doc.Channels * 4;
                it.SubItems.Add(SizeText((long)bytes));
                it.Tag = "track:" + t.Key;
                _list.Items.Add(it);
            }
            _list.SelectedItems.Clear();
        }
        finally { _suppressListOpen = false; }
        _listBack.Visible = true;
        SetListHeaders("曲目", "时长", "档案大小");
        _scanInfo.Text = "合集曲目 " + doc.Tracks.Count + " 首";
    }

    private void SelectTrack(string key)
    {
        var sys = _sysDoc ?? _doc;
        if (sys == null) return;
        var t = sys.Tracks.FirstOrDefault(x => x.Key == key);
        if (t == null) return;
        _trackInfo.Text = "曲目：" + (t.Name.Length > 0 ? t.Name : t.Key) + " · " + TimeText(t.Seconds)
            + " · " + t.Frames.ToString("N0") + " 帧 · 峰值 " + t.Peak.ToString("0.000")
            + " · RMS " + t.Rms.ToString("0.000") + " · 峰均比 " + t.Crest.ToString("0.00")
            + (t.Lufs != 0 ? " · LUFS " + t.Lufs.ToString("0.0") : "")
            + (t.Updated.Length > 0 ? " · 更新 " + t.Updated : "");
        _trackInfoOverride = _trackInfo.Text;
        string per = Path.Combine(_dir, LdteCrypto.LdteFileName(key) + ".ldte");
        if (File.Exists(per)) { LoadDoc(per, keepList: true); return; }
        _clipMode = false;
        _doc = sys;
        SelectTab(0);
        Log("该曲目尚无单曲档案，仅合集内有数据：" + (t.Name.Length > 0 ? t.Name : t.Key));
    }

    private void HighlightListPath(string path)
    {
        if (_trackMode || _suppressListOpen || string.IsNullOrEmpty(path)) return;
        _suppressListOpen = true;
        try
        {
            foreach (ListViewItem it in _list.Items)
            {
                if (!string.Equals(it.Tag as string, path, StringComparison.OrdinalIgnoreCase)) continue;
                it.Selected = true;
                it.EnsureVisible();
                break;
            }
        }
        finally { _suppressListOpen = false; }
    }

    private void OpenTag(string p)
    {
        if (string.IsNullOrEmpty(p)) return;
        if (p.StartsWith("track:", StringComparison.Ordinal)) { SelectTrack(p.Substring(6)); return; }
        if (p == _clipFailedPath) { ShowNeedKey(); return; }
        OpenPath(p);
    }

    private void OpenPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".ldtc") { _ = OpenClipAsync(path); return; }
        LoadDoc(path);
    }

    private void ShowNeedKey()
    {
        var sb = new StringBuilder();
        sb.AppendLine("该片段已加密，需要密钥才能查看。");
        sb.AppendLine();
        sb.AppendLine("本机主密钥：" + (_master != null ? "已载入" : "未找到（_keys\\master.key）"));
        sb.AppendLine("云端配置：" + (_cloudBase.Length > 0 && _cloudSecret.Length > 0
            ? "已配置 " + _cloudBase + "（仅支持 ≤9 MB 片段）"
            : "未配置（_keys\\cloud.json）"));
        if (_clipError.Length > 0) sb.AppendLine("失败原因：" + _clipError);
        else if (_cloudNote.Length > 0) sb.AppendLine("云端提示：" + _cloudNote);
        sb.AppendLine();
        sb.AppendLine("可粘贴 BAT1 离线密钥或 CFK1 云端密钥后点「验证」。");
        MessageBox.Show(this, sb.ToString(), "需要解密", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _key.Focus();
    }

    private IEnumerable<string> KeyDirs()
    {
        var list = new List<string>();
        if (_dir.Length > 0) list.Add(_dir);
        var probes = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "LDTE训练"),
            Path.Combine(AppContext.BaseDirectory, "app", "LDTE训练"),
            Path.Combine(Environment.CurrentDirectory, "LDTE训练")
        };
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 4 && d != null; i++)
            {
                probes.Add(Path.Combine(d.FullName, "LDTE训练"));
                d = d.Parent;
            }
        }
        catch { }
        foreach (var p in probes)
        {
            try { if (Directory.Exists(p)) list.Add(Path.GetFullPath(p)); } catch { }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ReloadLocalKey()
    {
        if (_master != null) return;
        foreach (var d in KeyDirs())
        {
            foreach (var f in new[] { Path.Combine(d, "_keys", "master.key"), Path.Combine(d, "master.key") })
            {
                if (!File.Exists(f)) continue;
                try
                {
                    var b = File.ReadAllBytes(f);
                    if (b.Length == 32) { _master = b; if (_keySource.Length == 0) _keySource = "file"; return; }
                }
                catch { }
            }
        }
    }

    private void ReloadCloudConfig()
    {
        foreach (var d in KeyDirs())
        {
            foreach (var f in new[] { Path.Combine(d, "_keys", "cloud.json"), Path.Combine(d, "cloud.json") })
            {
                if (!File.Exists(f)) continue;
                try
                {
                    using var jd = JsonDocument.Parse(File.ReadAllText(f));
                    var r = jd.RootElement;
                    string b = r.TryGetProperty("base", out var bv) ? (bv.GetString() ?? "") : "";
                    string a = r.TryGetProperty("adminSecret", out var av) ? (av.GetString() ?? "") : "";
                    if (b.Length == 0 || a.Length == 0) continue;
                    _cloudBase = b;
                    _cloudSecret = a;
                    _cloudPages = r.TryGetProperty("pages", out var pv) ? (pv.GetString() ?? "") : "";
                    if (r.TryGetProperty("hours", out var hv) && hv.ValueKind == JsonValueKind.Number)
                        _cloudHours = Math.Max(1, Math.Min(23, (int)hv.GetDouble()));
                    return;
                }
                catch { }
            }
        }
    }

    private string TokenCacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LDTE-Viewer", "cloud_token.json");

    private void LoadCachedToken()
    {
        if (_cloudToken.Length > 0) return;
        try
        {
            if (!File.Exists(TokenCacheFile)) return;
            using var jd = JsonDocument.Parse(File.ReadAllText(TokenCacheFile));
            var r = jd.RootElement;
            string t = r.TryGetProperty("token", out var tv) ? (tv.GetString() ?? "") : "";
            long exp = r.TryGetProperty("expires", out var ev) && ev.ValueKind == JsonValueKind.Number ? ev.GetInt64() : 0;
            string b = r.TryGetProperty("base", out var bv) ? (bv.GetString() ?? "") : "";
            if (t.Length == 0 || exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60) return;
            if (b.Length > 0 && _cloudBase.Length > 0 && !string.Equals(b.TrimEnd('/'), _cloudBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return;
            _cloudToken = t;
            _cloudTokenExpires = exp;
            _cloudTokenBase = _cloudBase;
        }
        catch { }
    }

    private void SaveCachedToken()
    {
        try
        {
            string dir = Path.GetDirectoryName(TokenCacheFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = "{\"token\":\"" + _cloudToken.Replace("\\", "\\\\").Replace("\"", "\\\"")
                + "\",\"expires\":" + _cloudTokenExpires
                + ",\"base\":\"" + _cloudBase.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
            File.WriteAllText(TokenCacheFile, json, Encoding.UTF8);
        }
        catch { }
    }

    private void DropCachedToken()
    {
        _cloudToken = "";
        _cloudTokenExpires = 0;
        _cloudTokenBase = "";
        try { if (File.Exists(TokenCacheFile)) File.Delete(TokenCacheFile); } catch { }
    }

    private static string ExpText(long unix)
    {
        if (unix <= 0) return "未知";
        return DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("MM-dd HH:mm");
    }

    private void LoadDefaultKey()
    {
        ReloadLocalKey();
        ReloadCloudConfig();
        LoadCachedToken();
        UpdateKeyState();
    }

    private void UpdateKeyState()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool master = _master != null;
        bool cloudCfg = _cloudBase.Length > 0 && _cloudSecret.Length > 0;
        _keyState.ForeColor = Color.FromArgb(150, 175, 210);
        if (_keySource == "bat1" && master)
        {
            _keyState.ForeColor = Color.FromArgb(127, 224, 168);
            _keyState.Text = "离线密钥有效 · 到期 " + ExpText(_batExp);
        }
        else if (_keySource == "cloud" && _cloudToken.Length > 0)
        {
            _keyState.ForeColor = Color.FromArgb(127, 224, 168);
            _keyState.Text = "云端密钥有效 · 到期 " + ExpText(_cloudTokenExpires);
        }
        else if (_cloudToken.Length > 0 && _cloudTokenExpires > now)
        {
            _keyState.ForeColor = Color.FromArgb(127, 224, 168);
            _keyState.Text = "云端密钥已缓存 · 到期 " + ExpText(_cloudTokenExpires);
        }
        else if (master && cloudCfg)
        {
            _keyState.ForeColor = Color.FromArgb(127, 224, 168);
            _keyState.Text = "本机主密钥 · 云端备用（" + _cloudHours + "h）";
        }
        else if (master)
        {
            _keyState.ForeColor = Color.FromArgb(127, 224, 168);
            _keyState.Text = "本机主密钥（静默解密）";
        }
        else if (cloudCfg)
        {
            _keyState.ForeColor = Color.FromArgb(255, 205, 120);
            _keyState.Text = "云端密钥（自动签发 " + _cloudHours + "h）";
        }
        else
        {
            _keyState.ForeColor = Color.FromArgb(255, 141, 141);
            _keyState.Text = "未配置密钥";
        }
    }

    private async Task OpenClipAsync(string path)
    {
        StopAudio();
        _clipMode = true;
        _clipFile = path;
        _clipError = "";
        _clipHow = "";
        _clipPlainBytes = 0;
        _clipEncBytes = 0;
        _clipChannels = 0; _clipRate = 0; _clipFrames = 0;
        try { _clipEncBytes = new FileInfo(path).Length; } catch { }
        _doc = null;
        _audioSelect.Items.Clear();
        _segments.Items.Clear();
        _segBar.Segments.Clear();
        _segBar.Duration = 0;
        _segBar.Invalidate();
        foreach (var c in _chartList) { c.Data = null; c.Duration = 0; c.Invalidate(); }
        _topInfo.Text = path;
        _headerBox.Text = "";
        _trackInfo.Text = "加密片段：" + Path.GetFileName(path);
        _clipFailedPath = "";
        RenderClipOverview();
        HighlightListPath(path);
        try
        {
            if (_master == null) ReloadLocalKey();
            if (_master == null && _clipEncBytes > CloudMaxBytes)
            {
                _clipError = "片段 " + (_clipEncBytes / 1048576.0).ToString("0.0")
                    + " MB，超过云端解密上限（网关约 10 MB）；请在本机放置 _keys\\master.key";
                _clipFailedPath = path;
                RenderClipOverview();
                SelectTab(0);
                Log("✘ " + _clipError);
                return;
            }

            byte[] enc = await Task.Run(() => File.ReadAllBytes(path));
            byte[] wav = null;
            if (_master != null)
            {
                try
                {
                    wav = LdteCrypto.DecryptClip(enc, _master);
                    _clipHow = _keySource == "bat1" ? "离线密钥" : "本机主密钥";
                }
                catch { wav = null; }
            }
            if (wav == null && _cloudBase.Length > 0)
            {
                _cloudNote = "";
                wav = await CloudDecryptAsync(enc);
                if (wav != null) { _clipHow = "云端密钥"; _cloudNote = ""; }
            }
            if (wav == null)
            {
                _clipFailedPath = path;
                _clipError = _master == null && _cloudBase.Length == 0
                    ? "没有可用密钥：本机无 master.key，云端也未配置"
                    : (_cloudNote.Length > 0 ? _cloudNote : "密钥不匹配或片段损坏");
                _topInfo.Text = Path.GetFileName(path) + " · 已加密 · 解密失败";
                RenderClipOverview();
                SelectTab(0);
                Log("加密片段未解密：" + Path.GetFileName(path) + " · " + _clipError);
                return;
            }

            var wi = WavUtil.Parse(wav);
            var bins = WavUtil.Bin(wi.Env, 2200);
            double avg = 0;
            foreach (var v in bins) avg += v;
            if (bins.Length > 0) avg /= bins.Length;
            var cd = new ChannelData
            {
                Title = "波形",
                Name = "waveform",
                Desc = "解密后的峰值包络",
                Bins = bins,
                Max = bins.Length > 0 ? bins.Max() : 0,
                Avg = (float)avg,
                Color = Color.FromArgb(120, 190, 250)
            };
            EnsureCharts();
            if (_chartList.Count > 0)
            {
                _chartList[0].Data = cd;
                _chartList[0].Duration = wi.Duration;
                _chartList[0].SetPosition(0);
                for (int i = 1; i < _chartList.Count; i++) { _chartList[i].Data = null; _chartList[i].Duration = 0; _chartList[i].Invalidate(); }
            }
            CleanupAudio();
            _tempWav = Path.Combine(Path.GetTempPath(), "ldte_clip_" + Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(_tempWav, wav);
            _clipStart = 0; _clipEnd = wi.Duration;
            _clipPlainBytes = wav.Length;
            _clipChannels = wi.Channels;
            _clipRate = wi.Rate;
            _clipFrames = (int)Math.Round(wi.Duration * wi.Rate);
            _doc = new LdteDoc { Path = path, Duration = wi.Duration, FrameMs = 0 };
            var seg = new SegmentRow { T0 = 0, T1 = wi.Duration, Peak = cd.Max, RmsMean = (float)avg };
            var si = new ListViewItem("0.00");
            si.SubItems.Add(wi.Duration.ToString("0.00"));
            si.SubItems.Add(wi.Duration.ToString("0.00"));
            si.SubItems.Add(cd.Max.ToString("0.000"));
            si.SubItems.Add(avg.ToString("0.0000"));
            si.SubItems.Add("—");
            si.SubItems.Add("—");
            si.Tag = seg;
            _segments.Items.Clear();
            _segments.Items.Add(si);
            _segBar.Segments = new List<SegmentRow> { seg };
            _segBar.Duration = wi.Duration;
            _segBar.Invalidate();
            FitSegmentColumns();
            _audioSelect.Items.Add("本片段（" + _clipHow + "）");
            _audioSelect.SelectedIndex = 0;
            _topInfo.Text = Path.GetFileName(path) + " · " + (enc.Length / 1048576.0).ToString("0.00") + " → "
                + (wav.Length / 1048576.0).ToString("0.00") + " MB · " + (wi.Rate / 1000.0).ToString("0.#") + " kHz · "
                + TimeText(wi.Duration) + " · " + _clipHow;
            _trackInfo.Text = "加密片段解密成功 · " + Path.GetFileName(path) + " · " + _clipHow;
            _headerBox.Text = string.Join(Environment.NewLine,
                "文件        " + path,
                "类型        LDTC1 加密片段（AES-256-GCM 双层）",
                "加密大小    " + enc.Length.ToString("N0") + " B",
                "解密大小    " + wav.Length.ToString("N0") + " B",
                "解密方式    " + _clipHow,
                "音频格式    PCM 16-bit · " + wi.Channels + " 通道 · " + wi.Rate + " Hz",
                "时长        " + wi.Duration.ToString("0.000") + " s",
                "采样帧数    " + ((int)Math.Round(wi.Duration * wi.Rate)).ToString("N0"),
                "波形峰值    " + (bins.Length > 0 ? bins.Max().ToString("0.000") : "—"));
            RenderClipOverview();
            _clipFailedPath = "";
            SelectTab(1);
            StartWmp(_tempWav);
            Log("✔ 解密成功：" + Path.GetFileName(path) + "（" + _clipHow + "）");
        }
        catch (Exception ex)
        {
            _clipError = ex.Message;
            _topInfo.Text = "处理失败：" + ex.Message;
            RenderClipOverview();
            Log("✘ " + ex.Message);
        }
    }

    private void RenderClipOverview()
    {
        if (!_clipMode) return;
        string name = Path.GetFileName(_clipFile);
        double encMb = _clipEncBytes / 1048576.0;
        string state = _clipError.Length > 0 ? "失败 · " + _clipError
            : (_clipHow.Length > 0 ? "已解密（" + _clipHow + "）" : "等待解密");

        _col1.Text = string.Join(Environment.NewLine,
            "片段文件",
            "文件名           " + name,
            "类型             LDTC1（AES-256-GCM 双层）",
            "加密大小         " + (encMb >= 1 ? encMb.ToString("0.00") + " MB" : (_clipEncBytes / 1024.0).ToString("0.0") + " KB"),
            "云端上限         9 MB（实测网关约 10 MB）",
            "状态             " + state);

        var sb2 = new StringBuilder();
        sb2.AppendLine("音频");
        if (_clipPlainBytes > 0)
        {
            sb2.AppendLine("解密大小         " + (_clipPlainBytes / 1048576.0).ToString("0.00") + " MB");
            sb2.AppendLine("格式             PCM 16-bit");
            sb2.AppendLine("采样率           " + _clipRate.ToString("N0") + " Hz");
            sb2.AppendLine("通道             " + _clipChannels);
            sb2.AppendLine("时长             " + TimeText((_doc != null ? _doc.Duration : 0)));
            sb2.AppendLine("采样帧数         " + _clipFrames.ToString("N0"));
            if (_chartList.Count > 0 && _chartList[0].Data != null)
            {
                sb2.AppendLine("峰值（包络）     " + _chartList[0].Data.Max.ToString("0.000"));
                sb2.AppendLine("平均（包络）     " + _chartList[0].Data.Avg.ToString("0.000"));
            }
            sb2.AppendLine();
            sb2.AppendLine("说明");
            sb2.AppendLine("加密片段只保存音频，不含 LDTE 统计；");
            sb2.AppendLine("如需通道与派生档案，请打开同名 .ldte。");
        }
        else
        {
            sb2.AppendLine("（解密成功后显示音频参数）");
        }
        _col2.Text = sb2.ToString();

        var sb3 = new StringBuilder();
        sb3.AppendLine("解密路径");
        sb3.AppendLine("1  本机主密钥     " + (_master != null ? "已载入" : "不可用"));
        sb3.AppendLine("2  云端解密       " + (_cloudBase.Length > 0 ? _cloudBase + "（≤9 MB）" : "未配置"));
        sb3.AppendLine("3  手动密钥       粘贴 BAT1 / CFK1 后点「验证」");
        sb3.AppendLine();
        sb3.AppendLine("云端密钥");
        sb3.AppendLine("当前状态         " + (_cloudToken.Length > 0 ? "已缓存 · " + ExpText(_cloudTokenExpires) : "无（按需签发）"));
        sb3.AppendLine("有效期           " + _cloudHours + " 小时（云端上限 23）");
        if (_cloudNote.Length > 0)
        {
            sb3.AppendLine();
            sb3.AppendLine("最近提示");
            sb3.AppendLine(_cloudNote);
        }
        _col3.Text = sb3.ToString();
    }

    private async Task<byte[]> CloudDecryptAsync(byte[] enc)
    {
        if (enc.Length > CloudMaxBytes)
        {
            _cloudNote = "片段 " + (enc.Length / 1048576.0).ToString("0.0") + " MB 超过云端解密上限（网关约 10 MB）";
            return null;
        }
        if (_cloudBase.Length == 0 || _cloudSecret.Length == 0) ReloadCloudConfig();
        if (_cloudBase.Length == 0)
        {
            _cloudNote = "未配置云端地址（_keys\\cloud.json）";
            return null;
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string token = await GetCloudTokenAsync(attempt > 0);
            if (string.IsNullOrEmpty(token)) return null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                using var req = new HttpRequestMessage(HttpMethod.Post, _cloudBase.TrimEnd('/') + "/decrypt");
                req.Headers.Add("x-ldte-token", token);
                req.Content = new ByteArrayContent(enc);
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var wav = await resp.Content.ReadAsByteArrayAsync();
                    if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF")
                    {
                        _cloudNote = "云端返回内容不是 WAV";
                        return null;
                    }
                    _cloudNote = "";
                    return wav;
                }
                string body = await resp.Content.ReadAsStringAsync();
                int code = (int)resp.StatusCode;
                if (code == 401 || code == 403)
                {
                    DropCachedToken();
                    _cloudNote = "云端密钥被拒绝（" + code + "），正在重新签发…";
                    Log("云端密钥被拒绝，重签中");
                    continue;
                }
                if (code == 413)
                    _cloudNote = "云端网关拒绝（413）：片段超过约 10 MB 上限，请改用本机主密钥";
                else if (code == 429)
                    _cloudNote = "云端签发/解密次数已达限制，请稍后重试";
                else
                    _cloudNote = "云端解密失败（" + code + "）：" + Shorten(body, 140);
                return null;
            }
            catch (Exception ex)
            {
                _cloudNote = "云端解密异常：" + ex.Message;
                return null;
            }
        }
        return null;
    }

    private async Task<string> GetCloudTokenAsync(bool force = false)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!force && _cloudToken.Length > 0 && (_cloudTokenExpires == 0 || now < _cloudTokenExpires - 30))
            return _cloudToken;
        if (_cloudBase.Length == 0 || _cloudSecret.Length == 0) ReloadCloudConfig();
        if (_cloudBase.Length == 0 || _cloudSecret.Length == 0)
        {
            _cloudNote = "未找到 cloud.json（缺少 base / adminSecret），无法自动签发云端密钥";
            UpdateKeyState();
            return null;
        }

        foreach (int h in new[] { _cloudHours, 8, 1 }.Distinct())
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var req = new HttpRequestMessage(HttpMethod.Post, _cloudBase.TrimEnd('/') + "/issue");
                req.Headers.Add("x-admin-secret", _cloudSecret);
                req.Content = new StringContent("{\"hours\":" + h + "}", Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    _cloudNote = "签发失败（" + (int)resp.StatusCode + "）：" + Shorten(body, 120);
                    Log(_cloudNote);
                    if ((int)resp.StatusCode == 429)
                        _cloudNote = "云端签发次数已达限制（约 20 次），请稍后重试或改用本机主密钥";
                    if (body.IndexOf("invalid body", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    break;
                }
                using var jd = JsonDocument.Parse(body);
                string tok = jd.RootElement.TryGetProperty("token", out var t) ? (t.GetString() ?? "") : "";
                long exp = jd.RootElement.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
                if (tok.Length == 0)
                {
                    _cloudNote = "签发响应缺少 token";
                    break;
                }
                _cloudToken = tok;
                _cloudTokenExpires = exp;
                _cloudTokenBase = _cloudBase;
                _cloudNote = "";
                SaveCachedToken();
                UpdateKeyState();
                Log("云端密钥已签发：" + h + " 小时 · 到期 " + ExpText(exp));
                return tok;
            }
            catch (Exception ex)
            {
                _cloudNote = "签发异常：" + ex.Message;
                break;
            }
        }
        UpdateKeyState();
        return null;
    }

    private static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    private string ResolveSourceAudio(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        string rel = key.StartsWith("mus:", StringComparison.OrdinalIgnoreCase) ? key.Substring(4) : key;
        if (rel.IndexOf('/') < 0 && rel.IndexOf('\\') < 0) return null;
        if (Path.GetExtension(rel).Length == 0) return null;
        foreach (var root in DataRoots())
        {
            try
            {
                string full = Path.Combine(root, rel.Replace('/', '\\'));
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private IEnumerable<string> DataRoots()
    {
        var list = new List<string>();
        try
        {
            string parent = Directory.GetParent(_dir)?.FullName ?? "";
            if (parent.Length > 0) list.Add(parent);
        }
        catch { }
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                string p = Path.Combine(d.RootDirectory.FullName, "kivo_wiki");
                if (Directory.Exists(p)) list.Add(p);
            }
            catch { }
        }
        return list;
    }

    private async void LoadDoc(string path, bool keepList = false)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        _clipFailedPath = "";
        _srcAudioPath = "";
        _clipMode = false;
        _clipError = "";
        _topInfo.Text = "正在读取 " + Path.GetFileName(path) + " …";
        StopAudio();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var doc = await Task.Run(() => LdteReader.Read(path));
            _doc = doc;
            _topInfo.Text = doc.Tracks.Count > 0
                ? Path.GetFileName(path) + " · " + doc.Frames.ToString("N0") + " 帧 · " + (doc.FileBytes / 1048576.0).ToString("0.00") + " MB"
                : Path.GetFileName(path) + " · " + doc.Frames.ToString("N0") + " 帧 · " + (doc.FileBytes / 1024.0).ToString("0.0") + " KB";
            if (_trackInfoOverride.Length > 0)
            {
                _trackInfo.Text = _trackInfoOverride;
                _trackInfoOverride = "";
            }
            else
            {
                _trackInfo.Text = (doc.System || doc.Tracks.Count > 0)
                    ? "系统合集 · " + Math.Max(doc.TrackCount, (uint)doc.Tracks.Count) + " 首 · " + doc.Frames.ToString("N0") + " 帧 · " + TimeText(doc.Duration)
                    : "单曲档案 · " + (doc.Name.Length > 0 ? doc.Name : Path.GetFileName(path)) + " · " + doc.Frames.ToString("N0") + " 帧 · " + TimeText(doc.Duration);
            }

            if (!keepList)
            {
                if (doc.Tracks.Count > 0) PopulateTracks(doc);
                else _trackMode = false;
            }

            RenderOverview();
            RenderChannels(doc);
            RenderSegments(doc);
            _headerBox.Text = PrettyJson(doc.HeaderJson);

            LoadClips(doc);
            _srcAudioPath = ResolveSourceAudio(doc.Key) ?? "";
            if (_srcAudioPath.Length > 0 && _clips.Count == 0)
            {
                _audioSelect.Items.Clear();
                _audioSelect.Items.Add("源文件：" + Path.GetFileName(_srcAudioPath));
                _audioSelect.SelectedIndex = 0;
                Log("已定位源文件：" + _srcAudioPath);
            }

            sw.Stop();
            string dur = TimeText(doc.Duration);
            _status.Text = doc.Frames.ToString("N0") + " 帧 · " + doc.Channels + " 通道 · " + doc.FrameMs + " ms/帧 · "
                + dur + " · " + (doc.FileBytes / 1048576.0).ToString("0.00") + " MB · 解析 " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms";
            SelectTab(0);
            HighlightListPath(path);
        }
        catch (Exception ex)
        {
            _topInfo.Text = "读取失败：" + ex.Message;
            Log("✘ " + ex.Message);
        }
    }

    private static string UpdText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length > 16 ? s.Substring(0, 16) : s;
    }

    private void RenderOverview()
    {
        if (_clipMode) { RenderClipOverview(); return; }
        if (_doc == null) return;
        var d = _doc;
        var s = d.Stats;
        string S(string k, string fmt = "0.###")
        {
            if (!s.TryGetValue(k, out var v) || v == null) return "—";
            try { return Convert.ToDouble(v).ToString(fmt); } catch { return Convert.ToString(v) ?? "—"; }
        }
        string Pct(string k)
        {
            if (!s.TryGetValue(k, out var v) || v == null) return "—";
            try { return (Convert.ToDouble(v) * 100).ToString("0.00") + "%"; } catch { return "—"; }
        }
        string L(string k, string fmt = "0.###")
        {
            if (!d.Limit.TryGetValue(k, out var v) || v == null) return "—";
            try { return Convert.ToDouble(v).ToString(fmt); } catch { return "—"; }
        }
        Dictionary<string, object> Prof(string n)
            => d.Profiles.TryGetValue(n, out var p) ? p : null;
        string Pv(Dictionary<string, object> p, string k, string fmt = "0.###")
        {
            if (p == null || !p.TryGetValue(k, out var v) || v == null) return "—";
            try { return Convert.ToDouble(v).ToString(fmt); } catch { return Convert.ToString(v) ?? "—"; }
        }
        float ChAvg(string name)
        {
            var col = d.Cols.FirstOrDefault(c => c.Name == name);
            return col != null ? col.Avg : 0;
        }

        string kind = d.System || d.Tracks.Count > 0
            ? "系统合集（" + Math.Max(d.TrackCount, (uint)d.Tracks.Count) + " 首）"
            : "单曲档案";

        _col1.Text = string.Join(Environment.NewLine,
            "档案",
            "类型             " + kind,
            "路径             " + (d.Path.Length > 0 ? Path.GetFileName(d.Path) : "—"),
            "曲目标识键       " + (d.Key.Length > 0 ? d.Key : "—"),
            "更新时间     " + UpdText(d.Updated),
            "",
            "固定头",
            "magic            LDTE",
            "version          v" + d.Version,
            "frameMs          " + d.FrameMs + " ms",
            "passCount        " + d.Passes,
            "frameCount       " + d.Frames.ToString("N0"),
            "headerLength     " + d.HeaderLen + " B",
            "channelCount     " + d.Channels,
            "sampleRate       " + (d.SampleRate / 1000.0).ToString("0.#") + " kHz",
            "flags            " + d.Flags + " (" + LdteReader.DescribeFlags(d.Flags) + ")",
            "segmentCount     " + d.SegCount,
            "文件大小         " + (d.FileBytes / 1048576.0).ToString("0.00") + " MB",
            "时长             " + TimeText(d.Duration));

        _col2.Text = string.Join(Environment.NewLine,
            "统计",
            "最大峰值         " + S("maxPeak"),
            "平均 RMS         " + S("avgRms", "0.00000"),
            "峰值因数         " + S("crest", "0.00"),
            "响度近似         " + S("lufsApprox", "0.0") + " LUFS",
            "动态范围 LRA     " + S("lra", "0.0") + " dB",
            "热峰比例         " + Pct("hotRatio"),
            "削波帧数         " + S("clipFrames", "0"),
            "最大动态变化     " + S("maxFlux", "0.000"),
            "统计帧数         " + S("frames", "0"),
            "",
            "推荐限幅",
            "ceiling          " + L("ceiling", "0.000"),
            "threshold        " + L("threshold", "0.0") + " dB",
            "ratio            " + L("ratio", "0.0") + " : 1",
            "knee             " + L("knee", "0.0") + " dB",
            "attack           " + L("attack", "0.000") + " s",
            "release          " + L("release", "0.000") + " s");

        var sb = new StringBuilder();
        var cal = Prof("calibration");
        var tim = Prof("timbre");
        var dyn = Prof("dynamics");
        var ste = Prof("stereo");
        var dec = Prof("decoder");
        var dol = Prof("dolby");
        bool hasProf = cal != null || tim != null || dyn != null || ste != null || dec != null || dol != null;

        sb.AppendLine("派生档案");
        if (hasProf)
        {
            sb.AppendLine("校准");
            sb.AppendLine("  响度           " + Pv(cal, "lufsApprox", "0.0") + " LUFS");
            sb.AppendLine("  动态范围       " + Pv(cal, "lra", "0.0") + " dB");
            sb.AppendLine("  余量           " + Pv(cal, "headroomDb", "0.0") + " dB");
            sb.AppendLine("  目标响度       " + Pv(cal, "targetLufs", "0.0") + " LUFS");
            sb.AppendLine("  建议增益       " + Pv(cal, "suggestGainDb", "0.0") + " dB");
            sb.AppendLine("音色");
            sb.AppendLine("  亮度           " + Pv(tim, "brightness", "0.000"));
            sb.AppendLine("  过零率         " + Pv(tim, "zcr", "0.000"));
            sb.AppendLine("  低/中/高       " + Pv(tim, "lowRatio", "0.00") + " / " + Pv(tim, "midRatio", "0.00") + " / " + Pv(tim, "highRatio", "0.00"));
            sb.AppendLine("  倾斜           " + Pv(tim, "tiltDb", "0.0") + " dB");
            sb.AppendLine("  建议 EQ 倾斜   " + Pv(tim, "suggestEqTilt", "0.0") + " dB");
            sb.AppendLine("动态");
            sb.AppendLine("  峰均比         " + Pv(dyn, "crest", "0.00"));
            sb.AppendLine("  动态范围       " + Pv(dyn, "dr", "0.0") + " dB");
            sb.AppendLine("  瞬态均值       " + Pv(dyn, "transAvg", "0.000"));
            sb.AppendLine("  建议阈值       " + Pv(dyn, "suggest.threshold", "0.0") + " dB");
            sb.AppendLine("  建议比率       " + Pv(dyn, "suggest.ratio", "0.0") + " : 1");
            sb.AppendLine("立体声");
            sb.AppendLine("  宽度均值       " + Pv(ste, "widthAvg", "0.000"));
            sb.AppendLine("  宽度峰值       " + Pv(ste, "widthMax", "0.000"));
            sb.AppendLine("  相关性         " + Pv(ste, "corrAvg", "0.000"));
            sb.AppendLine("  建议模式       " + Pv(ste, "suggest.mode"));
            sb.AppendLine("解码器");
            sb.AppendLine("  采样率         " + Pv(dec, "sampleRate", "0"));
            sb.AppendLine("  削波帧数       " + Pv(dec, "clippedFrames", "0"));
            sb.AppendLine("  削波比例       " + Pv(dec, "clipRatio", "0.00000"));
            sb.AppendLine("  峰值样本       " + Pv(dec, "peakSample", "0.000"));
            sb.AppendLine("  建议增益       " + Pv(dec, "suggestGainDb", "0.0") + " dB");
            sb.AppendLine("杜比");
            sb.AppendLine("  LFE            " + Pv(dol, "lfeDb", "0.0") + " dB");
            sb.AppendLine("  中置           " + Pv(dol, "centerDb", "0.0") + " dB");
            sb.AppendLine("  环绕           " + Pv(dol, "surroundDb", "0.0") + " dB");
            sb.AppendLine("  建议 LFE 增益  " + Pv(dol, "suggest.lfeGain", "0.00"));
            sb.AppendLine("  建议环绕增益   " + Pv(dol, "suggest.surroundGain", "0.00"));
            sb.AppendLine("  人声提升       " + Pv(dol, "suggest.dialogBoost", "0.00") + " dB");
        }
        else
        {
            sb.AppendLine("（该档案未写入 profiles，以下为通道均值）");
            sb.AppendLine("亮度             " + ChAvg("bright").ToString("0.000"));
            sb.AppendLine("过零率           " + ChAvg("zcr").ToString("0.000"));
            sb.AppendLine("低频             " + ChAvg("low").ToString("0.000"));
            sb.AppendLine("中频             " + ChAvg("mid").ToString("0.000"));
            sb.AppendLine("高频             " + ChAvg("high").ToString("0.000"));
            sb.AppendLine("宽度             " + ChAvg("width").ToString("0.000"));
            sb.AppendLine("相关性           " + ChAvg("corr").ToString("0.000"));
            sb.AppendLine("瞬态             " + ChAvg("trans").ToString("0.000"));
        }
        _col3.Text = sb.ToString();
    }

    private void EnsureCharts()
    {
        if (_chartList.Count > 0) return;
        for (int i = 0; i < 12; i++)
        {
            var ch = new ChannelChart { Height = 118, Width = Math.Max(300, _charts.ClientSize.Width - 30), Font = new Font("Microsoft YaHei UI", 8.5f) };
            _chartList.Add(ch);
            _charts.Controls.Add(ch);
        }
    }

    private void RenderChannels(LdteDoc doc)
    {
        EnsureCharts();
        for (int i = 0; i < _chartList.Count; i++)
        {
            var ch = _chartList[i];
            ch.Width = Math.Max(300, _charts.ClientSize.Width - 30);
            ch.Data = i < doc.Cols.Count ? doc.Cols[i] : null;
            ch.Duration = doc.Duration;
            ch.SetPosition(0);
        }
    }

    private void RenderSegments(LdteDoc doc)
    {
        _segments.Items.Clear();
        foreach (var s in doc.Segments)
        {
            var it = new ListViewItem(s.T0.ToString("0.00"));
            it.SubItems.Add((s.T1 - s.T0).ToString("0.00"));
            it.SubItems.Add(s.T1.ToString("0.00"));
            it.SubItems.Add(s.Peak.ToString("0.000"));
            it.SubItems.Add(s.RmsMean.ToString("0.0000"));
            it.SubItems.Add(s.HighMean.ToString("0.000"));
            it.SubItems.Add(s.LowMean.ToString("0.000"));
            it.Tag = s;
            _segments.Items.Add(it);
        }
        _segBar.Segments = doc.Segments;
        _segBar.Duration = doc.Duration;
        _segBar.Invalidate();
        FitSegmentColumns();
    }

    private void LoadClips(LdteDoc doc)
    {
        _audioSelect.Items.Clear();
        _clips.Clear();
        try
        {
            string idx = Path.Combine(_dir, "clips", "index.json");
            if (File.Exists(idx) && doc.Key.Length > 0)
            {
                using var jd = JsonDocument.Parse(File.ReadAllText(idx));
                if (jd.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in jd.RootElement.EnumerateArray())
                    {
                        string key = it.TryGetProperty("key", out var kv) ? kv.GetString() ?? "" : "";
                        if (key != doc.Key) continue;
                        var c = new ClipRef
                        {
                            Id = it.TryGetProperty("id", out var i1) ? i1.GetString() ?? "" : "",
                            File = it.TryGetProperty("file", out var f1) ? f1.GetString() ?? "" : "",
                            Start = it.TryGetProperty("start", out var s1) ? s1.GetDouble() : 0,
                            End = it.TryGetProperty("end", out var s2) ? s2.GetDouble() : 0,
                            Full = it.TryGetProperty("full", out var f2) && f2.ValueKind == JsonValueKind.True
                        };
                        if (c.File.Length == 0) continue;
                        _clips.Add(c);
                    }
                }
            }
        }
        catch { }
        foreach (var c in _clips) _audioSelect.Items.Add(c.Label);
        if (_clips.Count == 0) _audioSelect.Items.Add("（没有该曲目的加密片段，可用「打开音频」）");
        _audioSelect.SelectedIndex = 0;
    }

    private async void VerifyKey()
    {
        string raw = _key.Text.Trim();
        if (raw.Length == 0)
        {
            LoadDefaultKey();
            if (_master != null) Log("已载入本机主密钥");
            else if (_cloudBase.Length > 0) Log("未发现本机主密钥，将按需签发云端密钥");
            else Log("没有可用密钥：请粘贴 BAT1/CFK1，或配置 _keys\\master.key / cloud.json");
            return;
        }

        if (raw.StartsWith("CFK1.", StringComparison.Ordinal))
        {
            ReloadCloudConfig();
            if (_cloudBase.Length == 0)
            {
                _keyState.ForeColor = Color.FromArgb(255, 141, 141);
                _keyState.Text = "缺少云端地址：请配置 _keys\\cloud.json";
                return;
            }
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string txt = await http.GetStringAsync(_cloudBase.TrimEnd('/') + "/verify?token=" + Uri.EscapeDataString(raw));
                using var jd = JsonDocument.Parse(txt);
                if (!jd.RootElement.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException(txt);
                long exp = jd.RootElement.TryGetProperty("expires", out var ev) && ev.ValueKind == JsonValueKind.Number ? ev.GetInt64() : 0;
                _cloudToken = raw;
                _cloudTokenExpires = exp;
                _cloudTokenBase = _cloudBase;
                _keySource = "cloud";
                SaveCachedToken();
                UpdateKeyState();
                _clipFailedPath = "";
                Log("云端密钥校验通过 · 到期 " + ExpText(exp));
            }
            catch (Exception ex)
            {
                _cloudToken = "";
                _keySource = "";
                _keyState.ForeColor = Color.FromArgb(255, 141, 141);
                _keyState.Text = "云端校验失败：" + Shorten(ex.Message, 80);
            }
            return;
        }

        try
        {
            _master = LdteCrypto.ParseBat1(raw);
            _keySource = "bat1";
            var parts = raw.Split('.');
            _batExp = parts.Length == 4 && long.TryParse(parts[2], out long exp) ? exp : 0;
            UpdateKeyState();
            _clipFailedPath = "";
            Log("离线密钥校验通过 · 到期 " + ExpText(_batExp));
        }
        catch (Exception ex)
        {
            _master = null;
            if (_keySource == "bat1") _keySource = "";
            _keyState.ForeColor = Color.FromArgb(255, 141, 141);
            _keyState.Text = "密钥无效：" + Shorten(ex.Message, 80);
            UpdateKeyState();
        }
    }

    private void TogglePlay()
    {
        if (_playing)
        {
            try { _wmp.controls.pause(); } catch { }
            _playing = false;
            _playBtn.Text = "▶ 试听";
            return;
        }
        _ = PlayCurrentAsync();
    }

    private async Task<bool> PlayCurrentAsync()
    {
        if (_doc == null) { Log("请先选择档案。"); return false; }
        if (_clipMode && _tempWav != null && File.Exists(_tempWav))
        {
            try
            {
                StopAudio();
                _clipStart = 0;
                _clipEnd = _doc.Duration;
                StartWmp(_tempWav);
                Log("试听当前片段");
                return true;
            }
            catch (Exception ex) { Log("试听失败：" + ex.Message); return false; }
        }
        if (_clips.Count == 0)
        {
            if (_srcAudioPath.Length > 0 && File.Exists(_srcAudioPath))
            {
                try
                {
                    CleanupAudio();
                    _clipStart = 0;
                    _clipEnd = _doc.Duration;
                    StartWmp(_srcAudioPath);
                    Log("试听源文件：" + Path.GetFileName(_srcAudioPath));
                    return true;
                }
                catch (Exception ex) { Log("试听失败：" + ex.Message); return false; }
            }
            Log("没有可试听音频，请用「打开音频」选择。");
            return false;
        }
        int idx = _audioSelect.SelectedIndex;
        if (idx < 0 || idx >= _clips.Count) idx = 0;
        var c = _clips[idx];
        string full = Path.Combine(_dir, c.File);
        if (!File.Exists(full)) { Log("片段文件不存在：" + full); return false; }
        try
        {
            byte[] enc = await Task.Run(() => File.ReadAllBytes(full));
            byte[] wav = null;
            string how = "";
            ReloadLocalKey();
            if (_master != null)
            {
                try { wav = LdteCrypto.DecryptClip(enc, _master); how = _keySource == "bat1" ? "离线密钥" : "本机主密钥"; }
                catch { wav = null; }
            }
            if (wav == null && _cloudBase.Length > 0)
            {
                wav = await CloudDecryptAsync(enc);
                if (wav != null) how = "云端密钥";
            }
            if (wav == null)
            {
                Log("试听失败：" + (_cloudNote.Length > 0 ? _cloudNote : "密钥不匹配或片段损坏"));
                return false;
            }
            CleanupAudio();
            _tempWav = Path.Combine(Path.GetTempPath(), "ldte_view_" + Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(_tempWav, wav);
            _clipStart = c.Start;
            _clipEnd = c.End;
            StartWmp(_tempWav);
            Log("试听：" + c.Label + "（" + how + "）");
            return true;
        }
        catch (Exception ex) { Log("试听失败：" + ex.Message); return false; }
    }

    private async void PlaySegment()
    {
        if (_doc == null || _segments.SelectedItems.Count == 0) return;
        if (_segments.SelectedItems[0].Tag is not SegmentRow s) return;
        if (_clipMode && _tempWav != null)
        {
            try { if (_wmp != null) _wmp.controls.currentPosition = s.T0; } catch { }
            Log("定位片段 " + s.T0.ToString("0.00") + "s");
            return;
        }
        if (_clips.Count == 0)
        {
            if (_srcAudioPath.Length > 0 && File.Exists(_srcAudioPath))
            {
                try
                {
                    CleanupAudio();
                    _clipStart = 0;
                    _clipEnd = _doc.Duration;
                    StartWmp(_srcAudioPath);
                    try { _wmp.controls.currentPosition = s.T0; } catch { }
                    Log("定位源文件 " + s.T0.ToString("0.00") + "s");
                }
                catch (Exception ex) { Log("播放失败：" + ex.Message); }
                return;
            }
            Log("没有可试听音频。");
            return;
        }
        var c = _clips.FirstOrDefault(x => x.Start <= s.T0 + 0.5 && x.End >= s.T1 - 0.5) ?? _clips[0];
        int idx = _clips.IndexOf(c);
        if (idx >= 0) _audioSelect.SelectedIndex = idx;
        if (!await PlayCurrentAsync()) return;
        try
        {
            if (_wmp != null)
            {
                double offset = Math.Max(0, s.T0 - c.Start);
                _wmp.controls.currentPosition = offset;
            }
        }
        catch { }
        Log("定位到分段 " + s.T0.ToString("0.00") + "s–" + s.T1.ToString("0.00") + "s");
    }

    private void OpenLocalAudio()
    {
        using var dlg = new OpenFileDialog { Filter = "音频/视频|*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.mp4;*.webm;*.mkv|所有文件|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            CleanupAudio();
            _clipStart = 0;
            _clipEnd = _doc != null ? _doc.Duration : 0;
            StartWmp(dlg.FileName);
            Log("已载入音频：" + Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) { Log("打开失败：" + ex.Message); }
    }

    private void StartWmp(string url)
    {
        var t = Type.GetTypeFromProgID("WMPlayer.OCX");
        if (t == null) throw new InvalidOperationException("系统未安装 Windows Media Player 组件");
        try { if (_wmp != null) { _wmp.controls.stop(); _wmp.close(); } } catch { }
        _wmp = Activator.CreateInstance(t);
        _wmp.settings.autoStart = false;
        _wmp.settings.volume = 100;
        _wmp.URL = url;
        _wmp.controls.play();
        _playing = true;
        _playBtn.Text = "⏸ 暂停";
        _timer.Start();
    }

    private void StopAudio()
    {
        try { if (_wmp != null) _wmp.controls.stop(); } catch { }
        _playing = false;
        _playBtn.Text = "▶ 试听";
        _timer.Stop();
        foreach (var c in _chartList) c.SetPosition(0);
    }

    private void UpdatePlayback()
    {
        if (_doc == null || _wmp == null) return;
        try
        {
            double pos = 0;
            try { pos = (double)_wmp.controls.currentPosition; } catch { }
            double trackDur = _doc.Duration > 0 ? _doc.Duration : Math.Max(_clipEnd, 1);
            double abs = _clipStart + pos;
            double frac = Math.Max(0, Math.Min(1, abs / trackDur));
            foreach (var c in _chartList) c.SetPosition(frac);
        }
        catch { }
    }

    private void CleanupAudio()
    {
        _timer.Stop();
        try { if (_wmp != null) { _wmp.controls.stop(); _wmp.close(); } } catch { }
        _wmp = null;
        try { if (!string.IsNullOrEmpty(_tempWav) && File.Exists(_tempWav)) File.Delete(_tempWav); } catch { }
        _tempWav = null;
        _playing = false;
    }

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = false,
            Filter = "LDTE 文件|*.ldte;*.ldtc|LDTE 档案|*.ldte|加密片段|*.ldtc|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string p = dlg.FileName;
        string ext = Path.GetExtension(p).ToLowerInvariant();
        if (ext == ".ldtc") { _ = OpenClipAsync(p); return; }
        LoadDoc(p);
        string dir = Path.GetDirectoryName(p) ?? "";
        if (dir.Length > 0) { _dir = dir; _dirBox.Text = dir; }
    }

    private void ExportCsv()
    {
        if (_doc == null) { Log("请先打开档案。"); return; }
        using var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = Path.GetFileNameWithoutExtension(_doc.Path) + "-segments.csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder();
        sb.AppendLine("start,duration,end,peak,rms,highMean,lowMean");
        foreach (var s in _doc.Segments)
            sb.AppendLine(s.T0.ToString("0.###") + "," + (s.T1 - s.T0).ToString("0.###") + "," + s.T1.ToString("0.###") + ","
                + s.Peak.ToString("0.####") + "," + s.RmsMean.ToString("0.####") + "," + s.HighMean.ToString("0.####") + "," + s.LowMean.ToString("0.####"));
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        Log("CSV 已导出：" + dlg.FileName);
    }

    private void ExportSummary()
    {
        if (_doc == null) { Log("请先打开档案。"); return; }
        using var dlg = new SaveFileDialog { Filter = "文本|*.txt", FileName = Path.GetFileNameWithoutExtension(_doc.Path) + "-summary.txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder();
        sb.AppendLine("LDTE 摘要");
        sb.AppendLine("文件: " + _doc.Path);
        sb.AppendLine("帧数: " + _doc.Frames + " / 通道: " + _doc.Channels + " / 帧率: " + _doc.FrameMs + "ms");
        sb.AppendLine("时长: " + TimeText(_doc.Duration) + " / 大小: " + (_doc.FileBytes / 1048576.0).ToString("0.00") + " MB");
        sb.AppendLine("分段: " + _doc.Segments.Count + " / 训练遍数: " + _doc.Passes);
        sb.AppendLine("曲目: " + _doc.Tracks.Count);
        sb.AppendLine("最近更新: " + (_doc.Updated.Length > 0 ? _doc.Updated : "—"));
        sb.AppendLine();
        sb.AppendLine("通道统计:");
        foreach (var c in _doc.Cols)
            sb.AppendLine("  " + c.Name.PadRight(8) + " max " + c.Max.ToString("0.000") + "  avg " + c.Avg.ToString("0.000"));
        sb.AppendLine();
        sb.AppendLine("头部 JSON:");
        sb.AppendLine(_doc.HeaderJson);
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        Log("摘要已导出：" + dlg.FileName);
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return json; }
    }

    private static string TimeText(double s)
    {
        if (s < 0) s = 0;
        int total = (int)Math.Round(s);
        int h = total / 3600;
        int m = (total % 3600) / 60;
        int sec = total % 60;
        return h > 0 ? h + ":" + m.ToString("00") + ":" + sec.ToString("00") : m + ":" + sec.ToString("00");
    }

    private static string FormatTime(DateTime t)
    {
        return t.Year >= 2000 ? t.ToString("MM-dd HH:mm") : t.ToString("HH:mm");
    }

    private static string SizeText(long bytes)
    {
        if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.00") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return bytes + " B";
    }
}
