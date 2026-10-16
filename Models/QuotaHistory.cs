namespace NeonMon.Models;

// Min-max history kept on this machine: today's daily quest and recent finished weeks, per provider.
internal sealed class QuotaHistory
{
    public const int KeptWeeks = 8;

    public ProviderHistory Claude { get; set; } = new();
    public ProviderHistory Codex { get; set; } = new();
    public bool ResultsUnseen { get; set; }

    public ProviderHistory this[QuotaProvider provider] => provider == QuotaProvider.Claude ? Claude : Codex;

    // True when something changed that should be saved.
    public bool Observe(QuotaSnapshot snapshot, DateTimeOffset now, TimeZoneInfo zone)
    {
        var claude = Claude.Observe(snapshot.Claude, now, zone, out var claudeWeekEnded);
        var codex = Codex.Observe(snapshot.Codex, now, zone, out var codexWeekEnded);
        if (claudeWeekEnded || codexWeekEnded)
        {
            ResultsUnseen = true;
        }

        return claude || codex;
    }

    public static QuotaHistory Sample(QuotaSnapshot snapshot, DateTimeOffset now, TimeZoneInfo zone) => new()
    {
        Claude = ProviderHistory.Sample(snapshot.Claude, now, zone, 84, 15, [true, true, false, true], [52, 78, 88, 47, 91, 98, 96, 97]),
        Codex = ProviderHistory.Sample(snapshot.Codex, now, zone, 98, 16, [true, false, false, false], [35, 70, 31, 81, 55, 84, 62, 74]),
        ResultsUnseen = true
    };
}

internal sealed record QuestDay(DateOnly Date, bool Done);

internal sealed record FinishedWeek(DateTimeOffset EndedAt, double Used);

internal readonly record struct DailyQuest(double Progress, double Target, bool Done)
{
    public double Left => Math.Max(0, Target - Progress);
}

internal enum QuestDayState
{
    Done,
    Missed,
    Today,
    Ahead,
    Unknown
}

internal sealed class ProviderHistory
{
    // Reset times drift by seconds between reads; a reset later by more than this starts a new week.
    private static readonly TimeSpan NewWeekGap = TimeSpan.FromHours(1);

    public DateTimeOffset? WeekResetsAt { get; set; }
    public double WeekUsed { get; set; }
    public DateOnly? Day { get; set; }
    public double DayStartRemaining { get; set; }
    public double DayTarget { get; set; }
    public List<QuestDay> Days { get; set; } = [];
    public List<FinishedWeek> Weeks { get; set; } = [];

    public bool Observe(ProviderQuota quota, DateTimeOffset now, TimeZoneInfo zone, out bool weekEnded)
    {
        weekEnded = false;
        if (quota.Weekly is not { ResetsAt: { } resetsAt } weekly || weekly.HasReset(now) || quota.CapturedAt is null)
        {
            return false;
        }

        var changed = false;
        if (WeekResetsAt is { } known && resetsAt - known > NewWeekGap)
        {
            Weeks.Add(new FinishedWeek(known, WeekUsed));
            if (Weeks.Count > QuotaHistory.KeptWeeks)
            {
                Weeks.RemoveRange(0, Weeks.Count - QuotaHistory.KeptWeeks);
            }

            Days.Clear();
            WeekUsed = 0;
            Day = null;
            weekEnded = true;
        }

        if (WeekResetsAt is null || weekEnded)
        {
            WeekResetsAt = resetsAt;
            changed = true;
        }

        var used = weekly.Used(now);
        if (used > WeekUsed)
        {
            // Saving on whole-point steps keeps last week's result close without a write on every read.
            changed |= Math.Floor(used) > Math.Floor(WeekUsed);
            WeekUsed = used;
        }

        var today = LocalDate(now, zone);
        if (Day != today)
        {
            Day = today;
            DayStartRemaining = weekly.Remaining(now);
            DayTarget = DayStartRemaining / Math.Max(1, Math.Ceiling(weekly.TimeLeft(now)!.Value.TotalDays));
            Days.RemoveAll(day => day.Date == today);
            Days.Add(new QuestDay(today, false));
            changed = true;
        }

        if (Quest(quota, now, zone) is { Done: true } && Days.FindIndex(day => day.Date == today && !day.Done) is var index and >= 0)
        {
            Days[index] = Days[index] with { Done = true };
            changed = true;
        }

        return changed;
    }

    public DailyQuest? Quest(ProviderQuota quota, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (quota.Weekly is not { } weekly || weekly.HasReset(now) || Day != LocalDate(now, zone))
        {
            return null;
        }

        var progress = Math.Max(0, DayStartRemaining - weekly.Remaining(now));
        var done = progress >= DayTarget - 0.05 || Days.Any(day => day.Date == Day && day.Done);
        return new DailyQuest(progress, DayTarget, done);
    }

    public bool QuestDoneToday(DateTimeOffset now, TimeZoneInfo zone) =>
        Days.Any(day => day.Done && day.Date == LocalDate(now, zone));

    // One entry per calendar day the current week touches, oldest first.
    public IReadOnlyList<QuestDayState> WeekDays(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (WeekResetsAt is not { } resetsAt)
        {
            return [];
        }

        var today = LocalDate(now, zone);
        var first = LocalDate(resetsAt.AddDays(-7), zone);
        var last = LocalDate(resetsAt.AddSeconds(-1), zone);
        var states = new List<QuestDayState>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var record = Days.Find(day => day.Date == date);
            states.Add(record is { Done: true } ? QuestDayState.Done
                : date == today ? QuestDayState.Today
                : date > today ? QuestDayState.Ahead
                : record is not null ? QuestDayState.Missed
                : QuestDayState.Unknown);
        }

        return states;
    }

    public int Streak(double maxedAt)
    {
        var streak = 0;
        for (var i = Weeks.Count - 1; i >= 0 && Weeks[i].Used >= maxedAt; i--)
        {
            streak++;
        }

        return streak;
    }

    private static DateOnly LocalDate(DateTimeOffset value, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);

    public static ProviderHistory Sample(ProviderQuota quota, DateTimeOffset now, TimeZoneInfo zone, double dayStart, double target,
        bool[] pastDays, double[] weeks)
    {
        var resetsAt = quota.Weekly!.ResetsAt!.Value;
        var today = LocalDate(now, zone);
        var days = new List<QuestDay>();
        var index = 0;
        for (var date = LocalDate(resetsAt.AddDays(-7), zone); date < today; date = date.AddDays(1))
        {
            days.Add(new QuestDay(date, pastDays[index++ % pastDays.Length]));
        }

        days.Add(new QuestDay(today, false));
        return new ProviderHistory
        {
            WeekResetsAt = resetsAt,
            WeekUsed = quota.Weekly.Used(now),
            Day = today,
            DayStartRemaining = dayStart,
            DayTarget = target,
            Days = days,
            Weeks = [.. weeks.Select((used, i) => new FinishedWeek(resetsAt.AddDays(-7 * (weeks.Length - i)), used))]
        };
    }
}
