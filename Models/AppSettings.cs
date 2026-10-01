namespace NeonMon.Models;

internal sealed class AppSettings
{
    public WidgetSize Size { get; set; } = WidgetSize.Large;
    public DockEdge DockEdge { get; set; } = DockEdge.Top;
    public double DockOffset { get; set; } = 0.5;
    public bool KeepOpen { get; set; }
    public bool HtmlBridgeEnabled { get; set; }
    public int HtmlBridgePort { get; set; } = 27171;
}
