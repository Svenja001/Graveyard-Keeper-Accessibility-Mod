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
            var name = InteractionDetector.LocalizedObjectName(objId);
            return string.IsNullOrWhiteSpace(name) ? "Object" : name;
        }
        catch
        {
            return "Object";
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
                        CountBlockedCells(cells, zoneId, subZoneId, out int outside, out int busy, null, null);

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
            var blockers = new HashSet<string>();
            var characters = new HashSet<string>();
            MoveFootprintTo(best);
            CountBlockedCells(cells, zoneId, subZoneId, out _, out _, blockers, characters);

            _log?.LogInfo($"[BUILD] best fit at {best}: {bestBlocked}/{counted} cells blocked " +
                          $"(outside={bestOutside} busy={bestBusy}) blockers=[{string.Join(", ", blockers)}] " +
                          $"characters=[{string.Join(", ", characters)}]");

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

            var tail = "";
            // A person standing on the spot is the one blocker that clears itself — worth saying,
            // because the same build succeeds a minute later with no other change.
            if (characters.Count > 0 && bestOutside == 0)
                tail = " " + Loc.Fmt("build.tail.characters", string.Join(" " + Loc.Get("common.and") + " ", characters.Take(2)));
            else if (bestOutside > 0 && bestBusy == 0)
                tail = wall
                    ? " " + Loc.Get("build.tail.too_wide_mount")
                    : " " + Loc.Get("build.tail.too_wide_area");
            else if (bestBusy > 0 && bestOutside == 0 && blockers.Count == 1 && blockers.Contains("the building itself"))
                tail = " " + Loc.Get("build.tail.wall_structure");
            else if (wall && bestBusy > 0 && bestOutside == 0)
                tail = " " + Loc.Get("build.tail.mount_occupied");

            return string.Join(", ", parts) + "." + tail;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] AnalyzeBestFit failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Split the ghost's failing cells at its current position into "outside the build zone" and
    /// "occupied", mirroring <see cref="FloatingWorldGameObject.RecalculateAvailability"/>. When
    /// <paramref name="blockers"/> is given, the occupying objects are named into it.
    /// </summary>
    private static void CountBlockedCells(FlowGridCell[] cells, string zoneId, string subZoneId,
        out int outside, out int busy, HashSet<string> blockers, HashSet<string> characters)
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
                if (blockers == null) continue;
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
                        _log?.LogInfo($"[BUILD]   busy cell at {pos} blocked by unowned collider " +
                                      $"'{HierarchyPath(hit.transform)}' layer={hit.gameObject.layer} " +
                                      $"kind={hit.GetType().Name} bounds={hit.bounds.center}/{hit.bounds.size}");
                        blockers.Add(Loc.Get("build.blocker.building_itself"));
                        continue;
                    }
                    var name = WgoName(wgo);
                    if (string.IsNullOrEmpty(name)) continue;
                    blockers.Add(name);
                    if (characters != null && IsCharacter(wgo))
                        characters.Add(wgo.is_player ? "You" : name);
                }
                continue;
            }

            if (!cell.IsInsideWorldZone(zoneId, subZoneId))
            {
                outside++;
                // Naming pass only: record where the offending tiles sit, so a log tells us whether
                // the object misses the zone on one edge (nudge it) or overhangs everywhere (too big).
                if (blockers != null) _log?.LogInfo($"[BUILD]   outside-zone cell at {pos}");
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
                        CountBlockedCells(ghostCells, zoneIdForCount, subZoneId, out int outside, out int busy, null, null);
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
