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
        private DateTime _lastActive = DateTime.UtcNow;
        public Phone Phone { get; private set; }
        public string Drive { get; private set; }
        public bool IsMounted { get { var m = _main; return m != null && m.Alive; } }
        /// <summary>The phone's drive ended (disconnect, or the link to the phone was lost).</summary>
        public event Action Ended;
        /// <summary>The SD card's drive came or went on its own.</summary>
        public event Action SdChanged;

        private const string VersionFile = AppInfo.PhoneDir + "/rclone.version";

        private static (string path, DateTime stamp, string version) _rcloneVersion;

        /// <summary>The PC's rclone version, asked once per rclone build (not at every mount).</summary>
        public static string RcloneVersion()
        {
            lock (VersionGate) return RcloneVersionLocked(); // asked ahead of a connect and during it
        }

        private static readonly object VersionGate = new object();

        private static string RcloneVersionLocked()
        {
            var exe = Tools.Rclone;
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(exe); } catch { stamp = DateTime.MinValue; }
            var known = _rcloneVersion;
            if (known.path == exe && known.stamp == stamp && known.version != null) return known.version;
            var r = Tools.Run(exe, "version", 20000);
            var m = Regex.Match(r.Out, @"rclone (v\d+\.\d+\.\d+)");
            if (!m.Success) throw new UserError("Could not read the rclone version:\n" + r.All);
            _rcloneVersion = (exe, stamp, m.Groups[1].Value);
            return m.Groups[1].Value;
        }

        /// <summary>
        /// Puts a matching rclone build and the server scripts on the phone. Returns the SD card's
        /// folder, or null. Two adb round trips when all is in place: one to look, one to write.
        /// </summary>
        private static string EnsurePhoneServer(Phone p, Action<string> log)
        {
            var version = RcloneVersion();
            // The version comes from a small file written next to rclone: running the rclone
            // binary on the phone just to ask costs a noticeable moment at every mount.
            // The scripts already there are read too: usually nothing needs writing (one round trip).
            const string lost = "The connection to the phone dropped while getting it ready (it may have moved to another Wi-Fi access point). Try again.";
            List<string> facts = null;
            // The abi line is always there when the phone answered: without it the connection
            // dropped (Android cuts Wireless debugging when the phone moves to another access
            // point), which must not read as an unsupported phone. Asked twice before giving up.
            for (int attempt = 0; attempt < 2 && (facts == null || !facts.Any(l => l.StartsWith("abi="))); attempt++)
                facts = p.Shell(
                    "echo v=$(cat " + VersionFile + " 2>/dev/null); " +
                    "echo abi=$(getprop ro.product.cpu.abi); " +
                    "echo root=$(readlink -f /sdcard); " +
                    "for d in /storage/*; do echo card=$d; done; " +
                    "echo s1=$(cat " + AppInfo.ServerScript + " 2>/dev/null); " +
                    "echo s2=$(cat " + AppInfo.SdServerScript + " 2>/dev/null); " +
                    "[ -e " + AppInfo.PhoneDir + "/rclone.conf ] && echo conf=1", 20000).Out
                    .Split('\n').Select(l => l.Trim()).ToList();
            string Fact(string key) => facts.Where(l => l.StartsWith(key + "=")).Select(l => l.Substring(key.Length + 1)).FirstOrDefault() ?? "";
            if (Fact("abi") == "") throw new PhoneLostError(lost);

            bool pushed = false;
            // No file yet (installed by an older version): ask the binary itself, once.
            if (Fact("v") != version && !RcloneOnPhoneIs(p, version, lost))
            {
                var abi = Fact("abi");
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
                pushed = true;
            }
            // Serve the real path: /sdcard is a symlink, which rclone will not follow as a root.
            var root = Fact("root");
            if (!root.StartsWith("/")) root = "/storage/emulated/0";
            // A removable card (portable SD card or USB drive) shows up as /storage/XXXX-XXXX.
            // The adb shell can read and write it.
            var card = facts.Where(l => l.StartsWith("card=")).Select(l => l.Substring(5))
                .FirstOrDefault(d => Regex.IsMatch(d, @"^/storage/[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}$"));

            string main = ServerScriptFor(root), sd = card == null ? null : ServerScriptFor(card);
            if (pushed || Fact("v") != version || Fact("s1") != main || (sd != null && Fact("s2") != sd) || Fact("conf") != "1")
            {
                var write = "echo '" + main + "' > " + AppInfo.ServerScript;
                if (sd != null) write += " && echo '" + sd + "' > " + AppInfo.SdServerScript;
                var w = p.Shell(write + " && echo " + version + " > " + VersionFile + " && touch " + AppInfo.PhoneDir + "/rclone.conf", 10000);
                if (!w.Ok) throw new UserError("Could not prepare the file server on the phone:\n" + w.All);
            }
            return card;
        }

        /// <summary>
        /// Whether the rclone on the phone is this version. A dropped connection throws, so it
        /// never causes a needless new copy of rclone.
        /// </summary>
        private static bool RcloneOnPhoneIs(Phone p, string version, string lost)
        {
            var o = p.Shell("echo answered; " + AppInfo.PhoneDir + "/rclone version 2>/dev/null | head -1", 20000).Out;
            if (!o.Contains("answered")) throw new PhoneLostError(lost);
            return Regex.IsMatch(o, @"rclone " + Regex.Escape(version) + @"(\s|$)");
        }

        /// <summary>The server script serving this folder (one line, single spaces: compared as read back).</summary>
        private static string ServerScriptFor(string root)
        {
            // --dir-cache-time 1s: the server must not hide changes made on the phone itself;
            // the computer side does its own caching.
            // stderr goes to /dev/null so log lines can never corrupt the SFTP stream.
            return
                "export RCLONE_CONFIG=" + AppInfo.PhoneDir + "/rclone.conf; " +
                "export RCLONE_CACHE_DIR=" + AppInfo.PhoneDir + "/cache; " +
                "exec " + AppInfo.PhoneDir + "/rclone serve sftp --stdio --dir-cache-time 1s " + root + " 2>/dev/null";
        }

        /// <summary>Downloads rclone for the phone from downloads.rclone.org and checks its SHA-256.</summary>
        private static string DownloadRclone(string version, string arch, Action<string> log)
        {
            var cache = Path.Combine(Config.Dir, "phone-rclone", version + "-" + arch);
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

        /// <summary>Drive letters Windows has assigned, also those of empty card readers or locked drives.</summary>
        internal static HashSet<string> UsedDrives() =>
            new HashSet<string>(Environment.GetLogicalDrives().Select(d => d.Substring(0, 2).ToUpperInvariant()));

        /// <summary>The first free letter after this one (wrapping from Z to D), or null.</summary>
        internal static string NextFreeDrive(string after)
        {
            var used = UsedDrives();
            char c = char.ToUpperInvariant(after[0]);
            for (int i = 0; i < 26; i++)
            {
                c = c >= 'Z' ? 'D' : (char)(c + 1);
                if (c + ":" == after.ToUpperInvariant()) break;
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
            if (UsedDrives().Contains(drive)) throw new UserError("Drive " + drive + " is already in use. Pick another letter in Settings.");

            var sw = Stopwatch.StartNew();
            log("Getting the phone ready...");
            var card = EnsurePhoneServer(p, log);
            Log.Write("file server ready on the phone in " + sw.ElapsedMilliseconds + " ms" + (card != null ? ", SD card " + card : ""));
            log("Mounting " + drive + "...");
            var logFile = Path.Combine(Config.Dir, "rclone.log");
            Directory.CreateDirectory(Config.Dir);
            var model = string.IsNullOrEmpty(p.Model) ? "Phone" : p.Model;

            // The SD card, when there is one, as a second drive, mounted alongside the first.
            string sdDrive = card == null ? null : NextFreeDrive(drive);
            if (card != null && sdDrive == null) Log.Write("SD card found, but no free drive letter");

            Vol main, sd = null;
            lock (_sync)
            {
                if (cancelled()) throw new OperationCanceledException();
                main = StartRclone(p, drive, model, AppInfo.ServerScript, logFile);
                main.Proc.Exited += (s, e) =>
                {
                    // Not the mount of a later connect (this handler can run late).
                    var now = _main;
                    if (now != null && now != main) return;
                    // The phone link is gone: the SD card drive goes with it.
                    var other = _sd;
                    if (other != null) { _sd = null; StopVol(other); ShellNotify.DriveRemoved(other.Drive); }
                    ShellNotify.DriveRemoved(drive);
                    Ended?.Invoke();
                };
                _main = main;
                Phone = p;
                Drive = drive;
                _lastActive = DateTime.UtcNow;
                if (sdDrive != null)
                {
                    try
                    {
                        sd = StartRclone(p, sdDrive, model + " SD card", AppInfo.SdServerScript, logFile);
                        var mine = sd;
                        sd.Proc.Exited += (s, e) =>
                        {
                            // The card's drive alone ended (card removed, say): tell Explorer and the menu.
                            if (_sd != mine) return; // stopped on purpose, or with the phone's drive
                            _sd = null;
                            ShellNotify.DriveRemoved(mine.Drive);
                            SdChanged?.Invoke();
                        };
                        _sd = sd;
                    }
                    catch (Exception ex) { Log.Write("SD card: " + ex.Message); }
                }
            }
            if (!WaitForDrive(main, cancelled))
            {
                bool exited = main.Proc.HasExited;
                Stop();
                if (cancelled()) throw new OperationCanceledException();
                throw new UserError((exited ? "The drive could not be mounted." : "The drive did not appear in time.") + " Details in:\n" + logFile);
            }
            ShellNotify.DriveAdded(drive);
            Prewarm(main);
            if (sd == null) return;

            // The phone's drive is up: Explorer opens on it now, the card follows when ready.
            System.Threading.Tasks.Task.Run(() =>
            {
                if (WaitForDrive(sd, () => _sd != sd))
                {
                    ShellNotify.DriveAdded(sdDrive);
                    Log.Write("SD card " + card + " on " + sdDrive);
                }
                else if (_sd == sd)
                {
                    Log.Write("SD card did not mount, see rclone.log");
                    _sd = null;
                    StopVol(sd);
                }
                SdChanged?.Invoke();
            });
        }
        private Vol _main;

        /// <summary>True once the drive shows up; false if rclone ended, or after 20 s, or on cancel.</summary>
        private static bool WaitForDrive(Vol v, Func<bool> cancelled)
        {
            // Looked at often: the drive usually comes up within a second or two, and every
            // poll saved is time the user waits for Explorer to open.
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until)
            {
                if (cancelled()) return false;
                if (v.Proc.HasExited) return false;
                if (Directory.Exists(v.Drive + "\\")) return true;
                Thread.Sleep(100);
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

            // Files are written straight to the phone (no write cache): a copy to the drive ends
            // only once the whole file is on the phone, so Explorer's own progress is the real
            // one, and a cut and paste deletes the original only after that. With a write cache,
            // Explorer said a 2 GB copy was done in seconds while the phone still had a partial
            // file, and a moved file existed only in the PC's cache until its upload ended.
            // The price: an app cannot open a file on the drive to edit it in place (read and
            // write at once); it gets "access denied" and nothing is lost.
            var cacheDir = Path.Combine(Config.Dir, script == AppInfo.SdServerScript ? "cache-sd" : "cache");
            var args = string.Join(" ", new[]
            {
                "mount", ":sftp:", drive,
                "--volname", Tools.Q(volname),
                "--vfs-cache-mode", "off",
                // No read-ahead buffer: Explorer opens many files for a few KB each (thumbnails,
                // dates), and the pipe ramps reads up by itself for real copies (ReadShaper).
                "--buffer-size", "0",
                // Listings are kept: browsing never waits for the phone to list a big folder
                // again. Folders open in Explorer are refreshed in the background (KeepFresh).
                "--dir-cache-time", "10m",
                "--sftp-shell-type", "none",
                "--sftp-disable-hashcheck",
                "--sftp-idle-timeout", "60s",
                "--sftp-known-hosts-file", "none",
                "--config", Tools.Q(Path.Combine(Config.Dir, "rclone.conf")),
                "--cache-dir", Tools.Q(cacheDir),
                "--log-file", Tools.Q(logFile),
                "--log-level", "NOTICE",
                "--rc", "--rc-addr", "127.0.0.1:" + rcPort,
            });
            var psi = RclonePsi(p, script, args);
            // The remote-control login goes through the environment, not the command line,
            // where any local program could read it.
            psi.EnvironmentVariables["RCLONE_RC_USER"] = RcUser;
            psi.EnvironmentVariables["RCLONE_RC_PASS"] = rcPass;
            var proc = Process.Start(psi);
            // Tied to this app: if Phonestick ends in any way, Windows ends rclone and the drive goes.
            ChildJob.Add(proc);
            proc.EnableRaisingEvents = true;
            v.Proc = proc;
            return v;
        }

        /// <summary>rclone with the phone as its SFTP server, through our pipe.</summary>
        private static ProcessStartInfo RclonePsi(Phone p, string script, string args)
        {
            var psi = new ProcessStartInfo(Tools.Rclone, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Tools.ApplyAdbEnv(psi);
            // External "ssh" command for the SFTP backend: adb shell into the server script.
            // rclone appends "-s sftp", which --pipe ignores.
            psi.EnvironmentVariables["RCLONE_SFTP_SSH"] =
                "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --pipe " + p.Serial
                + (script == AppInfo.SdServerScript ? " sd" : "");
            return psi;
        }

        public void Stop()
        {
            lock (_sync)
            {
                var sd = _sd;
                _sd = null; // first, so its Exited handler knows this stop is on purpose
                if (sd != null) { StopVol(sd); ShellNotify.DriveRemoved(sd.Drive); }
                var m = _main;
                if (m != null) { StopVol(m); ShellNotify.DriveRemoved(m.Drive); }
                _main = null;
                // Their exit is handled (WaitForExit above raises Exited): free the handles.
                try { sd?.Proc?.Dispose(); m?.Proc?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Ends one rclone mount. Its "--pipe" helpers end by themselves once rclone is gone (their
        /// input closes), and they are in the job object anyway.
        /// </summary>
        private static void StopVol(Vol v)
        {
            var r = v.Proc;
            try
            {
                if (r != null && !r.HasExited)
                {
                    r.Kill();
                    r.WaitForExit(5000);
                }
            }
            catch { }
        }

        private IEnumerable<Vol> Vols()
        {
            var m = _main; var s = _sd;
            if (m != null && m.Alive) yield return m;
            if (s != null && s.Alive) yield return s;
        }

        /// <summary>One client for every call to rclone's remote control (it keeps the connection).</summary>
        private static readonly Lazy<HttpClient> RcHttp = new Lazy<HttpClient>(() => new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

        // ------------------------------------------------------------ folder listings

        /// <summary>When each folder open in Explorer was last refreshed (or first seen).</summary>
        private readonly Dictionary<string, DateTime> _refreshed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        /// <summary>How often a folder open in Explorer is listed again from the phone.</summary>
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);
        /// <summary>A folder just opened is listed again this soon, after Explorer's own first burst.</summary>
        private static readonly TimeSpan RefreshFirst = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Folder listings are kept 10 minutes (--dir-cache-time), so browsing never waits for the
        /// phone to list a big folder again. Folders open in Explorer are refreshed in the
        /// background instead: photos taken on the phone show up there within half a minute
        /// (F5), without anyone waiting on it.
        /// </summary>
        public void KeepFresh(IEnumerable<string> openFolders)
        {
            var now = DateTime.UtcNow;
            var vols = Vols().ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in openFolders)
            {
                var v = vols.FirstOrDefault(x => path.StartsWith(x.Drive, StringComparison.OrdinalIgnoreCase));
                if (v == null) continue;
                seen.Add(path);
                lock (_refreshed)
                {
                    if (!_refreshed.TryGetValue(path, out var last)) { _refreshed[path] = now - RefreshEvery + RefreshFirst; continue; }
                    if (now - last < RefreshEvery) continue;
                    _refreshed[path] = now;
                }
                var dir = path.Substring(2).Trim('\\').Replace('\\', '/');
                try { Rc(v, "vfs/refresh", "{\"dir\":" + JsonString(dir) + ",\"_async\":true}"); } catch { }
            }
            lock (_refreshed)
                foreach (var gone in _refreshed.Keys.Where(k => !seen.Contains(k)).ToList()) _refreshed.Remove(gone);
        }

        /// <summary>Lists the usual photo and download folders ahead, in the background, right after mounting.</summary>
        private static void Prewarm(Vol v)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Up to the drive showing first: the listing must not hold up Explorer's own.
                    Thread.Sleep(1500);
                    Rc(v, "vfs/refresh", "{\"dir\":\"DCIM\",\"dir2\":\"DCIM/Camera\",\"dir3\":\"Pictures\",\"dir4\":\"Download\",\"dir5\":\"Movies\",\"_async\":true}");
                }
                catch { }
            });
        }

        private static string JsonString(string s)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static string Rc(Vol v, string command, string json = "{}")
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, v.RcBase + command))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", v.RcAuth);
                req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using (var resp = RcHttp.Value.SendAsync(req).Result)
                    return resp.Content.ReadAsStringAsync().Result;
            }
        }

        private const string RcUser = "phonestick";

        /// <summary>
        /// An rclone mount left behind when Phonestick was killed keeps the drive letter busy and
        /// cannot be reached any more: ends it. Recognised by our ":sftp:" mount with our own
        /// rclone.conf, so the user's own rclone mounts are left alone.
        /// </summary>
        public static void KillStale()
        {
            try
            {
                // Usually no rclone runs at all: then the slow WMI query is skipped (faster start).
                var any = Process.GetProcessesByName("rclone");
                foreach (var x in any) x.Dispose();
                if (any.Length == 0) return;
                using (var s = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='rclone.exe'"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var cmd = o["CommandLine"] as string ?? "";
                        if (!cmd.Contains("mount :sftp:") || !cmd.Contains("--rc-addr 127.0.0.1:")
                            || cmd.IndexOf(Path.Combine(Config.Dir, "rclone.conf"), StringComparison.OrdinalIgnoreCase) < 0) continue;
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
