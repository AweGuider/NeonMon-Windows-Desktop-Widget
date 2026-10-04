using System.ComponentModel;
using System.Drawing.Drawing2D;
using NeonMon.Models;

namespace NeonMon.UI;

internal abstract class WidgetForm : Form
{
    protected static readonly Color BackgroundTop = Color.FromArgb(246, 7, 16, 21);
    protected static readonly Color BackgroundBottom = Color.FromArgb(250, 5, 11, 15);
    protected static readonly Color Cyan = Color.FromArgb(49, 247, 210);
    protected static readonly Color Ice = Color.FromArgb(117, 241, 255);
    protected static readonly Color Foreground = Color.FromArgb(224, 246, 249);
    protected static readonly Color Muted = Color.FromArgb(104, 147, 157);
    protected static readonly Color Track = Color.FromArgb(30, 91, 117, 126);
    protected static readonly Color Warning = Color.FromArgb(255, 173, 84);

    protected readonly Font LabelFont = new("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
    protected readonly Font DetailFont = new("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
    protected readonly Font ValueFont = new("Consolas", 13.5f, FontStyle.Regular, GraphicsUnit.Point);
    protected readonly Font UptimeFont = new("Consolas", 17f, FontStyle.Regular, GraphicsUnit.Point);
    protected readonly Font HeaderFont = new("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point);
    protected readonly Font IconFont = new("Segoe UI Symbol", 10.5f, FontStyle.Bold, GraphicsUnit.Point);

    private readonly Action _saveSettings;
    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly System.Windows.Forms.Timer _collapseTimer;
    private readonly System.Windows.Forms.Timer _hoverTimer;
    private readonly ToolTip _toolTip;
    private readonly Dictionary<WidgetSize, Rectangle> _sizeHitAreas = [];
    private Rectangle _dockHitArea;
    private Rectangle _pinHitArea;
    private Rectangle _hideHitArea;
    private Rectangle _exitHitArea;
    private string? _hoveredTooltip;
    private bool _tooltipVisible;
    private long _tooltipHoverStarted;
    private RevealState _state = RevealState.Hidden;
    private Rectangle _animationStart;
    private Rectangle _animationTarget;
    private long _animationStarted;
    private bool _mouseDown;
    private bool _dragging;
    private Point _mouseDownScreen;
    private double _dragStartOffset;
    private Screen _dockScreen;
    private bool _exiting;

    protected WidgetForm(StripSettings settings, Action saveSettings)
    {
        Settings = settings;
        _saveSettings = saveSettings;
        _dockScreen = Screen.PrimaryScreen ?? Screen.AllScreens[0];

        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(7, 16, 21);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Opacity = 0.94;
        DoubleBuffered = true;
        MinimumSize = new Size(6, 6);

        _animationTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animationTimer.Tick += (_, _) => AdvanceAnimation();
        _collapseTimer = new System.Windows.Forms.Timer { Interval = 850 };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Settings.KeepOpen && !GetHoverBounds().Contains(Cursor.Position) && !IsMenuVisible)
            {
                SetRevealState(RevealState.Hidden);
            }
        };

        _toolTip = new ToolTip
        {
            OwnerDraw = true,
            InitialDelay = 400,
            ReshowDelay = 80,
            AutoPopDelay = 4500,
            ShowAlways = true
        };
        _toolTip.Popup += (_, args) =>
        {
            var size = TextRenderer.MeasureText(_hoveredTooltip ?? string.Empty, DetailFont);
            args.ToolTipSize = new Size(size.Width + 18, size.Height + 10);
        };
        _toolTip.Draw += (_, args) =>
        {
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(248, 5, 14, 19));
            using var border = new Pen(Color.FromArgb(150, Cyan));
            args.Graphics.FillRectangle(background, args.Bounds);
            args.Graphics.DrawRectangle(border, 0, 0, args.Bounds.Width - 1, args.Bounds.Height - 1);
            TextRenderer.DrawText(args.Graphics, args.ToolTipText, DetailFont, new Point(9, 5), Foreground, TextFormatFlags.NoPadding);
        };

        _hoverTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _hoverTimer.Tick += (_, _) => EvaluatePointerState();

        MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_state == RevealState.Hidden && CanRevealNow())
            {
                SetRevealState(RevealState.Peek);
            }
        };
        MouseLeave += (_, _) => HideTooltip();
        MouseDown += HandleMouseDown;
        MouseMove += HandleMouseMove;
        MouseUp += HandleMouseUp;
        Load += (_, _) =>
        {
            Bounds = CalculateBounds(RevealState.Hidden);
            ApplyWindowRegion();
        };
        Shown += (_, _) => _hoverTimer.Start();
    }

    internal event Action<WidgetForm, RevealState>? RevealStateChanged;
    internal event Action<WidgetForm>? LayoutCommitted;
    internal event Action? ExitRequested;
    internal event Action<string>? NoticeRequested;

    internal StripSettings Settings { get; }
    internal RevealState State => _state;
    internal Screen DockScreen => _dockScreen;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<WidgetForm, bool>? CanReveal { get; set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int toolWindow = 0x00000080;
            const int noActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= toolWindow | noActivate;
            return parameters;
        }
    }

    protected bool IsHorizontal => Settings.DockEdge is DockEdge.Top or DockEdge.Bottom;

    private bool IsMenuVisible => ContextMenuStrip?.Visible == true;

    protected abstract string Title { get; }

    protected virtual Size GetLogicalHiddenSize(bool horizontal) => horizontal ? new Size(72, 6) : new Size(6, 72);

    protected virtual Size GetLogicalPeekSize(bool horizontal) => horizontal ? new Size(72, 28) : new Size(28, 72);

    protected virtual Size GetLogicalPeekReserve(bool horizontal) => GetLogicalPeekSize(horizontal);

    protected abstract Size GetLogicalOpenSize(WidgetSize size);

    protected abstract void DrawPeek(Graphics graphics);

    protected abstract void DrawBody(Graphics graphics);

    protected virtual bool HandleBodyClick(Point point) => false;

    protected virtual string? GetBodyTooltip(Point point) => null;

    protected virtual void OnRevealStateChanged(RevealState state)
    {
    }

    protected void ShowNotice(string text) => NoticeRequested?.Invoke(text);

    internal void SavePreview(string path, WidgetSize size = WidgetSize.Large, RevealState state = RevealState.Open)
    {
        _state = state;
        Settings.Size = size;
        Size = GetTargetSize(state);
        _ = Handle;
        ApplyWindowRegion();

        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        DrawToBitmap(bitmap, ClientRectangle);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    internal void PrepareExit() => _exiting = true;

    internal void ToggleOpen() => SetRevealState(_state == RevealState.Open ? RevealState.Hidden : RevealState.Open);

    internal void SetWidgetSize(WidgetSize size)
    {
        Settings.Size = size;
        SaveSettings();
        SetRevealState(RevealState.Open, true);
    }

    internal void SetDockEdge(DockEdge edge)
    {
        Settings.DockEdge = edge;
        _dockScreen = Screen.FromPoint(Cursor.Position);
        LayoutCommitted?.Invoke(this);
        SaveSettings();
        SetRevealState(_state, true);
    }

    internal void SetKeepOpen(bool keepOpen)
    {
        Settings.KeepOpen = keepOpen;
        SaveSettings();
        if (keepOpen)
        {
            SetRevealState(RevealState.Open);
        }

        Invalidate();
    }

    internal void SetRevealState(RevealState state, bool forceAnimation = false)
    {
        if (_state == state && !forceAnimation)
        {
            return;
        }

        _state = state;
        OnRevealStateChanged(state);
        _animationStart = Bounds;
        _animationTarget = CalculateBounds(state);
        _animationStarted = Environment.TickCount64;
        _animationTimer.Start();
        Invalidate();
        RevealStateChanged?.Invoke(this, state);
    }

    internal void AnimateToLayout()
    {
        if (CalculateBounds(_state) != Bounds)
        {
            SetRevealState(_state, true);
        }
    }

    protected void ContentSizeChanged()
    {
        if (!IsHandleCreated || _animationTimer.Enabled)
        {
            Invalidate();
            return;
        }

        Bounds = CalculateBounds(_state);
        ApplyWindowRegion();
        Invalidate();
    }

    internal void ScheduleCollapse()
    {
        if (!Settings.KeepOpen && !IsMenuVisible)
        {
            if (!_collapseTimer.Enabled)
            {
                _collapseTimer.Start();
            }
        }
    }

    internal Rectangle GetReservedHoverBounds(double offset)
    {
        var horizontal = IsHorizontal;
        var hidden = CalculateBounds(ToDevice(GetLogicalHiddenSize(horizontal)), offset);
        var peek = CalculateBounds(ToDevice(GetLogicalPeekReserve(horizontal)), offset);
        var bounds = Rectangle.Union(hidden, peek);
        var margin = HoverMargin();
        bounds.Inflate(margin, margin);
        return bounds;
    }

    private void HandleMouseDown(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        _mouseDown = true;
        _dragging = false;
        _mouseDownScreen = Cursor.Position;
        _dragStartOffset = Settings.DockOffset;
    }

    private void HandleMouseMove(object? sender, MouseEventArgs args)
    {
        if (!_mouseDown)
        {
            return;
        }

        var cursor = Cursor.Position;
        if (!_dragging && Math.Abs(cursor.X - _mouseDownScreen.X) + Math.Abs(cursor.Y - _mouseDownScreen.Y) > 5)
        {
            _dragging = true;
        }

        if (!_dragging)
        {
            return;
        }

        var area = _dockScreen.WorkingArea;
        if (IsHorizontal)
        {
            Settings.DockOffset = Math.Clamp(_dragStartOffset + (cursor.X - _mouseDownScreen.X) / (double)Math.Max(1, area.Width), 0, 1);
        }
        else
        {
            Settings.DockOffset = Math.Clamp(_dragStartOffset + (cursor.Y - _mouseDownScreen.Y) / (double)Math.Max(1, area.Height), 0, 1);
        }

        Bounds = CalculateBounds(_state);
    }

    private void HandleMouseUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        _mouseDown = false;
        if (_dragging)
        {
            _dragging = false;
            LayoutCommitted?.Invoke(this);
            SaveSettings();
            AnimateToLayout();
            return;
        }

        if (_state == RevealState.Open)
        {
            var point = args.Location;
            if (HandleBodyClick(point))
            {
                return;
            }

            if (_exitHitArea.Contains(point))
            {
                ExitRequested?.Invoke();
                return;
            }

            if (_hideHitArea.Contains(point))
            {
                SetRevealState(RevealState.Hidden);
                return;
            }

            if (_dockHitArea.Contains(point))
            {
                CycleDockEdge();
                return;
            }

            if (_pinHitArea.Contains(point))
            {
                Settings.KeepOpen = !Settings.KeepOpen;
                SaveSettings();
                Invalidate();
                return;
            }

            foreach (var hit in _sizeHitAreas)
            {
                if (hit.Value.Contains(point))
                {
                    SetWidgetSize(hit.Key);
                    return;
                }
            }
        }

        ToggleOpen();
    }

    private void CycleDockEdge()
    {
        var next = Settings.DockEdge switch
        {
            DockEdge.Top => DockEdge.Right,
            DockEdge.Right => DockEdge.Bottom,
            DockEdge.Bottom => DockEdge.Left,
            _ => DockEdge.Top
        };
        SetDockEdge(next);
    }

    private bool CanRevealNow() => CanReveal?.Invoke(this) ?? true;

    private void AdvanceAnimation()
    {
        const double durationMs = 145;
        var progress = Math.Clamp((Environment.TickCount64 - _animationStarted) / durationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        Bounds = Lerp(_animationStart, _animationTarget, eased);
        ApplyWindowRegion();
        Invalidate();

        if (progress >= 1)
        {
            _animationTimer.Stop();
            Bounds = _animationTarget;
            ApplyWindowRegion();
        }
    }

    private Rectangle CalculateBounds(RevealState state) => CalculateBounds(GetTargetSize(state), Settings.DockOffset);

    private Rectangle CalculateBounds(Size size, double offset)
    {
        var area = _dockScreen.WorkingArea;
        int x;
        int y;

        if (IsHorizontal)
        {
            x = area.Left + (int)Math.Round((area.Width - size.Width) * offset);
            y = Settings.DockEdge == DockEdge.Top ? area.Top + 1 : area.Bottom - size.Height - 1;
        }
        else
        {
            x = Settings.DockEdge == DockEdge.Left ? area.Left + 1 : area.Right - size.Width - 1;
            y = area.Top + (int)Math.Round((area.Height - size.Height) * offset);
        }

        return new Rectangle(new Point(x, y), size);
    }

    private Size GetTargetSize(RevealState state)
    {
        var horizontal = IsHorizontal;
        return state switch
        {
            RevealState.Hidden => ToDevice(GetLogicalHiddenSize(horizontal)),
            RevealState.Peek => ToDevice(GetLogicalPeekSize(horizontal)),
            _ => ToDevice(GetLogicalOpenSize(Settings.Size))
        };
    }

    private Size ToDevice(Size size)
    {
        var scale = DeviceDpi / 96f;
        return new((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
    }

    private static Rectangle Lerp(Rectangle from, Rectangle to, double amount) => new(
        (int)Math.Round(from.X + (to.X - from.X) * amount),
        (int)Math.Round(from.Y + (to.Y - from.Y) * amount),
        (int)Math.Round(from.Width + (to.Width - from.Width) * amount),
        (int)Math.Round(from.Height + (to.Height - from.Height) * amount));

    private int HoverMargin() => Math.Max(6, (int)Math.Round(8 * DeviceDpi / 96f));

    private Rectangle GetHoverBounds()
    {
        var margin = HoverMargin();
        var bounds = Bounds;
        bounds.Inflate(margin, margin);
        return bounds;
    }

    private void EvaluatePointerState()
    {
        if (!Visible || _dragging)
        {
            return;
        }

        var inside = GetHoverBounds().Contains(Cursor.Position);
        if (_state == RevealState.Hidden && inside)
        {
            if (CanRevealNow())
            {
                _collapseTimer.Stop();
                SetRevealState(RevealState.Peek);
            }
        }
        else if (_state == RevealState.Peek && !inside)
        {
            HideTooltip();
            SetRevealState(RevealState.Hidden);
        }
        else if (_state == RevealState.Open)
        {
            if (inside)
            {
                _collapseTimer.Stop();
            }
            else
            {
                ScheduleCollapse();
            }
        }

        UpdateTooltip(inside && _state == RevealState.Open ? PointToClient(Cursor.Position) : null);
    }

    private void UpdateTooltip(Point? point)
    {
        string? text = null;
        if (point is not null)
        {
            text = GetBodyTooltip(point.Value);

            foreach (var hit in _sizeHitAreas)
            {
                if (text is null && hit.Value.Contains(point.Value))
                {
                    text = $"Use the {hit.Key.ToString().ToLowerInvariant()} layout";
                    break;
                }
            }

            if (text is null && _dockHitArea.Contains(point.Value))
            {
                text = $"Docked to {Settings.DockEdge.ToString().ToLowerInvariant()} · click to move";
            }
            else if (text is null && _pinHitArea.Contains(point.Value))
            {
                text = Settings.KeepOpen ? "Unpin and allow auto-hide" : "Pin the widget open";
            }
            else if (text is null && _hideHitArea.Contains(point.Value))
            {
                text = "Minimize to the pulse strip";
            }
            else if (text is null && _exitHitArea.Contains(point.Value))
            {
                text = "Exit NeonMon";
            }
        }

        Cursor = text is null ? Cursors.Default : Cursors.Hand;

        if (text == _hoveredTooltip)
        {
            if (text is not null && !_tooltipVisible && Environment.TickCount64 - _tooltipHoverStarted >= 420)
            {
                _toolTip.Show(text, this, point!.Value.X + 10, (int)Math.Round(28 * DeviceDpi / 96f) + 5, 4000);
                _tooltipVisible = true;
            }
            return;
        }

        HideTooltip();
        _hoveredTooltip = text;
        _tooltipHoverStarted = Environment.TickCount64;
    }

    private void HideTooltip()
    {
        _toolTip.Hide(this);
        _tooltipVisible = false;
        _hoveredTooltip = null;
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        args.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (_state == RevealState.Hidden)
        {
            DrawHidden(args.Graphics);
        }
        else if (_state == RevealState.Peek)
        {
            DrawPeek(args.Graphics);
        }
        else
        {
            DrawBackground(args.Graphics, 13);
            DrawHeader(args.Graphics);
            DrawBody(args.Graphics);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs args)
    {
    }

    protected virtual void DrawHidden(Graphics graphics)
    {
        using var background = new SolidBrush(Color.FromArgb(225, 6, 15, 20));
        graphics.FillRectangle(background, ClientRectangle);
        using var line = new Pen(Cyan, Math.Max(1f, DeviceDpi / 96f));
        if (IsHorizontal)
        {
            var y = Settings.DockEdge == DockEdge.Top ? Height - 1 : 0;
            graphics.DrawLine(line, Width * 0.22f, y, Width * 0.78f, y);
        }
        else
        {
            var x = Settings.DockEdge == DockEdge.Left ? Width - 1 : 0;
            graphics.DrawLine(line, x, Height * 0.22f, x, Height * 0.78f);
        }
    }

    protected void DrawPeekOutline(Graphics graphics)
    {
        DrawBackground(graphics, 8);
        var scale = DeviceDpi / 96f;
        var inset = 2.5f * scale;
        var outlineBounds = new RectangleF(
            inset,
            inset,
            Math.Max(1, Width - 2 * inset - 1),
            Math.Max(1, Height - 2 * inset - 1));
        using var outline = RoundedRectangle(outlineBounds, 7 * scale);
        using var outlineGlow = new Pen(Color.FromArgb(60, Cyan), 3.5f * scale);
        using var outlineLine = new Pen(Color.FromArgb(230, Cyan), Math.Max(1.2f, 1.15f * scale));
        graphics.DrawPath(outlineGlow, outline);
        graphics.DrawPath(outlineLine, outline);
    }

    protected void DrawBackground(Graphics graphics, int radius)
    {
        using var path = RoundedRectangle(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), radius * DeviceDpi / 96f);
        using var background = new LinearGradientBrush(ClientRectangle, BackgroundTop, BackgroundBottom, LinearGradientMode.Vertical);
        graphics.FillPath(background, path);
        using var border = new Pen(Color.FromArgb(80, 75, 226, 246), 1f);
        graphics.DrawPath(border, path);
    }

    private void DrawHeader(Graphics graphics)
    {
        var scale = DeviceDpi / 96f;
        var headerHeight = (int)Math.Round(28 * scale);
        using var linePen = new Pen(Color.FromArgb(30, 75, 226, 246));
        graphics.DrawLine(linePen, 10 * scale, headerHeight, Width - 10 * scale, headerHeight);
        using var liveBrush = new SolidBrush(Cyan);
        graphics.FillEllipse(liveBrush, 12 * scale, 10 * scale, 6 * scale, 6 * scale);
        DrawText(graphics, Title, HeaderFont, Muted, new RectangleF(24 * scale, 6 * scale, 130 * scale, 16 * scale));

        var x = Width - (int)Math.Round(179 * scale);
        _sizeHitAreas.Clear();
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            var rectangle = new Rectangle(x, (int)Math.Round(5 * scale), (int)Math.Round(22 * scale), (int)Math.Round(18 * scale));
            _sizeHitAreas[size] = rectangle;
            using var fill = new SolidBrush(size == Settings.Size ? Color.FromArgb(35, Cyan) : Color.Transparent);
            graphics.FillRectangle(fill, rectangle);
            DrawCenteredText(graphics, size.ToString()[0].ToString(), LabelFont, size == Settings.Size ? Cyan : Muted, rectangle);
            x += (int)Math.Round(24 * scale);
        }

        _dockHitArea = new Rectangle(x + (int)(2 * scale), (int)(5 * scale), (int)(22 * scale), (int)(18 * scale));
        DrawCenteredText(graphics, DockGlyph(), IconFont, Ice, _dockHitArea);
        x += (int)Math.Round(25 * scale);

        _pinHitArea = new Rectangle(x + (int)(2 * scale), (int)(5 * scale), (int)(22 * scale), (int)(18 * scale));
        DrawCenteredText(graphics, Settings.KeepOpen ? "◆" : "◇", DetailFont, Settings.KeepOpen ? Cyan : Muted, _pinHitArea);
        _hideHitArea = new Rectangle(x + (int)(27 * scale), (int)(5 * scale), (int)(22 * scale), (int)(18 * scale));
        DrawCenteredText(graphics, "—", DetailFont, Muted, _hideHitArea);
        _exitHitArea = new Rectangle(x + (int)(52 * scale), (int)(5 * scale), (int)(22 * scale), (int)(18 * scale));
        DrawCenteredText(graphics, "×", IconFont, Warning, _exitHitArea);
    }

    private string DockGlyph() => Settings.DockEdge switch
    {
        DockEdge.Top => "↑",
        DockEdge.Right => "→",
        DockEdge.Bottom => "↓",
        _ => "←"
    };

    protected static float TextLineHeight(Graphics graphics, Font font, float padding) =>
        (float)Math.Ceiling(font.GetHeight(graphics)) + padding;

    protected static void DrawText(Graphics graphics, string value, Font font, Color color, RectangleF bounds)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(value, font, brush, bounds, format);
    }

    protected static void DrawCenteredText(Graphics graphics, string value, Font font, Color color, Rectangle bounds)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(value, font, brush, bounds, format);
    }

    private void ApplyWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var radius = _state == RevealState.Hidden ? 3f : 12f * DeviceDpi / 96f;
        using var path = RoundedRectangle(new Rectangle(0, 0, Width, Height), radius);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        var path = new GraphicsPath();
        if (diameter <= 1)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        var arc = new RectangleF(rectangle.X, rectangle.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void SaveSettings()
    {
        try
        {
            _saveSettings();
        }
        catch
        {
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (!_exiting && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            SetRevealState(RevealState.Hidden);
            return;
        }

        base.OnFormClosing(args);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
            _collapseTimer.Dispose();
            _hoverTimer.Dispose();
            _toolTip.Dispose();
            LabelFont.Dispose();
            DetailFont.Dispose();
            ValueFont.Dispose();
            UptimeFont.Dispose();
            HeaderFont.Dispose();
            IconFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
