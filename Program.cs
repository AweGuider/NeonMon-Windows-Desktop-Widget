using NeonMon.Models;
using NeonMon.Services;
using NeonMon.UI;
using System.Text.Json;

namespace NeonMon;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        using var telemetry = new TelemetryService();
        using var bridge = new MetricsBridge(() => telemetry.Latest);
        using var widget = new WidgetForm(settings, settingsStore, telemetry, bridge);

        var previewIndex = Array.FindIndex(args, argument => argument.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        if (previewIndex >= 0 && previewIndex + 1 < args.Length)
        {
            Thread.Sleep(250);
            var previewSize = previewIndex + 2 < args.Length && Enum.TryParse<WidgetSize>(args[previewIndex + 2], true, out var requestedSize)
                ? requestedSize
                : WidgetSize.Large;
            widget.SavePreview(args[previewIndex + 1], telemetry.Latest, previewSize);
            return 0;
        }

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _ = widget.Handle;
                if (!bridge.Start(0))
                {
                    return 2;
                }

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var json = client.GetStringAsync($"http://127.0.0.1:{bridge.Port}/api/v1/metrics").GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(json);
                return document.RootElement.TryGetProperty("cpuPercent", out _) ? 0 : 3;
            }
            catch
            {
                return 1;
            }
        }

        Application.Run(widget);
        return 0;
    }
}
