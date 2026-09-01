namespace GraveyardKeeperAccessibility;

/// <summary>
/// Hides the game's off-stage NPC parking spot from every readout.
///
/// WHY: Graveyard Keeper does not despawn a character that is finished for the day — it teleports
/// it to a GD point and leaves it there, still active in the hierarchy. The game's own flow nodes
/// say so plainly:
///
///   * <c>Flow_RemoveNPCToStock</c> sets <c>wgo.transform.position = gdPoint.transform.position</c>
///     for the tag it was given, defaulting to <c>"default_destroy_point"</c>, then calls
///     <c>wgo.OnCameToGDPoint(point)</c>. It does NOT deactivate the object.
///   * <c>Flow_IsWGOInStock</c> answers "is this NPC off-stage?" by comparing the object's position
///     to that same point for EXACT equality.
///   * <c>PlayersTavernEngine.TemporarilyRemoveVisitors</c> and
///     <c>WorldMap.RemoveZombieWorkerToStock</c> park their characters the same way.
///
/// A sighted player never sees that spot. A blind player using this mod saw the whole crowd — every
/// villager not on shift, every tavern guest between visits, every NPC whose day it is not — listed
/// under People and Vendors, offered as quest targets, and auto-walkable off into a part of the
/// world nobody is meant to stand in. That produced real bugs.
///
/// HOW WE KNOW: two tests, in order of how much they can be trusted.
///
///   1. <c>WorldGameObject.cur_gd_point</c>. <c>OnCameToGDPoint</c> stamps the GD point's tag onto
///      the object, and it is a serialized field (<c>SerializableWGO</c>), so it survives a save and
///      reload — which matters, because a character parked in one session is still parked in the
///      next. This is exact, costs a field read, and needs no coordinates at all.
///   2. The object's position against the stock GD points we can find in the world, for the parking
///      paths that move a character without going through <c>OnCameToGDPoint</c> (the tavern engine
///      is one).
///
/// NOT DONE HERE: guessing a parking spot from a pile-up. The first attempt at that hid the refugee
/// camp's chickens, which cluster around <c>camp_chicken_*</c> idle points a sighted player can see
/// perfectly well. Real content must never disappear on a heuristic, so a pile-up is now only
/// REPORTED to the log (see <see cref="NoteCharacters"/>) and left visible.
/// </summary>
internal static class StockPointFilter
{
    private const float TileSize = 96f;

    /// <summary>How close to a stock point an object has to be to count as parked on it.</summary>
    private const float ParkedRadius = 2f * TileSize;
    private const float ParkedRadiusSqr = ParkedRadius * ParkedRadius;

    /// <summary>GD points are static scene content; re-resolve occasionally as a backstop only.</summary>
    private const float RefreshSeconds = 20f;

    // ---- Pile-up diagnostic (reports, never hides) ----
    // The game parks by assigning the point's position verbatim, so a stock pile sits on one spot.
    // The radius is nevertheless a tile and a half, not a few units: a parked character can be
    // nudged by its idle animation, and the case this was written for — four NPCs the mod listed
    // 48-49 tiles due south, which is what a player heard as "the crowd is still there" — is spread
    // over about a tile. This only ever writes a log line, so erring wide costs nothing.
    private const int StackThreshold = 3;
    private const float StackRadius = 1.5f * TileSize;
    private const float StackRadiusSqr = StackRadius * StackRadius;

    private static ManualLogSource _log;

    private static readonly List<Vector2> _points = new(4);
    private static float _resolvedAt = float.NegativeInfinity;
    private static string _lastResolvedSummary;

    // Scratch for the pile-up report: parallel position / count / first-member tallies.
    private static readonly List<Vector2> _stackPos = new(32);
    private static readonly List<int> _stackCount = new(32);
    private static readonly List<string> _stackWho = new(32);
    private static readonly HashSet<string> _stacksReported = new(StringComparer.Ordinal);

    internal static void Init(ManualLogSource log) => _log = log;

    /// <summary>Force the points to be resolved again (a world/scene change moved them).</summary>
    internal static void Invalidate() => _resolvedAt = float.NegativeInfinity;

    /// <summary>
    /// True when this object is parked off-stage and must not be announced, listed or walked to.
    /// Cheap enough for a per-object gate in a scan: a string field read that almost always fails on
    /// the first character, then a squared-distance compare against a handful of points.
    /// </summary>
    internal static bool IsParked(WorldGameObject obj)
    {
        if (obj == null) return false;
        try
        {
            if (IsStockTag(obj.cur_gd_point)) return true;
            return IsParked(obj.pos);
        }
        catch { return false; }
    }

    /// <summary>Position-only form, for targets that have no world object (quest GD points).</summary>
    internal static bool IsParked(Vector2 pos)
    {
        EnsureResolved();

        for (int i = 0; i < _points.Count; i++)
            if ((_points[i] - pos).sqrMagnitude <= ParkedRadiusSqr) return true;

        return false;
    }

    /// <summary>
    /// True when a GD tag names a place CHARACTERS are parked, rather than a place they stand or a
    /// place goods are piled.
    ///
    /// The real world data, read out of a live save, is what this is shaped around. The engine's
    /// default is <c>default_destroy_point</c>, and next to it sits one point per off-duty cast
    /// member, all named the same way and all in the same off-map row at y ≈ -6200:
    ///
    ///     gd_stock_bishop  gd_stock_actress  gd_stock_merchant
    ///     gd_stock_cultist gd_stock_inquisitor gd_stock_astrologer
    ///
    /// The first attempt matched only <c>stock_point</c> / <c>npc_stock</c> and so found exactly one
    /// of the eight — which is why the tavern's cast was still being announced 48 tiles due south.
    ///
    /// But "stock" alone is too greedy in the other direction: <c>gd_zmb_wood_stock_1</c> is where a
    /// zombie stacks WOOD, real content a player needs to find. So the prefix has to be the whole
    /// test — <c>gd_stock_…</c> names a parked character, <c>gd_zmb_wood_stock_…</c> does not.
    /// </summary>
    private static bool IsStockTag(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return s.IndexOf("destroy_point", StringComparison.OrdinalIgnoreCase) >= 0
            || s.StartsWith("gd_stock_", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("npc_stock", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("stock_point", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureResolved()
    {
        if (Time.unscaledTime - _resolvedAt < RefreshSeconds) return;
        _resolvedAt = Time.unscaledTime;

        try
        {
            var points = WorldMap.gd_points;
            if (points == null) return;

            _points.Clear();
            string summary = null;

            // Deliberately NOT WorldMap.GetGDPointByGDTag: that returns the first ENABLED match,
            // and a stock point that has been disabled still has the crowd standing on it.
            foreach (var p in points)
            {
                if (p == null) continue;
                if (!IsStockTag(p.gd_tag) && !IsStockTag(p.name)) continue;
                var at = (Vector2)p.transform.position;
                _points.Add(at);
                summary = summary == null
                    ? $"{p.gd_tag}/{p.name} at {at}"
                    : $"{summary}; {p.gd_tag}/{p.name} at {at}";
            }

            // Logged with the positions, not just the count: when a player reports still hearing the
            // off-stage crowd, the first question is whether we found the right spot at all, and a
            // bare count cannot answer it.
            summary ??= "none found";
            if (summary != _lastResolvedSummary)
            {
                _lastResolvedSummary = summary;
                _log?.LogInfo($"[STOCK] Parking points: {summary}");
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[STOCK] Could not resolve parking points: {ex.Message}");
        }
    }

    /// <summary>
    /// Report — never hide — any spot where characters are standing on top of one another, with who
    /// they are and what GD point they think they are on. This exists purely so that a "the crowd is
    /// still there" report can be answered from the log instead of guessed at: if the pile turns out
    /// to be a parking spot the two tests above do not recognise, the line below says exactly which
    /// tag to add.
    ///
    /// Each distinct spot is logged once per session.
    /// </summary>
    internal static void NoteCharacters(List<(WorldGameObject Obj, Vector2 Pos)> characters)
    {
        if (characters == null || characters.Count < StackThreshold) return;

        try
        {
            _stackPos.Clear();
            _stackCount.Clear();
            _stackWho.Clear();

            foreach (var c in characters)
            {
                int found = -1;
                for (int i = 0; i < _stackPos.Count; i++)
                {
                    if ((_stackPos[i] - c.Pos).sqrMagnitude > StackRadiusSqr) continue;
                    found = i;
                    break;
                }

                string who = Describe(c.Obj);
                if (found >= 0)
                {
                    _stackCount[found]++;
                    if (_stackWho[found].Length < 200) _stackWho[found] += ", " + who;
                    continue;
                }
                _stackPos.Add(c.Pos);
                _stackCount.Add(1);
                _stackWho.Add(who);
            }

            for (int i = 0; i < _stackPos.Count; i++)
            {
                if (_stackCount[i] < StackThreshold) continue;
                if (IsParked(_stackPos[i])) continue;      // already a known parking point

                string key = $"{Mathf.RoundToInt(_stackPos[i].x)},{Mathf.RoundToInt(_stackPos[i].y)}";
                if (!_stacksReported.Add(key)) continue;

                _log?.LogWarning(
                    $"[STOCK] {_stackCount[i]} characters standing on the same spot {_stackPos[i]} " +
                    $"and NOT recognised as a parking point — {_stackWho[i]}");
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[STOCK] Pile-up check failed: {ex.Message}");
        }
    }

    private static string Describe(WorldGameObject obj)
    {
        try
        {
            if (obj == null) return "?";
            string gd = string.IsNullOrEmpty(obj.cur_gd_point) ? "-" : obj.cur_gd_point;
            return $"{obj.obj_id}[gd:{gd}]";
        }
        catch { return "?"; }
    }
}
