using System.Runtime.InteropServices;
using System.Text;

namespace NeonMon.Services;

internal sealed class NvmlReader : IDisposable
{
    private const int Success = 0;
    private const uint TemperatureGpu = 0;
    private const uint ClockGraphics = 0;
    private const uint ClockMemory = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShutdownDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceHandleDelegate(uint index, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UtilizationDelegate(nint device, out Utilization utilization);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TemperatureDelegate(nint device, uint sensorType, out uint temperature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ClockDelegate(nint device, uint clockType, out uint clock);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DriverVersionDelegate([Out] byte[] version, uint length);

    private readonly nint _library;
    private readonly ShutdownDelegate? _shutdown;
    private readonly UtilizationDelegate? _getUtilization;
    private readonly TemperatureDelegate? _getTemperature;
    private readonly ClockDelegate? _getClock;
    private readonly DriverVersionDelegate? _getDriverVersion;
    private readonly nint _device;
    private bool _initialized;

    public NvmlReader()
    {
        _library = LoadLibrary();
        if (_library == nint.Zero)
        {
            return;
        }

        try
        {
            var init = GetDelegate<InitDelegate>("nvmlInit_v2", "nvmlInit");
            _shutdown = GetDelegate<ShutdownDelegate>("nvmlShutdown");
            var getHandle = GetDelegate<DeviceHandleDelegate>("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
            _getUtilization = GetDelegate<UtilizationDelegate>("nvmlDeviceGetUtilizationRates");
            _getTemperature = GetDelegate<TemperatureDelegate>("nvmlDeviceGetTemperature");
            _getClock = GetDelegate<ClockDelegate>("nvmlDeviceGetClockInfo");
            _getDriverVersion = GetDelegate<DriverVersionDelegate>("nvmlSystemGetDriverVersion");

            _initialized = init() == Success && getHandle(0, out _device) == Success;
        }
        catch
        {
            _initialized = false;
        }
    }

    public GpuMetrics Read()
    {
        if (!_initialized)
        {
            return GpuMetrics.Empty;
        }

        var utilization = 0d;
        double? temperature = null;
        uint? graphicsClock = null;
        uint? memoryClock = null;

        if (_getUtilization is not null && _getUtilization(_device, out var usage) == Success)
        {
            utilization = usage.Gpu;
        }

        if (_getTemperature is not null && _getTemperature(_device, TemperatureGpu, out var temp) == Success)
        {
            temperature = temp;
        }

        if (_getClock is not null && _getClock(_device, ClockGraphics, out var gpuClock) == Success)
        {
            graphicsClock = gpuClock;
        }

        if (_getClock is not null && _getClock(_device, ClockMemory, out var vramClock) == Success)
        {
            memoryClock = vramClock;
        }

        var driver = "Unavailable";
        if (_getDriverVersion is not null)
        {
            var bytes = new byte[96];
            if (_getDriverVersion(bytes, (uint)bytes.Length) == Success)
            {
                driver = Encoding.ASCII.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0) is var end && end >= 0 ? end : bytes.Length);
            }
        }

        return new GpuMetrics(utilization, temperature, graphicsClock, memoryClock, driver);
    }

    private nint LoadLibrary()
    {
        var candidates = new[]
        {
            "nvml.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")
        };

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return nint.Zero;
    }

    private T GetDelegate<T>(params string[] names) where T : Delegate
    {
        foreach (var name in names)
        {
            if (NativeLibrary.TryGetExport(_library, name, out var export))
            {
                return Marshal.GetDelegateForFunctionPointer<T>(export);
            }
        }

        throw new EntryPointNotFoundException(string.Join(" or ", names));
    }

    public void Dispose()
    {
        if (_initialized)
        {
            _shutdown?.Invoke();
        }

        if (_library != nint.Zero)
        {
            NativeLibrary.Free(_library);
        }
    }
}

internal sealed record GpuMetrics(
    double Utilization,
    double? TemperatureC,
    uint? GraphicsClockMhz,
    uint? MemoryClockMhz,
    string Driver)
{
    public static GpuMetrics Empty { get; } = new(0, null, null, null, "Unavailable");
}
