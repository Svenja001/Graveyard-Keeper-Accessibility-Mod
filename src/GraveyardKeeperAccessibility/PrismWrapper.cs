using System.IO;
using System.Runtime.InteropServices;

namespace GraveyardKeeperAccessibility;

internal static class PrismWrapper
{
    private const string DllName = "prism";

    /// <summary>
    /// Mirrors <c>PrismConfig</c> from prism.h, as of the bundled <b>v0.17.3</b>.
    /// <para>
    /// ⚠ This struct must be kept in lockstep with the bundled binaries. It was a lone
    /// <c>uint8_t version</c> up to v0.16.5 and grew these eight fields in v0.17.0; a mismatch
    /// is not a compile error but memory corruption, because <c>prism_config_init</c> returns
    /// it by value (large structs come back through a hidden pointer, small ones in a
    /// register) and <c>prism_init</c> then reads the full struct back out.
    /// </para>
    /// <para>
    /// Every field is left exactly as <c>prism_config_init</c> filled it — the mod only wants
    /// the default registry and no availability callback. Kept blittable (the C <c>bool</c> is
    /// a <see cref="byte"/> here, not a <see cref="bool"/>, which would marshal as a 4-byte
    /// BOOL) so the by-value return needs no marshalling.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PrismConfig
    {
        public byte version;
        public IntPtr registry;                        // PrismRegistry*
        public IntPtr availability_callback;           // PrismAvailabilityCallback
        public IntPtr availability_userdata;           // void*
        public uint availability_poll_interval_ms;
        public uint availability_debounce_samples;
        public uint availability_backoff_max_ms;
        public byte availability_auto_power_manage;    // C bool
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string dllToLoad);

    // dlopen lives in libdl on older glibc and in libc on newer ones (and on macOS); try both.
    private const int RtldNow = 2;
    private const int RtldGlobal = 8;

    [DllImport("libdl", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen_libdl(string fileName, int flags);

    [DllImport("libc", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen_libc(string fileName, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern PrismConfig prism_config_init();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr prism_init(ref PrismConfig cfg);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void prism_shutdown(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr prism_registry_acquire_best(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int prism_backend_initialize(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong prism_backend_get_features(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int prism_backend_speak(IntPtr backend, [MarshalAs(UnmanagedType.LPStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int prism_backend_braille(IntPtr backend, [MarshalAs(UnmanagedType.LPStr)] string text);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int prism_backend_output(IntPtr backend, [MarshalAs(UnmanagedType.LPStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void prism_backend_free(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr prism_backend_name(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr prism_error_string(int error);

    // PrismBackendFeature bits (see include/prism.h). Only the ones we act on are listed.
    private const ulong FeatureSupportsBraille = 1UL << 4;
    private const ulong FeatureSupportsOutput = 1UL << 5;

    private static IntPtr _context = IntPtr.Zero;
    private static IntPtr _backend = IntPtr.Zero;
    private static ManualLogSource _log;

    /// <summary>
    /// True when the backend can drive a braille display. Screen reader backends (NVDA, JAWS,
    /// System Access, ZDSR, PC-Talker, Window-Eyes) implement this; plain TTS backends
    /// (SAPI, OneCore) do not. Says nothing about whether a display is actually plugged in —
    /// Prism explicitly documents that the return value can't be used to detect that.
    /// </summary>
    internal static bool SupportsBraille { get; private set; }

    /// <summary>
    /// True when the backend implements <c>prism_backend_output</c>, which sends one string to
    /// speech and braille in a single call — the way Prism wants combined output done.
    /// </summary>
    private static bool _supportsOutput;

    private static bool IsWindows =>
        Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

    private static bool IsMac =>
        Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;

    /// <summary>
    /// File name of the native Prism library for the platform we are running on. All three are
    /// bundled, so this only picks which one to load. Unity reports OSXPlayer for both Intel
    /// and Apple Silicon; the bundled dylib is a universal binary covering both.
    /// </summary>
    private static string NativeLibraryName()
    {
        if (IsWindows)
            return DllName + ".dll";

        return IsMac ? "lib" + DllName + ".dylib" : "lib" + DllName + ".so";
    }

    /// <summary>
    /// Full path to the native Prism library shipped alongside this assembly, or null when it is
    /// missing (a hand-installed copy in the game root, or an assembly loaded from memory with
    /// no <see cref="Assembly.Location"/>).
    /// </summary>
    private static string BundledPrismPath()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(PrismWrapper).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
                return null;

            var path = Path.Combine(dir, NativeLibraryName());
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Could not locate the bundled Prism library: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pre-loads the native library from an absolute path. Neither loader searches the BepInEx
    /// plugin folder on its own - Windows looks beside the executable, and Mono probes the
    /// standard library paths - so the module has to be brought in by hand before the first
    /// <c>[DllImport(DllName)]</c> call binds to it by name.
    /// </summary>
    private static IntPtr LoadNativeLibrary(string path)
    {
        if (IsWindows)
            return LoadLibrary(path);

        // RTLD_GLOBAL so the symbols are visible when Mono later resolves DllImport("prism").
        try
        {
            return dlopen_libdl(path, RtldNow | RtldGlobal);
        }
        catch (DllNotFoundException)
        {
            return dlopen_libc(path, RtldNow | RtldGlobal);
        }
    }

    internal static bool Init(ManualLogSource log)
    {
        _log = log;

        // Every bundled Prism binary is 64-bit, because upstream has never published a 32-bit
        // Windows build (checked across all 54 releases, v0.1.0 to v0.17.3). The GOG build of the
        // game is a 32-bit process, so there is nothing to load there and no version to fall back
        // to. Say so plainly instead of letting it read as a broken install: the caller starts the
        // SAPI voice next, which works fine, but loses NVDA/JAWS and braille.
        if (IntPtr.Size == 4)
        {
            log.LogWarning("32-bit game process (this is the GOG build). Prism has no 32-bit " +
                           "library, so speech falls back to Windows SAPI - no NVDA, JAWS or braille.");
            return false;
        }

        try
        {
            // The native library ships next to this assembly, one per platform. Loading it by
            // bare name would search the game's exe directory and the system library paths but
            // never the BepInEx plugin folder, so resolve the bundled copy by full path. Falling
            // back to the bare name keeps working for installs that still have a hand-placed
            // copy in the game root.
            var bundled = BundledPrismPath();
            var name = NativeLibraryName();
            _log.LogInfo(bundled != null ? $"Loading bundled {name} from {bundled}..." : $"Loading {name} from the game folder...");
            var handle = LoadNativeLibrary(bundled ?? name);
            if (handle == IntPtr.Zero)
            {
                _log.LogWarning($"Failed to load {name}");
                return false;
            }

            _log.LogInfo("Initializing Prism context...");
            var config = prism_config_init();
            _context = prism_init(ref config);

            if (_context == IntPtr.Zero)
            {
                _log.LogWarning("Failed to initialize Prism context");
                return false;
            }

            _log.LogInfo("Acquiring best Prism backend...");
            _backend = prism_registry_acquire_best(_context);

            if (_backend == IntPtr.Zero)
            {
                _log.LogWarning("No Prism backend available");
                prism_shutdown(_context);
                _context = IntPtr.Zero;
                return false;
            }

            var backendName = Marshal.PtrToStringAnsi(prism_backend_name(_backend));
            var features = prism_backend_get_features(_backend);
            SupportsBraille = (features & FeatureSupportsBraille) != 0;
            _supportsOutput = (features & FeatureSupportsOutput) != 0;
            _log.LogInfo($"Prism initialized with backend: {backendName} (braille: {SupportsBraille}, combined output: {_supportsOutput})");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Prism initialization error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends text to every modality the backend has. On a screen reader that means speech and
    /// the braille display at once; on a TTS-only backend it degrades to plain speech.
    /// </summary>
    internal static bool Speak(string text, bool interrupt = true)
    {
        if (_backend == IntPtr.Zero || string.IsNullOrWhiteSpace(text))
            return false;

        if (_supportsOutput)
        {
            var outResult = Call(() => prism_backend_output(_backend, text, interrupt), "output");
            // NOT_IMPLEMENTED means the feature bit lied; drop to speech for the rest of the session.
            if (outResult != ErrorNotImplemented)
                return outResult == 0;
            _supportsOutput = false;
        }

        return Call(() => prism_backend_speak(_backend, text, interrupt), "speak") == 0;
    }

    /// <summary>
    /// Puts text on the braille display without speaking it. Braille output is independent of
    /// speech: it neither interrupts speech in progress nor is cleared by later speech, and it
    /// stays on the display until the next braille write (or until the screen reader overwrites
    /// it with whatever the user focuses).
    /// </summary>
    internal static bool Braille(string text)
    {
        if (_backend == IntPtr.Zero || !SupportsBraille || string.IsNullOrWhiteSpace(text))
            return false;

        return Call(() => prism_backend_braille(_backend, text), "braille") == 0;
    }

    private const int ErrorNotImplemented = 3; // PRISM_ERROR_NOT_IMPLEMENTED

    /// <summary>Runs a Prism call, logs anything that isn't PRISM_OK, and returns the error code.</summary>
    private static int Call(Func<int> call, string what)
    {
        try
        {
            var result = call();
            if (result != 0)
            {
                var errMsg = Marshal.PtrToStringAnsi(prism_error_string(result));
                _log?.LogWarning($"Prism {what} failed: {errMsg}");
            }
            return result;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Prism {what} error: {ex.Message}");
            return -1;
        }
    }

    internal static void Shutdown()
    {
        if (_backend != IntPtr.Zero)
        {
            prism_backend_free(_backend);
            _backend = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero)
        {
            prism_shutdown(_context);
            _context = IntPtr.Zero;
        }

        SupportsBraille = false;
        _supportsOutput = false;
    }
}
