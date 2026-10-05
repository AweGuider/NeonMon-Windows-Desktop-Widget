# Contributing

NeonMon is a personal project shared as a showcase. Issues, ideas, and small pull requests are welcome; for anything larger, open an issue first so we can agree on the approach.

## Getting set up

Requirements: Windows 10/11 and the .NET 9 SDK.

```powershell
dotnet build -c Release
.\bin\Release\net9.0-windows\NeonMon.exe --self-test
```

The self-test exits with code 0 when the local bridge, quota data, and safety checks work. CI runs the same build and self-test on every push and pull request.

## Checking UI changes

NeonMon can render any strip to a PNG with fixed sample data, which makes UI changes easy to compare before and after:

```powershell
.\bin\Release\net9.0-windows\NeonMon.exe --render-preview system.png Large --sample
.\bin\Release\net9.0-windows\NeonMon.exe --render-preview quota.png Large --strip quota --state Peek --dock Top --sample
```

Options: `--strip system|quota`, `--state Hidden|Peek|Open`, `--dock Top|Bottom|Left|Right`, and a size of `Small`, `Medium`, or `Large`. If a change is not meant to affect a view, its render should stay pixel-identical. Include before and after renders in pull requests that change the UI.

## Ground rules

- **No paid API usage.** NeonMon must never call a model, require an API key, or consume quota. Read-only sources only.
- **Stay light.** Do no work while strips are hidden unless it is needed; avoid polling where an event exists.
- **Match the surrounding code.** Follow the existing style and naming, keep changes focused, and add comments only when the reason is not obvious from the code.
- **Commit messages** use the form `[Action : Area] Summary`, for example `[Fix : Quota Pulse] Showed why Claude data is stale`.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE) and that you will follow the [Code of Conduct](CODE_OF_CONDUCT.md).
