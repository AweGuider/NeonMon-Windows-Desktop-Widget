using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NeonMon.Models;

namespace NeonMon.UI;

internal abstract class WidgetForm : Form
{
    private static readonly Color BackgroundTop = Color.FromArgb(7, 16, 21);
    private static readonly Color BackgroundBottom = Color.FromArgb(5, 11, 15);
    protected static readonly Color Cyan = Color.FromArgb(49, 247, 210);
    protected static readonly Color Ice = Color.FromArgb(117, 241, 255);
    protected static readonly Color Foreground = Color.FromArgb(224, 246, 249);
    protected static readonly Color Muted = Color.FromArgb(104, 147, 157);
    protected static readonly Color Track = Color.FromArgb(30, 91, 117, 126);
    protected static readonly Color Warning = Color.FromArgb(255, 173, 84);
    protected static readonly Color Critical = Color.FromArgb(255, 92, 122);
    protected static readonly Color StripTrack = Color.FromArgb(70, 91, 117, 126);
    private static readonly Color HiddenRing = Color.FromArgb(205, 222, 226);
    private static readonly Color HiddenBody = Color.FromArgb(6, 15, 20);

    private const int HiddenHoverInterval = 200;
    private const int PeekRadius = 8;
    private const int OpenRadius = 13;
    private const int RevealedHoverInterval = 80;

    protected Font LabelFont { get; private set; } = null!;
    protected Font DetailFont { get; private set; } = null!;
    protected Font ValueFont { get; private set; } = null!;
    protected Font UptimeFont { get; private set; } = null!;
    protected Font HeaderFont { get; private set; } = null!;
    protected Font IconFont { get; private set; } = null!;

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
    private nint _menuPreviousForeground;
    private bool _renderQueued;
    private LayeredSurface? _surface;
    private (Size, RevealState, int)? _plainKey;
    private int[] _pixels = [];
    private int[] _plainPixels = [];
    private float[] _distance = [];

    protected WidgetForm(StripSettings settings, Action saveSettings)
    {
        Settings = settings;
        _saveSettings = saveSettings;
        _dockScreen = FindScreen(settings.Monitor);
        CreateFonts();

        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(7, 16, 21);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
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

        _hoverTimer = new System.Windows.Forms.Timer { Interval = HiddenHoverInterval };
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
            Invalidate();
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

    // While a group reveal holds the pulse in Peek, leaving the hover zone does not hide it.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool PeekHeld { get; set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int toolWindow = 0x00000080;
            const int layered = 0x00080000;
            const int noActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= toolWindow | layered | noActivate;
            return parameters;
        }
    }

    protected bool IsHorizontal => Settings.DockEdge is DockEdge.Top or DockEdge.Bottom;

    private bool IsMenuVisible => ContextMenuStrip?.Visible == true;

    protected abstract string Title { get; }

    protected const int TwoLineTabThickness = 16;

    protected virtual Size GetLogicalHiddenSize(bool horizontal) => horizontal ? new Size(132, 9) : new Size(9, 132);

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

    // Fonts are sized in pixels for this window's monitor; GDI+ point sizes would follow the primary monitor's DPI.
    protected Font CreateFont(string family, float points, FontStyle style) =>
        new(family, points * DeviceDpi / 72f, style, GraphicsUnit.Pixel);

    protected virtual void CreateFonts()
    {
        DisposeFonts();
        LabelFont = CreateFont("Segoe UI", 7.5f, FontStyle.Regular);
        DetailFont = CreateFont("Segoe UI", 7.5f, FontStyle.Regular);
        ValueFont = CreateFont("Consolas", 13.5f, FontStyle.Regular);
        UptimeFont = CreateFont("Consolas", 17f, FontStyle.Regular);
        HeaderFont = CreateFont("Segoe UI", 7.5f, FontStyle.Bold);
        IconFont = CreateFont("Segoe UI Symbol", 10.5f, FontStyle.Bold);
    }

    private void DisposeFonts()
    {
        LabelFont?.Dispose();
        DetailFont?.Dispose();
        ValueFont?.Dispose();
        UptimeFont?.Dispose();
        HeaderFont?.Dispose();
        IconFont?.Dispose();
    }

    internal void SavePreview(string path, WidgetSize size = WidgetSize.Large, RevealState state = RevealState.Open)
    {
        _state = state;
        Settings.Size = size;
        _ = Handle;
        Size = GetTargetSize(state);
        ApplyWindowRegion();

        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        ComposeFrame(bitmap);
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
        LayoutCommitted?.Invoke(this);
        SaveSettings();
        SetRevealState(_state, true);
    }

    internal void SetMonitor(Screen screen)
    {
        Settings.FollowMouse = false;
        Settings.Monitor = screen.DeviceName;
        MoveToScreen(screen);
        SaveSettings();
    }

    internal void SetFollowMouse(bool followMouse)
    {
        Settings.FollowMouse = followMouse;
        if (!followMouse)
        {
            Settings.Monitor = _dockScreen.DeviceName;
        }

        SaveSettings();
    }

    internal void BringToTop()
    {
        if (IsHandleCreated)
        {
            SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }
    }

    private void MoveToScreen(Screen screen)
    {
        _dockScreen = screen;
        LayoutCommitted?.Invoke(this);
        if (!IsHandleCreated)
        {
            return;
        }

        _animationTimer.Stop();
        Bounds = CalculateBounds(_state);
        ApplyWindowRegion();
        Invalidate();
    }

    private static Screen FindScreen(string? deviceName) =>
        Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == deviceName)
        ?? Screen.PrimaryScreen
        ?? Screen.AllScreens[0];

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
        _hoverTimer.Interval = state == RevealState.Hidden ? HiddenHoverInterval : RevealedHoverInterval;
        if (state != RevealState.Hidden)
        {
            BringToTop();
        }

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

    internal void ReattachToScreen()
    {
        _dockScreen = FindScreen(Settings.FollowMouse ? _dockScreen.DeviceName : Settings.Monitor ?? _dockScreen.DeviceName);
        if (!IsHandleCreated || _animationTimer.Enabled)
        {
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

    // The strips never activate, so their menu cannot see clicks in other apps and would stay open. Activating the
    // menu, as Windows tray menus do, lets any outside click deactivate NeonMon and close it.
    internal void MenuOpened(ToolStripDropDown menu)
    {
        HideTooltip();
        _menuPreviousForeground = GetForegroundWindow();
        SetWindowPos(menu.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        SetForegroundWindow(menu.Handle);
    }

    internal void MenuClosed(ToolStripDropDownCloseReason reason)
    {
        ScheduleCollapse();
        if (reason is ToolStripDropDownCloseReason.AppClicked or ToolStripDropDownCloseReason.AppFocusChange)
        {
            return;
        }

        var previous = _menuPreviousForeground;
        BeginInvoke(() =>
        {
            if (Form.ActiveForm is null && previous != nint.Zero)
            {
                SetForegroundWindow(previous);
            }
        });
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

            return;
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

    // Zones come from the target geometry, never the animated bounds. Peek is often shorter than the hidden tab, so
    // Peek is kept inside the reserved hidden-and-peek area; otherwise a pointer near a tab end would leave Peek the
    // moment it appeared and reveal it again.
    internal bool HoldsPointer(Point point) => _state != RevealState.Hidden && GetHoverBounds().Contains(point);

    private Rectangle GetHoverBounds()
    {
        var margin = HoverMargin();
        var bounds = CalculateBounds(_state);
        bounds.Inflate(margin, margin);
        return _state == RevealState.Peek ? Rectangle.Union(bounds, GetReservedHoverBounds(Settings.DockOffset)) : bounds;
    }

    private void EvaluatePointerState()
    {
        if (!Visible || _dragging)
        {
            return;
        }

        var cursor = Cursor.Position;
        if (_state == RevealState.Hidden && Settings.FollowMouse && !_dockScreen.Bounds.Contains(cursor))
        {
            MoveToScreen(Screen.FromPoint(cursor));
        }

        var inside = GetHoverBounds().Contains(cursor);
        if (_state == RevealState.Hidden && inside)
        {
            if (CanRevealNow())
            {
                _collapseTimer.Stop();
                SetRevealState(RevealState.Peek);
            }
        }
        else if (_state == RevealState.Peek && !inside && !IsMenuVisible && !PeekHeld)
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

        if (_state == RevealState.Open || _hoveredTooltip is not null)
        {
            UpdateTooltip(inside && _state == RevealState.Open && !IsMenuVisible ? PointToClient(Cursor.Position) : null);
        }
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

    // The strip is a per-pixel layered window, so it never paints through WM_PAINT: every invalidation renders a
    // frame for UpdateLayeredWindow. Pixels outside the window region stay fully transparent.
    private void DrawFrame(Graphics graphics, bool backgroundOnly = false)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (Region is not null)
        {
            graphics.Clip = Region;
        }

        if (_state == RevealState.Hidden)
        {
            DrawHidden(graphics);
        }
        else if (backgroundOnly)
        {
            DrawBackground(graphics, _state == RevealState.Peek ? PeekRadius : OpenRadius);
        }
        else if (_state == RevealState.Peek)
        {
            DrawPeek(graphics);
        }
        else
        {
            DrawBackground(graphics, OpenRadius);
            DrawHeader(graphics);
            DrawBody(graphics);
        }
    }

    // Hidden fades the whole tab; Peek and Open fade only the background, so text and bars stay solid.
    private byte WindowAlpha => _state == RevealState.Hidden
        ? ToAlpha(Settings.HiddenOpacity, StripSettings.HiddenOpacityChoices.Min())
        : (byte)255;

    private byte BackgroundAlpha => _state == RevealState.Hidden
        ? (byte)255
        : ToAlpha(Settings.BackgroundOpacity, StripSettings.BackgroundOpacityChoices.Min());

    private static byte ToAlpha(int percent, int minimum) => (byte)Math.Round(Math.Clamp(percent, minimum, 100) * 255 / 100d);

    // The frame is drawn on an opaque background, so text keeps ClearType. A layered window has one alpha per pixel
    // and cannot keep ClearType's per-channel edges over see-through pixels, so content and a ring around it stay
    // opaque and only the empty background fades.
    private void ComposeFrame(Bitmap target)
    {
        using (var graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            DrawFrame(graphics);
        }

        var alpha = BackgroundAlpha;
        if (alpha == 255)
        {
            return;
        }

        var plainKey = (target.Size, _state, DeviceDpi);
        if (_plainKey != plainKey)
        {
            using var plain = new Bitmap(target.Width, target.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(plain))
            {
                DrawFrame(graphics, backgroundOnly: true);
            }

            _plainPixels = new int[target.Width * target.Height];
            CopyPixels(plain, _plainPixels, toBitmap: false);
            _plainKey = plainKey;
        }

        FadeBackground(target, alpha);
    }

    private void FadeBackground(Bitmap target, byte alpha)
    {
        const float ring = 1;
        const float fade = 2;
        const float far = ring + fade;
        const float diagonal = 1.4142f;
        const int contentThreshold = 10;
        var width = target.Width;
        var height = target.Height;
        var stride = width + 2;
        if (_pixels.Length != width * height)
        {
            _pixels = new int[width * height];
            // A one-pixel border that stays at the cap lets the distance passes skip edge checks.
            _distance = new float[stride * (height + 2)];
            Array.Fill(_distance, far);
        }

        CopyPixels(target, _pixels, toBitmap: false);
        var pixels = _pixels.AsSpan();
        var plain = _plainPixels.AsSpan();
        var distance = _distance.AsSpan();

        for (int y = 0, i = 0; y < height; y++)
        {
            var j = (y + 1) * stride + 1;
            for (var x = 0; x < width; x++, i++, j++)
            {
                int a = pixels[i], b = plain[i];
                var content = Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) > contentThreshold
                    || Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) > contentThreshold
                    || Math.Abs((a & 255) - (b & 255)) > contentThreshold;
                distance[j] = content ? 0 : far;
            }
        }

        // Two-pass chamfer distance to the nearest content pixel, capped at the end of the fade.
        for (var y = 1; y <= height; y++)
        {
            var j = y * stride + 1;
            for (var x = 0; x < width; x++, j++)
            {
                var d = distance[j];
                if (d == 0)
                {
                    continue;
                }

                d = Math.Min(d, distance[j - 1] + 1);
                d = Math.Min(d, distance[j - stride] + 1);
                d = Math.Min(d, distance[j - stride - 1] + diagonal);
                distance[j] = Math.Min(d, distance[j - stride + 1] + diagonal);
            }
        }

        for (var y = height; y >= 1; y--)
        {
            var j = y * stride + width;
            for (var x = 0; x < width; x++, j--)
            {
                var d = distance[j];
                if (d == 0)
                {
                    continue;
                }

                d = Math.Min(d, distance[j + 1] + 1);
                d = Math.Min(d, distance[j + stride] + 1);
                d = Math.Min(d, distance[j + stride + 1] + diagonal);
                distance[j] = Math.Min(d, distance[j + stride - 1] + diagonal);
            }
        }

        Span<byte> faded = stackalloc byte[256];
        for (var v = 0; v < 256; v++)
        {
            faded[v] = (byte)((v * alpha + 127) / 255);
        }

        for (int y = 0, i = 0; y < height; y++)
        {
            var j = (y + 1) * stride + 1;
            for (var x = 0; x < width; x++, i++, j++)
            {
                var d = distance[j];
                var pixel = pixels[i];
                if (d <= ring || pixel == 0)
                {
                    continue;
                }

                if (d >= far)
                {
                    pixels[i] = (faded[(pixel >> 24) & 255] << 24) | (faded[(pixel >> 16) & 255] << 16)
                        | (faded[(pixel >> 8) & 255] << 8) | faded[pixel & 255];
                    continue;
                }

                var source = (pixel >> 24) & 255;
                var keep = 1 - (d - ring) / fade;
                var low = source * alpha / 255f;
                var final = low + (source - low) * keep;
                var factor = final / source;
                pixels[i] = ((int)(final + 0.5f) << 24)
                    | ((int)(((pixel >> 16) & 255) * factor + 0.5f) << 16)
                    | ((int)(((pixel >> 8) & 255) * factor + 0.5f) << 8)
                    | (int)((pixel & 255) * factor + 0.5f);
            }
        }

        CopyPixels(target, _pixels, toBitmap: true);
    }

    private static void CopyPixels(Bitmap bitmap, int[] pixels, bool toBitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size),
            toBitmap ? System.Drawing.Imaging.ImageLockMode.WriteOnly : System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            if (toBitmap)
            {
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            else
            {
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    protected override void OnInvalidated(InvalidateEventArgs args)
    {
        base.OnInvalidated(args);
        if (!_renderQueued && IsHandleCreated)
        {
            _renderQueued = true;
            BeginInvoke(RenderLayered);
        }
    }

    private void RenderLayered()
    {
        _renderQueued = false;
        if (IsDisposed || !IsHandleCreated || Width <= 0 || Height <= 0)
        {
            return;
        }

        // The fade buffers are sized for Peek or Open and the hidden tab never fades, so they are released here.
        if (_state == RevealState.Hidden && _pixels.Length > 0)
        {
            _pixels = [];
            _plainPixels = [];
            _distance = [];
            _plainKey = null;
        }

        if (_surface is null || _surface.Image.Size != Size)
        {
            _surface?.Dispose();
            _surface = new LayeredSurface(Width, Height);
        }

        ComposeFrame(_surface.Image);
        var size = _surface.Image.Size;
        var source = Point.Empty;
        var blend = new BlendFunction { SourceConstantAlpha = WindowAlpha, AlphaFormat = AcSrcAlpha };
        UpdateLayeredWindow(Handle, nint.Zero, nint.Zero, ref size, _surface.Dc, ref source, 0, ref blend, UlwAlpha);
    }

    // A premultiplied DIB that GDI+ draws into directly and UpdateLayeredWindow reads, kept between frames.
    private sealed class LayeredSurface : IDisposable
    {
        private readonly nint _bitmap;
        private readonly nint _previous;

        public LayeredSurface(int width, int height)
        {
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32
            };
            Dc = CreateCompatibleDC(nint.Zero);
            _bitmap = CreateDIBSection(Dc, ref header, 0, out var bits, nint.Zero, 0);
            _previous = SelectObject(Dc, _bitmap);
            Image = new Bitmap(width, height, width * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bits);
        }

        public nint Dc { get; }

        public Bitmap Image { get; }

        public void Dispose()
        {
            Image.Dispose();
            SelectObject(Dc, _previous);
            DeleteObject(_bitmap);
            DeleteDC(Dc);
        }
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        CreateFonts();
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs args)
    {
    }

    // Every size is derived from DeviceDpi, so skip WinForms' rescale and re-dock at the new DPI.
    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        args.Cancel = true;
        base.OnDpiChanged(args);
        CreateFonts();
        if (_animationTimer.Enabled)
        {
            _animationTarget = CalculateBounds(_state);
            return;
        }

        Bounds = CalculateBounds(_state);
        ApplyWindowRegion();
        Invalidate();
    }

    protected virtual void DrawHidden(Graphics graphics)
    {
        var core = DrawHiddenTab(graphics);
        FillPill(graphics, Cyan, core);
    }

    // A dark tab hanging off the dock edge with a light outer ring, so it reads on both light and dark
    // backgrounds. Returns the bar area inside the tab, which subclasses fill.
    protected RectangleF DrawHiddenTab(Graphics graphics, float barThickness = 3)
    {
        var scale = DeviceDpi / 96f;
        var ring = Math.Max(1f, (float)Math.Round(scale));
        using (var ringBrush = new SolidBrush(HiddenRing))
        {
            graphics.FillRectangle(ringBrush, ClientRectangle);
        }

        var body = Settings.DockEdge switch
        {
            DockEdge.Top => new RectangleF(0, 0, Width, Height - ring),
            DockEdge.Bottom => new RectangleF(0, ring, Width, Height - ring),
            DockEdge.Left => new RectangleF(0, 0, Width - ring, Height),
            _ => new RectangleF(ring, 0, Width - ring, Height)
        };
        body.Inflate(IsHorizontal ? -ring : 0, IsHorizontal ? 0 : -ring);
        using (var bodyPath = TabPath(body, HiddenRadius() - ring))
        using (var bodyBrush = new SolidBrush(HiddenBody))
        {
            graphics.FillPath(bodyBrush, bodyPath);
        }

        var inset = 3 * scale;
        var thickness = barThickness * scale;
        return IsHorizontal
            ? new RectangleF(body.Left + inset, body.Top + (body.Height - thickness) / 2f, body.Width - 2 * inset, thickness)
            : new RectangleF(body.Left + (body.Width - thickness) / 2f, body.Top + inset, thickness, body.Height - 2 * inset);
    }

    // The two-line tab gave up one pixel on the screen-edge side, so its bars keep their distance from the exposed ring.
    protected RectangleF ShiftTowardEdge(RectangleF core, float amount)
    {
        core.Offset(Settings.DockEdge switch { DockEdge.Left => -amount, DockEdge.Right => amount, _ => 0 },
            Settings.DockEdge switch { DockEdge.Top => -amount, DockEdge.Bottom => amount, _ => 0 });
        return core;
    }

    protected static void FillPill(Graphics graphics, Color color, RectangleF bounds)
    {
        using var brush = new SolidBrush(color);
        using var path = RoundedRectangle(bounds, Math.Min(bounds.Width, bounds.Height) / 2f);
        graphics.FillPath(brush, path);
    }

    private float HiddenRadius() => 4 * DeviceDpi / 96f;

    // Rounds only the corners on the open side, away from the dock edge.
    private GraphicsPath TabPath(RectangleF rectangle, float radius)
    {
        var diameter = Math.Max(0, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
        var path = new GraphicsPath();
        if (diameter <= 1)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        var edge = Settings.DockEdge;
        void Corner(bool round, float x, float y, float arcX, float arcY, float startAngle)
        {
            if (round)
            {
                path.AddArc(arcX, arcY, diameter, diameter, startAngle, 90);
            }
            else
            {
                path.AddLine(x, y, x, y);
            }
        }

        Corner(edge is DockEdge.Bottom or DockEdge.Right, rectangle.Left, rectangle.Top, rectangle.Left, rectangle.Top, 180);
        Corner(edge is DockEdge.Bottom or DockEdge.Left, rectangle.Right, rectangle.Top, rectangle.Right - diameter, rectangle.Top, 270);
        Corner(edge is DockEdge.Top or DockEdge.Left, rectangle.Right, rectangle.Bottom, rectangle.Right - diameter, rectangle.Bottom - diameter, 0);
        Corner(edge is DockEdge.Top or DockEdge.Right, rectangle.Left, rectangle.Bottom, rectangle.Left, rectangle.Bottom - diameter, 90);
        path.CloseFigure();
        return path;
    }

    protected void DrawPeekOutline(Graphics graphics)
    {
        DrawBackground(graphics, PeekRadius);
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

        using var path = _state == RevealState.Hidden
            ? TabPath(new Rectangle(0, 0, Width, Height), HiddenRadius())
            : RoundedRectangle(new Rectangle(0, 0, Width, Height), 12f * DeviceDpi / 96f);
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

    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private const byte AcSrcAlpha = 0x01;
    private const int UlwAlpha = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, nint destination, ref Size size, nint sourceDc,
        ref Point source, int colorKey, ref BlendFunction blend, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

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
            _surface?.Dispose();
            DisposeFonts();
        }

        base.Dispose(disposing);
    }
}
