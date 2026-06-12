namespace GraveyardKeeperAccessibility;

internal struct NavigationTarget
{
    internal WorldGameObject Object;
    internal string Label;
    internal float Distance;   // world units (96 per tile)
    internal Vector2 Position;  // canonical x-y world position (z is render depth)

    // Ground drops (DeadBody/loot) are DropResGameObjects, not WorldGameObjects, so Object
    // is null for them. They sit on walkable ground, so we walk onto the exact tile (no
    // approach offset) to land inside the game's pickup/highlight area.
    internal bool IsDrop;
    internal GameObject DropGo;  // the drop's GameObject (for selection-identity tracking)
}

/// <summary>
/// Categories of navigable points of interest. Ordered for cycling with
/// Ctrl+PageUp / Ctrl+PageDown.
/// </summary>
internal enum NavCategory
{
    Quests,
    Items,
    Doors,
    Graves,
    People,
    Storage,
    Stations,
    Other
}

internal static class ObjectNavigator
{
    private static ManualLogSource _log;
    private static bool _initialized = false;

    // One ordered list of targets per category.
    private static readonly Dictionary<NavCategory, List<NavigationTarget>> _byCategory = new();
    private static readonly NavCategory[] _categoryOrder =
    {
        NavCategory.Quests,
        NavCategory.Items,
        NavCategory.Doors,
        NavCategory.Graves,
        NavCategory.People,
        NavCategory.Storage,
        NavCategory.Stations,
        NavCategory.Other
    };

    private static NavCategory _currentCategory = NavCategory.Quests;
    private static int _selectedIndex = 0;

    private static bool _isWalking = false;
    private static int _updateCounter = 0;
    private static int _walkWatchdog = 0;

    // World position to turn and face when the current walk arrives, so the game's own
    // E-interaction / drop-pickup (which only fires on whatever is in front of the
    // character) works without the player having to manually aim their facing. Stored as
    // a point so it works for both WorldGameObjects and ground drops (which aren't WGOs).
    private static Vector2? _walkFacePos;

    // Deferred fallback walk: when A* fails we cannot re-issue GoTo synchronously
    // (the game's OnPathFailed clobbers the new request right after our callback),
    // so we queue a straight-line Direct attempt to run on the next frame.
    private static bool _fallbackPending = false;
    private static Vector2 _fallbackDest;
    private static string _fallbackLabel;

    // World coordinates use 96 units per tile. Only surface points of interest
    // within a generous radius so the per-category lists stay manageable.
    private const float TileSize = 96f;
    private const float MaxNavDistance = 60f * TileSize;   // ~60 tiles
    private const int UpdateInterval = 30;                 // refresh list every 30 frames
    private const float ApproachOffset = 80f;              // stop ~1 tile short, on walkable ground

    internal static bool IsWalking => _isWalking;

    // Set true only while we drive an A* GoTo, so the RefreshPlayerGraph patch pads
    // the player-graph bounds for our walks without affecting vanilla pathfinding.
    internal static bool PadPlayerGraph { get; private set; }

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        foreach (var cat in _categoryOrder)
            _byCategory[cat] = new List<NavigationTarget>();
        _initialized = true;
        _log?.LogInfo("[NAVIGATOR] ObjectNavigator initialized (native pathfinding, categorized)");
    }

    internal static void Update()
    {
        if (!_initialized) return;

        try
        {
            // Run a queued straight-line fallback (A* couldn't find a path).
            if (_fallbackPending)
            {
                _fallbackPending = false;
                StartWalk(_fallbackDest, _fallbackLabel, MovementComponent.GoToMethod.Direct);
            }

            _updateCounter++;
            if (_updateCounter >= UpdateInterval)
            {
                _updateCounter = 0;
                RefreshDestinations();
            }

            // Watch the game's own movement state while a walk is in progress.
            if (_isWalking)
            {
                var character = MainGame.me?.player?.components?.character;
                if (character == null)
                {
                    _isWalking = false;
                    _walkWatchdog = 0;
                }
                else if (!character.player_controlled_by_script)
                {
                    // Game released control (arrival completed normally).
                    _isWalking = false;
                    _walkWatchdog = 0;
                }
                else if (!character.IsInMovingState())
                {
                    // Still flagged as script-controlled but no longer moving and the
                    // flag was never released (e.g. a failed path that left it stuck).
                    // Release after a short grace so the player is never locked out.
                    if (++_walkWatchdog > 10)
                    {
                        _log?.LogWarning("[NAVIGATOR] Watchdog releasing stuck script control");
                        ReleaseScriptControl();
                        _isWalking = false;
                        _walkWatchdog = 0;
                    }
                }
                else
                {
                    _walkWatchdog = 0;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error in Update: {ex.Message}");
        }
    }

    private static List<NavigationTarget> CurrentList =>
        _byCategory.TryGetValue(_currentCategory, out var list) ? list : new List<NavigationTarget>();

    // ---- Category cycling (Ctrl+PageUp / Ctrl+PageDown) ---------------------

    internal static void NextCategory() => CycleCategory(+1);
    internal static void PreviousCategory() => CycleCategory(-1);

    private static void CycleCategory(int dir)
    {
        int start = Array.IndexOf(_categoryOrder, _currentCategory);
        if (start < 0) start = 0;

        // Find the next category that actually has targets.
        for (int step = 1; step <= _categoryOrder.Length; step++)
        {
            int idx = (start + dir * step) % _categoryOrder.Length;
            if (idx < 0) idx += _categoryOrder.Length;
            var cat = _categoryOrder[idx];
            if (_byCategory[cat].Count > 0)
            {
                _currentCategory = cat;
                _selectedIndex = 0;
                AnnounceCategory();
                return;
            }
        }

        ScreenReader.Say("No navigable objects nearby", interrupt: true);
    }

    private static void AnnounceCategory()
    {
        var list = CurrentList;
        var name = CategoryName(_currentCategory);
        if (list.Count == 0)
        {
            ScreenReader.Say($"{name}, empty", interrupt: true);
            return;
        }

        var target = list[_selectedIndex];
        ScreenReader.Say($"{name}, {list.Count}. {target.Label}, {DistanceText(target.Distance)}{SkullSuffix(target)}", interrupt: true);
        _log?.LogInfo($"[NAVIGATOR] Category {name} ({list.Count}) -> {target.Label}");
    }

    private static string CategoryName(NavCategory cat) => cat switch
    {
        NavCategory.Quests => "Quest targets",
        NavCategory.Items => "Items",
        NavCategory.Doors => "Doors",
        NavCategory.Graves => "Graves",
        NavCategory.People => "People",
        NavCategory.Storage => "Storage",
        NavCategory.Stations => "Crafting stations",
        _ => "Other"
    };

    // ---- Item cycling within the current category (PageUp / PageDown) -------

    internal static void SelectNext()
    {
        var list = CurrentList;
        if (list.Count == 0) { EnsureNonEmptyCategory(); return; }

        _selectedIndex = (_selectedIndex + 1) % list.Count;
        AnnounceSelected();
    }

    internal static void SelectPrevious()
    {
        var list = CurrentList;
        if (list.Count == 0) { EnsureNonEmptyCategory(); return; }

        _selectedIndex = (_selectedIndex - 1 + list.Count) % list.Count;
        AnnounceSelected();
    }

    internal static void AnnounceSelected()
    {
        var list = CurrentList;
        if (list.Count == 0)
        {
            ScreenReader.Say("No navigable objects nearby", interrupt: false);
            return;
        }

        try
        {
            if (_selectedIndex >= list.Count) _selectedIndex = 0;
            var target = list[_selectedIndex];
            var message = $"{target.Label}, {DistanceText(target.Distance)}, {_selectedIndex + 1} of {list.Count}{SkullSuffix(target)}";
            ScreenReader.Say(message, interrupt: false);
            _log?.LogInfo($"[NAVIGATOR] Announced: {message}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error announcing: {ex.Message}");
        }
    }

    // If the current category emptied out, jump to the first non-empty one.
    private static void EnsureNonEmptyCategory()
    {
        foreach (var cat in _categoryOrder)
        {
            if (_byCategory[cat].Count > 0)
            {
                _currentCategory = cat;
                _selectedIndex = 0;
                AnnounceCategory();
                return;
            }
        }
        ScreenReader.Say("No navigable objects nearby", interrupt: false);
    }

    private static string DistanceText(float worldDistance)
    {
        var tiles = worldDistance / TileSize;
        return $"{tiles:F0} meters away";
    }

    // Append red/white skull info when the target is a grave's body or a corpse drop.
    private static string SkullSuffix(NavigationTarget target)
    {
        var skulls = SkullInfo.Describe(SkullInfo.GetBodyItem(target));
        return string.IsNullOrEmpty(skulls) ? "" : $". {skulls}";
    }

    // ---- Walking via the game's native A* pathfinding ----------------------

    internal static void WalkToSelected()
    {
        // Don't walk away from a station mid-craft: timed station crafts (e.g. the autopsy
        // table cutting flesh) are performed by the player standing put, and moving cancels
        // them — which strands the body and wedges the table. Make the player wait it out.
        if (InteractionDetector.IsPlayerCrafting)
        {
            ScreenReader.Say("A craft is in progress. Stand still until it finishes.", interrupt: true);
            return;
        }

        var list = CurrentList;
        if (list.Count == 0)
        {
            ScreenReader.Say("Nothing selected to walk to", interrupt: true);
            return;
        }

        if (_selectedIndex >= list.Count) _selectedIndex = 0;
        var target = list[_selectedIndex];

        // Aim for a point ~1 tile short of the object, along the line from the
        // object toward the player. Most points of interest sit ON an unwalkable
        // tile; targeting their exact centre makes the player pathfinder reject the
        // path ("end point too far", a hard 17-unit limit). The approach point lands
        // on walkable ground right next to the object — where you'd stand anyway.
        // Drops sit on walkable ground, so walk onto the exact tile (no offset) to land
        // inside the game's pickup/highlight area.
        var dest = target.IsDrop ? target.Position : ApproachPoint(target.Position);

        // Pad the player-graph bounds for both the snap scan and the A* walk below,
        // so the search can route around fences/walls instead of failing.
        PadPlayerGraph = true;
        try
        {
            // Snap to an actual walkable navmesh node so A* accepts the destination and
            // routes AROUND obstacles instead of failing and falling back to a straight
            // line that just bumps into them.
            dest = SnapToWalkable(dest);

            var pp = MainGame.me?.player?.pos ?? Vector2.zero;
            _log?.LogInfo($"[NAVIGATOR] GEOMETRY player={pp} object={target.Position} approach->snapped={dest} " +
                          $"objDist={Vector2.Distance(pp, target.Position):F0} snapDist={Vector2.Distance(pp, dest):F0}");

            _fallbackPending = false;
            _walkFacePos = target.Position;   // face it on arrival so plain E interacts/picks up
            ScreenReader.Say($"Walking to {target.Label}, {DistanceText(target.Distance)}", interrupt: true);
            StartWalk(dest, target.Label, MovementComponent.GoToMethod.AStar);
        }
        finally
        {
            PadPlayerGraph = false;
        }
    }

    /// <summary>
    /// Turn the player to face the object we just walked to. The game's interaction
    /// fires only on whatever sits inside the character's forward-facing interaction
    /// collider (positioned by anim_direction), so a blind player who auto-walked up to
    /// an object usually isn't facing it and plain E does nothing. Facing the object on
    /// arrival rotates that collider onto it, so the vanilla E key just works.
    /// </summary>
    private static void FacePlayerAtTarget()
    {
        var facePos = _walkFacePos;
        _walkFacePos = null;
        if (facePos == null) return;

        try
        {
            var player = MainGame.me?.player;
            var character = player?.components?.character;
            if (character == null) return;

            // LookAt(Vector2) takes a DIRECTION, so pass target-minus-player. Works for both
            // WorldGameObjects and ground drops since we only need the point, not the object.
            var dir = facePos.Value - player.pos;
            if (dir.sqrMagnitude > 0.0001f)
                character.LookAt(dir);
            _log?.LogInfo($"[NAVIGATOR] Facing {facePos.Value} for interaction");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] FacePlayerAtTarget failed: {ex.Message}");
        }
    }

    private static Vector2 ApproachPoint(Vector2 objPos)
    {
        var player = MainGame.me?.player;
        if (player == null) return objPos;

        var playerPos = player.pos;
        var toPlayer = playerPos - objPos;
        var d = toPlayer.magnitude;
        if (d <= ApproachOffset) return playerPos;          // already adjacent
        return objPos + toPlayer / d * ApproachOffset;       // back off one tile
    }

    /// <summary>
    /// Snap a world point to the nearest walkable node ON THE PLAYER GRAPH (graph 2).
    /// The player pathfinder rejects any destination whose path endpoint is more than
    /// ~17 units away (AStarSearcher), and it searches only the dynamically-rescanned
    /// player graph — which has different walkability from the persistent graph. So we
    /// scan that graph around the target first (the same call GoTo makes), then snap to
    /// a node on it. Snapping against the persistent graph isn't good enough: it returns
    /// nodes that are unwalkable or unreachable once the player graph is built.
    /// </summary>
    private static Vector2 SnapToWalkable(Vector2 p)
    {
        try
        {
            var astar = AstarPath.active;
            if (astar == null) return p;

            // Build the player graph (graph 2) around the player->target span so we
            // snap to a node the upcoming A* search will actually have available.
            var player = MainGame.me?.player;
            if (player != null)
                AStarTools.RefreshPlayerGraph(player.pos, p);

            var constraint = Pathfinding.NNConstraint.Default;
            constraint.graphMask = 1 << 2;  // player graph only

            var nn = astar.GetNearest(new Vector3(p.x, p.y, 0f), constraint);
            if (nn.node != null && nn.node.Walkable)
            {
                var snapped = new Vector2(nn.clampedPosition.x, nn.clampedPosition.y);
                _log?.LogInfo($"[NAVIGATOR] Snapped {p} -> {snapped} (dist {Vector2.Distance(p, snapped):F0})");
                return snapped;
            }

            _log?.LogWarning("[NAVIGATOR] No walkable player-graph node near target");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] SnapToWalkable failed: {ex.Message}");
        }
        return p;
    }

    private static void StartWalk(Vector2 dest, string label, MovementComponent.GoToMethod method)
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null)
            {
                _log?.LogError("[NAVIGATOR] Player character component is null");
                ScreenReader.Say("Cannot walk right now", interrupt: true);
                _isWalking = false;
                return;
            }

            // from_script:true suspends player input so the movement state machine
            // drives the character cleanly. AStar routes around obstacles; Direct is
            // the straight-line fallback used when A* can't find a valid path.
            character.GoTo(
                dest,
                snap_to_node: false,   // we pre-snap to a walkable node ourselves
                on_complete: () =>
                {
                    _isWalking = false;
                    FacePlayerAtTarget();
                    ScreenReader.Say($"Arrived at {label}", interrupt: true);
                    _log?.LogInfo($"[NAVIGATOR] Arrived at {label} ({method})");
                },
                on_failed: () =>
                {
                    if (method == MovementComponent.GoToMethod.AStar)
                    {
                        // A* failed (no path / endpoint too far). Queue a straight-line
                        // attempt for next frame — re-issuing GoTo here would be undone
                        // by the game's OnPathFailed running right after this callback.
                        _log?.LogWarning($"[NAVIGATOR] A* failed to {label}, trying direct");
                        _fallbackDest = dest;
                        _fallbackLabel = label;
                        _fallbackPending = true;
                    }
                    else
                    {
                        // Direct fallback also failed (stuck against geometry). Release
                        // control so the player is never locked out, then report.
                        ReleaseScriptControl();
                        _isWalking = false;
                        ScreenReader.Say($"Could not reach {label}", interrupt: true);
                        _log?.LogWarning($"[NAVIGATOR] Direct walk failed to {label}");
                    }
                },
                with_cinematic: false,
                goto_method: method,
                event_on_complete: "",
                filter_astar_area: null,
                from_script: true,
                target_gd_point: null);

            _isWalking = true;
            _walkWatchdog = 0;
            _log?.LogInfo($"[NAVIGATOR] GoTo {label} via {method} to {dest}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error starting walk: {ex.Message}\n{ex.StackTrace}");
            ReleaseScriptControl();
            ScreenReader.Say("Walk failed", interrupt: true);
            _isWalking = false;
        }
    }

    internal static void StopWalking()
    {
        if (!_isWalking) return;

        try
        {
            ReleaseScriptControl();
            _isWalking = false;
            _walkWatchdog = 0;
            ScreenReader.Say("Walking stopped", interrupt: true);
            _log?.LogInfo("[NAVIGATOR] Walking stopped");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error stopping walk: {ex.Message}");
            _isWalking = false;
        }
    }

    /// <summary>
    /// Stop scripted movement and hand control back to the player. Safe to call
    /// redundantly; this is the guard against the player being locked out of input
    /// when the game's own OnPathFailed leaves player_controlled_by_script set.
    /// </summary>
    private static void ReleaseScriptControl()
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character != null)
            {
                character.StopMovement();
                character.player_controlled_by_script = false;
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error releasing script control: {ex.Message}");
        }
    }

    // ---- Building the categorized destination lists ------------------------

    private static void RefreshDestinations()
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null)
                return;

            // The world is a 2D x-y plane (z is only render-sorting depth), so use
            // WorldGameObject.pos which is the authoritative (x, y) world position.
            var playerPos = player.pos;
            var allObjects = UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true);
            if (allObjects == null || allObjects.Length == 0)
                return;

            // Remember what is currently selected so we can keep the cursor on it
            // across refreshes even as distances change. Drops have no WorldGameObject,
            // so track their GameObject separately.
            WorldGameObject previouslySelected = null;
            GameObject previouslySelectedDrop = null;
            var curList = CurrentList;
            if (curList.Count > 0 && _selectedIndex < curList.Count)
            {
                previouslySelected = curList[_selectedIndex].Object;
                previouslySelectedDrop = curList[_selectedIndex].DropGo;
            }

            foreach (var cat in _categoryOrder)
                _byCategory[cat].Clear();

            foreach (var obj in allObjects)
            {
                if (obj == null) continue;
                if (InteractionDetector.IsPlayer(obj) || InteractionDetector.IsPrefab(obj)) continue;
                if (!obj.gameObject.activeInHierarchy) continue;

                if (!TryClassify(obj, out var category)) continue;

                var objPos = obj.pos;
                var distance = Vector2.Distance(objPos, playerPos);
                if (distance > MaxNavDistance) continue;

                _byCategory[category].Add(new NavigationTarget
                {
                    Object = obj,
                    Label = GetObjectLabelSafe(obj),
                    Position = objPos,
                    Distance = distance
                });
            }

            // Active quest targets are gathered separately: they are resolved by
            // obj_id from the save's task list (not by walking the scene), and they
            // bypass the distance cap so a far-off quest objective always shows up.
            GatherQuestTargets(playerPos);

            // Ground drops (bodies/loot) are DropResGameObjects, not WorldGameObjects, so
            // they need their own scan or they stay invisible to the screen reader.
            GatherDropTargets(playerPos);

            foreach (var cat in _categoryOrder)
                _byCategory[cat].Sort((a, b) => a.Distance.CompareTo(b.Distance));

            // Start in the first non-empty category if the current one is empty.
            if (CurrentList.Count == 0)
            {
                foreach (var cat in _categoryOrder)
                {
                    if (_byCategory[cat].Count > 0) { _currentCategory = cat; break; }
                }
            }

            // Restore selection by object identity (WorldGameObject or drop), else clamp.
            var list = CurrentList;
            if (previouslySelected != null || previouslySelectedDrop != null)
            {
                var idx = list.FindIndex(t =>
                    (previouslySelected != null && t.Object == previouslySelected) ||
                    (previouslySelectedDrop != null && t.DropGo == previouslySelectedDrop));
                _selectedIndex = idx >= 0 ? idx : 0;
            }
            if (_selectedIndex >= list.Count)
                _selectedIndex = 0;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error refreshing destinations: {ex.Message}");
        }
    }

    /// <summary>
    /// Populate the Quests category from the active quests' arrow targets. Each
    /// <see cref="QuestDefinition"/> carries the on-screen quest arrow's destination via
    /// <c>arrow_wgo_custom_tag</c> / <c>arrow_wgo_obj_id</c> — the same "special marking"
    /// the sighted UI points its arrow at (see QuestListGUI). We resolve that world object
    /// per quest and expose it as a direct navigation target — the screen-reader
    /// equivalent of the quest arrow. Unlike the scene-scanned categories, quest targets
    /// ignore the distance cap so a far objective (e.g. "find Gerry") still appears.
    /// </summary>
    private static void GatherQuestTargets(Vector2 playerPos)
    {
        try
        {
            var quests = MainGame.me?.save?.quests?.GetCurrentQuests();
            if (quests == null) return;

            var questList = _byCategory[NavCategory.Quests];
            var seen = new HashSet<WorldGameObject>();

            foreach (var quest in quests)
            {
                var def = quest?.definition;
                if (def == null) continue;

                var target = ResolveQuestArrowTarget(def, playerPos);

                // No arrow target set (or its object isn't loaded): nothing to walk to.
                if (target == null || InteractionDetector.IsPlayer(target)) continue;
                if (!seen.Add(target)) continue;

                var questName = GetQuestLabelSafe(def.id);
                var objName = GetObjectLabelSafe(target);
                var label = string.IsNullOrEmpty(questName) ? objName : $"{questName}: {objName}";

                var objPos = target.pos;
                questList.Add(new NavigationTarget
                {
                    Object = target,
                    Label = label,
                    Position = objPos,
                    Distance = Vector2.Distance(objPos, playerPos)
                });
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error gathering quest targets: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve a quest's arrow target the same way the vanilla quest list does: prefer a
    /// custom-tagged object, else the nearest object matching the arrow's obj_id.
    /// </summary>
    private static WorldGameObject ResolveQuestArrowTarget(QuestDefinition def, Vector2 playerPos)
    {
        try
        {
            WorldGameObject target = null;

            if (!string.IsNullOrEmpty(def.arrow_wgo_custom_tag))
                target = WorldMap.GetWorldGameObjectByCustomTag(def.arrow_wgo_custom_tag);

            if (target == null && !string.IsNullOrEmpty(def.arrow_wgo_obj_id))
            {
                var matches = WorldMap.GetWorldGameObjectsByObjId(def.arrow_wgo_obj_id);
                if (matches != null)
                {
                    float best = float.MaxValue;
                    foreach (var m in matches)
                    {
                        if (m == null) continue;
                        float d = (playerPos - m.pos).sqrMagnitude;
                        if (d < best) { best = d; target = m; }
                    }
                }
            }

            return target;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] arrow resolve failed for {def?.id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Populate the Items category from ground drops. Bodies and large loot are
    /// <see cref="DropResGameObject"/>s (plain MonoBehaviours), not WorldGameObjects, so the
    /// scene scan in <see cref="RefreshDestinations"/> never sees them and a blind player has
    /// no way to find e.g. a delivered corpse. We enumerate the drops directly, expose each
    /// as a navigable target, and mark it <c>IsDrop</c> so the walk lands on its exact tile
    /// (inside the game's pickup/highlight area) and plain E carries it.
    /// </summary>
    private static void GatherDropTargets(Vector2 playerPos)
    {
        try
        {
            var drops = UnityEngine.Object.FindObjectsOfType<DropResGameObject>();
            if (drops == null || drops.Length == 0) return;

            var itemList = _byCategory[NavCategory.Items];

            foreach (var drop in drops)
            {
                if (drop == null || drop.is_collected) continue;

                var res = drop.res;
                if (res == null || res.IsEmpty() || res.definition == null) continue;

                var pos = (Vector2)drop.transform.position;
                var distance = Vector2.Distance(pos, playerPos);
                if (distance > MaxNavDistance) continue;

                itemList.Add(new NavigationTarget
                {
                    Object = null,
                    Label = GetDropLabelSafe(res),
                    Position = pos,
                    Distance = distance,
                    IsDrop = true,
                    DropGo = drop.gameObject
                });
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error gathering drop targets: {ex.Message}");
        }
    }

    private static string GetDropLabelSafe(Item res)
    {
        try
        {
            var name = res.definition.GetItemName();
            if (!string.IsNullOrEmpty(name))
                name = ScreenReader.StripNguiCodes(name).Trim();
            if (string.IsNullOrEmpty(name))
                name = res.id;

            // Bodies are the marquee case — make them obviously a corpse to carry.
            if (res.definition.type == ItemDefinition.ItemType.Body && !string.IsNullOrEmpty(name))
                return name;

            var count = res.value > 1 ? $" x{res.value}" : "";
            return name + count;
        }
        catch
        {
            return "Item";
        }
    }

    private static string GetQuestLabelSafe(string questId)
    {
        try
        {
            return ScreenReader.StripNguiCodes(GJL.L("qt_" + questId) ?? "").Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decide whether an object is a navigable point of interest and which
    /// category it belongs to. Non-interactive decoration is filtered out.
    /// </summary>
    private static bool TryClassify(WorldGameObject obj, out NavCategory category)
    {
        category = NavCategory.Other;

        // Doors / zone exits are detected by name (the game has no explicit
        // teleport interaction_type). Skip the non-usable arrival anchors
        // (e.g. teleport_point, interaction_type None) — you can't walk through those,
        // they are only where you land, and listing them clutters the door list.
        if (obj.name.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (obj.obj_def != null &&
                obj.obj_def.interaction_type == ObjectDefinition.InteractionType.None)
                return false;

            category = NavCategory.Doors;
            return true;
        }

        var def = obj.obj_def;
        if (def == null)
            return false;

        // People: NPCs and mobs.
        try
        {
            if (def.type == ObjectDefinition.ObjType.NPC ||
                def.type == ObjectDefinition.ObjType.Mob ||
                def.IsNPC())
            {
                category = NavCategory.People;
                return true;
            }
        }
        catch { }

        // Real, interactable graves (open the GraveGUI). Classify these by their
        // dedicated interaction type, NOT by an obj_id substring — "graveyard_builddesk"
        // (the grave planning/build desk) embeds "grave" but is a Builder station and is
        // handled below. The greedy substring catch is kept only as a default fallback so
        // non-interactive grave fixtures still list under Graves.
        if (def.interaction_type == ObjectDefinition.InteractionType.Grave)
        {
            category = NavCategory.Graves;
            return true;
        }

        switch (def.interaction_type)
        {
            case ObjectDefinition.InteractionType.Chest:
                category = NavCategory.Storage;
                return true;
            case ObjectDefinition.InteractionType.Craft:
            case ObjectDefinition.InteractionType.Builder:
                // Build desks (incl. the graveyard build desk where you plan/mark a grave)
                // open a build catalog — functionally a crafting station.
                category = NavCategory.Stations;
                return true;
            case ObjectDefinition.InteractionType.RunScript:
                // Script-driven objects that craft (e.g. the autopsy table mf_preparation_1,
                // whose E runs PutOverheadToWGO/OpenCraft) are functionally crafting stations,
                // so file them under Stations rather than the catch-all Other. has_craft is a
                // cheap obj_def flag — avoid touching obj.components, which lazily allocates a
                // ComponentsManager for every scene object on each refresh.
                category = def.has_craft ? NavCategory.Stations : NavCategory.Other;
                return true;
            default:
                // Non-interactive grave fixtures (empty grave grounds, graveyard zone
                // markers) have no Grave interaction but read as graves by id — keep them
                // navigable under Graves. Everything else (grass, scenery) is skipped.
                if (!string.IsNullOrEmpty(obj.obj_id) &&
                    obj.obj_id.IndexOf("grave", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    category = NavCategory.Graves;
                    return true;
                }
                return false;
        }
    }

    /// <summary>
    /// True for objects that open a build/craft/chest UI on interact. Used so a station
    /// whose obj_id happens to contain "grave" (the graveyard build desk) keeps its proper
    /// localized name instead of being relabelled as a tombstone.
    /// </summary>
    private static bool IsStationLike(WorldGameObject obj)
    {
        var it = obj?.obj_def?.interaction_type;
        return it == ObjectDefinition.InteractionType.Builder ||
               it == ObjectDefinition.InteractionType.Craft ||
               it == ObjectDefinition.InteractionType.Chest;
    }

    /// <summary>
    /// The recognizable name the tutorial/Gerry use for a build desk ("planning table"),
    /// localized to the player's language. The game itself names build desks after their
    /// zone (e.g. "Alter Friedhof"/"Old Cemetery"), which a player told to "go to the
    /// Planungstisch" can't connect to — so we lead with this word and keep the zone name
    /// only to tell multiple desks apart.
    /// </summary>
    private static string PlanningTableWord()
    {
        string code = "";
        try { code = (GJL.GetCurrentLocaleCode() ?? "").ToLowerInvariant(); } catch { }
        return code switch
        {
            "de" => "Planungstisch",
            "fr" => "Table de planification",
            "es" => "Mesa de planificación",
            "it" => "Tavolo di progettazione",
            "ru" => "Стол планирования",
            _ => "Planning table",
        };
    }

    private static string GetObjectLabelSafe(WorldGameObject obj)
    {
        try
        {
            // Build desks (the "planning table" Gerry sends you to) localize to their zone
            // name, e.g. "Alter Friedhof", which doesn't match what the player is told to look
            // for. Lead with the recognizable planning-table term, appending the zone name so
            // desks in different zones stay distinguishable.
            if (obj?.obj_def?.interaction_type == ObjectDefinition.InteractionType.Builder)
            {
                var zoneName = InteractionDetector.GetObjectLabel(obj);
                var planning = PlanningTableWord();
                return string.IsNullOrEmpty(zoneName) || zoneName == planning
                    ? planning
                    : $"{planning}: {zoneName}";
            }

            // Special handling for graves by checking obj_id. Skip build/craft/chest
            // stations whose id merely embeds "grave" (e.g. the graveyard build desk):
            // those localize to a proper station name, so prefixing "Grave " would both
            // mislabel them and bury them as if they were tombstones.
            if (obj != null && !string.IsNullOrEmpty(obj.obj_id) &&
                obj.obj_id.IndexOf("grave", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !IsStationLike(obj))
            {
                var cleanId = obj.obj_id.Replace("_", " ").Replace("-", " ");
                if (cleanId.Length > 0)
                    cleanId = char.ToUpper(cleanId[0]) + cleanId.Substring(1);
                return "Grave " + cleanId.Trim();
            }

            return InteractionDetector.GetObjectLabel(obj);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Failed to get label for object {obj?.name}: {ex.Message}");
            return "Unknown Object";
        }
    }
}
