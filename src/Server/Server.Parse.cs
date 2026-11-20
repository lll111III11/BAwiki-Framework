using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BAwikiWeb;

internal static partial class Server
{
    private static string InboxDir => Path.Combine(DataDir, "_inbox");
    private static string ParseCfg => Path.Combine(IdxDir, "parse.json");
    private static readonly object _parseLock = new();
    private static readonly ConcurrentDictionary<string, DateTime> _parseSeen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Dictionary<string, object>> _parseLog = new();
    private static bool _parseOn;
    private static bool _parseBusy;
    private static Timer? _parseTimer;

    private static void EnsureParseService()
    {
        try { Directory.CreateDirectory(InboxDir); } catch { }
        try
        {
            if (File.Exists(ParseCfg))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(ParseCfg));
                if (d != null && d.TryGetValue("on", out var v) && v.ValueKind == JsonValueKind.True) _parseOn = true;
            }
        }
        catch { }
        if (_parseTimer == null)
            _parseTimer = new Timer(_ => ParseTick(), null, 4000, 6000);
    }

    private static void ParseTick()
    {
        if (!_parseOn || _parseBusy) return;
        _parseBusy = true;
        try
        {
            if (!Directory.Exists(InboxDir)) return;
            bool changed = false;
            foreach (var f in Directory.GetFiles(InboxDir))
            {
                string name = Path.GetFileName(f);
                try
                {
                    string key = HashFile(f);
                    if (_parseSeen.TryGetValue(key, out var seenAt) && (DateTime.UtcNow - seenAt).TotalHours < 6) continue;

                    string ext0 = Path.GetExtension(f).ToLowerInvariant();
                    var byExt = FindParserPlugin(ext0);
                    string perr = "";
                    if (byExt != null && TryPluginHandle(byExt, f, name, out string prel, out perr))
                    {
                        changed = true;
                        _parseSeen[key] = DateTime.UtcNow;
                        AddParseLog(name, prel, true, "");
                        continue;
                    }
                    if (byExt != null && perr.Length > 0) AddParseLog(name, "", false, "E_PLUGIN:" + perr);

                    var (video, audio, _) = ProbeMedia(f);
                    if (!audio && !video)
                    {
                        if (TryFallbackPlugins(f, name, out string frel, out _))
                        {
                            changed = true;
                            _parseSeen[key] = DateTime.UtcNow;
                            AddParseLog(name, frel, true, "");
                            continue;
                        }
                        _parseSeen[key] = DateTime.UtcNow;
                        AddParseLog(name, "", false, "E_UNSUPPORTED");
                        continue;
                    }

                    string destDir = video
                        ? Path.Combine(DataDir, "08_本地导入", "视频")
                        : Path.Combine(DataDir, "10_无损音乐");
                    Directory.CreateDirectory(destDir);
                    string stem = SafeFileName(Path.GetFileNameWithoutExtension(f));
                    string ext = Path.GetExtension(f);
                    string dest = Path.Combine(destDir, stem + ext);
                    int n = 1;
                    while (File.Exists(dest)) dest = Path.Combine(destDir, stem + " (" + (++n) + ")" + ext);
                    File.Move(f, dest);
                    changed = true;
                    _parseSeen[key] = DateTime.UtcNow;
                    AddParseLog(name, Rel(dest), true, "");
                }
                catch (Exception ex)
                {
                    AddParseLog(name, "", false, ex.Message);
                }
            }
            if (changed)
            {
                try { BuildIndexes(useCache: false); } catch { }
            }
        }
        catch { }
        finally { _parseBusy = false; }
    }

    private static bool TryPluginHandle(PluginDef pp, string f, string name, out string destRel, out string err)
    {
        destRel = ""; err = "";
        try
        {
            string outDir = Path.Combine(InboxDir, ".out", pp.Id);
            Directory.CreateDirectory(outDir);
            bool ok = InvokePlugin(pp, "parse", new Dictionary<string, object>
            {
                ["file"] = f,
                ["outDir"] = outDir,
                ["ffmpeg"] = Ffmpeg ?? "",
                ["quality"] = QualityOf(f)
            }, out string pout, out string pmsg);
            lock (_plugLock) { pp.LastRun = DateTime.Now.ToString("MM-dd HH:mm:ss"); pp.LastError = ok ? "" : pmsg; }
            if (!ok || !File.Exists(pout)) { err = pmsg.Length > 0 ? pmsg : "E_PLUGIN"; return false; }

            var (pv, pa, _) = ProbeMedia(pout);
            if (!pv && !pa) { err = "E_PLUGIN_OUTPUT"; return false; }

            string pdDir = pv
                ? Path.Combine(DataDir, "08_本地导入", "视频")
                : Path.Combine(DataDir, "10_无损音乐");
            Directory.CreateDirectory(pdDir);
            string pstem = SafeFileName(Path.GetFileNameWithoutExtension(pout));
            string pext = Path.GetExtension(pout);
            string pdest = Path.Combine(pdDir, pstem + pext);
            int pn = 1;
            while (File.Exists(pdest)) pdest = Path.Combine(pdDir, pstem + " (" + (++pn) + ")" + pext);
            File.Move(pout, pdest);

            string doneDir = Path.Combine(InboxDir, ".done");
            Directory.CreateDirectory(doneDir);
            try { File.Move(f, Path.Combine(doneDir, name)); } catch { }
            destRel = Rel(pdest);
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    private static bool TryFallbackPlugins(string f, string name, out string destRel, out string err)
    {
        destRel = ""; err = "";
        List<PluginDef> cands;
        lock (_plugLock)
        {
            cands = _plugins.Where(p => p.Enabled
                && p.Formats.Any(x => x == "*")
                && (p.Force == "global" || p.Force == "parser"
                    || (p.Force.Length == 0 && (p.Categories.Any(c => c.Equals("global", StringComparison.OrdinalIgnoreCase))
                        || p.Categories.Any(c => c.Equals("parser", StringComparison.OrdinalIgnoreCase))))))
                .ToList();
        }
        foreach (var p in cands)
            if (TryPluginHandle(p, f, name, out destRel, out err)) return true;
        return false;
    }

    private static void AddParseLog(string file, string to, bool ok, string error)
    {
        lock (_parseLock)
        {
            _parseLog.Insert(0, new Dictionary<string, object>
            {
                ["file"] = file,
                ["to"] = to,
                ["ok"] = ok,
                ["error"] = error ?? "",
                ["time"] = DateTime.Now.ToString("MM-dd HH:mm:ss")
            });
            if (_parseLog.Count > 50) _parseLog.RemoveRange(50, _parseLog.Count - 50);
        }
        if (!ok) Log("[解析] " + file + " -> " + error);
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static void ServeAdminParse(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        if (req.HttpMethod == "POST")
        {
            try
            {
                using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = sr.ReadToEnd();
                _parseOn = body.Contains("\"on\":true") || body.Contains("\"on\": true");
                File.WriteAllText(ParseCfg, JsonSerializer.Serialize(new Dictionary<string, object> { ["on"] = _parseOn }));
                Log("[解析] 服务已" + (_parseOn ? "开启" : "关闭"));
            }
            catch { }
        }
        List<Dictionary<string, object>> log;
        lock (_parseLock) log = _parseLog.Take(20).ToList();
        ServeJson(ctx, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["on"] = _parseOn,
            ["busy"] = _parseBusy,
            ["inbox"] = InboxDir,
            ["results"] = log
        }, JsonOpts));
    }
}
