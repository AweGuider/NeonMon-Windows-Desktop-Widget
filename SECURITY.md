# Security policy

## Supported versions

Only the latest commit on `main` and the most recent release receive fixes.

## Reporting a vulnerability

Please do not open a public issue for security problems. Report them privately through GitHub instead: open the repository's **Security** tab and choose **Report a vulnerability**. You will get a reply within a week, and a fix or a decision within 30 days.

Include what you found, the steps to reproduce it, and the NeonMon version or commit.

## What NeonMon touches

These are the parts most relevant to a security review:

- **Local JSON bridge:** off by default. When enabled it listens only on `127.0.0.1`, serves read-only data, accepts no commands, and answers browser requests only from loopback origins or origins you add to `HtmlBridgeAllowedOrigins`.
- **Claude CLI sign-in:** the optional Claude endpoint fallback (off by default) reads `~/.claude/.credentials.json` to call Anthropic's plan-usage endpoint. NeonMon never writes, refreshes, logs, or sends that token anywhere else.
- **Codex:** NeonMon runs the local `codex app-server` and calls only `account/rateLimits/read`. It never calls methods that consume quota or reset credits.
- **No model calls and no API keys:** NeonMon never sends prompts to any AI service.
- **Hardware sensors:** NVIDIA and MSI sensor access is read-only. NeonMon never writes embedded-controller registers.
