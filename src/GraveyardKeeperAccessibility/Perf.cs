using System.Diagnostics;

namespace GraveyardKeeperAccessibility;

/// <summary>
/// A tiny always-on profiler for the mod's own per-frame work.
///
/// WHY: "the game lags" is the hardest kind of bug report to act on — it arrives without a number,
/// from a player who can't watch a frame graph, and the mod runs inside someone else's game where
/// any Unity profiler is unavailable. So the mod measures itself and writes the answer into the
/// BepInEx log, which a tester can simply send. One compact summary line per
/// <see cref="SummarySeconds"/>, plus a warning whenever a single frame's mod work blows past
/// <see cref="SlowFrameMs"/> — that second one is what catches a hitch, which an average hides.
///
/// The measurement itself has to be beneath notice or it becomes the problem it's diagnosing:
/// per section this is two <see cref="Stopwatch.GetTimestamp"/> calls (a raw performance-counter
/// read, tens of nanoseconds) and some arithmetic on preallocated arrays. Nothing allocates except
/// the summary string, once a minute.
/// </summary>
internal static class Perf
{
    /// <summary>The mod's per-frame sections, in the order they're reported.</summary>
    internal enum Section
    {
        Total,          // everything the mod does in one Update
        Registry,       // world-object index upkeep
        Navigator,      // ObjectNavigator.Update, including destination rebuilds
        Interaction,    // proximity readout
        Combat,         // combat assist scans
        Gui,            // menu/GUI polling
        Count
    }

    private const int N = (int)Section.Count;
    private const float SummarySeconds = 60f;
    private const float SlowFrameMs = 8f;       // half a 60fps frame budget spent in the mod
    private const float SlowFrameCooldown = 10f;

    private static ManualLogSource _log;
    private static readonly long[] _openedAt = new long[N];
    private static readonly long[] _frameTicks = new long[N];   // this frame
    private static readonly long[] _totalTicks = new long[N];   // since last summary
    private static readonly long[] _worstTicks = new long[N];   // worst single frame since summary

    private static int _frames;
    private static float _nextSummaryAt;
    private static float _lastSlowFrameWarnAt = float.NegativeInfinity;
    private static double _ticksToMs;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _ticksToMs = 1000.0 / Stopwatch.Frequency;
        _nextSummaryAt = Time.unscaledTime + SummarySeconds;
    }

    internal static void Begin(Section s)
    {
        _openedAt[(int)s] = Stopwatch.GetTimestamp();
    }

    internal static void End(Section s)
    {
        int i = (int)s;
        // A Begin that never happened (an early return between the two) would otherwise bank the
        // time since process start as this section's cost.
        if (_openedAt[i] == 0) return;
        _frameTicks[i] += Stopwatch.GetTimestamp() - _openedAt[i];
        _openedAt[i] = 0;
    }

    /// <summary>
    /// Close out the frame: fold this frame's numbers into the running totals and emit the log
    /// lines when they're due. Called once at the very end of <see cref="Plugin"/>'s Update.
    /// </summary>
    internal static void EndFrame()
    {
        try
        {
            _frames++;

            for (int i = 0; i < N; i++)
            {
                long t = _frameTicks[i];
                _totalTicks[i] += t;
                if (t > _worstTicks[i]) _worstTicks[i] = t;
                _frameTicks[i] = 0;
                _openedAt[i] = 0;
            }

            float now = Time.unscaledTime;

            // A single expensive frame is what a player actually feels, and an average over a
            // minute would smooth it away entirely — so report it separately, rate-limited so a
            // genuinely bad stretch doesn't flood the log.
            double totalMs = _worstTicks[(int)Section.Total] * _ticksToMs;
            if (totalMs > SlowFrameMs && now - _lastSlowFrameWarnAt > SlowFrameCooldown)
            {
                _lastSlowFrameWarnAt = now;
                _log?.LogWarning($"[PERF] Slow mod frame: {Describe(_worstTicks)}");
            }

            if (now < _nextSummaryAt) return;
            _nextSummaryAt = now + SummarySeconds;

            if (_frames > 0)
            {
                // Per-frame averages: the number that says whether the mod is affordable at all.
                var avg = new long[N];
                for (int i = 0; i < N; i++) avg[i] = _totalTicks[i] / _frames;
                _log?.LogInfo($"[PERF] {_frames} frames | avg {Describe(avg)}");
                _log?.LogInfo($"[PERF] {_frames} frames | worst {Describe(_worstTicks)} | {WorldObjectRegistry.Objects.Count} objects tracked");
            }

            Array.Clear(_totalTicks, 0, N);
            Array.Clear(_worstTicks, 0, N);
            _frames = 0;
        }
        catch
        {
            // Diagnostics must never be the thing that breaks the mod.
        }
    }

    private static string Describe(long[] ticks)
    {
        var sb = new System.Text.StringBuilder(128);
        for (int i = 0; i < N; i++)
        {
            if (sb.Length > 0) sb.Append("  ");
            sb.Append((Section)i).Append(' ').Append((ticks[i] * _ticksToMs).ToString("0.00")).Append("ms");
        }
        return sb.ToString();
    }
}
