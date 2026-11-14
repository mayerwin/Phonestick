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
            _start.Click += (s, e) => Watch();
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
                    Phone.StopServerIfUnused();
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

        private (Phone phone, bool usb) WaitForPhone()
        {
            Log("Waiting for your phone: scan the code, type a pairing code, or plug it in...");
            Phone.StartServer();
            var tried = new HashSet<string>();
            var paired = new HashSet<string>();
            bool toldUnauthorized = false, toldCode = false, toldMdns = false;
            string failedCode = null;
            var since = DateTime.UtcNow;
            while (!_closed)
            {
                if (!toldMdns && DateTime.UtcNow - since > TimeSpan.FromSeconds(90))
                {
                    Log("Nothing yet? Check the phone is on the same Wi-Fi as this PC. If you scanned the code and nothing " +
                        "happens, this network may hide devices from each other: plug the phone in with USB once instead.");
                    toldMdns = true;
                }
                foreach (var d in Phone.Devices())
                {
                    if (d.usb && d.state == "unauthorized" && !toldUnauthorized)
                    {
                        Log("On the phone: tick \"Always allow from this computer\" and tap Allow.");
                        toldUnauthorized = true;
                    }
                    if (d.state != "device" || d.serial.StartsWith("emulator-")) continue;
                    var p = Phone.Describe(d.serial);
                    if (p != null) return (p, d.usb);
                }
                foreach (var (name, endpoint) in Phone.MdnsServices("_adb-tls-pairing"))
                {
                    if (paired.Contains(endpoint)) continue;
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
                    if (r.Out.Contains("Successfully paired")) { Log("Paired. Connecting..."); paired.Add(endpoint); }
                    else if (byQr)
                    {
                        Log("Pairing failed: " + r.All.Trim() + "\r\nOn the phone, scan the code again.");
                        paired.Add(endpoint); // a new scan announces a new port
                    }
                    else
                    {
                        Log("Pairing failed: " + r.All.Trim() + "\r\nCheck the code on the phone and type it again.");
                        failedCode = secret;
                    }
                }
                // Paired phones announce themselves; adb connects on its own, this makes sure.
                foreach (var (_, endpoint) in Phone.MdnsServices())
                    if (tried.Add(endpoint)) Phone.Adb("connect " + endpoint, 10000);
                Thread.Sleep(1500);
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
                    foreach (var (name, endpoint) in Phone.MdnsServices())
                    {
                        if (!name.Contains(p.SerialNo)) continue;
                        Log("Connecting over Wi-Fi to " + endpoint + "...");
                        Phone.Adb("connect " + endpoint, 20000);
                        var candidate = new Phone { Serial = endpoint };
                        if (candidate.Shell("getprop ro.serialno", 15000).Out.Trim() == p.SerialNo) { wifi = candidate; break; }
                    }
                }
                if (wifi == null)
                    throw new UserError("This computer could not reach the phone over Wi-Fi. Check that both are on the same network " +
                        "and that Windows lets adb.exe through the firewall. Or pair over Wi-Fi with the QR code above and try again.");
                Log("Wi-Fi link works.");
            }

            _cfg.PhoneSerial = p.SerialNo;
            _cfg.PhoneModel = p.Model;
            _cfg.Save();
            _tray.ConfigChanged();

            Log("Adding the " + AppInfo.Name + " tile to Quick Settings...");
            var tile = p.Shell("cmd statusbar add-tile " + pkg + "/.PhonestickTileService", 15000);
            if (!tile.Ok) Log("  (could not add it: " + tile.All + ". The app will offer to add it.)");

            Log("Switching the phone app on (USB debugging is locked off now)...");
            p.Shell("am broadcast -a " + pkg + ".ARM -n " + pkg + "/.CommandReceiver", 15000);
            p.Shell("am start -n " + pkg + "/.MainActivity", 15000);

            Log("\r\nDone." + (overUsb ? " You can unplug the phone." : ""));
            Log("Opening your phone's files now. Next time: click the tray icon, or Open phone files.");
            Log("Tile on: this computer can reach the phone on this Wi-Fi. Tile off: nothing can.");
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
                            ? "Paired. Now click the tray icon, then Set up your phone."
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
            HandleCreated += (s, e) => Theme.ApplyChrome(this);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
