# Changelog

## 0.1.2 | 30 April 2026

- The +/- arrows are now also restored on items already sitting in your craft queue, not just the recipe list.
- Config section headers restyled to the "── Name ──" look used by the rest of the collection. Existing settings carry over automatically.
- Reduced unnecessary per-frame work across several systems (HUD labels, ground item highlighting, world-object processing, and a few others).
- Repairs corrupted refugee-camp tent occupancy on save load. Saves where a tent's "busy" count had drifted above its capacity (a vanilla bug visible as "Wrong vacant places count in tent" / "No refugee for spawn" errors) are now reset to match the actual refugees living in the camp, so spawning resumes working.
- Log messages now include timestamps for easier bug reporting.
- Mod settings are now logged at startup for easier bug reporting.

## 0.1.1 | 19 April 2026

- Fixes the mouse cursor sometimes vanishing and refusing to come back until you sleep or restart the game. Mostly reported on Linux.
- Fixes timber and ore stockpiles vanishing from the world whenever another mod raises their capacity above the original 10. The pile now stays visible as "full" for any amount above the usual limit, and snaps back to showing the exact count as soon as you drop below it.
- Added an update notice on the main menu that flags when this mod (or others in this collection) has a newer version on Nexus. Click an entry to open its Nexus page. Toggle off in settings if you'd rather not see it
- Also restores the up/down arrow glyph on the little expand/collapse toggle inside multiquality crafting recipes (same missing-asset cause as the +/- arrows).

## 0.1.0 | 12 April 2026

- First release.
- Restores the small left/right arrow icons that disappeared from the craft window's quantity buttons after a recent game update. The buttons still worked — they just had no arrow glyph on them. This fix brings the arrows back.
- Smooths out player movement by enabling physics interpolation so the character no longer snaps to grid lines.
- Adds a tunable cap on the number of footprints kept on the map, and fixes a vanilla bug where footprints pop out of existence instead of fading smoothly.
- Adds options to keep the game running when its window isn't focused, and to mute the audio while unfocused.
- Silently swallows a handful of harmless exceptions in the item tooltip, world-object interaction, tree prefab instantiation, and HUD update code that could otherwise nag your BepInEx log or cause a crash on edge-case save data.
