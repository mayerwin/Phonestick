using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Phonestick
{
    /// <summary>
    /// One-time setup of a phone. It starts looking as soon as the window opens and goes on by
    /// itself as soon as the phone shows up, whichever way: the QR code scanned, a pairing code
    /// typed, the USB cable plugged in, or a phone this PC already paired with.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private static SetupForm _open;
        private readonly TrayApp _tray;
        private readonly Config _cfg;
        private readonly TextBox _log;
        private readonly TextBox _code;
        private readonly Button _start;
        private readonly string _qrName, _qrPass;
        private volatile bool _closed;

        public static void ShowSetup(TrayApp tray, Config cfg)
        {
            if (_open != null && !_open.IsDisposed) { _open.Activate(); return; }
            _open = new SetupForm(tray, cfg);
            _open.Show();
        }

        private SetupForm(TrayApp tray, Config cfg)
        {
            _tray = tray;
            _cfg = cfg;
            Text = AppInfo.Name + ": set up phone";
            Icon = Icons.App;
            Font = Theme.UiFont(10f);
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            int S(int px) => (int)Math.Round(px * scale);
            Width = S(780);
            Height = S(640);
            MinimumSize = new Size(S(640), S(560));
            StartPosition = FormStartPosition.CenterScreen;

            // Android's "Pair device with QR code": the phone scans this, then announces a pairing
            // service under this name on the network, and this PC pairs with the password.
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            string Alnum(int n) { const string a = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"; var c = new char[n]; for (int i = 0; i < n; i++) c[i] = a[rnd.Next(a.Length)]; return new string(c); }
            _qrName = "phonestick-" + Alnum(8);
            _qrPass = Alnum(12);

            var bold = Theme.UiFont(10.5f, FontStyle.Bold);
            var intro = new Label
            {
                Text = "Once, on the phone: Settings > About phone: tap Build number 7 times. Then Settings > System > " +
                       "Developer options: turn on \"Disable adb authorization timeout\".",
            };
            var qr = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                Image = QrCode.Encode("WIFI:T:ADB;S:" + _qrName + ";P:" + _qrPass + ";;").ToBitmap(6),
            };
            var wifiTitle = new Label { Text = "Over Wi-Fi, no cable", Font = bold };
            var wifi = new Label
            {
                Text = "Put the phone on the same Wi-Fi as this PC. In Developer options, open Wireless debugging, " +
                       "turn it on, tap \"Pair device with QR code\" and scan this code.\r\n\r\n" +
                       "Or tap \"Pair device with pairing code\" and type the 6 digits here:",
            };
            _code = new TextBox { MaxLength = 12, Font = Theme.UiFont(13f) };
            var usbTitle = new Label { Text = "With a USB cable", Font = bold };
            var usb = new Label { Text = "Turn on USB debugging, plug the phone in, tick \"Always allow from this computer\" and tap Allow." };

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
            };
            _start = new Button { Text = "Looking for your phone...", Enabled = false };
            // While looking: confirms a phone that was already paired. Otherwise: looks again.
            _start.Click += (s, e) => { if (_watching) { _confirmed = true; _start.Enabled = false; } else Watch(); };
            Controls.AddRange(new Control[] { intro, qr, wifiTitle, wifi, _code, usbTitle, usb, _log, _start });
            Theme.Style(this);
            Theme.Primary(_start);
            foreach (var l in new[] { intro, wifiTitle, wifi, usbTitle, usb }) l.ForeColor = Theme.Text;
            _log.BorderStyle = BorderStyle.None;

            int Measure(Label l, int width) =>
                TextRenderer.MeasureText(l.Text, l.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + S(2);
            void Arrange()
            {
                int pad = S(16), w = ClientSize.Width;
                intro.SetBounds(pad, S(12), w - 2 * pad, Measure(intro, w - 2 * pad));
                int top = intro.Bottom + S(14), q = S(220);
                qr.SetBounds(pad, top, q, q);
                int x = pad + q + S(20), tw = Math.Max(S(200), w - x - pad);
                wifiTitle.SetBounds(x, top, tw, Measure(wifiTitle, tw));
                wifi.SetBounds(x, wifiTitle.Bottom + S(2), tw, Measure(wifi, tw));
                _code.SetBounds(x, wifi.Bottom + S(4), S(150), _code.PreferredHeight);
                usbTitle.SetBounds(x, _code.Bottom + S(18), tw, Measure(usbTitle, tw));
                usb.SetBounds(x, usbTitle.Bottom + S(2), tw, Measure(usb, tw));
                int logTop = Math.Max(qr.Bottom, usb.Bottom) + S(16), btn = S(46);
                _start.SetBounds(0, ClientSize.Height - btn, w, btn);
                _log.SetBounds(S(8), logTop, w - S(16), _start.Top - logTop - S(8));
            }
            Layout += (s, e) => Arrange();
            HandleCreated += (s, e) => Theme.ApplyChrome(this);
            Shown += (s, e) => { if (_tray != null) Watch(); };
            FormClosed += (s, e) => _closed = true;
        }

        internal static void Preview(string path)
        {
            using (var f = new SetupForm(null, new Config()))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, 0);
                f.Show();
                Application.DoEvents();
                f.Log("Waiting for your phone: scan the code, type a pairing code, or plug it in...");
                Application.DoEvents();
                using (var b = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                    b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        /// <summary>Setup or pairing is running: they use the adb server, which must not be stopped meanwhile.</summary>
        private static int _running;
        public static bool Busy => Volatile.Read(ref _running) > 0;
        internal static void BeginWork() => Interlocked.Increment(ref _running);
        internal static void EndWork() => Interlocked.Decrement(ref _running);

        /// <summary>Runs on the window's thread if it is still open; otherwise does nothing.</summary>
        internal static void OnUi(Control c, Action a)
        {
            try { if (!c.IsDisposed && c.IsHandleCreated) c.BeginInvoke(a); } catch { }
        }

        private void Log(string s)
        {
            Phonestick.Log.Write("setup: " + s.Trim());
            // The window may have been closed while setup goes on: then only the log file gets it.
            if (InvokeRequired || !IsHandleCreated) { OnUi(this, () => { if (!_log.IsDisposed) _log.AppendText(s + "\r\n"); }); return; }
            if (!_log.IsDisposed) _log.AppendText(s + "\r\n");
        }

        private string CodeTyped()
        {
            string v = "";
            try { Invoke((Action)(() => v = _code.Text)); } catch { }
            var digits = new string(v.Where(char.IsDigit).ToArray());
            return digits.Length == 6 ? digits : null;
        }

        private bool _watching;

        /// <summary>Waits for the phone (any of the ways shown), then sets it up.</summary>
        private void Watch()
        {
            if (_watching) return;
            if (Busy) { Log("Wait: a setup or pairing is still running."); return; }
            _watching = true;
            _start.Enabled = false;
            _start.Text = "Looking for your phone...";
            BeginWork();
            Task.Run(() =>
            {
                bool done = false;
                try
                {
                    Helpers.Ensure(Log);
                    var found = WaitForPhone();
                    if (found.phone == null) return; // window closed
                    RunSetup(found.phone, found.usb);
                    done = true;
                }
                catch (Exception ex)
                {
                    var m = ex is AggregateException ae ? ae.InnerException?.Message : ex.Message;
                    Log("\r\nStopped: " + m);
                }
                finally
                {
                    EndWork();
                    // After a setup, the tray app connects at once over the link this one opened:
                    // stopping adb in between would make it start over.
                    if (!done) Phone.StopServerIfUnused();
                    _watching = false;
                    OnUi(this, () =>
                    {
                        _start.Enabled = !done;
                        _start.Text = done ? "Done" : "Try again";
                    });
                    if (done) _tray.OpenPhoneSoon();
                }
            });
        }

        private volatile bool _confirmed;

        /// <summary>
        /// Waits for a phone. A USB phone, or one paired in this window (QR code or typed code),
        /// goes straight on. A phone this PC was already paired with only after a click, so
        /// opening this window never sets up a phone by itself.
        /// </summary>
        private (Phone phone, bool usb) WaitForPhone()
        {
            Log("Waiting for your phone: scan the code, type a pairing code, or plug it in...");
            Phone.StartServer();
            _confirmed = false;
            var lastTry = new Dictionary<string, DateTime>();
            var pairedHosts = new HashSet<string>();
            var failedPairing = new HashSet<string>();
            string offered = null;
            bool toldUnauthorized = false, toldCode = false, toldHint = false;
            string failedCode = null;
            var since = DateTime.UtcNow;
            // Each connected phone is described once (two adb round trips saved per round), and
            // the ones there before any pairing are told apart from the one just paired.
            var described = new Dictionary<string, Phone>();
            var before = new HashSet<string>();
            while (!_closed)
            {
                if (DateTime.UtcNow - since > TimeSpan.FromMinutes(10))
                    throw new UserError("Stopped looking after 10 minutes. Click Try again when the phone is ready.");
                if (!toldHint && DateTime.UtcNow - since > TimeSpan.FromSeconds(90))
                {
                    Log("Nothing yet? Check that the phone is on the same Wi-Fi as this PC, with Wireless debugging on. " +
                        "Or plug it in with USB once instead.");
                    toldHint = true;
                }
                foreach (var d in Phone.Devices())
                {
                    if (d.usb && d.state == "unauthorized" && !toldUnauthorized)
                    {
                        Log("On the phone: tick \"Always allow from this computer\" and tap Allow.");
                        toldUnauthorized = true;
                    }
                    if (d.state != "device" || d.serial.StartsWith("emulator-")) continue;
                    if (!described.TryGetValue(d.serial, out var p))
                    {
                        p = Phone.Describe(d.serial);
                        if (p == null) continue;
                        described[d.serial] = p;
                        if (pairedHosts.Count == 0) before.Add(d.serial);
                    }
                    // Explicit actions go on: a cable, the phone paired here, or the click on the
                    // button that offered this very phone.
                    bool justPaired = pairedHosts.Contains(d.serial.Split(':')[0])
                                      || (pairedHosts.Count > 0 && !before.Contains(d.serial));
                    if (d.usb || justPaired || (_confirmed && p.SerialNo == offered)) return (p, d.usb);
                    if (offered == null)
                    {
                        offered = p.SerialNo;
                        Log("Found " + p.Model + ", already paired with this PC. Click the button below to set it up, " +
                            "or pair another phone.");
                        var label = "Set up " + p.Model;
                        OnUi(this, () => { _start.Text = label; _start.Enabled = true; });
                    }
                }

                // Asked of the phones directly (see Mdns), both kinds in one query, then adb's own discovery.
                // Phones that answered before (a phone already set up, say) must not stop the
                // sweep that finds the new one: only a pairing offer ends it early.
                var direct = Mdns.Find(new[] { Mdns.Pairing, Mdns.Connect }, null, x => x.Type == Mdns.Pairing);

                // Pairing.
                var pairing = direct.Where(x => x.Type == Mdns.Pairing).Select(x => (x.Instance, x.Endpoint)).ToList();
                pairing.AddRange(Phone.MdnsServices("_adb-tls-pairing").Where(x => !pairing.Any(y => y.Endpoint == x.endpoint)));
                foreach (var (name, endpoint) in pairing)
                {
                    if (failedPairing.Contains(endpoint)) continue;
                    bool byQr = name == _qrName;
                    string secret = byQr ? _qrPass : CodeTyped();
                    if (secret == null || (!byQr && secret == failedCode))
                    {
                        if (!toldCode) Log("The phone shows a pairing code: type the 6 digits in the box above.");
                        toldCode = true;
                        continue;
                    }
                    Log("Pairing with the phone at " + endpoint + "...");
                    var r = Phone.Adb("pair " + Tools.Q(endpoint) + " " + secret, 30000);
                    if (r.Out.Contains("Successfully paired"))
                    {
                        Log("Paired. Connecting...");
                        pairedHosts.Add(endpoint.Split(':')[0]);
                        failedPairing.Add(endpoint); // done with this pairing port
                    }
                    else if (byQr)
                    {
                        Log("Pairing failed: " + r.All.Trim() + "\r\nOn the phone, scan the code again.");
                        failedPairing.Add(endpoint); // a new scan announces a new port
                    }
                    else
                    {
                        Log("Pairing failed: " + r.All.Trim() + "\r\nCheck the code on the phone and type it again.");
                        failedCode = secret;
                    }
                }

                // Paired phones: connect to them (only paired ones can be reached).
                var connect = direct.Where(x => x.Type == Mdns.Connect).Select(x => x.Endpoint).ToList();
                connect.AddRange(Phone.MdnsServices().Select(x => x.endpoint).Where(e => !connect.Contains(e)));
                foreach (var endpoint in connect)
                {
                    if (lastTry.TryGetValue(endpoint, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(5)) continue;
                    lastTry[endpoint] = DateTime.UtcNow;
                    Phone.Adb("connect " + endpoint, 10000);
                }
                Thread.Sleep(700);
            }
            return (null, false);
        }

        private void RunSetup(Phone p, bool overUsb)
        {
            var pkg = AppInfo.PhonePackage;
            Log("Found " + p.Model + " (" + p.SerialNo + ")" + (overUsb ? " on USB." : " on Wi-Fi."));

            var apk = Helpers.ExtractApk();
            Log("Installing the phone app...");
            var r = Phone.Adb("-s " + Tools.Q(p.Serial) + " install -r " + Tools.Q(apk), 180000);
            if (!r.Ok || !r.All.Contains("Success")) throw new UserError("Install failed:\r\n" + r.All);

            Log("Allowing it to switch wireless debugging (one-time permission)...");
            r = p.Shell("pm grant " + pkg + " android.permission.WRITE_SECURE_SETTINGS");
            if (!r.Ok) throw new UserError("Could not grant the permission:\r\n" + r.All);

            Log("Keeping this computer's authorization from expiring...");
            p.Shell("settings put global adb_allowed_connection_time 0");
            Log("Letting Android wake the app on network changes without delay...");
            p.Shell("cmd deviceidle whitelist +" + pkg);

            if (overUsb)
            {
                Log("Turning on wireless debugging...");
                p.Shell("settings put global adb_wifi_enabled 1");
                // The port is random and not readable from the adb shell on recent Android,
                // so find it the way the tray app will: the phone's mDNS announcement.
                Phone wifi = null;
                bool asked = false;
                // Its Wi-Fi address, readable over USB: asked directly, no sweep of the network.
                var m = System.Text.RegularExpressions.Regex.Match(
                    p.Shell("ip -f inet addr show wlan0", 10000).Out, @"inet (\d+\.\d+\.\d+\.\d+)/");
                System.Net.IPAddress wlan = m.Success ? System.Net.IPAddress.Parse(m.Groups[1].Value) : null;
                bool Mine(Mdns.Service x) => Phone.NameMatches(x.Instance, p.SerialNo);
                for (int i = 0; i < 45 && wifi == null; i++)
                {
                    Thread.Sleep(2000);
                    var on = p.Shell("settings get global adb_wifi_enabled", 10000).Out.Trim() == "1";
                    if (!on)
                    {
                        if (!asked) Log("On the phone: tick \"Always allow on this network\" and tap Allow.");
                        asked = true;
                        continue;
                    }
                    var endpoints = Mdns.Find(Mdns.Connect, wlan, Mine).Select(x => (x.Instance, x.Endpoint)).ToList();
                    endpoints.AddRange(Phone.MdnsServices().Where(x => !endpoints.Any(y => y.Endpoint == x.endpoint)));
                    foreach (var (name, endpoint) in endpoints)
                    {
                        if (!Phone.NameMatches(name, p.SerialNo)) continue;
                        Log("Connecting over Wi-Fi to " + endpoint + "...");
                        Phone.Adb("connect " + endpoint, 20000);
                        var candidate = new Phone { Serial = endpoint };
                        if (candidate.Shell("getprop ro.serialno", 15000).Out.Trim() == p.SerialNo) { wifi = candidate; break; }
                    }
                }
                if (wifi == null)
                    throw new UserError("This computer could not reach the phone over Wi-Fi. Check that both are on the same Wi-Fi " +
                        "network (some guest networks keep devices apart). Or pair over Wi-Fi with the QR code above and try again.");
                Log("Wi-Fi link works.");
                _cfg.LastEndpoint = wifi.Serial;
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(p.Serial, @"^\d+\.\d+\.\d+\.\d+:\d+$"))
                _cfg.LastEndpoint = p.Serial; // asked first at the next connect
            else if (p.SerialNo != _cfg.PhoneSerial)
                _cfg.LastEndpoint = ""; // another phone's address would only be asked in vain

            _cfg.PhoneSerial = p.SerialNo;
            _cfg.PhoneModel = p.Model;
            _cfg.Save();
            _tray.ConfigChanged();

            Log("Adding the " + AppInfo.Name + " tile to Quick Settings...");
            var tile = p.Shell("cmd statusbar add-tile " + pkg + "/.PhonestickTileService", 15000);
            if (!tile.Ok) Log("  (could not add it: " + tile.All + ". The app will offer to add it.)");

            Log("Switching the phone app on (USB debugging is locked off now)...");
            // The app first: switching on locks USB debugging off, which ends a USB link.
            p.Shell("am start -n " + pkg + "/.MainActivity", 15000);
            p.Shell("am broadcast -a " + pkg + ".ARM -n " + pkg + "/.CommandReceiver", 15000);

            Log("\r\nDone." + (overUsb ? " You can unplug the phone." : ""));
            Log("Opening your phone's files now. Next time: click the tray icon, or Open phone files.");
            Log("Tile on: this computer can reach the phone on this Wi-Fi. Tile off: nothing can.");
            Log("Normal: Android asks \"Allow wireless debugging on this network?\" once more for each other access point " +
                "(a repeater, a mesh unit, often each band of the router). Tick Always allow each time and it stops.");
        }
    }

    /// <summary>Pairs this computer with the phone using Android's Wi-Fi pairing code.</summary>
    internal sealed class PairForm : Form
    {
        public static void ShowPair()
        {
            using (var f = new PairForm()) f.ShowDialog();
        }

        private PairForm()
        {
            Text = AppInfo.Name + ": pair with a Wi-Fi code";
            Icon = Icons.App;
            Font = Theme.UiFont(10f);
            Width = 520;
            Height = 300;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var help = new Label
            {
                Left = 12, Top = 12, Width = 480, Height = 70,
                Text = "On the phone: Developer options > Wireless debugging > Pair device with pairing code. " +
                       "Enter the IP address and port and the 6-digit code it shows.",
            };
            var l1 = new Label { Left = 12, Top = 92, Width = 140, Text = "IP address:port" };
            var ep = new TextBox { Left = 160, Top = 88, Width = 220 };
            var l2 = new Label { Left = 12, Top = 126, Width = 140, Text = "Pairing code" };
            var code = new TextBox { Left = 160, Top = 122, Width = 120 };
            var status = new Label { Left = 12, Top = 160, Width = 480, Height = 40 };
            var ok = new Button { Left = 300, Top = 210, Width = 90, Text = "Pair" };
            var cancel = new Button { Left = 400, Top = 210, Width = 90, Text = "Close", DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                if (SetupForm.Busy) { status.Text = "Wait: a setup is still running."; return; }
                ok.Enabled = false;
                status.Text = "Pairing...";
                var endpoint = ep.Text.Trim();
                var pin = new string(code.Text.Where(char.IsDigit).ToArray());
                SetupForm.BeginWork();
                Task.Run(() =>
                {
                    string msg;
                    try
                    {
                        Helpers.Ensure(m => SetupForm.OnUi(this, () => status.Text = m));
                        Phone.StartServer();
                        var r = Phone.Adb("pair " + Tools.Q(endpoint) + " " + pin, 30000);
                        msg = r.Out.Contains("Successfully paired")
                            ? "Paired. Now click the tray icon, the gear, then Set up a phone."
                            : "Pairing failed: " + r.All;
                    }
                    catch (Exception ex) { msg = ex.Message; }
                    finally { SetupForm.EndWork(); }
                    SetupForm.OnUi(this, () => { status.Text = msg; ok.Enabled = true; });
                });
            };
            Controls.AddRange(new Control[] { help, l1, ep, l2, code, status, ok, cancel });
            Theme.Style(this);
            Theme.Primary(ok);
            ok.Height = cancel.Height = 32;
            // Laid out in 96-dpi pixels above: scaled to this screen, as the fonts already are.
            float k;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            if (Math.Abs(k - 1f) > 0.01f) Scale(new SizeF(k, k));
            HandleCreated += (s, e) => Theme.ApplyChrome(this);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
