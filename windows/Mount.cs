using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace Phonestick
{
    /// <summary>
    /// Mounts the phone's shared storage as a drive letter.
    ///
    /// The file server is rclone, run on the phone through the adb shell only while
    /// the drive is mounted. On the computer, rclone mount (with WinFsp) talks SFTP to
    /// it through adb (see AdbPipe), so no extra port or app is opened on the phone.
    /// </summary>
    internal sealed class Mount
    {
        private readonly object _sync = new object();
        private volatile Process _rclone;
        private DateTime _lastActive = DateTime.UtcNow;
        public Phone Phone { get; private set; }
        public string Drive { get; private set; }
        public bool IsMounted
        {
            get
            {
                var r = _rclone; // read once: Stop() clears it from another thread
                try { return r != null && !r.HasExited; } catch { return false; }
            }
        }
        public event Action Ended;

        public static string ServerScript => AppInfo.PhoneDir + "/sftp.sh";
        public static string SdServerScript => AppInfo.PhoneDir + "/sftp-sd.sh";

        public static string RcloneVersion()
        {
            var r = Tools.Run(Tools.Rclone, "version", 20000);
            var m = Regex.Match(r.Out, @"rclone (v\d+\.\d+\.\d+)");
            if (!m.Success) throw new UserError("Could not read the rclone version:\n" + r.All);
            return m.Groups[1].Value;
        }

        /// <summary>Puts a matching rclone build and the server script on the phone, once.</summary>
        public static void EnsurePhoneServer(Phone p, Action<string> log)
        {
            var version = RcloneVersion();
            var have = p.Shell(AppInfo.PhoneDir + "/rclone version 2>/dev/null | head -1", 20000).Out;
            if (!have.Contains(version))
            {
                var abi = p.Shell("getprop ro.product.cpu.abi", 10000).Out.Trim();
                string arch;
                if (abi.StartsWith("arm64")) arch = "linux-arm64";
                else if (abi.StartsWith("armeabi")) arch = "linux-arm-v7";
                else if (abi == "x86_64") arch = "linux-amd64";
                else if (abi == "x86") arch = "linux-386";
                else throw new UserError("Unsupported phone CPU: " + abi);

                var bin = DownloadRclone(version, arch, log);
                log("Copying the file server to the phone (one time)...");
                p.Shell("mkdir -p " + AppInfo.PhoneDir, 10000);
                var push = Phone.Adb("-s " + Tools.Q(p.Serial) + " push " + Tools.Q(bin) + " " + AppInfo.PhoneDir + "/rclone", 600000);
                if (!push.Ok) throw new UserError("Copy to the phone failed:\n" + push.All);
                p.Shell("chmod 755 " + AppInfo.PhoneDir + "/rclone", 10000);
            }
            // Serve the real path: /sdcard is a symlink, which rclone will not follow as a root.
            var root = p.Shell("readlink -f /sdcard", 10000).Out.Trim();
            if (!root.StartsWith("/")) root = "/storage/emulated/0";
            WriteServerScript(p, ServerScript, root);
        }

        private static void WriteServerScript(Phone p, string path, string root)
        {
            // --dir-cache-time 1s: the server must not hide changes made on the phone itself;
            // the computer side does its own caching.
            // stderr goes to /dev/null so log lines can never corrupt the SFTP stream.
            var script =
                "export RCLONE_CONFIG=" + AppInfo.PhoneDir + "/rclone.conf; " +
                "export RCLONE_CACHE_DIR=" + AppInfo.PhoneDir + "/cache; " +
                "exec " + AppInfo.PhoneDir + "/rclone serve sftp --stdio --dir-cache-time 1s " + root + " 2>/dev/null";
            p.Shell("echo '" + script + "' > " + path + " && touch " + AppInfo.PhoneDir + "/rclone.conf", 10000);
        }

        /// <summary>Downloads rclone for the phone from downloads.rclone.org and checks its SHA-256.</summary>
        private static string DownloadRclone(string version, string arch, Action<string> log)
        {
            var cache = Path.Combine(Config.LocalDir, "phone-rclone", version + "-" + arch);
            var bin = Path.Combine(cache, "rclone");
            // Only a fully extracted file is ever named "rclone" (extracted under a temp name, then renamed).
            if (File.Exists(bin) && new FileInfo(bin).Length > 1000000) return bin;
            Directory.CreateDirectory(cache);

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var name = "rclone-" + version + "-" + arch + ".zip";
            var baseUrl = "https://downloads.rclone.org/" + version + "/";
            log("Downloading " + name + "...");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                var sums = http.GetStringAsync(baseUrl + "SHA256SUMS").Result;
                var line = sums.Split('\n').FirstOrDefault(l => l.Trim().EndsWith(" " + name) || l.Trim().EndsWith("*" + name));
                if (line == null) throw new UserError("No checksum published for " + name);
                var expected = line.Trim().Split(' ')[0].ToLowerInvariant();

                var zipPath = Path.Combine(cache, name);
                var bytes = http.GetByteArrayAsync(baseUrl + name).Result;
                using (var sha = SHA256.Create())
                {
                    var actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    if (actual != expected) throw new UserError("Checksum mismatch for " + name + ". Not using it.");
                }
                File.WriteAllBytes(zipPath, bytes);
                using (var z = ZipFile.OpenRead(zipPath))
                {
                    var entry = z.Entries.First(e => e.Name == "rclone");
                    var tmp = bin + ".part";
                    entry.ExtractToFile(tmp, true);
                    if (File.Exists(bin)) File.Delete(bin);
                    File.Move(tmp, bin);
                }
                File.Delete(zipPath);
            }
            return bin;
        }

        /// <summary>One rclone mount: the phone's internal storage, or its SD card.</summary>
        private sealed class Vol
        {
            public Process Proc;
            public string Drive, RcBase, RcAuth;
            public long LastBytes = -1;
            public bool Alive { get { var r = Proc; try { return r != null && !r.HasExited; } catch { return false; } } }
        }

        private Vol _sd;

        /// <summary>The SD card's drive letter while it is mounted, else null.</summary>
        public string SdDrive { get { var v = _sd; return v != null && v.Alive ? v.Drive : null; } }

        /// <summary>
        /// The removable card's folder on the phone (a portable SD card or USB drive shows up as
        /// /storage/XXXX-XXXX), or null. The adb shell can read and write it.
        /// </summary>
        private static string FindCard(Phone p)
        {
            try
            {
                foreach (var name in p.Shell("ls /storage", 10000).Out.Split('\n').Select(x => x.Trim()))
                    if (Regex.IsMatch(name, @"^[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}$")) return "/storage/" + name;
            }
            catch (Exception ex) { Log.Write("SD card check: " + ex.Message); }
            return null;
        }

        /// <summary>The first free letter after the phone's drive, for the SD card.</summary>
        private static string NextFreeDrive(string after)
        {
            var used = DriveInfo.GetDrives().Select(d => d.Name.Substring(0, 2).ToUpperInvariant()).ToList();
            char c = after[0];
            for (int i = 0; i < 26; i++)
            {
                c = c >= 'Z' ? 'D' : (char)(c + 1);
                if (c + ":" == after) break;
                if (!used.Contains(c + ":")) return c + ":";
            }
            return null;
        }

        /// <param name="cancelled">Checked while starting: true stops and unmounts again.</param>
        public void Start(Phone p, Config cfg, Action<string> log, Func<bool> cancelled)
        {
            if (Tools.Rclone == null) throw new UserError("rclone was not found. Install it with:\nwinget install Rclone.Rclone");
            if (!Tools.WinFspInstalled) throw new UserError("WinFsp is needed to show the phone as a drive. Install it with:\nwinget install WinFsp.WinFsp");
            var drive = cfg.Drive;
            if (Directory.Exists(drive + "\\")) throw new UserError("Drive " + drive + " is already in use. Pick another letter in Settings.");

            EnsurePhoneServer(p, log);
            log("Mounting " + drive + "...");
            var logFile = Path.Combine(Config.LocalDir, "rclone.log");
            Directory.CreateDirectory(Config.LocalDir);
            var model = string.IsNullOrEmpty(p.Model) ? "Phone" : p.Model;

            Vol main;
            lock (_sync)
            {
                if (cancelled()) throw new OperationCanceledException();
                main = StartRclone(p, drive, model, ServerScript, logFile);
                main.Proc.Exited += (s, e) =>
                {
                    // The phone link is gone: the SD card drive goes with it.
                    var sd = _sd;
                    if (sd != null) { StopVol(sd); ShellNotify.DriveRemoved(sd.Drive); _sd = null; }
                    ShellNotify.DriveRemoved(drive);
                    Ended?.Invoke();
                };
                _rclone = main.Proc;
                _main = main;
                Phone = p;
                Drive = drive;
                _lastActive = DateTime.UtcNow;
            }
            if (!WaitForDrive(main, cancelled))
            {
                bool exited = main.Proc.HasExited;
                Stop();
                throw new UserError((exited ? "The drive could not be mounted." : "The drive did not appear in time.") + " Details in:\n" + logFile);
            }
            ShellNotify.DriveAdded(drive);

            // The SD card, when there is one, as a second drive. Never fails the connect.
            try
            {
                var card = FindCard(p);
                if (card == null || cancelled()) return;
                var sdDrive = NextFreeDrive(drive);
                if (sdDrive == null) { Log.Write("SD card found, but no free drive letter"); return; }
                log("Mounting the SD card as " + sdDrive + "...");
                WriteServerScript(p, SdServerScript, card);
                Vol sd;
                lock (_sync)
                {
                    if (cancelled() || !main.Alive) return;
                    sd = StartRclone(p, sdDrive, model + " SD card", SdServerScript, logFile);
                    _sd = sd;
                }
                if (WaitForDrive(sd, cancelled)) { ShellNotify.DriveAdded(sdDrive); Log.Write("SD card " + card + " on " + sdDrive); }
                else { Log.Write("SD card did not mount, see rclone.log"); StopVol(sd); _sd = null; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Write("SD card: " + ex.Message); }
        }

        private Vol _main;

        /// <summary>True once the drive shows up; false if rclone ended, or after 20 s, or on cancel.</summary>
        private static bool WaitForDrive(Vol v, Func<bool> cancelled)
        {
            for (int i = 0; i < 40; i++)
            {
                if (cancelled()) return false;
                if (v.Proc.HasExited) return false;
                if (Directory.Exists(v.Drive + "\\")) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        private static Vol StartRclone(Phone p, string drive, string volname, string script, string logFile)
        {
            // rclone's remote control on a random local port with a random password: the only
            // way to ask it how many bytes were transferred, for the idle check.
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int rcPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var rcPass = Guid.NewGuid().ToString("N");
            var v = new Vol
            {
                Drive = drive,
                RcBase = "http://127.0.0.1:" + rcPort + "/",
                RcAuth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(RcUser + ":" + rcPass)),
            };

            var args = string.Join(" ", new[]
            {
                "mount", ":sftp:", drive,
                "--volname", Tools.Q(volname),
                "--vfs-cache-mode", "writes",
                "--dir-cache-time", "15s",
                "--sftp-shell-type", "none",
                "--sftp-disable-hashcheck",
                "--sftp-idle-timeout", "60s",
                "--sftp-known-hosts-file", "none",
                "--config", Tools.Q(Path.Combine(Config.Dir, "rclone.conf")),
                "--cache-dir", Tools.Q(Path.Combine(Config.LocalDir, "cache")),
                "--log-file", Tools.Q(logFile),
                "--log-level", "NOTICE",
                "--rc", "--rc-addr", "127.0.0.1:" + rcPort,
            });
            var psi = new ProcessStartInfo(Tools.Rclone, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Tools.ApplyAdbEnv(psi);
            // The remote-control login goes through the environment, not the command line,
            // where any local program could read it.
            psi.EnvironmentVariables["RCLONE_RC_USER"] = RcUser;
            psi.EnvironmentVariables["RCLONE_RC_PASS"] = rcPass;
            // External "ssh" command for the SFTP backend: adb shell into the server script.
            // rclone appends "-s sftp", which --pipe ignores.
            psi.EnvironmentVariables["RCLONE_SFTP_SSH"] =
                "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --pipe " + p.Serial
                + (script == SdServerScript ? " sd" : "");

            var proc = Process.Start(psi);
            // Tied to this app: if Phonestick ends in any way, Windows ends rclone and the drive goes.
            ChildJob.Add(proc);
            proc.EnableRaisingEvents = true;
            v.Proc = proc;
            return v;
        }

        public void Stop()
        {
            lock (_sync)
            {
                var sd = _sd;
                if (sd != null) { StopVol(sd); ShellNotify.DriveRemoved(sd.Drive); }
                _sd = null;
                var m = _main;
                if (m != null) { StopVol(m); ShellNotify.DriveRemoved(m.Drive); }
                _main = null;
                _rclone = null;
            }
        }

        private static void StopVol(Vol v)
        {
            var r = v.Proc;
            try
            {
                if (r != null && !r.HasExited)
                {
                    foreach (var child in Children(r.Id)) { try { child.Kill(); } catch { } finally { child.Dispose(); } }
                    r.Kill();
                    r.WaitForExit(5000);
                }
            }
            catch { }
        }

        /// <summary>
        /// Copies to P: finish in Explorer once the data is in rclone's local cache; the upload
        /// to the phone goes on afterwards. Waits (up to <paramref name="max"/>) for those
        /// uploads before the drive is closed. rclone also resumes any left over at the next mount.
        /// </summary>
        public void FinishUploads(Action<string> log, TimeSpan max)
        {
            var until = DateTime.UtcNow + max;
            bool told = false;
            while (IsMounted && DateTime.UtcNow < until)
            {
                int n = PendingUploads();
                if (n <= 0) return;
                if (!told) { log("Finishing copies to the phone..."); Log.Write("waiting for " + n + " upload(s)"); told = true; }
                Thread.Sleep(1000);
            }
        }

        private IEnumerable<Vol> Vols()
        {
            var m = _main; var s = _sd;
            if (m != null && m.Alive) yield return m;
            if (s != null && s.Alive) yield return s;
        }

        /// <summary>Uploads queued or in progress on both drives, or -1 when rclone cannot be asked.</summary>
        private int PendingUploads()
        {
            int total = 0;
            foreach (var v in Vols())
            {
                try
                {
                    var json = Rc(v, "vfs/stats");
                    int Get(string k) { var m = Regex.Match(json, "\"" + k + "\"\\s*:\\s*(\\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; }
                    total += Get("uploadsInProgress") + Get("uploadsQueued");
                }
                catch { return -1; }
            }
            return total;
        }

        private static string Rc(Vol v, string command)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                var req = new HttpRequestMessage(HttpMethod.Post, v.RcBase + command);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", v.RcAuth);
                req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                return http.SendAsync(req).Result.Content.ReadAsStringAsync().Result;
            }
        }

        private const string RcUser = "phonestick";

        /// <summary>
        /// An rclone mount left behind when Phonestick was killed keeps the drive letter busy and
        /// cannot be reached any more: ends it. Recognised by our ":sftp:" mount and our --pipe.
        /// </summary>
        public static void KillStale()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='rclone.exe'"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var cmd = o["CommandLine"] as string ?? "";
                        if (!cmd.Contains("mount :sftp:") || !cmd.Contains("--rc-addr 127.0.0.1:")) continue;
                        try
                        {
                            using (var p = Process.GetProcessById(Convert.ToInt32(o["ProcessId"])))
                            {
                                foreach (var child in Children(p.Id)) { try { child.Kill(); } catch { } finally { child.Dispose(); } }
                                p.Kill();
                                Log.Write("ended a stale rclone mount (pid " + p.Id + ")");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Write("stale mount check failed: " + ex.Message); }
        }

        /// <summary>
        /// Called once a minute while mounted. Activity means file data moved (bytes
        /// transferred or a transfer in progress), so an Explorer window left open on the
        /// drive does not keep the phone connected forever.
        /// </summary>
        public bool IdleFor(TimeSpan limit)
        {
            if (!IsMounted) return false;
            foreach (var v in Vols().ToList())
            {
                try
                {
                    var json = Rc(v, "core/stats");
                    var m = Regex.Match(json, @"""bytes""\s*:\s*(\d+)");
                    long bytes = m.Success ? long.Parse(m.Groups[1].Value) : 0;
                    bool transferring = Regex.IsMatch(json, @"""transferring""\s*:\s*\[\s*\{");
                    if (bytes != v.LastBytes || transferring) _lastActive = DateTime.UtcNow;
                    v.LastBytes = bytes;
                }
                catch (Exception ex)
                {
                    Log.Write("idle check failed: " + (ex.InnerException ?? ex).Message);
                    _lastActive = DateTime.UtcNow; // unsure: do not disconnect
                }
            }
            // A copy waiting to reach the phone (in progress, queued or retrying) is activity too.
            if (PendingUploads() > 0) _lastActive = DateTime.UtcNow;
            return DateTime.UtcNow - _lastActive >= limit;
        }

        private static Process[] Children(int pid)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId=" + pid))
                {
                    return s.Get().Cast<ManagementObject>()
                        .Select(o => { try { return Process.GetProcessById(Convert.ToInt32(o["ProcessId"])); } catch { return null; } })
                        .Where(x => x != null).ToArray();
                }
            }
            catch { return new Process[0]; }
        }
    }
}
