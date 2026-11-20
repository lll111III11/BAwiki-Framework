using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BAudioExclusive
{

    internal static class Program
    {
        const int PORT = 8237;
        const int RATE = 48000;
        static readonly object gate = new object();
        static Process ff;
        static WasapiOut output;
        static BufferedWaveProvider buffer;
        static Thread pump;
        static volatile bool stopFlag;
        static string lastError = "";
        static string lastMode = "";
        static string currentUrl = "";
        static string ffmpegPath = "ffmpeg.exe";

        static int Main(string[] args)
        {
            foreach (var a in args)
            {
                if (a.StartsWith("--port=")) { }
                if (a.StartsWith("--ffmpeg=")) ffmpegPath = a.Substring(9);
            }
            var hl = new HttpListener();
            hl.Prefixes.Add("http://127.0.0.1:" + PORT + "/");
            try { hl.Start(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[错误] 端口 " + PORT + " 监听失败：" + ex.Message);
                return 1;
            }
            Console.Title = "BA WASAPI 独占音频引擎 · 127.0.0.1:" + PORT;
            Console.WriteLine("BA WASAPI 独占音频引擎");
            Console.WriteLine(" 地址: http://127.0.0.1:" + PORT + "/");
            Console.WriteLine(" 设备: " + SafeDeviceName());
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = hl.GetContext(); } catch { break; }
                try { Handle(ctx); }
                catch (Exception ex) { WriteJson(ctx, 500, "{\"ok\":false,\"error\":" + J(ex.Message) + "}"); }
            }
            return 0;
        }

        static string SafeDeviceName()
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return d.FriendlyName;
            }
            catch (Exception ex) { return "不可用：" + ex.Message; }
        }

        static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (ctx.Request.HttpMethod == "OPTIONS") { Cors(ctx.Response); ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }
            if (path == "/" || path == "/health")
            {
                string mode = "";
                lock (gate) mode = lastMode;
                WriteJson(ctx, 200, "{\"ok\":true,\"name\":\"BA WASAPI Exclusive\",\"device\":" + J(SafeDeviceName()) +
                    ",\"playing\":" + (IsPlaying() ? "true" : "false") + ",\"mode\":" + J(mode) +
                    ",\"error\":" + J(lastError) + "}");
                return;
            }
            if (path == "/play")
            {
                var body = ReadBody(ctx.Request);
                string url = Field(body, "url");
                if (string.IsNullOrEmpty(url) || (!url.StartsWith("http://127.0.0.1") && !url.StartsWith("http://localhost")))
                { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"只接受本机 http 地址\"}"); return; }
                string err = Start(url);
                WriteJson(ctx, err == null ? 200 : 500,
                    "{\"ok\":" + (err == null ? "true" : "false") + ",\"mode\":" + J(lastMode) + ",\"error\":" + J(err ?? "") + "}");
                return;
            }
            if (path == "/stop") { Stop(); WriteJson(ctx, 200, "{\"ok\":true}"); return; }
            WriteJson(ctx, 404, "{\"ok\":false,\"error\":\"no route\"}");
        }

        static bool IsPlaying()
        {
            lock (gate) return output != null && output.PlaybackState == PlaybackState.Playing;
        }

        static string Start(string url)
        {
            Stop();
            lastError = ""; lastMode = "";
            try
            {
                using var en = new MMDeviceEnumerator();
                var device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var wf = new WaveFormat(RATE, 16, 2);
                buffer = new BufferedWaveProvider(wf) { BufferDuration = TimeSpan.FromSeconds(20), DiscardOnBufferOverflow = true };
                try
                {
                    output = new WasapiOut(device, AudioClientShareMode.Exclusive, true, 60);
                    output.Init(buffer);
                    lastMode = "exclusive";
                }
                catch (Exception ex1)
                {

                    try { output?.Dispose(); } catch { }
                    output = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
                    output.Init(buffer);
                    lastMode = "shared-fallback";
                    lastError = ex1.Message;
                }
                output.Play();

                var psi = new ProcessStartInfo(ffmpegPath,
                    "-hide_banner -loglevel error -i \"" + url + "\" -f s16le -ac 2 -ar " + RATE + " -")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                ff = Process.Start(psi);
                currentUrl = url;
                stopFlag = false;
                pump = new Thread(() =>
                {
                    try
                    {
                        using var s = ff.StandardOutput.BaseStream;
                        var buf = new byte[16384];
                        int n;
                        while (!stopFlag && (n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            lock (gate) buffer?.AddSamples(buf, 0, n);
                        }
                    }
                    catch (Exception ex) { lastError = ex.Message; }
                    finally
                    {

                        try { Thread.Sleep(400); } catch { }
                    }
                }) { IsBackground = true };
                pump.Start();
                return null;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                return ex.Message;
            }
        }

        static void Stop()
        {
            stopFlag = true;
            try { if (ff != null && !ff.HasExited) ff.Kill(); } catch { }
            try { ff?.Dispose(); } catch { }
            ff = null;
            try { output?.Stop(); } catch { }
            try { output?.Dispose(); } catch { }
            output = null;
            lock (gate) { try { buffer?.ClearBuffer(); } catch { } buffer = null; }
            currentUrl = "";
        }

        static string ReadBody(HttpListenerRequest req)
        {
            try
            {
                using var s = req.InputStream;
                using var r = new StreamReader(s, Encoding.UTF8);
                return r.ReadToEnd();
            }
            catch { return ""; }
        }

        static string Field(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key);
            if (i < 0) return "";
            i = json.IndexOf(':', i + key.Length);
            if (i < 0) return "";
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return "";
            if (json[i] == '"')
            {
                int j = i + 1;
                var sb = new StringBuilder();
                while (j < json.Length && json[j] != '"')
                {
                    if (json[j] == '\\' && j + 1 < json.Length) { sb.Append(json[j + 1]); j += 2; }
                    else sb.Append(json[j++]);
                }
                return sb.ToString();
            }
            int k = i;
            while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '-' || json[k] == '.')) k++;
            return json.Substring(i, k - i);
        }

        static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static void Cors(HttpListenerResponse r)
        {
            r.Headers["Access-Control-Allow-Origin"] = "*";
            r.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
            r.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        }

        static void WriteJson(HttpListenerContext ctx, int code, string json)
        {
            Cors(ctx.Response);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var b = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.Close();
        }
    }
}
