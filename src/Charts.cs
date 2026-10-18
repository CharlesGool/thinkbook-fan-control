using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ThinkBookFanControl
{
    public static class Palette
    {
        public static readonly Color Back = Color.White;
        public static readonly Color Grid = Color.FromArgb(232, 234, 238);
        public static readonly Color Axis = Color.FromArgb(120, 126, 138);
        public static readonly Color Text = Color.FromArgb(70, 76, 88);
        public static readonly Color Temp = Color.FromArgb(234, 88, 12);
        public static readonly Color[] Fan = { Color.FromArgb(37, 99, 235), Color.FromArgb(16, 163, 127), Color.FromArgb(147, 51, 234), Color.FromArgb(219, 39, 119) };
        public static Color FanColor(int i) { return Fan[i % Fan.Length]; }

        // Control.DeviceDpi stays 96 on .NET Framework without per-monitor mode, so read the system DPI once.
        public static readonly float DpiScale = GetDpi();
        private static float GetDpi() { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
    }

    /// <summary>Temperature → RPM curve editor. Drag points, double-click to add, right-click to delete.</summary>
    public class CurveEditor : Control
    {
        public const int TempMin = 20, TempMax = 100;

        private List<CurvePoint>[] _curves;
        private FanInfo[] _fans;
        private int _active;
        private bool _linked;
        private int _drag = -1, _hover = -1;
        private double _liveTemp = double.NaN;
        private int[] _liveTarget;
        private int _yMax = 6000;

        public event EventHandler CurveChanged;

        public CurveEditor()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Back;
        }

        public void Bind(List<CurvePoint>[] curves, List<FanInfo> fans)
        {
            _curves = curves; _fans = fans.ToArray();
            int max = 0; foreach (var f in fans) max = Math.Max(max, f.MaxRpm);
            _yMax = (max + 999) / 1000 * 1000;
            Invalidate();
        }

        public int ActiveFan { get { return _active; } set { _active = value; Invalidate(); } }
        /// <summary>Allow points below the rated minimum (down to LowestStableRpm, or 0 = stop).</summary>
        public bool AllowLow { get { return _allowLow; } set { _allowLow = value; Invalidate(); } }
        private bool _allowLow;
        public bool Linked { get { return _linked; } set { _linked = value; Invalidate(); } }

        public void SetLive(double curveTemp, int[] targets) { _liveTemp = curveTemp; _liveTarget = targets; Invalidate(); }

        private int EditIndex { get { return _linked ? 0 : _active; } }
        private List<CurvePoint> Pts { get { return _curves[EditIndex]; } }
        private FanInfo Fan { get { return _fans[EditIndex]; } }

        private Rectangle Plot
        {
            get
            {
                int l = Scale(56), r = Scale(16), t = Scale(14), b = Scale(34);
                return new Rectangle(l, t, Math.Max(10, Width - l - r), Math.Max(10, Height - t - b));
            }
        }
        private static int Scale(int px) { return (int)Math.Round(px * Palette.DpiScale); }

        private PointF ToScreen(double temp, double rpm)
        {
            var p = Plot;
            return new PointF((float)(p.Left + (temp - TempMin) / (TempMax - TempMin) * p.Width),
                              (float)(p.Bottom - rpm / _yMax * p.Height));
        }
        private void FromScreen(Point pt, out int temp, out int rpm)
        {
            var p = Plot;
            temp = (int)Math.Round(TempMin + (pt.X - p.Left) / (double)p.Width * (TempMax - TempMin));
            rpm = (int)Math.Round((p.Bottom - pt.Y) / (double)p.Height * _yMax / 100.0) * 100;
        }

        private int HitTest(Point pt)
        {
            if (_curves == null) return -1;
            float r = Scale(9);
            for (int i = 0; i < Pts.Count; i++)
            {
                var s = ToScreen(Pts[i].Temp, Pts[i].Rpm);
                if (Math.Abs(s.X - pt.X) <= r && Math.Abs(s.Y - pt.Y) <= r) return i;
            }
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_curves == null || !Enabled) return;
            int hit = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && hit >= 0) { _drag = hit; Capture = true; }
            else if (e.Button == MouseButtons.Right && hit >= 0 && Pts.Count > 2) { Pts.RemoveAt(hit); _hover = -1; Changed(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_curves == null || !Enabled || e.Button != MouseButtons.Left || HitTest(e.Location) >= 0) return;
            int t, r; FromScreen(e.Location, out t, out r);
            t = Settings.Clamp(t, TempMin, TempMax);
            foreach (var p in Pts) if (p.Temp == t) return;
            Pts.Add(new CurvePoint(t, Settings.SnapRpm(r, Fan, _allowLow)));
            Pts.Sort((a, b) => a.Temp.CompareTo(b.Temp));
            Changed();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_curves == null) return;
            if (_drag >= 0)
            {
                int t, r; FromScreen(e.Location, out t, out r);
                int lo = _drag > 0 ? Pts[_drag - 1].Temp + 1 : TempMin;
                int hi = _drag < Pts.Count - 1 ? Pts[_drag + 1].Temp - 1 : TempMax;
                Pts[_drag] = new CurvePoint(Settings.Clamp(t, lo, hi), Settings.SnapRpm(r, Fan, _allowLow));
                Changed();
            }
            else
            {
                int h = HitTest(e.Location);
                if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag >= 0) { _drag = -1; Capture = false; Changed(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover >= 0) { _hover = -1; Invalidate(); } }

        private void Changed()
        {
            Invalidate();
            var h = CurveChanged; if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var p = Plot;
            using (var gridPen = new Pen(Palette.Grid))
            using (var axisPen = new Pen(Palette.Axis))
            using (var txt = new SolidBrush(Palette.Text))
            {
                var sfR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                var sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
                for (int t = TempMin; t <= TempMax; t += 10)
                {
                    var a = ToScreen(t, 0);
                    g.DrawLine(gridPen, a.X, p.Top, a.X, p.Bottom);
                    g.DrawString(t + "°C", Font, txt, a.X, p.Bottom + Scale(4), sfC);
                }
                for (int r = 0; r <= _yMax; r += 1000)
                {
                    var a = ToScreen(TempMin, r);
                    g.DrawLine(gridPen, p.Left, a.Y, p.Right, a.Y);
                    g.DrawString(r.ToString(), Font, txt, p.Left - Scale(4), a.Y, sfR);
                }
                g.DrawRectangle(axisPen, p);
            }
            if (_curves == null) return;

            // Below the rated minimum: grey; with low speed allowed, label it.
            using (var band = new SolidBrush(Color.FromArgb(14, 0, 0, 0)))
            {
                var top = ToScreen(TempMin, Fan.MinRpm);
                g.FillRectangle(band, p.Left, top.Y, p.Width, p.Bottom - top.Y);
                if (_allowLow)
                    using (var b = new SolidBrush(Color.FromArgb(150, Palette.Text)))
                        g.DrawString("低于额定最低转速（< " + LenovoWmi.LowestStableRpm + " 即停转）", Font, b, p.Left + Scale(6), top.Y + Scale(3));
            }

            // Inactive curves first (dim), active last.
            if (!_linked)
                for (int i = 0; i < _curves.Length; i++) if (i != _active) DrawCurve(g, _curves[i], Color.FromArgb(90, Palette.FanColor(i)), false);
            DrawCurve(g, Pts, _linked ? Palette.FanColor(0) : Palette.FanColor(_active), true);

            // Live operating point.
            if (!double.IsNaN(_liveTemp) && _liveTarget != null)
            {
                var x = ToScreen(_liveTemp, 0).X;
                using (var pen = new Pen(Palette.Temp, Scale(1)) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, x, p.Top, x, p.Bottom);
                int idx = EditIndex;
                if (idx < _liveTarget.Length && _liveTarget[idx] > 0)
                {
                    var s = ToScreen(_liveTemp, _liveTarget[idx]);
                    float r = Scale(6);
                    using (var b = new SolidBrush(Palette.Temp)) g.FillEllipse(b, s.X - r, s.Y - r, 2 * r, 2 * r);
                }
                using (var b = new SolidBrush(Palette.Temp))
                    g.DrawString(string.Format("{0:0.0}°C", _liveTemp), Font, b, x + Scale(4), p.Top + Scale(2));
            }

            // Tooltip of hovered / dragged point.
            int tip = _drag >= 0 ? _drag : _hover;
            if (tip >= 0 && tip < Pts.Count)
            {
                var pt = Pts[tip];
                var s = ToScreen(pt.Temp, pt.Rpm);
                string label = pt.Temp + "°C → " + (pt.Rpm == 0 ? "停转" : pt.Rpm + " RPM");
                var sz = g.MeasureString(label, Font);
                float bx = Math.Min(s.X + Scale(10), p.Right - sz.Width - Scale(8)), by = Math.Max(p.Top, s.Y - sz.Height - Scale(14));
                using (var bg = new SolidBrush(Color.FromArgb(235, 30, 34, 42))) g.FillRectangle(bg, bx, by, sz.Width + Scale(8), sz.Height + Scale(4));
                g.DrawString(label, Font, Brushes.White, bx + Scale(4), by + Scale(2));
            }
        }

        private void DrawCurve(Graphics g, List<CurvePoint> pts, Color c, bool active)
        {
            if (pts.Count == 0) return;
            var p = Plot;
            var line = new List<PointF>();
            line.Add(ToScreen(TempMin, pts[0].Rpm));
            foreach (var pt in pts) line.Add(ToScreen(pt.Temp, pt.Rpm));
            line.Add(ToScreen(TempMax, pts[pts.Count - 1].Rpm));

            if (active)
            {
                var fill = new List<PointF>(line);
                fill.Add(new PointF(p.Right, p.Bottom)); fill.Add(new PointF(p.Left, p.Bottom));
                using (var b = new SolidBrush(Color.FromArgb(28, c))) g.FillPolygon(b, fill.ToArray());
            }
            using (var pen = new Pen(c, Scale(active ? 2 : 1)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, line.ToArray());
            if (!active) return;
            float r = Scale(5);
            using (var b = new SolidBrush(c))
            using (var w = new Pen(Color.White, Scale(2)))
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    var s = ToScreen(pts[i].Temp, pts[i].Rpm);
                    float rr = (i == _hover || i == _drag) ? r * 1.4f : r;
                    g.FillEllipse(b, s.X - rr, s.Y - rr, 2 * rr, 2 * rr);
                    g.DrawEllipse(w, s.X - rr, s.Y - rr, 2 * rr, 2 * rr);
                }
            }
        }
    }

    /// <summary>Rolling history of temperature (right axis) and fan RPM (left axis).</summary>
    public class HistoryView : Control
    {
        private const int Capacity = 300; // seconds
        private readonly List<int> _temp = new List<int>();
        private readonly List<int[]> _rpm = new List<int[]>();
        private FanInfo[] _fans = new FanInfo[0];
        private int _yMax = 6000;

        public HistoryView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Back;
        }

        public void Bind(List<FanInfo> fans)
        {
            _fans = fans.ToArray();
            int max = 0; foreach (var f in fans) max = Math.Max(max, f.MaxRpm);
            _yMax = (max + 999) / 1000 * 1000;
        }

        public void Add(int temp, int[] rpm)
        {
            _temp.Add(temp); _rpm.Add((int[])rpm.Clone());
            if (_temp.Count > Capacity) { _temp.RemoveAt(0); _rpm.RemoveAt(0); }
            Invalidate();
        }

        private static int Scale(int px) { return (int)Math.Round(px * Palette.DpiScale); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = new Rectangle(Scale(56), Scale(24), Math.Max(10, Width - Scale(56) - Scale(48)), Math.Max(10, Height - Scale(24) - Scale(22)));
            using (var gridPen = new Pen(Palette.Grid))
            using (var axisPen = new Pen(Palette.Axis))
            using (var txt = new SolidBrush(Palette.Text))
            using (var tb = new SolidBrush(Palette.Temp))
            {
                var sfR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                var sfL = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
                for (int i = 0; i <= 4; i++)
                {
                    float y = p.Bottom - p.Height * i / 4f;
                    g.DrawLine(gridPen, p.Left, y, p.Right, y);
                    g.DrawString((_yMax * i / 4).ToString(), Font, txt, p.Left - Scale(4), y, sfR);
                    g.DrawString((20 + 80 * i / 4) + "°C", Font, tb, p.Right + Scale(4), y, sfL);
                }
                g.DrawRectangle(axisPen, p);
                g.DrawString("最近 5 分钟", Font, txt, p.Left, p.Bottom + Scale(3));

                // Legend.
                float lx = p.Left;
                for (int i = 0; i < _fans.Length; i++)
                {
                    using (var b = new SolidBrush(Palette.FanColor(i))) g.FillRectangle(b, lx, Scale(8), Scale(12), Scale(3));
                    g.DrawString(_fans[i].Name + " RPM", Font, txt, lx + Scale(16), Scale(1));
                    lx += Scale(16) + g.MeasureString(_fans[i].Name + " RPM", Font).Width + Scale(12);
                }
                g.FillRectangle(tb, lx, Scale(8), Scale(12), Scale(3));
                g.DrawString("CPU 温度", Font, txt, lx + Scale(16), Scale(1));
            }
            if (_temp.Count < 2) return;

            float dx = p.Width / (float)(Capacity - 1);
            float x0 = p.Right - (_temp.Count - 1) * dx;
            for (int f = 0; f < _fans.Length; f++)
            {
                var pts = new List<PointF>();
                for (int i = 0; i < _rpm.Count; i++)
                    if (_rpm[i][f] >= 0) pts.Add(new PointF(x0 + i * dx, p.Bottom - Math.Min(_rpm[i][f], _yMax) / (float)_yMax * p.Height));
                if (pts.Count > 1) using (var pen = new Pen(Palette.FanColor(f), Scale(2))) g.DrawLines(pen, pts.ToArray());
            }
            var tp = new List<PointF>();
            for (int i = 0; i < _temp.Count; i++)
                if (_temp[i] >= 0) tp.Add(new PointF(x0 + i * dx, p.Bottom - Settings.Clamp(_temp[i] - 20, 0, 80) / 80f * p.Height));
            if (tp.Count > 1) using (var pen = new Pen(Palette.Temp, Scale(2))) g.DrawLines(pen, tp.ToArray());
        }
    }
}
