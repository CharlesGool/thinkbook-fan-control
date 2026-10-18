using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ThinkBookFanControl
{
    /// <summary>
    /// The app's fan glyph on a transparent background: an outlined hub and outlined crescent blades, many of
    /// them like a laptop blower fan. Shared by the tray icon (drawn at runtime to follow the taskbar theme)
    /// and tools\IconGen.cs (which writes res\app.ico).
    /// </summary>
    public static class FanIcon
    {
        public static readonly Color Blue = Color.FromArgb(59, 130, 246);   // exe / window icon: readable on light and dark
        public static readonly Color DarkInk = Color.FromArgb(31, 41, 55);  // tray on a light taskbar
        public static readonly Color LightInk = Color.White;                // tray on a dark taskbar

        public static Bitmap Render(int size, Color color)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                Draw(g, size, color);
            }
            return bmp;
        }

        /// <summary>Tiny sizes fill the blades instead, since outlines would merge.</summary>
        public static void Draw(Graphics g, float S, Color color)
        {
            float cx = S / 2f, cy = S / 2f, R = S * 0.45f;
            bool tiny = S <= 32;
            int blades = tiny ? 7 : 9;
            float width = Math.Max(1f, S * 0.045f);
            float hub = R * (tiny ? 0.30f : 0.34f);
            float span = tiny ? 20f : 17f; // angular width of a blade tip
            using (var pen = new Pen(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var brush = new SolidBrush(color))
            {
                float root = hub + width * 0.5f; // blades start at the hub outline
                for (int i = 0; i < blades; i++)
                {
                    float a = i * 360f / blades;
                    using (var p = new GraphicsPath())
                    {
                        // Leading edge: hub → tip, sweeping clockwise.
                        p.AddBezier(Polar(cx, cy, root, a), Polar(cx, cy, R * 0.62f, a + 2), Polar(cx, cy, R * 0.90f, a + 16), Polar(cx, cy, R, a + 30));
                        // Rounded tip.
                        p.AddBezier(Polar(cx, cy, R, a + 30), Polar(cx, cy, R * 1.04f, a + 30 + span * 0.5f), Polar(cx, cy, R, a + 30 + span), Polar(cx, cy, R * 0.93f, a + 30 + span));
                        // Trailing edge back to the hub.
                        p.AddBezier(Polar(cx, cy, R * 0.93f, a + 30 + span), Polar(cx, cy, R * 0.80f, a + 26 + span * 0.6f), Polar(cx, cy, R * 0.55f, a + span * 0.9f), Polar(cx, cy, root, a + span * 0.75f));
                        p.CloseFigure();
                        if (tiny) g.FillPath(brush, p); else g.DrawPath(pen, p);
                    }
                }
                if (tiny) g.FillEllipse(brush, cx - hub, cy - hub, 2 * hub, 2 * hub);
                else g.DrawEllipse(pen, cx - hub, cy - hub, 2 * hub, 2 * hub);
            }
        }

        // Point at radius r and angle a (degrees, clockwise from up) around (cx, cy).
        private static PointF Polar(float cx, float cy, float r, float a)
        {
            double rad = a * Math.PI / 180;
            return new PointF(cx + (float)(r * Math.Sin(rad)), cy - (float)(r * Math.Cos(rad)));
        }

        /// <summary>True when the taskbar / tray uses the dark theme (Windows 10/11 "default app mode" for system UI).</summary>
        public static bool TaskbarIsDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v == null || Convert.ToInt32(v) == 0; // missing value: Windows 10 before 1903, dark taskbar
                }
            }
            catch (Exception) { return true; }
        }

        /// <summary>Tray icon in the colour that contrasts with the current taskbar. Dispose with DestroyTrayIcon.</summary>
        public static Icon CreateTrayIcon(int size)
        {
            using (var bmp = Render(size, TaskbarIsDark() ? LightInk : DarkInk))
                return Icon.FromHandle(bmp.GetHicon());
        }

        public static void DestroyTrayIcon(Icon icon)
        {
            if (icon == null) return;
            IntPtr h = icon.Handle;
            icon.Dispose();
            DestroyIcon(h);
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
