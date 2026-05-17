# Changelog

## 1.2.17 | 17 May 2026

- Fixed a launch crash on the 32-bit GOG build with certain mod combinations

## 1.2.16 | 10 May 2026

- Added a dedicated Gold Nugget Multiplier so you can boost just gold without scaling every other ore

## 1.2.15 | 3 May 2026

- New "Water Output Multiplier" to scale the water you get from wells
- Improved diagnostic logging for bug reports

## 1.2.14 | 27 April 2026

- Fixed Chinese translations not loading
- Added an update notice on the main menu that flags when this mod (or others in this collection) has a newer version on Nexus. Click an entry to open its Nexus page. Toggle off in settings if you'd rather not see it
- Tidied up the settings menu: sections now use the matching "── N. Name ──" style used by my other mods, multipliers are ordered by impact (Resource at the top), and every description has been rewritten to explain what on/off actually does in-game
- Your existing multiplier values carry over automatically — the old section names are rewritten on first launch so nothing resets
- Fixed the "Multiply Sticks" toggle so it finally matches its name: on means sticks get multiplied, off means sticks stay at vanilla drops. Default is now on, so sticks get multiplied out of the box like every other resource
- Resource Multiplier now covers a lot more items: harvested crops (wheat, cabbage, carrot, beet, onion, lentils, pumpkin, hops, hemp, grapes, crop waste, hiccup grass), extra ores (marble plates, faceted diamonds), logs (billets, planks, beams, flitches), miscellaneous drops (peat, salt, water, alcohol, eggs, milk, metal scrap), enemy drops, and common body parts (blood, flesh, fat, skin, bone, skull). Organs and specialised body parts stay vanilla
- New Categories section with nine toggles lets you pick exactly which item groups the Resource Multiplier touches — turn seeds off but crops on, turn body parts on for morgue-heavy runs, etc. Sensible defaults: crops/bugs/ores/logs/enemy drops ON, seeds/misc/body parts OFF
- New Crafting section with a Craft Output Multiplier — one log can now yield ×5 billets at the sawhorse, ×5 planks at the carpenter, etc.
- Progression crafts (station upgrades, quality-tier upgrades, repair, object placement, refugee build desk) are automatically skipped by the craft multiplier so you can't trivialise progression. Tools, weapons, armour and sermon scrolls are skipped too, so one craft still makes one sword. Both exclusions are toggleable
- Every multiplier now accepts values between 0.1 and 50 (was 1–50), so you can *reduce* drops/income as well as increase them — useful if you want a slower, more scarce run
- Reviewed every public bug report and comment on the mod page; the "faith multiplier affects crafting costs" report was not reproducible against the current code (the patched method only runs for sermon income, never for crafting costs), so no fix needed. The "sticks flood inventory when deconstructing" report is resolved by the Multiply Sticks toggle. The "no tech points" reports were caused by the 0-value config state that the value range now forbids
- The first time you load a save with Debug Logging turned on, a short reminder pops up so you don't leave it on by accident — translated in 11 languages
- Much more detailed debug log output (tagged by context like [Drop], [Faith], [TechPoints], [CraftApply]) to make bug reports easier to diagnose

## 1.2.13 | 12 April 2026

- The Advanced section now appears at the top of the settings list instead of the bottom, and its Debug Logging option is always visible (was hidden by default)

## 1.2.12 | 11 April 2026

- Added startup logging to help diagnose mod and game environment issues

## 1.2.11

- Fixed stick drop multiplier setting not being checked correctly
- Mod is now standalone — no longer requires GYK Helper

