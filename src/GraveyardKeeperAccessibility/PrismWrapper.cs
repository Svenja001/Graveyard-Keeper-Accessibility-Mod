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
    private static extern int prism_backend_speak(IntPtr backend, [MarshalAs(UnmanagedType.LPStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int prism_backend_output(IntPtr backend, [MarshalAs(UnmanagedType.LPStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void prism_backend_free(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr prism_backend_name(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr prism_error_string(int error);

    private static IntPtr _context = IntPtr.Zero;
    private static IntPtr _backend = IntPtr.Zero;
    private static ManualLogSource _log;

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
            _log.LogInfo($"Prism initialized with backend: {backendName}");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Prism initialization error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static bool Speak(string text, bool interrupt = true)
    {
        if (_backend == IntPtr.Zero || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var result = prism_backend_speak(_backend, text, interrupt);
            if (result != 0)
            {
                var errMsg = Marshal.PtrToStringAnsi(prism_error_string(result));
                _log?.LogWarning($"Prism speak failed: {errMsg}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Prism speak error: {ex.Message}");
            return false;
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
    }
}
