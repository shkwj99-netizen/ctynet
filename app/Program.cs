using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CtyNet
{
    public static class Program
    {
        private static long _lastBeat = 0;
        private static volatile bool _running = true;

        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void Main()
        {
            HideConsole();

            int port = 0;
            HttpListener listener = null;
            foreach (var cand in new[] { 7731, 7745, 7788, 8131, 8532, 9143 })
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{cand}/");
                    l.Start();
                    listener = l; port = cand; break;
                }
                catch { }
            }
            if (listener == null) return;

            _lastBeat = NowMs();
            OpenWindow($"http://127.0.0.1:{port}/");

            // 창이 닫히면(하트비트 끊기면) 스스로 종료
            new Thread(() =>
            {
                while (_running)
                {
                    Thread.Sleep(2000);
                    if (NowMs() - Interlocked.Read(ref _lastBeat) > 8000)
                    {
                        _running = false;
                        try { listener.Stop(); } catch { }
                        Environment.Exit(0);
                    }
                }
            })
            { IsBackground = true }.Start();

            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(ctx); } catch { } });
            }
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static void HideConsole()
        {
            try { if (OperatingSystem.IsWindows()) { var h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 0); } }
            catch { }
        }

        private static void OpenWindow(string url)
        {
            // 엣지 앱 모드(브라우저 테두리 없는 단독 창) 우선
            string[] edge = {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            };
            try
            {
                foreach (var e in edge)
                {
                    if (File.Exists(e))
                    {
                        Process.Start(new ProcessStartInfo(e, $"--app={url} --window-size=1280,820") { UseShellExecute = false });
                        return;
                    }
                }
            }
            catch { }
            // 안 되면 기본 브라우저
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            string path = req.Url.AbsolutePath;

            if (path == "/" || path == "") { ServeAsset("index.html", res); return; }
            if (path == "/api/alive") { Interlocked.Exchange(ref _lastBeat, NowMs()); WriteText(res, "ok", "text/plain"); return; }
            if (path == "/api/targets") { WriteJson(res, Net.Targets); return; }

            if (path == "/api/me")
            {
                var me = Net.WhoAmI().GetAwaiter().GetResult();
                WriteJson(res, me);
                return;
            }

            if (path == "/api/ping")
            {
                string host = req.QueryString["host"] ?? "";
                if (host.Length == 0) { res.StatusCode = 400; res.Close(); return; }
                var r = Net.PingHost(host);
                WriteJson(res, r);
                return;
            }

            if (path == "/api/trace")
            {
                string host = req.QueryString["host"] ?? "";
                if (host.Length == 0) { res.StatusCode = 400; res.Close(); return; }
                ServeTrace(host, res);
                return;
            }

            // 정적 파일
            string asset = path.TrimStart('/');
            if (asset.Length > 0 && !asset.Contains("..")) { ServeAsset(asset, res); return; }

            res.StatusCode = 404; res.Close();
        }

        // 경로 추적을 한 홉씩 스트리밍(SSE)
        private static void ServeTrace(string host, HttpListenerResponse res)
        {
            res.ContentType = "text/event-stream; charset=utf-8";
            res.Headers.Add("Cache-Control", "no-cache");
            res.SendChunked = true;
            var outStream = res.OutputStream;
            void Send(object o)
            {
                var json = JsonSerializer.Serialize(o, JsonOpts);
                var bytes = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
                outStream.Write(bytes, 0, bytes.Length);
                outStream.Flush();
            }
            try
            {
                Net.Trace(host, hop => { try { Send(hop); } catch { } });
                var endBytes = Encoding.UTF8.GetBytes("event: end\ndata: {}\n\n");
                outStream.Write(endBytes, 0, endBytes.Length);
                outStream.Flush();
            }
            catch { }
            finally { try { res.Close(); } catch { } }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = null,
            IncludeFields = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static void WriteJson(HttpListenerResponse res, object o)
        {
            var json = JsonSerializer.Serialize(o, JsonOpts);
            WriteText(res, json, "application/json; charset=utf-8");
        }

        private static void WriteText(HttpListenerResponse res, string text, string ctype)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = ctype;
            res.ContentLength64 = bytes.Length;
            try { res.OutputStream.Write(bytes, 0, bytes.Length); } catch { }
            try { res.Close(); } catch { }
        }

        private static void ServeAsset(string name, HttpListenerResponse res)
        {
            var asm = Assembly.GetExecutingAssembly();
            string match = null;
            string want = "web." + name.Replace('/', '.');
            foreach (var n in asm.GetManifestResourceNames())
                if (n.EndsWith(want, StringComparison.OrdinalIgnoreCase)) { match = n; break; }
            if (match == null) { res.StatusCode = 404; res.Close(); return; }
            using var rs = asm.GetManifestResourceStream(match);
            using var ms = new MemoryStream();
            rs.CopyTo(ms);
            var bytes = ms.ToArray();
            res.ContentType = MimeOf(name);
            res.ContentLength64 = bytes.Length;
            try { res.OutputStream.Write(bytes, 0, bytes.Length); } catch { }
            try { res.Close(); } catch { }
        }

        private static string MimeOf(string name)
        {
            if (name.EndsWith(".html")) return "text/html; charset=utf-8";
            if (name.EndsWith(".css")) return "text/css; charset=utf-8";
            if (name.EndsWith(".js")) return "application/javascript; charset=utf-8";
            if (name.EndsWith(".svg")) return "image/svg+xml";
            if (name.EndsWith(".png")) return "image/png";
            if (name.EndsWith(".woff2")) return "font/woff2";
            return "application/octet-stream";
        }
    }
}
