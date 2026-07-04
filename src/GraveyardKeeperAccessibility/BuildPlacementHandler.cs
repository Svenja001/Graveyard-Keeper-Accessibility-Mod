namespace GraveyardKeeperAccessibility;

/// <summary>
/// Makes the build-desk placement stage (the translucent "ghost" that normally follows the
/// mouse, left-click to place) usable without a mouse. While the game is in build
/// <c>Mode.Placing</c> we own the ghost: arrow keys nudge it in 32-unit steps, Enter places,
/// Escape cancels, R rotates, Space snaps to the nearest valid spot, and I reports where the
/// ghost sits relative to the player. After every move we read the game's own
/// <see cref="FloatingWorldGameObject.can_be_built"/> flag and announce valid/blocked.
///
/// We also drive the build desk's "Entfernen"/Remove stage (<c>Mode.Removing</c>), which is
/// normally a mouse cursor you hover over a building and left-click to demolish. Mouse-free we
/// present the zone's removable objects as a list: Up/Down cycle through them (the cursor snaps
/// to each and we announce its name, direction and removal state), Enter toggles "mark for
/// removal", Escape returns to the build menu.
///
/// The game's <c>BuildModeLogics.MoveObjectToMouse</c> would otherwise snap the ghost/cursor
/// back to the mouse every frame and fight our keyboard movement, so a Harmony prefix
/// (<see cref="Patches.MoveObjectToMouse_Prefix"/>) skips it while <see cref="Active"/> is set.
/// </summary>
internal static class BuildPlacementHandler
{
    private static ManualLogSource _log;
    private static bool _wasActive;

    // World units per tile, and the ghost's per-key nudge (matches FloatingWorldGameObject's
    // own 32-unit gamepad step, i.e. a third of a tile for fine positioning).
    private const float TileSize = 96f;
    private const float Step = 32f;

    // Reflection into BuildModeLogics' private placement internals (see decompiled source).
    private static FieldInfo _modeField;     // private enum Mode _mode
    private static FieldInfo _cdField;       // private ObjectCraftDefinition _cd
    private static FieldInfo _miField;       // private MultiInventory _multi_inventory (build-zone stock)
    private static MethodInfo _doPlace;      // private void DoPlace()
    private static MethodInfo _cancelPlacing; // private void CancelPlacing()
    private static MethodInfo _cancelRemoving; // private void CancelRemoving()

    // Remove-mode state: the zone's removable objects (sorted nearest-first) and our cursor in it.
    private static List<WorldGameObject> _removables;
    private static int _removeIndex;
    // Which sub-mode we were last in, so transitions read the right "left X" message.
    private static string _lastMode;

    // Wall-decoration placement: the WorldSubZone mount strips this object may sit on, plus the
    // GameObjects we temporarily switched on so the game's physics-based validity check can see
    // them (they ship inactive with zero-size colliders). Restored when placement ends.
    private static List<WorldSubZone> _wallZones;
    private static readonly List<GameObject> _tempActivated = new List<GameObject>();

    /// <summary>True only while we are driving the placement ghost or remove cursor (read by the Harmony prefix).</summary>
    internal static bool Active => _wasActive;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        try
        {
            var t = typeof(BuildModeLogics);
            _modeField = AccessTools.Field(t, "_mode");
            _cdField = AccessTools.Field(t, "_cd");
            _miField = AccessTools.Field(t, "_multi_inventory");
            _doPlace = AccessTools.Method(t, "DoPlace");
            _cancelPlacing = AccessTools.Method(t, "CancelPlacing");
            _cancelRemoving = AccessTools.Method(t, "CancelRemoving");
            _log?.LogInfo("[BUILD] BuildPlacementHandler initialized");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] Init failed: {ex.Message}");
        }
    }

    private static BuildModeLogics Logics => MainGame.me?.build_mode_logics;

    /// <summary>
    /// The build sub-mode we can drive: "Placing", "Removing", or null for anything else.
    /// Placing needs a live floating ghost; Removing uses the floating "_cursor".
    /// </summary>
    private static string CurrentMode()
    {
        try
        {
            var logics = Logics;
            if (logics == null || _modeField == null) return null;
            var mode = _modeField.GetValue(logics)?.ToString();
            if (mode == "Placing")
                return FloatingWorldGameObject.IsFloating() ? "Placing" : null;
            if (mode == "Removing")
                return "Removing";
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drive the placement ghost or remove cursor. Returns true when we are in a build sub-mode
    /// we own and have consumed this frame's input, so <see cref="Plugin"/> can skip the rest of
    /// its update (nav, menu reader) and let us own the keyboard.
    /// </summary>
    internal static bool Update()
    {
        var mode = CurrentMode();
        bool active = mode != null;

        if (active && !_wasActive)
        {
            _wasActive = true;
            AnnounceModeEntry(mode);
        }
        else if (active && _wasActive && mode != _lastMode)
        {
            // Switched between sub-modes without passing through None (rare); re-announce.
            AnnounceModeEntry(mode);
        }
        else if (!active && _wasActive)
        {
            // Left the sub-mode by a route other than our own keys (e.g. the game cancelled it).
            _wasActive = false;
            _removables = null;
            RestoreSubZones();
            ScreenReader.Say(_lastMode == "Removing" ? "Left removal" : "Left placement", interrupt: true);
        }

        _lastMode = mode;
        if (!active) return false;

        try
        {
            if (mode == "Removing") HandleRemoveInput();
            else HandleInput();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] build-mode input error: {ex.Message}");
        }
        return true;
    }

    private static void AnnounceModeEntry(string mode)
    {
        if (mode == "Removing") EnterRemoving();
        else AnnounceEntry();
    }

    private static void AnnounceEntry()
    {
        var name = CurrentBuildName();
        var what = string.IsNullOrEmpty(name) ? "Placement" : $"Placing {name}";

        // Wall decorations (Wandleuchter etc.) carry a sub_zone_id and can only sit on the wall
        // mount strips. Those strips ship as inactive GameObjects (zero-size colliders the game's
        // physics validity check can't see), so switch them on for the whole placement session —
        // otherwise no spot ever reads as buildable. Restored when placement ends.
        var subZoneId = CurrentSubZoneId();
        if (!string.IsNullOrEmpty(subZoneId))
        {
            _wallZones = CollectMatchingSubZones(subZoneId);
            EnsureSubZonesActive(_wallZones);
        }

        var snapHint = string.IsNullOrEmpty(subZoneId)
            ? "Space snaps to a free spot"
            : "This is a wall decoration, Space finds a spot on the wall";

        ScreenReader.Say(
            $"{what}. Arrow keys move, {snapHint}, R rotates, Enter places, Escape cancels. {Validity()}.",
            interrupt: true);
    }

    /// <summary>
    /// Switch on every GameObject in each matching sub-zone's parent chain that is currently off,
    /// so its trigger collider becomes visible to the game's physics-based build-validity checks.
    /// We remember exactly what we changed so <see cref="RestoreSubZones"/> can put it all back.
    /// </summary>
    private static void EnsureSubZonesActive(List<WorldSubZone> matching)
    {
        if (matching == null) return;
        foreach (var z in matching)
        {
            if (z == null) continue;
            try
            {
                // Collect the chain root→self, then activate top-down so activeInHierarchy resolves.
                var chain = new List<Transform>();
                for (var t = z.transform; t != null; t = t.parent) chain.Add(t);
                for (int k = chain.Count - 1; k >= 0; k--)
                {
                    var go = chain[k].gameObject;
                    if (!go.activeSelf)
                    {
                        go.SetActive(true);
                        _tempActivated.Add(go);
                        _log?.LogInfo($"[BUILD] activated sub-zone chain GO '{go.name}'");
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"[BUILD] EnsureSubZonesActive failed: {ex.Message}");
            }
        }
    }

    /// <summary>Undo every temporary activation done by <see cref="EnsureSubZonesActive"/>.</summary>
    private static void RestoreSubZones()
    {
        for (int k = _tempActivated.Count - 1; k >= 0; k--)
        {
            var go = _tempActivated[k];
            if (go != null)
            {
                try { go.SetActive(false); } catch { }
            }
        }
        _tempActivated.Clear();
        _wallZones = null;
    }

    private static void HandleInput()
    {
        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (Input.GetKeyDown(KeyCode.UpArrow)) { Move(Vector2.up); return; }
        if (Input.GetKeyDown(KeyCode.DownArrow)) { Move(Vector2.down); return; }
        if (Input.GetKeyDown(KeyCode.LeftArrow)) { Move(Vector2.left); return; }
        if (Input.GetKeyDown(KeyCode.RightArrow)) { Move(Vector2.right); return; }

        if (Input.GetKeyDown(KeyCode.Space)) { SnapToNearestValid(); return; }

        if (Input.GetKeyDown(KeyCode.R)) { Rotate(!shift); return; }

        if (Input.GetKeyDown(KeyCode.I)) { AnnouncePosition(); return; }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { Place(); return; }

        if (Input.GetKeyDown(KeyCode.Escape)) { Cancel(); return; }
    }

    private static void Move(Vector2 dir)
    {
        var before = FloatingWorldGameObject.cur_floating_pos;
        FloatingWorldGameObject.MoveCurrentByDir(dir);
        var after = FloatingWorldGameObject.cur_floating_pos;

        // MoveCurrentByDir refuses to step off-screen; tell the player instead of going silent.
        if ((after - before).sqrMagnitude < 1f)
        {
            ScreenReader.Say("Edge of view", interrupt: true);
            return;
        }
        ScreenReader.Say(Validity(), interrupt: true);
    }

    private static void Rotate(bool right)
    {
        if (!FloatingWorldGameObject.IsObjectRotatable())
        {
            ScreenReader.Say("Cannot rotate this", interrupt: true);
            return;
        }
        FloatingWorldGameObject.RotateCurrentFloatingObject(right);
        ScreenReader.Say($"Rotated {(right ? "right" : "left")}. {Validity()}", interrupt: true);
    }

    private static void Place()
    {
        var logics = Logics;
        if (logics == null) return;

        if (!FloatingWorldGameObject.can_be_built)
        {
            ScreenReader.Say("Blocked. Move to a free spot.", interrupt: true);
            return;
        }

        var cd = CurrentCraft();
        if (cd != null && !logics.CanBuild(cd))
        {
            var missing = MissingMaterialsText(cd);
            ScreenReader.Say(
                string.IsNullOrEmpty(missing)
                    ? "Not enough materials"
                    : $"Not enough materials. You still need {missing}",
                interrupt: true);
            return;
        }

        var name = CurrentBuildName();

        try
        {
            _doPlace?.Invoke(logics, null);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] DoPlace failed: {ex.Message}");
            ScreenReader.Say("Placement failed", interrupt: true);
            return;
        }

        ScreenReader.Say(string.IsNullOrEmpty(name) ? "Placed" : $"{name} placed", interrupt: true);
    }

    private static void Cancel()
    {
        var logics = Logics;
        RestoreSubZones();
        try
        {
            _cancelPlacing?.Invoke(logics, null);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] CancelPlacing failed: {ex.Message}");
        }
        ScreenReader.Say("Placement cancelled", interrupt: true);
    }

    // ---- remove mode ------------------------------------------------------

    /// <summary>
    /// Build the list of removable objects in the current build zone (the same set the game
    /// marks in <c>EnterRemoveMode</c>: every zone object with a removal craft), sorted
    /// nearest-first so cycling is predictable, and announce the first one plus the controls.
    /// </summary>
    private static void EnterRemoving()
    {
        BuildRemovableList();

        if (_removables == null || _removables.Count == 0)
        {
            ScreenReader.Say("Remove mode. Nothing here can be removed. Escape to go back.", interrupt: true);
            return;
        }

        int n = _removables.Count;
        var intro = $"Remove mode. {n} object{(n == 1 ? "" : "s")} can be removed. " +
                    "Up and Down choose, Enter marks for removal, Escape goes back. ";
        SelectRemovable(0, intro);
    }

    private static void BuildRemovableList()
    {
        _removables = new List<WorldGameObject>();
        _removeIndex = 0;
        try
        {
            var zone = Logics?.cur_build_zone;
            if (zone == null) return;

            foreach (var w in zone.GetZoneWGOs())
            {
                if (w != null && w.has_removal_craft)
                    _removables.Add(w);
            }

            var player = MainGame.me?.player;
            if (player != null)
            {
                var pp = player.pos;
                _removables.Sort((a, b) =>
                    (a.pos - pp).sqrMagnitude.CompareTo((b.pos - pp).sqrMagnitude));
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] BuildRemovableList failed: {ex.Message}");
        }
    }

    /// <summary>Select a removable by list index (wraps), snap the cursor to it, and announce it.</summary>
    private static void SelectRemovable(int index, string prefix = "")
    {
        if (_removables == null || _removables.Count == 0) return;

        // Drop any entries that vanished (e.g. removed since we built the list).
        _removables.RemoveAll(w => w == null);
        if (_removables.Count == 0)
        {
            ScreenReader.Say("Nothing left to remove. Escape to go back.", interrupt: true);
            return;
        }

        int n = _removables.Count;
        _removeIndex = ((index % n) + n) % n;
        var w = _removables[_removeIndex];

        // Snap the floating cursor onto the object so the game's own highlight follows us.
        try { FloatingWorldGameObject.MoveCurrentFloatingObject(w.pos, is_global_pos: true); }
        catch { }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(prefix)) parts.Add(prefix.TrimEnd());
        parts.Add(WgoName(w));
        var dir = DirectionFromPlayer(w.pos);
        if (!string.IsNullOrEmpty(dir)) parts.Add(dir);
        if (w.is_removing) parts.Add("Already marked for removal");
        parts.Add($"{_removeIndex + 1} of {n}");

        ScreenReader.Say(string.Join(". ", parts), interrupt: true);
    }

    private static void HandleRemoveInput()
    {
        if (_removables == null || _removables.Count == 0)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) CancelRemove();
            return;
        }

        if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.RightArrow)) { SelectRemovable(_removeIndex + 1); return; }
        if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.LeftArrow)) { SelectRemovable(_removeIndex - 1); return; }
        if (Input.GetKeyDown(KeyCode.I)) { AnnounceRemovablePosition(); return; }
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { ToggleRemoval(); return; }
        if (Input.GetKeyDown(KeyCode.Escape)) { CancelRemove(); return; }
    }

    /// <summary>
    /// Toggle the selected object's "mark for removal" flag (the same as the game's mouse click).
    /// Some objects — the translucent "_place" ghosts — are demolished outright by the game on
    /// mark, so we detect a destroyed object and drop it from the list.
    /// </summary>
    private static void ToggleRemoval()
    {
        if (_removables == null || _removeIndex >= _removables.Count) return;
        var w = _removables[_removeIndex];
        if (w == null) { SelectRemovable(_removeIndex); return; }

        var name = WgoName(w);
        try
        {
            w.MarkForRemoval();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] MarkForRemoval failed: {ex.Message}");
            ScreenReader.Say("Couldn't remove that", interrupt: true);
            return;
        }

        // MarkForRemoval may destroy the object immediately (Unity's overloaded == reports null).
        if (w == null)
        {
            _removables.RemoveAt(_removeIndex);
            ScreenReader.Say(
                _removables.Count == 0
                    ? $"{name} removed. Nothing left to remove. Escape to go back."
                    : $"{name} removed",
                interrupt: true);
            if (_removables.Count > 0) SelectRemovable(_removeIndex);
            return;
        }

        ScreenReader.Say(
            w.is_removing ? $"{name} marked for removal" : $"{name} removal cancelled",
            interrupt: true);
    }

    private static void AnnounceRemovablePosition()
    {
        if (_removables == null || _removeIndex >= _removables.Count) return;
        var w = _removables[_removeIndex];
        if (w == null) return;
        var dir = DirectionFromPlayer(w.pos);
        ScreenReader.Say(string.IsNullOrEmpty(dir) ? "On the player" : dir, interrupt: true);
    }

    private static void CancelRemove()
    {
        try
        {
            _cancelRemoving?.Invoke(Logics, null);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] CancelRemoving failed: {ex.Message}");
        }
        // Suppress the generic "Left removal" transition message; the build menu reopens and the
        // menu reader announces it.
        _wasActive = false;
        _lastMode = null;
        _removables = null;
        ScreenReader.Say("Removal cancelled", interrupt: true);
    }

    /// <summary>Localized name of a world object (strips the "_place" ghost suffix). Used both for
    /// removable objects and for naming whatever is blocking a placement spot.</summary>
    private static string WgoName(WorldGameObject w)
    {
        try
        {
            var objId = w?.obj_id;
            if (string.IsNullOrEmpty(objId)) return "Object";
            if (objId.EndsWith("_place"))
                objId = objId.Substring(0, objId.Length - "_place".Length);
            var name = InteractionDetector.LocalizedObjectName(objId);
            return string.IsNullOrWhiteSpace(name) ? "Object" : name;
        }
        catch
        {
            return "Object";
        }
    }

    /// <summary>
    /// Scan outward from the ghost's current spot in expanding 32-unit rings and stop the
    /// ghost on the first position the game reports as buildable. Mirrors the mod's
    /// "auto-walk, then nudge" philosophy: lands the player on a legal spot they can either
    /// confirm or fine-tune with the arrows.
    /// </summary>
    private static void SnapToNearestValid()
    {
        if (FloatingWorldGameObject.can_be_built)
        {
            ScreenReader.Say($"Already valid. {Validity()}", interrupt: true);
            return;
        }

        var subZoneId = CurrentSubZoneId();
        var origin = FloatingWorldGameObject.cur_floating_pos;

        Vector2 playerPos = origin;
        try
        {
            var player = MainGame.me?.player;
            if (player != null) playerPos = player.pos;
        }
        catch { }

        // Build the list of rectangles to sweep. For a wall/sub-zone object we target each matching
        // WorldSubZone collider directly (they're thin mount strips a coarse grid steps right past),
        // sweeping them finely. For a floor object we sweep the whole build-zone bounds.
        List<WorldSubZone> matching = null;
        if (!string.IsNullOrEmpty(subZoneId))
        {
            // Reuse the strips activated on entry; recollect+activate if we somehow got here first.
            matching = _wallZones ?? CollectMatchingSubZones(subZoneId);
            EnsureSubZonesActive(matching);
            _wallZones = matching;
        }
        var rects = new List<Bounds>();
        if (matching != null)
        {
            foreach (var z in matching)
            {
                if (z == null) continue;
                bool any = false;
                foreach (var col in z.GetComponentsInChildren<Collider2D>(includeInactive: true))
                {
                    if (col == null) continue;
                    var b = col.bounds;
                    if (b.size.sqrMagnitude < 1f) continue;
                    b.Expand(new Vector3(TileSize, TileSize, 0f)); // half-tile margin each side
                    rects.Add(b);
                    any = true;
                }
                if (!any) rects.Add(new Bounds(z.transform.position, new Vector3(2 * TileSize, 2 * TileSize, 0f)));
            }
        }

        // Fine step over the thin wall strips; coarse (one-cell) step over an open floor.
        float step = rects.Count > 0 ? 16f : Step;
        if (rects.Count == 0)
        {
            Bounds bounds = new Bounds(origin, new Vector3(16 * TileSize, 16 * TileSize, 0f));
            try
            {
                var zone = Logics?.cur_build_zone;
                if (zone != null)
                {
                    var zb = zone.GetBounds();
                    if (zb.size.sqrMagnitude > 1f) bounds = zb;
                }
            }
            catch { }
            rects.Add(bounds);
        }

        Vector2? best = null;
        float bestSqr = float.MaxValue;
        int tested = 0, valid = 0;
        const int maxSamples = 12000;   // safety cap

        foreach (var rect in rects)
        {
            for (float x = rect.min.x; x <= rect.max.x && tested < maxSamples; x += step)
            {
                for (float y = rect.min.y; y <= rect.max.y && tested < maxSamples; y += step)
                {
                    tested++;
                    var cand = new Vector2(x, y);
                    FloatingWorldGameObject.MoveCurrentFloatingObject(cand, is_global_pos: true);
                    if (!FloatingWorldGameObject.can_be_built) continue;

                    valid++;
                    float d = (cand - playerPos).sqrMagnitude;
                    if (d < bestSqr) { bestSqr = d; best = cand; }
                }
            }
        }

        if (best.HasValue)
        {
            FloatingWorldGameObject.MoveCurrentFloatingObject(best.Value, is_global_pos: true);
            var word = string.IsNullOrEmpty(subZoneId) ? "free spot" : "wall spot";
            ScreenReader.Say($"Found a {word}. {DirectionFromPlayer()}. Valid.", interrupt: true);
            return;
        }

        // Nothing valid. Restore the ghost, log the full picture (incl. per-zone details), and
        // speak a diagnosis so we can tell WHY without a log dive.
        FloatingWorldGameObject.MoveCurrentFloatingObject(origin, is_global_pos: true);
        ReportNoSpotDiagnostic(subZoneId, matching, tested, origin);
        FloatingWorldGameObject.MoveCurrentFloatingObject(origin, is_global_pos: true);
    }

    /// <summary>All WorldSubZone objects (active or not) whose sub_zone_id matches, i.e. the wall
    /// mount strips this decoration may sit on.</summary>
    private static List<WorldSubZone> CollectMatchingSubZones(string subZoneId)
    {
        var list = new List<WorldSubZone>();
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<WorldSubZone>(includeInactive: true);
            if (all != null)
                foreach (var z in all)
                    if (z != null && z.sub_zone_id == subZoneId)
                        list.Add(z);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] CollectMatchingSubZones failed: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// The sub-zone id the current build is restricted to (wall decorations set this), or null for
    /// ordinary floor objects. Prefer the live craft definition; fall back to the build grid's own
    /// active sub-zone.
    /// </summary>
    private static string CurrentSubZoneId()
    {
        try
        {
            var fromCraft = CurrentCraft()?.sub_zone_id;
            if (!string.IsNullOrEmpty(fromCraft)) return fromCraft;
            var fromGrid = BuildGrid.GetCurrentSubZoneID();
            return string.IsNullOrEmpty(fromGrid) ? null : fromGrid;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The sweep found no buildable cell. Log everything useful — including per-zone details for the
    /// matching wall strips (active state, collider bounds, and the game's can_be_built when the
    /// ghost is dropped on each zone's centre) — and speak a short diagnosis the user can relay.
    /// </summary>
    private static void ReportNoSpotDiagnostic(string subZoneId, List<WorldSubZone> matching, int tested, Vector3 origin)
    {
        string objId = null;
        try { objId = FloatingWorldGameObject.cur_floating?.wobj?.obj_id; } catch { }

        int matchCount = matching?.Count ?? 0;
        _log?.LogInfo(
            $"[BUILD] No valid spot. obj='{objId}' craftSubZone='{CurrentCraft()?.sub_zone_id}' " +
            $"gridSubZone='{SafeGridSubZone()}' buildZone='{Logics?.cur_build_zone_id}' " +
            $"tested={tested} subZonesMatch={matchCount}");

        int activeAtCenter = 0;
        if (matching != null)
        {
            int i = 0;
            foreach (var z in matching)
            {
                if (z == null || i >= 12) { i++; continue; }
                bool active = false; string colInfo = "none"; bool cbb = false; Vector3 pos = Vector3.zero;
                try
                {
                    active = z.gameObject.activeInHierarchy;
                    pos = z.transform.position;
                    var cols = z.GetComponentsInChildren<Collider2D>(includeInactive: true);
                    if (cols.Length > 0) colInfo = $"{cols.Length}col enabled={cols[0].enabled} bounds={cols[0].bounds.size}";
                    FloatingWorldGameObject.MoveCurrentFloatingObject(z.transform.position, is_global_pos: true);
                    cbb = FloatingWorldGameObject.can_be_built;
                    if (cbb) activeAtCenter++;
                }
                catch { }
                _log?.LogInfo($"[BUILD]  wallzone#{i} active={active} pos={pos} {colInfo} can_be_built@center={cbb}");
                i++;
            }
        }
        // Undo the probing moves.
        FloatingWorldGameObject.MoveCurrentFloatingObject(origin, is_global_pos: true);

        if (!string.IsNullOrEmpty(subZoneId))
        {
            ScreenReader.Say(
                matchCount == 0
                    ? "This is a wall object, but no matching wall zone exists here. The wall it needs may not be built."
                    : $"Found {matchCount} wall zone{(matchCount == 1 ? "" : "s")}, but no free spot on {(matchCount == 1 ? "it" : "them")}.",
                interrupt: true);
        }
        else
        {
            ScreenReader.Say("No buildable spot anywhere in this build area.", interrupt: true);
        }
    }

    private static string SafeGridSubZone()
    {
        try { return BuildGrid.GetCurrentSubZoneID(); }
        catch { return null; }
    }

    private static void AnnouncePosition()
    {
        ScreenReader.Say($"{DirectionFromPlayer()}. {Validity()}.", interrupt: true);
    }

    // ---- helpers ----------------------------------------------------------

    private static string Validity()
    {
        if (FloatingWorldGameObject.can_be_built) return "Valid";
        var blocker = BlockingObjectName();
        return string.IsNullOrEmpty(blocker) ? "Blocked" : $"Blocked by {blocker}";
    }

    /// <summary>
    /// Name of whatever object is occupying the ghost's footprint, or null when the spot is
    /// blocked for another reason (e.g. outside the build zone, or on impassable terrain).
    /// Mirrors <c>BuildGrid.IsCellBusy</c>: overlap-test each of the ghost's grid cells with the
    /// same layer mask (0/9/23), skip the ghost's own colliders and the colliders the game
    /// itself ignores, and report the first real world object found.
    /// </summary>
    private static string BlockingObjectName()
    {
        try
        {
            var floating = FloatingWorldGameObject.cur_floating;
            if (floating == null) return null;

            var cells = floating.gameObject.GetComponentsInChildren<FlowGridCell>();
            if (cells == null || cells.Length == 0) return null;

            const int mask = 8389121; // layers 0, 9, 23 — same as BuildGrid.IsCellBusy
            foreach (var cell in cells)
            {
                if (cell == null || cell.gameObject == null || !cell.gameObject.activeSelf) continue;
                if (cell.cell_type == FlowGridCell.CellType.TotemArea) continue;

                var hits = Physics2D.OverlapBoxAll(cell.transform.position, BuildGrid.GRID_CHECK_BOX_SIZE, 0f, mask);
                foreach (var hit in hits)
                {
                    if (hit == null) continue;
                    if (BuildGrid.SkipCollider(hit)) continue;
                    // The ghost overlaps its own colliders — ignore them.
                    if (hit.GetComponentInParent<FloatingWorldGameObject>() != null) continue;

                    var wgo = hit.GetComponentInParent<WorldGameObject>();
                    if (wgo == null) continue;
                    return WgoName(wgo);
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Spoken direction and distance of the placement ghost relative to the player, in tiles.</summary>
    private static string DirectionFromPlayer() => DirectionFromPlayer(FloatingWorldGameObject.cur_floating_pos);

    /// <summary>Spoken direction and distance of a world position relative to the player, in tiles.</summary>
    private static string DirectionFromPlayer(Vector2 target)
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null) return "";

            var delta = target - player.pos;
            var dx = delta.x / TileSize;
            var dy = delta.y / TileSize;

            var parts = new List<string>();
            if (Mathf.Abs(dy) >= 0.5f) parts.Add($"{Mathf.Abs(dy):F0} {(dy > 0 ? "up" : "down")}");
            if (Mathf.Abs(dx) >= 0.5f) parts.Add($"{Mathf.Abs(dx):F0} {(dx > 0 ? "right" : "left")}");

            return parts.Count == 0 ? "On the player" : string.Join(", ", parts);
        }
        catch
        {
            return "";
        }
    }

    private static ObjectCraftDefinition CurrentCraft()
    {
        try
        {
            return _cdField?.GetValue(Logics) as ObjectCraftDefinition;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Comma-separated list of the materials still missing for this build, with the shortfall
    /// amount each (e.g. "3 wood, 2 stone"). Materials are drawn from the build zone's own stock
    /// (the same <c>_multi_inventory</c> <see cref="BuildModeLogics.CanBuild"/> checks), so the
    /// count reflects what's actually deposited in the zone. Falls back to the full requirement
    /// list if the zone inventory can't be read.
    /// </summary>
    private static string MissingMaterialsText(CraftDefinition cd)
    {
        try
        {
            var needs = cd?.needs;
            if (needs == null || needs.Count == 0) return null;

            var stock = _miField?.GetValue(Logics) as MultiInventory;

            var parts = new List<string>();
            foreach (var need in needs)
            {
                if (need == null || string.IsNullOrEmpty(need.id)) continue;

                int have = stock != null ? stock.GetTotalCount(need.id) : 0;
                int shortfall = need.value - have;
                if (shortfall <= 0) continue;

                var iname = ScreenReader.StripNguiCodes(need.definition?.GetItemName() ?? need.id)?.Trim();
                if (string.IsNullOrWhiteSpace(iname)) iname = need.id;
                parts.Add(shortfall > 1 ? $"{shortfall} {iname}" : iname);
            }

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Localized name of the object currently being placed, derived from the craft's out_obj.</summary>
    private static string CurrentBuildName()
    {
        try
        {
            // Prefer the live ghost's own object id (always set); fall back to the craft def.
            var objId = FloatingWorldGameObject.cur_floating?.wobj?.obj_id;
            if (string.IsNullOrEmpty(objId))
                objId = CurrentCraft()?.out_obj;
            if (string.IsNullOrEmpty(objId)) return null;

            // Placement ghosts are sometimes the "<obj>_place" variant; the readable name lives
            // under the base id.
            if (objId.EndsWith("_place"))
                objId = objId.Substring(0, objId.Length - "_place".Length);

            return InteractionDetector.LocalizedObjectName(objId);
        }
        catch
        {
            return null;
        }
    }
}
