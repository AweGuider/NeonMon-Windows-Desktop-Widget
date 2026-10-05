# Gallery

All screenshots are rendered by NeonMon itself (`NeonMon.exe --render-preview <file> <size> --strip <system|quota> --state <Hidden|Peek|Open> --sample`) at 125% scaling with fixed sample data, so they are reproducible and contain no live system data.

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

**Large**

![Quota pulse, large layout](images/quota-large.png)

**Medium**

![Quota pulse, medium layout](images/quota-medium.png)

**Small**

![Quota pulse, small layout](images/quota-small.png)

## Peek

Hovering over a tab shows the headline numbers without opening the panel. Quota pulse shows each provider's 5-hour value, plus the weekly value on an amber chip when the weekly quota is the lower of the two.

![Quota pulse peek](images/quota-peek.png) &nbsp; ![System pulse peek](images/system-peek.png)

## Hidden tabs

The hidden state is a small tab hanging off the screen edge: a dark body with a bright bar, inside a light outer ring. The quota tab splits into a Claude segment and a Codex segment, each filled to its remaining quota, with brand-colored caps at the ends.

![Hidden tabs before and after the redesign, on four backgrounds](images/hidden-strips-before-after.png)
