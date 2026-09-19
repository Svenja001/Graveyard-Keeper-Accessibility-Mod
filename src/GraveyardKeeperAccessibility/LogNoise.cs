namespace GraveyardKeeperAccessibility;

/// <summary>
/// Drops two of the game's own debug messages before they reach the log.
///
/// The log is the mod's only diagnostic tool — README tells players to attach it to a bug report,
/// and every navigation problem so far has been solved by reading it. But a real session runs to
/// 40 MB, and measuring one (457,593 lines, 2026-09-02) showed that <b>80% of it is two messages
/// the game leaves lying around</b>:
///
/// <list type="bullet">
/// <item>138,036 lines of the game's install path, from a stray <c>Debug.Log(Application.dataPath)</c>
/// inside <c>DLCEngine.IsDLCRefugeesAvailable</c> and <c>IsDLCSoulsAvailable</c> — DLC ownership is
/// re-checked constantly, and each check prints the path.</item>
/// <item>214,324 lines of <c>#BAG# Found bag in multiinventory: …</c> from <c>MultiInventory</c>,
/// once per bag carried, every time the inventory is scanned for one.</item>
/// </list>
///
/// Neither says anything: the path never changes, and the bag line reports a successful lookup that
/// happens thousands of times a minute. Suppressing exactly these two takes a session from 40.6 MB
/// to 8.3 MB, which is what makes keeping logs across runs practical at all.
///
/// Deliberately a two-entry blocklist rather than a filter on volume. Plenty of the game's other
/// chatter is repetitive AND load-bearing — <c>Failed pathfinding!</c>, <c>end point is too far</c>,
/// <c>Say "…" on wgo</c>, <c>Run FlowScript</c> and <c>Open GUI:</c> are how the mod's own bugs get
/// diagnosed. Never suppress by frequency, and never turn off <c>WriteUnityLog</c> wholesale; add a
/// line here only when it is provably empty of information.
/// </summary>
internal static class LogNoise
{
    private const string BagScan = "#BAG# Found bag in multiinventory:";

    private static ManualLogSource _log;
    private static string _dataPath;
    private static int _suppressed;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _suppressed = 0;
        try { _dataPath = Application.dataPath; } catch { _dataPath = null; }
    }

    /// <summary>
    /// Prefix on <c>UnityEngine.Debug.Log(object)</c>. Returning false skips the call entirely, so
    /// the message reaches neither BepInEx's log file nor Unity's own player log.
    ///
    /// This sits on a genuinely hot path — roughly half a million calls in a session — so the test
    /// is ordered cheapest-first: a length check, then a single character, then at most one string
    /// comparison. It still costs far less than formatting and writing the line it prevents.
    ///
    /// Only the one-argument overload is patched. The two-argument
    /// <c>Debug.Log(object, UnityEngine.Object)</c> is a different method and neither offender uses
    /// it; if a future spammer does, patch that one too rather than widening this.
    /// </summary>
    public static bool Debug_Log_Prefix(object __0)
    {
        try
        {
            if (__0 is not string msg || msg.Length == 0) return true;

            bool noise = (msg[0] == '#' && msg.StartsWith(BagScan, StringComparison.Ordinal))
                         || (_dataPath != null && msg.Length == _dataPath.Length && msg == _dataPath);
            if (!noise) return true;

            // Proof it is still working, at a rate that cannot itself become noise: seven or so
            // lines across a long session. If these stop appearing, the patch has come unstuck.
            if (++_suppressed % 50000 == 0)
                _log?.LogInfo($"[LOGNOISE] {_suppressed} lines of game spam suppressed so far");
            return false;
        }
        catch
        {
            return true;   // never let a logging filter swallow a message by failing
        }
    }
}
