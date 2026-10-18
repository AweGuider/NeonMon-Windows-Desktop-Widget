# Gallery

All screenshots are rendered by NeonMon itself (`NeonMon.exe --render-preview <file> <size> --strip <system|quota> --state <Hidden|Peek|Open> --sample`, and `--render-settings <file> <General|System|Quota|Audio|Support>[/<sub-page>] --sample` for the Settings window, e.g. `Quota/Min-max`) at 125% scaling with fixed sample data, so they are reproducible and contain no live system data.

[Back to the README](../README.md)

## System pulse

**Large**

![System pulse, large layout](images/system-large.png)

**Medium**

![System pulse, medium layout](images/system-medium.png)

**Small**

![System pulse, small layout](images/system-small.png)

## Quota pulse

Claude Code is listed first and Codex second. Values count down from 100% remaining. The tick on each bar marks where usage would be at an even pace through the window, the amber ▲ line is a "use it or lose it" hint (at least half the 5-hour quota is left but it resets within an hour), and the chip under Codex shows available reset credits and the earliest expiry.

The Daily quest column is min-max mode: what's left of the weekly limit, divided by the days to the reset, with how far today has got. Small and Medium show today's quest as a lighter slice of the weekly bar.

**Large**

![Quota pulse, large layout](images/quota-large.png)

**Medium**

![Quota pulse, medium layout](images/quota-medium.png)

**Small**

![Quota pulse, small layout](images/quota-small.png)

**Results**

**Results** in the header shows quests this week (filled for done, amber for missed, dashed for today), last week's result and streak, and the last 8 weeks, with the current week as an open bar.

![Quota pulse Results view, large layout](images/quota-results.png)

## Peek

Hovering over a tab shows the headline numbers without opening the panel. Quota pulse shows each provider's 5-hour value, plus the weekly value on an amber chip when the weekly quota is the lower of the two. System pulse shows CPU and GPU load, free space on the system drive, and uptime by default; a drive turns amber when it is nearly full. **Settings → System pulse → Peek** picks the values, including temperatures, memory, and other drives.

![Quota pulse peek](images/quota-peek.png) &nbsp; ![System pulse peek](images/system-peek.png)

## Hidden tabs

The hidden state is a small tab hanging off the screen edge: a dark body with bright bars, inside a light outer ring. The system tab shows CPU load on the inner line and GPU load on the line nearest the screen edge, averaged over 15 seconds; **Settings → System pulse → Hidden tab** picks up to two metrics, or none for a plain bar. The quota tab shows two lines, split into a Claude half and a Codex half with brand-colored circles at the ends: the weekly limit on the line nearest the screen edge, outlined in amber, and the 5-hour limit inside it. **Hidden tab → One line** in the Quota pulse menu switches back to a single line showing whichever limit is tighter.

![System pulse and Quota pulse hidden tabs, docked to the bottom edge](images/hidden-tabs.png)

![Hidden tabs before and after the redesign, on four backgrounds](images/hidden-strips-before-after.png)

## Settings

**Settings…** in any menu opens a window with general options, interaction sounds, support links, and a page for each pulse, split into sub-pages such as Strip, Peek, Min-max, and one per quota provider. The right-click menus can also switch to a dark style there.

![Settings window, Quota pulse Min-max page](images/settings.png)

The General page holds the options that apply to both pulses, including the hold-to-peek hotkey and the daily update check.

![Settings window, General page](images/settings-general.png)
