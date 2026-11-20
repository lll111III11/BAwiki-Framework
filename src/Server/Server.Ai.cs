using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BAwikiWeb;

internal static partial class Server
{
    private const string AI_API = "https://api.deepseek.com/chat/completions";

    private static readonly object _aiLock = new();
    private static string _aiKey = "";
    private static string _aiModel = "deepseek-v4-flash";
    private static string _aiThinking = "high";
    private static string _aiWebThinking = "medium";
    private static bool _aiEnabled = true;
    private static int _aiMaxContext = 1_000_000;
    private static int _aiUserContextBytes = 512 * 1024;
    private static string _aiSessionDir = "";
    private static string _aiMemoryDir = "";
    private static string _aiConfigPath = "";
    private const string AI_CONSOLE_SESSION = "console";

    private static HttpClient? _aiHttp;

    private static HttpClient AiHttp
    {
        get
        {
            if (_aiHttp == null)
            {
                _aiHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                _aiHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _aiKey);
            }
            return _aiHttp;
        }
    }

    private const string AI_SYSTEM_PROMPT = """
你是 BAwiki（基沃托斯古书馆）本地离线版的官方智能运维助手，代号「Koyuki」。你不是通用聊天机器人，你是这套系统的常驻工程师、数据管理员和讲解员，直接面对站长本人。你的回答必须基于系统真实状态，不要凭空猜测；不确定就调用工具查，查不到就明确说查不到。

# 一、你对这套系统的认知

BAwiki Web 是一个完全本地运行的 Blue Archive 资料站，主体由三部分组成：

1. 本地服务端：C# / .NET 8 单文件程序 BAwikiWeb.exe，内置 HttpListener，默认端口 8234，由原生启动器 BAwikiLauncher.exe 拉起。服务端负责：扫描数据目录建立索引、生成缩略图、提供静态页面与媒体流、执行在线更新、处理本地音视频导入、维护音频学习库。
2. 网页前端：app\web 下的原生 HTML/CSS/JS，不使用框架。主要页面有学生图鉴、CG 画廊、留声机（音乐）、官方 PV、B 站视频、记忆大厅、3D 模型、在线更新、管理后台。
3. 数据目录：默认 E:\kivo_wiki，包含 01_学生、02_画廊、03_游戏音乐、04_官方PV、05_B站视频、06_回忆大厅、07_3D模型、08_本地导入 等编号目录，以及 _本地维基\index 索引缓存目录。

关键子目录：
- 01_学生\<ID>_<姓>_<名>\：_info.json、立绘\、画廊\<皮肤>\、皮肤\、语音\<服务器>\<类别>\、技能\
- 06_回忆大厅\：lobby_meta.json（学生 id → 大厅图 + spine id 列表）、home_spines.json（spine id → 名称与下载地址）、lobby\（大厅静态图）、spines\<名称>\（.skel/.atlas/贴图）
- _本地维基\index\：students.json、gallery.json、music.json、videos.json、_cache_meta.json、_cache_index.bin、audio-learn.json、gpu.json
- _本地维基\index\ai_sessions\ 与 ai_memory\：你的会话与长期记忆

# 二、在线更新系统的真实语义

「一键全部资源更新」（AR）不是只更新列表，而是完整补全：
1. 拉取 kivo.wiki 全量角色列表；
2. 以磁盘目录为准（不是索引）判断本地是否已有该学生，避免索引过期导致误判；
3. 对每个角色做六项体检：_info.json、立绘、画廊、语音、记忆大厅图、Spine；
4. 缺任何一项就抓取。新学生全量抓取，老学生只补缺失部分，已存在的文件自动跳过；
5. 语音按 voice（日服原声）与 voice_cn（国服配音）分别落到 语音\日服 与 语音\国服；
6. 记忆大厅同步 recollection_lobby_image 到 06_回忆大厅\lobby\<id>.<ext>，更新 lobby_meta.json；按 spine id 拉取 /data/spines/<id> 的 skel/atlas/贴图，更新 home_spines.json；
7. 最后重载大厅数据并全量重建索引。

如果用户报告「更新了列表但没有新学生」「新学生有了但大厅没更新」，先调用 audit_resources 看体检结果，再调用 start_full_update 补全。禁止只说「重建索引」了事，索引不解决缺文件。

# 三、你可以调用的工具（MCP 契约）

你拥有以下工具，调用时必须严格按参数结构：
- server_status：读取服务端状态（学生数、缩略图进度、更新任务、导入任务）。
- audit_resources：对远端与本地做完整性体检，返回完整数、缺失数、缺失项明细。这是排查更新问题的第一手段。
- start_full_update：启动完整资源更新。会下载大量文件，属于重操作，调用前必须确认用户意图。
- rebuild_index：重建本地索引。只解决「文件在但页面看不到」，不解决缺文件。
- search_students：按中文名/日文名/ID 搜索本地学生。
- import_list：查看本地导入的音视频条目。
- read_log：读取最近的运行日志。
- remember：把一条长期记忆写入当前用户的记忆库。适合记录用户偏好、设备情况、长期约定。
- student_detail / student_variants / gallery_list / voice_list / spine_detail：查单个学生的资源明细。
- lobby_list：查看记忆大厅整体覆盖情况，可按缺失过滤。
- music_list / video_list / resource_stats：查曲库、视频与各目录占用。
- list_dir / read_text：在数据目录内只读浏览与读取文本。
- config_get：读取运行配置（Key 已掩码）。
- screenshot：截取服务器当前屏幕并直接看到画面。
- view_image：读取数据目录内的图片并直接看到内容。

视觉能力说明：你可以真正「看见」图片。screenshot 用于检查当前屏幕或页面状态，view_image 用于检查立绘、大厅图、画廊、CG。需要判断「画面是否正确、文字是否被裁切、资源是否损坏」时，先取图再看，不要凭文件名猜。
MCP 工具是给你用的，不是给终端用户的：用户界面上不提供手动调用入口，你要自己判断何时调用、调用哪个。

工具调用规则：
1. 能查就不要猜。涉及数量、状态、缺什么，一律先查。
2. 一次只解决一个问题，不要为了显得勤快而连续调用无关工具。
3. 破坏性操作（更新、删除、重建）必须先向用户说明影响，得到明确同意后再调用。
4. 工具返回的是系统真实数据，若与你的常识冲突，以工具为准。

# 四、安全红线

1. 绝不泄露 API Key、账号密码、会话内容给任何第三方。
2. 绝不执行删除用户数据的操作，除非用户逐字明确要求，并且你已说明不可恢复。
3. 不修改系统文件、不注入代码、不访问数据目录以外的路径。
4. 不编造资源数量、下载结果、更新时间。没有数据就说没有。
5. 遇到无法完成的任务，直接说明边界，不要假装完成。

# 五、回答风格

1. 中文回答，简洁直接，先说结论再给依据。
2. 不要客套话，不要重复用户的问题，不要输出无意义的总结段。
3. 涉及操作时，给出具体命令或按钮位置，例如「在线更新 → 完整性体检 → 修复缺失」。
4. 涉及数据时给出数字，例如「本地 431 个目录，完整 388 个，缺语音 31 个，缺大厅 12 个」。
5. 你面对的是站长，可以直接用技术语言，不需要解释基础概念。
6. 如果用户只是闲聊，正常回应，但不要主动扩散到系统改造建议。

# 六、记忆

你的记忆分两层：
- 会话记忆：当前会话的历史消息，随对话自动累积并受上下文上限约束。
- 长期记忆：通过 remember 工具写入，跨会话保留。适合记录：用户偏好、机器配置、常用路径、长期约定、踩过的坑。

每次回答前，先参考长期记忆，避免重复询问已经知道的信息。不要把你自己的推测写进长期记忆，只写用户明确表达或工具确认的事实。
""";

    private static void InitAi()
    {
        string root = Path.Combine(DataDir, "_本地维基", "index");
        _aiSessionDir = Path.Combine(root, "ai_sessions");
        _aiMemoryDir = Path.Combine(root, "ai_memory");
        _aiConfigPath = Path.Combine(root, "ai.json");
        try
        {
            Directory.CreateDirectory(_aiSessionDir);
            Directory.CreateDirectory(_aiMemoryDir);
            if (File.Exists(_aiConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_aiConfigPath, Encoding.UTF8));
                var r = doc.RootElement;
                if (r.TryGetProperty("apiKey", out var k) && k.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(k.GetString()))
                    _aiKey = k.GetString()!;
                if (r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) _aiModel = m.GetString()!;
                if (r.TryGetProperty("thinking", out var t) && t.ValueKind == JsonValueKind.String) _aiThinking = t.GetString()!;
                if (r.TryGetProperty("webThinking", out var wt) && wt.ValueKind == JsonValueKind.String) _aiWebThinking = wt.GetString()!;
                if (r.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False) _aiEnabled = e.GetBoolean();
                if (r.TryGetProperty("maxContext", out var c) && c.TryGetInt32(out int cv)) _aiMaxContext = cv;
                if (r.TryGetProperty("userContextBytes", out var ub) && ub.TryGetInt32(out int uv)) _aiUserContextBytes = uv;
            }
        }
        catch (Exception ex) { Log("[AI] 配置载入失败: " + ex.Message); }
        Log("AI 已就绪：模型 " + _aiModel + " / 思考 " + _aiThinking + " / 上下文 " + _aiMaxContext);
    }

    private static void SaveAiConfig()
    {
        try
        {
            var obj = new Dictionary<string, object>
            {
                ["apiKey"] = _aiKey, ["model"] = _aiModel, ["thinking"] = _aiThinking,
                ["webThinking"] = _aiWebThinking, ["enabled"] = _aiEnabled, ["maxContext"] = _aiMaxContext,
                ["userContextBytes"] = _aiUserContextBytes
            };
            File.WriteAllText(_aiConfigPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch (Exception ex) { Log("[AI] 配置保存失败: " + ex.Message); }
    }

    private static string MaskKey(string k)
        => string.IsNullOrEmpty(k) ? "" : (k.Length <= 10 ? "****" : k.Substring(0, 6) + "…" + k.Substring(k.Length - 4));

    private class AiSession
    {
        public string User = "";
        public string Account = "";
        public string Source = "";
        public string Title = "";
        public string Model = "";
        public List<Dictionary<string, string>> Messages = new();
        public string Updated = "";
    }

    private static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        string r = sb.ToString();
        return string.IsNullOrEmpty(r) ? "anon" : r;
    }

    private static string SessionFile(string user) => Path.Combine(_aiSessionDir, SafeName(user) + ".json");
    private static string MemoryFile(string user) => Path.Combine(_aiMemoryDir, SafeName(user) + ".json");

    private static AiSession LoadSession(string user)
    {
        var s = new AiSession { User = user, Model = _aiModel };
        try
        {
            string f = SessionFile(user);
            if (!File.Exists(f)) { NormalizeSession(s); return s; }
            using var doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8));
            var r = doc.RootElement;
            if (r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) s.Model = m.GetString() ?? _aiModel;
            if (r.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.String) s.Updated = u.GetString() ?? "";
            if (r.TryGetProperty("account", out var ac) && ac.ValueKind == JsonValueKind.String) s.Account = ac.GetString() ?? "";
            if (r.TryGetProperty("source", out var sc) && sc.ValueKind == JsonValueKind.String) s.Source = sc.GetString() ?? "";
            if (r.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String) s.Title = ti.GetString() ?? "";
            if (r.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var x in arr.EnumerateArray())
                {
                    string role = x.TryGetProperty("role", out var ro) ? ro.GetString() ?? "user" : "user";
                    string content = x.TryGetProperty("content", out var co) ? co.GetString() ?? "" : "";
                    string time = x.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.String ? tm.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(content))
                        s.Messages.Add(new Dictionary<string, string> { ["role"] = role, ["content"] = content, ["time"] = time });
                }
        }
        catch (Exception ex) { Log("[AI] 会话读取失败: " + ex.Message); }
        NormalizeSession(s);
        return s;
    }

    private static bool IsAdminAccount(string name)
    {
        try
        {
            lock (_accLock)
            {
                var a = _accounts.Find(x => x.Name == name);
                return a != null && (a.Role == "admin" || a.Role == "super");
            }
        }
        catch { return false; }
    }

    private static string AccountRole(string name)
    {
        try
        {
            lock (_accLock)
            {
                var a = _accounts.Find(x => x.Name == name);
                return a?.Role ?? "";
            }
        }
        catch { return ""; }
    }

    private static void NormalizeSession(AiSession s)
    {
        if (string.IsNullOrEmpty(s.Source))
            s.Source = s.User == AI_CONSOLE_SESSION ? "console" : (IsAdminAccount(s.User) ? "admin" : "web");
        if (string.IsNullOrEmpty(s.Account))
            s.Account = s.User == AI_CONSOLE_SESSION ? "本机控制台" : s.User;
        if (string.IsNullOrEmpty(s.Title))
        {
            foreach (var m in s.Messages)
            {
                if (m["role"] != "user") continue;
                s.Title = Shorten(m["content"].Replace("\r", " ").Replace("\n", " "), 42);
                break;
            }
        }
    }

    private static void SaveSession(AiSession s)
    {
        try
        {
            s.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            NormalizeSession(s);
            int limit = s.User == AI_CONSOLE_SESSION ? _aiMaxContext * 2 : _aiUserContextBytes;
            TrimSession(s, limit);
            var obj = new Dictionary<string, object>
            {
                ["user"] = s.User, ["account"] = s.Account, ["source"] = s.Source, ["title"] = s.Title,
                ["model"] = s.Model, ["updated"] = s.Updated,
                ["messages"] = s.Messages
            };
            File.WriteAllText(SessionFile(s.User), JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8);
        }
        catch (Exception ex) { Log("[AI] 会话保存失败: " + ex.Message); }
    }

    private static void TrimSession(AiSession s, int limit)
    {
        bool byTokens = s.User == AI_CONSOLE_SESSION;
        while (s.Messages.Count > 2)
        {
            int size = 0;
            foreach (var m in s.Messages) size += Encoding.UTF8.GetByteCount(m["content"]);
            long cost = byTokens ? size / 3 : size;
            if (cost <= limit) break;
            s.Messages.RemoveAt(0);
        }
    }

    private static List<Dictionary<string, string>> LoadMemory(string user)
    {
        var list = new List<Dictionary<string, string>>();
        try
        {
            string f = MemoryFile(user);
            if (!File.Exists(f)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8));
            foreach (var x in doc.RootElement.EnumerateArray())
                list.Add(new Dictionary<string, string>
                {
                    ["time"] = x.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "",
                    ["text"] = x.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : ""
                });
        }
        catch { }
        return list;
    }

    private static void AddMemory(string user, string text)
    {
        try
        {
            var list = LoadMemory(user);
            list.Add(new Dictionary<string, string> { ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ["text"] = text });
            if (list.Count > 200) list = list.Skip(list.Count - 200).ToList();
            File.WriteAllText(MemoryFile(user), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch (Exception ex) { Log("[AI] 记忆写入失败: " + ex.Message); }
    }

    private static string BuildSystemPrompt(string user)
    {
        var sb = new StringBuilder(AI_SYSTEM_PROMPT);
        sb.AppendLine();
        sb.AppendLine("# 七、当前运行环境");
        sb.AppendLine("- 当前时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("- 数据目录：" + DataDir);
        sb.AppendLine("- 程序目录：" + AppDir);
        sb.AppendLine("- 学生总数：" + (_studentList?.Count ?? 0));
        sb.AppendLine("- 模型：" + _aiModel + "，思考档位：" + _aiThinking);
        sb.AppendLine("- 当前会话用户：" + user);
        var mem = LoadMemory(user);
        if (mem.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# 八、长期记忆（仅作事实参考，不要原样复述）");
            foreach (var m in mem.TakeLast(40)) sb.AppendLine("- [" + m["time"] + "] " + m["text"]);
        }
        return sb.ToString();
    }

    private static readonly JsonArray AiTools = JsonNode.Parse("""
[
 {"type":"function","function":{"name":"server_status","description":"读取服务端当前状态：学生数、缩略图进度、在线更新与导入任务状态。","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"audit_resources","description":"对比 kivo.wiki 全量角色与本地磁盘，做完整性体检。返回完整数、缺失数以及每个缺失角色的缺失项。","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"start_full_update","description":"启动完整资源更新：补全新学生、缺失语音、记忆大厅图与 Spine，最后重建索引。属于重操作。","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"rebuild_index","description":"重建本地索引。只解决文件已存在但页面看不到的问题。","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"search_students","description":"按中文名、日文名或 ID 搜索本地学生。","parameters":{"type":"object","properties":{"q":{"type":"string","description":"搜索关键词"}},"required":["q"]}}},
 {"type":"function","function":{"name":"import_list","description":"查看本地导入的音视频条目。","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"read_log","description":"读取最近的运行日志。","parameters":{"type":"object","properties":{"lines":{"type":"integer","description":"读取行数，默认 40"}}}}},
 {"type":"function","function":{"name":"remember","description":"写入一条长期记忆，跨会话保留。只记录用户明确表达或工具确认的事实。","parameters":{"type":"object","properties":{"text":{"type":"string","description":"要记住的内容"}},"required":["text"]}}},
 {"type":"function","function":{"name":"live2d_audit","description":"检查某个学生的动态资源：大厅图、Spine 目录与文件数、Live2D / 模型编号。用于确认动态立绘是否完整。","parameters":{"type":"object","properties":{"id":{"type":"integer","description":"学生 ID"}},"required":["id"]}}},
 {"type":"function","function":{"name":"fetch_live2d","description":"补齐某个学生的动态资源：重新同步大厅图、Spine 骨骼与贴图、模型编号，已存在的文件会跳过。","parameters":{"type":"object","properties":{"id":{"type":"integer","description":"学生 ID"}},"required":["id"]}}},
 {"type":"function","function":{"name":"student_detail","description":"查看单个学生的完整信息与本地资源数量（立绘、画廊、语音、皮肤、大厅、Spine）。","parameters":{"type":"object","properties":{"id":{"type":"integer","description":"学生 ID"}},"required":["id"]}}},
 {"type":"function","function":{"name":"student_variants","description":"列出某个学生的所有变体（皮肤）及其目录。","parameters":{"type":"object","properties":{"id":{"type":"integer","description":"学生 ID"}},"required":["id"]}}},
 {"type":"function","function":{"name":"gallery_list","description":"列出某个学生本地画廊的目录与文件数量。","parameters":{"type":"object","properties":{"id":{"type":"integer","description":"学生 ID"}},"required":["id"]}}},
 {"type":"function","function":{"name":"voice_list","description":"列出某个学生本地语音的服务器与分类统计。","parameters":{"type":"object","properties":{"id":{"type":"integer","description":"学生 ID"}},"required":["id"]}}},
 {"type":"function","function":{"name":"lobby_list","description":"列出记忆大厅数据中已有/缺失的学生，可按缺失过滤。","parameters":{"type":"object","properties":{"onlyMissing":{"type":"boolean","description":"只列缺失大厅或 Spine 的学生"},"limit":{"type":"integer","description":"最多返回数量，默认 50"}}}}},
 {"type":"function","function":{"name":"spine_detail","description":"查看某个学生的大厅 Spine 明细：spine id、名称、目录、文件数、skel/atlas 是否齐全。","parameters":{"type":"object","properties":{"id":{"type":"integer","description":"学生 ID"}},"required":["id"]}}},
 {"type":"function","function":{"name":"music_list","description":"列出游戏音乐目录（专辑/曲目）及数量。","parameters":{"type":"object","properties":{"q":{"type":"string","description":"可选关键词"},"limit":{"type":"integer","description":"默认 60"}}}}},
 {"type":"function","function":{"name":"video_list","description":"列出官方 PV 与 B站视频目录的文件数量。","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"resource_stats","description":"统计各资源目录的文件数与占用空间。","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"list_dir","description":"列出数据目录内某个子目录（只读，禁止越界）。","parameters":{"type":"object","properties":{"path":{"type":"string","description":"相对数据目录的路径，如 01_学生"}},"required":["path"]}}},
 {"type":"function","function":{"name":"read_text","description":"读取数据目录内的文本文件（json/txt/lrc 等，最多 200KB）。","parameters":{"type":"object","properties":{"path":{"type":"string","description":"相对数据目录的路径"},"maxBytes":{"type":"integer","description":"默认 200000"}},"required":["path"]}}},
 {"type":"function","function":{"name":"config_get","description":"读取当前 AI、GPU、端口、数据目录等运行配置（API Key 自动掩码）。","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"screenshot","description":"截取服务器当前屏幕，返回图片供你直接查看。用于视觉检查界面状态。","parameters":{"type":"object","properties":{"maxWidth":{"type":"integer","description":"缩放最大宽度，默认 1280"}}}}},
 {"type":"function","function":{"name":"view_image","description":"读取数据目录内的图片并直接查看（png/jpg/webp/bmp）。用于检查立绘、大厅图、画廊。","parameters":{"type":"object","properties":{"path":{"type":"string","description":"相对数据目录的图片路径"}},"required":["path"]}}}
]
""")!.AsArray();

    private static int ArgInt(JsonElement args, string key, int def = 0)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.TryGetInt32(out int n) ? n : def;

    private static string? SafeDataPath(string rel)
    {
        try
        {
            string root = Path.GetFullPath(DataDir).TrimEnd('\\');
            string full = Path.GetFullPath(Path.Combine(root, (rel ?? "").Replace('/', '\\')));
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return full;
            return full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    private static int SafeCountFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; } catch { return 0; }
    }

    private static (int files, long bytes) DirStat(string dir)
    {
        int files = 0; long bytes = 0;
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                files++;
                try { bytes += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return (files, bytes);
    }

    private static string ImagePayload(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (info.Length > 12 * 1024 * 1024) return "{\"error\":\"图片超过 12MB\"}";
            using var src = Image.FromFile(full);
            int maxW = 1280;
            double scale = src.Width > maxW ? (double)maxW / src.Width : 1;
            int w = Math.Max(1, (int)(src.Width * scale));
            int h = Math.Max(1, (int)(src.Height * scale));
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            var jp = new EncoderParameters(1);
            jp.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
            var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            bmp.Save(ms, enc, jp);
            string b64 = Convert.ToBase64String(ms.ToArray());
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["__image__"] = b64,
                ["__mime__"] = "image/jpeg",
                ["note"] = "图片已加载，请直接查看",
                ["width"] = w, ["height"] = h,
                ["path"] = full
            });
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = "读取图片失败: " + ex.Message }); }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    private static string CaptureScreenPayload(int maxWidth)
    {
        try
        {
            int sw = Math.Max(1, GetSystemMetrics(0));
            int sh = Math.Max(1, GetSystemMetrics(1));
            using var full = new Bitmap(sw, sh);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh));
            double scale = sw > maxWidth ? (double)maxWidth / sw : 1;
            int w = Math.Max(1, (int)(sw * scale));
            int h = Math.Max(1, (int)(sh * scale));
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            var jp = new EncoderParameters(1);
            jp.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            bmp.Save(ms, enc, jp);
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["__image__"] = Convert.ToBase64String(ms.ToArray()),
                ["__mime__"] = "image/jpeg",
                ["note"] = "服务器当前屏幕截图，请直接查看",
                ["width"] = w, ["height"] = h
            });
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = "截图失败: " + ex.Message }); }
    }

    private static string RunAiTool(string name, JsonElement args, string user)
    {
        try
        {
            switch (name)
            {
                case "server_status":
                    return JsonSerializer.Serialize(new
                    {
                        students = _studentList?.Count ?? 0,
                        thumbsDone = _thumbDone, thumbsTotal = _thumbTotal,
                        autoUpdateRunning = _autoUpdateRunning, autoUpdateStatus = _autoUpdateStatus,
                        repairRunning = _repairRunning, repairStatus = _repairStatus,
                        importDir = Path.Combine(DataDir, "08_本地导入")
                    });
                case "audit_resources":
                {
                    var remote = FetchRemoteStudents();
                    if (remote == null) return "{\"error\":\"无法获取远端角色列表\"}";
                    var local = ScanLocalStudents();
                    var missing = new List<object>();
                    int complete = 0;
                    foreach (var r in remote)
                    {
                        local.TryGetValue(r.id, out var dir);
                        var a = AuditStudent(r.id, dir ?? "");
                        var lack = new List<string>();
                        if (!a.Exists) lack.Add("目录");
                        else
                        {
                            if (!a.Info) lack.Add("信息");
                            if (!a.Illus) lack.Add("立绘");
                            if (!a.Gallery) lack.Add("画廊");
                            if (!a.Voice) lack.Add("语音");
                            if (!a.Lobby) lack.Add("大厅图");
                            if (!a.Spine) lack.Add("Spine");
                        }
                        if (lack.Count == 0) complete++;
                        else missing.Add(new { id = r.id, name = string.IsNullOrEmpty(a.Name) ? r.cn : a.Name, lack });
                    }
                    return JsonSerializer.Serialize(new
                    {
                        remote = remote.Count, local = local.Count, complete,
                        incomplete = missing.Count,
                        items = missing.Take(60)
                    }, JsonOpts);
                }
                case "start_full_update":
                    if (_autoUpdateRunning) return "{\"error\":\"已有更新任务在运行\",\"status\":\"" + _autoUpdateStatus + "\"}";
                    _autoUpdateRunning = true; _autoUpdateDone = 0; _autoUpdateTotal = 0;
                    _autoUpdateStatus = "正在启动…";
                    new Thread(() => { try { RunAutoUpdate(); } catch (Exception ex) { Log("[AR] " + ex.Message); } finally { _autoUpdateRunning = false; } })
                    { IsBackground = true }.Start();
                    return "{\"ok\":true,\"message\":\"完整资源更新已启动\"}";
                case "rebuild_index":
                    new Thread(() => { BuildIndexes(useCache: false); }) { IsBackground = true }.Start();
                    return "{\"ok\":true,\"message\":\"索引重建已在后台开始\"}";
                case "search_students":
                {
                    string q = args.TryGetProperty("q", out var qv) ? qv.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(q)) return "{\"error\":\"缺少关键词\"}";
                    var hits = new List<object>();
                    if (_studentList != null)
                        foreach (var s in _studentList)
                        {
                            string cn = (string)s["cn"], jp = (string)s["jp"], id = (string)s["id"];
                            if (cn.Contains(q) || jp.Contains(q) || id == q)
                                hits.Add(new { id, cn, jp });
                            if (hits.Count >= 30) break;
                        }
                    return JsonSerializer.Serialize(new { query = q, count = hits.Count, items = hits }, JsonOpts);
                }
                case "import_list":
                {
                    var vids = new List<string>();
                    var auds = new List<string>();
                    foreach (var sub in new[] { "视频", "音频" })
                    {
                        string d = Path.Combine(DataDir, "08_本地导入", sub);
                        if (!Directory.Exists(d)) continue;
                        foreach (var f in Directory.GetFiles(d))
                            (sub == "视频" ? vids : auds).Add(Path.GetFileName(f));
                    }
                    return JsonSerializer.Serialize(new { video = vids, audio = auds });
                }
                case "read_log":
                {
                    int lines = args.TryGetProperty("lines", out var lv) && lv.TryGetInt32(out int l) ? Math.Clamp(l, 10, 500) : 40;
                    if (!File.Exists(LogFile)) return "无日志文件";
                    var all = File.ReadAllLines(LogFile, Encoding.UTF8);
                    return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
                }
                case "remember":
                {
                    string text = args.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) return "{\"error\":\"记忆内容为空\"}";
                    AddMemory(user, text.Trim());
                    return "{\"ok\":true}";
                }
                case "live2d_audit":
                {
                    int id = args.TryGetProperty("id", out var iv) && iv.TryGetInt32(out int i) ? i : 0;
                    if (id <= 0) return "{\"error\":\"缺少学生 ID\"}";
                    return JsonSerializer.Serialize(AuditLive2D(id), JsonOpts);
                }
                case "fetch_live2d":
                {
                    int id = args.TryGetProperty("id", out var iv) && iv.TryGetInt32(out int i) ? i : 0;
                    if (id <= 0) return "{\"error\":\"缺少学生 ID\"}";
                    var r = FetchStudent(id);
                    return JsonSerializer.Serialize(new
                    {
                        ok = !r.ContainsKey("error"),
                        id,
                        lobby = r.ContainsKey("lobby") ? r["lobby"] : 0,
                        spine = r.ContainsKey("spine") ? r["spine"] : 0,
                        error = r.ContainsKey("error") ? r["error"] : ""
                    }, JsonOpts);
                }
                case "student_detail":
                {
                    int id = ArgInt(args, "id");
                    var local = ScanLocalStudents();
                    if (id <= 0 || !local.TryGetValue(id, out var dir)) return "{\"error\":\"本地没有该学生\"}";
                    var a = AuditStudent(id, dir);
                    return JsonSerializer.Serialize(new
                    {
                        id, name = a.Name, dir,
                        info = a.Info, illus = a.IllusCount, gallery = a.GalleryCount,
                        voices = a.VoiceCount, skin = a.Skin, lobby = a.Lobby, spine = a.Spine
                    }, JsonOpts);
                }
                case "student_variants":
                {
                    int id = ArgInt(args, "id");
                    var list = new List<object>();
                    string root = Path.Combine(DataDir, "01_学生");
                    if (Directory.Exists(root))
                        foreach (var d in Directory.GetDirectories(root))
                        {
                            string n = Path.GetFileName(d);
                            if (!n.StartsWith(id + "_", StringComparison.Ordinal)) continue;
                            list.Add(new { dir = n, files = SafeCountFiles(d) });
                        }
                    return JsonSerializer.Serialize(new { id, count = list.Count, items = list }, JsonOpts);
                }
                case "gallery_list":
                {
                    int id = ArgInt(args, "id");
                    var local = ScanLocalStudents();
                    if (!local.TryGetValue(id, out var dir)) return "{\"error\":\"本地没有该学生\"}";
                    string gp = Path.Combine(DataDir, "01_学生", dir, "画廊");
                    var subs = new List<object>();
                    if (Directory.Exists(gp))
                    {
                        foreach (var s in Directory.GetDirectories(gp))
                            subs.Add(new { name = Path.GetFileName(s), files = CountImages(s) });
                        foreach (var f in Directory.GetFiles(gp))
                            if (Array.IndexOf(ImgExt, Path.GetExtension(f).ToLower()) >= 0)
                                subs.Add(new { name = Path.GetFileName(f), files = 1 });
                    }
                    return JsonSerializer.Serialize(new { id, dir, count = subs.Count, items = subs }, JsonOpts);
                }
                case "voice_list":
                {
                    int id = ArgInt(args, "id");
                    var local = ScanLocalStudents();
                    if (!local.TryGetValue(id, out var dir)) return "{\"error\":\"本地没有该学生\"}";
                    string vp = Path.Combine(DataDir, "01_学生", dir, "语音");
                    var servers = new List<object>();
                    if (Directory.Exists(vp))
                        foreach (var s in Directory.GetDirectories(vp))
                        {
                            var cats = new List<object>();
                            foreach (var c in Directory.GetDirectories(s))
                            {
                                int n = Directory.GetFiles(c, "*.*", SearchOption.AllDirectories)
                                    .Count(f => Array.IndexOf(AudioExt, Path.GetExtension(f).ToLower()) >= 0);
                                if (n > 0) cats.Add(new { name = Path.GetFileName(c), files = n });
                            }
                            servers.Add(new { name = Path.GetFileName(s), total = Directory.GetFiles(s, "*.*", SearchOption.AllDirectories).Length, categories = cats });
                        }
                    return JsonSerializer.Serialize(new { id, dir, servers }, JsonOpts);
                }
                case "lobby_list":
                {
                    bool onlyMissing = args.TryGetProperty("onlyMissing", out var om) && om.ValueKind == JsonValueKind.True;
                    int limit = Math.Clamp(ArgInt(args, "limit", 50), 1, 200);
                    var rows = new List<object>();
                    int total = 0, missing = 0;
                    foreach (var kv in _lobbyMeta)
                    {
                        total++;
                        string file = JStr(kv.Value, "lobby");
                        bool img = !string.IsNullOrEmpty(file) && File.Exists(Path.Combine(_lobbyDir, file));
                        bool spine = false;
                        foreach (var tok in JStr(kv.Value, "spine").Split(',', StringSplitOptions.RemoveEmptyEntries))
                            if (int.TryParse(tok.Trim(), out int sid) && _homeSpines.TryGetValue(sid, out var sn) && Directory.Exists(Path.Combine(_spineDir, sn)))
                            { spine = true; break; }
                        if (!img || !spine) missing++;
                        if (onlyMissing && img && spine) continue;
                        if (rows.Count < limit)
                            rows.Add(new { id = kv.Key, name = JStr(kv.Value, "name"), lobbyImage = img, spine });
                    }
                    return JsonSerializer.Serialize(new { total, missing, items = rows }, JsonOpts);
                }
                case "spine_detail":
                {
                    int id = ArgInt(args, "id");
                    if (id <= 0) return "{\"error\":\"缺少学生 ID\"}";
                    return JsonSerializer.Serialize(AuditLive2D(id), JsonOpts);
                }
                case "music_list":
                {
                    string q = args.TryGetProperty("q", out var qv) ? qv.GetString() ?? "" : "";
                    int limit = Math.Clamp(ArgInt(args, "limit", 60), 1, 300);
                    string root = Path.Combine(DataDir, "03_游戏音乐");
                    var items = new List<object>();
                    if (Directory.Exists(root))
                        foreach (var d in Directory.GetDirectories(root).OrderBy(x => x))
                        {
                            string n = Path.GetFileName(d);
                            if (!string.IsNullOrEmpty(q) && n.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            int files = Directory.GetFiles(d, "*.*", SearchOption.TopDirectoryOnly)
                                .Count(f => Array.IndexOf(AudioExt, Path.GetExtension(f).ToLower()) >= 0);
                            items.Add(new { name = n, audio = files });
                            if (items.Count >= limit) break;
                        }
                    return JsonSerializer.Serialize(new { count = items.Count, items }, JsonOpts);
                }
                case "video_list":
                {
                    object Count(string sub)
                    {
                        string p = Path.Combine(DataDir, sub);
                        if (!Directory.Exists(p)) return new { dir = sub, files = 0 };
                        return new { dir = sub, files = Directory.GetFiles(p, "*.*", SearchOption.AllDirectories).Length };
                    }
                    return JsonSerializer.Serialize(new { items = new[] { Count("04_官方PV"), Count("05_B站视频") } }, JsonOpts);
                }
                case "resource_stats":
                {
                    var rows = new List<object>();
                    long totalBytes = 0;
                    foreach (var d in Directory.GetDirectories(DataDir).OrderBy(x => x))
                    {
                        string n = Path.GetFileName(d);
                        if (n.StartsWith("_")) continue;
                        var (files, bytes) = DirStat(d);
                        totalBytes += bytes;
                        rows.Add(new { name = n, files, mb = Math.Round(bytes / 1048576.0, 1) });
                    }
                    return JsonSerializer.Serialize(new { totalMb = Math.Round(totalBytes / 1048576.0, 1), items = rows }, JsonOpts);
                }
                case "list_dir":
                {
                    string rel = args.TryGetProperty("path", out var pv) ? pv.GetString() ?? "" : "";
                    string? full = SafeDataPath(rel);
                    if (full == null || !Directory.Exists(full)) return "{\"error\":\"目录不存在或越界\"}";
                    var items = new List<object>();
                    foreach (var d in Directory.GetDirectories(full).Take(200)) items.Add(new { name = Path.GetFileName(d), type = "dir" });
                    foreach (var f in Directory.GetFiles(full).Take(200)) items.Add(new { name = Path.GetFileName(f), type = "file", size = new FileInfo(f).Length });
                    return JsonSerializer.Serialize(new { path = rel, count = items.Count, items }, JsonOpts);
                }
                case "read_text":
                {
                    string rel = args.TryGetProperty("path", out var pv) ? pv.GetString() ?? "" : "";
                    int max = Math.Clamp(ArgInt(args, "maxBytes", 200000), 1024, 400000);
                    string? full = SafeDataPath(rel);
                    if (full == null || !File.Exists(full)) return "{\"error\":\"文件不存在或越界\"}";
                    string ext = Path.GetExtension(full).ToLower();
                    if (Array.IndexOf(new[] { ".json", ".txt", ".lrc", ".md", ".ini", ".log", ".csv", ".xml" }, ext) < 0)
                        return "{\"error\":\"只允许读取文本文件\"}";
                    var info = new FileInfo(full);
                    if (info.Length > max) return JsonSerializer.Serialize(new { error = "文件过大", size = info.Length });
                    return File.ReadAllText(full, Encoding.UTF8);
                }
                case "config_get":
                    return JsonSerializer.Serialize(new
                    {
                        ai = new { model = _aiModel, thinking = _aiThinking, webThinking = _aiWebThinking, maxContext = _aiMaxContext, userContextBytes = _aiUserContextBytes, key = MaskKey(_aiKey) },
                        dataDir = DataDir, appDir = AppDir, port = _localPort,
                        students = _studentList?.Count ?? 0
                    }, JsonOpts);
                case "screenshot":
                {
                    int maxW = Math.Clamp(ArgInt(args, "maxWidth", 1280), 320, 2560);
                    return CaptureScreenPayload(maxW);
                }
                case "view_image":
                {
                    string rel = args.TryGetProperty("path", out var pv) ? pv.GetString() ?? "" : "";
                    string? full = SafeDataPath(rel);
                    if (full == null || !File.Exists(full)) return "{\"error\":\"图片不存在或越界\"}";
                    string ext = Path.GetExtension(full).ToLower();
                    if (Array.IndexOf(new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" }, ext) < 0)
                        return "{\"error\":\"不是支持的图片格式\"}";
                    return ImagePayload(full);
                }
            }
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
        return JsonSerializer.Serialize(new { error = "未知工具: " + name });
    }

    private static (bool ok, string reply, string reasoning, string error) AiCall(string user, string message, int maxRounds, string source = "")
    {
        lock (_aiLock)
        {
            if (!_aiEnabled) return (false, "", "", "AI 已关闭。在控制台输入 AI ON 开启。");
            if (string.IsNullOrWhiteSpace(_aiKey)) return (false, "", "", "未配置 API Key。在控制台输入 AI KEY <key>。");

            var session = LoadSession(user);
            if (!string.IsNullOrEmpty(source)) session.Source = source;
            session.Messages.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = message, ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });

            string lastReasoning = "";
            string think = user == AI_CONSOLE_SESSION ? _aiThinking : _aiWebThinking;
            string useModel = think == "off" ? _aiModel.Replace("reasoner", "chat") : _aiModel;
            var traces = new List<object>();
            var visuals = new List<JsonObject>();
            try
            {
                for (int round = 0; round < maxRounds; round++)
                {
                    var payload = new JsonObject
                    {
                        ["model"] = useModel,
                        ["stream"] = false,
                        ["messages"] = BuildMessages(user, session, visuals)
                    };
                    payload["tools"] = JsonNode.Parse(AiTools.ToJsonString());
                    payload["tool_choice"] = "auto";

                    var req = new HttpRequestMessage(HttpMethod.Post, AI_API)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                    };
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _aiKey);
                    var resp = AiHttp.SendAsync(req).GetAwaiter().GetResult();
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!resp.IsSuccessStatusCode && body.Contains("tool"))
                    {
                        payload.Remove("tools");
                        payload.Remove("tool_choice");
                        req = new HttpRequestMessage(HttpMethod.Post, AI_API)
                        {
                            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                        };
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _aiKey);
                        resp = AiHttp.SendAsync(req).GetAwaiter().GetResult();
                        body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                    if (!resp.IsSuccessStatusCode)
                        return (false, "", lastReasoning, "DeepSeek 返回 " + (int)resp.StatusCode + "：" + Shorten(body, 300));

                    using var doc = JsonDocument.Parse(body);
                    var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
                    string content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                    string reasoning = msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(reasoning)) lastReasoning = reasoning;

                    bool hasTools = msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0;
                    if (!hasTools)
                    {
                        session.Messages.Add(new Dictionary<string, string> { ["role"] = "assistant", ["content"] = content, ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
                        SaveSession(session);
                        _aiLastTraces = traces;
                        return (true, content, lastReasoning, "");
                    }

                    var asst = new JsonObject { ["role"] = "assistant", ["content"] = content ?? "" };
                    asst["tool_calls"] = JsonNode.Parse(tcs.GetRawText());
                    session.Messages.Add(new Dictionary<string, string> { ["role"] = "assistant_json", ["content"] = asst.ToJsonString() });

                    foreach (var tc in tcs.EnumerateArray())
                    {
                        string tname = tc.GetProperty("function").GetProperty("name").GetString() ?? "";
                        string targsRaw = tc.GetProperty("function").TryGetProperty("arguments", out var ag) ? ag.GetString() ?? "{}" : "{}";
                        string tcallId = tc.TryGetProperty("id", out var idv) ? idv.GetString() ?? "" : "";
                        JsonElement argsEl = default;
                        try { using var ad = JsonDocument.Parse(targsRaw); argsEl = ad.RootElement.Clone(); } catch { argsEl = JsonDocument.Parse("{}").RootElement.Clone(); }
                        string result = RunAiTool(tname, argsEl, user);
                        traces.Add(new { name = tname, args = Shorten(targsRaw, 300), result = Shorten(result, 400) });
                        session.Messages.Add(new Dictionary<string, string>
                        {
                            ["role"] = "tool",
                            ["content"] = JsonSerializer.Serialize(new { tool_call_id = tcallId, name = tname, result = Shorten(result, 8000) })
                        });

                        try
                        {
                            using var rd = JsonDocument.Parse(result);
                            if (rd.RootElement.TryGetProperty("__image__", out var im) && im.ValueKind == JsonValueKind.String)
                            {
                                string mime = rd.RootElement.TryGetProperty("__mime__", out var mm) ? mm.GetString() ?? "image/jpeg" : "image/jpeg";
                                string b64 = im.GetString() ?? "";
                                if (!string.IsNullOrEmpty(b64))
                                {
                                    var arr = new JsonArray();
                                    arr.Add(new JsonObject { ["type"] = "text", ["text"] = "（工具返回的图片，请直接查看）" });
                                    arr.Add(new JsonObject
                                    {
                                        ["type"] = "image_url",
                                        ["image_url"] = new JsonObject { ["url"] = "data:" + mime + ";base64," + b64 }
                                    });
                                    visuals.Add(new JsonObject { ["role"] = "user", ["content"] = arr });
                                }
                            }
                        }
                        catch { }
                    }
                }
                session.Messages.Add(new Dictionary<string, string> { ["role"] = "assistant", ["content"] = "（工具调用轮次已达上限，请缩小任务范围后重试）" });
                SaveSession(session);
                _aiLastTraces = traces;
                return (false, "", lastReasoning, "工具调用轮次超限");
            }
            catch (Exception ex)
            {
                return (false, "", lastReasoning, ex.Message);
            }
        }
    }

    private static List<object> _aiLastTraces = new();

    private static JsonArray BuildMessages(string user, AiSession s, List<JsonObject>? visuals = null)
    {
        var arr = new JsonArray();
        arr.Add(new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt(user) });
        foreach (var m in s.Messages)
        {
            switch (m["role"])
            {
                case "assistant_json":
                    try { arr.Add(JsonNode.Parse(m["content"])); } catch { }
                    break;
                case "tool":
                    try
                    {
                        using var doc = JsonDocument.Parse(m["content"]);
                        var r = doc.RootElement;
                        arr.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = r.TryGetProperty("tool_call_id", out var t) ? t.GetString() ?? "" : "",
                            ["content"] = r.TryGetProperty("result", out var res) ? res.GetString() ?? "" : ""
                        });
                    }
                    catch { }
                    break;
                default:
                    arr.Add(new JsonObject { ["role"] = m["role"], ["content"] = m["content"] });
                    break;
            }
        }
        if (visuals != null)
            foreach (var v in visuals) arr.Add(v);
        return arr;
    }

    private static string Shorten(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    private static void ServeAiState(HttpListenerContext ctx)
    {
        var sessions = new List<Dictionary<string, object>>();
        try
        {
            foreach (var f in Directory.GetFiles(_aiSessionDir, "*.json"))
            {
                var fi = new FileInfo(f);
                sessions.Add(new Dictionary<string, object>
                {
                    ["user"] = Path.GetFileNameWithoutExtension(f),
                    ["bytes"] = fi.Length,
                    ["updated"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
        }
        catch { }

        string who = ctx.Request.QueryString["user"] ?? "";
        var current = new List<Dictionary<string, string>>();
        string acct = "", src = "", title = "", updated = "";
        if (!string.IsNullOrWhiteSpace(who))
        {
            var s = LoadSession(who);
            acct = s.Account;
            src = s.Source;
            title = s.Title;
            updated = s.Updated;
            foreach (var m in s.Messages)
            {
                if (m["role"] != "user" && m["role"] != "assistant") continue;
                current.Add(new Dictionary<string, string>
                {
                    ["role"] = m["role"],
                    ["content"] = m["content"],
                    ["time"] = m.TryGetValue("time", out var tm) ? tm : ""
                });
            }
            if (current.Count > 80) current = current.Skip(current.Count - 80).ToList();
        }

        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true, enabled = _aiEnabled, model = _aiModel, thinking = _aiThinking,
            webThinking = _aiWebThinking,
            hasKey = !string.IsNullOrEmpty(_aiKey), keyMasked = MaskKey(_aiKey),
            maxContext = _aiMaxContext, userContextBytes = _aiUserContextBytes,
            account = acct, source = src, title, updated,
            messages = current,
            sessions = sessions.OrderByDescending(x => (string)x["updated"]).Take(50)
        }, JsonOpts));
    }

    private static void ServeAiSessions(HttpListenerContext ctx)
    {
        if (!RequireAdmin(ctx, ctx.Request, out _, out _)) return;
        bool all = (ctx.Request.QueryString["all"] ?? "") == "1";
        var rows = new List<(string Updated, object Item)>();
        try
        {
            foreach (var f in Directory.GetFiles(_aiSessionDir, "*.json"))
            {
                var fi = new FileInfo(f);
                var s = LoadSession(Path.GetFileNameWithoutExtension(f));
                if (!all && s.Source == "web") continue;
                int users = 0, replies = 0;
                foreach (var m in s.Messages)
                {
                    if (m["role"] == "user") users++;
                    else if (m["role"] == "assistant") replies++;
                }
                string upd = string.IsNullOrEmpty(s.Updated) ? fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") : s.Updated;
                rows.Add((upd, new
                {
                    user = s.User,
                    account = s.Account,
                    role = AccountRole(s.User),
                    source = s.Source,
                    sourceName = s.Source == "console" ? "本机控制台" : (s.Source == "admin" ? "管理后台" : "网页对话"),
                    title = s.Title,
                    count = users + replies,
                    userCount = users,
                    bytes = fi.Length,
                    updated = upd
                }));
            }
        }
        catch (Exception ex) { Log("[AI] 会话列表失败: " + ex.Message); }
        var ordered = rows.OrderByDescending(r => r.Updated).Select(r => r.Item).ToList();
        ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, sessions = ordered }, JsonOpts));
    }

    private static void ServeAiHistory(HttpListenerContext ctx)
    {
        if (!RequireAdmin(ctx, ctx.Request, out _, out _)) return;
        string who = ctx.Request.QueryString["user"] ?? "";
        if (string.IsNullOrWhiteSpace(who)) { ServeJson(ctx, "{\"ok\":false,\"error\":\"缺少 user\"}"); return; }
        var s = LoadSession(who);
        var msgs = new List<object>();
        foreach (var m in s.Messages)
        {
            if (m["role"] != "user" && m["role"] != "assistant") continue;
            msgs.Add(new
            {
                role = m["role"],
                content = m["content"],
                time = m.TryGetValue("time", out var tm) ? tm : ""
            });
        }
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true,
            user = s.User,
            account = s.Account,
            role = AccountRole(s.User),
            source = s.Source,
            sourceName = s.Source == "console" ? "本机控制台" : (s.Source == "admin" ? "管理后台" : "网页对话"),
            title = s.Title,
            model = s.Model,
            updated = s.Updated,
            messages = msgs
        }, JsonOpts));
    }

    private static void ServeAiChat(HttpListenerContext ctx)
    {
        try
        {
            string? body = ReadBodyOr413(ctx, 1024 * 1024);
            if (body == null) return;
            using var doc = JsonDocument.Parse(body);
            string user = doc.RootElement.TryGetProperty("user", out var u) ? u.GetString() ?? "web" : "web";
            string message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            string src = doc.RootElement.TryGetProperty("source", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(message)) { ServeJson(ctx, "{\"ok\":false,\"error\":\"消息为空\"}"); return; }
            if (string.IsNullOrEmpty(src)) src = IsAdminAccount(user) ? "admin" : "web";
            var (ok, reply, reasoning, error) = AiCall(user, message, 6, src);
            if (!ok) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error, reasoning })); return; }
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, reply, reasoning, traces = _aiLastTraces }, JsonOpts));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
    }

    private static void ServeAiReset(HttpListenerContext ctx)
    {
        try
        {
            string? body = ReadBodyOr413(ctx, 1024 * 1024);
            if (body == null) return;
            string user = "web";
            if (!string.IsNullOrEmpty(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.String) user = u.GetString() ?? "web";
            }
            string f = SessionFile(user);
            if (File.Exists(f)) File.Delete(f);
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
    }

    private static void ServeAiTools(HttpListenerContext ctx)
    {
        if (!RequireAdmin(ctx, ctx.Request, out _, out _)) return;
        var list = new List<object>();
        foreach (var t in AiTools)
        {
            var f = t?["function"];
            if (f == null) continue;
            list.Add(new
            {
                name = f["name"]?.GetValue<string>() ?? "",
                description = f["description"]?.GetValue<string>() ?? ""
            });
        }
        ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, tools = list }, JsonOpts));
    }

    private static void ServeAiToolInvoke(HttpListenerContext ctx)
    {
        if (!RequireAdmin(ctx, ctx.Request, out _, out _)) return;
        try
        {
            string? body = ReadBodyOr413(ctx, 1024 * 1024);
            if (body == null) return;
            using var doc = JsonDocument.Parse(body);
            string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) { ServeJson(ctx, "{\"ok\":false,\"error\":\"缺少工具名\"}"); return; }
            JsonElement args = doc.RootElement.TryGetProperty("args", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            string result = RunAiTool(name, args, "admin");
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, name, result }, JsonOpts));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
    }

    private static bool HandleAiCommand(string line)
    {
        string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string head = parts[0].ToUpperInvariant();
        if (head != "AI" && head != "AID") return false;
        string arg = parts.Length > 1 ? parts[1].Trim() : "";
        string sub = arg.ToUpperInvariant();
        string rest = "";
        if (arg.Length > 0)
        {
            int sp = arg.IndexOf(' ');
            if (sp > 0) { sub = arg.Substring(0, sp).ToUpperInvariant(); rest = arg.Substring(sp + 1).Trim(); }
        }

        switch (sub)
        {
            case "":
                Console.WriteLine("        [AI] " + (_aiEnabled ? "已启用" : "已关闭") + " · 模型 " + _aiModel + " · 思考 " + _aiThinking
                    + " · 上下文 " + _aiMaxContext + " tokens · 用户上限 " + (_aiUserContextBytes / 1024) + " KB");
                Console.WriteLine("        [AI] Key " + MaskKey(_aiKey));
                Console.WriteLine("        [AI] 指令: AI ON|OFF / AI KEY <key> / AI MODEL <name> / AI THINK <off|low|medium|high> / AI CTX <tokens> / AI CHAT <内容> / AI TASK <任务> / AI SESSIONS / AI RESET");
                break;
            case "ON":  _aiEnabled = true;  SaveAiConfig(); Console.WriteLine("        [AI] 已启用"); break;
            case "OFF": _aiEnabled = false; SaveAiConfig(); Console.WriteLine("        [AI] 已关闭"); break;
            case "KEY":
                if (string.IsNullOrEmpty(rest)) { Console.WriteLine("        [AI] 用法: AI KEY <key>"); break; }
                _aiKey = rest; _aiHttp = null; SaveAiConfig();
                Console.WriteLine("        [AI] Key 已更新为 " + MaskKey(_aiKey));
                break;
            case "MODEL":
                if (string.IsNullOrEmpty(rest)) { Console.WriteLine("        [AI] 当前模型: " + _aiModel); break; }
                _aiModel = rest; SaveAiConfig(); Console.WriteLine("        [AI] 模型已切换为 " + _aiModel);
                break;
            case "THINK":
                if (string.IsNullOrEmpty(rest)) { Console.WriteLine("        [AI] 当前思考档位: " + _aiThinking); break; }
                rest = rest.ToLowerInvariant();
                if (rest != "off" && rest != "low" && rest != "medium" && rest != "high") { Console.WriteLine("        [AI] 可选: off / low / medium / high"); break; }
                _aiThinking = rest; SaveAiConfig(); Console.WriteLine("        [AI] 思考档位已设为 " + rest);
                break;
            case "CTX":
                if (string.IsNullOrEmpty(rest) || !int.TryParse(rest, out int ctx)) { Console.WriteLine("        [AI] 当前上下文: " + _aiMaxContext); break; }
                _aiMaxContext = Math.Clamp(ctx, 8192, 1_000_000); SaveAiConfig();
                Console.WriteLine("        [AI] 上下文上限已设为 " + _aiMaxContext + " tokens");
                break;
            case "SESSIONS":
                try
                {
                    foreach (var f in Directory.GetFiles(_aiSessionDir, "*.json"))
                    {
                        var fi = new FileInfo(f);
                        var s = LoadSession(Path.GetFileNameWithoutExtension(f));
                        Console.WriteLine("        " + s.User.PadRight(12) + " " + (s.Source == "console" ? "控制台" : (s.Source == "admin" ? "后台" : "网页"))
                            + "  " + s.Messages.Count + " 条  " + (fi.Length / 1024) + " KB  " + fi.LastWriteTime.ToString("MM-dd HH:mm")
                            + "  " + Shorten(s.Title, 30));
                    }
                }
                catch { }
                break;
            case "RESET":
                try { string f = SessionFile(string.IsNullOrEmpty(rest) ? AI_CONSOLE_SESSION : rest); if (File.Exists(f)) File.Delete(f); } catch { }
                Console.WriteLine("        [AI] 会话已清空");
                break;
            case "CHAT":
            case "TASK":
                if (string.IsNullOrEmpty(rest)) { Console.WriteLine("        [AI] 用法: AI " + sub + " <内容>"); break; }
                int rounds = sub == "TASK" ? 10 : 6;
                Console.WriteLine("        [AI] 思考中…");
                var (ok, reply, reasoning, error) = AiCall(AI_CONSOLE_SESSION, rest, rounds);
                if (!string.IsNullOrEmpty(reasoning))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("        ── 思考 ──");
                    Console.WriteLine("        " + reasoning.Replace("\n", "\n        "));
                    Console.ResetColor();
                }
                if (ok) Console.WriteLine("        [AI] " + reply.Replace("\n", "\n        "));
                else Console.WriteLine("        [AI] 失败: " + error);
                break;
            default:

                Console.WriteLine("        [AI] 思考中…");
                var (ok2, reply2, reasoning2, error2) = AiCall(AI_CONSOLE_SESSION, arg, 6);
                if (ok2) Console.WriteLine("        [AI] " + reply2.Replace("\n", "\n        "));
                else Console.WriteLine("        [AI] 失败: " + error2);
                break;
        }
        return true;
    }
}

