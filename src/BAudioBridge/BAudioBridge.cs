using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BAudioBridge
{

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    static class WinMM
    {
        public const uint WAVE_MAPPER = 0xFFFFFFFF;
        public const uint WAVE_FORMAT_QUERY = 0x00000001;
        public const int WHDR_DONE = 0x00000001;

        [DllImport("winmm.dll")] public static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveOutGetDevCaps(IntPtr uDeviceID, ref WAVEOUTCAPS caps, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutOpen(IntPtr phwo, IntPtr deviceId, IntPtr pwfx, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern uint waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] public static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutWrite(IntPtr hwo, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutReset(IntPtr hwo);
    }

    static class Dolby
    {

        public static readonly string[,] List = new string[,]
        {
            { "off",    "关闭杜比处理",                      "0" },
            { "dd",     "杜比数字 5.1 直解",                  "1" },
            { "dsu",    "Dolby Surround 上混（立体声→5.1）",   "1" },
            { "cinema", "杜比影院增强（对白+低频+压缩）",       "1" },
            { "night",  "杜比夜间模式（响度均衡+压缩）",        "1" }
        };

        public static string Name(string key)
        {
            for (int i = 0; i < List.GetLength(0); i++) if (List[i, 0] == key) return List[i, 1];
            return List[0, 1];
        }

        public static bool ForceSix(string key)
        {
            for (int i = 0; i < List.GetLength(0); i++) if (List[i, 0] == key) return List[i, 2] == "1";
            return false;
        }

        public static string Filter(string key)
        {
            switch (key)
            {
                case "dd":
                    return "";

                case "dsu":

                    return "aformat=channel_layouts=stereo,"
                         + "surround=chl_out=5.1:angle=90:focus=0.1:smooth=0.4";

                case "cinema":
                    return "aformat=channel_layouts=stereo,"
                         + "surround=chl_out=5.1:angle=95:focus=0.15:smooth=0.3,"
                         + "dialoguenhance=original=0.25:enhance=1.4:voice=2,"
                         + "bass=g=4:f=85:t=q:w=1,"
                         + "treble=g=-1:f=9000:t=q:w=1,"
                         + "acompressor=threshold=-16dB:ratio=3:attack=15:release=200:makeup=2";

                case "night":
                    return "loudnorm=I=-20:TP=-3:LRA=9:print_format=none,"
                         + "acompressor=threshold=-24dB:ratio=4:attack=25:release=300:makeup=4,"
                         + "bass=g=2:f=70:t=q:w=1";

                default:
                    return "";
            }
        }
    }

    public class Player
    {

        public class RunSignal { public volatile bool stop = false; }

        const int RATE = 48000;
        const int BITS = 16;
        static readonly Guid SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");

        IntPtr hwo = IntPtr.Zero;
        IntPtr fmtPtr = IntPtr.Zero;
        Process ff = null;
        Thread worker = null;
        volatile RunSignal run = null;
        public volatile bool finished = false;
        public volatile int channels = 0;
        public volatile int deviceId = -1;
        public volatile int rate = 48000;
        public string currentUrl = "";
        public volatile string currentDolby = "";
        public volatile string currentScheme = "";
        public string lastError = "";
        public string lastNote = "";
        public volatile string ffErr = "";
        public DateTime startedAt = DateTime.MinValue;

        public bool Playing { get { RunSignal r = run; return worker != null && worker.IsAlive && r != null && !r.stop; } }
        public double Elapsed { get { return startedAt == DateTime.MinValue ? 0 : (DateTime.Now - startedAt).TotalSeconds; } }

        static IntPtr MakeFormat(int ch, int rate)
        {
            int block = ch * BITS / 8;
            if (ch <= 2)
            {
                WAVEFORMATEX f = new WAVEFORMATEX();
                f.wFormatTag = 1; f.nChannels = (ushort)ch; f.nSamplesPerSec = (uint)rate;
                f.nAvgBytesPerSec = (uint)(rate * block); f.nBlockAlign = (ushort)block;
                f.wBitsPerSample = BITS; f.cbSize = 0;
                IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEFORMATEX)));
                Marshal.StructureToPtr(f, p, false);
                return p;
            }
            WAVEFORMATEXTENSIBLE e = new WAVEFORMATEXTENSIBLE();
            e.Format.wFormatTag = 0xFFFE; e.Format.nChannels = (ushort)ch; e.Format.nSamplesPerSec = (uint)rate;
            e.Format.nAvgBytesPerSec = (uint)(rate * block); e.Format.nBlockAlign = (ushort)block;
            e.Format.wBitsPerSample = BITS; e.Format.cbSize = 22;
            e.wValidBitsPerSample = BITS;
            e.dwChannelMask = ch == 6 ? 0x3Fu : (ch == 8 ? 0x63Fu : 0x3u);
            e.SubFormat = SUBTYPE_PCM;
            IntPtr p2 = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEFORMATEXTENSIBLE)));
            Marshal.StructureToPtr(e, p2, false);
            return p2;
        }

        public static bool TryOpen(int ch, int rate, int dev, out IntPtr handle, out IntPtr fmt)
        {
            fmt = MakeFormat(ch, rate);
            IntPtr slot = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr id = dev < 0 ? new IntPtr(-1) : new IntPtr(dev);
            try
            {
                uint r = WinMM.waveOutOpen(slot, id, fmt, IntPtr.Zero, IntPtr.Zero, 0);
                if (r != 0) { Marshal.FreeHGlobal(fmt); Marshal.FreeHGlobal(slot); handle = IntPtr.Zero; fmt = IntPtr.Zero; return false; }
                handle = Marshal.ReadIntPtr(slot);
                Marshal.FreeHGlobal(slot);
                return true;
            }
            catch
            {
                try { Marshal.FreeHGlobal(slot); } catch { }
                try { Marshal.FreeHGlobal(fmt); } catch { }
                handle = IntPtr.Zero; fmt = IntPtr.Zero; return false;
            }
        }

        public static bool TryOpen(int ch, int dev, out IntPtr handle, out IntPtr fmt)
        {
            return TryOpen(ch, RATE, dev, out handle, out fmt);
        }

        public static bool CanOpen(int ch, int dev)
        {
            IntPtr h, f;
            if (!TryOpen(ch, RATE, dev, out h, out f)) return false;
            WinMM.waveOutClose(h);
            Marshal.FreeHGlobal(f);
            return true;
        }

        public static bool ProbeFormat(int ch, int rate, int dev)
        {
            IntPtr fmt = MakeFormat(ch, rate);
            IntPtr id = dev < 0 ? new IntPtr(-1) : new IntPtr(dev);
            try
            {
                uint r = WinMM.waveOutOpen(IntPtr.Zero, id, fmt, IntPtr.Zero, IntPtr.Zero, WinMM.WAVE_FORMAT_QUERY);
                return r == 0;
            }
            catch { return false; }
            finally { try { Marshal.FreeHGlobal(fmt); } catch { } }
        }

        public static int BestRate(int dev, out int ch)
        {
            int[] rates = new int[] { 192000, 96000, 48000, 44100 };
            for (int c = 0; c < 2; c++)
            {
                int want = c == 0 ? 6 : 2;
                for (int i = 0; i < rates.Length; i++)
                {
                    if (ProbeFormat(want, rates[i], dev)) { ch = want; return rates[i]; }
                }
            }
            ch = 2;
            return 48000;
        }

        public static string[] ListDevices()
        {
            int n = (int)WinMM.waveOutGetNumDevs();
            string[] arr = new string[n];
            for (int i = 0; i < n; i++)
            {
                WAVEOUTCAPS c = new WAVEOUTCAPS();
                IntPtr id = new IntPtr(i);
                uint r = WinMM.waveOutGetDevCaps(id, ref c, (uint)Marshal.SizeOf(typeof(WAVEOUTCAPS)));
                arr[i] = (r == 0 ? c.szPname : "设备 " + i) + "|" + (r == 0 ? c.wChannels.ToString() : "0");
            }
            return arr;
        }

        public void Stop()
        {
            RunSignal r = run;
            if (r != null) r.stop = true;
            try { if (hwo != IntPtr.Zero) WinMM.waveOutReset(hwo); } catch { }
            try { if (ff != null && !ff.HasExited) ff.Kill(); } catch { }
            bool joined = true;
            try { if (worker != null) joined = worker.Join(3000); } catch { joined = false; }
            worker = null;

            if (!joined)
            {
                hwo = IntPtr.Zero; fmtPtr = IntPtr.Zero; channels = 0;
                return;
            }
            try { if (hwo != IntPtr.Zero) WinMM.waveOutReset(hwo); } catch { }
            try { if (hwo != IntPtr.Zero) WinMM.waveOutClose(hwo); } catch { }
            hwo = IntPtr.Zero;
            try { if (fmtPtr != IntPtr.Zero) Marshal.FreeHGlobal(fmtPtr); } catch { }
            fmtPtr = IntPtr.Zero;
            channels = 0;
        }

        public bool Play(string url, int wantCh, string dolby, string ffmpegPath, int wantRate = 0, string scheme = "")
        {
            Stop();
            lastError = "";
            lastNote = "";
            finished = false;
            RunSignal myRun = new RunSignal();
            run = myRun;
            currentUrl = url;
            currentScheme = scheme == null ? "" : scheme;
            currentDolby = dolby == null ? "" : dolby;

            if (currentScheme == "fidelity") currentDolby = "off";
            else if (currentScheme == "sim" && (currentDolby == "" || currentDolby == "off")) currentDolby = "cinema";
            if (currentDolby == "") currentDolby = "off";

            int ch = wantCh <= 0 ? 2 : wantCh;

            if (Dolby.ForceSix(currentDolby) && ch < 6) ch = 6;
            int rate = wantRate > 0 ? wantRate : RATE;
            if (currentScheme == "fidelity" && ch < 6 && ProbeFormat(6, rate, deviceId)) ch = 6;
            if (!TryOpen(ch, rate, deviceId, out hwo, out fmtPtr))
            {
                if (rate != RATE && TryOpen(ch, RATE, deviceId, out hwo, out fmtPtr))
                {
                    lastNote = "设备不支持 " + rate + " Hz，已降到 48000 Hz";
                    rate = RATE;
                }
                else if (ch > 2 && TryOpen(2, rate, deviceId, out hwo, out fmtPtr))
                {
                    lastNote = "设备打不开 " + ch + " 声道，已降为 2 声道";
                    ch = 2;
                }
                else if (ch > 2 && TryOpen(2, RATE, deviceId, out hwo, out fmtPtr))
                {
                    lastNote = "设备打不开 " + ch + " 声道 / " + rate + " Hz，已降为 2 声道 48000 Hz";
                    ch = 2; rate = RATE;
                }
                else
                {
                    lastError = "无法打开音频输出设备（错误码 " + ch + "）";
                    return false;
                }
            }
            if (currentScheme == "attempt" && ch < 6)
            {
                currentDolby = "cinema";
                lastNote = (lastNote == "" ? "" : lastNote + "；") + "未拿到 5.1 输出，已退回杜比模拟";
            }
            if (currentScheme == "fidelity" && ch < 6)
            {
                lastNote = (lastNote == "" ? "" : lastNote + "；") + "该设备只有 2 声道，无法保真输出 5.1";
            }
            this.rate = rate;
            channels = ch;
            Wave.Reset();
            Wave.Setup(rate);

            string filt = Dolby.Filter(currentDolby);
            string args = "-hide_banner -v error -i \"" + url + "\" -vn " +
                          (filt == "" ? "" : "-af \"" + filt + "\" ") +
                          "-ac " + ch + " -ar " + rate + " -f s16le -acodec pcm_s16le pipe:1";
            ProcessStartInfo psi = new ProcessStartInfo(ffmpegPath, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            try { ff = Process.Start(psi); }
            catch (Exception ex) { lastError = "ffmpeg 启动失败：" + ex.Message; Stop(); return false; }

            ffErr = "";
            try
            {
                Process _p = ff;
                Thread er = new Thread(delegate ()
                {
                    try { ffErr = _p.StandardError.ReadToEnd(); } catch { }
                });
                er.IsBackground = true;
                er.Start();
            }
            catch { }

            startedAt = DateTime.Now;

            IntPtr dev = hwo;
            worker = new Thread(delegate () { Loop(ff, dev, myRun); });
            worker.IsBackground = true;
            worker.Start();
            return true;
        }

        int ReadFull(Stream s, byte[] buf, int len, RunSignal r)
        {
            int got = 0;
            while (got < len && !r.stop)
            {
                int n = 0;
                try { n = s.Read(buf, got, len - got); } catch { n = 0; }
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        void Loop(Process proc, IntPtr dev, RunSignal r)
        {
            const int CHUNK_MS = 350;
            int frames = rate * CHUNK_MS / 1000;
            int bytes = frames * channels * BITS / 8;
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));

            IntPtr d0 = Marshal.AllocHGlobal(bytes), d1 = Marshal.AllocHGlobal(bytes);
            IntPtr h0 = Marshal.AllocHGlobal(hdrSize), h1 = Marshal.AllocHGlobal(hdrSize);
            byte[] tmp = new byte[bytes];
            try
            {
                Stream s = proc.StandardOutput.BaseStream;
                int n = ReadFull(s, tmp, bytes, r);
                if (n <= 0) { finished = true; return; }
                Wave.Append(tmp, n, channels);
                Marshal.Copy(tmp, 0, d0, n);
                Submit(dev, h0, d0, n);

                bool useFirst = false;
                while (!r.stop)
                {
                    int n2 = ReadFull(s, tmp, bytes, r);
                    WaitDone(useFirst ? h1 : h0, 2 * CHUNK_MS + 300);

                    if (r.stop) { finished = true; break; }
                    if (n2 <= 0) { WaitDone(useFirst ? h1 : h0, 1000); finished = true; break; }
                    Wave.Append(tmp, n2, channels);
                    IntPtr hdr = useFirst ? h1 : h0, dat = useFirst ? d1 : d0;
                    Reuse(dev, hdr, dat, n2, tmp);
                    Submit(dev, hdr, dat, n2);
                    useFirst = !useFirst;
                }
            }
            catch (Exception ex) { lastError = ex.Message; }
            finally
            {

                try
                {
                    WinMM.waveOutReset(dev);
                    WaitDone(h0, 600); WaitDone(h1, 600);
                    WinMM.waveOutUnprepareHeader(dev, h0, (uint)Marshal.SizeOf(typeof(WAVEHDR)));
                    WinMM.waveOutUnprepareHeader(dev, h1, (uint)Marshal.SizeOf(typeof(WAVEHDR)));
                }
                catch { }
                Marshal.FreeHGlobal(d0); Marshal.FreeHGlobal(d1);
                Marshal.FreeHGlobal(h0); Marshal.FreeHGlobal(h1);
            }
        }

        void Reuse(IntPtr dev, IntPtr hdr, IntPtr data, int len, byte[] src)
        {

            try { WinMM.waveOutUnprepareHeader(dev, hdr, (uint)Marshal.SizeOf(typeof(WAVEHDR))); }
            catch { }
            try
            {
                Marshal.Copy(src, 0, data, len);
                WAVEHDR h = new WAVEHDR();
                h.lpData = data; h.dwBufferLength = (uint)len;
                Marshal.StructureToPtr(h, hdr, false);
            }
            catch { }
        }

        void Submit(IntPtr dev, IntPtr hdr, IntPtr data, int len)
        {
            try
            {
                WAVEHDR h = new WAVEHDR();
                h.lpData = data; h.dwBufferLength = (uint)len;
                Marshal.StructureToPtr(h, hdr, false);
                WinMM.waveOutPrepareHeader(dev, hdr, (uint)Marshal.SizeOf(typeof(WAVEHDR)));
                WinMM.waveOutWrite(dev, hdr, (uint)Marshal.SizeOf(typeof(WAVEHDR)));
            }
            catch { }
        }

        void WaitDone(IntPtr hdr, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
                if ((h.dwFlags & WinMM.WHDR_DONE) != 0) return;
                Thread.Sleep(6);
                waited += 6;
            }
        }
    }

    static class Cap
    {
        public static string FfmpegPath = "";
        public static bool Multichannel = false;
        public static Dictionary<string, bool> Decoders = new Dictionary<string, bool>();

        public static bool FindFfmpeg(string hint)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string[] cands = new string[]
            {
                hint,
                Path.Combine(baseDir, "ffmpeg.exe"),
                Path.Combine(baseDir, @"..\ffmpeg.exe"),
                Path.Combine(baseDir, @"..\..\ffmpeg.exe"),
                Path.Combine(baseDir, @"..\..\..\ffmpeg.exe"),
                @"E:\BAwiki Web\app\ffmpeg.exe"
            };
            foreach (string c in cands)
            {
                if (string.IsNullOrEmpty(c)) continue;
                try { if (File.Exists(c)) { FfmpegPath = Path.GetFullPath(c); break; } } catch { }
            }
            if (FfmpegPath == "") FfmpegPath = "ffmpeg";
            return File.Exists(FfmpegPath);
        }

        public static void ProbeDecoders()
        {
            string[] keys = new string[] { "ac3", "eac3", "dca", "truehd", "mlp" };
            foreach (string k in keys) Decoders[k] = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(FfmpegPath, "-hide_banner -decoders");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                foreach (string line in outp.Split('\n'))
                {
                    string t = line.Trim();
                    foreach (string k in keys)
                    {

                        if (t.Contains(" " + k + " ") && t.StartsWith("A"))
                        {
                            int idx = t.IndexOf(" " + k + " ");
                            string after = t.Substring(idx + k.Length + 2).TrimStart();
                            if (after.StartsWith(k + "_")) continue;
                            Decoders[k] = true;
                        }
                    }
                }
            }
            catch { }
        }
    }

    static class Wave
    {
        public const int COLS = 480;
        static readonly float[] mnA = new float[COLS];
        static readonly float[] mxA = new float[COLS];
        static long total = 0;
        static readonly object gate = new object();
        static float[] pend = new float[8192];
        static int pendN = 0;
        static int perCol = 800;
        static float peak = 0;

        public static void Setup(int sampleRate)
        {
            lock (gate) { perCol = Math.Max(1, sampleRate / 60); peak = 0; }
        }

        public static void Reset()
        {
            lock (gate)
            {
                total = 0; pendN = 0; peak = 0;
                Array.Clear(mnA, 0, COLS); Array.Clear(mxA, 0, COLS);
            }
        }

        public static void Append(byte[] pcm, int len, int ch)
        {
            if (len <= 0 || ch <= 0) return;
            lock (gate)
            {
                int step = ch * 2;
                int frames = len / step;
                for (int i = 0; i < frames; i++)
                {
                    int off = i * step;
                    int sum = 0;
                    for (int c = 0; c < ch; c++) sum += BitConverter.ToInt16(pcm, off + c * 2);
                    float v = (sum / (float)ch) / 32768f;
                    float av = v < 0 ? -v : v;
                    if (av > peak) peak = av;
                    pend[pendN++] = v;
                    if (pendN >= perCol || pendN >= pend.Length) FlushCol();
                }
            }
        }

        static void FlushCol()
        {
            float mn = 1f, mx = -1f;
            int n = Math.Min(pendN, perCol);
            for (int i = 0; i < n; i++)
            {
                if (pend[i] < mn) mn = pend[i];
                if (pend[i] > mx) mx = pend[i];
            }
            pendN = 0;
            int idx = (int)(total % COLS);
            mnA[idx] = mn; mxA[idx] = mx;
            total++;
        }

        public static string Json(long since)
        {
            lock (gate)
            {
                long from = Math.Max(since, total - COLS);
                if (from < 0) from = 0;
                int n = (int)(total - from);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"ok\":true,\"total\":").Append(total)
                  .Append(",\"perCol\":").Append(perCol)
                  .Append(",\"peak\":").Append(Math.Round(peak, 4))
                  .Append(",\"cols\":[");
                for (int k = 0; k < n; k++)
                {
                    int idx = (int)((from + k) % COLS);
                    if (k > 0) sb.Append(',');
                    sb.Append('[').Append(Math.Round(mnA[idx], 3)).Append(',')
                      .Append(Math.Round(mxA[idx], 3)).Append(']');
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }
    }

    static class Program
    {
        static Player player = new Player();
        static int port = 8236;

        static void Main(string[] args)
        {
            string ffHint = "";
            bool noOpen = false;
            foreach (string a in args)
            {
                if (a.StartsWith("--port=")) port = int.Parse(a.Substring(7));
                if (a.StartsWith("--ffmpeg=")) ffHint = a.Substring(9);
                if (a == "--no-open") noOpen = true;
            }
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "BA 本地音频引擎 · 127.0.0.1:" + port;
            AppDomain.CurrentDomain.UnhandledException += delegate (object se, UnhandledExceptionEventArgs ue)
            {
                try
                {
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BAudioBridge.err.log"),
                        DateTime.Now.ToString("s") + " " + ue.ExceptionObject + "\r\n");
                }
                catch { }
            };

            bool hasFf = Cap.FindFfmpeg(ffHint);
            Cap.ProbeDecoders();

            int nd = (int)WinMM.waveOutGetNumDevs();
            bool mc = Player.CanOpen(6, -1);
            Cap.Multichannel = mc;

            Console.WriteLine("==================================================");
            Console.WriteLine("  BA 本地音频引擎  v1.0");
            Console.WriteLine("  短按：网页负责挑歌，真正的解码与多声道输出在这里。");
            Console.WriteLine("==================================================");
            Console.WriteLine(" ffmpeg   : " + Cap.FfmpegPath + (hasFf ? "" : "  (未找到！)"));
            Console.WriteLine(" 解码器   : AC-3 " + YN(Cap.Decoders["ac3"]) + "  E-AC-3 " + YN(Cap.Decoders["eac3"]) +
                              "  DTS " + YN(Cap.Decoders["dca"]) + "  TrueHD " + YN(Cap.Decoders["truehd"]));
            Console.WriteLine(" 杜比音效 : 直解 / 上混 5.1 / 影院增强 / 夜间模式（在网页控制台里切换）");
            Console.WriteLine(" 输出设备 : " + nd + " 个（默认设备 6 声道 " + (mc ? "可直接打开" : "打不开，将降为 2 声道") + "）");
            foreach (string d in Player.ListDevices()) Console.WriteLine("   · " + d.Replace("|", "  " + "声道="));
            Console.WriteLine(" 控制台   : http://127.0.0.1:" + port + "/");
            Console.WriteLine(" 关闭本窗口即停止引擎。");
            Console.WriteLine("==================================================");

            StartServer();

            bool opened = false;
            if (!noOpen) { try { Process.Start("http://127.0.0.1:" + port + "/"); opened = true; } catch { } }
            if (!noOpen && !opened) Console.WriteLine("（未能自动打开浏览器，请手动访问上面的地址）");

            while (true) Thread.Sleep(200);
        }

        static string YN(bool b) { return b ? "✓" : "✗"; }

        static void StartServer()
        {
            HttpListener hl = new HttpListener();
            hl.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try { hl.Start(); }
            catch (Exception ex)
            {
                Console.WriteLine("[错误] 端口 " + port + " 监听失败：" + ex.Message);
                Console.WriteLine("（可能已有一个引擎在运行，或端口被占用；可用 --port=8237 换端口）");
                Console.ReadLine();
                Environment.Exit(1);
            }
            Thread t = new Thread(delegate () { Serve(hl); });
            t.IsBackground = true;
            t.Start();
        }

        static void Serve(HttpListener hl)
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = hl.GetContext(); } catch { return; }
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    try { WriteJson(ctx, 500, "{\"ok\":false,\"error\":" + J(ex.Message) + "}"); } catch { }
                }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }

        static void Cors(HttpListenerResponse r)
        {
            r.Headers["Access-Control-Allow-Origin"] = "*";
            r.Headers["Access-Control-Allow-Headers"] = "content-type";
            r.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        }

        static void WriteJson(HttpListenerContext ctx, int code, string json)
        {
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            Cors(ctx.Response);
            byte[] b = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
        }

        static void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            string path = req.Url.AbsolutePath;
            if (req.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; Cors(ctx.Response); return; }

            if (path == "/" || path == "/index.html")
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                byte[] b = Encoding.UTF8.GetBytes(UI.Html(port));
                ctx.Response.ContentLength64 = b.Length;
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                return;
            }
            if (path == "/health" || path == "/devices")
            {
                WriteJson(ctx, 200, Health());
                return;
            }
            if (path == "/wave")
            {
                long since = 0;
                long.TryParse(req.QueryString["since"], out since);
                if (since < 0) since = 0;
                WriteJson(ctx, 200, Wave.Json(since));
                return;
            }
            if (path == "/play")
            {
                string body = ReadBody(req);
                string url = Field(body, "url");
                int ch = int.Parse(Field(body, "channels") == "" ? "0" : Field(body, "channels"));
                int dev = Field(body, "device") == "" ? -1 : int.Parse(Field(body, "device"));
                int rate = Field(body, "rate") == "" ? 0 : int.Parse(Field(body, "rate"));
                string scheme = Field(body, "scheme");
                string dolby = Field(body, "dolby");
                if (dolby == "") dolby = "off";
                if (url == "") { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"缺少 url\"}"); return; }
                if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"只接受 http(s) 地址\"}"); return; }
                player.deviceId = dev;
                bool ok = player.Play(url, ch, dolby, Cap.FfmpegPath, rate, scheme);
                WriteJson(ctx, ok ? 200 : 500,
                    "{\"ok\":" + (ok ? "true" : "false") + ",\"channels\":" + player.channels +
                    ",\"rate\":" + player.rate +
                    ",\"scheme\":" + J(player.currentScheme) +
                    ",\"note\":" + J(player.lastNote) +
                    ",\"dolby\":" + J(player.currentDolby) +
                    ",\"dolbyName\":" + J(Dolby.Name(player.currentDolby)) +
                    ",\"error\":" + J(player.lastError) + "}");
                return;
            }
            if (path == "/stop")
            {
                player.Stop();
                WriteJson(ctx, 200, "{\"ok\":true}");
                return;
            }
            WriteJson(ctx, 404, "{\"ok\":false,\"error\":\"no route\"}");
        }

        static string ReadBody(HttpListenerRequest req)
        {
            try
            {
                using (Stream s = req.InputStream)
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch { return ""; }
        }

        static string Field(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\":");
            if (i < 0) return "";
            i += key.Length + 3;
            while (i < json.Length && (json[i] == ' ')) i++;
            if (i >= json.Length) return "";
            if (json[i] == '"')
            {
                int j = i + 1;
                StringBuilder sb = new StringBuilder();
                while (j < json.Length && json[j] != '"')
                {
                    if (json[j] == '\\' && j + 1 < json.Length) { j++; }
                    sb.Append(json[j]); j++;
                }
                return sb.ToString();
            }
            int k = i;
            while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '-')) k++;
            return json.Substring(i, k - i);
        }

        static string J(string s)
        {
            if (s == null) s = "";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ") + "\"";
        }

        static string DefaultDeviceName()
        {
            WAVEOUTCAPS c = new WAVEOUTCAPS();
            uint r = WinMM.waveOutGetDevCaps(new IntPtr(-1), ref c, (uint)Marshal.SizeOf(typeof(WAVEOUTCAPS)));
            return r == 0 ? c.szPname : "";
        }

        static bool IsBluetooth(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return n.Contains("bluetooth") || n.Contains("蓝牙") || n.Contains("hands-free") || n.Contains("a2dp");
        }

        static string Health()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"name\":\"BA 本地音频引擎\",\"version\":\"1.0\",\"port\":").Append(port);
            sb.Append(",\"ffmpeg\":").Append(J(Cap.FfmpegPath));
            sb.Append(",\"decoders\":{");
            sb.Append("\"ac3\":").Append(Cap.Decoders["ac3"] ? "true" : "false");
            sb.Append(",\"eac3\":").Append(Cap.Decoders["eac3"] ? "true" : "false");
            sb.Append(",\"dca\":").Append(Cap.Decoders["dca"] ? "true" : "false");
            sb.Append(",\"truehd\":").Append(Cap.Decoders["truehd"] ? "true" : "false");
            sb.Append("}");
            sb.Append(",\"multichannel\":").Append(Cap.Multichannel ? "true" : "false");
            sb.Append(",\"devices\":[");
            string[] ds = Player.ListDevices();
            string defName = DefaultDeviceName();

            int[] probeRateN = new int[] { 44100, 48000, 96000, 192000 };
            bool btAny = false;
            for (int i = 0; i < ds.Length; i++)
            {
                string[] p = ds[i].Split('|');
                bool bt = IsBluetooth(p[0]);
                if (bt) btAny = true;
                bool isDef = defName != "" && p[0] == defName;
                if (i > 0) sb.Append(",");
                sb.Append("{\"id\":").Append(i)
                  .Append(",\"name\":").Append(J(p[0]))
                  .Append(",\"channels\":").Append(p[1])
                  .Append(",\"bluetooth\":").Append(bt ? "true" : "false")
                  .Append(",\"default\":").Append(isDef ? "true" : "false")
                  .Append(",\"formats\":[");
                bool first = true;
                for (int c = 0; c < 2; c++)
                {
                    int chWant = c == 0 ? 2 : 6;
                    for (int r = 0; r < probeRateN.Length; r++)
                    {
                        bool ok2 = Player.ProbeFormat(chWant, probeRateN[r], i);
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("{\"ch\":").Append(chWant).Append(",\"rate\":").Append(probeRateN[r])
                          .Append(",\"ok\":").Append(ok2 ? "true" : "false").Append("}");
                    }
                }
                sb.Append("]}");
            }
            sb.Append("]");
            sb.Append(",\"defaultName\":").Append(J(defName));
            sb.Append(",\"bluetooth\":").Append(btAny ? "true" : "false");
            sb.Append(",\"playing\":").Append(player.Playing ? "true" : "false");
            sb.Append(",\"channels\":").Append(player.channels);
            sb.Append(",\"rate\":").Append(player.rate);
            sb.Append(",\"scheme\":").Append(J(player.currentScheme));
            sb.Append(",\"note\":").Append(J(player.lastNote));
            sb.Append(",\"dolby\":").Append(J(player.currentDolby));
            sb.Append(",\"dolbyName\":").Append(J(Dolby.Name(player.currentDolby)));
            sb.Append(",\"dolbyPresets\":[");
            for (int di = 0; di < Dolby.List.GetLength(0); di++)
            {
                if (di > 0) sb.Append(",");
                sb.Append("{\"key\":").Append(J(Dolby.List[di, 0]));
                sb.Append(",\"name\":").Append(J(Dolby.List[di, 1]));
                sb.Append(",\"six\":").Append(Dolby.List[di, 2] == "1" ? "true" : "false").Append("}");
            }
            sb.Append("]");
            bool d6 = Player.ProbeFormat(6, 48000, -1);
            bool ac3 = Cap.Decoders.ContainsKey("ac3") && Cap.Decoders["ac3"];
            bool eac3 = Cap.Decoders.ContainsKey("eac3") && Cap.Decoders["eac3"];
            sb.Append(",\"schemes\":[");
            sb.Append("{\"k\":\"fidelity\",\"name\":\"绝对保真\",\"ok\":").Append(d6 && (ac3 || eac3) ? "true" : "false")
              .Append(",\"why\":").Append(J(d6
                  ? "引擎可解 AC-3 / E-AC-3 并打开 6 声道通路；是否硬件原生取决于驱动与接口"
                  : "当前输出打不开 6 声道，无法保真输出 5.1")).Append("}");
            sb.Append(",{\"k\":\"sim\",\"name\":\"明确告知的模拟\",\"ok\":true")
              .Append(",\"why\":").Append(J("不声称杜比授权效果，界面统一标注为「环绕增强模拟」")).Append("}");
            sb.Append(",{\"k\":\"attempt\",\"name\":\"直接尝试\",\"ok\":true")
              .Append(",\"why\":").Append(J("先按保真输出，设备拿不到 5.1 时自动退回模拟并写明原因")).Append("}");
            sb.Append("]");
            sb.Append(",\"elapsed\":").Append(Math.Round(player.Elapsed, 1));
            sb.Append(",\"url\":").Append(J(player.currentUrl));
            sb.Append(",\"error\":").Append(J(player.lastError));
            string fe = player.ffErr;
            if (fe.Length > 300) fe = fe.Substring(0, 300);
            sb.Append(",\"ffError\":").Append(J(fe));
            sb.Append("}");
            return sb.ToString();
        }
    }

    static class UI
    {
        public static string Html(int port)
        {
            string s;
            using (System.IO.Stream st = typeof(UI).Assembly.GetManifestResourceStream("BAudioBridge.ui.html"))
            {
                using (System.IO.StreamReader r = new System.IO.StreamReader(st, System.Text.Encoding.UTF8))
                    s = r.ReadToEnd();
            }
            return s.Replace("{{PORT}}", port.ToString());
        }
    }
}
