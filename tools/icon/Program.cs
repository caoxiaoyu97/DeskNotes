// Builds the DeskNotes icon set from the Segoe Fluent Icons glyphs that ship with
// Windows, composed the way an application icon normally is (white glyph on a
// coloured tile). Glyph outlines are filled as vectors so they stay crisp at 16 px.
//
// Frames up to 128 px are written as classic 32bpp BMP entries because Explorer
// draws tray icons through GDI, which does not render PNG-compressed ICO frames.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;

internal enum Backing { None, Tile, Circle }

internal sealed record Candidate(string Name, int Glyph, Backing Backing, string Title);

internal static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
    private static readonly int[] PreviewSizes = { 16, 24, 32, 48, 64, 128, 256 };
    private static readonly Color Green = Color.FromArgb(255, 92, 132, 79);

    private static readonly Candidate[] Candidates =
    {
        new("01-checklist-tile", 0xE9D5, Backing.Tile, "清单加对勾 + 绿底方块"),
        new("02-checklist-plain", 0xE9D5, Backing.None, "清单加对勾（无底）"),
        new("03-checkbox-solid", 0xE73D, Backing.None, "实心方块对勾（微软复选样式）"),
        new("04-check-circle", 0xE73E, Backing.Circle, "对勾 + 绿底圆形"),
        new("05-list-tile", 0xE8FD, Backing.Tile, "项目符号列表 + 绿底方块"),
        new("06-page-tile", 0xE7C3, Backing.Tile, "页面 + 绿底方块"),
        new("07-document-tile", 0xE8A5, Backing.Tile, "文档 + 绿底方块"),
        new("08-check-bold-plain", 0xE8FB, Backing.None, "加粗对勾（无底）")
    };

    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "work/icons";
        string preview = args.Length > 1 ? args[1] : "work/icon-variants.png";
        string canonical = args.Length > 2 ? args[2] : "assets/DeskNotes.ico";
        int shipped = args.Length > 3 ? int.Parse(args[3]) : 0;
        Directory.CreateDirectory(outDir);

        using var font = GlyphFont();
        using var family = new FontFamily(font.Families[0].Name);
        for (int i = 0; i < Candidates.Length; i++)
        {
            var frames = Sizes.Select(s => Draw(Candidates[i], family, s)).ToList();
            WriteIco(Path.Combine(outDir, Candidates[i].Name + ".ico"), frames);
            foreach (var frame in frames) frame.Dispose();
        }
        WriteSheet(preview, family);
        var chosen = Sizes.Select(s => Draw(Candidates[shipped], family, s)).ToList();
        WriteIco(canonical, chosen);
        foreach (var frame in chosen) frame.Dispose();
        Console.WriteLine($"{canonical} = {Candidates[shipped].Name} ({Candidates[shipped].Title})");
        return 0;
    }

    /// <summary>Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
    private static PrivateFontCollection GlyphFont()
    {
        var fonts = new PrivateFontCollection();
        foreach (string name in new[] { "SegoeIcons.ttf", "segmdl2.ttf" })
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", name);
            if (!File.Exists(path)) continue;
            try { fonts.AddFontFile(path); return fonts; }
            catch (Exception e) when (e is IOException or ArgumentException) { }
            if (fonts.Families.Length > 0) return fonts;
        }
        throw new FileNotFoundException("Segoe icon font not found in %WINDIR%\\Fonts");
    }

    private static Bitmap Draw(Candidate candidate, FontFamily family, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float pad = size * 0.055f;
        var box = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
        switch (candidate.Backing)
        {
            case Backing.Tile:
                using (var path = Rounded(box, size * 0.24f))
                using (var brush = new SolidBrush(Green))
                    g.FillPath(brush, path);
                break;
            case Backing.Circle:
                using (var brush = new SolidBrush(Green))
                    g.FillEllipse(brush, box);
                break;
        }
        float coverage = candidate.Backing == Backing.None ? 0.98f : 0.52f;
        using var glyph = GlyphPath(candidate.Glyph, family, (float)(size * coverage));
        if (glyph is null) return bmp;
        var ink = glyph.GetBounds();
        using var transform = new Matrix();
        transform.Translate((float)(size / 2.0 - ink.X - ink.Width / 2.0), (float)(size / 2.0 - ink.Y - ink.Height / 2.0));
        glyph.Transform(transform);
        using var pen = new SolidBrush(candidate.Backing == Backing.None ? Green : Color.White);
        g.FillPath(pen, glyph);
        return bmp;
    }

    private static GraphicsPath? GlyphPath(int codepoint, FontFamily family, float emSize)
    {
        var path = new GraphicsPath();
        try
        {
            path.AddString(char.ConvertFromUtf32(codepoint), family, 0, emSize, PointF.Empty, StringFormat.GenericTypographic);
            return path;
        }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or OutOfMemoryException)
        {
            path.Dispose();
            return null;
        }
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

    private static void WriteSheet(string path, FontFamily family)
    {
        int gap = 26, labelWidth = 300, rowHeight = 300;
        int width = labelWidth + PreviewSizes.Sum(s => Math.Max(s, 40) + gap) + gap;
        using var sheet = new Bitmap(width, rowHeight * Candidates.Length + 80);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(255, 246, 247, 243));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var titleFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            using var font = new Font("Microsoft YaHei UI", 10f);
            using var small = new Font("Microsoft YaHei UI", 8.5f);
            g.DrawString("桌面便签 · 图标候选（来源：Windows 自带 Segoe Fluent Icons 字形）", titleFont, Brushes.Black, gap, 16);
            g.DrawString("每行一个方案，左侧是任务栏真实的 16px 大小，向右依次放大。", font, Brushes.Gray, gap, 46);
            for (int i = 0; i < Candidates.Length; i++)
            {
                int baseline = 80 + rowHeight * i + rowHeight - 44;
                g.DrawString($"{i + 1}. {Candidates[i].Title}", font, Brushes.Black, gap, baseline - 100);
                g.DrawString($"{Candidates[i].Name}.ico   U+{Candidates[i].Glyph:X4}", small, Brushes.Gray, gap, baseline - 78);
                int x = labelWidth + gap;
                foreach (int size in PreviewSizes)
                {
                    using var icon = Draw(Candidates[i], family, size);
                    g.DrawImage(icon, x, baseline - size, size, size);
                    g.DrawString(size.ToString(), small, Brushes.Gray, x, baseline + 8);
                    x += Math.Max(size, 40) + gap;
                }
                using var line = new Pen(Color.FromArgb(58, 0, 0, 0));
                g.DrawLine(line, gap, baseline + 42, width - gap, baseline + 42);
            }
        }
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine("wrote " + path);
    }
}
