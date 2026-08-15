using System.Runtime.InteropServices;

namespace GraveyardKeeperAccessibility;

internal static class PrismWrapper
{
    private const string DllName = "prism";

    private struct PrismConfig
    {
        public byte version;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string dllToLoad);

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

    internal static bool Init(ManualLogSource log)
    {
        _log = log;
        try
        {
            _log.LogInfo("Loading prism.dll...");
            var handle = LoadLibrary(DllName + ".dll");
            if (handle == IntPtr.Zero)
            {
                _log.LogWarning("Failed to load prism.dll");
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
