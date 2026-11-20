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

    private sealed class ImportItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Type { get; set; } = "";
        public string File { get; set; } = "";
        public long Size { get; set; }
        public double Duration { get; set; }
        public double Lufs { get; set; }
        public double TruePeak { get; set; }
        public double Lra { get; set; }
        public string Added { get; set; } = "";
    }
    private static readonly object _importLock = new();
    private static readonly JsonSerializerOptions ImportJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string ImportDir => Path.Combine(DataDir, "08_本地导入");
    private static string ImportMeta => Path.Combine(ImportDir, "_import.json");

    private static List<ImportItem> LoadImportItems()
    {
        try
        {
            return File.Exists(ImportMeta)
                ? JsonSerializer.Deserialize<List<ImportItem>>(File.ReadAllText(ImportMeta)) ?? new()
                : new();
        }
        catch { return new(); }
    }
    private static void SaveImportItems(List<ImportItem> items)
    {
        lock (_importLock)
            File.WriteAllText(ImportMeta, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static bool RunFfmpeg(string args, out string err)
    {
        err = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Ffmpeg!,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var t = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(600000)) { try { p.Kill(); } catch { } return false; }
            err = t.Result;
            return p.ExitCode == 0;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    private static (bool video, bool audio, double duration) ProbeMedia(string file)
    {
        string err = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Ffmpeg!,
                Arguments = "-hide_banner -i \"" + file + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, false, 0);
            err = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
        }
        catch { return (false, false, 0); }
        bool v = Regex.IsMatch(err, @"Stream #\d+:\d+.*: Video:");
        bool a = Regex.IsMatch(err, @"Stream #\d+:\d+.*: Audio:");
        double dur = 0;
        var m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (m.Success &&
            double.TryParse(m.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sec))
            dur = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + sec;
        return (v, a, dur);
    }

    private static string SafeFileName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        name = name.Trim().Trim('.');
        return string.IsNullOrEmpty(name) ? "未命名" : name;
    }

    private static void ServeImportUpload(HttpListenerContext ctx, HttpListenerRequest req)
    {
        try
        {
            string token = Regex.Replace(req.QueryString["token"] ?? "", "[^a-zA-Z0-9]", "");
            string name = SafeFileName(Path.GetFileName(req.QueryString["name"] ?? ""));
            if (token.Length < 16) { ServeJson(ctx, "{\"ok\":false,\"error\":\"token 无效\"}"); return; }
            if (req.ContentLength64 > 8L * 1024 * 1024 * 1024)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"单个文件不能超过 8 GB\"}");
                return;
            }
            string dir = Path.Combine(ImportDir, "_tmp", token);
            bool fresh = !Directory.Exists(dir);
            if (fresh)
            {
                try
                {
                    string tmpRoot = Path.Combine(ImportDir, "_tmp");
                    if (Directory.Exists(tmpRoot) && Directory.GetDirectories(tmpRoot).Length >= 8)
                    {
                        ServeJson(ctx, "{\"ok\":false,\"error\":\"同时进行的上传会话过多，请稍后再试\"}");
                        return;
                    }
                }
                catch { }
            }
            Directory.CreateDirectory(dir);
            long used = 0;
            try { foreach (var f2 in Directory.GetFiles(dir)) used += new FileInfo(f2).Length; } catch { }
            if (used + Math.Max(0, req.ContentLength64) > 8L * 1024 * 1024 * 1024)
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"本次上传总量不能超过 8 GB\"}");
                return;
            }
            string dst = Path.Combine(dir, name);
            using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                req.InputStream.CopyTo(fs, 1 << 20);
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, name, size = new FileInfo(dst).Length }));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
    }

    private static void TryStripBiliM4sHeader(string file)
    {
        try
        {
            byte[] head = new byte[17];
            using (var fs = File.OpenRead(file))
                if (fs.Read(head, 0, head.Length) < head.Length) return;
            for (int i = 0; i < 9; i++)
                if (head[i] != 0x30 && head[i] != 0x00) return;
            if (!(head[13] == (byte)'f' && head[14] == (byte)'t' &&
                  head[15] == (byte)'y' && head[16] == (byte)'p')) return;

            string tmp = file + ".tmp";
            using (var src = File.OpenRead(file))
            using (var dst = File.Create(tmp))
            {
                src.Seek(9, SeekOrigin.Begin);
                src.CopyTo(dst);
            }
            File.Move(tmp, file, true);
            Log("[导入] 已剥离 B站 M4S 的 9 字节头: " + Path.GetFileName(file));
        }
        catch { }
    }

    private sealed class ImportJob
    {
        public string Id { get; set; } = "";
        public string State { get; set; } = "queued";
        public string Message { get; set; } = "";
        public int Total { get; set; }
        public int Done { get; set; }
        public string Error { get; set; } = "";
        public List<ImportItem> Items { get; set; } = new();
        public DateTime Started { get; set; } = DateTime.Now;
    }
    private static readonly ConcurrentDictionary<string, ImportJob> _importJobs = new();
    private static readonly SemaphoreSlim _importGate = new(1, 1);

    private static void CleanupImportTemp()
    {
        try
        {
            string tmp = Path.Combine(ImportDir, "_tmp");
            if (!Directory.Exists(tmp)) return;
            foreach (var d in Directory.GetDirectories(tmp))
            {
                try
                {
                    if ((DateTime.Now - Directory.GetLastWriteTime(d)).TotalHours > 24)
                        Directory.Delete(d, true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string ImportBaseName(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file);
        n = Regex.Replace(n, @"-\d+-\d+$", "");
        n = Regex.Replace(n, @"[-_](video|audio)$", "", RegexOptions.IgnoreCase);
        return n.Trim();
    }

    private sealed class ImportPair
    {
        public string? Video;
        public string? Audio;
        public double Duration;
    }

    private static List<ImportPair> BuildImportPairs(List<(string file, bool v, bool a, double dur)> media)
    {
        var groups = new Dictionary<string, ImportPair>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in media)
        {
            string k = ImportBaseName(m.file);
            if (!groups.TryGetValue(k, out var p)) { p = new ImportPair(); groups[k] = p; }
            if (m.v && p.Video == null) p.Video = m.file;
            if (m.a && p.Audio == null) p.Audio = m.file;
            if (m.dur > p.Duration) p.Duration = m.dur;
        }

        var list = groups.Values.ToList();

        foreach (var p in list) if (p.Audio == p.Video) p.Audio = null;

        var loneVideo = list.Where(p => p.Video != null && p.Audio == null).ToList();
        var loneAudio = list.Where(p => p.Video == null && p.Audio != null).ToList();
        if (loneVideo.Count == 1 && loneAudio.Count == 1)
        {
            loneVideo[0].Audio = loneAudio[0].Audio;
            list.Remove(loneAudio[0]);
        }
        return list;
    }

    private static (double lufs, double tp, double lra) MeasureLoudness(string file)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Ffmpeg!,
                Arguments = "-hide_banner -nostats -i \"" + file + "\" -af loudnorm=print_format=json -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (0, 0, 0);
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(180000);
            int a = err.LastIndexOf('{'), b = err.LastIndexOf('}');
            if (a < 0 || b <= a) return (0, 0, 0);
            using var doc = JsonDocument.Parse(err.Substring(a, b - a + 1));
            double Read(string k)
            {
                return doc.RootElement.TryGetProperty(k, out var v) &&
                       v.ValueKind == JsonValueKind.String &&
                       double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
                    ? d : 0;
            }
            return (Read("input_i"), Read("input_tp"), Read("input_lra"));
        }
        catch { return (0, 0, 0); }
    }

    private static void UpsertImportLearn(ImportItem item)
    {
        if (item.Lufs == 0 && item.TruePeak == 0) return;
        string key = "import:" + item.Id;
        lock (_alLock)
        {
            if (!_audioLearn.TryGetValue(key, out var p)) { p = new AudioLearn(); _audioLearn[key] = p; }
            p.Name = item.Title;
            double lin = item.TruePeak > -100 ? Math.Pow(10, item.TruePeak / 20.0) : 0;
            if (lin > p.Peak) p.Peak = lin;
            p.Lufs = item.Lufs;
            p.TruePeakDb = item.TruePeak;
            p.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        SaveAudioLearn();
    }

    private static string TrimErr(string err) => err.Length > 600 ? err.Substring(err.Length - 600) : err;

    private static ImportItem? ProcessPair(ImportPair pair, string vDir, string aDir, ImportJob job)
    {
        string id = Guid.NewGuid().ToString("N").Substring(0, 10);
        string err = "";
        if (pair.Video != null)
        {
            string title = SafeFileName(Regex.Replace(Path.GetFileNameWithoutExtension(pair.Video), @"-\d+-\d+$", ""));
            string outFull = Path.Combine(vDir, title + "_" + id + ".mp4");
            string outRel = "视频/" + Path.GetFileName(outFull);

            job.State = "merging";
            job.Message = pair.Audio != null
                ? "正在合成 " + Path.GetFileName(pair.Video) + " + " + Path.GetFileName(pair.Audio) + "…"
                : "正在封装 " + Path.GetFileName(pair.Video) + "…";
            bool ok = pair.Audio != null
                ? RunFfmpeg("-y -fflags +genpts -i \"" + pair.Video + "\" -i \"" + pair.Audio + "\" -map 0:v:0 -map 1:a:0 -c copy -avoid_negative_ts make_zero -movflags +faststart \"" + outFull + "\"", out err)
                : RunFfmpeg("-y -fflags +genpts -i \"" + pair.Video + "\" -c copy -avoid_negative_ts make_zero -movflags +faststart \"" + outFull + "\"", out err);
            if (!ok)
            {

                job.Message = "时间戳异常，修复后重试封装…";
                ok = pair.Audio != null
                    ? RunFfmpeg("-y -i \"" + pair.Video + "\" -i \"" + pair.Audio + "\" -map 0:v:0 -map 1:a:0 -c copy -movflags +faststart \"" + outFull + "\"", out err)
                    : RunFfmpeg("-y -i \"" + pair.Video + "\" -c copy -movflags +faststart \"" + outFull + "\"", out err);
            }
            if (!ok)
            {
                job.State = "transcoding";
                job.Message = "无损封装失败，正在转码 " + Path.GetFileName(pair.Video) + "…";
                ok = pair.Audio != null
                    ? RunFfmpeg("-y -i \"" + pair.Video + "\" -i \"" + pair.Audio + "\" -map 0:v:0 -map 1:a:0 -c:v libx264 -preset veryfast -crf 20 -c:a aac -b:a 192k -movflags +faststart \"" + outFull + "\"", out err)
                    : RunFfmpeg("-y -i \"" + pair.Video + "\" -c:v libx264 -preset veryfast -crf 20 -movflags +faststart \"" + outFull + "\"", out err);
            }
            if (!ok || !File.Exists(outFull))
            {
                job.Error = "合成 MP4 失败：" + TrimErr(err);
                try { File.Delete(outFull); } catch { }
                return null;
            }

            job.State = "measuring";
            job.Message = "正在测量响度 " + title + "…";
            var (lufs, tp, lra) = MeasureLoudness(outFull);
            var item = new ImportItem
            {
                Id = id, Title = title, Type = "video", File = outRel,
                Size = new FileInfo(outFull).Length, Duration = pair.Duration,
                Lufs = lufs, TruePeak = tp, Lra = lra,
                Added = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            UpsertImportLearn(item);
            return item;
        }

        if (pair.Audio == null) return null;
        {
            string title = SafeFileName(Regex.Replace(Path.GetFileNameWithoutExtension(pair.Audio), @"-\d+-\d+$", ""));
            string outFull = Path.Combine(aDir, title + "_" + id + ".m4a");
            string outRel = "音频/" + Path.GetFileName(outFull);
            job.State = "merging";
            job.Message = "正在处理音频 " + Path.GetFileName(pair.Audio) + "…";
            bool ok = RunFfmpeg("-y -fflags +genpts -i \"" + pair.Audio + "\" -vn -c:a copy -avoid_negative_ts make_zero -movflags +faststart \"" + outFull + "\"", out err);
            if (!ok) ok = RunFfmpeg("-y -i \"" + pair.Audio + "\" -vn -c:a copy -movflags +faststart \"" + outFull + "\"", out err);
            if (!ok)
            {
                job.State = "transcoding";
                job.Message = "正在转码音频 " + Path.GetFileName(pair.Audio) + "…";
                ok = RunFfmpeg("-y -i \"" + pair.Audio + "\" -vn -c:a aac -b:a 192k \"" + outFull + "\"", out err);
            }
            if (!ok || !File.Exists(outFull))
            {
                job.Error = "音频转换失败：" + TrimErr(err);
                try { File.Delete(outFull); } catch { }
                return null;
            }

            job.State = "measuring";
            job.Message = "正在测量响度 " + title + "…";
            var (lufs, tp, lra) = MeasureLoudness(outFull);
            var item = new ImportItem
            {
                Id = id, Title = title, Type = "audio", File = outRel,
                Size = new FileInfo(outFull).Length, Duration = pair.Duration,
                Lufs = lufs, TruePeak = tp, Lra = lra,
                Added = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            UpsertImportLearn(item);
            return item;
        }
    }

    private static void RunImportJob(ImportJob job, string dir)
    {
        _importGate.Wait();
        try
        {
            job.State = "probing";
            job.Message = "正在探测音视频轨道…";
            var files = Directory.GetFiles(dir).OrderBy(f => f).ToList();
            if (files.Count == 0) { job.State = "failed"; job.Error = "没有收到文件"; return; }
            foreach (var f in files) TryStripBiliM4sHeader(f);

            int rejectedFiles = 0;
            foreach (var f in files)
            {
                if (Array.IndexOf(AudioExt, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                if (ValidateAudioFile(f, out string why)) continue;
                try
                {
                    string rej = Path.Combine(InboxDir, ".rejected");
                    Directory.CreateDirectory(rej);
                    string dst = Path.Combine(rej, DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Path.GetFileName(f));
                    File.Move(f, dst, true);
                    rejectedFiles++;
                    Log("[合规] 已拒绝 " + Path.GetFileName(f) + "：" + why);
                }
                catch { }
            }
            if (rejectedFiles > 0)
            {
                files = Directory.GetFiles(dir).OrderBy(x => x).ToList();
                if (files.Count == 0) { job.State = "failed"; job.Error = "文件未通过合规检验（已移到 _inbox/.rejected）"; return; }
            }

            var media = new List<(string file, bool v, bool a, double dur)>();
            foreach (var f in files)
            {
                var p = ProbeMedia(f);
                if (p.video || p.audio) media.Add((f, p.video, p.audio, p.duration));
            }
            if (media.Count == 0) { job.State = "failed"; job.Error = "没有识别到音频或视频轨（需要 ffmpeg 可读的格式）"; return; }

            var pairs = BuildImportPairs(media);
            job.Total = pairs.Count;
            job.Done = 0;
            string vDir = Path.Combine(ImportDir, "视频");
            string aDir = Path.Combine(ImportDir, "音频");
            Directory.CreateDirectory(vDir);
            Directory.CreateDirectory(aDir);

            var produced = new List<ImportItem>();
            foreach (var pair in pairs)
            {
                try
                {
                    var item = ProcessPair(pair, vDir, aDir, job);
                    if (item != null) { produced.Add(item); lock (job.Items) job.Items.Add(item); }
                }
                catch (Exception ex) { job.Error = ex.Message; }
                job.Done++;
            }

            if (produced.Count == 0)
            {
                job.State = "failed";
                if (string.IsNullOrEmpty(job.Error)) job.Error = "没有生成任何文件";
                return;
            }

            var all = LoadImportItems();
            all.AddRange(produced);
            SaveImportItems(all);
            try { Directory.Delete(dir, true); } catch { }
            foreach (var it in produced)
                Log("[导入] " + it.Type + " " + it.Title + " (" + (it.Size / 1048576.0).ToString("0.0") + " MB, " + it.Lufs.ToString("0.0") + " LUFS)");
            job.State = "done";
            job.Message = "完成：已生成 " + produced.Count + " 个文件";
        }
        catch (Exception ex)
        {
            job.State = "failed";
            job.Error = ex.Message;
        }
        finally
        {
            _importGate.Release();
        }
    }

    private static void ServeImportProcess(HttpListenerContext ctx, HttpListenerRequest req)
    {
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            string token = "";
            try { using var d = JsonDocument.Parse(body); token = d.RootElement.TryGetProperty("token", out var t) ? (t.GetString() ?? "") : ""; } catch { }
            token = Regex.Replace(token, "[^a-zA-Z0-9]", "");
            string dir = Path.Combine(ImportDir, "_tmp", token);
            if (token.Length < 6 || !Directory.Exists(dir))
            {
                ServeJson(ctx, "{\"ok\":false,\"error\":\"上传会话不存在\"}");
                return;
            }

            foreach (var kv in _importJobs)
                if ((DateTime.Now - kv.Value.Started).TotalHours > 2) _importJobs.TryRemove(kv.Key, out _);

            string jid = Guid.NewGuid().ToString("N").Substring(0, 12);
            var job = new ImportJob { Id = jid, State = "queued", Message = "已接收，等待处理…" };
            _importJobs[jid] = job;
            _ = Task.Run(() => RunImportJob(job, dir));
            ServeJson(ctx, JsonSerializer.Serialize(new { ok = true, jobId = jid }));
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
    }

    private static void ServeImportStatus(HttpListenerContext ctx, HttpListenerRequest req)
    {
        string id = req.QueryString["job"] ?? "";
        if (!_importJobs.TryGetValue(id, out var job))
        {
            ServeJson(ctx, "{\"ok\":false,\"error\":\"任务不存在或已过期\"}");
            return;
        }
        List<ImportItem> snapshot;
        lock (job.Items) snapshot = job.Items.ToList();
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true, state = job.State, message = job.Message,
            total = job.Total, done = job.Done, error = job.Error,
            items = snapshot
        }, ImportJson));
    }

    private static void ServeImportList(HttpListenerContext ctx)
    {
        var items = LoadImportItems();
        ServeJson(ctx, JsonSerializer.Serialize(new
        {
            ok = true,
            video = items.Where(x => x.Type == "video").OrderByDescending(x => x.Added).ToList(),
            audio = items.Where(x => x.Type == "audio").OrderByDescending(x => x.Added).ToList()
        }, ImportJson));
    }

    private static void ServeImportDelete(HttpListenerContext ctx, HttpListenerRequest req)
    {
        try
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            string id = "";
            try { using var d = JsonDocument.Parse(body); id = d.RootElement.TryGetProperty("id", out var v) ? (v.GetString() ?? "") : ""; } catch { }
            var items = LoadImportItems();
            var hit = items.FirstOrDefault(x => x.Id == id);
            if (hit == null) { ServeJson(ctx, "{\"ok\":false,\"error\":\"条目不存在\"}"); return; }
            try { File.Delete(Path.Combine(ImportDir, hit.File)); } catch { }
            items.Remove(hit);
            SaveImportItems(items);
            bool removedLearn;
            lock (_alLock) removedLearn = _audioLearn.Remove("import:" + hit.Id);
            if (removedLearn) SaveAudioLearn();
            ServeJson(ctx, "{\"ok\":true}");
        }
        catch (Exception ex) { ServeJson(ctx, JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
    }

    private static void ServeImportMedia(HttpListenerContext ctx, string rel)
    {
        try
        {
            string root = Path.GetFullPath(ImportDir).TrimEnd('\\') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(ImportDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            ctx.Response.ContentType = SniffMime(full);
            ctx.Response.AddHeader("Cache-Control", "public, max-age=3600");
            WriteFileWithRange(ctx, full);
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }
}
