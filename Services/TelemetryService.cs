using System.Diagnostics;
using NeonMon.Models;

namespace NeonMon.Services;

internal sealed class TelemetryService : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SystemCpuSampler _cpu = new();
    private readonly NvmlReader _nvml = new();
    private readonly MsiEcReader _msi = new();
    private readonly Dictionary<int, ProcessSample> _processSamples = [];
    private readonly Task _loop;
    private volatile bool _active;
    private int _topProcessCountdown;
    private int _eventCountdown;
    private string _topProcess = "Sampling";
    private double _topProcessCpu;
    private DateTimeOffset? _lastGpuTimeout;
    private string? _gpuTimeoutCode;

    public TelemetryService()
    {
        _loop = Task.Run(SampleLoopAsync);
    }

    public TelemetrySnapshot Latest { get; private set; } = TelemetrySnapshot.Empty;

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    public void SetActive(bool active) => _active = active;

    private async Task SampleLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var snapshot = Capture();
                Latest = snapshot;
                SnapshotUpdated?.Invoke(snapshot);
            }
            catch
            {
            }

            try
            {
                await Task.Delay(_active ? 1000 : 5000, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private TelemetrySnapshot Capture()
    {
        var gpu = _nvml.Read();
        var msi = _msi.Read();

        if (_topProcessCountdown-- <= 0)
        {
            (_topProcess, _topProcessCpu) = ReadTopProcess();
            _topProcessCountdown = _active ? 4 : 0;
        }

        if (_eventCountdown-- <= 0)
        {
            (_lastGpuTimeout, _gpuTimeoutCode) = GpuTimeoutReader.ReadLatest();
            _eventCountdown = _active ? 59 : 11;
        }

        var memory = new NativeMethods.MemoryStatusEx();
        NativeMethods.GlobalMemoryStatusEx(memory);
        var usedBytes = memory.TotalPhysical - memory.AvailablePhysical;
        var drives = ReadDrives();

        return new TelemetrySnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            CpuPercent = _cpu.Read(),
            CpuTemperatureC = msi.CpuTemperatureC,
            GpuPercent = gpu.Utilization,
            GpuTemperatureC = gpu.TemperatureC ?? msi.GpuTemperatureC,
            GpuClockMhz = gpu.GraphicsClockMhz,
            GpuMemoryClockMhz = gpu.MemoryClockMhz,
            MemoryPercent = memory.TotalPhysical == 0 ? 0 : 100d * usedBytes / memory.TotalPhysical,
            MemoryUsedGb = usedBytes / 1024d / 1024d / 1024d,
            MemoryTotalGb = memory.TotalPhysical / 1024d / 1024d / 1024d,
            NvidiaDriver = gpu.Driver,
            TopProcess = _topProcess,
            TopProcessCpuPercent = _topProcessCpu,
            Drives = drives,
            LastGpuTimeout = _lastGpuTimeout,
            GpuTimeoutCode = _gpuTimeoutCode
        };
    }

    private static IReadOnlyList<DriveMetric> ReadDrives()
    {
        var result = new List<DriveMetric>(2);
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady).Take(2))
            {
                result.Add(new DriveMetric(
                    drive.Name.TrimEnd('\\'),
                    drive.AvailableFreeSpace / 1024d / 1024d / 1024d,
                    drive.TotalSize == 0 ? 0 : 100d * drive.AvailableFreeSpace / drive.TotalSize));
            }
        }
        catch
        {
        }

        return result;
    }

    private (string Name, double CpuPercent) ReadTopProcess()
    {
        var now = DateTimeOffset.UtcNow;
        var next = new Dictionary<int, ProcessSample>();
        var bestName = _topProcess;
        var bestCpu = 0d;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var sample = new ProcessSample(process.ProcessName, process.TotalProcessorTime, now);
                    next[process.Id] = sample;
                    if (!_processSamples.TryGetValue(process.Id, out var previous))
                    {
                        continue;
                    }

                    var elapsed = (sample.CapturedAt - previous.CapturedAt).TotalSeconds;
                    var cpuDelta = (sample.CpuTime - previous.CpuTime).TotalSeconds;
                    if (elapsed <= 0 || cpuDelta < 0)
                    {
                        continue;
                    }

                    var cpu = Math.Clamp(100d * cpuDelta / elapsed / Environment.ProcessorCount, 0, 100);
                    if (cpu > bestCpu)
                    {
                        bestCpu = cpu;
                        bestName = sample.Name;
                    }
                }
                catch
                {
                }
            }
        }

        _processSamples.Clear();
        foreach (var pair in next)
        {
            _processSamples[pair.Key] = pair.Value;
        }

        return (bestName, bestCpu);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _msi.Dispose();
        _nvml.Dispose();
        _cancellation.Dispose();
    }

    private sealed record ProcessSample(string Name, TimeSpan CpuTime, DateTimeOffset CapturedAt);
}
