# Changelog

## 2.2.14 | 17 May 2026

- Fixed a launch crash on the 32-bit GOG build with certain mod combinations

## 2.2.13 | 4 May 2026

- Fixed day-change reminders only firing on the Astrologer's day (Sloth) and not on any other day, a regression introduced in 2.2.11 when the wake-up delay was added

## 2.2.12 | 3 May 2026

- Korean translations updated (thanks VeNiKu)
- Improved diagnostic logging for bug reports

## 2.2.11 | 27 April 2026

- Reminders now wait a couple of seconds after you wake up, so the message isn't hidden behind the sleep screen while it fades. Adjust under Wake-Up Delay, or set it to 0 for instant reminders
- Fixed Chinese translations not loading
- Added a main-menu notice when a newer version is on Nexus. Toggle off in settings if you don't want it
- Fixed non-English translations not loading - the mod was showing English regardless of your game language
- Language changes in the game options are now picked up immediately without needing to restart
- Settings menu reorganised. Existing values preserved.
- Improved debug logging for easier bug reports

## 2.2.10 | 11 April 2026

- Translations are now loaded from editable JSON files in the lang folder
- Users can modify or contribute translations by editing the JSON files - do not rename or move them
- Fixed several translation errors across multiple languages
- Main menu now shows "BepInEx Modded" in the version text
- Added option to disable event-specific messages and show only the day name
- Fixed hotkeys and input features not working for some users
- Improved compatibility across different BepInEx versions

## 2.2.9

- Fixed day-change reminders being missed if the player was in a cutscene, dialog, or sleeping when the day changed
- Improved text display for eastern language users
- GYK Helper is no longer required

## 1

- Initial release

