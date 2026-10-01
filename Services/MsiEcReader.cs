using System.Management;

namespace NeonMon.Services;

internal sealed class MsiEcReader : IDisposable
{
    private readonly object _sync = new();
    private ManagementObject? _instance;
    private ManagementClass? _packageClass;
    private bool _initializationAttempted;

    public MsiTemperatureMetrics Read()
    {
        lock (_sync)
        {
            try
            {
                EnsureInitialized();
                if (_instance is null || _packageClass is null)
                {
                    return MsiTemperatureMetrics.Unavailable;
                }

                var cpuTemp = ReadByte(0x68);
                var gpuTemp = ReadByte(0x80);
                return new MsiTemperatureMetrics(cpuTemp, gpuTemp);
            }
            catch
            {
                return MsiTemperatureMetrics.Unavailable;
            }
        }
    }

    private void EnsureInitialized()
    {
        if (_initializationAttempted)
        {
            return;
        }

        _initializationAttempted = true;
        var scope = new ManagementScope(@"\\.\root\wmi");
        scope.Connect();

        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSI_ACPI"));
        _instance = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        _packageClass = new ManagementClass(scope, new ManagementPath("Package_32"), null);
    }

    private byte? ReadByte(byte address)
    {
        if (_instance is null || _packageClass is null)
        {
            return null;
        }

        using var input = _instance.GetMethodParameters("Get_Data");
        using var package = _packageClass.CreateInstance();
        var buffer = new byte[32];
        buffer[0] = address;
        package["Bytes"] = buffer;
        input["Data"] = package;

        using var output = _instance.InvokeMethod("Get_Data", input, null);
        using var resultPackage = output?["Data"] as ManagementBaseObject;
        var result = resultPackage?["Bytes"] as byte[];
        return result is { Length: > 1 } && result[0] == 1 ? result[1] : null;
    }

    public void Dispose()
    {
        _instance?.Dispose();
        _packageClass?.Dispose();
    }
}

internal sealed record MsiTemperatureMetrics(
    double? CpuTemperatureC,
    double? GpuTemperatureC)
{
    public static MsiTemperatureMetrics Unavailable { get; } = new(null, null);
}
