using System;
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
        private Process _rclone;
        private DateTime _lastActive = DateTime.UtcNow;
        private string _rcUrl, _rcAuth;
        private long _lastBytes = -1;
        public Phone Phone { get; private set; }
        public string Drive { get; private set; }
        public bool IsMounted => _rclone != null && !_rclone.HasExited;
        public event Action Ended;

        public static string ServerScript => AppInfo.PhoneDir + "/sftp.sh";

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
            // --dir-cache-time 1s: the server must not hide changes made on the phone itself;
            // the computer side does its own caching.
            // stderr goes to /dev/null so log lines can never corrupt the SFTP stream.
            var script =
                "export RCLONE_CONFIG=" + AppInfo.PhoneDir + "/rclone.conf; " +
                "export RCLONE_CACHE_DIR=" + AppInfo.PhoneDir + "/cache; " +
                "exec " + AppInfo.PhoneDir + "/rclone serve sftp --stdio --dir-cache-time 1s " + root + " 2>/dev/null";
            p.Shell("echo '" + script + "' > " + ServerScript + " && touch " + AppInfo.PhoneDir + "/rclone.conf", 10000);
        }

        /// <summary>Downloads rclone for the phone from downloads.rclone.org and checks its SHA-256.</summary>
        private static string DownloadRclone(string version, string arch, Action<string> log)
        {
            var cache = Path.Combine(Config.LocalDir, "phone-rclone", version + "-" + arch);
            var bin = Path.Combine(cache, "rclone");
            if (File.Exists(bin)) return bin;
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
                    entry.ExtractToFile(bin, true);
                }
                File.Delete(zipPath);
            }
            return bin;
        }

        public void Start(Phone p, Config cfg, Action<string> log)
        {
            if (Tools.Rclone == null) throw new UserError("rclone was not found. Install it with:\nwinget install Rclone.Rclone");
            if (!Tools.WinFspInstalled) throw new UserError("WinFsp is needed to show the phone as a drive. Install it with:\nwinget install WinFsp.WinFsp");
            var drive = cfg.Drive;
            if (Directory.Exists(drive + "\\")) throw new UserError("Drive " + drive + " is already in use. Pick another letter in Settings.");

            EnsurePhoneServer(p, log);
            log("Mounting " + drive + "...");

            var volname = string.IsNullOrEmpty(p.Model) ? "Phone" : p.Model;
            var logFile = Path.Combine(Config.LocalDir, "rclone.log");
            Directory.CreateDirectory(Config.LocalDir);
            // rclone's remote control on a random local port with a random password: the only
            // way to ask it how many bytes were transferred, for the idle check.
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int rcPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var rcUser = "phonestick";
            var rcPass = Guid.NewGuid().ToString("N");
            _rcUrl = "http://127.0.0.1:" + rcPort + "/core/stats";
            _rcAuth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(rcUser + ":" + rcPass));
            _lastBytes = -1;

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
                "--rc-user", rcUser, "--rc-pass", rcPass,
            });
            var psi = new ProcessStartInfo(Tools.Rclone, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Tools.ApplyAdbEnv(psi);
            // External "ssh" command for the SFTP backend: adb shell into the server script.
            // rclone appends "-s sftp", which --pipe ignores.
            psi.EnvironmentVariables["RCLONE_SFTP_SSH"] =
                "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --pipe " + p.Serial;

            _rclone = Process.Start(psi);
            _rclone.EnableRaisingEvents = true;
            _rclone.Exited += (s, e) => Ended?.Invoke();
            Phone = p;
            Drive = drive;
            _lastActive = DateTime.UtcNow;

            for (int i = 0; i < 40; i++)
            {
                if (_rclone.HasExited)
                    throw new UserError("The drive could not be mounted. Details in:\n" + logFile);
                if (Directory.Exists(drive + "\\")) return;
                Thread.Sleep(500);
            }
            Stop();
            throw new UserError("The drive did not appear in time. Details in:\n" + logFile);
        }

        public void Stop()
        {
            try
            {
                if (_rclone != null && !_rclone.HasExited)
                {
                    foreach (var child in Children(_rclone.Id)) { try { child.Kill(); } catch { } }
                    _rclone.Kill();
                    _rclone.WaitForExit(5000);
                }
            }
            catch { }
            _rclone = null;
        }

        /// <summary>
        /// Called once a minute while mounted. Activity means file data moved (bytes
        /// transferred or a transfer in progress), so an Explorer window left open on the
        /// drive does not keep the phone connected forever.
        /// </summary>
        public bool IdleFor(TimeSpan limit)
        {
            if (!IsMounted) return false;
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, _rcUrl);
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", _rcAuth);
                    req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                    var json = http.SendAsync(req).Result.Content.ReadAsStringAsync().Result;
                    var m = Regex.Match(json, @"""bytes""\s*:\s*(\d+)");
                    long bytes = m.Success ? long.Parse(m.Groups[1].Value) : 0;
                    bool transferring = Regex.IsMatch(json, @"""transferring""\s*:\s*\[\s*\{");
                    if (bytes != _lastBytes || transferring) _lastActive = DateTime.UtcNow;
                    _lastBytes = bytes;
                }
            }
            catch (Exception ex)
            {
                Log.Write("idle check failed: " + (ex.InnerException ?? ex).Message);
                _lastActive = DateTime.UtcNow; // unsure: do not disconnect
            }
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
