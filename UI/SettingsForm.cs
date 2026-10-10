using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NeonMon.UI;

internal sealed record SettingsPage(string Title, IReadOnlyList<SettingsRow> Rows);

internal abstract record SettingsRow(string Label, string? Hint = null);

internal sealed record SectionRow(string Label, string? Hint = null) : SettingsRow(Label, Hint);

internal sealed record ToggleRow(string Label, Func<bool> Get, Action<bool> Set, string? Hint = null) : SettingsRow(Label, Hint);

internal sealed record ChoiceRow(string Label, IReadOnlyList<string> Options, Func<int> Get, Action<int> Set, string? Hint = null)
    : SettingsRow(Label, Hint);

internal sealed record ActionRow(string Label, Func<string> Value, string Button, Action Click, string? Hint = null,
    Image? ButtonIcon = null, Image? HintIcon = null) : SettingsRow(Label, Hint);

internal sealed class SettingsForm : Form
{
    private const int LogicalWidth = 640;
    private const int LogicalHeight = 560;
    private const int NavWidth = 150;
    private const int PagePadding = 18;
    private const int SectionHeight = 28;
    private const int RowHeight = 32;
    private const int HintHeight = 15;

    private static readonly Color Background = Color.FromArgb(7, 16, 21);
    private static readonly Color Foreground = Color.FromArgb(224, 246, 249);
    private static readonly Color Muted = Color.FromArgb(104, 147, 157);
    private static readonly Color Cyan = Color.FromArgb(49, 247, 210);
    private static readonly Color Ice = Color.FromArgb(117, 241, 255);
    private static readonly Color Line = Color.FromArgb(28, 75, 226, 246);
    private static readonly Color ControlBorder = Color.FromArgb(70, 75, 226, 246);

    private readonly Func<IReadOnlyList<SettingsPage>> _pages;
    private readonly List<(Rectangle Area, Action Click)> _hitAreas = [];
    private int _pageIndex;
    private float _scroll;
    private RectangleF _track;
    private RectangleF _thumb;
    private bool _draggingThumb;
    private float _dragStartY;
    private float _dragStartScroll;
    private Font _labelFont = null!;
    private Font _hintFont = null!;
    private Font _sectionFont = null!;
    private Font _valueFont = null!;

    public SettingsForm(Func<IReadOnlyList<SettingsPage>> pages, Icon icon)
    {
        _pages = pages;
        Text = "NeonMon settings";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        CreateFonts();
        ClientSize = new Size(Px(LogicalWidth), Px(LogicalHeight));
    }

    internal void SavePreview(string path, int page)
    {
        _pageIndex = page;
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Background);
            OnPaint(new PaintEventArgs(graphics, ClientRectangle));
        }

        bitmap.Save(path);
    }

    // Set while a row records a key combination; it receives each key press and returns true when it used it.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Keys, bool>? KeyCapture { get; set; }

    // Runs before Alt combinations reach the system menu, so they can be recorded too.
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (KeyCapture?.Invoke(keyData) == true)
        {
            Invalidate();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private float DpiScale => DeviceDpi / 96f;

    private int Px(float logical) => (int)Math.Round(logical * DpiScale);

    private Font CreateFont(string family, float points, FontStyle style) =>
        new(family, points * DeviceDpi / 72f, style, GraphicsUnit.Pixel);

    private void CreateFonts()
    {
        _labelFont?.Dispose();
        _hintFont?.Dispose();
        _sectionFont?.Dispose();
        _valueFont?.Dispose();
        _labelFont = CreateFont("Segoe UI", 9.5f, FontStyle.Regular);
        _hintFont = CreateFont("Segoe UI", 8f, FontStyle.Regular);
        _sectionFont = CreateFont("Segoe UI", 7.5f, FontStyle.Bold);
        _valueFont = CreateFont("Consolas", 8.5f, FontStyle.Regular);
    }

    private float ContentHeight(SettingsPage page) => Px(page.Rows.Sum(RowLogicalHeight) + 2 * PagePadding);

    private static int RowLogicalHeight(SettingsRow row) => row switch
    {
        SectionRow => SectionHeight + (row.Hint is null ? 0 : HintHeight),
        _ => RowHeight + (row.Hint is null ? 0 : HintHeight)
    };

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        var dark = 1;
        DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        base.OnDpiChanged(args);
        CreateFonts();
        ClientSize = new Size(Px(LogicalWidth), Px(LogicalHeight));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        _hitAreas.Clear();

        var pages = _pages();
        _pageIndex = Math.Clamp(_pageIndex, 0, pages.Count - 1);
        var page = pages[_pageIndex];
        var contentHeight = ContentHeight(page);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, contentHeight - ClientSize.Height));

        DrawNavigation(graphics, pages);
        graphics.SetClip(new RectangleF(Px(NavWidth) + 1, 0, ClientSize.Width, ClientSize.Height));
        var y = Px(PagePadding) - _scroll;
        foreach (var row in page.Rows)
        {
            var height = Px(RowLogicalHeight(row));
            if (y + height > 0 && y < ClientSize.Height)
            {
                DrawRow(graphics, row, y);
            }

            y += height;
        }

        graphics.ResetClip();
        DrawScrollBar(graphics, contentHeight);
    }

    // The page area scrolls when it is taller than the window; the navigation stays put.
    private void DrawScrollBar(Graphics graphics, float contentHeight)
    {
        var visible = (float)ClientSize.Height;
        if (contentHeight <= visible)
        {
            _track = _thumb = RectangleF.Empty;
            return;
        }

        _track = new RectangleF(ClientSize.Width - Px(9), Px(8), Px(5), visible - Px(16));
        var thumbHeight = Math.Max(Px(28), _track.Height * visible / contentHeight);
        var thumbTop = _track.Y + (_track.Height - thumbHeight) * _scroll / (contentHeight - visible);
        _thumb = new RectangleF(_track.X, thumbTop, _track.Width, thumbHeight);
        using (var trackPath = RoundedRectangle(_track, _track.Width / 2))
        using (var trackBrush = new SolidBrush(Color.FromArgb(16, Cyan)))
        {
            graphics.FillPath(trackBrush, trackPath);
        }

        using var thumbPath = RoundedRectangle(_thumb, _thumb.Width / 2);
        using var thumbBrush = new SolidBrush(Color.FromArgb(_draggingThumb ? 150 : 90, Cyan));
        graphics.FillPath(thumbBrush, thumbPath);
    }

    private void ScrollTo(float scroll)
    {
        _scroll = scroll;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs args)
    {
        base.OnMouseWheel(args);
        ScrollTo(_scroll - args.Delta / 120f * Px(RowHeight * 2));
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left || !_track.Contains(args.Location.X, args.Location.Y))
        {
            return;
        }

        if (_thumb.Contains(args.Location.X, args.Location.Y))
        {
            _draggingThumb = true;
            _dragStartY = args.Y;
            _dragStartScroll = _scroll;
            Capture = true;
            Invalidate();
        }
        else
        {
            ScrollTo(_scroll + (args.Y < _thumb.Y ? -1 : 1) * ClientSize.Height * 0.8f);
        }
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (_draggingThumb)
        {
            _draggingThumb = false;
            Capture = false;
            Invalidate();
        }
    }

    private void DrawNavigation(Graphics graphics, IReadOnlyList<SettingsPage> pages)
    {
        using (var line = new Pen(Line))
        {
            graphics.DrawLine(line, Px(NavWidth), 0, Px(NavWidth), ClientSize.Height);
        }

        for (var i = 0; i < pages.Count; i++)
        {
            var index = i;
            var item = new RectangleF(Px(8), Px(PagePadding - 6 + i * 34), Px(NavWidth - 16), Px(30));
            var selected = i == _pageIndex;
            if (selected)
            {
                using var fill = new SolidBrush(Color.FromArgb(26, Cyan));
                using var path = RoundedRectangle(item, Px(6));
                graphics.FillPath(fill, path);
                using var accent = new SolidBrush(Cyan);
                graphics.FillRectangle(accent, item.X, item.Y + Px(6), Px(2), item.Height - Px(12));
            }

            DrawText(graphics, pages[i].Title, _labelFont, selected ? Foreground : Muted,
                new RectangleF(item.X + Px(12), item.Y, item.Width - Px(12), item.Height), StringAlignment.Center);
            _hitAreas.Add((Rectangle.Round(item), () =>
            {
                _pageIndex = index;
                _scroll = 0;
            }));
        }
    }

    private void DrawRow(Graphics graphics, SettingsRow row, float y)
    {
        var left = Px(NavWidth + PagePadding);
        var right = ClientSize.Width - Px(PagePadding);
        if (row is SectionRow)
        {
            DrawText(graphics, row.Label.ToUpperInvariant(), _sectionFont, Muted,
                new RectangleF(left, y + Px(8), right - left, Px(SectionHeight - 8)), StringAlignment.Near);
            if (row.Hint is not null)
            {
                DrawText(graphics, row.Hint, _hintFont, Muted, new RectangleF(left, y + Px(SectionHeight) - Px(2), right - left, Px(HintHeight)),
                    StringAlignment.Near);
            }

            return;
        }

        var line = new RectangleF(left, y, right - left, Px(RowHeight));
        DrawText(graphics, row.Label, _labelFont, Foreground, line, StringAlignment.Center);
        if (row.Hint is not null)
        {
            var hintTop = y + Px(RowHeight) - Px(6);
            DrawText(graphics, row.Hint, _hintFont, Muted, new RectangleF(left, hintTop, right - left, Px(HintHeight)), StringAlignment.Near);
            if (row is ActionRow { HintIcon: { } hintIcon })
            {
                var size = Px(13);
                var x = left + MeasureText(graphics, row.Hint, _hintFont) + Px(5);
                DrawIcon(graphics, hintIcon, new RectangleF(x, hintTop + (Px(HintHeight) - size) / 2f - Px(1), size, size));
            }
        }

        var centerY = y + Px(RowHeight) / 2f;
        switch (row)
        {
            case ToggleRow toggle:
                DrawToggle(graphics, toggle, right, centerY);
                break;
            case ChoiceRow choice:
                DrawChoice(graphics, choice, right, centerY);
                break;
            case ActionRow action:
                DrawAction(graphics, action, right, centerY);
                break;
        }
    }

    private void DrawToggle(Graphics graphics, ToggleRow toggle, float right, float centerY)
    {
        var on = toggle.Get();
        var track = new RectangleF(right - Px(32), centerY - Px(8.5f), Px(32), Px(17));
        using (var path = RoundedRectangle(track, track.Height / 2))
        using (var fill = new SolidBrush(on ? Cyan : Color.FromArgb(128, 91, 117, 126)))
        {
            graphics.FillPath(fill, path);
        }

        var knob = Px(13);
        using (var brush = new SolidBrush(on ? Background : Foreground))
        {
            graphics.FillEllipse(brush, on ? track.Right - knob - Px(2) : track.X + Px(2), track.Y + Px(2), knob, knob);
        }

        _hitAreas.Add((Rectangle.Round(track), () => toggle.Set(!on)));
    }

    private void DrawChoice(Graphics graphics, ChoiceRow choice, float right, float centerY)
    {
        var widths = choice.Options.Select(option => MeasureText(graphics, option, _hintFont) + Px(20)).ToArray();
        var height = Px(22);
        var x = right - widths.Sum();
        var outline = new RectangleF(x, centerY - height / 2f, widths.Sum(), height);
        var selected = choice.Get();
        for (var i = 0; i < choice.Options.Count; i++)
        {
            var index = i;
            var segment = new RectangleF(x, outline.Y, widths[i], height);
            if (i == selected)
            {
                using var fill = new SolidBrush(Color.FromArgb(36, Cyan));
                graphics.FillRectangle(fill, segment);
            }

            DrawText(graphics, choice.Options[i], _hintFont, i == selected ? Foreground : Muted, segment, StringAlignment.Center, centered: true);
            _hitAreas.Add((Rectangle.Round(segment), () => choice.Set(index)));
            x += widths[i];
        }

        using var path = RoundedRectangle(outline, Px(6));
        using var border = new Pen(ControlBorder);
        graphics.DrawPath(border, path);
    }

    private void DrawAction(Graphics graphics, ActionRow action, float right, float centerY)
    {
        var height = Px(22);
        var iconSize = Px(15);
        var iconSpace = action.ButtonIcon is null ? 0 : iconSize + Px(5);
        var buttonWidth = MeasureText(graphics, action.Button, _hintFont) + iconSpace + Px(20);
        var button = new RectangleF(right - buttonWidth, centerY - height / 2f, buttonWidth, height);
        using (var path = RoundedRectangle(button, Px(6)))
        using (var border = new Pen(Color.FromArgb(110, 75, 226, 246)))
        {
            graphics.DrawPath(border, path);
        }

        if (action.ButtonIcon is not null)
        {
            DrawIcon(graphics, action.ButtonIcon, new RectangleF(button.X + Px(10), centerY - iconSize / 2f, iconSize, iconSize));
        }

        DrawText(graphics, action.Button, _hintFont, Ice, new RectangleF(button.X + iconSpace, button.Y, button.Width - iconSpace, height),
            StringAlignment.Center, centered: true);
        _hitAreas.Add((Rectangle.Round(button), action.Click));

        var valueWidth = Px(210);
        DrawText(graphics, action.Value(), _valueFont, Foreground,
            new RectangleF(button.X - Px(8) - valueWidth, button.Y, valueWidth, height), StringAlignment.Center, alignRight: true);
    }

    private static void DrawIcon(Graphics graphics, Image icon, RectangleF bounds)
    {
        var previous = graphics.InterpolationMode;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(icon, bounds);
        graphics.InterpolationMode = previous;
    }

    private static float MeasureText(Graphics graphics, string text, Font font) =>
        graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    private static void DrawText(Graphics graphics, string text, Font font, Color color, RectangleF bounds, StringAlignment vertical,
        bool centered = false, bool alignRight = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            LineAlignment = vertical,
            Alignment = centered ? StringAlignment.Center : alignRight ? StringAlignment.Far : StringAlignment.Near,
            Trimming = alignRight ? StringTrimming.EllipsisPath : StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
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

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (_draggingThumb)
        {
            var range = _track.Height - _thumb.Height;
            var contentRange = ContentHeight(_pages()[_pageIndex]) - ClientSize.Height;
            ScrollTo(_dragStartScroll + (args.Y - _dragStartY) * (range > 0 ? contentRange / range : 0));
            return;
        }

        Cursor = _hitAreas.Any(hit => hit.Area.Contains(args.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs args)
    {
        base.OnMouseClick(args);
        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        foreach (var hit in _hitAreas)
        {
            if (hit.Area.Contains(args.Location))
            {
                hit.Click();
                Invalidate();
                return;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont?.Dispose();
            _hintFont?.Dispose();
            _sectionFont?.Dispose();
            _valueFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
