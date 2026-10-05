using System.Text.Json;
using NeonMon.Models;

namespace NeonMon.Services;

internal sealed class ClaudeStatuslineReader
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonMon", "claude-statusline.json");

    private DateTime _lastWrite;
    private ProviderQuota? _latest;

    public ProviderQuota? Read()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                return _latest;
            }

            if (_latest is not null && info.LastWriteTimeUtc == _lastWrite)
            {
                return _latest;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            _lastWrite = info.LastWriteTimeUtc;
            if (!root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            {
                return _latest;
            }

            _latest = new ProviderQuota
            {
                Provider = QuotaProvider.Claude,
                FiveHour = ReadWindow(limits, "five_hour", 300),
                Weekly = ReadWindow(limits, "seven_day", 10080),
                Source = "CLI statusline",
                CapturedAt = root.TryGetProperty("savedAt", out var saved) && saved.TryGetDateTimeOffset(out var savedAt)
                    ? savedAt
                    : new DateTimeOffset(info.LastWriteTimeUtc)
            };
            return _latest;
        }
        catch
        {
            return _latest;
        }
    }

    private static QuotaWindow? ReadWindow(JsonElement limits, string name, int windowMinutes)
    {
        if (!limits.TryGetProperty(name, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used)
            || used.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return new QuotaWindow(used.GetDouble(), QuotaJson.ReadEpoch(window, "resets_at"), windowMinutes);
    }
}
