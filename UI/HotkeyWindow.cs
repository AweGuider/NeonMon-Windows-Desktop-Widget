using System.Runtime.InteropServices;
using NeonMon.Models;

namespace NeonMon.UI;

// A message-only window that owns one global hotkey. RegisterHotKey reports presses only, so callers watch for the
// release themselves, and only while they act on a press.
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly nint MessageOnlyParent = -3;
    private bool _registered;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams { Parent = MessageOnlyParent });
    }

    public event Action? Pressed;

    // Returns false when another app already holds the combination.
    public bool Register(HotkeyModifiers modifiers, Keys key)
    {
        Unregister();
        _registered = RegisterHotKey(Handle, HotkeyId, (uint)modifiers | ModNoRepeat, (uint)key);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
    }

    public static string Describe(HotkeyModifiers modifiers, Keys key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(Keys key)
    {
        const uint virtualKeyToChar = 2;
        if (key is >= Keys.D0 and <= Keys.D9)
        {
            return ((char)('0' + (key - Keys.D0))).ToString();
        }

        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            return $"Num {key - Keys.NumPad0}";
        }

        if (key.ToString().StartsWith("Oem", StringComparison.Ordinal) && (MapVirtualKey((uint)key, virtualKeyToChar) & 0xFFFF) is var character and > 32)
        {
            return ((char)character).ToString();
        }

        return key switch
        {
            Keys.Prior => "PageUp",
            Keys.Next => "PageDown",
            Keys.Return => "Enter",
            Keys.Back => "Backspace",
            _ => key.ToString()
        };
    }

    public static bool IsModifierKey(Keys key) =>
        key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu;

    public static bool WinKeyDown() => IsDown(Keys.LWin) || IsDown(Keys.RWin);

    // True while every key of the combination is still down.
    public static bool IsHeld(HotkeyModifiers modifiers, Keys key) =>
        IsDown(key)
        && (!modifiers.HasFlag(HotkeyModifiers.Control) || IsDown(Keys.ControlKey))
        && (!modifiers.HasFlag(HotkeyModifiers.Alt) || IsDown(Keys.Menu))
        && (!modifiers.HasFlag(HotkeyModifiers.Shift) || IsDown(Keys.ShiftKey))
        && (!modifiers.HasFlag(HotkeyModifiers.Win) || WinKeyDown());

    private static bool IsDown(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam == HotkeyId)
        {
            Pressed?.Invoke();
            return;
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
