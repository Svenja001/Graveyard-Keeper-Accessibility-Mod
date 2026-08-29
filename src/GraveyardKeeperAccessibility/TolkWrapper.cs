using System.IO;

namespace GraveyardKeeperAccessibility;

/// <summary>
/// P/Invoke wrapper for Tolk, the screen-reader abstraction used on the <b>32-bit GOG build</b> of
/// the game and nowhere else.
/// <para>
/// Everywhere else the mod speaks through <see cref="PrismWrapper"/>, which is newer, does more and
/// is what the rest of this project is built around. Prism has simply never published a 32-bit
/// Windows build — checked across every release — and a 32-bit process cannot load a 64-bit DLL, so
/// GOG players had no screen reader at all and fell through to the SAPI voice.
/// </para>
/// <para>
/// The two libraries cannot fight over the screen reader, because only one is ever initialised:
/// <see cref="PrismWrapper.Init"/> returns early when <c>IntPtr.Size == 4</c> and
/// <see cref="Init"/> here returns early when it is not. See <c>libs\native\tolk\README.md</c>.
/// </para>
/// </summary>
internal static class TolkWrapper
{
    private const string DllName = "Tolk";

    // TOLK_CALL is __cdecl and the exports are undecorated (verified with dumpbin). The strings are
    // const wchar_t*, so UTF-16 straight from C# - German umlauts need no conversion. The bools are
    // C++ bools, one byte: without the U1 marshalling below the runtime would read four.
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Tolk_Load();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Tolk_IsLoaded();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Tolk_DetectScreenReader();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Tolk_HasBraille();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Tolk_Output([MarshalAs(UnmanagedType.LPWStr)] string str, [MarshalAs(UnmanagedType.U1)] bool interrupt);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Tolk_Braille([MarshalAs(UnmanagedType.LPWStr)] string str);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string dllToLoad);

    /// <summary>
    /// Screen-reader client DLLs Tolk loads by bare name from its own drivers. Only NVDA's ships
    /// with the mod; the other two are proprietary vendor binaries and are loaded only if a player
    /// has put them next to the mod themselves. See the README in <c>libs\native\tolk\</c>.
    /// </summary>
    private static readonly string[] ClientLibraries =
    {
        "nvdaControllerClient32.dll", // NVDA        - bundled
        "SAAPI32.dll",                // System Access - optional, not bundled
        "dolapi32.dll",               // SuperNova   - optional, not bundled
    };

    private static bool _loaded;
    private static ManualLogSource _log;

    /// <summary>
    /// True when the detected screen reader can drive a braille display. NVDA, JAWS and
    /// Window-Eyes can; ZoomText cannot. Says nothing about whether a display is plugged in.
    /// </summary>
    internal static bool SupportsBraille { get; private set; }

    /// <summary>Common name of the screen reader Tolk picked, for the log line only.</summary>
    internal static string ScreenReaderName { get; private set; }

    /// <summary>
    /// Loads Tolk and reports whether a screen reader is actually running. False means the caller
    /// should start the SAPI voice instead - Tolk drives screen readers only here, because the
    /// mod's own SAPI fallback is already proven and runs out of process.
    /// </summary>
    internal static bool Init(ManualLogSource log)
    {
        _log = log;

        // Tolk exists in this mod purely to cover the case Prism cannot: a 32-bit Windows process.
        // Anywhere else Prism has already had its turn and is the better library, so refuse to
        // initialise rather than give the two of them a chance to both hold the screen reader.
        if (IntPtr.Size != 4)
            return false;

        if (Application.platform != RuntimePlatform.WindowsPlayer &&
            Application.platform != RuntimePlatform.WindowsEditor)
            return false;

        try
        {
            var dir = AssemblyDirectory();
            if (dir == null)
            {
                _log.LogWarning("Could not locate the mod folder, so Tolk cannot be loaded.");
                return false;
            }

            // Order matters: Tolk's NVDA driver calls LoadLibrary("nvdaControllerClient32.dll")
            // with a bare name inside Tolk_Load, and the plugin folder is neither the working
            // directory nor on PATH. Pre-loading by full path puts the module in the process, and
            // the loader then satisfies Tolk's bare-name request from it.
            PreloadClientLibraries(dir);

            var tolkPath = Path.Combine(dir, DllName + ".dll");
            if (!File.Exists(tolkPath))
            {
                _log.LogWarning($"Tolk.dll is missing from {dir} - falling back to the SAPI voice.");
                return false;
            }

            _log.LogInfo($"Loading bundled Tolk.dll from {tolkPath}...");
            if (LoadLibrary(tolkPath) == IntPtr.Zero)
            {
                _log.LogWarning($"Failed to load Tolk.dll (error {Marshal.GetLastWin32Error()})");
                return false;
            }

            Tolk_Load();
            if (!Tolk_IsLoaded())
            {
                _log.LogWarning("Tolk_Load did not initialize");
                return false;
            }
            _loaded = true;

            // Tolk has no concept of "no screen reader" beyond this returning NULL. SAPI is left
            // out of its auto-detection (Tolk_TrySAPI is never called), so a null here means no
            // screen reader is running and the caller should use the mod's own SAPI voice.
            var namePtr = Tolk_DetectScreenReader();
            if (namePtr == IntPtr.Zero)
            {
                _log.LogInfo("Tolk loaded but no screen reader is running - using the SAPI voice.");
                return false;
            }

            ScreenReaderName = Marshal.PtrToStringUni(namePtr);
            SupportsBraille = Tolk_HasBraille();
            _log.LogInfo($"Tolk initialized, screen reader: {ScreenReaderName} (braille: {SupportsBraille})");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Tolk initialization error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Brings the screen readers' client DLLs into the process by full path, before Tolk asks for
    /// them by bare name. Missing ones are not an error: only NVDA's ships with the mod, and Tolk's
    /// drivers are written to keep working when their DLL is absent.
    /// </summary>
    private static void PreloadClientLibraries(string dir)
    {
        foreach (var name in ClientLibraries)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
                continue;

            if (LoadLibrary(path) == IntPtr.Zero)
                _log.LogWarning($"Could not pre-load {name} (error {Marshal.GetLastWin32Error()})");
            else
                _log.LogInfo($"Pre-loaded {name} for Tolk");
        }
    }

    private static string AssemblyDirectory()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(TolkWrapper).Assembly.Location);
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Could not locate the mod folder: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sends text to speech and braille at once, the way Tolk wants combined output done.
    /// Returns false if the screen reader has since gone away, which is the caller's cue to fall
    /// back to SAPI - Tolk re-detects on every call, so it recovers by itself if it comes back.
    /// </summary>
    internal static bool Speak(string text, bool interrupt = true)
    {
        if (!_loaded || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            return Tolk_Output(text, interrupt);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Tolk output error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes to the braille display only, leaving speech alone. Mirrors
    /// <see cref="PrismWrapper.Braille"/>.
    /// </summary>
    internal static bool Braille(string text)
    {
        if (!_loaded || !SupportsBraille || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            return Tolk_Braille(text);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Tolk braille error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Drops the mod's use of Tolk. <c>Tolk_Unload</c> is deliberately <b>not</b> called: it ends
    /// with an unconditional <c>CoUninitialize</c>, while its <c>Tolk_Load</c> counterpart asks for
    /// an MTA apartment that Unity's main thread — already an STA — refuses with
    /// <c>RPC_E_CHANGED_MODE</c>. The pair is therefore unbalanced on this thread, and the missing
    /// call would come out of the game's own COM reference count, with Rewired's DirectInput and
    /// the game's file dialogs sitting on the other side of it. This runs from
    /// <c>Plugin.OnDestroy</c>, i.e. at shutdown, so letting the OS reclaim the drivers is the
    /// cheaper mistake by a wide margin.
    /// </summary>
    internal static void Shutdown()
    {
        _loaded = false;
        SupportsBraille = false;
        ScreenReaderName = null;
    }
}
