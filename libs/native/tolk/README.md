# Tolk (bundled native library — 32-bit only)

Tolk is the screen-reader abstraction the mod speaks through **on the 32-bit GOG build of the
game**. Everywhere else the mod uses Prism (`libs/native/prism/`), which is better in every way
that matters here — but Prism has never published a 32-bit Windows build, and a 32-bit process
cannot load a 64-bit DLL. Without Tolk, GOG players fall through to the SAPI voice in
`ScreenReader.InitSapi`: everything is still spoken, but in the Windows voice, at the Windows
voice's speed, with no braille and no way to use their own screen reader's settings.

| | |
|---|---|
| Upstream | <https://github.com/dkager/tolk> |
| Commit | `e5149f0cb6ef9b941673017e0e7b7c409e485fbe` (2024-06-08, the tip of `master`) |
| Version | 1.0 (`src/TolkVersion.h`) |
| License | LGPL-3.0 (`LICENSE.txt`) |

## Only one of the two is ever loaded

`ScreenReader.Init` picks by process bitness and there is no overlap:

| Process | Library | Decided in |
|---|---|---|
| 64-bit (Steam, Epic, MS Store) | Prism | `PrismWrapper.Init` — `IntPtr.Size == 4` early-out |
| 32-bit (GOG) | Tolk | `TolkWrapper.Init` — `IntPtr.Size != 4` early-out |

Both guards are unconditional, so the two can never both be initialised and cannot end up talking
over each other at NVDA. Only the losing one's DLL is even present in a usable form: `prism.dll`
is x86-64 and `Tolk.dll` is x86, so the wrong one physically fails to load anyway.

Because the choice is made by architecture and not at runtime, the **bundled release ZIPs carry only
the library their loader can use**: `_WithBepInEx.zip` ships Prism, `_WithBepInEx_GOG_32bit.zip`
ships Tolk, and `ZipWithBepInEx` in the csproj swaps them in the staging directory alongside the
32-bit `winhttp.dll`. The storefront-agnostic `_ModOnly.zip` still ships both, because nothing in it
says which store the game came from.

Note that Prism's own release zips contain a file called `tolk.dll` — a Tolk-*compatible shim*
built on top of Prism, for apps already written against the Tolk API. That is a different thing
from this, it is 64-bit, and it is **not** bundled (see `libs/native/prism/README.md`). Windows
file names are case-insensitive, so the two must never land in the same folder.

## What ships, and what does not

| File | What it is | License |
|---|---|---|
| `Tolk.dll` | The library itself, x86, built here from the commit above | LGPL-3.0 |
| `nvdaControllerClient32.dll` | NVDA's client API, verbatim from the upstream repo's `libs/x86/` | LGPL-2.1 (`LICENSE-NVDA.txt`) |

That covers **NVDA, JAWS, Window-Eyes, ZoomText and SAPI**: only NVDA needs a client DLL, the
other three drivers talk COM and need nothing on disk.

Deliberately **not** bundled are the other two DLLs in upstream's `libs/x86/`:

- `SAAPI32.dll` — System Access (Serotek)
- `dolapi32.dll` — SuperNova / Hal / Guide (Dolphin)

Both are proprietary vendor binaries. Upstream redistributes them but ships a licence text only
for NVDA's, and neither vendor's redistribution terms were checked here, so they stay out of a
GPL-3.0 release. `TolkWrapper.PreloadClientLibraries` loads them **if they are present**, so a
player who has them can simply drop them next to `GraveyardKeeperAccessibility.dll` and those two
screen readers start working with no code change. Both are rare next to NVDA and JAWS.

## How it is loaded

Same trap as Prism, plus one more. `Tolk.dll` is loaded by **full path** from the folder next to
`GraveyardKeeperAccessibility.dll`, because Windows searches the game's executable directory and
never the BepInEx plugin folder.

The extra step is that Tolk loads its *own* dependency by bare name: the NVDA driver's constructor
runs `LoadLibrary(L"nvdaControllerClient32.dll")` during `Tolk_Load()`, and upstream's README says
it "expects these DLLs to be found either in the current working directory or somewhere in the
`PATH`" — the plugin folder is neither. So `TolkWrapper` pre-loads the client DLLs by full path
first; the Windows loader then matches Tolk's bare-name request against the already-resident module
and hands back the same handle. That is why `SetDllDirectory` is not called: it would change the
search path for the whole process, including the game's own and every other mod's.

## Rebuilding it

Nothing about `Tolk.dll` was modified — it is the upstream source, built for x86. Under LGPL-3.0
that is what lets us ship it in a GPL-3.0 mod, and the point of writing the command down is that
anyone can reproduce or replace the binary:

```bat
git clone https://github.com/dkager/tolk.git
cd tolk\src
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x86
nmake OUTDIR=<output folder> ^
  CFLAGS="/nologo /O2 /EHsc /LD /Gw /W4 /WL /D_EXPORTING /DUNICODE" ^
  SOURCES="Tolk.cpp ScreenReaderDriverJAWS.cpp ScreenReaderDriverNVDA.cpp ScreenReaderDriverSA.cpp ScreenReaderDriverSNova.cpp ScreenReaderDriverWE.cpp ScreenReaderDriverZT.cpp ScreenReaderDriverSAPI.cpp fsapi.c wineyes.c zt.c"
```

Two differences from a plain `nmake` in that folder, both deliberate:

- **`TolkJNI.cpp` and `/D_WITH_JNI` are dropped.** They add the Java binding, which needs a JDK's
  `jni.h` on the include path and is of no use to a C# mod.
- **The CRT stays static.** That is `cl`'s default with no `/M` flag, and it is load-bearing: with
  the dynamic CRT the DLL would import `VCRUNTIME140.dll` / `MSVCP140.dll` **x86**, which far
  fewer machines have installed than the x64 redistributable. Verified after building — the import
  table is `USER32`, `ole32`, `OLEAUT32`, `KERNEL32` and nothing else.

Verify a rebuild with `dumpbin /headers` (machine must be `14C`), `dumpbin /dependents` (no
`VCRUNTIME*`), and `dumpbin /exports` (thirteen undecorated `Tolk_*` names — `TOLK_CALL` is
`__cdecl`, so 32-bit MSVC exports them without the `_name@N` decoration `__stdcall` would add).
Then launch the GOG build with NVDA running and check `LogOutput.log` for
`Tolk initialized, screen reader: NVDA`.
