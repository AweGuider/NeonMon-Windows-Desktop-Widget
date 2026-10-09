using NeonMon.Models;

namespace NeonMon.Services;

internal sealed class QuotaService : IDisposable
{
    private static readonly TimeSpan IdleReadInterval = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan ActivityTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan AppServerRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AppServerRefreshOnOpen = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan EndpointRefreshWhileOpen = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan CodexMergeHorizon = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResetJitter = TimeSpan.FromMinutes(2);

    private readonly Func<bool> _claudeEndpointEnabled;
    private readonly Func<QuotaProvider, TimeSpan> _activeReadInterval;
    private readonly Func<QuotaProvider, bool> _shown;
    private readonly ClaudeStatuslineReader _claude = new();
    private readonly ClaudeUsageEndpoint _claudeEndpoint = new();
    private readonly CodexSessionReader _codexSession = new();
    private readonly CodexAppServerReader _codexAppServer = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;
    private volatile bool _active;
    private volatile bool _paused;
    private ProviderQuota? _endpointQuota;
    private ProviderQuota? _appServerQuota;
    private DateTimeOffset _appServerDueAt = DateTimeOffset.MaxValue;
    private volatile bool _appServerRequested;
    private DateTimeOffset _lastAppServerAttempt;
    private bool _lastAppServerFailed;
    private DateTimeOffset _lastAppServerSuccess = DateTimeOffset.MinValue;
    private DateTimeOffset _codexActivity = DateTimeOffset.MinValue;
    private DateTimeOffset _claudeActivity = DateTimeOffset.MinValue;
    private long _claudeTranscriptWriteTicks;
    private FileSystemWatcher? _claudeTranscripts;

    // A provider hidden in settings is not read at all, so it costs nothing and shows as no data everywhere.
    public QuotaService(Func<bool> claudeEndpointEnabled, Func<QuotaProvider, TimeSpan> activeReadInterval, Func<QuotaProvider, bool> shown)
    {
        _claudeEndpointEnabled = () => shown(QuotaProvider.Claude) && claudeEndpointEnabled();
        _activeReadInterval = activeReadInterval;
        _shown = shown;
    }

    public QuotaSnapshot Latest { get; private set; } = QuotaSnapshot.Empty;

    public event Action<QuotaSnapshot>? SnapshotUpdated;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _lastAppServerAttempt = DateTimeOffset.Now;
        _appServerDueAt = DateTimeOffset.Now.AddSeconds(5);
        _claudeTranscripts = WatchClaudeTranscripts();
        _loop = Task.Run(LoopAsync);
    }

    // Claude Code transcripts (CLI and the desktop Code tab) are written on every turn, which marks Claude as in use
    // even when no statusline runs. Notifications are only enabled while the endpoint fallback can use them.
    private FileSystemWatcher? WatchClaudeTranscripts()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var watcher = new FileSystemWatcher(root, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            watcher.Changed += (_, _) => Interlocked.Exchange(ref _claudeTranscriptWriteTicks, DateTimeOffset.UtcNow.UtcTicks);
            watcher.Created += (_, _) => Interlocked.Exchange(ref _claudeTranscriptWriteTicks, DateTimeOffset.UtcNow.UtcTicks);
            return watcher;
        }
        catch
        {
            return null;
        }
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (!active)
        {
            return;
        }

        if (DateTimeOffset.Now - _lastAppServerSuccess > AppServerRefreshOnOpen)
        {
            _appServerRequested = true;
        }

        Wake();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (!paused)
        {
            Wake();
        }
    }

    public QuotaSnapshot RefreshLocal()
    {
        Latest = Compose();
        return Latest;
    }

    public async Task<QuotaSnapshot> RefreshAllAsync()
    {
        await ReadAppServerAsync().ConfigureAwait(false);
        await RefreshClaudeEndpointAsync().ConfigureAwait(false);
        return RefreshLocal();
    }

    public void RequestRefresh() => Wake();

    // A provider is active while it shows real use: a recent Codex session line, Claude statusline save or Claude Code
    // transcript write, or a provider read reporting higher usage than the previous one. An open but idle app does not count.
    internal static bool IsActive(DateTimeOffset lastActivity, DateTimeOffset now) => now - lastActivity < ActivityTimeout;

    internal static TimeSpan AppServerInterval(bool lastFailed, DateTimeOffset codexActivity, DateTimeOffset now, TimeSpan activeInterval) =>
        lastFailed ? AppServerRetry : IsActive(codexActivity, now) ? activeInterval : IdleReadInterval;

    internal static TimeSpan EndpointMaxAge(bool open, DateTimeOffset claudeActivity, DateTimeOffset now, TimeSpan activeInterval) =>
        open ? EndpointRefreshWhileOpen : IsActive(claudeActivity, now) ? activeInterval : IdleReadInterval;

    internal static bool Consumed(ProviderQuota? before, ProviderQuota? after) =>
        before is not null && after is not null && (Grew(before.FiveHour, after.FiveHour) || Grew(before.Weekly, after.Weekly));

    private static bool Grew(QuotaWindow? before, QuotaWindow? after) =>
        before?.ResetsAt is { } beforeReset && after?.ResetsAt is { } afterReset
        && (afterReset - beforeReset).Duration() <= ResetJitter
        && after.UsedPercent > before.UsedPercent;

    private bool AppServerDue(DateTimeOffset now) =>
        _appServerRequested || now >= _appServerDueAt
        || now - _lastAppServerAttempt >= AppServerInterval(_lastAppServerFailed, _codexActivity, now, _activeReadInterval(QuotaProvider.Codex));

    private async Task ReadAppServerAsync()
    {
        _appServerRequested = false;
        _appServerDueAt = DateTimeOffset.MaxValue;
        if (!_shown(QuotaProvider.Codex))
        {
            return;
        }

        var quota = await _codexAppServer.ReadAsync(_cancellation.Token).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        _lastAppServerAttempt = now;
        _lastAppServerFailed = quota is null;
        if (quota is null)
        {
            return;
        }

        if (Consumed(_appServerQuota, quota))
        {
            _codexActivity = now;
        }

        _appServerQuota = quota;
        _lastAppServerSuccess = now;
    }

    private async Task RefreshClaudeEndpointAsync()
    {
        if (!_claudeEndpointEnabled())
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var current = Newest(_claude.Read(), _endpointQuota);
        var freshEnough = current?.CapturedAt is { } capturedAt && now - capturedAt < EndpointMaxAge(_active, _claudeActivity, now, _activeReadInterval(QuotaProvider.Claude));
        if (freshEnough || !_claudeEndpoint.CanRequest(now))
        {
            return;
        }

        var quota = await _claudeEndpoint.ReadAsync(_cancellation.Token).ConfigureAwait(false);
        if (quota is not null)
        {
            if (Consumed(current, quota))
            {
                _claudeActivity = DateTimeOffset.Now;
            }

            _endpointQuota = quota;
        }
    }

    private static ProviderQuota? Newest(ProviderQuota? first, ProviderQuota? second) =>
        first is null || (second is not null && second.CapturedAt > first.CapturedAt) ? second : first;

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task LoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            if (_paused)
            {
                try
                {
                    await _wake.WaitAsync(Timeout.Infinite, _cancellation.Token).ConfigureAwait(false);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                var sessions = ReadCodexSessions();
                var statusline = ReadStatusline();
                ObserveLocalActivity(sessions, statusline);
                if (AppServerDue(DateTimeOffset.Now))
                {
                    await ReadAppServerAsync().ConfigureAwait(false);
                }

                await RefreshClaudeEndpointAsync().ConfigureAwait(false);
                var snapshot = Compose(sessions, statusline);
                Latest = snapshot;
                SnapshotUpdated?.Invoke(snapshot);
            }
            catch
            {
            }

            try
            {
                await _wake.WaitAsync(_active ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30), _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ObserveLocalActivity(IReadOnlyList<ProviderQuota> sessions, ProviderQuota? statusline)
    {
        var now = DateTimeOffset.Now;
        foreach (var capturedAt in sessions.Select(session => session.CapturedAt).OfType<DateTimeOffset>())
        {
            _codexActivity = Max(_codexActivity, Min(capturedAt, now));
        }

        if (statusline?.CapturedAt is { } savedAt)
        {
            _claudeActivity = Max(_claudeActivity, Min(savedAt, now));
        }

        if (_claudeTranscripts is not null)
        {
            _claudeTranscripts.EnableRaisingEvents = _claudeEndpointEnabled();
            if (Interlocked.Read(ref _claudeTranscriptWriteTicks) is var ticks and > 0)
            {
                _claudeActivity = Max(_claudeActivity, new DateTimeOffset(ticks, TimeSpan.Zero));
            }
        }

        static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;
        static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
    }

    private IReadOnlyList<ProviderQuota> ReadCodexSessions() => _shown(QuotaProvider.Codex) ? _codexSession.Read() : [];

    private ProviderQuota? ReadStatusline() => _shown(QuotaProvider.Claude) ? _claude.Read() : null;

    private QuotaSnapshot Compose() => Compose(ReadCodexSessions(), ReadStatusline());

    private QuotaSnapshot Compose(IReadOnlyList<ProviderQuota> sessions, ProviderQuota? statusline)
    {
        var claude = _claudeEndpointEnabled() ? Newest(statusline, _endpointQuota) : statusline;
        return new QuotaSnapshot(
            claude ?? new ProviderQuota { Provider = QuotaProvider.Claude },
            _shown(QuotaProvider.Codex) ? MergeCodex(sessions, _appServerQuota) : new ProviderQuota { Provider = QuotaProvider.Codex })
        {
            ClaudeEndpointRequests = _claudeEndpoint.RequestCount,
            ClaudeEndpointStatus = _claudeEndpointEnabled() ? _claudeEndpoint.Status : null
        };
    }

    // Parallel Codex sessions each log the limits from their own last response, so a newer line can carry an older,
    // lower value. Within one window usage only grows, so the highest recent value wins. Snapshots older than the
    // horizon are dropped, which lets a genuine mid-window reset show once stale sessions age out.
    internal static ProviderQuota MergeCodex(IReadOnlyList<ProviderQuota> sessions, ProviderQuota? appServer)
    {
        IReadOnlyList<ProviderQuota> all = appServer is null ? sessions : [.. sessions, appServer];
        if (all.Count == 0)
        {
            return new ProviderQuota { Provider = QuotaProvider.Codex };
        }

        var newest = all.MaxBy(quota => quota.CapturedAt ?? DateTimeOffset.MinValue)!;
        var horizon = (newest.CapturedAt ?? DateTimeOffset.MinValue) - CodexMergeHorizon;
        var recent = all
            .Where(quota => ReferenceEquals(quota, newest) || quota.CapturedAt >= horizon)
            .OrderByDescending(quota => quota.CapturedAt)
            .ToList();
        return newest with
        {
            Plan = newest.Plan ?? recent.Select(quota => quota.Plan).FirstOrDefault(plan => plan is not null),
            FiveHour = MergeWindow(recent.Select(quota => quota.FiveHour)),
            Weekly = MergeWindow(recent.Select(quota => quota.Weekly)),
            ResetCreditCount = appServer?.ResetCreditCount ?? 0,
            ResetCredits = appServer?.ResetCredits ?? []
        };
    }

    private static QuotaWindow? MergeWindow(IEnumerable<QuotaWindow?> windows)
    {
        var list = windows.OfType<QuotaWindow>().ToList();
        var latestReset = list.Where(window => window.ResetsAt is not null).MaxBy(window => window.ResetsAt);
        if (latestReset is null)
        {
            return list.FirstOrDefault();
        }

        return list
            .Where(window => window.ResetsAt is { } resetsAt && (latestReset.ResetsAt!.Value - resetsAt).Duration() <= ResetJitter)
            .MaxBy(window => window.UsedPercent);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }

        _cancellation.Dispose();
        _wake.Dispose();
        _claudeTranscripts?.Dispose();
        _claudeEndpoint.Dispose();
    }
}
