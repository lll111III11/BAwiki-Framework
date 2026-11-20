using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BAwikiWeb;

internal static partial class Server
{
    private class PluginDef
    {
        public string Id = "";
        public string Name = "";
        public string Version = "";
        public string Kind = "process";
        public string Runtime = "";
        public string RuntimePath = "";
        public string Entry = "";
        public string[] Args = Array.Empty<string>();
        public string[] Categories = Array.Empty<string>();
        public string[] Formats = Array.Empty<string>();
        public int Timeout = 120;
        public bool Enabled;
        public bool Auto;
        public string Force = "";
        public string ConfigText = "{}";
        public string LastError = "";
        public string LastRun = "";
        public string Dir = "";
    }

    private static string PluginsDir => Path.Combine(AppDir, "plugins");
    private static string PluginsState => Path.Combine(PluginsDir, "state.json");
    private static readonly object _plugLock = new();
    private static List<PluginDef> _plugins = new();
    private static FileSystemWatcher? _plugWatcher;
    private static Timer? _plugReload;

    private static void EnsurePlugins()
    {
        try { Directory.CreateDirectory(PluginsDir); } catch { }
        LoadPlugins();
        if (_plugWatcher == null)
        {
            try
            {
                _plugWatcher = new FileSystemWatcher(PluginsDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler h = (s, e) => QueuePluginReload();
                RenamedEventHandler rh = (s, e) => QueuePluginReload();
                _plugWatcher.Created += h;
                _plugWatcher.Deleted += h;
                _plugWatcher.Changed += h;
                _plugWatcher.Renamed += rh;
            }
            catch { }
        }
    }

    private static void QueuePluginReload()
    {
        if (_plugReload == null)
            _plugReload = new Timer(_ => { try { LoadPlugins(); } catch { } }, null, 1200, Timeout.Infinite);
        else
            _plugReload.Change(1200, Timeout.Infinite);
    }

    private static void LoadPlugins()
    {
        var list = new List<PluginDef>();
        try
        {
            if (Directory.Exists(PluginsDir))
            {
                foreach (var d in Directory.GetDirectories(PluginsDir))
                {
                    string mf = Path.Combine(d, "plugin.json");
                    if (!File.Exists(mf)) continue;
                    try
                    {
                        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(mf, Encoding.UTF8));
                        if (doc == null) continue;
                        var p = new PluginDef
                        {
                            Id = Str(doc, "id"),
                            Name = Str(doc, "name"),
                            Version = Str(doc, "version"),
                            Kind = Str(doc, "kind"),
                            Entry = Str(doc, "entry"),
                            Dir = d
                        };
                        if (string.IsNullOrEmpty(p.Id)) p.Id = Path.GetFileName(d);
                        if (string.IsNullOrEmpty(p.Name)) p.Name = p.Id;
                        if (string.IsNullOrEmpty(p.Kind)) p.Kind = "process";
                        p.Runtime = Str(doc, "runtime");
                        p.RuntimePath = Str(doc, "runtimePath");
                        p.Args = Arr(doc, "args");
                        p.Categories = Arr(doc, "categories");
                        p.Formats = Arr(doc, "formats").Select(x => x.StartsWith(".") || x == "*" ? x.ToLowerInvariant() : "." + x.ToLowerInvariant()).ToArray();
                        if (doc.TryGetValue("timeout", out var tv) && tv.TryGetInt32(out int t) && t > 0) p.Timeout = t;
                        var st = GetPluginState(p.Id);
                        p.Enabled = st.enabled;
                        p.Force = st.force;
                        try
                        {
                            string cf = Path.Combine(d, "config.json");
                            if (File.Exists(cf)) p.ConfigText = File.ReadAllText(cf, Encoding.UTF8);
                        }
                        catch { }
                        list.Add(p);
                    }
                    catch (Exception ex) { Log("[插件] " + Path.GetFileName(d) + " 清单解析失败: " + ex.Message); }
                }

                foreach (var f in Directory.GetFiles(PluginsDir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".dll" && ext != ".exe" && ext != ".jar" && ext != ".py" && ext != ".js" && ext != ".cjs" && ext != ".ahk") continue;
                    string id = Path.GetFileNameWithoutExtension(f);
                    var p = new PluginDef
                    {
                        Id = id,
                        Name = Path.GetFileName(f),
                        Version = "",
                        Kind = ext == ".dll" ? "dll" : ext == ".jar" ? "jar" : "process",
                        Runtime = ext == ".py" ? "python" : (ext == ".js" || ext == ".cjs") ? "node"
                                : ext == ".ahk" ? "ahk" : ext == ".jar" ? "java"
                                : ext == ".dll" ? "dotnet" : "process",
                        Entry = Path.GetFileName(f),
                        Dir = PluginsDir,
                        Auto = true
                    };
                    string side = f + ".plugin.json";
                    if (File.Exists(side))
                    {
                        try
                        {
                            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(side, Encoding.UTF8));
                            if (doc != null)
                            {
                                if (doc.ContainsKey("name")) p.Name = Str(doc, "name");
                                p.Version = Str(doc, "version");
                                if (doc.ContainsKey("kind")) p.Kind = Str(doc, "kind");
                                if (doc.ContainsKey("entry")) p.Entry = Str(doc, "entry");
                                p.Runtime = Str(doc, "runtime");
                                p.RuntimePath = Str(doc, "runtimePath");
                                p.Args = Arr(doc, "args");
                                p.Categories = Arr(doc, "categories");
                                p.Formats = Arr(doc, "formats").Select(x => x.StartsWith(".") || x == "*" ? x.ToLowerInvariant() : "." + x.ToLowerInvariant()).ToArray();
                                if (doc.TryGetValue("timeout", out var tvv) && tvv.TryGetInt32(out int tt) && tt > 0) p.Timeout = tt;
                                p.Auto = false;
                            }
                        }
                        catch { }
                    }
                    var st2 = GetPluginState(id);
                    p.Enabled = st2.enabled;
                    p.Force = st2.force;
                    try
                    {
                        string cf2 = Path.Combine(PluginsDir, id + ".config.json");
                        if (File.Exists(cf2)) p.ConfigText = File.ReadAllText(cf2, Encoding.UTF8);
                    }
                    catch { }
                    list.Add(p);
                }
            }
        }
        catch { }
        lock (_plugLock) _plugins = list;
    }

    private static string Str(Dictionary<string, JsonElement> d, string k)
        => d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string[] Arr(Dictionary<string, JsonElement> d, string k)
        => d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray()
            : Array.Empty<string>();

    private static (bool enabled, string force) GetPluginState(string id)
    {
        try
        {
            if (!File.Exists(PluginsState)) return (false, "");
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(PluginsState));
            if (d == null || !d.TryGetValue(id, out var v)) return (false, "");
            if (v.ValueKind == JsonValueKind.True) return (true, "");
            if (v.ValueKind == JsonValueKind.Object)
            {
                bool en = v.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                string f = v.TryGetProperty("force", out var fv) && fv.ValueKind == JsonValueKind.String ? fv.GetString() ?? "" : "";
                return (en, f);
            }
        }
        catch { }
        return (false, "");
    }

    private static void SavePluginState(string id, bool? enabled, string? force)
    {
        var map = new Dictionary<string, object>();
        try
        {
            if (File.Exists(PluginsState))
            {
                var old = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(PluginsState));
                if (old != null)
                    foreach (var kv in old)
                    {
                        bool en = kv.Value.ValueKind == JsonValueKind.True;
                        string f = "";
                        if (kv.Value.ValueKind == JsonValueKind.Object)
                        {
                            en = kv.Value.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                            f = kv.Value.TryGetProperty("force", out var fv) && fv.ValueKind == JsonValueKind.String ? fv.GetString() ?? "" : "";
                        }
                        map[kv.Key] = new Dictionary<string, object> { ["enabled"] = en, ["force"] = f };
                    }
            }
        }
        catch { }
        var cur = map.TryGetValue(id, out var o)
            ? (Dictionary<string, object>)o
            : new Dictionary<string, object> { ["enabled"] = false, ["force"] = "" };
        if (enabled.HasValue) cur["enabled"] = enabled.Value;
        if (force != null) cur["force"] = force;
        map[id] = cur;
        try { File.WriteAllText(PluginsState, JsonSerializer.Serialize(map)); } catch { }
        lock (_plugLock)
        {
            var p = _plugins.FirstOrDefault(x => x.Id == id);
            if (p != null) { if (enabled.HasValue) p.Enabled = enabled.Value; if (force != null) p.Force = force; }
        }
    }

    private static PluginDef? FindParserPlugin(string ext)
    {
        lock (_plugLock)
        {
            foreach (var p in _plugins)
            {
                if (!p.Enabled) continue;
                bool isParser = p.Force == "parser" || p.Force == "global"
                    || (p.Force.Length == 0 && p.Categories.Any(c => c.Equals("parser", StringComparison.OrdinalIgnoreCase)));
                if (!isParser) continue;
                if (p.Formats.Any(f => f != "*" && f.Equals(ext, StringComparison.OrdinalIgnoreCase))) return p;
            }
        }
        return null;
    }

    private static bool InvokePlugin(PluginDef p, string action, Dictionary<string, object> payload, out string output, out string message)
    {
        output = ""; message = "";
        try
        {
            string exe, args;
            string rt = (p.Runtime ?? "").ToLowerInvariant();
            string entryPath = Path.Combine(p.Dir, p.Entry);
            string rtPath = string.IsNullOrEmpty(p.RuntimePath) ? "" : p.RuntimePath;

            if (rt == "native") { message = "E_NATIVE_UNSUPPORTED"; return false; }
            if (rt == "python") { exe = rtPath.Length > 0 ? rtPath : "python"; args = Quote(entryPath); }
            else if (rt == "node") { exe = rtPath.Length > 0 ? rtPath : "node"; args = Quote(entryPath); }
            else if (rt == "ahk") { exe = rtPath.Length > 0 ? rtPath : "AutoHotkey.exe"; args = Quote(entryPath); }
            else if (rt == "java") { exe = rtPath.Length > 0 ? rtPath : "java"; args = "-jar " + Quote(entryPath); }
            else if (rt == "dotnet") { exe = rtPath.Length > 0 ? rtPath : "dotnet"; args = Quote(entryPath); }
            else if (p.Kind.Equals("command", StringComparison.OrdinalIgnoreCase))
            {
                exe = p.Entry;
                args = string.Join(" ", p.Args.Select(Quote));
            }
            else
            {
                string ext = Path.GetExtension(entryPath).ToLowerInvariant();
                if (ext == ".dll") { exe = "dotnet"; args = Quote(entryPath); }
                else if (ext == ".jar") { exe = "java"; args = "-jar " + Quote(entryPath); }
                else if (ext == ".py") { exe = "python"; args = Quote(entryPath); }
                else if (ext == ".js" || ext == ".cjs") { exe = "node"; args = Quote(entryPath); }
                else { exe = entryPath; args = ""; }
            }
            if (p.Kind.Equals("command", StringComparison.OrdinalIgnoreCase))
            {
                if (p.Args.Length > 0) args = (args + " " + string.Join(" ", p.Args.Select(Quote))).Trim();
            }
            else if (p.Args.Length > 0)
                args = (args + " " + string.Join(" ", p.Args.Select(Quote))).Trim();

            object cfg = new Dictionary<string, object>();
            try { cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(p.ConfigText) ?? new Dictionary<string, JsonElement>(); }
            catch { }

            var req = new Dictionary<string, object>(payload)
            {
                ["api"] = 1,
                ["action"] = action,
                ["config"] = cfg
            };

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = p.Dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null) { message = "E_SPAWN"; return false; }
            var so = proc.StandardOutput.ReadToEndAsync();
            var se = proc.StandardError.ReadToEndAsync();
            proc.StandardInput.WriteLine(JsonSerializer.Serialize(req));
            proc.StandardInput.Close();
            if (!proc.WaitForExit(p.Timeout * 1000))
            {
                try { proc.Kill(true); } catch { }
                message = "E_TIMEOUT";
                return false;
            }
            string stdout = so.Result.Trim();
            string stderr = se.Result.Trim();
            if (stdout.Length == 0)
            {
                message = string.IsNullOrEmpty(stderr) ? "E_EMPTY" : "E_STDERR:" + stderr;
                return false;
            }
            var line = stdout.Split('\n').Last(x => x.Trim().Length > 0).Trim();
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
            if (doc == null) { message = "E_BADJSON"; return false; }
            bool ok = doc.TryGetValue("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
            bool handled = doc.TryGetValue("handled", out var hv) && hv.ValueKind == JsonValueKind.True;
            if (doc.TryGetValue("output", out var ov) && ov.ValueKind == JsonValueKind.String) output = ov.GetString() ?? "";
            if (doc.TryGetValue("message", out var mv) && mv.ValueKind == JsonValueKind.String) message = mv.GetString() ?? "";
            if (doc.TryGetValue("error", out var ev) && ev.ValueKind == JsonValueKind.String) message = ev.GetString() ?? "";
            return ok && handled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

    private static Dictionary<string, object> QualityOf(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }
        int rate = 0, ch = 0, br = 0;
        double dur = 0;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Ffmpeg!,
                Arguments = "-hide_banner -i \"" + path + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                var am = Regex.Match(err, @"Audio:.*?(\d+)\s*Hz,\s*([^,]+),.*?(\d+)\s*kb/s");
                if (am.Success)
                {
                    rate = int.Parse(am.Groups[1].Value);
                    ch = am.Groups[2].Value.Contains("stereo") ? 2 : 1;
                    br = int.Parse(am.Groups[3].Value);
                }
                var dm = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (dm.Success)
                    dur = int.Parse(dm.Groups[1].Value) * 3600 + int.Parse(dm.Groups[2].Value) * 60
                        + double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        }
        catch { }
        bool lossless = ext == ".flac" || ext == ".wav" || ext == ".ape" || ext == ".dsf";
        string tier = (lossless || rate >= 48000 || br >= 256) ? "high"
                    : (br >= 128 || rate >= 44100) ? "mid" : "low";
        return new Dictionary<string, object>
        {
            ["tier"] = tier,
            ["ext"] = ext,
            ["bytes"] = size,
            ["sampleRate"] = rate,
            ["channels"] = ch,
            ["bitrate"] = br,
            ["duration"] = Math.Round(dur, 1),
            ["lossless"] = lossless
        };
    }

    private static List<string> PeImports(string path)
    {
        var res = new List<string>();
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            int pe = br.ReadInt32();
            fs.Seek(pe, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) return res;
            br.ReadUInt16();
            int secCount = br.ReadUInt16();
            br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
            int optSize = br.ReadUInt16();
            br.ReadUInt16();
            long optStart = fs.Position;
            ushort magic = br.ReadUInt16();
            long ddStart = optStart + (magic == 0x20B ? 112 : 96);
            fs.Seek(ddStart + 8, SeekOrigin.Begin);
            int impRva = br.ReadInt32();
            br.ReadInt32();
            if (impRva <= 0) return res;
            fs.Seek(optStart + optSize, SeekOrigin.Begin);
            var secs = new List<(uint va, uint vs, uint raw, uint rs)>();
            for (int i = 0; i < secCount; i++)
            {
                br.ReadBytes(8);
                uint vs = br.ReadUInt32();
                uint va = br.ReadUInt32();
                uint rs = br.ReadUInt32();
                uint raw = br.ReadUInt32();
                br.ReadBytes(16);
                secs.Add((va, vs, raw, rs));
            }
            long Off(int rva)
            {
                foreach (var s in secs)
                    if (rva >= s.va && rva < s.va + Math.Max(s.vs, s.rs)) return s.raw + (rva - s.va);
                return -1;
            }
            long o = Off(impRva);
            if (o < 0) return res;
            fs.Seek(o, SeekOrigin.Begin);
            while (true)
            {
                br.ReadInt32(); br.ReadInt32(); br.ReadInt32();
                int nameRva = br.ReadInt32();
                br.ReadInt32();
                if (nameRva == 0) break;
                long no = Off(nameRva);
                if (no < 0) break;
                long back = fs.Position;
                fs.Seek(no, SeekOrigin.Begin);
                var sb = new StringBuilder();
                while (true)
                {
                    int c = fs.ReadByte();
                    if (c <= 0) break;
                    if (sb.Length < 128) sb.Append((char)c);
                }
                if (sb.Length > 0) res.Add(sb.ToString());
                fs.Seek(back, SeekOrigin.Begin);
            }
        }
        catch { }
        return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, object> CheckNativeDeps(string path)
    {
        var search = new List<string>();
        bool copy = false;
        try
        {
            string req = path + ".requires.json";
            if (File.Exists(req))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(req, Encoding.UTF8));
                if (d != null)
                {
                    if (d.TryGetValue("searchPaths", out var sp) && sp.ValueKind == JsonValueKind.Array)
                        foreach (var x in sp.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String) search.Add(Environment.ExpandEnvironmentVariables(x.GetString() ?? ""));
                    if (d.TryGetValue("copyToPlugin", out var cp) && cp.ValueKind == JsonValueKind.True) copy = true;
                }
            }
        }
        catch { }

        var dirs = new List<string> { Path.GetDirectoryName(path) ?? "", AppDir, Path.Combine(AppDir, "plugins", "_deps") };
        dirs.AddRange(search);
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));

        var imports = PeImports(path);
        var resolved = new List<object>();
        var missing = new List<string>();
        int copied = 0;
        foreach (var name in imports)
        {
            string? hit = null;
            foreach (var d in dirs)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                try { string p = Path.Combine(d.Trim(), name); if (File.Exists(p)) { hit = p; break; } } catch { }
            }
            if (hit == null) { missing.Add(name); continue; }
            resolved.Add(new Dictionary<string, object> { ["name"] = name, ["from"] = hit });
            if (copy)
            {
                try
                {
                    string dst = Path.Combine(Path.GetDirectoryName(path) ?? "", name);
                    if (!File.Exists(dst)) { File.Copy(hit, dst, true); copied++; }
                }
                catch { }
            }
        }
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["path"] = path,
            ["imports"] = imports,
            ["resolved"] = resolved,
            ["missing"] = missing,
            ["copied"] = copied,
            ["searchDirs"] = dirs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList()
        };
    }

    private static Dictionary<string, object> DumpAssembly(string path)
    {
        var asm = Assembly.LoadFrom(path);
        Type[] ts;
        try { ts = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { ts = ex.Types.Where(t => t != null).ToArray(); }
        var list = new List<object>();
        foreach (var t in ts.Where(x => x.IsPublic || x.IsNestedPublic).OrderBy(x => x.FullName).Take(300))
        {
            var methods = new List<string>();
            var props = new List<string>();
            var fields = new List<string>();
            try { foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) methods.Add(m.Name); } catch { }
            try { foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) props.Add(p.Name); } catch { }
            try { foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) fields.Add(f.Name); } catch { }
            string baseName = "";
            try { baseName = t.BaseType?.FullName ?? ""; } catch { }
            list.Add(new Dictionary<string, object>
            {
                ["name"] = t.FullName ?? t.Name,
                ["base"] = baseName,
                ["methods"] = methods,
                ["properties"] = props,
                ["fields"] = fields
            });
        }
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["path"] = path,
            ["count"] = ts.Length,
            ["types"] = list
        };
    }

    private static Dictionary<string, object> AnalyzeAssembly(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        var refs = new List<string>();
        foreach (var h in md.AssemblyReferences)
            refs.Add(md.GetString(md.GetAssemblyReference(h).Name));

        var types = new List<string>();
        var methods = new List<string>();
        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            string ns = md.GetString(td.Namespace);
            string name = md.GetString(td.Name);
            if (name == "<Module>") continue;
            bool pub = (td.Attributes & System.Reflection.TypeAttributes.Public) != 0;
            if (!pub) continue;
            types.Add(string.IsNullOrEmpty(ns) ? name : ns + "." + name);
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if ((m.Attributes & System.Reflection.MethodAttributes.Public) != 0)
                    methods.Add(md.GetString(m.Name));
            }
        }

        string blob = string.Join(" ", refs) + " " + string.Join(" ", types) + " " + string.Join(" ", methods);
        blob = blob.ToLowerInvariant();
        var cats = new List<string>();
        void hit(string cat, params string[] keys)
        {
            foreach (var k in keys) if (blob.Contains(k)) { if (!cats.Contains(cat)) cats.Add(cat); return; }
        }
        hit("parser", "parse", "probe", "demux", "container", "format", "extract", "metadata");
        hit("audio", "audio", "sound", "wave", "flac", "mp3", "ogg", "opus", "resampl", "mixer", "dsp");
        hit("ai", "model", "infer", "prompt", "token", "llm", "embedding");
        hit("ui", "render", "theme", "html", "css", "view", "layout");
        hit("mcp", "mcp", "jsonrpc", "rpc", "tool");
        if (refs.Any(r => r.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase)
                       || r.StartsWith("avcodec", StringComparison.OrdinalIgnoreCase)
                       || r.StartsWith("avformat", StringComparison.OrdinalIgnoreCase)))
            if (!cats.Contains("audio")) cats.Add("audio");
        if (cats.Count == 0) cats.Add("global");

        var probeDirs = new List<string> { Path.GetDirectoryName(path) ?? "" , AppDir, Path.Combine(AppDir, "plugins", "_deps") };
        foreach (var d in (Environment.GetEnvironmentVariable("BAW_PLUGIN_DEPS") ?? "").Split(';'))
            if (!string.IsNullOrWhiteSpace(d)) probeDirs.Add(d.Trim());
        var resolved = new List<string>();
        var missing = new List<string>();
        foreach (var r in refs.Distinct())
        {
            bool found = false;
            foreach (var d in probeDirs)
            {
                if (string.IsNullOrEmpty(d)) continue;
                try { if (File.Exists(Path.Combine(d, r + ".dll"))) { found = true; break; } } catch { }
            }
            if (found) resolved.Add(r); else missing.Add(r);
        }

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["path"] = path,
            ["resolved"] = resolved,
            ["missing"] = missing,
            ["probeDirs"] = probeDirs.Where(x => !string.IsNullOrEmpty(x)).ToList(),
            ["references"] = refs.Take(40).ToList(),
            ["types"] = types.Take(80).ToList(),
            ["methods"] = methods.Distinct().Take(120).ToList(),
            ["typeCount"] = types.Count,
            ["methodCount"] = methods.Count,
            ["categories"] = cats
        };
    }

    private static void ServeAdminPlugins(HttpListenerContext ctx, HttpListenerRequest req)
    {
        if (!RequireAdmin(ctx, req, out _, out _)) return;
        if (req.HttpMethod == "POST")
        {
            try
            {
                using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = sr.ReadToEnd();
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                if (doc != null && doc.TryGetValue("test", out var tv2) && tv2.ValueKind == JsonValueKind.String)
                {
                    string tid = tv2.GetString() ?? "";
                    PluginDef? tp = null;
                    lock (_plugLock) tp = _plugins.FirstOrDefault(x => x.Id == tid);
                    if (tp == null) { ServeJson(ctx, "{\"ok\":false,\"error\":\"E_NOT_FOUND\"}"); return; }
                    bool tok = InvokePlugin(tp, "ping", new Dictionary<string, object>(), out string tout, out string tmsg);
                    lock (_plugLock) { tp.LastRun = DateTime.Now.ToString("MM-dd HH:mm:ss"); tp.LastError = tok ? "" : tmsg; }
                    ServeJson(ctx, JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["ok"] = true, ["handled"] = tok, ["output"] = tout, ["message"] = tmsg
                    }, JsonOpts));
                    return;
                }
                if (doc != null && doc.TryGetValue("manifest", out var mvf) && mvf.ValueKind == JsonValueKind.String)
                {
                    string mid = mvf.GetString() ?? "";
                    PluginDef? t = null;
                    lock (_plugLock) t = _plugins.FirstOrDefault(x => x.Id == mid);
                    if (t != null && t.Auto && t.Dir == PluginsDir)
                    {
                        string side = Path.Combine(PluginsDir, t.Entry + ".plugin.json");
                        var obj = new Dictionary<string, object>
                        {
                            ["id"] = t.Id, ["name"] = t.Name, ["version"] = "1.0.0", ["api"] = 1,
                            ["kind"] = t.Kind, ["entry"] = t.Entry, ["args"] = Array.Empty<string>(),
                            ["categories"] = Array.Empty<string>(), ["formats"] = Array.Empty<string>(), ["timeout"] = 120
                        };
                        File.WriteAllText(side, JsonSerializer.Serialize(obj));
                        LoadPlugins();
                        ServeJson(ctx, "{\"ok\":true}");
                    }
                    else ServeJson(ctx, "{\"ok\":false,\"error\":\"E_NOT_AUTO\"}");
                    return;
                }
                if (doc != null && doc.TryGetValue("deps", out var depv) && depv.ValueKind == JsonValueKind.String)
                {
                    string dp2 = depv.GetString() ?? "";
                    if (!File.Exists(dp2) || Path.GetExtension(dp2).ToLowerInvariant() != ".dll") ServeJson(ctx, "{\"ok\":false,\"error\":\"E_NOT_FOUND\"}");
                    else
                    {
                        try { ServeJson(ctx, JsonSerializer.Serialize(CheckNativeDeps(dp2), JsonOpts)); }
                        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message }, JsonOpts)); }
                    }
                    return;
                }
                if (doc != null && doc.TryGetValue("dump", out var dv) && dv.ValueKind == JsonValueKind.String)
                {
                    string dp = dv.GetString() ?? "";
                    if (!File.Exists(dp) || Path.GetExtension(dp).ToLowerInvariant() != ".dll") ServeJson(ctx, "{\"ok\":false,\"error\":\"E_NOT_FOUND\"}");
                    else
                    {
                        try { ServeJson(ctx, JsonSerializer.Serialize(DumpAssembly(dp), JsonOpts)); }
                        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message }, JsonOpts)); }
                    }
                    return;
                }
                if (doc != null && doc.TryGetValue("analyze", out var av) && av.ValueKind == JsonValueKind.String)
                {
                    string ap = av.GetString() ?? "";
                    string aext = Path.GetExtension(ap).ToLowerInvariant();
                    if (!File.Exists(ap) || (aext != ".dll" && aext != ".exe" && aext != ".jar"))
                        ServeJson(ctx, "{\"ok\":false,\"error\":\"E_NOT_FOUND\"}");
                    else
                    {
                        try { ServeJson(ctx, JsonSerializer.Serialize(AnalyzeAssembly(ap), JsonOpts)); }
                        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message }, JsonOpts)); }
                    }
                    return;
                }
                if (doc != null && doc.TryGetValue("id", out var idv) && idv.ValueKind == JsonValueKind.String)
                {
                    string id = idv.GetString() ?? "";
                    bool on = doc.TryGetValue("enabled", out var ev) && ev.ValueKind == JsonValueKind.True;
                    if (id == "*" && doc.TryGetValue("refresh", out var rv) && rv.ValueKind == JsonValueKind.True)
                        LoadPlugins();
                    else
                    {
                        if (doc.ContainsKey("enabled")) SavePluginState(id, on, null);
                        if (doc.TryGetValue("force", out var fv2) && fv2.ValueKind == JsonValueKind.String)
                            SavePluginState(id, null, fv2.GetString() ?? "");
                        if (doc.TryGetValue("config", out var cv2) && cv2.ValueKind == JsonValueKind.String)
                        {
                            string cfg = cv2.GetString() ?? "{}";
                            try
                            {
                                JsonSerializer.Deserialize<JsonElement>(cfg);
                                PluginDef? target;
                                lock (_plugLock) target = _plugins.FirstOrDefault(x => x.Id == id);
                                if (target != null)
                                {
                                    string cp = target.Dir == PluginsDir
                                        ? Path.Combine(PluginsDir, id + ".config.json")
                                        : Path.Combine(target.Dir, "config.json");
                                    File.WriteAllText(cp, cfg);
                                    target.ConfigText = cfg;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }
        List<object> list = new();
        lock (_plugLock)
        {
            foreach (var p in _plugins)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["version"] = p.Version,
                    ["kind"] = p.Kind,
                    ["runtime"] = p.Runtime,
                    ["categories"] = p.Categories,
                    ["formats"] = p.Formats,
                    ["enabled"] = p.Enabled,
                    ["auto"] = p.Auto,
                    ["force"] = p.Force,
                    ["config"] = p.ConfigText,
                    ["lastError"] = p.LastError,
                    ["lastRun"] = p.LastRun
                });
            }
        }
        ServeJson(ctx, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["dir"] = PluginsDir,
            ["plugins"] = list
        }, JsonOpts));
    }
}
