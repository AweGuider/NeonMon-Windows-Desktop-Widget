namespace NeonMon.Models;

internal enum QuotaProvider
{
    Claude,
    Codex
}

internal sealed record QuotaWindow(double UsedPercent, DateTimeOffset? ResetsAt, int WindowMinutes)
{
    public bool HasReset(DateTimeOffset now) => ResetsAt is { } resetsAt && resetsAt <= now;

    public double Remaining(DateTimeOffset now) => HasReset(now) ? 100 : Math.Clamp(100 - UsedPercent, 0, 100);

    public double Used(DateTimeOffset now) => 100 - Remaining(now);

    public TimeSpan? TimeLeft(DateTimeOffset now) => ResetsAt is { } resetsAt
        ? resetsAt > now ? resetsAt - now : TimeSpan.Zero
        : null;

    public double? TimeLeftFraction(DateTimeOffset now) => ResetsAt is null || WindowMinutes <= 0
        ? null
        : Math.Clamp((ResetsAt.Value - now).TotalMinutes / WindowMinutes, 0, 1);
}

internal sealed record ResetCredit(DateTimeOffset? ExpiresAt, string? Title);

internal sealed record ProviderQuota
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(45);

    public required QuotaProvider Provider { get; init; }
    public string? Plan { get; init; }
    public QuotaWindow? FiveHour { get; init; }
    public QuotaWindow? Weekly { get; init; }
    public int ResetCreditCount { get; init; }
    public IReadOnlyList<ResetCredit> ResetCredits { get; init; } = [];
    public string Source { get; init; } = "no data";
    public DateTimeOffset? CapturedAt { get; init; }

    public bool HasData => FiveHour is not null || Weekly is not null;

    public bool IsStale(DateTimeOffset now) => CapturedAt is null || now - CapturedAt.Value > StaleAfter;

    public double? WorstRemaining(DateTimeOffset now) => (FiveHour, Weekly) switch
    {
        ({ } fiveHour, { } weekly) => Math.Min(fiveHour.Remaining(now), weekly.Remaining(now)),
        ({ } fiveHour, null) => fiveHour.Remaining(now),
        (null, { } weekly) => weekly.Remaining(now),
        _ => null
    };

    public QuotaWindow? WorstWindow(DateTimeOffset now) => WeeklyRunsOutFirst(now) ? Weekly : FiveHour ?? Weekly;

    public bool WeeklyRunsOutFirst(DateTimeOffset now) =>
        FiveHour is not null && Weekly is not null && Weekly.Remaining(now) < FiveHour.Remaining(now);

    public bool UseItOrLoseIt(DateTimeOffset now) =>
        FiveHour is { } fiveHour
        && !fiveHour.HasReset(now)
        && fiveHour.TimeLeft(now) is { } left && left <= TimeSpan.FromHours(1)
        && fiveHour.Remaining(now) >= 50
        && (Weekly?.Remaining(now) ?? 100) >= 25;

    public double? WeeklyBudgetPerDay(DateTimeOffset now) => Weekly?.TimeLeft(now) is { } left
        ? Weekly.Remaining(now) / Math.Max(left.TotalDays, 1d / 24)
        : null;

    public double? WeeklyPaceDelta(DateTimeOffset now) => Weekly?.TimeLeftFraction(now) is { } fraction
        ? Weekly.Remaining(now) - fraction * 100
        : null;

    public DateTimeOffset? EarliestCreditExpiry => ResetCredits
        .Where(credit => credit.ExpiresAt is not null)
        .Select(credit => credit.ExpiresAt)
        .Min();
}

internal sealed record QuotaSnapshot(ProviderQuota Claude, ProviderQuota Codex)
{
    public static QuotaSnapshot Empty { get; } = new(
        new ProviderQuota { Provider = QuotaProvider.Claude },
        new ProviderQuota { Provider = QuotaProvider.Codex });

    public static QuotaSnapshot Sample(DateTimeOffset now) => new(
        new ProviderQuota
        {
            Provider = QuotaProvider.Claude,
            Plan = "Pro",
            FiveHour = new QuotaWindow(19, now.AddHours(3).AddMinutes(25), 300),
            Weekly = new QuotaWindow(27, now.AddDays(4).AddHours(17), 10080),
            Source = "sample data",
            CapturedAt = now.AddMinutes(-12)
        },
        new ProviderQuota
        {
            Provider = QuotaProvider.Codex,
            Plan = "Plus",
            FiveHour = new QuotaWindow(23, now.AddMinutes(48), 300),
            Weekly = new QuotaWindow(4, now.AddDays(6).AddHours(3), 10080),
            ResetCreditCount = 2,
            ResetCredits = [new ResetCredit(now.AddDays(17), null), new ResetCredit(now.AddDays(24), null)],
            Source = "sample data",
            CapturedAt = now.AddMinutes(-1)
        });

    public int ClaudeEndpointRequests { get; init; }

    public ProviderQuota this[QuotaProvider provider] => provider == QuotaProvider.Claude ? Claude : Codex;
}
