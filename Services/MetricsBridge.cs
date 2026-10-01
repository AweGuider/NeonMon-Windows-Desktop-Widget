using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NeonMon.Models;

namespace NeonMon.Services;

internal sealed class MetricsBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Func<TelemetrySnapshot> _snapshot;
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;

    public MetricsBridge(Func<TelemetrySnapshot> snapshot)
    {
        _snapshot = snapshot;
    }

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
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
        {
            var request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
            var path = request.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";

            string body;
            var status = "200 OK";
            if (path.StartsWith("/api/v1/metrics", StringComparison.OrdinalIgnoreCase))
            {
                body = JsonSerializer.Serialize(_snapshot(), JsonOptions);
            }
            else if (path.StartsWith("/api/v1/schema", StringComparison.OrdinalIgnoreCase))
            {
                body = JsonSerializer.Serialize(new
                {
                    version = 1,
                    endpoint = "/api/v1/metrics",
                    refreshRecommendedMs = 1000
                }, JsonOptions);
            }
            else
            {
                status = "404 Not Found";
                body = "{\"error\":\"Use /api/v1/metrics or /api/v1/schema\"}";
            }

            var payload = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {payload.Length}\r\nCache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => Stop();
}
