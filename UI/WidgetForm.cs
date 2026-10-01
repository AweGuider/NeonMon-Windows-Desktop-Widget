using System.Drawing.Drawing2D;
using System.Diagnostics;
using NeonMon.Models;
using NeonMon.Services;

namespace NeonMon.UI;

internal sealed class WidgetForm : Form
{
    private static readonly Color BackgroundTop = Color.FromArgb(246, 7, 16, 21);
    private static readonly Color BackgroundBottom = Color.FromArgb(250, 5, 11, 15);
    private static readonly Color Cyan = Color.FromArgb(49, 247, 210);
    private static readonly Color Ice = Color.FromArgb(117, 241, 255);
    private static readonly Color Foreground = Color.FromArgb(224, 246, 249);
    private static readonly Color Muted = Color.FromArgb(104, 147, 157);
    private static readonly Color Track = Color.FromArgb(30, 91, 117, 126);
    private static readonly Color Warning = Color.FromArgb(255, 173, 84);

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly TelemetryService _telemetry;
    private readonly MetricsBridge _bridge;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly System.Windows.Forms.Timer _collapseTimer;
    private readonly System.Windows.Forms.Timer _hoverTimer;
    private readonly ToolTip _toolTip;
    private readonly Font _labelFont = new("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _detailFont = new("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _valueFont = new("Consolas", 13.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _uptimeFont = new("Consolas", 17f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _headerFont = new("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _iconFont = new("Segoe UI Symbol", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Dictionary<WidgetSize, Rectangle> _sizeHitAreas = [];
    private readonly Dictionary<string, Rectangle> _driveHitAreas = [];
    private Rectangle _dockHitArea;
    private Rectangle _pinHitArea;
    private Rectangle _hideHitArea;
    private Rectangle _exitHitArea;
    private string? _hoveredTooltip;
    private bool _tooltipVisible;
    private long _tooltipHoverStarted;
    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Empty;
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

    public WidgetForm(
        AppSettings settings,
        SettingsStore settingsStore,
        TelemetryService telemetry,
        MetricsBridge bridge)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _telemetry = telemetry;
        _bridge = bridge;
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

        _menu = BuildMenu();
        ContextMenuStrip = _menu;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "NeonMon",
            Visible = false,
            ContextMenuStrip = _menu
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ToggleOpen();
            }
        };

        _animationTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animationTimer.Tick += (_, _) => AdvanceAnimation();
        _collapseTimer = new System.Windows.Forms.Timer { Interval = 850 };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_settings.KeepOpen && !GetHoverBounds().Contains(Cursor.Position) && !_menu.Visible)
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
            var size = TextRenderer.MeasureText(_hoveredTooltip ?? string.Empty, _detailFont);
            args.ToolTipSize = new Size(size.Width + 18, size.Height + 10);
        };
        _toolTip.Draw += (_, args) =>
        {
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(248, 5, 14, 19));
            using var border = new Pen(Color.FromArgb(150, Cyan));
            args.Graphics.FillRectangle(background, args.Bounds);
            args.Graphics.DrawRectangle(border, 0, 0, args.Bounds.Width - 1, args.Bounds.Height - 1);
            TextRenderer.DrawText(args.Graphics, args.ToolTipText, _detailFont, new Point(9, 5), Foreground, TextFormatFlags.NoPadding);
        };

        _hoverTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _hoverTimer.Tick += (_, _) => EvaluatePointerState();

        MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_state == RevealState.Hidden)
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
            if (_settings.HtmlBridgeEnabled && !_bridge.Start(_settings.HtmlBridgePort))
            {
                _settings.HtmlBridgeEnabled = false;
            }
        };
        Shown += (_, _) =>
        {
            _tray.Visible = true;
            _hoverTimer.Start();
        };

        _telemetry.SnapshotUpdated += HandleSnapshot;
        _telemetry.SetActive(false);
    }

    protected override bool ShowWithoutActivation => true;

    internal void SavePreview(
        string path,
        TelemetrySnapshot snapshot,
        WidgetSize size = WidgetSize.Large,
        RevealState state = RevealState.Open)
    {
        _state = state;
        _settings.Size = size;
        _snapshot = snapshot;
        Size = GetTargetSize(state);
        _ = Handle;
        ApplyWindowRegion();

        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        DrawToBitmap(bitmap, ClientRectangle);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

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

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open", null, (_, _) => SetRevealState(RevealState.Open));
        menu.Items.Add("Hide", null, (_, _) => SetRevealState(RevealState.Hidden));

        var sizeMenu = new ToolStripMenuItem("Layout size");
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            var item = new ToolStripMenuItem(size.ToString()) { Checked = _settings.Size == size };
            item.Click += (_, _) => SetWidgetSize(size);
            sizeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(sizeMenu);

        var dockMenu = new ToolStripMenuItem("Dock edge");
        foreach (var edge in Enum.GetValues<DockEdge>())
        {
            var item = new ToolStripMenuItem(edge.ToString()) { Checked = _settings.DockEdge == edge };
            item.Click += (_, _) => SetDockEdge(edge);
            dockMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(dockMenu);

        var keepOpen = new ToolStripMenuItem("Keep open") { Checked = _settings.KeepOpen, CheckOnClick = true };
        keepOpen.CheckedChanged += (_, _) =>
        {
            _settings.KeepOpen = keepOpen.Checked;
            SaveSettings();
            if (_settings.KeepOpen)
            {
                SetRevealState(RevealState.Open);
            }
        };
        menu.Items.Add(keepOpen);

        var htmlBridge = new ToolStripMenuItem($"HTML bridge · 127.0.0.1:{_settings.HtmlBridgePort}")
        {
            Checked = _settings.HtmlBridgeEnabled,
            CheckOnClick = true
        };
        htmlBridge.CheckedChanged += (_, _) =>
        {
            if (htmlBridge.Checked && !_bridge.Start(_settings.HtmlBridgePort))
            {
                htmlBridge.Checked = false;
                _tray.ShowBalloonTip(2500, "NeonMon", "The HTML bridge port is unavailable.", ToolTipIcon.Warning);
                return;
            }

            if (!htmlBridge.Checked)
            {
                _bridge.Stop();
            }

            _settings.HtmlBridgeEnabled = htmlBridge.Checked;
            SaveSettings();
            Invalidate();
        };
        menu.Items.Add(htmlBridge);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _exiting = true;
            Close();
        });
        menu.Closed += (_, _) => ScheduleCollapse();
        return menu;
    }

    private void HandleSnapshot(TelemetrySnapshot snapshot)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            _snapshot = snapshot;
            Invalidate();
        }));
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
        _dragStartOffset = _settings.DockOffset;
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
        if (_settings.DockEdge is DockEdge.Top or DockEdge.Bottom)
        {
            _settings.DockOffset = Math.Clamp(_dragStartOffset + (cursor.X - _mouseDownScreen.X) / (double)Math.Max(1, area.Width), 0, 1);
        }
        else
        {
            _settings.DockOffset = Math.Clamp(_dragStartOffset + (cursor.Y - _mouseDownScreen.Y) / (double)Math.Max(1, area.Height), 0, 1);
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
            SaveSettings();
            return;
        }

        if (_state == RevealState.Open)
        {
            var point = args.Location;
            foreach (var drive in _driveHitAreas)
            {
                if (drive.Value.Contains(point))
                {
                    OpenDrive(drive.Key);
                    return;
                }
            }

            if (_exitHitArea.Contains(point))
            {
                _exiting = true;
                Close();
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
                _settings.KeepOpen = !_settings.KeepOpen;
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

    private void ToggleOpen() => SetRevealState(_state == RevealState.Open ? RevealState.Hidden : RevealState.Open);

    private void SetWidgetSize(WidgetSize size)
    {
        _settings.Size = size;
        SaveSettings();
        UpdateMenuChecks();
        SetRevealState(RevealState.Open, true);
    }

    private void SetDockEdge(DockEdge edge)
    {
        _settings.DockEdge = edge;
        _dockScreen = Screen.FromPoint(Cursor.Position);
        SaveSettings();
        UpdateMenuChecks();
        SetRevealState(_state, true);
    }

    private void CycleDockEdge()
    {
        var next = _settings.DockEdge switch
        {
            DockEdge.Top => DockEdge.Right,
            DockEdge.Right => DockEdge.Bottom,
            DockEdge.Bottom => DockEdge.Left,
            _ => DockEdge.Top
        };
        SetDockEdge(next);
    }

    private void OpenDrive(string driveName)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = driveName + Path.DirectorySeparatorChar,
                UseShellExecute = true
            });
        }
        catch
        {
            _tray.ShowBalloonTip(2500, "NeonMon", $"Could not open {driveName}.", ToolTipIcon.Warning);
        }
    }

    private void UpdateMenuChecks()
    {
        foreach (var root in _menu.Items.OfType<ToolStripMenuItem>())
        {
            if (root.Text == "Layout size")
            {
                foreach (ToolStripMenuItem item in root.DropDownItems)
                {
                    item.Checked = item.Text == _settings.Size.ToString();
                }
            }
            else if (root.Text == "Dock edge")
            {
                foreach (ToolStripMenuItem item in root.DropDownItems)
                {
                    item.Checked = item.Text == _settings.DockEdge.ToString();
                }
            }
        }
    }

    private void SetRevealState(RevealState state, bool forceAnimation = false)
    {
        if (_state == state && !forceAnimation)
        {
            return;
        }

        _state = state;
        _telemetry.SetActive(state == RevealState.Open);
        _animationStart = Bounds;
        _animationTarget = CalculateBounds(state);
        _animationStarted = Environment.TickCount64;
        _animationTimer.Start();
        Invalidate();
    }

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

    private Rectangle CalculateBounds(RevealState state)
    {
        var size = GetTargetSize(state);
        var area = _dockScreen.WorkingArea;
        int x;
        int y;

        if (_settings.DockEdge is DockEdge.Top or DockEdge.Bottom)
        {
            x = area.Left + (int)Math.Round((area.Width - size.Width) * _settings.DockOffset);
            y = _settings.DockEdge == DockEdge.Top ? area.Top + 1 : area.Bottom - size.Height - 1;
        }
        else
        {
            x = _settings.DockEdge == DockEdge.Left ? area.Left + 1 : area.Right - size.Width - 1;
            y = area.Top + (int)Math.Round((area.Height - size.Height) * _settings.DockOffset);
        }

        return new Rectangle(new Point(x, y), size);
    }

    private Size GetTargetSize(RevealState state)
    {
        var scale = DeviceDpi / 96f;
        Size Logical(Size size) => new((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));

        if (state == RevealState.Hidden)
        {
            return Logical(_settings.DockEdge is DockEdge.Top or DockEdge.Bottom ? new Size(72, 6) : new Size(6, 72));
        }

        if (state == RevealState.Peek)
        {
            return Logical(_settings.DockEdge is DockEdge.Top or DockEdge.Bottom ? new Size(72, 28) : new Size(28, 72));
        }

        return Logical(_settings.Size switch
        {
            WidgetSize.Small => new Size(380, 88),
            WidgetSize.Medium => new Size(580, 124),
            _ => new Size(780, 190)
        });
    }

    private static Rectangle Lerp(Rectangle from, Rectangle to, double amount) => new(
        (int)Math.Round(from.X + (to.X - from.X) * amount),
        (int)Math.Round(from.Y + (to.Y - from.Y) * amount),
        (int)Math.Round(from.Width + (to.Width - from.Width) * amount),
        (int)Math.Round(from.Height + (to.Height - from.Height) * amount));

    private void ScheduleCollapse()
    {
        if (!_settings.KeepOpen && !_menu.Visible)
        {
            if (!_collapseTimer.Enabled)
            {
                _collapseTimer.Start();
            }
        }
    }

    private Rectangle GetHoverBounds()
    {
        var margin = Math.Max(6, (int)Math.Round(8 * DeviceDpi / 96f));
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
            _collapseTimer.Stop();
            SetRevealState(RevealState.Peek);
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
            foreach (var drive in _driveHitAreas)
            {
                if (drive.Value.Contains(point.Value))
                {
                    text = $"Open {drive.Key} in File Explorer";
                    break;
                }
            }

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
                text = $"Docked to {_settings.DockEdge.ToString().ToLowerInvariant()} · click to move";
            }
            else if (text is null && _pinHitArea.Contains(point.Value))
            {
                text = _settings.KeepOpen ? "Unpin and allow auto-hide" : "Pin the widget open";
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
            DrawOpen(args.Graphics);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs args)
    {
    }

    private void DrawHidden(Graphics graphics)
    {
        using var background = new SolidBrush(Color.FromArgb(225, 6, 15, 20));
        graphics.FillRectangle(background, ClientRectangle);
        using var line = new Pen(Cyan, Math.Max(1f, DeviceDpi / 96f));
        if (_settings.DockEdge is DockEdge.Top or DockEdge.Bottom)
        {
            var y = _settings.DockEdge == DockEdge.Top ? Height - 1 : 0;
            graphics.DrawLine(line, Width * 0.22f, y, Width * 0.78f, y);
        }
        else
        {
            var x = _settings.DockEdge == DockEdge.Left ? Width - 1 : 0;
            graphics.DrawLine(line, x, Height * 0.22f, x, Height * 0.78f);
        }
    }

    private void DrawPeek(Graphics graphics)
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

        using var glow = new Pen(Color.FromArgb(95, Cyan), 4f);
        using var pulse = new Pen(Cyan, 1.4f);
        var centerX = Width / 2f;
        var centerY = Height / 2f;
        var horizontal = _settings.DockEdge is DockEdge.Top or DockEdge.Bottom;
        var points = horizontal
            ? new[] { new PointF(centerX - 16, centerY), new PointF(centerX - 7, centerY), new PointF(centerX - 3, centerY - 6), new PointF(centerX + 2, centerY + 6), new PointF(centerX + 7, centerY), new PointF(centerX + 16, centerY) }
            : new[] { new PointF(centerX, centerY - 16), new PointF(centerX, centerY - 7), new PointF(centerX - 6, centerY - 3), new PointF(centerX + 6, centerY + 2), new PointF(centerX, centerY + 7), new PointF(centerX, centerY + 16) };
        graphics.DrawLines(glow, points);
        graphics.DrawLines(pulse, points);
    }

    private void DrawOpen(Graphics graphics)
    {
        DrawBackground(graphics, 13);
        _driveHitAreas.Clear();
        DrawHeader(graphics);

        switch (_settings.Size)
        {
            case WidgetSize.Small:
                DrawSmall(graphics);
                break;
            case WidgetSize.Medium:
                DrawMedium(graphics);
                break;
            default:
                DrawLarge(graphics);
                break;
        }
    }

    private void DrawBackground(Graphics graphics, int radius)
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
        DrawText(graphics, "SYSTEM PULSE", _headerFont, Muted, new RectangleF(24 * scale, 6 * scale, 130 * scale, 16 * scale));

        var x = Width - (int)Math.Round(179 * scale);
        _sizeHitAreas.Clear();
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            var rectangle = new Rectangle(x, (int)Math.Round(5 * scale), (int)Math.Round(22 * scale), (int)Math.Round(18 * scale));
            _sizeHitAreas[size] = rectangle;
            using var fill = new SolidBrush(size == _settings.Size ? Color.FromArgb(35, Cyan) : Color.Transparent);
            graphics.FillRectangle(fill, rectangle);
            DrawCenteredText(graphics, size.ToString()[0].ToString(), _labelFont, size == _settings.Size ? Cyan : Muted, rectangle);
            x += (int)Math.Round(24 * scale);
        }

        _dockHitArea = new Rectangle(x + (int)(2 * scale), (int)(5 * scale), (int)(22 * scale), (int)(18 * scale));
        DrawCenteredText(graphics, DockGlyph(), _iconFont, Ice, _dockHitArea);
        x += (int)Math.Round(25 * scale);

        _pinHitArea = new Rectangle(x + (int)(2 * scale), (int)(5 * scale), (int)(22 * scale), (int)(18 * scale));
        DrawCenteredText(graphics, _settings.KeepOpen ? "◆" : "◇", _detailFont, _settings.KeepOpen ? Cyan : Muted, _pinHitArea);
        _hideHitArea = new Rectangle(x + (int)(27 * scale), (int)(5 * scale), (int)(22 * scale), (int)(18 * scale));
        DrawCenteredText(graphics, "—", _detailFont, Muted, _hideHitArea);
        _exitHitArea = new Rectangle(x + (int)(52 * scale), (int)(5 * scale), (int)(22 * scale), (int)(18 * scale));
        DrawCenteredText(graphics, "×", _iconFont, Warning, _exitHitArea);
    }

    private string DockGlyph() => _settings.DockEdge switch
    {
        DockEdge.Top => "↑",
        DockEdge.Right => "→",
        DockEdge.Bottom => "↓",
        _ => "←"
    };

    private void DrawSmall(Graphics graphics)
    {
        var scale = DeviceDpi / 96f;
        var top = 34 * scale;
        DrawUptime(graphics, new RectangleF(14 * scale, top, 126 * scale, 38 * scale), compact: true);
        DrawMetric(graphics, new RectangleF(148 * scale, top, 58 * scale, 38 * scale), "CPU", Percent(_snapshot.CpuPercent), null, _snapshot.CpuPercent);
        DrawMetric(graphics, new RectangleF(214 * scale, top, 58 * scale, 38 * scale), "GPU", Percent(_snapshot.GpuPercent), null, _snapshot.GpuPercent);
        DrawDriveMetric(graphics, new RectangleF(280 * scale, top, 86 * scale, 38 * scale), _snapshot.Drives.FirstOrDefault(), compact: true);
    }

    private void DrawMedium(Graphics graphics)
    {
        var scale = DeviceDpi / 96f;
        var top = 40 * scale;
        DrawUptime(graphics, new RectangleF(16 * scale, top, 150 * scale, 55 * scale), compact: false);
        DrawMetric(graphics, new RectangleF(180 * scale, top, 78 * scale, 55 * scale), "CPU", Percent(_snapshot.CpuPercent), Temperature(_snapshot.CpuTemperatureC), _snapshot.CpuPercent);
        DrawMetric(graphics, new RectangleF(270 * scale, top, 78 * scale, 55 * scale), "GPU", Percent(_snapshot.GpuPercent), Temperature(_snapshot.GpuTemperatureC), _snapshot.GpuPercent);
        DrawMetric(graphics, new RectangleF(360 * scale, top, 88 * scale, 55 * scale), "MEMORY", Percent(_snapshot.MemoryPercent), $"{_snapshot.MemoryUsedGb:0.0} GB", _snapshot.MemoryPercent);
        DrawDriveMetric(graphics, new RectangleF(462 * scale, top, 102 * scale, 55 * scale), _snapshot.Drives.FirstOrDefault(), compact: false);
    }

    private void DrawLarge(Graphics graphics)
    {
        var scale = DeviceDpi / 96f;
        var top = 42 * scale;
        var metricHeight = 62 * scale;
        DrawUptime(graphics, new RectangleF(16 * scale, top, 155 * scale, metricHeight), compact: false);
        DrawMetric(graphics, new RectangleF(184 * scale, top, 86 * scale, metricHeight), "CPU", Percent(_snapshot.CpuPercent), Temperature(_snapshot.CpuTemperatureC), _snapshot.CpuPercent);
        DrawMetric(graphics, new RectangleF(282 * scale, top, 86 * scale, metricHeight), "GPU", Percent(_snapshot.GpuPercent), Temperature(_snapshot.GpuTemperatureC), _snapshot.GpuPercent);
        DrawMetric(graphics, new RectangleF(380 * scale, top, 100 * scale, metricHeight), "MEMORY", Percent(_snapshot.MemoryPercent), $"{_snapshot.MemoryUsedGb:0.0}/{_snapshot.MemoryTotalGb:0} GB", _snapshot.MemoryPercent);
        DrawDriveMetric(graphics, new RectangleF(494 * scale, top, 110 * scale, metricHeight), _snapshot.Drives.FirstOrDefault(), compact: false);

        var secondDrive = _snapshot.Drives.Skip(1).FirstOrDefault();
        if (secondDrive is not null)
        {
            DrawDriveMetric(graphics, new RectangleF(618 * scale, top, 110 * scale, metricHeight), secondDrive, compact: false);
        }
        else
        {
            var clock = _snapshot.GpuClockMhz is null ? "—" : $"{_snapshot.GpuClockMhz:N0}";
            var memoryClock = _snapshot.GpuMemoryClockMhz is null ? null : $"VRAM {_snapshot.GpuMemoryClockMhz:N0} MHz";
            DrawMetric(graphics, new RectangleF(618 * scale, top, 110 * scale, metricHeight), "GPU CLOCK", clock, memoryClock, null);
        }

        DrawInfoCard(graphics, new RectangleF(16 * scale, 121 * scale, 354 * scale, 52 * scale), "TOP CPU PROCESS", $"{_snapshot.TopProcess} · {_snapshot.TopProcessCpuPercent:0.0}%", false);
        DrawInfoCard(graphics, new RectangleF(386 * scale, 121 * scale, 378 * scale, 52 * scale), "GPU STABILITY", FormatTimeout(), _snapshot.LastGpuTimeout is not null);
    }

    private void DrawUptime(Graphics graphics, RectangleF area, bool compact)
    {
        var scale = DeviceDpi / 96f;
        var labelHeight = TextLineHeight(graphics, _labelFont, 2 * scale);
        var valueFont = compact ? _valueFont : _uptimeFont;
        var valueHeight = TextLineHeight(graphics, valueFont, 2 * scale);

        DrawText(graphics, "UPTIME", _labelFont, Muted, new RectangleF(area.X, area.Y, area.Width, labelHeight));
        var value = compact
            ? $"{(int)_snapshot.Uptime.TotalDays:00}:{_snapshot.Uptime.Hours:00}:{_snapshot.Uptime.Minutes:00}"
            : $"{(int)_snapshot.Uptime.TotalDays:00}:{_snapshot.Uptime.Hours:00}:{_snapshot.Uptime.Minutes:00}:{_snapshot.Uptime.Seconds:00}";
        var valueTop = area.Y + labelHeight;
        DrawText(graphics, value, valueFont, Ice, new RectangleF(area.X, valueTop, area.Width, valueHeight));
        if (!compact)
        {
            var detailHeight = TextLineHeight(graphics, _detailFont, 2 * scale);
            DrawText(graphics, "days · hrs · min · sec", _detailFont, Muted, new RectangleF(area.X, valueTop + valueHeight, area.Width, detailHeight));
        }
    }

    private void DrawMetric(Graphics graphics, RectangleF area, string label, string value, string? detail, double? percent, bool warning = false)
    {
        var scale = DeviceDpi / 96f;
        var accent = warning ? Warning : Cyan;
        var labelHeight = TextLineHeight(graphics, _labelFont, 2 * scale);
        var valueHeight = TextLineHeight(graphics, _valueFont, 2 * scale);
        DrawText(graphics, label, _labelFont, Muted, new RectangleF(area.X, area.Y, area.Width, labelHeight));
        DrawText(graphics, value, _valueFont, warning ? Warning : Foreground, new RectangleF(area.X, area.Y + labelHeight, area.Width, valueHeight));

        var track = new RectangleF(area.X, area.Y + labelHeight + valueHeight + 2 * scale, area.Width, 3 * scale);
        using var trackBrush = new SolidBrush(Track);
        graphics.FillRectangle(trackBrush, track);
        if (percent is not null)
        {
            using var fill = new SolidBrush(accent);
            graphics.FillRectangle(fill, track.X, track.Y, Math.Max(2, track.Width * (float)Math.Clamp(percent.Value / 100d, 0, 1)), track.Height);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            var detailTop = track.Bottom + 5 * scale;
            var detailHeight = TextLineHeight(graphics, _detailFont, 2 * scale);
            DrawText(graphics, detail, _detailFont, warning ? Warning : Muted, new RectangleF(area.X, detailTop, area.Width, detailHeight));
        }
    }

    private void DrawDriveMetric(Graphics graphics, RectangleF area, DriveMetric? drive, bool compact)
    {
        if (drive is null)
        {
            DrawMetric(graphics, area, "DISK FREE", "—", null, null);
            return;
        }

        _driveHitAreas[drive.Name] = Rectangle.Ceiling(area);

        DrawMetric(
            graphics,
            area,
            $"{drive.Name} FREE",
            $"{drive.FreeGb:0} GB",
            compact ? null : $"{drive.FreePercent:0.#}% available",
            drive.FreePercent,
            drive.FreePercent < 10);
    }

    private void DrawInfoCard(Graphics graphics, RectangleF area, string label, string value, bool warning)
    {
        var scale = DeviceDpi / 96f;
        using var path = RoundedRectangle(area, 7 * scale);
        using var fill = new SolidBrush(Color.FromArgb(92, 10, 27, 34));
        using var border = new Pen(Color.FromArgb(35, 75, 226, 246));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        var left = area.X + 11 * scale;
        var width = area.Width - 22 * scale;
        var labelTop = area.Y + 7 * scale;
        var labelHeight = TextLineHeight(graphics, _labelFont, 2 * scale);
        var valueTop = labelTop + labelHeight + scale;
        var valueHeight = TextLineHeight(graphics, _detailFont, 2 * scale);
        DrawText(graphics, label, _labelFont, Muted, new RectangleF(left, labelTop, width, labelHeight));
        DrawText(graphics, value, _detailFont, warning ? Warning : Foreground, new RectangleF(left, valueTop, width, valueHeight));
    }

    private static float TextLineHeight(Graphics graphics, Font font, float padding) =>
        (float)Math.Ceiling(font.GetHeight(graphics)) + padding;

    private string FormatTimeout()
    {
        if (_snapshot.LastGpuTimeout is null)
        {
            return "None found";
        }

        var age = DateTimeOffset.Now - _snapshot.LastGpuTimeout.Value;
        var text = age.TotalHours < 48 ? $"{Math.Max(0, age.TotalHours):0}h ago" : $"{Math.Max(0, age.TotalDays):0}d ago";
        return _snapshot.GpuTimeoutCode is null ? text : $"{text} · 0x{_snapshot.GpuTimeoutCode}";
    }

    private static string Percent(double value) => $"{value:0}%";
    private static string Temperature(double? value) => value is null ? "temp unavailable" : $"{value:0}°C";
    private static void DrawText(Graphics graphics, string value, Font font, Color color, RectangleF bounds)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(value, font, brush, bounds, format);
    }

    private static void DrawCenteredText(Graphics graphics, string value, Font font, Color color, Rectangle bounds)
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

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
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
            _settingsStore.Save(_settings);
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
            _telemetry.SnapshotUpdated -= HandleSnapshot;
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _animationTimer.Dispose();
            _collapseTimer.Dispose();
            _hoverTimer.Dispose();
            _toolTip.Dispose();
            _labelFont.Dispose();
            _detailFont.Dispose();
            _valueFont.Dispose();
            _uptimeFont.Dispose();
            _headerFont.Dispose();
            _iconFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
