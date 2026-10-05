using System.Text.Json;
using NeonMon.Models;

namespace NeonMon.Services;

internal static class QuotaJson
{
    public static DateTimeOffset? ReadEpoch(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => DateTimeOffset.FromUnixTimeMilliseconds((long)(value.GetDouble() * 1000)),
            JsonValueKind.String when value.TryGetDateTimeOffset(out var parsed) => parsed,
            _ => null
        };
    }

    public static double? ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    public static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static string? PlanName(string? planType) => string.IsNullOrWhiteSpace(planType) || planType == "unknown"
        ? null
        : char.ToUpperInvariant(planType[0]) + planType[1..].Replace('_', ' ');

    public static (QuotaWindow? FiveHour, QuotaWindow? Weekly) SplitWindows(params QuotaWindow?[] windows)
    {
        QuotaWindow? fiveHour = null;
        QuotaWindow? weekly = null;
        foreach (var window in windows)
        {
            if (window is null)
            {
                continue;
            }

            if (window.WindowMinutes is > 0 and <= 24 * 60)
            {
                fiveHour ??= window;
            }
            else
            {
                weekly ??= window;
            }
        }

        return (fiveHour, weekly);
    }
}
