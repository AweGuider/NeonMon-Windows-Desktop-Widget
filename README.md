# NeonMon

NeonMon is a lightweight Windows system-monitoring widget. It stays as a thin screen-edge pulse strip, expands on hover, and opens on click.

## Current features

- Small, medium, and large layouts.
- CPU/GPU load and temperature, memory use, uptime, remaining disk space, GPU clocks, top CPU process, and recent GPU-timeout diagnostics.
- Click a disk card to open that drive's root folder in File Explorer.
- Adjustable top, right, bottom, or left docking. Click the arrow in the header to cycle edges, or use the tray menu.
- Pin, minimize, custom control tooltips, translucent styling, and short transition animations.
- Optional read-only HTML integration at `http://127.0.0.1:27171/api/v1/metrics`.
- Low-overhead sampling: every second while open and every five seconds while collapsed.
- Quota pulse: a second strip showing Claude Code and Codex 5-hour and weekly limits, reset times, pacing, and Codex reset credits.

## Quota pulse

Quota pulse never calls a model and never uses API keys. Its sources are local:

- **Claude Code:** the plan limits Claude Code passes to its status line. Point the `statusLine` setting in `~/.claude/settings.json` at the bundled script:

  ```json
  "statusLine": { "type": "command", "command": "node \"C:/Projects/Programming/NeonMon/tools/neonmon-statusline.js\"" }
  ```

  The script writes `%LOCALAPPDATA%\NeonMon\claude-statusline.json` whenever a CLI session renders its status line. The Claude desktop app does not run status lines, so Claude values refresh only when you use the `claude` CLI; older values are marked stale.
- **Codex:** the newest session log under `~/.codex/sessions`, plus a read-only `codex app-server` call (`account/rateLimits/read`) every 30 minutes for fresh limits and reset credits. The read does not consume quota.

Values show remaining quota by default; switch to used quota from the tray menu (**Show quota as**). Run `NeonMon.exe --dump-quota quota.json` to write the current quota data to a file.

## Build and run

Requirements: Windows 10/11 and the .NET 9 SDK.

Double-click `build.bat`, then run:

```text
bin\Release\net9.0-windows\NeonMon.exe
```

Or use PowerShell:

```powershell
dotnet build -c Release --no-restore -p:TargetPlatformDisplayName=Windows
.\bin\Release\net9.0-windows\NeonMon.exe
```

Settings are stored in `%LOCALAPPDATA%\NeonMon\settings.json`.

## HTML integration

Enable **HTML bridge** from the tray menu, then fetch the local JSON endpoint:

```javascript
const metrics = await fetch('http://127.0.0.1:27171/api/v1/metrics')
  .then(response => response.json());
```

Quota data is available at `http://127.0.0.1:27171/api/v1/quota`.

The bridge is disabled by default, binds only to `127.0.0.1`, and accepts no commands.

## Currently unavailable

- Reliable fan RPM/duty telemetry. The MSI GS65 Stealth 9SG does not expose a documented, dependable tachometer mapping. NeonMon does not guess values or write unsafe embedded-controller registers.
- CPU temperature can remain unavailable when MSI's read-only WMI interface is not exposed to the process.

## Potential future features

- Selectable metrics and warning thresholds.
- History graphs and lightweight data export.
- Native embeddable HTML component rather than JSON only.
- Start-with-Windows option and packaged installer.
- Additional documented hardware sensor backends.
