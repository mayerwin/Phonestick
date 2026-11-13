using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Phonestick
{
    /// <summary>
    /// Installs the helper programs Phonestick needs (scrcpy, which brings adb; rclone; WinFsp)
    /// with winget, the first time they are needed. Nothing to do by hand.
    /// </summary>
    internal static class Helpers
    {
        private static readonly object Gate = new object();

        private sealed class Helper
        {
            public string Id, Label;
            public Func<bool> Present;
        }

        private static readonly Helper[] All =
        {
            new Helper { Id = "Genymobile.scrcpy", Label = "scrcpy and adb", Present = () => Tools.Scrcpy != null && Tools.Adb != null },
            new Helper { Id = "Rclone.Rclone", Label = "rclone", Present = () => Tools.Rclone != null },
            new Helper { Id = "WinFsp.WinFsp", Label = "WinFsp (Windows may ask for permission)", Present = () => Tools.WinFspInstalled },
        };

        public static bool AllPresent => All.All(h => h.Present());

        /// <summary>Installs whatever is missing. Safe to call from several places: one install at a time.</summary>
        public static void Ensure(Action<string> log)
        {
            lock (Gate)
            {
                var missing = All.Where(h => !h.Present()).ToList();
                if (missing.Count == 0) return;
                var winget = FindWinget();
                if (winget == null)
                    throw new UserError("Phonestick needs a few free helper programs, and installs them with winget, " +
                        "which this computer does not have. Install \"App Installer\" from the Microsoft Store, then try again, " +
                        "or run:\nwinget install " + string.Join(" ", missing.Select(h => h.Id)));
                foreach (var h in missing)
                {
                    log("Installing " + h.Label + " (one time)...");
                    Log.Write("winget install " + h.Id);
                    var r = Tools.Run(winget, "install --id " + h.Id + " --exact --source winget --silent " +
                        "--accept-source-agreements --accept-package-agreements --disable-interactivity", 15 * 60 * 1000);
                    Log.Write("winget " + h.Id + " exit " + r.Exit);
                    if (!h.Present())
                        throw new UserError("Could not install " + h.Label + ".\n\n" + Tail(r.All) +
                            "\n\nYou can install it yourself with:\nwinget install " + h.Id);
                }
            }
        }

        private static string FindWinget()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var p = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(p)) return p;
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    var c = Path.Combine(d.Trim(), "winget.exe");
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return null;
        }

        private static string Tail(string s) => s.Length > 600 ? s.Substring(s.Length - 600) : s;

        /// <summary>Writes the phone app, embedded in this exe, to the data folder and returns its path.</summary>
        public static string ExtractApk()
        {
            Directory.CreateDirectory(Config.Dir);
            var path = Path.Combine(Config.Dir, AppInfo.Name + ".apk");
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Phonestick.Assets." + AppInfo.Name + ".apk"))
            {
                if (s == null) throw new UserError("This build does not contain the phone app.");
                using (var f = File.Create(path)) s.CopyTo(f);
            }
            return path;
        }
    }
}
