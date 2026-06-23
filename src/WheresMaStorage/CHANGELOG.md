# Changelog

## 2.2.0 | 20 June 2026

- Apiary and cellar build desks now see items stored elsewhere instead of showing nothing
- Zombie work stations now pull ingredients from shared storage by default. Toggle off under Inventory to keep them limited to their own zone
- The "Exclude Zombie Mill" inventory option now actually keeps that area out of shared storage

## 2.1.18 | 23 May 2026

- Dead bodies are no longer swept to your dump site on save load

## 2.1.17 | 19 May 2026

- Fixed a launch crash on the 32-bit GOG build with certain mod combinations
- Cellar crates no longer get swept over to your house by the loose-loot mover
- Stack sizes now stay at your configured values when Oyasumi Infinite Stack is also installed
- Added a main-menu notice when a known conflicting mod is loaded alongside this one
- Mod settings menu (F1) labels now follow your in-game language

## 2.1.16 | 3 May 2026

- Fixed building options being greyed out at the Refugee Camp build desk
- Stopped the BepInEx log filling up with repeated inventory-reload notices on some saves
- Fixed slowdown when zombies auto-cut stone or marble at the quarry
- Crafts now pull ingredients from the closest storage first instead of by zone discovery order. Toggle off under Inventory if you preferred the previous order
- Improved diagnostic logging for bug reports

## 2.1.15 | 27 April 2026

- Fixed a crash during the game-load loot sweep that could leave the mod in a broken state until the next restart

## 2.1.14 | 27 April 2026

- Fixed the apiary build desk showing no shared inventory
- Fixed a duplicate "Player (7/7)" tool belt widget appearing next to your bag
- New "Hide Bag Widgets" option hides carried bags from the inventory panel. Right-click the bag icon to open it
- Fixed Chinese translations not loading
- Added a main-menu notice when a newer version is on Nexus. Toggle off in settings if you don't want it
- New option to pile load-time loot next to your house instead of pulling it straight into your pockets. Also gathers large items the vacuum previously skipped
- New "Near-House Dump Zone Radius" slider (default 8 tiles)
- Vendor windows open much faster when "Show Only Personal Inventory" is on
- Vendor windows now respect the Hide Stockpile/Tavern/Soul/Warehouse Shop Widgets toggles
- Fixed the "Inventory Dimming" toggle being inverted
- Items outside your personal inventory are no longer dimmed at vendors when the full shared list is showing
- New "Player Loot Magnet Range" slider, 1.8 to 20 tiles (vanilla is 1.8)
- Bag widgets now lay out in a 5-column grid instead of 3
- Quest items and story-critical drops are no longer scooped up by the load-time loot sweep
- Additional Inventory Space slider split into separate player and container sliders. Existing value is migrated to both
- Both sliders accept 0 as the minimum
- Changing either slider now resizes existing containers in the world, not just newly built ones
- Shrinking a slider below current item count now shows a Yes/No prompt before dropping anything
- Items previously hidden by the shrink bug reappear next time you open the container
- Configuration Manager closes automatically when the shrink prompt appears
- Turning off Modify Inventory Size reliably keeps your player inventory at 20 slots
- Turning Modify Inventory Size on reliably keeps your extra slots
- Slider and toggle changes apply straight away
- Fixed quarry and zombie mill containers vanishing from their own zone after crafting elsewhere with Exclude options on
- Turning off "Don't Show Empty Rows In Inventory" now shows empty rows in your personal inventory too
- Dragging Configuration Manager sliders no longer hitches the game
- Settings menu reorganised. Existing values preserved.
- BepInEx log now records inventory check durations

## 2.1.13 | 12 April 2026

- Fixed the +20 inventory slots option not being respected - disabling it now correctly keeps your player inventory at 20 slots, even after loading a save
- If you had previously enabled +20 slots, disabling the option and loading your save now properly restores the standard 20-slot inventory

## 2.1.12 | 12 April 2026

- Fixed merchants showing empty trade tabs after installing the mod
- Fixed the "Exclude Zombie Mill From Shared Inventory" option not actually hiding zombie mill items from crafting elsewhere
- Fixed sin shards stacking to 999 on soul containers, which was breaking gratitude point crafting
- Fixed non-English translations not loading - the mod was showing English regardless of your game language
- Language changes in the game options are now picked up immediately without needing to restart
- The Advanced section's Debug Logging option is now always visible (was hidden by default)
- Enabling Debug logging now shows a one-time in-game dialog warning you it's on, so you don't forget it's enabled

## 2.1.11 | 11 April 2026

- Translations are now loaded from editable JSON files in the lang folder
- Users can modify or contribute translations by editing the JSON files - do not rename or move them
- Fixed several translation errors across multiple languages
- Main menu now shows "BepInEx Modded" in the version text
- Improved compatibility across different BepInEx versions

## 2.1.10

- Fixed game freezing when cancelling the exit-to-menu dialog
- Fixed organ duplication when using the BSS organ enhancer with shared inventory
- Newly built or destroyed storage containers now refresh the shared inventory automatically
- Quarry crafting stations can now access shared inventory from other zones when Exclude Quarry is enabled
- GYK Helper is no longer required

## 2.1.7

- Quarry is now totally independent when Exclude Quarry enabled (as is the mill etc.). If you want to craft something in those zones, you will need to have the materials on hand or in storage within those zones.

## 2.1.6

- Fix for not being able to see quarry inventories when crafting from the quarry zone with Exclude Quarry enabled
- Fix for apple trees and bushes gaining access to shared inventories (just caused unnecessary overhead)

## 2.1.5

- Allow/disallow zombies access to shared inventory (means they can only access storage in the same zone as them - game default).
- Stack sizes and inventory sizes can be adjusted without restart.
- Fixed player inventory not increasing

## 2.1.4

- Allow/disallow zombies access to shared inventory (means they can only access storage in the same zone as them - game default).
- Stack sizes and inventory sizes can be adjusted without restart.
- Fixed player inventory not increasing

## 2.0.2

- Refugee happiness should be correct on the refugee build desk.
- Stockpiles should be visible correctly.

## 2.0

- Russian localization corrections (Credits Sonju).
- Fixed bug that would cause grave items to be stackable despite having the option turned off.
- Rewrote multi-inventory stuff (core of the mod), should notice an FPS gain compared to previous release.
- Potential fix for NPC inventories (when found in the "wilderness") appearing in player inventory.
- Potential fix for toolbelt shenanigans.

