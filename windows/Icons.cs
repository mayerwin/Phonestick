using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Phonestick
{
    /// <summary>The brand icons, embedded from Assets\*.ico (made by branding/tools/build_icons.py).</summary>
    internal static class Icons
    {
        public static Icon App => Load("phonestick.ico", SystemInformation.IconSize);
        public enum TrayState { Idle, On, Mounted }

        /// <summary>
        /// Tray icon, drawn at the exact pixel size the taskbar uses. Like Windows' own tray icons
        /// it is a single-colour glyph that follows the taskbar theme (white on a dark taskbar,
        /// ink on a light one): a solid phone with the Wi-Fi mark cut out of it. It turns violet
        /// while connected, and gets a green dot while the files are mounted.
        /// </summary>
        public static Icon Tray(TrayState state, int size = 0)
        {
            if (size <= 0) size = TraySize();
            using (var bmp = TrayBitmap(state, size, TaskbarIsDark()))
            {
                var h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return icon;
            }
        }

        /// <summary>
        /// The small-icon size at the taskbar's own DPI. SystemInformation gives the DPI this
        /// process started with, which goes stale after a scaling change (the icon then blurs).
        /// </summary>
        private static int TraySize()
        {
            try
            {
                var bar = FindWindow("Shell_TrayWnd", null);
                uint dpi = bar == IntPtr.Zero ? 0 : GetDpiForWindow(bar);
                if (dpi > 0)
                {
                    int s = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi);
                    if (s > 0) return s;
                }
            }
            catch { } // before Windows 10 1607: no per-DPI metrics
            return SystemInformation.SmallIconSize.Width;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        public static bool TaskbarIsDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return !(k?.GetValue("SystemUsesLightTheme") is int v && v == 1);
            }
            catch { return true; }
        }

        public static Bitmap TrayBitmap(TrayState state, int s, bool darkTaskbar)
        {
            var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                // Idle: the taskbar's own glyph colour. Connected: the brand violet, tuned per taskbar.
                var ink = darkTaskbar ? Color.White : Color.FromArgb(0x1F, 0x21, 0x27);
                var violet = darkTaskbar ? Color.FromArgb(0xB3, 0x9D, 0xFF) : Color.FromArgb(0x6D, 0x28, 0xD9);
                var fill = state == TrayState.Idle ? ink : violet;

                // A solid phone, edges on whole pixels.
                float w = (float)Math.Round(s * 0.60f), h = (float)Math.Round(s * 0.92f);
                if ((s - w) % 2 != 0) w++;
                float x = (s - w) / 2f, y = (float)Math.Floor((s - h) / 2f);
                using (var b = new SolidBrush(fill))
                using (var path = Theme.Rounded(new RectangleF(x, y, w, h), Math.Max(2f, s * 0.17f)))
                    g.FillPath(b, path);

                // Knocked out of it: an earpiece slot and the Wi-Fi mark (negative space reads best tiny).
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                var clear = Color.Transparent;
                float unit = Math.Max(1f, (float)Math.Round(s / 14f));           // 16:1 20:1 24:2 28:2 32:2 40:3
                using (var b = new SolidBrush(clear))
                {
                    float slotW = (float)Math.Round(w * 0.30f), slotH = unit;
                    if (s >= 20) g.FillRectangle(b, (float)Math.Floor((s - slotW) / 2f), y + (float)Math.Round(h * 0.10f), slotW, slotH);
                }
                // Wi-Fi mark on a pixel grid: dot 2u, then gap u, arc u, gap u, arc u.
                float cx = s / 2f, cy = y + (float)Math.Round(h * 0.74f) + 0.5f * (unit % 2);
                float dot = unit * 2;
                using (var b = new SolidBrush(clear)) g.FillEllipse(b, cx - dot / 2f, cy - dot / 2f, dot, dot);
                using (var pen = new Pen(clear, unit) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                {
                    float r1 = unit * 2.5f, r2 = unit * 4.5f;
                    g.DrawArc(pen, cx - r1, cy - r1, r1 * 2, r1 * 2, 226, 88);
                    if (r2 * 1.42f + unit < w) g.DrawArc(pen, cx - r2, cy - r2, r2 * 2, r2 * 2, 226, 88);
                }

                // Mounted: a green dot, cut out of the phone so it reads at any size.
                if (state == TrayState.Mounted)
                {
                    float br = (float)Math.Round(s * 0.34f), gap = Math.Max(1f, unit);
                    float bx = s - br, by = s - br;
                    using (var b = new SolidBrush(clear)) g.FillEllipse(b, bx - gap, by - gap, br + gap * 2, br + gap * 2);
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    using (var b = new SolidBrush(Color.FromArgb(0x22, 0xC5, 0x5E))) g.FillEllipse(b, bx, by, br, br);
                }
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            }
            return bmp;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(System.IntPtr h);

        public static Bitmap Logo(int size)
        {
            using (var ico = Load("phonestick.ico", new Size(256, 256)))
            using (var big = ico.ToBitmap())
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(big, 0, 0, size, size);
                }
                return bmp;
            }
        }

        private static Icon Load(string name, Size size)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Phonestick.Assets." + name))
            {
                if (s == null) return SystemIcons.Application;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return new Icon(ms, size);
            }
        }
    }
}
