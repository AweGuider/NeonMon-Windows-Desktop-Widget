using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NeonMon.UI;

internal enum StatusLineChoice
{
    Cancel,
    Connect,
    CopyInstead
}

// Asks before NeonMon edits Claude's settings.json, and shows the status line it would replace.
internal sealed class StatusLineConfirmForm : Form
{
    private const int LogicalWidth = 460;
    private const int EdgePadding = 16;

    private static readonly Color Background = Color.FromArgb(7, 16, 21);
    private static readonly Color Foreground = Color.FromArgb(224, 246, 249);
    private static readonly Color Muted = Color.FromArgb(104, 147, 157);
    private static readonly Color Cyan = Color.FromArgb(49, 247, 210);
    private static readonly Color Ice = Color.FromArgb(117, 241, 255);
    private static readonly Color Warning = Color.FromArgb(255, 173, 84);

    private readonly string _settingsPath;
    private readonly string? _replaced;
    private readonly List<(RectangleF Area, StatusLineChoice Choice)> _buttons = [];
    private Font _titleFont = null!;
    private Font _bodyFont = null!;
    private Font _codeFont = null!;
    private RectangleF _hovered;

    public StatusLineConfirmForm(string settingsPath, string? replaced, Icon icon)
    {
        _settingsPath = settingsPath;
        _replaced = replaced;
        Text = "Connect Claude status line";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Background;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        CreateFonts();
        ClientSize = new Size(Px(LogicalWidth), Px(replaced is null ? 136 : 186));
    }

    internal StatusLineChoice Choice { get; private set; } = StatusLineChoice.Cancel;

    internal void SavePreview(string path)
    {
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Background);
            OnPaint(new PaintEventArgs(graphics, ClientRectangle));
        }

        bitmap.Save(path);
    }

    private int Px(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);

    private Font CreateFont(string family, float points) => new(family, points * DeviceDpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);

    private void CreateFonts()
    {
        _titleFont?.Dispose();
        _bodyFont?.Dispose();
        _codeFont?.Dispose();
        _titleFont = CreateFont("Segoe UI", 10.5f);
        _bodyFont = CreateFont("Segoe UI", 8.5f);
        _codeFont = CreateFont("Consolas", 8.5f);
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        base.OnDpiChanged(args);
        CreateFonts();
        ClientSize = new Size(Px(LogicalWidth), Px(_replaced is null ? 136 : 186));
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        _buttons.Clear();

        var left = Px(EdgePadding);
        var width = ClientSize.Width - 2 * left;
        var y = (float)Px(EdgePadding - 2);
        DrawText(graphics, "Connect Claude status line", _titleFont, Foreground, new RectangleF(left, y, width, Px(22)));
        y += Px(26);
        var body = $"NeonMon will add a statusLine entry to {ShortPath(_settingsPath)}. A copy is saved as settings.json.neonmon-backup first.";
        DrawText(graphics, body, _bodyFont, Muted, new RectangleF(left, y, width, Px(34)), wrap: true);
        y += Px(40);

        if (_replaced is not null)
        {
            using (var line = new SolidBrush(Warning))
            {
                graphics.FillRectangle(line, left, y, Px(2), Px(36));
            }

            DrawText(graphics, "You already have a status line. It will be replaced:", _bodyFont, Foreground,
                new RectangleF(left + Px(10), y, width - Px(10), Px(17)));
            DrawText(graphics, _replaced, _codeFont, Muted, new RectangleF(left + Px(10), y + Px(19), width - Px(10), Px(17)));
            y += Px(50);
        }

        var right = (float)(ClientSize.Width - left);
        var centerY = y + Px(12);
        right = DrawButton(graphics, "Connect", StatusLineChoice.Connect, Cyan, right, centerY) - Px(8);
        right = DrawButton(graphics, "Cancel", StatusLineChoice.Cancel, Ice, right, centerY) - Px(8);
        DrawButton(graphics, "Copy setting instead", StatusLineChoice.CopyInstead, Ice, right, centerY);
    }

    private float DrawButton(Graphics graphics, string text, StatusLineChoice choice, Color color, float right, float centerY)
    {
        var height = Px(24);
        var width = graphics.MeasureString(text, _bodyFont, PointF.Empty, StringFormat.GenericTypographic).Width + Px(22);
        var area = new RectangleF(right - width, centerY - height / 2f, width, height);
        using (var path = RoundedRectangle(area, Px(6)))
        {
            if (area == _hovered)
            {
                using var fill = new SolidBrush(Color.FromArgb(22, Cyan));
                graphics.FillPath(fill, path);
            }

            using var border = new Pen(choice == StatusLineChoice.Connect ? Cyan : Color.FromArgb(110, 75, 226, 246));
            graphics.DrawPath(border, path);
        }

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        using var brush = new SolidBrush(color);
        graphics.DrawString(text, _bodyFont, brush, area, format);
        _buttons.Add((area, choice));
        return area.X;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var hovered = _buttons.FirstOrDefault(button => button.Area.Contains(args.Location)).Area;
        Cursor = hovered.IsEmpty ? Cursors.Default : Cursors.Hand;
        if (hovered != _hovered)
        {
            _hovered = hovered;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs args)
    {
        base.OnMouseClick(args);
        foreach (var (area, choice) in _buttons)
        {
            if (args.Button == MouseButtons.Left && area.Contains(args.Location))
            {
                Choice = choice;
                Close();
                return;
            }
        }
    }

    private static string ShortPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..].Replace('\\', '/') : path;
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, RectangleF bounds, bool wrap = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = wrap ? 0 : StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
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
            _titleFont?.Dispose();
            _bodyFont?.Dispose();
            _codeFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
