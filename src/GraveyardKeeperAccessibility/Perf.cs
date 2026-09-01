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

    // Destination rebuilds are metered separately from the per-frame sections: they are the mod's
    // one genuinely heavy operation, they run on their own cadence rather than every frame, and an
    // average spread over sixty frames' worth of idle Updates says nothing about what one of them
    // costs. These three numbers are what a report needs — how often, how long, and over how much.
    /// <summary>The stages of one destination rebuild, so a slow one says WHICH part is slow.</summary>
    internal enum RefreshPhase
    {
        Snapshot,    // copying the registry
        Objects,     // the per-object walk, end to end
        Classify,    // ...of which: deciding what kind of thing each object is
        Label,       // ...of which: naming the ones that made it into a list
        Doors,       // collapsing duplicate door variants
        Quests,      // quest arrows and the hand-authored objective tables
        Landmarks,   // zones, entrances, named world objects
        Drops,       // ground items
        Finish,      // sorting and restoring the selection
        Count
    }

    private static readonly long[] _refreshPhaseTicks = new long[(int)RefreshPhase.Count];

    private static int _refreshCount;
    private static long _refreshTicks;
    private static long _refreshWorstTicks;
    private static long _refreshObjects;      // objects walked, summed
    private static long _refreshClassified;   // of those, the ones that survived to classification
    private static long _refreshCacheHits;    // of those, the ones answered from the classification cache
    private static long _refreshLabelled;     // objects that made it into a list and had to be named
    private static long _refreshLabelCacheHits;  // of those, the ones answered from the name cache

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

                if (_refreshCount > 0)
                {
                    double avgMs = _refreshTicks * _ticksToMs / _refreshCount;
                    _log?.LogInfo(
                        $"[PERF] {_refreshCount} destination rebuilds | avg {avgMs:0.0}ms " +
                        $"| worst {_refreshWorstTicks * _ticksToMs:0.0}ms " +
                        $"| {_refreshObjects / _refreshCount} objects walked, " +
                        $"{_refreshClassified / _refreshCount} classified " +
                        $"({_refreshCacheHits / _refreshCount} from cache), " +
                        $"{_refreshLabelled / _refreshCount} named " +
                        $"({_refreshLabelCacheHits / _refreshCount} from cache) each");

                    var phases = new System.Text.StringBuilder(160);
                    for (int i = 0; i < (int)RefreshPhase.Count; i++)
                    {
                        if (phases.Length > 0) phases.Append("  ");
                        phases.Append((RefreshPhase)i).Append(' ')
                              .Append((_refreshPhaseTicks[i] * _ticksToMs / _refreshCount).ToString("0.0"))
                              .Append("ms");
                    }
                    _log?.LogInfo($"[PERF] rebuild phases (avg) | {phases}");
                }
            }

            Array.Clear(_refreshPhaseTicks, 0, (int)RefreshPhase.Count);

            _refreshCount = 0;
            _refreshTicks = 0;
            _refreshWorstTicks = 0;
            _refreshObjects = 0;
            _refreshClassified = 0;
            _refreshCacheHits = 0;
            _refreshLabelled = 0;
            _refreshLabelCacheHits = 0;

            Array.Clear(_totalTicks, 0, N);
            Array.Clear(_worstTicks, 0, N);
            _frames = 0;
        }
        catch
        {
            // Diagnostics must never be the thing that breaks the mod.
        }
    }

    /// <summary>
    /// Record one destination rebuild. <paramref name="startedAt"/> is a
    /// <see cref="Stopwatch.GetTimestamp"/> taken at the top of the rebuild.
    /// </summary>
    internal static void NoteRefresh(long startedAt, int objectsWalked, int objectsClassified,
                                     int cacheHits, int labelled, int labelCacheHits)
    {
        try
        {
            long ticks = Stopwatch.GetTimestamp() - startedAt;
            _refreshCount++;
            _refreshTicks += ticks;
            if (ticks > _refreshWorstTicks) _refreshWorstTicks = ticks;
            _refreshObjects += objectsWalked;
            _refreshClassified += objectsClassified;
            _refreshCacheHits += cacheHits;
            _refreshLabelled += labelled;
            _refreshLabelCacheHits += labelCacheHits;
        }
        catch { }
    }

    /// <summary>Record the cost of one stage of a rebuild. <paramref name="startedAt"/> from <see cref="Now"/>.</summary>
    internal static void NoteRefreshPhase(RefreshPhase phase, long startedAt)
    {
        try { _refreshPhaseTicks[(int)phase] += Stopwatch.GetTimestamp() - startedAt; }
        catch { }
    }

    /// <summary>A timestamp to hand back to <see cref="NoteRefresh"/> / <see cref="NoteRefreshPhase"/>.</summary>
    internal static long Now() => Stopwatch.GetTimestamp();

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
