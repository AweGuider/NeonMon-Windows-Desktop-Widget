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
