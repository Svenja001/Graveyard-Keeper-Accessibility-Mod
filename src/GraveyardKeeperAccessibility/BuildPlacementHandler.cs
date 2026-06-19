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
        ScreenReader.Say(
            $"{what}. Arrow keys move, Space snaps to a free spot, R rotates, Enter places, Escape cancels. {Validity()}.",
            interrupt: true);
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

        var origin = FloatingWorldGameObject.cur_floating_pos;
        const int maxRings = 25;   // ~8 tiles in every direction

        for (int ring = 1; ring <= maxRings; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            {
                for (int dy = -ring; dy <= ring; dy++)
                {
                    // Only the outer border of this ring (inner cells were tested already).
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;

                    var cand = new Vector2(origin.x + dx * Step, origin.y + dy * Step);
                    FloatingWorldGameObject.MoveCurrentFloatingObject(cand, is_global_pos: true);

                    if (FloatingWorldGameObject.can_be_built)
                    {
                        ScreenReader.Say($"Found a free spot. {DirectionFromPlayer()}. Valid.", interrupt: true);
                        return;
                    }
                }
            }
        }

        // Nothing within range — put the ghost back where it was.
        FloatingWorldGameObject.MoveCurrentFloatingObject(origin, is_global_pos: true);
        ScreenReader.Say("No free spot nearby. Try moving closer.", interrupt: true);
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
