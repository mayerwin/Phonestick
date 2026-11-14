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
            var p = new Phone { Serial = serial };
            p.SerialNo = p.Shell("getprop ro.serialno", 15000).Out.Trim();
            p.Model = p.Shell("getprop ro.product.model", 15000).Out.Trim();
            return string.IsNullOrEmpty(p.SerialNo) ? null : p;
        }

        /// <summary>
        /// Finds the configured phone on the Wi-Fi network: already connected, or announced
        /// over mDNS as _adb-tls-connect. Only the phone whose ro.serialno matches is used.
        /// </summary>
        /// <summary>What the last FindOnWifi saw, to tell the user what is wrong when it fails.</summary>
        public static bool LastFindSawService;
        /// <summary>The phone's ip:port as last found by a direct mDNS query, to remember for next time.</summary>
        public static string LastFoundEndpoint;

        public static bool NameMatches(string instance, string serialNo) =>
            serialNo == "" || instance.Contains("-" + serialNo + "-") || instance == "adb-" + serialNo;

        public static Phone FindOnWifi(string wantedSerialNo, int waitMs = 15000, string lastEndpoint = null)
        {
            StartServer();
            LastFindSawService = false;
            LastFoundEndpoint = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            var lastTry = new Dictionary<string, DateTime>();
            var offlineSince = new Dictionary<string, DateTime>();
            System.Net.IPAddress lastIp = null;
            if (!string.IsNullOrEmpty(lastEndpoint)) System.Net.IPAddress.TryParse(lastEndpoint.Split(':')[0], out lastIp);

            bool TryConnect(string endpoint)
            {
                if (lastTry.TryGetValue(endpoint, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(4)) return false;
                lastTry[endpoint] = DateTime.UtcNow;
                var r = Adb("connect " + endpoint, 10000);
                Log.Write("adb connect " + endpoint + ": " + r.All.Trim());
                return true;
            }

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
                    if (d.state != "device") continue;
                    if (wantedSerialNo == "" && d.serial.StartsWith("emulator-")) continue;
                    var p = Describe(d.serial);
                    if (p != null && (wantedSerialNo == "" || p.SerialNo == wantedSerialNo)) return p;
                }
                // Ask the phone itself where wireless debugging listens (fast and reliable, see
                // Mdns), then adb's own discovery as a second source.
                foreach (var s in Mdns.Find(Mdns.Connect, lastIp).Where(s => NameMatches(s.Instance, wantedSerialNo)))
                {
                    LastFindSawService = true;
                    LastFoundEndpoint = s.Endpoint;
                    lastIp = s.Address;
                    TryConnect(s.Endpoint);
                }
                foreach (var (name, endpoint) in MdnsServices())
                    if (NameMatches(name, wantedSerialNo)) { LastFindSawService = true; TryConnect(endpoint); }
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

        public static Phone FindUsb()
        {
            StartServer();
            var d = Devices().FirstOrDefault(x => x.usb);
            if (d.serial == null) return null;
            if (d.state == "unauthorized")
                throw new UserError("The phone is asking whether to allow USB debugging. Tick \"Always allow from this computer\" and tap Allow, then try again.");
            if (d.state != "device") return null;
            return Describe(d.serial);
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
