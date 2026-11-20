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

    private sealed class AudioLearn
    {
        public string Name { get; set; } = "";
        public double Peak { get; set; }
        public double Rms { get; set; }
        public double RmsWeight { get; set; }
        public double Hot { get; set; }
        public double Seconds { get; set; }
        public int Plays { get; set; }
        public double Lufs { get; set; }
        public double TruePeakDb { get; set; }
        public string Updated { get; set; } = "";
    }
    private static readonly object _alLock = new();
    private static Dictionary<string, AudioLearn> _audioLearn = new();
    private static string AudioLearnFile => Path.Combine(IdxDir, "audio-learn.json");

    private static void LoadAudioLearn()
    {
        try
        {
            lock (_alLock)
            {
                _audioLearn = File.Exists(AudioLearnFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, AudioLearn>>(File.ReadAllText(AudioLearnFile)) ?? new()
                    : new();
            }
            Log("音频学习库 " + _audioLearn.Count + " 首");
        }
        catch { _audioLearn = new(); }
    }

    private static void SaveAudioLearn()
    {
        try
        {
            lock (_alLock)
                File.WriteAllText(AudioLearnFile, JsonSerializer.Serialize(_audioLearn), Encoding.UTF8);
        }
        catch { }
    }

    private static void ServeAudioLearn(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (req.HttpMethod == "GET")
        {
            string key = req.QueryString["key"] ?? "";
            AudioLearn? p = null;
            lock (_alLock) _audioLearn.TryGetValue(key, out p);
            if (p == null) { ServeJson(ctx, "{\"ok\":true,\"learned\":false}"); return; }
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                ok = true, learned = true, p.Name, p.Peak, p.Rms,
                p.Hot, p.Seconds, p.Plays, p.Updated
            }));
            return;
        }
        try
        {
            string? body = ReadBodyOr413(ctx, 1024 * 1024);
            if (body == null) return;
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string key = r.TryGetProperty("key", out var kv) ? (kv.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(key)) { ServeJson(ctx, "{\"ok\":false}"); return; }
            string name = r.TryGetProperty("name", out var nv) ? (nv.GetString() ?? "") : "";
            double peak = r.TryGetProperty("peak", out var pv) ? pv.GetDouble() : 0;
            double rms = r.TryGetProperty("rms", out var rv) ? rv.GetDouble() : 0;
            double hot = r.TryGetProperty("hot", out var hv) ? hv.GetDouble() : 0;
            double seconds = r.TryGetProperty("seconds", out var sv) ? sv.GetDouble() : 0;
            bool first = r.TryGetProperty("first", out var fv) && fv.ValueKind == JsonValueKind.True;

            lock (_alLock)
            {
                if (!_audioLearn.TryGetValue(key, out var p))
                {
                    p = new AudioLearn();
                    _audioLearn[key] = p;
                }
                if (!string.IsNullOrEmpty(name)) p.Name = name;
                if (peak > p.Peak) p.Peak = peak;
                if (hot > p.Hot) p.Hot = hot;
                if (seconds > p.Seconds) p.Seconds = seconds;
                if (rms > 0 && seconds > 0)
                {
                    double w = Math.Min(seconds, 3600);
                    p.Rms = p.RmsWeight <= 0 ? rms : (p.Rms * p.RmsWeight + rms * w) / (p.RmsWeight + w);
                    p.RmsWeight += w;
                }
                if (first) p.Plays++;
                p.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            SaveAudioLearn();
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
    }
}
