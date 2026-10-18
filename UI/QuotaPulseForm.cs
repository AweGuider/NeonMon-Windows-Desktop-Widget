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
    private const float CountdownGap = 4;

    private static readonly Color ClaudeTint = Color.FromArgb(217, 119, 87);
    private static readonly Color CodexTint = Color.FromArgb(237, 237, 237);
    private static readonly Color ExhaustedText = Color.FromArgb(170, 70, 84);

    private readonly QuotaSettings _quotaSettings;
    private Font _peekFont = null!;
    private Font _chipFont = null!;
    private readonly System.Windows.Forms.Timer _pulseTimer;
    private QuotaSnapshot _snapshot = QuotaSnapshot.Empty;
    private Rectangle _claudeLaunchArea;
    private Rectangle _codexLaunchArea;
    private bool _claudeCliInstalled = true;
    private bool _codexCliInstalled = true;
    private QuotaProvider? _hoveredLaunch;
    private bool _pulseOn = true;
    private bool _showResults;
    private Rectangle _quotaTabArea;
    private Rectangle _resultsTabArea;

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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal QuotaHistory History { get; set; } = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action? HistoryChanged { get; set; }

    protected override string Title => "QUOTA PULSE";

    private bool ShowRemaining => _quotaSettings.Display == QuotaDisplay.Remaining;

    private bool MaxingOut => _quotaSettings.MaxingOut;

    private bool ShowsQuest => MaxingOut && _quotaSettings.DailyQuest;

    private bool ResultsVisible => _showResults && _quotaSettings.ResultsTab;

    private DailyQuest? Quest(ProviderQuota quota, DateTimeOffset now) => ShowsQuest ? History[quota.Provider].Quest(quota, now, TimeZone) : null;

    private bool RingLit(ProviderQuota quota, DateTimeOffset now) =>
        ShowsQuest && _quotaSettings.QuestDoneRing && History[quota.Provider].QuestDoneToday(now, TimeZone);

    private bool WeeklyAlert(ProviderQuota quota, DateTimeOffset now) => MaxingOut && _quotaSettings.WeeklyEndsSoonAlert && quota.WeeklyEndsSoon(now);

    internal void ApplyCodexBackfill(IReadOnlyList<(DateTimeOffset At, QuotaWindow Weekly)> readings)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            History.Codex.Backfill(readings, Clock());
            HistoryChanged?.Invoke();
            Invalidate();
        }));
    }

    internal void ShowResults(bool show)
    {
        _showResults = show;
        if (ResultsVisible && History.ResultsUnseen)
        {
            History.ResultsUnseen = false;
            HistoryChanged?.Invoke();
        }

        Invalidate();
    }

    internal void PostSnapshot(QuotaSnapshot snapshot)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() => SetSnapshot(snapshot)));
    }

    internal void SetSnapshot(QuotaSnapshot snapshot)
    {
        var now = Clock();
        var previousPeek = GetLogicalPeekSize(IsHorizontal);
        var previousHidden = HiddenKey(now);
        _snapshot = snapshot;
        if (History.Observe(snapshot, now, TimeZone))
        {
            HistoryChanged?.Invoke();
        }

        UpdatePulseTimer();
        if (State == RevealState.Peek && GetLogicalPeekSize(IsHorizontal) != previousPeek)
        {
            ContentSizeChanged();
        }
        else if (State != RevealState.Hidden || HiddenKey(now) != previousHidden)
        {
            Invalidate();
        }
    }

    private (int, int, int, int, int, int, bool, bool) HiddenKey(DateTimeOffset now) => (
        SegmentKey(_snapshot.Claude, _snapshot.Claude.WorstWindow(now), now), SegmentKey(_snapshot.Codex, _snapshot.Codex.WorstWindow(now), now),
        SegmentKey(_snapshot.Claude, _snapshot.Claude.Weekly, now), SegmentKey(_snapshot.Claude, _snapshot.Claude.FiveHour, now),
        SegmentKey(_snapshot.Codex, _snapshot.Codex.Weekly, now), SegmentKey(_snapshot.Codex, _snapshot.Codex.FiveHour, now),
        RingLit(_snapshot.Claude, now), RingLit(_snapshot.Codex, now));

    private int SegmentKey(ProviderQuota quota, QuotaWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            return -1;
        }

        var flags = (quota.IsStale(now) ? 1 : 0) | (quota.UseItOrLoseIt(now) ? 2 : 0) | (WeeklyAlert(quota, now) ? 4 : 0);
        return (int)Math.Round(DisplayValue(window, now)) * 8 + flags;
    }

    internal event Action<QuotaProvider>? CliRequested;

    internal void SetCliAvailability(bool claude, bool codex)
    {
        if (claude == _claudeCliInstalled && codex == _codexCliInstalled)
        {
            return;
        }

        _claudeCliInstalled = claude;
        _codexCliInstalled = codex;
        Invalidate();
    }

    private bool IsCliInstalled(QuotaProvider provider) => provider == QuotaProvider.Claude ? _claudeCliInstalled : _codexCliInstalled;

    private bool IsMissing(ProviderQuota quota) => !IsCliInstalled(quota.Provider) && !quota.HasData;

    private QuotaProvider? LaunchAreaAt(Point point) =>
        _claudeLaunchArea.Contains(point) ? QuotaProvider.Claude : _codexLaunchArea.Contains(point) ? QuotaProvider.Codex : null;

    protected override bool HandleBodyClick(Point point)
    {
        if (_quotaSettings.ResultsTab && (_quotaTabArea.Contains(point) || _resultsTabArea.Contains(point)))
        {
            ShowResults(_resultsTabArea.Contains(point));
            return true;
        }

        if (LaunchAreaAt(point) is not { } provider)
        {
            return false;
        }

        if (IsCliInstalled(provider))
        {
            CliRequested?.Invoke(provider);
        }
        else
        {
            PlaySound(SoundCue.Failure);
        }

        return true;
    }

    protected override string? GetBodyAction(Point point)
    {
        if (_quotaSettings.ResultsTab && _quotaTabArea.Contains(point))
        {
            return "quota tab";
        }

        if (_quotaSettings.ResultsTab && _resultsTabArea.Contains(point))
        {
            return "results tab";
        }

        return LaunchAreaAt(point) is { } provider ? $"launch {provider}" : null;
    }

    protected override string? GetBodyTooltip(Point point)
    {
        if (_quotaSettings.ResultsTab && _quotaTabArea.Contains(point))
        {
            return "Show quota";
        }

        if (_quotaSettings.ResultsTab && _resultsTabArea.Contains(point))
        {
            return "Show quests and past weeks";
        }

        var provider = LaunchAreaAt(point);
        SetHoveredLaunch(provider);
        return provider switch
        {
            QuotaProvider.Claude => _claudeCliInstalled ? "Open the Claude CLI to refresh Claude data" : "Claude CLI not installed",
            QuotaProvider.Codex => _codexCliInstalled ? "Open the Codex CLI" : "Codex CLI not installed",
            _ => null
        };
    }

    private void SetHoveredLaunch(QuotaProvider? provider)
    {
        if (provider != _hoveredLaunch)
        {
            _hoveredLaunch = provider;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoveredLaunch(null);
    }

    internal void RefreshDisplay()
    {
        UpdatePulseTimer();
        ContentSizeChanged();
    }

    protected override void OnRevealStateChanged(RevealState state)
    {
        base.OnRevealStateChanged(state);
        if (state == RevealState.Hidden)
        {
            _showResults = false;
        }

        UpdatePulseTimer();
    }

    // The weekly alert can last a day, so it only blinks while the strip is open; hidden it stays lit.
    private void UpdatePulseTimer()
    {
        var now = Clock();
        var pulsing = _snapshot.Claude.UseItOrLoseIt(now) || _snapshot.Codex.UseItOrLoseIt(now)
            || (State != RevealState.Hidden && (WeeklyAlert(_snapshot.Claude, now) || WeeklyAlert(_snapshot.Codex, now)));
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

    private bool TwoLines => _quotaSettings.HiddenTab == HiddenTabStyle.TwoLines;

    private ProviderQuota[] VisibleQuotas => (_quotaSettings.Shows(QuotaProvider.Claude), _quotaSettings.Shows(QuotaProvider.Codex)) switch
    {
        (true, false) => [_snapshot.Claude],
        (false, true) => [_snapshot.Codex],
        _ => [_snapshot.Claude, _snapshot.Codex]
    };

    private static Color Tint(QuotaProvider provider) => provider == QuotaProvider.Claude ? ClaudeTint : CodexTint;

    // One provider keeps the same bar length as each half of the two-provider tab.
    protected override Size GetLogicalHiddenSize(bool horizontal)
    {
        var thickness = TwoLines ? TwoLineTabThickness : 9;
        var length = VisibleQuotas.Length == 2 ? 132 : 68;
        return horizontal ? new Size(length, thickness) : new Size(thickness, length);
    }

    // The Quota / Results switch is a bordered control, so the header is taller than System pulse's plain title.
    protected override int HeaderHeight => 28 + (int)BodyShift;

    private const float BodyShift = 4;

    protected override Size GetLogicalOpenSize(WidgetSize size) => (size, VisibleQuotas.Length == 2) switch
    {
        (WidgetSize.Small, true) => new Size(400, 112 + (int)BodyShift),
        (WidgetSize.Small, false) => new Size(400, 74 + (int)BodyShift),
        (WidgetSize.Medium, true) => new Size(520, 140 + (int)BodyShift),
        (WidgetSize.Medium, false) => new Size(520, 89 + (int)BodyShift),
        (_, true) => new Size(660, 200 + (int)BodyShift),
        _ => new Size(660, 120 + (int)BodyShift)
    };

    protected override Size GetLogicalPeekSize(bool horizontal)
    {
        var now = Clock();
        return PeekSize(horizontal, VisibleQuotas.Select(quota => GetPeekItem(quota, now)).ToList());
    }

    protected override Size GetLogicalPeekReserve(bool horizontal)
    {
        var widest = new PeekItem("100%", Foreground, "100%");
        return PeekSize(horizontal, VisibleQuotas.Select(_ => widest).ToList());
    }

    private static Size PeekSize(bool horizontal, IReadOnlyList<PeekItem> items) => (horizontal, items.Count) switch
    {
        (true, 1) => new Size((int)Math.Ceiling(2 * PeekPadding + PeekBlockWidth(items[0])), 28),
        (true, _) => new Size((int)Math.Ceiling(2 * PeekPadding + PeekBlockWidth(items[0]) + PeekSeparator + PeekBlockWidth(items[1])), 28),
        (false, 1) => new Size(54, (int)Math.Ceiling(24 + PeekBlockHeight(items[0]))),
        _ => new Size(54, (int)Math.Ceiling(24 + PeekBlockHeight(items[0]) + 11 + PeekBlockHeight(items[1])))
    };

    private static float PeekTextWidth(string text) => text.Length * PeekCharWidth;

    private static float PeekBlockWidth(PeekItem item) =>
        PeekIcon + PeekIconGap + PeekTextWidth(item.Primary) + (item.Chip is null ? 0 : ChipGap + PeekTextWidth(item.Chip) + 10)
        + (item.Countdown is null ? 0 : CountdownGap + PeekTextWidth(item.Countdown));

    private static float PeekBlockHeight(PeekItem item) => 14 + 3 + 16 + (item.Chip is null ? 0 : 4 + 17)
        + (item.Countdown is null ? 0 : 15) + (item.Chip?.Contains(' ') == true ? 15 : 0);

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
        var stale = quota.IsStale(now);
        var chip = quota.WeeklyRunsOutFirst(now) ? WithReset(FormatPercent(quota.Weekly!, now), quota.Weekly!, now) : null;
        if (!ShowsReset(primary, now))
        {
            return new PeekItem(FormatPercent(primary, now), color, chip);
        }

        var exhausted = IsExhausted(primary, now);
        return new PeekItem(FormatPercent(primary, now), exhausted && !stale ? ExhaustedText : color, chip,
            PeekSpan(primary.TimeLeft(now)), exhausted && !stale ? Foreground : Muted);
    }

    // At 0% the reset time always shows; from 1% to 10% it follows the setting.
    private bool ShowsReset(QuotaWindow window, DateTimeOffset now) =>
        window.ResetsAt is not null && !window.HasReset(now)
        && (IsExhausted(window, now) || (window.Remaining(now) <= 10 && _quotaSettings.PeekResetWhenLow));

    private string WithReset(string text, QuotaWindow window, DateTimeOffset now) =>
        ShowsReset(window, now) ? $"{text} {PeekSpan(window.TimeLeft(now))}" : text;

    private static string PeekSpan(TimeSpan? span)
    {
        var value = span ?? TimeSpan.Zero;
        return value.TotalHours < 1 ? $"{Math.Max(0, (int)value.TotalMinutes)}m"
            : value.TotalDays < 1 ? $"{(int)value.TotalHours}h{value.Minutes:00}"
            : $"{(int)value.TotalDays}d{value.Hours}h";
    }

    private string FormatPercent(QuotaWindow window, DateTimeOffset now) => $"{DisplayValue(window, now):0}%";

    private double DisplayValue(QuotaWindow window, DateTimeOffset now) => ShowRemaining ? window.Remaining(now) : window.Used(now);

    private static bool IsExhausted(QuotaWindow window, DateTimeOffset now) => Math.Round(window.Remaining(now), MidpointRounding.AwayFromZero) <= 0;

    private Color StatusColor(double remaining) =>
        remaining > 25 ? Cyan
        : MaxingOut && _quotaSettings.LowQuotaColors == LowQuotaColors.Reward ? Ice
        : remaining < 10 ? Critical : Warning;

    protected override void DrawHidden(Graphics graphics)
    {
        if (!TwoLines)
        {
            DrawOneLineHidden(graphics);
            return;
        }

        var scale = DeviceDpi / 96f;
        var weeklyThickness = 4f * scale;
        var lineGap = 1.5f * scale;
        var fiveHourThickness = 3.5f * scale;
        var core = ShiftTowardEdge(DrawHiddenTab(graphics, (weeklyThickness + lineGap + fiveHourThickness) / scale), 0.5f * scale);
        var now = Clock();
        var length = IsHorizontal ? core.Width : core.Height;
        var circle = 8.5f * scale;
        var circleGap = 4 * scale;
        var middleGap = 4 * scale;
        var visible = VisibleQuotas;
        var segment = visible.Length == 2 ? (length - 2 * (circle + circleGap) - middleGap) / 2f : length - (circle + circleGap);

        var weeklyOnFarSide = Settings.DockEdge is DockEdge.Bottom or DockEdge.Right;
        var weekly = Across(core, weeklyOnFarSide ? weeklyThickness + lineGap : 0, weeklyThickness);
        var fiveHour = Across(core, weeklyOnFarSide ? 0 : weeklyThickness + lineGap, fiveHourThickness);

        // The amber outline is one pixel wide and centred on the weekly line's edge, so that edge sits on a half
        // pixel; anywhere else the outline smears across two rows.
        if (IsHorizontal)
        {
            weekly.Y = MathF.Round(weekly.Y - 0.5f) + 0.5f;
        }
        else
        {
            weekly.X = MathF.Round(weekly.X - 0.5f) + 0.5f;
        }

        FillCircleAlong(graphics, core, Tint(visible[0].Provider), 0, circle, RingLit(visible[0], now));
        var segments = new List<(ProviderQuota, float)> { (visible[0], circle + circleGap) };
        if (visible.Length == 2)
        {
            FillCircleAlong(graphics, core, Tint(visible[1].Provider), length - circle, circle, RingLit(visible[1], now));
            segments.Add((visible[1], circle + circleGap + segment + middleGap));
        }

        foreach (var (quota, start) in segments)
        {
            DrawStripSegment(graphics, weekly, quota, quota.Weekly, WeeklyAlert(quota, now), now, start, segment);
            StrokeAlong(graphics, weekly, start, segment);
            DrawStripSegment(graphics, fiveHour, quota, quota.FiveHour, quota.UseItOrLoseIt(now), now, start, segment);
        }
    }

    private void DrawOneLineHidden(Graphics graphics)
    {
        var core = DrawHiddenTab(graphics);
        var scale = DeviceDpi / 96f;
        var now = Clock();
        var length = IsHorizontal ? core.Width : core.Height;
        var cap = 4 * scale;
        var capGap = 2 * scale;
        var middleGap = 4 * scale;
        var visible = VisibleQuotas;
        var segment = visible.Length == 2 ? (length - 2 * (cap + capGap) - middleGap) / 2f : length - (cap + capGap);

        var first = visible[0];
        FillAlong(graphics, core, Tint(first.Provider), 0, cap, 1);
        DrawStripSegment(graphics, core, first, first.WorstWindow(now), OneLinePulsing(first, now), now, cap + capGap, segment);
        if (visible.Length == 2)
        {
            var second = visible[1];
            FillAlong(graphics, core, Tint(second.Provider), length - cap, cap, 1);
            DrawStripSegment(graphics, core, second, second.WorstWindow(now), OneLinePulsing(second, now), now,
                cap + capGap + segment + middleGap, segment);
        }
    }

    private bool OneLinePulsing(ProviderQuota quota, DateTimeOffset now) =>
        quota.UseItOrLoseIt(now) || (WeeklyAlert(quota, now) && quota.WorstWindow(now) == quota.Weekly);

    private RectangleF Across(RectangleF core, float offset, float thickness) => IsHorizontal
        ? new RectangleF(core.Left, core.Top + offset, core.Width, thickness)
        : new RectangleF(core.Left + offset, core.Top, thickness, core.Height);

    private void DrawStripSegment(Graphics graphics, RectangleF core, ProviderQuota quota, QuotaWindow? window, bool pulsing, DateTimeOffset now,
        float start, float length)
    {
        FillAlong(graphics, core, StripTrack, start, length, 1);
        if (window is null)
        {
            return;
        }

        var remaining = window.Remaining(now);
        if (ShowRemaining && IsExhausted(window, now))
        {
            FillAlong(graphics, core, Color.FromArgb(quota.IsStale(now) ? 60 : 120, Critical), start, length, 1);
            return;
        }

        var color = pulsing ? Ice : StatusColor(remaining);
        var alpha = quota.IsStale(now) ? 110 : pulsing && !_pulseOn ? 140 : 255;
        var fraction = (float)Math.Clamp(DisplayValue(window, now) / 100d, 0, 1);
        var thickness = IsHorizontal ? core.Height : core.Width;
        FillAlong(graphics, core, Color.FromArgb(alpha, color), start, Math.Max(thickness, length * fraction), pulsing ? 1.4f : 1);
    }

    // Fills part of the hidden-strip bar; start and length run along the strip, widen scales it across.
    private void FillAlong(Graphics graphics, RectangleF core, Color color, float start, float length, float widen) =>
        FillPill(graphics, color, AlongBounds(core, start, length, widen));

    private void StrokeAlong(Graphics graphics, RectangleF core, float start, float length)
    {
        var width = DeviceDpi / 96f;
        var bounds = AlongBounds(core, start, length, 1);
        bounds.Inflate(-width / 2f, -width / 2f);
        using var path = RoundedRectangle(bounds, Math.Min(bounds.Width, bounds.Height) / 2f);
        using var pen = new Pen(Color.FromArgb(230, Warning), width);
        graphics.DrawPath(pen, path);
    }

    private void FillCircleAlong(Graphics graphics, RectangleF core, Color color, float start, float diameter, bool ring = false)
    {
        using var brush = new SolidBrush(color);
        var bounds = IsHorizontal
            ? new RectangleF(core.Left + start, core.Top + (core.Height - diameter) / 2f, diameter, diameter)
            : new RectangleF(core.Left + (core.Width - diameter) / 2f, core.Top + start, diameter, diameter);
        graphics.FillEllipse(brush, bounds);
        if (ring)
        {
            // Inside the dot's edge, so the lit dot stays the same size.
            var width = 1.25f * DeviceDpi / 96f;
            bounds.Inflate(-width / 2f, -width / 2f);
            using var pen = new Pen(Ice, width);
            graphics.DrawEllipse(pen, bounds);
        }
    }

    private RectangleF AlongBounds(RectangleF core, float start, float length, float widen)
    {
        if (IsHorizontal)
        {
            var thickness = core.Height * widen;
            return new RectangleF(core.Left + start, core.Top + (core.Height - thickness) / 2f, length, thickness);
        }

        var across = core.Width * widen;
        return new RectangleF(core.Left + (core.Width - across) / 2f, core.Top + start, across, length);
    }

    protected override void DrawPeek(Graphics graphics)
    {
        DrawPeekOutline(graphics);
        var scale = DeviceDpi / 96f;
        var now = Clock();
        var visible = VisibleQuotas;
        if (visible.Length == 1)
        {
            var item = GetPeekItem(visible[0], now);
            if (IsHorizontal)
            {
                DrawPeekBlock(graphics, visible[0].Provider, item, (Width - MeasurePeekBlock(graphics, item)) / 2f, Height / 2f);
            }
            else
            {
                DrawPeekColumn(graphics, visible[0].Provider, item, 12 * scale);
            }

            return;
        }

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
        if (item.Countdown is not null)
        {
            width += CountdownGap * scale + MeasureText(graphics, item.Countdown, _peekFont);
        }

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
        if (item.Countdown is not null)
        {
            var countdownWidth = MeasureText(graphics, item.Countdown, _peekFont);
            DrawText(graphics, item.Countdown, _peekFont, item.CountdownColor,
                new RectangleF(x + textWidth + CountdownGap * scale, centerY - textHeight / 2f, countdownWidth + 2, textHeight));
            textWidth += CountdownGap * scale + countdownWidth;
        }

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
        if (item.Countdown is not null)
        {
            var countdownWidth = MeasureText(graphics, item.Countdown, _peekFont);
            DrawText(graphics, item.Countdown, _peekFont, item.CountdownColor, new RectangleF(centerX - countdownWidth / 2f, y - scale, countdownWidth + 2, textHeight));
            y += 15 * scale;
        }

        if (item.Chip is not null)
        {
            // The side column is too narrow for "3% 2d3h", so the chip's reset time goes underneath.
            var parts = item.Chip.Split(' ', 2);
            var chipWidth = MeasureText(graphics, parts[0], _peekFont) + 8 * scale;
            DrawWeeklyChip(graphics, parts[0], centerX - chipWidth / 2f, y + 4 * scale + 8.5f * scale);
            y += 21 * scale;
            if (parts.Length == 2)
            {
                var resetWidth = MeasureText(graphics, parts[1], _peekFont);
                DrawText(graphics, parts[1], _peekFont, Color.FromArgb(200, Warning), new RectangleF(centerX - resetWidth / 2f, y, resetWidth + 2, textHeight));
                y += 15 * scale;
            }
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

    protected override void DrawTitle(Graphics graphics)
    {
        _quotaTabArea = Rectangle.Empty;
        _resultsTabArea = Rectangle.Empty;
        if (!_quotaSettings.ResultsTab)
        {
            base.DrawTitle(graphics);
            return;
        }

        var scale = DeviceDpi / 96f;
        var x = 10 * scale;
        var top = (HeaderHeight - 18) / 2f * scale;
        var height = 18 * scale;
        var quotaWidth = MeasureText(graphics, "QUOTA", HeaderFont) + 16 * scale;
        var resultsWidth = MeasureText(graphics, "RESULTS", HeaderFont) + 16 * scale;
        _quotaTabArea = Rectangle.Round(new RectangleF(x, top, quotaWidth, height));
        _resultsTabArea = Rectangle.Round(new RectangleF(x + quotaWidth, top, resultsWidth, height));

        using var outline = RoundedRectangle(new RectangleF(x + 0.5f, top + 0.5f, quotaWidth + resultsWidth - 1, height - 1), 4 * scale);
        using (var selected = new SolidBrush(Color.FromArgb(35, Cyan)))
        {
            var state = graphics.Save();
            graphics.SetClip(outline);
            graphics.FillRectangle(selected, ResultsVisible ? _resultsTabArea : _quotaTabArea);
            graphics.Restore(state);
        }

        using (var border = new Pen(Color.FromArgb(72, 75, 226, 246)))
        {
            graphics.DrawPath(border, outline);
        }

        DrawCenteredText(graphics, "QUOTA", HeaderFont, ResultsVisible ? Muted : Cyan, _quotaTabArea);
        DrawCenteredText(graphics, "RESULTS", HeaderFont, ResultsVisible ? Cyan : Muted, _resultsTabArea);
        if (History.ResultsUnseen && !ResultsVisible)
        {
            using var dot = new SolidBrush(Cyan);
            graphics.FillEllipse(dot, _resultsTabArea.Right - 5.5f * scale, top + 2.5f * scale, 3.5f * scale, 3.5f * scale);
        }
    }

    protected override void DrawBody(Graphics graphics)
    {
        var now = Clock();
        var scale = DeviceDpi / 96f;
        var visible = VisibleQuotas;
        var size = Settings.Size;
        var (cardX, cardWidth, cardHeight, firstCard, secondCard, firstRow, secondRow) = size switch
        {
            WidgetSize.Small => (8f, 56f, 32f, 34f, 72f, 36f, 74f),
            WidgetSize.Medium => (8f, 100f, 45f, 33f, 84f, 38f, 89f),
            _ => (10f, 142f, 72f, 34f, 114f, 38f, 118f)
        };

        _claudeLaunchArea = Rectangle.Empty;
        _codexLaunchArea = Rectangle.Empty;
        for (var index = 0; index < visible.Length; index++)
        {
            var area = Rectangle.Round(new RectangleF(cardX * scale, ((index == 0 ? firstCard : secondCard) + BodyShift) * scale,
                cardWidth * scale, cardHeight * scale));
            if (visible[index].Provider == QuotaProvider.Claude)
            {
                _claudeLaunchArea = area;
            }
            else
            {
                _codexLaunchArea = area;
            }
        }

        DrawLaunchOutlines(graphics);
        if (visible.Length == 2)
        {
            var y = ((firstCard + cardHeight + secondCard) / 2f + BodyShift) * scale;
            using var divider = new Pen(Color.FromArgb(22, 75, 226, 246));
            graphics.DrawLine(divider, 16 * scale, y, Width - 16 * scale, y);
        }

        for (var index = 0; index < visible.Length; index++)
        {
            var quota = visible[index];
            var top = ((index == 0 ? firstRow : secondRow) + BodyShift) * scale;
            if (size == WidgetSize.Large)
            {
                DrawLargeCard(graphics, quota, now, top);
                if (ResultsVisible)
                {
                    DrawLargeResultsColumns(graphics, quota, now, top);
                }
                else
                {
                    DrawLargeColumns(graphics, quota, now, top);
                }

                continue;
            }

            DrawCellOutlines(graphics, ((index == 0 ? firstCard : secondCard) + BodyShift) * scale, cardHeight * scale, size);
            DrawCompactCard(graphics, quota, now, top, size);
            var small = size == WidgetSize.Small;
            var first = new RectangleF((small ? 72 : 116) * scale, top, (small ? 150 : 186) * scale, 30 * scale);
            var second = new RectangleF((small ? 236 : 318) * scale, top, (small ? 150 : 188) * scale, 30 * scale);
            if (ResultsVisible)
            {
                DrawQuestDots(graphics, quota, now, first.X, top + (small ? 8 : 4) * scale, small ? null : first.Width);
                DrawLastWeekCompact(graphics, quota, now, second, size);
            }
            else
            {
                DrawCompactCell(graphics, quota, quota.FiveHour, now, first, fiveHour: true, size);
                DrawCompactCell(graphics, quota, quota.Weekly, now, second, fiveHour: false, size);
            }
        }
    }

    // Faint outlines around the two metric cells of a Small or Medium row, lined up with the provider card.
    private void DrawCellOutlines(Graphics graphics, float top, float height, WidgetSize size)
    {
        var scale = DeviceDpi / 96f;
        var cells = size == WidgetSize.Small ? new[] { (68f, 158f), (232f, 158f) } : [(112f, 194f), (314f, 196f)];
        using var pen = new Pen(Color.FromArgb(34, 75, 226, 246));
        foreach (var (x, width) in cells)
        {
            using var path = RoundedRectangle(new RectangleF(x * scale + 0.5f, top + 0.5f, width * scale - 1, height - 1), 5 * scale);
            graphics.DrawPath(pen, path);
        }
    }

    private static string ProviderName(ProviderQuota quota) => quota.Provider == QuotaProvider.Claude ? "CLAUDE" : "CODEX";

    private void DrawLargeResultsColumns(Graphics graphics, ProviderQuota quota, DateTimeOffset now, float top)
    {
        var scale = DeviceDpi / 96f;
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var valueHeight = TextLineHeight(graphics, UptimeFont, 0);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);

        DrawText(graphics, ShowsQuest ? "QUESTS THIS WEEK" : "QUESTS", LabelFont, Muted, new RectangleF(166 * scale, top, 146 * scale, labelHeight));
        DrawQuestDots(graphics, quota, now, 166 * scale, top + labelHeight + 6 * scale, 146 * scale);

        var history = History[quota.Provider];
        var valueTop = top + labelHeight;
        var detailTop = valueTop + valueHeight + 8 * scale;
        if (history.Weeks.Count == 0)
        {
            // Until a week has finished, the current one keeps the column alive.
            DrawText(graphics, "THIS WEEK SO FAR", LabelFont, Muted, new RectangleF(330 * scale, top, 146 * scale, labelHeight));
            var current = CurrentWeekUsed(quota, now);
            DrawText(graphics, current is { } used ? $"{used:0}%" : "—", UptimeFont, current is null ? Muted : Foreground,
                new RectangleF(330 * scale, valueTop, 146 * scale, valueHeight + 2));
            var left = quota.Weekly?.TimeLeft(now) is { } span ? $" · {FormatSpan(span)} left" : string.Empty;
            DrawText(graphics, $"first week tracked{left}", DetailFont, Muted, new RectangleF(330 * scale, detailTop, 146 * scale, detailHeight));
        }
        else
        {
            DrawText(graphics, "LAST WEEK", LabelFont, Muted, new RectangleF(330 * scale, top, 146 * scale, labelHeight));
            var used = history.Weeks[^1].Used;
            var maxed = MaxingOut && used >= _quotaSettings.MaxedAt;
            var value = $"{used:0}%";
            var valueWidth = MeasureText(graphics, value, UptimeFont);
            DrawText(graphics, value, UptimeFont, maxed ? Ice : Foreground, new RectangleF(330 * scale, valueTop, valueWidth + 2, valueHeight + 2));
            if (maxed)
            {
                DrawText(graphics, "★ maxed", DetailFont, Ice,
                    new RectangleF(330 * scale + valueWidth + 5 * scale, valueTop + valueHeight - detailHeight - scale, 70 * scale, detailHeight));
                var streak = history.Streak(_quotaSettings.MaxedAt);
                DrawText(graphics, streak == 1 ? "streak 1 week" : $"streak {streak} weeks", DetailFont, Muted,
                    new RectangleF(330 * scale, detailTop, 146 * scale, detailHeight));
            }
            else if (MaxingOut)
            {
                DrawText(graphics, $"{100 - used:0}% left on the table", DetailFont, Warning, new RectangleF(330 * scale, detailTop, 146 * scale, detailHeight));
            }
            else
            {
                DrawText(graphics, "of the weekly limit used", DetailFont, Muted, new RectangleF(330 * scale, detailTop, 146 * scale, detailHeight));
            }
        }

        DrawText(graphics, "LAST 8 WEEKS", LabelFont, Muted, new RectangleF(494 * scale, top, 150 * scale, labelHeight));
        DrawWeekBars(graphics, quota, now, new RectangleF(494 * scale, top + labelHeight + 4 * scale, 150 * scale, 34 * scale));
    }

    private static double? CurrentWeekUsed(ProviderQuota quota, DateTimeOffset now) =>
        quota.Weekly is { } weekly && !weekly.HasReset(now) ? weekly.Used(now) : null;

    private void DrawLastWeekCompact(Graphics graphics, ProviderQuota quota, DateTimeOffset now, RectangleF area, WidgetSize size)
    {
        var scale = DeviceDpi / 96f;
        var history = History[quota.Provider];
        var valueHeight = TextLineHeight(graphics, ValueFont, 2 * scale);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        var detailArea = new RectangleF(area.X, area.Y + valueHeight - detailHeight - 2 * scale, area.Width, detailHeight);
        if (history.Weeks.Count == 0)
        {
            var current = CurrentWeekUsed(quota, now);
            DrawText(graphics, current is { } soFar ? $"{soFar:0}%" : "—", ValueFont, current is null ? Muted : Foreground,
                new RectangleF(area.X, area.Y, area.Width * 0.45f, valueHeight));
            var left = quota.Weekly?.TimeLeft(now) is { } span ? $" · {FormatSpan(span)} left" : string.Empty;
            DrawTextRight(graphics, size == WidgetSize.Small ? "this wk" : $"this week so far{left}", DetailFont, Muted, detailArea);
            if (size == WidgetSize.Medium)
            {
                DrawWeekBars(graphics, quota, now, new RectangleF(area.X, area.Y + valueHeight + 3 * scale, area.Width, 9 * scale));
            }

            return;
        }

        var used = history.Weeks[^1].Used;
        var maxed = MaxingOut && used >= _quotaSettings.MaxedAt;
        DrawText(graphics, $"{used:0}%", ValueFont, maxed ? Ice : Foreground, new RectangleF(area.X, area.Y, area.Width * 0.45f, valueHeight));
        var streak = history.Streak(_quotaSettings.MaxedAt);
        var detail = (size, maxed) switch
        {
            (WidgetSize.Small, true) => "last wk ★",
            (WidgetSize.Small, false) => "last wk",
            (_, true) => streak > 1 ? $"last wk ★ maxed · {streak} wk streak" : "last wk ★ maxed",
            _ => MaxingOut ? $"last wk · {100 - used:0}% unused" : "last week"
        };
        DrawTextRight(graphics, detail, DetailFont, maxed ? Ice : Muted, detailArea);
        if (size == WidgetSize.Medium)
        {
            DrawWeekBars(graphics, quota, now, new RectangleF(area.X, area.Y + valueHeight + 3 * scale, area.Width, 9 * scale));
        }
    }

    // Done days fill ice, missed days ring amber, today is a dashed ring and days still ahead are faint.
    private void DrawQuestDots(Graphics graphics, ProviderQuota quota, DateTimeOffset now, float x, float y, float? textWidth)
    {
        var scale = DeviceDpi / 96f;
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        if (!ShowsQuest)
        {
            DrawText(graphics, "daily quest is off", DetailFont, Muted, new RectangleF(x, y, (textWidth ?? 150 * scale), detailHeight));
            return;
        }

        var days = History[quota.Provider].WeekDays(now, TimeZone);
        if (days.Count == 0)
        {
            DrawText(graphics, "waiting for weekly data", DetailFont, Muted, new RectangleF(x, y, (textWidth ?? 150 * scale), detailHeight));
            return;
        }

        var size = 8 * scale;
        var gap = 4 * scale;
        var width = Math.Max(1f, scale);
        for (var i = 0; i < days.Count; i++)
        {
            var bounds = new RectangleF(x + i * (size + gap) + width / 2f, y + width / 2f, size - width, size - width);
            switch (days[i])
            {
                case QuestDayState.Done:
                    using (var brush = new SolidBrush(Ice))
                    {
                        graphics.FillEllipse(brush, x + i * (size + gap), y, size, size);
                    }

                    break;
                case QuestDayState.Missed:
                    using (var pen = new Pen(Warning, width))
                    {
                        graphics.DrawEllipse(pen, bounds);
                    }

                    break;
                case QuestDayState.Today:
                    using (var pen = new Pen(Cyan, width) { DashStyle = DashStyle.Dash })
                    {
                        graphics.DrawEllipse(pen, bounds);
                    }

                    break;
                default:
                    using (var pen = new Pen(Color.FromArgb(90, Muted), width))
                    {
                        graphics.DrawEllipse(pen, bounds);
                    }

                    break;
            }
        }

        if (textWidth is not { } available)
        {
            return;
        }

        var done = days.Count(day => day == QuestDayState.Done);
        var counted = days.Count(day => day != QuestDayState.Ahead);
        var today = History[quota.Provider].QuestDoneToday(now, TimeZone) ? "today done" : "today in progress";
        var text = Settings.Size == WidgetSize.Large ? $"{done} of {counted} · {today}" : $"{done} of {counted} quests";
        DrawText(graphics, text, DetailFont, Muted, new RectangleF(x, y + size + 5 * scale, available, detailHeight));
    }

    // Finished weeks, then the current week as an open bar that fills as it is used.
    private void DrawWeekBars(Graphics graphics, ProviderQuota quota, DateTimeOffset now, RectangleF area)
    {
        var scale = DeviceDpi / 96f;
        var history = History[quota.Provider];
        var slots = QuotaHistory.KeptWeeks;
        var gap = 3 * scale;
        var width = Math.Min(9 * scale, (area.Width - gap * (slots - 1)) / slots);
        var finished = history.Weeks.TakeLast(slots - 1).ToList();
        var offset = slots - 1 - finished.Count;
        using var empty = new SolidBrush(Track);
        for (var i = 0; i < slots - 1; i++)
        {
            var x = area.X + i * (width + gap);
            if (i < offset)
            {
                graphics.FillRectangle(empty, x, area.Bottom - 2 * scale, width, 2 * scale);
                continue;
            }

            var used = finished[i - offset].Used;
            using var brush = new SolidBrush(!MaxingOut ? Cyan : used >= _quotaSettings.MaxedAt ? Ice : used >= 70 ? Cyan : Warning);
            graphics.FillRectangle(brush, x, area.Bottom - BarHeight(used), width, BarHeight(used));
        }

        var currentX = area.X + (slots - 1) * (width + gap);
        if (CurrentWeekUsed(quota, now) is not { } current)
        {
            graphics.FillRectangle(empty, currentX, area.Bottom - 2 * scale, width, 2 * scale);
            return;
        }

        var line = Math.Max(1f, scale);
        using (var outline = new Pen(Color.FromArgb(150, Cyan), line) { DashStyle = DashStyle.Dash })
        {
            graphics.DrawRectangle(outline, currentX + line / 2f, area.Y + line / 2f, width - line, area.Height - line);
        }

        using var fill = new SolidBrush(Cyan);
        graphics.FillRectangle(fill, currentX, area.Bottom - BarHeight(current), width, BarHeight(current));

        float BarHeight(double used) => Math.Max(2 * scale, area.Height * (float)Math.Clamp(used / 100, 0, 1));
    }

    private void DrawLaunchOutlines(Graphics graphics)
    {
        DrawLaunchOutline(graphics, QuotaProvider.Claude, _claudeLaunchArea);
        DrawLaunchOutline(graphics, QuotaProvider.Codex, _codexLaunchArea);
    }

    private void DrawLaunchOutline(Graphics graphics, QuotaProvider provider, Rectangle area)
    {
        if (area.IsEmpty || !IsCliInstalled(provider))
        {
            return;
        }

        var scale = DeviceDpi / 96f;
        var hovered = _hoveredLaunch == provider;
        using var path = RoundedRectangle(new RectangleF(area.X + 0.5f, area.Y + 0.5f, area.Width - 1, area.Height - 1), 5 * scale);
        if (hovered)
        {
            using var fill = new SolidBrush(Color.FromArgb(18, 75, 226, 246));
            graphics.FillPath(fill, path);
        }

        using var border = new Pen(Color.FromArgb(hovered ? 165 : 72, 75, 226, 246));
        graphics.DrawPath(border, path);
    }

    private void DrawCompactCard(Graphics graphics, ProviderQuota quota, DateTimeOffset now, float top, WidgetSize size)
    {
        var scale = DeviceDpi / 96f;
        var small = size == WidgetSize.Small;
        DrawGlyph(graphics, quota.Provider, new RectangleF(14 * scale, top + 3 * scale, 15 * scale, 15 * scale), IsCliInstalled(quota.Provider) ? 255 : 90);

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

            var note = EndpointNote(quota, now);
            var age = IsMissing(quota) ? "CLI not installed" : note is null ? AgeText(quota, now) : note[^1];
            DrawText(graphics, age, DetailFont, IsMissing(quota) ? Muted : AgeColor(quota, now), new RectangleF(14 * scale, top + 23 * scale, 92 * scale, TextLineHeight(graphics, DetailFont, 2 * scale)));
        }
    }

    private void DrawCompactCell(Graphics graphics, ProviderQuota quota, QuotaWindow? window, DateTimeOffset now, RectangleF area, bool fiveHour, WidgetSize size)
    {
        var scale = DeviceDpi / 96f;
        var valueHeight = TextLineHeight(graphics, ValueFont, 2 * scale);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        var value = window is null ? "—" : FormatPercent(window, now);
        var valueColor = window is null ? Muted : ValueColor(quota, window, now, fiveHour);
        DrawText(graphics, value, ValueFont, valueColor, new RectangleF(area.X, area.Y, area.Width * 0.45f, valueHeight));

        var detail = window is null ? IsMissing(quota) ? "not installed" : "no data" : fiveHour ? FiveHourDetail(window, now, size) : WeeklyDetail(window, now, size);
        var flagged = fiveHour ? quota.UseItOrLoseIt(now) : WeeklyAlert(quota, now);
        DrawTextRight(graphics, detail, DetailFont, flagged ? PulseColor(Warning) : Muted,
            new RectangleF(area.X, area.Y + valueHeight - detailHeight - 2 * scale, area.Width, detailHeight));
        DrawBar(graphics, quota, window, now, new RectangleF(area.X, area.Y + valueHeight + scale, area.Width, 3 * scale),
            fiveHour ? null : QuestSlice(quota, now));
    }

    // Today's quest as a fixed span of the weekly bar: from where the bar stood at midnight to today's target.
    private (float Start, float End)? QuestSlice(ProviderQuota quota, DateTimeOffset now)
    {
        if (Quest(quota, now) is null)
        {
            return null;
        }

        var history = History[quota.Provider];
        var start = ShowRemaining ? history.DayStartRemaining - history.DayTarget : 100 - history.DayStartRemaining;
        return ((float)Math.Clamp(start / 100, 0, 1), (float)Math.Clamp((start + history.DayTarget) / 100, 0, 1));
    }

    private void DrawLargeCard(Graphics graphics, ProviderQuota quota, DateTimeOffset now, float top)
    {
        var scale = DeviceDpi / 96f;
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        var left = 16 * scale;

        DrawGlyph(graphics, quota.Provider, new RectangleF(left, top, 14 * scale, 14 * scale), IsCliInstalled(quota.Provider) ? 255 : 90);
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
            var expiry = quota.EarliestCreditExpiry is { } expires ? $" · exp {Local(expires):MMM d}" : string.Empty;
            var noun = quota.ResetCreditCount == 1 ? "reset" : "resets";
            DrawCreditChip(graphics, quota, now, $"↻ {quota.ResetCreditCount} {noun}{expiry}", left, lineTop - 1 * scale);
            lineTop += 19 * scale;
            DrawText(graphics, $"{quota.Source} · {AgeText(quota, now)}", DetailFont, AgeColor(quota, now), new RectangleF(left, lineTop, 132 * scale, detailHeight));
        }
        else if (EndpointNote(quota, now) is { } note)
        {
            DrawText(graphics, quota.CapturedAt is null ? quota.Source : $"{quota.Source} · {AgeText(quota, now)}", DetailFont, Muted,
                new RectangleF(left, lineTop, 132 * scale, detailHeight));
            for (var i = 0; i < Math.Min(2, note.Length); i++)
            {
                DrawText(graphics, note[i], DetailFont, Warning, new RectangleF(left, lineTop + (i + 1) * 14 * scale, 132 * scale, detailHeight));
            }
        }
        else if (IsMissing(quota))
        {
            DrawText(graphics, "CLI not installed", DetailFont, Muted, new RectangleF(left, lineTop, 132 * scale, detailHeight));
        }
        else
        {
            DrawText(graphics, quota.Source, DetailFont, Muted, new RectangleF(left, lineTop, 132 * scale, detailHeight));
            DrawText(graphics, AgeText(quota, now), DetailFont, AgeColor(quota, now), new RectangleF(left, lineTop + 14 * scale, 132 * scale, detailHeight));
        }
    }

    private void DrawLargeColumns(Graphics graphics, ProviderQuota quota, DateTimeOffset now, float top)
    {
        var scale = DeviceDpi / 96f;
        var verb = ShowRemaining ? "LEFT" : "USED";
        var weeklyLabel = quota.Provider == QuotaProvider.Claude ? $"WEEKLY {verb} · ALL MODELS" : $"WEEKLY {verb}";
        DrawLargeCell(graphics, quota, quota.FiveHour, now, new RectangleF(166 * scale, top, 146 * scale, 64 * scale), $"5-HOUR {verb}", fiveHour: true);
        DrawLargeCell(graphics, quota, quota.Weekly, now, new RectangleF(330 * scale, top, 146 * scale, 64 * scale), weeklyLabel, fiveHour: false);
        var lastColumn = new RectangleF(494 * scale, top, 150 * scale, 64 * scale);
        if (ShowsQuest)
        {
            DrawQuest(graphics, quota, now, lastColumn);
        }
        else
        {
            DrawBudget(graphics, quota, now, lastColumn);
        }
    }

    private void DrawQuest(Graphics graphics, ProviderQuota quota, DateTimeOffset now, RectangleF area)
    {
        var scale = DeviceDpi / 96f;
        var labelHeight = TextLineHeight(graphics, LabelFont, 2 * scale);
        var valueHeight = TextLineHeight(graphics, UptimeFont, 0);
        var detailHeight = TextLineHeight(graphics, DetailFont, 2 * scale);
        DrawText(graphics, "DAILY QUEST", LabelFont, Muted, new RectangleF(area.X, area.Y, area.Width, labelHeight));

        var barTop = area.Y + labelHeight + valueHeight + scale;
        if (Quest(quota, now) is not { } quest)
        {
            DrawText(graphics, "—", UptimeFont, Muted, new RectangleF(area.X, area.Y + labelHeight, area.Width, valueHeight + 2));
            DrawBar(graphics, quota, null, now, new RectangleF(area.X, barTop, area.Width, 3 * scale));
            DrawText(graphics, "waiting for weekly data", DetailFont, Muted, new RectangleF(area.X, barTop + 7 * scale, area.Width, detailHeight));
            return;
        }

        var stale = quota.IsStale(now);
        var value = $"{quest.Progress:0}";
        var valueWidth = MeasureText(graphics, value, UptimeFont);
        DrawText(graphics, value, UptimeFont, stale ? Muted : Foreground, new RectangleF(area.X, area.Y + labelHeight, valueWidth + 2, valueHeight + 2));
        DrawText(graphics, $"/ {quest.Target:0}%", DetailFont, Muted,
            new RectangleF(area.X + valueWidth + 4 * scale, area.Y + labelHeight + valueHeight - detailHeight - scale, 60 * scale, detailHeight));

        using (var trackBrush = new SolidBrush(Track))
        using (var fill = new SolidBrush(Color.FromArgb(stale ? 110 : 255, Cyan)))
        {
            graphics.FillRectangle(trackBrush, area.X, barTop, area.Width, 3 * scale);
            var fraction = quest.Target <= 0 ? 1 : (float)Math.Clamp(quest.Progress / quest.Target, 0, 1);
            if (fraction > 0)
            {
                graphics.FillRectangle(fill, area.X, barTop, Math.Max(2, area.Width * fraction), 3 * scale);
            }
        }

        var (detail, color) = QuestVerdict(quota, quest, now);
        DrawText(graphics, detail, DetailFont, color, new RectangleF(area.X, barTop + 7 * scale, area.Width, detailHeight));
    }

    private (string Text, Color Color) QuestVerdict(ProviderQuota quota, DailyQuest quest, DateTimeOffset now)
    {
        if (quest.Done)
        {
            return ("done for today", Ice);
        }

        var projected = quota.ProjectedWeeklyUsed(now);
        if (projected >= _quotaSettings.MaxedAt)
        {
            return ("on track to max out", Ice);
        }

        // A quarter of the week in, the average rate says enough to warn about.
        if (projected is { } used && quota.Weekly?.TimeLeftFraction(now) <= 0.75 && 100 - used >= 15)
        {
            return ($"~{100 - used:0}% will go unused", Warning);
        }

        return ($"{Math.Max(1, Math.Ceiling(quest.Left)):0}% to go today", Muted);
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
            detail = IsMissing(quota) ? "not installed" : "no data";
        }
        else if (fiveHour ? quota.UseItOrLoseIt(now) : WeeklyAlert(quota, now))
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
    }

    private void DrawBar(Graphics graphics, ProviderQuota quota, QuotaWindow? window, DateTimeOffset now, RectangleF track,
        (float Start, float End)? slice = null)
    {
        var scale = DeviceDpi / 96f;
        using var trackBrush = new SolidBrush(Track);
        graphics.FillRectangle(trackBrush, track);
        if (window is null)
        {
            return;
        }

        var color = StatusColor(window.Remaining(now));
        var alpha = quota.IsStale(now) ? 110 : 255;
        using var fill = new SolidBrush(Color.FromArgb(alpha, color));
        var fraction = (float)Math.Clamp(DisplayValue(window, now) / 100d, 0, 1);
        if (Math.Round(DisplayValue(window, now), MidpointRounding.AwayFromZero) > 0)
        {
            graphics.FillRectangle(fill, track.X, track.Y, Math.Max(2, track.Width * fraction), track.Height);
        }

        if (slice is { } quest)
        {
            // Inside the slice, the part still filled is lighter and the part not filled is tinted track.
            using var light = new SolidBrush(Color.FromArgb(alpha, (color.R + 255) / 2, (color.G + 255) / 2, (color.B + 255) / 2));
            using var tint = new SolidBrush(Color.FromArgb(quota.IsStale(now) ? 40 : 70, Cyan));
            if (fraction > quest.Start)
            {
                var end = Math.Min(fraction, quest.End);
                graphics.FillRectangle(light, track.X + track.Width * quest.Start, track.Y, track.Width * (end - quest.Start), track.Height);
            }

            if (quest.End > fraction)
            {
                var start = Math.Max(fraction, quest.Start);
                graphics.FillRectangle(tint, track.X + track.Width * start, track.Y, track.Width * (quest.End - start), track.Height);
            }
        }

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

        return (fiveHour ? quota.UseItOrLoseIt(now) : WeeklyAlert(quota, now)) ? Ice : Foreground;
    }

    private Color PulseColor(Color color) => _pulseOn ? color : Color.FromArgb(150, color);

    private DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZone);

    private string FiveHourDetail(QuotaWindow window, DateTimeOffset now, WidgetSize size)
    {
        if (window.HasReset(now) || window.ResetsAt is null)
        {
            return size == WidgetSize.Large ? "reset · awaiting fresh data" : "5h · reset";
        }

        var left = FormatSpan(window.TimeLeft(now));
        var at = Local(window.ResetsAt.Value).ToString("HH:mm");
        return size switch
        {
            WidgetSize.Small => $"5h · {left}",
            WidgetSize.Medium => $"5h · {left} · {at}",
            _ => $"resets in {left} · {at}"
        };
    }

    private string WeeklyDetail(QuotaWindow window, DateTimeOffset now, WidgetSize size)
    {
        if (window.HasReset(now) || window.ResetsAt is null)
        {
            return size == WidgetSize.Large ? "reset · awaiting fresh data" : "wk · reset";
        }

        var left = FormatSpan(window.TimeLeft(now));
        var at = Local(window.ResetsAt.Value).ToString("ddd HH:mm");
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
            return quota.Provider == QuotaProvider.Claude ? "open the claude CLI to sync" : "open Codex to sync";
        }

        var age = now - captured;
        var text = age.TotalMinutes < 1 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 48 ? $"{(int)age.TotalHours}h ago"
            : $"{(int)age.TotalDays}d ago";
        return quota.IsStale(now) ? $"{text} · stale" : text;
    }

    private static Color AgeColor(ProviderQuota quota, DateTimeOffset now) => quota.IsStale(now) ? Warning : Muted;

    // Why the Claude endpoint fallback could not refresh stale data, split into "reason" and "action" lines.
    private string[]? EndpointNote(ProviderQuota quota, DateTimeOffset now) =>
        quota.Provider == QuotaProvider.Claude && quota.IsStale(now) && _snapshot.ClaudeEndpointStatus is { } status
            ? status.Split(" · ")
            : null;

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

    private void DrawGlyph(Graphics graphics, QuotaProvider provider, RectangleF box, int alpha = 255)
    {
        var size = Math.Min(box.Width, box.Height);
        var centerX = box.X + box.Width / 2f;
        var centerY = box.Y + box.Height / 2f;

        if (provider == QuotaProvider.Claude)
        {
            using var pen = new Pen(Color.FromArgb(alpha, ClaudeTint), Math.Max(1.2f, size * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
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

        using var stroke = new Pen(Color.FromArgb(alpha, CodexTint), Math.Max(1f, size * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
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

    protected override void CreateFonts()
    {
        base.CreateFonts();
        _peekFont?.Dispose();
        _chipFont?.Dispose();
        _peekFont = CreateFont("Consolas", 10.5f, FontStyle.Regular);
        _chipFont = CreateFont("Segoe UI Symbol", 7f, FontStyle.Regular);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulseTimer.Dispose();
            _peekFont?.Dispose();
            _chipFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record PeekItem(string Primary, Color Color, string? Chip, string? Countdown = null, Color CountdownColor = default);
}
