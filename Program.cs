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
        using var quota = new QuotaService();
        using var bridge = new MetricsBridge(() => telemetry.Latest, () => quota.Latest);
        using var context = new NeonMonContext(settings, settingsStore, telemetry, quota, bridge);

        var useSample = args.Contains("--sample", StringComparer.OrdinalIgnoreCase);
        WidgetForm previewTarget = string.Equals(ArgumentValue(args, "--strip"), "quota", StringComparison.OrdinalIgnoreCase)
            ? context.QuotaForm
            : context.SystemForm;
        if (Enum.TryParse<DockEdge>(ArgumentValue(args, "--dock"), true, out var previewDock))
        {
            previewTarget.Settings.DockEdge = previewDock;
        }

        var peekPreviewIndex = Array.FindIndex(args, argument => argument.Equals("--render-peek-preview", StringComparison.OrdinalIgnoreCase));
        if (peekPreviewIndex >= 0 && peekPreviewIndex + 1 < args.Length)
        {
            PreparePreview(context, telemetry, quota, useSample);
            previewTarget.SavePreview(args[peekPreviewIndex + 1], WidgetSize.Large, RevealState.Peek);
            return 0;
        }

        var previewIndex = Array.FindIndex(args, argument => argument.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        if (previewIndex >= 0 && previewIndex + 1 < args.Length)
        {
            if (!useSample)
            {
                Thread.Sleep(250);
            }

            PreparePreview(context, telemetry, quota, useSample);
            var previewSize = previewIndex + 2 < args.Length && Enum.TryParse<WidgetSize>(args[previewIndex + 2], true, out var requestedSize)
                ? requestedSize
                : WidgetSize.Large;
            var previewState = Enum.TryParse<RevealState>(ArgumentValue(args, "--state"), true, out var requestedState)
                ? requestedState
                : RevealState.Open;
            previewTarget.SavePreview(args[previewIndex + 1], previewSize, previewState);
            return 0;
        }

        if (ArgumentValue(args, "--dump-quota") is { } dumpPath)
        {
            File.WriteAllText(dumpPath, MetricsBridge.SerializeQuota(quota.RefreshAllAsync().GetAwaiter().GetResult()));
            return 0;
        }

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _ = context.SystemForm.Handle;
                if (!bridge.Start(0))
                {
                    return 2;
                }

                quota.RefreshLocal();
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var json = client.GetStringAsync($"http://127.0.0.1:{bridge.Port}/api/v1/metrics").GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(json);
                var quotaJson = client.GetStringAsync($"http://127.0.0.1:{bridge.Port}/api/v1/quota").GetAwaiter().GetResult();
                using var quotaDocument = JsonDocument.Parse(quotaJson);
                return document.RootElement.TryGetProperty("cpuPercent", out _)
                    && quotaDocument.RootElement.TryGetProperty("claude", out _)
                    && quotaDocument.RootElement.TryGetProperty("codex", out _) ? 0 : 3;
            }
            catch
            {
                return 1;
            }
        }

        context.Start();
        Application.Run(context);
        return 0;
    }

    private static void PreparePreview(NeonMonContext context, TelemetryService telemetry, QuotaService quota, bool useSample)
    {
        context.SystemForm.SetSnapshot(useSample ? TelemetrySnapshot.Sample : telemetry.Latest);
        if (useSample)
        {
            var now = TelemetrySnapshot.Sample.CapturedAt;
            context.QuotaForm.Clock = () => now;
            context.QuotaForm.SetSnapshot(QuotaSnapshot.Sample(now));
        }
        else
        {
            context.QuotaForm.SetSnapshot(quota.RefreshAllAsync().GetAwaiter().GetResult());
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
