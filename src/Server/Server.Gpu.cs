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

    private delegate int GpuThumbFn([MarshalAs(UnmanagedType.LPWStr)] string src,
                                    [MarshalAs(UnmanagedType.LPWStr)] string dst, int maxDim, int quality);
    private delegate int GpuAvailFn();
    private static volatile bool _gpuThumb;
    private static bool _gpuForce;
    private static bool _gpuAvailable;
    private static long _gpuThumbCount;
    private static long _cpuThumbCount;
    private static IntPtr _gpuDll;
    private static GpuThumbFn? _gpuThumbFn;

    private static string GpuConfigPath => Path.Combine(IdxDir, "gpu.json");

    private static void LoadGpuConfig()
    {
        bool enabled = false;
        try
        {
            if (File.Exists(GpuConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(GpuConfigPath));
                enabled = doc.RootElement.TryGetProperty("enabled", out var v) && v.ValueKind == JsonValueKind.True;
                _gpuForce = doc.RootElement.TryGetProperty("forced", out var f) && f.ValueKind == JsonValueKind.True;
            }
        }
        catch { }

        string dll = Path.Combine(AppDir, "BAwikiGpu.dll");
        if (File.Exists(dll))
        {
            try
            {
                _gpuDll = NativeLibrary.Load(dll);
                var avail = Marshal.GetDelegateForFunctionPointer<GpuAvailFn>(NativeLibrary.GetExport(_gpuDll, "BawikiGpuAvailable"));
                _gpuThumbFn = Marshal.GetDelegateForFunctionPointer<GpuThumbFn>(NativeLibrary.GetExport(_gpuDll, "BawikiGpuThumb"));
                _gpuAvailable = avail() == 1;
            }
            catch (Exception ex) { Log("GPU 载入失败: " + ex.Message); }
        }
        _gpuThumb = enabled && _gpuThumbFn != null && (_gpuAvailable || _gpuForce);
        if (enabled)
            Log("GPU 缩略图 " + (_gpuThumb ? "已启用" : "未启用") +
                "（硬件" + (_gpuAvailable ? "可用" : "不可用") + (_gpuForce ? "，强制" : "") + "）");
    }

    private static bool GpuMakeThumb(string src, string dst, int maxDim)
    {
        if (!_gpuThumb || _gpuThumbFn == null) return false;
        try
        {
            if (_gpuThumbFn(src, dst, maxDim, 82) != 0) return false;
            if (File.Exists(dst) && new FileInfo(dst).Length > 0)
            {
                Interlocked.Increment(ref _gpuThumbCount);
                return true;
            }
            return false;
        }
        catch { return false; }
    }
}
