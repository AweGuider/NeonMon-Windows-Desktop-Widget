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
    public int HiddenOpacity { get; set; } = 90;
    public int BackgroundOpacity { get; set; } = 90;

    // The lowest choice is also the floor applied to hand-edited settings.
    public static int[] HiddenOpacityChoices { get; } = [35, 50, 75, 90, 100];
    public static int[] BackgroundOpacityChoices { get; } = [80, 90, 100];
}

internal sealed class AppSettings : StripSettings
{
    public bool HtmlBridgeEnabled { get; set; }
    public int HtmlBridgePort { get; set; } = 27171;
    public List<string> HtmlBridgeAllowedOrigins { get; set; } = [];
    public FullscreenBehavior Fullscreen { get; set; } = FullscreenBehavior.StayOnTop;
    public MenuStyle MenuStyle { get; set; } = MenuStyle.Windows;
    public PeekValues Peek { get; set; } = PeekValues.Cpu | PeekValues.Gpu | PeekValues.Memory;
    public List<string>? PeekDrives { get; set; }
    public List<HiddenMetric> HiddenMetrics { get; set; } = [];
    public int HiddenMetricSeconds { get; set; } = 15;

    public static int[] HiddenMetricSecondsChoices { get; } = [1, 5, 15, 30];
    public bool PeekHotkeyEnabled { get; set; }
    public HotkeyModifiers PeekHotkeyModifiers { get; set; } = DefaultPeekHotkeyModifiers;
    public Keys PeekHotkeyKey { get; set; } = DefaultPeekHotkeyKey;

    public const HotkeyModifiers DefaultPeekHotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    public const Keys DefaultPeekHotkeyKey = Keys.Space;
    public QuotaSettings? Quota { get; set; }
}

internal sealed class QuotaSettings : StripSettings
{
    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    public bool ClaudeEndpointFallback { get; set; }
    public QuotaDisplay Display { get; set; } = QuotaDisplay.Remaining;
    public HiddenTabStyle HiddenTab { get; set; } = HiddenTabStyle.TwoLines;
    public bool PeekResetWhenLow { get; set; } = true;
    public string ClaudeCliDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string CodexCliDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public int ClaudeActiveRefreshMinutes { get; set; } = ActiveRefreshChoices[0];
    public int CodexActiveRefreshMinutes { get; set; } = ActiveRefreshChoices[0];

    public static int[] ActiveRefreshChoices { get; } = [2, 5, 10];

    // With both providers off the pulse is disabled, so a layout never has to draw zero providers.
    public bool Shows(QuotaProvider provider) => (!ShowClaude && !ShowCodex) || (provider == QuotaProvider.Claude ? ShowClaude : ShowCodex);

    public TimeSpan ActiveRefresh(QuotaProvider provider)
    {
        var minutes = provider == QuotaProvider.Claude ? ClaudeActiveRefreshMinutes : CodexActiveRefreshMinutes;
        return TimeSpan.FromMinutes(ActiveRefreshChoices.Contains(minutes) ? minutes : ActiveRefreshChoices[0]);
    }
}
