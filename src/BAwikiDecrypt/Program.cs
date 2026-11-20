using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BAwikiDecrypt;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--decrypt-clip")
        {
            try
            {
                var key = Crypto.ParseBat1(args[1]);
                var bytes = File.ReadAllBytes(args[2]);
                var wav = Crypto.DecryptClip(bytes, key.Key);
                File.WriteAllBytes(args[3], wav);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(args[3] + ".error.txt", ex.Message, Encoding.UTF8); } catch { }
                Environment.Exit(2);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Length > 0 ? args[0] : ""));
    }
}

internal static class Crypto
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

    public static (byte[] Key, long Exp) ParseBat1(string token)
    {
        var p = (token ?? "").Trim().Split('.');
        if (p.Length != 4 || p[0] != "BAT1") throw new FormatException("格式不是 BAT1 离线密钥");
        var key = FromB64Url(p[1]);
        if (key.Length != 32) throw new FormatException("密钥长度不正确");
        if (!long.TryParse(p[2], out long exp)) throw new FormatException("到期时间无效");
        using var sha = SHA256.Create();
        string fp = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(p[1] + "." + p[2]))).Substring(0, 16).ToLowerInvariant();
        if (fp != p[3].ToLowerInvariant()) throw new FormatException("密钥校验失败，可能复制不完整");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now > exp) throw new FormatException("密钥已过期：" + DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        return (key, exp);
    }

    public static bool IsCloudToken(string token) => (token ?? "").Trim().StartsWith("CFK1.", StringComparison.Ordinal);

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
        if (len <= 0 || off + len > bytes.Length) throw new InvalidDataException("片段长度信息损坏");
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

    public static LdteInfo ParseLdte(byte[] bytes, bool collectEnvelope)
    {
        if (bytes.Length < 20) throw new InvalidDataException("档案文件过短");
        if (Encoding.ASCII.GetString(bytes, 0, 4) != "LDTE") throw new InvalidDataException("不是 LDTE 训练档案");
        int off = 4;
        int ver = BitConverter.ToUInt16(bytes, off); off += 2;
        int frameMs = BitConverter.ToUInt16(bytes, off); off += 2;
        uint passes = BitConverter.ToUInt32(bytes, off); off += 4;
        uint frames = BitConverter.ToUInt32(bytes, off); off += 4;
        uint jsonLen = BitConverter.ToUInt32(bytes, off); off += 4;
        uint channels = 4, sampleRate = 48000, flags = 0, segCount = 0;
        if (ver >= 2)
        {
            channels = BitConverter.ToUInt32(bytes, off); off += 4;
            sampleRate = BitConverter.ToUInt32(bytes, off); off += 4;
            flags = BitConverter.ToUInt32(bytes, off); off += 4;
            segCount = BitConverter.ToUInt32(bytes, off); off += 4;
            off += 12;
        }
        if (ver == 0 || ver > 2) throw new InvalidDataException("档案版本不支持：" + ver);
        if (channels == 0 || channels > 64) throw new InvalidDataException("通道数异常");
        if (frames > 20000000) throw new InvalidDataException("帧数异常");
        if (jsonLen > 4 * 1024 * 1024 || off + jsonLen > bytes.Length) throw new InvalidDataException("头部长度异常");
        string json = Encoding.UTF8.GetString(bytes, off, (int)jsonLen);
        off += (int)jsonLen;

        float[] env = null;
        if (collectEnvelope && channels > 0 && frames > 0 && off + (long)channels * frames * 4L <= bytes.Length)
        {
            env = new float[frames];
            for (int i = 0; i < frames; i++)
                env[i] = BitConverter.ToSingle(bytes, off + i * 4);
        }

        return new LdteInfo
        {
            Version = ver,
            FrameMs = frameMs,
            Passes = passes,
            Frames = frames,
            Channels = channels,
            SampleRate = sampleRate,
            Flags = flags,
            Segments = segCount,
            HeaderJson = json,
            Envelope = env
        };
    }

    public static WavInfo ParseWav(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new InvalidDataException("不是有效 WAV");
        int off = 12;
        int channels = 0, rate = 0, bits = 0;
        int dataOff = -1, dataLen = 0;
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
        if (dataOff < 0 || channels <= 0 || rate <= 0 || bits != 16)
            throw new InvalidDataException("仅支持 16 位 PCM WAV");

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
        return new WavInfo { Channels = channels, Rate = rate, Bits = bits, Frames = frames, Envelope = env, Duration = frames / (double)rate };
    }
}

internal sealed class LdteInfo
{
    public int Version;
    public int FrameMs;
    public uint Passes;
    public uint Frames;
    public uint Channels;
    public uint SampleRate;
    public uint Flags;
    public uint Segments;
    public string HeaderJson;
    public float[] Envelope;
    public double Duration => Frames * FrameMs / 1000.0;
}

internal sealed class WavInfo
{
    public int Channels;
    public int Rate;
    public int Bits;
    public int Frames;
    public double Duration;
    public float[] Envelope;
}

internal sealed class WavePanel : Panel
{
    private float[] _env;
    private double _duration;
    private double _position;
    public event Action<double> SeekRequested;

    public WavePanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(10, 16, 28);
        ResizeRedraw = true;
        Cursor = Cursors.Hand;
    }

    public void SetData(float[] env, double duration)
    {
        _env = env;
        _duration = duration;
        _position = 0;
        Invalidate();
    }

    public void SetPosition(double seconds)
    {
        _position = seconds;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int w = Width, h = Height;
        using (var pen = new Pen(Color.FromArgb(34, 48, 74))) g.DrawLine(pen, 0, h / 2, w, h / 2);
        if (_env == null || _env.Length == 0)
        {
            using var br = new SolidBrush(Color.FromArgb(120, 145, 180));
            g.DrawString("解密后在这里显示波形峰值", Font, br, 12, h / 2 - 8);
            return;
        }
        using (var pen = new Pen(Color.FromArgb(91, 140, 247)))
        {
            int n = _env.Length;
            for (int x = 0; x < w; x++)
            {
                int a = (int)((long)x * n / Math.Max(1, w));
                int b = (int)((long)(x + 1) * n / Math.Max(1, w));
                float m = 0;
                for (int i = a; i < b && i < n; i++) if (_env[i] > m) m = _env[i];
                float y = m * (h / 2f - 4);
                g.DrawLine(pen, x, h / 2f - y, x, h / 2f + y);
            }
        }
        if (_duration > 0)
        {
            double frac = Math.Max(0, Math.Min(1, _position / _duration));
            int px = (int)(frac * w);
            using var pen = new Pen(Color.FromArgb(255, 205, 120), 2);
            g.DrawLine(pen, px, 0, px, h);
        }
        using (var br = new SolidBrush(Color.FromArgb(120, 145, 180)))
        {
            g.DrawString("0:00", Font, br, 4, h - 18);
            if (_duration > 0) g.DrawString(TimeText(_duration), Font, br, w - 40, h - 18);
        }
    }

    private static string TimeText(double s)
    {
        int m = (int)(s / 60);
        int sec = (int)(s % 60);
        return m + ":" + sec.ToString("00");
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_env == null || _duration <= 0 || Width <= 0) return;
        SeekRequested?.Invoke(_duration * Math.Max(0, Math.Min(1, e.X / (double)Width)));
    }
}

internal sealed class MainForm : Form
{
    private readonly Panel _splash = new();
    private readonly Panel _listPage = new();
    private readonly Panel _viewPage = new();

    private readonly TextBox _key = new();
    private readonly TextBox _base = new();
    private readonly Label _status = new();

    private readonly TextBox _dirBox = new();
    private readonly ListView _list = new();
    private readonly Label _scanInfo = new();

    private readonly WavePanel _wave = new();
    private readonly Label _title = new();
    private readonly TextBox _details = new();
    private readonly Button _playBtn = new();
    private readonly TrackBar _seek = new();
    private readonly Label _time = new();
    private readonly RichTextBox _log = new();
    private readonly RichTextBox _logView = new();

    private string _mode = "";
    private byte[] _master;
    private string _cloudToken = "";
    private string _cloudBase = "";
    private byte[] _lastWav;
    private string _lastName = "";
    private string _ldteDir = "";

    private dynamic _wmp;
    private SoundPlayer _sp;
    private string _tempWav;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private bool _seeking;
    private bool _wmpReady;

    public MainForm(string initialFile)
    {
        Text = "BAwiki LDTE 解密工具";
        Width = 1000;
        Height = 700;
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(16, 22, 38);
        ForeColor = Color.FromArgb(220, 230, 245);
        Font = new Font("Microsoft YaHei UI", 9f);
        AllowDrop = true;

        BuildSplash();
        BuildListPage();
        BuildViewPage();
        Controls.Add(_viewPage);
        Controls.Add(_listPage);
        Controls.Add(_splash);
        _splash.Dock = DockStyle.Fill;
        _listPage.Dock = DockStyle.Fill;
        _viewPage.Dock = DockStyle.Fill;

        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                AddFiles(files);
                ShowPage(_listPage);
            }
        };
        FormClosing += (_, _) => CleanupAudio();
        _timer.Tick += (_, _) => UpdatePlayback();

        Shown += async (_, _) =>
        {
            await Task.Run(() => { }).ContinueWith(_ => { });
            BeginInvoke(new Action(async () =>
            {
                _ldteDir = FindLdteDir();
                _dirBox.Text = _ldteDir;
                await ScanAsync();
                ShowPage(_listPage);
                if (!string.IsNullOrEmpty(initialFile)) AddFiles(new[] { initialFile });
            }));
        };
    }

    private void BuildSplash()
    {
        var box = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 22, 38) };
        var t = new Label
        {
            Text = "BAwiki LDTE 解密工具",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 120,
            Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 220, 245)
        };
        var s = new Label
        {
            Name = "splashText",
            Text = "正在检测 LDTE 训练目录…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 10f),
            ForeColor = Color.FromArgb(150, 175, 210)
        };
        box.Controls.Add(s);
        box.Controls.Add(t);
        _splash.Controls.Add(box);
    }

    private void BuildListPage()
    {
        var lblKey = new Label { Text = "密钥（BAT1 离线 / CFK1 云端）", Left = 14, Top = 10, Width = 320, ForeColor = Color.FromArgb(150, 175, 210) };
        _key.Left = 14; _key.Top = 34; _key.Width = 800; _key.Height = 52;
        _key.Multiline = true; _key.ScrollBars = ScrollBars.Vertical;
        _key.BackColor = Color.FromArgb(12, 19, 34); _key.ForeColor = Color.FromArgb(160, 205, 255);
        _key.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var verify = new Button { Text = "验证密钥", Left = 826, Top = 34, Width = 140, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        verify.Click += (_, _) => VerifyKey();

        _status.Left = 14; _status.Top = 92; _status.Width = 950; _status.Height = 20;
        _status.ForeColor = Color.FromArgb(150, 175, 210);
        _status.Text = "未验证";
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var lblBase = new Label { Text = "云端地址（CFK1 时使用）", Left = 14, Top = 116, Width = 240, ForeColor = Color.FromArgb(150, 175, 210) };
        _base.Left = 14; _base.Top = 138; _base.Width = 800; _base.Height = 26;
        _base.BackColor = Color.FromArgb(12, 19, 34); _base.ForeColor = Color.FromArgb(255, 215, 159);
        _base.Text = "https://37kt3jg5wgu3z6k0i2i.traesite.cn";
        _base.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var lblDir = new Label { Text = "训练目录", Left = 14, Top = 174, Width = 70, ForeColor = Color.FromArgb(150, 175, 210) };
        _dirBox.Left = 86; _dirBox.Top = 170; _dirBox.Width = 660; _dirBox.Height = 26;
        _dirBox.BackColor = Color.FromArgb(12, 19, 34); _dirBox.ForeColor = Color.FromArgb(200, 220, 245);
        _dirBox.ReadOnly = true;
        _dirBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var pick = new Button { Text = "选择目录", Left = 754, Top = 168, Width = 100, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        pick.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _ldteDir = dlg.SelectedPath;
                _dirBox.Text = _ldteDir;
                _ = ScanAsync();
            }
        };
        var refresh = new Button { Text = "重新扫描", Left = 862, Top = 168, Width = 100, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        refresh.Click += (_, _) => _ = ScanAsync();

        _scanInfo.Left = 14; _scanInfo.Top = 204; _scanInfo.Width = 950; _scanInfo.Height = 20;
        _scanInfo.ForeColor = Color.FromArgb(150, 175, 210);
        _scanInfo.Text = "—";
        _scanInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _list.Left = 14; _list.Top = 228; _list.Width = 950; _list.Height = 360;
        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.Columns.Add("名称", 360);
        _list.Columns.Add("类型", 110);
        _list.Columns.Add("大小", 110);
        _list.Columns.Add("时间", 160);
        _list.Columns.Add("路径", 400);
        _list.BackColor = Color.FromArgb(12, 19, 34); _list.ForeColor = Color.FromArgb(210, 225, 245);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.DoubleClick += (_, _) => OpenSelected();

        var open = new Button { Text = "解密 / 查看", Left = 14, Top = 596, Width = 130, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        open.Click += (_, _) => OpenSelected();
        var addBtn = new Button { Text = "打开其它文件", Left = 152, Top = 596, Width = 130, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        addBtn.Click += (_, _) => PickFiles();

        _log.Left = 292; _log.Top = 592; _log.Width = 672; _log.Height = 60;
        _log.ReadOnly = true; _log.BackColor = Color.FromArgb(10, 16, 28); _log.ForeColor = Color.FromArgb(160, 190, 225);
        _log.Font = new Font("Consolas", 9f);
        _log.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _listPage.Controls.AddRange(new Control[] { lblKey, _key, verify, _status, lblBase, _base, lblDir, _dirBox, pick, refresh, _scanInfo, _list, open, addBtn, _log });
    }

    private void BuildViewPage()
    {
        var back = new Button { Text = "← 返回列表", Left = 14, Top = 10, Width = 110, Height = 30 };
        back.Click += (_, _) => ShowPage(_listPage);

        _title.Left = 134; _title.Top = 14; _title.Width = 830; _title.Height = 24;
        _title.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        _title.ForeColor = Color.FromArgb(220, 235, 255);
        _title.Text = "—";
        _title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _wave.Left = 14; _wave.Top = 46; _wave.Width = 950; _wave.Height = 170;
        _wave.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _wave.SeekRequested += SeekTo;

        _playBtn.Text = "▶ 试听"; _playBtn.Left = 14; _playBtn.Top = 226; _playBtn.Width = 100; _playBtn.Height = 32;
        _playBtn.Enabled = false;
        _playBtn.Click += (_, _) => TogglePlay();
        var stop = new Button { Text = "■ 停止", Left = 122, Top = 226, Width = 90, Height = 32 };
        stop.Click += (_, _) => StopAudio();
        var export = new Button { Text = "导出 WAV", Left = 220, Top = 226, Width = 100, Height = 32 };
        export.Click += (_, _) => ExportLast();
        export.Anchor = AnchorStyles.Top | AnchorStyles.Left;

        _seek.Left = 334; _seek.Top = 224; _seek.Width = 520; _seek.Height = 36;
        _seek.Minimum = 0; _seek.Maximum = 1000; _seek.TickStyle = TickStyle.None;
        _seek.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _seek.Scroll += (_, _) => { if (!_seeking) SeekTo(_seek.Value / 1000.0 * _waveDuration()); };

        _time.Left = 862; _time.Top = 232; _time.Width = 100; _time.Height = 20;
        _time.Text = "0:00 / 0:00";
        _time.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _details.Left = 14; _details.Top = 272; _details.Width = 950; _details.Height = 230;
        _details.Multiline = true; _details.ReadOnly = true; _details.ScrollBars = ScrollBars.Vertical;
        _details.BackColor = Color.FromArgb(12, 19, 34); _details.ForeColor = Color.FromArgb(190, 210, 235);
        _details.Font = new Font("Consolas", 9.5f);
        _details.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        var logLabel = new Label { Text = "日志", Left = 14, Top = 512, Width = 60, ForeColor = Color.FromArgb(150, 175, 210) };
        logLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _logView.Left = 14; _logView.Top = 536; _logView.Width = 950; _logView.Height = 110;
        _logView.ReadOnly = true; _logView.BackColor = Color.FromArgb(10, 16, 28); _logView.ForeColor = Color.FromArgb(160, 190, 225);
        _logView.Font = new Font("Consolas", 9f);
        _logView.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _viewPage.Controls.AddRange(new Control[] { back, _title, _wave, _playBtn, stop, export, _seek, _time, _details, logLabel, _logView });
    }

    private void ShowPage(Panel page)
    {
        _splash.Visible = false;
        _listPage.Visible = page == _listPage;
        _viewPage.Visible = page == _viewPage;
        if (page == _viewPage) _viewPage.BringToFront();
        else if (page == _listPage) _listPage.BringToFront();
    }

    private void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
        foreach (var box in new[] { _log, _logView })
        {
            try
            {
                box.AppendText(line);
                box.SelectionStart = box.TextLength;
                box.ScrollToCaret();
            }
            catch { }
        }
    }

    private void SetStatus(string text, bool ok)
    {
        _status.Text = text;
        _status.ForeColor = ok ? Color.FromArgb(127, 224, 168) : Color.FromArgb(255, 141, 141);
    }

    private string FindLdteDir()
    {
        var cands = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "LDTE训练"),
            Path.Combine(AppContext.BaseDirectory, "..", "LDTE训练"),
            Path.Combine(AppContext.BaseDirectory, "app", "LDTE训练")
        };
        foreach (var c in cands)
        {
            try { if (Directory.Exists(Path.GetFullPath(c))) return Path.GetFullPath(c); } catch { }
        }
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 4 && dir != null; i++)
            {
                string probe = Path.Combine(dir.FullName, "LDTE训练");
                if (Directory.Exists(probe)) return probe;
                dir = dir.Parent;
            }
        }
        catch { }
        return "";
    }

    private async Task ScanAsync()
    {
        _list.Items.Clear();
        _scanInfo.Text = "正在扫描 " + (_ldteDir.Length > 0 ? _ldteDir : "目录") + " …";
        if (_ldteDir.Length == 0 || !Directory.Exists(_ldteDir))
        {
            _scanInfo.Text = "没有找到 LDTE训练 目录，可点「选择目录」手动指定。";
            return;
        }
        var files = await Task.Run(() =>
        {
            var result = new List<string>();
            try
            {
                result.AddRange(Directory.GetFiles(_ldteDir, "*.ldtc", SearchOption.AllDirectories));
                result.AddRange(Directory.GetFiles(_ldteDir, "*.ldte", SearchOption.AllDirectories));
            }
            catch { }
            return result;
        });
        var ordered = files.Select(f => new FileInfo(f))
            .OrderBy(fi => fi.Extension.Equals(".ldtc", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(fi => fi.LastWriteTime)
            .ToList();
        foreach (var fi in ordered)
        {
            var it = new ListViewItem(fi.Name);
            it.SubItems.Add(fi.Extension.Equals(".ldtc", StringComparison.OrdinalIgnoreCase) ? "加密片段" : "训练档案");
            it.SubItems.Add(fi.Length.ToString("N0") + " B");
            it.SubItems.Add(fi.LastWriteTime.ToString("MM-dd HH:mm"));
            it.SubItems.Add(fi.FullName);
            it.Tag = fi.FullName;
            _list.Items.Add(it);
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        _scanInfo.Text = "扫描完成：加密片段 " + ordered.Count(x => x.Extension.Equals(".ldtc", StringComparison.OrdinalIgnoreCase))
            + " 个 / 训练档案 " + ordered.Count(x => x.Extension.Equals(".ldte", StringComparison.OrdinalIgnoreCase)) + " 个";
    }

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "LDTE 文件|*.ldtc;*.ldte|加密片段 (*.ldtc)|*.ldtc|训练档案 (*.ldte)|*.ldte|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            AddFiles(dlg.FileNames);
            ShowPage(_listPage);
        }
    }

    private void AddFiles(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".ldtc" && ext != ".ldte") continue;
            bool exists = false;
            foreach (ListViewItem x in _list.Items) if ((x.Tag as string) == f) { exists = true; break; }
            if (exists) continue;
            var fi = new FileInfo(f);
            var it = new ListViewItem(fi.Name);
            it.SubItems.Add(ext == ".ldtc" ? "加密片段" : "训练档案");
            it.SubItems.Add(fi.Length.ToString("N0") + " B");
            it.SubItems.Add(fi.LastWriteTime.ToString("MM-dd HH:mm"));
            it.SubItems.Add(fi.FullName);
            it.Tag = f;
            _list.Items.Add(it);
            it.Selected = true;
        }
        if (_list.Items.Count > 0 && _list.SelectedItems.Count == 0) _list.Items[0].Selected = true;
    }

    private void VerifyKey()
    {
        string raw = _key.Text.Trim();
        _mode = ""; _master = null; _cloudToken = ""; _cloudBase = "";
        if (raw.Length == 0) { SetStatus("请先粘贴密钥", false); return; }

        if (Crypto.IsCloudToken(raw))
        {
            string baseUrl = _base.Text.Trim().TrimEnd('/');
            if (baseUrl.Length == 0) { SetStatus("请填写云端地址", false); return; }
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string txt = http.GetStringAsync(baseUrl + "/verify?token=" + Uri.EscapeDataString(raw)).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(txt);
                if (!doc.RootElement.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("云端拒绝：" + txt);
                long exp = doc.RootElement.TryGetProperty("expires", out var ev) ? ev.GetInt64() : 0;
                _mode = "cloud"; _cloudToken = raw; _cloudBase = baseUrl;
                SetStatus("云密钥有效，到期 " + DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（云端解密）", true);
            }
            catch (Exception ex) { SetStatus("云端校验失败：" + ex.Message, false); }
            return;
        }

        try
        {
            var (key, exp) = Crypto.ParseBat1(raw);
            _master = key;
            _mode = "local";
            SetStatus("离线密钥有效，到期 " + DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（本机解密）", true);
        }
        catch (Exception ex) { SetStatus("密钥无效：" + ex.Message, false); }
    }

    private async void OpenSelected()
    {
        if (_list.SelectedItems.Count == 0) { Log("请先选择一个文件。"); return; }
        string f = _list.SelectedItems[0].Tag as string;
        if (string.IsNullOrEmpty(f) || !File.Exists(f)) { Log("文件不存在。"); return; }
        string ext = Path.GetExtension(f).ToLowerInvariant();
        _title.Text = Path.GetFileName(f);
        _details.Text = "";
        _wave.SetData(null, 0);
        _playBtn.Enabled = false;
        StopAudio();
        ShowPage(_viewPage);
        _details.Text = "正在处理…";

        try
        {
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(f));
            if (ext == ".ldte")
            {
                var info = await Task.Run(() => Crypto.ParseLdte(bytes, true));
                var sb = new StringBuilder();
                sb.AppendLine("文件      : " + Path.GetFileName(f));
                sb.AppendLine("类型      : LDTE 训练档案");
                sb.AppendLine("大小      : " + (bytes.Length / 1024.0).ToString("0.0") + " KB");
                sb.AppendLine("版本      : v" + info.Version);
                sb.AppendLine("帧率      : " + info.FrameMs + " ms/帧");
                sb.AppendLine("帧数      : " + info.Frames);
                sb.AppendLine("时长      : " + TimeText(info.Duration) + "（" + (info.Duration / 60).ToString("0.00") + " 分钟）");
                sb.AppendLine("通道      : " + info.Channels + " · 采样率 " + info.SampleRate + " Hz");
                sb.AppendLine("分段/训练 : " + info.Segments + " 段 · " + info.Passes + " 遍");
                sb.AppendLine();
                sb.AppendLine("头部 JSON：");
                sb.AppendLine(PrettyJson(info.HeaderJson));
                _details.Text = sb.ToString();
                _wave.SetData(info.Envelope, info.Duration);
                Log("已解析训练档案：" + Path.GetFileName(f));
                return;
            }

            if (_mode.Length == 0) VerifyKey();
            if (_mode.Length == 0) { _details.Text = "密钥不可用，请先在上方粘贴并验证密钥。"; return; }

            byte[] wav;
            if (_mode == "cloud")
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                using var req = new HttpRequestMessage(HttpMethod.Post, _cloudBase + "/decrypt");
                req.Headers.Add("x-ldte-token", _cloudToken);
                req.Content = new ByteArrayContent(bytes);
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var resp = await http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("云端解密失败：" + body);
                wav = await resp.Content.ReadAsByteArrayAsync();
            }
            else
            {
                wav = await Task.Run(() => Crypto.DecryptClip(bytes, _master));
            }

            var wi = await Task.Run(() => Crypto.ParseWav(wav));
            _lastWav = wav;
            _lastName = Path.GetFileNameWithoutExtension(f);
            LoadAudio(wav);
            _playBtn.Enabled = true;
            _wave.SetData(wi.Envelope, wi.Duration);

            var d = new StringBuilder();
            d.AppendLine("文件      : " + Path.GetFileName(f));
            d.AppendLine("类型      : LDTC1 加密片段（" + (_mode == "cloud" ? "云端解密" : "本机解密") + "）");
            d.AppendLine("加密大小  : " + (bytes.Length / 1024.0).ToString("0.0") + " KB");
            d.AppendLine("解密大小  : " + (wav.Length / 1024.0).ToString("0.0") + " KB");
            d.AppendLine("音频格式  : PCM 16-bit · " + wi.Channels + " 通道 · " + wi.Rate + " Hz");
            d.AppendLine("时长      : " + TimeText(wi.Duration));
            d.AppendLine("采样帧数  : " + wi.Frames);
            d.AppendLine();
            d.AppendLine("可以直接点「▶ 试听」，或拖动进度条定位；点击波形也可跳转。");
            d.AppendLine("需要保存时点「导出 WAV」。");
            _details.Text = d.ToString();
            Log("✔ 解密完成：" + Path.GetFileName(f) + "（" + (_mode == "cloud" ? "云端" : "本机") + "）");
        }
        catch (Exception ex)
        {
            _details.Text = "处理失败：" + ex.Message;
            Log("✘ " + Path.GetFileName(f) + "：" + ex.Message);
        }
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

    private double _waveDuration()
    {
        return _duration;
    }

    private double _duration;

    private void LoadAudio(byte[] wav)
    {
        CleanupAudio();
        _duration = 0;
        try
        {
            _tempWav = Path.Combine(Path.GetTempPath(), "baw_dec_" + Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(_tempWav, wav);
            var wi = Crypto.ParseWav(wav);
            _duration = wi.Duration;

            try
            {
                var t = Type.GetTypeFromProgID("WMPlayer.OCX");
                if (t != null)
                {
                    _wmp = Activator.CreateInstance(t);
                    _wmp.settings.autoStart = false;
                    _wmp.settings.volume = 100;
                    _wmp.URL = _tempWav;
                    _wmpReady = true;
                }
            }
            catch
            {
                _wmpReady = false;
                _wmp = null;
            }

            if (!_wmpReady)
            {
                _sp = new SoundPlayer(_tempWav);
                _sp.Load();
            }
            _timer.Start();
            UpdateTimeLabel(0);
        }
        catch (Exception ex)
        {
            Log("音频准备失败：" + ex.Message);
        }
    }

    private void TogglePlay()
    {
        if (_lastWav == null) return;
        try
        {
            if (_wmpReady && _wmp != null)
            {
                int state = (int)_wmp.playState;
                if (state == 3) { _wmp.controls.pause(); _playBtn.Text = "▶ 试听"; }
                else { _wmp.controls.play(); _playBtn.Text = "⏸ 暂停"; }
            }
            else if (_sp != null)
            {
                _sp.Play();
                _playBtn.Text = "▶ 播放中";
            }
        }
        catch (Exception ex) { Log("播放失败：" + ex.Message); }
    }

    private void StopAudio()
    {
        try { if (_wmpReady && _wmp != null) { _wmp.controls.stop(); } } catch { }
        try { _sp?.Stop(); } catch { }
        _playBtn.Text = "▶ 试听";
        _wave.SetPosition(0);
        UpdateTimeLabel(0);
    }

    private void SeekTo(double seconds)
    {
        if (_lastWav == null) return;
        if (seconds < 0) seconds = 0;
        if (_duration > 0 && seconds > _duration) seconds = _duration;
        try
        {
            if (_wmpReady && _wmp != null)
            {
                _wmp.controls.currentPosition = seconds;
                _wave.SetPosition(seconds);
            }
            else
            {
                StopAudio();
                UpdateTimeLabel(seconds);
            }
        }
        catch { }
    }

    private void UpdatePlayback()
    {
        if (_lastWav == null) return;
        try
        {
            if (_wmpReady && _wmp != null)
            {
                double pos = 0, dur = _duration;
                try { pos = (double)_wmp.controls.currentPosition; } catch { }
                try { double d2 = (double)_wmp.currentMedia.duration; if (d2 > 0) dur = d2; } catch { }
                _duration = dur;
                _wave.SetPosition(pos);
                UpdateTimeLabel(pos);
                _seeking = true;
                _seek.Value = dur > 0 ? (int)Math.Max(0, Math.Min(1000, pos / dur * 1000)) : 0;
                _seeking = false;
                int state = 3;
                try { state = (int)_wmp.playState; } catch { }
                if (state != 3 && _playBtn.Text == "⏸ 暂停") _playBtn.Text = "▶ 试听";
            }
        }
        catch { }
    }

    private void UpdateTimeLabel(double pos)
    {
        _time.Text = TimeText(pos) + " / " + TimeText(_duration);
    }

    private static string TimeText(double s)
    {
        if (s < 0) s = 0;
        int m = (int)(s / 60);
        int sec = (int)(s % 60);
        return m + ":" + sec.ToString("00");
    }

    private void ExportLast()
    {
        if (_lastWav == null) { Log("还没有已解密的片段。"); return; }
        using var dlg = new SaveFileDialog
        {
            Filter = "WAV 音频|*.wav",
            FileName = (_lastName.Length > 0 ? _lastName : "ldte") + ".wav"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, _lastWav);
            Log("已导出：" + dlg.FileName);
        }
        catch (Exception ex) { Log("导出失败：" + ex.Message); }
    }

    private void CleanupAudio()
    {
        _timer.Stop();
        try { if (_wmp != null) { _wmp.controls.stop(); _wmp.close(); } } catch { }
        _wmp = null;
        _wmpReady = false;
        try { _sp?.Dispose(); } catch { }
        _sp = null;
        try { if (!string.IsNullOrEmpty(_tempWav) && File.Exists(_tempWav)) File.Delete(_tempWav); } catch { }
        _tempWav = null;
    }
}
