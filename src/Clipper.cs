// Clipper: trim, compress and GIF videos with ffmpeg.
// C# 5 / .NET Framework 4. Built by build.ps1 with the csc.exe that ships with Windows.
// The exe runs a small HTTP server on localhost and shows ui/index.html in an Edge app window.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class App
{
    static string DataDir, SettingsFile, PortFile, TempDir, Ffmpeg, Ffprobe, Token, ExeDir;
    static int Port;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly object Gate = new object();
    static readonly Queue<string> Pending = new Queue<string>();
    static Process Edge;
    static DateTime LastPing = DateTime.Now;

    // Current ffmpeg job.
    static Process Job;
    static bool JobCancel;
    static string JobState = "idle", JobError = "", JobOut = "";
    static double JobDone, JobTotal, JobPassOffset;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    [STAThread]
    static void Main(string[] args)
    {
        ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipper");
        Directory.CreateDirectory(DataDir);
        SettingsFile = Path.Combine(DataDir, "settings.json");
        PortFile = Path.Combine(DataDir, "port.txt");
        TempDir = Path.Combine(Path.GetTempPath(), "Clipper");
        Directory.CreateDirectory(TempDir);
        string arg = args.Length > 0 ? args[0] : null;

        bool first;
        var mutex = new Mutex(true, "Local\\Clipper-single-instance", out first);
        if (!first)
        {
            // Already running: hand the file to that window.
            try
            {
                var lines = File.ReadAllLines(PortFile);
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers["X-Token"] = lines[1];
                    wc.UploadString("http://localhost:" + lines[0] + "/api/push", arg ?? "");
                }
            }
            catch { }
            return;
        }

        if (arg != null) Pending.Enqueue(Path.GetFullPath(arg));
        FindFfmpeg();
        Token = Guid.NewGuid().ToString("N");

        var listener = StartListener();
        File.WriteAllLines(PortFile, new[] { Port.ToString(), Token });
        new Thread(() =>
        {
            while (true)
            {
                try { var c = listener.GetContext(); ThreadPool.QueueUserWorkItem(_ => Handle(c)); }
                catch { break; }
            }
        }) { IsBackground = true }.Start();

        string url = "http://localhost:" + Port + "/?t=" + Token;
        string edge = FindEdge();
        DateTime started = DateTime.Now;
        if (edge != null)
        {
            string profile = Path.Combine(DataDir, "window");
            Edge = Process.Start(new ProcessStartInfo(edge,
                "--app=\"" + url + "\" --user-data-dir=\"" + profile + "\" --window-size=1240,800 " +
                "--no-first-run --no-default-browser-check --disable-extensions --disable-sync " +
                "--disable-background-networking --disable-component-update --disable-default-apps " +
                "--renderer-process-limit=1 --disable-features=Translate,msEdgeSidebarV2,msShoppingExp,msEdgeCollections,EdgeCollections,AutofillServerCommunication"));
            Edge.WaitForExit();
        }
        else Process.Start(url);

        // If Edge handed the window to another process, stay up while the page keeps pinging.
        if (edge == null || (DateTime.Now - started).TotalSeconds < 5)
            while ((DateTime.Now - LastPing).TotalSeconds < 35) Thread.Sleep(5000);

        KillJob();
        try { File.Delete(PortFile); } catch { }
        GC.KeepAlive(mutex);
    }

    static HttpListener StartListener()
    {
        for (int p = 47950; p < 48050; p++)
        {
            var l = new HttpListener();
            l.Prefixes.Add("http://localhost:" + p + "/");
            try { l.Start(); Port = p; return l; }
            catch { l.Close(); }
        }
        throw new Exception("No free port");
    }

    static string FindEdge()
    {
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA" })
        {
            var b = Environment.GetEnvironmentVariable(env);
            if (b == null) continue;
            var p = Path.Combine(b, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static void FindFfmpeg()
    {
        var dirs = new List<string> { ExeDir, Path.Combine(ExeDir, "ffmpeg") };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links"));
        try
        {
            var pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
            foreach (var d in Directory.GetDirectories(pk, "*FFmpeg*"))
                foreach (var f in Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories)) dirs.Add(Path.GetDirectoryName(f));
        }
        catch { }
        foreach (var d in dirs)
        {
            try
            {
                if (d.Trim() == "") continue;
                var f = Path.Combine(d.Trim(), "ffmpeg.exe");
                var p = Path.Combine(d.Trim(), "ffprobe.exe");
                if (File.Exists(f) && File.Exists(p)) { Ffmpeg = f; Ffprobe = p; return; }
            }
            catch { }
        }
    }

    // ---------- HTTP ----------

    static void Handle(HttpListenerContext c)
    {
        var req = c.Request;
        var res = c.Response;
        string path = req.Url.AbsolutePath;
        try
        {
            if (path == "/" || path == "/index.html") { SendBytes(res, "text/html; charset=utf-8", Page()); return; }
            if (path == "/icon.png") { SendBytes(res, "image/png", Resource("icon.png")); return; }
            if (req.QueryString["t"] != Token && req.Headers["X-Token"] != Token) { res.StatusCode = 403; res.Close(); return; }
            var q = req.QueryString;
            switch (path)
            {
                case "/media": Media(req, res, q["path"]); return;
                case "/api/ping": LastPing = DateTime.Now; SendJson(res, new { ok = true }); return;
                case "/api/init":
                    SendJson(res, new
                    {
                        ffmpeg = Ffmpeg != null,
                        videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                        settings = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "{}"
                    });
                    return;
                case "/api/push":
                    {
                        var p = Body(req).Trim();
                        lock (Gate) if (p != "") Pending.Enqueue(p);
                        FocusWindow();
                        SendJson(res, new { ok = true });
                        return;
                    }
                case "/api/pending":
                    {
                        string p = null;
                        lock (Gate) if (Pending.Count > 0) p = Pending.Dequeue();
                        SendJson(res, new { path = p });
                        return;
                    }
                case "/api/settings":
                    if (req.HttpMethod == "POST") File.WriteAllText(SettingsFile, Body(req));
                    SendJson(res, new { ok = true });
                    return;
                case "/api/open":
                    {
                        string picked = null;
                        OnSta(() =>
                        {
                            using (var owner = Owner())
                            using (var d = new OpenFileDialog())
                            {
                                d.Title = "Open a video";
                                d.Filter = "Videos|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.wmv;*.flv;*.gif;*.mpg;*.mpeg;*.ts;*.3gp|All files|*.*";
                                if (q["dir"] != null && Directory.Exists(q["dir"])) d.InitialDirectory = q["dir"];
                                if (d.ShowDialog(owner) == DialogResult.OK) picked = d.FileName;
                            }
                        });
                        SendJson(res, new { path = picked });
                        return;
                    }
                case "/api/saveas":
                    {
                        string picked = null;
                        OnSta(() =>
                        {
                            using (var owner = Owner())
                            using (var d = new SaveFileDialog())
                            {
                                d.Title = "Choose where to save";
                                string ext = Path.GetExtension(q["name"] ?? ".mp4");
                                d.Filter = (ext == ".gif" ? "GIF|*.gif" : "MP4 video|*.mp4") + "|All files|*.*";
                                d.FileName = q["name"];
                                if (q["dir"] != null && Directory.Exists(q["dir"])) d.InitialDirectory = q["dir"];
                                if (d.ShowDialog(owner) == DialogResult.OK) picked = d.FileName;
                            }
                        });
                        SendJson(res, new { path = picked });
                        return;
                    }
                case "/api/probe":
                    {
                        var r = RunCapture(Ffprobe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", q["path"] });
                        var f = new FileInfo(q["path"]);
                        SendJson(res, new { probe = r, size = f.Exists ? f.Length : 0 });
                        return;
                    }
                case "/api/locate": SendJson(res, new { path = Locate(q["name"], long.Parse(q["size"] ?? "0")) }); return;
                case "/api/upload":
                    {
                        var dest = Path.Combine(TempDir, Path.GetFileName(q["name"]));
                        using (var o = File.Create(dest)) req.InputStream.CopyTo(o);
                        SendJson(res, new { path = dest });
                        return;
                    }
                case "/api/unique": SendJson(res, new { path = Unique(q["path"]) }); return;
                case "/api/run": Run(Body(req)); SendJson(res, new { ok = true }); return;
                case "/api/progress":
                    lock (Gate)
                    {
                        long size = 0;
                        try { if (JobOut != "" && File.Exists(JobOut)) size = new FileInfo(JobOut).Length; } catch { }
                        SendJson(res, new { state = JobState, done = JobDone, total = JobTotal, error = JobError, output = JobOut, size = size });
                    }
                    return;
                case "/api/cancel": JobCancel = true; KillJob(); SendJson(res, new { ok = true }); return;
                case "/api/reveal":
                    Process.Start("explorer.exe", "/select,\"" + q["path"] + "\"");
                    SendJson(res, new { ok = true });
                    return;
                case "/api/launch": Process.Start(q["path"]); SendJson(res, new { ok = true }); return;
            }
            res.StatusCode = 404; res.Close();
        }
        catch (Exception e)
        {
            try { res.StatusCode = 500; SendBytes(res, "text/plain", Encoding.UTF8.GetBytes(e.Message)); } catch { }
        }
    }

    static byte[] Page()
    {
        // Dev: a ui/index.html next to the exe wins, so the page can change without a rebuild.
        foreach (var p in new[] { Path.Combine(ExeDir, "ui\\index.html"), Path.Combine(ExeDir, "..\\ui\\index.html") })
            if (File.Exists(p)) return File.ReadAllBytes(p);
        return Resource("index.html");
    }

    static byte[] Resource(string name)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); }
    }

    static string Body(HttpListenerRequest r)
    {
        using (var s = new StreamReader(r.InputStream, Encoding.UTF8)) return s.ReadToEnd();
    }

    static void SendJson(HttpListenerResponse res, object o)
    {
        SendBytes(res, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Serialize(o)));
    }

    static void SendBytes(HttpListenerResponse res, string type, byte[] b)
    {
        res.ContentType = type;
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = b.Length;
        res.OutputStream.Write(b, 0, b.Length);
        res.Close();
    }

    // Streams a local file with Range support so the <video> preview can seek.
    static void Media(HttpListenerRequest req, HttpListenerResponse res, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var types = new Dictionary<string, string> {
            { ".mp4", "video/mp4" }, { ".m4v", "video/mp4" }, { ".mov", "video/mp4" }, { ".webm", "video/webm" },
            { ".mkv", "video/webm" }, { ".gif", "image/gif" }, { ".ogg", "video/ogg" } };
        string type;
        if (!types.TryGetValue(ext, out type)) type = "application/octet-stream";
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long len = fs.Length, start = 0, end = len - 1;
            var range = req.Headers["Range"];
            res.ContentType = type;
            res.Headers["Accept-Ranges"] = "bytes";
            if (range != null && range.StartsWith("bytes="))
            {
                var parts = range.Substring(6).Split('-');
                if (parts[0] != "") start = long.Parse(parts[0]);
                if (parts.Length > 1 && parts[1] != "") end = Math.Min(long.Parse(parts[1]), len - 1);
                res.StatusCode = 206;
                res.Headers["Content-Range"] = "bytes " + start + "-" + end + "/" + len;
            }
            res.ContentLength64 = end - start + 1;
            fs.Position = start;
            var buf = new byte[1 << 16];
            long left = end - start + 1;
            try
            {
                while (left > 0)
                {
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    res.OutputStream.Write(buf, 0, n);
                    left -= n;
                }
            }
            catch { }
            try { res.Close(); } catch { }
        }
    }

    // A file dropped into the window has no path. Look for it by name and size in the usual places.
    static string Locate(string name, long size)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string> {
            Path.Combine(home, "Downloads"), Path.Combine(home, "Desktop"), Path.Combine(home, "Videos"),
            Path.Combine(home, "Documents"), Path.Combine(home, "Pictures"), Path.Combine(home, "OneDrive"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots)
        {
            var hit = Search(r, name, size, 4, seen);
            if (hit != null) return hit;
        }
        return null;
    }

    static string Search(string dir, string name, long size, int depth, HashSet<string> seen)
    {
        if (depth < 0 || !Directory.Exists(dir) || !seen.Add(dir)) return null;
        try
        {
            var f = Path.Combine(dir, name);
            if (File.Exists(f) && new FileInfo(f).Length == size) return f;
            foreach (var d in Directory.GetDirectories(dir))
            {
                var n = Path.GetFileName(d);
                if (n.StartsWith(".") || n == "node_modules" || n == "AppData") continue;
                var hit = Search(d, name, size, depth - 1, seen);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(p)) return p;
        }
    }

    // ---------- Dialogs ----------

    static void OnSta(Action a)
    {
        var t = new Thread(() => { try { a(); } catch { } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    // A hidden topmost form so dialogs open in front of the app window.
    static Form Owner()
    {
        var f = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.CenterScreen, Width = 1, Height = 1 };
        f.Show();
        f.Activate();
        return f;
    }

    static void FocusWindow()
    {
        try
        {
            if (Edge == null) return;
            Edge.Refresh();
            var h = Edge.MainWindowHandle;
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9);
            SetForegroundWindow(h);
        }
        catch { }
    }

    // ---------- ffmpeg ----------

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (char ch in a)
        {
            if (ch == '\\') { bs++; continue; }
            if (ch == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
            sb.Append('\\', bs); bs = 0; sb.Append(ch);
        }
        sb.Append('\\', bs * 2).Append('"');
        return sb.ToString();
    }

    static string Join(IEnumerable<string> args) { return string.Join(" ", args.Select(Quote)); }

    static string RunCapture(string exe, string[] args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, Join(args))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        });
        p.ErrorDataReceived += (s, e) => { };
        p.BeginErrorReadLine();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    // Body: { passes: [[args...], ...], output: "path", total: seconds-per-pass }
    static void Run(string body)
    {
        var spec = Json.Deserialize<Dictionary<string, object>>(body);
        var passes = ((System.Collections.ArrayList)spec["passes"]).Cast<System.Collections.ArrayList>()
            .Select(a => a.Cast<object>().Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
        string output = (string)spec["output"];
        double total = Convert.ToDouble(spec["total"], System.Globalization.CultureInfo.InvariantCulture);
        lock (Gate)
        {
            if (JobState == "running") throw new Exception("Already exporting");
            JobState = "running"; JobError = ""; JobOut = output; JobDone = 0; JobTotal = total * passes.Count; JobCancel = false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        new Thread(() =>
        {
            var tail = new Queue<string>();
            try
            {
                for (int i = 0; i < passes.Count; i++)
                {
                    JobPassOffset = total * i;
                    var args = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
                    args.AddRange(passes[i]);
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo(Ffmpeg, Join(args))
                        {
                            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                            RedirectStandardError = true, WorkingDirectory = TempDir
                        }
                    };
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        lock (tail) { tail.Enqueue(e.Data); if (tail.Count > 12) tail.Dequeue(); }
                    };
                    p.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null || !e.Data.StartsWith("out_time_us=")) return;
                        long us;
                        if (long.TryParse(e.Data.Substring(12), out us)) JobDone = JobPassOffset + us / 1e6;
                    };
                    lock (Gate) Job = p;
                    p.Start();
                    p.BeginErrorReadLine();
                    p.BeginOutputReadLine();
                    p.WaitForExit();
                    if (JobCancel) throw new OperationCanceledException();
                    if (p.ExitCode != 0)
                    {
                        lock (tail) throw new Exception(string.Join("\n", tail.Where(l => !l.StartsWith("  ")).ToArray()));
                    }
                }
                lock (Gate) { JobDone = JobTotal; JobState = "done"; Job = null; }
            }
            catch (OperationCanceledException)
            {
                try { File.Delete(output); } catch { }
                lock (Gate) { JobState = "cancelled"; Job = null; }
            }
            catch (Exception e)
            {
                try { File.Delete(output); } catch { }
                lock (Gate) { JobState = "error"; JobError = e.Message; Job = null; }
            }
        }) { IsBackground = true }.Start();
    }

    static void KillJob()
    {
        lock (Gate)
        {
            try { if (Job != null && !Job.HasExited) Job.Kill(); } catch { }
        }
    }
}
