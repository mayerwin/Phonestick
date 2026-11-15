using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Phonestick
{
    /// <summary>What the panel shows; rebuilt by the tray app whenever something changes.</summary>
    internal sealed class FlyoutState
    {
        public string Phone = "";
        public bool SetUp;
        public bool Busy;
        public string BusyText = "Connecting...";
        public bool Mounted;
        public bool Screen;
        public string Drive = "P:";
        public int IdleMinutes = 60;
        public bool Autostart;
        public bool CheckUpdates = true;
        /// <summary>Right-hand text of the "Check for updates" row: version, "Checking...", "Up to date"...</summary>
        public string UpdateText = "";
        /// <summary>A newer version ready to install ("0.3.2"), or null.</summary>
        public string UpdateAvailable;
        public bool Connected => Mounted || Screen;
    }

    /// <summary>What the panel can ask the tray app to do.</summary>
    internal interface IPanelHost
    {
        void Open();
        void Screen();
        void Disconnect();
        void SetIdle(int minutes);
        void NextDrive();
        void SetAutostart(bool on);
        void SetCheckUpdates(bool on);
        void CheckUpdatesNow();
        void Setup();
        void Pair();
        void Help();
        void Quit();
    }

    /// <summary>
    /// The panel shown when the tray icon is clicked. A white card on the porcelain canvas:
    /// the phone, its state in a tinted band, one primary action and two quiet ones; the gear
    /// turns the same panel into settings. Painted in logical 96-dpi units, scaled per monitor.
    /// Created on click and disposed when it loses focus, so it costs nothing while idle.
    /// </summary>
    internal sealed class Flyout : Form
    {
        public static Flyout Current;
        /// <summary>Only a connect can be cancelled (not an update or a disconnect).</summary>
        private bool Cancellable => _s.Busy && _s.BusyText == "Connecting...";
        /// <summary>When the panel last closed: a click on the tray icon that closed it must not reopen it.</summary>
        public static int LastClosedTick = Environment.TickCount - 100000;
        /// <summary>Keeps the panel open when it loses focus (Phonestick.exe --stay, for screenshots).</summary>
        public static bool StayOpen;

        private const float W = 360, Pad = 16;
        /// <summary>Made once: a new StringFormat per paint was never freed.</summary>
        private static readonly StringFormat BandFormat = new StringFormat(StringFormat.GenericTypographic)
        { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        private const string ChevronLeft = "M15 18l-6-6 6-6";
        private const string ChevronRight = "M9 18l6-6-6-6";
        private static readonly int[] IdleChoices = { 15, 30, 60, 120, 0 };

        private readonly IPanelHost _host;
        private readonly List<Btn> _buttons = new List<Btn>();
        private readonly Bitmap _logo;
        private readonly float S;
        private FlyoutState _s;
        private bool _settings;
        private float _cardBottom, _card2Top, _card2Bottom, _height;
        private Btn _hover, _down;

        private sealed class Btn
        {
            public RectangleF R;
            public string Text, Icon;
            public bool Primary, Enabled = true, IconOnly, Pill, Selected, Row, Switch, Link;
            public string Detail;
            public Action Click;
        }

        public Flyout(FlyoutState s, IPanelHost host, bool settings = false)
        {
            _s = s;
            _host = host;
            _settings = settings;
            S = DpiScaleAt(Cursor.Position);
            _logo = Icons.Logo((int)Math.Round(28 * S));
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Back;
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += (o, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                if (_settings) { _settings = false; Relayout(); } else Close();
            };
            Relayout(first: true);

            // Close when focus moves elsewhere, but only after the panel really had focus
            // (Windows may refuse focus to a panel opened from a shortcut).
            bool hadFocus = false;
            Activated += (o, e) => hadFocus = true;
            Deactivate += (o, e) => { if (hadFocus && !StayOpen) Close(); };
            FormClosed += (o, e) => { if (Current == this) Current = null; LastClosedTick = Environment.TickCount; _logo.Dispose(); };
        }

        /// <summary>Called by the tray app when the state changes while the panel is open.</summary>
        public void UpdateState(FlyoutState s)
        {
            _s = s;
            Relayout();
        }

        private int Sc(float v) => (int)Math.Round(v * S);

        // ------------------------------------------------------------ layout

        private void Relayout(bool first = false)
        {
            _buttons.Clear();
            _hover = _down = null;
            if (_settings) LayoutSettings(); else LayoutMain();
            var oldBottom = Bottom;
            ClientSize = new Size(Sc(W), Sc(_height));
            if (!first) Top = oldBottom - Height; // grow upwards, staying above the taskbar
            Invalidate();
        }

        private void LayoutMain()
        {
            float inner = Pad + 16, innerW = W - Pad * 2 - 32;
            _buttons.Add(new Btn { R = new RectangleF(W - Pad - 32, 14, 32, 32), Icon = Glyphs.Settings, IconOnly = true, Click = () => { _settings = true; Relayout(); } });
            float y = 60 + 16 + 44 + 14 + 40 + 14;
            _buttons.Add(new Btn
            {
                R = new RectangleF(inner, y, innerW, 40),
                Text = !_s.SetUp ? "Set up your phone" : _s.Mounted ? "Show files on " + _s.Drive : "Open phone files",
                Icon = !_s.SetUp ? Glyphs.Smartphone : Glyphs.FolderOpen,
                Primary = true,
                Enabled = _s.Mounted || !_s.Busy,
                Click = () => { Close(); _host.Open(); },
            });
            y += 40 + 8;
            float half = (innerW - 8) / 2;
            _buttons.Add(new Btn
            {
                R = new RectangleF(inner, y, half, 36), Text = "Show screen", Icon = Glyphs.Monitor,
                Enabled = _s.SetUp && !_s.Busy && !_s.Screen, Click = () => { Close(); _host.Screen(); },
            });
            _buttons.Add(new Btn
            {
                // Always possible while something is connected or connecting: it also cancels.
                R = new RectangleF(inner + half + 8, y, half, 36), Text = Cancellable && !_s.Connected ? "Cancel" : "Disconnect", Icon = Glyphs.Unplug,
                Enabled = Cancellable || (!_s.Busy && _s.Connected), Click = () => _host.Disconnect(),
            });
            _cardBottom = y + 36 + 16;
            _height = _cardBottom + 12 + 18 + 12;
            if (_s.UpdateAvailable != null)
                _buttons.Add(new Btn
                {
                    R = new RectangleF(Pad - 4, _cardBottom + 6, W - Pad * 2 + 8, 30), Link = true, Icon = Glyphs.Download,
                    Text = AppInfo.Name + " " + _s.UpdateAvailable + " is available. Install",
                    Click = () => { Close(); _host.CheckUpdatesNow(); },
                });
        }

        private void LayoutSettings()
        {
            float x = Pad + 16, w = W - Pad * 2 - 32;
            _buttons.Add(new Btn { R = new RectangleF(Pad - 4, 14, 32, 32), Icon = ChevronLeft, IconOnly = true, Click = () => { _settings = false; Relayout(); } });

            // Card 1: preferences
            float y = 60 + 16 + 20 + 8;
            float pw = (w - 4 * 6) / 5;
            for (int i = 0; i < IdleChoices.Length; i++)
            {
                int m = IdleChoices[i];
                _buttons.Add(new Btn
                {
                    R = new RectangleF(x + i * (pw + 6), y, pw, 32),
                    Text = m == 0 ? "Never" : m < 60 ? m + " min" : (m / 60) + " h",
                    Pill = true, Selected = _s.IdleMinutes == m,
                    Click = () => _host.SetIdle(m),
                });
            }
            y += 32 + 18;
            _buttons.Add(new Btn
            {
                R = new RectangleF(x + w - 72, y - 4, 72, 30), Text = _s.Drive, Pill = true, Icon = ChevronRight,
                Enabled = !_s.Mounted, Click = () => _host.NextDrive(),
            });
            y += 22 + 18;
            _buttons.Add(new Btn
            {
                R = new RectangleF(x + w - 44, y - 3, 44, 24), Switch = true, Selected = _s.Autostart,
                Click = () => _host.SetAutostart(!_s.Autostart),
            });
            y += 22 + 18;
            _buttons.Add(new Btn
            {
                R = new RectangleF(x + w - 44, y - 3, 44, 24), Switch = true, Selected = _s.CheckUpdates,
                Click = () => _host.SetCheckUpdates(!_s.CheckUpdates),
            });
            y += 22 + 16;
            _cardBottom = y;

            // Card 2: one-off actions, as rows
            _card2Top = _cardBottom + 12;
            float ry = _card2Top + 6;
            void Row(string text, string icon, Action a, string detail = null)
            {
                _buttons.Add(new Btn { R = new RectangleF(Pad + 6, ry, W - Pad * 2 - 12, 40), Text = text, Icon = icon, Row = true, Click = a, Detail = detail });
                ry += 40;
            }
            Row("Set up a phone", Glyphs.Smartphone, () => { Close(); _host.Setup(); });
            Row("Pair with a Wi-Fi code", Glyphs.Wifi, () => { Close(); _host.Pair(); });
            Row(_s.UpdateAvailable != null ? "Install update" : "Check for updates", Glyphs.Download, () => _host.CheckUpdatesNow(), _s.UpdateText);
            Row("Help and source code", Glyphs.FileText, () => { Close(); _host.Help(); });
            Row("Quit " + AppInfo.Name, Glyphs.Power, () => { Close(); _host.Quit(); });
            _card2Bottom = ry + 6;
            _height = _card2Bottom + 16;
        }

        // ------------------------------------------------------------ showing

        /// <summary>Opens above the taskbar, near the tray, like the Windows 11 flyouts.</summary>
        public void ShowNearTray()
        {
            Current = this;
            var wa = System.Windows.Forms.Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - Sc(12), wa.Bottom - Height - Sc(12));
            Opacity = 0;
            Show();
            Theme.ApplyChrome(this);
            Activate();
            var start = Environment.TickCount;
            var t = new Timer { Interval = 10 };
            // The panel can close (focus lost, a click) before the fade ends: stop with it.
            FormClosed += (o, e) => { t.Stop(); t.Dispose(); };
            t.Tick += (o, e) =>
            {
                if (IsDisposed || Disposing) { t.Stop(); t.Dispose(); return; }
                var k = Math.Min(1.0, (Environment.TickCount - start) / 140.0);
                Opacity = 1 - Math.Pow(1 - k, 3);
                if (k >= 1) { t.Stop(); t.Dispose(); }
            };
            t.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: not in Alt+Tab
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        // ------------------------------------------------------------ input

        private Btn Hit(Point p)
        {
            var lp = new PointF(p.X / S, p.Y / S);
            foreach (var b in _buttons) if (b.Enabled && b.R.Contains(lp)) return b;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = Hit(e.Location);
            if (h != _hover) { _hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = null; _down = null; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = Hit(e.Location); Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var b = Hit(e.Location);
            var was = _down;
            _down = null;
            Invalidate();
            if (b != null && b == was) b.Click();
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(S, S);
            if (_settings) PaintSettings(g); else PaintMain(g);
            foreach (var btn in _buttons) PaintButton(g, btn);
        }

        private void PaintMain(Graphics g)
        {
            g.DrawImage(_logo, Pad, 16, 28, 28);
            DrawText(g, AppInfo.Name, Theme.Brand(18), Theme.Text, Pad + 36, 18);

            var card = new RectangleF(Pad, 60, W - Pad * 2, _cardBottom - 60);
            Card(g, card);
            float x = card.X + 16, y = card.Y + 16, w = card.Width - 32;

            // Device row
            using (var path = Theme.Rounded(new RectangleF(x, y, 44, 44), 10))
            using (var b = new SolidBrush(Theme.AccentTint))
                g.FillPath(b, path);
            Glyphs.Draw(g, Glyphs.Smartphone, Theme.AccentText, x + 11, y + 11, 22, 1.8f);
            DrawText(g, _s.Phone, Theme.Px(16, FontStyle.Bold, display: true), Theme.Text, x + 56, y + 3);
            string sub = !_s.SetUp ? "Not set up on this computer yet"
                : _s.Connected ? "Connected over Wi-Fi"
                : _s.Busy ? _s.BusyText
                : "Ready, connects when you need it";
            DrawText(g, sub, Theme.Px(12.5f), Theme.Muted, x + 56, y + 25);

            // Band
            y += 44 + 14;
            var band = new RectangleF(x, y, w, 40);
            bool ok = _s.Connected, busy = _s.Busy && !ok;
            using (var path = Theme.Rounded(band, 10))
            using (var b = new SolidBrush(ok ? Theme.OkBg : busy ? Theme.AccentTint : Theme.Tonal))
                g.FillPath(b, path);
            var tone = ok ? Theme.OkFg : busy ? Theme.AccentText : Theme.Text2;
            Glyphs.Draw(g, ok ? Glyphs.Check : busy ? Glyphs.Wifi : (_s.SetUp ? Glyphs.Wifi : Glyphs.Lock), tone, band.X + 12, band.Y + 11, 18, 2f);
            string head = ok ? (_s.Mounted && _s.Screen ? "Files on " + _s.Drive + ", screen open" : _s.Mounted ? "Files open on " + _s.Drive : "Screen open")
                : busy ? _s.BusyText
                : _s.SetUp ? "Not connected" : "Pair once, then it just works";
            string detail = ok ? (_s.IdleMinutes <= 0 ? "stays open" : "closes after " + Short(_s.IdleMinutes) + " idle")
                : busy ? "" : _s.SetUp ? "opens in seconds" : "";
            var sf = BandFormat;
            using (var fh = Theme.Px(13, FontStyle.Bold))
            using (var fd = Theme.Px(12.5f))
            using (var bh = new SolidBrush(tone))
            using (var bd = new SolidBrush(ok ? Color.FromArgb(210, Theme.OkFg) : Theme.Muted))
            {
                var hs = g.MeasureString(head, fh, PointF.Empty, sf);
                float tx = band.X + 40, ty = band.Y + 12;
                g.DrawString(head, fh, bh, new RectangleF(tx, ty, band.Right - 12 - tx, 20), sf);
                float dx = tx + hs.Width + 8;
                if (detail.Length > 0 && dx < band.Right - 40)
                    g.DrawString(detail, fd, bd, new RectangleF(dx, ty + 0.5f, band.Right - 12 - dx, 20), sf);
            }

            if (_s.UpdateAvailable == null)
                DrawText(g, _s.SetUp ? "The tile on your phone switches access on or off" : "Right-click the tray icon for more",
                    Theme.Px(12), Theme.Muted, Pad + 2, _cardBottom + 13);
        }

        private void PaintSettings(Graphics g)
        {
            DrawText(g, "Settings", Theme.Brand(18), Theme.Text, Pad + 34, 18);

            var card = new RectangleF(Pad, 60, W - Pad * 2, _cardBottom - 60);
            Card(g, card);
            float x = card.X + 16, y = card.Y + 16;
            DrawText(g, "Disconnect when unused for", Theme.Px(13.5f, FontStyle.Bold), Theme.Text, x, y);
            y += 20 + 8 + 32 + 18;
            DrawText(g, "Drive letter", Theme.Px(13.5f, FontStyle.Bold), Theme.Text, x, y);
            y += 22 + 18;
            DrawText(g, "Start with Windows", Theme.Px(13.5f, FontStyle.Bold), Theme.Text, x, y);
            y += 22 + 18;
            DrawText(g, "Check for updates", Theme.Px(13.5f, FontStyle.Bold), Theme.Text, x, y);
            using (var fb = Theme.Px(13.5f, FontStyle.Bold))
            {
                var lw = g.MeasureString("Check for updates", fb, PointF.Empty, StringFormat.GenericTypographic).Width;
                DrawText(g, "monthly", Theme.Px(12.5f), Theme.Muted, x + lw + 8, y + 1);
            }

            var card2 = new RectangleF(Pad, _card2Top, W - Pad * 2, _card2Bottom - _card2Top);
            Card(g, card2);
        }

        private static string Short(int min) => min < 60 ? min + " min" : (min / 60) + (min == 60 ? " hour" : " hours");

        private static void DrawText(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (f)
            using (var b = new SolidBrush(c))
                g.DrawString(s, f, b, x, y, StringFormat.GenericTypographic);
        }

        private void PaintButton(Graphics g, Btn b)
        {
            bool hover = b == _hover, down = b == _down;
            var sf = StringFormat.GenericTypographic;

            if (b.Switch)
            {
                var track = Theme.Rounded(b.R, b.R.Height / 2);
                using (var br = new SolidBrush(b.Selected ? (hover ? Theme.AccentHover : Theme.Accent) : (hover ? Theme.TonalHover : Theme.Border.A < 40 && !Theme.Dark ? Color.FromArgb(0xD5, 0xD8, 0xDE) : Theme.TonalHover)))
                    g.FillPath(br, track);
                float d = b.R.Height - 6;
                float kx = b.Selected ? b.R.Right - 3 - d : b.R.X + 3;
                using (var br = new SolidBrush(Color.White)) g.FillEllipse(br, kx, b.R.Y + 3, d, d);
                track.Dispose();
                return;
            }
            if (b.Row)
            {
                if (hover || down)
                    using (var path = Theme.Rounded(b.R, 8))
                    using (var br = new SolidBrush(down ? Theme.TonalHover : Theme.Tonal))
                        g.FillPath(br, path);
                Glyphs.Draw(g, b.Icon, Theme.Text2, b.R.X + 12, b.R.Y + 11, 18, 1.8f);
                using (var f = Theme.Px(13.5f))
                using (var br = new SolidBrush(Theme.Text))
                    g.DrawString(b.Text, f, br, b.R.X + 42, b.R.Y + 11, sf);
                if (!string.IsNullOrEmpty(b.Detail))
                    using (var f = Theme.Px(12.5f))
                    using (var br = new SolidBrush(Theme.Muted))
                    {
                        var ds = g.MeasureString(b.Detail, f, PointF.Empty, sf);
                        g.DrawString(b.Detail, f, br, b.R.Right - 34 - ds.Width, b.R.Y + 12, sf);
                    }
                Glyphs.Draw(g, ChevronRight, Theme.Muted, b.R.Right - 26, b.R.Y + 12, 16, 1.8f);
                return;
            }
            if (b.Link)
            {
                using (var f = Theme.Px(12.5f, FontStyle.Bold))
                using (var br = new SolidBrush(hover ? Theme.AccentHover : Theme.AccentText))
                {
                    if (hover || down)
                        using (var path = Theme.Rounded(b.R, 8))
                        using (var bg = new SolidBrush(Theme.AccentTint))
                            g.FillPath(bg, path);
                    Glyphs.Draw(g, b.Icon, hover ? Theme.AccentHover : Theme.AccentText, b.R.X + 6, b.R.Y + 7, 16, 2f);
                    g.DrawString(b.Text, f, br, b.R.X + 28, b.R.Y + 8, sf);
                }
                return;
            }
            if (b.Pill)
            {
                Color back = !b.Enabled ? Color.FromArgb(120, Theme.Tonal) : b.Selected ? Theme.Accent : (down || hover) ? Theme.TonalHover : Theme.Tonal;
                Color fore = !b.Enabled ? Theme.Muted : b.Selected ? Color.White : Theme.Text;
                using (var path = Theme.Rounded(b.R, 8))
                using (var br = new SolidBrush(back))
                    g.FillPath(br, path);
                using (var f = Theme.Px(13, b.Selected ? FontStyle.Bold : FontStyle.Regular))
                using (var br = new SolidBrush(fore))
                {
                    var ts = g.MeasureString(b.Text, f, PointF.Empty, sf);
                    float iconW = b.Icon != null ? 18 : 0;
                    float sx = b.R.X + (b.R.Width - ts.Width - iconW) / 2;
                    g.DrawString(b.Text, f, br, sx, b.R.Y + (b.R.Height - ts.Height) / 2, sf);
                    if (b.Icon != null) Glyphs.Draw(g, b.Icon, fore, sx + ts.Width + 4, b.R.Y + (b.R.Height - 14) / 2, 14, 2f);
                }
                return;
            }

            Color backC, foreC;
            if (b.IconOnly)
            {
                backC = down ? Theme.TonalHover : hover ? Theme.Tonal : Color.Transparent;
                foreC = Theme.Text2;
            }
            else if (b.Primary && b.Enabled)
            {
                backC = down || hover ? Theme.AccentHover : Theme.Accent;
                foreC = Color.White;
            }
            else
            {
                backC = b.Enabled ? (down || hover ? Theme.TonalHover : Theme.Tonal) : Color.FromArgb(Theme.Dark ? 110 : 150, Theme.Tonal);
                foreC = b.Enabled ? Theme.Text : Color.FromArgb(120, Theme.Muted);
            }
            if (backC.A > 0)
                using (var path = Theme.Rounded(b.R, 8))
                using (var br = new SolidBrush(backC))
                    g.FillPath(br, path);

            if (b.IconOnly)
            {
                Glyphs.Draw(g, b.Icon, foreC, b.R.X + 7, b.R.Y + 7, 18, 1.8f);
                return;
            }
            using (var f = Theme.Px(b.Primary ? 14 : 13, b.Primary ? FontStyle.Bold : FontStyle.Regular))
            {
                var ts = g.MeasureString(b.Text, f, PointF.Empty, sf);
                float icon = b.Primary ? 18 : 16, gap = 8;
                float sx = b.R.X + (b.R.Width - (icon + gap + ts.Width)) / 2;
                Glyphs.Draw(g, b.Icon, foreC, sx, b.R.Y + (b.R.Height - icon) / 2, icon, 1.9f);
                using (var br = new SolidBrush(foreC))
                    g.DrawString(b.Text, f, br, sx + icon + gap, b.R.Y + (b.R.Height - ts.Height) / 2, sf);
            }
        }

        /// <summary>A white card on a soft layered shadow (light theme), a faint edge in dark.</summary>
        private static void Card(Graphics g, RectangleF r)
        {
            const float radius = 14;
            if (!Theme.Dark)
            {
                for (int i = 6; i >= 1; i--)
                {
                    var sr = new RectangleF(r.X - i * 0.5f, r.Y - i * 0.25f + 2, r.Width + i, r.Height + i);
                    using (var path = Theme.Rounded(sr, radius + i * 0.5f))
                    using (var b = new SolidBrush(Color.FromArgb(5, 16, 24, 40)))
                        g.FillPath(b, path);
                }
            }
            using (var path = Theme.Rounded(r, radius))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
        }

        // ------------------------------------------------------------ DPI

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

        internal static float DpiScaleAt(Point pt)
        {
            try
            {
                if (GetDpiForMonitor(MonitorFromPoint(pt, 2), 0, out var x, out _) == 0) return x / 96f;
            }
            catch { }
            return 1f;
        }
    }
}
