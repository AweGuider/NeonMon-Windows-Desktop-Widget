namespace NeonMon.Models;

internal class StripSettings
{
    public bool Enabled { get; set; } = true;
    public WidgetSize Size { get; set; } = WidgetSize.Large;
    public DockEdge DockEdge { get; set; } = DockEdge.Top;
    public double DockOffset { get; set; } = 0.5;
    public bool KeepOpen { get; set; }
    public string? Monitor { get; set; }
    public bool FollowMouse { get; set; }
}

internal sealed class AppSettings : StripSettings
{
    public bool HtmlBridgeEnabled { get; set; }
    public int HtmlBridgePort { get; set; } = 27171;
    public List<string> HtmlBridgeAllowedOrigins { get; set; } = [];
    public FullscreenBehavior Fullscreen { get; set; } = FullscreenBehavior.StayOnTop;
    public MenuStyle MenuStyle { get; set; } = MenuStyle.Windows;
    public QuotaSettings? Quota { get; set; }
}

internal sealed class QuotaSettings : StripSettings
{
    public bool ClaudeEndpointFallback { get; set; }
    public QuotaDisplay Display { get; set; } = QuotaDisplay.Remaining;
    public string ClaudeCliDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string CodexCliDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
