using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Phonestick
{
    /// <summary>One-time setup of a phone, over the USB cable (or an already paired Wi-Fi link).</summary>
    internal sealed class SetupForm : Form
    {
        private static SetupForm _open;
        private readonly TrayApp _tray;
        private readonly Config _cfg;
        private readonly TextBox _log;
        private readonly Button _start;

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
            Width = 640;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.UiFont(10f);

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 200,
                Padding = new Padding(12),
                Text =
                    "Before you start, on the phone:\r\n" +
                    "1. Settings > About phone: tap Build number 7 times.\r\n" +
                    "2. Settings > System > Developer options: turn on \"Disable adb authorization timeout\", then either\r\n" +
                    "    - Wi-Fi (no cable): turn on Wireless debugging and use \"Pair with a Wi-Fi code\" from the tray menu, or\r\n" +
                    "    - USB: turn on USB debugging, plug the phone in, tick \"Always allow from this computer\" and tap Allow.\r\n\r\n" +
                    "Then click Start. The phone app is installed, its tile is added to Quick Settings, and access is switched on. " +
                    "USB debugging is locked off, so from then on only this computer can connect, over Wi-Fi, while the tile is on.",
            };
            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
            };
            _start = new Button { Text = "Start", Dock = DockStyle.Bottom, Height = 44 };
            _start.Click += (s, e) => Start();
            Controls.Add(_log);
            Controls.Add(_start);
            Controls.Add(intro);
            Theme.Style(this);
            Theme.Primary(_start);
            intro.ForeColor = Theme.Text;
            _log.BorderStyle = BorderStyle.None;
            HandleCreated += (s, e) => Theme.ApplyChrome(this);
        }

        private void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(s))); return; }
            _log.AppendText(s + "\r\n");
        }

        private void Start()
        {
            _start.Enabled = false;
            Task.Run(() =>
            {
                try
                {
                    RunSetup();
                }
                catch (Exception ex)
                {
                    var m = ex is AggregateException ae ? ae.InnerException?.Message : ex.Message;
                    Log("\r\nStopped: " + m);
                }
                finally
                {
                    BeginInvoke(new Action(() => { _start.Enabled = true; _start.Text = "Run again"; }));
                }
            });
        }

        private void RunSetup()
        {
            var pkg = AppInfo.PhonePackage;
            Helpers.Ensure(Log);
            Log("Looking for the phone on USB...");
            var p = Phone.FindUsb();
            bool overUsb = p != null;
            if (p == null)
            {
                Log("No phone on USB. Looking for an already paired phone on Wi-Fi...");
                p = Phone.FindOnWifi("", 8000);
            }
            if (p == null) throw new UserError("No phone found. Plug it in with USB debugging on, and allow this computer.");
            Log("Found " + p.Model + " (" + p.SerialNo + ").");

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
                        "and that Windows lets adb.exe through the firewall. If the phone refuses this computer over Wi-Fi, " +
                        "open Developer options > Wireless debugging > Pair device with pairing code on the phone, " +
                        "use \"Pair with a Wi-Fi code\" in the tray menu, and run this setup again.");
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

            Log("\r\nDone.");
            Log("- Unplug the phone.");
            Log("- Tile on: this computer can reach the phone on this Wi-Fi. Tile off: nothing can.");
            Log("- Open your files from the tray icon: Open phone files.");
            Phone.StopServer();
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
                ok.Enabled = false;
                status.Text = "Pairing...";
                var endpoint = ep.Text.Trim();
                var pin = new string(code.Text.Where(char.IsDigit).ToArray());
                Task.Run(() =>
                {
                    string msg;
                    try
                    {
                        Helpers.Ensure(m => BeginInvoke(new Action(() => status.Text = m)));
                        Phone.StartServer();
                        var r = Phone.Adb("pair " + Tools.Q(endpoint) + " " + pin, 30000);
                        msg = r.Out.Contains("Successfully paired")
                            ? "Paired. Now run \"Set up phone\" from the tray menu."
                            : "Pairing failed: " + r.All;
                    }
                    catch (Exception ex) { msg = ex.Message; }
                    BeginInvoke(new Action(() => { status.Text = msg; ok.Enabled = true; }));
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
