using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.IO.MemoryMappedFiles;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BAwikiWeb;

internal static partial class Server
{

    private static string AppDir = "";
    private static long _stageMark;
    private static string WebDir = "";
    private static string IdxDir = "";
    private static string ThumbDir = "";
    private static string DataDir = "";
    private static string BiliDir = "";
    private static string? Ffmpeg;
    private static string LogFile = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string? _jsonStudents;
    private static string? _jsonGallery;
    private static string? _jsonMusic;
    private static string? _jsonVideos;
    private static string? _jsonSteam;
    private static readonly object _musicLock = new();
    private static readonly object _musicWatchLock = new();
    private static FileSystemWatcher? _musicWatcher;
    private static System.Threading.Timer? _musicTimer;
    private static List<Dictionary<string, object>>? _studentList;
    private static Dictionary<string, Dictionary<string, object>>? _studentDirMap;

    private static readonly HttpClient _kivoHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string KIVO_API = "https://api.kivo.wiki/api/v1";

    private static volatile bool _autoUpdateRunning = false;
    private static string _autoUpdateStatus = "空闲";
    private static int _autoUpdateTotal, _autoUpdateDone;
    private static readonly object _autoUpdLock = new();

    private class ThumbJob { public string Src; public string Rel; public string Type; public int MaxDim; }
    private static readonly ConcurrentQueue<ThumbJob> _thumbQueue = new();
    private static int _thumbDone;
    private static int _thumbTotal;
    private static int _posterDone;
    private static int _posterTotal;
    private static readonly object _thumbStatLock = new();

    private static readonly object _playlistLock = new();
    private static List<Dictionary<string, object>> _playlist = new();
    private static int _playlistIdx = -1;
    private static string _playMode = "seq";
    private static readonly object _favLock = new();
    private static HashSet<string> _favorites = new();

    private static volatile bool _mediaKeysEnabled = false;

    private static int _localPort = 0;
    private static bool _extEnabled = false;
    private static int _extPort = 8235;
    private static TcpListener? _extListener;
    private static volatile bool _extRunning = false;
    private static readonly object _extLock = new();
    private static string _extSettingsFile = "";
    private static string _accountsFile = "";

    private static readonly string _extMark = Guid.NewGuid().ToString("N");

    private static readonly ConcurrentDictionary<string, (int Count, long WindowStart)> _loginFail = new();
    private const int LoginFailMax = 5;
    private static readonly long LoginFailWindow = TimeSpan.FromMinutes(10).Ticks;

    private static bool LoginBlocked(string? ip, string? name)
    {
        string key = (ip ?? "") + "|" + (name ?? "");
        long now = DateTime.UtcNow.Ticks;
        var v = _loginFail.GetOrAdd(key, _ => (0, now));
        if (now - v.WindowStart > LoginFailWindow) { _loginFail[key] = (0, now); return false; }
        return v.Count >= LoginFailMax;
    }

    private static void LoginFail(string? ip, string? name)
    {
        string key = (ip ?? "") + "|" + (name ?? "");
        long now = DateTime.UtcNow.Ticks;
        _loginFail.AddOrUpdate(key, _ => (1, now),
            (_, v) => now - v.WindowStart > LoginFailWindow ? (1, now) : (v.Count + 1, v.WindowStart));
    }

    private static void LoginOk(string? ip, string? name)
        => _loginFail.TryRemove((ip ?? "") + "|" + (name ?? ""), out _);

    private class Account
    {
        public string Name = "";
        public string Pass = "";
        public string Role = "user";
        public List<string> Perms = new();
        public int KbPerSec = 0;
        public long QuotaMB = 0;
        public long UsedTodayKB = 0;
        public string DayKey = "";

        public bool UiAnim = true, UiTrans = true, UiFast = true, UiDl = true;
        public bool HasUiSet = false;
    }
    private class Session
    {
        public string Token = "";
        public Account Acct;
        public string Ip = "";
        public long StartTicks;
        public long LastSeenTicks;
        public long Bytes;
    }
    private class AdminSession
    {
        public string Token = "";
        public string Name = "";
        public string Role = "user";
        public long LastSeenTicks;
    }
    private static readonly object _accLock = new();
    private static readonly object _sessLock = new();
    private static readonly object _admLock = new();
    private static List<Account> _accounts = new();
    private static readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AdminSession> _adminSessions = new(StringComparer.Ordinal);
    private static long _extBytesToday;
    private static string _extDayKey = "";

    private static string _settingsFile = "";
    private class SiteSettings
    {
        public bool UiAnimation = true;
        public bool UiTransition = true;
        public bool FastView = true;
        public bool ShowDownload = true;
    }
    private static SiteSettings _settings = new();
    private static readonly object _settingsLock = new();

    private static readonly HashSet<string> _authIps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _authLock = new();
    private static string _authFile = "";
    private const string AuthPsk = "CHANGE_ME_AUTH_PSK";

    private static readonly string[] ImgExt   = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif" };
    private static readonly string[] AudioExt = { ".flac", ".mp3", ".m4a", ".ogg", ".wav" };
    private static readonly string[] VideoExt = { ".mp4", ".mkv", ".webm", ".mov" };

    private static volatile string _dlStatus = "idle";
    private static volatile int _dlTotal;
    private static volatile int _dlDone;
    private static volatile int _dlFail;
    private static volatile string _dlCurrent = "";
    private static readonly object _dlLock = new();

    private static void Main(string[] args)
    {

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { Log("[FATAL] 未捕获异常: " + e.ExceptionObject); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Log("[TASK] 未观察的任务异常: " + e.Exception); } catch { }
            e.SetObserved();
        };
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            CrashLog(ex);
        }
    }

    private static void CrashLog(Exception ex)
    {
        string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BAwiki_crash.log");
        try
        {
            File.AppendAllText(p,
                "===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====" + Environment.NewLine +
                "系统  : " + RuntimeInformation.OSDescription + Environment.NewLine +
                "运行时: " + RuntimeInformation.FrameworkDescription + Environment.NewLine +
                "进程  : " + (Environment.Is64BitProcess ? "64 位" : "32 位") + Environment.NewLine +
                ex + Environment.NewLine + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("  ==================================================");
            Console.WriteLine("    启动失败： " + ex.Message);
            Console.WriteLine("    详细记录已写入：" + p);
            Console.WriteLine("  ==================================================");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  窗口会保留，按任意键退出。");
            try { Console.ReadKey(true); } catch { Thread.Sleep(60000); }
        }
        catch { }
    }

    private static void Run(string[] args)
    {
        var sw = Stopwatch.StartNew();
        _stageMark = 0;
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Console.Title = "BAwiki Web - 本地维基服务器 (请勿关闭)";

        AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        WebDir = Path.Combine(AppDir, "web");
        LogFile = Path.Combine(AppDir, "BAwikiWeb.log");
        Ffmpeg = Path.Combine(AppDir, "ffmpeg.exe");
        if (!File.Exists(Ffmpeg)) Ffmpeg = null;

        Banner();
        SelfCheck();
        Log("服务器启动");
        Console.WriteLine("  [警告] 请勿关闭本窗口！本地维基服务器正在运行。");
        Console.WriteLine();

        Console.WriteLine("  [1/4] 正在定位数据文件夹……");
        DataDir = FindDataDir() ?? PromptForDataDir();
        if (string.IsNullOrEmpty(DataDir) || !Directory.Exists(DataDir))
        {
            Console.WriteLine("        数据目录无效，按任意键退出。");
            TryReadKey();
            return;
        }
        Console.WriteLine("        数据目录: " + DataDir);
        Stage("定位数据目录", sw);

        string wiki = Path.Combine(DataDir, "_本地维基");
        IdxDir   = Path.Combine(wiki, "index");
        ThumbDir = Path.Combine(wiki, "thumbs");
        BiliDir  = Path.Combine(DataDir, "05_B站视频");
        Directory.CreateDirectory(IdxDir);
        Directory.CreateDirectory(ThumbDir);
        Directory.CreateDirectory(BiliDir);
        LoadPerfConfig();
        LoadGpuConfig();
        LoadAudioLearn();
        CleanupImportTemp();
        Stage("性能配置", sw);

        _extSettingsFile = Path.Combine(IdxDir, "external.json");
        _accountsFile = Path.Combine(IdxDir, "accounts.json");
        _settingsFile = Path.Combine(IdxDir, "settings.json");
        _authFile = Path.Combine(IdxDir, "auth_devices.json");
        LoadAccounts();
        LoadExternalSettings();
        LoadSettings();
        LoadAuthDevices();
        RefreshLocalIps();
        LoadLobbyData();
        InitAi();
        Stage("配置与账号", sw);

        Console.WriteLine();
        Console.WriteLine("  [2/4] 正在载入索引……");
        var favFile = Path.Combine(IdxDir, "favorites.json");
        if (File.Exists(favFile))
        {
            try { _favorites = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(favFile)) ?? new(); }
            catch { _favorites = new(); }
        }
        bool fastCache = TryLoadCacheFast();
        Stage(fastCache ? "载入索引缓存" : "无缓存", sw);

        Console.WriteLine();
        Console.WriteLine("  [3/4] 正在启动本地服务器……");
        int port = StartServer();
        if (port == 0)
        {
            Console.WriteLine("        服务器启动失败。按任意键退出。");
            TryReadKey();
            return;
        }
        _localPort = port;
        Stage("端口就绪", sw);

        Console.WriteLine();
        Console.WriteLine("  [4/4] 服务器已就绪！索引与缩略图在后台继续处理，不影响浏览。");
        new Thread(() =>
        {
            var bsw = Stopwatch.StartNew();
            try
            {
                BuildSteamIndex();
                if (!fastCache)
                {
                    Console.WriteLine("        首次运行：正在后台建立索引……");
                    BuildIndexes(useCache: false);
                    Console.WriteLine("        索引建立完成。");
                }
                else if (CacheStale())
                {
                    Console.WriteLine("        数据目录有变化，正在后台重建索引……");
                    BuildIndexes(useCache: false);
                    Console.WriteLine("        索引重建完成。");
                }
                Console.WriteLine("        后台索引总耗时 " + bsw.ElapsedMilliseconds + " ms");
                Log("后台索引总耗时 " + bsw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex) { Console.WriteLine("        后台索引失败: " + ex.Message); }
        }) { IsBackground = true }.Start();

        StartThumbWorkers();
        StartPosterWorker();
        EnsureParseService();
        EnsurePlugins();
        StartMusicWatcher();
        StartCloudKeyJanitor();

        if (_extEnabled)
        {
            Console.WriteLine("        对外服务已开启（端口 " + _extPort + "），局域网用户需凭账号访问。");
            StartExternal(_extPort);
        }

        string url = "http://127.0.0.1:" + port + "/";
        Console.WriteLine();
        Console.WriteLine("  ==================================================");
        Console.WriteLine("    基沃托斯古书馆 · 本地离线版 已启动！");
        Console.WriteLine("    访问地址: " + url);
        Console.WriteLine("  ==================================================");
        Console.WriteLine("    可用命令（输入后回车）:");
        Console.WriteLine("      AR    - 全部资源更新（从 kivo.wiki 抓取新角色+清理+重建索引）");
        Console.WriteLine("      RI    - 重建索引");
        Console.WriteLine("      ST    - 查看状态");
        Console.WriteLine("      AI    - AI 指令（AI HELP 查看）");
        Console.WriteLine("      HELP  - 列出全部命令");
        Console.WriteLine("      Q     - 退出服务器");
        Console.WriteLine("    提示：若控制台输入无反应，可把命令写入 console_cmd.txt（每行一条）");
        Console.WriteLine("  ==================================================");
        Console.WriteLine("  [启动完成] 从进程启动到页面可访问: " + sw.ElapsedMilliseconds + " ms");
        Log("启动完成 总耗时 " + sw.ElapsedMilliseconds + " ms，端口 " + port);
        Console.WriteLine("  ==================================================");
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }

        var cmdThread = new Thread(CommandLoop) { IsBackground = true };
        cmdThread.Start();
        new Thread(PollCommandFile) { IsBackground = true }.Start();

        while (true)
        {
            Thread.Sleep(60000);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    private static string ConsoleCmdFile => Path.Combine(AppDir, "console_cmd.txt");
    private static string ConsoleOutFile => Path.Combine(AppDir, "console_out.txt");

    private static void CommandLoop()
    {
        TextReader? reader = null;
        try
        {
            IntPtr h = CreateFileW("CONIN$", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, true), FileAccess.Read);
                reader = new StreamReader(fs, Console.InputEncoding, false, 4096);
            }
            else if (!Console.IsInputRedirected)
            {
                reader = Console.In;
            }
        }
        catch { }

        if (reader == null)
        {
            try
            {
                reader = Console.In;
            }
            catch { }
        }

        if (reader != null)
        {
            int nulls = 0;
            while (true)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch { line = null; }
                if (line == null)
                {
                    if (++nulls >= 3) break;
                    Thread.Sleep(200);
                    continue;
                }
                nulls = 0;
                line = line.Trim();
                if (line.Length == 0) continue;
                ExecuteConsoleCommand(line);
            }
        }

        try
        {
            Console.WriteLine("  [控制台] 当前进程没有可用的输入通道。");
            Console.WriteLine("  [控制台] 可把命令写入 " + ConsoleCmdFile + "（每行一条），每秒自动执行并清空。");
        }
        catch { }
        Log("[控制台] 无输入通道，改用文件指令");
    }

    private static void PollCommandFile()
    {
        string last = "";
        while (true)
        {
            try
            {
                if (File.Exists(ConsoleCmdFile))
                {
                    string text = File.ReadAllText(ConsoleCmdFile);
                    if (text.Trim().Length > 0 && text.Trim() != last)
                    {
                        last = text.Trim();
                        var lines = text.Split('\n');
                        foreach (var raw in lines)
                        {
                            string line = raw.Trim();
                            if (line.Length == 0) continue;
                            ExecuteConsoleCommand(line);
                        }
                        try { File.WriteAllText(ConsoleCmdFile, "", Encoding.UTF8); } catch { }
                    }
                }
            }
            catch { }
            Thread.Sleep(1000);
        }
    }

    private static string ExecuteConsoleCommand(string line)
    {
        string cmd = line.Trim().ToUpperInvariant();
        var outp = new StringBuilder();
        void Say(string s)
        {
            Console.WriteLine(s);
            outp.AppendLine(s);
        }
        try
        {
            switch (cmd)
            {
                case "AR":
                    if (_autoUpdateRunning)
                        Say("        [AR] 已在运行中，当前状态: " + _autoUpdateStatus);
                    else
                    {
                        Say("        [AR] 启动全部资源更新…");
                        new Thread(() => { try { RunAutoUpdate(); } catch (Exception ex) { Log("[AR] 异常: " + ex.Message); } finally { _autoUpdateRunning = false; } })
                        { IsBackground = true }.Start();
                        _autoUpdateRunning = true;
                    }
                    break;
                case "RI":
                    Say("        [RI] 重建索引中…");
                    new Thread(() => { BuildIndexes(useCache: false); Console.WriteLine("        [RI] 索引重建完成。"); }) { IsBackground = true }.Start();
                    break;
                case "ST":
                case "STATUS":
                    Say("        [状态] 自动更新: " + (_autoUpdateRunning ? "运行中 - " + _autoUpdateStatus : "空闲"));
                    Say("        [状态] 缩略图: " + _thumbDone + " / " + _thumbTotal);
                    Say("        [状态] 学生: " + (_studentList?.Count ?? 0) + " 名");
                    break;
                case "HELP":
                case "H":
                case "?":
                    Say("        AR      全部资源更新");
                    Say("        RI      重建索引");
                    Say("        ST      查看状态");
                    Say("        AI ...  AI 指令（AI HELP 查看）");
                    Say("        Q       退出");
                    break;
                case "Q":
                case "EXIT":
                    Say("        正在退出…");
                    ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); Environment.Exit(0); });
                    break;
                default:
                    if (HandleAiCommand(line)) break;
                    Say("        未知命令: " + line + "（输入 HELP 查看可用命令）");
                    break;
            }
        }
        catch (Exception ex)
        {
            Say("        命令执行失败: " + ex.Message);
            Log("[控制台] " + line + " 失败: " + ex.Message);
        }
        try { if (outp.Length > 0) File.AppendAllText(ConsoleOutFile, outp.ToString(), Encoding.UTF8); } catch { }
        return outp.ToString();
    }

    private static void SelfCheck()
    {
        Console.WriteLine("  [自检] 系统 " + RuntimeInformation.OSDescription);
        Console.WriteLine("  [自检] 运行时 " + RuntimeInformation.FrameworkDescription + " / " +
                          (Environment.Is64BitProcess ? "64 位" : "32 位"));
        Console.WriteLine("  [自检] 程序目录 " + AppDir);
        try
        {
            string probe = Path.Combine(AppDir, ".write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Console.WriteLine("  [自检] 目录可写 ✓");
        }
        catch
        {
            Console.WriteLine("  [自检] 目录不可写 ✗ —— 请把程序放到非受保护目录，或以管理员身份运行");
        }
        Console.WriteLine("  [自检] ffmpeg " + (Ffmpeg != null ? "✓" : "✗ 未找到，视频封面将用占位图"));
        Console.WriteLine();
    }

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine("  ==================================================");
        Console.WriteLine("        BAwiki Web  基沃托斯古书馆 · 本地离线版");
        Console.WriteLine("        学生图鉴 / CG画廊 / 留声机 / 官方PV / B站视频");
        Console.WriteLine("        + Steam 索引 + 桌宠 + 媒体键 + 歌词 + 收藏");
        Console.WriteLine("  ==================================================");
        Console.WriteLine();
    }

    private static void Log(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
            File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    private static void TryReadKey()
    {
        try { Console.ReadKey(); } catch { }
    }

    private static void Stage(string name, Stopwatch sw)
    {
        long now = sw.ElapsedMilliseconds;
        string line = "[耗时] " + name + " " + (now - _stageMark) + " ms（累计 " + now + " ms）";
        Console.WriteLine("        " + line);
        Log("启动" + line);
        _stageMark = now;
    }

    private static string? FindDataDir()
    {
        string[] cands = {
            "E:\\kivo_wiki",
            "D:\\kivo_wiki",
            "F:\\kivo_wiki",
            Path.Combine(AppDir, "..", "..", "kivo_wiki"),
            Path.Combine(AppDir, "kivo_wiki")
        };
        foreach (var p in cands)
        {
            try
            {
                var full = Path.GetFullPath(p);
                if (Directory.Exists(Path.Combine(full, "01_学生"))) return full;
            }
            catch { }
        }
        return null;
    }

    private static string? PromptForDataDir()
    {
        Console.Write("        未自动找到 kivo_wiki，请手动输入完整路径（如 E:\\kivo_wiki）: ");
        string? text = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(text) && Directory.Exists(text.Trim()))
            return text.Trim();
        return null;
    }

    private static bool TryLoadCacheFast()
    {
        try
        {

            if (TryLoadBinaryCache())
            {
                FinishFastLoad("内存缓存命中");
                return true;
            }

            string sp = Path.Combine(IdxDir, "students.json");
            string gp = Path.Combine(IdxDir, "gallery.json");
            string mp = Path.Combine(IdxDir, "music.json");
            string vp = Path.Combine(IdxDir, "videos.json");
            if (!File.Exists(sp) || !File.Exists(gp) || !File.Exists(mp) || !File.Exists(vp)) return false;

            _jsonStudents = File.ReadAllText(sp, Encoding.UTF8);
            _jsonGallery  = File.ReadAllText(gp, Encoding.UTF8);
            _jsonMusic    = File.ReadAllText(mp, Encoding.UTF8);
            _jsonVideos   = File.ReadAllText(vp, Encoding.UTF8);

            WriteBinaryCache();
            FinishFastLoad("已载入上次索引");
            return true;
        }
        catch { return false; }
    }

    private static void FinishFastLoad(string note)
    {
        RebuildStudentIndexFromJson();
        EnqueueMissingThumbs();
        lock (_thumbStatLock) _thumbTotal = _thumbQueue.Count;
        Console.WriteLine("        ✓ " + note + "（后台校验中）");
        Log("索引 " + note);
    }

    private static bool CacheStale()
    {
        try
        {
            string cacheMeta = Path.Combine(IdxDir, "_cache_meta.json");
            if (!File.Exists(cacheMeta)) return true;
            var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(cacheMeta));
            if (meta == null || !meta.TryGetValue("mru", out var v) || !v.TryGetInt64(out long m)) return true;
            return m != GetDirMruTimestamp(DataDir, 3);
        }
        catch { return true; }
    }

    private static void BuildIndexes(bool useCache)
    {

        lock (_thumbStatLock) { _thumbDone = 0; _thumbTotal = 0; }
        lock (_thumbEnqLock) _thumbQueued.Clear();

        long dataMru = GetDirMruTimestamp(DataDir, 3);
        string cacheMeta = Path.Combine(IdxDir, "_cache_meta.json");
        if (useCache && File.Exists(cacheMeta))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(cacheMeta));
                if (meta != null && meta.TryGetValue("mru", out var v) && v.TryGetInt64(out long m) && m == dataMru)
                {
                    if (File.Exists(Path.Combine(IdxDir, "students.json")) &&
                        File.Exists(Path.Combine(IdxDir, "gallery.json")) &&
                        File.Exists(Path.Combine(IdxDir, "music.json")) &&
                        File.Exists(Path.Combine(IdxDir, "videos.json")))
                    {
                        _jsonStudents = File.ReadAllText(Path.Combine(IdxDir, "students.json"), Encoding.UTF8);
                        _jsonGallery  = File.ReadAllText(Path.Combine(IdxDir, "gallery.json"), Encoding.UTF8);
                        _jsonMusic    = File.ReadAllText(Path.Combine(IdxDir, "music.json"), Encoding.UTF8);
                        _jsonVideos   = File.ReadAllText(Path.Combine(IdxDir, "videos.json"), Encoding.UTF8);
                        RebuildStudentIndexFromJson();
                        BuildSteamIndex();
                        Console.WriteLine("        ✓ 索引命中缓存（数据目录未变化）");
                        EnqueueMissingThumbs();
                        lock (_thumbStatLock) _thumbTotal = _thumbQueue.Count;
                        return;
                    }
                }
            }
            catch { }
        }

        BuildStudentIndex();
        BuildGalleryIndex();
        BuildMusicIndex();
        BuildVideoIndex();
        BuildSteamIndex();

        File.WriteAllText(Path.Combine(IdxDir, "students.json"), _jsonStudents ?? "{}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(IdxDir, "gallery.json"),  _jsonGallery  ?? "{}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(IdxDir, "music.json"),    _jsonMusic    ?? "{}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(IdxDir, "videos.json"),  _jsonVideos   ?? "{}", Encoding.UTF8);
        File.WriteAllText(cacheMeta, JsonSerializer.Serialize(new Dictionary<string, object> { ["mru"] = dataMru }), Encoding.UTF8);
        WriteBinaryCache();

        lock (_thumbStatLock) _thumbTotal = _thumbQueue.Count;
    }

    private static long GetDirMruTimestamp(string dir, int maxDepth)
    {
        long ts = 0;
        try
        {
            var di = new DirectoryInfo(dir);
            if (di.Exists)
            {
                ts = Math.Max(ts, di.LastWriteTimeUtc.Ticks);
                if (maxDepth > 0)
                {
                    foreach (var sub in di.GetDirectories())
                    {

                        if (string.Equals(sub.Name, "_本地维基", StringComparison.OrdinalIgnoreCase)) continue;
                        ts = Math.Max(ts, GetDirMruTimestamp(sub.FullName, maxDepth - 1));
                    }
                }
            }
        }
        catch { }
        return ts;
    }

    private static void RebuildStudentIndexFromJson()
    {
        try
        {
            using var doc = JsonDocument.Parse(_jsonStudents!);
            _studentList = new List<Dictionary<string, object>>();
            _studentDirMap = new Dictionary<string, Dictionary<string, object>>();
            foreach (var stu in doc.RootElement.GetProperty("students").EnumerateArray())
            {
                var dict = JsonElementToDict(stu);
                _studentList.Add(dict);
                if (dict.TryGetValue("_dirMap", out var dm) && dm is Dictionary<string, object> dmo)
                    foreach (var kv in dmo) _studentDirMap[kv.Key] = dict;
            }
        }
        catch { }
    }

    private static Dictionary<string, object> JsonElementToDict(JsonElement e)
    {
        var d = new Dictionary<string, object>();
        foreach (var p in e.EnumerateObject())
        {
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? (object)l : p.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => JsonElementToList(p.Value),
                JsonValueKind.Object => JsonElementToDict(p.Value),
                _ => ""
            };
        }
        return d;
    }

    private static List<object> JsonElementToList(JsonElement e)
    {
        var l = new List<object>();
        foreach (var i in e.EnumerateArray()) l.Add(i.ValueKind == JsonValueKind.Object ? JsonElementToDict(i) : i.ToString());
        return l;
    }

    private static void BuildStudentIndex()
    {
        var students = new List<Dictionary<string, object>>();
        string root = Path.Combine(DataDir, "01_学生");
        int total = Directory.Exists(root) ? Directory.GetDirectories(root).Length : 0;
        if (!Directory.Exists(root)) goto done;

        var knownSkins = new HashSet<string>(StringComparer.Ordinal) { "恐怖", "原皮" };
        foreach (var d in Directory.GetDirectories(root))
        {
            var infoFile = Path.Combine(d, "_info.json");
            if (!File.Exists(infoFile)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(infoFile, Encoding.UTF8));
                var skin = Str(info, "skin_cn", "skin").Trim('*', ' ');
                if (!string.IsNullOrEmpty(skin)) knownSkins.Add(skin);
            }
            catch { }
        }

        var dirs = Directory.GetDirectories(root);
        var slot = new Dictionary<string, object>?[dirs.Length];
        int doneCount = 0;
        var po = new ParallelOptions { MaxDegreeOfParallelism = _ioThreads };
        Parallel.For(0, dirs.Length, po, idx =>
        {
            slot[idx] = BuildStudentGroup(dirs[idx], knownSkins);
            int n = Interlocked.Increment(ref doneCount);
            if (n % 50 == 0 || n == dirs.Length)
                lock (_consoleLock) ConsoleWriteAt("        学生: " + n + " / " + dirs.Length);
        });
        var groups = new List<Dictionary<string, object>>(dirs.Length);
        foreach (var g in slot) if (g != null) groups.Add(g);

        var byKey = new Dictionary<string, List<Dictionary<string, object>>>();
        foreach (var g in groups)
        {
            string key = (string)g["chKey"];
            if (!byKey.ContainsKey(key)) byKey[key] = new List<Dictionary<string, object>>();
            byKey[key].Add(g);
        }
        foreach (var list in byKey.Values)
        {
            list.Sort((a, b) =>
            {
                bool a0 = (string)a["skin"] == "原皮";
                bool b0 = (string)b["skin"] == "原皮";
                if (a0 != b0) return a0 ? -1 : 1;
                return ((int)a["idNum"]).CompareTo((int)b["idNum"]);
            });

            bool firstOrig = true;
            var seenSkins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in list)
            {
                string sk = (string)v["skin"];
                if (sk == "原皮")
                {
                    if (!firstOrig)
                    {

                        string[] pp = ((string)v["dir"]).Split('_');
                        string tail = pp.Length > 2 ? pp[^1] : "";
                        if (!string.IsNullOrEmpty(tail) && tail != pp[1] && !int.TryParse(tail, out _))
                            v["skin"] = tail;
                        else
                            v["skin"] = "原皮(" + v["id"] + ")";
                    }
                    firstOrig = false;
                }

                string curSkin = (string)v["skin"];
                if (seenSkins.Contains(curSkin))
                    v["skin"] = curSkin + "(" + v["id"] + ")";
                else
                    seenSkins.Add(curSkin);
            }
            var head = list[0];
            var variants = new List<object>();
            var dirMap = new Dictionary<string, object>();
            foreach (var v in list)
            {
                variants.Add(new Dictionary<string, object> { ["id"] = v["id"], ["dir"] = v["dir"], ["skin"] = v["skin"] });
                dirMap[(string)v["id"]] = v["dir"];
            }
            students.Add(new Dictionary<string, object>
            {
                ["id"] = head["id"], ["cn"] = head["cn"], ["jp"] = head["jp"],
                ["school"] = head["school"], ["grade"] = head["grade"], ["birthday"] = head["birthday"],
                ["height"] = head["height"], ["weapon"] = head["weapon"],
                ["illustrator"] = head["illustrator"], ["voice"] = head["voice"],
                ["rarity"] = head["rarity"], ["pos"] = head["pos"], ["attr"] = head["attr"],
                ["dir"] = head["dir"], ["variants"] = variants, ["_dirMap"] = dirMap
            });
        }
        students.Sort((a, b) =>
        {
            int x = 0, y = 0;
            int.TryParse((string)a["id"], out x);
            int.TryParse((string)b["id"], out y);
            return x.CompareTo(y);
        });
        Console.WriteLine();

        done:
        _studentList = students;
        _studentDirMap = new Dictionary<string, Dictionary<string, object>>();
        foreach (Dictionary<string, object> s in students)
        {
            if (s.TryGetValue("_dirMap", out var dm) && dm is Dictionary<string, object> dmo)
                foreach (var kv in dmo) _studentDirMap[(string)kv.Key] = s;
        }
        _jsonStudents = JsonSerializer.Serialize(new Dictionary<string, object> { ["students"] = students }, JsonOpts);
        Console.WriteLine("        学生索引: " + students.Count + " 名角色（含皮肤版本 " + total + " 个）");
    }

    private static Dictionary<string, object>? BuildStudentGroup(string dir, HashSet<string> knownSkins)
    {
        var infoFile = Path.Combine(dir, "_info.json");
        Dictionary<string, object>? info = null;
        if (File.Exists(infoFile))
        {
            try { info = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(infoFile, Encoding.UTF8)); }
            catch { }
        }
        string id = Str(info, "id");
        if (string.IsNullOrEmpty(id)) id = Path.GetFileName(dir).Split('_')[0];
        int.TryParse(id, out int idNum);

        string cnName = (Str(info, "family_name_cn", "family_name") + Str(info, "given_name_cn", "given_name")).Trim();
        string jpName = (Str(info, "family_name_jp", "family_name") + Str(info, "given_name_jp", "given_name")).Trim();
        string fileName = Path.GetFileName(dir);
        string after = fileName.IndexOf('_') >= 0 ? fileName.Substring(fileName.IndexOf('_') + 1) : fileName;
        string[] parts = after.Split('_');
        string skin = Str(info, "skin_cn", "skin").Trim('*', ' ');
        if (string.IsNullOrEmpty(skin) && parts.Length > 2 && knownSkins.Contains(parts[^1]))
            skin = parts[^1];

        string chKey;
        if (!string.IsNullOrEmpty(cnName))
        {
            chKey = cnName;
            if (!string.IsNullOrEmpty(skin))
            {
                string s2 = skin.Replace("*", ""), c2 = cnName.Replace("*", "");
                if (c2.EndsWith(s2) && c2.Length > s2.Length) chKey = c2.Substring(0, c2.Length - s2.Length);
            }
        }
        else
        {
            int take = (parts.Length > 2 && knownSkins.Contains(parts[^1])) ? parts.Length - 1 : parts.Length;
            var sb = new StringBuilder();
            for (int j = 0; j < take; j++) sb.Append(parts[j]);
            chKey = sb.ToString();
        }
        chKey = chKey.Replace("_", "").Replace(" ", "").Replace("*", "");
        if (string.IsNullOrWhiteSpace(cnName)) cnName = string.Join("", parts);

        string rarity = "", pos = "", attr = "";
        if (info != null && info.TryGetValue("character_datas", out var cd) && cd is JsonElement cde && cde.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in cde.EnumerateArray())
            {
                var dd = JsonElementToDict(item);
                rarity = Str(dd, "rarity");
                pos    = Str(dd, "team_position");
                attr   = Str(dd, "attack_attribute");
                break;
            }
        }

        string dirRel = fileName.Replace('\\', '/');

        bool hasContent = false;
        try
        {
            hasContent = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".webp") ||
                          f.EndsWith(".ogg") || f.EndsWith(".mp3") || f.EndsWith(".wav"));
        }
        catch { }
        if (!hasContent) return null;

        var avatar = Path.Combine(dir, "立绘", "头像.png");
        if (File.Exists(avatar)) EnqueueThumb(avatar, dirRel + "/avatar", "students", 256);

        return new Dictionary<string, object>
        {
            ["id"] = id, ["idNum"] = idNum, ["dir"] = dirRel,
            ["cn"] = cnName, ["jp"] = jpName, ["chKey"] = chKey,
            ["skin"] = string.IsNullOrEmpty(skin) ? "原皮" : skin,
            ["school"]   = SchoolName(Str(info, "school")),
            ["grade"]    = Str(info, "grade"),
            ["birthday"]= Str(info, "birthday"),
            ["height"]   = Str(info, "height"),
            ["weapon"]   = Str(info, "weapon_type"),
            ["illustrator"]= Str(info, "illustrator"),
            ["voice"]    = Str(info, "character_voice"),
            ["rarity"]   = rarity, ["pos"] = pos, ["attr"] = attr
        };
    }

    private static void BuildGalleryIndex()
    {

        var catList = new List<(string path, string group)>();
        foreach (var sub in new[] { "02_官方画廊", "02_galleries" })
        {
            var path = Path.Combine(DataDir, sub);
            if (!Directory.Exists(path)) continue;
            foreach (var cat in Directory.GetDirectories(path)) catList.Add((cat, sub));
        }

        var cats = new Dictionary<string, object>[catList.Count];
        int count = 0;
        var po = new ParallelOptions { MaxDegreeOfParallelism = _ioThreads };
        Parallel.For(0, catList.Count, po, i =>
        {
            var (cat, sub) = catList[i];
            var subs = new List<object>();
            var topImgs = Array.FindAll(Directory.GetFiles(cat), f => Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0);
            if (topImgs.Length > 0)
            {
                var files = new List<object>();
                foreach (var f in topImgs)
                    if (IsValidImage(f)) { files.Add(Rel(f)); EnqueueThumb(f, Rel(f), "gallery", 500); Interlocked.Increment(ref count); }
                if (files.Count > 0) subs.Add(new Dictionary<string, object> { ["name"] = "全部", ["files"] = files });
            }
            foreach (var sub2 in Directory.GetDirectories(cat))
            {
                var files = new List<object>();
                foreach (var f in Directory.GetFiles(sub2, "*.*", SearchOption.AllDirectories))
                    if (Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0 && IsValidImage(f))
                    { files.Add(Rel(f)); EnqueueThumb(f, Rel(f), "gallery", 500); Interlocked.Increment(ref count); }
                if (files.Count > 0) subs.Add(new Dictionary<string, object> { ["name"] = Path.GetFileName(sub2), ["files"] = files });
            }
            cats[i] = new Dictionary<string, object> { ["name"] = Path.GetFileName(cat), ["group"] = sub, ["subs"] = subs };
        });

        var ordered = cats.Where(c => c != null).ToList();
        _jsonGallery = JsonSerializer.Serialize(new Dictionary<string, object> { ["cats"] = ordered }, JsonOpts);
        Console.WriteLine("        画廊索引: " + ordered.Count + " 个分类, " + count + " 张图片（" + _ioThreads + " 线程并行）");
    }

    private static void BuildMusicIndex()
    {
        var tracks = new List<object>();
        var root = Path.Combine(DataDir, "03_游戏音乐");
        if (Directory.Exists(root))
        {
            var dirs = Directory.GetDirectories(root);
            var parts = new List<Dictionary<string, object>>?[dirs.Length];
            int done = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = _ioThreads };
            Parallel.For(0, dirs.Length, po, idx =>
            {
                parts[idx] = BuildMusicTracks(dirs[idx]);
                int n = Interlocked.Increment(ref done);
                if (n % 100 == 0 || n == dirs.Length)
                    lock (_consoleLock) ConsoleWriteAt("        音乐: " + n + " / " + dirs.Length);
            });
            foreach (var p in parts) if (p != null) tracks.AddRange(p);

            var ostKeys = new HashSet<string>();
            foreach (Dictionary<string, object> t in tracks)
                if (Convert.ToInt32(t["ost"]) > 0) ostKeys.Add((string)t["_nkey"]);
            int removed = 0;
            for (int k = tracks.Count - 1; k >= 0; k--)
            {
                var t = (Dictionary<string, object>)tracks[k];
                if (Convert.ToInt32(t["ost"]) == 0 && (bool)t["_reprint"] && ostKeys.Contains((string)t["_nkey"]))
                { tracks.RemoveAt(k); removed++; }
                else { t.Remove("_nkey"); t.Remove("_reprint"); }
            }
            tracks.Sort((a, b) =>
            {
                var da = (Dictionary<string, object>)a; var db = (Dictionary<string, object>)b;
                int x = Convert.ToInt32(da["ost"]); int y = Convert.ToInt32(db["ost"]);
                if (x > 0 && y > 0) return x.CompareTo(y);
                if (x != y) return x <= 0 ? 1 : -1;
                int r = Convert.ToInt32(da["num"]).CompareTo(Convert.ToInt32(db["num"]));
                return r != 0 ? r : string.CompareOrdinal((string)da["name"], (string)db["name"]);
            });
            Console.WriteLine();
            if (removed > 0) Console.WriteLine("        去重专辑转存版: " + removed + " 首");
        }

        var lossTracks = BuildLosslessTracks(Path.Combine(DataDir, "10_无损音乐"));
        if (lossTracks.Count > 0)
        {
            tracks.AddRange(lossTracks);
            Console.WriteLine("        无损音轨: " + lossTracks.Count + " 首");
        }

        lock (_musicLock) _jsonMusic = JsonSerializer.Serialize(new Dictionary<string, object> { ["tracks"] = tracks }, JsonOpts);
        Console.WriteLine("        留声机索引: " + tracks.Count + " 首（OST + MV，并行扫描）");
    }

    private static List<Dictionary<string, object>>? BuildMusicTracks(string d)
    {
        string? audio = null, cover = null, lrc = null, mp4 = null;
        foreach (var f in Directory.GetFiles(d))
        {
            string ext = Path.GetExtension(f).ToLower();
            string fname = Path.GetFileName(f);
            if (Array.IndexOf(AudioExt, ext) >= 0 && audio == null) audio = f;
            else if (ext == ".mp4" && mp4 == null) mp4 = f;
            else if (fname.Contains("封面") && ext == ".png") cover = f;
            else if (ext == ".lrc") lrc = f;
        }
        if (audio == null && mp4 == null) return null;
        string fileName = Path.GetFileName(d);
        int num = 0;
        int us = fileName.IndexOf('_');
        if (us > 0) int.TryParse(fileName.Substring(0, us), out num);
        int ost = 0; string? themeTag = null;
        var m = Regex.Match(fileName, @"Theme_(\d+)(?:_([A-Za-z]+))?$");
        if (m.Success)
        {
            int.TryParse(m.Groups[1].Value, out ost);
            themeTag = m.Groups[2].Success ? m.Groups[2].Value : null;
        }
        string name = us > 0 && us < 4 ? fileName.Substring(us + 1) : fileName;
        name = Regex.Replace(name, @"_Theme_\d+.*$", "");
        name = name.Replace("_unk", "").Replace("（OST）", "").Replace("(OST)", "").Replace('_', ' ').Trim();
        if (cover != null) EnqueueThumb(cover, Rel(d) + "/cover", "music", 320);

        string? artist = null, use = null, album = null, bv = null, title2 = null;
        var infoFile = Path.Combine(d, "_info.json");
        if (File.Exists(infoFile))
        {
            try
            {
                string content = File.ReadAllText(infoFile, Encoding.UTF8);
                var info = JsonSerializer.Deserialize<Dictionary<string, object>>(content);
                artist = Str(info, "artist", "composer", "author", "music_by");
                title2 = Str(info, "name_cn", "name", "title");
                use = Str(info, "use");
                album = Str(info, "album");
                bv = ExtractBv(content);
            }
            catch { }
        }
        string displayName = string.IsNullOrEmpty(title2) ? name : title2;
        if (themeTag != null && displayName.IndexOf(themeTag, StringComparison.OrdinalIgnoreCase) < 0)
            displayName += " (" + themeTag + ")";
        bool isReprint = fileName.Contains("（OST）") || fileName.Contains("(OST)");
        string normKey = Regex.Replace(displayName.Replace("（", "(").Replace("）", ")"), @"[\(（].*$", "");
        normKey = Regex.Replace(normKey, @"[^0-9a-zA-Z一-鿿]", "").ToLower();

        var list = new List<Dictionary<string, object>>(2);
        if (audio != null)
        {
            var t = new Dictionary<string, object>
            {
                ["name"] = displayName, ["kind"] = "ost", ["num"] = num, ["ost"] = ost,
                ["dir"] = Rel(d), ["audio"] = Rel(audio)
            };
            if (cover != null) t["cover"] = Rel(cover);
            if (lrc != null)   t["lrc"]   = Rel(lrc);
            if (artist != null) t["artist"] = artist;
            if (use != null)   t["use"]   = use;
            if (album != null) t["album"] = album;
            if (bv != null)    t["bv"]    = bv;
            t["_nkey"] = normKey; t["_reprint"] = isReprint;
            list.Add(t);
        }
        if (mp4 != null)
        {
            var t = new Dictionary<string, object>
            {
                ["name"] = displayName + " (MV)", ["kind"] = "mv", ["num"] = num, ["ost"] = ost,
                ["dir"] = Rel(d), ["video"] = Rel(mp4)
            };
            if (cover != null) t["cover"] = Rel(cover);
            if (artist != null) t["artist"] = artist;
            if (use != null)   t["use"]   = use;
            if (album != null) t["album"] = album;
            if (bv != null)    t["bv"]    = bv;
            t["_nkey"] = normKey; t["_reprint"] = isReprint;
            list.Add(t);
        }
        return list;
    }

    private static List<Dictionary<string, object>> BuildLosslessTracks(string root)
    {
        var list = new List<Dictionary<string, object>>();
        if (!Directory.Exists(root)) return list;
        int rejected = 0;
        var guides = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (Array.IndexOf(AudioExt, ext) < 0) continue;
            if (!ValidateAudioFile(f, out string why))
            {
                rejected++;
                Log("[合规] 已跳过 " + Path.GetFileName(f) + "：" + why);
                continue;
            }

            string dir = Path.GetDirectoryName(f) ?? root;
            var guide = LoadLosslessGuide(dir, root, guides);
            var track = LoadTrackGuide(f);
            bool isLossless = !(guide.TryGetValue("lossless", out var lv) && lv is bool lb && !lb);
            if (track != null && track.TryGetValue("lossless", out var tv) && tv is bool tb) isLossless = tb;
            if (!isLossless) continue;
            if (track != null && track.TryGetValue("enabled", out var ev) && ev is bool eb && !eb) continue;
            var meta = new Dictionary<string, object>(guide, StringComparer.OrdinalIgnoreCase);
            if (track != null)
                foreach (var kv in track) meta[kv.Key] = kv.Value;

            string fn = Path.GetFileNameWithoutExtension(f);
            string artist = "", title = fn;
            int dash = fn.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                artist = fn.Substring(0, dash).Trim();
                title = fn.Substring(dash + 3).Trim();
            }
            string? trackName = track == null ? null : (GuideText(track, "name") ?? GuideText(track, "title"));
            if (trackName != null) title = trackName;
            string? trackArtist = track == null ? null : (GuideText(track, "artist") ?? GuideText(track, "author"));
            if (trackArtist != null) artist = trackArtist;
            else if (artist.Length == 0) artist = GuideText(guide, "artist") ?? GuideText(guide, "author") ?? "";

            var t = new Dictionary<string, object>
            {
                ["name"] = title,
                ["kind"] = "lossless",
                ["num"] = 0,
                ["ost"] = 0,
                ["dir"] = Rel(f),
                ["audio"] = Rel(f),
                ["lossless"] = true,
                ["fmt"] = ext.TrimStart('.').ToUpperInvariant(),
                ["bytes"] = new FileInfo(f).Length
            };
            if (artist.Length > 0) t["artist"] = artist;
            string? album = GuideText(meta, "album");
            if (album != null) t["album"] = album;
            string? genre = GuideText(meta, "genre");
            if (genre != null) t["genre"] = genre;
            string? year = GuideText(meta, "year") ?? GuideText(meta, "date");
            if (year != null) t["year"] = year;
            string? cover = GuideText(meta, "cover");
            if (cover != null && File.Exists(cover))
            {
                t["cover"] = Rel(cover);
                EnqueueThumb(cover, Rel(f) + "/cover", "music", 320);
            }

            if (ext == ".flac")
            {
                try
                {
                    var info = ReadFlacInfo(f);
                    if (info != null)
                    {
                        t["bits"] = info.Value.Bits;
                        t["hz"] = info.Value.Rate;
                        t["ch"] = info.Value.Channels;
                        t["dur"] = Math.Round(info.Value.Duration, 1);
                    }
                }
                catch { }
            }
            list.Add(t);
        }
        list.Sort((a, b) => string.CompareOrdinal(
            (string)((Dictionary<string, object>)a)["name"],
            (string)((Dictionary<string, object>)b)["name"]));
        if (rejected > 0) Console.WriteLine("        合规检验: 跳过 " + rejected + " 个不合格音频");
        return list;
    }

    private static Dictionary<string, object> LoadLosslessGuide(string dir, string root, Dictionary<string, Dictionary<string, object>> cache)
    {
        if (cache.TryGetValue(dir, out var cached)) return cached;
        var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string? parent = Path.GetDirectoryName(dir);
        if (parent != null && dir.Length > root.Length && !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kv in LoadLosslessGuide(parent, root, cache)) merged[kv.Key] = kv.Value;
        }
        string gf = Path.Combine(dir, "_lossless.json");
        var g = ReadGuideFile(gf, dir);
        if (g != null)
            foreach (var kv in g) merged[kv.Key] = kv.Value;
        cache[dir] = merged;
        return merged;
    }

    private static Dictionary<string, object>? LoadTrackGuide(string audioPath)
    {
        string path = audioPath + ".json";
        if (!File.Exists(path))
        {
            path = Path.ChangeExtension(audioPath, ".json");
            if (!File.Exists(path)) return null;
        }
        return ReadGuideFile(path, Path.GetDirectoryName(audioPath) ?? "");
    }

    private static Dictionary<string, object>? ReadGuideFile(string path, string baseDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                object val = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString()!,
                    JsonValueKind.Number => p.Value.TryGetInt64(out long l) ? l : p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => p.Value.Clone()
                };
                if (p.Name.Equals("cover", StringComparison.OrdinalIgnoreCase) && val is string cs && cs.Length > 0 && !Path.IsPathRooted(cs))
                    val = Path.GetFullPath(Path.Combine(baseDir, cs));
                d[p.Name] = val;
            }
            return d;
        }
        catch { return null; }
    }

    private static string? GuideText(Dictionary<string, object> guide, string key)
    {
        if (!guide.TryGetValue(key, out var v) || v == null) return null;
        string? s = v as string ?? v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static void StartMusicWatcher()
    {
        try
        {
            string root = Path.Combine(DataDir, "10_无损音乐");
            if (!Directory.Exists(root)) return;
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            FileSystemEventHandler on = (_, e) => { if (MusicWatchRelevant(e.FullPath)) ScheduleMusicRefresh(); };
            w.Created += on;
            w.Changed += on;
            w.Deleted += on;
            w.Renamed += (_, e) => { if (MusicWatchRelevant(e.FullPath) || MusicWatchRelevant(e.OldFullPath)) ScheduleMusicRefresh(); };
            w.Error += (_, _) => { try { w.EnableRaisingEvents = false; w.EnableRaisingEvents = true; } catch { } };
            w.EnableRaisingEvents = true;
            _musicWatcher = w;
            Console.WriteLine("        无损音乐热加载已开启（目录级引导 _lossless.json）");
            Log("无损音乐热加载已开启");
        }
        catch (Exception ex)
        {
            Console.WriteLine("        无损音乐热加载开启失败: " + ex.Message);
        }
    }

    private static bool MusicWatchRelevant(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".json") return true;
        return Array.IndexOf(AudioExt, ext) >= 0;
    }

    private static void ScheduleMusicRefresh()
    {
        if (_musicWatcher == null) return;
        lock (_musicWatchLock)
        {
            _musicTimer?.Dispose();
            _musicTimer = new System.Threading.Timer(_ => RefreshLosslessTracks(), null, 2000, Timeout.Infinite);
        }
    }

    private static void RefreshLosslessTracks()
    {
        try
        {
            string root = Path.Combine(DataDir, "10_无损音乐");
            var loss = BuildLosslessTracks(root);
            string merged = MergeLosslessTracks(loss);
            lock (_musicLock) _jsonMusic = merged;
            File.WriteAllText(Path.Combine(IdxDir, "music.json"), merged, Encoding.UTF8);
            long mru = GetDirMruTimestamp(DataDir, 3);
            File.WriteAllText(Path.Combine(IdxDir, "_cache_meta.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["mru"] = mru }), Encoding.UTF8);
            WriteBinaryCache();
            Console.WriteLine("        [热加载] 无损音轨 " + loss.Count + " 首已更新");
            Log("无损热加载 " + loss.Count + " 首");
        }
        catch (Exception ex) { Log("无损热加载失败: " + ex.Message); }
    }

    private static string MergeLosslessTracks(List<Dictionary<string, object>> loss)
    {
        var sb = new StringBuilder("{\"tracks\":[");
        bool first = true;
        string? cur = _jsonMusic;
        if (!string.IsNullOrEmpty(cur))
        {
            try
            {
                using var doc = JsonDocument.Parse(cur);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("tracks", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in arr.EnumerateArray())
                    {
                        if (t.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "lossless") continue;
                        if (!first) sb.Append(',');
                        sb.Append(t.GetRawText());
                        first = false;
                    }
                }
            }
            catch { }
        }
        foreach (var t in loss)
        {
            if (!first) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(t, JsonOpts));
            first = false;
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static (int Bits, int Rate, int Channels, double Duration)? ReadFlacInfo(string path)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[26];
        int n = 0;
        while (n < b.Length)
        {
            int r = fs.Read(b, n, b.Length - n);
            if (r <= 0) break;
            n += r;
        }
        if (n < b.Length) return null;
        if (b[0] != 0x66 || b[1] != 0x4C || b[2] != 0x61 || b[3] != 0x43) return null;
        if ((b[4] & 0x7F) != 0) return null;

        int rate = (b[18] << 12) | (b[19] << 4) | (b[20] >> 4);
        int ch = ((b[20] >> 1) & 0x07) + 1;
        int bits = (((b[20] & 0x01) << 4) | (b[21] >> 4)) + 1;
        long total = ((long)(b[21] & 0x0F) << 32) | ((long)b[22] << 24) | ((long)b[23] << 16) | ((long)b[24] << 8) | b[25];
        if (rate <= 0) return null;
        return (bits, rate, ch, (double)total / rate);
    }

    private static bool ValidateAudioFile(string path, out string reason)
    {
        reason = "";
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(AudioExt, ext) < 0) { reason = "扩展名不支持"; return false; }
            var fi = new FileInfo(path);
            if (fi.Length < 64) { reason = "文件过短"; return false; }
            var head = new byte[16];
            using (var fs = File.OpenRead(path))
            {
                int got = 0;
                while (got < head.Length)
                {
                    int r = fs.Read(head, got, head.Length - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got < 12) { reason = "文件头不足"; return false; }
            }
            switch (ext)
            {
                case ".flac":
                    if (!(head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')) { reason = "FLAC 头不匹配"; return false; }
                    var info = ReadFlacInfo(path);
                    if (info != null)
                    {
                        var v = info.Value;
                        if (v.Channels < 1 || v.Channels > 8) { reason = "声道数超出上限"; return false; }
                        if (v.Rate < 8000 || v.Rate > 384000) { reason = "采样率超出范围"; return false; }
                        if (v.Bits < 4 || v.Bits > 32) { reason = "位深超出范围"; return false; }
                        if (v.Duration <= 0 || v.Duration > 24 * 3600) { reason = "时长超出上限"; return false; }
                    }
                    break;
                case ".wav":
                    if (!(head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                          && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E'))
                    { reason = "WAV 头不匹配"; return false; }
                    break;
                case ".mp3":
                    if (!(head[0] == 'I' && head[1] == 'D' && head[2] == '3') && !(head[0] == 0xFF && (head[1] & 0xE0) == 0xE0))
                    { reason = "MP3 头不匹配"; return false; }
                    break;
                case ".m4a":
                    if (!(head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')) { reason = "M4A 头不匹配"; return false; }
                    break;
                case ".ogg":
                    if (!(head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S')) { reason = "OGG 头不匹配"; return false; }
                    break;
            }
            return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    private static void BuildVideoIndex()
    {
        var videos = new List<object>();
        string pv = Path.Combine(DataDir, "04_官方PV视频");
        if (Directory.Exists(pv))
        {
            var catMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string idx = Path.Combine(pv, "_PV索引.txt");
            if (File.Exists(idx))
            {
                string curCat = "官方PV";
                foreach (var line in File.ReadAllLines(idx, Encoding.UTF8))
                {
                    string t = line.Trim();
                    if (t.StartsWith("【") && t.Contains("】")) { curCat = t.Trim('【', '】').Trim(); continue; }
                    foreach (var token in t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        if (token.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) catMap[token] = curCat;
                }
            }
            foreach (var f in Directory.GetFiles(pv))
            {
                if (Array.IndexOf(VideoExt, Path.GetExtension(f).ToLower()) >= 0)
                {
                    string fname = Path.GetFileName(f);
                    string cat = catMap.TryGetValue(fname, out var c) ? c : "官方PV";
                    string title = Path.GetFileNameWithoutExtension(f);
                    var bv = Regex.Match(title, @"BV[A-Za-z0-9]+");
                    if (bv.Success) title = title.Replace("_" + bv.Value, "").Replace(bv.Value, "").Trim('_', ' ');
                    while (title.Length > 0 && (char.IsDigit(title[0]) || title[0] == '_')) title = title.Substring(1);
                    videos.Add(new Dictionary<string, object>
                    {
                        ["title"] = title, ["file"] = Rel(f), ["category"] = cat,
                        ["posterKey"] = Rel(f), ["bv"] = bv.Success ? bv.Value : ""
                    });
                }
            }
        }
        if (Directory.Exists(BiliDir))
        {
            foreach (var f in Directory.GetFiles(BiliDir, "*.*", SearchOption.AllDirectories))
            {
                if (Array.IndexOf(VideoExt, Path.GetExtension(f).ToLower()) >= 0)
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    string rel = f.Substring(BiliDir.TrimEnd('\\').Length).TrimStart('\\').Replace('\\', '/');
                    videos.Add(new Dictionary<string, object>
                    {
                        ["title"] = name, ["file"] = "bili:" + rel, ["category"] = "B站官方视频",
                        ["posterKey"] = "bili:" + rel
                    });
                }
            }
        }
        _jsonVideos = JsonSerializer.Serialize(new Dictionary<string, object> { ["videos"] = videos }, JsonOpts);
        Console.WriteLine("        视频索引: " + videos.Count + " 个 (官方PV/B站)");
    }

    private static void BuildSteamIndex()
    {
        var result = new Dictionary<string, object>();
        string steamGame = "E:\\SteamLibrary\\steamapps\\common\\BlueArchive";
        if (!Directory.Exists(steamGame))
        {
            foreach (var sp in new[] { "D:\\SteamLibrary", "C:\\Program Files (x86)\\Steam" })
            {
                var guess = Path.Combine(sp, "steamapps\\common\\BlueArchive");
                if (Directory.Exists(guess)) { steamGame = guess; break; }
            }
        }
        if (!Directory.Exists(steamGame))
        {
            result["found"] = false;
            result["note"] = "未在 E:/D:/C: 找到 Steam 中的《蔚蓝档案》游戏目录";
            _jsonSteam = JsonSerializer.Serialize(result, JsonOpts);
            Console.WriteLine("        Steam 索引: 未找到游戏目录");
            return;
        }

        result["found"] = true;
        result["gameDir"] = steamGame;
        string dataDir = Path.Combine(steamGame, "BlueArchive_Data");
        string mediaDir = Path.Combine(dataDir, "StreamingAssets\\PUB\\Resource");

        var bundles = new List<object>();
        if (Directory.Exists(mediaDir))
        {
            foreach (var f in Directory.GetFiles(mediaDir, "*.molru", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                string rel = f.Substring(mediaDir.Length).TrimStart('\\').Replace('\\', '/');
                bundles.Add(new Dictionary<string, object>
                {
                    ["path"] = rel, ["size"] = fi.Length, ["mtime"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                });
            }
        }
        result["mediaDir"] = mediaDir;
        result["bundles"] = bundles;
        result["totalBundles"] = bundles.Count;
        long totalSize = bundles.Sum(o => (long)((Dictionary<string, object>)o)["size"]);
        result["totalSize"] = totalSize;
        _jsonSteam = JsonSerializer.Serialize(result, JsonOpts);
        Console.WriteLine("        Steam 索引: " + bundles.Count + " 个 .molru 资源包");
    }

    private static string Str(Dictionary<string, object>? d, params string[] keys)
    {
        if (d == null) return "";
        foreach (var k in keys)
        {
            if (d.TryGetValue(k, out var v) && v != null)
            {
                string s = v switch
                {
                    string str => str,
                    JsonElement je => je.ValueKind == JsonValueKind.String ? (je.GetString() ?? "") : je.ToString(),
                    _ => v.ToString() ?? ""
                };
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return "";
    }

    private static string Rel(string full) => full.Substring(DataDir.Length).TrimStart('\\').Replace('\\', '/');

    private static string SchoolName(string id) => id switch
    {
        "1" => "阿拜多斯高等学院", "2" => "崔尼蒂综合学院", "3" => "歌赫娜学院",
        "4" => "千禧年科技学院", "5" => "红冬联邦学院", "6" => "沙勒 (夏莱)",
        "7" => "山海经高等学院", "8" => "瓦尔基丽警员学院", "9" => "百鬼夜行联合学院",
        "10" => "阿里乌斯分院", "11" => "SRT特殊学院", "12" => "联邦理事会",
        "13" => "克罗诺斯学院", "14" => "狂猎艺术学院", "15" => "奥德赛海洋学院",
        "16" => "天使24便利店", "17" => "不良少女们", "18" => "地方组织与居民",
        "19" => "凯撒集团", "20" => "数秘会", "21" => "神名十文字", "22" => "斯兰匹亚",
        "23" => "无尽怪谈图书馆", "24" => "海兰德铁道学院", "25" => "所属存疑",
        "26" => "联动作品", "27" => "诸圣相通", "28" => "幻魉百物语", "29" => "星界诸神",
        "30" => "制作组相关", _ => "学园 #" + id
    };

    private static void ConsoleWriteAt(string s)
    {
        try { Console.CursorLeft = 0; Console.Write(s + "        "); }
        catch { Console.WriteLine(s); }
    }

    private static bool IsValidImage(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            long len = fs.Length;
            if (len < 4) return false;
            byte[] b = new byte[12];
            int r = fs.Read(b, 0, 12);
            if (r < 4) return false;
            if (b[0] == 0xFF && b[1] == 0xD8)
            {
                if (len < 8) return false;
                byte[] t = new byte[2];
                fs.Seek(-2, SeekOrigin.End); fs.Read(t, 0, 2);
                return t[0] == 0xFF && t[1] == 0xD9;
            }
            if (b[0] == 137 && b[1] == 80 && b[2] == 78 && b[3] == 71)
            {
                if (len < 12) return false;
                byte[] t = new byte[8]; fs.Seek(-8, SeekOrigin.End); fs.Read(t, 0, 8);
                return t[0] == 73 && t[1] == 69 && t[2] == 78 && t[3] == 68;
            }
            if (b[0] == 71 && b[1] == 73 && b[2] == 70) return true;
            if (b[0] == 82 && b[1] == 73 && b[2] == 70 && b[3] == 70)
            {
                if (len < 12) return false;
                return b[8] == 87 && b[9] == 69 && b[10] == 66 && b[11] == 80;
            }
            if (b[0] == 66 && b[1] == 77) return true;
            if (len >= 12 && b[4] == 102 && b[5] == 116 && b[6] == 121 && b[7] == 112) return true;
            return false;
        }
        catch { return false; }
    }

    private static readonly HashSet<string> _thumbQueued = new(StringComparer.Ordinal);
    private static readonly object _thumbEnqLock = new();
    private static readonly object _consoleLock = new();
    private static void EnqueueThumb(string src, string key, string type, int maxDim)
    {

        string dst = ThumbPath(type, key);
        if (File.Exists(dst)) return;
        lock (_thumbEnqLock)
        {

            string dedup = type + "|" + key;
            if (!_thumbQueued.Add(dedup)) return;
        }
        _thumbQueue.Enqueue(new ThumbJob { Src = src, Rel = key, Type = type, MaxDim = maxDim });
    }

    private static void EnqueueMissingThumbs()
    {
        try
        {
            if (_jsonStudents != null)
            {
                using var doc = JsonDocument.Parse(_jsonStudents);
                foreach (var s in doc.RootElement.GetProperty("students").EnumerateArray())
                {
                    if (s.TryGetProperty("dir", out var d))
                    {
                        string dir = d.GetString() ?? "";
                        string avatar = Path.Combine(DataDir, "01_学生", dir.Replace('/', '\\'), "立绘", "头像.png");
                        if (File.Exists(avatar)) EnqueueThumb(avatar, dir + "/avatar", "students", 256);
                    }
                }
            }
        }
        catch { }
    }

    private static string ThumbPath(string type, string key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(type + "|" + key));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return Path.Combine(ThumbDir, type, sb.ToString() + ".jpg");
    }

    private static void StartThumbWorkers()
    {

        int workers = Math.Clamp(_perf.Threads > 0 ? _perf.Threads : Environment.ProcessorCount, 4, 32);
        for (int i = 0; i < workers; i++)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    if (!_thumbQueue.TryDequeue(out var job))
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                    try
                    {
                        string dst = ThumbPath(job.Type, job.Rel);
                        if (File.Exists(dst)) { Interlocked.Increment(ref _thumbDone); continue; }
                        MakeThumb(job.Src, dst, job.MaxDim);
                    }
                    catch (Exception ex)
                    {
                        Log("[缩略图] 生成失败: " + job.Type + "|" + job.Rel + " :: " + ex.Message);
                    }
                    finally
                    {
                        lock (_thumbEnqLock) _thumbQueued.Remove(job.Type + "|" + job.Rel);
                    }
                    Interlocked.Increment(ref _thumbDone);
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    private static void StartPosterWorker()
    {
        if (Ffmpeg == null)
        {
            Console.WriteLine("        (未检测到 ffmpeg.exe，视频封面将使用标题占位图)");
            lock (_thumbStatLock) { _posterTotal = 0; _posterDone = 1; }
            return;
        }
        var t = new Thread(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(_jsonVideos ?? "{}");
                if (!doc.RootElement.TryGetProperty("videos", out var vids))
                {
                    lock (_thumbStatLock) { _posterTotal = 1; _posterDone = 1; }
                    return;
                }
                int n = vids.GetArrayLength();
                lock (_thumbStatLock) _posterTotal = n;

                var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
                Parallel.ForEach(vids.EnumerateArray(), options, v =>
                {
                    try
                    {
                        if (!v.TryGetProperty("posterKey", out var pk))
                        { lock (_thumbStatLock) { _posterDone++; } return; }
                        string key = pk.GetString() ?? "";
                        if (string.IsNullOrEmpty(key)) { lock (_thumbStatLock) { _posterDone++; } return; }
                        string dst = ThumbPath("video", key);
                        if (File.Exists(dst)) { lock (_thumbStatLock) { _posterDone++; } return; }
                        string src = key.StartsWith("bili:")
                            ? Path.Combine(BiliDir, key.Substring(5).Replace('/', Path.DirectorySeparatorChar))
                            : Path.Combine(DataDir, key.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(src)) { lock (_thumbStatLock) { _posterDone++; } return; }
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            var psi = new ProcessStartInfo
                            {
                                FileName = Ffmpeg,
                                Arguments = "-y -ss 2 -i \"" + src + "\" -frames:v 1 -vf \"scale='min(640,iw)':-2\" -q:v 4 \"" + dst + "\"",
                                CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true
                            };
                            var p = Process.Start(psi);
                            if (p != null && !p.WaitForExit(30000))
                            {
                                try { p.Kill(); } catch { }
                            }
                        }
                        catch { }
                    }
                    catch { }
                    finally { lock (_thumbStatLock) { _posterDone++; } }
                });
                Console.WriteLine("        视频封面生成完成。");
            }
            catch
            {
                lock (_thumbStatLock) { _posterTotal = 1; _posterDone = 1; }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private static void MakeThumb(string src, string dst, int maxDim)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string mime = SniffMime(src);
        if (mime == "image/webp" || mime == "image/avif" || mime == "image/gif")
        {
            if (Ffmpeg != null && FfmpegThumb(src, dst, maxDim)) return;
            try { File.Copy(src, dst, overwrite: true); return; } catch { }
        }
        if (GpuMakeThumb(src, dst, maxDim)) return;
        using var img = Image.FromFile(src);
        double scale = Math.Min(1.0, (double)maxDim / Math.Max(img.Width, img.Height));
        int w = Math.Max(1, (int)(img.Width * scale));
        int h = Math.Max(1, (int)(img.Height * scale));
        using var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.Clear(Color.White);
        g.DrawImage(img, 0, 0, w, h);
        ImageCodecInfo? enc = null;
        foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") enc = c;
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
        bmp.Save(dst, enc!, ep);
        Interlocked.Increment(ref _cpuThumbCount);
    }

    private static bool FfmpegThumb(string src, string dst, int maxDim)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = Ffmpeg!,
                Arguments = "-y -i \"" + src + "\" -vf \"scale='min(" + maxDim + ",iw)':-2\" -frames:v 1 -q:v 4 \"" + dst + "\"",
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true
            });
            if (p == null) return false;
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return false; }
            return File.Exists(dst) && new FileInfo(dst).Length > 0;
        }
        catch { return false; }
    }

    private static int StartServer()
    {
        for (int p = 8234; p < 8260; p++)
        {
            try
            {
                var hl = new HttpListener();
                hl.Prefixes.Add("http://127.0.0.1:" + p + "/");
                hl.Start();
                hl.BeginGetContext(OnRequest, hl);
                Console.WriteLine("        本地服务器: http://127.0.0.1:" + p + "/");
                return p;
            }
            catch { }
        }
        return 0;
    }

    private static void OnRequest(IAsyncResult ar)
    {
        HttpListener? hl = null;
        HttpListenerContext? ctx = null;
        try
        {
            hl = (HttpListener)ar.AsyncState!;
            ctx = hl.EndGetContext(ar);
            var ctxCopy = ctx;
            ThreadPool.QueueUserWorkItem(_ => Handle(ctxCopy));
        }
        catch { }
        try { hl?.BeginGetContext(OnRequest, hl); } catch { }
    }

    private static string Q(NameValueCollection q, string key)
    {
        var v = q[key];
        if (string.IsNullOrEmpty(v)) return v ?? "";
        try { return Encoding.GetEncoding("utf-8").GetString(Encoding.GetEncoding(936).GetBytes(v)); }
        catch { return v; }
    }

    private static string PathParam(NameValueCollection q)
    {
        string? p = q["p"];
        if (!string.IsNullOrEmpty(p))
        {
            try
            {
                int n = p.Length / 2;
                var b = new byte[n];
                for (int i = 0; i < n; i++) b[i] = Convert.ToByte(p.Substring(i * 2, 2), 16);
                return Encoding.UTF8.GetString(b);
            }
            catch { }
        }
        return Q(q, "path");
    }

    private static string? ReadBodyText(HttpListenerRequest req, int maxChars, out bool tooLarge)
    {
        tooLarge = req.ContentLength64 > maxChars;
        if (tooLarge) return null;
        try
        {
            using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
            var sb = new StringBuilder(Math.Min(maxChars, 1 << 16));
            var buf = new char[8192];
            int n;
            while ((n = sr.Read(buf, 0, buf.Length)) > 0)
            {
                if (sb.Length + n > maxChars) { tooLarge = true; return null; }
                sb.Append(buf, 0, n);
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string? ReadBodyOr413(HttpListenerContext ctx, int maxChars)
    {
        var body = ReadBodyText(ctx.Request, maxChars, out bool tooLarge);
        if (body != null) return body;
        ctx.Response.StatusCode = tooLarge ? 413 : 400;
        ServeJson(ctx, "{\"ok\":false,\"error\":\"请求体无效或过大\"}");
        return null;
    }

    private static void Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url!.AbsolutePath;
            var qs = ctx.Request.QueryString!;
            if (IsDangerousLocalPath(path) && IsCrossSiteBrowserRequest(ctx.Request))
            {
                ctx.Response.StatusCode = 403;
                var deny = Encoding.UTF8.GetBytes("403 Forbidden（跨站请求被拒绝）");
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.OutputStream.Write(deny, 0, deny.Length);
                ctx.Response.Close();
                return;
            }
            if (path != "/api/import/upload" && path != "/api/ldte/train" && path != "/api/ldte/clip/save"
                && ctx.Request.ContentLength64 > 8L * 1024 * 1024)
            {
                ctx.Response.StatusCode = 413;
                var big = Encoding.UTF8.GetBytes("413 Payload Too Large");
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.OutputStream.Write(big, 0, big.Length);
                ctx.Response.Close();
                return;
            }
            if (path == "/" || path == "") { ctx.Response.Redirect("/web/index.html"); ctx.Response.Close(); return; }
            if (path == "/admin.html") { ctx.Response.Redirect("/web/admin.html"); ctx.Response.Close(); return; }
            if (path.StartsWith("/web/"))
            {
                ServeStatic(ctx, Path.Combine(WebDir, path.Substring(5).Replace('/', Path.DirectorySeparatorChar)));
                return;
            }

            if (path.StartsWith("/spines/"))    { ServeLobbyAsset(ctx, _spineDir, path.Substring(8));  return; }
            if (path.StartsWith("/lobby-img/")) { ServeLobbyAsset(ctx, _lobbyDir, path.Substring(11)); return; }

            if ((path == "/api/reindex" || path == "/api/admin/cache-clear" || path == "/api/admin/external"
                 || path == "/api/device/revoke" || path == "/api/online/profile-sync") && ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = 405;
                var m405 = Encoding.UTF8.GetBytes("405 Method Not Allowed");
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.OutputStream.Write(m405, 0, m405.Length);
                ctx.Response.Close();
                return;
            }
            switch (path)
            {
                case "/api/students":   ServeJson(ctx, _jsonStudents ?? "{}"); break;
                case "/api/gallery":    ServeJson(ctx, _jsonGallery ?? "{}"); break;
                case "/api/music":      ServeJson(ctx, _jsonMusic ?? "{}"); break;
                case "/api/videos":     ServeJson(ctx, _jsonVideos ?? "{}"); break;
                case "/api/steam":      ServeJson(ctx, _jsonSteam ?? "{}"); break;
                case "/api/student":    ServeStudentDetail(ctx, Q(qs, "id")); break;
                case "/api/student/profile": ServeStudentProfile(ctx, Q(qs, "id")); break;
                case "/api/equipment":  ServeEquipmentDetail(ctx, Q(qs, "id")); break;
                case "/api/items":      ServeItemsQuery(ctx, qs); break;
                case "/api/ldte/train": ServeLdteTrain(ctx, ctx.Request); break;
                case "/api/ldte/get":   ServeLdteGet(ctx, Q(qs, "key")); break;
                case "/api/ldte/list":  ServeLdteList(ctx); break;
                case "/api/ldte/system": ServeLdteSystem(ctx); break;
                case "/api/ldte/clip/save":   ServeLdteClipSave(ctx, ctx.Request); break;
                case "/api/ldte/clip/list":   ServeLdteClipList(ctx, Q(qs, "key")); break;
                case "/api/ldte/clip/get":    ServeLdteClipGet(ctx, Q(qs, "id")); break;
                case "/api/ldte/clip/delete": ServeLdteClipDelete(ctx, ctx.Request); break;
                case "/api/lobby":      ServeLobby(ctx, Q(qs, "id")); break;
                case "/api/lobby-engine": ServeJson(ctx, "{\"engine\":\"web\"}"); break;
                case "/api/audio/learn":  ServeAudioLearn(ctx, ctx.Request); break;
                case "/api/import/upload":  ServeImportUpload(ctx, ctx.Request); break;
                case "/api/import/process": ServeImportProcess(ctx, ctx.Request); break;
                case "/api/import/status":  ServeImportStatus(ctx, ctx.Request); break;
                case "/api/import/list":    ServeImportList(ctx); break;
                case "/api/import/delete":  ServeImportDelete(ctx, ctx.Request); break;
                case "/import-media":       ServeImportMedia(ctx, PathParam(qs)); break;
                case "/api/online/check":   ServeOnlineCheck(ctx); break;
                case "/api/online/download": ServeOnlineDownload(ctx); break;
                case "/api/online/status":   ServeOnlineStatus(ctx); break;
                case "/api/online/kivo-characters": ServeKivoCharacters(ctx); break;
                case "/api/online/kivo-fetch":       ServeKivoFetch(ctx, Q(qs, "id")); break;
                case "/api/online/kivo-cleanup":     ServeKivoCleanup(ctx, ctx.Request); break;
                case "/api/online/auto-update":      ServeAutoUpdate(ctx); break;
                case "/api/online/audit":            ServeOnlineAudit(ctx); break;
                case "/api/online/repair":           ServeOnlineRepair(ctx); break;
                case "/api/online/repair-status":    ServeOnlineRepairStatus(ctx); break;
                case "/api/online/profile-sync":     ServeProfileSync(ctx, ctx.Request); break;
                case "/api/online/profile-status":   ServeProfileStatus(ctx); break;
                case "/api/ai/state":                ServeAiState(ctx); break;
                case "/api/ai/chat":                 ServeAiChat(ctx); break;
                case "/api/ai/reset":                ServeAiReset(ctx); break;
                case "/api/ai/tools":                ServeAiTools(ctx); break;
                case "/api/ai/tool":                 ServeAiToolInvoke(ctx); break;
                case "/api/ai/sessions":             ServeAiSessions(ctx); break;
                case "/api/ai/history":              ServeAiHistory(ctx); break;
                case "/api/playlist":   ServePlaylist(ctx, ctx.Request); break;
                case "/api/favorites":  ServeFavorites(ctx, ctx.Request); break;
                case "/api/media-keys":  ServeMediaKeys(ctx, ctx.Request); break;
                case "/api/lrc":        ServeLrc(ctx, PathParam(qs)); break;
                case "/api/reindex":
                    new Thread(() =>
                    {
                        try
                        {
                            Log("手动重建索引开始");
                            BuildIndexes(useCache: false);
                            Log("手动重建索引完成");
                        }
                        catch (Exception ex) { Log("[RI] 索引重建失败: " + ex); }
                    })
                    { IsBackground = true }.Start();
                    ServeJson(ctx, "{\"ok\":true,\"msg\":\"正在后台重建索引，请稍后刷新\"}");
                    break;
                case "/media":  ServeMedia(ctx, PathParam(qs)); break;
                case "/thumb":  ServeThumb(ctx, qs["type"], PathParam(qs)); break;
                case "/api/local/open-folder": ServeOpenFolder(ctx, PathParam(qs)); break;
                case "/api/local/copy":        ServeCopyFile(ctx, PathParam(qs)); break;
                case "/api/settings":  ServeSettingsApi(ctx, ctx.Request); break;
                case "/api/me":     ServeMe(ctx, ctx.Request); break;
                case "/api/me/ui":  ServeMeUi(ctx, ctx.Request); break;
                case "/api/admin/login":     ServeAdminLogin(ctx, ctx.Request); break;
                case "/api/admin/logout":    ServeAdminLogout(ctx, ctx.Request); break;
                case "/api/admin/status":      ServeAdminStatus(ctx, ctx.Request); break;
                case "/api/admin/external":    ServeAdminExternal(ctx, ctx.Request); break;
                case "/api/admin/accounts":    ServeAdminAccounts(ctx, ctx.Request); break;
                case "/api/admin/stats":       ServeAdminStats(ctx, ctx.Request); break;
                case "/api/admin/cache-clear": ServeAdminCacheClear(ctx, ctx.Request); break;
                case "/api/admin/settings":    ServeAdminSettings(ctx, ctx.Request); break;
                case "/api/admin/gpu":         ServeAdminGpu(ctx, ctx.Request); break;
                case "/api/admin/parse":       ServeAdminParse(ctx, ctx.Request); break;
                case "/api/admin/plugins":     ServeAdminPlugins(ctx, ctx.Request); break;
                case "/api/admin/ldte":        ServeAdminLdte(ctx, ctx.Request); break;
                case "/api/admin/ldte/tempkey": ServeAdminLdteTempKey(ctx, ctx.Request); break;
                case "/api/admin/ldte/cloudkey": ServeAdminLdteCloudKey(ctx, ctx.Request); break;
                case "/api/device/auth":   ServeDeviceAuth(ctx, ctx.Request); break;
                case "/api/device/list":   ServeDeviceList(ctx, ctx.Request); break;
                case "/api/device/revoke": ServeDeviceRevoke(ctx, ctx.Request); break;
                default:
                    ctx.Response.StatusCode = 404;
                    var bytes = Encoding.UTF8.GetBytes("404 Not Found");
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("[请求异常] " + ex.GetType().Name + ": " + ex.Message + " | URL=" + (ctx.Request?.Url?.OriginalString ?? "?"));
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static void ServeJson(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.AddHeader("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
        ctx.Response.AddHeader("Pragma", "no-cache");
        ctx.Response.AddHeader("Expires", "0");
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static string? ExtractBv(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, @"bilibili\.com/video/(BV[0-9A-Za-z]{10})");
        if (!m.Success) m = Regex.Match(text, @"(BV[0-9A-Za-z]{10})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static void ServeOnlineCheck(HttpListenerContext ctx)
    {
        try
        {
            var list = new List<object>();
            var root = Path.Combine(DataDir, "03_游戏音乐");
            if (Directory.Exists(root))
            {
                foreach (var d in Directory.GetDirectories(root))
                {
                    if (Directory.GetFiles(d, "*.mp4", SearchOption.TopDirectoryOnly).Length > 0) continue;
                    var infoFile = Path.Combine(d, "_info.json");
                    if (!File.Exists(infoFile)) continue;
                    string content;
                    try { content = File.ReadAllText(infoFile, Encoding.UTF8); } catch { continue; }
                    string use = "", title = "";
                    var bv = ExtractBv(content);
                    try
                    {
                        var info = JsonSerializer.Deserialize<Dictionary<string, object>>(content);
                        if (info != null)
                        {
                            use = Str(info, "use");
                            title = Str(info, "title");
                        }
                    } catch { }
                    bool isTheme = Regex.IsMatch(use ?? "", "主题曲|主题歌|ED|OP|MV");
                    int score = 0;
                    if (Regex.IsMatch(content, "提取自|音源|音频.*视频")) score += 10;
                    if (isTheme) score += 5;
                    if (Regex.IsMatch(Path.GetFileName(d), "MV|Short|ver")) score += 2;
                    if (Regex.IsMatch(use ?? "", "战斗|界面|制造|编队|大厅")) score -= 5;
                    var item = new Dictionary<string, object>
                    {
                        ["dir"] = Path.GetFileName(d), ["use"] = use, ["title"] = title,
                        ["hasBv"] = !string.IsNullOrEmpty(bv), ["isTheme"] = isTheme, ["score"] = score
                    };
                    if (!string.IsNullOrEmpty(bv)) item["bv"] = bv;
                    list.Add(item);
                }
            }
            ServeJson(ctx, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["items"] = list, ["total"] = list.Count,
                ["withBv"] = list.Count(o => (bool)((Dictionary<string, object>)o)["hasBv"]),
                ["noBv"] = list.Count(o => !(bool)((Dictionary<string, object>)o)["hasBv"]),
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            }, JsonOpts));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeOnlineStatus(HttpListenerContext ctx)
    {
        lock (_dlLock)
        {
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                status = _dlStatus, total = _dlTotal, done = _dlDone, fail = _dlFail, current = _dlCurrent
            }));
        }
    }

    private static string? KivoGet(string url)
    {
        try
        {
            var resp = _kivoHttp.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) { Log("[kivo] GET " + url + " 失败: " + ex.Message); return null; }
    }

    private static void ServeKivoCharacters(HttpListenerContext ctx)
    {
        try
        {

            var remote = new List<Dictionary<string, object>>();
            int page = 1, maxPage = 1;
            while (page <= maxPage)
            {
                string? json = KivoGet(KIVO_API + "/data/students?page=" + page);
                if (json == null) break;
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                maxPage = data.GetProperty("max_page").GetInt32();
                foreach (var s in data.GetProperty("students").EnumerateArray())
                {
                    int id = s.GetProperty("id").GetInt32();
                    string cn = (s.TryGetProperty("family_name_cn", out var fn) ? fn.GetString() : "")
                              + (s.TryGetProperty("given_name_cn", out var gn) ? gn.GetString() : "");
                    string jp = (s.TryGetProperty("family_name_jp", out var fj) ? fj.GetString() : "")
                              + (s.TryGetProperty("given_name_jp", out var gj) ? gj.GetString() : "");
                    remote.Add(new Dictionary<string, object> { ["id"] = id, ["cn"] = cn, ["jp"] = jp });
                }
                page++;
            }

            var localIds = new HashSet<int>();
            if (_studentList != null)
                foreach (var s in _studentList)
                    if (int.TryParse((string)s["id"], out var lid)) localIds.Add(lid);

            var items = new List<object>();
            foreach (var r in remote)
            {
                int rid = (int)r["id"];
                items.Add(new Dictionary<string, object>
                {
                    ["id"] = rid, ["cn"] = r["cn"], ["jp"] = r["jp"], ["local"] = localIds.Contains(rid)
                });
            }

            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                items, total = items.Count,
                localCount = localIds.Count,
                newCount = items.Count(i => !((Dictionary<string, object>)i)["local"].Equals(true))
            }));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeKivoFetch(HttpListenerContext ctx, string? idStr)
    {
        if (string.IsNullOrEmpty(idStr) || !int.TryParse(idStr, out int id))
        { ServeJson(ctx, "{\"error\":\"无效的角色 ID\"}"); return; }
        try
        {
            var r = FetchStudent(id);
            if (r.ContainsKey("error")) { ServeJson(ctx, JsonSerializer.Serialize(r)); return; }
            ServeJson(ctx, JsonSerializer.Serialize(r));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static Dictionary<string, object> FetchStudent(int id)
    {
        string? json = KivoGet(KIVO_API + "/data/students/" + id);
        if (json == null) return new Dictionary<string, object> { ["error"] = "从 kivo.wiki 获取数据失败" };
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return new Dictionary<string, object> { ["error"] = "返回数据格式异常" };

        string familyCn = data.TryGetProperty("family_name_cn", out var fn) ? fn.GetString() ?? "" : "";
        string givenCn = data.TryGetProperty("given_name_cn", out var gn) ? gn.GetString() ?? "" : "";
        string cn = familyCn + givenCn;
        string jp = (data.TryGetProperty("family_name_jp", out var fj) ? fj.GetString() ?? "" : "")
                  + (data.TryGetProperty("given_name_jp", out var gj) ? gj.GetString() ?? "" : "");
        string dirName = id + "_" + familyCn + "_" + givenCn;
        if (string.IsNullOrEmpty(familyCn) && string.IsNullOrEmpty(givenCn)) dirName = "student_" + id;

        string stuRoot = Path.Combine(DataDir, "01_学生", dirName);
        Directory.CreateDirectory(Path.Combine(stuRoot, "立绘"));
        Directory.CreateDirectory(Path.Combine(stuRoot, "画廊"));
        Directory.CreateDirectory(Path.Combine(stuRoot, "语音", "日服"));

        int illusCount = 0, galleryCount = 0, voiceCount = 0;

        string StrProp(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            _ => ""
        };

        var info = new Dictionary<string, object>
        {
            ["id"] = id, ["cn"] = cn, ["jp"] = jp,
            ["school"] = data.TryGetProperty("school", out var sch) ? StrProp(sch) : "",
            ["grade"] = data.TryGetProperty("grade", out var gr) ? StrProp(gr) : "",
            ["birthday"] = data.TryGetProperty("birthday", out var bd) ? StrProp(bd) : "",
            ["height"] = data.TryGetProperty("height", out var h) ? StrProp(h) : "",
            ["illustrator"] = data.TryGetProperty("illustrator", out var il) ? StrProp(il) : "",
            ["voice"] = data.TryGetProperty("character_voice", out var cv) ? StrProp(cv) : "",
        };
        File.WriteAllText(Path.Combine(stuRoot, "_info.json"), JsonSerializer.Serialize(info, JsonOpts), Encoding.UTF8);

        if (data.TryGetProperty("avatar", out var av) && av.GetString() is string avUrl && !string.IsNullOrEmpty(avUrl))
        {
            string fullUrl = avUrl.StartsWith("//") ? "https:" + avUrl : avUrl;
            string dst = Path.Combine(stuRoot, "立绘", "头像.png");
            if (DownloadFile(fullUrl, dst)) illusCount++;
        }

        if (data.TryGetProperty("skin_list", out var skins) && skins.ValueKind == JsonValueKind.Array)
        {
            foreach (var sk in skins.EnumerateArray())
            {
                if (sk.TryGetProperty("avatar", out var sav) && sav.GetString() is string savUrl && !string.IsNullOrEmpty(savUrl))
                {
                    string fullUrl = savUrl.StartsWith("//") ? "https:" + savUrl : savUrl;
                    string skinName = sk.TryGetProperty("skin_cn", out var scn) ? (scn.GetString() ?? "") : "";
                    string fname = string.IsNullOrEmpty(skinName) ? "初始立绘.png" : skinName + ".png";
                    string dst = Path.Combine(stuRoot, "立绘", fname);
                    if (DownloadFile(fullUrl, dst)) illusCount++;
                }
            }
        }

        if (data.TryGetProperty("sd_model_image", out var sd) && sd.GetString() is string sdUrl && !string.IsNullOrEmpty(sdUrl))
        {
            string fullUrl = sdUrl.StartsWith("//") ? "https:" + sdUrl : sdUrl;
            string dst = Path.Combine(stuRoot, "立绘", "SD模型.png");
            if (DownloadFile(fullUrl, dst)) illusCount++;
        }

        if (data.TryGetProperty("gallery", out var gal) && gal.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in gal.EnumerateArray())
            {
                var urls = new List<string>();
                if (g.ValueKind == JsonValueKind.String)
                {
                    string? gu = g.GetString();
                    if (!string.IsNullOrEmpty(gu)) urls.Add(gu);
                }
                else if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in imgs.EnumerateArray())
                        if (img.ValueKind == JsonValueKind.String && img.GetString() is string iu && !string.IsNullOrEmpty(iu))
                            urls.Add(iu);
                }
                foreach (var gurl in urls)
                {
                    string fullUrl = gurl.StartsWith("//") ? "https:" + gurl : gurl;
                    string fname;
                    try { fname = Path.GetFileName(new Uri(fullUrl).AbsolutePath); }
                    catch { fname = "gallery_" + galleryCount + ".png"; }
                    if (string.IsNullOrEmpty(fname)) fname = "gallery_" + galleryCount + ".png";
                    string dst = Path.Combine(stuRoot, "画廊", fname);
                    if (DownloadFile(fullUrl, dst)) galleryCount++;
                }
            }
        }

        voiceCount += DownloadVoices(data, "voice", "日服", stuRoot);
        voiceCount += DownloadVoices(data, "voice_cn", "国服", stuRoot);

        int lobbyAdded = 0, spineAdded = 0;
        try
        {
            var lobbyLog = new StringWriter();
            SyncLobbyForStudent(data, id, cn, lobbyLog, out lobbyAdded, out spineAdded);
        }
        catch (Exception ex) { Log("[大厅] " + id + " 同步失败: " + ex.Message); }

        try
        {
            bool hasLobby = data.TryGetProperty("recollection_lobby_image", out var li2)
                            && li2.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(li2.GetString());
            int apiVoice = (data.TryGetProperty("voice", out var av1) && av1.ValueKind == JsonValueKind.Array ? av1.GetArrayLength() : 0)
                         + (data.TryGetProperty("voice_cn", out var av2) && av2.ValueKind == JsonValueKind.Array ? av2.GetArrayLength() : 0);
            int apiIllus = (data.TryGetProperty("avatar", out var avt) && avt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(avt.GetString()) ? 1 : 0)
                         + (data.TryGetProperty("skin_list", out var skl) && skl.ValueKind == JsonValueKind.Array ? skl.GetArrayLength() : 0)
                         + (data.TryGetProperty("sd_model_image", out var sdl) && sdl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sdl.GetString()) ? 1 : 0);
            var modelIds = new List<int>();
            if (data.TryGetProperty("model", out var md) && md.ValueKind == JsonValueKind.Array)
                foreach (var x in md.EnumerateArray())
                {
                    int mid = x.ValueKind == JsonValueKind.Number ? x.GetInt32() : (int.TryParse(x.GetString(), out int mt) ? mt : 0);
                    if (mid > 0) modelIds.Add(mid);
                }
            info["_kivo"] = new Dictionary<string, object>
            {
                ["fetchedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["voice"] = apiVoice,
                ["gallery"] = CountGalleryUpstream(data),
                ["illus"] = apiIllus,
                ["lobby"] = hasLobby ? 1 : 0,
                ["spineHome"] = spineAdded,
                ["model"] = string.Join(",", modelIds)
            };
            File.WriteAllText(Path.Combine(stuRoot, "_info.json"), JsonSerializer.Serialize(info, JsonOpts), Encoding.UTF8);
        }
        catch (Exception ex) { Log("[抓取] " + id + " 写基线失败: " + ex.Message); }

        return new Dictionary<string, object>
        {
            ["ok"] = true, ["id"] = id, ["cn"] = cn, ["jp"] = jp,
            ["illus"] = illusCount, ["gallery"] = galleryCount, ["voices"] = voiceCount,
            ["lobby"] = lobbyAdded, ["spine"] = spineAdded,
            ["dir"] = dirName
        };
    }

    private static int CountGalleryUpstream(JsonElement data)
    {
        int n = 0;
        if (data.TryGetProperty("gallery", out var g) && g.ValueKind == JsonValueKind.Array)
            foreach (var x in g.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String) n++;
                else if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array)
                    n += im.GetArrayLength();
            }
        return n;
    }

    private static int DownloadVoices(JsonElement data, string field, string server, string stuRoot)
    {
        int added = 0;
        if (!data.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;
        foreach (var v in arr.EnumerateArray())
        {
            try
            {
                if (!v.TryGetProperty("file", out var fv) || fv.ValueKind != JsonValueKind.String) continue;
                string url = fv.GetString() ?? "";
                if (string.IsNullOrEmpty(url)) continue;
                url = url.StartsWith("//") ? "https:" + url : url;
                string desc = v.TryGetProperty("description", out var dv) ? dv.GetString() ?? "" : "";
                string cat = v.TryGetProperty("category", out var cv2) ? cv2.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(desc))
                    desc = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
                foreach (var bad in Path.GetInvalidFileNameChars()) desc = desc.Replace(bad, '_');
                if (string.IsNullOrEmpty(cat)) cat = "other";
                foreach (var bad in Path.GetInvalidFileNameChars()) cat = cat.Replace(bad, '_');
                string ext = Path.GetExtension(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".ogg";
                string dst = Path.Combine(stuRoot, "语音", server, cat, desc + ext);
                if (DownloadFile(url, dst)) added++;
            }
            catch { }
        }
        return added;
    }

    private static bool DownloadFile(string url, string dst)
    {
        try
        {
            if (File.Exists(dst)) return false;
            var bytes = _kivoHttp.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (bytes == null || bytes.Length == 0) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, bytes);
            return true;
        }
        catch (Exception ex) { Log("[下载] " + url + " 失败: " + ex.Message); return false; }
    }

    private static void ServeOnlineDownload(HttpListenerContext ctx)
    {
        try
        {
            if (_dlStatus == "running") { ServeJson(ctx, "{\"error\":\"已有下载任务在运行\"}"); return; }
            string? body = ReadBodyOr413(ctx, 1024 * 1024);
            if (body == null) return;
            var req = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
            if (!(req!["dirs"] is JsonElement je && je.ValueKind == JsonValueKind.Array))
            { ServeJson(ctx, "{\"error\":\"未选择任何曲目\"}"); return; }
            var dirs = new List<string>();
            foreach (var x in je.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) dirs.Add(x.GetString()!);
            if (dirs.Count == 0) { ServeJson(ctx, "{\"error\":\"未选择任何曲目\"}"); return; }

            string bb = Path.Combine(AppDir, "BBDown.exe");
            if (!File.Exists(bb)) { ServeJson(ctx, "{\"error\":\"未找到 BBDown.exe\"}"); return; }
            string musicRoot = Path.Combine(DataDir, "03_游戏音乐");
            string musicRootFull = Path.GetFullPath(musicRoot).TrimEnd('\\') + Path.DirectorySeparatorChar;
            var jobs = new List<KeyValuePair<string, string>>();
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string dirFull;
                try { dirFull = Path.GetFullPath(Path.Combine(musicRoot, dir)); }
                catch { continue; }
                if (!dirFull.StartsWith(musicRootFull, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(dirFull) || Directory.GetFiles(dirFull, "*.mp4", SearchOption.TopDirectoryOnly).Length > 0) continue;
                var infoFile = Path.Combine(dirFull, "_info.json");
                if (File.Exists(infoFile))
                {
                    var bv = ExtractBv(File.ReadAllText(infoFile, Encoding.UTF8));
                    if (!string.IsNullOrEmpty(bv)) jobs.Add(new KeyValuePair<string, string>(dir, bv));
                }
            }
            if (jobs.Count == 0) { ServeJson(ctx, "{\"error\":\"所选曲目均无可下载的 BV 号或已有视频\"}"); return; }

            lock (_dlLock) { _dlStatus = "running"; _dlTotal = jobs.Count; _dlDone = 0; _dlFail = 0; _dlCurrent = ""; }
            var worker = new Thread(() =>
            {
                foreach (var job in jobs)
                {
                    lock (_dlLock) _dlCurrent = job.Key;
                    try
                    {
                        var dirFull = Path.Combine(musicRoot, job.Key);
                        var p = Process.Start(new ProcessStartInfo
                        {
                            FileName = bb,
                            Arguments = "\"" + job.Value + "\" --work-dir \"" + dirFull + "\" --file-pattern \"MV视频\"",
                            CreateNoWindow = true, UseShellExecute = false,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        });
                        if (p != null && !p.WaitForExit(180000)) { try { p.Kill(); } catch { } }
                        if (Directory.GetFiles(dirFull, "*.mp4", SearchOption.TopDirectoryOnly).Length > 0)
                            lock (_dlLock) { _dlDone++; }
                        else
                            lock (_dlLock) { _dlFail++; }
                    }
                    catch { lock (_dlLock) { _dlFail++; } }
                }
                lock (_dlLock) { _dlStatus = "done"; _dlCurrent = ""; }
            });
            worker.IsBackground = true;
            worker.Start();
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, total = jobs.Count }));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static string? ResolveMedia(string rel)
    {
        try
        {
            if (string.IsNullOrEmpty(rel)) return null;
            if (rel.StartsWith("bili:"))
            {
                string p = rel.Substring(5).Replace('/', Path.DirectorySeparatorChar);
                string full = Path.GetFullPath(Path.Combine(BiliDir, p));
                string root = Path.GetFullPath(BiliDir).TrimEnd('\\') + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            string full2 = Path.GetFullPath(Path.Combine(DataDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            string root2 = Path.GetFullPath(DataDir).TrimEnd('\\') + Path.DirectorySeparatorChar;
            if (!full2.StartsWith(root2, StringComparison.OrdinalIgnoreCase)) return null;

            string metaRoot = Path.Combine(Path.GetFullPath(DataDir), "_本地维基").TrimEnd('\\') + Path.DirectorySeparatorChar;
            if (full2.StartsWith(metaRoot, StringComparison.OrdinalIgnoreCase)) return null;
            return full2;
        }
        catch { return null; }
    }

    private class MemCacheEntry { public byte[] Data; public long Mtime; }
    private static readonly ConcurrentDictionary<string, MemCacheEntry> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private const long CACHE_MAX_SIZE = 4 * 1024 * 1024;
    private const long FILE_CACHE_BUDGET = 256L * 1024 * 1024;
    private static long _fileCacheBytes;

    private static byte[]? ReadFileCached(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (fi.Length > CACHE_MAX_SIZE) return File.ReadAllBytes(path);
            long mtime = fi.LastWriteTimeUtc.Ticks;
            string key = Path.GetFullPath(path);
            if (_fileCache.TryGetValue(key, out var entry) && entry.Mtime == mtime)
                return entry.Data;
            byte[] data = File.ReadAllBytes(path);
            _fileCache[key] = new MemCacheEntry { Data = data, Mtime = mtime };
            if (Interlocked.Add(ref _fileCacheBytes, data.Length) > FILE_CACHE_BUDGET)
            {
                _fileCache.Clear();
                Interlocked.Exchange(ref _fileCacheBytes, 0);
            }
            return data;
        }
        catch { return null; }
    }

    private static void ServeMedia(HttpListenerContext ctx, string rel)
    {
        var f = ResolveMedia(rel);
        if (f == null || !File.Exists(f)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        ctx.Response.ContentType = SniffMime(f);
        ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");

        var data = ReadFileCached(f);
        if (data != null && data.Length <= CACHE_MAX_SIZE && ctx.Request.HttpMethod == "GET")
        {
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }
        else
        {
            WriteFileWithRange(ctx, f);
        }
    }

    private static string SniffMime(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var b = new byte[12];
            int n = fs.Read(b, 0, 12);
            if (n >= 12)
            {
                if (b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
                if (b[0] == 137 && b[1] == 80) return "image/png";
                if (b[0] == 71 && b[1] == 73) return "image/gif";
                if (b[0] == 82 && b[1] == 73 && b[2] == 70 && b[3] == 70) return "image/webp";

                if (b[4] == 102 && b[5] == 116 && b[6] == 121 && b[7] == 112)
                {
                    string brand = Encoding.ASCII.GetString(b, 8, 4);
                    if (brand == "avif" || brand == "avis" || brand == "heic" || brand == "heix" ||
                        brand == "mif1" || brand == "msf1")
                        return "image/avif";
                    return Mime(Path.GetExtension(file));
                }
            }
        }
        catch { }
        return Mime(Path.GetExtension(file));
    }

    private static string Mime(string ext) => ext.ToLower() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        ".wasm" => "application/wasm",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".mp4" => "video/mp4",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".lrc" or ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static void ServeStatic(HttpListenerContext ctx, string file)
    {
        var full = Path.GetFullPath(file);

        string webRoot = Path.GetFullPath(WebDir).TrimEnd('\\') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }
        ctx.Response.ContentType = Mime(Path.GetExtension(full));
        string ext = Path.GetExtension(full).ToLower();
        if (ext == ".html" || ext == ".js" || ext == ".css")
            ctx.Response.AddHeader("Cache-Control", "no-cache, must-revalidate");
        else
            ctx.Response.AddHeader("Cache-Control", "public, max-age=3600");
        WriteFileWithRange(ctx, full);
    }

    private static void ServeThumb(HttpListenerContext ctx, string? type, string key)
    {

        if (type != "students" && type != "gallery" && type != "music" && type != "video" && type != "fast")
        {
            ctx.Response.StatusCode = 404; ctx.Response.Close(); return;
        }
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(key))
        {
            ctx.Response.StatusCode = 404; ctx.Response.Close(); return;
        }
        string thumb = ThumbPath(type!, key);
        if (File.Exists(thumb))
        {
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.AddHeader("Cache-Control", "public, max-age=604800");

            var td = ReadFileCached(thumb);
            if (td != null)
            {
                ctx.Response.ContentLength64 = td.Length;
                ctx.Response.OutputStream.Write(td, 0, td.Length);
                ctx.Response.Close();
            }
            else WriteFileWithRange(ctx, thumb);
            return;
        }
        if (type == "video")
        {
            var src = ResolveMedia(key);
            if (src != null && File.Exists(src) && Array.IndexOf(ImgExt, Path.GetExtension(src).ToLower()) >= 0)
            {
                ctx.Response.ContentType = Mime(Path.GetExtension(src));
                ctx.Response.AddHeader("Cache-Control", "public, max-age=604800");
                WriteFileWithRange(ctx, src);
                return;
            }
        }
        if (type == "gallery" || type == "music" || type == "students" || type == "fast")
        {
            string rel = key;
            if (key.EndsWith("/avatar")) rel = "01_学生/" + key.Replace("/avatar", "/立绘/头像.png");
            else if (key.EndsWith("/cover")) rel = key.Replace("/cover", "/封面.png");
            var src = ResolveMedia(rel);
            if (src != null && File.Exists(src))
            {
                if (SniffMime(src) == "image/avif")
                {
                    ctx.Response.ContentType = "image/avif";
                    ctx.Response.AddHeader("Cache-Control", "public, max-age=604800");
                    WriteFileWithRange(ctx, src);
                    return;
                }

                int maxDim = type == "fast" ? 128 : (type == "gallery" ? 500 : (type == "music" ? 320 : 256));
                EnqueueThumb(src, key, type, maxDim);
                ctx.Response.ContentType = Mime(Path.GetExtension(src));
                ctx.Response.AddHeader("Cache-Control", "public, max-age=3600");
                WriteFileWithRange(ctx, src);
                return;
            }
        }
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    private static void WriteFileWithRange(HttpListenerContext ctx, string file)
    {
        var fi = new FileInfo(file);
        long total = fi.Length;
        long start = 0, end = total - 1;
        string? range = ctx.Request.Headers["Range"];
        bool partial = false;
        if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes="))
        {
            var parts = range.Substring(6).Split('-');
            long.TryParse(parts[0], out start);
            if (parts.Length > 1 && parts[1].Length > 0) long.TryParse(parts[1], out end);
            if (start > end || start >= total) { ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }
            if (end >= total) end = total - 1;
            partial = true;
        }
        long len = end - start + 1;
        ctx.Response.ContentLength64 = len;
        ctx.Response.AddHeader("Accept-Ranges", "bytes");
        if (partial)
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + total);
        }
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[65536];
            long remain = len;
            while (remain > 0)
            {
                int read = fs.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                if (read <= 0) break;
                ctx.Response.OutputStream.Write(buf, 0, read);
                remain -= read;
            }
        }
        catch { }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private static void ServeStudentDetail(HttpListenerContext ctx, string id)
    {
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        if (_studentList == null || _studentDirMap == null || !_studentDirMap.TryGetValue(id, out var stu))
        {
            ctx.Response.StatusCode = 404; ctx.Response.Close(); return;
        }
        var dirMap = (Dictionary<string, object>)stu["_dirMap"]!;
        string dirRel = (string)dirMap[id]!;
        string root = Path.Combine(DataDir, "01_学生", dirRel);

        var illus = new List<object>();
        var lp = Path.Combine(root, "立绘");
        if (Directory.Exists(lp))
            foreach (var f in Directory.GetFiles(lp))
                if (Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0 && IsValidImage(f))
                    illus.Add("立绘/" + Path.GetFileName(f));

        var skins = new List<object>();
        var sp = Path.Combine(root, "皮肤");
        if (Directory.Exists(sp))
        {
            var def = new List<object>();
            foreach (var f in Directory.GetFiles(sp))
                if (Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0 && IsValidImage(f))
                    def.Add("皮肤/" + Path.GetFileName(f));
            if (def.Count > 0) skins.Add(new Dictionary<string, object> { ["name"] = "默认", ["files"] = def });
            foreach (var sub in Directory.GetDirectories(sp))
            {
                var files = new List<object>();
                foreach (var f in Directory.GetFiles(sub, "*.*", SearchOption.AllDirectories))
                    if (Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0 && IsValidImage(f))
                        files.Add("皮肤/" + Path.GetFileName(sub) + "/" + f.Substring(sub.Length).TrimStart('\\').Replace('\\', '/'));
                if (files.Count > 0) skins.Add(new Dictionary<string, object> { ["name"] = Path.GetFileName(sub), ["files"] = files });
            }
        }

        var gallery = new List<object>();
        var gp = Path.Combine(root, "画廊");
        if (Directory.Exists(gp))
        {
            var def = new List<object>();
            foreach (var f in Directory.GetFiles(gp))
                if (Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0 && IsValidImage(f))
                    def.Add("画廊/" + Path.GetFileName(f));
            if (def.Count > 0) gallery.Add(new Dictionary<string, object> { ["name"] = "全部", ["files"] = def });
            foreach (var sub in Directory.GetDirectories(gp))
            {
                var files = new List<object>();
                foreach (var f in Directory.GetFiles(sub, "*.*", SearchOption.AllDirectories))
                    if (Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0 && IsValidImage(f))
                        files.Add("画廊/" + Path.GetFileName(sub) + "/" + f.Substring(sub.Length).TrimStart('\\').Replace('\\', '/'));
                if (files.Count > 0) gallery.Add(new Dictionary<string, object> { ["name"] = Path.GetFileName(sub), ["files"] = files });
            }
        }

        var voices = new Dictionary<string, object>();
        var vp = Path.Combine(root, "语音");
        if (Directory.Exists(vp))
        {
            foreach (var sub in Directory.GetDirectories(vp))
            {
                var files = new List<object>();
                foreach (var f in Directory.GetFiles(sub, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f).ToLower();
                    if (Array.IndexOf(AudioExt, ext) >= 0)
                        files.Add(new Dictionary<string, object>
                        {
                            ["name"] = Path.GetFileNameWithoutExtension(f),
                            ["file"] = "语音/" + Path.GetFileName(sub) + "/" + f.Substring(sub.Length).TrimStart('\\').Replace('\\', '/')
                        });
                }
                if (files.Count > 0) voices[Path.GetFileName(sub)] = files;
            }
        }

        var skills = new List<object>();
        var skp = Path.Combine(root, "技能");
        if (Directory.Exists(skp))
            foreach (var f in Directory.GetFiles(skp, "*.*", SearchOption.AllDirectories))
                if (Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0)
                    skills.Add("技能/" + f.Substring(skp.Length).TrimStart('\\').Replace('\\', '/'));

        string curSkin = "";
        foreach (Dictionary<string, object> v in (List<object>)stu["variants"])
            if ((string)v["id"] == id) curSkin = (string)v["skin"];

        var result = new Dictionary<string, object>(stu);
        result.Remove("_dirMap");
        result["dir"] = dirRel;
        result["curVariant"] = id;
        result["curSkin"] = curSkin;
        result["base"] = "01_学生/" + dirRel + "/";
        result["illus"] = illus;
        result["skins"] = skins;
        result["gallery"] = gallery;
        result["voices"] = voices;
        result["skills"] = skills;
        var prof = GetProfile(id);
        if (prof != null) { EnrichProfile(prof); result["profile"] = prof; }
        ServeJson(ctx, JsonSerializer.Serialize(result, JsonOpts));
    }

    private static void ServeOpenFolder(HttpListenerContext ctx, string rel)
    {
        try
        {
            var f = ResolveMedia(rel);
            if (string.IsNullOrEmpty(f) || !File.Exists(f))
            { ServeJson(ctx, "{\"error\":\"文件不存在\"}"); return; }
            var psi = new ProcessStartInfo("explorer.exe", "/select,\"" + f + "\"") { UseShellExecute = true };
            Process.Start(psi);
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeCopyFile(HttpListenerContext ctx, string rel)
    {
        try
        {
            var f = ResolveMedia(rel);
            if (string.IsNullOrEmpty(f) || !File.Exists(f))
            { ServeJson(ctx, "{\"error\":\"文件不存在\"}"); return; }
            var files = new StringCollection { f };
            Exception? inner = null;
            var t = new Thread(() =>
            {
                try { Clipboard.SetFileDropList(files); }
                catch (Exception ex) { inner = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();
            if (inner != null) throw inner;
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeAutoUpdate(HttpListenerContext ctx)
    {
        if (_autoUpdateRunning)
        {
            ServeJson(ctx, JsonSerializer.Serialize(new
            { running = true, status = _autoUpdateStatus, done = _autoUpdateDone, total = _autoUpdateTotal }));
            return;
        }
        _autoUpdateRunning = true;
        _autoUpdateDone = 0; _autoUpdateTotal = 0;
        _autoUpdateStatus = "正在启动…";
        ServeJson(ctx, JsonSerializer.Serialize(new { running = true, status = "已启动全部资源更新" }));

        new Thread(() =>
        {
            try { RunAutoUpdate(); }
            catch (Exception ex) { Log("[AutoUpdate] 异常: " + ex.Message); }
            finally { _autoUpdateRunning = false; }
        })
        { IsBackground = true }.Start();
    }

    private static void SetAutoStatus(string s)
    {
        lock (_autoUpdLock) { _autoUpdateStatus = s; }
        Console.WriteLine("        [AR] " + s);
    }

    private static void RunAutoUpdate()
    {

        SetAutoStatus("正在从 kivo.wiki 获取角色列表…");
        var remote = FetchRemoteStudents();
        if (remote == null) { SetAutoStatus("获取远端角色列表失败，请检查网络。"); return; }
        SetAutoStatus("kivo.wiki 共 " + remote.Count + " 名角色");

        var local = ScanLocalStudents();
        var todo = new List<int>();
        int complete = 0;
        foreach (var r in remote)
        {
            local.TryGetValue(r.id, out var dir);
            var a = AuditStudent(r.id, dir ?? "");
            if (!a.Exists) { todo.Add(r.id); continue; }
            if (!a.Info || !a.Illus || !a.Gallery || !a.Voice || !a.Lobby || !a.Spine) todo.Add(r.id);
            else complete++;
        }
        _autoUpdateTotal = todo.Count;
        _autoUpdateDone = 0;
        SetAutoStatus($"本地目录 {local.Count} 个，完整 {complete} 个，待补全 {todo.Count} 个");

        int fetched = 0, failed = 0;
        var dynamicIds = new List<int>();
        foreach (var id in todo)
        {
            try
            {
                SetAutoStatus($"抓取 [{id}] 中…（{_autoUpdateDone + 1}/{_autoUpdateTotal}）");
                var r = FetchStudent(id);
                if (r.ContainsKey("error"))
                {
                    failed++;
                    SetAutoStatus($"[{id}] 失败：{r["error"]}");
                }
                else
                {
                    fetched++;
                    int lb = r.ContainsKey("lobby") ? Convert.ToInt32(r["lobby"]) : 0;
                    int sp = r.ContainsKey("spine") ? Convert.ToInt32(r["spine"]) : 0;
                    if (lb > 0 || sp > 0) dynamicIds.Add(id);
                    SetAutoStatus($"[{id}] {r["cn"]} 完成：立绘{r["illus"]}，画廊{r["gallery"]}，语音{r["voices"]}，大厅{lb}，Spine{sp}");
                }
            }
            catch (Exception ex) { failed++; SetAutoStatus($"[{id}] 异常：{ex.Message}"); }
            _autoUpdateDone++;
        }

        if (dynamicIds.Count > 0)
        {
            SetAutoStatus("发现动态立绘资源，临时拉起 AI 处理…");
            RunLive2DWorker(dynamicIds);
        }

        SetAutoStatus("正在清理空目录…");
        string root = Path.Combine(DataDir, "01_学生");
        int cleaned = 0;
        if (Directory.Exists(root))
        {
            var remoteSet = new HashSet<int>(remote.Select(x => x.id));
            foreach (var d in Directory.GetDirectories(root))
            {
                string idStr = Path.GetFileName(d).Split('_')[0];
                if (!int.TryParse(idStr, out int id)) continue;
                if (remoteSet.Contains(id)) continue;
                bool hasFiles = false;
                try { hasFiles = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length > 0; } catch { }
                if (!hasFiles) { try { Directory.Delete(d, true); cleaned++; } catch { } }
            }
        }

        SetAutoStatus("正在同步学生档案（技能/属性/数值）…");
        try
        {
            if (!_profSyncRunning)
            {
                _profSyncRunning = true;
                try { RunProfileSync(remote.Select(x => x.id).ToList(), false); }
                finally { _profSyncRunning = false; }
            }
        }
        catch (Exception ex) { Log("[AR] 学生档案同步失败: " + ex.Message); }

        SetAutoStatus("正在重载大厅数据…");
        LoadLobbyData();
        SetAutoStatus("正在重建索引…");
        BuildIndexes(useCache: false);
        SetAutoStatus($"全部更新完成！补全 {fetched} 成功 / {failed} 失败，清理空目录 {cleaned} 个。");
    }

    private static void ServeKivoCleanup(HttpListenerContext ctx, HttpListenerRequest req)
    {
        string root = Path.Combine(DataDir, "01_学生");
        string rootFull = Directory.Exists(root) ? Path.GetFullPath(root) : "";

        if (req.HttpMethod == "GET")
        {
            try
            {

                var remoteIds = new HashSet<int>();
                int page = 1, maxPage = 1;
                while (page <= maxPage)
                {
                    string? json = KivoGet(KIVO_API + "/data/students?page=" + page);
                    if (json == null) break;
                    using var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");
                    maxPage = data.GetProperty("max_page").GetInt32();
                    foreach (var s in data.GetProperty("students").EnumerateArray())
                        remoteIds.Add(s.GetProperty("id").GetInt32());
                    page++;
                }

                var orphans = new List<object>();
                int localTotal = 0;
                if (Directory.Exists(root))
                {
                    foreach (var d in Directory.GetDirectories(root))
                    {
                        localTotal++;
                        string dirName = Path.GetFileName(d);
                        string idStr = dirName.Split('_')[0];
                        if (!int.TryParse(idStr, out int id)) continue;
                        if (remoteIds.Contains(id)) continue;

                        long size = 0; int fileCount = 0;
                        try
                        {
                            var files = Directory.GetFiles(d, "*", SearchOption.AllDirectories);
                            fileCount = files.Length;
                            foreach (var f in files) { try { size += new FileInfo(f).Length; } catch { } }
                        }
                        catch { }

                        string cnName = dirName;
                        var infoFile = Path.Combine(d, "_info.json");
                        if (File.Exists(infoFile))
                        {
                            try
                            {
                                var info = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(infoFile, Encoding.UTF8));
                                string fn = Str(info, "family_name_cn", "family_name");
                                string gn = Str(info, "given_name_cn", "given_name");
                                if (!string.IsNullOrEmpty(fn) || !string.IsNullOrEmpty(gn)) cnName = fn + gn;
                            }
                            catch { }
                        }

                        orphans.Add(new Dictionary<string, object>
                        {
                            ["id"] = id, ["dir"] = dirName, ["cn"] = cnName,
                            ["fileCount"] = fileCount, ["size"] = size
                        });
                    }
                }

                ServeJson(ctx, JsonSerializer.Serialize(new
                {
                    items = orphans,
                    total = orphans.Count,
                    remoteCount = remoteIds.Count,
                    localCount = localTotal
                }, JsonOpts));
            }
            catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
        }
        else
        {
            try
            {
                string body;
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                using var doc = JsonDocument.Parse(body);
                var dirs = new List<string>();
                if (doc.RootElement.TryGetProperty("dirs", out var dirsEl))
                    foreach (var d in dirsEl.EnumerateArray()) dirs.Add(d.GetString() ?? "");

                int deleted = 0; long freedBytes = 0;
                var errors = new List<object>();
                foreach (var dirName in dirs)
                {
                    string full = Path.Combine(root, dirName);
                    if (string.IsNullOrEmpty(rootFull) || !Directory.Exists(full)) continue;

                    string fullCheck = Path.GetFullPath(full);
                    if (!fullCheck.StartsWith(rootFull + Path.DirectorySeparatorChar) && fullCheck != rootFull) continue;
                    try
                    {
                        var files = Directory.GetFiles(full, "*", SearchOption.AllDirectories);
                        foreach (var f in files) { try { freedBytes += new FileInfo(f).Length; } catch { } }
                        Directory.Delete(full, true);
                        deleted++;
                    }
                    catch (Exception ex) { errors.Add(new { dir = dirName, error = ex.Message }); }
                }
                ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, deleted, freedBytes, errors }));
            }
            catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
        }
    }

    private static void ServePlaylist(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (req.HttpMethod == "GET")
        {
            lock (_playlistLock)
            {
                ServeJson(ctx, JsonSerializer.Serialize(new
                {
                    list = _playlist, index = _playlistIdx, mode = _playMode,
                    current = (_playlistIdx >= 0 && _playlistIdx < _playlist.Count) ? _playlist[_playlistIdx] : null
                }, JsonOpts));
            }
            return;
        }
        try
        {
            string? body = ReadBodyOr413(ctx, 4 * 1024 * 1024);
            if (body == null) return;
            var msg = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
            string action = Str(msg, "action");
            lock (_playlistLock)
            {
                switch (action)
                {
                    case "set":
                        var list = new List<Dictionary<string, object>>();
                        if (msg!.TryGetValue("list", out var l) && l is JsonElement je && je.ValueKind == JsonValueKind.Array)
                            foreach (var x in je.EnumerateArray())
                                list.Add(x.ValueKind == JsonValueKind.Object ? JsonElementToDict(x) : new Dictionary<string, object>());
                        _playlist = list;
                        _playlistIdx = msg.TryGetValue("index", out var idx) && idx is JsonElement jidx && jidx.ValueKind == JsonValueKind.Number ? jidx.GetInt32() : -1;
                        if (_playlistIdx < 0 && _playlist.Count > 0) _playlistIdx = 0;
                        if (msg.TryGetValue("mode", out var m) && m is JsonElement jm && jm.ValueKind == JsonValueKind.String)
                            _playMode = jm.GetString() ?? "seq";
                        break;
                    case "next":
                        if (_playlist.Count == 0) _playlistIdx = -1;
                        else if (_playMode == "repeat") { }
                        else if (_playMode == "shuffle" && _playlist.Count > 1)
                        {
                            var rnd = new Random();
                            int next; do { next = rnd.Next(_playlist.Count); } while (next == _playlistIdx);
                            _playlistIdx = next;
                        }
                        else _playlistIdx = (_playlistIdx + 1) % _playlist.Count;
                        break;
                    case "prev":
                        if (_playlist.Count == 0) _playlistIdx = -1;
                        else if (_playMode == "repeat") { }
                        else if (_playMode == "shuffle" && _playlist.Count > 1)
                        {
                            var rnd = new Random();
                            int next; do { next = rnd.Next(_playlist.Count); } while (next == _playlistIdx);
                            _playlistIdx = next;
                        }
                        else _playlistIdx = (_playlistIdx - 1 + _playlist.Count) % _playlist.Count;
                        break;
                    case "mode":
                        if (msg.TryGetValue("mode", out var mm) && mm is JsonElement jmm && jmm.ValueKind == JsonValueKind.String)
                            _playMode = jmm.GetString() ?? "seq";
                        break;
                    case "jump":
                        if (msg.TryGetValue("index", out var ji) && ji is JsonElement jje && jje.ValueKind == JsonValueKind.Number)
                        {
                            int j = jje.GetInt32();
                            if (j >= 0 && j < _playlist.Count) _playlistIdx = j;
                        }
                        break;
                }
                ServeJson(ctx, JsonSerializer.Serialize(new
                {
                    ok = true, index = _playlistIdx, mode = _playMode,
                    current = (_playlistIdx >= 0 && _playlistIdx < _playlist.Count) ? _playlist[_playlistIdx] : null
                }, JsonOpts));
            }
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeFavorites(HttpListenerContext ctx, HttpListenerRequest req)
    {
        var file = Path.Combine(IdxDir, "favorites.json");
        if (req.HttpMethod == "GET")
        {
            lock (_favLock) { ServeJson(ctx, JsonSerializer.Serialize(_favorites)); }
            return;
        }
        try
        {
            string? body = ReadBodyOr413(ctx, 256 * 1024);
            if (body == null) return;
            var msg = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
            string action = Str(msg, "action");
            string key = Str(msg, "key");
            lock (_favLock)
            {
                if (action == "add") _favorites.Add(key);
                else if (action == "remove") _favorites.Remove(key);
                else if (action == "clear") _favorites.Clear();
                File.WriteAllText(file, JsonSerializer.Serialize(_favorites), Encoding.UTF8);
            }
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeMediaKeys(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (req.HttpMethod == "GET")
        {
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                enabled = _mediaKeysEnabled,
                note = "媒体键由前端 JS 注册，浏览器支持时即可响应 F7/F8/F9 / MediaPlayPause 等"
            }));
            return;
        }
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            var msg = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
            if (msg!.TryGetValue("enabled", out var e) && e is JsonElement je && je.ValueKind == JsonValueKind.True)
                _mediaKeysEnabled = true;
            else
                _mediaKeysEnabled = false;
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeLrc(HttpListenerContext ctx, string rel)
    {
        try
        {
            var f = ResolveMedia(rel);
            if (f == null || !File.Exists(f)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            string lrc = File.ReadAllText(f, Encoding.UTF8);
            var bytes = Encoding.UTF8.GetBytes(lrc);
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
    }

    private static readonly string AdminDefaultPass = Guid.NewGuid().ToString("N").Substring(0, 16);
    private static readonly string GuestDefaultPass = Guid.NewGuid().ToString("N").Substring(0, 16);

    private static void WriteInitialPassword(string name, string pass)
    {
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "初始密码.txt");
            File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + name + " = " + pass + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    private const int Pbkdf2Iterations = 210000;

    private static string HashPass(string name, string pass)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(name + ":" + pass), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return "pbkdf2$" + Pbkdf2Iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
    }

    private static string LegacyHashPass(string name, string pass)
    {
        using var sha = SHA256.Create();
        var b = sha.ComputeHash(Encoding.UTF8.GetBytes(name + ":" + pass));
        var sb = new StringBuilder();
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    private static bool VerifyPass(string name, string pass, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        if (stored.StartsWith("pbkdf2$", StringComparison.Ordinal))
        {
            try
            {
                var parts = stored.Split('$');
                if (parts.Length != 4) return false;
                int iter = int.Parse(parts[1]);
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] want = Convert.FromBase64String(parts[3]);
                byte[] got = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(name + ":" + pass), salt, iter, HashAlgorithmName.SHA256, want.Length);
                return CryptographicOperations.FixedTimeEquals(got, want);
            }
            catch { return false; }
        }
        return string.Equals(stored, LegacyHashPass(name, pass), StringComparison.Ordinal);
    }

    private static void TryUpgradeLegacyHash(Account? acct, string name, string pass)
    {
        if (acct == null || string.IsNullOrEmpty(acct.Pass)) return;
        if (acct.Pass.StartsWith("pbkdf2$", StringComparison.Ordinal)) return;
        if (!VerifyPass(name, pass, acct.Pass)) return;
        acct.Pass = HashPass(name, pass);
        SaveAccounts();
        Log("[账号] 密码哈希已升级为 PBKDF2: " + name);
    }

    private static void LoadAccounts()
    {
        try
        {
            lock (_accLock)
            {
                _accounts = new List<Account>();
                if (File.Exists(_accountsFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_accountsFile, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("accounts", out var arr))
                        foreach (var a in arr.EnumerateArray())
                        {
                            var acct = new Account
                            {
                                Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                                Pass = a.TryGetProperty("pass", out var p) ? p.GetString() ?? "" : "",
                                Role = a.TryGetProperty("role", out var ro) ? ro.GetString() ?? "user" : "user",
                                KbPerSec = a.TryGetProperty("kb", out var kb) ? kb.GetInt32() : 0,
                                QuotaMB = a.TryGetProperty("quota", out var q) ? q.GetInt64() : 0,
                                UsedTodayKB = a.TryGetProperty("used", out var u) ? u.GetInt64() : 0,
                                DayKey = a.TryGetProperty("day", out var d) ? d.GetString() ?? "" : ""
                            };

                            if (a.TryGetProperty("uis", out var uis) && uis.ValueKind == JsonValueKind.Object)
                            {
                                acct.HasUiSet = true;
                                if (uis.TryGetProperty("anim", out var v1)) acct.UiAnim = v1.GetBoolean();
                                if (uis.TryGetProperty("trans", out var v2)) acct.UiTrans = v2.GetBoolean();
                                if (uis.TryGetProperty("fast", out var v3)) acct.UiFast = v3.GetBoolean();
                                if (uis.TryGetProperty("dl", out var v4)) acct.UiDl = v4.GetBoolean();
                            }
                            if (a.TryGetProperty("perms", out var pe) && pe.ValueKind == JsonValueKind.Array)
                                foreach (var x in pe.EnumerateArray()) acct.Perms.Add(x.GetString() ?? "");
                            if (acct.DayKey != DateTime.Now.ToString("yyyyMMdd")) { acct.DayKey = DateTime.Now.ToString("yyyyMMdd"); acct.UsedTodayKB = 0; }

                            if (a.TryGetProperty("name", out var n2) && n2.GetString() == "admin") acct.Role = "super";
                            if (acct.Name.Length > 0) _accounts.Add(acct);
                        }
                }

                if (!_accounts.Any(a => a.Name == "admin"))
                {
                    _accounts.Add(new Account
                    {
                        Name = "admin", Pass = HashPass("admin", AdminDefaultPass), Role = "super",
                        Perms = new List<string> { "students", "gallery", "music", "videos", "bili" },
                        DayKey = DateTime.Now.ToString("yyyyMMdd")
                    });
                    SaveAccounts();
                    WriteInitialPassword("admin", AdminDefaultPass);
                    Log("[账号] 已创建内置管理员 admin，初始密码见 初始密码.txt，请及时在管理后台修改");
                }
                else
                {

                    var ad = _accounts.Find(a => a.Name == "admin");
                    if (ad != null && VerifyPass("admin", "admin123", ad.Pass))
                    {
                        ad.Pass = HashPass("admin", AdminDefaultPass);
                        SaveAccounts();
                        WriteInitialPassword("admin", AdminDefaultPass);
                        Console.WriteLine();
                        Console.WriteLine("        [账号] 内置 admin 的旧默认密码已升级，新密码见 初始密码.txt");
                        Log("[账号] 内置 admin 的旧默认密码已升级，新密码见 初始密码.txt");
                    }
                }

                if (!_accounts.Any(a => a.Name == "guest"))
                {
                    _accounts.Add(new Account
                    {
                        Name = "guest", Pass = HashPass("guest", GuestDefaultPass), Role = "user",
                        Perms = new List<string> { "students", "gallery", "music", "videos", "bili" },
                        DayKey = DateTime.Now.ToString("yyyyMMdd")
                    });
                    SaveAccounts();
                    WriteInitialPassword("guest", GuestDefaultPass);
                    Log("[账号] 已创建默认只读账户 guest，初始密码见 初始密码.txt");
                }
            }
        }
        catch (Exception ex) { Log("[账号] 加载失败: " + ex.Message); }
    }

    private static void SaveAccounts()
    {
        try
        {
            lock (_accLock)
            {
                var list = _accounts.Select(a => new Dictionary<string, object>
                {
                    ["name"] = a.Name, ["pass"] = a.Pass, ["role"] = a.Role,
                    ["perms"] = a.Perms.Cast<object>().ToList(),
                    ["kb"] = a.KbPerSec, ["quota"] = a.QuotaMB,
                    ["used"] = a.UsedTodayKB, ["day"] = a.DayKey,
                    ["uis"] = a.HasUiSet ? new Dictionary<string, object>
                    {
                        ["anim"] = a.UiAnim, ["trans"] = a.UiTrans, ["fast"] = a.UiFast, ["dl"] = a.UiDl
                    } : null
                }).ToList();
                File.WriteAllText(_accountsFile, JsonSerializer.Serialize(new Dictionary<string, object> { ["accounts"] = list }, JsonOpts), Encoding.UTF8);
            }
        }
        catch (Exception ex) { Log("[账号] 保存失败: " + ex.Message); }
    }

    private static void LoadExternalSettings()
    {
        try
        {
            if (File.Exists(_extSettingsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_extSettingsFile, Encoding.UTF8));
                _extEnabled = doc.RootElement.TryGetProperty("enabled", out var en) && en.GetBoolean();
                _extPort = doc.RootElement.TryGetProperty("port", out var po) ? po.GetInt32() : 8235;
            }
        }
        catch { }
    }

    private static void SaveExternalSettings()
    {
        try { File.WriteAllText(_extSettingsFile, JsonSerializer.Serialize(new Dictionary<string, object> { ["enabled"] = _extEnabled, ["port"] = _extPort }, JsonOpts), Encoding.UTF8); }
        catch { }
    }

    private static void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_settingsFile, Encoding.UTF8));
                var r = doc.RootElement;
                lock (_settingsLock)
                {
                    if (r.TryGetProperty("ui_animation", out var v)) _settings.UiAnimation = v.GetBoolean();
                    if (r.TryGetProperty("ui_transition", out var v2)) _settings.UiTransition = v2.GetBoolean();
                    if (r.TryGetProperty("fast_view", out var v3)) _settings.FastView = v3.GetBoolean();
                    if (r.TryGetProperty("show_download", out var v4)) _settings.ShowDownload = v4.GetBoolean();
                }
            }
        }
        catch { }
    }

    private static void SaveSettings()
    {
        try
        {
            lock (_settingsLock)
            {
                File.WriteAllText(_settingsFile, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["ui_animation"] = _settings.UiAnimation,
                    ["ui_transition"] = _settings.UiTransition,
                    ["fast_view"] = _settings.FastView,
                    ["show_download"] = _settings.ShowDownload
                }, JsonOpts), Encoding.UTF8);
            }
        }
        catch { }
    }

    private static void LoadAuthDevices()
    {
        try
        {
            if (File.Exists(_authFile))
            {
                var list = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_authFile, Encoding.UTF8)) ?? new HashSet<string>();
                lock (_authLock)
                {
                    _authIps.Clear();
                    foreach (var x in list) _authIps.Add(x);
                }
            }
        }
        catch { }
    }

    private static void SaveAuthDevices()
    {
        try { lock (_authLock) File.WriteAllText(_authFile, JsonSerializer.Serialize(_authIps), Encoding.UTF8); }
        catch { }
    }

    private static bool IsAuthorizedIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        lock (_authLock) return _authIps.Contains(ip);
    }

    private static void ServeDeviceAuth(HttpListenerContext ctx, HttpListenerRequest req)
    {
        try
        {
            string? body = ReadBodyOr413(ctx, 64 * 1024);
            if (body == null) return;
            var doc = JsonDocument.Parse(body);
            string key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            if (key != AuthPsk)
            {
                ctx.Response.StatusCode = 401;
                ServeJson(ctx, "{\"error\":\"密钥无效\"}");
                return;
            }
            string? ip = "";
            try { ip = req.RemoteEndPoint?.Address.ToString() ?? ""; } catch { }
            lock (_authLock) { if (_authIps.Add(ip)) SaveAuthDevices(); }
            Log("[授权] 设备已授权: " + ip);
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, ip, note = "该设备已获管理员免登录权限" }, JsonOpts));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeDeviceList(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        lock (_authLock)
        {
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, devices = _authIps.ToList() }, JsonOpts));
        }
    }

    private static void ServeDeviceRevoke(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            var doc = JsonDocument.Parse(body);
            string ip = doc.RootElement.TryGetProperty("ip", out var v) ? v.GetString() ?? "" : "";
            bool removed;
            lock (_authLock) { removed = _authIps.Remove(ip); if (removed) SaveAuthDevices(); }
            Log("[授权] 已撤销设备: " + ip);
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, removed }, JsonOpts));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static string? GetAdminToken(HttpListenerRequest req)
    {
        string? t = req.Headers["x-admin-token"];
        if (string.IsNullOrEmpty(t)) t = req.Cookies["BAWIKI_ADMIN"]?.Value;
        if (string.IsNullOrEmpty(t) && req.Headers["Cookie"] != null)
        {
            foreach (var pair in req.Headers["Cookie"].Split(';'))
            {
                var kv = pair.Trim().Split('=');
                if (kv.Length == 2 && kv[0] == "BAWIKI_ADMIN") t = kv[1];
            }
        }
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static AdminSession? ValidateAdminToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_admLock)
        {
            long now = DateTime.UtcNow.Ticks;
            var dead = _adminSessions.Where(kv => now - kv.Value.LastSeenTicks > TimeSpan.FromHours(12).Ticks).Select(kv => kv.Key).ToList();
            foreach (var dk in dead) _adminSessions.Remove(dk);
            if (_adminSessions.TryGetValue(token, out var s))
            {
                s.LastSeenTicks = now;

                lock (_accLock)
                {
                    var acct = _accounts.Find(a => a.Name == s.Name);
                    if (acct == null) { _adminSessions.Remove(token); return null; }
                    s.Role = acct.Role;
                    if (s.Role == "user") { _adminSessions.Remove(token); return null; }
                }
                return s;
            }
            return null;
        }
    }

    private static void RollDay()
    {
        string key = DateTime.Now.ToString("yyyyMMdd");
        lock (_extLock) { if (_extDayKey != key) { _extDayKey = key; _extBytesToday = 0; } }
        lock (_accLock)
        {
            foreach (var a in _accounts)
                if (a.DayKey != key) { a.DayKey = key; a.UsedTodayKB = 0; }
        }
    }

    private static void StartExternal(int port)
    {
        lock (_extLock)
        {
            StopExternal();
            _extPort = port;
            var t = new Thread(() =>
            {
                try
                {
                    var lst = new TcpListener(IPAddress.Any, port);
                    lst.Start();
                    lock (_extLock) { _extListener = lst; _extRunning = true; }
                    Log("[外服] 已启动，局域网端口 " + port);
                    Console.WriteLine("        [外服] 已启动，局域网端口 " + port);
                    while (_extRunning)
                    {
                        TcpClient? c = null;
                        try { c = lst.AcceptTcpClient(); }
                        catch { break; }
                        if (c == null) continue;
                        var cl = c;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { ServeExternalConn(cl); }
                            catch { }
                            finally { try { cl.Close(); } catch { } }
                        });
                    }
                }
                catch (Exception ex) { Log("[外服] 监听失败: " + ex.Message); Console.WriteLine("        [外服] 监听失败: " + ex.Message); }
                finally { lock (_extLock) { _extRunning = false; _extListener = null; } }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    private static void StopExternal()
    {
        lock (_extLock)
        {
            _extRunning = false;
            try { _extListener?.Stop(); } catch { }
            _extListener = null;
        }
        lock (_sessLock) _sessions.Clear();
    }

    private static string ReadRequestHead(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (sb.Length < 65536)
        {
            int n = stream.Read(buf, 0, 1);
            if (n <= 0) break;
            char ch = (char)buf[0];
            sb.Append(ch);
            int L = sb.Length;
            if (L >= 4 && sb[L - 4] == '\r' && sb[L - 3] == '\n' && sb[L - 2] == '\r' && sb[L - 1] == '\n') break;
        }
        return sb.ToString();
    }

    private static byte[] ReadExactly(NetworkStream ns, long count)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int r = ns.Read(buf, got, (int)(count - got));
            if (r <= 0) break;
            got += r;
        }
        if (got < count) Array.Resize(ref buf, got);
        return buf;
    }

    private static void SendSimple(NetworkStream s, string code, string reason, string ct, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(ct).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var hb = Encoding.ASCII.GetBytes(sb.ToString());
        s.Write(hb, 0, hb.Length);
        s.Write(body, 0, body.Length);
    }

    private static void SendRedirect(NetworkStream s, string loc)
    {
        var sb = new StringBuilder("HTTP/1.1 302 Found\r\n");
        sb.Append("Location: ").Append(loc).Append("\r\n");
        sb.Append("Content-Length: 0\r\nConnection: close\r\n\r\n");
        var hb = Encoding.ASCII.GetBytes(sb.ToString());
        s.Write(hb, 0, hb.Length);
    }

    private static string? GetCookie(Dictionary<string, string> headers, string name)
    {
        if (!headers.TryGetValue("Cookie", out var cookie)) return null;
        foreach (var pair in cookie.Split(';'))
        {
            var kv = pair.Trim().Split('=');
            if (kv.Length == 2 && kv[0] == name) return kv[1];
        }
        return null;
    }

    private const string ExtLoginHtml = @"<!DOCTYPE html>
<html lang=""zh-CN""><head><meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>基沃托斯古书馆 · 登录</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Microsoft YaHei',sans-serif;min-height:100vh;display:flex;align-items:center;justify-content:center;background:linear-gradient(120deg,#263473 0%,#3d5bb0 55%,#5a86d8 100%)}
.card{background:#fff;border-radius:18px;width:350px;padding:36px 30px 28px;box-shadow:0 20px 60px rgba(0,0,0,.35)}
h1{font-size:20px;color:#263473;text-align:center;margin-bottom:6px}
.sub{font-size:12.5px;color:#8a93a6;text-align:center;margin-bottom:22px}
label{display:block;font-size:13px;color:#55607a;font-weight:600;margin:14px 0 6px}
input{width:100%;padding:11px 14px;border:1.5px solid #dce3f0;border-radius:10px;font-size:14px;outline:none;background:#f8fafd;color:#333}
input:focus{border-color:#4a7fd4}
button{width:100%;margin-top:22px;padding:12px;border:none;border-radius:999px;background:#263473;color:#fff;font-size:15px;font-weight:700;cursor:pointer}
button:hover{background:#3d4f9e}
.err{background:#fdecec;color:#c0392b;font-size:12.5px;padding:8px 12px;border-radius:8px;margin-top:14px;text-align:center}
.hint{font-size:11.5px;color:#aab3c6;text-align:center;margin-top:16px;line-height:1.7}
</style></head><body>
<div class=""card"">
<h1>📚 基沃托斯古书馆</h1>
<div class=""sub"">局域网访客入口 · 凭账号登录后浏览</div>
<form method=""POST"" action=""/__login"">
<label>账号</label><input type=""text"" name=""user"" autocomplete=""username"" required autofocus>
<label>密码</label><input type=""password"" name=""pass"" autocomplete=""current-password"" required>
<button type=""submit"">进 入</button>
</form>
[ERR]
<div class=""hint"">账号由服务器管理员在管理后台创建<br>仅限局域网内使用，请勿外传</div>
</div></body></html>";

    private static void ServeExtLogin(NetworkStream s, string method, Dictionary<string, string> headers, string? ip)
    {
        if (method == "GET")
        {

            string? token = GetCookie(headers, "BAWIKI");
            bool ok = false;
            lock (_sessLock) ok = token != null && _sessions.ContainsKey(token);
            if (ok) { SendRedirect(s, "/"); return; }
            var body = Encoding.UTF8.GetBytes(ExtLoginHtml);
            SendSimple(s, "200", "OK", "text/html; charset=utf-8", body);
            return;
        }

        long len = 0;
        if (headers.TryGetValue("Content-Length", out var cl)) long.TryParse(cl, out len);
        if (len <= 0 || len > 65536) { SendSimple(s, "400", "Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("请求无效")); return; }
        string form = Encoding.UTF8.GetString(ReadExactly(s, len));
        string user = "", pass = "";
        foreach (var pair in form.Split('&'))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2) continue;
            string k, v;
            try { k = Uri.UnescapeDataString(kv[0].Replace('+', ' ')); v = Uri.UnescapeDataString(kv[1].Replace('+', ' ')); }
            catch { continue; }
            if (k == "user") user = v; else if (k == "pass") pass = v;
        }
        if (LoginBlocked(ip, user))
        {
            SendSimple(s, "429", "Too Many Requests", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("登录尝试过于频繁，请 10 分钟后再试"));
            return;
        }
        Account? acct = null;
        lock (_accLock) acct = _accounts.Find(a => a.Name == user && VerifyPass(user, pass, a.Pass));
        if (acct == null)
        {
            LoginFail(ip, user);
            string html = ExtLoginHtml.Replace("[ERR]", "<div class=\"err\">账号或密码错误</div>");
            var body = Encoding.UTF8.GetBytes(html);
            SendSimple(s, "200", "OK", "text/html; charset=utf-8", body);
            return;
        }
        LoginOk(ip, user);
        TryUpgradeLegacyHash(acct, user, pass);
        var session = new Session
        {
            Token = Guid.NewGuid().ToString("N"),
            Acct = acct,
            Ip = ip ?? "",
            StartTicks = DateTime.UtcNow.Ticks,
            LastSeenTicks = DateTime.UtcNow.Ticks
        };
        lock (_sessLock) _sessions[session.Token] = session;

        bool adminAcct = acct.Role == "super" || acct.Role == "admin";
        var sb = new StringBuilder("HTTP/1.1 302 Found\r\n");
        sb.Append("Location: ").Append(adminAcct ? "/web/admin.html" : "/").Append("\r\n");
        sb.Append("Set-Cookie: BAWIKI=").Append(session.Token).Append("; Path=/; HttpOnly; SameSite=Lax; Max-Age=86400\r\n");
        sb.Append("Content-Length: 0\r\nConnection: close\r\n\r\n");
        s.Write(Encoding.ASCII.GetBytes(sb.ToString()), 0, sb.Length);
    }

    private static void ServeExtLogout(NetworkStream s, Dictionary<string, string> headers)
    {
        string? token = GetCookie(headers, "BAWIKI");
        if (token != null) lock (_sessLock) _sessions.Remove(token);
        var sb = new StringBuilder("HTTP/1.1 302 Found\r\n");
        sb.Append("Location: /__login\r\n");
        sb.Append("Set-Cookie: BAWIKI=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT\r\n");
        sb.Append("Content-Length: 0\r\nConnection: close\r\n\r\n");
        s.Write(Encoding.ASCII.GetBytes(sb.ToString()), 0, sb.Length);
    }

    private static string? GetQueryParam(string? query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split('=');
            if (kv.Length == 2 && kv[0] == key) return kv[1];
        }
        return null;
    }

    private static string DecodeHexParam(string? query, string key)
    {
        string? p = GetQueryParam(query, key);
        if (string.IsNullOrEmpty(p)) return "";
        try
        {
            int n = p.Length / 2;
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = Convert.ToByte(p.Substring(i * 2, 2), 16);
            return Encoding.UTF8.GetString(b);
        }
        catch { return p; }
    }

    private static string? Require(Account a, string perm) => a.Perms.Contains(perm) ? null : "当前账号没有访问该资源分区的权限";

    private static bool IsLoopbackAddress(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        return ip == "127.0.0.1" || ip == "::1" || ip.StartsWith("127.") || ip.StartsWith("[::1]");
    }

    private static readonly object _localIpsLock = new();
    private static readonly HashSet<string> _localIps = new(StringComparer.OrdinalIgnoreCase);

    private static void RefreshLocalIps()
    {
        try
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "::1" };
            foreach (var a in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (a.ToString().Length > 0) set.Add(a.ToString());
            }
            lock (_localIpsLock)
            {
                _localIps.Clear();
                foreach (var x in set) _localIps.Add(x);
            }
        }
        catch { }
    }

    private static bool IsLocalIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        lock (_localIpsLock) return _localIps.Contains(ip) || IsLoopbackAddress(ip.ToLowerInvariant());
    }

    private static string? NormalizeExtPath(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw[0] != '/') return null;
        string s;
        try { s = Uri.UnescapeDataString(raw); } catch { return null; }
        if (s.IndexOf('\\') >= 0) s = s.Replace('\\', '/');
        foreach (char c in s)
            if (c < 0x20 || c == 0x7F || c == ' ' || c == '?' || c == '#') return null;
        var parts = new List<string>();
        foreach (var seg in s.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return "/" + string.Join("/", parts);
    }

    private static string EncodePath(string norm)
    {
        if (string.IsNullOrEmpty(norm) || norm == "/") return "/";
        var sb = new StringBuilder();
        foreach (var seg in norm.TrimStart('/').Split('/'))
        {
            sb.Append('/');
            sb.Append(Uri.EscapeDataString(seg));
        }
        return sb.ToString();
    }

    private static NameValueCollection ParseQuery(string? query)
    {
        var qs = new NameValueCollection();
        if (string.IsNullOrEmpty(query)) return qs;
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? pair.Substring(0, eq) : pair;
            string v = eq >= 0 ? pair.Substring(eq + 1) : "";
            try { k = Uri.UnescapeDataString(k.Replace('+', ' ')); } catch { }
            try { v = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { }
            qs.Add(k, v);
        }
        return qs;
    }

    private static string? PartOfPath(string full)
    {
        if (string.IsNullOrEmpty(full)) return null;
        string dataRoot = Path.GetFullPath(DataDir).TrimEnd('\\') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)) return null;
        string rel = full.Substring(dataRoot.Length);
        int sep = rel.IndexOf(Path.DirectorySeparatorChar);
        string first = sep >= 0 ? rel.Substring(0, sep) : rel;
        switch (first)
        {
            case "01_学生": return "students";
            case "02_官方画廊":
            case "02_galleries": return "gallery";
            case "03_游戏音乐": return "music";
            case "04_官方PV视频": return "videos";
            case "05_B站视频": return "bili";
            case "06_回忆大厅": return "students";
            case "07_3D模型": return "students";
            case "08_本地导入": return "music";
            case "10_无损音乐": return "music";
            case "_BA提取试点":
            case "_BA模型库": return "students";
            default: return null;
        }
    }

    private static string? ExternalDenyReason(Account a, string path, string? query)
        => ExternalDenyReason(a, path, ParseQuery(query));

    private static string? ExternalDenyReason(Account a, string path, NameValueCollection qs)
    {
        bool isAdmin = a.Role == "super" || a.Role == "admin";

        string lp = (path ?? "").ToLowerInvariant();

        if (lp.StartsWith("/api/admin") || lp == "/admin.html" || lp == "/web/admin.html")
            return isAdmin ? null : "此功能仅限管理员使用";

        if (lp.StartsWith("/api/ai")) return isAdmin ? null : "此功能仅限管理员使用";
        if (lp.StartsWith("/api/ldte/clip")) return "此功能仅限本地使用";
        if (lp == "/api/ldte/train") return "此功能仅限本地使用";
        if (lp.StartsWith("/api/ldte")) return Require(a, "music");

        if (lp.StartsWith("/api/local") || lp.StartsWith("/api/online")
            || lp == "/api/reindex" || lp == "/api/media-keys" || lp.StartsWith("/__"))
            return "此功能仅限本地使用";
        if (lp == "/api/students" || lp == "/api/student" || lp == "/api/lobby"
            || lp.StartsWith("/spines/") || lp.StartsWith("/lobby-img/"))
            return Require(a, "students");
        if (lp == "/api/gallery") return Require(a, "gallery");
        if (lp == "/api/music" || lp == "/api/playlist" || lp == "/api/favorites" || lp == "/api/lrc"
            || lp == "/api/audio/learn")
            return Require(a, "music");
        if (lp.StartsWith("/api/import") && lp != "/api/import/list")
            return "此功能仅限本地使用";
        if (lp == "/api/import/list") return Require(a, "music");
        if (lp == "/api/videos") return Require(a, "videos");

        if (lp == "/media" || lp == "/import-media" || lp == "/thumb")
        {
            string? perm;
            if (lp == "/thumb")
            {
                string type = (Q(qs, "type") ?? "").ToLowerInvariant();
                if (type == "students" || type == "gallery" || type == "music") perm = type;
                else if (type == "video") perm = "videos";
                else if (type == "fast")
                    perm = PartOfPath(ResolveMedia(PathParam(qs)) ?? "");
                else
                    return "无效的缩略图类型";
            }
            else
            {
                perm = PartOfPath(ResolveMedia(PathParam(qs)) ?? "");
            }
            if (perm == null) return "路径无效或不可访问";
            return Require(a, perm);
        }
        return null;
    }

    private static void ServeExternalConn(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 15000;
            using var stream = client.GetStream();
            string head = ReadRequestHead(stream);
            if (head.Length == 0) return;
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length < 3) return;
            string method = parts[0].ToUpperInvariant();
            string u = parts[1];
            string path, query = "";
            int q = u.IndexOf('?');
            if (q >= 0) { path = u.Substring(0, q); query = u.Substring(q + 1); }
            else path = u;

            string? normPath = NormalizeExtPath(path);
            if (normPath == null)
            {
                SendSimple(stream, "400", "Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("请求路径无效"));
                return;
            }
            path = normPath;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) break;
                int c = lines[i].IndexOf(':');
                if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            string? ip = "";
            try { ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(); } catch { }

            if (path == "/__login") { ServeExtLogin(stream, method, headers, ip); return; }
            if (path == "/__logout") { ServeExtLogout(stream, headers); return; }
            if (method != "GET" && method != "HEAD" && method != "POST") { SendSimple(stream, "405", "Method Not Allowed", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("仅支持浏览（GET/POST）")); return; }

            byte[]? postBody = null;
            if (method == "POST" && headers.TryGetValue("Content-Length", out var pcl) && long.TryParse(pcl, out var plen) && plen > 0 && plen <= 2 * 1024 * 1024)
            {
                try { postBody = ReadExactly(stream, plen); } catch { postBody = null; }
            }

            if (IsLocalIp(ip) || IsAuthorizedIp(ip))
            {

                headers["Host"] = "127.0.0.1:" + _localPort;
                ForwardExtNoAuth(stream, method, path, query, headers, postBody, ip: null, injectExt: false);
                return;
            }

            if (path == "/api/admin/login" || path == "/api/admin/logout")
            {
                ForwardExtNoAuth(stream, method, path, query, headers, postBody, ip: ip);
                return;
            }

            if (path == "/api/device/auth" && method == "POST")
            {
                string key = "";
                try
                {
                    if (postBody != null)
                    {
                        using var doc2 = JsonDocument.Parse(Encoding.UTF8.GetString(postBody));
                        key = doc2.RootElement.TryGetProperty("key", out var kk) ? kk.GetString() ?? "" : "";
                    }
                }
                catch { }
                if (key != AuthPsk)
                {
                    SendSimple(stream, "401", "Unauthorized", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"密钥无效\"}"));
                    return;
                }
                lock (_authLock) { if (_authIps.Add(ip)) SaveAuthDevices(); }
                Log("[授权] 设备已授权: " + ip);
                SendSimple(stream, "200", "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = true, ip, note = "该设备已获管理员免登录权限" }, JsonOpts)));
                return;
            }

            bool adminPage = string.Equals(path, "/admin.html", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(path, "/web/admin.html", StringComparison.OrdinalIgnoreCase);
            if (adminPage)
            {

                ForwardExtNoAuth(stream, method, path, query, headers, postBody, ip: ip);
                return;
            }
            bool adminPath = path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase);
            if (adminPath)
            {
                string? admTok = null;
                if (headers.TryGetValue("Cookie", out var ck))
                    foreach (var pair in ck.Split(';'))
                    {
                        var kv3 = pair.Trim().Split('=');
                        if (kv3.Length == 2 && kv3[0] == "BAWIKI_ADMIN") { admTok = kv3[1]; break; }
                    }

                if (string.IsNullOrEmpty(admTok) && headers.TryGetValue("X-Admin-Token", out var hx)) admTok = hx;
                if (ValidateAdminToken(admTok) != null)
                {
                    ForwardExtNoAuth(stream, method, path, query, headers, postBody, ip: ip);
                    return;
                }
            }

            RollDay();
            string? token = GetCookie(headers, "BAWIKI");
            Session? sess = null;
            lock (_sessLock)
            {
                if (token != null && _sessions.TryGetValue(token, out var s))
                {
                    sess = s;
                    s.LastSeenTicks = DateTime.UtcNow.Ticks;
                }
                long now = DateTime.UtcNow.Ticks;
                var dead = _sessions.Where(kv => now - kv.Value.LastSeenTicks > TimeSpan.FromHours(24).Ticks).Select(kv => kv.Key).ToList();
                foreach (var dk in dead) _sessions.Remove(dk);
            }
            if (sess == null)
            {

                if (adminPath)
                    SendSimple(stream, "401", "Unauthorized", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"未登录或会话已过期\",\"needLogin\":true}"));
                else if (path.StartsWith("/api/") || path == "/media" || path == "/thumb")
                    SendSimple(stream, "401", "Unauthorized", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"未登录或会话已过期\"}"));
                else
                    SendRedirect(stream, "/__login");
                return;
            }

            string? deny = ExternalDenyReason(sess.Acct, path, query);
            if (deny != null)
            {
                SendSimple(stream, "403", "Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(deny));
                return;
            }

            ForwardExt(stream, sess, method, path, query, headers, postBody);
        }
        catch (Exception ex) { Log("[外服] 连接处理异常: " + ex.Message); }
    }

    private static void ForwardExt(NetworkStream outStream, Session sess, string method, string path, string query, Dictionary<string, string> headers, byte[]? postBody)
    {

        if (method == "GET" && sess.Acct.QuotaMB > 0 && headers.TryGetValue("Content-Length", out var tmp))
        {   }
        ForwardCore(outStream, method, path, query, headers, postBody, countStats: method == "GET", sess: sess, clientIp: sess.Ip);
    }

    private static void ForwardExtNoAuth(NetworkStream outStream, string method, string path, string query, Dictionary<string, string> headers, byte[]? postBody, string? ip, bool injectExt = true)
    {
        ForwardCore(outStream, method, path, query, headers, postBody, countStats: false, sess: null, injectExt: injectExt, clientIp: ip);
    }

    private static void ForwardCore(NetworkStream outStream, string method, string path, string query, Dictionary<string, string> headers, byte[]? postBody, bool countStats, Session? sess, bool injectExt = true, string? clientIp = null)
    {

        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(EncodePath(path));
        if (!string.IsNullOrEmpty(query)) sb.Append('?').Append(query);
        sb.Append(" HTTP/1.1\r\n");

        sb.Append("Host: 127.0.0.1:").Append(_localPort).Append("\r\n");

        if (injectExt) sb.Append("X-BAW-Ext: ").Append(_extMark).Append("\r\n");

        if (injectExt && !string.IsNullOrEmpty(clientIp))
            sb.Append("X-BAW-IP: ").Append(clientIp).Append("\r\n");
        foreach (var kv in headers)
        {
            string h = kv.Key.ToLowerInvariant();
            if (h == "host" || h == "connection" || h == "cookie" || h == "content-length"
                || h == "accept-encoding" || h == "accept-language" || h == "origin"
                || h == "referer" || h == "upgrade-insecure-requests" || h.StartsWith("x-baw")
                || h.StartsWith("sec-fetch")) continue;
            sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        }

        if (headers.TryGetValue("Cookie", out var ckHdr))
        {
            var keep = new List<string>();
            foreach (var pair in ckHdr.Split(';'))
            {
                var kv2 = pair.Trim().Split('=');
                if (kv2.Length == 2 && (kv2[0] == "BAWIKI" || kv2[0] == "BAWIKI_ADMIN"))
                    keep.Add(kv2[0] + "=" + kv2[1]);
            }
            if (keep.Count > 0) sb.Append("Cookie: ").Append(string.Join("; ", keep)).Append("\r\n");
        }
        if (postBody != null) sb.Append("Content-Length: ").Append(postBody.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(sb.ToString());

        using var inner = new TcpClient();
        inner.Connect("127.0.0.1", _localPort);
        using var ns = inner.GetStream();
        ns.Write(headBytes, 0, headBytes.Length);
        if (postBody != null) ns.Write(postBody, 0, postBody.Length);

        string respHead = ReadRequestHead(ns);
        if (respHead.Length == 0) return;

        var rl = respHead.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var rh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < rl.Length; i++)
        {
            if (rl[i].Length == 0) break;
            int c = rl[i].IndexOf(':');
            if (c > 0) rh[rl[i].Substring(0, c).Trim()] = rl[i].Substring(c + 1).Trim();
        }
        long clen = rh.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var v) ? v : 0;

        if (countStats && clen > 0 && sess != null && sess.Acct.QuotaMB > 0)
        {
            long usedKB = 0;
            lock (_accLock) usedKB = sess.Acct.UsedTodayKB;
            if (usedKB + clen / 1024 > sess.Acct.QuotaMB * 1024)
            {
                SendSimple(outStream, "429", "Too Many Requests", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("今日下载配额已用尽，请联系管理员"));
                return;
            }
        }

        byte[]? inject = null;
        if (injectExt && method == "GET" && (path == "/web/index.html" || path == "/web/assets/app.js") && clen > 0 && clen < 400000)
        {
            var body = ReadExactly(ns, clen);
            inject = InjectExternalFlag(path, body);
            clen = inject.Length;
        }

        var outHead = new StringBuilder();
        string status = rl[0].Length > 9 ? rl[0].Substring(9) : "200 OK";
        outHead.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        foreach (var kv in rh)
        {
            string h = kv.Key.ToLowerInvariant();
            if (h == "content-length" || h == "connection" || h == "transfer-encoding" || h == "keep-alive") continue;
            outHead.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        }
        outHead.Append("Content-Length: ").Append(clen).Append("\r\n");
        outHead.Append("Connection: close\r\n\r\n");
        outStream.Write(Encoding.ASCII.GetBytes(outHead.ToString()), 0, outHead.Length);

        if (countStats && sess != null)
        {
            RollDay();
            lock (_accLock) sess.Acct.UsedTodayKB += clen / 1024;
            lock (_sessLock) sess.Bytes += clen;
            lock (_extLock) _extBytesToday += clen;
        }

        int speed = sess?.Acct.KbPerSec ?? 0;
        if (inject != null) WriteThrottled(outStream, inject, speed);
        else if (clen > 0) WriteThrottledStream(outStream, ns, clen, speed);
    }

    private static byte[] InjectExternalFlag(string path, byte[] body)
    {
        string text = Encoding.UTF8.GetString(body);
        if (path == "/web/index.html")
        {
            string script =
                "<script>window.BAWIKI_EXTERNAL=1;(function(){function h(){var nav=document.querySelector('.nav-links');if(nav){" +
                "var a=document.querySelector('[data-route=online]');if(a)a.remove();" +
                "var out=document.createElement('a');out.href='/__logout';out.style.padding='8px 14px';out.style.borderRadius='999px';" +
                "out.style.fontSize='14px';out.style.fontWeight='600';out.style.color='#55607a';out.textContent='↪ 退出登录';nav.appendChild(out);" +
                "}}if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',h);else h();})();</script>";
            if (text.Contains("</head>")) text = text.Replace("</head>", script + "</head>");
            else text = script + text;
        }
        else
        {
            text = "window.BAWIKI_EXTERNAL=1;\n" + text;
        }
        return Encoding.UTF8.GetBytes(text);
    }

    private static void WriteThrottled(NetworkStream ns, byte[] data, int kbPerSec)
    {
        int off = 0;
        var sw = Stopwatch.StartNew();
        while (off < data.Length)
        {
            int n = Math.Min(65536, data.Length - off);
            ns.Write(data, off, n);
            off += n;
            if (kbPerSec > 0)
            {
                long expectMs = off * 1000L / (kbPerSec * 1024L);
                long el = sw.ElapsedMilliseconds;
                if (el < expectMs) Thread.Sleep((int)Math.Min(expectMs - el, 500));
            }
        }
    }

    private static void WriteThrottledStream(NetworkStream ns, NetworkStream src, long count, int kbPerSec)
    {
        var buf = new byte[65536];
        long remain = count;
        long sent = 0;
        var sw = Stopwatch.StartNew();
        while (remain > 0)
        {
            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
            if (r <= 0) break;
            ns.Write(buf, 0, r);
            remain -= r; sent += r;
            if (kbPerSec > 0)
            {
                long expectMs = sent * 1000L / (kbPerSec * 1024L);
                long el = sw.ElapsedMilliseconds;
                if (el < expectMs) Thread.Sleep((int)Math.Min(expectMs - el, 500));
            }
        }
    }

    private static bool RequireAdmin(HttpListenerContext ctx, HttpListenerRequest req, out AdminSession? sess, out string? err)
    {
        err = null;

        sess = null;
        if (IsLocalDirect(req))
        {
            sess = new AdminSession { Token = "local", Name = "admin", Role = "super", LastSeenTicks = DateTime.UtcNow.Ticks };
            return true;
        }
        sess = ValidateAdminToken(GetAdminToken(req));
        if (sess == null)
        {

            string? vt = req.Cookies["BAWIKI"]?.Value;
            if (!string.IsNullOrEmpty(vt))
            {
                Account? va = null;
                lock (_sessLock) { if (_sessions.TryGetValue(vt, out var vs)) va = vs.Acct; }
                if (va != null && (va.Role == "super" || va.Role == "admin"))
                    sess = new AdminSession { Token = vt, Name = va.Name, Role = va.Role, LastSeenTicks = DateTime.UtcNow.Ticks };
            }
        }
        if (sess == null)
        {
            ctx.Response.StatusCode = 401;
            ServeJson(ctx, JsonSerializer.Serialize(new { error = "未登录或会话已过期", needLogin = true }));
            return false;
        }
        return true;
    }

    private static string ClientIpOf(HttpListenerRequest req)
    {
        if (!string.IsNullOrEmpty(req.Headers["X-BAW-Ext"]))
        {
            string fw = (req.Headers["X-BAW-IP"] ?? "").Trim();
            if (fw.Length > 0 && fw.Length <= 64) return fw;
        }
        return req.RemoteEndPoint?.Address?.ToString() ?? "";
    }

    private static bool IsLocalDirect(HttpListenerRequest req)
    {
        try
        {
            if (!string.IsNullOrEmpty(req.Headers["X-BAW-Ext"])) return false;
            var addr = req.RemoteEndPoint?.Address;
            if (addr == null || !IsLocalIp(addr.ToString())) return false;
            string host = (req.Headers["Host"] ?? "").Trim().ToLowerInvariant();
            int colon = host.LastIndexOf(':');
            string portPart = colon >= 0 ? host.Substring(colon + 1) : "";
            if (int.TryParse(portPart, out int p))
                return p == _localPort;
            return false;
        }
        catch { return false; }
    }

    private static bool IsCrossSiteBrowserRequest(HttpListenerRequest req)
    {
        string sfs = (req.Headers["Sec-Fetch-Site"] ?? "").ToLowerInvariant();
        if (sfs == "cross-site" || sfs == "same-site") return true;
        string origin = req.Headers["Origin"] ?? "";
        if (origin.Length > 0)
        {
            try
            {
                var ou = new Uri(origin);
                string host = (req.Headers["Host"] ?? "").ToLowerInvariant();
                return !host.StartsWith(ou.Authority.ToLowerInvariant(), StringComparison.Ordinal);
            }
            catch { return true; }
        }
        return false;
    }

    private static bool IsDangerousLocalPath(string path)
    {
        string p = (path ?? "").ToLowerInvariant();
        return p.StartsWith("/api/local/")
            || p.StartsWith("/api/admin/")
            || p.StartsWith("/api/import/")
            || p.StartsWith("/api/ai/")
            || p.StartsWith("/api/ldte/")
            || p == "/api/audio/learn"
            || p == "/api/playlist"
            || p == "/api/favorites"
            || p == "/api/reindex"
            || p == "/api/online/auto-update"
            || p == "/api/online/kivo-cleanup"
            || p == "/api/online/profile-sync"
            || p == "/api/online/download"
            || p == "/api/device/revoke"
            || p == "/api/media-keys"
            || p.StartsWith("/__");
    }

    private static void ServeAdminLogin(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (req.HttpMethod != "POST") { ctx.Response.StatusCode = 405; ctx.Response.Close(); return; }
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string pass = doc.RootElement.TryGetProperty("pass", out var p) ? p.GetString() ?? "" : "";
            string ip = ClientIpOf(req);
            if (LoginBlocked(ip, name))
            {
                ctx.Response.StatusCode = 429;
                ServeJson(ctx, "{\"error\":\"登录尝试过于频繁，请 10 分钟后再试\"}");
                return;
            }
            Account? acct = null;
            lock (_accLock) acct = _accounts.Find(a => a.Name == name && VerifyPass(name, pass, a.Pass));
            if (acct == null || acct.Role == "user")
            {
                LoginFail(ip, name);
                ServeJson(ctx, "{\"error\":\"账号不存在或没有管理权限\"}");
                return;
            }
            LoginOk(ip, name);
            TryUpgradeLegacyHash(acct, name, pass);
            var s = new AdminSession { Token = Guid.NewGuid().ToString("N"), Name = acct.Name, Role = acct.Role, LastSeenTicks = DateTime.UtcNow.Ticks };
            lock (_admLock) _adminSessions[s.Token] = s;
            byte[] bodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = true, token = s.Token, name = s.Name, role = s.Role }, JsonOpts));
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.AppendHeader("Set-Cookie", "BAWIKI_ADMIN=" + s.Token + "; Path=/; HttpOnly; SameSite=Lax; Max-Age=43200");
            ctx.Response.ContentLength64 = bodyBytes.Length;
            ctx.Response.OutputStream.Write(bodyBytes, 0, bodyBytes.Length);
            ctx.Response.Close();
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeAdminLogout(HttpListenerContext ctx, HttpListenerRequest req)
    {
        string? token = GetAdminToken(req);
        if (token != null) lock (_admLock) _adminSessions.Remove(token);

        string? vt = req.Cookies["BAWIKI"]?.Value;
        if (!string.IsNullOrEmpty(vt)) lock (_sessLock) _sessions.Remove(vt);
        byte[] b = Encoding.UTF8.GetBytes("{\"ok\":true}");
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.AppendHeader("Set-Cookie", "BAWIKI_ADMIN=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
        ctx.Response.AppendHeader("Set-Cookie", "BAWIKI=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
        ctx.Response.ContentLength64 = b.Length;
        ctx.Response.OutputStream.Write(b, 0, b.Length);
        ctx.Response.Close();
    }

    private static void ServeSettingsApi(HttpListenerContext ctx, HttpListenerRequest req)
    {

        Account? v = GetVisitorAccount(req);
        bool pers = v != null && v.HasUiSet;
        bool anim, trans, fast, dl;
        lock (_settingsLock)
        {
            anim = _settings.UiAnimation; trans = _settings.UiTransition;
            fast = _settings.FastView; dl = _settings.ShowDownload;
        }
        if (pers)
        {
            anim = v!.UiAnim; trans = v.UiTrans; fast = v.UiFast; dl = v.UiDl;
        }
        var resp = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["ui_animation"] = anim, ["ui_transition"] = trans,
            ["fast_view"] = fast, ["show_download"] = dl,
            ["personal"] = pers, ["me"] = v?.Name ?? "", ["role"] = v?.Role ?? "guest"
        };
        ServeJson(ctx, JsonSerializer.Serialize(resp, JsonOpts));
    }

    private static Account? GetVisitorAccount(HttpListenerRequest req)
    {
        Account? acct = null;
        string? t = req.Cookies["BAWIKI"]?.Value;
        if (!string.IsNullOrEmpty(t))
        {
            lock (_sessLock) { if (_sessions.TryGetValue(t, out var s)) acct = s.Acct; }
        }
        if (acct == null && IsLocalDirect(req))
        {
            lock (_accLock) acct = _accounts.Find(a => a.Name == "admin");
        }
        return acct;
    }

    private static void ServeMe(HttpListenerContext ctx, HttpListenerRequest req)
    {
        Account? v = GetVisitorAccount(req);
        if (v == null) { ServeJson(ctx, "{\"error\":\"未登录\",\"needLogin\":true}"); return; }
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true,
            name = v.Name, role = v.Role,
            perms = v.Perms.Cast<object>().ToList(),
            kb = v.KbPerSec, quota = v.QuotaMB,
            uis = v.HasUiSet ? new Dictionary<string, object>
            {
                ["anim"] = v.UiAnim, ["trans"] = v.UiTrans, ["fast"] = v.UiFast, ["dl"] = v.UiDl
            } : null
        }, JsonOpts));
    }

    private static void ServeMeUi(HttpListenerContext ctx, HttpListenerRequest req)
    {
        Account? v = GetVisitorAccount(req);
        if (v == null) { ServeJson(ctx, "{\"error\":\"未登录\",\"needLogin\":true}"); return; }
        if (req.HttpMethod != "POST") { ctx.Response.StatusCode = 405; ctx.Response.Close(); return; }
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            lock (_accLock)
            {
                var acct = _accounts.Find(a => a.Name == v.Name);
                if (acct == null) { ServeJson(ctx, "{\"error\":\"账号不存在\"}"); return; }
                if (r.TryGetProperty("anim", out var a1)) acct.UiAnim = a1.GetBoolean();
                if (r.TryGetProperty("trans", out var a2)) acct.UiTrans = a2.GetBoolean();
                if (r.TryGetProperty("fast", out var a3)) acct.UiFast = a3.GetBoolean();
                if (r.TryGetProperty("dl", out var a4)) acct.UiDl = a4.GetBoolean();
                acct.HasUiSet = true;
                SaveAccounts();
            }
            Log("[设置] 账户 " + v.Name + " 的个人 UI 设置已保存");
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeAdminSettings(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        if (req.HttpMethod != "POST") { ctx.Response.StatusCode = 405; ctx.Response.Close(); return; }
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            lock (_settingsLock)
            {
                if (r.TryGetProperty("ui_animation", out var v)) _settings.UiAnimation = v.GetBoolean();
                if (r.TryGetProperty("ui_transition", out var v2)) _settings.UiTransition = v2.GetBoolean();
                if (r.TryGetProperty("fast_view", out var v3)) _settings.FastView = v3.GetBoolean();
                if (r.TryGetProperty("show_download", out var v4)) _settings.ShowDownload = v4.GetBoolean();
            }
            SaveSettings();
            Log("[设置] 站点设置已保存");
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeAdminGpu(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        bool dllFound = File.Exists(Path.Combine(AppDir, "BAwikiGpu.dll"));
        if (req.HttpMethod == "GET")
        {
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                ok = true, enabled = _gpuThumb, forced = _gpuForce,
                available = _gpuAvailable, dll = dllFound,
                gpuMade = Interlocked.Read(ref _gpuThumbCount),
                cpuMade = Interlocked.Read(ref _cpuThumbCount)
            }));
            return;
        }
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            bool enable = r.TryGetProperty("enabled", out var ev) && ev.ValueKind == JsonValueKind.True;
            bool force = r.TryGetProperty("force", out var fv) && fv.ValueKind == JsonValueKind.True;

            string uac = "not-needed";
            if (enable && !_gpuAvailable)
            {
                if (!force)
                {
                    ctx.Response.StatusCode = 409;
                    ServeJson(ctx, "{\"ok\":false,\"needForce\":true,\"error\":\"本机没有可用的硬件加速设备，需要强行设置\"}");
                    return;
                }
                uac = TryElevateForGpu();
            }

            if (enable && _gpuThumbFn == null && !dllFound)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"缺少 BAwikiGpu.dll\"}");
                return;
            }

            _gpuForce = force;
            _gpuThumb = enable && _gpuThumbFn != null && (_gpuAvailable || force);
            try
            {
                File.WriteAllText(GpuConfigPath,
                    JsonSerializer.Serialize(new { enabled = _gpuThumb, forced = force }), Encoding.UTF8);
            }
            catch { }
            Log("[设置] GPU 缩略图 " + (_gpuThumb ? "开启" : "关闭") +
                "（硬件" + (_gpuAvailable ? "可用" : "不可用") + (force ? "，强制" : "") + "，UAC=" + uac + "）");
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                ok = true, enabled = _gpuThumb, forced = force,
                available = _gpuAvailable, uac
            }));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
    }

    private static string TryElevateForGpu()
    {
        try
        {
            string exe = Path.Combine(AppDir, "BAwikiLauncher.exe");
            if (!File.Exists(exe)) return "missing";
            var psi = new ProcessStartInfo(exe, "--uac-probe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Minimized
            };
            using var p = Process.Start(psi);
            if (p == null) return "failed";
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0 ? "granted" : "failed";
        }
        catch (System.ComponentModel.Win32Exception wex)
        {
            return wex.NativeErrorCode == 1223 ? "cancelled" : "unavailable";
        }
        catch { return "unavailable"; }
    }

    private static void ServeAdminStatus(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out var sess, out _)) return;
        lock (_extLock)
        {
            int online;
            lock (_sessLock) online = _sessions.Count;

            var lanIps = new List<string>();
            string lanIp = "";
            try
            {
                foreach (var a in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    {
                        lanIps.Add(a.ToString());
                        if (lanIp.Length == 0) lanIp = a.ToString();
                    }
                }
            }
            catch { }

            if (lanIps.Count == 0) lanIps.Add("127.0.0.1");
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                ok = true,
                me = sess!.Name, role = sess.Role,
                localPort = _localPort,
                localUrl = "http://127.0.0.1:" + _localPort + "/",
                lanIp = lanIp,
                lanIps = lanIps,
                external = _extEnabled,
                extRunning = _extRunning,
                extPort = _extPort,
                onlineCount = online
            }, JsonOpts));
        }
    }

    private static void ServeAdminExternal(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            bool enabled = doc.RootElement.TryGetProperty("enabled", out var en) && en.GetBoolean();
            int port = doc.RootElement.TryGetProperty("port", out var po) ? po.GetInt32() : 8235;
            if (port < 1024 || port > 65535) { ServeJson(ctx, "{\"error\":\"端口无效\"}"); return; }
            _extEnabled = enabled;
            _extPort = port;
            SaveExternalSettings();
            if (enabled) StartExternal(port);
            else StopExternal();
            Log("[外服] 设置: enabled=" + enabled + " port=" + port);
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, enabled, port, running = _extRunning }));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeAdminAccounts(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out var sess, out _)) return;
        string? myRole = sess!.Role;
        string? myName = sess.Name;
        if (req.HttpMethod == "GET")
        {
            lock (_accLock)
            {
                var list = _accounts.Select(a => new Dictionary<string, object>
                {
                    ["name"] = a.Name, ["role"] = a.Role,
                    ["perms"] = a.Perms.Cast<object>().ToList(),
                    ["kb"] = a.KbPerSec,
                    ["quota"] = a.QuotaMB,
                    ["usedKB"] = a.UsedTodayKB
                }).ToList();
                ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, accounts = list, me = myName, role = myRole }, JsonOpts));
            }
            return;
        }
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string action = r.TryGetProperty("action", out var ac) ? ac.GetString() ?? "" : "";
            string name = r.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            string pass = r.TryGetProperty("pass", out var pw) ? pw.GetString() ?? "" : "";
            string role = r.TryGetProperty("role", out var rl) ? rl.GetString() ?? "user" : "user";
            int kb = r.TryGetProperty("kb", out var k) ? k.GetInt32() : 0;
            long quota = r.TryGetProperty("quota", out var q) ? q.GetInt64() : 0;
            var perms = new List<string>();
            if (r.TryGetProperty("perms", out var pm) && pm.ValueKind == JsonValueKind.Array)
                foreach (var x in pm.EnumerateArray()) perms.Add(x.GetString() ?? "");

            lock (_accLock)
            {
                var existing = _accounts.Find(a => a.Name == name);

                bool isBuiltin = name == "admin";
                bool isSuper = myRole == "super";

                if (action == "add")
                {
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pass)) { ServeJson(ctx, "{\"error\":\"用户名与密码不能为空\"}"); return; }
                    if (existing != null) { ServeJson(ctx, "{\"error\":\"账号已存在\"}"); return; }
                    if (name == "admin") { ServeJson(ctx, "{\"error\":\"不允许创建内置管理员同名账号\"}"); return; }
                    if (!isSuper && role != "user") { ServeJson(ctx, "{\"error\":\"普通管理员只能创建普通用户，不可添加管理员权限\"}"); return; }
                    if (role == "super") { ServeJson(ctx, "{\"error\":\"不可创建超级管理员\"}"); return; }

                    _accounts.Add(new Account
                    {
                        Name = name, Pass = HashPass(name, pass), Role = role,
                        Perms = perms, KbPerSec = Math.Max(0, kb), QuotaMB = Math.Max(0, quota),
                        DayKey = DateTime.Now.ToString("yyyyMMdd")
                    });
                }
                else if (action == "update")
                {
                    if (existing == null) { ServeJson(ctx, "{\"error\":\"账号不存在\"}"); return; }
                    if (isBuiltin && !isSuper) { ServeJson(ctx, "{\"error\":\"不可修改内置本地安全后台 admin\"}"); return; }
                    if (!isSuper && existing.Role != "user") { ServeJson(ctx, "{\"error\":\"普通管理员不可管理其他管理员账号\"}"); return; }
                    if (!isSuper && role != "user") { ServeJson(ctx, "{\"error\":\"不可将其他账户添加为管理员权限\"}"); return; }

                    existing.Perms = perms;
                    existing.KbPerSec = Math.Max(0, kb);
                    existing.QuotaMB = Math.Max(0, quota);
                    if (!string.IsNullOrEmpty(pass)) existing.Pass = HashPass(name, pass);
                }
                else if (action == "delete")
                {
                    if (existing == null) { ServeJson(ctx, "{\"error\":\"账号不存在\"}"); return; }
                    if (isBuiltin) { ServeJson(ctx, "{\"error\":\"不可以删除原本就有的本地安全后台 admin 管理员\"}"); return; }
                    if (!isSuper && existing.Role != "user") { ServeJson(ctx, "{\"error\":\"普通管理员不可删除管理员账号\"}"); return; }
                    _accounts.Remove(existing);
                    lock (_sessLock)
                    {
                        var gone = _sessions.Where(kv => kv.Value.Acct == existing).Select(kv => kv.Key).ToList();
                        foreach (var gk in gone) _sessions.Remove(gk);
                    }
                }
                else { ServeJson(ctx, "{\"error\":\"未知操作\"}"); return; }
                SaveAccounts();
            }
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeAdminStats(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        RollDay();
        var online = new List<object>();
        lock (_sessLock)
        {
            foreach (var kv in _sessions)
            {
                var s = kv.Value;

                if (IsLoopbackAddress(s.Ip)) continue;
                online.Add(new Dictionary<string, object>
                {
                    ["user"] = s.Acct.Name,
                    ["ip"] = s.Ip,
                    ["minutes"] = (int)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - s.StartTicks).TotalMinutes,
                    ["bytes"] = s.Bytes
                });
            }
        }

        if (GetThumbStatCached(out long thumbCount, out long thumbSize))
        {

        }
        int acctCount;
        lock (_accLock) acctCount = _accounts.Count;
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true,
            onlineUsers = online,
            onlineCount = online.Count,
            todayBytes = _extBytesToday,
            thumbCount, thumbSize,
            accountCount = acctCount,
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        }, JsonOpts));
    }

    private static long _thumbStatMs, _thumbStatCount, _thumbStatSize;
    private static readonly object _thumbStatCacheLock = new();
    private static bool GetThumbStatCached(out long count, out long size)
    {
        long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        lock (_thumbStatCacheLock)
        {
            if (_thumbStatMs != 0 && nowMs - _thumbStatMs < 60000)
            {
                count = _thumbStatCount; size = _thumbStatSize;
                return true;
            }
        }
        long c = 0, s = 0;
        try
        {
            if (Directory.Exists(ThumbDir))
            {

                var dirs = new Stack<string>();
                dirs.Push(ThumbDir);
                while (dirs.Count > 0)
                {
                    var d = dirs.Pop();
                    string[] subs;
                    try { subs = Directory.GetDirectories(d); } catch { subs = Array.Empty<string>(); }
                    for (int i = 0; i < subs.Length; i++) dirs.Push(subs[i]);
                    string[] files;
                    try { files = Directory.GetFiles(d); } catch { files = Array.Empty<string>(); }
                    for (int i = 0; i < files.Length; i++)
                    {
                        long len;
                        try { len = new FileInfo(files[i]).Length; } catch { len = 0; }
                        c++; s += len;
                    }
                }
            }
        }
        catch { }
        lock (_thumbStatCacheLock)
        {
            _thumbStatCount = c; _thumbStatSize = s; _thumbStatMs = nowMs;
            count = c; size = s;
        }
        return false;
    }

    private static void ServeAdminCacheClear(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        try
        {
            long n = 0, sz = 0;
            if (Directory.Exists(ThumbDir))
                foreach (var f in Directory.GetFiles(ThumbDir, "*.*", SearchOption.AllDirectories))
                { try { sz += new FileInfo(f).Length; File.Delete(f); n++; } catch { } }
            _fileCache.Clear();
            Interlocked.Exchange(ref _fileCacheBytes, 0);

            lock (_thumbEnqLock) _thumbQueued.Clear();
            lock (_thumbStatCacheLock) _thumbStatMs = 0;
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, deleted = n, freed = sz }));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }
}

