using NeonMon.Models;
using NeonMon.Services;
using NeonMon.UI;
using System.Globalization;
using System.Text.Json;

namespace NeonMon;

internal static class Program
{
    private const string InstanceMutexName = @"Local\NeonMon.Instance";
    private const string OpenSignalName = @"Local\NeonMon.Open";
    private const string ExitSignalName = @"Local\NeonMon.Exit";
    private static readonly string[] ToolArguments = ["--render-settings", "--render-preview", "--render-peek-preview", "--self-test", "--dump-quota", "--export-icon"];

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            SignalRunningInstance(ExitSignalName);
            return 0;
        }

        Mutex? instance = null;
        EventWaitHandle? openSignal = null;
        EventWaitHandle? exitSignal = null;
        if (!args.Any(argument => ToolArguments.Contains(argument, StringComparer.OrdinalIgnoreCase)))
        {
            instance = new Mutex(true, InstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                instance.Dispose();
                SignalRunningInstance(OpenSignalName);
                return 0;
            }

            openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSignalName);
            exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
        }

        if (ArgumentValue(args, "--export-icon") is { } iconPath)
        {
            TrayIcon.Export(iconPath);
            return 0;
        }

        using var instanceLease = instance;
        using var openSignalLease = openSignal;
        using var exitSignalLease = exitSignal;
        using var settingsStore = new SettingsStore();
        var useSample = args.Contains("--sample", StringComparer.OrdinalIgnoreCase);
        var settingsPreviewIndex = Array.FindIndex(args, argument => argument.Equals("--render-settings", StringComparison.OrdinalIgnoreCase));
        var rendering = args.Any(argument => argument.StartsWith("--render-", StringComparison.OrdinalIgnoreCase));
        var settings = rendering && useSample ? SampleSettings() : settingsStore.Load();
        using var telemetry = new TelemetryService();
        using var quota = new QuotaService(
            () => settings.Quota?.ClaudeEndpointFallback == true,
            provider => settings.Quota?.ActiveRefresh(provider) ?? TimeSpan.FromMinutes(QuotaSettings.ActiveRefreshChoices[0]),
            provider => settings.Quota?.Shows(provider) ?? true);
        using var bridge = new MetricsBridge(() => telemetry.Latest, () => quota.Latest, () => settings.HtmlBridgeAllowedOrigins);
        using var updates = new UpdateChecker(() => settings.CheckForUpdates, Application.ProductVersion);
        using var context = new NeonMonContext(settings, settingsStore, telemetry, quota, bridge, updates);

        if (settingsPreviewIndex >= 0 && settingsPreviewIndex + 1 < args.Length)
        {
            var pageName = settingsPreviewIndex + 2 < args.Length ? args[settingsPreviewIndex + 2] : "General";
            var page = Array.FindIndex(["General", "System", "Quota", "Support"], name => name.Equals(pageName, StringComparison.OrdinalIgnoreCase));
            context.SaveSettingsPreview(args[settingsPreviewIndex + 1], Math.Max(0, page), useSample ? "updated 12 min ago" : null);
            return 0;
        }

        WidgetForm previewTarget = string.Equals(ArgumentValue(args, "--strip"), "quota", StringComparison.OrdinalIgnoreCase)
            ? context.QuotaForm
            : context.SystemForm;
        if (Enum.TryParse<DockEdge>(ArgumentValue(args, "--dock"), true, out var previewDock))
        {
            previewTarget.Settings.DockEdge = previewDock;
        }

        if (Enum.TryParse<QuotaProvider>(ArgumentValue(args, "--provider"), true, out var onlyProvider) && settings.Quota is { } previewQuota)
        {
            previewQuota.ShowClaude = onlyProvider == QuotaProvider.Claude;
            previewQuota.ShowCodex = onlyProvider == QuotaProvider.Codex;
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
                telemetry.SetActive(true);
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

                if (!SelfTestClaudeEndpoint(settings, quota))
                {
                    return 4;
                }

                if (!SelfTestCodexMerge())
                {
                    return 6;
                }

                if (!SelfTestRefreshPolicy())
                {
                    return 7;
                }

                if (!SelfTestUpdateParsing())
                {
                    return 8;
                }

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var json = client.GetStringAsync($"http://127.0.0.1:{bridge.Port}/api/v1/metrics").GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(json);
                var quotaJson = client.GetStringAsync($"http://127.0.0.1:{bridge.Port}/api/v1/quota").GetAwaiter().GetResult();
                using var quotaDocument = JsonDocument.Parse(quotaJson);
                if (!SelfTestBridgeOrigins(client, bridge.Port))
                {
                    return 5;
                }

                return document.RootElement.TryGetProperty("cpuPercent", out _)
                    && quotaDocument.RootElement.TryGetProperty("claude", out _)
                    && quotaDocument.RootElement.TryGetProperty("codex", out _) ? 0 : 3;
            }
            catch
            {
                return 1;
            }
        }

        var openRegistration = openSignal is null
            ? null
            : ThreadPool.RegisterWaitForSingleObject(openSignal, (_, _) => context.ActivateFromSecondInstance(), null, Timeout.Infinite, executeOnlyOnce: false);
        var exitRegistration = exitSignal is null
            ? null
            : ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => context.ExitFromSignal(), null, Timeout.Infinite, executeOnlyOnce: true);
        context.Start();
        Application.Run(context);
        openRegistration?.Unregister(null);
        exitRegistration?.Unregister(null);
        return 0;
    }

    private static void SignalRunningInstance(string signalName)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(signalName);
            signal.Set();
        }
        catch
        {
        }
    }

    private static AppSettings SampleSettings() => new()
    {
        FollowMouse = true,
        BackgroundOpacity = 100,
        Quota = new QuotaSettings
        {
            FollowMouse = true,
            BackgroundOpacity = 100,
            ClaudeCliDirectory = @"C:\Users\you\Projects",
            CodexCliDirectory = @"C:\Users\you"
        }
    };

    private static void PreparePreview(NeonMonContext context, TelemetryService telemetry, QuotaService quota, bool useSample)
    {
        context.SystemForm.SetSnapshot(useSample ? TelemetrySnapshot.Sample : telemetry.Latest);
        if (useSample)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            context.QuotaForm.SetCliAvailability(true, true);
            var now = TelemetrySnapshot.Sample.CapturedAt;
            context.QuotaForm.Clock = () => now;
            context.QuotaForm.TimeZone = TimeZoneInfo.Utc;
            context.QuotaForm.SetSnapshot(QuotaSnapshot.Sample(now));
        }
        else
        {
            context.QuotaForm.SetSnapshot(quota.RefreshAllAsync().GetAwaiter().GetResult());
        }
    }

    private static bool SelfTestBridgeOrigins(HttpClient client, int port)
    {
        string? AllowedOrigin(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/quota");
            request.Headers.TryAddWithoutValidation("Origin", origin);
            using var response = client.Send(request);
            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.FirstOrDefault() : null;
        }

        return AllowedOrigin("https://example.com") is null
            && AllowedOrigin("null") is null
            && AllowedOrigin("http://localhost:5173") == "http://localhost:5173";
    }

    private static bool SelfTestClaudeEndpoint(AppSettings settings, QuotaService quota)
    {
        const string sample = """
            {"five_hour":{"utilization":19.0,"resets_at":"2026-10-05T02:19:59.543Z"},
             "seven_day":{"utilization":27.0,"resets_at":"2026-10-09T15:59:59.543Z"}}
            """;
        using var document = JsonDocument.Parse(sample);
        var parsed = ClaudeUsageEndpoint.Parse(document.RootElement, "Pro", DateTimeOffset.Now);
        var parsedCorrectly = parsed is { FiveHour.UsedPercent: 19, Weekly.UsedPercent: 27 }
            && parsed.FiveHour.ResetsAt == new DateTimeOffset(2026, 10, 5, 2, 19, 59, 543, TimeSpan.Zero);

        if (settings.Quota is not null)
        {
            settings.Quota.ClaudeEndpointFallback = false;
        }

        var snapshot = quota.RefreshAllAsync().GetAwaiter().GetResult();
        return parsedCorrectly && snapshot.ClaudeEndpointRequests == 0;
    }

    private static bool SelfTestCodexMerge()
    {
        var now = new DateTimeOffset(2026, 10, 8, 17, 36, 0, TimeSpan.Zero);
        var reset = now.AddHours(2);
        ProviderQuota Session(double used, int secondsAgo, DateTimeOffset resetsAt) => new()
        {
            Provider = QuotaProvider.Codex,
            FiveHour = new QuotaWindow(used, resetsAt, 300),
            Source = "session log",
            CapturedAt = now.AddSeconds(-secondsAgo)
        };

        var staleLater = QuotaService.MergeCodex([Session(99, 60, reset), Session(95, 10, reset.AddSeconds(40))], null);
        var afterReset = QuotaService.MergeCodex([Session(28, 900, reset), Session(1, 10, reset)], null);
        var newWindow = QuotaService.MergeCodex([Session(99, 120, reset), Session(3, 10, reset.AddHours(5))], null);
        var exhausted = QuotaService.MergeCodex([Session(99, 600, reset)], Session(100, 0, reset) with { Source = "app-server" });

        return staleLater.FiveHour?.UsedPercent == 99
            && afterReset.FiveHour?.UsedPercent == 1
            && newWindow.FiveHour?.UsedPercent == 3
            && exhausted.FiveHour?.UsedPercent == 100 && exhausted.Source == "app-server";
    }

    private static bool SelfTestRefreshPolicy()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(2);
        ProviderQuota Used(double used, DateTimeOffset resetsAt) => new()
        {
            Provider = QuotaProvider.Codex,
            FiveHour = new QuotaWindow(used, resetsAt, 300)
        };

        var active = TimeSpan.FromMinutes(5);
        var settings = new QuotaSettings { CodexActiveRefreshMinutes = 7 };
        return QuotaService.AppServerInterval(false, now.AddMinutes(-3), now, active) == active
            && QuotaService.AppServerInterval(false, now.AddMinutes(-11), now, active) == TimeSpan.FromMinutes(45)
            && QuotaService.AppServerInterval(true, now.AddMinutes(-11), now, active) == TimeSpan.FromMinutes(5)
            && QuotaService.EndpointMaxAge(true, DateTimeOffset.MinValue, now, active) == TimeSpan.FromMinutes(2)
            && QuotaService.EndpointMaxAge(false, now.AddMinutes(-3), now, active) == active
            && QuotaService.EndpointMaxAge(false, DateTimeOffset.MinValue, now, active) == TimeSpan.FromMinutes(45)
            && settings.ActiveRefresh(QuotaProvider.Codex) == TimeSpan.FromMinutes(10)
            && settings.ActiveRefresh(QuotaProvider.Claude) == TimeSpan.FromMinutes(5)
            && QuotaService.Consumed(Used(40, reset), Used(41, reset.AddSeconds(30)))
            && !QuotaService.Consumed(Used(41, reset), Used(41, reset))
            && !QuotaService.Consumed(Used(90, reset), Used(5, reset.AddHours(5)));
    }

    private static bool SelfTestUpdateParsing()
    {
        const string releases = """
            [
              { "tag_name": "v9.0.0", "draft": true, "html_url": "https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/releases/tag/v9.0.0" },
              { "tag_name": "v0.4.9", "prerelease": true, "html_url": "https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/releases/tag/v0.4.9" },
              { "tag_name": "v0.4.10-beta", "prerelease": true, "html_url": "https://example.com/elsewhere" },
              { "tag_name": "nightly" },
              { "tag_name": "v0.4.2", "html_url": "https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/releases/tag/v0.4.2" }
            ]
            """;
        var latest = UpdateChecker.FindLatest(releases);
        return latest is not null
            && latest.Version == new Version(0, 4, 10)
            && latest.PageUrl == UpdateChecker.ReleasesPage
            && UpdateChecker.ParseVersion("0.5") == new Version(0, 5, 0)
            && UpdateChecker.ParseVersion("0.4.2+abc123") == new Version(0, 4, 2)
            && UpdateChecker.ParseVersion("latest") is null
            && UpdateChecker.FindLatest("[]") is null
            && UpdateChecker.FindLatest("{\"message\":\"rate limited\"}") is null;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
