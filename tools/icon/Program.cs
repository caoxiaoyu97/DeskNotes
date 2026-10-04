// Draws assets/DeskNotes.ico: a rounded green tile with a white check mark.
// Deliberately bold so it still reads at 16 px in the taskbar and tray.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "assets/DeskNotes.ico";
        var images = new List<byte[]>();
        foreach (int size in Sizes) images.Add(Render(size));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        WriteIco(output, Sizes, images);
        Console.WriteLine($"wrote {output} ({Sizes.Length} sizes, up to {Sizes[^1]}px)");
        if (args.Length > 1) WritePreview(args[1], new[] { 16, 32, 64, 128, 256 });
        return 0;
    }

    /// <summary>Side-by-side render at a few sizes so the shapes can be eyeballed.</summary>
    private static void WritePreview(string path, int[] sizes)
    {
        int height = 280, gap = 24, x = gap;
        using var sheet = new Bitmap(gap * (sizes.Length + 1) + sizes.Length * 256, height);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (int size in sizes)
            {
                using var icon = new Bitmap(Image.FromStream(new MemoryStream(Render(size))));
                g.DrawImage(icon, x, height - size - gap, size, size);
                x += size + gap;
            }
        }
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine("wrote " + path);
    }

    private static byte[] Render(int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            float pad = size * 0.055f;
            float radius = size * 0.24f;
            var tile = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            using (var path = RoundedRect(tile, radius))
            using (var brush = new LinearGradientBrush(tile, Color.FromArgb(255, 122, 158, 104), Color.FromArgb(255, 74, 104, 66), 90f))
                g.FillPath(brush, path);

            // Check mark: three points, round caps, thick enough to survive downscaling.
            float w = Math.Max(1.6f, size * 0.115f);
            using var pen = new Pen(Color.White, w)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLines(pen, new[]
            {
                new PointF(size * 0.27f, size * 0.52f),
                new PointF(size * 0.43f, size * 0.68f),
                new PointF(size * 0.74f, size * 0.33f)
            });
        }
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>ICO container with PNG-compressed entries (supported since Vista).</summary>
    private static void WriteIco(string path, int[] sizes, List<byte[]> images)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (byte[] image in images) writer.Write(image);
    }
}
