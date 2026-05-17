# Changelog

## 1.4.13 | 17 May 2026

- Fixed a launch crash on the 32-bit GOG build with certain mod combinations

## 1.4.12 | 3 May 2026

- New off-by-default toggles to speed up the home water pump, the zombie brewery, the zombie winemaking station, and the zombie crafting table — each with its own multiplier
- Improved diagnostic logging for bug reports

## 1.4.11 | 27 April 2026

- Fixed tools wearing out faster than vanilla when gathering clay, stone, wood, and ore
- Fixed Chinese translations not loading
- Repair crafts (soul machines, broken buildings, etc.) no longer use the Craft Speed Multiplier — at high multipliers the energy cost climbed faster than your bar could refill, making some repairs impossible to start
- Settings pane redone with clearer section headings and fuller descriptions for every option
- Debug log output is less spammy — one line per craft session instead of one per frame
- Enabling Debug Logging now shows a one-time in-game reminder so you don't forget it's on, with translations across all 11 supported languages
- Added an update notice on the main menu that flags when this mod (or others in this collection) has a newer version on Nexus. Click an entry to open its Nexus page. Toggle off in settings if you'd rather not see it
- Configuration Manager now indents each speed multiplier under its parent toggle, matching the rest of this mod collection
- Clarified the Craft Speed Multiplier description so it's obvious gardens, composting, and zombie stations have their own dedicated toggles and aren't covered by the main multiplier
- Clarified what the Speed Up Zombie Mines toggle actually covers — the stone yard, marble deposit, and iron mine. The old description mentioned the zombie mill, but that's the sawmill zone and it's covered by the Speed Up Zombie Sawmill toggle instead

## 1.4.10 | 11 April 2026

- Added startup logging to help diagnose mod and game environment issues

## 1.4.9

- Fixed build/destroy speed multiplier affecting non-player interactions like BSS machines and blockage clearing
- Mod is now standalone — no longer requires GYK Helper

