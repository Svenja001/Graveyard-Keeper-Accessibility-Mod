namespace GraveyardKeeperAccessibility;

/// <summary>
/// Makes the build-desk placement stage (the translucent "ghost" that normally follows the
/// mouse, left-click to place) usable without a mouse. While the game is in build
/// <c>Mode.Placing</c> we own the ghost: arrow keys nudge it in 32-unit steps, Enter places,
/// Escape cancels, R rotates, Space snaps to the nearest valid spot, and I reports where the
/// ghost sits relative to the player. After every move we read the game's own
/// <see cref="FloatingWorldGameObject.can_be_built"/> flag and announce valid/blocked.
///
/// We also drive the fixed-slot interior-furniture stage (<c>Mode.ScriptBuilding</c>): pieces like
/// the cupboard or cooking table have no floating ghost — a FlowScript drops them at a
/// predetermined spot in the room and waits for a confirming click. Mouse-free we expose Enter to
/// confirm, Escape to cancel (refunding materials), and R to cycle the piece's style variations.
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
    private static MethodInfo _removeMarks;   // private void RemoveMarksFromAllWGOs()

    // FloatingWorldGameObject's own live footprint list (private static List<FlowGridCell> _cells).
    // See FootprintCells for why we must read this instead of walking the ghost's children.
    private static FieldInfo _floatingCells;

    // Script-building (fixed-slot interior furniture) confirm/cancel/rotate hooks. These are
    // private static events on BuildModeLogics that the placement FlowScript subscribes to; we
    // invoke them to finalize or abort exactly as the game's own UpdateWhileScriptBuilding does.
    private static FieldInfo _applyEvent;     // on_apply_while_script_building
    private static FieldInfo _cancelEvent;    // on_cancel_while_script_building
    private static FieldInfo _rotLeftEvent;   // on_rotate_left_while_script_building
    private static FieldInfo _rotRightEvent;  // on_rotate_right_while_script_building

    // Remove-mode state: the zone's removable objects (sorted nearest-first) and our cursor in it.
    private static List<WorldGameObject> _removables;
    private static int _removeIndex;
    // Which sub-mode we were last in, so transitions read the right "left X" message.
    private static string _lastMode;

    // Wall-decoration placement: the WorldSubZone mount strips this object may sit on, plus the
    // GameObjects we temporarily switched on so the game's physics-based validity check can see
    // them (they ship inactive with zero-size colliders). Restored when placement ends.
    private static List<WorldSubZone> _wallZones;
    // GameObjects EnsureSubZonesActive toggled, with the activeSelf they had before, so the scene
    // is put back exactly as it was on cancel / on leaving placement.
    private static readonly List<KeyValuePair<GameObject, bool>> _tempToggled =
        new List<KeyValuePair<GameObject, bool>>();

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
            _removeMarks = AccessTools.Method(t, "RemoveMarksFromAllWGOs");
            _applyEvent = AccessTools.Field(t, "on_apply_while_script_building");
            _cancelEvent = AccessTools.Field(t, "on_cancel_while_script_building");
            _rotLeftEvent = AccessTools.Field(t, "on_rotate_left_while_script_building");
            _rotRightEvent = AccessTools.Field(t, "on_rotate_right_while_script_building");
            _floatingCells = AccessTools.Field(typeof(FloatingWorldGameObject), "_cells");
            _triedRemovalCraft = AccessTools.Field(typeof(WorldGameObject), "_tried_to_find_removal_craft");
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
            // Interior furniture (cupboard, cooking table…) placed at a fixed room slot by a
            // FlowScript; no floating ghost, just confirm/cancel/style.
            if (mode == "ScriptBuilding")
                return "ScriptBuilding";
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
            ScreenReader.Say(Loc.Get(_lastMode == "Removing" ? "remove.left_mode" : "build.left_mode"), interrupt: true);
        }

        _lastMode = mode;
        if (!active) return false;

        try
        {
            if (mode == "Removing") HandleRemoveInput();
            else if (mode == "ScriptBuilding") HandleScriptBuildInput();
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
        else if (mode == "ScriptBuilding") AnnounceScriptEntry();
        else AnnounceEntry();
    }

    private static void AnnounceEntry()
    {
        var name = CurrentBuildName();
        var what = string.IsNullOrEmpty(name) ? Loc.Get("build.placement") : Loc.Fmt("build.placing", name);

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
            ? Loc.Get("build.hint.snap_free")
            : Loc.Get("build.hint.snap_wall");

        ScreenReader.Say(
            Loc.Fmt("build.intro.free", what, snapHint, Validity(), PointsSuffix()),
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

        // Every transform on a root→zone path. Switching an ancestor on would otherwise light up ALL
        // of its children, and those ancestors are whole church-interior variants: turning on
        // 'church_inside_2' to reach one mount strip also turned on that variant's Walls/ colliders,
        // which then sat inside the live interior and occupied the very tiles we were trying to
        // build on. So anything off-path gets pruned before its parent goes live.
        var onPath = new HashSet<Transform>();
        foreach (var z in matching)
        {
            if (z == null) continue;
            for (var t = z.transform; t != null; t = t.parent) onPath.Add(t);
        }

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
                    var node = chain[k];
                    var go = node.gameObject;
                    if (go.activeSelf) continue;   // already live — leave its branch untouched

                    // Prune first, activate second: only the on-path child should come with it.
                    foreach (Transform child in node)
                    {
                        if (onPath.Contains(child) || !child.gameObject.activeSelf) continue;
                        _tempToggled.Add(new KeyValuePair<GameObject, bool>(child.gameObject, true));
                        child.gameObject.SetActive(false);
                    }

                    _tempToggled.Add(new KeyValuePair<GameObject, bool>(go, false));
                    go.SetActive(true);
                    _log?.LogInfo($"[BUILD] activated sub-zone chain GO '{go.name}' " +
                                  $"(pruned {node.childCount - 1} off-path child branch(es))");
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
        // Reverse order, so a branch we pruned is restored before the parent we switched on goes off.
        for (int k = _tempToggled.Count - 1; k >= 0; k--)
        {
            var go = _tempToggled[k].Key;
            if (go != null)
            {
                try { go.SetActive(_tempToggled[k].Value); } catch { }
            }
        }
        _tempToggled.Clear();
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
            ScreenReader.Say(Loc.Get("build.edge_of_view"), interrupt: true);
            return;
        }
        ScreenReader.Say(Validity(), interrupt: true);
    }

    private static void Rotate(bool right)
    {
        if (!FloatingWorldGameObject.IsObjectRotatable())
        {
            ScreenReader.Say(Loc.Get("build.cannot_rotate"), interrupt: true);
            return;
        }
        FloatingWorldGameObject.RotateCurrentFloatingObject(right);
        ScreenReader.Say(Loc.Fmt(right ? "build.rotated_right" : "build.rotated_left", Validity()), interrupt: true);
    }

    private static void Place()
    {
        var logics = Logics;
        if (logics == null) return;

        if (!FloatingWorldGameObject.can_be_built)
        {
            ScreenReader.Say(Loc.Get("build.blocked_move"), interrupt: true);
            return;
        }

        var cd = CurrentCraft();
        if (cd != null && !logics.CanBuild(cd))
        {
            var missing = MissingMaterialsText(cd);
            ScreenReader.Say(
                string.IsNullOrEmpty(missing)
                    ? Loc.Get("build.not_enough_materials")
                    : Loc.Fmt("build.not_enough_materials_missing", missing),
                interrupt: true);
            return;
        }

        var name = CurrentBuildName();

        try
        {
            // Tell the DoPlace postfix to stay quiet: we announce "X placed" ourselves below, so
            // the shared postfix (which exists for the game's own auto-placements) must not
            // double-announce for this player-driven placement.
            _manualPlaceInProgress = true;
            _doPlace?.Invoke(logics, null);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] DoPlace failed: {ex.Message}");
            ScreenReader.Say(Loc.Get("build.placement_failed"), interrupt: true);
            return;
        }
        finally
        {
            _manualPlaceInProgress = false;
        }

        ScreenReader.Say(string.IsNullOrEmpty(name) ? Loc.Get("build.placed") : Loc.Fmt("build.placed_named", name), interrupt: true);
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
        ScreenReader.Say(Loc.Get("build.placement_cancelled"), interrupt: true);
    }

    // ---- script-building (fixed-slot interior furniture) ------------------

    /// <summary>
    /// Announce a script-built piece (cupboard, cooking table, bed…). These don't float — the game
    /// spawns them at a predetermined spot in the room and waits for a click to confirm. So there's
    /// nothing to move; we just tell the player how to confirm, cancel, or (if the piece has
    /// alternative looks) change its style.
    /// </summary>
    private static void AnnounceScriptEntry()
    {
        var name = CurrentBuildName();
        var what = string.IsNullOrEmpty(name) ? Loc.Get("build.placement") : Loc.Fmt("build.placing", name);
        var cd = CurrentCraft();
        var style = (cd != null && cd.has_variations) ? " " + Loc.Get("build.style_hint") : "";
        ScreenReader.Say(
            Loc.Fmt("build.intro.fixed", what, style, PointsSuffix()),
            interrupt: true);
    }

    private static void HandleScriptBuildInput()
    {
        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { ConfirmScriptBuild(); return; }
        if (Input.GetKeyDown(KeyCode.Escape)) { CancelScriptBuild(); return; }
        if (Input.GetKeyDown(KeyCode.R)) { RotateScriptVariation(!shift); return; }
        if (Input.GetKeyDown(KeyCode.I)) { AnnounceScriptEntry(); return; }
    }

    /// <summary>Invoke a BuildModeLogics private-static script-building event (apply/cancel/rotate).</summary>
    private static void InvokeScriptEvent(FieldInfo f)
    {
        try { (f?.GetValue(null) as Action)?.Invoke(); }
        catch (Exception ex) { _log?.LogError($"[BUILD] script-build event invoke failed: {ex.Message}"); }
    }

    private static object ModeNone() => Enum.Parse(_modeField.FieldType, "None");

    /// <summary>
    /// Confirm the fixed-slot placement — mirrors the Interaction/LeftClick branch of
    /// <c>BuildModeLogics.UpdateWhileScriptBuilding</c>: clear the build zone, fire the apply event
    /// (the FlowScript that owns the piece finalizes it), hide the build GUIs and leave build mode.
    /// </summary>
    private static void ConfirmScriptBuild()
    {
        var logics = Logics;
        if (logics == null) return;
        var name = CurrentBuildName();

        try
        {
            logics.SetCurrentBuildZone(string.Empty);
            InvokeScriptEvent(_applyEvent);
            GUIElements.me.build_mode_gui.Hide();
            _modeField.SetValue(logics, ModeNone());
            GUIElements.me.craft.Hide();
            MainGame.me.ExitBuildMode();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] ConfirmScriptBuild failed: {ex.Message}");
            ScreenReader.Say(Loc.Get("build.placement_failed"), interrupt: true);
            return;
        }

        // Suppress the generic "Left placement" transition message on the next frame.
        _wasActive = false;
        _lastMode = null;
        ScreenReader.Say(string.IsNullOrEmpty(name) ? Loc.Get("build.placed") : Loc.Fmt("build.placed_named", name), interrupt: true);
    }

    /// <summary>
    /// Abort the fixed-slot placement — mirrors the Back/RightClick branch: refund the materials
    /// (they were consumed on entry), fire the cancel event, tear down build mode, and reopen the
    /// build desk catalog so the player can pick something else.
    /// </summary>
    private static void CancelScriptBuild()
    {
        var logics = Logics;
        if (logics == null) return;

        try
        {
            var cd = CurrentCraft();
            if (cd?.needs != null) MainGame.me.player.AddToInventory(cd.needs);
            logics.SetCurrentBuildZone(string.Empty);
            InvokeScriptEvent(_cancelEvent);
            GUIElements.me.build_mode_gui.Hide();
            MainGame.me.ExitBuildMode();
            GUIElements.me.craft.Hide();
            _modeField.SetValue(logics, ModeNone());
            _removeMarks?.Invoke(logics, null);
            MainGame.me.ExitBuildMode();
            MainGame.me.OpenBuildObjectGUI(BuildModeLogics.last_build_desk);
            logics.cur_build_zone?.RedrawQualities(false);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] CancelScriptBuild failed: {ex.Message}");
        }

        _wasActive = false;
        _lastMode = null;
        ScreenReader.Say(Loc.Get("build.placement_cancelled"), interrupt: true);
    }

    /// <summary>Cycle a script-built piece's alternative look (only some pieces have variations).</summary>
    private static void RotateScriptVariation(bool right)
    {
        var cd = CurrentCraft();
        if (cd == null || !cd.has_variations)
        {
            ScreenReader.Say(Loc.Get("build.no_other_styles"), interrupt: true);
            return;
        }
        InvokeScriptEvent(right ? _rotRightEvent : _rotLeftEvent);
        ScreenReader.Say(Loc.Get("build.changed_style"), interrupt: true);
    }

    // ---- removal crafts ---------------------------------------------------

    // obj_id -> its BuildType.Remove craft (or null). Pure balance data, so it never goes stale;
    // the desk and unlock filters below are re-applied live on every query.
    private static readonly Dictionary<string, ObjectCraftDefinition> _removeCraftByObj =
        new Dictionary<string, ObjectCraftDefinition>();

    // WorldGameObject._tried_to_find_removal_craft — the game's one-shot cache flag, reset in
    // RefreshRemovalCache. See HasRemovalCraft for why it has to be resettable.
    private static FieldInfo _triedRemovalCraft;

    /// <summary>
    /// Does the build desk define a demolish craft for this object? Answers the same question as
    /// <see cref="WorldGameObject.has_removal_craft"/>, but <em>never reads that property</em> —
    /// and no mod code may, outside build mode.
    ///
    /// The game's getter is a one-shot lazy cache: it sets <c>_tried_to_find_removal_craft</c>
    /// BEFORE looking the craft up, and the lookup
    /// (<c>BuildModeLogics.GetObjectRemoveCraftDefinition</c>) dereferences the static
    /// <c>BuildModeLogics.last_build_desk</c>, which stays null until the player opens their first
    /// build desk in a session. So an early read — our navigator categorises every scene object on
    /// each refresh, from the moment a save loads — throws a NullReferenceException inside the
    /// getter, leaves <c>_has_removal_craft</c> at its default false, and leaves the "already
    /// tried" flag set. The object is then permanently un-demolishable for the rest of the session,
    /// for the game's own <c>EnterRemoveMode</c> as much as for us: remove mode lists nothing and
    /// the desk looks like it defines no removals at all. The exception was invisible because every
    /// call site sat inside a catch-all.
    ///
    /// This mirrors the game's own filters (desk lock list, craft unlock) without the null
    /// dereference, and touches nothing on the WorldGameObject.
    /// </summary>
    internal static bool HasRemovalCraft(WorldGameObject wgo)
    {
        try
        {
            var id = wgo?.obj_id;
            if (string.IsNullOrEmpty(id)) return false;

            if (!_removeCraftByObj.TryGetValue(id, out var def))
            {
                def = null;
                foreach (var c in GameBalance.me.craft_obj_data)
                {
                    if (c == null || c.out_obj != id) continue;
                    if (c.build_type != ObjectCraftDefinition.BuildType.Remove) continue;
                    def = c;
                    break;
                }
                _removeCraftByObj[id] = def;
            }
            if (def == null) return false;

            // Only a desk that is NOT in the craft's lock list may demolish it. With no desk open
            // yet the game would have crashed here; we simply skip the filter, exactly as
            // GetObjectPutCraftDefinition does for its own null check.
            var desk = BuildModeLogics.last_build_desk;
            if (desk != null && def.locked_builders_ids != null &&
                def.locked_builders_ids.Contains(desk.obj_id)) return false;

            return !def.IsLocked();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] HasRemovalCraft failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clear the game's one-shot removal cache on a zone's objects so its own remove-mode highlight
    /// re-evaluates with a build desk in hand. Heals objects poisoned before <see
    /// cref="HasRemovalCraft"/> existed — the flag is a runtime field, never serialized, so this is
    /// enough and a restart would do the same.
    /// </summary>
    private static void RefreshRemovalCache(IEnumerable<WorldGameObject> wgos)
    {
        if (_triedRemovalCraft == null || wgos == null) return;
        foreach (var w in wgos)
        {
            if (w == null) continue;
            try { _triedRemovalCraft.SetValue(w, false); } catch { }
        }
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
            ScreenReader.Say(Loc.Get("remove.nothing_here"), interrupt: true);
            return;
        }

        int n = _removables.Count;
        var intro = Loc.Plural("remove.intro", n, n) + " ";
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

            var zoneWgos = zone.GetZoneWGOs();
            // The desk is open, so last_build_desk is set: let the game recompute its own flag now
            // that the lookup can succeed, keeping its highlight in step with our list.
            RefreshRemovalCache(zoneWgos);

            foreach (var w in zoneWgos)
            {
                if (w != null && HasRemovalCraft(w))
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
            ScreenReader.Say(Loc.Get("remove.nothing_left"), interrupt: true);
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
        if (w.is_removing) parts.Add(Loc.Get("remove.already_marked"));
        parts.Add(Loc.Fmt("common.x_of_y", _removeIndex + 1, n));

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
            ScreenReader.Say(Loc.Get("remove.failed"), interrupt: true);
            return;
        }

        // MarkForRemoval may destroy the object immediately (Unity's overloaded == reports null).
        if (w == null)
        {
            _removables.RemoveAt(_removeIndex);
            ScreenReader.Say(
                _removables.Count == 0
                    ? Loc.Fmt("remove.removed_last", name)
                    : Loc.Fmt("remove.removed", name),
                interrupt: true);
            if (_removables.Count > 0) SelectRemovable(_removeIndex);
            return;
        }

        ScreenReader.Say(
            Loc.Fmt(w.is_removing ? "remove.marked" : "remove.unmarked", name),
            interrupt: true);
    }

    private static void AnnounceRemovablePosition()
    {
        if (_removables == null || _removeIndex >= _removables.Count) return;
        var w = _removables[_removeIndex];
        if (w == null) return;
        var dir = DirectionFromPlayer(w.pos);
        ScreenReader.Say(string.IsNullOrEmpty(dir) ? Loc.Get("build.on_the_player") : dir, interrupt: true);
    }

    private static void CancelRemove()
    {
        // Escape is the game's only way out of remove mode (there is no separate "confirm"). The
        // game's CancelRemoving is misleadingly named: RemoveMarksFromAllWGOs only clears the
        // highlight from objects we did NOT mark — anything we set is_removing on stays queued for
        // demolition. So count the surviving marks BEFORE we invoke it, and report the truth
        // instead of "cancelled" (which wrongly implied our marks were undone).
        int marked = 0;
        if (_removables != null)
        {
            foreach (var w in _removables)
            {
                try { if (w != null && w.is_removing) marked++; }
                catch { }
            }
        }

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

        string msg = marked > 0
            ? (marked == 1
                ? Loc.Fmt("remove.left_marked.one", marked)
                : Loc.Fmt("remove.left_marked.other", marked))
            : Loc.Get("remove.left_nothing");
        ScreenReader.Say(msg, interrupt: true);
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

            // Name a resource node by its work group, as the rest of the mod does: "big tree" and
            // "small tree" are one technology apart, so lumping them together as "tree" would hide
            // that one of the two is the reason the yard cannot be cleared yet. Only where the game
            // has no name of its own, so a real translation still wins.
            if (!InteractionDetector.HasTranslation(objId))
            {
                var nodeName = DescriptiveNames.ForNode(w);
                if (!string.IsNullOrEmpty(nodeName)) return nodeName;
            }

            var name = InteractionDetector.LocalizedObjectName(objId);
            return string.IsNullOrWhiteSpace(name) ? "Object" : name;
        }
        catch
        {
            return "Object";
        }
    }

    /// <summary>
    /// A blocker's name with how to get rid of it — "Tree (chop down with the axe)" — or the bare
    /// name when nothing can be done about it.
    /// </summary>
    private static string WithRemovalHint(string name, WorldGameObject w)
    {
        var hint = RemovalHint(w);
        return string.IsNullOrEmpty(hint) ? name : Loc.Fmt("build.blocker.removable", name, hint);
    }

    /// <summary>
    /// What to say about a blocker beyond its name: how to clear it, or — when the work is behind a
    /// technology — which technology. Null when there is nothing useful to add.
    ///
    /// The two are kept apart on purpose: only the first is an instruction the player can act on
    /// now, and only the first may count towards a clearing plan.
    /// </summary>
    private static string BlockerNote(WorldGameObject w)
    {
        var hint = RemovalHint(w);
        if (!string.IsNullOrEmpty(hint)) return hint;

        try { return WorkUnlock.LockedNote(w?.obj_def); }
        catch { return null; }
    }

    /// <summary>
    /// How the player could clear this object out of a build spot, or null when they cannot.
    ///
    /// "Blocked by tree" leaves a blind player with no way to know whether that is a dead end or
    /// thirty seconds of work: a sighted player recognises a fellable tree, a mineable rock and an
    /// immovable cliff on sight, and a screen reader user has only the name. The answer is in the
    /// object's own definition, so it needs no table of ids:
    ///
    ///   a Remove craft at the build desk                = a built thing, demolish it;
    ///   a harvest tool_action AND something that drops  = a resource node, work it away;
    ///   neither                                         = terrain or a building, no.
    ///
    /// The demolish test comes first, and the harvest test insists on drop_items, because a
    /// crafting station carries a tool_action too — that is how you WORK at it (the oven, every
    /// workbench and every zombie desk answers to the Hand, the sawmill to the Axe). Going by the
    /// tool alone would tell the player to pick the oven up by hand. A node that yields nothing is
    /// not a node; 103 stations in the balance data match the tool test and not one of them drops
    /// anything, so the pair separates them cleanly.
    ///
    /// People are left out on purpose — <see cref="AnalyzeBestFit"/> already says they will move on
    /// their own, which is a different instruction from "clear it away".
    /// </summary>
    private static string RemovalHint(WorldGameObject w)
    {
        try
        {
            var def = w?.obj_def;
            if (def == null || IsCharacter(w)) return null;

            // Safe from anywhere — it reads the balance data, never the game's poisonable
            // has_removal_craft property. See HasRemovalCraft.
            if (HasRemovalCraft(w)) return Loc.Get("build.clear.demolish");

            if (def.drop_items == null || def.drop_items.Count == 0) return null;

            // A node whose work the player has not unlocked is not something they can clear today,
            // however good an axe they own — the game will refuse the swing. Telling them to fell it
            // would send them across the yard for a refusal, so it is reported as a blocker with the
            // technology named (see BlockerNote) and never offered as a plan.
            if (WorkUnlock.IsLocked(def)) return null;

            var tools = def.tool_actions;
            if (tools == null || tools.no_actions) return null;

            string how =
                tools.HasToolK(ItemDefinition.ItemType.Axe) ? Loc.Get("build.clear.chop") :
                tools.HasToolK(ItemDefinition.ItemType.Pickaxe) ? Loc.Get("build.clear.mine") :
                tools.HasToolK(ItemDefinition.ItemType.Shovel) ? Loc.Get("build.clear.dig") :
                tools.HasToolK(ItemDefinition.ItemType.Hand) ? Loc.Get("build.clear.gather") :
                null;
            if (how == null) return null;

            // Working a node does not always make the tile free: the game replaces some objects
            // rather than removing them, and a felled tree leaves a stump that goes on blocking the
            // build exactly as the tree did. Saying "chop it down" and stopping there sends the
            // player off to do the work and come back to the same refusal, so name what will still
            // be standing. Generic — it reads the definition's own replacement id, so it covers
            // whatever else in the game works that way, not just trees.
            var leftover = LeftoverName(w, def);
            return leftover == null ? how : Loc.Fmt("build.clear.leftover", how, leftover);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Where the ghost's occupied tiles sit relative to its transform, in world units. The game
    /// keeps this as <see cref="FloatingWorldGameObject.center_offsest"/> (in tiles) because an
    /// object's anchor is not its footprint: a wall decoration hangs well above its anchor, so its
    /// FlowGridCells — the cells the validity check actually tests — are metres away from
    /// transform.position. Sweeping raw transform positions therefore aims the wrong point at the
    /// wall strip and can miss a free mount entirely.
    /// </summary>
    private static Vector2 FootprintOffset()
    {
        try
        {
            var f = FloatingWorldGameObject.cur_floating;
            return f == null ? Vector2.zero : f.center_offsest * 96f;
        }
        catch { return Vector2.zero; }
    }

    /// <summary>Put the ghost's <em>footprint centre</em> (not its anchor) on <paramref name="center"/>.</summary>
    private static void MoveFootprintTo(Vector2 center)
        => FloatingWorldGameObject.MoveCurrentFloatingObject(center - FootprintOffset(), is_global_pos: true);

    /// <summary>
    /// The ghost's tile layout as "WxH span=(dx,dy)", measured from the live FlowGridCells. A cell
    /// count alone can't tell a 2-wide-by-3-tall decoration (which fits a narrow wall strip) from a
    /// 3-by-2 one (which never can), and that is exactly the question a "no spot" verdict turns on.
    /// </summary>
    private static string FootprintShape()
    {
        try
        {
            var floating = FloatingWorldGameObject.cur_floating;
            if (floating == null) return "none";
            var cells = FootprintCells(floating);
            if (cells == null || cells.Length == 0) return "none";

            var centre = (Vector2)floating.transform.position + FootprintOffset();
            var xs = new HashSet<int>();
            var ys = new HashSet<int>();
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var c in cells)
            {
                Vector2 d = (Vector2)c.transform.position - centre;
                xs.Add(Mathf.RoundToInt(d.x / Step));
                ys.Add(Mathf.RoundToInt(d.y / Step));
                minX = Mathf.Min(minX, d.x); maxX = Mathf.Max(maxX, d.x);
                minY = Mathf.Min(minY, d.y); maxY = Mathf.Max(maxY, d.y);
            }
            if (xs.Count == 0) return "none";
            return $"{xs.Count}x{ys.Count} span=({minX:0}..{maxX:0}, {minY:0}..{maxY:0})";
        }
        catch { return "err"; }
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
            ScreenReader.Say(Loc.Fmt("build.already_valid", Validity()), interrupt: true);
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
                    // Bounds.Expand adds HALF of what you pass to each side. A mount strip can be
                    // narrower than the decoration's own footprint, so the footprint centre may have
                    // to sit a fair way off the strip for the tiles to land on it — give a full tile
                    // of slack on each side.
                    b.Expand(new Vector3(2 * TileSize, 2 * TileSize, 0f));
                    rects.Add(b);
                    any = true;
                }
                if (!any) rects.Add(new Bounds(z.transform.position, new Vector3(2 * TileSize, 2 * TileSize, 0f)));
            }
        }

        bool wallSweep = rects.Count > 0;
        if (!wallSweep)
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

        // Placement doesn't snap to a grid (MoveWhenPlacingGlobalPos sets the position exactly), so a
        // large object's valid-anchor band can be only a few units wide — a coarse one-cell step walks
        // straight past it (this is why the pyre in the cramped cremation room reported "no spot"). Step
        // finely: 16u over thin wall strips; for open floor, adapt the step to the zone so a small room
        // is swept densely while a huge zone stays under the sample cap.
        const int maxSamples = 30000;   // safety cap
        float step;
        if (wallSweep)
        {
            step = 8f;
        }
        else
        {
            var b = rects[0];
            float w = Mathf.Max(b.size.x, 1f), h = Mathf.Max(b.size.y, 1f);
            // Aim for ~8000 samples: as fine as 8u in a small room, no coarser than the 32u cell grid.
            step = Mathf.Clamp(Mathf.Sqrt(w * h / 8000f), 8f, Step);
        }

        int tested = 0;

        // Sweep every rotation, not just every position. A rotatable object's variations are
        // different child sprites with different colliders, so the footprint changes shape with the
        // rotation — a mirrored decoration can fit against the west wall in the variant that fits
        // nowhere against the east one. Without this a "no spot" verdict is only true for whichever
        // variant the game happened to hand us.
        int startVariation = CurrentVariation();
        int rotations = 0;
        const int maxRotations = 8;

        while (true)
        {
            Vector2? best = null;
            float bestSqr = float.MaxValue;

            foreach (var rect in rects)
            {
                for (float x = rect.min.x; x <= rect.max.x && tested < maxSamples; x += step)
                {
                    for (float y = rect.min.y; y <= rect.max.y && tested < maxSamples; y += step)
                    {
                        tested++;
                        var cand = new Vector2(x, y);
                        // cand is where the FOOTPRINT should land, not where the anchor goes — see
                        // FootprintOffset. Rotating changes the footprint, so the offset is re-read
                        // every move rather than cached.
                        MoveFootprintTo(cand);
                        if (!FloatingWorldGameObject.can_be_built) continue;

                        float d = (cand - playerPos).sqrMagnitude;
                        if (d < bestSqr) { bestSqr = d; best = cand; }
                    }
                }
            }

            if (best.HasValue)
            {
                MoveFootprintTo(best.Value);
                var word = Loc.Get(string.IsNullOrEmpty(subZoneId) ? "build.spot.free" : "build.spot.wall");
                var turned = rotations > 0 ? " " + Loc.Get("build.rotated_to_fit") : "";
                _log?.LogInfo($"[BUILD] found spot at {best.Value} after {rotations} rotation(s), variation={CurrentVariation()}");
                ScreenReader.Say(Loc.Fmt("build.found_spot", word, turned, DirectionFromPlayer(), PointsSuffix()), interrupt: true);
                return;
            }

            if (!TryRotate() || ++rotations >= maxRotations || CurrentVariation() == startVariation)
                break;
            tested = 0; // each variation gets its own sample budget
            _log?.LogInfo($"[BUILD] no spot in variation {startVariation}+{rotations}; trying next rotation");
        }

        // Leave the ghost on the variation the player started with.
        for (int i = 0; i < maxRotations && CurrentVariation() != startVariation; i++)
            if (!TryRotate()) break;

        // Nothing valid. Log the footprint size + swept area/step so a repeat tells us whether the
        // object simply can't fit the zone (many cells) or the sweep was still too coarse.
        try
        {
            int cellCount = FloatingWorldGameObject.cur_floating?
                .gameObject.GetComponentsInChildren<FlowGridCell>()?.Length ?? -1;
            var sb = rects.Count > 0 ? rects[0] : new Bounds();
            _log?.LogInfo($"[BUILD] no-spot detail: footprintCells={cellCount} step={step} " +
                $"sweptBounds=center{sb.center}size{sb.size} rects={rects.Count} " +
                $"shape={FootprintShape()}");
        }
        catch { }

        // Restore the ghost, log the full picture (incl. per-zone details), and speak a diagnosis so
        // we can tell WHY without a log dive. The closest-fit pass runs for wall mounts too: "4 of 6
        // tiles taken by <thing>" is the difference between "this mount is occupied" and "this
        // decoration is too big for any mount", and the player can act on the first.
        FloatingWorldGameObject.MoveCurrentFloatingObject(origin, is_global_pos: true);
        var closest = AnalyzeBestFit(rects, subZoneId, playerPos, step);
        ReportNoSpotDiagnostic(subZoneId, matching, tested, origin, closest);
        FloatingWorldGameObject.MoveCurrentFloatingObject(origin, is_global_pos: true);
    }

    /// <summary>The ghost's current rotation variant, or -1 when it has none.</summary>
    private static int CurrentVariation()
    {
        try { return FloatingWorldGameObject.cur_floating?.wobj?.variation ?? -1; }
        catch { return -1; }
    }

    /// <summary>Turn the ghost to its next rotation variant. False when it doesn't rotate at all.</summary>
    private static bool TryRotate()
    {
        try
        {
            if (!FloatingWorldGameObject.IsObjectRotatable()) return false;
            FloatingWorldGameObject.RotateCurrentFloatingObject(true);
            return true;
        }
        catch
        {
            return false;
        }
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
    /// The ghost's live footprint: the cells the game itself tests in
    /// <see cref="FloatingWorldGameObject.RecalculateAvailability"/>, already filtered to the ones
    /// that count (active, not the totem-radius ring).
    ///
    /// Read from the game's private static <c>_cells</c> list rather than
    /// <c>GetComponentsInChildren&lt;FlowGridCell&gt;</c> on the ghost. Every rotation rebuilds the
    /// grid through <c>DrawFlowGrid</c>, which clears that list but disposes the old cell objects
    /// with <c>Object.Destroy</c> — deferred to the end of the frame. Our snap sweep tries up to
    /// eight rotations inside a single frame, so the ghost's children still hold every superseded
    /// generation, and walking them counted the same tile once per rotation attempted: a 36-tile
    /// table was reported as "180 of 180 tiles blocked" after five variations. The game's list holds
    /// only the live generation, so the counts we speak match the footprint the player is placing.
    /// </summary>
    private static FlowGridCell[] FootprintCells(FloatingWorldGameObject floating)
    {
        try
        {
            var live = _floatingCells?.GetValue(null) as List<FlowGridCell>;
            IEnumerable<FlowGridCell> source = live;
            // Reflection failed (game update renamed the field): fall back to the ghost's children.
            // The counts can then be inflated by stale generations, but a diagnosis is still better
            // than none — and the ranking is unaffected, since the copies sit on the same tiles.
            if (source == null)
                source = floating.gameObject.GetComponentsInChildren<FlowGridCell>();

            return source.Where(c => c != null && c.gameObject != null
                                     && c.gameObject.activeSelf
                                     && c.cell_type != FlowGridCell.CellType.TotemArea)
                         .ToArray();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] FootprintCells failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The sweep found no buildable cell. Log everything useful — including per-zone details for the
    /// matching wall strips (active state, collider bounds, and the game's can_be_built when the
    /// ghost is dropped on each zone's centre) — and speak a short diagnosis the user can relay.
    /// </summary>
    /// <summary>
    /// The object fits nowhere — so find where it comes CLOSEST and say what is in the way there.
    /// The game accepts a spot only when every one of the ghost's grid cells is both free
    /// (<see cref="BuildGrid.IsCellBusy"/>) and inside the build zone
    /// (<see cref="FlowGridCell.IsInsideWorldZone"/>), and those two failures need opposite fixes:
    /// something movable standing in the way (a worker, a dropped crate, the player) versus the
    /// object simply being bigger than the room. Sweeps coarsely — this only runs after the fine
    /// sweep already failed — and names the blockers at the single best position.
    /// Returns a spoken sentence, or null when nothing could be measured.
    /// </summary>
    private static string AnalyzeBestFit(List<Bounds> rects, string subZoneId, Vector2 playerPos, float step)
    {
        try
        {
            var floating = FloatingWorldGameObject.cur_floating;
            if (floating == null) return null;

            var cells = FootprintCells(floating);
            if (cells == null || cells.Length == 0) return null;

            var zoneId = Logics?.cur_build_zone_id;
            int counted = cells.Length;

            Vector2 best = playerPos;
            int bestBlocked = int.MaxValue, bestOutside = 0, bestBusy = 0;

            // Spots whose ONLY problem is that something is standing on them — the candidates for
            // "clear this and it fits". Collected during the sweep because a second sweep would
            // double the cost of a pass that already takes most of a second, and kept spread out
            // (see NoteClearCandidate) so the shortlist offers genuinely different places rather
            // than twenty samples of the same one.
            var candidates = new List<(Vector2 Pos, int Busy)>();

            // Wall strips are small enough to re-measure at the sweep's own resolution; an open floor
            // zone is not, so there we stay on the coarse cell grid.
            float probe = string.IsNullOrEmpty(subZoneId) ? Step : Mathf.Max(step, 4f);

            foreach (var rect in rects)
            {
                for (float x = rect.min.x; x <= rect.max.x; x += probe)
                {
                    for (float y = rect.min.y; y <= rect.max.y; y += probe)
                    {
                        var cand = new Vector2(x, y);
                        MoveFootprintTo(cand);
                        CountBlockedCells(cells, zoneId, subZoneId, out int outside, out int busy, null);

                        if (outside == 0 && busy > 0) NoteClearCandidate(candidates, cand, busy, playerPos);

                        int blocked = outside + busy;
                        if (blocked > bestBlocked) continue;
                        // Prefer fewer blocked cells; on a tie take the spot nearer the player.
                        if (blocked == bestBlocked && (cand - playerPos).sqrMagnitude >= (best - playerPos).sqrMagnitude)
                            continue;
                        bestBlocked = blocked;
                        bestOutside = outside;
                        bestBusy = busy;
                        best = cand;
                    }
                }
            }

            if (bestBlocked == int.MaxValue) return null;

            // Name what sits on the best spot.
            var names = new BlockerNames();
            MoveFootprintTo(best);
            CountBlockedCells(cells, zoneId, subZoneId, out _, out _, names, logDetail: true);
            var blockers = names.Spoken;
            var characters = names.Characters;

            _log?.LogInfo($"[BUILD] best fit at {best}: {bestBlocked}/{counted} cells blocked " +
                          $"(outside={bestOutside} busy={bestBusy}) blockers=[{string.Join(", ", blockers)}] " +
                          $"characters=[{string.Join(", ", characters)}] " +
                          $"clearable=[{string.Join(", ", names.Clearable)}] immovable={names.ImmovableCount}");

            // Tile-by-tile at that spot: which corner of the footprint fails tells us whether the
            // decoration overhangs the mount in one direction (nudgeable) or is boxed in.
            foreach (var c in cells)
            {
                if (c == null || c.gameObject == null || !c.gameObject.activeSelf) continue;
                if (c.cell_type == FlowGridCell.CellType.TotemArea) continue;
                Vector2 p = c.transform.position;
                _log?.LogInfo($"[BUILD]   tile rel=({p.x - best.x:0},{p.y - best.y:0}) " +
                              $"busy={BuildGrid.IsCellBusy(p)} inZone={c.IsInsideWorldZone(zoneId, subZoneId)}");
            }

            // Which shortlisted spots would actually open up if the player cleared what stands on
            // them, and what each would cost in work.
            var clearingPlans = FindClearingPlans(candidates, cells, zoneId, subZoneId, playerPos);

            bool wall = !string.IsNullOrEmpty(subZoneId);
            var area = Loc.Get(wall ? "build.area.wall_mount" : "build.area.build_area");

            var where = DirectionFromPlayer(best);
            var parts = new List<string> { Loc.Fmt("build.closest", where, bestBlocked, counted) };
            if (bestBusy > 0)
                parts.Add(blockers.Count > 0
                    ? Loc.Fmt("build.taken_by", bestBusy, string.Join(", ", blockers.Take(3)))
                    : Loc.Fmt("build.taken_by_something", bestBusy));
            if (bestOutside > 0)
                parts.Add(Loc.Fmt("build.outside_area", bestOutside, area));

            // Names of clearable objects the sentence has already mentioned, so the zone-wide list
            // at the end adds only things the player has not just been told about.
            var spokenClearables = new HashSet<string>(names.Clearable, StringComparer.Ordinal);

            var tail = "";
            // A person standing on the spot is the one blocker that clears itself — worth saying,
            // because the same build succeeds a minute later with no other change.
            if (characters.Count > 0 && bestOutside == 0)
                tail = " " + Loc.Fmt("build.tail.characters", string.Join(" " + Loc.Get("common.and") + " ", characters.Take(2)));
            else if (bestOutside > 0 && bestBusy == 0)
                tail = wall
                    ? " " + Loc.Get("build.tail.too_wide_mount")
                    : " " + Loc.Get("build.tail.too_wide_area");
            // Compared against the localized string, not the English one: that is what
            // CountBlockedCells put in the set, so testing the literal made this branch dead in
            // every language but English.
            else if (bestBusy > 0 && bestOutside == 0 && blockers.Count == 1 &&
                     blockers.Contains(Loc.Get("build.blocker.building_itself")))
                tail = " " + Loc.Get("build.tail.wall_structure");
            // Something is in the way, and it is something the player can get rid of. Spell out what
            // to clear and where the spot then opens up — and offer a second option when a different
            // set of objects would do it, since "demolish the sawbuck" and "mine two stones" are very
            // different amounts of work. Said before the "occupied" wording below, which reads like a
            // dead end.
            else if (bestBusy > 0 && bestOutside == 0 && clearingPlans.Count > 0)
                tail = DescribeClearingPlans(clearingPlans, best, names, spokenClearables);
            else if (bestBusy > 0 && bestOutside == 0 && names.Clearable.Count > 0)
                tail = " " + Loc.Fmt("build.tail.clear_first", string.Join(", ", names.Clearable.Take(2).ToArray()));
            else if (wall && bestBusy > 0 && bestOutside == 0)
                tail = " " + Loc.Get("build.tail.mount_occupied");

            // Finally: anything else in the zone that could go. Only when something is actually in
            // the way — when the object simply does not fit the area, clearing the yard cannot help.
            if (bestBusy > 0)
            {
                var elsewhere = ZoneClearables(playerPos, spokenClearables);
                if (elsewhere.Count > 0)
                    tail += " " + Loc.Fmt("build.clear_zone", string.Join(", ", elsewhere.ToArray()));
            }

            return string.Join(", ", parts) + "." + tail;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] AnalyzeBestFit failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The name of what this object leaves behind when it is worked to nothing (a tree leaves its
    /// stump), or null when it simply disappears.
    ///
    /// <c>after_hp_0</c> is the game's own "replace with this id" field, read through its
    /// <c>GetValue</c> the way <c>WorldGameObject.DoZeroHPActivity</c> reads it. An id we cannot
    /// resolve to a definition is treated as nothing left behind: this only ever adds a warning
    /// clause, so being silent is the safe way to be wrong.
    /// </summary>
    private static string LeftoverName(WorldGameObject w, ObjectDefinition def)
    {
        try
        {
            var id = def.after_hp_0?.GetValue(w, MainGame.me?.player);
            if (string.IsNullOrEmpty(id)) return null;
            if (GameBalance.me.GetDataOrNull<ObjectDefinition>(id) == null) return null;
            return InteractionDetector.LocalizedObjectName(id);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Everything worth knowing about what occupies a candidate spot. One object rather than four
    /// out-parameters, because the plan search below needs all four and the counting sweep needs
    /// none of them.
    /// </summary>
    private sealed class BlockerNames
    {
        /// <summary>Names as spoken, each with how to clear it where that is possible.</summary>
        internal readonly HashSet<string> Spoken = new HashSet<string>();
        /// <summary>People standing there — they move on their own, so they are not an obstacle to clear.</summary>
        internal readonly HashSet<string> Characters = new HashSet<string>();
        /// <summary>Plain names of what the player could work away.</summary>
        internal readonly HashSet<string> Clearable = new HashSet<string>();
        /// <summary>How many blockers there is nothing to be done about (terrain, buildings, people).</summary>
        internal int ImmovableCount;
    }

    /// <summary>
    /// How many spots to keep as candidates for a "clear this and it fits" suggestion.
    ///
    /// Generous on purpose. Whether a spot can be cleared is only known once its occupants have been
    /// named, which is far too expensive to do for every sample, so the shortlist is built on tile
    /// counts alone — and a spot held by terrain or by the building looks just as promising by that
    /// measure as one held by two stones. Keeping forty neighbourhoods means the clearable ones
    /// survive the ranking even when immovable spots score better on tiles. The plan pass that
    /// follows costs one naming pass each, against a sweep of several thousand samples.
    /// </summary>
    private const int MaxClearCandidates = 80;

    /// <summary>
    /// How far apart shortlisted candidates must be. Positions are sampled every few units, so
    /// without this the whole shortlist is one spot sampled eighty times over — and the point of the
    /// shortlist is to find a DIFFERENT place, blocked by different things.
    ///
    /// Half a tile, cut down from three. Three tiles sounded safely generous and threw away the one
    /// case this feature was built for: the spot blocked by bushes sat 64 units — two thirds of a
    /// tile — from the spot blocked by the sawbuck, so it was folded into it and never reported.
    /// Spots this close really are different spots when they are held by different things, and
    /// FindClearingPlans now removes the duplicates that a tight spread lets through by comparing
    /// what each plan asks the player to clear.
    /// </summary>
    private const float ClearCandidateSpread = 0.5f * TileSize;

    /// <summary>
    /// Offer a spot to the shortlist of "blocked, but only by things standing on it". Keeps the
    /// least-blocked candidate in each neighbourhood, nearest to the player on a tie, and evicts the
    /// worst entry once the list is full.
    /// </summary>
    private static void NoteClearCandidate(List<(Vector2 Pos, int Busy)> shortlist, Vector2 pos,
                                           int busy, Vector2 playerPos)
    {
        bool Better(int busyA, Vector2 posA, int busyB, Vector2 posB) =>
            busyA != busyB
                ? busyA < busyB
                : (posA - playerPos).sqrMagnitude < (posB - playerPos).sqrMagnitude;

        float spreadSqr = ClearCandidateSpread * ClearCandidateSpread;
        for (int i = 0; i < shortlist.Count; i++)
        {
            if ((shortlist[i].Pos - pos).sqrMagnitude > spreadSqr) continue;
            // Same neighbourhood: keep only the better of the two.
            if (Better(busy, pos, shortlist[i].Busy, shortlist[i].Pos)) shortlist[i] = (pos, busy);
            return;
        }

        if (shortlist.Count < MaxClearCandidates)
        {
            shortlist.Add((pos, busy));
            return;
        }

        int worst = 0;
        for (int i = 1; i < shortlist.Count; i++)
            if (Better(shortlist[worst].Busy, shortlist[worst].Pos, shortlist[i].Busy, shortlist[i].Pos))
                worst = i;
        if (Better(busy, pos, shortlist[worst].Busy, shortlist[worst].Pos)) shortlist[worst] = (pos, busy);
    }

    /// <summary>
    /// Of the shortlisted spots, which ones would actually become buildable if the player cleared
    /// what stands on them — cheapest first, counted in objects to get rid of rather than tiles.
    ///
    /// This is the question behind "can I make room here?", and the closest-fit report on its own
    /// answers a narrower one: it names what sits on the single spot with the fewest blocked tiles,
    /// which may well be the most expensive thing in the zone to shift. Demolishing the sawbuck
    /// needs a trip to the build desk and the materials back; two stones five tiles away need a
    /// pickaxe and give stone. The player can only weigh that up if both are said out loud.
    ///
    /// A spot counts only when EVERY blocker on it can go: one immovable collider — the building
    /// itself, terrain, a person who happens to be standing there — and clearing the rest changes
    /// nothing, so promising it would send the player off to work for a spot that still will not
    /// take the build.
    /// </summary>
    private static List<(Vector2 Pos, List<string> Objects, List<string> Plain)> FindClearingPlans(
        List<(Vector2 Pos, int Busy)> shortlist, FlowGridCell[] cells, string zoneId,
        string subZoneId, Vector2 playerPos)
    {
        var plans = new List<(Vector2 Pos, List<string> Objects, List<string> Plain)>();
        try
        {
            foreach (var cand in shortlist.OrderBy(c => c.Busy)
                                          .ThenBy(c => (c.Pos - playerPos).sqrMagnitude))
            {
                MoveFootprintTo(cand.Pos);
                var names = new BlockerNames();
                CountBlockedCells(cells, zoneId, subZoneId, out int outside, out _, names);

                if (outside != 0) continue;                  // also hangs off the zone edge
                if (names.ImmovableCount > 0) continue;      // something here cannot be shifted at all
                if (names.Clearable.Count == 0) continue;

                plans.Add((cand.Pos, names.Spoken.ToList(), names.Clearable.ToList()));
            }

            // Fewest things to clear wins; nearer to the player breaks the tie.
            plans = plans.OrderBy(p => p.Objects.Count)
                         .ThenBy(p => (p.Pos - playerPos).sqrMagnitude)
                         .ToList();

            // Two spots blocked by the same things are one option, however far apart they sit.
            // Position alone cannot dedupe them — the shortlist is spread over the zone precisely
            // so that different spots survive — so drop repeats by what they ask the player to do.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            plans = plans.Where(p => seen.Add(string.Join("|", p.Objects.OrderBy(o => o).ToArray())))
                         .ToList();

            foreach (var p in plans)
                _log?.LogInfo($"[BUILD] clearing plan at {p.Pos}: {string.Join(" + ", p.Objects.ToArray())}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] FindClearingPlans failed: {ex.Message}");
        }
        return plans;
    }

    /// <summary>
    /// Everything standing in the build zone that the player could clear away, grouped by name with
    /// how many there are: "5 bushes (dig up with the shovel), 2 stones (mine away with the
    /// pickaxe)".
    ///
    /// This exists because naming blockers spot by spot answers a narrower question than the player
    /// is asking, and a real session showed how narrowly. The oven reported "6 tiles taken by
    /// sawbuck"; the player cleared a few bushes on their own initiative and the same search then
    /// reported 2 tiles. The bushes were never at the closest-fit spot — they were two tiles south,
    /// blocking the better spot the sweep then found — and no per-spot report was ever going to
    /// mention them. Nor did the clearing-plan pass: candidates are held one per neighbourhood and
    /// those two spots are 64 units apart, so the bush spot was collapsed into the sawbuck spot and
    /// vanished from the shortlist.
    ///
    /// So say plainly what is IN the zone. It promises nothing about a particular spot — it answers
    /// "is there anything here I could get rid of", which is what a player standing in their own
    /// yard with nowhere to build actually wants to know, and the Trees / Bushes / Stones lists in
    /// the navigator (which now say what each one yields) are how they go and find them.
    ///
    /// The zone's own <c>GetZoneWGOs</c> is no use here: it only holds objects whose definition sets
    /// <c>can_belong_to_zone</c>, and trees, stones, mushrooms and most bushes do not — they do not
    /// count towards a zone's rating, so the game never files them under the zone. The mod's own
    /// registry sees every world object, so ask that and test zone membership with the zone's
    /// colliders, exactly as <c>WorldZone.DoesObjectBelongToZone</c> does.
    /// </summary>
    private static List<string> ZoneClearables(Vector2 playerPos, HashSet<string> alreadySaid)
    {
        var groups = new List<string>();
        try
        {
            var zone = Logics?.cur_build_zone;
            if (zone == null) return groups;

            var zoneCols = zone.GetComponentsInChildren<Collider2D>(includeInactive: true);
            if (zoneCols == null || zoneCols.Length == 0) return groups;

            var bounds = zone.GetBounds();
            var centre = (Vector2)bounds.center;
            // Enough to reach the far corner of the zone from its centre.
            float radius = new Vector2(bounds.size.x, bounds.size.y).magnitude * 0.5f + TileSize;

            var found = new List<WorldGameObject>();
            WorldObjectRegistry.CollectNear(centre, radius, null, found, requireActive: false);

            // name -> (how many, the spoken name with its note, where the nearest one is)
            var byName = new Dictionary<string, (int Count, string Text, Vector2 Nearest, float Dist)>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var w in found)
            {
                if (w == null || w.is_player) continue;

                // Locked nodes are listed too, with their technology named. "Three big trees you
                // cannot fell yet" is why the yard cannot be cleared, and a player who is not told
                // goes and tries anyway.
                var note = BlockerNote(w);
                if (string.IsNullOrEmpty(note)) continue;

                Vector2 p;
                try { p = w.pos; } catch { continue; }

                bool inZone = false;
                foreach (var col in zoneCols)
                {
                    if (col == null) continue;
                    try { if (col.OverlapPoint(p)) { inZone = true; break; } } catch { }
                }
                if (!inZone) continue;

                var name = WgoName(w);
                if (string.IsNullOrEmpty(name) || alreadySaid.Contains(name)) continue;

                float dist = (p - playerPos).magnitude;
                if (byName.TryGetValue(name, out var have))
                {
                    byName[name] = dist < have.Dist
                        ? (have.Count + 1, have.Text, p, dist)
                        : (have.Count + 1, have.Text, have.Nearest, have.Dist);
                }
                else
                {
                    byName[name] = (1, Loc.Fmt("build.blocker.removable", name, note), p, dist);
                    order.Add(name);
                }
            }

            _log?.LogInfo($"[BUILD] zone '{zone.id}' clearables: {order.Count} kind(s) out of " +
                          $"{found.Count} object(s) within {radius:0} units of {centre}");
            foreach (var name in order)
                _log?.LogInfo($"[BUILD] zone clearable: {byName[name].Count}x {name}");

            // Most numerous first: clearing five bushes frees the most ground, and it is also the
            // group the player is most likely to have walked past without knowing.
            foreach (var name in order.OrderByDescending(n => byName[n].Count).Take(3))
            {
                var entry = byName[name];
                var text = entry.Count > 1 ? Loc.Fmt("build.clear_group", entry.Count, entry.Text) : entry.Text;
                // Where to go. Without it the player is told five bushes exist and has to hunt the
                // yard for them; with it they can walk straight there (and the Bushes / Trees /
                // Stones lists in the navigator take them the rest of the way).
                groups.Add(Loc.Fmt("build.clear_where", text, DirectionFromPlayer(entry.Nearest)));
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] ZoneClearables failed: {ex.Message}");
        }
        return groups;
    }

    /// <summary>
    /// Speak the cheapest clearing plan, plus one genuinely different alternative when there is one.
    ///
    /// "Different" means it does not ask for the same objects: two plans that both come down to
    /// demolishing the same sawbuck are one option said twice, and the whole point of the second
    /// sentence is to offer a choice of work.
    ///
    /// When the cheapest plan is the closest-fit spot itself, its direction and its blockers have
    /// both just been spoken, so it gets the short wording rather than saying the sawbuck twice in
    /// one breath.
    /// </summary>
    private static string DescribeClearingPlans(List<(Vector2 Pos, List<string> Objects, List<string> Plain)> plans,
                                                Vector2 bestFit, BlockerNames bestNames,
                                                HashSet<string> spokenObjects)
    {
        if (plans.Count == 0) return "";

        var first = plans[0];
        bool isBestFit = (first.Pos - bestFit).sqrMagnitude <= TileSize * TileSize;

        var text = isBestFit
            ? " " + Loc.Fmt("build.tail.clear_first",
                            string.Join(", ", bestNames.Clearable.Take(2).ToArray()))
            : " " + Loc.Fmt("build.clear_plan",
                            string.Join(", ", first.Objects.Take(3).ToArray()),
                            DirectionFromPlayer(first.Pos));
        foreach (var plain in first.Plain) spokenObjects.Add(plain);

        foreach (var alt in plans.Skip(1))
        {
            if (alt.Objects.Any(o => first.Objects.Contains(o))) continue;
            text += " " + Loc.Fmt("build.clear_plan_alt",
                                  string.Join(", ", alt.Objects.Take(3).ToArray()),
                                  DirectionFromPlayer(alt.Pos));
            foreach (var plain in alt.Plain) spokenObjects.Add(plain);
            break;
        }

        return text;
    }

    /// <summary>
    /// Split the ghost's failing cells at its current position into "outside the build zone" and
    /// "occupied", mirroring <see cref="FloatingWorldGameObject.RecalculateAvailability"/>. When
    /// <paramref name="names"/> is given, the occupying objects are named into it.
    /// </summary>
    private static void CountBlockedCells(FlowGridCell[] cells, string zoneId, string subZoneId,
        out int outside, out int busy, BlockerNames names, bool logDetail = false)
    {
        outside = 0;
        busy = 0;
        const int mask = 8389121; // layers 0, 9, 23 — same as BuildGrid.IsCellBusy

        foreach (var cell in cells)
        {
            if (cell == null || cell.gameObject == null || !cell.gameObject.activeSelf) continue;
            if (cell.cell_type == FlowGridCell.CellType.TotemArea) continue;

            var pos = cell.transform.position;
            if (BuildGrid.IsCellBusy(pos))
            {
                busy++;
                if (names == null) continue;
                foreach (var hit in Physics2D.OverlapBoxAll(pos, BuildGrid.GRID_CHECK_BOX_SIZE, 0f, mask))
                {
                    if (hit == null || BuildGrid.SkipCollider(hit)) continue;
                    if (hit.GetComponentInParent<FloatingWorldGameObject>() != null) continue;
                    var wgo = hit.GetComponentInParent<WorldGameObject>();
                    if (wgo == null)
                    {
                        // A collider with no WorldGameObject still blocks: BuildGrid.SkipCollider
                        // bails out with "don't skip" when it can't attribute a collider to an
                        // object, so bare level geometry (wall pieces, door frames, stair rails)
                        // counts as occupied while having nothing to name. Log the hierarchy so it
                        // can be identified, and tell the player it's the building — not something
                        // they can move out of the way.
                        if (logDetail)
                            _log?.LogInfo($"[BUILD]   busy cell at {pos} blocked by unowned collider " +
                                      $"'{HierarchyPath(hit.transform)}' layer={hit.gameObject.layer} " +
                                      $"kind={hit.GetType().Name} bounds={hit.bounds.center}/{hit.bounds.size}");
                        names.Spoken.Add(Loc.Get("build.blocker.building_itself"));
                        names.ImmovableCount++;
                        continue;
                    }
                    var name = WgoName(wgo);
                    if (string.IsNullOrEmpty(name)) continue;
                    // Name it, and say what can be done about it — a tree in the way is a chore, a
                    // cliff in the way is a different spot, and a tree whose technology is missing is
                    // a chore that has to wait.
                    var hint = RemovalHint(wgo);
                    var note = BlockerNote(wgo);
                    names.Spoken.Add(string.IsNullOrEmpty(note)
                        ? name
                        : Loc.Fmt("build.blocker.removable", name, note));
                    if (string.IsNullOrEmpty(hint)) names.ImmovableCount++;
                    else names.Clearable.Add(name);
                    if (IsCharacter(wgo))
                        names.Characters.Add(wgo.is_player ? "You" : name);
                }
                continue;
            }

            if (!cell.IsInsideWorldZone(zoneId, subZoneId))
            {
                outside++;
                // Naming pass only: record where the offending tiles sit, so a log tells us whether
                // the object misses the zone on one edge (nudge it) or overhangs everywhere (too big).
                if (logDetail) _log?.LogInfo($"[BUILD]   outside-zone cell at {pos}");
            }
        }
    }

    /// <summary>Full scene path of a transform, for identifying colliders that own no game object.</summary>
    private static string HierarchyPath(Transform t)
    {
        var parts = new List<string>();
        for (var cur = t; cur != null && parts.Count < 8; cur = cur.parent) parts.Add(cur.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>A person (the player, an NPC, a mob, a zombie worker) rather than a fixed object.</summary>
    private static bool IsCharacter(WorldGameObject w)
    {
        try
        {
            if (w.is_player) return true;
            var t = w.obj_def?.type;
            return t == ObjectDefinition.ObjType.NPC || t == ObjectDefinition.ObjType.Mob;
        }
        catch
        {
            return false;
        }
    }

    private static void ReportNoSpotDiagnostic(string subZoneId, List<WorldSubZone> matching, int tested, Vector3 origin,
        string closest = null)
    {
        string objId = null;
        try { objId = FloatingWorldGameObject.cur_floating?.wobj?.obj_id; } catch { }

        int matchCount = matching?.Count ?? 0;
        _log?.LogInfo(
            $"[BUILD] No valid spot. obj='{objId}' craftSubZone='{CurrentCraft()?.sub_zone_id}' " +
            $"gridSubZone='{SafeGridSubZone()}' buildZone='{Logics?.cur_build_zone_id}' " +
            $"tested={tested} subZonesMatch={matchCount} footprintOffset={FootprintOffset()}");

        // Per-strip split of WHY it failed, measured with the footprint centred on the strip:
        // busy>0 = mount already occupied, outside>0 = the object's tiles hang off the strip.
        FlowGridCell[] ghostCells = null;
        try
        {
            var ghost = FloatingWorldGameObject.cur_floating;
            if (ghost != null) ghostCells = FootprintCells(ghost);
        }
        catch { }
        var zoneIdForCount = Logics?.cur_build_zone_id;

        if (matching != null)
        {
            int i = 0;
            foreach (var z in matching)
            {
                if (z == null || i >= 12) { i++; continue; }
                bool active = false; string colInfo = "none"; bool cbb = false; Vector3 pos = Vector3.zero;
                string fit = "";
                try
                {
                    active = z.gameObject.activeInHierarchy;
                    pos = z.transform.position;
                    // Probe the COLLIDER's centre, not the zone transform's — they need not coincide,
                    // and it's the collider the game's overlap test actually sees.
                    var probeAt = (Vector2)z.transform.position;
                    var cols = z.GetComponentsInChildren<Collider2D>(includeInactive: true);
                    if (cols.Length > 0)
                    {
                        colInfo = $"{cols.Length}col enabled={cols[0].enabled} " +
                                  $"colCenter={(Vector2)cols[0].bounds.center} bounds={cols[0].bounds.size}";
                        probeAt = cols[0].bounds.center;
                    }
                    MoveFootprintTo(probeAt);
                    cbb = FloatingWorldGameObject.can_be_built;
                    if (ghostCells != null && ghostCells.Length > 0)
                    {
                        CountBlockedCells(ghostCells, zoneIdForCount, subZoneId, out int outside, out int busy, null);
                        fit = $" outside={outside} busy={busy}";
                    }
                }
                catch { }
                _log?.LogInfo($"[BUILD]  wallzone#{i} active={active} pos={pos} {colInfo} can_be_built@center={cbb}{fit}");
                i++;
            }
        }
        // Undo the probing moves.
        FloatingWorldGameObject.MoveCurrentFloatingObject(origin, is_global_pos: true);

        if (!string.IsNullOrEmpty(subZoneId))
        {
            ScreenReader.Say(
                matchCount == 0
                    ? Loc.Get("build.no_wall_zone")
                    : string.IsNullOrEmpty(closest)
                        ? Loc.Get("build.no_wall_mount")
                        : Loc.Fmt("build.no_wall_mount_closest", closest),
                interrupt: true);
        }
        else
        {
            ScreenReader.Say(
                string.IsNullOrEmpty(closest)
                    ? Loc.Get("build.no_spot")
                    : Loc.Fmt("build.no_spot_closest", closest),
                interrupt: true);
        }
    }

    private static string SafeGridSubZone()
    {
        try { return BuildGrid.GetCurrentSubZoneID(); }
        catch { return null; }
    }

    private static void AnnouncePosition()
    {
        ScreenReader.Say(Loc.Fmt("build.position", DirectionFromPlayer(), Validity(), PointsSuffix()), interrupt: true);
    }

    /// <summary>
    /// Leading-space, sentence-terminated form of <see cref="PointsText"/> for appending to
    /// another spoken line, or "" when the object contributes no visible rating.
    /// </summary>
    private static string PointsSuffix()
    {
        var p = PointsText();
        return string.IsNullOrEmpty(p) ? "" : $" {p}.";
    }

    /// <summary>
    /// How many rating points the object being placed will add to its zone — the same number a
    /// sighted player sees floating over the ghost (e.g. the graveyard or church quality icon).
    /// Null when the object shows no number: its quality is Hidden, it isn't counted at the zone,
    /// its contribution rounds to zero, or it sits in an unscored zone (ordinary house furniture).
    /// </summary>
    private static string PointsText()
    {
        try
        {
            var w = FloatingWorldGameObject.cur_floating?.wobj;
            if (w == null || w.obj_def == null) return null;
            if (w.obj_def.quality_type == ObjectDefinition.QualityType.Hidden) return null;
            if (w.obj_def.ignore_counting_at_zone) return null;

            float q = w.quality;
            if (Mathf.Abs(q) < 0.05f) return null;

            var zone = ZoneLabelFor(w);
            var val = q.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return zone != null ? Loc.Fmt("build.points_zone", val, zone) : Loc.Fmt("build.points", val);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Friendly name of the scored zone the ghost currently sits in (graveyard, church, the alchemy
    /// cellar, the tavern…), or null when the zone isn't one the game rates — in which case the
    /// caller speaks a plain "Gives N points" or, for an unscored zone, nothing at all. We treat a
    /// zone as scored exactly as the game does: its definition's <c>calc_method</c> isn't None. The
    /// name is the same localized string the HUD banner uses (<c>GJL.L("zone_" + id)</c>).
    /// </summary>
    private static string ZoneLabelFor(WorldGameObject w)
    {
        try
        {
            var zone = w.GetMyWorldZone();
            if (zone?.definition == null) return null;
            if (zone.definition.calc_method == WorldZoneDefinition.QualityCalcMethod.None) return null;

            var key = "zone_" + zone.id;
            var loc = ScreenReader.StripNguiCodes(GJL.L(key) ?? "").Trim();
            return (string.IsNullOrEmpty(loc) || loc == key) ? null : loc;
        }
        catch
        {
            return null;
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static string Validity()
    {
        if (FloatingWorldGameObject.can_be_built) return Loc.Get("build.valid");
        var blocker = BlockingObjectName();
        return string.IsNullOrEmpty(blocker) ? Loc.Get("build.blocked") : Loc.Fmt("build.blocked_by", blocker);
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
                    // Say whether it can be cleared, not just what it is: "Blocked by tree" and
                    // "Blocked by cliff" call for completely different next moves.
                    return WithRemovalHint(WgoName(wgo), wgo);
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
            if (Mathf.Abs(dy) >= 0.5f) parts.Add(Loc.Fmt(dy > 0 ? "build.offset.up" : "build.offset.down", Mathf.Abs(dy).ToString("F0")));
            if (Mathf.Abs(dx) >= 0.5f) parts.Add(Loc.Fmt(dx > 0 ? "build.offset.right" : "build.offset.left", Mathf.Abs(dx).ToString("F0")));

            return parts.Count == 0 ? Loc.Get("build.on_the_player") : string.Join(", ", parts);
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
                iname += InventoryItemHandler.NeedQualitySuffix(need);
                parts.Add(shortfall > 1 ? Loc.Fmt("audit.material", shortfall, iname) : iname);
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

    // ---- generic build-commit announcement (DoPlace) ----------------------
    //
    // Set while our own Enter-confirm Place() is running so the DoPlace postfix it triggers
    // doesn't double up on the "X placed" we already say.
    private static bool _manualPlaceInProgress;
    // Captured in the DoPlace prefix and consumed by the postfix: whether the commit will really
    // place (same gate DoPlace itself uses) and the finished object's readable name.
    private static bool _doPlaceWillPlace;
    private static string _doPlaceName;

    /// <summary>
    /// DoPlace prefix hook. DoPlace bails early unless the spot is buildable and the zone has the
    /// materials, so we evaluate that same gate here (before the floating ghost is consumed) and
    /// stash the finished object's name for the postfix to announce.
    /// </summary>
    internal static void CaptureDoPlace()
    {
        _doPlaceWillPlace = false;
        _doPlaceName = null;
        try
        {
            var logics = Logics;
            if (logics == null) return;
            if (!FloatingWorldGameObject.can_be_built) return;
            var cd = CurrentCraft();
            if (cd == null || !logics.CanBuild(cd)) return;

            _doPlaceWillPlace = true;

            var objId = cd.out_obj;
            if (!string.IsNullOrEmpty(objId) && objId.EndsWith("_place"))
                objId = objId.Substring(0, objId.Length - "_place".Length);
            _doPlaceName = string.IsNullOrEmpty(objId) ? null : InteractionDetector.LocalizedObjectName(objId);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[BUILD] CaptureDoPlace failed: {ex.Message}");
        }
    }

    /// <summary>
    /// DoPlace postfix hook. Announces builds the game commits on its own — e.g. a quest building
    /// auto-placed at a fixed spot the instant it's picked from the catalog. Player-driven Enter
    /// placements are already announced by <see cref="Place"/>, so those are suppressed here.
    /// </summary>
    internal static void AnnounceDoPlace()
    {
        try
        {
            if (!_doPlaceWillPlace || _manualPlaceInProgress) return;
            ScreenReader.Say(string.IsNullOrEmpty(_doPlaceName) ? Loc.Get("build.built") : Loc.Fmt("build.built_named", _doPlaceName), interrupt: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[BUILD] AnnounceDoPlace failed: {ex.Message}");
        }
        finally
        {
            _doPlaceWillPlace = false;
            _doPlaceName = null;
        }
    }
}
