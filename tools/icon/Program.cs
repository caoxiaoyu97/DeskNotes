// Generates the DeskNotes icon set.
//
// Small frames are written as classic BMP entries on purpose: Explorer draws the
// tray icon through GDI, and GDI cannot render PNG-compressed ICO frames - such an
// icon shows up blank or falls back to a generic one. Only the 256 px frame is PNG.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
    private static readonly string[] Names = { "a-tile", "b-circle", "c-outline", "d-page", "e-checklist", "f-lines" };
    private static readonly string[] Titles =
    {
        "圆角方块 + 对勾",
        "圆形 + 对勾",
        "描边方块 + 对勾（深浅背景都能用）",
        "便签纸（折角）+ 对勾",
        "清单：两勾一空",
        "便签三行 + 对勾"
    };
    private static readonly int[] PreviewSizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "work/icons";
        string preview = args.Length > 1 ? args[1] : "work/icon-variants.png";
        string canonical = args.Length > 2 ? args[2] : "assets/DeskNotes.ico";
        int shipped = args.Length > 3 ? int.Parse(args[3]) : 0;
        Directory.CreateDirectory(outDir);
        for (int i = 0; i < Names.Length; i++)
        {
            var frames = new List<Bitmap>();
            foreach (int size in Sizes) frames.Add(Draw(i, size));
            string path = Path.Combine(outDir, Names[i] + ".ico");
            WriteIco(path, frames);
            Console.WriteLine("wrote " + path);
        }
        WriteSheet(preview);
        WriteDirectory(canonical, shipped);
        Console.WriteLine($"{canonical} = 方案 {shipped + 1} ({Names[shipped]})");
        return 0;
    }

    private static Bitmap Draw(int design, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float pad = size * 0.05f;
        var box = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
        var green = Color.FromArgb(255, 92, 132, 79);
        var light = Color.FromArgb(255, 128, 164, 108);
        float radius = size * 0.24f;
        switch (design)
        {
            case 0:
                Fill(g, box, radius, light, green);
                Check(g, size, Color.White, 0.115f);
                break;
            case 1:
                using (var brush = new LinearGradientBrush(box, light, green, 90f))
                    g.FillEllipse(brush, box);
                Check(g, size, Color.White, 0.115f);
                break;
            case 2:
                using (var path = Rounded(box, radius))
                using (var pen = new Pen(green, Math.Max(1.4f, size * 0.10f)))
                    g.DrawPath(pen, path);
                Check(g, size, green, 0.10f);
                break;
            case 3:
                using (var path = Rounded(box, radius * 0.6f))
                {
                    g.FillPath(Brushes.White, path);
                    using var pen = new Pen(Color.FromArgb(255, 206, 214, 200), Math.Max(1f, size * 0.045f));
                    g.DrawPath(pen, path);
                }
                using (var fold = new SolidBrush(Color.FromArgb(255, 224, 231, 219)))
                    g.FillPolygon(fold, new[]
                    {
                        new PointF(box.Right - box.Width * 0.34f, box.Bottom),
                        new PointF(box.Right, box.Bottom - box.Height * 0.34f),
                        new PointF(box.Right, box.Bottom)
                    });
                Check(g, size, green, 0.10f);
                break;
            case 4:
                Fill(g, box, radius, light, green);
                float rowH = size * 0.15f;
                for (int i = 0; i < 3; i++)
                {
                    float y = size * 0.28f + i * rowH * 1.35f;
                    float x = size * 0.22f;
                    float side = size * 0.16f;
                    if (i < 2)
                    {
                        g.FillRectangle(Brushes.White, x, y, side, side);
                        using var pen = new Pen(green, Math.Max(1f, size * 0.045f))
                        { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLines(pen, new[]
                        {
                            new PointF(x + side * 0.2f, y + side * 0.55f),
                            new PointF(x + side * 0.45f, y + side * 0.78f),
                            new PointF(x + side * 0.85f, y + side * 0.22f)
                        });
                    }
                    else
                    {
                        using var pen = new Pen(Color.FromArgb(210, 255, 255, 255), Math.Max(1f, size * 0.07f))
                        { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLine(pen, x + side * 0.15f, y + side * 0.5f, x + side * 1.4f, y + side * 0.5f);
                    }
                }
                break;
            default:
                Fill(g, box, radius, light, green);
                using (var pen = new Pen(Color.White, Math.Max(1f, size * 0.085f))
                { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, size * 0.26f, size * 0.36f, size * 0.74f, size * 0.36f);
                    g.DrawLine(pen, size * 0.26f, size * 0.54f, size * 0.62f, size * 0.54f);
                }
                using (var pen = new Pen(Color.White, Math.Max(1.4f, size * 0.10f))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(size * 0.30f, size * 0.72f),
                        new PointF(size * 0.42f, size * 0.83f),
                        new PointF(size * 0.74f, size * 0.60f)
                    });
                break;
        }
        return bmp;
    }

    private static void Fill(Graphics g, RectangleF box, float radius, Color from, Color to)
    {
        using var path = Rounded(box, radius);
        using var brush = new LinearGradientBrush(box, from, to, 90f);
        g.FillPath(brush, path);
    }

    private static void Check(Graphics g, int size, Color color, float weight)
    {
        using var pen = new Pen(color, Math.Max(1.5f, size * weight))
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(size * 0.27f, size * 0.52f),
            new PointF(size * 0.43f, size * 0.68f),
            new PointF(size * 0.74f, size * 0.33f)
        });
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void WriteIco(string path, List<Bitmap> frames)
    {
        var blobs = new List<byte[]>();
        for (int i = 0; i < frames.Count; i++)
            blobs.Add(Sizes[i] > 128 ? Png(frames[i]) : BmpEntry(frames[i]));
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)blobs.Count);
        int offset = 6 + 16 * blobs.Count;
        for (int i = 0; i < blobs.Count; i++)
        {
            w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
            w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(blobs[i].Length);
            w.Write(offset);
            offset += blobs[i].Length;
        }
        foreach (byte[] blob in blobs) w.Write(blob);
    }

    private static byte[] Png(Bitmap bmp)
    {
        using var buffer = new MemoryStream();
        bmp.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    /// <summary>32bpp BITMAPINFOHEADER entry with an AND mask, the format GDI understands.</summary>
    private static byte[] BmpEntry(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, maskStride = ((w + 31) / 32) * 4;
        using var buffer = new MemoryStream();
        using var bw = new BinaryWriter(buffer);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h * 2);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(w * h * 4);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                Color c = bmp.GetPixel(x, y);
                bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
            }
        for (int y = h - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < w; x++)
                if (bmp.GetPixel(x, y).A < 128) row[x / 8] |= (byte)(0x80 >> (x % 8));
            bw.Write(row);
        }
        return buffer.ToArray();
    }

    /// <summary>Comparison sheet: one row per design, 16 px first.</summary>
    private static void WriteSheet(string path)
    {
        int gap = 26, labelWidth = 250, rowHeight = 300;
        int width = labelWidth + PreviewSizes.Sum(s => Math.Max(s, 40) + gap) + gap;
        using var sheet = new Bitmap(width, rowHeight * Names.Length + 70);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(255, 246, 247, 243));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var titleFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            using var font = new Font("Microsoft YaHei UI", 9.5f);
            using var small = new Font("Microsoft YaHei UI", 8.5f);
            g.DrawString("桌面便签 · 图标方案", titleFont, Brushes.Black, gap, 16);
            g.DrawString("每行一个方案，最左是任务栏实际的 16px 大小。", font, Brushes.Gray, gap, 44);
            for (int d = 0; d < Names.Length; d++)
            {
                int baseline = 70 + rowHeight * d + rowHeight - 40;
                g.DrawString($"{d + 1}. {Titles[d]}", font, Brushes.Black, gap, baseline - 96);
                g.DrawString(Names[d] + ".ico", small, Brushes.Gray, gap, baseline - 74);
                int x = labelWidth + gap;
                foreach (int size in PreviewSizes)
                {
                    using var icon = Draw(d, size);
                    g.DrawImage(icon, x, baseline - size, size, size);
                    g.DrawString(size.ToString(), small, Brushes.Gray, x, baseline + 8);
                    x += Math.Max(size, 40) + gap;
                }
                using var line = new Pen(Color.FromArgb(60, 0, 0, 0));
                g.DrawLine(line, gap, baseline + 40, width - gap, baseline + 40);
            }
        }
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine("wrote " + path);
    }

    private static void WriteDirectory(string path, int design)
    {
        var frames = new List<Bitmap>();
        foreach (int size in Sizes) frames.Add(Draw(design, size));
        WriteIco(path, frames);
        Console.WriteLine("wrote " + path);
    }
}
