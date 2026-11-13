using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NeonMon.UI;

// Shows a monitor's number in its corner, like Identify in Windows display settings. It never takes focus or clicks.
internal sealed class IdentifyOverlay : Form
{
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;

    private static readonly Color Background = Color.FromArgb(7, 16, 21);
    private static readonly Color Cyan = Color.FromArgb(49, 247, 210);
    private static readonly Color Border = Color.FromArgb(70, 75, 226, 246);

    private int _number;
    private Font? _font;

    public IdentifyOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Background;
        DoubleBuffered = true;
        Opacity = 0.94;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
            return parameters;
        }
    }

    // Sized from the monitor's own pixels, so it looks the same on monitors with different DPI.
    internal void ShowOn(Rectangle monitor, int number)
    {
        var side = Math.Max(48, monitor.Height / 5);
        var margin = side / 4;
        _number = number;
        _font?.Dispose();
        _font = new Font("Segoe UI", side * 0.55f, FontStyle.Regular, GraphicsUnit.Pixel);
        Bounds = new Rectangle(monitor.Left + margin, monitor.Bottom - margin - side, side, side);
        using (var path = RoundedSquare(new RectangleF(0, 0, side, side), side / 8f))
        {
            Region = new Region(path);
        }

        if (!Visible)
        {
            Show();
        }

        Invalidate();
    }

    // Moving to a monitor with another DPI must not rescale the window; ShowOn already sized it for that monitor.
    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        args.Cancel = true;
        base.OnDpiChanged(args);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var bounds = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        using (var path = RoundedSquare(RectangleF.Inflate(bounds, -1, -1), bounds.Width / 8f))
        using (var border = new Pen(Border, Math.Max(1, bounds.Width / 80f)))
        {
            graphics.DrawPath(border, path);
        }

        if (_font is null)
        {
            return;
        }

        using var brush = new SolidBrush(Cyan);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(_number.ToString(System.Globalization.CultureInfo.InvariantCulture), _font, brush, bounds, format);
    }

    private static GraphicsPath RoundedSquare(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font?.Dispose();
        }

        base.Dispose(disposing);
    }
}
