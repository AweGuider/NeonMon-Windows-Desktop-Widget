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

        _trayIcon = TrayIcon.Create();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "NeonMon",
            Visible = false,
            ContextMenuStrip = _systemMenu
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                SystemForm.ToggleOpen();
            }
        };
    }

    public SystemPulseForm SystemForm { get; }
    public QuotaPulseForm QuotaForm { get; }

    private WidgetForm[] Forms => [SystemForm, QuotaForm];

    public void Start()
    {
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
                SystemForm.SetRevealState(RevealState.Open);
            }
        }));
    }

    private void ShowStrips(bool reopenPinned = false)
    {
        SystemForm.Show();
        if (_quotaSettings.Enabled)
        {
            QuotaForm.Show();
            ResolveSpacing(QuotaForm);
            QuotaForm.AnimateToLayout();
        }

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
            if (!SystemForm.Visible)
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
        menu.Closed += (_, _) => target.ScheduleCollapse();
        return menu;
    }

    private void PopulateMenu(ContextMenuStrip menu, WidgetForm target)
    {
        var previous = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var item in previous)
        {
            item.Dispose();
        }

        menu.Items.Add("Open", null, (_, _) => target.SetRevealState(RevealState.Open));
        menu.Items.Add("Hide", null, (_, _) => target.SetRevealState(RevealState.Hidden));

        var sizeMenu = new ToolStripMenuItem("Layout size");
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            var item = new ToolStripMenuItem(size.ToString()) { Checked = target.Settings.Size == size };
            item.Click += (_, _) => target.SetWidgetSize(size);
            sizeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(sizeMenu);

        var dockMenu = new ToolStripMenuItem("Dock edge");
        foreach (var edge in Enum.GetValues<DockEdge>())
        {
            var item = new ToolStripMenuItem(edge.ToString()) { Checked = target.Settings.DockEdge == edge };
            item.Click += (_, _) => target.SetDockEdge(edge);
            dockMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(dockMenu);

        var monitorMenu = new ToolStripMenuItem("Monitor");
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var label = $"{i + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? " (primary)" : "")}";
            var item = new ToolStripMenuItem(label)
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
        menu.Items.Add(monitorMenu);

        var keepOpen = new ToolStripMenuItem("Keep open") { Checked = target.Settings.KeepOpen };
        keepOpen.Click += (_, _) => target.SetKeepOpen(!target.Settings.KeepOpen);
        menu.Items.Add(keepOpen);

        var fullscreenMenu = new ToolStripMenuItem("Over fullscreen apps");
        foreach (var behavior in Enum.GetValues<FullscreenBehavior>())
        {
            var label = behavior == FullscreenBehavior.StayOnTop ? "Stay on top" : "Hide";
            var item = new ToolStripMenuItem(label) { Checked = _settings.Fullscreen == behavior };
            item.Click += (_, _) => SetFullscreenBehavior(behavior);
            fullscreenMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(fullscreenMenu);

        var htmlBridge = new ToolStripMenuItem($"HTML bridge · 127.0.0.1:{_settings.HtmlBridgePort}") { Checked = _settings.HtmlBridgeEnabled };
        htmlBridge.Click += (_, _) => ToggleBridge();
        menu.Items.Add(htmlBridge);

        menu.Items.Add(new ToolStripSeparator());
        if (ReferenceEquals(target, SystemForm) && _quotaSettings.Enabled)
        {
            menu.Items.Add("Open quota pulse", null, (_, _) => QuotaForm.SetRevealState(RevealState.Open));
        }

        AddCliItem(menu, QuotaProvider.Claude, "Open Claude CLI");
        AddCliItem(menu, QuotaProvider.Codex, "Open Codex CLI");
        menu.Items.Add("Copy Claude statusLine setting", null, (_, _) => CopyStatusLineSetting());

        var quotaStrip = new ToolStripMenuItem("Quota pulse strip") { Checked = _quotaSettings.Enabled };
        quotaStrip.Click += (_, _) => SetQuotaEnabled(!_quotaSettings.Enabled);
        menu.Items.Add(quotaStrip);

        var endpoint = new ToolStripMenuItem("Claude: live endpoint fallback")
        {
            Checked = _quotaSettings.ClaudeEndpointFallback,
            ToolTipText = "When the CLI statusline data is stale, read plan usage from Anthropic's usage endpoint "
                + "with the Claude CLI sign-in. Never refreshes tokens or calls a model."
        };
        endpoint.Click += (_, _) => SetClaudeEndpointFallback(!_quotaSettings.ClaudeEndpointFallback);
        menu.Items.Add(endpoint);
        menu.Items.Add(new ToolStripMenuItem(_quotaSettings.ClaudeEndpointFallback
            ? "    on · used when statusline data is stale"
            : "    off · statusline only (free, local)") { Enabled = false });

        var displayMenu = new ToolStripMenuItem("Show quota as");
        foreach (var display in Enum.GetValues<QuotaDisplay>())
        {
            var label = display == QuotaDisplay.Remaining ? "Remaining (100 → 0)" : "Used (0 → 100)";
            var item = new ToolStripMenuItem(label) { Checked = _quotaSettings.Display == display };
            item.Click += (_, _) => SetQuotaDisplay(display);
            displayMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(displayMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
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
        SaveSettings();
    }

    private void SetQuotaEnabled(bool enabled)
    {
        _quotaSettings.Enabled = enabled;
        SaveSettings();
        if (enabled)
        {
            if (!HideForFullscreen)
            {
                QuotaForm.Show();
                ResolveSpacing(QuotaForm);
                QuotaForm.AnimateToLayout();
            }
        }
        else
        {
            QuotaForm.SetRevealState(RevealState.Hidden);
            QuotaForm.Hide();
        }
    }

    private void AddCliItem(ContextMenuStrip menu, QuotaProvider provider, string text)
    {
        var installed = CliCommand(provider) is not null;
        menu.Items.Add(new ToolStripMenuItem(installed ? text : $"{text} (not installed)", null, (_, _) => OpenCli(provider)) { Enabled = installed });
    }

    private static string? CliCommand(QuotaProvider provider) =>
        provider == QuotaProvider.Claude ? CliLauncher.ClaudeCommand() : CliLauncher.CodexCommand();

    private void RefreshCliAvailability() =>
        QuotaForm.SetCliAvailability(CliCommand(QuotaProvider.Claude) is not null, CliCommand(QuotaProvider.Codex) is not null);

    private void OpenCli(QuotaProvider provider)
    {
        var name = provider == QuotaProvider.Claude ? "Claude" : "Codex";
        if (CliCommand(provider) is not { } command)
        {
            ShowNotice($"The {name} CLI is not installed.");
        }
        else if (!CliLauncher.Launch(command, provider == QuotaProvider.Claude ? _quotaSettings.ClaudeCliDirectory : null))
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
        return !(other.Visible && other.State != RevealState.Hidden && other.Bounds.Contains(Cursor.Position));
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
    }

    private void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
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
        }

        base.Dispose(disposing);
    }
}
