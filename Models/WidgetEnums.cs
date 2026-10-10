namespace NeonMon.Models;

internal enum WidgetSize
{
    Small,
    Medium,
    Large
}

internal enum DockEdge
{
    Top,
    Bottom,
    Left,
    Right
}

internal enum RevealState
{
    Hidden,
    Peek,
    Open
}

internal enum QuotaDisplay
{
    Remaining,
    Used
}

internal enum FullscreenBehavior
{
    StayOnTop,
    Hide
}

internal enum HiddenTabStyle
{
    TwoLines,
    OneLine
}

internal enum MenuStyle
{
    Windows,
    Dark
}

// Values match the RegisterHotKey MOD_ flags.
[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

internal enum HiddenMetric
{
    Cpu,
    Gpu,
    Memory,
    Drive
}

[Flags]
internal enum PeekValues
{
    None = 0,
    Cpu = 1,
    CpuTemperature = 2,
    Gpu = 4,
    GpuTemperature = 8,
    Memory = 16,
    Uptime = 32
}
