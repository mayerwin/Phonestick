using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

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

        public static string Adb => Find("adb.exe", new[]
        {
            Path.Combine(Local, "Android", "Sdk", "platform-tools"),
            Path.Combine(Local, "Microsoft", "WinGet", "Links"),
        }, "Google.PlatformTools*", "Genymobile.scrcpy*");

        public static string Rclone => Find("rclone.exe", new[]
        {
            Path.Combine(Local, "Microsoft", "WinGet", "Links"),
        }, "Rclone.Rclone*");

        public static string Scrcpy => Find("scrcpy.exe", new[]
        {
            Path.Combine(Local, "Microsoft", "WinGet", "Links"),
        }, "Genymobile.scrcpy*");

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
            var candidates = new List<string> { AppDomain.CurrentDomain.BaseDirectory };
            candidates.AddRange(dirs);
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
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
            var pkgRoot = Path.Combine(Local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgRoot))
            {
                foreach (var pattern in wingetPackages)
                {
                    foreach (var pkg in Directory.GetDirectories(pkgRoot, pattern))
                    {
                        var hit = Directory.GetFiles(pkg, exe, SearchOption.AllDirectories)
                            .OrderByDescending(f => f).FirstOrDefault();
                        if (hit != null) return hit;
                    }
                }
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
            {
                var o = new StringBuilder();
                var e = new StringBuilder();
                p.OutputDataReceived += (s, a) => { if (a.Data != null) lock (o) o.AppendLine(a.Data); };
                p.ErrorDataReceived += (s, a) => { if (a.Data != null) lock (e) e.AppendLine(a.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    r.Exit = -1;
                    r.Err = "timed out";
                    return r;
                }
                p.WaitForExit();
                r.Exit = p.ExitCode;
                lock (o) r.Out = o.ToString().Trim();
                lock (e) r.Err = (r.Err + e).Trim();
            }
            return r;
        }

        /// <summary>Quotes one command-line argument for Windows programs.</summary>
        public static string Q(string s) =>
            s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 && s.Length > 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
