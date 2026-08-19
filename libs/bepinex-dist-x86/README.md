# BepInEx, 32-bit Doorstop (for the GOG build)

**The GOG build of Graveyard Keeper is a 32-bit process.** A 32-bit process cannot load the 64-bit
Doorstop `winhttp.dll` in `../bepinex-dist/`; Windows falls through to the real system
`winhttp.dll`, and the game starts unmodded with **no error and no `LogOutput.log` at all** —
BepInEx never runs. That is what the first release shipped to every GOG player.

| | |
|---|---|
| Upstream | <https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5> |
| Asset | `BepInEx_win_x86_5.4.23.5.zip` |
| SHA-256 (zip) | `37651c79e40d6f909572a4f461ac25350bb3ef8fe7fbd29f1aa8791a33b84c82` |
| Downloaded | 2026-08-19 |
| Licence | LGPL-2.1, same as the x64 asset — see `../bepinex-dist/Doorstop-LICENSE.txt` |

## Only one file lives here, on purpose

Every file in the x86 asset was hashed against the extracted x64 asset in `../bepinex-dist/`:
**`winhttp.dll` is the only one that differs** (22016 bytes vs 26112). `.doorstop_version`,
`doorstop_config.ini`, `changelog.txt` and all eighteen files of `BepInEx/core/` are
byte-identical, because BepInEx's core is managed AnyCPU.

So `ZipWithBepInEx` in `src/GraveyardKeeperAccessibility/GraveyardKeeperAccessibility.csproj`
stages the bundle **once**, zips it as `..._WithBepInEx.zip`, copies this one file over
`winhttp.dll`, and zips it again as `..._WithBepInEx_GOG_32bit.zip`. The two archives cannot drift
apart in anything else, which is the point of doing it that way rather than staging twice.

The licence texts and the rest of the loader are not duplicated here; they ship from
`../bepinex-dist/` into both archives.

## Verifying a replacement

If you re-download this file, confirm it is actually 32-bit before committing it — the failure it
guards against is silent:

```sh
python -c "import struct;d=open('winhttp.dll','rb').read(0x400);o=struct.unpack_from('<I',d,0x3c)[0];print(hex(struct.unpack_from('<H',d,o+4)[0]))"
# 0x14c = 32-bit (correct here).  0x8664 = 64-bit (that is ../bepinex-dist/winhttp.dll).
```

## What this does *not* fix

- **Speech falls back to SAPI on GOG.** Prism has never published a 32-bit Windows build — checked
  across all 54 releases, v0.1.0 to v0.17.3, where the only Windows assets are
  `prism-windows-x64.zip` and `prism-windows-arm64.zip`. So no NVDA, JAWS or braille on the GOG
  build; `PrismWrapper.Init` detects the 32-bit process and says so in the log.
- **The mod is compiled against Steam's `Assembly-CSharp`.** `$(Storefront)` exists upstream
  because GOG's differs, and `libs/gog/` is not present on this machine, so `-p:Storefront=gog`
  has never been run. Nothing about GOG compatibility beyond the loader is verified.
