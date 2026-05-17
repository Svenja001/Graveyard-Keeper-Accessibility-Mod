# Changelog

## 1.9.9 | 17 May 2026

- Fixed a launch crash on the 32-bit GOG build with certain mod combinations

## 1.9.8 | 3 May 2026

- Fixed the HUD staying hidden after Gerry's empty-trunk visit
- Improved diagnostic logging for bug reports

## 1.9.7 | 27 April 2026

- Reduced Gerry's cut a touch at every unlock stage
- Added an option to disable Gerry's cut entirely
- Added a tint colour option for the shipping box
- Fixed Gerry's price line appearing twice on bag items (Farmers Bag, Potion Bag, etc.) in the inventory tooltip
- Fixed Chinese translations not loading
- Added a main-menu notice when a newer version is on Nexus. Toggle off in settings if you don't want it
- Settings menu reorganised. Existing values preserved.
- Fixed Gerry occasionally getting stuck hovering above the shipping box after an interrupted midnight visit
- Gerry no longer blocks building placement on the trunk's tile while hovering during his visit

## 1.9.6 | 15 April 2026

- Fixed newly-built trunks not working as a shipping box

## 1.9.5 | 14 April 2026

- Fixed the HUD and character controls staying gone after an interrupted midnight visit
- Camera no longer keeps following the spot where Gerry was after he's left
- Fixed a regular wooden storage occasionally being treated as the shipping box
- Added a Cinematic Mode toggle in the Gerry settings: turn it off if you'd rather Gerry's midnight visit happen in the background while you keep playing, instead of pausing the game and hiding the HUD

## 1.9.4 | 12 April 2026

- Fixed non-English translations not loading - the mod was showing English regardless of your game language
- Language changes in the game options are now picked up immediately without needing to restart
- The Advanced section now appears at the top of the settings list instead of the bottom, and its Debug Logging option is always visible (was hidden by default)
- Enabling Debug logging now shows a one-time in-game dialog warning you it's on, so you don't forget it's enabled

## 1.9.3 | 11 April 2026

- Translations are now loaded from editable JSON files in the lang folder
- Users can modify or contribute translations by editing the JSON files - do not rename or move them
- Fixed several translation errors across multiple languages
- Main menu now shows "BepInEx Modded" in the version text
- Fixed hotkeys and input features not working for some users
- Improved compatibility across different BepInEx versions

## 1.9.2

- Fixed HUD occasionally not restoring after the midnight sale animation
- Gerry sale animation no longer triggers during NPC conversations or cutscenes
- Trunk sale prices now better match vendor pricing and no longer overpay for certain items
- GYK Helper is no longer required

## 1.7

- - Russian localization corrections (Credits Sonju).
- - Gerry will now actually sell the items if spawning Gerry is disabled.
- - Fixed bug that could cause crafted items to be deleted instead of being dumped in a chest.
- - Fixed total chest value being wrong when adding up the tooltips manually.
- - Removed need to spawn/destroy copy vendors to check pricing each time.

