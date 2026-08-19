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
  none is running, the mod falls back to Windows SAPI and still speaks. On the 32-bit GOG build
  it is always SAPI — see *A note for GOG players*.

Everything the mod needs travels inside its ZIP — the Prism speech library is bundled for Windows,
Linux and macOS too, so there is nothing separate to install.

### Do not install BepInEx Configuration Manager

The mod has no settings to configure, and Configuration Manager actively breaks it: opening it
(F1) stops the arrow keys, Escape and all speech from working, because it and the game's own UI
both poll the keyboard at once. If you already have it, simply never press F1.

## Install

There is no installer, and the mod does not look for your game folder — you extract one ZIP into
it yourself. That is the whole install.

### Which download to take

- **`GraveyardKeeperAccessibility_<version>_WithBepInEx.zip`** — **for Steam** (and Epic or Xbox).
  Take this one unless you know you need one of the others. It contains the mod *and* BepInEx, so
  there is nothing else to fetch.
- **`GraveyardKeeperAccessibility_<version>_WithBepInEx_GOG_32bit.zip`** — **for GOG.** The same
  thing, with the 32-bit loader. GOG sells a 32-bit build of the game, which cannot load the
  64-bit loader in the file above; it would start with no mod, no error and no log file.
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

That is everything. Nothing needs to be run first, and no folders need creating by hand.

### Installing (the mod-only ZIP)

If you already have BepInEx, extract `..._ModOnly.zip` into the **`BepInEx` folder inside** your
game folder instead:

```
C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper\BepInEx\
```

It contains a `plugins` folder, so the mod lands in
`BepInEx\plugins\GraveyardKeeperAccessibility\`.

Either way, do not move the individual files around afterwards — the speech library and the `lang`
folder have to stay beside the DLL.

### A note for GOG players

GOG sells a 32-bit build of the game, and two things follow from that.

**Speech comes from the Windows SAPI voice, not from your screen reader.** Prism, the library the
mod uses to talk to NVDA, JAWS and braille displays, has never been built for 32-bit Windows, so
there is nothing to install and no older version that would help. Everything is still spoken —
just in the Windows voice, and without braille.

**It is tested and working, but less thoroughly than on Steam.** The mod is built against the Steam
version of the game, and GOG's is older and differs in places. If something behaves oddly only on
GOG, a report is very welcome.

### Start the game

The mod announces itself once the game has finished loading. From there the title screen, the save
slots and everything after them are read aloud.

## Keys

`KEYBINDINGS.md` ships next to the mod inside `BepInEx\plugins\GraveyardKeeperAccessibility\` and
lists every key the mod adds, grouped by where it works.

The game's own keys can be changed in the pause menu (Escape) under Controls, which is fully
keyboard-navigable. The mod's own keys are fixed for now, since the GyK Configurationmanager is not yet accessible yet and there is no configuration file for it yet.

## Languages

English and German translations are complete. Spanish, French, Italian and
Russian files exist but are near-empty and fall back to English.

## Save compatibility

The mod only reads the game and speaks; it writes nothing of its own to your save. It is safe to
add or remove at any point in a playthrough.

## Known limitations

- The game's opening intro is not narrated — its subtitles are drawn by a separate system that is
  not hooked yet.
- Linux and macOS are untested. Windows is verified. Playing through Proton works, but Prism
  cannot reach a Linux screen reader such as Orca from inside Proton; those DLLs have to be
  installed by hand, as a mod cannot do it in that case.
- BepInEx Configuration Manager is not accessible (see above).
- manual fishing is not accessible yet, but its possible to automatically fish.

`CHANGELOG.md` ships with the mod and has the full list.

## Something went wrong?

`BepInEx\LogOutput.log` inside the game folder records what the mod did. It is a plain text file,
and the lines beginning with `Graveyard Keeper Accessibility` are this mod's. That log is the
first thing worth looking at, and the most useful thing to attach to a bug report.

## Licence & credits

Licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE) (shipped with the
mod as `LICENSE.txt`) for the full text.
In short: you are free to use, study, modify and redistribute the source under the same licence;
any distributed fork must also be GPL v3.

Speech is provided by [Prism](https://github.com/ethindp/prism), used under the **Mozilla Public
License 2.0**; its licence and notice travel with the mod as `prism-LICENSE.txt` and
`prism-NOTICE.txt`.

The bundled ZIP also contains [BepInEx](https://github.com/BepInEx/BepInEx) and its
[Doorstop](https://github.com/NeighTools/UnityDoorstop) loader, both under the **GNU Lesser General
Public License 2.1**, redistributed unmodified. Their licence texts ship as `BepInEx-LICENSE.txt`
and `Doorstop-LICENSE.txt` in the game folder. The exact release used is
`BepInEx_win_x64_5.4.23.5.zip` from BepInEx's own releases page.

This repository is a fork of [p1xel8ted's Graveyard Keeper mod collection](https://github.com/p1xel8ted/Graveyard-Keeper-Mods),
whose build tooling it still uses.
