# Changelog

Release notes for each version. When a version tag is pushed, the Release workflow copies the matching section into the GitHub release. The [devlog](docs/devlog.md) has the longer story with screenshots.

## v0.9.0

### What's new

- **Min-max mode, on by default.** A daily quest splits what's left of the weekly limit across the days to the reset. The Large layout shows today's progress and whether you're on track to max out or how much will go unused; Small and Medium show today's slice on the weekly bar. Finishing it lights the provider's dot on the hidden tab for the rest of the day.
- **Results view.** A Quota / Results switch in the header shows quests this week, last week's result with a streak of maxed weeks, and the last 8 weeks. Past Codex weeks are rebuilt from local session logs; Claude history starts on first run.
- **Weekly ends soon.** The last 24 hours of a week are flagged while 25% or more is left.
- **Settings → Quota pulse → Min-max** turns each part on or off, sets what counts as maxed (90/95/100%), and has a Reward option that shows low quota in ice instead of amber and red.

### Improvements

- The ahead/behind pace line is gone; with min-max off, the Large layout shows the daily budget on its own.
- Small and Medium rows have faint outlines and a divider between providers.
- History stays on your machine in `quota-history.json`.

**Full Changelog**: https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/compare/v0.8.0...v0.9.0

## v0.8.0

### What's new

- **Update notice.** Once a day NeonMon asks GitHub whether a newer release exists and tells you once per version; the tray menu and Settings → Support keep showing it until you update. Nothing is downloaded or installed. Turn it off with Settings → General → Check for updates.
- **Downloads.** Each release now has two zips for 64-bit Windows: a small one that needs the .NET 9 Desktop Runtime, and a standalone one with .NET included.

### Improvements

- The exe's details show AweDev as company and copyright.

**Full Changelog**: https://github.com/AweGuider/NeonMon-Windows-Desktop-Widget/compare/v0.7.1...v0.8.0
