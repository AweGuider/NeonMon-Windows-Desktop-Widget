namespace NeonMon.Models;

internal sealed record DriveMetric(string Name, double FreeGb, double FreePercent);

internal sealed record TelemetrySnapshot
{
    public static TelemetrySnapshot Empty { get; } = new();

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
