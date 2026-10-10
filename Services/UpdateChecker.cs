using System.Text.Json;
using Microsoft.Win32;

namespace NeonMon.Services;

internal sealed record AvailableUpdate(Version Version, string PageUrl);

// Asks GitHub once a day whether a newer release exists. It only reads the public release list: no account, no
// token, and nothing is downloaded or run. State lives beside the settings so restarts do not repeat a request.
internal sealed class UpdateChecker : IDisposable
{
    public const string RepositoryUrl = "https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget";
    public const string ReleasesPage = RepositoryUrl + "/releases";
    // The list, not /releases/latest: that endpoint skips pre-releases, which NeonMon's releases are.
    private const string ReleasesApi = "https://api.github.com/repos/AweGuider/NeonMon-Windows-Desktop-Widget/releases?per_page=10";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromHours(1);

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonMon", "update.json");

    private readonly Func<bool> _enabled;
    private readonly Version _current;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private HttpClient? _client;
    private Task? _loop;
    private State _state = new();
    private volatile AvailableUpdate? _available;

    public UpdateChecker(Func<bool> enabled, string currentVersion)
    {
        _enabled = enabled;
        _current = ParseVersion(currentVersion) ?? new Version(0, 0, 0);
    }

    // The newer release last seen, or null when up to date or when checks are off.
    public AvailableUpdate? Available => _enabled() ? _available : null;

    // Raised after each completed check. The flag is true the first time a version is seen, and only then.
    public event Action<AvailableUpdate?, bool>? Checked;

    public void Start()
    {
        _state = LoadState();
        _available = ToUpdate(_state.LatestVersion, _state.LatestUrl);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _loop = Task.Run(LoopAsync);
    }

    // Re-evaluates the schedule, for a changed setting or a clock that moved while the PC slept.
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume)
        {
            Wake();
        }
    }

    private async Task LoopAsync()
    {
        var token = _cancellation.Token;
        try
        {
            await Task.Delay(StartDelay, token).ConfigureAwait(false);
            var retry = TimeSpan.Zero;
            var lastAttempt = DateTimeOffset.MinValue;
            while (!token.IsCancellationRequested)
            {
                var wait = Timeout.InfiniteTimeSpan;
                if (_enabled())
                {
                    var now = DateTimeOffset.UtcNow;
                    var due = retry == TimeSpan.Zero ? _state.LastCheckUtc + Interval : lastAttempt + retry;
                    // A clock set back must not postpone the check indefinitely.
                    if (now >= due || _state.LastCheckUtc > now)
                    {
                        lastAttempt = now;
                        if (await CheckAsync(token).ConfigureAwait(false))
                        {
                            retry = TimeSpan.Zero;
                            due = now + Interval;
                        }
                        else
                        {
                            retry = retry == TimeSpan.Zero ? FirstRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, Interval.Ticks));
                            due = now + retry;
                        }
                    }

                    wait = due - DateTimeOffset.UtcNow;
                    wait = wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait;
                }

                await _wake.WaitAsync(wait, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> CheckAsync(CancellationToken token)
    {
        try
        {
            _client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            request.Headers.UserAgent.ParseAdd($"NeonMon/{_current}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var latest = FindLatest(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var update = latest is not null && latest.Version > _current ? latest : null;
            var announce = update is not null && update.Version.ToString() != _state.AnnouncedVersion;
            _state.LastCheckUtc = DateTimeOffset.UtcNow;
            _state.LatestVersion = latest?.Version.ToString();
            _state.LatestUrl = latest?.PageUrl;
            if (announce)
            {
                _state.AnnouncedVersion = update!.Version.ToString();
            }

            SaveState(_state);
            _available = update;
            Checked?.Invoke(update, announce);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private AvailableUpdate? ToUpdate(string? version, string? url) =>
        ParseVersion(version) is { } parsed && parsed > _current ? new AvailableUpdate(parsed, SafePageUrl(url)) : null;

    // The highest published version in GitHub's release list; drafts and unreadable tags are skipped.
    internal static AvailableUpdate? FindLatest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        AvailableUpdate? latest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                || ParseVersion(tag.GetString()) is not { } version
                || (latest is not null && version <= latest.Version))
            {
                continue;
            }

            var url = release.TryGetProperty("html_url", out var page) && page.ValueKind == JsonValueKind.String ? page.GetString() : null;
            latest = new AvailableUpdate(version, SafePageUrl(url));
        }

        return latest;
    }

    // Accepts "v0.4.2", "0.5" and "0.5.0-beta"; always three parts so "0.5" equals "0.5.0".
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Trim().TrimStart('v', 'V');
        var end = core.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
        {
            core = core[..end];
        }

        return Version.TryParse(core, out var version) ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : null;
    }

    // Only this project's own release pages are ever opened, whatever the response says.
    private static string SafePageUrl(string? url) =>
        url is not null && url.StartsWith(ReleasesPage + "/", StringComparison.Ordinal) ? url : ReleasesPage;

    private static State LoadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State() : new State();
        }
        catch
        {
            return new State();
        }
    }

    private static void SaveState(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var temporaryPath = StatePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, StatePath, true);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _cancellation.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _client?.Dispose();
        _cancellation.Dispose();
        _wake.Dispose();
    }

    private sealed class State
    {
        public DateTimeOffset LastCheckUtc { get; set; }
        public string? LatestVersion { get; set; }
        public string? LatestUrl { get; set; }
        public string? AnnouncedVersion { get; set; }
    }
}
