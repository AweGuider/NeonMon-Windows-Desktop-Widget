# Devlog

How NeonMon grew from a single system-monitor strip into a two-strip desktop widget. Every screenshot below is rendered by the build from that point in history.

[Back to the README](../README.md)

## October 1, 2026: first version

NeonMon started as one dockable strip: a thin line on the screen edge that expands into a system panel with uptime, CPU, GPU, memory, free disk space, the busiest process, and GPU-timeout diagnostics.

The first build had a DPI bug: at 125% scaling, the labels above each value were clipped at the top (look at UPTIME, CPU, and GPU).

![First version with clipped labels](images/history-v1-clipped-labels.png)

The same day, the clipping was fixed across all three layouts, and drive cards became shortcuts that open the drive in File Explorer.

![Labels fixed](images/history-v2-dpi-fix.png)

## October 5, 2026: Quota pulse

The goal: one glance at Claude Code and Codex usage limits, to get the most out of both subscriptions without opening either app.

Before writing code, the layout went through mockups. Decisions that came out of that review:

- One combined strip rather than a separate app, so it shares NeonMon's tray, settings, and edge behavior.
- Both providers count down from 100% remaining, the way Codex reports it, rather than Claude's 0-to-100% used.
- Claude first and Codex second, each marked with its own icon instead of text labels.
- Peek shows each provider's 5-hour value, plus the weekly value on an amber chip only when weekly is the tighter limit.
- Codex reset credits are shown with their earliest expiry.

**No paid API usage** was a hard rule. Claude limits come from the data Claude Code already passes to its status line, and Codex limits come from its local session logs plus a read-only app-server call. An optional fallback reads Claude's plan usage endpoint with the existing CLI sign-in. It is off by default, never refreshes tokens, and never calls a model.

![First Quota pulse panel](images/history-quota-first.png)

Rendering also gained a fixed sample-data mode, so every UI change could be checked as pixel-identical against a baseline before and after.

## October 5, 2026: performance and robustness

A profiling pass cut idle cost. Sampling now pauses while strips are hidden, the expensive process scan only runs while the panel is open, and hidden strips repaint only when what they show actually changes.

| | Before | After |
| --- | --- | --- |
| CPU, strips hidden | 0.52–0.70% of one core | 0.09–0.16% of one core |
| Private memory | 39 MB | 33–34 MB |

Measured by alternating before and after builds on the same machine.

Robustness work followed: single instance (launching again opens the running copy), recovery from display and taskbar changes, fullscreen handling, and origin checks on the local JSON bridge.

Smaller additions: the app icon, a visible reason when Claude data is stale (such as an expired CLI sign-in), a one-click **Open Claude CLI** shortcut to refresh it, and an immediate refresh when you peek.

## October 5, 2026: owner notes round

Feedback from daily use became the next batch:

- **Fullscreen:** strips were disappearing behind fullscreen apps. Windows can drop always-on-top windows below a newly focused fullscreen window, so NeonMon now re-asserts on-top after every focus change. Staying visible is the default, and hiding is an option.
- **Multiple monitors:** each strip can be moved to any monitor and remembers it. **Follow mouse** moves a hidden strip to the monitor the pointer is on. Testing on two monitors with different scaling exposed text drawn 25% too large on the second one, so fonts are now sized for each monitor's DPI.
- **Visibility:** the old hidden strip, a dark sliver with a thin line, nearly vanished on white pages and on dark video. Three designs were mocked up on four backgrounds, and a combination won: a tab hanging off the screen edge (its flat side against the edge) with a dark body, a bright bar, and a light outer ring.

![Hidden strips before and after the redesign](images/hidden-strips-before-after.png)
