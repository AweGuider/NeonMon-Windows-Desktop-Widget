using System.Text;
using System.Text.Json;
using NeonMon.Models;

namespace NeonMon.Services;

internal sealed class CodexSessionReader
{
    private const int InitialTail = 256 * 1024;
    private const int MaxTail = 4 * 1024 * 1024;
    private const int DayFolders = 7;

    private static readonly string SessionsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    private readonly Dictionary<string, (long Length, ProviderQuota? Quota)> _files = new(StringComparer.OrdinalIgnoreCase);

    // The latest snapshot of every recent session. Sessions run in parallel and a thread stays in the day folder it
    // started in, so one file is not enough. Directory listings can report a stale size and modified time for a file
    // Codex is still writing, so each file is opened to read its real length.
    public IReadOnlyList<ProviderQuota> Read()
    {
        lock (_files)
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in RecentSessionFiles())
                {
                    seen.Add(path);
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (_files.TryGetValue(path, out var known) && known.Length == stream.Length)
                        {
                            continue;
                        }

                        _files[path] = (stream.Length, ReadLatestRateLimits(stream) ?? known.Quota);
                    }
                    catch (IOException)
                    {
                    }
                }

                foreach (var path in _files.Keys.Where(path => !seen.Contains(path)).ToList())
                {
                    _files.Remove(path);
                }
            }
            catch
            {
            }

            return _files.Values.Select(file => file.Quota).OfType<ProviderQuota>().ToList();
        }
    }

    private static IEnumerable<string> RecentSessionFiles()
    {
        if (!Directory.Exists(SessionsRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(SessionsRoot)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .SelectMany(year => Directory.EnumerateDirectories(year).OrderByDescending(path => path, StringComparer.Ordinal))
            .SelectMany(month => Directory.EnumerateDirectories(month).OrderByDescending(path => path, StringComparer.Ordinal))
            .Select(day => Directory.GetFiles(day, "rollout-*.jsonl"))
            .Where(files => files.Length > 0)
            .Take(DayFolders)
            .SelectMany(files => files);
    }

    private static ProviderQuota? ReadLatestRateLimits(FileStream stream)
    {
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
