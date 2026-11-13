using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Phonestick
{
    /// <summary>
    /// Settings stored as key=value lines in settings.ini. All of Phonestick's data (settings, log,
    /// rclone config and cache) lives in a "Phonestick.config" folder next to the exe, so the app
    /// stays portable: delete the exe and that folder and nothing is left behind.
    /// </summary>
    internal sealed class Config
    {
        private static string _dir;
        public static string Dir => _dir ?? (_dir = ResolveDir());
        public static string LocalDir => Dir;
        private static string FilePath => Path.Combine(Dir, "settings.ini");

        /// <summary>The sibling folder, or %LOCALAPPDATA%\Phonestick when the exe sits somewhere read-only.</summary>
        private static string ResolveDir()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var sibling = Path.Combine(exeDir, AppInfo.Name + ".config");
            try
            {
                Directory.CreateDirectory(sibling);
                var probe = Path.Combine(sibling, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                MigrateOld(sibling);
                return sibling;
            }
            catch
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);
            }
        }

        /// <summary>Earlier versions kept settings in %APPDATA%\Phonestick: copy them over once.</summary>
        private static void MigrateOld(string dir)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "settings.ini"))) return;
                var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
                foreach (var name in new[] { "settings.ini", "rclone.conf" })
                {
                    var src = Path.Combine(old, name);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(dir, name), false);
                }
            }
            catch { }
        }

        /// <summary>ro.serialno of the phone set up with this computer. Other phones are ignored.</summary>
        public string PhoneSerial { get; set; } = "";
        public string PhoneModel { get; set; } = "";
        /// <summary>Last ip:port that worked, a fallback when mDNS is slow to announce the phone.</summary>
        public string LastEndpoint { get; set; } = "";
        public string Drive { get; set; } = "P:";
        /// <summary>Minutes without file activity before the drive is disconnected. 0 = never.</summary>
        public int IdleMinutes { get; set; } = 60;
        public bool FirstRunDone { get; set; }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                var d = File.ReadAllLines(FilePath)
                    .Select(l => l.Split(new[] { '=' }, 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                if (d.TryGetValue("PhoneSerial", out var s)) c.PhoneSerial = s;
                if (d.TryGetValue("PhoneModel", out var m)) c.PhoneModel = m;
                if (d.TryGetValue("LastEndpoint", out var le)) c.LastEndpoint = le;
                if (d.TryGetValue("Drive", out var dr) && dr.Length >= 1) c.Drive = dr.Substring(0, 1).ToUpperInvariant() + ":";
                if (d.TryGetValue("IdleMinutes", out var im) && int.TryParse(im, out var i) && i >= 0) c.IdleMinutes = i;
                if (d.TryGetValue("FirstRunDone", out var f)) c.FirstRunDone = f == "1";
            }
            catch { /* defaults */ }
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string>
            {
                "PhoneSerial=" + PhoneSerial,
                "PhoneModel=" + PhoneModel,
                "LastEndpoint=" + LastEndpoint,
                "Drive=" + Drive,
                "IdleMinutes=" + IdleMinutes,
                "FirstRunDone=" + (FirstRunDone ? "1" : "0"),
            };
            File.WriteAllLines(FilePath, lines);
        }
    }
}
