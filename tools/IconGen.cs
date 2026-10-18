// Writes res\app.ico (exe / window icon) from the shared glyph in src\FanIcon.cs, and optionally a preview.
//   IconGen.exe <out.ico> [preview.png]
// Build & run: tools\make-icon.cmd
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ThinkBookFanControl;

static class IconGen
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        var images = new List<byte[]>();
        foreach (int s in Sizes) using (var bmp = FanIcon.Render(s, FanIcon.Blue)) images.Add(Dib(bmp)); // DIB everywhere: System.Drawing.Icon cannot read PNG frames

        using (var fs = File.Create(outPath))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
        Console.WriteLine("wrote " + outPath);
        if (args.Length > 1) Preview(args[1]);
        return 0;
    }

    // App icon on light / dark backgrounds, plus the tray variants on a light and a dark taskbar.
    static void Preview(string outPath)
    {
        int[] show = { 16, 24, 32, 48, 128 };
        using (var sheet = new Bitmap(700, 330))
        using (var g = Graphics.FromImage(sheet))
        using (var font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            Color[] backs = { Color.FromArgb(243, 243, 243), Color.FromArgb(32, 32, 32) };
            string[] names = { "程序图标·浅色", "程序图标·深色" };
            for (int row = 0; row < 2; row++)
            {
                int y = 10 + row * 150;
                using (var b = new SolidBrush(backs[row])) g.FillRectangle(b, 150, y, 540, 140);
                g.DrawString(names[row], font, Brushes.Black, 10, y + 60);
                int x = 165;
                foreach (int s in show)
                    using (var bmp = FanIcon.Render(s, FanIcon.Blue)) { g.DrawImage(bmp, x, y + 6 + (128 - s) / 2, s, s); x += s + 18; }
                // Tray variant for this taskbar theme.
                using (var bmp = FanIcon.Render(32, row == 0 ? FanIcon.DarkInk : FanIcon.LightInk)) g.DrawImage(bmp, x + 30, y + 54, 32, 32);
                using (var bmp = FanIcon.Render(16, row == 0 ? FanIcon.DarkInk : FanIcon.LightInk)) g.DrawImage(bmp, x + 80, y + 62, 16, 16);
                using (var b = new SolidBrush(row == 0 ? Color.Black : Color.White)) g.DrawString("托盘", font, b, x + 36, y + 100);
            }
            sheet.Save(outPath, ImageFormat.Png);
        }
    }

    // Classic ICO entry: BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bit AND mask (all zero, alpha is used).
    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            int maskStride = ((s + 31) / 32) * 4;
            w.Write(new byte[maskStride * s]);
            return ms.ToArray();
        }
    }
}
