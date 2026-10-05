using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NeonMon.UI;

internal static class TrayIcon
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    public static Icon Create()
    {
        using var stream = new MemoryStream(CreateIcoBytes());
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    // NeonMon.ico (the executable icon) is generated from this drawing with --export-icon.
    public static void Export(string path) => File.WriteAllBytes(path, CreateIcoBytes());

    // Builds a multi-size ICO (PNG frames) with the pulse glyph used by the strips.
    private static byte[] CreateIcoBytes()
    {
        var frames = Sizes.Select(size => (Size: size, Png: DrawFrame(size))).ToList();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Count);
            var offset = 6 + 16 * frames.Count;
            foreach (var frame in frames)
            {
                writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
                writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frame.Png.Length);
                writer.Write(offset);
                offset += frame.Png.Length;
            }

            foreach (var frame in frames)
            {
                writer.Write(frame.Png);
            }
        }

        return stream.ToArray();
    }

    private static byte[] DrawFrame(int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = size / 16f;
            var radius = 3.5f * scale;
            var borderWidth = Math.Max(1f, 0.8f * scale);
            using var path = new GraphicsPath();
            var inset = borderWidth / 2f;
            var bounds = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            path.AddArc(bounds.X, bounds.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(bounds.Right - radius * 2, bounds.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(bounds.Right - radius * 2, bounds.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            using var background = new SolidBrush(Color.FromArgb(255, 7, 16, 21));
            using var border = new Pen(Color.FromArgb(150, 49, 247, 210), borderWidth);
            graphics.FillPath(background, path);
            graphics.DrawPath(border, path);

            var center = size / 2f;
            var points = new[]
            {
                new PointF(center - 5.5f * scale, center),
                new PointF(center - 2.5f * scale, center),
                new PointF(center - 1f * scale, center - 3.5f * scale),
                new PointF(center + 1f * scale, center + 3.5f * scale),
                new PointF(center + 2.5f * scale, center),
                new PointF(center + 5.5f * scale, center)
            };
            using var pulse = new Pen(Color.FromArgb(49, 247, 210), Math.Max(1.2f, 1.4f * scale)) { LineJoin = LineJoin.Round };
            graphics.DrawLines(pulse, points);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
