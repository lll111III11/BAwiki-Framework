using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BAwikiWeb;

internal static partial class Server
{
    private static readonly object _lobbyWriteLock = new();

    private class StuAudit
    {
        public int Id;
        public string Dir = "";
        public string Name = "";
        public bool Exists;
        public bool Info;
        public bool Illus;
        public bool Gallery;
        public bool Voice;
        public bool Skin;
        public bool Lobby;
        public bool Spine;
        public int IllusCount, GalleryCount, VoiceCount;
        public string Title => string.IsNullOrEmpty(Name) ? Dir : Name;
    }

    private static Dictionary<int, string> ScanLocalStudents()
    {
        var map = new Dictionary<int, string>();
        string root = Path.Combine(DataDir, "01_学生");
        if (!Directory.Exists(root)) return map;
        foreach (var d in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(d);
            string head = name.Split('_')[0];
            if (int.TryParse(head, out int id) && id > 0) map[id] = name;
        }
        return map;
    }

    private static StuAudit AuditStudent(int id, string dirName)
    {
        var a = new StuAudit { Id = id, Dir = dirName, Exists = !string.IsNullOrEmpty(dirName) };
        if (!a.Exists) return a;
        string root = Path.Combine(DataDir, "01_学生", dirName);
        try
        {
            string infoFile = Path.Combine(root, "_info.json");
            a.Info = File.Exists(infoFile);
            bool fresh = false;
            int kVoice = -1, kGallery = -1, kIllus = -1, kLobby = -1, kSpine = -1;
            if (a.Info)
            {
                try
                {
                    var info = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(infoFile, Encoding.UTF8));
                    a.Name = (Str(info, "family_name_cn", "family_name") + Str(info, "given_name_cn", "given_name")).Trim();
                    if (info.TryGetValue("_kivo", out var kvo) && kvo is JsonElement ke && ke.ValueKind == JsonValueKind.Object)
                    {
                        string at = ke.TryGetProperty("fetchedAt", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() ?? "" : "";
                        fresh = DateTime.TryParse(at, out var dt) && (DateTime.Now - dt).TotalDays < 7;
                        kVoice = ke.TryGetProperty("voice", out var x1) && x1.TryGetInt32(out int i1) ? i1 : -1;
                        kGallery = ke.TryGetProperty("gallery", out var x2) && x2.TryGetInt32(out int i2) ? i2 : -1;
                        kIllus = ke.TryGetProperty("illus", out var x3) && x3.TryGetInt32(out int i3) ? i3 : -1;
                        kLobby = ke.TryGetProperty("lobby", out var x4) && x4.TryGetInt32(out int i4) ? i4 : -1;
                        kSpine = ke.TryGetProperty("spineHome", out var x5) && x5.TryGetInt32(out int i5) ? i5 : -1;
                    }
                }
                catch { }
            }
            a.IllusCount = CountImages(Path.Combine(root, "立绘"));
            a.GalleryCount = CountImages(Path.Combine(root, "画廊"));
            a.VoiceCount = Directory.Exists(Path.Combine(root, "语音"))
                ? Directory.GetFiles(Path.Combine(root, "语音"), "*.*", SearchOption.AllDirectories)
                    .Count(f => Array.IndexOf(AudioExt, Path.GetExtension(f).ToLower()) >= 0)
                : 0;
            a.Skin = Directory.Exists(Path.Combine(root, "皮肤")) && Directory.GetFileSystemEntries(Path.Combine(root, "皮肤")).Length > 0;
            a.Illus = a.IllusCount > 0 || (fresh && kIllus == 0);
            a.Gallery = a.GalleryCount > 0 || (fresh && kGallery == 0);
            a.Voice = a.VoiceCount > 0 || (fresh && kVoice == 0);

            if (_lobbyMeta.TryGetValue(id, out var meta))
            {
                string file = JStr(meta, "lobby");
                a.Lobby = !string.IsNullOrEmpty(file) && File.Exists(Path.Combine(_lobbyDir, file));
                foreach (var tok in JStr(meta, "spine").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(tok.Trim(), out int sid)) continue;
                    if (_homeSpines.TryGetValue(sid, out var sn) && Directory.Exists(Path.Combine(_spineDir, sn)))
                    { a.Spine = true; break; }
                }
            }

            if (fresh)
            {
                if (kLobby == 0) a.Lobby = true;
                if (kSpine == 0) a.Spine = true;
            }
        }
        catch (Exception ex) { Log("[体检] " + dirName + " 失败: " + ex.Message); }
        return a;
    }

    private static int CountImages(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        return Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Count(f => Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0);
    }

    private static void ServeOnlineAudit(HttpListenerContext ctx)
    {
        try
        {
            var remote = FetchRemoteStudents();
            if (remote == null) { ServeJson(ctx, "{\"error\":\"无法获取 kivo.wiki 角色列表\"}"); return; }
            var local = ScanLocalStudents();
            var profMap = LoadProfiles();
            var items = new List<object>();
            foreach (var r in remote)
            {
                int id = r.id;
                local.TryGetValue(id, out var dir);
                var a = AuditStudent(id, dir ?? "");
                if (string.IsNullOrEmpty(a.Name)) a.Name = r.cn;
                items.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["name"] = a.Title, ["dir"] = a.Dir,
                    ["exists"] = a.Exists, ["info"] = a.Info, ["illus"] = a.Illus,
                    ["gallery"] = a.Gallery, ["voice"] = a.Voice, ["skin"] = a.Skin,
                    ["lobby"] = a.Lobby, ["spine"] = a.Spine,
                    ["profile"] = profMap.ContainsKey(id.ToString()),
                    ["illusCount"] = a.IllusCount, ["galleryCount"] = a.GalleryCount, ["voiceCount"] = a.VoiceCount
                });
            }
            int complete = items.Count(x =>
            {
                var d = (Dictionary<string, object>)x;
                return (bool)d["exists"] && (bool)d["info"] && (bool)d["illus"] && (bool)d["gallery"] && (bool)d["voice"] && (bool)d["lobby"];
            });
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                ok = true, total = items.Count, localCount = local.Count,
                completeCount = complete, incompleteCount = items.Count - complete,
                items
            }, JsonOpts));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void ServeOnlineRepair(HttpListenerContext ctx)
    {
        if (_repairRunning) { ServeJson(ctx, "{\"error\":\"已有修复任务在运行\"}"); return; }
        try
        {
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            var ids = new List<int>();
            bool all = true;
            if (!string.IsNullOrEmpty(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in arr.EnumerateArray()) if (x.TryGetInt32(out int v)) ids.Add(v);
                    all = false;
                }
                if (doc.RootElement.TryGetProperty("all", out var al) && al.ValueKind == JsonValueKind.True) all = true;
            }
            _repairRunning = true;
            _repairStatus = "正在启动修复…";
            _repairDone = 0; _repairTotal = 0;
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, ids, all }));
            new Thread(() =>
            {
                try { RunRepair(ids, all); }
                catch (Exception ex) { Log("[修复] 异常: " + ex.Message); }
                finally { _repairRunning = false; }
            }) { IsBackground = true }.Start();
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static void RunRepair(List<int> ids, bool all)
    {
        var local = ScanLocalStudents();
        var targets = new List<int>();
        if (all)
        {
            var remote = FetchRemoteStudents();
            if (remote == null) { SetRepairStatus("无法获取远端列表"); return; }
            foreach (var r in remote)
            {
                local.TryGetValue(r.id, out var dir);
                var a = AuditStudent(r.id, dir ?? "");
                if (!a.Exists || !a.Info || !a.Illus || !a.Gallery || !a.Voice || !a.Lobby || !a.Spine)
                    targets.Add(r.id);
            }
        }
        else targets = ids;
        _repairTotal = targets.Count; _repairDone = 0;
        if (targets.Count == 0) { SetRepairStatus("全部学生已完整，无需修复。"); return; }
        SetRepairStatus("需修复 " + targets.Count + " 个学生");

        int ok = 0, fail = 0;
        var dynamicIds = new List<int>();
        foreach (var id in targets)
        {
            try
            {
                SetRepairStatus($"[{id}] 正在修复…（{_repairDone + 1}/{_repairTotal}）");
                var r = FetchStudent(id);
                if (r.ContainsKey("error")) { fail++; SetRepairStatus($"[{id}] 失败：{r["error"]}"); }
                else
                {
                    ok++;
                    int lb = r.ContainsKey("lobby") ? Convert.ToInt32(r["lobby"]) : 0;
                    int sp = r.ContainsKey("spine") ? Convert.ToInt32(r["spine"]) : 0;
                    if (lb > 0 || sp > 0) dynamicIds.Add(id);
                    SetRepairStatus($"[{id}] {r["cn"]} 完成：立绘{r["illus"]}，画廊{r["gallery"]}，语音{r["voices"]}，大厅{lb}，Spine{sp}");
                }
            }
            catch (Exception ex) { fail++; SetRepairStatus($"[{id}] 异常：{ex.Message}"); }
            _repairDone++;
        }
        if (dynamicIds.Count > 0) RunLive2DWorker(dynamicIds);
        SetRepairStatus("重建索引…");
        BuildIndexes(useCache: false);
        SetRepairStatus($"修复完成：成功 {ok}，失败 {fail}。");
    }

    private class RemoteStu { public int id; public string cn = ""; public string jp = ""; }

    private static List<RemoteStu>? FetchRemoteStudents()
    {
        try
        {
            var list = new List<RemoteStu>();
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
                    list.Add(new RemoteStu
                    {
                        id = s.GetProperty("id").GetInt32(),
                        cn = (s.TryGetProperty("family_name_cn", out var fn) ? fn.GetString() : "") + (s.TryGetProperty("given_name_cn", out var gn) ? gn.GetString() : ""),
                        jp = (s.TryGetProperty("family_name_jp", out var fj) ? fj.GetString() : "") + (s.TryGetProperty("given_name_jp", out var gj) ? gj.GetString() : "")
                    });
                }
                page++;
            }
            return list.Count > 0 ? list : null;
        }
        catch (Exception ex) { Log("[远端] 获取角色列表失败: " + ex.Message); return null; }
    }

    private static void SyncLobbyForStudent(JsonElement data, int id, string cnName, StringWriter log)
        => SyncLobbyForStudent(data, id, cnName, log, out _, out _);

    private static void SyncLobbyForStudent(JsonElement data, int id, string cnName, StringWriter log, out int lobbyAdded, out int spineAdded)
    {
        lobbyAdded = 0; spineAdded = 0;
        try
        {
            string lobbyUrl = data.TryGetProperty("recollection_lobby_image", out var li) && li.ValueKind == JsonValueKind.String
                ? li.GetString() ?? "" : "";
            string lobbyFile = "";
            if (!string.IsNullOrEmpty(lobbyUrl))
            {
                string full = lobbyUrl.StartsWith("//") ? "https:" + lobbyUrl : lobbyUrl;
                string ext = Path.GetExtension(new Uri(full).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                lobbyFile = id + ext;

                foreach (var old in new[] { ".jpg", ".png", ".webp" })
                {
                    string p = Path.Combine(_lobbyDir, id + old);
                    if (old != ext && File.Exists(p)) { try { File.Delete(p); } catch { } }
                }
                if (DownloadFile(full, Path.Combine(_lobbyDir, lobbyFile))) lobbyAdded++;
            }

            var spineIds = new List<int>();
            if (data.TryGetProperty("spine", out var sp) && sp.ValueKind == JsonValueKind.Array)
                foreach (var x in sp.EnumerateArray())
                {
                    int sid = x.ValueKind == JsonValueKind.Number ? x.GetInt32() : (int.TryParse(x.GetString(), out int t) ? t : 0);
                    if (sid > 0) spineIds.Add(sid);
                }
            var modelIds = new List<int>();
            if (data.TryGetProperty("model", out var md) && md.ValueKind == JsonValueKind.Array)
                foreach (var x in md.EnumerateArray())
                {
                    int mid = x.ValueKind == JsonValueKind.Number ? x.GetInt32() : (int.TryParse(x.GetString(), out int t) ? t : 0);
                    if (mid > 0) modelIds.Add(mid);
                }

            int spineOk = EnsureSpines(spineIds, log);
            spineAdded = spineOk;

            if (spineIds.Count > 0 || !string.IsNullOrEmpty(lobbyFile))
                UpsertLobbyMeta(id, cnName, "", lobbyFile, spineIds, modelIds);

            log.Write(" 大厅:" + (string.IsNullOrEmpty(lobbyFile) ? "无图" : "图✓") + " spine:" + spineOk + "/" + spineIds.Count);
        }
        catch (Exception ex) { log.Write(" 大厅失败:" + ex.Message); }
    }

    private static int EnsureSpines(List<int> ids, StringWriter log)
    {
        int ok = 0;
        foreach (var sid in ids)
        {
            try
            {
                string name = "";
                if (_homeSpines.TryGetValue(sid, out var known)) name = known;
                string? detailJson = KivoGet(KIVO_API + "/data/spines/" + sid);
                if (detailJson == null) continue;
                using var doc = JsonDocument.Parse(detailJson);
                if (!doc.RootElement.TryGetProperty("data", out var d)) continue;
                string type = d.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                name = d.TryGetProperty("name", out var nm) ? nm.GetString() ?? name : name;
                if (string.IsNullOrEmpty(name)) continue;
                if (type != "home" && !_homeSpines.ContainsKey(sid) && !type.Contains("home")) continue;

                string dir = Path.Combine(_spineDir, name);
                Directory.CreateDirectory(dir);
                string skel = d.TryGetProperty("skel_file", out var sk) ? sk.GetString() ?? "" : "";
                string atlas = d.TryGetProperty("atlas_file", out var at) ? at.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(skel)) DownloadFile(Norm(skel), Path.Combine(dir, Path.GetFileName(new Uri(Norm(skel)).AbsolutePath)));
                if (!string.IsNullOrEmpty(atlas)) DownloadFile(Norm(atlas), Path.Combine(dir, Path.GetFileName(new Uri(Norm(atlas)).AbsolutePath)));
                var imgs = new List<string>();
                if (d.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array)
                    foreach (var i in im.EnumerateArray())
                    {
                        string u = i.GetString() ?? "";
                        if (string.IsNullOrEmpty(u)) continue;
                        u = Norm(u);
                        DownloadFile(u, Path.Combine(dir, Path.GetFileName(new Uri(u).AbsolutePath)));
                        imgs.Add(u);
                    }
                if (spineIdsHasHome(name) || type == "home")
                    UpsertHomeSpine(sid, name, skel, atlas, imgs);
                ok++;
            }
            catch (Exception ex) { log.Write(" spine" + sid + "失败:" + ex.Message); }
        }
        return ok;
    }

    private static bool spineIdsHasHome(string name)
    {
        foreach (var kv in _homeSpines) if (kv.Value == name) return true;
        return false;
    }

    private static string Norm(string u) => u.StartsWith("//") ? "https:" + u : u;

    private static void UpsertLobbyMeta(int id, string name, string skin, string lobbyFile, List<int> spineIds, List<int> modelIds)
    {
        lock (_lobbyWriteLock)
        {
            try
            {
                string path = Path.Combine(DataDir, "06_回忆大厅", "lobby_meta.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var list = new List<object>();
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        int eid = e.TryGetProperty("id", out var idv)
                            ? (idv.ValueKind == JsonValueKind.Number ? idv.GetInt32() : (int.TryParse(idv.GetString(), out int x) ? x : 0)) : 0;
                        if (eid == id) continue;
                        list.Add(JsonSerializer.Deserialize<object>(e.GetRawText())!);
                    }
                }
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["name"] = name, ["skin"] = skin,
                    ["lobby"] = lobbyFile, ["spine"] = string.Join(",", spineIds),
                    ["model"] = string.Join(",", modelIds)
                });
                list = list
                    .Select(x => (Dictionary<string, JsonElement>)JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(x))!)
                    .OrderBy(x => x["id"].GetInt32())
                    .Select(x => (object)JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(x))!)
                    .ToList();
                File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOpts), Encoding.UTF8);
            }
            catch (Exception ex) { Log("[大厅] 写入 lobby_meta 失败: " + ex.Message); }
        }
    }

    private static void UpsertHomeSpine(int id, string name, string skel, string atlas, List<string> images)
    {
        lock (_lobbyWriteLock)
        {
            try
            {
                string path = Path.Combine(DataDir, "06_回忆大厅", "home_spines.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var list = new List<Dictionary<string, object>>();
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var d = new Dictionary<string, object>();
                        foreach (var p in e.EnumerateObject())
                            d[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : (object)(p.Value.GetString() ?? "");
                        int eid = d.TryGetValue("id", out var iv) ? Convert.ToInt32(iv) : 0;
                        if (eid == id) continue;
                        list.Add(d);
                    }
                }
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["name"] = name, ["remark"] = "",
                    ["skel"] = skel, ["atlas"] = atlas,
                    ["images"] = string.Join("|", images)
                });
                list.Sort((a, b) => Convert.ToInt32(a["id"]).CompareTo(Convert.ToInt32(b["id"])));
                File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOpts), Encoding.UTF8);
            }
            catch (Exception ex) { Log("[大厅] 写入 home_spines 失败: " + ex.Message); }
        }
    }

    private static void ServeOnlineRepairStatus(HttpListenerContext ctx)
    {
        lock (_repairLock)
        {
            ServeJson(ctx, JsonSerializer.Serialize(new
            {
                running = _repairRunning, status = _repairStatus,
                done = _repairDone, total = _repairTotal
            }));
        }
    }

    private static Dictionary<string, object> AuditLive2D(int id)
    {
        var res = new Dictionary<string, object> { ["id"] = id, ["found"] = false };
        var local = ScanLocalStudents();
        if (!local.TryGetValue(id, out var dir))
        {
            res["error"] = "本地没有该学生目录";
            return res;
        }
        res["found"] = true;
        res["dir"] = dir;
        var a = AuditStudent(id, dir);
        res["lobbyImage"] = a.Lobby;
        res["spineOk"] = a.Spine;

        var modelIds = new List<string>();
        try
        {
            string infoFile = Path.Combine(DataDir, "01_学生", dir, "_info.json");
            if (File.Exists(infoFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(infoFile, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("_kivo", out var k) &&
                    k.TryGetProperty("model", out var mv) && mv.ValueKind == JsonValueKind.String)
                    modelIds.AddRange((mv.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        catch { }
        res["modelIds"] = string.Join(",", modelIds);

        var spines = new List<object>();
        if (_lobbyMeta.TryGetValue(id, out var meta))
        {
            foreach (var tok in JStr(meta, "spine").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(tok.Trim(), out int sid)) continue;
                string name = _homeSpines.TryGetValue(sid, out var sn) ? sn : "";
                string path = string.IsNullOrEmpty(name) ? "" : Path.Combine(_spineDir, name);
                int files = 0;
                bool skel = false, atlas = false;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    var all = Directory.GetFiles(path);
                    files = all.Length;
                    skel = all.Any(f => f.EndsWith(".skel", StringComparison.OrdinalIgnoreCase));
                    atlas = all.Any(f => f.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase));
                }
                spines.Add(new { id = sid, name, exists = files > 0, files, skel, atlas });
            }
        }
        res["spines"] = spines;
        return res;
    }

    private static void RunLive2DWorker(List<int> ids)
    {
        if (ids == null || ids.Count == 0) return;
        ids = ids.Distinct().Take(200).ToList();
        SetRepairStatus("发现 " + ids.Count + " 个动态立绘资源，临时拉起 AI 处理…");

        if (!_aiEnabled || string.IsNullOrEmpty(_aiKey))
        {
            foreach (var id in ids) { try { FetchStudent(id); } catch { } }
            SetRepairStatus("AI 未启用，已直接补齐动态立绘资源。");
            return;
        }

        int done = 0;
        for (int i = 0; i < ids.Count; i += 25)
        {
            var batch = ids.Skip(i).Take(25).ToList();
            string list = string.Join(",", batch);
            string prompt =
                "对以下学生执行动态立绘（Live2D / Spine）资源处理，学生 ID：" + list + "。\n" +
                "步骤：1) 对每个 ID 调用 live2d_audit 检查大厅图、Spine 目录、skel/atlas 是否齐全；" +
                "2) 缺失的调用 fetch_live2d 补齐；" +
                "3) 对补齐后的大厅图用 view_image 抽查，确认画面正常、人物完整；" +
                "4) 全部处理完后用一段话汇总成功与失败数量，不要逐个列长清单。\n" +
                "只做这些，不要重建索引，不要触发全量更新。";
            var (ok, reply, _, err) = AiCall("live2d-worker", prompt, 10);
            if (ok)
            {
                done += batch.Count;
                Log("[Live2D] AI 处理 " + batch.Count + " 个： " + Shorten(reply, 400));
            }
            else
            {
                Log("[Live2D] AI 失败，回退直接补齐: " + err);
                foreach (var id in batch) { try { FetchStudent(id); done++; } catch { } }
            }
        }
        SetRepairStatus("动态立绘处理完成：" + done + " 个。");
    }

    private static volatile bool _repairRunning;
    private static string _repairStatus = "";
    private static int _repairDone, _repairTotal;
    private static readonly object _repairLock = new();

    private static void SetRepairStatus(string s)
    {
        lock (_repairLock) { _repairStatus = s; }
        Console.WriteLine("        [修复] " + s);
    }
}
