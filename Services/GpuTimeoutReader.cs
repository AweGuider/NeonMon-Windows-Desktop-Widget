using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonMon.Services;

internal static partial class GpuTimeoutReader
{
    private const string LiveKernelEventQuery = "*[System[(EventID=1001)]] and *[EventData[Data='LiveKernelEvent']]";

    private static long? _lastRecordId;
    private static (DateTimeOffset? Time, string? Code) _lastResult;

    [GeneratedRegex(@"(?im)^\s*P1:\s*(117|141)\s*$")]
    private static partial Regex TimeoutCodeRegex();

    [GeneratedRegex(@"WATCHDOG-(\d{8}-\d{4})\.dmp", RegexOptions.IgnoreCase)]
    private static partial Regex DumpTimeRegex();

    public static (DateTimeOffset? Time, string? Code) ReadLatest()
    {
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName, LiveKernelEventQuery)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);
            var newest = reader.ReadEvent();
            if (newest is null)
            {
                return (null, null);
            }

            if (newest.RecordId == _lastRecordId)
            {
                newest.Dispose();
                return _lastResult;
            }

            var newestRecordId = newest.RecordId;
            (DateTimeOffset? Time, string? Code) result = (null, null);
            var entry = newest;
            for (var i = 0; i < 250 && entry is not null; i++, entry = reader.ReadEvent())
            {
                using (entry)
                {
                    var message = entry.FormatDescription() ?? string.Empty;
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

                    result = (timestamp, codeMatch.Groups[1].Value);
                    break;
                }
            }

            _lastRecordId = newestRecordId;
            _lastResult = result;
        }
        catch
        {
        }

        return _lastResult;
    }
}
