using System.Diagnostics;
using System.Drawing.Drawing2D;
using NeonMon.Models;
using NeonMon.Services;

namespace NeonMon.UI;

internal sealed class SystemPulseForm : WidgetForm
{
    private const float PeekCharWidth = 7.7f;
    private const float PeekPadding = 18;
    private const string PeekSeparator = " · ";

    private readonly AppSettings _settings;
    private readonly TelemetryService _telemetry;
    private readonly Dictionary<string, Rectangle> _driveHitAreas = [];
    private readonly List<Rectangle> _taskManagerHitAreas = [];
    private Rectangle _uptimeArea;
    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Empty;
    private IReadOnlyList<double> _hiddenValues = [];
    private Font _peekFont = null!;

    public SystemPulseForm(AppSettings settings, Action saveSettings, TelemetryService telemetry)
        : base(settings, saveSettings)
    {
        _settings = settings;
        _telemetry = telemetry;
        _telemetry.SnapshotUpdated += HandleSnapshot;
        _telemetry.HiddenMetricsUpdated += HandleHiddenMetrics;
        _telemetry.SetActive(false);
        _telemetry.SetPeekTemperatures(ShowsTemperature);
    }

    protected override string Title => "SYSTEM PULSE";

    internal void HiddenMetricsChanged()
    {
        _hiddenValues = [];
        ApplyHiddenMetrics();
        ContentSizeChanged();
    }

    private void ApplyHiddenMetrics() =>
        _telemetry.SetHiddenMetrics(Visible && Settings.Enabled ? _settings.HiddenMetrics : [], _settings.HiddenMetricSeconds);

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        ApplyHiddenMetrics();
    }

    private void HandleHiddenMetrics(IReadOnlyList<double> values)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            _hiddenValues = values;
            if (State == RevealState.Hidden)
            {
                Invalidate();
            }
        }));
    }

    protected override Size GetLogicalHiddenSize(bool horizontal)
    {
        var count = _settings.HiddenMetrics.Count;
        if (count == 0)
        {
            return base.GetLogicalHiddenSize(horizontal);
        }

        var thickness = count == 1 ? 9 : TwoLineTabThickness;
        return horizontal ? new Size(132, thickness) : new Size(thickness, 132);
    }

    // One metric fills the 9 px tab; two use the two-line tab with the first metric on the inner line.
    protected override void DrawHidden(Graphics graphics)
    {
        var metrics = _settings.HiddenMetrics;
        if (metrics.Count == 0)
        {
            base.DrawHidden(graphics);
            return;
        }

        if (metrics.Count == 1)
        {
            DrawMetricBar(graphics, DrawHiddenTab(graphics, 3.5f), metrics[0], HiddenValue(0));
            return;
        }

        var scale = DeviceDpi / 96f;
        var outer = 4.5f * scale;
        var gap = 1.5f * scale;
        var inner = 3.5f * scale;
        var core = ShiftTowardEdge(DrawHiddenTab(graphics, (outer + gap + inner) / scale), 0.5f * scale);
        var outerOnFarSide = Settings.DockEdge is DockEdge.Bottom or DockEdge.Right;
        RectangleF Across(float offset, float thickness) => IsHorizontal
            ? new RectangleF(core.Left, core.Top + offset, core.Width, thickness)
            : new RectangleF(core.Left + offset, core.Top, thickness, core.Height);
        DrawMetricBar(graphics, Across(outerOnFarSide ? 0 : outer + gap, inner), metrics[0], HiddenValue(0));
        DrawMetricBar(graphics, Across(outerOnFarSide ? inner + gap : 0, outer), metrics[1], HiddenValue(1));
    }

    // Until the first average arrives, the last full snapshot stands in.
    private double HiddenValue(int index)
    {
        if (index < _hiddenValues.Count)
        {
            return _hiddenValues[index];
        }

        return _settings.HiddenMetrics[index] switch
        {
            HiddenMetric.Cpu => _snapshot.CpuPercent,
            HiddenMetric.Gpu => _snapshot.GpuPercent,
            HiddenMetric.Memory => _snapshot.MemoryPercent,
            _ => _snapshot.Drives.FirstOrDefault(drive => drive.Name.Equals(TelemetryService.SystemDrive, StringComparison.OrdinalIgnoreCase)) is { } drive
                ? 100 - drive.FreePercent
                : 0
        };
    }

    // Side docks fill from the bottom up, like a level.
    private void DrawMetricBar(Graphics graphics, RectangleF bar, HiddenMetric metric, double percent)
    {
        FillPill(graphics, StripTrack, bar);
        var fraction = (float)Math.Clamp(percent / 100d, 0, 1);
        var fill = IsHorizontal
            ? new RectangleF(bar.Left, bar.Top, Math.Max(bar.Height, bar.Width * fraction), bar.Height)
            : new RectangleF(bar.Left, bar.Bottom - Math.Max(bar.Width, bar.Height * fraction), bar.Width, Math.Max(bar.Width, bar.Height * fraction));
        FillPill(graphics, MetricColor(metric, percent), fill);
    }

    private static Color MetricColor(HiddenMetric metric, double percent) => metric == HiddenMetric.Drive
        ? percent >= 95 ? Critical : percent >= 85 ? Warning : Cyan
        : percent >= 90 ? Critical : percent >= 75 ? Warning : Cyan;

    internal void SetSnapshot(TelemetrySnapshot snapshot)
    {
        _snapshot = snapshot;
        Invalidate();
    }

    protected override Size GetLogicalOpenSize(WidgetSize size) => size switch
    {
        WidgetSize.Small => new Size(380, 88),
        WidgetSize.Medium => new Size(580, 124),
        _ => new Size(780, 190)
    };

    protected override void OnRevealStateChanged(RevealState state)
    {
        _telemetry.SetActive(state == RevealState.Open);
        _telemetry.SetPeeking(state == RevealState.Peek);
    }

    private bool ShowsTemperature => (_settings.Peek & (PeekValues.CpuTemperature | PeekValues.GpuTemperature)) != 0;

    internal void PeekValuesChanged()
    {
        _telemetry.SetPeekTemperatures(ShowsTemperature);
        if (State == RevealState.Peek)
        {
            ContentSizeChanged();
        }
    }

    // Load and temperature reserve their widest text so the peek does not resize every second.
    protected override Size GetLogicalPeekSize(bool horizontal)
    {
        var items = PeekItems();
        return horizontal
            ? new Size((int)Math.Ceiling(2 * PeekPadding + PeekCharWidth * (items.Sum(item => item.Label.Length + item.Parts.Sum(part => 1 + (part.Widest ?? part.Text).Length))
                + Math.Max(0, items.Count - 1) * PeekSeparator.Length)), 28)
            : new Size(54, 18 + items.Sum(item => 18 + 15 * item.Parts.Count));
    }

    protected override void CreateFonts()
    {
        base.CreateFonts();
        _peekFont?.Dispose();
        _peekFont = CreateFont("Consolas", 10.5f, FontStyle.Regular);
    }

    private void HandleSnapshot(TelemetrySnapshot snapshot)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            var previousPeek = GetLogicalPeekSize(IsHorizontal);
            _snapshot = snapshot;
            if (State == RevealState.Peek && GetLogicalPeekSize(IsHorizontal) != previousPeek)
            {
                ContentSizeChanged();
            }
            else if (State != RevealState.Hidden)
            {
                Invalidate();
            }
        }));
    }

    protected override bool HandleBodyClick(Point point)
    {
        foreach (var drive in _driveHitAreas)
        {
            if (drive.Value.Contains(point))
            {
                OpenDrive(drive.Key);
                return true;
            }
        }

        if (_taskManagerHitAreas.Any(area => area.Contains(point)))
        {
            OpenTaskManager();
            return true;
        }

        return false;
    }

    protected override string? GetBodyTooltip(Point point)
    {
        foreach (var drive in _driveHitAreas)
        {
            if (drive.Value.Contains(point))
            {
                return $"Open {drive.Key} in File Explorer";
            }
        }

        if (_taskManagerHitAreas.Any(area => area.Contains(point)))
        {
            return "Open Task Manager";
        }

        return _uptimeArea.Contains(point)
            ? $"Since power-on or wake · last full Windows boot {(int)_snapshot.WindowsUptime.TotalDays}d {_snapshot.WindowsUptime.Hours:00}h ago"
            : null;
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
            ShowNotice($"Could not open {driveName}.");
        }
    }

    // Shell execute lets Windows handle Task Manager's elevation; a direct start fails for administrators.
    private void OpenTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        }
        catch
        {
            ShowNotice("Could not open Task Manager.");
        }
    }

    protected override void DrawPeek(Graphics graphics)
    {
        DrawPeekOutline(graphics);
        var scale = DeviceDpi / 96f;
        var items = PeekItems();
        var textHeight = TextLineHeight(graphics, _peekFont, 0);
        if (IsHorizontal)
        {
            var segments = new List<(string Text, Color Color, bool Stopwatch)>();
            foreach (var item in items)
            {
                if (segments.Count > 0)
                {
                    segments.Add((PeekSeparator, Muted, false));
                }

                segments.Add(($"{item.Label} ", Foreground, item.Stopwatch));
                segments.AddRange(item.Parts.Select((part, index) => (index == 0 ? part.Text : $" {part.Text}", part.Warning ? Warning : Ice, false)));
            }

            var x = (Width - segments.Sum(segment => MeasureText(graphics, segment.Text, _peekFont))) / 2f;
            var y = (Height - textHeight) / 2f;
            foreach (var segment in segments)
            {
                if (segment.Stopwatch)
                {
                    DrawStopwatch(graphics, x + MeasureText(graphics, segment.Text.TrimEnd(), _peekFont) / 2f, Height / 2f, segment.Color);
                    x += MeasureText(graphics, segment.Text, _peekFont);
                    continue;
                }

                x = DrawPeekText(graphics, segment.Text, segment.Color, x, y, textHeight);
            }

            return;
        }

        var top = 12 * scale;
        foreach (var item in items)
        {
            if (item.Stopwatch)
            {
                DrawStopwatch(graphics, Width / 2f, top + textHeight / 2f, Muted);
            }
            else
            {
                DrawPeekCentered(graphics, item.Label, Muted, top, textHeight);
            }
            foreach (var part in item.Parts)
            {
                top += 15 * scale;
                DrawPeekCentered(graphics, part.Text, part.Warning ? Warning : Ice, top, textHeight);
            }

            top += 18 * scale;
        }
    }

    private sealed record PeekPart(string Text, string? Widest = null, bool Warning = false);

    // A stopwatch item draws an icon in place of its label; the label still sizes the peek.
    private sealed record PeekItem(string Label, IReadOnlyList<PeekPart> Parts, bool Stopwatch = false);

    private List<PeekItem> PeekItems()
    {
        var peek = _settings.Peek;
        var items = new List<PeekItem>();
        AddLoad("CPU", peek.HasFlag(PeekValues.Cpu), _snapshot.CpuPercent, peek.HasFlag(PeekValues.CpuTemperature), _snapshot.CpuTemperatureC);
        AddLoad("GPU", peek.HasFlag(PeekValues.Gpu), _snapshot.GpuPercent, peek.HasFlag(PeekValues.GpuTemperature), _snapshot.GpuTemperatureC);
        if (peek.HasFlag(PeekValues.Memory))
        {
            items.Add(new PeekItem("RAM", [new PeekPart(Percent(_snapshot.MemoryPercent), "100%")]));
        }

        foreach (var (name, drive) in PeekDrives())
        {
            items.Add(new PeekItem(name, [new PeekPart(drive is null ? "—" : $"{drive.FreeGb:0}G", Warning: drive?.FreePercent < 10)]));
        }

        if (peek.HasFlag(PeekValues.Uptime))
        {
            items.Add(new PeekItem("UP", [new PeekPart($"{(int)_snapshot.Uptime.TotalDays}d"), new PeekPart($"{_snapshot.Uptime.Hours}h")], Stopwatch: true));
        }

        return items;

        void AddLoad(string label, bool load, double percent, bool temperature, double? celsius)
        {
            var parts = new List<PeekPart>();
            if (load)
            {
                parts.Add(new PeekPart(Percent(percent), "100%"));
            }

            if (temperature)
            {
                parts.Add(celsius is null ? new PeekPart("—") : new PeekPart($"{celsius:0}°", "100°"));
            }

            if (parts.Count > 0)
            {
                items.Add(new PeekItem(label, parts));
            }
        }
    }

    // A chosen drive that is missing is skipped, except before the first sample when no drive is known yet.
    private IEnumerable<(string Name, DriveMetric? Drive)> PeekDrives()
    {
        if (_settings.PeekDrives is null)
        {
            var systemDrive = TelemetryService.SystemDrive;
            var drive = _snapshot.Drives.FirstOrDefault(drive => drive.Name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
                ?? _snapshot.Drives.FirstOrDefault();
            yield return (drive?.Name ?? systemDrive, drive);
            yield break;
        }

        foreach (var name in _settings.PeekDrives)
        {
            var drive = _snapshot.Drives.FirstOrDefault(drive => drive.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (drive is not null || _snapshot.Drives.Count == 0)
            {
                yield return (name, drive);
            }
        }
    }

    private void DrawStopwatch(Graphics graphics, float centerX, float centerY, Color color)
    {
        var scale = DeviceDpi / 96f;
        var radius = 4.6f * scale;
        var dialY = centerY + scale;
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.25f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawEllipse(pen, centerX - radius, dialY - radius, 2 * radius, 2 * radius);
        graphics.DrawLine(pen, centerX, dialY - radius, centerX, dialY - radius - 1.6f * scale);
        graphics.DrawLine(pen, centerX - 1.6f * scale, dialY - radius - 1.8f * scale, centerX + 1.6f * scale, dialY - radius - 1.8f * scale);
        var side = radius * 0.72f;
        graphics.DrawLine(pen, centerX + side, dialY - side, centerX + side + 1.3f * scale, dialY - side - 1.3f * scale);
        graphics.DrawLine(pen, centerX, dialY, centerX + 1.8f * scale, dialY - 2.2f * scale);
        graphics.SmoothingMode = smoothing;
    }

    private float DrawPeekText(Graphics graphics, string text, Color color, float x, float y, float height)
    {
        var width = MeasureText(graphics, text, _peekFont);
        DrawText(graphics, text, _peekFont, color, new RectangleF(x, y, width + 2, height));
        return x + width;
    }

    private void DrawPeekCentered(Graphics graphics, string text, Color color, float y, float height)
    {
        var width = MeasureText(graphics, text, _peekFont);
        DrawText(graphics, text, _peekFont, color, new RectangleF((Width - width) / 2f, y, width + 2, height));
    }

    private static float MeasureText(Graphics graphics, string text, Font font)
    {
        using var format = new StringFormat(StringFormat.GenericTypographic);
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return graphics.MeasureString(text, font, PointF.Empty, format).Width;
    }

    protected override void DrawBody(Graphics graphics)
    {
        _driveHitAreas.Clear();
        _taskManagerHitAreas.Clear();

        switch (Settings.Size)
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

    private void DrawSmall(Graphics graphics)
    {
        var scale = DeviceDpi / 96f;
        var top = 34 * scale;
        DrawUptime(graphics, new RectangleF(14 * scale, top, 126 * scale, 38 * scale), compact: true);
        DrawLoadMetric(graphics, new RectangleF(148 * scale, top, 58 * scale, 38 * scale), "CPU", Percent(_snapshot.CpuPercent), null, _snapshot.CpuPercent);
        DrawLoadMetric(graphics, new RectangleF(214 * scale, top, 58 * scale, 38 * scale), "GPU", Percent(_snapshot.GpuPercent), null, _snapshot.GpuPercent);
        DrawDriveMetric(graphics, new RectangleF(280 * scale, top, 86 * scale, 38 * scale), _snapshot.Drives.FirstOrDefault(), compact: true);
    }

    private void DrawMedium(Graphics graphics)
    {
        var scale = DeviceDpi / 96f;
        var top = 40 * scale;
        DrawUptime(graphics, new RectangleF(16 * scale, top, 150 * scale, 55 * scale), compact: false);
        DrawLoadMetric(graphics, new RectangleF(180 * scale, top, 78 * scale, 55 * scale), "CPU", Percent(_snapshot.CpuPercent), Temperature(_snapshot.CpuTemperatureC), _snapshot.CpuPercent);
        DrawLoadMetric(graphics, new RectangleF(270 * scale, top, 78 * scale, 55 * scale), "GPU", Percent(_snapshot.GpuPercent), Temperature(_snapshot.GpuTemperatureC), _snapshot.GpuPercent);
        DrawLoadMetric(graphics, new RectangleF(360 * scale, top, 88 * scale, 55 * scale), "MEMORY", Percent(_snapshot.MemoryPercent), $"{_snapshot.MemoryUsedGb:0.0} GB", _snapshot.MemoryPercent);
        DrawDriveMetric(graphics, new RectangleF(462 * scale, top, 102 * scale, 55 * scale), _snapshot.Drives.FirstOrDefault(), compact: false);
    }

    private void DrawLarge(Graphics graphics)
    {
        var scale = DeviceDpi / 96f;
        var top = 42 * scale;
        var metricHeight = 62 * scale;
        DrawUptime(graphics, new RectangleF(16 * scale, top, 155 * scale, metricHeight), compact: false);
        DrawLoadMetric(graphics, new RectangleF(184 * scale, top, 86 * scale, metricHeight), "CPU", Percent(_snapshot.CpuPercent), Temperature(_snapshot.CpuTemperatureC), _snapshot.CpuPercent);
        DrawLoadMetric(graphics, new RectangleF(282 * scale, top, 86 * scale, metricHeight), "GPU", Percent(_snapshot.GpuPercent), Temperature(_snapshot.GpuTemperatureC), _snapshot.GpuPercent);
        DrawLoadMetric(graphics, new RectangleF(380 * scale, top, 100 * scale, metricHeight), "MEMORY", Percent(_snapshot.MemoryPercent), $"{_snapshot.MemoryUsedGb:0.0}/{_snapshot.MemoryTotalGb:0} GB", _snapshot.MemoryPercent);
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
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var valueFont = compact ? ValueFont : UptimeFont;
        var valueHeight = TextLineHeight(graphics, valueFont, 2 * scale);
        _uptimeArea = Rectangle.Ceiling(area);

        DrawText(graphics, "UPTIME", LabelFont, Muted, new RectangleF(area.X, area.Y, area.Width, labelHeight));
        var value = compact
            ? $"{(int)_snapshot.Uptime.TotalDays:00}:{_snapshot.Uptime.Hours:00}:{_snapshot.Uptime.Minutes:00}"
            : $"{(int)_snapshot.Uptime.TotalDays:00}:{_snapshot.Uptime.Hours:00}:{_snapshot.Uptime.Minutes:00}:{_snapshot.Uptime.Seconds:00}";
        var valueTop = area.Y + labelHeight;
        DrawText(graphics, value, valueFont, Ice, new RectangleF(area.X, valueTop, area.Width, valueHeight));
        if (!compact)
        {
            var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
            DrawText(graphics, "days · hrs · min · sec", DetailFont, Muted, new RectangleF(area.X, valueTop + valueHeight, area.Width, detailHeight));
        }
    }

    private void DrawLoadMetric(Graphics graphics, RectangleF area, string label, string value, string? detail, double percent)
    {
        _taskManagerHitAreas.Add(Rectangle.Ceiling(area));
        DrawMetric(graphics, area, label, value, detail, percent);
    }

    private void DrawMetric(Graphics graphics, RectangleF area, string label, string value, string? detail, double? percent, bool warning = false)
    {
        var scale = DeviceDpi / 96f;
        var accent = warning ? Warning : Cyan;
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var valueHeight = TextLineHeight(graphics, ValueFont, 2 * scale);
        DrawText(graphics, label, LabelFont, Muted, new RectangleF(area.X, area.Y, area.Width, labelHeight));
        DrawText(graphics, value, ValueFont, warning ? Warning : Foreground, new RectangleF(area.X, area.Y + labelHeight, area.Width, valueHeight));

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
            var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
            DrawText(graphics, detail, DetailFont, warning ? Warning : Muted, new RectangleF(area.X, detailTop, area.Width, detailHeight));
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
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var valueTop = labelTop + labelHeight + scale;
        var valueHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        DrawText(graphics, label, LabelFont, Muted, new RectangleF(left, labelTop, width, labelHeight));
        DrawText(graphics, value, DetailFont, warning ? Warning : Foreground, new RectangleF(left, valueTop, width, valueHeight));
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _telemetry.SnapshotUpdated -= HandleSnapshot;
            _telemetry.HiddenMetricsUpdated -= HandleHiddenMetrics;
            _peekFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}
