using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NeonMon.Models;

namespace NeonMon.Services;

// GDI names such as \\.\DISPLAY2 and the AllScreens order belong to one session, so a saved monitor is keyed by
// its EDID serial, its device path, and its size and position relative to the primary monitor instead.
internal static class MonitorIdentity
{
    private const uint GetDeviceInterfaceName = 1;
    private const byte SerialTag = 0xFF;
    private const byte NameTag = 0xFC;
    private static readonly int[] DescriptorOffsets = [54, 72, 90, 108];

    // Read on every call: a session reconnected at the console stops being remote while NeonMon keeps running.
    public static bool IsRemoteSession => SystemInformation.TerminalServerSession;

    public static Screen[] Ordered() =>
        [.. Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).ThenBy(screen => screen.Bounds.Top)];

    public static MonitorKey Identify(Screen screen)
    {
        var devicePath = DevicePath(screen);
        var primary = Screen.PrimaryScreen?.Bounds.Location ?? Point.Empty;
        var bounds = screen.Bounds;
        var layout = string.Create(CultureInfo.InvariantCulture,
            $"{bounds.Width}x{bounds.Height}@{bounds.X - primary.X},{bounds.Y - primary.Y}");
        return new MonitorKey(EdidKey(ReadEdid(devicePath)), devicePath, layout);
    }

    public static string? FriendlyName(Screen screen) => EdidName(ReadEdid(DevicePath(screen)));

    public static Screen? Find(MonitorKey? key) =>
        Match(key, Ordered().Select(screen => (screen, Identify(screen))));

    public static T? Match<T>(MonitorKey? key, IEnumerable<(T Item, MonitorKey Key)> candidates) where T : class
    {
        if (key is null)
        {
            return null;
        }

        var list = candidates.ToList();
        return First(key.Edid, candidate => candidate.Edid)
            ?? First(key.DevicePath, candidate => candidate.DevicePath)
            ?? First(key.Layout, candidate => candidate.Layout);

        T? First(string? wanted, Func<MonitorKey, string?> field) => wanted is null
            ? null
            : list.FirstOrDefault(candidate => string.Equals(field(candidate.Key), wanted, StringComparison.OrdinalIgnoreCase)).Item;
    }

    public static string? EdidKey(byte[]? edid)
    {
        if (edid is not { Length: >= 128 })
        {
            return null;
        }

        var serial = DescriptorText(edid, SerialTag);
        var number = BitConverter.ToUInt32(edid, 12);
        if (string.IsNullOrEmpty(serial) && number != 0)
        {
            serial = number.ToString(CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrEmpty(serial))
        {
            return null;
        }

        var maker = (edid[8] << 8) | edid[9];
        var manufacturer = new string([(char)(((maker >> 10) & 31) + 64), (char)(((maker >> 5) & 31) + 64), (char)((maker & 31) + 64)]);
        var product = edid[10] | (edid[11] << 8);
        return string.Create(CultureInfo.InvariantCulture, $"{manufacturer}{product:X4}:{serial}");
    }

    public static string? EdidName(byte[]? edid) =>
        edid is { Length: >= 128 } && DescriptorText(edid, NameTag) is { Length: > 0 } name ? name : null;

    public static string Dump()
    {
        var remote = IsRemoteSession;
        var screens = Ordered().Select(screen => new
        {
            screen.DeviceName,
            screen.Primary,
            Bounds = new { screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height },
            MonitorKey = Identify(screen),
            FriendlyName = FriendlyName(screen)
        });
        return JsonSerializer.Serialize(new { IsRemoteSession = remote, Screens = screens },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    private static string? DescriptorText(byte[] edid, byte tag)
    {
        foreach (var offset in DescriptorOffsets)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != tag)
            {
                continue;
            }

            var text = Encoding.Latin1.GetString(edid, offset + 5, 13);
            var end = text.IndexOf('\n');
            return (end >= 0 ? text[..end] : text).Trim(' ', '\0');
        }

        return null;
    }

    private static string? DevicePath(Screen screen)
    {
        try
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            return EnumDisplayDevices(screen.DeviceName, 0, ref device, GetDeviceInterfaceName) && device.DeviceId is { Length: > 0 } path
                ? path
                : null;
        }
        catch
        {
            return null;
        }
    }

    // \\?\DISPLAY#DELA0B9#5&3522c3f8&0&UID4609#{guid} keeps its EDID under Enum\DISPLAY\DELA0B9\5&3522c3f8&0&UID4609.
    private static byte[]? ReadEdid(string? devicePath)
    {
        var parts = devicePath?.Split('#');
        if (parts is not { Length: >= 4 })
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
}
