namespace NeonMon.Models;

internal sealed record DriveMetric(string Name, double FreeGb, double FreePercent);

internal sealed record TelemetrySnapshot
{
    public static TelemetrySnapshot Empty { get; } = new();

    public static TelemetrySnapshot Sample { get; } = new()
    {
        CapturedAt = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero),
        Uptime = new TimeSpan(2, 9, 27, 12),
        CpuPercent = 12,
        GpuPercent = 19,
        GpuTemperatureC = 47,
        GpuClockMhz = 1350,
        GpuMemoryClockMhz = 6000,
        MemoryPercent = 84,
        MemoryUsedGb = 26.6,
        MemoryTotalGb = 32,
        NvidiaDriver = "581.57",
        TopProcess = "claude",
        TopProcessCpuPercent = 3.4,
        Drives = [new DriveMetric("C:", 102, 5.4), new DriveMetric("D:", 130, 7)]
    };

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Uptime { get; init; }
    public double CpuPercent { get; init; }
    public double? CpuTemperatureC { get; init; }
    public double GpuPercent { get; init; }
    public double? GpuTemperatureC { get; init; }
    public uint? GpuClockMhz { get; init; }
    public uint? GpuMemoryClockMhz { get; init; }
    public double MemoryPercent { get; init; }
    public double MemoryUsedGb { get; init; }
    public double MemoryTotalGb { get; init; }
    public string NvidiaDriver { get; init; } = "Unavailable";
    public string TopProcess { get; init; } = "Sampling";
    public double TopProcessCpuPercent { get; init; }
    public IReadOnlyList<DriveMetric> Drives { get; init; } = [];
    public DateTimeOffset? LastGpuTimeout { get; init; }
    public string? GpuTimeoutCode { get; init; }
}
