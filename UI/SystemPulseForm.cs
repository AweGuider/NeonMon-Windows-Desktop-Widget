using System.Diagnostics;
using NeonMon.Models;
using NeonMon.Services;

namespace NeonMon.UI;

internal sealed class SystemPulseForm : WidgetForm
{
    private readonly TelemetryService _telemetry;
    private readonly Dictionary<string, Rectangle> _driveHitAreas = [];
    private Rectangle _uptimeArea;
    private TelemetrySnapshot _snapshot = TelemetrySnapshot.Empty;
    private Font _peekFont = null!;

    public SystemPulseForm(AppSettings settings, Action saveSettings, TelemetryService telemetry)
        : base(settings, saveSettings)
    {
        _telemetry = telemetry;
        _telemetry.SnapshotUpdated += HandleSnapshot;
        _telemetry.SetActive(false);
    }

    protected override string Title => "SYSTEM PULSE";

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

    protected override Size GetLogicalPeekSize(bool horizontal) => horizontal ? new Size(344, 28) : new Size(54, 150);

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
            _snapshot = snapshot;
            if (State != RevealState.Hidden)
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

    protected override void DrawPeek(Graphics graphics)
    {
        DrawPeekOutline(graphics);
        var scale = DeviceDpi / 96f;
        var items = PeekItems();
        var textHeight = TextLineHeight(graphics, _peekFont, 0);
        if (IsHorizontal)
        {
            var separator = " · ";
            var width = items.Sum(item => MeasureText(graphics, $"{item.Label} ", _peekFont) + MeasureText(graphics, item.Value, _peekFont))
                + (items.Count - 1) * MeasureText(graphics, separator, _peekFont);
            var x = (Width - width) / 2f;
            var y = (Height - textHeight) / 2f;
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    x = DrawPeekText(graphics, separator, Muted, x, y, textHeight);
                }

                x = DrawPeekText(graphics, $"{items[i].Label} ", Foreground, x, y, textHeight);
                x = DrawPeekText(graphics, items[i].Value, items[i].Warning ? Warning : Ice, x, y, textHeight);
            }

            return;
        }

        var top = 12 * scale;
        foreach (var item in items)
        {
            DrawPeekCentered(graphics, item.Label, Muted, top, textHeight);
            DrawPeekCentered(graphics, item.Value, item.Warning ? Warning : Ice, top + 15 * scale, textHeight);
            top += 33 * scale;
        }
    }

    private List<(string Label, string Value, bool Warning)> PeekItems()
    {
        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var drive = _snapshot.Drives.FirstOrDefault(drive => drive.Name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
            ?? _snapshot.Drives.FirstOrDefault();
        return
        [
            ("CPU", Percent(_snapshot.CpuPercent), false),
            ("GPU", Percent(_snapshot.GpuPercent), false),
            ("RAM", Percent(_snapshot.MemoryPercent), false),
            (drive?.Name ?? systemDrive, drive is null ? "—" : $"{drive.FreeGb:0}G", drive?.FreePercent < 10)
        ];
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
            _peekFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}
