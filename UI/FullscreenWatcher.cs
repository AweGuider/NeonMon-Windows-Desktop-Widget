using System.Runtime.InteropServices;

namespace NeonMon.UI;

// Registers a space-less app bar so the shell pushes ABN_FULLSCREENAPP when a fullscreen app opens.
// The shell does not reliably report the fullscreen app closing, so while fullscreen is active a
// two-second check confirms when it ends. Nothing is polled outside fullscreen.
internal sealed class FullscreenWatcher : NativeWindow, IDisposable
{
    private const uint AbmNew = 0x00;
    private const uint AbmRemove = 0x01;
    private const int AbnFullscreenApp = 0x02;
    private const int CallbackMessage = 0x8000 + 0x4E4D;
    private const uint MonitorDefaultToNearest = 2;

    private readonly System.Windows.Forms.Timer _exitCheck = new() { Interval = 2000 };
    private bool _registered;
    private bool _fullscreen;

    public FullscreenWatcher()
    {
        _exitCheck.Tick += (_, _) =>
        {
            if (!IsFullscreenActive())
            {
                SetFullscreen(false);
            }
        };

        CreateHandle(new CreateParams());
        var data = CreateData();
        _registered = SHAppBarMessage(AbmNew, ref data) != 0;
    }

    public event Action<bool>? FullscreenChanged;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == CallbackMessage && (int)message.WParam == AbnFullscreenApp)
        {
            SetFullscreen(message.LParam != 0);
        }

        base.WndProc(ref message);
    }

    private void SetFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            _exitCheck.Start();
        }
        else
        {
            _exitCheck.Stop();
        }

        if (_fullscreen == fullscreen)
        {
            return;
        }

        _fullscreen = fullscreen;
        FullscreenChanged?.Invoke(fullscreen);
    }

    private static bool IsFullscreenActive()
    {
        var window = GetForegroundWindow();
        if (window == nint.Zero || window == GetShellWindow() || window == GetDesktopWindow() || !GetWindowRect(window, out var bounds))
        {
            return false;
        }

        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref monitor))
        {
            return false;
        }

        return bounds.Left <= monitor.Monitor.Left && bounds.Top <= monitor.Monitor.Top
            && bounds.Right >= monitor.Monitor.Right && bounds.Bottom >= monitor.Monitor.Bottom;
    }

    private AppBarData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<AppBarData>(),
        Window = Handle,
        CallbackMessage = CallbackMessage
    };

    public void Dispose()
    {
        _exitCheck.Dispose();
        if (_registered)
        {
            var data = CreateData();
            SHAppBarMessage(AbmRemove, ref data);
            _registered = false;
        }

        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Bounds;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect bounds);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
