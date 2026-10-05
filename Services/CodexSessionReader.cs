using System.Text;
using System.Text.Json;
using NeonMon.Models;

namespace NeonMon.Services;

internal sealed class CodexSessionReader
{
    private const int InitialTail = 256 * 1024;
    private const int MaxTail = 4 * 1024 * 1024;

    private static readonly string SessionsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    private string? _path;
    private long _length;
    private DateTime _lastWrite;
    private ProviderQuota? _latest;

    public ProviderQuota? Read()
    {
        try
        {
            var newest = FindNewestSession();
            if (newest is null)
            {
                return _latest;
            }

            if (newest.FullName == _path && newest.Length == _length && newest.LastWriteTimeUtc == _lastWrite)
            {
                return _latest;
            }

            _path = newest.FullName;
            _length = newest.Length;
            _lastWrite = newest.LastWriteTimeUtc;
            _latest = ReadLatestRateLimits(newest) ?? _latest;
            return _latest;
        }
        catch
        {
            return _latest;
        }
    }

    private static FileInfo? FindNewestSession()
    {
        if (!Directory.Exists(SessionsRoot))
        {
            return null;
        }

        var dayDirectories = Directory.EnumerateDirectories(SessionsRoot)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .SelectMany(year => Directory.EnumerateDirectories(year).OrderByDescending(path => path, StringComparer.Ordinal))
            .SelectMany(month => Directory.EnumerateDirectories(month).OrderByDescending(path => path, StringComparer.Ordinal))
            .Where(day => Directory.EnumerateFiles(day, "rollout-*.jsonl").Any())
            .Take(2);

        FileInfo? newest = null;
        foreach (var directory in dayDirectories)
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("rollout-*.jsonl"))
            {
                if (newest is null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                {
                    newest = file;
                }
            }
        }

        return newest;
    }

    private static ProviderQuota? ReadLatestRateLimits(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        for (var tail = InitialTail; ; tail *= 2)
        {
            var size = (int)Math.Min(length, tail);
            var buffer = new byte[size];
            stream.Seek(length - size, SeekOrigin.Begin);
            stream.ReadExactly(buffer);

            var quota = ParseLastRateLimits(Encoding.UTF8.GetString(buffer), size == length);
            if (quota is not null || size == length || tail >= MaxTail)
            {
                return quota;
            }
        }
    }

    private static ProviderQuota? ParseLastRateLimits(string text, bool startsAtFileStart)
    {
        var searchEnd = text.Length;
        while (searchEnd > 0)
        {
            var index = text.LastIndexOf("\"rate_limits\":{", searchEnd - 1, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            var lineStart = text.LastIndexOf('\n', index);
            if (lineStart < 0 && !startsAtFileStart)
            {
                return null;
            }

            lineStart++;
            var lineEnd = text.IndexOf('\n', index);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var quota = TryParseLine(text[lineStart..lineEnd]);
            if (quota is not null)
            {
                return quota;
            }

            searchEnd = lineStart;
        }

        return null;
    }

    private static ProviderQuota? TryParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("rate_limits", out var limits)
                || limits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var capturedAt = root.TryGetProperty("timestamp", out var timestamp) && timestamp.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : DateTimeOffset.Now;
            var (fiveHour, weekly) = QuotaJson.SplitWindows(ReadWindow(limits, "primary", capturedAt), ReadWindow(limits, "secondary", capturedAt));
            return new ProviderQuota
            {
                Provider = QuotaProvider.Codex,
                Plan = QuotaJson.PlanName(QuotaJson.ReadString(limits, "plan_type")),
                FiveHour = fiveHour,
                Weekly = weekly,
                Source = "session log",
                CapturedAt = capturedAt
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static QuotaWindow? ReadWindow(JsonElement limits, string name, DateTimeOffset capturedAt)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || QuotaJson.ReadNumber(window, "used_percent") is not { } used)
        {
            return null;
        }

        var resetsAt = QuotaJson.ReadEpoch(window, "resets_at")
            ?? (QuotaJson.ReadNumber(window, "resets_in_seconds") is { } seconds ? capturedAt.AddSeconds(seconds) : null);
        var minutes = (int)(QuotaJson.ReadNumber(window, "window_minutes") ?? 0);
        return new QuotaWindow(used, resetsAt, minutes);
    }
}
