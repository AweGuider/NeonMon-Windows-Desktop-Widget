using NeonMon.Models;

namespace NeonMon.Services;

internal sealed class QuotaService : IDisposable
{
    private static readonly TimeSpan AppServerInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AppServerRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AppServerRefreshOnOpen = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan EndpointRefreshWhileOpen = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan CodexMergeHorizon = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResetJitter = TimeSpan.FromMinutes(2);

    private readonly Func<bool> _claudeEndpointEnabled;
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
    private DateTimeOffset _nextAppServerRead;
    private DateTimeOffset _lastAppServerSuccess = DateTimeOffset.MinValue;

    public QuotaService(Func<bool> claudeEndpointEnabled)
    {
        _claudeEndpointEnabled = claudeEndpointEnabled;
    }

    public QuotaSnapshot Latest { get; private set; } = QuotaSnapshot.Empty;

    public event Action<QuotaSnapshot>? SnapshotUpdated;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _nextAppServerRead = DateTimeOffset.Now.AddSeconds(5);
        _loop = Task.Run(LoopAsync);
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
            _nextAppServerRead = DateTimeOffset.Now;
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
        var quota = await _codexAppServer.ReadAsync(_cancellation.Token).ConfigureAwait(false);
        if (quota is not null)
        {
            _appServerQuota = quota;
            _lastAppServerSuccess = DateTimeOffset.Now;
        }

        await RefreshClaudeEndpointAsync().ConfigureAwait(false);
        return RefreshLocal();
    }

    public void RequestRefresh() => Wake();

    private async Task RefreshClaudeEndpointAsync()
    {
        if (!_claudeEndpointEnabled())
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var current = Newest(_claude.Read(), _endpointQuota);
        var freshEnough = current?.CapturedAt is { } capturedAt
            && (_active ? now - capturedAt < EndpointRefreshWhileOpen : !current.IsStale(now));
        if (freshEnough || !_claudeEndpoint.CanRequest(now))
        {
            return;
        }

        var quota = await _claudeEndpoint.ReadAsync(_cancellation.Token).ConfigureAwait(false);
        if (quota is not null)
        {
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
                if (DateTimeOffset.Now >= _nextAppServerRead)
                {
                    var quota = await _codexAppServer.ReadAsync(_cancellation.Token).ConfigureAwait(false);
                    if (quota is not null)
                    {
                        _appServerQuota = quota;
                        _lastAppServerSuccess = DateTimeOffset.Now;
                    }

                    _nextAppServerRead = DateTimeOffset.Now + (quota is null ? AppServerRetry : AppServerInterval);
                }

                await RefreshClaudeEndpointAsync().ConfigureAwait(false);
                var snapshot = Compose();
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

    private QuotaSnapshot Compose()
    {
        var statusline = _claude.Read();
        var claude = _claudeEndpointEnabled() ? Newest(statusline, _endpointQuota) : statusline;
        return new QuotaSnapshot(
            claude ?? new ProviderQuota { Provider = QuotaProvider.Claude },
            MergeCodex(_codexSession.Read(), _appServerQuota))
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
        _claudeEndpoint.Dispose();
    }
}
