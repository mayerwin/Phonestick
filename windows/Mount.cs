using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace Phonestick
{
    /// <summary>
    /// Mounts the phone's shared storage as a drive letter, like a drive mounted over SSH.
    ///
    /// On the phone, a small SFTP server (phone-server/, embedded in this exe) runs through the
    /// adb shell only while the drive is mounted. On the computer, sshfs (SSHFS-Win, on WinFsp)
    /// mounts it, reaching it through SftpBridge. sshfs turns each file operation into the same
    /// operation on the phone, so the drive behaves like a USB stick: apps open, edit and save
    /// files in place, Explorer's progress is the real transfer, and a write is on the phone
    /// once the app's write returns. No port or app is opened on the phone.
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

        private const string ServerFile = AppInfo.PhoneDir + "/psftp", ServerVersionFile = AppInfo.PhoneDir + "/psftp.version";
        private const string LostMessage = "The connection to the phone dropped while getting it ready (it may have moved to another Wi-Fi access point). Try again.";

        /// <summary>
        /// Puts the file server on the phone if it is missing or another version, and finds the
        /// folders to serve: the shared storage, and a removable card if there is one (else
        /// null). One adb round trip when the server is in place.
        /// </summary>
        private static string EnsurePhoneServer(Phone p, Action<string> log, out string root)
        {
            List<string> facts = null;
            // The abi line is always there when the phone answered: without it the connection
            // dropped (Android cuts Wireless debugging when the phone moves to another access
            // point), which must not read as an unsupported phone. Asked twice before giving up.
            for (int attempt = 0; attempt < 2 && (facts == null || !facts.Any(l => l.StartsWith("abi="))); attempt++)
                facts = p.Shell(
                    "mkdir -p " + AppInfo.PhoneDir + "; " +
                    "echo v=$(cat " + ServerVersionFile + " 2>/dev/null); " +
                    "echo abi=$(getprop ro.product.cpu.abi); " +
                    "echo root=$(readlink -f /sdcard); " +
                    "for d in /storage/*; do echo card=$d; done", 20000).Out
                    .Split('\n').Select(l => l.Trim()).ToList();
            string Fact(string key) => facts.Where(l => l.StartsWith(key + "=")).Select(l => l.Substring(key.Length + 1)).FirstOrDefault() ?? "";
            if (Fact("abi") == "") throw new PhoneLostError(LostMessage);

            if (Fact("v") != AppInfo.PhoneServerVersion) InstallServer(p, Fact("abi"), log);

            // Serve the real path: /sdcard is a symlink.
            root = Fact("root");
            if (!root.StartsWith("/")) root = "/storage/emulated/0";
            // A removable card (portable SD card or USB drive) shows up as /storage/XXXX-XXXX.
            // The adb shell can read and write it.
            return facts.Where(l => l.StartsWith("card=")).Select(l => l.Substring(5))
                .FirstOrDefault(d => Regex.IsMatch(d, @"^/storage/[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}$"));
        }

        /// <summary>Copies the file server for this phone's CPU to the phone (about 3 MB, once per server version).</summary>
        private static void InstallServer(Phone p, string abi, Action<string> log)
        {
            string arch = abi.StartsWith("arm64") ? "arm64" : abi.StartsWith("armeabi") ? "arm"
                : abi == "x86_64" ? "amd64" : abi == "x86" ? "386" : null;
            if (arch == null) throw new UserError("This phone's processor (" + abi + ") is not supported.");
            log("Copying the file server to the phone (one time)...");
            var tmp = Path.Combine(Config.Dir, "psftp-" + arch);
            Directory.CreateDirectory(Config.Dir);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Phonestick.Assets.psftp-" + arch + ".gz"))
            {
                if (res == null) throw new UserError("This build of " + AppInfo.Name + " lacks the phone's file server (psftp-" + arch + ").");
                using (var gz = new GZipStream(res, CompressionMode.Decompress))
                using (var f = File.Create(tmp))
                    gz.CopyTo(f);
            }
            try
            {
                // Under a temporary name, then moved: a running server is never half replaced.
                var push = Phone.Adb("-s " + Tools.Q(p.Serial) + " push " + Tools.Q(tmp) + " " + ServerFile + ".new", 120000);
                if (!push.Ok) throw new PhoneLostError("Copying the file server to the phone failed:\n" + push.All.Trim());
                var w = p.Shell("chmod 755 " + ServerFile + ".new && mv -f " + ServerFile + ".new " + ServerFile +
                    " && echo " + AppInfo.PhoneServerVersion + " > " + ServerVersionFile + " && echo installed", 15000);
                if (!w.Out.Contains("installed")) throw new PhoneLostError("Could not set up the file server on the phone:\n" + w.All.Trim());
                Log.Write("file server " + AppInfo.PhoneServerVersion + " (" + arch + ") copied to the phone");
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>One mount: the phone's internal storage, or its SD card.</summary>
        private sealed class Vol
        {
            public Process Proc;
            public SftpBridge Bridge;
            public string Drive;
            public long LastBytes;
            public bool Alive { get { var r = Proc; try { return r != null && !r.HasExited; } catch { return false; } } }
        }

        private Vol _main, _sd;

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
            if (!Tools.WinFspInstalled) throw new UserError("WinFsp is needed to show the phone as a drive. Install it with:\nwinget install WinFsp.WinFsp");
            if (Tools.Sshfs == null) throw new UserError("SSHFS-Win is needed to show the phone as a drive. Install it with:\nwinget install SSHFS-Win.SSHFS-Win");
            var drive = cfg.Drive;
            if (UsedDrives().Contains(drive)) throw new UserError("Drive " + drive + " is already in use. Pick another letter in Settings.");

            var sw = Stopwatch.StartNew();
            log("Getting the phone ready...");
            var card = EnsurePhoneServer(p, log, out var root);
            Log.Write("file server ready on the phone in " + sw.ElapsedMilliseconds + " ms" + (card != null ? ", SD card " + card : ""));
            log("Mounting " + drive + "...");
            var model = string.IsNullOrEmpty(p.Model) ? "Phone" : p.Model;

            // The SD card, when there is one, as a second drive, mounted alongside the first.
            string sdDrive = card == null ? null : NextFreeDrive(drive);
            if (card != null && sdDrive == null) Log.Write("SD card found, but no free drive letter");

            Vol main, sd = null;
            lock (_sync)
            {
                if (cancelled()) throw new OperationCanceledException();
                main = StartSshfs(p, drive, model, root);
                main.Proc.Exited += (s, e) =>
                {
                    // Not the mount of a later connect (this handler can run late).
                    var now = _main;
                    if (now != null && now != main) return;
                    main.Bridge.Dispose();
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
                        sd = StartSshfs(p, sdDrive, model + " SD card", card);
                        var mine = sd;
                        sd.Proc.Exited += (s, e) =>
                        {
                            mine.Bridge.Dispose();
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
                throw new UserError((exited ? "The drive could not be mounted." : "The drive did not appear in time.") +
                    " Details in:\n" + Path.Combine(Config.Dir, "phonestick.log"));
            }
            ShellNotify.DriveAdded(drive);
            Prewarm(drive);
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
                    Log.Write("SD card did not mount");
                    _sd = null;
                    StopVol(sd);
                }
                SdChanged?.Invoke();
            });
        }

        /// <summary>True once the drive shows up; false if sshfs ended, or after 20 s, or on cancel.</summary>
        private static bool WaitForDrive(Vol v, Func<bool> cancelled)
        {
            // Looked at often: the drive usually comes up within a second, and every poll saved
            // is time the user waits for Explorer to open.
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

        /// <summary>Lists the usual photo and download folders ahead, in the background, so the first look is instant.</summary>
        private static void Prewarm(string drive)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                Thread.Sleep(1000); // after Explorer's own first look at the drive
                foreach (var d in new[] { @"DCIM\Camera", "Pictures", "Download" })
                    try { Directory.GetFileSystemEntries(drive + "\\" + d); } catch { }
            });
        }

        private static Vol StartSshfs(Phone p, string drive, string volname, string folder)
        {
            var bridge = new SftpBridge(p.Serial, "exec " + ServerFile + " '" + folder.Replace("'", "'\\''") + "'");
            var args = string.Join(" ", new[]
            {
                "127.0.0.1:", drive, "-f",
                "-o", "directport=" + bridge.Port,
                // Every write is acknowledged by the phone before the app's write returns. So when
                // Explorer's copy ends, the file is on the phone (Windows does not wait for the
                // close to reach sshfs), and a cut and paste removes the original only then.
                "-o", "sshfs_sync",
                // Folder listings are kept 30 s: browsing back and forth never waits for the
                // phone to list a big folder again, and new photos show up after that (F5).
                "-o", "dcache_timeout=30",
                // Files belong to the user, with full rights (the phone has no Windows owners).
                "-o", "idmap=user,uid=-1,gid=-1,umask=000,create_umask=000",
                "-o", "rellinks,dothidden",
                "-o", Tools.Q("volname=" + Regex.Replace(volname, "[,\"\\\\]", " ").Trim()),
                "-o", "fstypename=" + AppInfo.Name,
            });
            var psi = new ProcessStartInfo(Tools.Sshfs, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Tools.Sshfs),
            };
            Process proc;
            try { proc = Process.Start(psi); }
            catch { bridge.Dispose(); throw; }
            bridge.ClientPid = proc.Id;
            // Tied to this app: if Phonestick ends in any way, Windows ends sshfs and the drive goes.
            ChildJob.Add(proc);
            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write("sshfs " + drive + ": " + e.Data.Trim()); };
            proc.BeginErrorReadLine();
            proc.EnableRaisingEvents = true;
            return new Vol { Proc = proc, Bridge = bridge, Drive = drive };
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

        /// <summary>Ends one mount: sshfs (WinFsp removes the drive), then its bridge (the server on the phone ends with it).</summary>
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
            v.Bridge?.Dispose();
        }

        private IEnumerable<Vol> Vols()
        {
            var m = _main; var s = _sd;
            if (m != null && m.Alive) yield return m;
            if (s != null && s.Alive) yield return s;
        }

        /// <summary>
        /// An sshfs mount left behind when Phonestick was killed keeps the drive letter busy and
        /// cannot be reached any more: ends it. Recognised by our file system name, so the
        /// user's own SSHFS-Win mounts are left alone.
        /// </summary>
        public static void KillStale()
        {
            try
            {
                // Usually no sshfs runs at all: then the slow WMI query is skipped (faster start).
                var any = Process.GetProcessesByName("sshfs");
                foreach (var x in any) x.Dispose();
                if (any.Length == 0) return;
                using (var s = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='sshfs.exe'"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var cmd = o["CommandLine"] as string ?? "";
                        if (!cmd.Contains("directport=") || !cmd.Contains("fstypename=" + AppInfo.Name)) continue;
                        try
                        {
                            using (var p = Process.GetProcessById(Convert.ToInt32(o["ProcessId"])))
                            {
                                p.Kill();
                                Log.Write("ended a stale sshfs mount (pid " + p.Id + ")");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Write("stale mount check failed: " + ex.Message); }
        }

        /// <summary>
        /// Called once a minute while mounted. Activity means file data moved (more than a folder
        /// listing's worth), so an Explorer window left open on the drive does not keep the phone
        /// connected forever.
        /// </summary>
        public bool IdleFor(TimeSpan limit)
        {
            if (!IsMounted) return false;
            foreach (var v in Vols().ToList())
            {
                long bytes = v.Bridge.Bytes;
                if (bytes - v.LastBytes > 64 * 1024) _lastActive = DateTime.UtcNow;
                v.LastBytes = bytes;
            }
            return DateTime.UtcNow - _lastActive >= limit;
        }
    }
}
