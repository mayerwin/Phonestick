using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Phonestick
{
    /// <summary>Windows 11 look: light/dark from the system setting, brand gradient, rounded corners.</summary>
    internal static class Theme
    {
        public static readonly Color Violet = Color.FromArgb(0xA8, 0x55, 0xF7);
        public static readonly Color Indigo = Color.FromArgb(0x6D, 0x28, 0xD9);
        public static readonly Color Cyan = Color.FromArgb(0x08, 0x91, 0xB2);

        public static bool Dark
        {
            get
            {
                var forced = Environment.GetEnvironmentVariable("PHONESTICK_THEME"); // "light" or "dark", for screenshots
                if (forced == "light") return false;
                if (forced == "dark") return true;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
                }
                catch { return false; }
            }
        }

        // Tokens: calm porcelain canvas, white cards on soft shadows, one confident accent.
        public static Color Back => Dark ? C(0x121316) : C(0xF4F5F7);          // canvas
        public static Color Card => Dark ? C(0x1A1B1F) : Color.White;          // cards, menus
        public static Color Tonal => Dark ? C(0x26272D) : C(0xEEF0F3);         // secondary buttons
        public static Color TonalHover => Dark ? C(0x303139) : C(0xE4E7EB);
        public static Color CardHover => TonalHover;
        public static Color Text => Dark ? C(0xEDEEF0) : C(0x16181D);          // ink
        public static Color Text2 => Dark ? C(0xB4B8C0) : C(0x454A54);         // labels
        public static Color Muted => Dark ? C(0x8D929C) : C(0x646A75);         // captions
        public static Color Border => Dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);
        /// <summary>The one accent: a deep, confident violet.</summary>
        public static Color Accent => Dark ? C(0x8B5CF6) : C(0x6D28D9);
        public static Color AccentHover => Dark ? C(0x9F7AEA) : C(0x5B21B6);
        public static Color AccentText => Dark ? C(0xC4B5FD) : C(0x6D28D9);
        public static Color AccentTint => Dark ? Color.FromArgb(48, 0x8B, 0x5C, 0xF6) : Color.FromArgb(24, 0x6D, 0x28, 0xD9);
        public static Color OkBg => Dark ? C(0x14301F) : C(0xE6F4EA);
        public static Color OkFg => Dark ? C(0x6EE7A0) : C(0x15803D);

        private static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

        // ------------------------------------------------------------ fonts

        private static System.Drawing.Text.PrivateFontCollection _brandFonts;
        private static IntPtr _brandFontMem;

        /// <summary>Manrope Bold (bundled, SIL OFL) for the wordmark and headings.</summary>
        public static Font Brand(float px)
        {
            try
            {
                if (_brandFonts == null)
                {
                    using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Phonestick.Assets.Manrope-Bold.ttf"))
                    {
                        var data = new byte[s.Length];
                        s.Read(data, 0, data.Length);
                        _brandFontMem = Marshal.AllocCoTaskMem(data.Length); // kept for the app's lifetime
                        Marshal.Copy(data, 0, _brandFontMem, data.Length);
                        var pfc = new System.Drawing.Text.PrivateFontCollection();
                        pfc.AddMemoryFont(_brandFontMem, data.Length);
                        _brandFonts = pfc;
                    }
                }
                return new Font(_brandFonts.Families[0], px, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
            }
        }

        /// <summary>Native text font in pixels (Segoe UI Variable where available).</summary>
        public static Font Px(float px, FontStyle style = FontStyle.Regular, bool display = false)
        {
            var names = display
                ? new[] { "Segoe UI Variable Display Semib", "Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI" }
                : new[] { "Segoe UI Variable Text", "Segoe UI" };
            foreach (var name in names)
            {
                try
                {
                    var f = new Font(name, px, style, GraphicsUnit.Pixel);
                    if (f.Name == name) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
        }

        public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI" })
            {
                try
                {
                    var f = new Font(name, size, style);
                    if (f.Name == name) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        public static Font DisplayFont(float size, FontStyle style = FontStyle.Bold)
        {
            foreach (var name in new[] { "Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI" })
            {
                try
                {
                    var f = new Font(name, size, style);
                    if (f.Name == name) return f;
                    f.Dispose();
                }
                catch { }
            }
            return UiFont(size, style);
        }

        public static LinearGradientBrush BrandBrush(Rectangle r) =>
            new LinearGradientBrush(r, Violet, Cyan, 35f)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Violet, Indigo, Cyan },
                    Positions = new[] { 0f, 0.5f, 1f },
                },
            };

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------------------ window chrome

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Dark or light title bar and Windows 11 rounded corners. Harmless on Windows 10.</summary>
        public static void ApplyChrome(Form f, bool roundSmall = false)
        {
            try
            {
                int dark = Dark ? 1 : 0;
                DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);           // DWMWA_USE_IMMERSIVE_DARK_MODE
                int corner = roundSmall ? 3 : 2;                              // ROUNDSMALL / ROUND
                DwmSetWindowAttribute(f.Handle, 33, ref corner, 4);         // DWMWA_WINDOW_CORNER_PREFERENCE
            }
            catch { }
        }

        /// <summary>Recolours a plain form and its controls for the current theme.</summary>
        public static void Style(Control root)
        {
            root.BackColor = Back;
            root.ForeColor = Text;
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case TextBox t:
                        t.BackColor = Card;
                        t.ForeColor = Text;
                        t.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case Button b:
                        b.FlatStyle = FlatStyle.Flat;
                        b.FlatAppearance.BorderSize = 0;
                        b.FlatAppearance.MouseOverBackColor = TonalHover;
                        b.BackColor = Tonal;
                        b.ForeColor = Text;
                        b.Cursor = Cursors.Hand;
                        break;
                    default:
                        c.BackColor = Back;
                        c.ForeColor = Text;
                        break;
                }
                if (c.HasChildren) Style(c);
            }
        }

        /// <summary>Makes a button the primary action: brand colour, white text.</summary>
        public static void Primary(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            b.FlatAppearance.MouseDownBackColor = AccentHover;
            b.Font = UiFont(10f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
        }
    }

    /// <summary>Context menu painted to match Windows 11: rounded highlight, theme colours.</summary>
    internal sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = true; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using (var p = Theme.Rounded(r, 5))
            using (var b = new SolidBrush(Theme.CardHover))
                g.FillPath(b, p);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using (var pen = new Pen(Theme.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLines(pen, new[]
                {
                    new PointF(r.Left + r.Width * 0.18f, r.Top + r.Height * 0.52f),
                    new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.76f),
                    new PointF(r.Left + r.Width * 0.84f, r.Top + r.Height * 0.26f),
                });
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Back;
            public override Color ImageMarginGradientBegin => Theme.Back;
            public override Color ImageMarginGradientMiddle => Theme.Back;
            public override Color ImageMarginGradientEnd => Theme.Back;
            public override Color MenuBorder => Theme.Border;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Theme.CardHover;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Border;
        }
    }
}
