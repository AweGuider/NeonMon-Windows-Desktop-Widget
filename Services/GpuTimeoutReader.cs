using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonMon.Services;

internal static partial class GpuTimeoutReader
{
    [GeneratedRegex(@"(?im)^\s*P1:\s*(117|141)\s*$")]
    private static partial Regex TimeoutCodeRegex();

    [GeneratedRegex(@"WATCHDOG-(\d{8}-\d{4})\.dmp", RegexOptions.IgnoreCase)]
    private static partial Regex DumpTimeRegex();

    public static (DateTimeOffset? Time, string? Code) ReadLatest()
    {
        try
        {
            var query = new EventLogQuery(
                "Application",
                PathType.LogName,
                "*[System[(EventID=1001)]]")
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);
            for (var i = 0; i < 250; i++)
            {
                using var entry = reader.ReadEvent();
                if (entry is null)
                {
                    break;
                }

                var message = entry.FormatDescription() ?? string.Empty;
                if (!message.Contains("Event Name: LiveKernelEvent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var codeMatch = TimeoutCodeRegex().Match(message);
                if (!codeMatch.Success)
                {
                    continue;
                }

                var timestamp = entry.TimeCreated is { } eventTime
                    ? new DateTimeOffset(eventTime)
                    : (DateTimeOffset?)null;

                var dumpMatch = DumpTimeRegex().Match(message);
                if (dumpMatch.Success && DateTime.TryParseExact(
                    dumpMatch.Groups[1].Value,
                    "yyyyMMdd-HHmm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var dumpTime))
                {
                    timestamp = new DateTimeOffset(dumpTime);
                }

                return (timestamp, codeMatch.Groups[1].Value);
            }
        }
        catch
        {
        }

        return (null, null);
    }
}
