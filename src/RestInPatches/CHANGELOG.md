# Changelog

## 0.1.5 | 20 June 2026

- Added an option to turn off player movement smoothing

## 0.1.4 | 25 May 2026

- Fixed workers and other NPCs freezing after the game has been left running in the background

## 0.1.3 | 23 May 2026

- Removed the dropped-item walk-through setting; it now lives in the Get Outta Ma Way mod

## 0.1.2 | 19 May 2026

- The +/- arrows are now also restored on items already sitting in your craft queue, not just the recipe list
- Mod settings menu (F1) labels now follow your in-game language
- Settings menu reorganised. Existing values preserved.
- Reduced per-frame work for smoother performance
- Fixed refugees no longer spawning at the camp on saves where the tent counts had drifted (logged as "Wrong vacant places count in tent" / "No refugee for spawn"). The counts self-repair on load now
- Improved diagnostic logging for bug reports

## 0.1.1 | 19 April 2026

- Fixes the mouse cursor sometimes vanishing and refusing to come back until you sleep or restart the game
- Fixes timber and ore stockpiles vanishing from the world whenever another mod raises their capacity above the original 10. The pile now stays visible as "full" for any amount above the usual limit, and snaps back to showing the exact count as soon as you drop below it
- Added a main-menu notice when a newer version is on Nexus. Toggle off in settings if you don't want it
- Also restores the up/down arrow glyph on the little expand/collapse toggle inside multiquality crafting recipes (same missing-asset cause as the +/- arrows)

## 0.1.0 | 12 April 2026

- First release
- Restores the small left/right arrow icons that disappeared from the craft window's quantity buttons after a recent game update. The buttons still worked - they just had no arrow glyph on them. This fix brings the arrows back
- Smooths out player movement so the character no longer snaps to grid lines
- Adds a tunable cap on the number of footprints kept on the map, and makes them fade smoothly
- Adds options to keep the game running when its window isn't focused, and to mute the audio while unfocused
- Suppresses harmless exceptions on edge-case save data
