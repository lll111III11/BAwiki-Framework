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

    private static readonly Dictionary<int, Dictionary<string, JsonElement>> _lobbyMeta = new();
    private static readonly Dictionary<int, string> _homeSpines = new();
    private static string _lobbyDir = "";
    private static string _spineDir = "";

    private static void LoadLobbyData()
    {
        try
        {
            string root = Path.Combine(DataDir, "06_回忆大厅");
            _lobbyDir = Path.Combine(root, "lobby");
            _spineDir = Path.Combine(root, "spines");
            _lobbyMeta.Clear();
            _homeSpines.Clear();

            string meta = Path.Combine(root, "lobby_meta.json");
            if (File.Exists(meta))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (!e.TryGetProperty("id", out var idv)) continue;
                    int id = idv.ValueKind == JsonValueKind.Number ? idv.GetInt32()
                           : (int.TryParse(idv.GetString(), out int x) ? x : 0);
                    if (id <= 0) continue;
                    _lobbyMeta[id] = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.GetRawText())!;
                }
            }

            string hs = Path.Combine(root, "home_spines.json");
            if (File.Exists(hs))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(hs));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (!e.TryGetProperty("id", out var idv) || !e.TryGetProperty("name", out var nv)) continue;
                    int id = idv.ValueKind == JsonValueKind.Number ? idv.GetInt32() : 0;
                    string? name = nv.GetString();
                    if (id > 0 && !string.IsNullOrEmpty(name)) _homeSpines[id] = name!;
                }
            }
            Log("大厅数据 " + _lobbyMeta.Count + " 条 / Spine 映射 " + _homeSpines.Count + " 个");
        }
        catch (Exception ex) { Log("大厅数据载入失败: " + ex.Message); }
    }

    private static string JStr(Dictionary<string, JsonElement> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        return "";
    }

    private static void ServeLobby(HttpListenerContext ctx, string rawId)
    {
        Dictionary<string, JsonElement>? e = null;
        if (!string.IsNullOrEmpty(rawId))
        {
            string head = rawId.Split('_')[0];
            if (int.TryParse(head, out int id) && id > 0) _lobbyMeta.TryGetValue(id, out e);
        }
        if (e == null)
        {
            ctx.Response.StatusCode = 404;
            ServeJson(ctx, "{\"ok\":false,\"error\":\"no lobby\"}");
            return;
        }

        string image = "";
        string file = JStr(e, "lobby");
        if (!string.IsNullOrEmpty(file) && File.Exists(Path.Combine(_lobbyDir, file)))
            image = "/lobby-img/" + Uri.EscapeDataString(file);

        var spines = new List<string>();
        foreach (var tok in JStr(e, "spine").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(tok.Trim(), out int sid)) continue;
            if (!_homeSpines.TryGetValue(sid, out var name)) continue;
            if (!Directory.Exists(Path.Combine(_spineDir, name))) continue;
            if (!spines.Contains(name)) spines.Add(name);
        }

        var payload = new Dictionary<string, object>
        {
            ["id"] = rawId, ["image"] = image, ["spines"] = spines
        };
        ServeJson(ctx, JsonSerializer.Serialize(payload));
    }

    private static void ServeLobbyAsset(HttpListenerContext ctx, string root, string rel)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            string rootFull = Path.GetFullPath(root).TrimEnd('\\') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            ctx.Response.ContentType = SniffMime(full);
            ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");
            WriteFileWithRange(ctx, full);
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }
}
