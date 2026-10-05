using System.Runtime.InteropServices;

namespace NeonMon.UI;

// Raises Settled shortly after the foreground window changes. Windows can drop topmost windows
// below a newly activated (especially fullscreen) window, so strips re-assert topmost on this signal.
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private readonly WinEventProc _callback;
    private readonly System.Windows.Forms.Timer _settle = new() { Interval = 250 };
    private nint _hook;

    public ForegroundWatcher()
    {
        _callback = (_, _, _, _, _, _, _) =>
        {
            _settle.Stop();
            _settle.Start();
        };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Settled?.Invoke();
        };
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, nint.Zero, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    public event Action? Settled;

    public void Dispose()
    {
        if (_hook != nint.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = nint.Zero;
        }

        _settle.Dispose();
    }

    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
}
