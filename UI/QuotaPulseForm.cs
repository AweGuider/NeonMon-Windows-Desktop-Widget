using System.ComponentModel;
using System.Drawing.Drawing2D;
using NeonMon.Models;

namespace NeonMon.UI;

internal sealed class QuotaPulseForm : WidgetForm
{
    private const float PeekPadding = 12;
    private const float PeekIcon = 13;
    private const float PeekIconGap = 5;
    private const float PeekSeparator = 18;
    private const float PeekCharWidth = 8.2f;
    private const float ChipGap = 5;

    private static readonly Color ClaudeTint = Color.FromArgb(217, 119, 87);
    private static readonly Color CodexTint = Color.FromArgb(237, 237, 237);
    private static readonly Color Critical = Color.FromArgb(255, 92, 122);
    private static readonly Color StripTrack = Color.FromArgb(70, 91, 117, 126);

    private readonly QuotaSettings _quotaSettings;
    private readonly Font _peekFont = new("Consolas", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _chipFont = new("Segoe UI Symbol", 7f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly System.Windows.Forms.Timer _pulseTimer;
    private QuotaSnapshot _snapshot = QuotaSnapshot.Empty;
    private bool _pulseOn = true;

    public QuotaPulseForm(QuotaSettings settings, Action saveSettings)
        : base(settings, saveSettings)
    {
        _quotaSettings = settings;
        _pulseTimer = new System.Windows.Forms.Timer { Interval = 800 };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulseOn = !_pulseOn;
            Invalidate();
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    protected override string Title => "QUOTA PULSE";

    private bool ShowRemaining => _quotaSettings.Display == QuotaDisplay.Remaining;

    internal void SetSnapshot(QuotaSnapshot snapshot)
    {
        var previousPeek = GetLogicalPeekSize(IsHorizontal);
        _snapshot = snapshot;
        UpdatePulseTimer();
        if (State == RevealState.Peek && GetLogicalPeekSize(IsHorizontal) != previousPeek)
        {
            ContentSizeChanged();
        }
        else
        {
            Invalidate();
        }
    }

    internal void RefreshDisplay()
    {
        if (State == RevealState.Peek)
        {
            ContentSizeChanged();
        }
        else
        {
            Invalidate();
        }
    }

    private void UpdatePulseTimer()
    {
        var now = Clock();
        var pulsing = _snapshot.Claude.UseItOrLoseIt(now) || _snapshot.Codex.UseItOrLoseIt(now);
        if (pulsing && !_pulseTimer.Enabled)
        {
            _pulseTimer.Start();
        }
        else if (!pulsing && _pulseTimer.Enabled)
        {
            _pulseTimer.Stop();
            _pulseOn = true;
        }
    }

    protected override Size GetLogicalHiddenSize(bool horizontal) => horizontal ? new Size(88, 6) : new Size(6, 88);

    protected override Size GetLogicalOpenSize(WidgetSize size) => size switch
    {
        WidgetSize.Small => new Size(400, 112),
        WidgetSize.Medium => new Size(520, 128),
        _ => new Size(660, 200)
    };

    protected override Size GetLogicalPeekSize(bool horizontal)
    {
        var now = Clock();
        var claude = GetPeekItem(_snapshot.Claude, now);
        var codex = GetPeekItem(_snapshot.Codex, now);
        return horizontal
            ? new Size((int)Math.Ceiling(2 * PeekPadding + PeekBlockWidth(claude) + PeekSeparator + PeekBlockWidth(codex)), 28)
            : new Size(54, (int)Math.Ceiling(24 + PeekBlockHeight(claude) + 11 + PeekBlockHeight(codex)));
    }

    protected override Size GetLogicalPeekReserve(bool horizontal)
    {
        var widest = new PeekItem("100%", Foreground, "100%");
        return horizontal
            ? new Size((int)Math.Ceiling(2 * PeekPadding + 2 * PeekBlockWidth(widest) + PeekSeparator), 28)
            : new Size(54, (int)Math.Ceiling(24 + 2 * PeekBlockHeight(widest) + 11));
    }

    private static float PeekTextWidth(string text) => text.Length * PeekCharWidth;

    private static float PeekBlockWidth(PeekItem item) =>
        PeekIcon + PeekIconGap + PeekTextWidth(item.Primary) + (item.Chip is null ? 0 : ChipGap + PeekTextWidth(item.Chip) + 10);

    private static float PeekBlockHeight(PeekItem item) => 14 + 3 + 16 + (item.Chip is null ? 0 : 4 + 17);

    private PeekItem GetPeekItem(ProviderQuota quota, DateTimeOffset now)
    {
        var primary = quota.FiveHour ?? quota.Weekly;
        if (primary is null)
        {
            return new PeekItem("—", Muted, null);
        }

        var color = quota.IsStale(now)
            ? Muted
            : StatusColor(primary.Remaining(now)) == Cyan
                ? quota.UseItOrLoseIt(now) ? Ice : Foreground
                : StatusColor(primary.Remaining(now));
        var chip = quota.WeeklyRunsOutFirst(now) ? FormatPercent(quota.Weekly!, now) : null;
        return new PeekItem(FormatPercent(primary, now), color, chip);
    }

    private string FormatPercent(QuotaWindow window, DateTimeOffset now) => $"{DisplayValue(window, now):0}%";

    private double DisplayValue(QuotaWindow window, DateTimeOffset now) => ShowRemaining ? window.Remaining(now) : window.Used(now);

    private static Color StatusColor(double remaining) => remaining < 10 ? Critical : remaining <= 25 ? Warning : Cyan;

    protected override void DrawHidden(Graphics graphics)
    {
        using var background = new SolidBrush(Color.FromArgb(225, 6, 15, 20));
        graphics.FillRectangle(background, ClientRectangle);

        var scale = DeviceDpi / 96f;
        var now = Clock();
        var length = IsHorizontal ? Width : Height;
        var padding = 6 * scale;
        var cap = 3 * scale;
        var capGap = 2 * scale;
        var middleGap = 6 * scale;
        var segment = (length - 2 * (padding + cap + capGap) - middleGap) / 2f;
        var thickness = Math.Max(1f, scale);

        FillAlong(graphics, ClaudeTint, padding, cap, 2 * scale);
        FillAlong(graphics, CodexTint, length - padding - cap, cap, 2 * scale);
        DrawStripSegment(graphics, _snapshot.Claude, now, padding + cap + capGap, segment, thickness);
        DrawStripSegment(graphics, _snapshot.Codex, now, padding + cap + capGap + segment + middleGap, segment, thickness);
    }

    private void DrawStripSegment(Graphics graphics, ProviderQuota quota, DateTimeOffset now, float start, float length, float thickness)
    {
        FillAlong(graphics, StripTrack, start, length, thickness);
        var window = quota.WorstWindow(now);
        if (window is null)
        {
            return;
        }

        var remaining = window.Remaining(now);
        var pulsing = quota.UseItOrLoseIt(now);
        var color = pulsing ? Ice : StatusColor(remaining);
        var alpha = quota.IsStale(now) ? 110 : pulsing && !_pulseOn ? 140 : 255;
        var fraction = (float)Math.Clamp(DisplayValue(window, now) / 100d, 0, 1);
        FillAlong(graphics, Color.FromArgb(alpha, color), start, Math.Max(thickness, length * fraction), pulsing ? thickness * 1.6f : thickness);
    }

    private void FillAlong(Graphics graphics, Color color, float start, float length, float thickness)
    {
        using var brush = new SolidBrush(color);
        if (IsHorizontal)
        {
            var y = Settings.DockEdge == DockEdge.Top ? Height - thickness : 0;
            graphics.FillRectangle(brush, start, y, length, thickness);
        }
        else
        {
            var x = Settings.DockEdge == DockEdge.Left ? Width - thickness : 0;
            graphics.FillRectangle(brush, x, start, thickness, length);
        }
    }

    protected override void DrawPeek(Graphics graphics)
    {
        DrawPeekOutline(graphics);
        var scale = DeviceDpi / 96f;
        var now = Clock();
        var claude = GetPeekItem(_snapshot.Claude, now);
        var codex = GetPeekItem(_snapshot.Codex, now);

        if (IsHorizontal)
        {
            var claudeWidth = MeasurePeekBlock(graphics, claude);
            var codexWidth = MeasurePeekBlock(graphics, codex);
            var x = (Width - (claudeWidth + PeekSeparator * scale + codexWidth)) / 2f;
            var centerY = Height / 2f;
            DrawPeekBlock(graphics, QuotaProvider.Claude, claude, x, centerY);
            var separatorX = x + claudeWidth + PeekSeparator * scale / 2f;
            using var separator = new SolidBrush(Muted);
            graphics.FillEllipse(separator, separatorX - 1.2f * scale, centerY - 1.2f * scale, 2.4f * scale, 2.4f * scale);
            DrawPeekBlock(graphics, QuotaProvider.Codex, codex, x + claudeWidth + PeekSeparator * scale, centerY);
            return;
        }

        var y = 12 * scale;
        y = DrawPeekColumn(graphics, QuotaProvider.Claude, claude, y);
        using var divider = new Pen(Color.FromArgb(64, 75, 226, 246));
        graphics.DrawLine(divider, Width / 2f - 11 * scale, y + 5 * scale, Width / 2f + 11 * scale, y + 5 * scale);
        DrawPeekColumn(graphics, QuotaProvider.Codex, codex, y + 11 * scale);
    }

    private float MeasurePeekBlock(Graphics graphics, PeekItem item)
    {
        var scale = DeviceDpi / 96f;
        var width = (PeekIcon + PeekIconGap) * scale + MeasureText(graphics, item.Primary, _peekFont);
        if (item.Chip is not null)
        {
            width += ChipGap * scale + MeasureText(graphics, item.Chip, _peekFont) + 8 * scale;
        }

        return width;
    }

    private void DrawPeekBlock(Graphics graphics, QuotaProvider provider, PeekItem item, float x, float centerY)
    {
        var scale = DeviceDpi / 96f;
        DrawGlyph(graphics, provider, new RectangleF(x, centerY - PeekIcon * scale / 2f, PeekIcon * scale, PeekIcon * scale));
        x += (PeekIcon + PeekIconGap) * scale;
        var textHeight = TextLineHeight(graphics, _peekFont, 0);
        var textWidth = MeasureText(graphics, item.Primary, _peekFont);
        DrawText(graphics, item.Primary, _peekFont, item.Color, new RectangleF(x, centerY - textHeight / 2f, textWidth + 2, textHeight));
        if (item.Chip is not null)
        {
            DrawWeeklyChip(graphics, item.Chip, x + textWidth + ChipGap * scale, centerY);
        }
    }

    private float DrawPeekColumn(Graphics graphics, QuotaProvider provider, PeekItem item, float y)
    {
        var scale = DeviceDpi / 96f;
        var centerX = Width / 2f;
        DrawGlyph(graphics, provider, new RectangleF(centerX - 7 * scale, y, 14 * scale, 14 * scale));
        y += 17 * scale;
        var textHeight = TextLineHeight(graphics, _peekFont, 0);
        var textWidth = MeasureText(graphics, item.Primary, _peekFont);
        DrawText(graphics, item.Primary, _peekFont, item.Color, new RectangleF(centerX - textWidth / 2f, y, textWidth + 2, textHeight));
        y += 16 * scale;
        if (item.Chip is not null)
        {
            var chipWidth = MeasureText(graphics, item.Chip, _peekFont) + 8 * scale;
            DrawWeeklyChip(graphics, item.Chip, centerX - chipWidth / 2f, y + 4 * scale + 8.5f * scale);
            y += 21 * scale;
        }

        return y;
    }

    private void DrawWeeklyChip(Graphics graphics, string text, float x, float centerY)
    {
        var scale = DeviceDpi / 96f;
        var textWidth = MeasureText(graphics, text, _peekFont);
        var textHeight = TextLineHeight(graphics, _peekFont, 0);
        var chip = new RectangleF(x, centerY - 8.5f * scale, textWidth + 8 * scale, 17 * scale);
        using var path = RoundedRectangle(chip, 4 * scale);
        using var fill = new SolidBrush(Color.FromArgb(42, Warning));
        using var border = new Pen(Color.FromArgb(170, Warning));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        DrawText(graphics, text, _peekFont, Warning, new RectangleF(x + 4 * scale, centerY - textHeight / 2f, textWidth + 2, textHeight));
    }

    protected override void DrawBody(Graphics graphics)
    {
        var now = Clock();
        var scale = DeviceDpi / 96f;
        switch (Settings.Size)
        {
            case WidgetSize.Small:
                DrawCompactRow(graphics, _snapshot.Claude, now, 36 * scale, WidgetSize.Small);
                DrawCompactRow(graphics, _snapshot.Codex, now, 74 * scale, WidgetSize.Small);
                break;
            case WidgetSize.Medium:
                DrawCompactRow(graphics, _snapshot.Claude, now, 38 * scale, WidgetSize.Medium);
                DrawCompactRow(graphics, _snapshot.Codex, now, 82 * scale, WidgetSize.Medium);
                break;
            default:
                DrawLargeRow(graphics, _snapshot.Claude, now, 38 * scale);
                using (var divider = new Pen(Color.FromArgb(22, 75, 226, 246)))
                {
                    graphics.DrawLine(divider, 16 * scale, 110 * scale, Width - 16 * scale, 110 * scale);
                }

                DrawLargeRow(graphics, _snapshot.Codex, now, 118 * scale);
                break;
        }
    }

    private void DrawCompactRow(Graphics graphics, ProviderQuota quota, DateTimeOffset now, float top, WidgetSize size)
    {
        var scale = DeviceDpi / 96f;
        var small = size == WidgetSize.Small;
        DrawGlyph(graphics, quota.Provider, new RectangleF(14 * scale, top + 3 * scale, 15 * scale, 15 * scale));

        if (small)
        {
            if (quota.ResetCreditCount > 0)
            {
                DrawCreditChip(graphics, quota, now, $"↻{quota.ResetCreditCount}", 33 * scale, top + 2.5f * scale);
            }
        }
        else
        {
            var chipRight = 33 * scale;
            if (quota.Plan is not null)
            {
                chipRight += DrawOutlineChip(graphics, quota.Plan, chipRight, top + 2.5f * scale, Muted, Color.FromArgb(46, 75, 226, 246)) + 4 * scale;
            }

            if (quota.ResetCreditCount > 0)
            {
                DrawCreditChip(graphics, quota, now, $"↻{quota.ResetCreditCount}", chipRight, top + 2.5f * scale);
            }

            var age = AgeText(quota, now);
            DrawText(graphics, age, DetailFont, AgeColor(quota, now), new RectangleF(14 * scale, top + 23 * scale, 92 * scale, TextLineHeight(graphics, DetailFont, 2 * scale)));
        }

        var firstX = (small ? 72 : 116) * scale;
        var firstWidth = (small ? 150 : 186) * scale;
        var secondX = (small ? 236 : 318) * scale;
        var secondWidth = (small ? 150 : 188) * scale;
        DrawCompactCell(graphics, quota, quota.FiveHour, now, new RectangleF(firstX, top, firstWidth, 30 * scale), fiveHour: true, size);
        DrawCompactCell(graphics, quota, quota.Weekly, now, new RectangleF(secondX, top, secondWidth, 30 * scale), fiveHour: false, size);
    }

    private void DrawCompactCell(Graphics graphics, ProviderQuota quota, QuotaWindow? window, DateTimeOffset now, RectangleF area, bool fiveHour, WidgetSize size)
    {
        var scale = DeviceDpi / 96f;
        var valueHeight = TextLineHeight(graphics, ValueFont, 2 * scale);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        var value = window is null ? "—" : FormatPercent(window, now);
        var valueColor = window is null ? Muted : ValueColor(quota, window, now, fiveHour);
        DrawText(graphics, value, ValueFont, valueColor, new RectangleF(area.X, area.Y, area.Width * 0.45f, valueHeight));

        var detail = window is null ? "no data" : fiveHour ? FiveHourDetail(window, now, size) : WeeklyDetail(window, now, size);
        var flagged = fiveHour && quota.UseItOrLoseIt(now);
        DrawTextRight(graphics, detail, DetailFont, flagged ? PulseColor(Warning) : Muted,
            new RectangleF(area.X, area.Y + valueHeight - detailHeight - 2 * scale, area.Width, detailHeight));
        DrawBar(graphics, quota, window, now, new RectangleF(area.X, area.Y + valueHeight + scale, area.Width, 3 * scale));
    }

    private void DrawLargeRow(Graphics graphics, ProviderQuota quota, DateTimeOffset now, float top)
    {
        var scale = DeviceDpi / 96f;
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        var left = 16 * scale;

        DrawGlyph(graphics, quota.Provider, new RectangleF(left, top, 14 * scale, 14 * scale));
        var name = quota.Provider == QuotaProvider.Claude ? "CLAUDE" : "CODEX";
        var nameWidth = MeasureText(graphics, name, LabelFont);
        DrawText(graphics, name, LabelFont, Muted, new RectangleF(left + 19 * scale, top + scale, nameWidth + 2, labelHeight));
        if (quota.Plan is not null)
        {
            DrawOutlineChip(graphics, quota.Plan, left + 25 * scale + nameWidth, top, Muted, Color.FromArgb(46, 75, 226, 246));
        }

        var lineTop = top + 20 * scale;
        if (quota.ResetCreditCount > 0)
        {
            var expiry = quota.EarliestCreditExpiry is { } expires ? $" · exp {expires.ToLocalTime():MMM d}" : string.Empty;
            var noun = quota.ResetCreditCount == 1 ? "reset" : "resets";
            DrawCreditChip(graphics, quota, now, $"↻ {quota.ResetCreditCount} {noun}{expiry}", left, lineTop - 1 * scale);
            lineTop += 19 * scale;
            DrawText(graphics, $"{quota.Source} · {AgeText(quota, now)}", DetailFont, AgeColor(quota, now), new RectangleF(left, lineTop, 132 * scale, detailHeight));
        }
        else
        {
            DrawText(graphics, quota.Source, DetailFont, Muted, new RectangleF(left, lineTop, 132 * scale, detailHeight));
            DrawText(graphics, AgeText(quota, now), DetailFont, AgeColor(quota, now), new RectangleF(left, lineTop + 14 * scale, 132 * scale, detailHeight));
        }

        var verb = ShowRemaining ? "LEFT" : "USED";
        var weeklyLabel = quota.Provider == QuotaProvider.Claude ? $"WEEKLY {verb} · ALL MODELS" : $"WEEKLY {verb}";
        DrawLargeCell(graphics, quota, quota.FiveHour, now, new RectangleF(166 * scale, top, 146 * scale, 64 * scale), $"5-HOUR {verb}", fiveHour: true);
        DrawLargeCell(graphics, quota, quota.Weekly, now, new RectangleF(330 * scale, top, 146 * scale, 64 * scale), weeklyLabel, fiveHour: false);
        DrawBudget(graphics, quota, now, new RectangleF(494 * scale, top, 150 * scale, 64 * scale));
    }

    private void DrawLargeCell(Graphics graphics, ProviderQuota quota, QuotaWindow? window, DateTimeOffset now, RectangleF area, string label, bool fiveHour)
    {
        var scale = DeviceDpi / 96f;
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var valueHeight = TextLineHeight(graphics, UptimeFont, 0);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        DrawText(graphics, label, LabelFont, Muted, new RectangleF(area.X, area.Y, area.Width, labelHeight));

        var value = window is null ? "—" : FormatPercent(window, now);
        var valueColor = window is null ? Muted : ValueColor(quota, window, now, fiveHour);
        DrawText(graphics, value, UptimeFont, valueColor, new RectangleF(area.X, area.Y + labelHeight, area.Width, valueHeight + 2));

        var barTop = area.Y + labelHeight + valueHeight + scale;
        DrawBar(graphics, quota, window, now, new RectangleF(area.X, barTop, area.Width, 3 * scale));

        string detail;
        var detailColor = Muted;
        if (window is null)
        {
            detail = "no data";
        }
        else if (fiveHour && quota.UseItOrLoseIt(now))
        {
            detail = $"▲ {window.Remaining(now):0}% left · resets in {FormatSpan(window.TimeLeft(now))}";
            detailColor = PulseColor(Warning);
        }
        else
        {
            detail = fiveHour ? FiveHourDetail(window, now, WidgetSize.Large) : WeeklyDetail(window, now, WidgetSize.Large);
        }

        DrawText(graphics, detail, DetailFont, detailColor, new RectangleF(area.X, barTop + 7 * scale, area.Width, detailHeight));
    }

    private void DrawBudget(Graphics graphics, ProviderQuota quota, DateTimeOffset now, RectangleF area)
    {
        var scale = DeviceDpi / 96f;
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var valueHeight = TextLineHeight(graphics, ValueFont, 2 * scale);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        DrawText(graphics, "WEEKLY BUDGET", LabelFont, Muted, new RectangleF(area.X, area.Y, area.Width, labelHeight));

        var budget = quota.WeeklyBudgetPerDay(now);
        if (budget is null)
        {
            DrawText(graphics, "—", ValueFont, Muted, new RectangleF(area.X, area.Y + labelHeight, area.Width, valueHeight));
            return;
        }

        var value = $"{budget.Value:0.0}%";
        var valueWidth = MeasureText(graphics, value, ValueFont);
        var valueTop = area.Y + labelHeight;
        DrawText(graphics, value, ValueFont, quota.IsStale(now) ? Muted : Ice, new RectangleF(area.X, valueTop, valueWidth + 2, valueHeight));
        DrawText(graphics, "/day", DetailFont, Muted, new RectangleF(area.X + valueWidth + 4 * scale, valueTop + valueHeight - detailHeight - 2 * scale, 40 * scale, detailHeight));

        if (quota.WeeklyPaceDelta(now) is { } delta)
        {
            var ahead = delta >= 0;
            var text = ahead ? $"{delta:0}% ahead of pace" : $"{-delta:0}% behind pace";
            DrawText(graphics, text, DetailFont, ahead ? Muted : Warning, new RectangleF(area.X, valueTop + valueHeight + 2 * scale, area.Width, detailHeight));
        }
    }

    private void DrawBar(Graphics graphics, ProviderQuota quota, QuotaWindow? window, DateTimeOffset now, RectangleF track)
    {
        var scale = DeviceDpi / 96f;
        using var trackBrush = new SolidBrush(Track);
        graphics.FillRectangle(trackBrush, track);
        if (window is null)
        {
            return;
        }

        var color = StatusColor(window.Remaining(now));
        using var fill = new SolidBrush(quota.IsStale(now) ? Color.FromArgb(110, color) : color);
        var fraction = (float)Math.Clamp(DisplayValue(window, now) / 100d, 0, 1);
        graphics.FillRectangle(fill, track.X, track.Y, Math.Max(2, track.Width * fraction), track.Height);

        if (window.TimeLeftFraction(now) is { } timeLeft)
        {
            var tickFraction = ShowRemaining ? timeLeft : 1 - timeLeft;
            var x = track.X + track.Width * (float)tickFraction;
            using var tick = new SolidBrush(Color.FromArgb(215, Ice));
            graphics.FillRectangle(tick, x - 0.5f * scale, track.Y - 3 * scale, Math.Max(1f, scale), 9 * scale);
        }
    }

    private Color ValueColor(ProviderQuota quota, QuotaWindow window, DateTimeOffset now, bool fiveHour)
    {
        if (quota.IsStale(now))
        {
            return Muted;
        }

        var status = StatusColor(window.Remaining(now));
        if (status != Cyan)
        {
            return status;
        }

        return fiveHour && quota.UseItOrLoseIt(now) ? Ice : Foreground;
    }

    private Color PulseColor(Color color) => _pulseOn ? color : Color.FromArgb(150, color);

    private static string FiveHourDetail(QuotaWindow window, DateTimeOffset now, WidgetSize size)
    {
        if (window.HasReset(now) || window.ResetsAt is null)
        {
            return size == WidgetSize.Large ? "reset · awaiting fresh data" : "5h · reset";
        }

        var left = FormatSpan(window.TimeLeft(now));
        var at = window.ResetsAt.Value.ToLocalTime().ToString("HH:mm");
        return size switch
        {
            WidgetSize.Small => $"5h · {left}",
            WidgetSize.Medium => $"5h · {left} · {at}",
            _ => $"resets in {left} · {at}"
        };
    }

    private static string WeeklyDetail(QuotaWindow window, DateTimeOffset now, WidgetSize size)
    {
        if (window.HasReset(now) || window.ResetsAt is null)
        {
            return size == WidgetSize.Large ? "reset · awaiting fresh data" : "wk · reset";
        }

        var left = FormatSpan(window.TimeLeft(now));
        var at = window.ResetsAt.Value.ToLocalTime().ToString("ddd HH:mm");
        return size switch
        {
            WidgetSize.Small => $"wk · {at}",
            WidgetSize.Medium => $"wk · {at} · {left}",
            _ => $"resets {at} · {left}"
        };
    }

    private static string FormatSpan(TimeSpan? span)
    {
        if (span is not { } value)
        {
            return "—";
        }

        return value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d{value.Hours:00}h"
            : $"{(int)value.TotalHours}h{value.Minutes:00}m";
    }

    private static string AgeText(ProviderQuota quota, DateTimeOffset now)
    {
        if (quota.CapturedAt is not { } captured)
        {
            return quota.Provider == QuotaProvider.Claude ? "no data · open the claude CLI" : "no data · open Codex";
        }

        var age = now - captured;
        var text = age.TotalMinutes < 1 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 48 ? $"{(int)age.TotalHours}h ago"
            : $"{(int)age.TotalDays}d ago";
        return quota.IsStale(now) ? $"{text} · stale" : text;
    }

    private static Color AgeColor(ProviderQuota quota, DateTimeOffset now) => quota.IsStale(now) ? Warning : Muted;

    private void DrawCreditChip(Graphics graphics, ProviderQuota quota, DateTimeOffset now, string text, float x, float y)
    {
        var expiringSoon = quota.EarliestCreditExpiry is { } expires && expires - now <= TimeSpan.FromDays(3);
        var color = expiringSoon ? Warning : Ice;
        DrawOutlineChip(graphics, text, x, y, color, Color.FromArgb(100, color));
    }

    private float DrawOutlineChip(Graphics graphics, string text, float x, float y, Color textColor, Color borderColor)
    {
        var scale = DeviceDpi / 96f;
        var textWidth = MeasureText(graphics, text, _chipFont);
        var textHeight = TextLineHeight(graphics, _chipFont, 0);
        var chip = new RectangleF(x, y, textWidth + 9 * scale, Math.Max(14 * scale, textHeight + 2 * scale));
        using var path = RoundedRectangle(chip, 3.5f * scale);
        using var border = new Pen(borderColor);
        graphics.DrawPath(border, path);
        DrawText(graphics, text, _chipFont, textColor, new RectangleF(x + 4.5f * scale, y + (chip.Height - textHeight) / 2f, textWidth + 2, textHeight));
        return chip.Width;
    }

    private void DrawGlyph(Graphics graphics, QuotaProvider provider, RectangleF box)
    {
        var size = Math.Min(box.Width, box.Height);
        var centerX = box.X + box.Width / 2f;
        var centerY = box.Y + box.Height / 2f;

        if (provider == QuotaProvider.Claude)
        {
            using var pen = new Pen(ClaudeTint, Math.Max(1.2f, size * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var radius = size * 0.42f;
            for (var i = 0; i < 4; i++)
            {
                var angle = i * Math.PI / 4;
                var dx = (float)(Math.Cos(angle) * radius);
                var dy = (float)(Math.Sin(angle) * radius);
                graphics.DrawLine(pen, centerX - dx, centerY - dy, centerX + dx, centerY + dy);
            }

            return;
        }

        using var stroke = new Pen(CodexTint, Math.Max(1f, size * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var frame = new RectangleF(centerX - size * 0.45f, centerY - size * 0.4f, size * 0.9f, size * 0.8f);
        using var framePath = RoundedRectangle(frame, size * 0.18f);
        graphics.DrawPath(stroke, framePath);
        graphics.DrawLines(stroke, new[]
        {
            new PointF(frame.X + size * 0.2f, centerY - size * 0.14f),
            new PointF(frame.X + size * 0.34f, centerY),
            new PointF(frame.X + size * 0.2f, centerY + size * 0.14f)
        });
        graphics.DrawLine(stroke, centerX + size * 0.02f, centerY + size * 0.16f, centerX + size * 0.24f, centerY + size * 0.16f);
    }

    private static float MeasureText(Graphics graphics, string text, Font font) =>
        graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    private static void DrawTextRight(Graphics graphics, string value, Font font, Color color, RectangleF bounds)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(value, font, brush, bounds, format);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulseTimer.Dispose();
            _peekFont.Dispose();
            _chipFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record PeekItem(string Primary, Color Color, string? Chip);
}
