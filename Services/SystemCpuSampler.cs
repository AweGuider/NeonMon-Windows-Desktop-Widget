namespace NeonMon.Services;

internal sealed class SystemCpuSampler
{
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _initialized;

    public double Read()
    {
        if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();

        if (!_initialized)
        {
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _initialized = true;
            return 0;
        }

        var idleDelta = idle - _lastIdle;
        var kernelDelta = kernel - _lastKernel;
        var userDelta = user - _lastUser;
        var total = kernelDelta + userDelta;

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        return total == 0 ? 0 : Math.Clamp(100d * (total - idleDelta) / total, 0, 100);
    }
}
