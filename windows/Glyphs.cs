using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Phonestick
{
    /// <summary>
    /// Lucide-style stroked icons (24-unit canvas, ISC licence), drawn from their SVG path data,
    /// so they stay crisp at any scale and recolour with the theme.
    /// </summary>
    internal static class Glyphs
    {
        public const string FolderOpen = "M6 14l1.45-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.93a2 2 0 0 1 1.66.9l.82 1.2a2 2 0 0 0 1.66.9H18a2 2 0 0 1 2 2v2";
        public const string Smartphone = "M7 2h10a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2zM12 18h.01";
        public const string Monitor = "M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM8 21h8M12 17v4";
        public const string Unplug = "M19 5l3-3M2 22l3-3M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4zM7.5 13.5L10 11M10.5 16.5L13 14M12 6l6 6 2.3-2.3a2.4 2.4 0 0 0 0-3.4l-2.6-2.6a2.4 2.4 0 0 0-3.4 0z";
        public const string Settings = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z";
        public const string Check = "M20 6L9 17l-5-5";
        public const string Power = "M12 2v10M18.4 6.6a9 9 0 1 1-12.77.04";
        public const string FileText = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M16 13H8M16 17H8M10 9H8";
        public const string Wifi = "M12 20h.01M2 8.82a15 15 0 0 1 20 0M5 12.86a10 10 0 0 1 14 0M8.5 16.43a5 5 0 0 1 7 0";
        public const string Lock = "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2zM7 11V7a5 5 0 0 1 10 0v4";

        private static readonly Dictionary<string, GraphicsPath> Cache = new Dictionary<string, GraphicsPath>();

        /// <summary>Draws icon <paramref name="d"/> into a square box at (x, y) of the given size.</summary>
        public static void Draw(Graphics g, string d, Color color, float x, float y, float size, float stroke = 2f)
        {
            if (!Cache.TryGetValue(d, out var path)) Cache[d] = path = Parse(d);
            var state = g.Save();
            g.TranslateTransform(x, y);
            g.ScaleTransform(size / 24f, size / 24f);
            using (var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawPath(pen, path);
            g.Restore(state);
        }

        // ------------------------------------------------------------ SVG path parser

        private static GraphicsPath Parse(string d)
        {
            var p = new GraphicsPath();
            var tok = Tokens(d);
            int i = 0;
            char cmd = 'M';
            PointF cur = PointF.Empty, start = PointF.Empty, lastCtrl = PointF.Empty;
            char prev = ' ';
            float N() => float.Parse(tok[i++], CultureInfo.InvariantCulture);
            bool IsNum(int k) => k < tok.Count && !char.IsLetter(tok[k][0]);

            while (i < tok.Count)
            {
                if (char.IsLetter(tok[i][0])) cmd = tok[i++][0];
                bool rel = char.IsLower(cmd);
                PointF R(float x, float y) => rel ? new PointF(cur.X + x, cur.Y + y) : new PointF(x, y);
                switch (char.ToUpper(cmd))
                {
                    case 'M':
                        cur = R(N(), N());
                        start = cur;
                        p.StartFigure();
                        cmd = rel ? 'l' : 'L'; // following pairs are lines
                        break;
                    case 'L':
                        { var n = R(N(), N()); Line(p, cur, n); cur = n; }
                        break;
                    case 'H':
                        { var x = N(); var n = new PointF(rel ? cur.X + x : x, cur.Y); Line(p, cur, n); cur = n; }
                        break;
                    case 'V':
                        { var y = N(); var n = new PointF(cur.X, rel ? cur.Y + y : y); Line(p, cur, n); cur = n; }
                        break;
                    case 'C':
                        {
                            var c1 = R(N(), N()); var c2 = R(N(), N()); var e = R(N(), N());
                            p.AddBezier(cur, c1, c2, e); lastCtrl = c2; cur = e;
                        }
                        break;
                    case 'S':
                        {
                            var c1 = "CS".IndexOf(char.ToUpper(prev)) >= 0 ? new PointF(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                            var c2 = R(N(), N()); var e = R(N(), N());
                            p.AddBezier(cur, c1, c2, e); lastCtrl = c2; cur = e;
                        }
                        break;
                    case 'A':
                        {
                            float rx = N(), ry = N(); N(); // x-axis rotation: unused by these icons
                            bool large = N() != 0, sweep = N() != 0;
                            var e = R(N(), N());
                            Arc(p, cur, e, rx, ry, large, sweep);
                            cur = e;
                        }
                        break;
                    case 'Z':
                        p.CloseFigure();
                        cur = start;
                        break;
                    default:
                        i++;
                        break;
                }
                prev = cmd;
                if (char.ToUpper(cmd) == 'Z' && IsNum(i)) cmd = 'L';
            }
            return p;
        }

        private static void Line(GraphicsPath p, PointF a, PointF b)
        {
            // A zero-length segment is Lucide's "dot" (e.g. h.01): give it a tiny length so round caps draw it.
            if (Math.Abs(a.X - b.X) < 0.001f && Math.Abs(a.Y - b.Y) < 0.001f) b = new PointF(b.X + 0.01f, b.Y);
            p.AddLine(a, b);
        }

        /// <summary>SVG elliptical arc (no rotation) converted to GDI+ AddArc.</summary>
        private static void Arc(GraphicsPath p, PointF a, PointF b, float rx, float ry, bool large, bool sweep)
        {
            if (rx == 0 || ry == 0) { p.AddLine(a, b); return; }
            double x1p = (a.X - b.X) / 2.0, y1p = (a.Y - b.Y) / 2.0;
            double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
            if (lambda > 1) { var s = Math.Sqrt(lambda); rx = (float)(rx * s); ry = (float)(ry * s); }
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double coef = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
            double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;
            double cx = cxp + (a.X + b.X) / 2.0, cy = cyp + (a.Y + b.Y) / 2.0;
            double t1 = Math.Atan2((a.Y - cy) / ry, (a.X - cx) / rx);
            double t2 = Math.Atan2((b.Y - cy) / ry, (b.X - cx) / rx);
            double dt = t2 - t1;
            if (sweep && dt < 0) dt += 2 * Math.PI;
            if (!sweep && dt > 0) dt -= 2 * Math.PI;
            p.AddArc((float)(cx - rx), (float)(cy - ry), 2 * rx, 2 * ry, (float)(t1 * 180 / Math.PI), (float)(dt * 180 / Math.PI));
        }

        private static List<string> Tokens(string d)
        {
            var list = new List<string>();
            int i = 0;
            while (i < d.Length)
            {
                char c = d[i];
                if (char.IsLetter(c)) { list.Add(c.ToString()); i++; continue; }
                if (c == ',' || char.IsWhiteSpace(c)) { i++; continue; }
                int s = i;
                if (c == '-' || c == '+') i++;
                bool dot = false;
                while (i < d.Length && (char.IsDigit(d[i]) || (d[i] == '.' && !dot)))
                {
                    if (d[i] == '.') dot = true;
                    i++;
                }
                if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
                {
                    i++;
                    if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
                    while (i < d.Length && char.IsDigit(d[i])) i++;
                }
                list.Add(d.Substring(s, i - s));
            }
            return list;
        }
    }
}
