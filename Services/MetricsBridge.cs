using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeonMon.Models;

namespace NeonMon.Services;

internal sealed partial class MetricsBridge : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Func<TelemetrySnapshot> _snapshot;
    private readonly Func<QuotaSnapshot> _quota;
    private readonly Func<IReadOnlyCollection<string>> _allowedOrigins;
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;

    public MetricsBridge(Func<TelemetrySnapshot> snapshot, Func<QuotaSnapshot> quota, Func<IReadOnlyCollection<string>> allowedOrigins)
    {
        _snapshot = snapshot;
        _quota = quota;
        _allowedOrigins = allowedOrigins;
    }

    [GeneratedRegex(@"^https?://(localhost|127\.0\.0\.1|\[::1\])(:\d{1,5})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LoopbackOriginRegex();

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }

    public bool Start(int port)
    {
        Stop();
        try
        {
            Port = port;
            _cancellation = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start(8);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
            {
                await RespondAsync(stream, reader, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private async Task RespondAsync(NetworkStream stream, StreamReader reader, CancellationToken cancellationToken)
    {
        var request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var parts = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var method = parts.ElementAtOrDefault(0) ?? "GET";
        var path = parts.ElementAtOrDefault(1) ?? "/";

        string? origin = null;
        for (var i = 0; i < 64; i++)
        {
            var header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(header))
            {
                break;
            }

            if (header.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))
            {
                origin = header[7..].Trim();
            }
        }

        var cors = origin is not null && IsAllowedOrigin(origin)
            ? $"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\nAccess-Control-Allow-Private-Network: true\r\n"
            : origin is not null ? "Vary: Origin\r\n" : string.Empty;

        if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflight = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 204 No Content\r\n{cors}Access-Control-Allow-Methods: GET\r\n" +
                "Access-Control-Max-Age: 600\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(preflight, cancellationToken).ConfigureAwait(false);
            return;
        }

        string body;
        var status = "200 OK";
        if (path.StartsWith("/api/v1/metrics", StringComparison.OrdinalIgnoreCase))
        {
            body = JsonSerializer.Serialize(_snapshot(), JsonOptions);
        }
        else if (path.StartsWith("/api/v1/quota", StringComparison.OrdinalIgnoreCase))
        {
            body = JsonSerializer.Serialize(CreateQuotaModel(_quota(), DateTimeOffset.Now), JsonOptions);
        }
        else if (path.StartsWith("/api/v1/schema", StringComparison.OrdinalIgnoreCase))
        {
            body = JsonSerializer.Serialize(new
            {
                version = 1,
                endpoint = "/api/v1/metrics",
                quotaEndpoint = "/api/v1/quota",
                refreshRecommendedMs = 1000
            }, JsonOptions);
        }
        else
        {
            status = "404 Not Found";
            body = "{\"error\":\"Use /api/v1/metrics, /api/v1/quota or /api/v1/schema\"}";
        }

        var payload = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\nCache-Control: no-store\r\n" +
            $"{cors}Connection: close\r\n\r\n");

        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private bool IsAllowedOrigin(string origin) =>
        LoopbackOriginRegex().IsMatch(origin)
        || _allowedOrigins().Any(allowed => string.Equals(allowed.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));

    public static string SerializeQuota(QuotaSnapshot snapshot) =>
        JsonSerializer.Serialize(CreateQuotaModel(snapshot, DateTimeOffset.Now), JsonOptions);

    private static object CreateQuotaModel(QuotaSnapshot snapshot, DateTimeOffset now)
    {
        object? Window(QuotaWindow? window) => window is null ? null : new
        {
            usedPercent = window.Used(now),
            remainingPercent = window.Remaining(now),
            resetsAt = window.ResetsAt,
            windowMinutes = window.WindowMinutes
        };

        object Provider(ProviderQuota quota) => new
        {
            plan = quota.Plan,
            source = quota.Source,
            capturedAt = quota.CapturedAt,
            stale = quota.IsStale(now),
            fiveHour = Window(quota.FiveHour),
            weekly = Window(quota.Weekly),
            weeklyRunsOutFirst = quota.WeeklyRunsOutFirst(now),
            useItOrLoseIt = quota.UseItOrLoseIt(now),
            resetCredits = new { count = quota.ResetCreditCount, earliestExpiry = quota.EarliestCreditExpiry }
        };

        return new
        {
            capturedAt = now,
            claude = Provider(snapshot.Claude),
            codex = Provider(snapshot.Codex),
            claudeEndpointRequests = snapshot.ClaudeEndpointRequests
        };
    }

    public void Dispose() => Stop();
}
