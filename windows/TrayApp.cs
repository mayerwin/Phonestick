using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Phonestick
{
    /// <summary>
    /// The tray icon. Idle, it does nothing at all: no timers, no network, no adb.
    /// Everything happens when a menu item is clicked; the one timer (auto-disconnect)
    /// only runs while the drive is mounted.
    /// </summary>
    internal sealed class ControlContext : SynchronizationContext
    {
        private readonly Control _c;
        public ControlContext(Control c) { _c = c; }
        public override void Post(SendOrPostCallback d, object state) => _c.BeginInvoke(d, state);
        public override void Send(SendOrPostCallback d, object state) => _c.Invoke(d, state);
    }

    internal sealed class TrayApp : ApplicationContext, IPanelHost
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private readonly NotifyIcon _tray;
        private Icon _iconIdle = Icons.Tray(Icons.TrayState.Idle);
        private Icon _iconActive = Icons.Tray(Icons.TrayState.On);
        private Icon _iconMounted = Icons.Tray(Icons.TrayState.Mounted);
        private readonly SynchronizationContext _ui;
        private readonly Config _cfg = Config.Load();
        private readonly Mount _mount = new Mount();
        private readonly System.Windows.Forms.Timer _idleTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        private Process _scrcpy;
        private bool _busy;
        private string _busyText = "Connecting...";
        /// <summary>Set by Disconnect while a connect is running: its error, if any, is not shown.</summary>
        private volatile bool _cancelled;

        private ToolStripMenuItem _open, _disconnect, _screen;

        public TrayApp(EventWaitHandle openSignal, bool openNow)
        {
            // Marshal back to this (UI) thread through a control whose window exists now.
            // SynchronizationContext.Current is not set yet before Application.Run, and a
            // context created lazily can end up bound to a worker thread with no message
            // loop, where timers never tick.
            var invoker = new Control();
            invoker.CreateControl();
            if (invoker.Handle == IntPtr.Zero) throw new InvalidOperationException("no window handle");
            _ui = new ControlContext(invoker);
            _tray = new NotifyIcon { Icon = _iconIdle, Visible = true, Text = AppInfo.Name };
            _tray.ContextMenuStrip = BuildMenu();
            _tray.ContextMenuStrip.Opening += (s, e) => RefreshMenu();
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
            _mount.Ended += () => _ui.Post(_ => OnMountEnded(), null);
            _idleTimer.Tick += (s, e) => CheckIdle();
            // Redraw the tray glyph when the taskbar theme or scaling changes (events, not polling).
            SystemEvents.UserPreferenceChanged += (s, e) => _ui.Post(_ => RedrawTrayIcons(), null);
            SystemEvents.DisplaySettingsChanged += (s, e) => _ui.Post(_ => RedrawTrayIcons(), null);

            if (!_cfg.FirstRunDone)
            {
                _cfg.FirstRunDone = true;
                _cfg.Save();
                SetAutostart(true);
                Balloon(AppInfo.Name + " is in the tray",
                    string.IsNullOrEmpty(_cfg.PhoneSerial)
                        ? "Right-click the icon and choose \"Set up phone\" to get started."
                        : "Click the icon to open your phone.");
            }
            RefreshMenu();

            // "Phonestick.exe --open" from a shortcut signals this event. Waiting on it costs nothing.
            ThreadPool.RegisterWaitForSingleObject(openSignal,
                (st, timedOut) => _ui.Post(_ => OpenPhone(), null), null, Timeout.Infinite, false);
            if (openNow) _ui.Post(_ => OpenPhone(), null);
        }

        // ------------------------------------------------------------------ menu

        private ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip
            {
                Renderer = new MenuRenderer(),
                Font = Theme.UiFont(9.5f),
                ShowImageMargin = false,
                ShowCheckMargin = false,
                Padding = new Padding(4),
            };
            _open = new ToolStripMenuItem("Open phone files", null, (s, e) => OpenPhone());
            _screen = new ToolStripMenuItem("Show phone screen", null, (s, e) => ShowScreen());
            _disconnect = new ToolStripMenuItem("Disconnect", null, (s, e) => Disconnect());
            foreach (var i in new[] { _open, _screen, _disconnect }) i.Padding = new Padding(6, 5, 6, 5);
            var settings = new ToolStripMenuItem("Settings", null, (s, e) => ShowPanel(true)) { Padding = new Padding(6, 5, 6, 5) };
            var quit = new ToolStripMenuItem("Quit " + AppInfo.Name, null, (s, e) => ExitApp()) { Padding = new Padding(6, 5, 6, 5) };
            m.Items.AddRange(new ToolStripItem[] { _open, _screen, _disconnect, new ToolStripSeparator(), settings, quit });
            return m;
        }

        private void RedrawTrayIcons()
        {
            _iconIdle = Icons.Tray(Icons.TrayState.Idle);
            _iconActive = Icons.Tray(Icons.TrayState.On);
            _iconMounted = Icons.Tray(Icons.TrayState.Mounted);
            RefreshMenu();
        }

        private void RefreshMenu()
        {
            bool setUp = !string.IsNullOrEmpty(_cfg.PhoneSerial);
            bool screen = _scrcpy != null && !_scrcpy.HasExited;
            var name = string.IsNullOrEmpty(_cfg.PhoneModel) ? "phone" : _cfg.PhoneModel;
            var status = !setUp ? "no phone set up yet"
                : _mount.IsMounted ? name + " on " + _mount.Drive
                : screen ? name + ", screen open"
                : _busy ? "connecting..."
                : name;
            _open.Text = _mount.IsMounted ? "Show phone files" : "Open phone files";
            _open.Enabled = setUp && (_mount.IsMounted || !_busy);
            _screen.Enabled = setUp && !_busy && !screen;
            _disconnect.Enabled = _busy || _mount.IsMounted || screen;
            _disconnect.Text = _busy && !_mount.IsMounted && !screen ? "Cancel" : "Disconnect";
            _tray.Icon = _mount.IsMounted ? _iconMounted : (screen || _busy) ? _iconActive : _iconIdle;
            _tray.Text = Trim63(AppInfo.Name + ": " + status);
            if (Flyout.Current != null && !Flyout.Current.IsDisposed) Flyout.Current.UpdateState(BuildState());
        }

        // ------------------------------------------------------------------ flyout

        /// <summary>"--demo": shows a connected phone named "Pixel" without connecting (screenshots).</summary>
        internal static bool Demo, DemoIdle;

        private FlyoutState BuildState()
        {
            bool setUp = !string.IsNullOrEmpty(_cfg.PhoneSerial);
            if (Demo || DemoIdle) return new FlyoutState { SetUp = true, Mounted = Demo, Drive = "P:", Phone = "Pixel", IdleMinutes = 60, Autostart = true };
            return new FlyoutState
            {
                SetUp = setUp,
                Busy = _busy,
                BusyText = _busyText,
                Mounted = _mount.IsMounted,
                Screen = _scrcpy != null && !_scrcpy.HasExited,
                Drive = _mount.IsMounted ? _mount.Drive : _cfg.Drive,
                Phone = string.IsNullOrEmpty(_cfg.PhoneModel) ? "Your phone" : _cfg.PhoneModel,
                IdleMinutes = _cfg.IdleMinutes,
                Autostart = IsAutostart(),
            };
        }

        private void ToggleFlyout()
        {
            if (Flyout.Current != null && !Flyout.Current.IsDisposed) { Flyout.Current.Close(); return; }
            ShowPanel(false);
        }

        private void ShowPanel(bool settings)
        {
            if (Flyout.Current != null && !Flyout.Current.IsDisposed) Flyout.Current.Close();
            RefreshMenu();
            new Flyout(BuildState(), this, settings).ShowNearTray();
        }

        /// <summary>"Phonestick.exe --panel": opens the panel at start (for a shortcut, or screenshots).</summary>
        internal void ShowPanelSoon(bool settings) => _ui.Post(_ => ShowPanel(settings), null);

        // IPanelHost
        void IPanelHost.Open() => OpenPhone();
        void IPanelHost.Screen() => ShowScreen();
        void IPanelHost.Disconnect() => Disconnect();
        void IPanelHost.SetIdle(int minutes)
        {
            _cfg.IdleMinutes = minutes;
            _cfg.Save();
            if (_mount.IsMounted) { if (minutes > 0) _idleTimer.Start(); else _idleTimer.Stop(); }
            RefreshMenu();
        }
        void IPanelHost.NextDrive()
        {
            if (_mount.IsMounted) return;
            var used = DriveInfo.GetDrives().Select(d => d.Name.Substring(0, 2).ToUpperInvariant()).ToList();
            char c = _cfg.Drive[0];
            for (int i = 0; i < 26; i++)
            {
                c = c >= 'Z' ? 'D' : (char)(c + 1);
                if (!used.Contains(c + ":")) break;
            }
            _cfg.Drive = c + ":";
            _cfg.Save();
            RefreshMenu();
        }
        void IPanelHost.SetAutostart(bool on) { SetAutostart(on); RefreshMenu(); }
        void IPanelHost.Setup() => SetupForm.ShowSetup(this, _cfg);
        void IPanelHost.Pair() => PairForm.ShowPair();
        void IPanelHost.Help() { try { Process.Start(AppInfo.RepoUrl); } catch { } }
        void IPanelHost.Quit() => ExitApp();

        internal static string IdleLabel(int min) =>
            min == 0 ? "Never" : min < 60 ? min + " minutes" : (min / 60) + (min == 60 ? " hour" : " hours");

        // ------------------------------------------------------------------ actions

        private void OpenPhone()
        {
            if (_mount.IsMounted) { Explore(_mount.Drive); return; }
            if (string.IsNullOrEmpty(_cfg.PhoneSerial)) { SetupForm.ShowSetup(this, _cfg); return; }
            Run("Connecting to your phone", log =>
            {
                var p = FindPhone();
                _mount.Start(p, _cfg, log);
                _ui.Post(_ =>
                {
                    if (_cfg.IdleMinutes > 0) _idleTimer.Start();
                    Log.Write("mounted " + _mount.Drive + ", idle timer " + (_idleTimer.Enabled ? "on" : "off"));
                    Explore(_mount.Drive);
                }, null);
            });
        }

        private void ShowScreen()
        {
            if (Tools.Scrcpy == null)
            {
                MessageBox.Show("scrcpy was not found. Install it with:\nwinget install Genymobile.scrcpy", AppInfo.Name);
                return;
            }
            Run("Connecting to your phone", log =>
            {
                var p = FindPhone();
                var psi = new ProcessStartInfo(Tools.Scrcpy, "-s " + Tools.Q(p.Serial) + " --window-title " + Tools.Q(p.Model)
                    + " --stay-awake")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                Tools.ApplyAdbEnv(psi);
                var err = new System.Text.StringBuilder();
                var proc = Process.Start(psi);
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();
                // scrcpy fails fast when something is wrong: report what it said.
                if (proc.WaitForExit(6000) && proc.ExitCode != 0)
                {
                    string said;
                    lock (err) said = err.ToString().Trim();
                    Log.Write("scrcpy failed: " + said);
                    throw new UserError("The phone screen could not be shown.\n\n" +
                        (said.Length > 900 ? said.Substring(said.Length - 900) : said));
                }
                proc.EnableRaisingEvents = true;
                proc.Exited += (s, e) => _ui.Post(_ =>
                {
                    if (!_mount.IsMounted && !_busy) Task.Run(() => Phone.StopServer());
                    RefreshMenu();
                }, null);
                _scrcpy = proc;
            });
        }

        private Phone FindPhone()
        {
            var p = Phone.FindOnWifi(_cfg.PhoneSerial, 15000, _cfg.LastEndpoint);
            if (p != null && System.Text.RegularExpressions.Regex.IsMatch(p.Serial, @"^\d+\.\d+\.\d+\.\d+:\d+$") && p.Serial != _cfg.LastEndpoint)
            {
                _cfg.LastEndpoint = p.Serial;
                _cfg.Save();
            }
            if (p != null) { var phone = p; Task.Run(() => { Thread.Sleep(1500); phone.HideDebugNotice(); }); }
            if (p == null)
            {
                // Tell apart "phone not on this network" from "phone here, but access is off".
                var host = (_cfg.LastEndpoint ?? "").Split(':')[0];
                bool answers = false;
                if (host.Length > 0)
                {
                    try
                    {
                        using (var ping = new System.Net.NetworkInformation.Ping())
                            answers = ping.Send(host, 1500).Status == System.Net.NetworkInformation.IPStatus.Success;
                    }
                    catch { }
                }
                throw new UserError(answers
                    ? "Your phone is on this network, but it is not accepting connections.\n\n" +
                      "Pull down Quick Settings and check the " + AppInfo.Name + " tile is on. If it already is, tap it off and on."
                    : "Your phone was not found on this network.\n\n" +
                      "Check that the phone is on the same Wi-Fi as this computer and that the " + AppInfo.Name + " tile is on.");
            }
            return p;
        }

        /// <summary>Disconnects now, even while a connect is still running (it then stops quietly).</summary>
        private void Disconnect()
        {
            if (_busy)
            {
                _cancelled = true;
                Task.Run(() => DisconnectAll());
                return;
            }
            Run("Disconnecting", log => DisconnectAll());
        }

        private void DisconnectAll()
        {
            var phone = _mount.Phone;
            _mount.Stop();
            try { if (_scrcpy != null && !_scrcpy.HasExited) _scrcpy.Kill(); } catch { }
            try { phone?.Disconnect(); } catch { }
            Phone.StopServer();
            _ui.Post(_ => _idleTimer.Stop(), null);
        }

        private bool _checking;

        private void CheckIdle()
        {
            if (!_mount.IsMounted) { _idleTimer.Stop(); return; }
            if (_cfg.IdleMinutes <= 0 || _checking || _busy) return;
            _checking = true;
            var limit = TimeSpan.FromMinutes(_cfg.IdleMinutes);
            Task.Run(() => _mount.IdleFor(limit)).ContinueWith(t =>
            {
                _checking = false;
                if (t.Status == TaskStatus.RanToCompletion && t.Result && _mount.IsMounted)
                {
                    Run("Disconnecting", log => DisconnectAll());
                    Balloon("Phone disconnected", "Unused for " + IdleLabel(_cfg.IdleMinutes) + ". Open it again from the tray.");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnMountEnded()
        {
            Log.Write("mount ended");
            _idleTimer.Stop();
            if (!_busy && (_scrcpy == null || _scrcpy.HasExited)) Task.Run(() => Phone.StopServer());
            RefreshMenu();
        }

        /// <summary>Runs an action off the UI thread, showing errors as messages.</summary>
        internal void Run(string what, Action<Action<string>> action)
        {
            if (_busy) return;
            _busy = true;
            _cancelled = false;
            _busyText = what.StartsWith("Disconnect") ? "Disconnecting..." : "Connecting...";
            RefreshMenu();
            _tray.Text = AppInfo.Name + ": " + what + "...";
            Task.Run(() =>
            {
                string error = null;
                try { action(msg => _ui.Post(_ => { _tray.Text = Trim63(AppInfo.Name + ": " + msg); }, null)); }
                catch (AggregateException ae) { error = ae.InnerException?.Message ?? ae.Message; }
                catch (UserError ue) { error = ue.Message; }
                catch (Exception ex) { error = ex.Message; }
                if (error != null) Log.Write(what + ": " + error + (_cancelled ? " (cancelled)" : ""));
                if (_cancelled) error = null;
                _ui.Post(_ =>
                {
                    _busy = false;
                    RefreshMenu();
                    if (error != null) MessageBox.Show(error, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }, null);
            });
        }

        internal void ConfigChanged() => _ui.Post(_ => RefreshMenu(), null);

        // ------------------------------------------------------------------ helpers

        private static string Trim63(string s) => s.Length > 63 ? s.Substring(0, 63) : s;

        private static void Explore(string drive)
        {
            try { Process.Start("explorer.exe", drive + "\\"); } catch { }
        }

        private void Balloon(string title, string text)
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.ShowBalloonTip(5000);
        }

        private static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k?.GetValue(AppInfo.Name) != null;
        }

        private static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppInfo.Name, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(AppInfo.Name, false);
            }
        }

        private void ExitApp()
        {
            _tray.Visible = false;
            DisconnectAll();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Dispose();
                _idleTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
