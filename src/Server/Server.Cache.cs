using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BAwikiWeb;

internal static partial class Server
{

    private sealed class PerfConfig
    {
        public int Threads { get; set; }
        public bool HighPriority { get; set; } = true;
        public bool MmfCache { get; set; } = true;
    }

    private static PerfConfig _perf = new();
    private static int _ioThreads = Math.Min(32, Math.Max(4, Environment.ProcessorCount * 2));
    private static MemoryMappedFile? _cacheMmf;
    private static MemoryMappedViewAccessor? _cacheView;

    private static void ApplyPerf()
    {
        try
        {
            int cpu = Environment.ProcessorCount;
            _ioThreads = _perf.Threads > 0 ? _perf.Threads : Math.Min(32, Math.Max(4, cpu * 2));
            ThreadPool.SetMinThreads(Math.Max(32, _ioThreads * 2), Math.Max(32, _ioThreads));
            try
            {
                Process.GetCurrentProcess().PriorityClass = _perf.HighPriority
                    ? ProcessPriorityClass.High
                    : ProcessPriorityClass.Normal;
            }
            catch { }
            Console.WriteLine("        [性能] CPU " + cpu + " 核 / 扫描线程 " + _ioThreads +
                              " / 优先级 " + (_perf.HighPriority ? "高" : "普通") +
                              " / 内存缓存 " + (_perf.MmfCache ? "开" : "关"));
            Log("性能 CPU " + cpu + " 核 / 扫描线程 " + _ioThreads +
                " / 优先级 " + (_perf.HighPriority ? "高" : "普通") +
                " / 内存缓存 " + (_perf.MmfCache ? "开" : "关"));
        }
        catch { }
    }

    private static void LoadPerfConfig()
    {
        string p = Path.Combine(IdxDir, "perf.json");
        try
        {
            if (File.Exists(p))
            {
                var cfg = JsonSerializer.Deserialize<PerfConfig>(File.ReadAllText(p));
                if (cfg != null) _perf = cfg;
            }
            else
            {
                File.WriteAllText(p, JsonSerializer.Serialize(_perf,
                    new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
        }
        catch { }
        ApplyPerf();
    }

    private static void DisposeCacheMmf()
    {
        try { _cacheView?.Dispose(); } catch { }
        try { _cacheMmf?.Dispose(); } catch { }
        _cacheView = null;
        _cacheMmf = null;
    }

    private static bool TryLoadBinaryCache()
    {
        if (!_perf.MmfCache) return false;
        string bp = Path.Combine(IdxDir, "_cache_index.bin");
        if (!File.Exists(bp)) return false;
        try
        {
            DisposeCacheMmf();
            _cacheMmf = MemoryMappedFile.CreateFromFile(bp, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _cacheView = _cacheMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var v = _cacheView;
            byte[] magic = new byte[8];
            if (v.ReadArray(0, magic, 0, 8) != 8 || Encoding.ASCII.GetString(magic) != "BAWCACH1")
            {
                DisposeCacheMmf();
                return false;
            }
            var parts = new string[4];
            long off = 32;
            for (int i = 0; i < 4; i++)
            {
                int len = v.ReadInt32(16 + i * 4);
                if (len < 0 || len > 256 * 1024 * 1024) { DisposeCacheMmf(); return false; }
                byte[] buf = new byte[len];
                if (len > 0 && v.ReadArray(off, buf, 0, len) != len) { DisposeCacheMmf(); return false; }
                parts[i] = Encoding.UTF8.GetString(buf);
                off += len;
            }
            _jsonStudents = parts[0];
            _jsonGallery  = parts[1];
            _jsonMusic    = parts[2];
            _jsonVideos   = parts[3];
            return true;
        }
        catch
        {
            DisposeCacheMmf();
            return false;
        }
    }

    private static readonly object _cacheWriteLock = new();

    private static void WriteBinaryCache()
    {
        if (!_perf.MmfCache) return;
        lock (_cacheWriteLock)
        {
            try
            {
                DisposeCacheMmf();
                string bp = Path.Combine(IdxDir, "_cache_index.bin");
                string tmp = bp + ".tmp";
                var bodies = new[]
                {
                    Encoding.UTF8.GetBytes(_jsonStudents ?? "{}"),
                    Encoding.UTF8.GetBytes(_jsonGallery  ?? "{}"),
                    Encoding.UTF8.GetBytes(_jsonMusic    ?? "{}"),
                    Encoding.UTF8.GetBytes(_jsonVideos   ?? "{}")
                };
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(Encoding.ASCII.GetBytes("BAWCACH1"));
                    bw.Write(ReadCacheMru());
                    foreach (var b in bodies) bw.Write(b.Length);
                    foreach (var b in bodies) bw.Write(b);
                }
                File.Move(tmp, bp, true);
            }
            catch { }
        }
    }

    private static long ReadCacheMru()
    {
        try
        {
            string p = Path.Combine(IdxDir, "_cache_meta.json");
            if (!File.Exists(p)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("mru", out var v) && v.TryGetInt64(out long m) ? m : 0;
        }
        catch { return 0; }
    }
}
