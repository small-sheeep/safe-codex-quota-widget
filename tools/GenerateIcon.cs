using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class GenerateIcon
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: GenerateIcon.exe <output.ico> <preview.png>");
            return 2;
        }

        using (Bitmap master = DrawMaster(1024))
        {
            using (Bitmap preview = Resize(master, 512))
                preview.Save(args[1], ImageFormat.Png);

            int[] sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            List<byte[]> frames = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (Bitmap image = Resize(master, size))
                using (MemoryStream stream = new MemoryStream())
                {
                    image.Save(stream, ImageFormat.Png);
                    frames.Add(stream.ToArray());
                }
            }
            WriteIcon(args[0], sizes, frames);
        }
        return 0;
    }

    private static Bitmap DrawMaster(int size)
    {
        Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            RectangleF tile = new RectangleF(48, 48, 928, 928);
            using (GraphicsPath tilePath = RoundedRectangle(tile, 232))
            using (LinearGradientBrush tileFill = new LinearGradientBrush(
                tile,
                Color.FromArgb(255, 12, 25, 43),
                Color.FromArgb(255, 55, 30, 78),
                35F))
            {
                ColorBlend blend = new ColorBlend();
                blend.Colors = new[]
                {
                    Color.FromArgb(255, 12, 25, 43),
                    Color.FromArgb(255, 13, 62, 68),
                    Color.FromArgb(255, 55, 30, 78)
                };
                blend.Positions = new[] { 0F, 0.58F, 1F };
                tileFill.InterpolationColors = blend;
                graphics.FillPath(tileFill, tilePath);
                using (Pen edge = new Pen(Color.FromArgb(185, 112, 231, 207), 18F))
                {
                    edge.Alignment = PenAlignment.Inset;
                    graphics.DrawPath(edge, tilePath);
                }
            }

            RectangleF ring = new RectangleF(224, 224, 576, 576);
            using (Pen track = new Pen(Color.FromArgb(230, 48, 64, 91), 92F))
            {
                track.StartCap = LineCap.Round;
                track.EndCap = LineCap.Round;
                graphics.DrawArc(track, ring, -68F, 292F);
            }

            using (LinearGradientBrush progressFill = new LinearGradientBrush(
                ring,
                Color.FromArgb(255, 86, 229, 181),
                Color.FromArgb(255, 109, 105, 255),
                35F))
            {
                ColorBlend progressBlend = new ColorBlend();
                progressBlend.Colors = new[]
                {
                    Color.FromArgb(255, 86, 229, 181),
                    Color.FromArgb(255, 78, 204, 244),
                    Color.FromArgb(255, 137, 105, 255)
                };
                progressBlend.Positions = new[] { 0F, 0.58F, 1F };
                progressFill.InterpolationColors = progressBlend;
                using (Pen progress = new Pen(progressFill, 92F))
                {
                    progress.StartCap = LineCap.Round;
                    progress.EndCap = LineCap.Round;
                    graphics.DrawArc(progress, ring, -68F, 254F);
                }
            }

            using (SolidBrush ledGlow = new SolidBrush(Color.FromArgb(72, 94, 238, 184)))
                graphics.FillEllipse(ledGlow, 188, 172, 126, 126);
            using (SolidBrush led = new SolidBrush(Color.FromArgb(255, 96, 237, 184)))
                graphics.FillEllipse(led, 219, 203, 64, 64);
            using (Pen ledEdge = new Pen(Color.FromArgb(235, 211, 255, 238), 10F))
                graphics.DrawEllipse(ledEdge, 219, 203, 64, 64);

            PointF[] bolt = new[]
            {
                new PointF(553, 326),
                new PointF(400, 542),
                new PointF(496, 542),
                new PointF(454, 706),
                new PointF(630, 474),
                new PointF(530, 474)
            };
            using (GraphicsPath boltPath = new GraphicsPath())
            {
                boltPath.AddPolygon(bolt);
                using (SolidBrush boltGlow = new SolidBrush(Color.FromArgb(72, 92, 213, 255)))
                using (Pen glowPen = new Pen(boltGlow, 34F))
                {
                    glowPen.LineJoin = LineJoin.Round;
                    graphics.DrawPath(glowPen, boltPath);
                }
                using (LinearGradientBrush boltFill = new LinearGradientBrush(
                    new RectangleF(400, 326, 230, 380),
                    Color.White,
                    Color.FromArgb(255, 196, 239, 255),
                    90F))
                    graphics.FillPath(boltFill, boltPath);
            }
        }
        return bitmap;
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        Bitmap output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(output))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        return output;
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2F;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void WriteIcon(string path, int[] sizes, IList<byte[]> frames)
    {
        using (FileStream stream = File.Create(path))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)frames[i].Length);
                writer.Write((uint)offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
    }
}
