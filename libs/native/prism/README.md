# Prism (bundled native library)

Prism is the screen-reader / TTS abstraction the mod speaks through. It is bundled here so
that releases work out of the box — without it the mod silently falls back to the SAPI voice
in `ScreenReader.InitSapi`, which cannot drive NVDA or JAWS, never outputs braille, and does
not exist at all off Windows.

| | |
|---|---|
| Upstream | <https://github.com/ethindp/prism> |
| Version | **v0.17.3** — see the ABI warning below before changing this |
| License | MPL-2.0 (see `LICENSE`; third-party components in `NOTICE`) |

All three binaries come from the `dynamic/release` folder of that release's platform zips
(not `static/`, and not the `debug` variants):

| File | From | Architecture |
|---|---|---|
| `prism.dll` | `prism-windows-x64.zip` | PE32+ x86-64 |
| `libprism.so` | `prism-linux-x64.zip` | ELF x86-64 |
| `libprism.dylib` | `prism-macos-universal.zip` | Mach-O universal (x86-64 + arm64) |

One release zip serves every storefront and we cannot know which OS it will be installed on,
so all three ship (~3.5 MB) and `PrismWrapper.NativeLibraryName()` picks one at runtime.

⚠️ **Only the Windows path is verified.** The Linux and macOS libraries are bundled
speculatively — nobody has confirmed that BepInEx 5 even loads on this game's native Mac and
Linux builds. Worst case those platforms stay as mute as they were before. Most Linux players
run the Windows build through Proton anyway, which uses `prism.dll`.

## ⚠️ Upgrading Prism — read this first

`PrismWrapper.PrismConfig` mirrors the C `PrismConfig` struct and **must be updated in the
same commit as the binaries**. A mismatch is not a compile error, it is memory corruption:
`prism_config_init` returns the struct by value (large structs come back through a hidden
pointer, small ones in a register) and `prism_init` reads the whole thing back out.

This already changed once. Up to v0.16.5 the struct was a lone `uint8_t version`; **v0.17.0
grew it to eight fields**. The current C# copy matches v0.17.3:

| Field | C type | Offset |
|---|---|---|
| `version` | `uint8_t` | 0 |
| `registry` | `PrismRegistry *` | 8 |
| `availability_callback` | `PrismAvailabilityCallback` | 16 |
| `availability_userdata` | `void *` | 24 |
| `availability_poll_interval_ms` | `uint32_t` | 32 |
| `availability_debounce_samples` | `uint32_t` | 36 |
| `availability_backoff_max_ms` | `uint32_t` | 40 |
| `availability_auto_power_manage` | `bool` | 44 |

`sizeof` is 48, verified against `Marshal.SizeOf`/`OffsetOf` on the managed side. Note the C
`bool` is mirrored as a C# `byte`, not `bool` — a `bool` field marshals as a 4-byte `BOOL`
and would shift nothing here but breaks blittability.

Everything else the wrapper imports was checked field-by-field across v0.16.5 → v0.17.3 and
is unchanged: all twelve function signatures, the `PRISM_BACKEND_SUPPORTS_*` feature bits
(braille is still `1 << 4`, output `1 << 5`), and `PRISM_ERROR_NOT_IMPLEMENTED == 3`. The new
v0.17 error codes were appended before `PRISM_ERROR_COUNT`, so existing values did not shift.

Keep all three platform libraries on the same version. After any upgrade, launch the game
once with a screen reader running and check `LogOutput.log` for
`Prism initialized with backend: ...` — an ABI or API break shows up as a failed init and a
silent drop to SAPI, which is easy to miss.

## How it is loaded

`PrismWrapper.Init` loads the library by **full path** from the folder next to
`GraveyardKeeperAccessibility.dll`, via `LoadLibrary` on Windows and `dlopen` with
`RTLD_GLOBAL` elsewhere. Neither loader would find it otherwise — Windows searches the
game's executable directory, and Mono probes the standard system library paths; neither
looks in the BepInEx plugin folder. Once the module is resident, the `[DllImport("prism")]`
entries bind to it by name.

## Not bundled

- **`tolk.dll` / `libtolk.so`** in the release zips are a Tolk-compatible *shim built on top
  of* Prism, for apps already written against Tolk. Prism itself has no tolk dependency
  (verified: no `tolk` import in `prism.dll`), and this mod calls the `prism_*` API directly.
- **`prism_orca_bridge.dll.so` / `prism_speech_dispatcher_bridge.dll.so`** from the Linux zip
  are Winelib bridges that let a Windows Prism running under Wine/Proton reach the host's
  Orca or speech-dispatcher. They have to be installed into the Wine prefix, which a plugin
  folder cannot do, so Proton users currently get no speech unless they set that up
  themselves.
