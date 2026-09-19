# Graveyard Keeper Accessibility

A BepInEx mod that makes [Graveyard Keeper](https://store.steampowered.com/app/599140/Graveyard_Keeper/)
playable for blind players. Menus, dialogue, the world, crafting, the graveyard, the dungeon and
the DLC content are all narrated through a screen reader.

## Requirements

- **Graveyard Keeper** on Windows (Steam, GOG, Epic or Xbox). Steam's version is 64-bit and GOG's
  is 32-bit, so there is a download for each — see *Which download to take* below.
- **Nothing else.** BepInEx, the loader that makes mods run at all, is included in the download —
  see below if you already have it.
- **A screen reader is optional.** NVDA, JAWS, Orca, VoiceOver and others are driven directly; if
  none is running, the mod falls back to Windows SAPI and still speaks.

Everything the mod needs travels inside its ZIP — the Prism speech library is bundled for Windows,
Linux and macOS, and Tolk for the 32-bit GOG build, so there is nothing separate to install.

### Do not install BepInEx Configuration Manager

The mod has no settings to configure, and Configuration Manager actively breaks it: opening it
(F1) stops the arrow keys, Escape and all speech from working, because it and the game's own UI
both poll the keyboard at once. If you already have it, simply never press F1.

## Install

There is no installer, and the mod does not look for your game folder — you extract one ZIP into
it yourself.

### Which download to take

- **`GraveyardKeeperAccessibility_<version>_WithBepInEx.zip`** — **for Steam** (and Epic or Xbox).
  Take this one unless you know you need one of the others. It contains the mod *and* BepInEx, so
  there is nothing else to fetch.
- **`GraveyardKeeperAccessibility_<version>_WithBepInEx_GOG_32bit.zip`** — **for GOG.** The same
  thing, with the 32-bit loader and the 32-bit speech library. GOG sells a 32-bit build of the game,
  which cannot load the 64-bit loader in the file above; it would start with no mod, no error and no
  log file.
- **`GraveyardKeeperAccessibility_<version>_ModOnly.zip`** — the mod on its own, without BepInEx,
  and it does not matter which store your game came from. **If you already run other Graveyard
  Keeper mods, use this one**: the bundled ZIPs would overwrite your loader with version 5.4.23.5
  and could disturb mods that expect a different one.

If you are not sure which build you have: start the game, open Task Manager, and find
`Graveyard Keeper` under *Processes*. If the name is followed by *(32 bit)*, take the GOG
download; otherwise take the first one.

### Finding your game folder

The folder you want is the one that contains `Graveyard Keeper.exe` and a folder called
`Graveyard Keeper_Data`. Where that is depends on where you bought the game:

- **Steam** — `C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper`
  Steam libraries can live on any drive. In the Steam client: right-click the game → *Manage* →
  *Browse local files*.
- **GOG** — `C:\Program Files (x86)\GOG Galaxy\Games\Graveyard Keeper`
  or `C:\GOG Games\Graveyard Keeper` if you used an offline installer.
- **Epic** — `C:\Program Files\Epic Games\GraveyardKeeper` (no space in that folder name).
- **Xbox / Microsoft Store** — `C:\XboxGames\Graveyard Keeper\Content`.
  Only this layout works. If the game sits inside `WindowsApps` instead, Windows locks the folder
  down and BepInEx cannot be installed there at all.

### Installing (the bundled ZIP)

Extract `..._WithBepInEx.zip` — or `..._WithBepInEx_GOG_32bit.zip` if your game came from GOG —
**into the game folder itself**, the one holding `Graveyard Keeper.exe`. For example:

```
C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper\
C:\GOG Games\Graveyard Keeper\
C:\XboxGames\Graveyard Keeper\Content\
```

If Windows asks whether to merge folders, say yes. When it is done, `winhttp.dll` sits next to
`Graveyard Keeper.exe`, and the mod is in `BepInEx\plugins\GraveyardKeeperAccessibility\`.

Four documents land next to `Graveyard Keeper.exe` as well —
`Accessibility-Mod-README.md` (this file), `Accessibility-Mod-KEYBINDINGS.md`,
`Accessibility-Mod-CHANGELOG.md` and `Accessibility-Mod-LICENSE.txt`. They are there so the keys
and the instructions are in the folder you are already standing in, not buried inside the mod.

### Installing (the mod-only ZIP)

If you already have BepInEx, extract `..._ModOnly.zip` into the **`BepInEx` folder inside** your
game folder instead:

```
C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper\BepInEx\
```

It contains a `plugins` folder, so the mod lands in
`BepInEx\plugins\GraveyardKeeperAccessibility\`, and the four `Accessibility-Mod-*` documents land
directly in `BepInEx\` where you can see them.

Either way, do not move the individual files around afterwards — the speech library and the `lang`
folder have to stay beside the DLL.

### A note for GOG players

GOG sells a 32-bit build of the game, and two things follow from that.

**Your screen reader is used, through a different library.** Prism, which the mod speaks through
everywhere else, has never been built for 32-bit Windows. The GOG download therefore uses Tolk
instead, which is bundled with it and needs nothing installed. NVDA, JAWS and ZoomText
are driven directly, and braille works on the readers that support it. If no screen reader is
running, speech falls back to the Windows SAPI voice exactly as it does on Steam.

**It works.** GOG players have confirmed the mod running on their installs. It is built against
the Steam version of the game and GOG's is older, so if something ever behaves oddly only on
GOG, a report is still very welcome.

### Start the game

The mod announces itself once the game has finished loading. From there the title screen, the save
slots and everything after them are read aloud.

## Keys

`Accessibility-Mod-KEYBINDINGS.md` lists every key the mod adds, grouped by where it works. It sits
in the folder you extracted the ZIP into, right beside the other three documents — you do not have
to go looking for it. (A second copy also travels inside
`BepInEx\plugins\GraveyardKeeperAccessibility\`, so it survives a Vortex install.)

The game's own keys can be changed in the pause menu (Escape) under Controls, which is fully
keyboard-navigable. The mod's own keys are fixed for now, since the GyK Configurationmanager is not yet accessible yet and there is no configuration file for it yet.

## Languages

English and German translations are complete. Spanish, French, Italian and
Russian files exist but are near-empty and fall back to English.

## Save compatibility

The mod only reads the game and speaks; it writes nothing of its own to your save. It is safe to
add or remove at any point in a playthrough.

## Known limitations

- Linux and macOS are untested. Windows is verified. Playing through Proton works, but Prism
  cannot reach a Linux screen reader such as Orca from inside Proton; those DLLs have to be
  installed by hand, as a mod cannot do it in that case.
- BepInEx Configuration Manager is not accessible (see above).
- Manual fishing is now mostly accessible. The only thing not yet confirmed is if every fish can be caught as they have different requirements.
- Auto-walk is still a bit buggy, it can still slip through where it should not. Sometimes it also claims to not find a way further, but you can walk around the obstacle manually and then auto-walk the rest.
- The turn-by-turn guidance for manual walking is in, but can still behave oddly at times.

## Something went wrong?

`BepInEx\LogOutput.log` inside the game folder records what the mod did. It is a plain text file,
and the lines beginning with `Graveyard Keeper Accessibility` are this mod's. That log is the
first thing worth looking at, and the most useful thing to attach to a bug report.

Bug reports, questions and suggestions are welcome through any of these:

- **GitHub** — [open an issue](https://github.com/Svenja001/Graveyard-Keeper-Accessibility-Mod/issues)
- **Mastodon** — [@svenja@mstdn.games](https://mstdn.games/@svenja)
- **Discord** — `@svenjadev`
- **E-mail** — [stream@svenja-blog.de](mailto:stream@svenja-blog.de)

## Licence & credits

Licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE) (shipped with the
mod as `Accessibility-Mod-LICENSE.txt`) for the full text.
In short: you are free to use, study, modify and redistribute the source under the same licence;
any distributed fork must also be GPL v3.

Speech goes through one of two screen-reader libraries, picked by the bitness of the game process
(see [A note for GOG players](#a-note-for-gog-players)). Both are redistributed unmodified, and
each one's licence text travels with the download that contains it.

[**Prism**](https://github.com/ethindp/prism) — used on the 64-bit builds (Steam, Epic, MS Store),
under the **Mozilla Public License 2.0**. Its licence and notice ship as `prism-LICENSE.txt` and
`prism-NOTICE.txt`. In `..._WithBepInEx.zip` and `..._ModOnly.zip`.

[**Tolk**](https://github.com/dkager/tolk) — used on the 32-bit GOG build, where Prism has no
binary, under the **GNU Lesser General Public License 3.0**. It ships as `Tolk.dll` with its licence
as `tolk-LICENSE.txt`. In `..._WithBepInEx_GOG_32bit.zip` and `..._ModOnly.zip`.

Tolk brings one dependency with it: **`nvdaControllerClient32.dll`**, NVDA's client API, under the
**GNU Lesser General Public License 2.1**, verbatim from Tolk's own repository. Its licence ships as
`nvdaControllerClient-LICENSE.txt`.

`Tolk.dll` is not an upstream binary — the project publishes none — but it is built from unmodified
upstream source, and LGPL-3.0 asks that you be able to rebuild or replace it. The exact commit, the
build command and how to verify the result are written down in
[libs/native/tolk/README.md](libs/native/tolk/README.md); [libs/native/prism/README.md](libs/native/prism/README.md)
does the same for Prism.

The bundled ZIPs also contain [BepInEx](https://github.com/BepInEx/BepInEx) and its
[Doorstop](https://github.com/NeighTools/UnityDoorstop) loader, both under the **GNU Lesser General
Public License 2.1**, redistributed unmodified. Their licence texts ship as `BepInEx-LICENSE.txt`
and `Doorstop-LICENSE.txt` in the game folder. Both come from release
[v5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5): asset
`BepInEx_win_x64_5.4.23.5.zip` for `..._WithBepInEx.zip`, and `BepInEx_win_x86_5.4.23.5.zip` for
`..._WithBepInEx_GOG_32bit.zip`.

This repository is a fork of [p1xel8ted's Graveyard Keeper mod collection](https://github.com/p1xel8ted/Graveyard-Keeper-Mods),
whose build tooling it still uses.
