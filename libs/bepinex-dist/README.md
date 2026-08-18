# BepInEx (bundled loader)

The unmodified BepInEx 5 release, vendored here so the mod can ship a **one-extract install**:
a blind player should not have to assemble a loader and a mod from two downloads, each with its
own way of failing silently. See `src/GraveyardKeeperAccessibility.csproj`, which folds this
folder into `..._WithBepInEx.zip`.

| | |
|---|---|
| Upstream | <https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5> |
| Asset | `BepInEx_win_x64_5.4.23.5.zip` |
| SHA-256 | `82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4` |
| Downloaded | 2026-08-16 |
| Licence | LGPL-2.1 (BepInEx and the Doorstop `winhttp.dll` alike) |

**Take `win_x64`, never `win_x86` or any 6.x pre-release.** Graveyard Keeper is a 64-bit process,
so the 32-bit Doorstop cannot inject into it and the game simply starts unmodded with no error at
all. BepInEx 6 moved the plugin base type and a 5.x-compiled mod will not load.

## Contents

Extracted verbatim from that asset. The layout is already rooted at the **game folder**, which is
why the bundled ZIP can be extracted straight into it:

- `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` — the Doorstop loader that starts
  BepInEx when the game launches.
- `BepInEx/core/` — BepInEx itself plus Harmony, Cecil and MonoMod.
- `changelog.txt` — upstream's.

Nothing here is patched. `libs/bepinex/` is a *different* thing: reference assemblies to compile
against, not files to ship.

## Licence files are added by us

The upstream ZIP ships **no licence text**, but LGPL-2.1 requires it to accompany a
redistribution, so `BepInEx-LICENSE.txt` and `Doorstop-LICENSE.txt` were fetched separately from
the two projects' repositories and are shipped alongside the binaries.

## Updating

Replace the extracted files, refresh the version and hash above, and re-check the two traps. Also
update `libs/bepinex/` if the compile-time assemblies moved version, so the mod is not built
against a different BepInEx than it ships with.
