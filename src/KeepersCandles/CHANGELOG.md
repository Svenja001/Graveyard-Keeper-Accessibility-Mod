# Changelog

## 0.2.0 | 20 June 2026

- Fixed candelabras that couldn't be extinguished after saving and reloading

## 0.1.11 | 23 May 2026

- Fixed incense burners that couldn't be extinguished after saving and reloading

## 0.1.10 | 19 May 2026

- Fixed a launch crash on the 32-bit GOG build with certain mod combinations
- Mod settings menu (F1) labels now follow your in-game language

## 0.1.9 | 3 May 2026

- Improved diagnostic logging for bug reports

## 0.1.8 | 27 April 2026

- Incense burners now burn forever and auto-light on placement, just like candelabras
- Added a separate keybind for extinguishing the nearest lit incense and recovering the unused incense. Unbound by default.
- The candle keybind entries were renamed to make room for the incense pair. Existing custom bindings are migrated.
- Fixed Chinese translations not loading
- Added a main-menu notice when a newer version is on Nexus. Toggle off in settings if you don't want it
- The Advanced section's Debug Logging option is now always visible (was hidden by default)
- Settings menu reorganised. Existing values preserved.
- Existing settings are migrated automatically - your keybinds, distance, and column toggle are preserved
- Translations are now loaded from JSON files in the mod's `lang/` folder, so they're easier to fix up or contribute to
- Improved diagnostic logging for bug reports
- A one-time reminder now pops up in-game when Debug Logging is left on, so it doesn't stay on forever by accident

## 0.1.7 | 11 April 2026

- Fixed hotkeys and input features not working for some users
- Improved compatibility across different BepInEx versions

## 0.1.6

- GYK Helper is no longer required

## 0.1.4

- Lang corrections

## 0.1.3

- Added option to toggle the visibility of the church columns
- Adjustments to prevent candles from burning still

## 0.1.2

- Changed distance setting to a slider, increments of 0.25
- Implemented arrow pointer, pointing to the nearest lit candle

## 0.1.1

- Fixed not being able to remove candles after saving and re-loading
- Fixed not being able to craft candles

## 0.1.0

- Initial release

