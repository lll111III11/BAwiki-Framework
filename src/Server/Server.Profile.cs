using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BAwikiWeb;

internal static partial class Server
{
    private static readonly object _profileLock = new();
    private static Dictionary<string, Dictionary<string, object>>? _profiles;

    private static volatile bool _profSyncRunning;
    private static string _profSyncStatus = "空闲";
    private static int _profSyncTotal, _profSyncDone, _profSyncFail;
    private static string _profSyncCurrent = "";
    private static readonly List<string> _profSyncLog = new();

    private static string ProfilesDir() => Path.Combine(DataDir, "_本地维基", "index");
    private static string ProfilesFile() => Path.Combine(ProfilesDir(), "students_detail.json");
    private static string SchoolCacheFile() => Path.Combine(ProfilesDir(), "kivo_schools.json");
    private static string EquipCacheFile() => Path.Combine(ProfilesDir(), "kivo_equipments.json");
    private static string GiftCacheFile() => Path.Combine(ProfilesDir(), "kivo_gifts.json");

    private static object? JVal(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var d = new Dictionary<string, object>();
                    foreach (var p in e.EnumerateObject()) d[p.Name] = JVal(p.Value)!;
                    return d;
                }
            case JsonValueKind.Array:
                {
                    var l = new List<object>();
                    foreach (var x in e.EnumerateArray()) l.Add(JVal(x)!);
                    return l;
                }
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.Number:
                if (e.TryGetInt64(out long lv)) return lv;
                return e.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }

    private static Dictionary<string, object> JObj(JsonElement e)
    {
        var d = new Dictionary<string, object>();
        if (e.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in e.EnumerateObject()) d[p.Name] = JVal(p.Value)!;
        return d;
    }

    private static string JStr(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) ?? "" : "";

    private static List<object> JArr(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) && v is List<object> l ? l : new List<object>();

    private static Dictionary<string, Dictionary<string, object>> LoadProfiles()
    {
        lock (_profileLock)
        {
            if (_profiles != null) return _profiles;
            var map = new Dictionary<string, Dictionary<string, object>>();
            try
            {
                string f = ProfilesFile();
                if (File.Exists(f))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                        foreach (var p in items.EnumerateObject())
                            map[p.Name] = JObj(p.Value);
                }
            }
            catch (Exception ex) { Log("[资料] 读取本地档案失败: " + ex.Message); }
            _profiles = map;
            return map;
        }
    }

    private static Dictionary<string, object>? GetProfile(string id)
    {
        var map = LoadProfiles();
        return map.TryGetValue(id, out var p) ? p : null;
    }

    private static void SaveProfiles(Dictionary<string, Dictionary<string, object>> map)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir());
            var root = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["count"] = map.Count,
                ["items"] = map
            };
            string json = JsonSerializer.Serialize(root, JsonOpts);
            string tmp = ProfilesFile() + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(ProfilesFile())) File.Delete(ProfilesFile());
            File.Move(tmp, ProfilesFile());
            lock (_profileLock) { _profiles = map; }
        }
        catch (Exception ex) { Log("[资料] 保存失败: " + ex.Message); }
    }

    private static Dictionary<string, string> LoadNameCache(string file)
    {
        var map = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(file)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            foreach (var p in doc.RootElement.EnumerateObject())
                map[p.Name] = p.Value.GetString() ?? "";
        }
        catch { }
        return map;
    }

    private static void SaveNameCache(string file, Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir());
            File.WriteAllText(file, JsonSerializer.Serialize(map, JsonOpts), Encoding.UTF8);
        }
        catch (Exception ex) { Log("[资料] 写名称缓存失败: " + ex.Message); }
    }

    private static Dictionary<string, string> EnsureSchoolMap()
    {
        var map = LoadNameCache(SchoolCacheFile());
        if (map.Count > 0) return map;
        try
        {
            int page = 1, maxPage = 1;
            while (page <= maxPage)
            {
                string? json = KivoGet(KIVO_API + "/data/schools/?page=" + page);
                if (json == null) break;
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                if (data.TryGetProperty("max_page", out var mp)) maxPage = mp.GetInt32();
                if (!data.TryGetProperty("school", out var arr)) break;
                foreach (var s in arr.EnumerateArray())
                {
                    string id = s.TryGetProperty("id", out var iv) ? iv.GetRawText() : "";
                    string name = "";
                    if (s.TryGetProperty("name_cn", out var n1) && !string.IsNullOrWhiteSpace(n1.GetString())) name = n1.GetString()!;
                    else if (s.TryGetProperty("name", out var n2)) name = n2.GetString() ?? "";
                    if (!string.IsNullOrEmpty(id)) map[id] = name;
                }
                page++;
            }
            SaveNameCache(SchoolCacheFile(), map);
        }
        catch (Exception ex) { Log("[资料] 学校表失败: " + ex.Message); }
        return map;
    }

    private static Dictionary<string, string> EnsureEquipMap()
    {
        var map = LoadNameCache(EquipCacheFile());
        if (map.Count > 0) return map;
        try
        {
            int page = 1, maxPage = 1;
            while (page <= maxPage)
            {
                string? json = KivoGet(KIVO_API + "/data/equipments/?page=" + page);
                if (json == null) break;
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                if (data.TryGetProperty("max_page", out var mp)) maxPage = mp.GetInt32();
                if (!data.TryGetProperty("equipment", out var arr)) break;
                foreach (var s in arr.EnumerateArray())
                {
                    string id = s.TryGetProperty("id", out var iv) ? iv.GetRawText() : "";
                    string name = "";
                    if (s.TryGetProperty("name_cn", out var n1) && !string.IsNullOrWhiteSpace(n1.GetString())) name = n1.GetString()!;
                    else if (s.TryGetProperty("name", out var n2)) name = n2.GetString() ?? "";
                    if (!string.IsNullOrEmpty(id)) map[id] = name;
                }
                page++;
            }
            SaveNameCache(EquipCacheFile(), map);
        }
        catch (Exception ex) { Log("[资料] 装备表失败: " + ex.Message); }
        return map;
    }

    private static Dictionary<string, string> EnsureGiftNames(IEnumerable<int> ids)
    {
        var map = LoadNameCache(GiftCacheFile());
        var missing = ids.Distinct().Where(i => !map.ContainsKey(i.ToString())).ToList();
        if (missing.Count == 0) return map;
        var newOnes = new Dictionary<string, string>();
        int idx = 0;
        Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = 4 }, gid =>
        {
            try
            {
                string? json = KivoGet(KIVO_API + "/data/items/" + gid);
                if (json == null) return;
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var d)) return;
                string name = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name)) return;
                lock (newOnes) newOnes[gid.ToString()] = name;
            }
            catch { }
            finally { Interlocked.Increment(ref idx); }
        });
        foreach (var kv in newOnes) map[kv.Key] = kv.Value;
        if (newOnes.Count > 0) SaveNameCache(GiftCacheFile(), map);
        return map;
    }

    private static readonly string[] SkillKinds = { "ex", "normal", "passive", "sub" };
    private static readonly string[] SkillKindNames = { "EX技能", "普通技能", "被动技能", "副技能" };

    private static Dictionary<string, object> ExtractProfile(JsonElement d, int id,
        Dictionary<string, string> schools, Dictionary<string, string> equips,
        Dictionary<string, string> gifts, string localDir)
    {
        string S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string N(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";
        bool B(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

        string family = S("family_name_cn"); if (string.IsNullOrEmpty(family)) family = S("family_name");
        string given = S("given_name_cn"); if (string.IsNullOrEmpty(given)) given = S("given_name");
        string familyJp = S("family_name_jp"); if (string.IsNullOrEmpty(familyJp)) familyJp = S("family_name");
        string givenJp = S("given_name_jp"); if (string.IsNullOrEmpty(givenJp)) givenJp = S("given_name");

        string schoolId = N("school");
        string schoolName = schoolId != "" && schools.TryGetValue(schoolId, out var sn) ? sn : "";

        var p = new Dictionary<string, object>
        {
            ["id"] = id,
            ["skin"] = S("skin_cn"),
            ["name"] = family + given,
            ["nameJp"] = familyJp + givenJp,
            ["nameTw"] = S("family_name_zh_tw") + S("given_name_zh_tw"),
            ["nameEn"] = (S("family_name_en") + " " + S("given_name_en")).Trim(),
            ["nick"] = S("nick_name"),
            ["school"] = schoolName,
            ["schoolId"] = schoolId,
            ["grade"] = S("grade"),
            ["age"] = N("age"),
            ["height"] = N("height"),
            ["birthday"] = S("birthday"),
            ["hobby"] = S("hobby"),
            ["designer"] = S("designer"),
            ["illustrator"] = S("illustrator"),
            ["voice"] = S("character_voice"),
            ["voiceCn"] = S("character_voice_cn"),
            ["release"] = S("release_date"),
            ["releaseCn"] = S("release_date_cn"),
            ["releaseGlobal"] = S("release_date_global"),
            ["weaponType"] = S("weapon_type"),
            ["bodyShape"] = S("body_shape"),
            ["isNpc"] = B("is_npc"),
            ["intro"] = S("introduction"),
            ["introCn"] = S("introduction_cn"),
            ["signature"] = S("momo_talk_signature"),
            ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        var combat = new Dictionary<string, object>();
        Dictionary<string, object>? cd = null;
        if (d.TryGetProperty("character_datas", out var cds) && cds.ValueKind == JsonValueKind.Array)
            foreach (var x in cds.EnumerateArray())
            {
                var t = JObj(x);

                if (cd == null || (JArr(t, "basic").Count > 0 && JArr(t, "skill").Count > 0)) cd = t;
                if (JArr(t, "basic").Count > 0 && t.ContainsKey("skill")) { cd = t; break; }
            }

        var skills = new List<object>();
        var stats = new Dictionary<string, object>();
        var weaponObj = new Dictionary<string, object>();

        if (cd != null)
        {
            combat["characterId"] = JStr(cd, "character_id");
            combat["devName"] = JStr(cd, "dev_name");
            combat["type"] = JStr(cd, "type");
            combat["combatStyle"] = JStr(cd, "combat_style");
            combat["attackAttr"] = JStr(cd, "attack_attribute");
            combat["defenseAttr"] = JStr(cd, "defensive_attributes");
            combat["crowdControl"] = cd.TryGetValue("is_groupc_control", out var cc) && cc is bool cb && cb;
            combat["teamPos"] = JStr(cd, "team_position");
            combat["fieldPos"] = JStr(cd, "battlefield_position");
            combat["rarity"] = JStr(cd, "rarity");
            combat["limited"] = cd.TryGetValue("limited", out var lim) && lim is bool lb && lb;
            combat["outdoor"] = JStr(cd, "outdoor_adaptability");
            combat["indoor"] = JStr(cd, "indoor_adaptability");
            combat["street"] = JStr(cd, "street_adaptability");
            combat["favoriteEquipment"] = JStr(cd, "favorite_equipment");

            var eqList = new List<object>();
            foreach (var e in JArr(cd, "equipment"))
            {
                string eid = Convert.ToString(e) ?? "";
                eqList.Add(string.IsNullOrEmpty(eid) || eid == "0" ? "" : (equips.TryGetValue(eid, out var en) ? en : eid));
            }
            combat["equipment"] = eqList;

            if (cd.TryGetValue("skill", out var skObj) && skObj is Dictionary<string, object> sk)
            {
                foreach (var kindKey in new[] { "ex_skill", "passive_skill" })
                {
                    if (!sk.TryGetValue(kindKey, out var raw) || raw is not List<object> list) continue;
                    int i = 0;
                    foreach (var item in list)
                    {
                        if (item is not Dictionary<string, object> sj) { i++; continue; }
                        int kindIdx;
                        if (kindKey == "ex_skill") kindIdx = 0;
                        else kindIdx = Math.Min(3, 1 + i);
                        string kind = SkillKinds[kindIdx];

                        var levels = new List<object>();
                        int lv = 1;
                        foreach (var info in JArr(sj, "info"))
                        {
                            if (info is not Dictionary<string, object> ij) continue;
                            levels.Add(new Dictionary<string, object>
                            {
                                ["lv"] = lv++,
                                ["cost"] = JStr(ij, "cost"),
                                ["desc"] = JStr(ij, "describe")
                            });
                        }
                        string iconUrl = JStr(sj, "icon");
                        if (iconUrl.StartsWith("//")) iconUrl = "https:" + iconUrl;
                        string iconLocal = "";
                        if (!string.IsNullOrEmpty(localDir) && !string.IsNullOrEmpty(iconUrl))
                        {
                            string fname = kind + "_" + i + Path.GetExtension(new Uri(iconUrl).AbsolutePath);
                            string dst = Path.Combine(DataDir, "01_学生", localDir, "技能", "kivo", fname);
                            if (DownloadFile(iconUrl, dst)) { }
                            if (File.Exists(dst)) iconLocal = "技能/kivo/" + fname;
                        }
                        string preview = JStr(sj, "preview");
                        if (preview.StartsWith("//")) preview = "https:" + preview;

                        skills.Add(new Dictionary<string, object>
                        {
                            ["kind"] = kind,
                            ["kindName"] = SkillKindNames[kindIdx],
                            ["name"] = JStr(sj, "title"),
                            ["nameCn"] = JStr(sj, "title_cn"),
                            ["icon"] = iconUrl,
                            ["iconLocal"] = iconLocal,
                            ["preview"] = preview,
                            ["maxLevel"] = JStr(sj, "max_level"),
                            ["cost"] = levels.Count > 0 && levels[0] is Dictionary<string, object> l0 ? JStr(l0, "cost") : "",
                            ["levels"] = levels
                        });
                        i++;
                    }
                }
            }

            var basic = JArr(cd, "basic");
            if (basic.Count > 0 && basic[0] is Dictionary<string, object> b0)
                foreach (var kv in b0) stats[kv.Key] = kv.Value;
            else if (basic.Count > 0)
                stats["note"] = "";

            if (cd.TryGetValue("weapons", out var wObj) && wObj is Dictionary<string, object> w)
            {
                string wIcon = JStr(w, "icon");
                if (wIcon.StartsWith("//")) wIcon = "https:" + wIcon;
                weaponObj["name"] = JStr(w, "name");
                weaponObj["nameCn"] = JStr(w, "name_cn");
                weaponObj["desc"] = JStr(w, "description");
                weaponObj["descCn"] = JStr(w, "description_cn");
                weaponObj["icon"] = wIcon;
            }
        }

        p["combat"] = combat;
        p["stats"] = stats;
        p["skills"] = skills;
        p["weapon"] = weaponObj;

        var giftList = new List<object>();
        if (d.TryGetProperty("gift_data", out var gd) && gd.ValueKind == JsonValueKind.Array)
            foreach (var g in gd.EnumerateArray())
            {
                var gj = JObj(g);
                string gid = JStr(gj, "id");
                giftList.Add(new Dictionary<string, object>
                {
                    ["id"] = gid,
                    ["name"] = gifts.TryGetValue(gid, out var gn) ? gn : "",
                    ["favor"] = JStr(gj, "favorability")
                });
            }
        p["gifts"] = giftList;

        p["counts"] = new Dictionary<string, object>
        {
            ["voice"] = d.TryGetProperty("voice", out var v1) && v1.ValueKind == JsonValueKind.Array ? v1.GetArrayLength() : 0,
            ["voiceCn"] = d.TryGetProperty("voice_cn", out var v2) && v2.ValueKind == JsonValueKind.Array ? v2.GetArrayLength() : 0,
            ["voiceKr"] = d.TryGetProperty("voice_kr", out var v3) && v3.ValueKind == JsonValueKind.Array ? v3.GetArrayLength() : 0,
            ["furniture"] = d.TryGetProperty("furniture", out var f1) && f1.ValueKind == JsonValueKind.Array ? f1.GetArrayLength() : 0
        };

        return p;
    }

    private static void ServeProfileSync(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (_profSyncRunning) { ServeJson(ctx, "{\"error\":\"资料同步任务已在运行\"}"); return; }
        var ids = new List<int>();
        bool all = true;
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
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
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { error = ex.Message })); return; }

        _profSyncRunning = true;
        _profSyncStatus = "正在启动资料同步…";
        _profSyncDone = 0; _profSyncTotal = 0; _profSyncFail = 0; _profSyncCurrent = "";
        lock (_profSyncLog) _profSyncLog.Clear();
        ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, all, ids }));
        new Thread(() =>
        {
            try { RunProfileSync(ids, all); }
            catch (Exception ex) { _profSyncStatus = "失败：" + ex.Message; Log("[资料] 同步异常: " + ex.Message); }
            finally { _profSyncRunning = false; }
        }) { IsBackground = true }.Start();
    }

    private static void ProfLog(string msg)
    {
        lock (_profSyncLog)
        {
            _profSyncLog.Add(DateTime.Now.ToString("HH:mm:ss") + " " + msg);
            if (_profSyncLog.Count > 400) _profSyncLog.RemoveRange(0, _profSyncLog.Count - 400);
        }
    }

    private static void RunProfileSync(List<int> ids, bool all)
    {
        _profSyncStatus = "准备中…";
        var schools = EnsureSchoolMap();
        var equips = EnsureEquipMap();
        var local = ScanLocalStudents();

        if (all || ids.Count == 0)
        {
            _profSyncStatus = "获取远端角色列表…";
            ids = new List<int>();
            int page = 1, maxPage = 1;
            while (page <= maxPage)
            {
                string? json = KivoGet(KIVO_API + "/data/students?page=" + page);
                if (json == null) break;
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                if (data.TryGetProperty("max_page", out var mp)) maxPage = mp.GetInt32();
                foreach (var s in data.GetProperty("students").EnumerateArray())
                    if (s.TryGetProperty("id", out var iv) && iv.TryGetInt32(out int sid) && sid > 0) ids.Add(sid);
                page++;
            }
        }
        ids = ids.Distinct().ToList();
        _profSyncTotal = ids.Count;
        _profSyncStatus = "同步学生资料（0/" + _profSyncTotal + "）";
        ProfLog("待同步 " + _profSyncTotal + " 个角色");

        var giftIds = new List<int>();
        var rawMap = new System.Collections.Concurrent.ConcurrentDictionary<int, JsonElement>();
        var rawText = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        int done = 0;
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 4 }, id =>
        {
            try
            {
                string? json = KivoGet(KIVO_API + "/data/students/" + id);
                if (json == null) { Interlocked.Increment(ref _profSyncFail); return; }
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out _)) { Interlocked.Increment(ref _profSyncFail); return; }
                rawText[id] = json;
                if (doc.RootElement.GetProperty("data").TryGetProperty("gift_data", out var gd) && gd.ValueKind == JsonValueKind.Array)
                    foreach (var g in gd.EnumerateArray())
                        if (g.TryGetProperty("id", out var gidEl) && gidEl.TryGetInt32(out int gid)) { lock (giftIds) giftIds.Add(gid); }
            }
            catch { Interlocked.Increment(ref _profSyncFail); }
            finally
            {
                int d = Interlocked.Increment(ref done);
                _profSyncCurrent = "下载资料 " + d + "/" + _profSyncTotal;
                if (d % 20 == 0 || d == _profSyncTotal) _profSyncStatus = _profSyncCurrent;
            }
        });

        _profSyncStatus = "获取礼物名称…";
        var gifts = EnsureGiftNames(giftIds);
        _profSyncStatus = "写入档案…";

        var map = new Dictionary<string, Dictionary<string, object>>();
        int ok = 0;
        foreach (var kv in rawText)
        {
            try
            {
                using var doc = JsonDocument.Parse(kv.Value);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                local.TryGetValue(kv.Key, out var dir);
                var prof = ExtractProfile(data, kv.Key, schools, equips, gifts, dir ?? "");
                map[kv.Key.ToString()] = prof;
                ok++;
            }
            catch (Exception ex) { Log("[资料] 解析 " + kv.Key + " 失败: " + ex.Message); }
        }

        SaveProfiles(map);
        _profSyncDone = ok;
        _profSyncStatus = "完成：" + ok + " 条档案（失败 " + _profSyncFail + "）";
        ProfLog("同步完成，已写入 " + ok + " 条");
    }

    private static void ServeProfileStatus(HttpListenerContext ctx)
    {
        List<string> log;
        lock (_profSyncLog) log = _profSyncLog.ToList();
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            running = _profSyncRunning,
            status = _profSyncStatus,
            total = _profSyncTotal,
            done = _profSyncDone,
            fail = _profSyncFail,
            current = _profSyncCurrent,
            localCount = LoadProfiles().Count,
            log
        }, JsonOpts));
    }

    private static void ServeStudentProfile(HttpListenerContext ctx, string? id)
    {
        if (string.IsNullOrEmpty(id)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        var p = GetProfile(id);
        if (p == null) { ServeJson(ctx, "{\"error\":\"暂无该学生档案，请先在线更新\"}"); return; }
        EnrichProfile(p);
        ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, data = p }, JsonOpts));
    }

    private static readonly object _equipLock = new();
    private static Dictionary<string, Dictionary<string, object>>? _equipDetailById;
    private static Dictionary<string, Dictionary<string, object>>? _equipByName;
    private static Dictionary<string, object>? _equipRoot;
    private static Dictionary<string, object>? _itemsRoot;

    private static Dictionary<string, object>? LoadJsonFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            return JObj(doc.RootElement);
        }
        catch (Exception ex) { Log("[物品] 读取失败 " + Path.GetFileName(file) + ": " + ex.Message); return null; }
    }

    private static void EnsureEquipData()
    {
        if (_equipDetailById != null) return;
        lock (_equipLock)
        {
            if (_equipDetailById != null) return;
            var byId = new Dictionary<string, Dictionary<string, object>>();
            var byName = new Dictionary<string, Dictionary<string, object>>();
            var root = LoadJsonFile(Path.Combine(ProfilesDir(), "kivo_equipments_full.json"));
            if (root != null && root.TryGetValue("items", out var arr) && arr is List<object> list)
            {
                foreach (var x in list)
                {
                    if (x is not Dictionary<string, object> e) continue;
                    string id = JStr(e, "id");
                    if (id == "") continue;
                    byId[id] = e;
                    string cn = JStr(e, "name");
                    string jp = JStr(e, "nameJp");
                    if (cn != "") byName[cn] = e;
                    if (jp != "" && !byName.ContainsKey(jp)) byName[jp] = e;
                }
            }
            _equipRoot = root;
            _equipDetailById = byId;
            _equipByName = byName;
        }
    }

    private static Dictionary<string, object>? EnsureItemsData()
    {
        if (_itemsRoot != null) return _itemsRoot;
        lock (_equipLock)
        {
            if (_itemsRoot == null)
                _itemsRoot = LoadJsonFile(Path.Combine(ProfilesDir(), "kivo_items.json"));
        }
        return _itemsRoot;
    }

    private static void EnrichProfile(Dictionary<string, object> p)
    {
        try
        {
            EnsureEquipData();
            if (p.TryGetValue("combat", out var cv) && cv is Dictionary<string, object> combat
                && combat.TryGetValue("equipment", out var ev) && ev is List<object> list)
            {
                var outList = new List<object>();
                foreach (var x in list)
                {

                    if (x is Dictionary<string, object> done) { outList.Add(done); continue; }
                    string name = Convert.ToString(x) ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    if (_equipByName != null && _equipByName.TryGetValue(name, out var e))
                        outList.Add(new Dictionary<string, object>
                        {
                            ["id"] = JStr(e, "id"),
                            ["name"] = name,
                            ["icon"] = JStr(e, "icon"),
                            ["iconLocal"] = "equipment/eq" + JStr(e, "id") + "_1.png",
                            ["favorite"] = e.TryGetValue("isFavorite", out var fv) && fv is bool fb && fb,
                            ["tiers"] = e.TryGetValue("tiers", out var tv) && tv is List<object> tl ? tl.Count : 0
                        });
                    else
                        outList.Add(new Dictionary<string, object> { ["name"] = name });
                }
                combat["equipment"] = outList;
            }

            var items = EnsureItemsData();
            if (items != null && items.TryGetValue("items", out var iv) && iv is Dictionary<string, object> itemMap
                && p.TryGetValue("gifts", out var gv) && gv is List<object> gifts)
            {
                foreach (var g in gifts)
                {
                    if (g is not Dictionary<string, object> gd) continue;
                    string gid = JStr(gd, "id");
                    if (gid != "" && itemMap.TryGetValue(gid, out var itv) && itv is Dictionary<string, object> it)
                    {
                        gd["icon"] = JStr(it, "icon");
                        gd["iconLocal"] = JStr(it, "iconLocal");
                        gd["rarity"] = JStr(it, "rarity");
                        if (string.IsNullOrEmpty(JStr(gd, "name"))) gd["name"] = JStr(it, "name");
                    }
                }
            }
        }
        catch (Exception ex) { Log("[物品] 补全档案失败: " + ex.Message); }
    }

    private static void ServeEquipmentDetail(HttpListenerContext ctx, string? id)
    {
        EnsureEquipData();
        if (string.IsNullOrEmpty(id) || _equipDetailById == null || !_equipDetailById.TryGetValue(id, out var e))
        {
            ServeJson(ctx, "{\"error\":\"未找到该装备\"}");
            return;
        }
        ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, data = e }, JsonOpts));
    }

    private static void ServeItemsQuery(HttpListenerContext ctx, NameValueCollection q)
    {
        EnsureItemsData();
        string type = q["type"] ?? "";
        string kw = (q["q"] ?? "").Trim();
        int limit = int.TryParse(q["limit"], out int lv) ? Math.Clamp(lv, 1, 500) : 200;
        var result = new List<object>();
        if (_itemsRoot != null && _itemsRoot.TryGetValue("items", out var iv) && iv is Dictionary<string, object> map)
        {
            foreach (var kv in map)
            {
                if (kv.Value is not Dictionary<string, object> it) continue;
                if (type != "" && JStr(it, "type") != type) continue;
                if (kw != "" && !JStr(it, "name").Contains(kw, StringComparison.OrdinalIgnoreCase)
                             && !JStr(it, "description").Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new Dictionary<string, object>
                {
                    ["id"] = kv.Key,
                    ["name"] = JStr(it, "name"),
                    ["type"] = JStr(it, "type"),
                    ["rarity"] = JStr(it, "rarity"),
                    ["icon"] = JStr(it, "icon"),
                    ["iconLocal"] = JStr(it, "iconLocal"),
                    ["description"] = JStr(it, "description")
                });
                if (result.Count >= limit) break;
            }
        }
        ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, count = result.Count, items = result }, JsonOpts));
    }
}
