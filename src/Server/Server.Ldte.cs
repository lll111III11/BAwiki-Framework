using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BAwikiWeb;

internal static partial class Server
{
    private const int LdteV1HeaderSize = 4 + 2 + 2 + 4 + 4 + 4;
    private const int LdteV2HeaderSize = 48;
    private const int LdteChannels = 12;
    private static readonly string[] LdteChannelNames =
        { "peak", "rms", "zcr", "flux", "bright", "low", "mid", "high", "width", "corr", "trans", "clip" };
    private static readonly object _ldteLock = new();

    private sealed class LdteConfig
    {
        public int MaxGB { get; set; } = 12;
        public bool SingleFile { get; set; }
        public bool FullRecord { get; set; }
    }
    private static LdteConfig? _ldteCfg;
    private static readonly object _ldteCfgLock = new();
    private static string LdteCfgFile => Path.Combine(LdteDir(), "ldte.json");

    private static LdteConfig LdteCfg
    {
        get
        {
            lock (_ldteCfgLock)
            {
                if (_ldteCfg != null) return _ldteCfg;
                var cfg = new LdteConfig();
                try
                {
                    string p = LdteCfgFile;
                    if (File.Exists(p))
                    {
                        var d = JsonSerializer.Deserialize<LdteConfig>(File.ReadAllText(p));
                        if (d != null) cfg = d;
                    }
                }
                catch { }
                cfg.MaxGB = Math.Clamp(cfg.MaxGB <= 0 ? 12 : cfg.MaxGB, 1, 12);
                _ldteCfg = cfg;
                return cfg;
            }
        }
    }

    private static void SaveLdteCfg(int maxGB, bool? singleFile, bool? fullRecord)
    {
        var cfg = LdteCfg;
        cfg.MaxGB = Math.Clamp(maxGB, 1, 12);
        if (singleFile.HasValue) cfg.SingleFile = singleFile.Value;
        if (fullRecord.HasValue) cfg.FullRecord = fullRecord.Value;
        try
        {
            File.WriteAllText(LdteCfgFile, JsonSerializer.Serialize(cfg,
                new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch { }
    }

    private static long LdteCapBytes => Math.Clamp(LdteCfg.MaxGB, 1, 12) * 1024L * 1024L * 1024L;

    private static long LdteUsedBytes()
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.GetFiles(LdteDir(), "*.ldte"))
                try { total += new FileInfo(f).Length; } catch { }
            string clips = Path.Combine(LdteDir(), "clips");
            if (Directory.Exists(clips))
                foreach (var f in Directory.GetFiles(clips, "*.ldtc"))
                    try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    private static void EnforceLdteBudget()
    {
        try
        {
            long cap = LdteCapBytes;
            long total = LdteUsedBytes();
            if (total <= cap) return;
            Directory.CreateDirectory(LdteDir());
            string sys = LdtePath(SystemKey);
            var old = new DirectoryInfo(LdteDir()).GetFiles("*.ldte")
                .Where(f => !string.Equals(f.FullName, sys, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            int removed = 0;
            foreach (var f in old)
            {
                if (total <= cap) break;
                long len = 0;
                try { len = f.Length; } catch { }
                try { f.Delete(); total -= len; removed++; } catch { }
            }
            string clipsDir = Path.Combine(LdteDir(), "clips");
            if (total > cap && Directory.Exists(clipsDir))
            {
                var grips = new DirectoryInfo(clipsDir).GetFiles("*.ldtc").OrderBy(f => f.LastWriteTimeUtc).ToList();
                var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in grips)
                {
                    if (total <= cap) break;
                    long len = f.Length;
                    try { f.Delete(); total -= len; removed++; gone.Add(f.Name); } catch { }
                }
                if (gone.Count > 0)
                {
                    lock (_ldteClipLock)
                    {
                        var list = LoadClipIndex();
                        list.RemoveAll(x => x.TryGetValue("file", out var rf) && gone.Contains(Path.GetFileName(rf?.ToString() ?? "")));
                        SaveClipIndex(list);
                    }
                }
            }
            if (removed > 0) Log("[LDTE] 超出 " + LdteCfg.MaxGB + " GB 上限，已清理 " + removed + " 个旧档案");
        }
        catch (Exception ex) { Log("[LDTE] 额度检查失败: " + ex.Message); }
    }

    private static readonly HttpClient _ldteCloudHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly object _ldteCloudLock = new();
    private static volatile bool _cloudSawOffline;

    private static string LdteCloudCfgFile => Path.Combine(LdteDir(), "_keys", "cloud.json");
    private static string LdteCloudIssuedFile => Path.Combine(LdteDir(), "_keys", "cloud_issued.json");

    private static Dictionary<string, string> LoadCloudCfg()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(LdteCloudCfgFile)) return d;
            using var doc = JsonDocument.Parse(File.ReadAllText(LdteCloudCfgFile));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString() ?? "";
        }
        catch { }
        return d;
    }

    private static List<Dictionary<string, object>> LoadCloudIssued()
    {
        try
        {
            if (File.Exists(LdteCloudIssuedFile))
            {
                var raw = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(File.ReadAllText(LdteCloudIssuedFile));
                if (raw != null)
                {
                    var list = new List<Dictionary<string, object>>();
                    foreach (var d in raw)
                    {
                        var o = new Dictionary<string, object>();
                        foreach (var kv in d)
                            o[kv.Key] = kv.Value.ValueKind switch
                            {
                                JsonValueKind.String => kv.Value.GetString() ?? "",
                                JsonValueKind.Number => kv.Value.TryGetInt64(out long l) ? l : kv.Value.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => kv.Value.ToString() ?? ""
                            };
                        list.Add(o);
                    }
                    return list;
                }
            }
        }
        catch { }
        return new List<Dictionary<string, object>>();
    }

    private static void SaveCloudIssued(List<Dictionary<string, object>> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LdteCloudIssuedFile) ?? LdteDir());
            if (File.Exists(LdteCloudIssuedFile))
            {
                try { File.SetAttributes(LdteCloudIssuedFile, FileAttributes.Normal); } catch { }
            }
            File.WriteAllText(LdteCloudIssuedFile, JsonSerializer.Serialize(list), Encoding.UTF8);
            try { File.SetAttributes(LdteCloudIssuedFile, FileAttributes.Hidden); } catch { }
        }
        catch (Exception ex) { Log("[LDTE] 云密钥记录写入失败: " + ex.Message); }
    }

    private static bool CloudRevoke(string baseUrl, string adminSecret, string id)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/revoke");
            req.Headers.Add("x-admin-secret", adminSecret);
            req.Content = new StringContent(JsonSerializer.Serialize(new { id }), Encoding.UTF8, "application/json");
            using var resp = _ldteCloudHttp.SendAsync(req).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static void StartCloudKeyJanitor()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(60000);
                    var cfg = LoadCloudCfg();
                    if (!cfg.TryGetValue("base", out var b) || !cfg.TryGetValue("adminSecret", out var sec)) continue;
                    bool online = false;
                    try
                    {
                        using var resp = _ldteCloudHttp.GetAsync(b.TrimEnd('/') + "/health").GetAwaiter().GetResult();
                        online = resp.IsSuccessStatusCode;
                    }
                    catch { online = false; }

                    if (!online) { _cloudSawOffline = true; continue; }
                    if (!_cloudSawOffline) continue;

                    List<Dictionary<string, object>> list;
                    lock (_ldteCloudLock) list = LoadCloudIssued();
                    int n = 0;
                    foreach (var x in list)
                    {
                        bool revoked = x.TryGetValue("revoked", out var rv) && rv is bool rb && rb;
                        if (revoked) continue;
                        string id = x.TryGetValue("id", out var iv) ? iv?.ToString() ?? "" : "";
                        if (id.Length == 0) continue;
                        if (CloudRevoke(b, sec, id)) { x["revoked"] = true; n++; }
                    }
                    if (n > 0) { lock (_ldteCloudLock) SaveCloudIssued(list); Log("[LDTE] 恢复联网，已自动吊销 " + n + " 个云密钥"); }
                    _cloudSawOffline = false;
                }
                catch { }
            }
        }) { IsBackground = true };
        t.Start();
    }

    private static void ServeAdminLdteCloudKey(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        var cfg = LoadCloudCfg();
        cfg.TryGetValue("base", out var baseUrl);
        cfg.TryGetValue("adminSecret", out var adminSecret);
        string b = baseUrl ?? "";
        string sec = adminSecret ?? "";

        if (req.HttpMethod == "POST")
        {
            string? body = ReadBodyOr413(ctx, 16 * 1024);
            if (body == null) return;
            int hours = 23;
            string revoke = "";
            bool hasRevoke = false;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                if (r.TryGetProperty("hours", out var hv) && hv.ValueKind == JsonValueKind.Number) hours = hv.GetInt32();
                if (r.TryGetProperty("revoke", out var rv) && rv.ValueKind == JsonValueKind.String) { revoke = rv.GetString() ?? ""; hasRevoke = true; }
            }
            catch { }

            if (b.Length == 0 || sec.Length == 0)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"未配置 cloud.json（base / adminSecret）\"}");
                return;
            }

            if (hasRevoke)
            {
                bool ok = CloudRevoke(b, sec, revoke);
                if (ok)
                {
                    lock (_ldteCloudLock)
                    {
                        var list = LoadCloudIssued();
                        foreach (var x in list)
                            if ((x.TryGetValue("id", out var iv) ? iv?.ToString() : "") == revoke) x["revoked"] = true;
                        SaveCloudIssued(list);
                    }
                }
                ServeJson(ctx, JsonSerializer.Serialize(new { ok, revoked = revoke }));
                return;
            }

            hours = Math.Clamp(hours, 1, 23);
            try
            {
                var req2 = new HttpRequestMessage(HttpMethod.Post, b.TrimEnd('/') + "/issue");
                req2.Headers.Add("x-admin-secret", sec);
                req2.Content = new StringContent(JsonSerializer.Serialize(new { hours }), Encoding.UTF8, "application/json");
                using var resp = _ldteCloudHttp.SendAsync(req2).GetAwaiter().GetResult();
                string txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = "云端签发失败", detail = txt }, JsonOpts));
                    return;
                }
                using var doc = JsonDocument.Parse(txt);
                var r = doc.RootElement;
                string id = r.TryGetProperty("id", out var iv) ? iv.GetString() ?? "" : "";
                string token = r.TryGetProperty("token", out var tv) ? tv.GetString() ?? "" : "";
                long exp = r.TryGetProperty("expires", out var ev) && ev.ValueKind == JsonValueKind.Number ? ev.GetInt64() : 0;
                lock (_ldteCloudLock)
                {
                    var list = LoadCloudIssued();
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["expires"] = exp,
                        ["revoked"] = false
                    });
                    if (list.Count > 50) list.RemoveRange(0, list.Count - 50);
                    SaveCloudIssued(list);
                }
                Log("[LDTE] 已签发云密钥: " + id + " / " + hours + " 小时");
                ServeJson(ctx, JsonSerializer.Serialize(new
                {
                    ok = true,
                    id,
                    token,
                    hours,
                    expires = exp,
                    expiresAt = exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "",
                    baseUrl = b
                }, JsonOpts));
                return;
            }
            catch (Exception ex)
            {
                ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = "无法连接云端: " + ex.Message }));
                return;
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<Dictionary<string, object>> items;
        lock (_ldteCloudLock) items = LoadCloudIssued();
        var view = items.Select(x =>
        {
            long exp = x.TryGetValue("expires", out var ev) && long.TryParse(ev?.ToString(), out long e) ? e : 0;
            bool revoked = x.TryGetValue("revoked", out var rv) && rv is bool rb && rb;
            return new
            {
                id = x.TryGetValue("id", out var iv) ? iv?.ToString() : "",
                created = x.TryGetValue("created", out var cv) ? cv?.ToString() : "",
                expiresAt = exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "",
                expired = exp > 0 && exp <= now,
                revoked,
                active = exp > now && !revoked
            };
        }).ToList();
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true,
            configured = b.Length > 0 && sec.Length > 0,
            baseUrl = b,
            items = view
        }, JsonOpts));
    }

    private static void ServeAdminLdte(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        if (req.HttpMethod == "POST")
        {
            string? body = ReadBodyOr413(ctx, 64 * 1024);
            if (body == null) return;
            int maxGB = LdteCfg.MaxGB;
            bool? single = null;
            bool? full = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                if (r.TryGetProperty("maxGB", out var g) && g.ValueKind == JsonValueKind.Number) maxGB = g.GetInt32();
                if (r.TryGetProperty("singleFile", out var s) && (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False))
                    single = s.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("fullRecord", out var f) && (f.ValueKind == JsonValueKind.True || f.ValueKind == JsonValueKind.False))
                    full = f.ValueKind == JsonValueKind.True;
            }
            catch { }
            SaveLdteCfg(maxGB, single, full);
            EnforceLdteBudget();
            Log("[LDTE] 配置已更新：上限 " + LdteCfg.MaxGB + " GB / " + (LdteCfg.SingleFile ? "单文件" : "多文件") + " / 全录制 " + (LdteCfg.FullRecord ? "开" : "关"));
        }
        int count = 0;
        try { count = Directory.GetFiles(LdteDir(), "*.ldte").Length; } catch { }
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true,
            maxGB = LdteCfg.MaxGB,
            singleFile = LdteCfg.SingleFile,
            fullRecord = LdteCfg.FullRecord,
            usedBytes = LdteUsedBytes(),
            capBytes = LdteCapBytes,
            files = count,
            clips = LoadClipIndex().Count,
            dir = LdteDir()
        }, JsonOpts));
    }

    private static string LdteDir()
    {

        string d = Path.Combine(AppContext.BaseDirectory, "LDTE训练");
        Directory.CreateDirectory(d);
        MigrateLegacyLdte(d);
        EnsureSystemIndex();
        return d;
    }

    private static bool _ldteMigrated;
    private static void MigrateLegacyLdte(string target)
    {
        if (_ldteMigrated) return;
        _ldteMigrated = true;
        try
        {
            string old = Path.Combine(DataDir, "10_LDTE训练");
            if (!Directory.Exists(old)) return;
            int n = 0;
            foreach (var f in Directory.GetFiles(old, "*.ldte"))
            {
                string dst = Path.Combine(target, Path.GetFileName(f));
                if (!File.Exists(dst)) { File.Copy(f, dst); n++; }
            }
            try { Directory.Delete(old, true); } catch (Exception) { }
            Log("[LDTE] 已把旧训练数据迁移到程序目录：" + target + "（" + n + " 个文件）");
        }
        catch (Exception ex) { Log("[LDTE] 旧训练数据迁移失败: " + ex.Message); }
    }

    private static string LdteKey(string key)
    {
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? ""));
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static string LdtePath(string key) => Path.Combine(LdteDir(), LdteKey(key) + ".ldte");

    private static List<float> ReadFloatColumn(byte[] buf, ref int off, int count)
    {
        var list = new List<float>(count);
        for (int i = 0; i < count && off + 4 <= buf.Length; i++, off += 4)
            list.Add(BitConverter.ToSingle(buf, off));
        return list;
    }

    private static Dictionary<string, object> ReadHeaderJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JObj(doc.RootElement);
    }

    private static Dictionary<string, object>? ReadLdte(string key) => ReadLdteFile(LdtePath(key));

    private static Dictionary<string, object>? ReadLdteFile(string f)
    {
        try
        {
            if (!File.Exists(f)) return null;
            byte[] buf = File.ReadAllBytes(f);
            if (buf.Length < LdteV1HeaderSize || buf[0] != (byte)'L' || buf[1] != (byte)'D' || buf[2] != (byte)'T' || buf[3] != (byte)'E')
                return null;
            int off = 4;
            ushort ver = BitConverter.ToUInt16(buf, off); off += 2;
            ushort frameMs = BitConverter.ToUInt16(buf, off); off += 2;
            uint passes = BitConverter.ToUInt32(buf, off); off += 4;
            uint frames = BitConverter.ToUInt32(buf, off); off += 4;
            uint jsonLen = BitConverter.ToUInt32(buf, off); off += 4;

            uint channelCount = 4, sampleRate = 48000, flags = 0, segCount = 0;
            if (ver >= 2)
            {
                if (buf.Length < LdteV2HeaderSize) return null;
                channelCount = BitConverter.ToUInt32(buf, off); off += 4;
                sampleRate = BitConverter.ToUInt32(buf, off); off += 4;
                flags = BitConverter.ToUInt32(buf, off); off += 4;
                segCount = BitConverter.ToUInt32(buf, off); off += 4;
                off += 12;
            }
            const uint MaxFrames = 20_000_000;
            const uint MaxChannels = 64;
            const uint MaxSegments = 1_000_000;
            const uint MaxHeaderJson = 4 * 1024 * 1024;
            if (ver == 0 || ver > 2) return null;
            if (channelCount == 0 || channelCount > MaxChannels) return null;
            if (frames > MaxFrames) return null;
            if (segCount > MaxSegments) return null;
            if (jsonLen > MaxHeaderJson) return null;
            if (jsonLen > 0 && off + (long)jsonLen > buf.Length) return null;
            if (channelCount > 0 && frames > 0 && off + (long)jsonLen + (long)channelCount * frames * 4L > buf.Length) return null;
            string headerJson = Encoding.UTF8.GetString(buf, off, (int)jsonLen);
            off += (int)jsonLen;

            var channels = new List<List<float>>();
            for (int c = 0; c < channelCount; c++)
                channels.Add(ReadFloatColumn(buf, ref off, (int)frames));

            var segs = new List<Dictionary<string, object>>();
            for (int i = 0; i < segCount && off + 16 <= buf.Length; i++)
            {
                uint s0 = BitConverter.ToUInt32(buf, off); off += 4;
                uint s1 = BitConverter.ToUInt32(buf, off); off += 4;
                float sp = BitConverter.ToSingle(buf, off); off += 4;
                float sr = BitConverter.ToSingle(buf, off); off += 4;
                segs.Add(new Dictionary<string, object>
                {
                    ["t0"] = Math.Round(s0 * frameMs / 1000.0, 2),
                    ["t1"] = Math.Round(s1 * frameMs / 1000.0, 2),
                    ["peak"] = Math.Round(sp, 4),
                    ["rms"] = Math.Round(sr, 5)
                });
            }

            var header = ReadHeaderJson(headerJson);
            return new Dictionary<string, object>
            {
                ["version"] = ver,
                ["frameMs"] = frameMs,
                ["passes"] = passes,
                ["frames"] = frames,
                ["sampleRate"] = sampleRate,
                ["channelCount"] = channelCount,
                ["flags"] = flags,
                ["header"] = header,
                ["channels"] = channels,
                ["segments"] = segs
            };
        }
        catch (Exception ex) { Log("[LDTE] 读取失败: " + ex.Message); return null; }
    }

    private static List<float> Ch(Dictionary<string, object>? d, int idx)
    {
        if (d == null || d["channels"] is not List<List<float>> ch) return new List<float>();
        return idx < ch.Count ? ch[idx] : new List<float>();
    }

    private static bool WriteLdteV2(string key, Dictionary<string, object> header, List<List<float>> channels,
        ushort frameMs, uint sampleRate, uint flags, List<float[]> segs)
    {
        string tmp = "";
        try
        {
            string f = LdtePath(key);
            tmp = f + ".tmp";
            string headerJson = JsonSerializer.Serialize(header, JsonOpts);
            byte[] json = Encoding.UTF8.GetBytes(headerJson);

            int frames = channels.Count > 0 ? channels.Max(c => c.Count) : 0;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((byte)'L'); bw.Write((byte)'D'); bw.Write((byte)'T'); bw.Write((byte)'E');
                bw.Write((ushort)2);
                bw.Write(frameMs);
                bw.Write((uint)(header.TryGetValue("passes", out var pv) && pv is List<object> pl ? pl.Count : 0));
                bw.Write((uint)frames);
                bw.Write((uint)json.Length);
                bw.Write((uint)channels.Count);
                bw.Write(sampleRate);
                bw.Write(flags);
                bw.Write((uint)segs.Count);
                bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);
                bw.Write(json);
                foreach (var col in channels)
                    for (int i = 0; i < frames; i++) bw.Write(i < col.Count ? col[i] : 0f);
                foreach (var s in segs)
                {
                    bw.Write((uint)s[0]); bw.Write((uint)s[1]); bw.Write(s[2]); bw.Write(s[3]);
                }
            }
            if (File.Exists(f))
            {
                try { File.Replace(tmp, f, null); }
                catch (IOException) { File.Copy(tmp, f, true); File.Delete(tmp); }
            }
            else File.Move(tmp, f);
            return true;
        }
        catch (Exception ex)
        {
            Log("[LDTE] 写入失败: " + ex.Message);
            try { if (tmp.Length > 0 && File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            return false;
        }
    }

    private static List<float> MergeAt(List<float> oldEnv, List<float> add, int offset)
    {
        if (add == null || add.Count == 0) return oldEnv ?? new List<float>();
        if (offset < 0) offset = 0;
        if ((long)offset + add.Count > 20_000_000) throw new ArgumentOutOfRangeException(nameof(offset), "LDTE 合并长度超出上限");
        int n = Math.Max(oldEnv?.Count ?? 0, offset + add.Count);
        var res = new List<float>(n);
        for (int i = 0; i < n; i++) res.Add(oldEnv != null && i < oldEnv.Count ? oldEnv[i] : 0f);
        for (int i = 0; i < add.Count; i++)
        {
            int j = offset + i;
            if (j >= res.Count) break;
            if (add[i] > res[j]) res[j] = add[i];
        }
        return res;
    }

    private static List<float> ResampleTo(List<float> col, int fromMs, int toMs)
    {
        if (col == null || col.Count == 0 || fromMs == toMs || fromMs <= 0 || toMs <= 0) return col;
        int n = Math.Max(1, (int)Math.Round(col.Count * (double)fromMs / toMs));
        var res = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            int j = (int)Math.Round(i * (double)toMs / fromMs);
            res.Add(col[Math.Clamp(j, 0, col.Count - 1)]);
        }
        return res;
    }

    private static Dictionary<string, object> MakeLimit(Dictionary<string, object> stats)
    {
        double maxPeak = stats.TryGetValue("maxPeak", out var mp) ? Convert.ToDouble(mp) : 0;
        double hot = stats.TryGetValue("hotRatio", out var hr) ? Convert.ToDouble(hr) : 0;
        double ceiling = maxPeak > 0.98 ? 0.86 : (maxPeak > 0.9 ? 0.9 : 0.95);
        double threshold = maxPeak > 0.98 ? -6.0 : (maxPeak > 0.9 ? -4.0 : -2.0);
        double ratio = maxPeak > 0.98 ? 12.0 : (maxPeak > 0.9 ? 8.0 : 4.0);
        if (hot > 0.01) ratio = Math.Max(ratio, 10.0);
        return new Dictionary<string, object>
        {
            ["ceiling"] = ceiling,
            ["threshold"] = threshold,
            ["ratio"] = ratio,
            ["knee"] = 6.0,
            ["attack"] = 0.003,
            ["release"] = 0.25
        };
    }

    private static double Avg(List<float> a) => a.Count == 0 ? 0 : a.Average(v => (double)v);
    private static double Max(List<float> a) => a.Count == 0 ? 0 : a.Max(v => (double)v);

    private static double Pct(List<float> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        int i = (int)Math.Round((sorted.Count - 1) * q);
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }

    private static double Db(double v) => v <= 0 ? -120 : Math.Round(20 * Math.Log10(v), 1);

    private static List<float[]> BuildPeakSegments(List<float> peak, ushort frameMs)
    {
        var segs = new List<float[]>();
        const float th = 0.9f;
        int fm = Math.Max(1, (int)frameMs);
        int gap = Math.Max(1, 160 / fm);
        int start = -1, last = -1;
        float sp = 0;
        for (int i = 0; i < peak.Count; i++)
        {
            if (peak[i] >= th)
            {
                if (start < 0) { start = i; sp = 0; }
                else if (i - last > gap)
                {
                    segs.Add(new float[] { start, last, sp, 0 });
                    start = i; sp = 0;
                }
                if (peak[i] > sp) sp = peak[i];
                last = i;
            }
        }
        if (start >= 0) segs.Add(new float[] { start, last, sp, 0 });

        int mergeGap = gap;
        while (segs.Count > 400 && mergeGap < 6000)
        {
            mergeGap *= 4;
            var merged = new List<float[]>();
            foreach (var s in segs)
            {
                if (merged.Count > 0 && s[0] - merged[^1][1] <= mergeGap)
                {
                    var m = merged[^1];
                    m[1] = s[1];
                    if (s[2] > m[2]) m[2] = s[2];
                }
                else merged.Add(new float[] { s[0], s[1], s[2], 0 });
            }
            segs = merged;
        }
        return segs;
    }

    private static (Dictionary<string, object> stats, Dictionary<string, object> profiles, List<float[]> segs)
        BuildStats(List<List<float>> ch, ushort frameMs, uint sampleRate)
    {

        int total = ch.Count == 0 ? 0 : ch.Max(c => c.Count);
        foreach (var col in ch)
            if (col.Count < total) col.AddRange(new float[total - col.Count]);

        List<float> Get(int i) => i < ch.Count ? ch[i] : new List<float>();
        var peak = Get(0); var rms = Get(1); var zcr = Get(2); var flux = Get(3);
        var bright = Get(4); var low = Get(5); var mid = Get(6); var high = Get(7);
        var width = Get(8); var corr = Get(9); var trans = Get(10);

        int n = peak.Count;
        double maxPeak = Max(peak);
        double avgRms = Avg(rms);
        double sumSq = 0;
        foreach (var r in rms) sumSq += (double)r * r;
        double meanSq = n > 0 ? sumSq / n : 0;
        double lufs = meanSq > 0 ? Math.Round(-0.691 + 10 * Math.Log10(meanSq), 1) : -70;
        double crest = avgRms > 0 ? maxPeak / avgRms : 0;
        int hotFrames = peak.Count(v => v >= 0.985f);
        int clipFrames = peak.Count(v => v >= 0.995f);
        var sortedRms = rms.Where(v => v > 0).OrderBy(v => v).ToList();
        double p05 = Pct(sortedRms, 0.05), p95 = Pct(sortedRms, 0.95);
        double dr = (p05 > 0 && p95 > 0) ? Math.Round(20 * Math.Log10(p95 / p05), 1) : 0;

        var stats = new Dictionary<string, object>
        {
            ["maxPeak"] = Math.Round(maxPeak, 4),
            ["avgRms"] = Math.Round(avgRms, 5),
            ["crest"] = Math.Round(crest, 2),
            ["hotRatio"] = Math.Round(hotFrames / (double)Math.Max(1, n), 4),
            ["maxFlux"] = Math.Round(Max(flux), 3),
            ["frames"] = n,
            ["seconds"] = Math.Round(n * frameMs / 1000.0, 1),
            ["clipFrames"] = clipFrames,
            ["lufsApprox"] = lufs,
            ["lra"] = dr
        };

        double avgLow = Avg(low), avgMid = Avg(mid), avgHigh = Avg(high);
        double tot = avgLow + avgMid + avgHigh;
        var timbre = new Dictionary<string, object>
        {
            ["brightness"] = Math.Round(Avg(bright), 4),
            ["zcr"] = Math.Round(Avg(zcr), 4),
            ["lowRatio"] = Math.Round(tot > 0 ? avgLow / tot : 0, 4),
            ["midRatio"] = Math.Round(tot > 0 ? avgMid / tot : 0, 4),
            ["highRatio"] = Math.Round(tot > 0 ? avgHigh / tot : 0, 4),
            ["tiltDb"] = Math.Round((avgLow > 0 && avgHigh > 0) ? 20 * Math.Log10(avgHigh / avgLow) : 0, 1),
            ["suggestEqTilt"] = Math.Round((avgLow > 0 && avgHigh > 0) ? Math.Clamp(20 * Math.Log10(avgLow / avgHigh) * 0.25, -4, 4) : 0, 1)
        };
        var dynamics = new Dictionary<string, object>
        {
            ["crest"] = Math.Round(crest, 2),
            ["dr"] = dr,
            ["maxFlux"] = Math.Round(Max(flux), 3),
            ["transAvg"] = Math.Round(Avg(trans), 3),
            ["suggest"] = new Dictionary<string, object>
            {
                ["threshold"] = Math.Round(-Math.Clamp(dr * 0.45, 6, 24), 1),
                ["ratio"] = crest > 14 ? 6.0 : crest > 10 ? 4.5 : 3.0,
                ["attack"] = 0.01,
                ["release"] = crest > 14 ? 0.15 : 0.25,
                ["knee"] = 6.0
            }
        };
        double wAvg = Avg(width), wMax = Max(width), cAvg = Avg(corr);
        var stereo = new Dictionary<string, object>
        {
            ["widthAvg"] = Math.Round(wAvg, 3),
            ["widthMax"] = Math.Round(wMax, 3),
            ["corrAvg"] = Math.Round(cAvg, 3),
            ["suggest"] = new Dictionary<string, object>
            {
                ["mode"] = wAvg < 0.12 ? "wide" : (wAvg > 0.4 ? "stereo" : "wide"),
                ["spread"] = Math.Round(Math.Clamp(0.5 - wAvg, 0, 0.5), 3)
            }
        };
        var decoder = new Dictionary<string, object>
        {
            ["sampleRate"] = sampleRate,
            ["clippedFrames"] = clipFrames,
            ["clipRatio"] = Math.Round(clipFrames / (double)Math.Max(1, n), 5),
            ["headroomDb"] = Math.Round(-Db(maxPeak), 1),
            ["peakSample"] = Math.Round(maxPeak, 5),
            ["suggestGainDb"] = Math.Round(Math.Clamp(-Math.Min(0, -Db(maxPeak)) - 0.5, -12, 0), 1)
        };
        var dolby = new Dictionary<string, object>
        {
            ["lfeDb"] = Db(avgLow),
            ["centerDb"] = Db(avgMid),
            ["surroundDb"] = Db(Avg(width) * Math.Max(avgMid, avgHigh)),
            ["suggest"] = new Dictionary<string, object>
            {
                ["lfeGain"] = Math.Round(Math.Clamp(avgLow > 0 ? 0.6 / Math.Max(0.05, avgLow) : 1, 0.5, 2), 2),
                ["surroundGain"] = Math.Round(Math.Clamp(0.25 / Math.Max(0.02, wAvg), 0.5, 2), 2),
                ["dialogBoost"] = Math.Round(Math.Clamp((avgMid - avgHigh) / Math.Max(0.01, avgMid) * 1.5, 0, 3), 2)
            }
        };
        var calibration = new Dictionary<string, object>
        {
            ["lufsApprox"] = lufs,
            ["lra"] = dr,
            ["headroomDb"] = Math.Round(-Db(maxPeak), 1),
            ["targetLufs"] = -14.0,
            ["suggestGainDb"] = Math.Round(Math.Clamp(-14 - lufs, -12, 12), 1)
        };

        var profiles = new Dictionary<string, object>
        {
            ["calibration"] = calibration,
            ["timbre"] = timbre,
            ["dynamics"] = dynamics,
            ["stereo"] = stereo,
            ["decoder"] = decoder,
            ["dolby"] = dolby
        };
        return (stats, profiles, BuildPeakSegments(peak, frameMs));
    }

    private static string? ReadBodyLimited(StreamReader sr, int maxChars)
    {
        var sb = new StringBuilder(Math.Min(maxChars, 1 << 20));
        var buf = new char[8192];
        int n;
        while ((n = sr.Read(buf, 0, buf.Length)) > 0)
        {
            if (sb.Length + n > maxChars) return null;
            sb.Append(buf, 0, n);
        }
        return sb.ToString();
    }

    private static void ServeLdteTrain(HttpListenerContext ctx, HttpListenerRequest req)
    {
        try
        {
            const int MaxBodyChars = 128 * 1024 * 1024;
            if (req.ContentLength64 > MaxBodyChars)
            {
                ctx.Response.StatusCode = 413;
                ServeJson(ctx, "{\"error\":\"训练数据过大\",\"error_code\":\"E_PAYLOAD_TOO_LARGE\"}");
                return;
            }
            string? body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = ReadBodyLimited(sr, MaxBodyChars);
            if (body == null)
            {
                ctx.Response.StatusCode = 413;
                ServeJson(ctx, "{\"error\":\"训练数据过大\",\"error_code\":\"E_PAYLOAD_TOO_LARGE\"}");
                return;
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string key = root.TryGetProperty("key", out var kv) ? kv.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "";
            double seconds = root.TryGetProperty("seconds", out var sv) ? sv.GetDouble() : 0;
            ushort frameMs = root.TryGetProperty("frameMs", out var fm) ? (ushort)Math.Clamp(fm.GetInt32(), 1, 1000) : (ushort)16;
            int startFrame = root.TryGetProperty("startFrame", out var sf) ? Math.Max(0, sf.GetInt32()) : 0;
            uint sampleRate = root.TryGetProperty("sampleRate", out var srv) ? (uint)srv.GetInt32() : 48000u;
            if (string.IsNullOrEmpty(key)) { ServeJson(ctx, "{\"error\":\"缺少 key\"}"); return; }

            var chans = new List<List<float>>();
            if (root.TryGetProperty("channels", out var carr) && carr.ValueKind == JsonValueKind.Array)
            {
                foreach (var col in carr.EnumerateArray())
                {
                    var list = new List<float>();
                    if (col.ValueKind == JsonValueKind.Array)
                        foreach (var v in col.EnumerateArray()) list.Add((float)v.GetDouble());
                    chans.Add(list);
                }
            }
            else if (root.TryGetProperty("frames", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {

                var p = new List<float>(); var r = new List<float>(); var z = new List<float>(); var f = new List<float>();
                foreach (var fr in arr.EnumerateArray())
                {
                    p.Add(fr.TryGetProperty("p", out var a) ? (float)a.GetDouble() : 0);
                    r.Add(fr.TryGetProperty("r", out var b) ? (float)b.GetDouble() : 0);
                    z.Add(fr.TryGetProperty("z", out var c) ? (float)c.GetDouble() : 0);
                    f.Add(fr.TryGetProperty("f", out var d) ? (float)d.GetDouble() : 0);
                }
                chans.Add(p); chans.Add(r); chans.Add(z); chans.Add(f);
            }
            if (chans.Count == 0 || chans[0].Count == 0) { ServeJson(ctx, "{\"error\":\"缺少帧数据\"}"); return; }

            if (chans[0].Count < 8)
            {
                ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, skipped = true, key }));
                return;
            }
            while (chans.Count < LdteChannels) chans.Add(new List<float>(new float[chans[0].Count]));
            if (chans.Count > LdteChannels) chans = chans.Take(LdteChannels).ToList();

            const int MaxFrames = 20_000_000;
            int maxLen = chans[0].Count;
            for (int ci = 1; ci < chans.Count; ci++) if (chans[ci].Count > maxLen) maxLen = chans[ci].Count;
            if (maxLen > MaxFrames || startFrame > MaxFrames || (long)startFrame + maxLen > MaxFrames)
            {
                ctx.Response.StatusCode = 400;
                ServeJson(ctx, "{\"ok\":false,\"error\":\"帧数/起始位置超出 LDTE 上限\"}");
                return;
            }

            lock (_ldteLock)
            {
                var old = ReadLdte(key);
                var header = old != null && old["header"] is Dictionary<string, object> h
                    ? h : new Dictionary<string, object>();
                header["key"] = key;
                header["name"] = string.IsNullOrEmpty(name) ? JStr(header, "name") : name;
                header["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                header["seconds"] = Math.Round(Math.Max(seconds, header.TryGetValue("seconds", out var os) && os != null ? Convert.ToDouble(os) : 0), 1);
                header["frameMs"] = frameMs;
                header["sampleRate"] = sampleRate;
                header["channels"] = LdteChannelNames;
                header["version"] = 2;
                if (!header.ContainsKey("passes") || header["passes"] is not List<object>) header["passes"] = new List<object>();

                int oldMs = old != null && old["frameMs"] is ushort ofm ? (int)ofm : (int)frameMs;
                var merged = new List<List<float>>();
                for (int c = 0; c < LdteChannels; c++)
                {
                    var oldCol = Ch(old, c);
                    if (oldCol.Count > 0 && oldMs != frameMs) oldCol = ResampleTo(oldCol, oldMs, frameMs);
                    merged.Add(MergeAt(oldCol, chans[c], startFrame));
                }

                var (stats, profiles, segs) = BuildStats(merged, frameMs, sampleRate);

                var passes = (List<object>)header["passes"];
                passes.Add(new Dictionary<string, object>
                {
                    ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["seconds"] = Math.Round(seconds, 1),
                    ["frames"] = chans[0].Count,
                    ["frameMs"] = frameMs,
                    ["startFrame"] = startFrame,
                    ["peak"] = stats["maxPeak"],
                    ["rms"] = stats["avgRms"],
                    ["crest"] = stats["crest"]
                });
                if (passes.Count > 60) passes.RemoveRange(0, passes.Count - 60);

                header["passes"] = passes;
                header["stats"] = stats;
                header["profiles"] = profiles;
                header["segmentCount"] = segs.Count;
                header["segments"] = segs.Take(120).Select(s => new Dictionary<string, object>
                {
                    ["t0"] = Math.Round(s[0] * frameMs / 1000.0, 2),
                    ["t1"] = Math.Round(s[1] * frameMs / 1000.0, 2),
                    ["peak"] = Math.Round(s[2], 4)
                }).ToList();

                header["limit"] = MakeLimit(stats);

                uint flags = (uint)(frameMs == 16 ? 1 : 0) | 2u | 4u;
                if (!LdteCfg.SingleFile)
                {
                    if (!WriteLdteV2(key, header, merged, frameMs, sampleRate, flags, segs))
                    {
                        ctx.Response.StatusCode = 500;
                        ServeJson(ctx, "{\"ok\":false,\"error\":\"训练数据写入失败\",\"error_code\":\"E_IO\"}");
                        return;
                    }
                }

                UpdateSystemLdte(key, JStr(header, "name"), merged, startFrame, frameMs, sampleRate,
                    stats, profiles, header["limit"] as Dictionary<string, object> ?? new(), seconds);
            }

            EnforceLdteBudget();

            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                ok = true,
                key,
                frameMs,
                frames = chans[0].Count,
                singleFile = LdteCfg.SingleFile,
                file = LdteCfg.SingleFile ? Path.GetFileName(LdtePath(SystemKey)) : Path.GetFileName(LdtePath(key))
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            Log("[LDTE] 训练失败: " + ex.Message);
            ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    private static void ServeLdteGet(HttpListenerContext ctx, string? key)
    {
        if (string.IsNullOrEmpty(key)) { ServeJson(ctx, "{\"error\":\"缺少 key\"}"); return; }
        var d = ReadLdte(key);
        if (d == null)
        {
            if (LdteCfg.SingleFile)
            {
                var sys = ReadLdte(SystemKey);
                if (sys != null && sys["header"] is Dictionary<string, object> sh
                    && sh.TryGetValue("tracks", out var trv) && trv is Dictionary<string, object> trk
                    && trk.TryGetValue(key!, out var item) && item is Dictionary<string, object> it)
                {
                    ServeJson(ctx, JsonSerializer.Serialize(new
                    {
                        learned = true,
                        key,
                        singleFile = true,
                        name = JStr(it, "name"),
                        version = sys["version"],
                        frameMs = sys["frameMs"],
                        frames = it.TryGetValue("frames", out var fr) ? fr : 0,
                        channelCount = sys["channelCount"],
                        sampleRate = sys["sampleRate"],
                        bytes = LdteUsedBytes(),
                        passes = sys["passes"],
                        passList = sh.TryGetValue("passes", out var pls) ? pls : null,
                        seconds = it.TryGetValue("seconds", out var sec1) ? sec1 : null,
                        updated = JStr(it, "updated"),
                        stats = (object)null,
                        profiles = sh.TryGetValue("profiles", out var profs) ? profs : null,
                        limit = sh.TryGetValue("limit", out var lim) ? lim : null,
                        segments = sys["segments"]
                    }, JsonOpts));
                    return;
                }
            }
            ServeJson(ctx, "{\"learned\":false}");
            return;
        }
        var header = d["header"] as Dictionary<string, object> ?? new();
        long size = 0;
        try { size = new FileInfo(LdtePath(key!)).Length; } catch (Exception) { }
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            learned = true,
            key,
            name = JStr(header, "name"),
            version = d["version"],
            frameMs = d["frameMs"],
            frames = d["frames"],
            channelCount = d["channelCount"],
            sampleRate = d["sampleRate"],
            bytes = size,
            passes = d["passes"],
            passList = header.TryGetValue("passes", out var pl) ? pl : null,
            seconds = header.TryGetValue("seconds", out var sec) ? sec : null,
            updated = JStr(header, "updated"),
            stats = header.TryGetValue("stats", out var s) ? s : null,
            profiles = header.TryGetValue("profiles", out var pr) ? pr : null,
            limit = header.TryGetValue("limit", out var l) ? l : null,
            segments = d["segments"]
        }, JsonOpts));
    }

    private static void ServeLdteList(HttpListenerContext ctx)
    {
        var list = new List<object>();
        try
        {
            foreach (var f in Directory.GetFiles(LdteDir(), "*.ldte"))
            {
                var item = new Dictionary<string, object>
                {
                    ["file"] = Path.GetFileName(f),
                    ["size"] = new FileInfo(f).Length,
                    ["updated"] = File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm:ss")
                };
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var head = new byte[LdteV2HeaderSize];
                    int got = fs.Read(head, 0, head.Length);
                    if (got >= LdteV1HeaderSize)
                    {
                        ushort fver = BitConverter.ToUInt16(head, 4);
                        item["version"] = fver;
                        item["frameMs"] = BitConverter.ToUInt16(head, 6);
                        item["passes"] = BitConverter.ToUInt32(head, 8);
                        item["frames"] = BitConverter.ToUInt32(head, 12);
                        if (fver >= 2 && got >= LdteV2HeaderSize)
                        {
                            item["channels"] = BitConverter.ToUInt32(head, 20);
                            item["sampleRate"] = BitConverter.ToUInt32(head, 24);
                            item["segments"] = BitConverter.ToUInt32(head, 32);
                        }
                    }
                }
                catch (Exception) { }
                list.Add(item);
            }
        }
        catch (Exception ex) { Log("[LDTE] 列表失败: " + ex.Message); }
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true,
            dir = LdteDir(),
            count = list.Count,
            items = list,
            singleFile = LdteCfg.SingleFile,
            maxGB = LdteCfg.MaxGB,
            usedBytes = LdteUsedBytes(),
            capBytes = LdteCapBytes
        }, JsonOpts));
    }

    private const string SystemKey = "__system__";

    private static void UpdateSystemLdte(string key, string name, List<List<float>> chans, int startFrame,
        ushort frameMs, uint sampleRate, Dictionary<string, object> stats, Dictionary<string, object> profiles,
        Dictionary<string, object> limit, double seconds)
    {
        try
        {
            if (chans.Count == 0 || chans[0].Count < 8) return;
            var old = ReadLdte(SystemKey);
            var header = old != null && old["header"] is Dictionary<string, object> h ? h : new Dictionary<string, object>();
            header["system"] = true;
            header["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            header["frameMs"] = frameMs;
            header["sampleRate"] = sampleRate;
            header["channels"] = LdteChannelNames;
            header["version"] = 2;

            var tracks = header.TryGetValue("tracks", out var tv) && tv is Dictionary<string, object> td
                ? td : new Dictionary<string, object>();
            tracks[key] = new Dictionary<string, object>
            {
                ["key"] = key,
                ["name"] = name,
                ["seconds"] = Math.Round(seconds, 1),
                ["frames"] = chans[0].Count,
                ["peak"] = stats.TryGetValue("maxPeak", out var mp) ? mp : 0,
                ["rms"] = stats.TryGetValue("avgRms", out var ar) ? ar : 0,
                ["crest"] = stats.TryGetValue("crest", out var cr) ? cr : 0,
                ["lufs"] = stats.TryGetValue("lufsApprox", out var lu) ? lu : 0,
                ["updated"] = header["updated"]
            };
            header["tracks"] = tracks;
            header["trackCount"] = tracks.Count;

            if (header.TryGetValue("limit", out var ol) && ol is Dictionary<string, object> oldLimit)
            {
                double oc = oldLimit.TryGetValue("ceiling", out var o1) ? Convert.ToDouble(o1) : 1.0;
                double nc = limit.TryGetValue("ceiling", out var n1) ? Convert.ToDouble(n1) : 1.0;
                double ot = oldLimit.TryGetValue("threshold", out var o2) ? Convert.ToDouble(o2) : 0.0;
                double nt = limit.TryGetValue("threshold", out var n2) ? Convert.ToDouble(n2) : 0.0;
                double orr = oldLimit.TryGetValue("ratio", out var o3) ? Convert.ToDouble(o3) : 0.0;
                double nr = limit.TryGetValue("ratio", out var n3) ? Convert.ToDouble(n3) : 0.0;
                limit["ceiling"] = Math.Min(oc, nc);
                limit["threshold"] = Math.Min(ot, nt);
                limit["ratio"] = Math.Max(orr, nr);
            }
            header["limit"] = limit;

            int oldMs = old != null && old["frameMs"] is ushort ofm ? (int)ofm : (int)frameMs;
            var merged = new List<List<float>>();
            for (int c = 0; c < LdteChannels; c++)
            {
                var oldCol = Ch(old, c);
                if (oldCol.Count > 0 && oldMs != frameMs) oldCol = ResampleTo(oldCol, oldMs, frameMs);
                merged.Add(MergeAt(oldCol, c < chans.Count ? chans[c] : new List<float>(), startFrame));
            }

            var (sysStats, sysProfiles, segs) = BuildStats(merged, frameMs, sampleRate);
            header["stats"] = sysStats;
            header["profiles"] = sysProfiles;
            header["segmentCount"] = segs.Count;
            header["segments"] = segs.Take(120).Select(s => new Dictionary<string, object>
            {
                ["t0"] = Math.Round(s[0] * frameMs / 1000.0, 2),
                ["t1"] = Math.Round(s[1] * frameMs / 1000.0, 2),
                ["peak"] = Math.Round(s[2], 4)
            }).ToList();

            uint flags = (uint)(frameMs == 16 ? 1 : 0) | 2u | 4u;
            WriteLdteV2(SystemKey, header, merged, frameMs, sampleRate, flags, segs);
        }
        catch (Exception ex) { Log("[LDTE] 系统合集更新失败: " + ex.Message); }
    }

    private static bool _sysChecked;
    private static void EnsureSystemIndex()
    {
        if (_sysChecked) return;
        _sysChecked = true;
        try
        {
            var sys = ReadLdte(SystemKey);
            if (sys != null && sys["header"] is Dictionary<string, object> sh
                && sh.TryGetValue("rebuilt", out var rv) && rv != null && Convert.ToInt32(rv) >= 4)
                return;

            var merged = new List<List<float>>();
            for (int c = 0; c < LdteChannels; c++) merged.Add(new List<float>());
            var tracks = new Dictionary<string, object>();
            ushort fm = 16; uint sr = 48000;
            string sysName = Path.GetFileName(LdtePath(SystemKey));

            foreach (var f in Directory.GetFiles(LdteDir(), "*.ldte"))
            {
                if (string.Equals(Path.GetFileName(f), sysName, StringComparison.OrdinalIgnoreCase)) continue;
                var d = ReadLdteFile(f);
                if (d == null) continue;
                var h = d["header"] as Dictionary<string, object> ?? new Dictionary<string, object>();
                int ofm = d["frameMs"] is ushort u ? (int)u : 120;
                int cnt = d["frames"] is uint uf ? (int)uf : 0;
                if (cnt < 8) continue;
                for (int c = 0; c < LdteChannels; c++)
                {
                    var col = Ch(d, c);
                    if (col.Count == 0) continue;
                    if (ofm != fm) col = ResampleTo(col, ofm, fm);
                    merged[c] = MergeAt(merged[c], col, 0);
                }
                string k = JStr(h, "key");
                if (string.IsNullOrEmpty(k)) k = Path.GetFileNameWithoutExtension(f);
                var st = h.TryGetValue("stats", out var sv) && sv is Dictionary<string, object> sd ? sd : null;
                tracks[k] = new Dictionary<string, object>
                {
                    ["key"] = k,
                    ["name"] = JStr(h, "name"),
                    ["seconds"] = h.TryGetValue("seconds", out var se) ? se : 0,
                    ["frames"] = cnt,
                    ["peak"] = st != null && st.TryGetValue("maxPeak", out var mp) ? mp : 0,
                    ["rms"] = st != null && st.TryGetValue("avgRms", out var ar) ? ar : 0,
                    ["legacy"] = ofm != fm
                };
            }
            if (merged[0].Count == 0) return;

            var (stats, profiles, segs) = BuildStats(merged, fm, sr);
            var header = new Dictionary<string, object>
            {
                ["system"] = true,
                ["rebuilt"] = 4,
                ["version"] = 2,
                ["frameMs"] = fm,
                ["sampleRate"] = sr,
                ["channels"] = LdteChannelNames,
                ["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["tracks"] = tracks,
                ["trackCount"] = tracks.Count,
                ["stats"] = stats,
                ["profiles"] = profiles,
                ["segmentCount"] = segs.Count,
                ["segments"] = segs.Take(120).Select(s => new Dictionary<string, object>
                {
                    ["t0"] = Math.Round(s[0] * fm / 1000.0, 2),
                    ["t1"] = Math.Round(s[1] * fm / 1000.0, 2),
                    ["peak"] = Math.Round(s[2], 4)
                }).ToList(),
                ["limit"] = MakeLimit(stats)
            };
            WriteLdteV2(SystemKey, header, merged, fm, sr, 1u | 2u | 4u, segs);
            Log("[LDTE] 系统合集已重建：" + tracks.Count + " 首 / " + merged[0].Count + " 帧 @16ms");
        }
        catch (Exception ex) { Log("[LDTE] 系统合集重建失败: " + ex.Message); }
    }

    private static string LdteClipsDir => Path.Combine(LdteDir(), "clips");
    private static string LdteClipIndexFile => Path.Combine(LdteClipsDir, "index.json");
    private static string LdteKeyFile => Path.Combine(LdteDir(), "_keys", "master.key");
    private static string LdteTempKeysFile => Path.Combine(LdteDir(), "_keys", "temp_keys.json");
    private static readonly object _ldteClipLock = new();
    private static readonly object _ldteTempKeyLock = new();

    private static List<Dictionary<string, object>> LoadTempKeys()
    {
        try
        {
            if (File.Exists(LdteTempKeysFile))
            {
                var raw = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(File.ReadAllText(LdteTempKeysFile));
                if (raw != null)
                {
                    var list = new List<Dictionary<string, object>>();
                    foreach (var d in raw)
                    {
                        var o = new Dictionary<string, object>();
                        foreach (var kv in d)
                        {
                            o[kv.Key] = kv.Value.ValueKind switch
                            {
                                JsonValueKind.String => kv.Value.GetString() ?? "",
                                JsonValueKind.Number => kv.Value.TryGetInt64(out long l) ? l : kv.Value.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => kv.Value.ToString() ?? ""
                            };
                        }
                        list.Add(o);
                    }
                    return list;
                }
            }
        }
        catch (Exception ex) { Log("[LDTE] 临时密钥读取失败: " + ex.Message); }
        return new List<Dictionary<string, object>>();
    }

    private static void SaveTempKeys(List<Dictionary<string, object>> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LdteTempKeysFile) ?? LdteDir());
            if (File.Exists(LdteTempKeysFile))
            {
                try { File.SetAttributes(LdteTempKeysFile, FileAttributes.Normal); } catch { }
            }
            File.WriteAllText(LdteTempKeysFile, JsonSerializer.Serialize(list), Encoding.UTF8);
            try { File.SetAttributes(LdteTempKeysFile, FileAttributes.Hidden); } catch { }
        }
        catch (Exception ex) { Log("[LDTE] 临时密钥写入失败: " + ex.Message); }
    }

    private static string B64Url(byte[] b)
        => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromB64Url(string s)
    {
        try
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return null;
            }
            return Convert.FromBase64String(s);
        }
        catch { return null; }
    }

    private static void ServeAdminLdteTempKey(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        if (req.HttpMethod == "POST")
        {
            string? body = ReadBodyOr413(ctx, 16 * 1024);
            if (body == null) return;
            string revoke = "";
            bool hasRevoke = false;
            int hours = 24;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                if (r.TryGetProperty("revoke", out var rv) && rv.ValueKind == JsonValueKind.String) { revoke = rv.GetString() ?? ""; hasRevoke = true; }
                if (r.TryGetProperty("hours", out var hv) && hv.ValueKind == JsonValueKind.Number) hours = hv.GetInt32();
            }
            catch { }

            var master = LdteMasterKey();
            string b64 = B64Url(master);

            lock (_ldteTempKeyLock)
            {
                var list = LoadTempKeys();
                if (hasRevoke)
                {
                    bool hit = false;
                    foreach (var x in list)
                        if ((x.TryGetValue("id", out var iv) ? iv?.ToString() : "") == revoke) { x["revoked"] = true; hit = true; }
                    if (hit) SaveTempKeys(list);
                    ServeJson(ctx, JsonSerializer.Serialize(new { ok = hit, revoked = revoke }));
                    return;
                }

                hours = Math.Clamp(hours, 1, 24);
                long exp = DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeSeconds();
                string fpRaw = b64 + "." + exp;
                string fp;
                using (var sha = SHA256.Create())
                    fp = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(fpRaw))).Substring(0, 16).ToLowerInvariant();
                string token = "BAT1." + b64 + "." + exp + "." + fp;
                string id = Guid.NewGuid().ToString("N").Substring(0, 12);
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["expires"] = exp,
                    ["hours"] = hours,
                    ["revoked"] = false
                });
                if (list.Count > 50) list.RemoveRange(0, list.Count - 50);
                SaveTempKeys(list);
                Log("[LDTE] 已生成离线临时密钥: " + id + " / " + hours + " 小时");
                ServeJson(ctx, JsonSerializer.Serialize(new
                {
                    ok = true,
                    id,
                    token,
                    hours,
                    expires = exp,
                    expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    tool = Path.Combine(AppContext.BaseDirectory, "BAwikiDecrypt.exe"),
                    toolHtml = Path.Combine(AppContext.BaseDirectory, "tools", "ldte-decrypt.html")
                }, JsonOpts));
                return;
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<Dictionary<string, object>> items;
        lock (_ldteTempKeyLock) items = LoadTempKeys();
        var view = items.Select(x =>
        {
            long exp = x.TryGetValue("expires", out var ev) && long.TryParse(ev?.ToString(), out long e) ? e : 0;
            bool rev = x.TryGetValue("revoked", out var rv) && rv is bool b && b;
            return new
            {
                id = x.TryGetValue("id", out var iv) ? iv?.ToString() : "",
                created = x.TryGetValue("created", out var cv) ? cv?.ToString() : "",
                hours = x.TryGetValue("hours", out var hv) ? hv : null,
                expiresAt = exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "",
                expired = exp > 0 && exp <= now,
                revoked = rev,
                active = exp > now && !rev
            };
        }).ToList();
        ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, items = view }, JsonOpts));
    }

    private static byte[] LdteMasterKey()
    {
        try
        {
            if (File.Exists(LdteKeyFile))
            {
                var b = File.ReadAllBytes(LdteKeyFile);
                if (b.Length == 32) return b;
            }
        }
        catch { }
        var k = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LdteKeyFile) ?? LdteDir());
            File.WriteAllBytes(LdteKeyFile, k);
            try { File.SetAttributes(LdteKeyFile, FileAttributes.Hidden); } catch { }
        }
        catch { }
        return k;
    }

    private static List<Dictionary<string, object>> LoadClipIndex()
    {
        try
        {
            if (File.Exists(LdteClipIndexFile))
            {
                var d = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(LdteClipIndexFile));
                if (d != null) return d;
            }
        }
        catch { }
        return new List<Dictionary<string, object>>();
    }

    private static void SaveClipIndex(List<Dictionary<string, object>> list)
    {
        try
        {
            Directory.CreateDirectory(LdteClipsDir);
            File.WriteAllText(LdteClipIndexFile, JsonSerializer.Serialize(list), Encoding.UTF8);
        }
        catch { }
    }

    private static bool WriteClipFile(string path, byte[] plain, out long size)
    {
        size = 0;
        try
        {
            var master = LdteMasterKey();
            var dk = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var wnonce = RandomNumberGenerator.GetBytes(12);
            var ct = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(dk, 16)) aes.Encrypt(nonce, plain, ct, tag);
            var wkey = new byte[32];
            var wtag = new byte[16];
            using (var maes = new AesGcm(master, 16)) maes.Encrypt(wnonce, dk, wkey, wtag);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            using var bw = new BinaryWriter(fs);
            bw.Write(Encoding.ASCII.GetBytes("LDTC1"));
            bw.Write((byte)1);
            bw.Write(nonce);
            bw.Write(tag);
            bw.Write(wnonce);
            bw.Write(wtag);
            bw.Write(wkey);
            bw.Write((long)plain.Length);
            bw.Write(ct);
            size = fs.Length;
            return true;
        }
        catch (Exception ex)
        {
            Log("[LDTE] 片段加密写入失败: " + ex.Message);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return false;
        }
    }

    private static byte[]? ReadClipFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            byte[] magic = br.ReadBytes(5);
            if (Encoding.ASCII.GetString(magic) != "LDTC1") return null;
            if (br.ReadByte() != 1) return null;
            var nonce = br.ReadBytes(12);
            var tag = br.ReadBytes(16);
            var wnonce = br.ReadBytes(12);
            var wtag = br.ReadBytes(16);
            var wkey = br.ReadBytes(32);
            long len = br.ReadInt64();
            if (len < 0 || len > 512L * 1024 * 1024) return null;
            long rest = fs.Length - fs.Position;
            if (rest != len) return null;
            var ct = br.ReadBytes((int)len);
            var master = LdteMasterKey();
            var dk = new byte[32];
            using (var maes = new AesGcm(master, 16)) maes.Decrypt(wnonce, wkey, wtag, dk);
            var plain = new byte[len];
            using (var aes = new AesGcm(dk, 16)) aes.Decrypt(nonce, ct, tag, plain);
            return plain;
        }
        catch (Exception ex)
        {
            Log("[LDTE] 片段解密失败: " + ex.Message);
            return null;
        }
    }

    private static void ServeLdteClipSave(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (req.HttpMethod != "POST") { ctx.Response.StatusCode = 405; ctx.Response.Close(); return; }
        try
        {
            string key = req.QueryString["key"] ?? "";
            string name = req.QueryString["name"] ?? "";
            if (string.IsNullOrEmpty(key)) { ServeJson(ctx, "{\"ok\":false,\"error\":\"缺少 key\"}"); return; }
            double start = 0, end = 0;
            double.TryParse(req.QueryString["start"], NumberStyles.Any, CultureInfo.InvariantCulture, out start);
            double.TryParse(req.QueryString["end"], NumberStyles.Any, CultureInfo.InvariantCulture, out end);
            bool full = req.QueryString["full"] == "1";
            const int MaxClip = 128 * 1024 * 1024;
            if (req.ContentLength64 > MaxClip)
            {
                ctx.Response.StatusCode = 413;
                ServeJson(ctx, "{\"ok\":false,\"error\":\"片段不能超过 128 MB\"}");
                return;
            }
            byte[] plain;
            using (var ms = new MemoryStream())
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = req.InputStream.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > MaxClip)
                    {
                        ctx.Response.StatusCode = 413;
                        ServeJson(ctx, "{\"ok\":false,\"error\":\"片段不能超过 128 MB\"}");
                        return;
                    }
                    ms.Write(buf, 0, n);
                }
                plain = ms.ToArray();
            }
            if (plain.Length < 64) { ServeJson(ctx, "{\"ok\":false,\"error\":\"片段数据为空\"}"); return; }

            string id = Guid.NewGuid().ToString("N").Substring(0, 16);
            Directory.CreateDirectory(LdteClipsDir);
            string rel = Path.Combine("clips", id + ".ldtc");
            string path = Path.Combine(LdteDir(), rel);
            if (!WriteClipFile(path, plain, out long size))
            {
                ctx.Response.StatusCode = 500;
                ServeJson(ctx, "{\"ok\":false,\"error\":\"片段加密写入失败\",\"error_code\":\"E_IO\"}");
                return;
            }
            lock (_ldteClipLock)
            {
                var list = LoadClipIndex();
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["key"] = key,
                    ["name"] = name,
                    ["start"] = Math.Round(start, 2),
                    ["end"] = Math.Round(end, 2),
                    ["full"] = full,
                    ["plainBytes"] = plain.Length,
                    ["bytes"] = size,
                    ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["file"] = rel
                });
                SaveClipIndex(list);
            }
            EnforceLdteBudget();
            Log("[LDTE] 片段已加密归档: " + (name.Length > 0 ? name : key) + " " + start.ToString("0.0") + "s-" + end.ToString("0.0") + "s " + (plain.Length / 1024) + " KB");
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, id, bytes = size, plainBytes = plain.Length, encrypted = true }, JsonOpts));
        }
        catch (Exception ex)
        {
            Log("[LDTE] 片段保存失败: " + ex.Message);
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    private static void ServeLdteClipList(HttpListenerContext ctx, string? key)
    {
        List<Dictionary<string, object>> list;
        lock (_ldteClipLock) list = LoadClipIndex();
        var items = string.IsNullOrEmpty(key) ? list : list.Where(x => x.TryGetValue("key", out var k) && (k?.ToString() ?? "") == key).ToList();
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true,
            count = items.Count,
            items,
            fullRecord = LdteCfg.FullRecord,
            singleFile = LdteCfg.SingleFile
        }, JsonOpts));
    }

    private static void ServeLdteClipGet(HttpListenerContext ctx, string? id)
    {
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        Dictionary<string, object>? item;
        lock (_ldteClipLock) item = LoadClipIndex().FirstOrDefault(x => x.TryGetValue("id", out var v) && (v?.ToString() ?? "") == id);
        if (item == null || !item.TryGetValue("file", out var rf))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }
        var data = ReadClipFile(Path.Combine(LdteDir(), rf.ToString() ?? ""));
        if (data == null)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }
        ctx.Response.ContentType = "audio/wav";
        ctx.Response.AddHeader("Cache-Control", "no-store");
        ctx.Response.ContentLength64 = data.Length;
        ctx.Response.OutputStream.Write(data, 0, data.Length);
        ctx.Response.Close();
    }

    private static void ServeLdteClipDelete(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (req.HttpMethod != "POST") { ctx.Response.StatusCode = 405; ctx.Response.Close(); return; }
        string? body = ReadBodyOr413(ctx, 64 * 1024);
        if (body == null) return;
        string id = "";
        try { using var d = JsonDocument.Parse(body); id = d.RootElement.TryGetProperty("id", out var v) ? (v.GetString() ?? "") : ""; } catch { }
        if (string.IsNullOrEmpty(id)) { ServeJson(ctx, "{\"ok\":false,\"error\":\"缺少 id\"}"); return; }
        bool removed = false;
        lock (_ldteClipLock)
        {
            var list = LoadClipIndex();
            var hit = list.FirstOrDefault(x => x.TryGetValue("id", out var v) && (v?.ToString() ?? "") == id);
            if (hit != null)
            {
                removed = true;
                list.Remove(hit);
                SaveClipIndex(list);
                try
                {
                    if (hit.TryGetValue("file", out var rf))
                        File.Delete(Path.Combine(LdteDir(), rf.ToString() ?? ""));
                }
                catch { }
            }
        }
        ServeJson(ctx, JsonSerializer.Serialize(new { ok = removed }));
    }

    private static void ServeLdteSystem(HttpListenerContext ctx)
    {
        var d = ReadLdte(SystemKey);
        if (d == null)
        {
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                learned = false,
                fullRecord = LdteCfg.FullRecord,
                singleFile = LdteCfg.SingleFile,
                maxGB = LdteCfg.MaxGB
            }, JsonOpts));
            return;
        }
        var header = d["header"] as Dictionary<string, object> ?? new Dictionary<string, object>();
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            learned = true,
            fullRecord = LdteCfg.FullRecord,
            singleFile = LdteCfg.SingleFile,
            maxGB = LdteCfg.MaxGB,
            usedBytes = LdteUsedBytes(),
            capBytes = LdteCapBytes,
            updated = JStr(header, "updated"),
            trackCount = header.TryGetValue("trackCount", out var tc) ? tc : 0,
            tracks = header.TryGetValue("tracks", out var tr) ? tr : new Dictionary<string, object>(),
            stats = header.TryGetValue("stats", out var st) ? st : null,
            profiles = header.TryGetValue("profiles", out var pr) ? pr : null,
            limit = header.TryGetValue("limit", out var li) ? li : null,
            segments = header.TryGetValue("segments", out var sg) ? sg : null,
            frames = d["frames"],
            frameMs = d["frameMs"],
            channelCount = d["channelCount"],
            sampleRate = d["sampleRate"],
            version = d["version"]
        }, JsonOpts));
    }
}
