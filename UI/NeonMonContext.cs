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
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _systemMenu;
    private readonly ContextMenuStrip _quotaMenu;
    private readonly bool _quotaSettingsCreated;
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

        _systemMenu = CreateMenu(SystemForm);
        _quotaMenu = CreateMenu(QuotaForm);
        SystemForm.ContextMenuStrip = _systemMenu;
        QuotaForm.ContextMenuStrip = _quotaMenu;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
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
        SystemForm.Show();
        if (_quotaSettings.Enabled)
        {
            QuotaForm.Show();
            ResolveSpacing(QuotaForm);
            QuotaForm.AnimateToLayout();
        }

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

        var keepOpen = new ToolStripMenuItem("Keep open") { Checked = target.Settings.KeepOpen };
        keepOpen.Click += (_, _) => target.SetKeepOpen(!target.Settings.KeepOpen);
        menu.Items.Add(keepOpen);

        var htmlBridge = new ToolStripMenuItem($"HTML bridge · 127.0.0.1:{_settings.HtmlBridgePort}") { Checked = _settings.HtmlBridgeEnabled };
        htmlBridge.Click += (_, _) => ToggleBridge();
        menu.Items.Add(htmlBridge);

        menu.Items.Add(new ToolStripSeparator());
        if (ReferenceEquals(target, SystemForm) && _quotaSettings.Enabled)
        {
            menu.Items.Add("Open quota pulse", null, (_, _) => QuotaForm.SetRevealState(RevealState.Open));
        }

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
            QuotaForm.Show();
            ResolveSpacing(QuotaForm);
            QuotaForm.AnimateToLayout();
        }
        else
        {
            QuotaForm.SetRevealState(RevealState.Hidden);
            QuotaForm.Hide();
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
            _quota.SetActive(state == RevealState.Open);
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
            _quota.SnapshotUpdated -= QuotaForm.PostSnapshot;
            _tray.Visible = false;
            _tray.Dispose();
            SystemForm.Dispose();
            QuotaForm.Dispose();
            _systemMenu.Dispose();
            _quotaMenu.Dispose();
        }

        base.Dispose(disposing);
    }
}
