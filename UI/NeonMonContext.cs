using Microsoft.Win32;
using NeonMon.Models;
using NeonMon.Services;

namespace NeonMon.UI;

internal sealed class NeonMonContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly QuotaSettings _quotaSettings;
    private readonly SettingsStore _settingsStore;
    private readonly TelemetryService _telemetry;
    private readonly QuotaService _quota;
    private readonly MetricsBridge _bridge;
    private readonly Icon _trayIcon;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _systemMenu;
    private readonly ContextMenuStrip _quotaMenu;
    private readonly ContextMenuStrip _trayMenu;
    private readonly DarkMenuRenderer _darkMenuRenderer = new();
    private SettingsForm? _settingsForm;
    private string? _sampleStatusLine;
    private List<string> _settingsDrives = [];
    private bool _startWithWindows;
    private bool _startupOtherCopy;
    private readonly bool _quotaSettingsCreated;
    private FullscreenWatcher? _fullscreenWatcher;
    private ForegroundWatcher? _foregroundWatcher;
    private bool _fullscreen;
    private bool _started;
    private bool _exiting;

    public NeonMonContext(AppSettings settings, SettingsStore settingsStore, TelemetryService telemetry, QuotaService quota, MetricsBridge bridge)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _telemetry = telemetry;
        _quota = quota;
        _bridge = bridge;
        _quotaSettingsCreated = settings.Quota is null;
        _quotaSettings = settings.Quota ??= CreateDefaultQuotaSettings(settings);

        SystemForm = new SystemPulseForm(settings, SaveSettings, telemetry);
        QuotaForm = new QuotaPulseForm(_quotaSettings, SaveSettings);
        foreach (var form in Forms)
        {
            form.CanReveal = CanReveal;
            form.RevealStateChanged += OnRevealStateChanged;
            form.LayoutCommitted += ResolveSpacing;
            form.ExitRequested += Exit;
            form.NoticeRequested += ShowNotice;
        }

        _quota.SnapshotUpdated += QuotaForm.PostSnapshot;
        QuotaForm.CliRequested += OpenCli;
        RefreshCliAvailability();

        _systemMenu = CreateMenu(SystemForm);
        _quotaMenu = CreateMenu(QuotaForm);
        SystemForm.ContextMenuStrip = _systemMenu;
        QuotaForm.ContextMenuStrip = _quotaMenu;
        _trayMenu = CreateTrayMenu();

        _trayIcon = TrayIcon.Create();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "NeonMon",
            Visible = false,
            ContextMenuStrip = _trayMenu
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                FirstEnabledForm?.ToggleOpen();
            }
        };
    }

    public SystemPulseForm SystemForm { get; }
    public QuotaPulseForm QuotaForm { get; }

    private WidgetForm[] Forms => [SystemForm, QuotaForm];

    private WidgetForm? FirstEnabledForm => Forms.FirstOrDefault(form => form.Settings.Enabled);

    public void Start()
    {
        _ = SystemForm.Handle;
        _ = QuotaForm.Handle;
        QuotaForm.SetSnapshot(_quota.RefreshLocal());
        _quota.Start();
        ShowStrips();

        if (_quotaSettingsCreated)
        {
            SaveSettings();
        }

        _tray.Visible = true;
        if (_settings.HtmlBridgeEnabled && !_bridge.Start(_settings.HtmlBridgePort))
        {
            _settings.HtmlBridgeEnabled = false;
        }

        _telemetry.SetBackgroundSampling(_bridge.IsRunning);
        UpdateQuotaWork();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        try
        {
            _fullscreenWatcher = new FullscreenWatcher();
            _fullscreenWatcher.FullscreenChanged += OnFullscreenChanged;
        }
        catch
        {
            _fullscreenWatcher = null;
        }

        UpdateForegroundWatcher();
        _started = true;
    }

    public void ExitFromSignal()
    {
        if (SystemForm.IsHandleCreated && !SystemForm.IsDisposed)
        {
            SystemForm.BeginInvoke(new Action(Exit));
        }
    }

    public void ActivateFromSecondInstance()
    {
        if (!SystemForm.IsHandleCreated || SystemForm.IsDisposed)
        {
            return;
        }

        SystemForm.BeginInvoke(new Action(() =>
        {
            if (!HideForFullscreen)
            {
                FirstEnabledForm?.SetRevealState(RevealState.Open);
            }
        }));
    }

    private void ShowStrips(bool reopenPinned = false)
    {
        foreach (var form in Forms.Where(form => form.Settings.Enabled))
        {
            form.Show();
        }

        ResolveSpacing(QuotaForm);
        QuotaForm.AnimateToLayout();

        if (!reopenPinned)
        {
            return;
        }

        foreach (var form in Forms.Where(form => form.Visible && form.Settings.KeepOpen))
        {
            form.SetRevealState(RevealState.Open);
        }
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        if (_fullscreen == fullscreen)
        {
            return;
        }

        _fullscreen = fullscreen;
        ApplyFullscreen();
    }

    private bool HideForFullscreen => _fullscreen && _settings.Fullscreen == FullscreenBehavior.Hide;

    private void ApplyFullscreen()
    {
        if (!HideForFullscreen)
        {
            if (!Forms.Any(form => form.Visible))
            {
                ShowStrips(reopenPinned: true);
            }

            BringStripsToTop();
            return;
        }

        foreach (var form in Forms)
        {
            form.SetRevealState(RevealState.Hidden);
            form.Hide();
        }
    }

    private void SetFullscreenBehavior(FullscreenBehavior behavior)
    {
        _settings.Fullscreen = behavior;
        SaveSettings();
        UpdateForegroundWatcher();
        ApplyFullscreen();
    }

    private void UpdateForegroundWatcher()
    {
        if (_settings.Fullscreen != FullscreenBehavior.StayOnTop)
        {
            _foregroundWatcher?.Dispose();
            _foregroundWatcher = null;
            return;
        }

        if (_foregroundWatcher is null)
        {
            _foregroundWatcher = new ForegroundWatcher();
            _foregroundWatcher.Settled += BringStripsToTop;
        }
    }

    private void BringStripsToTop()
    {
        foreach (var form in Forms.Where(form => form.Visible))
        {
            form.BringToTop();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args) => ReattachStrips();

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category == UserPreferenceCategory.Desktop)
        {
            ReattachStrips();
        }
    }

    private void ReattachStrips()
    {
        foreach (var form in Forms)
        {
            form.ReattachToScreen();
        }

        ResolveSpacing(QuotaForm);
        QuotaForm.AnimateToLayout();
    }

    private static QuotaSettings CreateDefaultQuotaSettings(AppSettings settings) => new()
    {
        Size = WidgetSize.Small,
        DockEdge = settings.DockEdge,
        DockOffset = settings.DockOffset > 0.5 ? settings.DockOffset - 0.3 : settings.DockOffset + 0.3
    };

    private ContextMenuStrip CreateMenu(WidgetForm target)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        PopulateMenu(menu, target);
        menu.Opening += (_, _) => PopulateMenu(menu, target);
        menu.Opened += (_, _) => target.MenuOpened(menu);
        menu.Closed += (_, args) => target.MenuClosed(args.CloseReason);
        return menu;
    }

    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        PopulateTrayMenu(menu);
        menu.Opening += (_, _) => PopulateTrayMenu(menu);
        return menu;
    }

    private static void ClearMenu(ContextMenuStrip menu)
    {
        var previous = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var item in previous)
        {
            item.Dispose();
        }
    }

    private void PopulateMenu(ContextMenuStrip menu, WidgetForm target)
    {
        ClearMenu(menu);
        ApplyMenuStyle(menu);
        menu.Items.Add(new ToolStripMenuItem(ReferenceEquals(target, SystemForm) ? "SYSTEM PULSE" : "QUOTA PULSE") { Enabled = false });

        var stripItems = new List<ToolStripItem>
        {
            new ToolStripMenuItem("Open", null, (_, _) => target.SetRevealState(RevealState.Open)),
            new ToolStripMenuItem("Hide", null, (_, _) => target.SetRevealState(RevealState.Hidden)),
            new ToolStripSeparator()
        };

        var sizeMenu = new ToolStripMenuItem("Size");
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            var item = new ToolStripMenuItem(size.ToString()) { Checked = target.Settings.Size == size };
            item.Click += (_, _) => target.SetWidgetSize(size);
            sizeMenu.DropDownItems.Add(item);
        }
        stripItems.Add(sizeMenu);

        var dockMenu = new ToolStripMenuItem("Dock edge");
        foreach (var edge in Enum.GetValues<DockEdge>())
        {
            var item = new ToolStripMenuItem(edge.ToString()) { Checked = target.Settings.DockEdge == edge };
            item.Click += (_, _) => target.SetDockEdge(edge);
            dockMenu.DropDownItems.Add(item);
        }
        stripItems.Add(dockMenu);

        var monitorMenu = new ToolStripMenuItem("Monitor");
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var item = new ToolStripMenuItem($"{i + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? " (primary)" : "")}")
            {
                Checked = !target.Settings.FollowMouse && target.DockScreen.DeviceName == screen.DeviceName
            };
            item.Click += (_, _) => target.SetMonitor(screen);
            monitorMenu.DropDownItems.Add(item);
        }
        monitorMenu.DropDownItems.Add(new ToolStripSeparator());
        var followMouse = new ToolStripMenuItem("Follow mouse")
        {
            Checked = target.Settings.FollowMouse,
            ToolTipText = "While hidden, the strip moves to whichever monitor the pointer is on"
        };
        followMouse.Click += (_, _) => target.SetFollowMouse(!target.Settings.FollowMouse);
        monitorMenu.DropDownItems.Add(followMouse);
        stripItems.Add(monitorMenu);

        var keepOpen = new ToolStripMenuItem("Keep open") { Checked = target.Settings.KeepOpen };
        keepOpen.Click += (_, _) => target.SetKeepOpen(!target.Settings.KeepOpen);
        stripItems.Add(keepOpen);

        if (ReferenceEquals(target, QuotaForm))
        {
            stripItems.Add(new ToolStripSeparator());
            if (_quotaSettings.Shows(QuotaProvider.Claude))
            {
                stripItems.Add(CliItem(QuotaProvider.Claude, "Open Claude CLI"));
            }

            if (_quotaSettings.Shows(QuotaProvider.Codex))
            {
                stripItems.Add(CliItem(QuotaProvider.Codex, "Open Codex CLI"));
            }

            var displayMenu = new ToolStripMenuItem("Show quota as");
            foreach (var display in Enum.GetValues<QuotaDisplay>())
            {
                var label = display == QuotaDisplay.Remaining ? "Remaining (100 → 0)" : "Used (0 → 100)";
                var item = new ToolStripMenuItem(label) { Checked = _quotaSettings.Display == display };
                item.Click += (_, _) => SetQuotaDisplay(display);
                displayMenu.DropDownItems.Add(item);
            }
            stripItems.Add(displayMenu);

            var hiddenTabMenu = new ToolStripMenuItem("Hidden tab");
            foreach (var style in Enum.GetValues<HiddenTabStyle>())
            {
                var label = style == HiddenTabStyle.TwoLines ? "Two lines (weekly + 5-hour)" : "One line (worst limit)";
                var item = new ToolStripMenuItem(label) { Checked = _quotaSettings.HiddenTab == style };
                item.Click += (_, _) => SetHiddenTab(style);
                hiddenTabMenu.DropDownItems.Add(item);
            }
            stripItems.Add(hiddenTabMenu);
        }

        foreach (var item in stripItems)
        {
            item.Enabled &= target.Settings.Enabled;
            menu.Items.Add(item);
        }

        AddCommonItems(menu);
    }

    private void PopulateTrayMenu(ContextMenuStrip menu)
    {
        ClearMenu(menu);
        ApplyMenuStyle(menu);
        foreach (var (form, name) in Pulses)
        {
            menu.Items.Add(new ToolStripMenuItem($"Open {name}", null, (_, _) => form.SetRevealState(RevealState.Open))
            {
                Enabled = form.Settings.Enabled
            });
        }

        AddCommonItems(menu);
    }

    private void AddCommonItems(ContextMenuStrip menu)
    {
        menu.Items.Add(new ToolStripSeparator());
        var pulses = new ToolStripMenuItem("Pulses");
        foreach (var (form, name) in Pulses)
        {
            var item = new ToolStripMenuItem(name) { Checked = form.Settings.Enabled };
            item.Click += (_, _) => SetPulseEnabled(form, !form.Settings.Enabled);
            pulses.DropDownItems.Add(item);
        }
        menu.Items.Add(pulses);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Exit", null, (_, _) => Exit());
    }

    private (WidgetForm Form, string Name)[] Pulses => [(SystemForm, "System pulse"), (QuotaForm, "Quota pulse")];

    private void ApplyMenuStyle(ContextMenuStrip menu)
    {
        if (_settings.MenuStyle == MenuStyle.Dark)
        {
            menu.Renderer = _darkMenuRenderer;
        }
        else
        {
            menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
        }
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsDrives = TelemetryService.DriveNames();
        RefreshStartupState();
        _settingsForm = new SettingsForm(BuildSettingsPages, _trayIcon);
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    internal void SaveSettingsPreview(string path, int page, string? sampleStatusLine)
    {
        _sampleStatusLine = sampleStatusLine;
        _settingsDrives = sampleStatusLine is null
            ? TelemetryService.DriveNames()
            : TelemetrySnapshot.Sample.Drives.Select(drive => drive.Name).ToList();
        if (sampleStatusLine is null)
        {
            RefreshStartupState();
        }
        using var form = new SettingsForm(BuildSettingsPages, _trayIcon);
        form.SavePreview(path, page);
    }

    private IReadOnlyList<SettingsPage> BuildSettingsPages()
    {
        var general = new List<SettingsRow> { new SectionRow("Pulses") };
        general.AddRange(Pulses.Select(pulse => new ToggleRow(pulse.Name, () => pulse.Form.Settings.Enabled, on => SetPulseEnabled(pulse.Form, on))));
        general.AddRange(
        [
            new SectionRow("Appearance"),
            new ChoiceRow("Menu style", ["Windows", "NeonMon dark"], () => (int)_settings.MenuStyle, SetMenuStyle, "Right-click and tray menus."),
            new SectionRow("Behaviour"),
            new ChoiceRow("Over fullscreen apps", ["Stay on top", "Hide"], () => (int)_settings.Fullscreen,
                index => SetFullscreenBehavior((FullscreenBehavior)index)),
            new ToggleRow("Start with Windows", () => _startWithWindows, SetStartWithWindows,
                _startupOtherCopy ? "Starts a copy in another folder. Turn off and on to use this one." : "Adds NeonMon to your Startup folder."),
            new SectionRow("Advanced"),
            new ToggleRow($"HTML bridge · 127.0.0.1:{_settings.HtmlBridgePort}", () => _settings.HtmlBridgeEnabled, _ => ToggleBridge(),
                "Local JSON for your own dashboards.")
        ]);

        var quota = StripRows(QuotaForm);
        quota.AddRange(
        [
            new ChoiceRow("Show quota as", ["Remaining", "Used"], () => (int)_quotaSettings.Display, index => SetQuotaDisplay((QuotaDisplay)index)),
            new ChoiceRow("Hidden tab", ["Two lines", "One line"], () => (int)_quotaSettings.HiddenTab, index => SetHiddenTab((HiddenTabStyle)index),
                "Two lines: weekly outside, 5-hour inside. One line: the tighter limit."),
            new ToggleRow("Reset time when low", () => _quotaSettings.PeekResetWhenLow, SetPeekResetWhenLow,
                "Peek adds the reset time at 10% or less. At 0% it always shows."),
            new SectionRow("Claude"),
            new ToggleRow("Show Claude", () => _quotaSettings.ShowClaude, on => SetProviderShown(QuotaProvider.Claude, on)),
            new ActionRow("Status line data", StatusLineStatus, "Copy setting", CopyStatusLineSetting,
                "Claude Code sends limits through its status line. Needs Node.js."),
            new ToggleRow("Live endpoint fallback", () => _quotaSettings.ClaudeEndpointFallback, SetClaudeEndpointFallback,
                "Uses your CLI sign-in when status line data is stale. Never calls a model."),
            ActiveRefreshRow(QuotaProvider.Claude, "Endpoint reads while Claude is in use. Idle: every 45 min."),
            new ActionRow("CLI folder", () => _quotaSettings.ClaudeCliDirectory, "Browse", () => ChooseCliFolder(QuotaProvider.Claude)),
            new SectionRow("Codex"),
            new ToggleRow("Show Codex", () => _quotaSettings.ShowCodex, on => SetProviderShown(QuotaProvider.Codex, on)),
            ActiveRefreshRow(QuotaProvider.Codex, "Limit reads while Codex is in use. Idle: every 45 min."),
            new ActionRow("CLI folder", () => _quotaSettings.CodexCliDirectory, "Browse", () => ChooseCliFolder(QuotaProvider.Codex))
        ]);

        return
        [
            new SettingsPage("General", general),
            new SettingsPage("System pulse", [.. StripRows(SystemForm), .. PeekRows()]),
            new SettingsPage("Quota pulse", quota),
            new SettingsPage("Support", SupportRows())
        ];
    }

    private static readonly Image CoffeeIcon = LoadAsset("coffee.png");
    private static readonly Image SmileyIcon = LoadAsset("smiley.png");

    private static Image LoadAsset(string name)
    {
        using var stream = typeof(NeonMonContext).Assembly.GetManifestResourceStream($"NeonMon.Assets.{name}")!;
        return Image.FromStream(new MemoryStream(ReadAll(stream)));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private const string RepositoryUrl = "https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget";

    private List<SettingsRow> SupportRows()
    {
        var version = Application.ProductVersion.Split('+')[0];
        return
        [
            new SectionRow("Support NeonMon"),
            new ActionRow("Ko-fi", () => "ko-fi.com/awedev", "Buy a cappuccino", () => OpenUrl("https://ko-fi.com/awedev"),
                "NeonMon stays free. Support is optional and helps future releases", CoffeeIcon, SmileyIcon),
            new SectionRow("Project"),
            new ActionRow("Source code", () => "GitHub", "Open", () => OpenUrl(RepositoryUrl)),
            new ActionRow("Report an issue", () => "GitHub issues", "Open", () => OpenUrl($"{RepositoryUrl}/issues/new/choose")),
            new ActionRow("Version", () => version, "Release notes", () => OpenUrl($"{RepositoryUrl}/releases"))
        ];
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            ShowNotice("Could not open the browser.");
        }
    }

    private List<SettingsRow> StripRows(WidgetForm form)
    {
        var rows = new List<SettingsRow>
        {
            new SectionRow("Strip"),
            new ToggleRow("Show this pulse", () => form.Settings.Enabled, on => SetPulseEnabled(form, on))
        };
        if (!form.Settings.Enabled)
        {
            return rows;
        }

        var screens = Screen.AllScreens;
        rows.AddRange(
        [
            new ChoiceRow("Size", ["S", "M", "L"], () => (int)form.Settings.Size, index => form.SetWidgetSize((WidgetSize)index)),
            new ChoiceRow("Dock edge", ["Top", "Bottom", "Left", "Right"], () => (int)form.Settings.DockEdge,
                index => form.SetDockEdge((DockEdge)index)),
            new ChoiceRow("Monitor", ["Follow mouse", .. screens.Select((_, i) => $"{i + 1}")],
                () => form.Settings.FollowMouse ? 0 : Array.FindIndex(screens, screen => screen.DeviceName == form.DockScreen.DeviceName) + 1,
                index =>
                {
                    if (index == 0)
                    {
                        form.SetFollowMouse(true);
                    }
                    else
                    {
                        form.SetMonitor(screens[index - 1]);
                    }
                }),
            new ToggleRow("Keep open", () => form.Settings.KeepOpen, form.SetKeepOpen),
            new SectionRow("Opacity"),
            OpacityRow(form, "Hidden tab", StripSettings.HiddenOpacityChoices, settings => settings.HiddenOpacity,
                (settings, value) => settings.HiddenOpacity = value, "The whole tab."),
            OpacityRow(form, "Background", StripSettings.BackgroundOpacityChoices, settings => settings.BackgroundOpacity,
                (settings, value) => settings.BackgroundOpacity = value, "Peek and open. Text and bars stay solid.")
        ]);
        return rows;
    }

    private ChoiceRow OpacityRow(WidgetForm form, string label, int[] choices, Func<StripSettings, int> get,
        Action<StripSettings, int> set, string hint) =>
        new(label, [.. choices.Select(percent => $"{percent}%")],
            () => Array.IndexOf(choices, choices.MinBy(choice => Math.Abs(choice - get(form.Settings)))),
            index =>
            {
                set(form.Settings, choices[index]);
                SaveSettings();
                form.Invalidate();
            },
            hint);

    private List<SettingsRow> PeekRows()
    {
        if (!_settings.Enabled)
        {
            return [];
        }

        var chosen = _settings.PeekDrives ?? [TelemetryService.SystemDrive];
        var drives = _settingsDrives.Union(chosen, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        return
        [
            new SectionRow("Hidden tab", "Up to two. The first one you pick is the inner line."),
            HiddenMetricRow("CPU load", HiddenMetric.Cpu),
            HiddenMetricRow("GPU load", HiddenMetric.Gpu),
            HiddenMetricRow("Memory", HiddenMetric.Memory),
            HiddenMetricRow($"Drive {TelemetryService.SystemDrive} used", HiddenMetric.Drive),
            new ChoiceRow("Average over", [.. AppSettings.HiddenMetricSecondsChoices.Select(seconds => $"{seconds} s")],
                () => Math.Max(0, Array.IndexOf(AppSettings.HiddenMetricSecondsChoices, _settings.HiddenMetricSeconds)),
                index => SetHiddenMetricSeconds(AppSettings.HiddenMetricSecondsChoices[index]),
                "Samples once a second while the tab is hidden."),
            new SectionRow("Peek"),
            PeekValueRow("CPU load", PeekValues.Cpu),
            PeekValueRow("CPU temperature", PeekValues.CpuTemperature, "Needs a supported sensor (MSI today)."),
            PeekValueRow("GPU load", PeekValues.Gpu),
            PeekValueRow("GPU temperature", PeekValues.GpuTemperature),
            PeekValueRow("Memory", PeekValues.Memory),
            .. drives.Select(drive => new ToggleRow($"Drive {drive}", () => chosen.Contains(drive, StringComparer.OrdinalIgnoreCase),
                on => SetPeekDrive(drive, on), _settingsDrives.Contains(drive, StringComparer.OrdinalIgnoreCase) ? null : "Not found.")),
            PeekValueRow("Uptime", PeekValues.Uptime, "Since the last power-on or wake.")
        ];
    }

    private ToggleRow HiddenMetricRow(string label, HiddenMetric metric, string? hint = null) =>
        new(label, () => _settings.HiddenMetrics.Contains(metric), on => SetHiddenMetric(metric, on), hint);

    private void SetHiddenMetric(HiddenMetric metric, bool on)
    {
        if (on && !_settings.HiddenMetrics.Contains(metric) && _settings.HiddenMetrics.Count >= 2)
        {
            ShowNotice("The hidden tab shows up to two metrics.");
            return;
        }

        _settings.HiddenMetrics.Remove(metric);
        if (on)
        {
            _settings.HiddenMetrics.Add(metric);
        }

        SaveSettings();
        SystemForm.HiddenMetricsChanged();
    }

    private void SetHiddenMetricSeconds(int seconds)
    {
        _settings.HiddenMetricSeconds = seconds;
        SaveSettings();
        SystemForm.HiddenMetricsChanged();
    }

    private ToggleRow PeekValueRow(string label, PeekValues value, string? hint = null) =>
        new(label, () => _settings.Peek.HasFlag(value), on => SetPeekValue(value, on), hint);

    private bool IsLastPeekValue()
    {
        var count = Enum.GetValues<PeekValues>().Count(value => value != PeekValues.None && _settings.Peek.HasFlag(value))
            + (_settings.PeekDrives?.Count ?? 1);
        if (count > 1)
        {
            return false;
        }

        ShowNotice("The peek needs at least one value.");
        return true;
    }

    private void SetPeekValue(PeekValues value, bool on)
    {
        if (!on && IsLastPeekValue())
        {
            return;
        }

        _settings.Peek = on ? _settings.Peek | value : _settings.Peek & ~value;
        SaveSettings();
        SystemForm.PeekValuesChanged();
    }

    private void SetPeekDrive(string drive, bool on)
    {
        if (!on && IsLastPeekValue())
        {
            return;
        }

        var drives = (_settings.PeekDrives ?? [TelemetryService.SystemDrive]).Where(name => !name.Equals(drive, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on)
        {
            drives.Add(drive);
        }

        _settings.PeekDrives = [.. drives.Order(StringComparer.OrdinalIgnoreCase)];
        SaveSettings();
        SystemForm.PeekValuesChanged();
    }

    private string StatusLineStatus()
    {
        if (_sampleStatusLine is not null)
        {
            return _sampleStatusLine;
        }

        var file = new FileInfo(ClaudeStatuslineReader.FilePath);
        if (!file.Exists)
        {
            return "no data yet";
        }

        var age = DateTime.UtcNow - file.LastWriteTimeUtc;
        return age.TotalMinutes < 1 ? "updated just now"
            : age.TotalHours < 1 ? $"updated {(int)age.TotalMinutes} min ago"
            : age.TotalDays < 2 ? $"updated {(int)age.TotalHours}h ago"
            : $"updated {(int)age.TotalDays}d ago";
    }

    private void RefreshStartupState()
    {
        _startWithWindows = StartupShortcut.IsEnabled;
        _startupOtherCopy = _startWithWindows && !StartupShortcut.PointsToThisCopy();
    }

    private void SetStartWithWindows(bool on)
    {
        try
        {
            if (on)
            {
                StartupShortcut.Enable();
            }
            else
            {
                StartupShortcut.Disable();
            }
        }
        catch
        {
            ShowNotice("Could not change the Startup folder shortcut.");
        }

        RefreshStartupState();
    }

    private void SetMenuStyle(int index)
    {
        _settings.MenuStyle = (MenuStyle)index;
        SaveSettings();
    }

    private void ToggleBridge()
    {
        if (!_settings.HtmlBridgeEnabled && !_bridge.Start(_settings.HtmlBridgePort))
        {
            ShowNotice("The HTML bridge port is unavailable.");
            return;
        }

        if (_settings.HtmlBridgeEnabled)
        {
            _bridge.Stop();
        }

        _settings.HtmlBridgeEnabled = !_settings.HtmlBridgeEnabled;
        _telemetry.SetBackgroundSampling(_bridge.IsRunning);
        UpdateQuotaWork();
        SaveSettings();
    }

    private void SetPulseEnabled(WidgetForm form, bool enabled)
    {
        if (ReferenceEquals(form, QuotaForm) && enabled && !_quotaSettings.ShowClaude && !_quotaSettings.ShowCodex)
        {
            _quotaSettings.ShowClaude = true;
            _quotaSettings.ShowCodex = true;
            _quota.RequestRefresh();
        }

        form.Settings.Enabled = enabled;
        SaveSettings();
        UpdateQuotaWork();
        if (!enabled)
        {
            form.SetRevealState(RevealState.Hidden);
            form.Hide();
        }
        else if (!HideForFullscreen)
        {
            form.Show();
            ResolveSpacing(QuotaForm);
            QuotaForm.AnimateToLayout();
        }
    }

    // Hiding both providers turns the Quota pulse off; showing one again turns it back on.
    private void SetProviderShown(QuotaProvider provider, bool shown)
    {
        if (provider == QuotaProvider.Claude)
        {
            _quotaSettings.ShowClaude = shown;
        }
        else
        {
            _quotaSettings.ShowCodex = shown;
        }

        _quota.RequestRefresh();
        if (!_quotaSettings.ShowClaude && !_quotaSettings.ShowCodex)
        {
            SetPulseEnabled(QuotaForm, false);
            return;
        }

        if (shown && !_quotaSettings.Enabled)
        {
            SetPulseEnabled(QuotaForm, true);
        }
        else
        {
            SaveSettings();
        }

        ResolveSpacing(QuotaForm);
        QuotaForm.AnimateToLayout();
        QuotaForm.Invalidate();
    }

    private void UpdateQuotaWork() => _quota.SetPaused(!_quotaSettings.Enabled && !_bridge.IsRunning);

    private ToolStripMenuItem CliItem(QuotaProvider provider, string text)
    {
        var installed = CliCommand(provider) is not null;
        return new ToolStripMenuItem(installed ? text : $"{text} (not installed)", null, (_, _) => OpenCli(provider)) { Enabled = installed };
    }

    private static string? CliCommand(QuotaProvider provider) =>
        provider == QuotaProvider.Claude ? CliLauncher.ClaudeCommand() : CliLauncher.CodexCommand();

    private void RefreshCliAvailability() =>
        QuotaForm.SetCliAvailability(CliCommand(QuotaProvider.Claude) is not null, CliCommand(QuotaProvider.Codex) is not null);

    private string CliDirectory(QuotaProvider provider) =>
        provider == QuotaProvider.Claude ? _quotaSettings.ClaudeCliDirectory : _quotaSettings.CodexCliDirectory;

    private void ChooseCliFolder(QuotaProvider provider)
    {
        var name = provider == QuotaProvider.Claude ? "Claude" : "Codex";
        using var dialog = new FolderBrowserDialog
        {
            Description = $"Folder the {name} CLI opens in",
            UseDescriptionForTitle = true,
            SelectedPath = CliDirectory(provider)
        };
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        if (provider == QuotaProvider.Claude)
        {
            _quotaSettings.ClaudeCliDirectory = dialog.SelectedPath;
        }
        else
        {
            _quotaSettings.CodexCliDirectory = dialog.SelectedPath;
        }

        SaveSettings();
    }

    private void OpenCli(QuotaProvider provider)
    {
        var name = provider == QuotaProvider.Claude ? "Claude" : "Codex";
        if (CliCommand(provider) is not { } command)
        {
            ShowNotice($"The {name} CLI is not installed.");
        }
        else if (!CliLauncher.Launch(command, CliDirectory(provider)))
        {
            ShowNotice($"Could not open a terminal for the {name} CLI.");
        }
    }

    private void SetClaudeEndpointFallback(bool enabled)
    {
        _quotaSettings.ClaudeEndpointFallback = enabled;
        SaveSettings();
        _quota.RequestRefresh();
    }

    private ChoiceRow ActiveRefreshRow(QuotaProvider provider, string hint) => new(
        "Refresh while in use",
        [.. QuotaSettings.ActiveRefreshChoices.Select(minutes => $"{minutes} min")],
        () => Array.IndexOf(QuotaSettings.ActiveRefreshChoices, (int)_quotaSettings.ActiveRefresh(provider).TotalMinutes),
        index => SetActiveRefresh(provider, QuotaSettings.ActiveRefreshChoices[index]),
        hint);

    private void SetActiveRefresh(QuotaProvider provider, int minutes)
    {
        if (provider == QuotaProvider.Claude)
        {
            _quotaSettings.ClaudeActiveRefreshMinutes = minutes;
        }
        else
        {
            _quotaSettings.CodexActiveRefreshMinutes = minutes;
        }

        SaveSettings();
        _quota.RequestRefresh();
    }

    private void SetPeekResetWhenLow(bool enabled)
    {
        _quotaSettings.PeekResetWhenLow = enabled;
        SaveSettings();
        QuotaForm.RefreshDisplay();
    }

    private void SetHiddenTab(HiddenTabStyle style)
    {
        _quotaSettings.HiddenTab = style;
        SaveSettings();
        QuotaForm.RefreshDisplay();
    }

    private void SetQuotaDisplay(QuotaDisplay display)
    {
        _quotaSettings.Display = display;
        SaveSettings();
        QuotaForm.RefreshDisplay();
    }

    private WidgetForm Other(WidgetForm form) => ReferenceEquals(form, SystemForm) ? QuotaForm : SystemForm;

    private bool CanReveal(WidgetForm form)
    {
        var other = Other(form);
        return !(other.Visible && other.HoldsPointer(Cursor.Position));
    }

    private void OnRevealStateChanged(WidgetForm form, RevealState state)
    {
        if (ReferenceEquals(form, QuotaForm))
        {
            _quota.SetActive(state != RevealState.Hidden);
            if (state != RevealState.Hidden)
            {
                RefreshCliAvailability();
            }
        }

        if (state == RevealState.Hidden)
        {
            return;
        }

        var other = Other(form);
        if (other.Visible && other.State != RevealState.Hidden && !other.Settings.KeepOpen)
        {
            other.SetRevealState(RevealState.Hidden);
        }
    }

    private void ResolveSpacing(WidgetForm moved)
    {
        var other = Other(moved);
        if (!moved.Visible || !other.Visible
            || moved.Settings.DockEdge != other.Settings.DockEdge
            || moved.DockScreen.DeviceName != other.DockScreen.DeviceName)
        {
            return;
        }

        var theirs = other.GetReservedHoverBounds(other.Settings.DockOffset);
        var mine = moved.GetReservedHoverBounds(moved.Settings.DockOffset);
        if (!mine.IntersectsWith(theirs))
        {
            return;
        }

        var horizontal = moved.Settings.DockEdge is DockEdge.Top or DockEdge.Bottom;
        int Start(Rectangle bounds) => horizontal ? bounds.Left : bounds.Top;
        int End(Rectangle bounds) => horizontal ? bounds.Right : bounds.Bottom;
        var preferBefore = Start(mine) + End(mine) < Start(theirs) + End(theirs);

        double? Solve(bool before)
        {
            double low = 0;
            double high = 1;
            bool Fits(double offset)
            {
                var candidate = moved.GetReservedHoverBounds(offset);
                return before ? End(candidate) <= Start(theirs) : Start(candidate) >= End(theirs);
            }

            if (before ? !Fits(0) : !Fits(1))
            {
                return null;
            }

            for (var i = 0; i < 30; i++)
            {
                var middle = (low + high) / 2;
                if (Fits(middle) == before)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return before ? low : high;
        }

        var offset = Solve(preferBefore) ?? Solve(!preferBefore);
        if (offset is not null)
        {
            moved.Settings.DockOffset = offset.Value;
            SaveSettings();
        }
    }

    private void CopyStatusLineSetting()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "tools", "neonmon-statusline.js");
        if (!File.Exists(script))
        {
            ShowNotice("The status line script is missing next to NeonMon.exe.");
            return;
        }

        var command = $"node \\\"{script.Replace('\\', '/')}\\\"";
        Clipboard.SetText($"\"statusLine\": {{ \"type\": \"command\", \"command\": \"{command}\" }}");
        _tray.ShowBalloonTip(4000, "NeonMon", "Copied. Paste it into ~/.claude/settings.json. Needs Node.js.", ToolTipIcon.Info);
    }

    private void ShowNotice(string text) => _tray.ShowBalloonTip(2500, "NeonMon", text, ToolTipIcon.Warning);

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch
        {
        }

        _settingsForm?.Invalidate();
    }

    private void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _settingsForm?.Close();
        foreach (var form in Forms)
        {
            form.PrepareExit();
            form.Close();
        }

        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_started)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }

            _fullscreenWatcher?.Dispose();
            _foregroundWatcher?.Dispose();
            _quota.SnapshotUpdated -= QuotaForm.PostSnapshot;
            _tray.Visible = false;
            _tray.Dispose();
            _trayIcon.Dispose();
            SystemForm.Dispose();
            QuotaForm.Dispose();
            _systemMenu.Dispose();
            _quotaMenu.Dispose();
            _trayMenu.Dispose();
        }

        base.Dispose(disposing);
    }
}
