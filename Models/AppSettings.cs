namespace NeonMon.Models;

internal class StripSettings
{
    public WidgetSize Size { get; set; } = WidgetSize.Large;
    public DockEdge DockEdge { get; set; } = DockEdge.Top;
    public double DockOffset { get; set; } = 0.5;
    public bool KeepOpen { get; set; }
}

internal sealed class AppSettings : StripSettings
{
    public bool HtmlBridgeEnabled { get; set; }
    public int HtmlBridgePort { get; set; } = 27171;
    public List<string> HtmlBridgeAllowedOrigins { get; set; } = [];
    public QuotaSettings? Quota { get; set; }
}

internal sealed class QuotaSettings : StripSettings
{
    public bool Enabled { get; set; } = true;
    public bool ClaudeEndpointFallback { get; set; }
    public QuotaDisplay Display { get; set; } = QuotaDisplay.Remaining;
    public string ClaudeCliDirectory { get; set; } = @"C:\Projects\Claude";
}
