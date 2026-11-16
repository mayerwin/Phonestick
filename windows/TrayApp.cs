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

        private volatile Process _scrcpy;
        private volatile bool _busy;
        private string _busyText = "Connecting...";
        /// <summary>Set by Disconnect while a connect is running: its error, if any, is not shown.</summary>
        private volatile bool _cancelled;

        private ToolStripMenuItem _open, _disconnect, _screen, _updateItem;

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
            _mount.SdChanged += () => _ui.Post(_ => RefreshMenu(), null);
            _idleTimer.Tick += (s, e) => CheckIdle();
            _tray.BalloonTipClicked += (s, e) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
            _tray.BalloonTipClosed += (s, e) => _balloonClick = null;
            // Redraw the tray glyph when the taskbar theme or scaling changes (events, not polling).
            // Only for the kinds of change that can alter them (this event fires for many settings).
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color
                    && e.Category != UserPreferenceCategory.VisualStyle && e.Category != UserPreferenceCategory.Desktop) return;
                Theme.Refresh();
                _ui.Post(_ => RedrawTrayIcons(), null);
            };
            SystemEvents.DisplaySettingsChanged += (s, e) => _ui.Post(_ => RedrawTrayIcons(), null);
            // Going to sleep: close the drive and the screen cleanly. After a resume the link to the
            // phone is gone, and a dead drive would make Explorer hang on it.
            SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Suspend) OnSuspend(); };
            // The adb server is only stopped when nothing uses it any more.
            Phone.InUse = () => _busy || _mount.IsMounted || ScreenOpen || SetupForm.Busy;

            if (!_cfg.FirstRunDone)
            {
                try
                {
                    _cfg.FirstRunDone = true;
                    _cfg.Save();
                    SetAutostart(true);
                    // Fetch the helper programs right away, in the background, so the first
                    // "Open phone files" is quick. Any failure is reported when they are needed.
                    Task.Run(() => { try { if (!Helpers.AllPresent) Helpers.Ensure(m => Log.Write(m)); } catch (Exception ex) { Log.Write("helpers: " + ex.Message); } });
                    Balloon(AppInfo.Name + " is in the tray",
                        string.IsNullOrEmpty(_cfg.PhoneSerial)
                            ? "Click the icon to set up your phone."
                            : "Click the icon to open your phone.");
                }
                catch (Exception ex) { Log.Write("first run: " + ex.Message); }
            }
            RefreshMenu();
            // First update check a minute after start, away from the login rush. One-shot.
            var first = new System.Windows.Forms.Timer { Interval = 60000 };
            first.Tick += (s, e) => { first.Stop(); first.Dispose(); MaybeCheckForUpdates(); };
            first.Start();

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
            _updateItem = new ToolStripMenuItem("Update", null, (s, e) => { if (_update != null) InstallUpdate(_update); })
            { Padding = new Padding(6, 5, 6, 5), Visible = false, Font = Theme.UiFont(9.5f, FontStyle.Bold) };
            m.Items.AddRange(new ToolStripItem[] { _open, _screen, _disconnect, new ToolStripSeparator(), _updateItem, settings, quit });
            return m;
        }

        private void RedrawTrayIcons()
        {
            var old = new[] { _iconIdle, _iconActive, _iconMounted };
            _iconIdle = Icons.Tray(Icons.TrayState.Idle);
            _iconActive = Icons.Tray(Icons.TrayState.On);
            _iconMounted = Icons.Tray(Icons.TrayState.Mounted);
            RefreshMenu(); // the tray now shows a new icon: the old ones can go
            foreach (var i in old) i?.Dispose();
        }

        private bool ScreenOpen
        {
            get
            {
                var s = _scrcpy; // read once: it is replaced from a worker thread
                try { return s != null && !s.HasExited; } catch { return false; }
            }
        }

        /// <summary>The running operation is a connect, which "Cancel" may stop.</summary>
        private bool Connecting => _busy && _busyText == "Connecting...";

        private void RefreshMenu()
        {
            bool setUp = !string.IsNullOrEmpty(_cfg.PhoneSerial);
            bool screen = ScreenOpen;
            var name = string.IsNullOrEmpty(_cfg.PhoneModel) ? "phone" : _cfg.PhoneModel;
            var status = !setUp ? "no phone set up yet"
                : _mount.IsMounted ? name + " on " + _mount.Drive + (_mount.SdDrive != null ? ", SD card on " + _mount.SdDrive : "")
                : screen ? name + ", screen open"
                : _busy ? "connecting..."
                : name;
            _open.Text = _mount.IsMounted ? "Show phone files" : "Open phone files";
            _open.Enabled = setUp && (_mount.IsMounted || !_busy);
            _screen.Enabled = setUp && !_busy && !screen;
            // While busy, only a connect can be cancelled (not an update or a disconnect).
            _disconnect.Enabled = Connecting || (!_busy && (_mount.IsMounted || screen));
            _disconnect.Text = Connecting && !_mount.IsMounted && !screen ? "Cancel" : "Disconnect";
            _updateItem.Visible = _update != null;
            if (_update != null) _updateItem.Text = "Update to " + AppInfo.Name + " " + _update.VersionText;
            _updateItem.Enabled = !_busy;
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
            if (Demo || DemoIdle) return new FlyoutState { SetUp = true, Mounted = Demo, Drive = "P:", Phone = "Pixel", IdleMinutes = 60, Autostart = true, UpdateText = "v" + Updater.CurrentText };
            return new FlyoutState
            {
                SetUp = setUp,
                Busy = _busy,
                BusyText = _busyText,
                Mounted = _mount.IsMounted,
                Screen = ScreenOpen,
                Drive = _mount.IsMounted ? _mount.Drive : _cfg.Drive,
                Phone = string.IsNullOrEmpty(_cfg.PhoneModel) ? "Your phone" : _cfg.PhoneModel,
                IdleMinutes = _cfg.IdleMinutes,
                Autostart = IsAutostart(),
                CheckUpdates = _cfg.CheckUpdates,
                UpdateText = UpdateText(),
                UpdateAvailable = _update?.VersionText,
            };
        }

        private void ToggleFlyout()
        {
            if (Flyout.Current != null && !Flyout.Current.IsDisposed) { Flyout.Current.Close(); return; }
            // The click on the icon has just closed the panel (it lost focus): leave it closed.
            if (unchecked(Environment.TickCount - Flyout.LastClosedTick) < 400) return;
            ShowPanel(false);
        }

        private void ShowPanel(bool settings)
        {
            if (Flyout.Current != null && !Flyout.Current.IsDisposed) Flyout.Current.Close();
            RefreshMenu();
            new Flyout(BuildState(), this, settings).ShowNearTray();
            if (!settings) PrewarmIfLikely(); // the panel, not the right-click menu (often for Quit)
            MaybeCheckForUpdates();
        }

        /// <summary>The panel or menu is open on a phone that is set up but not connected: a connect is likely.</summary>
        private void PrewarmIfLikely()
        {
            if (Demo || DemoIdle || string.IsNullOrEmpty(_cfg.PhoneSerial) || _busy || _mount.IsMounted || ScreenOpen) return;
            if (Tools.Adb != null) Phone.Prewarm();
        }

        /// <summary>"Phonestick.exe --panel": opens the panel at start (for a shortcut, or screenshots).</summary>
        internal void ShowPanelSoon(bool settings) => _ui.Post(_ => ShowPanel(settings), null);
        /// <summary>"--update-now": checks GitHub and installs a newer release straight away (command line, tests).</summary>
        internal void UpdateNowSoon() => _ui.Post(_ => CheckForUpdates(manual: true, install: true), null);

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
            var next = Mount.NextFreeDrive(_cfg.Drive);
            if (next == null) return;
            _cfg.Drive = next;
            _cfg.Save();
            RefreshMenu();
        }
        void IPanelHost.SetAutostart(bool on) { SetAutostart(on); RefreshMenu(); }
        void IPanelHost.Setup() => SetupForm.ShowSetup(this, _cfg);
        void IPanelHost.Pair() => PairForm.ShowPair();
        void IPanelHost.Help() { try { Process.Start(AppInfo.RepoUrl); } catch { } }
        void IPanelHost.Quit() => ExitApp();
        void IPanelHost.SetCheckUpdates(bool on)
        {
            _cfg.CheckUpdates = on;
            _cfg.Save();
            RefreshMenu();
            if (on) MaybeCheckForUpdates();
        }
        void IPanelHost.CheckUpdatesNow()
        {
            if (_update != null) { InstallUpdate(_update); return; }
            CheckForUpdates(manual: true);
        }

        // ------------------------------------------------------------------ updates

        private Updater.Info _update;
        private bool _updateChecking;
        private string _updateNote;

        private string UpdateText() =>
            _updateChecking ? "Checking..."
            : _update != null ? "v" + _update.VersionText
            : _updateNote ?? "v" + Updater.CurrentText;

        /// <summary>Checks GitHub when the last check is older than the interval. Called at start and
        /// when the panel opens, so a tray app left running for weeks still checks, with no timer.</summary>
        private void MaybeCheckForUpdates()
        {
            if (!_cfg.CheckUpdates || _updateChecking || Demo || DemoIdle) return;
            if (DateTime.UtcNow - _cfg.LastUpdateCheck < Updater.Interval) return;
            CheckForUpdates(manual: false);
        }

        private void CheckForUpdates(bool manual, bool install = false)
        {
            if (_updateChecking) return;
            _updateChecking = true;
            _updateNote = null;
            RefreshMenu();
            Task.Run(() =>
            {
                Updater.Info info = null;
                string error = null;
                try { info = Updater.Check(); }
                catch (Exception ex) { error = (ex is AggregateException ae ? ae.InnerException ?? ex : ex).Message; }
                _ui.Post(_ =>
                {
                    _updateChecking = false;
                    // A failed check (offline at login, say) is retried after a day, not a month.
                    _cfg.LastUpdateCheck = error == null ? DateTime.UtcNow : DateTime.UtcNow - Updater.Interval + TimeSpan.FromDays(1);
                    _cfg.Save();
                    if (error != null)
                    {
                        Log.Write("update check failed: " + error);
                        _updateNote = manual ? "Could not check" : null;
                    }
                    else if (info == null) _updateNote = manual ? "Up to date" : null;
                    else
                    {
                        Log.Write("update available: " + info.VersionText);
                        _update = info;
                    }
                    // Never a popup or a notification: a new version shows in the panel and the
                    // tray menu only, for whenever the user next opens them.
                    RefreshMenu();
                    if (install && info != null) InstallUpdate(info);
                }, null);
            });
        }

        private void InstallUpdate(Updater.Info info)
        {
            if (Flyout.Current != null && !Flyout.Current.IsDisposed) Flyout.Current.Close();
            if (!Updater.CanInstall(out var reason))
            {
                MessageBox.Show(reason + "\n\nThe release page will open so you can download it yourself.", AppInfo.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Updater.OpenReleasePage(info.Page);
                return;
            }
            if (_busy) { MessageBox.Show("Wait for the current connection to finish, then try again.", AppInfo.Name); return; }
            bool done = false;
            Run("Updating", log =>
            {
                // Download and check first: if that fails, the drive and screen stay as they are.
                Updater.Download(info, log);
                // Never swap the exe under a mounted drive or a screen session.
                DisconnectAll();
                Updater.Swap(info, log);
                done = true;
            }, after: () =>
            {
                if (!done) return;
                try { Updater.Relaunch(); }
                catch (Exception ex)
                {
                    // The new exe would not start (blocked by antivirus or SmartScreen, say): put the
                    // previous one back and keep running, rather than leave no Phonestick at all.
                    Log.Write("relaunch failed: " + ex.Message + "; rolling back");
                    Updater.Rollback();
                    MessageBox.Show("The new version could not be started (" + ex.Message + "). " + AppInfo.Name +
                        " " + Updater.CurrentText + " was kept.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _tray.Visible = false;
                ExitThread();
            });
        }

        internal static string IdleLabel(int min) =>
            min == 0 ? "Never" : min < 60 ? min + " minutes" : (min / 60) + (min == 60 ? " hour" : " hours");

        // ------------------------------------------------------------------ actions

        private void OpenPhone()
        {
            if (_mount.IsMounted) { Explore(_mount.Drive); return; }
            if (string.IsNullOrEmpty(_cfg.PhoneSerial)) { SetupForm.ShowSetup(this, _cfg); return; }
            Run("Connecting to your phone", log =>
            {
                var sw = Stopwatch.StartNew();
                Helpers.Ensure(log);
                StopIfCancelled();
                Phone p;
                long found;
                for (int attempt = 0; ; attempt++)
                {
                    p = FindPhone(log);
                    found = sw.ElapsedMilliseconds;
                    StopIfCancelled();
                    try
                    {
                        _mount.Start(p, _cfg, log, () => _cancelled);
                        break;
                    }
                    catch (PhoneLostError) when (attempt == 0 && !_cancelled)
                    {
                        // Android cuts Wireless debugging when the phone moves to another access
                        // point; the phone app turns it back on within a second or two.
                        Log.Write("connection dropped while getting the phone ready: trying again");
                        log("The connection dropped: connecting again...");
                    }
                }
                long mounted = sw.ElapsedMilliseconds;
                _ui.Post(_ =>
                {
                    if (!_mount.IsMounted) return; // cancelled or ended meanwhile
                    if (_cfg.IdleMinutes > 0) _idleTimer.Start();
                    Log.Write("mounted " + _mount.Drive + " in " + mounted + " ms (phone found in " + found + " ms), idle timer "
                        + (_idleTimer.Enabled ? "on" : "off"));
                    Explore(_mount.Drive);
                }, null);
                // After the drive is up, not before: an app update is never waited for.
                Task.Run(() => { if (_mount.IsMounted) p.EnsureAppCurrent(Log.Write); });
            });
        }

        /// <summary>Ends a connect quietly between its steps once the user clicked Cancel.</summary>
        private void StopIfCancelled()
        {
            if (_cancelled) throw new OperationCanceledException();
        }

        private void ShowScreen()
        {
            Run("Connecting to your phone", log =>
            {
                Helpers.Ensure(log);
                StopIfCancelled();
                var p = FindPhone(log);
                StopIfCancelled();
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
                ChildJob.Add(proc); // the screen window closes with Phonestick, whatever ends it
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();
                // scrcpy fails fast when something is wrong: report what it said. It is up once
                // its window shows (no need to wait the full 6 s then).
                for (int i = 0; i < 60 && !proc.HasExited; i++)
                {
                    proc.Refresh();
                    if (proc.MainWindowHandle != IntPtr.Zero) break;
                    Thread.Sleep(100);
                }
                if (proc.HasExited && proc.ExitCode != 0)
                {
                    string said;
                    lock (err) said = err.ToString().Trim();
                    proc.Dispose();
                    Log.Write("scrcpy failed: " + said);
                    throw new UserError("The phone screen could not be shown.\n\n" +
                        (said.Length > 900 ? said.Substring(said.Length - 900) : said));
                }
                proc.EnableRaisingEvents = true;
                proc.Exited += (s, e) => _ui.Post(_ =>
                {
                    Task.Run(() => Phone.StopServerIfUnused());
                    RefreshMenu();
                }, null);
                var previous = _scrcpy;
                _scrcpy = proc;
                previous?.Dispose();
                if (_cancelled) { try { proc.Kill(); } catch { } throw new OperationCanceledException(); }
                Task.Run(() => { if (ScreenOpen) p.EnsureAppCurrent(Log.Write); });
            });
        }

        /// <summary>Shown while the phone is looked for; the hint (second line) only if it takes a while.</summary>
        private const string LookingStep = "Looking for your phone on Wi-Fi...\n" +
            "Taking a while? Check that the phone is on this Wi-Fi, its " + AppInfo.Name + " tile is on, and no prompt is waiting on its screen.";

        private Phone FindPhone(Action<string> log)
        {
            log(LookingStep);
            var p = Phone.FindOnWifi(_cfg.PhoneSerial, 20000, _cfg.LastEndpoint, () => _cancelled);
            if (p == null && Phone.LastFindSawService && !_cancelled && !ScreenOpen && !_mount.IsMounted)
            {
                // The phone offers wireless debugging but adb did not get through: start adb afresh once.
                Log.Write("phone seen but not connected: restarting adb");
                Phone.StopServer();
                StopIfCancelled();
                log("Still looking: starting the connection afresh...\n" + LookingStep.Split('\n')[1]);
                p = Phone.FindOnWifi(_cfg.PhoneSerial, 15000, Phone.LastFoundEndpoint ?? _cfg.LastEndpoint, () => _cancelled);
            }
            StopIfCancelled();
            var seen = Phone.LastFoundEndpoint ?? (p != null && System.Text.RegularExpressions.Regex.IsMatch(p.Serial, @"^\d+\.\d+\.\d+\.\d+:\d+$") ? p.Serial : null);
            if (seen != null && seen != _cfg.LastEndpoint)
            {
                _cfg.LastEndpoint = seen; // its address is asked first next time
                _cfg.Save();
            }
            // Not after a cancel or disconnect: the adb command would start the server again.
            if (p != null) { var phone = p; Task.Run(() => { Thread.Sleep(1500); if (!_cancelled && Phone.InUse()) phone.HideDebugNotice(); }); }
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
                if (Phone.LastFindSawService)
                    throw new UserError("Your phone has Wireless debugging on, but did not let this computer in.\n\n" +
                        "If this computer was removed from the phone's paired devices, set it up again: click the tray icon, the gear, then Set up a phone.");
                throw new UserError(answers
                    ? "Your phone is on this network, but it is not accepting connections.\n\n" +
                      "Pull down Quick Settings and check the " + AppInfo.Name + " tile is on. If it already is, tap it off and on. " +
                      "If Android then asks \"Allow wireless debugging on this network?\", tick Always allow and tap Allow."
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
                // Only a connect can be cancelled: it stops at its next step, and undoes a
                // mount that comes up meanwhile.
                if (!Connecting) return;
                _cancelled = true;
                RefreshMenu();
                Task.Run(() => DisconnectAll());
                return;
            }
            Run("Disconnecting", log => DisconnectAll());
        }

        private void DisconnectAll()
        {
            var phone = _mount.Phone;
            _mount.Stop();
            var s = _scrcpy;
            try { if (s != null && !s.HasExited) s.Kill(); } catch { }
            try { phone?.Disconnect(); } catch { }
            // Not while a setup or pairing uses the adb server.
            if (!SetupForm.Busy) Phone.StopServer();
            _ui.Post(_ => _idleTimer.Stop(), null);
        }

        private void OnSuspend()
        {
            // Runs on the UI thread, and Windows gives only a moment before sleeping: the drive and
            // the screen close at once (a copy still running fails in Explorer, which keeps the
            // original); the slower adb clean-up goes on in the background, or after the wake-up.
            if (!_mount.IsMounted && !ScreenOpen) return;
            Log.Write("going to sleep: disconnecting");
            if (_busy && Connecting) _cancelled = true;
            var phone = _mount.Phone;
            try
            {
                _mount.Stop();
                var s = _scrcpy;
                if (s != null && !s.HasExited) s.Kill();
            }
            catch (Exception ex) { Log.Write("suspend: " + ex.Message); }
            _idleTimer.Stop();
            Task.Run(() =>
            {
                try { phone?.Disconnect(); } catch { }
                Phone.StopServerIfUnused();
            });
            RefreshMenu();
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
                if (t.Status == TaskStatus.RanToCompletion && t.Result && _mount.IsMounted
                    && Run("Disconnecting", log => DisconnectAll(), quiet: true))
                    Balloon("Phone disconnected", "Unused for " + IdleLabel(_cfg.IdleMinutes) + ". Open it again from the tray.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnMountEnded()
        {
            Log.Write("mount ended");
            _idleTimer.Stop();
            Task.Run(() => Phone.StopServerIfUnused());
            RefreshMenu();
        }

        /// <summary>Runs an action off the UI thread, showing errors as messages. False when busy (nothing started).</summary>
        internal bool Run(string what, Action<Action<string>> action, Action after = null, bool quiet = false)
        {
            if (_busy) return false;
            _cancelled = false;
            _busyText = what.StartsWith("Disconnect") ? "Disconnecting..." : what.StartsWith("Updat") ? "Updating..." : "Connecting...";
            _busy = true;
            RefreshMenu();
            _tray.Text = Trim63(AppInfo.Name + ": " + what + "...");
            if (!quiet) ShowProgressSoon(what);
            Task.Run(() =>
            {
                string error = null;
                try
                {
                    action(msg => _ui.Post(_ =>
                    {
                        _tray.Text = Trim63(AppInfo.Name + ": " + msg.Split('\n')[0]);
                        _step = msg;
                        _card?.SetStep(msg);
                    }, null));
                }
                catch (OperationCanceledException) { }
                catch (AggregateException ae) { error = ae.InnerException?.Message ?? ae.Message; }
                catch (UserError ue) { error = ue.Message; }
                catch (Exception ex) { error = ex.Message; }
                if (error != null) Log.Write(what + ": " + error + (_cancelled ? " (cancelled)" : ""));
                bool cancelled = _cancelled;
                if (cancelled) error = null;
                _ui.Post(_ =>
                {
                    _busy = false;
                    EndProgress();
                    // A cancelled connect may have restarted the adb server after Cancel stopped it.
                    if (cancelled) Task.Run(() => Phone.StopServerIfUnused());
                    try
                    {
                        RefreshMenu();
                        if (error != null) MessageBox.Show(error, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    finally { after?.Invoke(); } // always: for an update, this relaunches or rolls back
                }, null);
            });
            return true;
        }

        private ProgressCard _card;
        private System.Windows.Forms.Timer _cardDelay;
        /// <summary>The latest step of the running work, for a card that opens after it was set.</summary>
        private string _step;

        /// <summary>
        /// Shows the progress card if the work is still running a moment later (quick work, like
        /// most disconnects, never flashes one).
        /// </summary>
        private void ShowProgressSoon(string what)
        {
            EndProgress();
            var name = string.IsNullOrEmpty(_cfg.PhoneModel) ? "your phone" : _cfg.PhoneModel;
            string title = what.StartsWith("Connecting") ? "Connecting to " + name
                : what.StartsWith("Updat") ? "Updating " + AppInfo.Name
                : what;
            _step = what.StartsWith("Connecting") ? "Starting..." : what.StartsWith("Disconnect") ? "Closing the connection..." : "Starting...";
            bool cancellable = Connecting;
            _cardDelay = new System.Windows.Forms.Timer { Interval = 600 };
            _cardDelay.Tick += (o, e) =>
            {
                EndDelay();
                if (!_busy || _card != null) return;
                _card = new ProgressCard(title, _step, cancellable ? (Action)Disconnect : null);
                _card.ShowCard();
            };
            _cardDelay.Start();
        }

        private void EndDelay()
        {
            if (_cardDelay == null) return;
            _cardDelay.Stop();
            _cardDelay.Dispose();
            _cardDelay = null;
        }

        private void EndProgress()
        {
            EndDelay();
            _card?.Finish();
            _card = null;
        }

        internal void ConfigChanged() => _ui.Post(_ => RefreshMenu(), null);
        /// <summary>After setup: connect and show the files straight away.</summary>
        internal void OpenPhoneSoon() => _ui.Post(_ => OpenPhone(), null);

        // ------------------------------------------------------------------ helpers

        private static string Trim63(string s) => s.Length > 63 ? s.Substring(0, 63) : s;

        private static void Explore(string drive)
        {
            try { Process.Start("explorer.exe", drive + "\\"); } catch { }
        }

        private Action _balloonClick;

        private void Balloon(string title, string text, Action onClick = null)
        {
            _balloonClick = onClick;
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
            if (Flyout.Current != null && !Flyout.Current.IsDisposed) Flyout.Current.Close();
            // The icon stays until the drive is closed, so the app is never running unseen.
            if (Run("Disconnecting", log => DisconnectAll(), after: () => { _tray.Visible = false; ExitThread(); }))
                return;
            // Busy: an update cannot be interrupted, a connect is cancelled.
            if (_busyText == "Updating...") return;
            _tray.Visible = false;
            _cancelled = true;
            Task.Run(() => DisconnectAll()).Wait(20000);
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
