using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Phonestick
{
    /// <summary>
    /// A small card above the tray while the user waits for something (connecting, finishing
    /// copies, updating): what is going on, a moving bar and the seconds spent, so a wait never
    /// looks like a freeze. It never takes focus, steps aside while the panel is open, and is
    /// closed by the tray app when the work ends. Painted in 96-dpi units, scaled per monitor.
    /// </summary>
    internal sealed class ProgressCard : Form
    {
        private const float W = 340, Pad = 16;
        /// <summary>How long a step runs before its hint (what to check) shows, by default.</summary>
        private const int HintAfterDefault = 8000;
        private static readonly StringFormat OneLine = new StringFormat(StringFormat.GenericTypographic)
        { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        private readonly float S;
        private readonly Bitmap _logo;
        private readonly Timer _tick = new Timer { Interval = 33 };
        private readonly int _start = Environment.TickCount;
        private readonly string _title;
        private readonly Action _cancel;
        private readonly int _hintAfterMs;
        private int _stepStart = Environment.TickCount;
        private string _step = "", _hint;
        private bool _hintShown, _hoverCancel, _closing, _cancelClicked;
        private RectangleF _cancelR;

        /// <param name="cancel">Shown as a Cancel link when not null.</param>
        /// <param name="hintAfterMs">How long a step runs before its hint shows (0: at once).</param>
        public ProgressCard(string title, string step, Action cancel, int hintAfterMs = HintAfterDefault)
        {
            _title = title;
            _cancel = cancel;
            _hintAfterMs = hintAfterMs;
            var wa = Screen.PrimaryScreen.WorkingArea; // the taskbar with the tray icon
            S = Flyout.DpiScaleAt(new Point(wa.Right - 1, wa.Bottom - 1));
            _logo = Icons.Logo((int)Math.Round(20 * S));
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            SetStep(step);
            Place();
            _tick.Tick += (o, e) => OnTick();
            FormClosed += (o, e) => { _tick.Stop(); _tick.Dispose(); _logo.Dispose(); };
        }

        /// <summary>The current step. A second line, after a newline, is a hint shown only if the step lasts.</summary>
        public void SetStep(string text)
        {
            var parts = (text ?? "").Split(new[] { '\n' }, 2);
            var hint = parts.Length > 1 ? parts[1] : null;
            if (parts[0] == _step && hint == _hint) return;
            _step = parts[0];
            // Same hint (progress text changing, say): the step goes on, its hint stays.
            if (hint != _hint)
            {
                _hint = hint;
                _stepStart = Environment.TickCount;
                if (_hintShown) { _hintShown = false; Place(); }
            }
            Invalidate();
        }

        public void ShowCard()
        {
            Show(); // without focus, see ShowWithoutActivation
            Theme.ApplyChrome(this);
            _tick.Start();
        }

        /// <summary>Closes for good (the work ended).</summary>
        public void Finish()
        {
            _closing = true;
            if (!IsDisposed) Close();
        }

        private int Sc(float v) => (int)Math.Round(v * S);

        private float _hintH;
        private float LogicalHeight => _hintShown ? 84 + _hintH + 12 : 84;

        /// <summary>The hint's height in logical units, wrapped to the card's width.</summary>
        private float MeasureHint()
        {
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            using (var f = Theme.Px(12))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                return (float)Math.Ceiling(g.MeasureString(_hint ?? "", f, (int)(W - Pad * 2), StringFormat.GenericTypographic).Height);
            }
        }

        private void Place()
        {
            ClientSize = new Size(Sc(W), Sc(LogicalHeight));
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Sc(12), wa.Bottom - Height - Sc(12));
        }

        private void OnTick()
        {
            if (_closing || IsDisposed) return;
            // The panel shows the same state: step aside while it is open.
            var fly = Flyout.Current;
            bool panel = fly != null && !fly.IsDisposed && fly.Visible;
            if (panel && Visible) Hide();
            else if (!panel && !Visible) Show();
            if (!_hintShown && _hint != null && unchecked(Environment.TickCount - _stepStart) >= _hintAfterMs)
            {
                _hintShown = true;
                _hintH = MeasureHint();
                Place();
            }
            if (Visible) Invalidate();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: not in Alt+Tab
                cp.ExStyle |= 0x8;         // WS_EX_TOPMOST
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE: clicks never take focus from the user's window
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(S, S);
            float h = LogicalHeight;

            using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, W - 1 / S, h - 1 / S);

            g.DrawImage(_logo, Pad, 16, 20, 20);
            float right = W - Pad;
            _cancelR = RectangleF.Empty;
            if (_cancel != null && !_cancelClicked)
                using (var f = Theme.Px(12.5f, FontStyle.Bold))
                {
                    var sz = g.MeasureString("Cancel", f, PointF.Empty, StringFormat.GenericTypographic);
                    _cancelR = new RectangleF(right - sz.Width - 8, 14, sz.Width + 16, 24);
                    if (_hoverCancel)
                        using (var path = Theme.Rounded(_cancelR, 6))
                        using (var b = new SolidBrush(Theme.AccentTint))
                            g.FillPath(b, path);
                    using (var b = new SolidBrush(_hoverCancel ? Theme.AccentHover : Theme.AccentText))
                        g.DrawString("Cancel", f, b, _cancelR.X + 8, _cancelR.Y + 4, StringFormat.GenericTypographic);
                    right = _cancelR.X - 8;
                }
            DrawLine(g, _title, Theme.Px(14, FontStyle.Bold), Theme.Text, new RectangleF(Pad + 30, 18, right - Pad - 30, 22));

            // Step, with the seconds spent once it is more than a moment.
            int secs = unchecked(Environment.TickCount - _start) / 1000;
            float stepRight = W - Pad;
            if (secs >= 3)
                using (var f = Theme.Px(12))
                {
                    var t = secs + " s";
                    var sz = g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic);
                    using (var b = new SolidBrush(Theme.Muted))
                        g.DrawString(t, f, b, W - Pad - sz.Width, 47, StringFormat.GenericTypographic);
                    stepRight = W - Pad - sz.Width - 10;
                }
            DrawLine(g, _step, Theme.Px(12.5f), Theme.Text2, new RectangleF(Pad, 46, stepRight - Pad, 20));
            if (_hintShown)
                DrawLine(g, _hint, Theme.Px(12), Theme.Muted, new RectangleF(Pad, 68, W - Pad * 2, _hintH + 4), wrap: true);

            // Moving bar: a segment sliding along a track, eased.
            var track = new RectangleF(Pad, h - 18, W - Pad * 2, 4);
            using (var path = Theme.Rounded(track, 2))
            using (var b = new SolidBrush(Theme.Tonal))
                g.FillPath(b, path);
            const int period = 1500;
            double k = (unchecked(Environment.TickCount - _start) % period) / (double)period;
            k = k < 0.5 ? 2 * k * k : 1 - Math.Pow(-2 * k + 2, 2) / 2; // ease in-out
            float seg = track.Width * 0.3f;
            float x0 = track.X - seg + (float)k * (track.Width + seg);
            var segR = RectangleF.Intersect(new RectangleF(x0, track.Y, seg, track.Height), track);
            if (segR.Width > 0.5f)
                using (var path = Theme.Rounded(segR, 2))
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, path);
        }

        private static void DrawLine(Graphics g, string s, Font f, Color c, RectangleF r, bool wrap = false)
        {
            using (f)
            using (var b = new SolidBrush(c))
                g.DrawString(s ?? "", f, b, r, wrap ? StringFormat.GenericTypographic : OneLine);
        }

        // ------------------------------------------------------------ input

        private bool OverCancel(Point p) => _cancel != null && !_cancelClicked && _cancelR.Contains(p.X / S, p.Y / S);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = OverCancel(e.Location);
            if (over != _hoverCancel) { _hoverCancel = over; Cursor = over ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverCancel) { _hoverCancel = false; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !OverCancel(e.Location)) return;
            _cancelClicked = true;
            _hoverCancel = false;
            Cursor = Cursors.Default;
            SetStep("Cancelling...");
            _cancel();
        }
    }
}
