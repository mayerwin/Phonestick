using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Phonestick
{
    internal sealed class RunResult
    {
        public int Exit;
        public string Out = "";
        public string Err = "";
        public bool Ok => Exit == 0;
        public string All => (Out + "\n" + Err).Trim();
    }

    /// <summary>Finds the helper programs (adb, rclone, scrcpy) and runs them.</summary>
    internal static class Tools
    {
        private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string Programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        /// <summary>winget's link folders, per user and per machine ("--scope machine").</summary>
        private static readonly string[] WingetLinks =
        {
            Path.Combine(Local, "Microsoft", "WinGet", "Links"),
            Path.Combine(Programs, "WinGet", "Links"),
        };

        private static readonly string[] WingetPackages =
        {
            Path.Combine(Local, "Microsoft", "WinGet", "Packages"),
            Path.Combine(Programs, "WinGet", "Packages"),
        };

        public static string Adb => Find("adb.exe",
            new[] { Path.Combine(Local, "Android", "Sdk", "platform-tools") }.Concat(WingetLinks),
            "Google.PlatformTools*", "Genymobile.scrcpy*");

        public static string Rclone => Find("rclone.exe", WingetLinks, "Rclone.Rclone*");

        public static string Scrcpy => Find("scrcpy.exe", WingetLinks, "Genymobile.scrcpy*");

        /// <summary>Last path found per exe: reused while the file is still there (no rescans per adb call).</summary>
        private static readonly Dictionary<string, string> Found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// PATH as Windows has it now (machine + user), not as it was when this process started:
        /// a helper installed meanwhile is found without a restart.
        /// </summary>
        private static IEnumerable<string> CurrentPath()
        {
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
            {
                string v = null;
                try { v = Environment.GetEnvironmentVariable("PATH", target); } catch { }
                foreach (var d in (v ?? "").Split(';'))
                {
                    string e = null;
                    try { e = Environment.ExpandEnvironmentVariables(d); } catch { }
                    if (!string.IsNullOrWhiteSpace(e)) yield return e;
                }
            }
        }

        public static bool WinFspInstalled
        {
            get
            {
                foreach (var pf in new[] { "ProgramFiles(x86)", "ProgramFiles" })
                {
                    var root = Environment.GetEnvironmentVariable(pf);
                    if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "WinFsp", "bin", "winfsp-x64.dll")))
                        return true;
                }
                return false;
            }
        }

        /// <summary>Next to this exe, then the usual folders, then PATH, then winget package folders.</summary>
        private static string Find(string exe, IEnumerable<string> dirs, params string[] wingetPackages)
        {
            lock (Found)
            {
                if (Found.TryGetValue(exe, out var known) && File.Exists(known)) return known;
                var hit = Search(exe, dirs, wingetPackages);
                if (hit != null) Found[exe] = hit;
                else Found.Remove(exe);
                return hit;
            }
        }

        private static string Search(string exe, IEnumerable<string> dirs, string[] wingetPackages)
        {
            var candidates = new List<string> { AppDomain.CurrentDomain.BaseDirectory };
            candidates.AddRange(dirs);
            candidates.AddRange(CurrentPath());
            foreach (var d in candidates)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    var p = Path.Combine(d.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            foreach (var pkgRoot in WingetPackages)
            {
                // A folder being installed or removed, or one we may not read, must not throw.
                try
                {
                    if (!Directory.Exists(pkgRoot)) continue;
                    foreach (var pattern in wingetPackages)
                    {
                        foreach (var pkg in Directory.GetDirectories(pkgRoot, pattern))
                        {
                            try
                            {
                                var hit = Directory.GetFiles(pkg, exe, SearchOption.AllDirectories)
                                    .OrderByDescending(f => f).FirstOrDefault();
                                if (hit != null) return hit;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>Environment for every adb and scrcpy process: our private adb server port.</summary>
        public static void ApplyAdbEnv(ProcessStartInfo psi)
        {
            psi.EnvironmentVariables["ANDROID_ADB_SERVER_PORT"] = AppInfo.AdbPort.ToString();
            var adb = Adb;
            if (adb != null) psi.EnvironmentVariables["ADB"] = adb; // scrcpy uses this adb
        }

        public static RunResult Run(string exe, string args, int timeoutMs = 60000, Action<ProcessStartInfo> setup = null)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            ApplyAdbEnv(psi);
            setup?.Invoke(psi);
            var r = new RunResult();
            using (var p = new Process { StartInfo = psi })
            using (var outDone = new ManualResetEvent(false))
            using (var errDone = new ManualResetEvent(false))
            {
                var o = new StringBuilder();
                var e = new StringBuilder();
                p.OutputDataReceived += (s, a) => { if (a.Data != null) lock (o) o.AppendLine(a.Data); else try { outDone.Set(); } catch { } };
                p.ErrorDataReceived += (s, a) => { if (a.Data != null) lock (e) e.AppendLine(a.Data); else try { errDone.Set(); } catch { } };
                p.Start();
                // adb may start our private adb server: that server then belongs to the job too, so
                // the link to the phone also closes if Phonestick ends unexpectedly.
                if (string.Equals(Path.GetFileName(exe), "adb.exe", StringComparison.OrdinalIgnoreCase)) ChildJob.Add(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    KillTree(p);
                    r.Exit = -1;
                    r.Err = "timed out";
                    return r;
                }
                // Wait for the output to be read, but not for ever: a child process that inherited
                // the pipes (an installer, the adb server) can keep them open long after the exit.
                // (One handle at a time: WaitAll is not allowed on the UI thread.)
                var until = DateTime.UtcNow.AddSeconds(5);
                outDone.WaitOne(5000);
                errDone.WaitOne(Math.Max(0, (int)(until - DateTime.UtcNow).TotalMilliseconds));
                r.Exit = p.ExitCode;
                lock (o) r.Out = o.ToString().Trim();
                lock (e) r.Err = (r.Err + e).Trim();
            }
            return r;
        }

        /// <summary>Ends a process and everything it started (an installer's children too).</summary>
        private static void KillTree(Process p)
        {
            try
            {
                using (var t = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id)
                { UseShellExecute = false, CreateNoWindow = true }))
                    t?.WaitForExit(10000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        /// <summary>Quotes one command-line argument for Windows programs.</summary>
        public static string Q(string s) =>
            s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 && s.Length > 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
