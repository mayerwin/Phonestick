using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace Phonestick
{
    /// <summary>
    /// Self-updater for the single-file exe, from the project's GitHub releases.
    ///
    /// Checks /releases/latest (at most once a month by default, and only when the app is started or
    /// its panel is opened: no timer), downloads Phonestick.exe into Phonestick.config\update, checks
    /// its size and the SHA-256 digest GitHub publishes for the asset, then swaps it in: a running
    /// exe cannot be overwritten but can be moved, so the old one moves into the update folder, the
    /// new one takes its place, starts, and the old process exits. Nothing is ever left next to the
    /// exe; the update folder is emptied on the next start. Any failure rolls the swap back.
    /// </summary>
    internal static class Updater
    {
        private const string Owner = "mayerwin", Repo = AppInfo.Name, Asset = AppInfo.Name + ".exe";
        public static readonly TimeSpan Interval = TimeSpan.FromDays(30);
        public static string ReleasesPage => "https://github.com/" + Owner + "/" + Repo + "/releases/latest";

        public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version);
        public static string CurrentText => Current.ToString(3);

        private static string Dir => Path.Combine(Config.Dir, "update");
        private static string ExePath => Process.GetCurrentProcess().MainModule.FileName;

        internal sealed class Info
        {
            public Version Version;
            public string Tag, Url, Sha256, Page;
            public long Size;
            public string VersionText => Version.ToString(3);
        }

        /// <summary>The newer release, or null when this build is current. Throws on network errors.</summary>
        public static Info Check()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var http = NewClient(TimeSpan.FromSeconds(20)))
            {
                var json = http.GetStringAsync("https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest").Result;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
                var tag = root.TryGetValue("tag_name", out var t) ? t as string : null;
                var v = ParseTag(tag);
                if (v == null || v <= Current) return null;
                if (!(root.TryGetValue("assets", out var a) && a is IEnumerable assets)) return null;
                foreach (var o in assets)
                {
                    if (!(o is Dictionary<string, object> asset) || !Equals(asset.TryGetValue("name", out var n) ? n : null, Asset)) continue;
                    var digest = asset.TryGetValue("digest", out var d) ? d as string : null;
                    return new Info
                    {
                        Version = v,
                        Tag = tag,
                        Url = asset["browser_download_url"] as string,
                        Size = Convert.ToInt64(asset.TryGetValue("size", out var s) ? s : 0),
                        Sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7).Trim() : null,
                        Page = root.TryGetValue("html_url", out var h) ? h as string ?? ReleasesPage : ReleasesPage,
                    };
                }
                return null; // a release without the exe: nothing to install
            }
        }

        /// <summary>Whether this copy can replace itself (its folder must be writable).</summary>
        public static bool CanInstall(out string reason)
        {
            reason = "";
            try
            {
                var exe = ExePath;
                var dir = Path.GetDirectoryName(exe);
                // Config.Dir is the sibling folder only when the exe's folder is writable.
                if (!string.Equals(Path.GetDirectoryName(Config.Dir.TrimEnd('\\')), dir, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Windows does not let " + AppInfo.Name + " write to its own folder.";
                    return false;
                }
                return true;
            }
            catch (Exception ex) { reason = ex.Message; return false; }
        }

        /// <summary>Downloads and verifies the new exe, then swaps it in. The caller starts it and exits.</summary>
        public static void Install(Info info, Action<string> log)
        {
            var exe = ExePath;
            Directory.CreateDirectory(Dir);
            var fresh = Path.Combine(Dir, Asset);
            var old = Path.Combine(Dir, AppInfo.Name + ".old.exe");
            if (File.Exists(fresh)) File.Delete(fresh);

            log("Downloading " + AppInfo.Name + " " + info.VersionText + "...");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var http = NewClient(TimeSpan.FromMinutes(5)))
            {
                var bytes = http.GetByteArrayAsync(info.Url).Result;
                if (info.Size > 0 && bytes.LongLength != info.Size)
                    throw new UserError("The download was incomplete. Nothing was changed; try again later.");
                if (info.Sha256 != null)
                {
                    using (var sha = SHA256.Create())
                    {
                        var got = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                        if (!string.Equals(got, info.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new UserError("The download did not match its published checksum. Nothing was changed.");
                    }
                }
                File.WriteAllBytes(fresh, bytes);
            }

            log("Installing...");
            if (File.Exists(old)) File.Delete(old); // fails only if an old process still runs
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch
            {
                try { File.Move(old, exe); } catch { }
                throw;
            }
            Log.Write("updated to " + info.VersionText);
        }

        /// <summary>Starts the new exe; it waits for this process to exit before taking over the tray.</summary>
        public static void Relaunch()
        {
            Process.Start(new ProcessStartInfo(ExePath, "--after-update " + Process.GetCurrentProcess().Id)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(ExePath),
            });
        }

        /// <summary>Empties the update folder (the previous exe and any partial download). Called at start.</summary>
        public static void Cleanup()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
        }

        public static void OpenReleasePage(string url = null)
        {
            try { Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true }); } catch { }
        }

        private static HttpClient NewClient(TimeSpan timeout)
        {
            var c = new HttpClient { Timeout = timeout };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.Name + "/" + CurrentText); // GitHub requires one
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        private static bool Flag(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is bool b && b;

        private static Version Normalize(Version v) =>
            v == null ? new Version(0, 0, 0) : new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

        /// <summary>"v1.4.0" or "1.4" (anything after - or + ignored) to a Version, or null.</summary>
        internal static Version ParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var s = tag.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (!s.Contains(".")) s += ".0";
            return Version.TryParse(s, out var v) ? Normalize(v) : null;
        }
    }
}
