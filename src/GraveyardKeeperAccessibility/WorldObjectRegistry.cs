namespace GraveyardKeeperAccessibility;

/// <summary>
/// A live index of every <see cref="WorldGameObject"/> in the scene, plus the per-object facts we
/// used to recompute from scratch on every frame.
///
/// WHY THIS EXISTS — the mod's per-frame scanners (proximity readout, combat assist, the nav
/// tracker) each need "every world object". They each called
/// <c>UnityEngine.Object.FindObjectsOfType&lt;WorldGameObject&gt;()</c> to get it, which is the
/// single most expensive call in the mod:
///
///   * it walks EVERY object of EVERY type in the scene natively, then
///   * allocates a fresh managed array of several thousand elements, every call.
///
/// Three callers did that every frame. On top of it, the filters they ran over the result read
/// <c>obj.name</c> (Unity's name getter marshals a BRAND NEW string out of native code on every
/// single access — <see cref="InteractionDetector.IsPrefab"/> alone read it three times) and
/// lower-cased <c>obj_id</c> for the DLC check. That is tens of thousands of short-lived string
/// allocations per frame, so Gen0 filled and collected several times a second — which is exactly
/// what a player feels as "it stutters while I walk".
///
/// Instead we keep the list ourselves. Harmony postfixes <c>WorldGameObject.Awake</c> to add and
/// prefixes <c>OnDestroy</c> to remove, so the list is maintained incrementally at zero per-frame
/// cost. The name/DLC facts are pure functions of fields that never change after the object is
/// initialised, so they're computed once per object and cached in a parallel byte array.
///
/// SAFETY: nothing here is allowed to be the reason an object goes missing from a readout — a
/// blind player navigating to something that isn't listed has no fallback. So the registry never
/// trusts itself blindly: <see cref="Tick"/> re-syncs from a real <c>FindObjectsOfType</c> sweep on
/// every scene change, on request, and periodically as a backstop. If the Harmony patches failed
/// outright the registry degrades to exactly the old behaviour (a full sweep), just throttled to
/// once every <see cref="FallbackResyncInterval"/> frames instead of three times per frame.
/// </summary>
internal static class WorldObjectRegistry
{
    // Per-object cached facts. All are pure functions of fields that don't change once the object
    // is initialised, so they survive for the object's lifetime.
    [Flags]
    private enum Flag : byte
    {
        None = 0,
        Computed = 1 << 0,  // the flags below have been filled in
        Excluded = 1 << 1,  // the player themselves, or a prefab/template shell — never announced
        DlcOk = 1 << 2,     // base-game content, or DLC content this player owns
    }

    private static ManualLogSource _log;

    // Parallel arrays rather than a list of small objects: the per-frame scanners walk these end to
    // end, so keeping the flags in a byte[] next to the reference keeps the whole sweep cache-warm
    // and allocation-free.
    private static readonly List<WorldGameObject> _objects = new(4096);
    private static readonly List<byte> _flags = new(4096);

    // instanceID -> index in the lists above, so OnDestroy removal is O(1) instead of a linear
    // scan. Removal swaps the last entry into the hole (order is meaningless to every caller).
    private static readonly Dictionary<int, int> _slotOf = new(4096);

    private static bool _patched;
    private static bool _resyncPending = true;
    private static string _resyncReason = "first tick";
    private static int _framesSinceResync;

    // With the patches live a re-sync is belt-and-braces, so it can be rare. Without them it IS the
    // registry, so it has to keep up with the world — but even then it's 1 sweep per 30 frames
    // rather than the 3 per frame we used to do.
    private const int VerifyResyncInterval = 1800;      // ~30s at 60fps, patched
    private const int FallbackResyncInterval = 30;      // ~0.5s at 60fps, unpatched

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>Called by <see cref="Plugin"/> once the Awake/OnDestroy patches are confirmed.</summary>
    internal static void MarkPatched()
    {
        _patched = true;
        _log?.LogInfo("[REGISTRY] Live WorldGameObject tracking enabled (Awake/OnDestroy patched)");
    }

    /// <summary>
    /// Objects currently known. Entries may be destroyed-but-not-yet-compacted, so callers must
    /// still null-check — every caller already did, because FindObjectsOfType could return
    /// half-torn-down objects too. Treat as read-only; never mutate.
    /// </summary>
    internal static List<WorldGameObject> Objects => _objects;

    /// <summary>Ask for a full re-sync on the next tick (scene change, teleport, dungeon load).</summary>
    internal static void RequestResync(string reason)
    {
        _resyncPending = true;
        _resyncReason = reason;
    }

    // ---- Maintenance -------------------------------------------------------

    /// <summary>Harmony postfix target: a new world object entered the scene.</summary>
    internal static void Register(WorldGameObject wgo)
    {
        if (wgo == null) return;
        int id = wgo.GetInstanceID();
        if (_slotOf.ContainsKey(id)) return;

        _slotOf[id] = _objects.Count;
        _objects.Add(wgo);
        _flags.Add((byte)Flag.None);
    }

    /// <summary>Harmony prefix target: a world object is being torn down.</summary>
    internal static void Unregister(WorldGameObject wgo)
    {
        if (wgo == null) return;
        RemoveAt(wgo.GetInstanceID());
    }

    private static void RemoveAt(int instanceId)
    {
        if (!_slotOf.TryGetValue(instanceId, out int slot)) return;
        _slotOf.Remove(instanceId);

        int last = _objects.Count - 1;
        if (slot != last)
        {
            // Move the tail entry into the hole and repoint its index.
            var moved = _objects[last];
            _objects[slot] = moved;
            _flags[slot] = _flags[last];
            if (moved != null) _slotOf[moved.GetInstanceID()] = slot;
        }
        _objects.RemoveAt(last);
        _flags.RemoveAt(last);
    }

    /// <summary>
    /// Housekeeping, once per frame from <see cref="Plugin"/>. Drops destroyed entries and runs a
    /// re-sync when one is due. Both are cheap: compaction only touches entries that actually died,
    /// and a re-sync happens on the order of once every 30 seconds while the patches are live.
    /// </summary>
    internal static void Tick()
    {
        try
        {
            _framesSinceResync++;

            int interval = _patched ? VerifyResyncInterval : FallbackResyncInterval;
            if (_resyncPending || _framesSinceResync >= interval)
            {
                Resync(_resyncPending ? _resyncReason : "periodic");
                _resyncPending = false;
                _resyncReason = null;
                _framesSinceResync = 0;
                return; // Resync already compacted.
            }

            Compact();
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[REGISTRY] Tick failed: {ex.Message}");
            // Never let a broken registry silently starve the readouts — force a clean rebuild.
            RequestResync("tick error");
        }
    }

    // Where the next incremental compaction slice starts.
    private static int _compactCursor;
    private const int CompactBudget = 512;

    /// <summary>
    /// Drop entries whose object has been destroyed without OnDestroy reaching us.
    ///
    /// Deliberately incremental. The OnDestroy prefix already removes objects the moment they go,
    /// so this is a backstop that almost never finds anything — and sweeping several thousand
    /// entries every frame just to confirm that would put back a slice of the per-frame cost this
    /// whole class exists to remove. A bounded slice per frame still walks the entire list every
    /// few frames, and consumers null-check regardless, so a stale entry is harmless in the
    /// meantime.
    /// </summary>
    private static void Compact()
    {
        int count = _objects.Count;
        if (count == 0) { _compactCursor = 0; return; }

        if (_compactCursor >= count) _compactCursor = 0;

        int budget = Mathf.Min(CompactBudget, count);
        int i = _compactCursor;

        while (budget-- > 0)
        {
            if (i >= _objects.Count) { i = 0; if (_objects.Count == 0) break; }

            if (_objects[i] != null) { i++; continue; }

            // Destroyed: the reference may be non-null while the Unity object is gone, so the
            // instance id is still readable and the dictionary entry still needs clearing. Find it
            // by slot rather than by id, since a truly null reference has no id to ask for.
            RemoveSlotByIndex(i);
            // Removal swaps the tail into this slot, so re-check the same index rather than
            // stepping past the element that just landed here.
        }

        _compactCursor = i;
    }

    private static void RemoveSlotByIndex(int slot)
    {
        // Clear whichever dictionary key maps here. The common case is that the (destroyed but
        // non-null) reference can still report its id; a genuinely null reference needs the
        // reverse lookup, which is why we only fall back to it when we must.
        var dead = _objects[slot];
        int deadId = 0;
        bool haveId = false;
        try
        {
            if (!ReferenceEquals(dead, null)) { deadId = dead.GetInstanceID(); haveId = true; }
        }
        catch { }

        if (haveId && _slotOf.TryGetValue(deadId, out int mapped) && mapped == slot)
        {
            RemoveAt(deadId);
            return;
        }

        // Reverse lookup fallback (rare).
        int foundKey = 0;
        bool found = false;
        foreach (var kv in _slotOf)
        {
            if (kv.Value != slot) continue;
            foundKey = kv.Key;
            found = true;
            break;
        }
        if (found) { RemoveAt(foundKey); return; }

        // Not in the map at all — excise the slot directly to keep the arrays consistent.
        int last = _objects.Count - 1;
        if (slot != last)
        {
            var moved = _objects[last];
            _objects[slot] = moved;
            _flags[slot] = _flags[last];
            if (moved != null) _slotOf[moved.GetInstanceID()] = slot;
        }
        _objects.RemoveAt(last);
        _flags.RemoveAt(last);
    }

    /// <summary>
    /// Rebuild from a real scene sweep. This is the one place the expensive call still lives, and
    /// it is also the safety net that makes the whole registry trustworthy: whatever the patches
    /// missed, this puts back. Cached flags are preserved for objects we already knew, so a re-sync
    /// costs a sweep, not a recompute of every name and DLC check.
    /// </summary>
    private static void Resync(string reason)
    {
        WorldGameObject[] found;
        try
        {
            found = UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[REGISTRY] Resync sweep failed: {ex.Message}");
            return;
        }
        if (found == null) return;

        int before = _objects.Count;

        // Carry the already-computed flags across by instance id, so a re-sync doesn't throw away
        // the caching work that is the whole point of this class.
        var keptFlags = new Dictionary<int, byte>(_objects.Count);
        for (int i = 0; i < _objects.Count; i++)
        {
            var obj = _objects[i];
            if (obj == null) continue;
            if (((Flag)_flags[i] & Flag.Computed) == 0) continue;
            keptFlags[obj.GetInstanceID()] = _flags[i];
        }

        _objects.Clear();
        _flags.Clear();
        _slotOf.Clear();

        foreach (var obj in found)
        {
            if (obj == null) continue;
            int id = obj.GetInstanceID();
            if (_slotOf.ContainsKey(id)) continue;
            _slotOf[id] = _objects.Count;
            _objects.Add(obj);
            _flags.Add(keptFlags.TryGetValue(id, out var f) ? f : (byte)Flag.None);
        }

        // Only interesting when it actually changes something, otherwise this would spam the log
        // every 30 seconds for the rest of the session.
        int delta = _objects.Count - before;
        if (!_patched || Mathf.Abs(delta) > 0)
            _log?.LogInfo($"[REGISTRY] Resync ({reason}): {_objects.Count} objects ({delta:+#;-#;0})");
    }

    // ---- Cached per-object facts -------------------------------------------

    /// <summary>
    /// True for objects no readout ever wants: the player themselves, and prefab/template shells.
    /// Same predicate as before (<see cref="InteractionDetector.IsPlayer"/> /
    /// <see cref="InteractionDetector.IsPrefab"/>) — but the four <c>obj.name</c> reads behind it
    /// now happen once in the object's life instead of once per object per frame.
    /// </summary>
    internal static bool IsExcluded(WorldGameObject wgo, int slot)
    {
        return (EnsureFlags(wgo, slot) & Flag.Excluded) != 0;
    }

    /// <summary>
    /// <see cref="IsExcluded(WorldGameObject,int)"/> for callers that don't hold a slot index —
    /// e.g. anything walking a snapshot rather than the live list. Costs one dictionary lookup
    /// instead of the four <c>obj.name</c> string allocations the old predicates cost.
    /// </summary>
    internal static bool IsExcluded(WorldGameObject wgo)
    {
        return (EnsureFlags(wgo, SlotOf(wgo)) & Flag.Excluded) != 0;
    }

    /// <summary>Slot-free <see cref="IsDlcAvailable(WorldGameObject,int)"/>; see above.</summary>
    internal static bool IsDlcAvailable(WorldGameObject wgo)
    {
        return (EnsureFlags(wgo, SlotOf(wgo)) & Flag.DlcOk) != 0;
    }

    private static int SlotOf(WorldGameObject wgo)
    {
        if (wgo == null) return -1;
        try
        {
            return _slotOf.TryGetValue(wgo.GetInstanceID(), out int slot) ? slot : -1;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Copy the live list into a caller-owned buffer.
    ///
    /// Callers that do heavy per-object work (the nav tracker's rebuild) must iterate a snapshot,
    /// not the live list: labelling an object can spawn or destroy one, which would mutate the
    /// list mid-walk. The copy is a single memcpy-class operation once every refresh — nothing next
    /// to the full-scene sweep it replaces.
    /// </summary>
    internal static void Snapshot(List<WorldGameObject> into)
    {
        into.Clear();
        var objects = _objects;
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            if (obj != null) into.Add(obj);
        }
    }

    /// <summary>
    /// True if this object may be announced — base-game content, or DLC content the player owns.
    /// Wraps <see cref="ObjectNavigator.IsObjectDlcAvailable"/>, whose <c>ToLowerInvariant()</c> +
    /// substring sweep is far too expensive to repeat per frame.
    /// </summary>
    internal static bool IsDlcAvailable(WorldGameObject wgo, int slot)
    {
        return (EnsureFlags(wgo, slot) & Flag.DlcOk) != 0;
    }

    private static Flag EnsureFlags(WorldGameObject wgo, int slot)
    {
        // slot < 0 means "not in the registry" (an object mid-teardown, or one created since the
        // last sync). It still gets a correct answer, just an uncached one.
        if (slot >= 0 && slot < _flags.Count)
        {
            var cached = (Flag)_flags[slot];
            if ((cached & Flag.Computed) != 0) return cached;
        }

        var computed = ComputeFlags(wgo, out bool stable);

        // Don't cache while the object is still half-initialised: a WorldGameObject exists for a
        // moment before its obj_id is assigned, and latching "no id, so base game" then would
        // permanently mislabel DLC content. Recompute next frame instead — this affects a handful
        // of freshly spawned objects, never the steady state.
        if (stable && slot >= 0 && slot < _flags.Count)
            _flags[slot] = (byte)(computed | Flag.Computed);

        return computed;
    }

    private static Flag ComputeFlags(WorldGameObject wgo, out bool stable)
    {
        stable = false;
        var result = Flag.None;
        if (wgo == null) return result;

        try
        {
            // One name read for both tests, instead of the four the old predicates cost.
            var name = wgo.name;
            if (!string.IsNullOrEmpty(name)
                && (name.Contains("Player")
                    || name.Contains("prefab") || name.Contains("Prefab") || name.Contains("template")))
                result |= Flag.Excluded;

            if (ObjectNavigator.IsObjectDlcAvailable(wgo))
                result |= Flag.DlcOk;

            // obj_id is what the DLC test keys off; once it's set the answer can't change.
            stable = !string.IsNullOrEmpty(wgo.obj_id);
        }
        catch
        {
            // Mirror the old fail-open behaviour: an object we can't classify stays announceable.
            result |= Flag.DlcOk;
        }

        return result;
    }

    // ---- Queries -----------------------------------------------------------

    /// <summary>
    /// Nearest object to <paramref name="origin"/> within <paramref name="maxDistance"/> that
    /// passes <paramref name="filter"/>, or null.
    ///
    /// Replaces the LINQ <c>Where(...).OrderBy(Distance).FirstOrDefault()</c> the proximity readout
    /// used to run every frame, which sorted the entire scene to look at one element. This is a
    /// single pass, compares squared distances (no per-object <c>sqrt</c>), tests the cheap
    /// distance gate before anything that touches native Unity state, and allocates nothing.
    /// </summary>
    internal static WorldGameObject Nearest(Vector2 origin, float maxDistance, Func<WorldGameObject, bool> filter)
    {
        WorldGameObject best = null;
        float bestSqr = maxDistance * maxDistance;

        var objects = _objects;
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            if (obj == null) continue;

            // Cheapest first, and in this order deliberately: is_removed and pos are plain field
            // reads (pos returns WorldGameObject's own cached position), while activeInHierarchy
            // and every obj_def lookup cross into native code. Distance rejects the overwhelming
            // majority of the scene, so everything costly runs on a handful of objects.
            if (obj.is_removed) continue;

            Vector2 p;
            try { p = obj.pos; } catch { continue; }

            float dx = p.x - origin.x, dy = p.y - origin.y;
            float sqr = dx * dx + dy * dy;
            if (sqr > bestSqr) continue;

            if (IsExcluded(obj, i)) continue;
            if (!IsDlcAvailable(obj, i)) continue;

            try
            {
                if (!obj.gameObject.activeInHierarchy) continue;
                if (filter != null && !filter(obj)) continue;
            }
            catch { continue; }

            bestSqr = sqr;
            best = obj;
        }

        return best;
    }

    /// <summary>
    /// Collect every object within <paramref name="maxDistance"/> passing <paramref name="filter"/>
    /// into <paramref name="into"/>. The caller owns and reuses the list, so a per-frame scan does
    /// no allocation at all. Same cheap-gate ordering as <see cref="Nearest"/>.
    /// </summary>
    internal static void CollectNear(Vector2 origin, float maxDistance,
        Func<WorldGameObject, bool> filter, List<WorldGameObject> into,
        bool requireActive = true, bool applyDlcFilter = true, bool applyExclusions = true)
    {
        into.Clear();
        float maxSqr = maxDistance * maxDistance;

        var objects = _objects;
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            if (obj == null) continue;
            if (obj.is_removed) continue;

            Vector2 p;
            try { p = obj.pos; } catch { continue; }

            float dx = p.x - origin.x, dy = p.y - origin.y;
            if (dx * dx + dy * dy > maxSqr) continue;

            if (applyExclusions && IsExcluded(obj, i)) continue;
            if (applyDlcFilter && !IsDlcAvailable(obj, i)) continue;

            try
            {
                if (requireActive && !obj.gameObject.activeInHierarchy) continue;
                if (filter != null && !filter(obj)) continue;
            }
            catch { continue; }

            into.Add(obj);
        }
    }
}
