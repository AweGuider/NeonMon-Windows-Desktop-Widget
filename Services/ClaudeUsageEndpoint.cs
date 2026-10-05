using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NeonMon.Models;

namespace NeonMon.Services;

// Opt-in fallback for Claude plan limits. Reads the Claude CLI sign-in from ~/.claude/.credentials.json
// without refreshing or writing it, and only issues a GET to the plan-usage endpoint (no model calls).
internal sealed class ClaudeUsageEndpoint : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(60);

    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private HttpClient? _client;
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private TimeSpan _backoff = MinimumInterval;
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public bool CanRequest(DateTimeOffset now) => now >= _nextAllowed;

    public async Task<ProviderQuota?> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (!CanRequest(now))
        {
            return null;
        }

        if (ReadCredentials(now, out var problem) is not { } credentials)
        {
            Status = problem;
            return null;
        }

        _nextAllowed = now + MinimumInterval;
        try
        {
            _client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("NeonMon/1.0");

            Interlocked.Increment(ref _requestCount);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                _backoff = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                    ? TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, _backoff.Ticks * 2))
                    : MaximumBackoff;
                _nextAllowed = now + (retryAfter is { } delay && delay > _backoff ? delay : _backoff);
                Status = $"usage endpoint returned {(int)response.StatusCode} · retry {_nextAllowed.ToLocalTime():HH:mm}";
                return null;
            }

            _backoff = MinimumInterval;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var quota = Parse(document.RootElement, credentials.Plan, DateTimeOffset.Now);
            Status = quota is null ? "usage endpoint format changed" : null;
            return quota;
        }
        catch
        {
            _backoff = TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, _backoff.Ticks * 2));
            _nextAllowed = now + _backoff;
            Status = "usage endpoint unreachable";
            return null;
        }
    }

    // Why the last attempt produced no data, or null after a successful read.
    public string? Status { get; private set; }

    internal static ProviderQuota? Parse(JsonElement root, string? plan, DateTimeOffset capturedAt)
    {
        var fiveHour = ReadWindow(root, "five_hour", 300);
        var weekly = ReadWindow(root, "seven_day", 10080);
        if (fiveHour is null && weekly is null)
        {
            return null;
        }

        return new ProviderQuota
        {
            Provider = QuotaProvider.Claude,
            Plan = plan,
            FiveHour = fiveHour,
            Weekly = weekly,
            Source = "usage endpoint",
            CapturedAt = capturedAt
        };
    }

    private static QuotaWindow? ReadWindow(JsonElement root, string name, int windowMinutes)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || QuotaJson.ReadNumber(window, "utilization") is not { } utilization)
        {
            return null;
        }

        return new QuotaWindow(utilization, QuotaJson.ReadEpoch(window, "resets_at"), windowMinutes);
    }

    private static Credentials? ReadCredentials(DateTimeOffset now, out string? problem)
    {
        problem = "Claude CLI is not signed in";
        try
        {
            if (!File.Exists(CredentialsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || QuotaJson.ReadString(oauth, "accessToken") is not { Length: > 0 } accessToken
                || QuotaJson.ReadNumber(oauth, "expiresAt") is not { } expiresAtMilliseconds)
            {
                return null;
            }

            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAtMilliseconds);
            if (expiresAt <= now.AddMinutes(1))
            {
                var local = expiresAt.ToLocalTime();
                problem = $"CLI sign-in expired {local.ToString(local.Date == now.ToLocalTime().Date ? "HH:mm" : "MMM d")} · open the claude CLI";
                return null;
            }

            problem = null;
            return new Credentials(accessToken, QuotaJson.PlanName(QuotaJson.ReadString(oauth, "subscriptionType")));
        }
        catch
        {
            problem = "could not read the Claude CLI sign-in";
            return null;
        }
    }

    public void Dispose() => _client?.Dispose();

    private sealed class Credentials(string accessToken, string? plan)
    {
        public string AccessToken { get; } = accessToken;
        public string? Plan { get; } = plan;
    }
}
