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
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Task _loop;
    private volatile bool _active;
    private volatile bool _background;
    private volatile bool _peeking;
    private volatile bool _peekTemperatures;
    private int _topProcessCountdown;
    private int _eventCountdown;
    private string _topProcess = "Sampling";
    private double _topProcessCpu;
    private DateTimeOffset? _lastGpuTimeout;
    private string? _gpuTimeoutCode;

    public TelemetryService()
    {
        _cpu.Read();
        _loop = Task.Run(SampleLoopAsync);
    }

    public TelemetrySnapshot Latest { get; private set; } = TelemetrySnapshot.Empty;

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    public void SetActive(bool active)
    {
        var activated = active && !_active;
        _active = active;
        if (activated)
        {
            _topProcessCountdown = 0;
            Wake();
        }
    }

    public void SetPeeking(bool peeking)
    {
        var started = peeking && !_peeking;
        _peeking = peeking;
        if (started)
        {
            Wake();
        }
    }

    public void SetPeekTemperatures(bool enabled) => _peekTemperatures = enabled;

    // Keeps sampling every five seconds while collapsed, for consumers such as the HTML bridge.
    public void SetBackgroundSampling(bool enabled)
    {
        _background = enabled;
        if (enabled)
        {
            Wake();
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    // LastWakeTime uses the same interrupt-time clock as TickCount64; Fast Startup "boots" count as wakes.
    private static TimeSpan SinceLastWake(TimeSpan sinceBoot)
    {
        const int LastWakeTime = 14;
        return NativeMethods.CallNtPowerInformation(LastWakeTime, 0, 0, out var wake, sizeof(ulong)) == 0
            && wake > 0 && (long)wake < sinceBoot.Ticks
                ? sinceBoot - TimeSpan.FromTicks((long)wake)
                : sinceBoot;
    }

    private async Task SampleLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            if (_active || _peeking || _background)
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
            }

            try
            {
                var delay = _active || _peeking ? 1000 : _background ? 5000 : Timeout.Infinite;
                await _wake.WaitAsync(delay, _cancellation.Token).ConfigureAwait(false);
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
        var msi = _active || _background || _peekTemperatures ? _msi.Read() : MsiTemperatureMetrics.Unavailable;

        if (_active && _topProcessCountdown-- <= 0)
        {
            (_topProcess, _topProcessCpu) = ReadTopProcess();
            _topProcessCountdown = 4;
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
            Uptime = SinceLastWake(TimeSpan.FromMilliseconds(Environment.TickCount64)),
            WindowsUptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
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

    public static string SystemDrive => (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');

    public static List<string> FixedDriveNames()
    {
        try
        {
            return DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.Name.TrimEnd('\\')).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<DriveMetric> ReadDrives()
    {
        var result = new List<DriveMetric>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
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
        _wake.Dispose();
    }

    private sealed record ProcessSample(string Name, TimeSpan CpuTime, DateTimeOffset CapturedAt);
}
