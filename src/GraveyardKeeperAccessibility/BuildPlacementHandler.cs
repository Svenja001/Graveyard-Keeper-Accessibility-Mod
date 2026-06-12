namespace GraveyardKeeperAccessibility;

/// <summary>
/// Makes the build-desk placement stage (the translucent "ghost" that normally follows the
/// mouse, left-click to place) usable without a mouse. While the game is in build
/// <c>Mode.Placing</c> we own the ghost: arrow keys nudge it in 32-unit steps, Enter places,
/// Escape cancels, R rotates, Space snaps to the nearest valid spot, and I reports where the
/// ghost sits relative to the player. After every move we read the game's own
/// <see cref="FloatingWorldGameObject.can_be_built"/> flag and announce valid/blocked.
///
/// The game's <c>BuildModeLogics.MoveObjectToMouse</c> would otherwise snap the ghost back to
/// the cursor every frame and fight our keyboard movement, so a Harmony prefix
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
    private static MethodInfo _doPlace;      // private void DoPlace()
    private static MethodInfo _cancelPlacing; // private void CancelPlacing()

    /// <summary>True only while we are driving the placement ghost (read by the Harmony prefix).</summary>
    internal static bool Active => _wasActive;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        try
        {
            var t = typeof(BuildModeLogics);
            _modeField = AccessTools.Field(t, "_mode");
            _cdField = AccessTools.Field(t, "_cd");
            _doPlace = AccessTools.Method(t, "DoPlace");
            _cancelPlacing = AccessTools.Method(t, "CancelPlacing");
            _log?.LogInfo("[BUILD] BuildPlacementHandler initialized");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] Init failed: {ex.Message}");
        }
    }

    private static BuildModeLogics Logics => MainGame.me?.build_mode_logics;

    /// <summary>True when the game is in the placement sub-mode with a live ghost.</summary>
    private static bool IsPlacing()
    {
        try
        {
            if (!FloatingWorldGameObject.IsFloating()) return false;
            var logics = Logics;
            if (logics == null || _modeField == null) return false;
            return _modeField.GetValue(logics)?.ToString() == "Placing";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Drive the placement ghost. Returns true when we are in placement mode and have
    /// consumed this frame's input, so <see cref="Plugin"/> can skip the rest of its update
    /// (nav, menu reader) and let us own the keyboard.
    /// </summary>
    internal static bool Update()
    {
        bool placing = IsPlacing();

        if (placing && !_wasActive)
        {
            _wasActive = true;
            AnnounceEntry();
        }
        else if (!placing && _wasActive)
        {
            // Left placement by a route other than our own keys (e.g. the game cancelled it).
            _wasActive = false;
            ScreenReader.Say("Left placement", interrupt: true);
        }

        if (!placing) return false;

        try
        {
            HandleInput();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[BUILD] placement input error: {ex.Message}");
        }
        return true;
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
            ScreenReader.Say("Not enough materials", interrupt: true);
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

    private static string Validity() =>
        FloatingWorldGameObject.can_be_built ? "Valid" : "Blocked";

    /// <summary>Spoken direction and distance of the ghost relative to the player, in tiles.</summary>
    private static string DirectionFromPlayer()
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null) return "";

            Vector2 ghost = FloatingWorldGameObject.cur_floating_pos;
            var delta = ghost - player.pos;
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
