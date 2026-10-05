using NeonMon.Models;

namespace NeonMon.Services;

internal sealed class QuotaService : IDisposable
{
    private static readonly TimeSpan AppServerInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AppServerRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AppServerRefreshOnOpen = TimeSpan.FromMinutes(10);

    private readonly ClaudeStatuslineReader _claude = new();
    private readonly CodexSessionReader _codexSession = new();
    private readonly CodexAppServerReader _codexAppServer = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;
    private volatile bool _active;
    private ProviderQuota? _appServerQuota;
    private DateTimeOffset _nextAppServerRead;
    private DateTimeOffset _lastAppServerSuccess = DateTimeOffset.MinValue;

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

        return RefreshLocal();
    }

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

    private QuotaSnapshot Compose() => new(
        _claude.Read() ?? new ProviderQuota { Provider = QuotaProvider.Claude },
        MergeCodex(_codexSession.Read(), _appServerQuota));

    private static ProviderQuota MergeCodex(ProviderQuota? session, ProviderQuota? appServer)
    {
        if (session is null && appServer is null)
        {
            return new ProviderQuota { Provider = QuotaProvider.Codex };
        }

        var newest = session is null || (appServer is not null && appServer.CapturedAt > session.CapturedAt)
            ? appServer!
            : session;
        return newest with
        {
            Plan = newest.Plan ?? session?.Plan ?? appServer?.Plan,
            ResetCreditCount = appServer?.ResetCreditCount ?? 0,
            ResetCredits = appServer?.ResetCredits ?? []
        };
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
    }
}
