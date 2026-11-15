using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Phonestick
{
    /// <summary>One phone reached through adb on our private server port.</summary>
    internal sealed class Phone
    {
        public string Serial;   // adb transport serial, e.g. "192.168.1.23:41234" or "adb-XXXX._adb-tls-connect._tcp"
        public string SerialNo; // ro.serialno, stable hardware id
        public string Model;

        public static string AdbExe => Tools.Adb ?? throw new UserError(
            "adb was not found. Install it with:\nwinget install Genymobile.scrcpy");

        public static RunResult Adb(string args, int timeoutMs = 60000) => Tools.Run(AdbExe, args, timeoutMs);

        public RunResult Shell(string cmd, int timeoutMs = 60000) =>
            Adb("-s " + Tools.Q(Serial) + " shell " + Tools.Q(cmd), timeoutMs);

        /// <summary>Starting and stopping the shared adb server never overlap.</summary>
        private static readonly object Gate = new object();

        /// <summary>True while anything still needs the adb server (set by the tray app).</summary>
        public static Func<bool> InUse = () => false;

        public static void StartServer() { lock (Gate) Adb("start-server", 30000); }

        /// <summary>Whether our adb server is up (something listens on its local port).</summary>
        private static bool ServerRunning()
        {
            try
            {
                return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners().Any(e => e.Port == AppInfo.AdbPort);
            }
            catch { return false; }
        }

        /// <summary>
        /// Starts the adb server ahead of a likely connect (the panel or the menu was opened): it
        /// takes about two seconds, which the user then does not wait for. Stopped again a little
        /// later if nothing used it.
        /// </summary>
        public static void Prewarm()
        {
            _prewarmedAt = DateTime.UtcNow; // a later panel open pushes the stop back
            if (Interlocked.Exchange(ref _prewarming, 1) == 1) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!ServerRunning()) StartServer();
                    while (DateTime.UtcNow - _prewarmedAt < TimeSpan.FromSeconds(30)) Thread.Sleep(1000);
                    StopServerIfUnused();
                }
                catch { }
                finally { Volatile.Write(ref _prewarming, 0); }
            });
        }

        private static int _prewarming;
        private static DateTime _prewarmedAt;

        /// <summary>Stops our private adb server: every connection to the phone closes.</summary>
        public static void StopServer()
        {
            lock (Gate)
            {
                try { if (Tools.Adb != null) Adb("kill-server", 15000); } catch { }
            }
        }

        /// <summary>
        /// Stops the adb server when nothing uses it any more (a mount or a screen session ended).
        /// Checked under the same lock as StartServer, so a connect that just began is never cut.
        /// </summary>
        public static void StopServerIfUnused()
        {
            lock (Gate)
            {
                bool used;
                try { used = InUse(); } catch { used = true; }
                if (used) return;
                try { if (Tools.Adb != null) Adb("kill-server", 15000); } catch { }
            }
        }

        /// <summary>Devices in state "device", with their transport serial and whether they are USB.</summary>
        public static List<(string serial, bool usb, string state)> Devices()
        {
            var list = new List<(string, bool, string)>();
            foreach (var line in Adb("devices -l", 20000).Out.Split('\n').Skip(1))
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                list.Add((parts[0], line.Contains(" usb:"), parts[1]));
            }
            return list;
        }

        public static Phone Describe(string serial)
        {
            // One round trip for both values: each adb command over Wi-Fi costs a little.
            var p = new Phone { Serial = serial };
            var lines = p.Shell("echo s=$(getprop ro.serialno); echo m=$(getprop ro.product.model)", 15000)
                .Out.Split('\n').Select(l => l.Trim()).ToList();
            p.SerialNo = lines.Where(l => l.StartsWith("s=")).Select(l => l.Substring(2)).FirstOrDefault() ?? "";
            p.Model = lines.Where(l => l.StartsWith("m=")).Select(l => l.Substring(2)).FirstOrDefault() ?? "";
            return string.IsNullOrEmpty(p.SerialNo) ? null : p;
        }

        /// <summary>What the last FindOnWifi saw, to tell the user what is wrong when it fails.</summary>
        public static bool LastFindSawService;
        /// <summary>The phone's ip:port as last found by a direct mDNS query, to remember for next time.</summary>
        public static string LastFoundEndpoint;

        public static bool NameMatches(string instance, string serialNo) =>
            serialNo == "" || instance.Contains("-" + serialNo + "-") || instance == "adb-" + serialNo;

        /// <summary>
        /// Finds the configured phone on the Wi-Fi network: already connected, or announced
        /// over mDNS as _adb-tls-connect. Only the phone whose ro.serialno matches is used.
        /// </summary>
        public static Phone FindOnWifi(string wantedSerialNo, int waitMs = 15000, string lastEndpoint = null)
        {
            LastFindSawService = false;
            LastFoundEndpoint = null;
            System.Net.IPAddress lastIp = null;
            if (!string.IsNullOrEmpty(lastEndpoint)) System.Net.IPAddress.TryParse(lastEndpoint.Split(':')[0], out lastIp);
            bool Wanted(Mdns.Service x) => NameMatches(x.Instance, wantedSerialNo);
            // A fresh adb server takes about two seconds to start: the phone is asked meanwhile.
            var firstIp = lastIp;
            var early = ServerRunning() ? null : System.Threading.Tasks.Task.Run(() => Mdns.Find(Mdns.Connect, firstIp, Wanted));
            StartServer();
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            var lastTry = new Dictionary<string, DateTime>();
            var offlineSince = new Dictionary<string, DateTime>();

            // True when adb says it is connected: then the phone is looked for again at once.
            bool TryConnect(string endpoint)
            {
                if (lastTry.TryGetValue(endpoint, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(4)) return false;
                lastTry[endpoint] = DateTime.UtcNow;
                var r = Adb("connect " + endpoint, 10000);
                Log.Write("adb connect " + endpoint + ": " + r.All.Trim());
                return r.Out.Contains("connected to");
            }
            // Another phone or an emulator on the same adb server is described once, not every round.
            var others = new HashSet<string>();

            while (true)
            {
                foreach (var d in Devices().Where(d => !d.usb))
                {
                    if (d.state == "offline")
                    {
                        // A transport still coming up shows offline for a moment: only one that
                        // stays offline is stale (old port), and is dropped so it can be redone.
                        if (!offlineSince.ContainsKey(d.serial)) offlineSince[d.serial] = DateTime.UtcNow;
                        else if (DateTime.UtcNow - offlineSince[d.serial] > TimeSpan.FromSeconds(5))
                        {
                            Adb("disconnect " + Tools.Q(d.serial), 10000);
                            offlineSince.Remove(d.serial);
                        }
                        continue;
                    }
                    if (d.state != "device" || others.Contains(d.serial)) continue;
                    if (wantedSerialNo == "" && d.serial.StartsWith("emulator-")) continue;
                    var p = Describe(d.serial);
                    if (p != null && (wantedSerialNo == "" || p.SerialNo == wantedSerialNo)) return p;
                    if (p != null) others.Add(d.serial);
                }
                // Ask the phone itself where wireless debugging listens (fast and reliable, see
                // Mdns), then adb's own discovery as a second source.
                bool direct = false, connected = false;
                var services = early != null ? early.Result : Mdns.Find(Mdns.Connect, lastIp, Wanted);
                early = null;
                foreach (var s in services.Where(Wanted))
                {
                    LastFindSawService = direct = true;
                    LastFoundEndpoint = s.Endpoint;
                    lastIp = s.Address;
                    connected |= TryConnect(s.Endpoint);
                }
                if (!direct)
                    foreach (var (name, endpoint) in MdnsServices())
                        if (NameMatches(name, wantedSerialNo)) { LastFindSawService = true; connected |= TryConnect(endpoint); }
                if (connected) continue; // look at the devices again right away (also past the deadline)
                if (DateTime.UtcNow > deadline) return null;
                Thread.Sleep(300);
            }
        }

        /// <summary>(service name, ip:port) for each wireless debugging endpoint seen on the LAN.</summary>
        public static List<(string name, string endpoint)> MdnsServices(string type = "_adb-tls-connect")
        {
            var list = new List<(string, string)>();
            foreach (var line in Adb("mdns services", 15000).Out.Split('\n'))
            {
                if (!line.Contains(type + ".")) continue;
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var ep = parts.FirstOrDefault(x => Regex.IsMatch(x, @"^\d+\.\d+\.\d+\.\d+:\d+$"));
                if (ep != null) list.Add((parts[0], ep));
            }
            return list;
        }

        /// <summary>
        /// Hides Android's "Wireless debugging connected" notification for this session. Android
        /// offers no setting for it (its channel is locked), but the adb shell may snooze it. A
        /// snoozed notification is removed for good when the connection ends. It may still flash
        /// briefly as the connection opens, before this runs.
        /// </summary>
        public void HideDebugNotice()
        {
            const string script =
                "for k in $(cmd notification list 2>/dev/null | grep '|android|'); do " +
                "case \"$k\" in *'|android|62|'*) hit=1;; *) hit=$(cmd notification get \"$k\" 2>/dev/null | grep -c -i -e DEVELOPER_IMPORTANT -e 'wireless debugging');; esac; " +
                "[ \"$hit\" != 0 ] && [ -n \"$hit\" ] && cmd notification snooze --for 86400000 \"$k\" >/dev/null 2>&1; done; true";
            try { Shell(script, 15000); } catch { }
        }

        private static bool _appChecked;

        /// <summary>
        /// Brings the phone app up to the version built into this exe, once per run, so phone-side
        /// fixes arrive with the PC update. Never fails a connect: problems are only logged.
        /// </summary>
        public void EnsureAppCurrent(Action<string> log)
        {
            if (_appChecked) return;
            try
            {
                var o = Shell("dumpsys package " + AppInfo.PhonePackage + " | grep versionCode", 15000).Out;
                var m = Regex.Match(o, @"versionCode=(\d+)");
                if (!m.Success) return; // not installed, or unreadable: setup installs it
                _appChecked = true;
                int have = int.Parse(m.Groups[1].Value);
                if (have >= AppInfo.PhoneAppVersion) return;
                log("Updating the phone app...");
                var r = Adb("-s " + Tools.Q(Serial) + " install -r " + Tools.Q(Helpers.ExtractApk()), 180000);
                Log.Write("phone app " + have + " -> " + AppInfo.PhoneAppVersion + ": " + (r.All.Contains("Success") ? "updated" : r.All));
            }
            catch (Exception ex) { Log.Write("phone app check: " + ex.Message); }
        }

        public void Disconnect()
        {
            if (Serial.Contains(":") || Serial.Contains("._adb-tls-connect")) Adb("disconnect " + Tools.Q(Serial), 10000);
        }
    }

    /// <summary>An error meant to be shown to the user as is.</summary>
    internal sealed class UserError : Exception
    {
        public UserError(string m) : base(m) { }
    }
}
