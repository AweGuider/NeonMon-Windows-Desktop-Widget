<p align="center">
  <img src="docs/images/banner.png" width="100%" alt="NeonMon: system stats and Claude Code + Codex usage limits, tucked into a tab on your screen edge">
</p>

<p align="center">
  <a href="https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/actions/workflows/build.yml"><img src="https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/actions/workflows/build.yml/badge.svg" alt="Build"></a>
  <a href="https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/releases"><img src="https://img.shields.io/github/v/release/AweGuider/NeonMon-Windows-Desktop-Widget?include_prereleases" alt="Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/AweGuider/NeonMon-Windows-Desktop-Widget" alt="License"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white" alt="Platform: Windows 10 | 11">
  <a href="https://ko-fi.com/awedev"><img src="https://img.shields.io/badge/Ko--fi-Buy%20a%20cappuccino-794BC4?logo=ko-fi&logoColor=white" alt="Support on Ko-fi"></a>
</p>

<p align="center">
  <a href="#why-neonmon">Why NeonMon</a> ·
  <a href="#what-it-is">What it is</a> ·
  <a href="#highlights">Highlights</a> ·
  <a href="#quota-pulse-data-sources">Data sources</a> ·
  <a href="#download">Download</a> ·
  <a href="#build-from-source">Build from source</a> ·
  <a href="#faq">FAQ</a> ·
  <a href="docs/gallery.md">Gallery</a> ·
  <a href="docs/devlog.md">Devlog</a> ·
  <a href="SECURITY.md">Security</a>
</p>

# NeonMon: Claude Code and Codex usage limits on your Windows desktop

NeonMon is a free, open-source Windows 10/11 widget that shows how much of your Claude Code and Codex usage limits is left: the 5-hour session limit and the weekly limit, with reset times and a weekly pace marker. It also shows CPU, GPU, memory, and disk stats. It hides as a thin tab on the screen edge and opens on hover. No API key, no paid API calls.

> Unofficial. NeonMon is not affiliated with, endorsed by, or sponsored by Anthropic or OpenAI.

## Why NeonMon

Without it, checking your machine or your quota means opening separate apps again and again. With the numbers always at hand, you catch resource pressure early and see a limit coming before it stops your work.

## What it is

NeonMon sits on the edge of your screen as a small tab. Hover over it for a quick glance, click it to open the full panel, and move away to let it tuck back in. It never takes a taskbar slot and does not steal focus.

It has two strips:

- **System pulse** shows uptime, CPU and GPU load and temperature, memory, free disk space, the busiest process, and recent GPU-timeout diagnostics.
- **Quota pulse** shows Claude Code and Codex 5-hour and weekly limits side by side, counting down from 100%, with reset times, a weekly pace marker, and Codex reset credits. Its min-max mode turns the week into a daily quest, so less quota goes unused, and a Results view shows quests this week, last week's result, and the last 8 weeks.

| Hidden | Peek | Open |
| --- | --- | --- |
| A tab on the screen edge | Hover for the headline numbers | Click for the full panel |
| ![NeonMon hidden as two thin tabs on the Windows screen edge](docs/images/hidden-tabs.png) | ![Quota pulse peek showing Claude Code and Codex usage remaining on hover](docs/images/quota-peek.png) <br> ![System pulse peek showing CPU and GPU load, free disk space, and uptime on hover](docs/images/system-peek.png) | ![Quota pulse small layout with Claude Code and Codex 5-hour and weekly limits and reset times](docs/images/quota-small.png) |

![System pulse large layout with uptime, CPU and GPU load, GPU temperature, memory, free space per drive, top CPU process, and GPU stability](docs/images/system-large.png)

![Quota pulse large layout with Claude Code and Codex 5-hour and weekly usage limits, reset times, and a daily quest for maxing out the weekly limit](docs/images/quota-large.png)

More screenshots are in the [gallery](docs/gallery.md), and the [devlog](docs/devlog.md) shows how the UI evolved.

## Highlights

- **Out of the way:** hidden by default and never steals focus. Strips stay above fullscreen apps by default, or can hide while a fullscreen app runs on the primary monitor (**Settings → General → Over fullscreen apps**).
- **Light:** sampling pauses while a strip is hidden. Measured with both strips hidden, NeonMon uses about 0.1% of one CPU core and about 35 MB of memory.
- **Multi-monitor:** put each strip on any monitor, dock it to any edge, and drag it along that edge. **Follow mouse** moves a hidden strip to whichever monitor the pointer is on. Text and layout scale correctly across monitors with different scaling.
- **Readable anywhere:** the hidden tab pairs a dark body with a light ring, so it stays visible on white pages, bright video, and dark fullscreen video alike.
- **Three layouts:** small, medium, and large for each strip.
- **Your peek:** choose what System pulse shows on hover: load, temperatures, memory, any drive, and uptime.
- **Min-max:** a daily quest splits what's left of the weekly limit across the days to reset. Finishing it lights the provider's dot on the hidden tab, and the last day of the week is flagged while a lot is left. Quest days and weekly results stay in a local file (**Settings → Quota pulse → Min-max**).
- **Settings window:** every option grouped by pulse, and either pulse can be turned off if you only want one.
- **No paid API usage:** Quota pulse never calls a model and never uses API keys (see below).
- **Optional local JSON bridge** for your own dashboards.
- **Update notice:** once a day NeonMon asks GitHub whether a newer release exists and tells you once per version. It reads the public release list only, sends no account or usage data, and never downloads or installs anything. Turn it off with **Settings → General → Check for updates**.

## NeonMon vs the built-in ways

| | NeonMon | `/usage` in Claude Code | `/status` in Codex | claude.ai usage page |
| --- | --- | --- | --- | --- |
| Visible while you work | Yes, a tab on the screen edge | No, run it in a session | No, run it in a session | No, open it in a browser |
| Claude and Codex together | Yes | Claude only | Codex only | Claude only |
| Reset times | Yes | Yes | Yes | Yes |
| Weekly pace and daily quest | Yes | No | No | No |
| System stats (CPU, GPU, memory, disk) | Yes | No | No | No |

## Quota pulse data sources

Quota pulse never calls a model and never uses API keys. Its sources are local:

- **Claude Code:** the plan limits Claude Code passes to its status line. This needs [Node.js](https://nodejs.org). The script ships as `tools\neonmon-statusline.js` next to `NeonMon.exe`, and **Copy setting** in **Settings → Quota pulse** copies the line below with the right path. Paste it into `~/.claude/settings.json`; it replaces any status line you already have:

  ```json
  "statusLine": { "type": "command", "command": "node \"<path-to-NeonMon>/tools/neonmon-statusline.js\"" }
  ```

  The script writes `%LOCALAPPDATA%\NeonMon\claude-statusline.json` whenever a CLI session renders its status line. The Claude desktop app does not run status lines, so Claude values refresh only when you use the `claude` CLI; older values are marked stale. **Open Claude CLI** in the Quota pulse menu opens a terminal and starts `claude` in the CLI folder set in **Settings → Quota pulse** (your user folder by default). **Open Codex CLI** works the same way for Codex.
- **Codex:** the recent session logs under `~/.codex/sessions`, plus a read-only `codex app-server` call (`account/rateLimits/read`) for fresh limits and reset credits: every 10 minutes while Codex is in use (2 or 5 in **Settings → Quota pulse**), every 45 minutes otherwise. The read does not consume quota.
- **Claude live endpoint fallback (off by default):** when enabled in **Settings → Quota pulse** and the statusline data is older than 2 minutes while the Quota pulse is revealed, 5 minutes while Claude is in use (2 or 10 in settings), or 45 minutes otherwise, NeonMon reads plan usage from `api.anthropic.com/api/oauth/usage` with the existing Claude CLI sign-in in `~/.claude/.credentials.json`. Claude counts as in use while the statusline, the endpoint's usage, or a Claude Code transcript under `~/.claude/projects` changes; NeonMon only receives change notifications for transcripts and never reads them. It only reads that file, never refreshes or stores tokens, waits at least two minutes between requests, backs off on errors, and skips the call when the sign-in has expired. The endpoint is undocumented and may change.

Values show remaining quota by default; switch to used quota with **Show quota as** in the Quota pulse menu or Settings. Run `NeonMon.exe --dump-quota quota.json` to write the current quota data to a file.

## Download

Each [release](https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/releases) from v0.8.0 on has two downloads for 64-bit Windows 10/11:

- `NeonMon-<version>.zip`: under 1 MB. Needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0); Windows offers to install it if it is missing.
- `NeonMon-<version>-standalone.zip`: about 45 MB, with .NET included. Nothing else to install, but it starts a little slower.

Unzip either one anywhere and run `NeonMon.exe`. The builds are not code-signed, so Windows SmartScreen may ask for confirmation the first time. To update, close NeonMon and unzip the new version over the old one; your settings are kept.

## Build from source

Requirements: Windows 10/11 and the .NET 9 SDK.

Double-click `build.bat`. It offers to close a running NeonMon, builds, and starts the new build (answer N to skip). The app is at:

```text
bin\Release\net9.0-windows\NeonMon.exe
```

Or use PowerShell:

```powershell
dotnet build -c Release --no-restore -p:TargetPlatformDisplayName=Windows
.\bin\Release\net9.0-windows\NeonMon.exe
```

**Settings → General → Start with Windows** adds a shortcut to your Startup folder that starts this copy at sign-in; if you move the folder, turn it off and on again.

Settings are stored in `%LOCALAPPDATA%\NeonMon\settings.json`, and min-max quest days and weekly results in `quota-history.json` next to it. Launching NeonMon again opens the running copy instead of starting a second one, and `NeonMon.exe --exit` closes it.

## HTML integration

Enable **HTML bridge** in **Settings → General**, then fetch the local JSON endpoint:

```javascript
const metrics = await fetch('http://127.0.0.1:27171/api/v1/metrics')
  .then(response => response.json());
```

Quota data is available at `http://127.0.0.1:27171/api/v1/quota`.

The bridge is disabled by default, binds only to `127.0.0.1`, and accepts no commands. Browser pages can read it only from loopback origins (`http://localhost:*`, `http://127.0.0.1:*`). To allow another page, such as a hosted dashboard, add its exact origin to `HtmlBridgeAllowedOrigins` in `settings.json`:

```json
"HtmlBridgeAllowedOrigins": ["https://dashboard.example"]
```

## Currently unavailable

- Reliable fan RPM/duty telemetry. Many laptops do not expose a documented, dependable tachometer mapping, so NeonMon does not guess values or write embedded-controller registers.
- CPU temperature comes from vendor sensor interfaces (currently MSI's read-only WMI interface) and shows as unavailable on machines without one.

## Potential future features

- Warning thresholds.
- History graphs and lightweight data export.
- Native embeddable HTML component rather than JSON only.
- Packaged installer.
- Additional documented hardware sensor backends.

## FAQ

### How do I see my Claude Code usage limit on Windows?

Run NeonMon and paste the status line from **Settings → Quota pulse → Copy setting** into `~/.claude/settings.json`. Your 5-hour and weekly limits then stay on the screen edge.

### When does my Claude Code 5-hour limit reset?

Quota pulse shows the reset time for both the 5-hour and the weekly limit, with a countdown, so you don't have to run `/usage`.

### Does NeonMon use my API key or cost tokens?

No. It reads data Claude Code and Codex already write on your machine and never calls a model. See [Quota pulse data sources](#quota-pulse-data-sources).

### Does it work with the Claude desktop app?

Not directly. The desktop app does not run status lines, so Claude values refresh when you use the `claude` CLI. An opt-in fallback can read the usage endpoint instead.

### Does it show Codex usage limits too?

Yes. Codex limits and reset credits show side by side with Claude, read from local session logs and a read-only call that does not consume quota.

### Does it run on macOS or Linux?

Not yet. NeonMon is Windows 10/11 only for now, and a macOS version is planned. Watch the repo's releases to hear when it's ready.

## Contributing

Issues and small pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md), and report security issues privately as described in [SECURITY.md](SECURITY.md).

## Support AweDev

If NeonMon saved you time, you can buy a cappuccino to support future releases and testing. Support is optional; NeonMon stays free.

[☕ Buy a Cappuccino on Ko-fi](https://ko-fi.com/awedev)

## License

[MIT](LICENSE) © 2026 AweDev
