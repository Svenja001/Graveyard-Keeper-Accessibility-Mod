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
    Landmarks,
    Items,
    Corpses,
    Doors,
    Graves,
    ExhumableGraves,
    People,
    Storage,
    Stations,
    Trees,
    Stones,
    Ores,
    Bushes,
    Flowers,
    Mushrooms,
    Gatherables,
    Fences,
    GravesToDecorate,
    Buildables,
    Roofs,
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
        NavCategory.Landmarks,
        NavCategory.Items,
        NavCategory.Corpses,
        NavCategory.Doors,
        NavCategory.Graves,
        NavCategory.ExhumableGraves,
        NavCategory.People,
        NavCategory.Storage,
        NavCategory.Stations,
        NavCategory.Trees,
        NavCategory.Stones,
        NavCategory.Ores,
        NavCategory.Bushes,
        NavCategory.Flowers,
        NavCategory.Mushrooms,
        NavCategory.Gatherables,
        NavCategory.Fences,
        NavCategory.GravesToDecorate,
        NavCategory.Buildables,
        NavCategory.Roofs,
        NavCategory.Other
    };

    private static NavCategory _currentCategory = NavCategory.Quests;
    private static int _selectedIndex = 0;

    private static bool _isWalking = false;
    private static int _updateCounter = 0;
    private static int _walkWatchdog = 0;

    // Teleport detection: the player's position the previous busy frame. If it jumps by more
    // than TeleportJumpDistance in a single frame while navigation is active, the player was
    // teleported (Ruhestein, fast-travel, sleep respawn, dungeon transition) and the stale walk
    // state must be torn down — otherwise the stuck watchdog mistakes the jump for "no progress"
    // and the beacon re-announces a now-wrong target endlessly.
    private static Vector2 _lastBusyPos;
    private static bool _hasBusyPos = false;

    // Long-distance auto-walk: targets too far for the A* player-graph to path to in one go
    // (e.g. the Tavern from home) are walked in short hops. Each tick we aim a chunk-sized
    // step toward the target, snap it to walkable ground, and let native A* route that hop;
    // on arrival we issue the next hop until close enough for the precise final approach.
    private static bool _longWalkActive = false;
    private static NavigationTarget _longWalkTarget;

    // True while a game cutscene/cinematic owns the player (GS.SetPlayerEnable(false, cinematic)).
    // During a cutscene we must NEVER set control_enabled = true or call StopMovement: doing so
    // flips the body to Dynamic (UpdateBodyPhysics) and jams the cutscene's own scripted player
    // GoTo against a fence/gate, freezing the scene forever. See OnGameSetPlayerEnable.
    private static bool _gameOwnsPlayer = false;

    // True while WE have forced control_enabled = false for a scripted walk (see StartNativePathWalk).
    // The game gates every menu hotkey (N / Inventory / Map / Techs) on control being enabled, so if
    // a walk ever ends without our restore running (e.g. a cutscene grabbed the player mid-walk and
    // the completion callback bailed early), the player is silently locked out of all their menus.
    // The idle watchdog in Update() uses this flag to undo ONLY our own disable — never control the
    // game disabled for a cutscene/dialogue — once navigation is idle and no cutscene owns the player.
    private static bool _weDisabledControl = false;
    private static Vector2 _longWalkProgressPos;     // last position where we made real progress
    private static int _longWalkStuckTicks = 0;      // consecutive hops with no progress
    private static Vector2 _longWalkAnnouncePos;     // last position we announced remaining distance

    // Obstacle-aware route computed on the whole-map NPC navmesh (graph 0). The player's own
    // GoTo is locked to the thin graph-2 box and a 17-unit endpoint cap, so it walks straight
    // into fences it should route around. Instead we ask graph 0 (what villagers path on) for
    // a full route, then drive the player hop-by-hop along its waypoints — hugging the navmesh
    // around walls/fences. Null while none is computed (then we fall back to straight hops).
    private static bool _routePending = false;
    private static bool _routeNeedsRecompute = false;
    // Exit-assist: building interiors are navmesh regions disconnected from the outside world, so
    // a route from inside to an outdoor target fails. When that happens we instead walk the player
    // to the nearest exit door and prompt them to press E to step outside, then retry.
    private static bool _exitAssisting = false;
    private static string _exitAssistLabel;
    // Island pull-back: some targets sit on a graph-0 component disconnected from the rest of the
    // map (the player's house — you cross a threshold the NPC navmesh doesn't bake). The route
    // errors. We then pull the destination toward the player and retry until it lands on reachable
    // navmesh (the island's edge nearest the target), walk there, and report the remaining gap.
    private static int _pullbackTries = 0;
    private static Vector2 _longWalkDest;            // route end (approach point near the target)
    // Partial-route chaining: when the target is unreachable on the navmesh (e.g. an NPC inside
    // a building), graph 0 returns a path to the closest reachable node — the entrance/outside.
    // We walk that, then re-route from the new spot to advance region-by-region, until either
    // the target becomes reachable or the closest-reachable gap stops improving (navmesh limit).
    private static bool _routeReachesTarget = true;  // this route's endpoint actually reaches the goal
    private static bool _finalPartial = false;       // navmesh can't get closer; stop at route end
    private static float _bestEndGap;                // smallest route-endpoint-to-goal gap seen so far
    private static int _stalledRecomputes = 0;       // consecutive partial routes with no gap improvement

    // Compass beacon: the manual fallback used only when the auto-walker gets boxed in by
    // geometry it can't route around. We call out bearing + distance and let the player walk.
    private static bool _beaconActive = false;
    private static NavigationTarget _beaconTarget;
    private static Vector2 _beaconLastAnnouncePos;

    // World position to turn and face when the current walk arrives, so the game's own
    // E-interaction / drop-pickup (which only fires on whatever is in front of the
    // character) works without the player having to manually aim their facing. Stored as
    // a point so it works for both WorldGameObjects and ground drops (which aren't WGOs).
    private static Vector2? _walkFacePos;

    // The specific object the player just auto-walked to. On arrival the game's interaction
    // component picks whatever interactable is nearest/most-aligned in front of the player
    // (InteractionComponent.GetGameObject scores by angle + distance) — so a chest sitting next to
    // the bed you navigated to can win, and vanilla E opens the wrong thing. While the player is
    // still standing at the navigated object we bias that selection back to it (see
    // Patches.InteractionComponent_FindCurrentInteractionNearest_Postfix) so E acts on the object
    // they actually chose. Cleared when the player walks away (distance check) or starts a new walk.
    private static WorldGameObject _arrivedTarget;
    private static Vector2 _arrivedTargetPos;
    private const float ArrivedTargetHoldDistance = 2.5f * TileSize;

    // Deferred fallback walk: when A* fails we cannot re-issue GoTo synchronously
    // (the game's OnPathFailed clobbers the new request right after our callback),
    // so we queue a straight-line Direct attempt to run on the next frame.
    private static bool _fallbackPending = false;
    private static Vector2 _fallbackDest;
    private static string _fallbackLabel;

    // When a SHORT A* walk fails, the target is usually behind a fence (the plain player-graph
    // A* can't path through a gate, and the straight-line Direct fallback just jams on the rail).
    // Before giving up to Direct, escalate to the same fence-aware graph-0 route the long walk
    // uses, which threads gates like an NPC. Deferred to the next frame for the same reason as
    // the Direct fallback (the game's OnPathFailed runs right after our callback).
    private static bool _escalatePending = false;
    private static NavigationTarget _shortWalkTarget;

    // After teleport, only the patch of navmesh around the landing spot is active; a nearby target
    // (e.g. the bed across the room) reports no walkable node and A* fails, even though it becomes
    // reachable once the player walks toward it and that area streams in. Without a guard the beacon
    // hands straight back to A* (target is within handoff distance), A* fails, it re-escalates and
    // bails back to the beacon: an infinite in-place "walking…" loop. So we record where A* last
    // failed and only let the beacon retry the handoff once the player has moved HandoffRetryDistance
    // closer (the area has likely activated) — breaking the stationary loop while still auto-finishing
    // as the player approaches. Reset on a fresh user walk (WalkToSelected).
    private static bool _astarFailedForWalk = false;
    private static Vector2 _astarFailPos;

    // World coordinates use 96 units per tile. Only surface points of interest
    // within a generous radius so the per-category lists stay manageable.
    private const float TileSize = 96f;
    private const float MaxNavDistance = 60f * TileSize;   // ~60 tiles
    // Resource nodes (Trees/Stones/Ores/Bushes/Gatherables) get a longer reach: they sit out in
    // the world (e.g. coal/iron deposits deep in the mountains) and a blind player can't pan the
    // camera to find one, so they must be able to select and walk to one from farther away than the
    // general 60-tile cap. Without this, distant deposits never enter the list and so can never be
    // walked toward (chicken-and-egg). ~120 tiles covers the mountain mining area.
    private const float MaxHarvestableNavDistance = 120f * TileSize;
    private const int UpdateInterval = 30;                 // refresh list every 30 frames
    private const float ApproachOffset = 80f;              // stop ~1 tile short, on walkable ground

    // Beyond LongWalkStartDistance the A* player graph can't path in one shot, so Ctrl+Home
    // follows a graph-0 route until within FinalApproachDistance, then does the precise single
    // A* approach. ProgressDistance/StuckTickLimit detect being boxed in.
    private const float LongWalkStartDistance = 14f * TileSize;
    private const float FinalApproachDistance = 11f * TileSize;
    // After the native walk, if we're within AtTargetDistance of the target we're effectively
    // there (just face + "Arrived"). If we ended further short (pulled back to an island edge) but
    // within FinalApproachReach, finish the last stretch onto the door with player-graph A* so the
    // player only needs to press E.
    private const float AtTargetDistance = 3f * TileSize;
    // Stations/build desks/chests must be entered to within ~1 tile or the game's interaction
    // overlap test (which fires inside the player's forward collider) finds nothing and vanilla
    // E/F does nothing. The lenient AtTargetDistance is fine for doors/teleports but too far for
    // these, so a close-interaction target uses this tighter "arrived" radius and otherwise gets a
    // precise final approach onto its (possibly synthetic) dock tile. See NeedsCloseInteraction.
    private const float InteractionArrivalDistance = 1.2f * TileSize;
    private const float FinalApproachReach = 16f * TileSize;
    private const float ProgressDistance = 3f * TileSize;
    private const float AnnounceProgressDistance = 10f * TileSize;
    // A single-frame position change larger than this means the player teleported (no walk speed
    // covers 6 tiles in one frame); real teleports jump hundreds-to-thousands of units.
    private const float TeleportJumpDistance = 6f * TileSize;
    // TickLongWalk runs every frame, so this is in frames: how long the player may make less
    // than ProgressDistance of headway before we treat the native follow as stuck. Generous so
    // brief pauses at waypoints / slow stretches don't trip it.
    private const int StuckTickLimit = 180;
    // A graph-0 route whose endpoint is farther than this from the goal is a partial path: the
    // target isn't navmesh-reachable, so we walk to that closest reachable point (the entrance).
    private const float PartialRouteThreshold = 10f * TileSize;
    // A partial re-route must shrink the endpoint-to-goal gap by at least this much to count as
    // progress; after StalledRecomputeLimit partials with no improvement we've hit the navmesh
    // limit (as close as walking can get) and stop at the entrance.
    private const float EndGapImprove = 3f * TileSize;
    private const int StalledRecomputeLimit = 2;
    // Island pull-back tuning (see _pullbackTries).
    private const float PullbackStep = 6f * TileSize;
    private const int MaxPullbackTries = 12;
    private const float PullbackMinToPlayer = 12f * TileSize;

    // Beacon (manual fallback) thresholds.
    private const float BeaconHandoffDistance = 15f * TileSize;
    private const float BeaconReannounceDistance = 6f * TileSize;
    // How far the player must move after an A* failure before the beacon retries the A* handoff
    // (enough to have streamed in / activated the destination area; small enough to keep finishing).
    private const float HandoffRetryDistance = 3f * TileSize;

    internal static bool IsWalking => _isWalking;
    internal static bool IsBeaconActive => _beaconActive;
    internal static bool IsBusy => _isWalking || _beaconActive || _longWalkActive;

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
            // Teleport guard: if the player jumped a long way in a single frame while a walk/beacon
            // is active, they were teleported (Ruhestein, etc.). Tear down the stale navigation so
            // it doesn't mistake the jump for a stuck walk and chatter at a now-wrong target.
            if (IsBusy)
            {
                var pl = MainGame.me?.player;
                if (pl != null)
                {
                    var pos = pl.pos;
                    if (_hasBusyPos && Vector2.Distance(pos, _lastBusyPos) >= TeleportJumpDistance)
                    {
                        _log?.LogWarning($"[NAVIGATOR] Teleport detected (jump {Vector2.Distance(pos, _lastBusyPos):F0}u), aborting navigation");
                        AbortForTeleport();
                        return;
                    }
                    _lastBusyPos = pos;
                    _hasBusyPos = true;
                }
            }
            else
            {
                _hasBusyPos = false;

                // Control-lock watchdog: navigation is idle, so nothing of ours should be holding the
                // player in script control. If we forced control_enabled = false for a walk and a
                // teardown path skipped the restore (e.g. a cutscene grabbed the player mid-walk),
                // the player is silently locked out of every menu hotkey (N / Inventory / Map / Techs,
                // all gated on control_enabled). Hand control back — but only our own disable, and
                // never while a cutscene owns the player.
                if (_weDisabledControl && !_gameOwnsPlayer)
                {
                    _weDisabledControl = false;
                    var character = MainGame.me?.player?.components?.character;
                    if (character != null && !character.control_enabled)
                    {
                        character.player_controlled_by_script = false;
                        character.control_enabled = true;
                        _log?.LogWarning("[NAVIGATOR] Control-lock watchdog restored player control (a walk teardown left it disabled)");
                    }
                }
            }

            // A* failed on a short walk — retry through the fence-aware graph-0 route (gates)
            // before resorting to the straight line. Runs next frame so the game's OnPathFailed
            // has finished clobbering the previous request.
            if (_escalatePending)
            {
                _escalatePending = false;
                StartLongWalk(_shortWalkTarget);
            }
            // Run a queued straight-line fallback (A* couldn't find a path).
            else if (_fallbackPending)
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

            // Monitor the long-distance auto-walk (the native follower does the moving).
            if (_longWalkActive)
                TickLongWalk();

            // Drive the compass beacon (manual fallback guidance) if one is active.
            if (_beaconActive)
                UpdateBeacon();

            // Watch the game's own movement state while a single A* walk is in progress. Skipped
            // during a long native follow — that legitimately pauses at waypoints, and its own
            // monitor (TickLongWalk) handles stalls; this short-grace watchdog would kill it.
            if (_isWalking && !_longWalkActive)
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
        ScreenReader.Say($"{name}, {list.Count}. {target.Label}, {DirectionTo(target)}{DistanceText(target.Distance)}{SkullSuffix(target)}", interrupt: true);
        _log?.LogInfo($"[NAVIGATOR] Category {name} ({list.Count}) -> {target.Label}");
    }

    private static string CategoryName(NavCategory cat) => cat switch
    {
        NavCategory.Quests => "Quest targets",
        NavCategory.Landmarks => "Landmarks",
        NavCategory.Items => "Items",
        NavCategory.Corpses => "Corpses",
        NavCategory.Doors => "Doors",
        NavCategory.Graves => "Graves",
        NavCategory.ExhumableGraves => "Exhumable graves",
        NavCategory.People => "People",
        NavCategory.Storage => "Storage",
        NavCategory.Stations => "Crafting stations",
        NavCategory.Trees => "Trees",
        NavCategory.Stones => "Stones",
        NavCategory.Ores => "Ores",
        NavCategory.Bushes => "Bushes",
        NavCategory.Flowers => "Flowers",
        NavCategory.Mushrooms => "Mushrooms",
        NavCategory.Gatherables => "Gatherables",
        NavCategory.Fences => "Broken fences",
        NavCategory.GravesToDecorate => "Graves to decorate",
        NavCategory.Buildables => "Built objects",
        NavCategory.Roofs => "Roofs",
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
            var dir = DirectionTo(target);
            var message = $"{target.Label}, {dir}{DistanceText(target.Distance)}, {_selectedIndex + 1} of {list.Count}{SkullSuffix(target)}";
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

    // Compass heading from the player to a target, formatted as a trailing ", " so it can
    // be slotted before the distance text. Empty if the player position isn't available.
    private static string DirectionTo(NavigationTarget target)
    {
        var player = MainGame.me?.player;
        if (player == null) return "";
        return CompassDirection(player.pos, target.Position) + ", ";
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

        // Fresh user walk: clear the "A* already failed" guard so this attempt may use A*/handoff,
        // and drop any previous arrival bias (the new arrival sets its own).
        _astarFailedForWalk = false;
        ClearArrivedTarget();

        // For a faraway target (e.g. the Tavern from home) the A* player graph can't path
        // there in one shot, so auto-walk it in short hops instead of a single GoTo.
        var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
        if (Vector2.Distance(playerPos, target.Position) > LongWalkStartDistance)
        {
            StartLongWalk(target);
            return;
        }

        WalkToTarget(target);
    }

    /// <summary>
    /// Auto-walk (native A*) to a target that is within the player graph's reach. Used both
    /// by Ctrl+Home on a near target and as the final approach when a compass beacon brings
    /// the player into range.
    /// </summary>
    private static void WalkToTarget(NavigationTarget target)
    {
        // Prefer the game's own interaction tile (nearest dock point) so we land exactly
        // where vanilla E/F works. Falls back to a point ~1 tile short of the object, along
        // the line from the object toward the player: most points of interest sit ON an
        // unwalkable tile, so targeting their exact centre makes the player pathfinder reject
        // the path ("end point too far", a hard 17-unit limit). Drops sit on walkable ground,
        // so we walk onto their exact tile to land inside the game's pickup/highlight area.
        var dest = InteractionDest(target, out var facePos);

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
            _escalatePending = false;
            _shortWalkTarget = target;        // kept so an A* failure can escalate to fence-aware routing
            _walkFacePos = facePos;           // face it on arrival so plain E interacts/picks up
            ScreenReader.Say($"Walking to {target.Label}, {DistanceText(target.Distance)}", interrupt: true);
            StartWalk(dest, target.Label, MovementComponent.GoToMethod.AStar);
        }
        finally
        {
            PadPlayerGraph = false;
        }
    }

    // ---- Long-distance auto-walk (native full-path follow) -----------------

    private static void StartLongWalk(NavigationTarget target)
    {
        _longWalkActive = true;
        _longWalkTarget = target;
        _longWalkStuckTicks = 0;
        _routeNeedsRecompute = false;
        _exitAssisting = false;
        _pullbackTries = 0;
        _routeReachesTarget = true;
        _finalPartial = false;
        _bestEndGap = float.MaxValue;
        _stalledRecomputes = 0;
        var pp = MainGame.me?.player?.pos ?? Vector2.zero;
        _longWalkProgressPos = pp;
        _longWalkAnnouncePos = pp;
        ScreenReader.Say($"Walking to {target.Label}, {DirectionTo(target)}{DistanceText(Vector2.Distance(pp, target.Position))}", interrupt: true);
        _log?.LogInfo($"[NAVIGATOR] Long walk started to {target.Label}");

        // Ask the whole-map NPC navmesh for an obstacle-aware route to the interaction tile;
        // OnRouteComputed injects it into the native follower.
        _longWalkDest = InteractionDest(target, out _);
        RequestGraph0Route(pp, _longWalkDest);
    }

    /// <summary>
    /// Launch an async path query on graph 0 (the whole-map NPC navmesh, which knows every
    /// wall/fence). Uses <see cref="AstarPath.StartPath"/> directly rather than the player's
    /// Seeker, so the player-only 17-unit endpoint cap does not apply and we get a full route.
    /// </summary>
    private static void RequestGraph0Route(Vector2 from, Vector2 to)
    {
        _routePending = false;
        try
        {
            if (AstarPath.active == null) return;

            // Snap the destination onto an actual graph-0 node first. A landmark anchor (a door
            // at a building wall, a zone object) can sit on a navmesh VOID — then the path query's
            // own GetNearest finds nothing and errors out ("route unavailable"). Snapping with our
            // own search pulls the target onto the nearest real walkable node so a route exists.
            if (TrySnapGraph0(to, out var snappedTo, out var snapDist))
            {
                if (snapDist > 1f)
                    _log?.LogInfo($"[NAVIGATOR] Snapped route dest {to} -> {snappedTo} ({snapDist:F0}u, graph 0)");
                to = snappedTo;
            }

            var path = Pathfinding.ABPath.Construct(
                new Vector3(from.x, from.y, 0f),
                new Vector3(to.x, to.y, 0f),
                OnRouteComputed);

            // Fresh constraint (don't mutate a shared Default) restricting snapping to graph 0.
            var constraint = Pathfinding.NNConstraint.Default;
            constraint.graphMask = 1 << 0;
            path.nnConstraint = constraint;

            _routePending = true;
            AstarPath.StartPath(path);
            _log?.LogInfo($"[NAVIGATOR] Graph-0 route requested {from} -> {to}");
        }
        catch (Exception ex)
        {
            _routePending = false;
            _log?.LogWarning($"[NAVIGATOR] Graph-0 route request failed: {ex.Message}");
        }
    }

    private static void OnRouteComputed(Pathfinding.Path p)
    {
        _routePending = false;
        if (!_longWalkActive) return;   // walk was cancelled while computing

        try
        {
            if (p == null || p.error || p.vectorPath == null || p.vectorPath.Count < 2)
            {
                HandleNoRoute();
                return;
            }

            // Does this route actually reach the target, or only the closest reachable point
            // (target unreachable on the navmesh, e.g. an NPC inside a building)?
            var endpoint = (Vector2)p.vectorPath[p.vectorPath.Count - 1];
            var endGap = Vector2.Distance(endpoint, _longWalkDest);
            _routeReachesTarget = endGap <= PartialRouteThreshold;

            if (_routeReachesTarget)
            {
                _log?.LogInfo($"[NAVIGATOR] Graph-0 route: {p.vectorPath.Count} wp, reaches goal ({endGap:F0}u)");
            }
            else
            {
                // Partial: walk to the closest reachable point, then re-route to advance. Once
                // re-routes stop getting closer we've hit the navmesh limit (the entrance).
                if (endGap < _bestEndGap - EndGapImprove) { _bestEndGap = endGap; _stalledRecomputes = 0; }
                else _stalledRecomputes++;
                _finalPartial = _stalledRecomputes > StalledRecomputeLimit;
                _log?.LogInfo($"[NAVIGATOR] Graph-0 route: {p.vectorPath.Count} wp, PARTIAL ends {endGap:F0}u " +
                              $"(best {_bestEndGap:F0}, stalled {_stalledRecomputes}, final={_finalPartial})");
            }

            StartNativePathWalk(p.vectorPath);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] OnRouteComputed error: {ex.Message}");
            BeaconBail("route error");
        }
    }

    /// <summary>
    /// Hand the whole graph-0 route to the game's own path follower by overwriting the player's
    /// public <c>cur_astar_path</c>. The follower (UpdatePathfinding) walks the entire list with
    /// physics-based, collision-aware movement — the same system NPCs use to thread village gates
    /// — so the player no longer jams at narrow passages the way our leg-by-leg driving did.
    /// </summary>
    private static void StartNativePathWalk(List<Vector3> waypoints)
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null) { StopLongWalk(announce: false); return; }

            // Copy with z=0 — a waypoint with z>=1000 is a teleport marker in the follower.
            var path = new List<Vector3>(waypoints.Count);
            foreach (var w in waypoints) path.Add(new Vector3(w.x, w.y, 0f));

            var finalDest = (Vector2)path[path.Count - 1];

            // Disable player control so the body becomes Kinematic (UpdateBodyPhysics). A Dynamic
            // body physically collides and JAMS at fences/gates; Kinematic glides along the navmesh
            // path exactly like an NPC. This is the key to scripted long-distance walking.
            character.control_enabled = false;
            _weDisabledControl = true;

            // GoTo(Direct, from_script) sets up the movement state, script control and callbacks
            // and leaves path_waypoint = 1; we then swap in the full route for the follower to walk.
            character.GoTo(
                finalDest,
                snap_to_node: false,
                on_complete: OnNativeWalkComplete,
                on_failed: OnNativeWalkFailed,
                with_cinematic: false,
                goto_method: MovementComponent.GoToMethod.Direct,
                event_on_complete: "",
                filter_astar_area: null,
                from_script: true,
                target_gd_point: null);

            character.cur_astar_path = path;

            _isWalking = true;
            _walkWatchdog = 0;
            var pp = MainGame.me.player.pos;
            _longWalkProgressPos = pp;
            _longWalkAnnouncePos = pp;
            _longWalkStuckTicks = 0;
            _log?.LogInfo($"[NAVIGATOR] Native walk injected: {path.Count} points");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] StartNativePathWalk error: {ex.Message}");
            BeaconBail("inject failed");
        }
    }

    private static void OnNativeWalkComplete()
    {
        _isWalking = false;

        // A cutscene cancelled our walk (it calls StopMovement, which fires this completion).
        // The cutscene now owns the player — leave control_enabled / the body alone, otherwise we
        // re-Dynamic the body and freeze the cutscene's own scripted player walk against the gate.
        if (_gameOwnsPlayer)
        {
            _longWalkActive = false;
            _log?.LogInfo("[NAVIGATOR] Native walk completion ignored: cutscene owns the player");
            return;
        }

        // Restore player control / Dynamic body (we forced Kinematic for the scripted walk).
        var ch = MainGame.me?.player?.components?.character;
        if (ch != null) ch.control_enabled = true;
        _weDisabledControl = false;

        if (!_longWalkActive) return;
        var target = _longWalkTarget;

        // Exit-assist arrival: at the door. Face it and remind the player to step outside.
        if (_exitAssisting)
        {
            _exitAssisting = false;
            _longWalkActive = false;
            _walkFacePos = target.Position;
            FacePlayerAtTarget();
            SetArrivedTarget(target.Object);
            ScreenReader.Say($"At the door. Press E to step outside, then go to {_exitAssistLabel} again.", interrupt: true);
            _log?.LogInfo("[NAVIGATOR] Exit-assist reached door");
            return;
        }

        if (_routeReachesTarget)
        {
            // The native walk landed at the approach point next to the target. Do NOT run a
            // graph-2 "final approach": the player A* graph can't path onto teleport/door tiles,
            // so it fails and falsely says "Could not reach" even though we arrived. Just face the
            // target so vanilla E interacts. If we only got to a pulled-back island edge, the
            // player is still some way off — report the remaining gap instead of "Arrived".
            _longWalkActive = false;
            var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
            var remaining = Vector2.Distance(playerPos, target.Position);
            _log?.LogInfo($"[NAVIGATOR] Native walk ended {remaining:F0}u from {target.Label}");

            // Stations/build desks need the player within ~1 tile to interact; doors/teleports can
            // be triggered from the lenient AtTargetDistance. Pick the right "arrived" radius so a
            // close-interaction target that ended a tile-plus short still gets the precise final
            // approach below (onto its synthetic dock) instead of being declared arrived too far out.
            var arrivedRadius = NeedsCloseInteraction(target.Object)
                ? InteractionArrivalDistance
                : AtTargetDistance;

            if (remaining <= arrivedRadius)
            {
                // At the interaction tile. Face it (don't graph-2 onto teleport tiles, which
                // fails) so vanilla E works.
                _walkFacePos = target.Position;
                FacePlayerAtTarget();
                SetArrivedTarget(target.Object);
                ScreenReader.Say($"Arrived at {target.Label}, {DistanceText(remaining)}", interrupt: true);
            }
            else if (remaining <= FinalApproachReach)
            {
                // Ended short (pulled back to an island edge near the target). Finish onto the door
                // with the player-graph A* so the player only needs to press E. WalkToTarget faces
                // the target and announces arrival, or "Could not reach" if even that last bit fails.
                _log?.LogInfo($"[NAVIGATOR] Final approach to {target.Label} ({remaining:F0}u)");
                WalkToTarget(target);
            }
            else
            {
                _walkFacePos = target.Position;
                FacePlayerAtTarget();
                ScreenReader.Say($"As close as auto-walk can get to {target.Label}, {DistanceText(remaining)}. It's {DirectionTo(target)}walk the rest yourself.", interrupt: true);
            }
        }
        else if (_finalPartial)
        {
            // Navmesh can't get any closer — this is the entrance / closest reachable point.
            _longWalkActive = false;
            var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
            _walkFacePos = target.Position;
            FacePlayerAtTarget();
            SetArrivedTarget(target.Object);
            ScreenReader.Say($"Arrived as close as I can walk to {target.Label}, {DistanceText(Vector2.Distance(playerPos, target.Position))}. Look for the entrance ahead.", interrupt: true);
            _log?.LogInfo($"[NAVIGATOR] Reached closest navmesh point to {target.Label}");
        }
        else
        {
            // Partial route still closing in: re-route from here to continue into the next region.
            _routeNeedsRecompute = true;
        }
    }

    private static void OnNativeWalkFailed()
    {
        _isWalking = false;
        ReleaseScriptControl();
        if (!_longWalkActive) return;
        // The native follower got stuck. Re-route once from here; the stuck monitor in
        // TickLongWalk falls back to the beacon if re-routing keeps failing.
        _log?.LogWarning("[NAVIGATOR] Native walk failed, re-routing");
        _routeNeedsRecompute = true;
    }

    /// <summary>
    /// No graph-0 route to the target. If the player is inside a building (a navmesh region
    /// disconnected from the outdoors), walk them to the nearest exit door and tell them to press
    /// E to step outside, then retry — otherwise fall back to the manual compass beacon.
    /// </summary>
    private static void HandleNoRoute()
    {
        var character = MainGame.me?.player?.components?.character;
        bool inside = character != null &&
                      character.cur_environment == BaseCharacterComponent.Environment.Inside;

        if (inside && !_exitAssisting)
        {
            var door = NearestDoor();
            if (door != null)
            {
                _exitAssisting = true;
                _exitAssistLabel = _longWalkTarget.Label;
                _longWalkTarget = door.Value;
                _longWalkDest = ApproachPoint(door.Value.Position);
                _routeReachesTarget = true;
                _finalPartial = false;
                ScreenReader.Say($"You are inside a building. Walking to the nearest door — press E to step outside, then go to {_exitAssistLabel} again.", interrupt: true);
                _log?.LogInfo($"[NAVIGATOR] Inside building; exit-assist to {door.Value.Label}");
                RequestGraph0Route(MainGame.me.player.pos, _longWalkDest);
                return;
            }
        }

        // Target on a graph-0 island (e.g. the house): pull the destination toward the player and
        // retry. The first point that routes is the reachable navmesh nearest the target; we walk
        // there and OnNativeWalkComplete reports the remaining gap to the real target.
        var player = MainGame.me?.player;
        if (player != null && _pullbackTries < MaxPullbackTries)
        {
            var pp = player.pos;
            var toPlayer = pp - _longWalkDest;
            var d = toPlayer.magnitude;
            if (d > PullbackMinToPlayer)
            {
                _pullbackTries++;
                _longWalkDest += toPlayer / d * Mathf.Min(PullbackStep, d - PullbackMinToPlayer);
                _log?.LogInfo($"[NAVIGATOR] Unreachable; pulling dest toward player (try {_pullbackTries}) -> {_longWalkDest}");
                RequestGraph0Route(pp, _longWalkDest);
                return;
            }
        }

        BeaconBail("Graph-0 route unavailable");
    }

    private static NavigationTarget? NearestDoor()
    {
        var doors = _byCategory[NavCategory.Doors];
        if (doors.Count == 0) return null;
        var pp = MainGame.me?.player?.pos ?? Vector2.zero;
        NavigationTarget best = default;
        float bestSq = float.MaxValue;
        bool found = false;
        foreach (var d in doors)
        {
            float sq = (d.Position - pp).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = d; found = true; }
        }
        return found ? best : (NavigationTarget?)null;
    }

    private static void BeaconBail(string reason)
    {
        var target = _longWalkTarget;
        _longWalkActive = false;
        ScreenReader.Say($"No clear auto-walk path. Switching to manual guidance to {target.Label}.", interrupt: true);
        _log?.LogWarning($"[NAVIGATOR] {reason}; beacon fallback");
        StartBeacon(target);
    }

    internal static void StopLongWalk(bool announce)
    {
        if (!_longWalkActive) return;
        _longWalkActive = false;
        _routePending = false;
        _routeNeedsRecompute = false;
        _exitAssisting = false;
        ReleaseScriptControl();
        _isWalking = false;
        if (announce)
            ScreenReader.Say("Walking stopped", interrupt: true);
        _log?.LogInfo("[NAVIGATOR] Long walk stopped");
    }

    /// <summary>
    /// Per-frame monitor while a long walk is active. The native follower does the moving; this
    /// only handles route re-requests, periodic progress announcements, and a stuck watchdog that
    /// bails to the compass beacon if the player stops making progress (or the walk drops out
    /// without a completion callback).
    /// </summary>
    private static void TickLongWalk()
    {
        var player = MainGame.me?.player;
        if (player == null) { StopLongWalk(announce: false); return; }

        var playerPos = player.pos;
        var target = _longWalkTarget;

        // Waiting on an async route query.
        if (_routePending) return;

        // A re-route was requested (partial-route chaining, or recovery after a stuck).
        if (_routeNeedsRecompute)
        {
            _routeNeedsRecompute = false;
            _log?.LogInfo($"[NAVIGATOR] Recomputing route from {playerPos}");
            RequestGraph0Route(playerPos, _longWalkDest);
            return;
        }

        // Stuck watchdog: progress resets it; no progress for StuckTickLimit ticks (or the native
        // walk dropping out without finishing) hands off to manual guidance.
        if (Vector2.Distance(playerPos, _longWalkProgressPos) >= ProgressDistance)
        {
            _longWalkProgressPos = playerPos;
            _longWalkStuckTicks = 0;
        }
        else if (!_isWalking || ++_longWalkStuckTicks >= StuckTickLimit)
        {
            _longWalkActive = false;
            ScreenReader.Say($"Auto-walk is blocked. Switching to manual guidance to {target.Label}.", interrupt: true);
            _log?.LogWarning($"[NAVIGATOR] Long walk stuck near {playerPos} (walking={_isWalking}), beacon fallback");
            StartBeacon(target);
            return;
        }

        // Periodic remaining-distance announcement so the player knows it's progressing.
        if (Vector2.Distance(playerPos, _longWalkAnnouncePos) >= AnnounceProgressDistance)
        {
            _longWalkAnnouncePos = playerPos;
            ScreenReader.Say($"{target.Label}, {DistanceText(Vector2.Distance(playerPos, target.Position))}", interrupt: false);
        }
    }

    // ---- Compass beacon (manual fallback guidance) -------------------------

    private static void StartBeacon(NavigationTarget target)
    {
        // The player walks manually in beacon mode, so make sure scripted control is released
        // (a failed auto-walk hop can leave the player frozen otherwise).
        ReleaseScriptControl();
        _isWalking = false;
        _beaconActive = true;
        _beaconTarget = target;
        var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
        _beaconLastAnnouncePos = playerPos;
        _log?.LogInfo($"[NAVIGATOR] Beacon started to {target.Label}");
        AnnounceBeacon(playerPos, prefix: "Guiding to ");
    }

    internal static void StopBeacon(bool announce = true)
    {
        if (!_beaconActive) return;
        _beaconActive = false;
        if (announce)
            ScreenReader.Say("Guidance stopped", interrupt: true);
        _log?.LogInfo("[NAVIGATOR] Beacon stopped");
    }

    /// <summary>
    /// Per-tick beacon driver: re-announce bearing + distance as the player moves, and once
    /// they are within A* range hand off to the precise auto-walk for the final approach.
    /// </summary>
    private static void UpdateBeacon()
    {
        var player = MainGame.me?.player;
        if (player == null) { StopBeacon(announce: false); return; }

        var playerPos = player.pos;
        var dist = Vector2.Distance(playerPos, _beaconTarget.Position);

        // Close enough for the player graph to path the rest of the way: finish with A*. If A* just
        // failed for this target, don't hand straight back to it (that re-escalates, bails here, and
        // loops in place). Wait until the player has moved HandoffRetryDistance closer — by then the
        // destination area has usually streamed in/activated, so the retry succeeds. Until then the
        // beacon keeps giving manual guidance as the player walks the last stretch.
        bool astarRetryReady = !_astarFailedForWalk ||
            Vector2.Distance(playerPos, _astarFailPos) >= HandoffRetryDistance;
        if (dist <= BeaconHandoffDistance && astarRetryReady)
        {
            var target = _beaconTarget;
            _beaconActive = false;
            ScreenReader.Say($"{target.Label} is close. Walking the rest of the way.", interrupt: true);
            _log?.LogInfo($"[NAVIGATOR] Beacon handoff to A* for {target.Label} at {dist:F0}u");
            WalkToTarget(target);
            return;
        }

        // Otherwise re-announce the heading each time the player has moved a fair distance.
        if (Vector2.Distance(playerPos, _beaconLastAnnouncePos) >= BeaconReannounceDistance)
        {
            _beaconLastAnnouncePos = playerPos;
            AnnounceBeacon(playerPos);
        }
    }

    private static void AnnounceBeacon(Vector2 playerPos, string prefix = "")
    {
        var dir = CompassDirection(playerPos, _beaconTarget.Position);
        var dist = Vector2.Distance(playerPos, _beaconTarget.Position);
        ScreenReader.Say($"{prefix}{_beaconTarget.Label}, {dir}, {DistanceText(dist)}", interrupt: true);
    }

    /// <summary>
    /// Eight-point compass direction from one world point toward another. The world plane is
    /// x-y with +x east and +y north, so the bearing is atan2(dy, dx).
    /// </summary>
    private static string CompassDirection(Vector2 from, Vector2 to)
    {
        var d = to - from;
        if (d.sqrMagnitude < 1f) return "here";

        // 0 deg = east, increasing counter-clockwise. Convert to a 0..8 sector.
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        int sector = Mathf.RoundToInt(angle / 45f) % 8;
        return sector switch
        {
            0 => "east",
            1 => "north-east",
            2 => "north",
            3 => "north-west",
            4 => "west",
            5 => "south-west",
            6 => "south",
            7 => "south-east",
            _ => "",
        };
    }

    /// <summary>
    /// Turn the player to face the object we just walked to. The game's interaction
    /// fires only on whatever sits inside the character's forward-facing interaction
    /// collider (positioned by anim_direction), so a blind player who auto-walked up to
    /// an object usually isn't facing it and plain E does nothing. Facing the object on
    /// arrival rotates that collider onto it, so the vanilla E key just works.
    /// </summary>
    /// <summary>
    /// Record the object we just reached so vanilla E targets it even when another interactable is
    /// closer/more aligned. Ground drops (<paramref name="obj"/> == null) are excluded: they're
    /// picked up via the game's own highlighted-drop path, not the interaction component.
    /// </summary>
    private static void SetArrivedTarget(WorldGameObject obj)
    {
        _arrivedTarget = obj;
        _arrivedTargetPos = MainGame.me?.player?.pos ?? Vector2.zero;
    }

    private static void ClearArrivedTarget() => _arrivedTarget = null;

    /// <summary>
    /// The object the player auto-walked to, while they're still standing at it — so the
    /// E-interaction patch can prefer it over a different interactable that happens to be nearer or
    /// better aligned. Null once the player walks away from it (or it's gone/removed). Read by
    /// <see cref="Patches.InteractionComponent_FindCurrentInteractionNearest_Postfix"/>.
    /// </summary>
    internal static WorldGameObject PreferredInteractionTarget()
    {
        var obj = _arrivedTarget;
        if (obj == null) return null;
        try
        {
            if (obj.is_removed || obj.gameObject == null || !obj.gameObject.activeInHierarchy)
            {
                _arrivedTarget = null;
                return null;
            }
            var pp = MainGame.me?.player?.pos ?? Vector2.zero;
            if (Vector2.Distance(pp, _arrivedTargetPos) > ArrivedTargetHoldDistance)
            {
                _arrivedTarget = null;
                return null;
            }
        }
        catch
        {
            _arrivedTarget = null;
            return null;
        }
        return obj;
    }

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
    /// The game's own "stand here to interact" tile for an object: the nearest usable
    /// <see cref="DockPoint"/> to the player (same mechanism the player uses when you tap an
    /// object). Walking onto the dock tile and facing its action direction lands you exactly
    /// where vanilla E/F works, instead of <see cref="ApproachPoint"/>'s crude back-off toward
    /// wherever you happen to be standing (which leaves you a tile off-axis on e.g. doors).
    /// Returns false when the object has no dock points so callers fall back to ApproachPoint.
    /// </summary>
    private static bool TryDockDestination(WorldGameObject obj, out Vector2 dest, out Vector2 facePos)
    {
        dest = Vector2.zero;
        facePos = Vector2.zero;
        try
        {
            if (obj == null) return false;

            var docks = obj.RefindDockPointsAndGet();
            if (docks == null || docks.Length == 0)
            {
                _log?.LogInfo($"[NAVIGATOR] {obj.name} has no dock points; using approach offset");
                return false;
            }

            var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
            DockPoint best = null;          // nearest reachable dock
            float bestSq = float.MaxValue;
            DockPoint fallback = null;      // nearest dock ignoring reachability
            float fallbackSq = float.MaxValue;

            foreach (var dp in docks)
            {
                if (dp == null || dp.tf == null) continue;
                if (!dp.gameObject.activeInHierarchy) continue;
                if (dp.shouldnt_be_used) continue;

                float sq = ((Vector2)dp.tf.position - playerPos).sqrMagnitude;
                if (sq < fallbackSq) { fallbackSq = sq; fallback = dp; }

                if (dp.IsUnreachable(15.36f)) continue;   // blocked by another object
                if (sq < bestSq) { bestSq = sq; best = dp; }
            }

            var chosen = best ?? fallback;   // prefer reachable; otherwise nearest anyway
            if (chosen == null) return false;

            dest = chosen.tf.position;
            facePos = (Vector2)chosen.tf.position + chosen.GetActionDir().ToVec();
            _log?.LogInfo($"[NAVIGATOR] {obj.name} dock dest={dest} (of {docks.Length}, reachable={best != null})");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] TryDockDestination failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Pick the destination tile to walk to for a target: ground drops land on their exact
    /// tile; objects with dock points use the game's interaction tile (and report the point to
    /// face on arrival); everything else falls back to <see cref="ApproachPoint"/>.
    /// </summary>
    private static Vector2 InteractionDest(NavigationTarget target, out Vector2? facePos)
    {
        facePos = target.Position;
        if (target.IsDrop) return target.Position;
        if (TryDockDestination(target.Object, out var dock, out var face))
        {
            facePos = face;
            return dock;
        }

        // No dock points, but the object still needs the player INSIDE its interaction zone to use
        // (a build desk, craft station, chest, grave). Some of these ship without dock points (e.g.
        // cellar_builddesk), and the door/teleport back-off below would leave the player a tile off
        // on whatever side they happened to approach from — outside the interaction overlap, so
        // vanilla E does nothing. Synthesize a dock: a walkable tile right beside the collider on
        // the side nearest the player, faced toward the collider centre.
        if (NeedsCloseInteraction(target.Object) &&
            TrySyntheticDock(target.Object, out var synth, out var synthFace))
        {
            facePos = synthFace;
            return synth;
        }

        // No dock points (doors/teleports and similar). Back off from the object's COLLIDER
        // centre, not its pos: pos is the depth-sort anchor (often the top of a doorway), while
        // the interactive collider sits offset from it (e.g. one tile south for a door). Backing
        // off from pos lands the player too far from the collider — they'd still have to step
        // toward it. The collider centre is where the game's interaction overlap actually happens.
        var basis = InteractionBasis(target.Object, target.Position);
        facePos = basis;
        return ApproachPoint(basis);
    }

    /// <summary>
    /// The point the player must reach to interact with a dock-less object: the centre of its
    /// collider bounds (where the interaction overlap test fires), falling back to the object's
    /// pos when it has no colliders. Differs from pos mainly for doors and other objects whose
    /// sprite/collider is offset from the depth-sort anchor.
    /// </summary>
    private static Vector2 InteractionBasis(WorldGameObject obj, Vector2 pos)
    {
        try
        {
            if (obj == null) return pos;
            var b = obj.GetTotalBounds();
            if (b.size.sqrMagnitude <= 0.0001f) return pos;   // no colliders -> default bounds
            var center = new Vector2(b.center.x, b.center.y);
            _log?.LogInfo($"[NAVIGATOR] {obj.name} interaction basis: pos={pos} colliderCenter={center}");
            return center;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] InteractionBasis failed: {ex.Message}");
            return pos;
        }
    }

    /// <summary>
    /// True for objects the player must stand INSIDE the interaction overlap zone to use (build
    /// desks, craft/script-craft stations, chests, graves) — as opposed to doors/teleports, which
    /// the game lets you trigger from a tile back. Only these get the synthetic dock + tighter
    /// arrival, so door/teleport approach behaviour is left untouched.
    /// </summary>
    private static bool NeedsCloseInteraction(WorldGameObject obj)
    {
        try
        {
            var def = obj?.obj_def;
            if (def == null) return false;
            switch (def.interaction_type)
            {
                case ObjectDefinition.InteractionType.Builder:
                case ObjectDefinition.InteractionType.Craft:
                case ObjectDefinition.InteractionType.Chest:
                case ObjectDefinition.InteractionType.Grave:
                    return true;
                case ObjectDefinition.InteractionType.RunScript:
                    return def.has_craft;   // script crafting stations (e.g. autopsy table)
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Build a synthetic "stand here" tile for an interactive object that has no dock points (e.g.
    /// cellar_builddesk). Mirrors what dock points do: test tiles just beyond the collider edge in
    /// the eight compass directions, keep the ones that snap to walkable navmesh, and pick the one
    /// nearest the player (so we approach from the open side they're on). The face point is the
    /// collider centre so the player's forward interaction collider lands on the object and vanilla
    /// E works. Returns false when the object has no real collider or no walkable tile beside it.
    /// </summary>
    private static bool TrySyntheticDock(WorldGameObject obj, out Vector2 dest, out Vector2 facePos)
    {
        dest = Vector2.zero;
        facePos = Vector2.zero;
        try
        {
            if (obj == null) return false;
            var b = obj.GetTotalBounds();
            if (b.size.sqrMagnitude <= 0.0001f) return false;   // no colliders to stand beside

            var center = new Vector2(b.center.x, b.center.y);
            var ext = new Vector2(b.extents.x, b.extents.y);
            var playerPos = MainGame.me?.player?.pos ?? center;

            const float gap = 0.5f * TileSize;        // stand ~half a tile off the collider edge
            const float maxSnap = 0.75f * TileSize;   // reject a side with no walkable tile nearby
            const float diag = 0.7071f;

            var dirs = new[]
            {
                new Vector2( 1f,  0f), new Vector2(-1f,  0f), new Vector2( 0f,  1f), new Vector2( 0f, -1f),
                new Vector2( diag,  diag), new Vector2(-diag,  diag),
                new Vector2( diag, -diag), new Vector2(-diag, -diag),
            };

            bool found = false;
            Vector2 best = Vector2.zero;
            float bestScore = float.MaxValue;

            foreach (var d in dirs)
            {
                var cand = center + new Vector2(d.x * (ext.x + gap), d.y * (ext.y + gap));
                if (!TrySnapGraph0(cand, out var snapped, out var snapDist)) continue;
                if (snapDist > maxSnap) continue;     // nothing walkable beside the collider here
                float score = Vector2.Distance(snapped, playerPos);   // prefer the player's side
                if (score < bestScore)
                {
                    bestScore = score;
                    best = snapped;
                    found = true;
                }
            }

            if (!found) return false;
            dest = best;
            facePos = center;
            _log?.LogInfo($"[NAVIGATOR] {obj.name} synthetic dock dest={dest} (no dock points)");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] TrySyntheticDock failed: {ex.Message}");
            return false;
        }
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

    /// <summary>
    /// Snap a world point to the nearest walkable node on graph 0 (the whole-map NPC navmesh,
    /// always scanned — no RefreshPlayerGraph needed). Used to pull a landmark anchor that sits
    /// on a navmesh void (building wall/interior) onto a real node so a route can be found, and
    /// to tell whether a candidate door is actually on the navmesh. Returns false if no node.
    /// </summary>
    private static bool TrySnapGraph0(Vector2 p, out Vector2 snapped, out float dist)
    {
        snapped = p;
        dist = float.MaxValue;
        try
        {
            var astar = AstarPath.active;
            if (astar == null) return false;

            var constraint = Pathfinding.NNConstraint.Default;
            constraint.graphMask = 1 << 0;  // graph 0 only

            var nn = astar.GetNearest(new Vector3(p.x, p.y, 0f), constraint);
            if (nn.node != null && nn.node.Walkable)
            {
                snapped = new Vector2(nn.clampedPosition.x, nn.clampedPosition.y);
                dist = Vector2.Distance(p, snapped);
                return true;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] TrySnapGraph0 failed: {ex.Message}");
        }
        return false;
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
                    // Bias vanilla E onto the object we walked to, not a closer neighbour.
                    SetArrivedTarget(_shortWalkTarget.Object);
                    ScreenReader.Say($"Arrived at {label}", interrupt: true);
                    _log?.LogInfo($"[NAVIGATOR] Arrived at {label} ({method})");
                },
                on_failed: () =>
                {
                    if (method == MovementComponent.GoToMethod.AStar)
                    {
                        // Remember A* couldn't reach this target, and from where, so the beacon won't
                        // keep handing back to it in place (which would re-escalate and loop forever)
                        // but WILL retry once the player has walked closer and the area has activated.
                        _astarFailedForWalk = true;
                        _astarFailPos = MainGame.me?.player?.pos ?? Vector2.zero;
                        // A* failed (no path / endpoint too far) — typically the target sits
                        // behind a fence the player graph can't path through. Escalate to the
                        // fence-aware graph-0 route (threads gates like an NPC) instead of a
                        // straight line that just jams on the rail. Deferred to next frame for
                        // the same reason as the Direct fallback (OnPathFailed runs right after
                        // this callback). Falls through to Direct only if we have no target to
                        // escalate with.
                        if (_shortWalkTarget.Object != null || _shortWalkTarget.DropGo != null)
                        {
                            _log?.LogWarning($"[NAVIGATOR] A* failed to {label}, escalating to fence-aware route");
                            _escalatePending = true;
                        }
                        else
                        {
                            _log?.LogWarning($"[NAVIGATOR] A* failed to {label}, trying direct");
                            _fallbackDest = dest;
                            _fallbackLabel = label;
                            _fallbackPending = true;
                        }
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

    /// <summary>
    /// Cancel whatever navigation is active — the compass beacon or an in-progress
    /// auto-walk. Bound to Escape so one key always stops guidance.
    /// </summary>
    internal static void CancelNavigation()
    {
        if (_longWalkActive) StopLongWalk(announce: true);
        else if (_beaconActive) StopBeacon();
        else if (_isWalking) StopWalking();
    }

    /// <summary>
    /// Full navigation teardown after a teleport. Unlike CancelNavigation (which only handles the
    /// three top-level states), this clears every pending/in-flight flag so no stale route, hop, or
    /// beacon survives the position jump, releases scripted control, and gives a single short notice.
    /// </summary>
    private static void AbortForTeleport()
    {
        try
        {
            _longWalkActive = false;
            _beaconActive = false;
            _isWalking = false;
            _routePending = false;
            _routeNeedsRecompute = false;
            _exitAssisting = false;
            _fallbackPending = false;
            _escalatePending = false;
            _walkWatchdog = 0;
            _longWalkStuckTicks = 0;
            _pullbackTries = 0;
            _stalledRecomputes = 0;
            _astarFailedForWalk = false;
            _exitAssisting = false;
            _hasBusyPos = false;
            ClearArrivedTarget();
            ReleaseScriptControl();
            ScreenReader.Say("Navigation cancelled", interrupt: true);
            _log?.LogInfo("[NAVIGATOR] Navigation aborted after teleport");
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error aborting navigation after teleport: {ex.Message}");
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
    /// Fired from a postfix on GS.SetPlayerEnable. A cutscene grabs the player with
    /// SetPlayerEnable(false, affect_cinematic:true) and hands control back with
    /// SetPlayerEnable(true, ...). If it fires mid auto-walk we must abandon our walk WITHOUT
    /// touching control/the body — the cutscene drives the player itself and any control_enabled
    /// = true from us would re-Dynamic the body and freeze the scene.
    /// </summary>
    internal static void OnGameSetPlayerEnable(bool playerEnabled, bool affectCinematic)
    {
        if (!playerEnabled && affectCinematic)
        {
            _gameOwnsPlayer = true;
            if (_isWalking || _longWalkActive || _beaconActive)
            {
                // Drop every walk flag so our monitors stop poking the player, but leave
                // control_enabled / cur_astar_path exactly as the cutscene set them.
                _isWalking = false;
                _longWalkActive = false;
                _beaconActive = false;
                _routePending = false;
                _routeNeedsRecompute = false;
                _exitAssisting = false;
                _log?.LogInfo("[NAVIGATOR] Cutscene took the player mid-walk; releasing without touching control");
            }
        }
        else if (playerEnabled)
        {
            _gameOwnsPlayer = false;
        }
    }

    /// <summary>
    /// Stop scripted movement and hand control back to the player. Safe to call
    /// redundantly; this is the guard against the player being locked out of input
    /// when the game's own OnPathFailed leaves player_controlled_by_script set.
    /// </summary>
    private static void ReleaseScriptControl()
    {
        // A cutscene owns the player right now — StopMovement would cancel its scripted walk and
        // control_enabled = true would re-Dynamic the body and jam the scene. Stay out of its way.
        if (_gameOwnsPlayer) return;
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character != null)
            {
                character.StopMovement();
                character.player_controlled_by_script = false;
                character.control_enabled = true;   // re-enable input + restore Dynamic body
                _weDisabledControl = false;
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

            // No x-ray for blind players: when the player is in an enclosed interior, a sighted
            // player can't see the outdoor world through the walls, so the tracker shouldn't either.
            // The game flags this with its interior LIGHTING preset — EnvironmentEngine state goes
            // Inside for dungeons, the mortuary, the tavern and other teleport interiors (it stays
            // RealTime in open, roof-less areas like the keeper's yard, which therefore keep showing
            // distant objects, exactly as a sighted player outdoors would see them). In a real
            // interior the game already culls (deactivates) every outdoor object; the ONLY ones that
            // still leak are harvestables, which we deliberately keep listed even when culled so a
            // blind player can find distant ore. So while sight is wall-blocked we drop that
            // exception and require harvestables to be active too — see the cull check below.
            bool interiorSightBlocked =
                EnvironmentEngine.me?.data?.state == EnvironmentEngine.State.Inside;

            // Remember what is currently selected so we can keep the cursor on it
            // across refreshes even as distances change. Drops have no WorldGameObject,
            // so track their GameObject separately.
            WorldGameObject previouslySelected = null;
            GameObject previouslySelectedDrop = null;
            string previouslySelectedLabel = null;
            var curList = CurrentList;
            if (curList.Count > 0 && _selectedIndex < curList.Count)
            {
                previouslySelected = curList[_selectedIndex].Object;
                previouslySelectedDrop = curList[_selectedIndex].DropGo;
                previouslySelectedLabel = curList[_selectedIndex].Label;
            }

            foreach (var cat in _categoryOrder)
                _byCategory[cat].Clear();

            foreach (var obj in allObjects)
            {
              // Per-object guard: a single malformed object must never abort the whole refresh.
              // Including inactive (culled) objects below means we occasionally hit a pooled/half-
              // initialized WorldGameObject whose transform/components are null and throws on
              // obj.pos or labelling — skip just that one instead of losing landmarks/quests/items
              // (gathered after this loop) to a thrown exception.
              try
              {
                if (obj == null || obj.is_removed) continue;
                if (InteractionDetector.IsPlayer(obj) || InteractionDetector.IsPrefab(obj)) continue;

                if (!TryClassify(obj, out var category)) continue;

                // The game culls off-screen objects by deactivating their GameObject (they
                // reactivate on interaction via WorldGameObject.OnWorkAction). For most categories
                // we only list active objects, otherwise culled duplicates from other contexts
                // (doors/graves loaded but inactive while you're indoors, etc.) pollute the lists.
                // Resource nodes are normally the exception: a blind player can't pan the camera to
                // spot a culled iron-ore rock a few tiles away, and these are simple static world
                // objects that stay valid while culled — so we keep harvestables navigable even when
                // culled. BUT inside a wall-enclosed interior that exception would x-ray the whole
                // outdoor world (which is all culled), so there we require harvestables to be active
                // too — the surviving active ones are only those in the interior with the player.
                bool keepIfCulled = IsHarvestableCategory(category) && !interiorSightBlocked;
                if (!keepIfCulled && !obj.gameObject.activeInHierarchy) continue;

                var objPos = obj.pos;
                var distance = Vector2.Distance(objPos, playerPos);
                var maxDist = IsHarvestableCategory(category) ? MaxHarvestableNavDistance : MaxNavDistance;
                if (distance > maxDist) continue;

                var label = GetObjectLabelSafe(obj);
                _byCategory[category].Add(new NavigationTarget
                {
                    Object = obj,
                    Label = label,
                    Position = objPos,
                    Distance = distance
                });

                // A grave holding a body can be exhumed (needs the exhumation permit). Mirror
                // those into a dedicated focused list so the player can jump straight to a
                // dig-able grave instead of cycling every tombstone. They stay in Graves too,
                // so the general browse remains complete.
                if (category == NavCategory.Graves && HasExhumableBody(obj))
                {
                    _byCategory[NavCategory.ExhumableGraves].Add(new NavigationTarget
                    {
                        Object = obj,
                        Label = label,
                        Position = objPos,
                        Distance = distance
                    });
                }

                // A grave's fence (and cross) wear down over time and can be repaired with a
                // repair kit from the grave menu. Mirror graves whose fence is worn into the
                // Fences list so the player can head straight to one that needs a kit. They stay
                // under Graves too, so the general browse stays complete.
                if (category == NavCategory.Graves && TryGetWornFence(obj, out var fenceDesc))
                {
                    _byCategory[NavCategory.Fences].Add(new NavigationTarget
                    {
                        Object = obj,
                        Label = $"{fenceDesc}, {label}",
                        Position = objPos,
                        Distance = distance
                    });
                }

                // Graves missing a fence and/or cross can have decoration added (open with E, pick
                // the empty slot). Mirror them into a dedicated list so the player can go straight
                // to one to decorate it instead of cycling every grave. They stay under Graves too.
                if (category == NavCategory.Graves && TryGetMissingDecoration(obj, out var decoDesc))
                {
                    _byCategory[NavCategory.GravesToDecorate].Add(new NavigationTarget
                    {
                        Object = obj,
                        Label = $"{decoDesc}, {label}",
                        Position = objPos,
                        Distance = distance
                    });
                }

                // Any object holding a LOOSE corpse — a morgue bed/fridge, or a prep/autopsy
                // table — is mirrored into the Corpses list so the player can jump straight to a
                // body the moment the donkey delivers it. Graves are deliberately excluded: an
                // interred body is already covered by Graves/ExhumableGraves, and the whole point
                // of this list is to surface fresh corpses that still need processing, not the
                // dozens of bodies already buried in the graveyard.
                if (category != NavCategory.Graves && HoldsBody(obj))
                {
                    _byCategory[NavCategory.Corpses].Add(new NavigationTarget
                    {
                        Object = obj,
                        Label = label,
                        Position = objPos,
                        Distance = distance
                    });
                }
              }
              catch { /* skip this one object, keep building the rest of the list */ }
            }

            // Active quest targets are gathered separately: they are resolved by
            // obj_id from the save's task list (not by walking the scene), and they
            // bypass the distance cap so a far-off quest objective always shows up.
            GatherQuestTargets(playerPos);

            // Fixed landmarks (Tavern, Church, home Graveyard). These are world zones that
            // are always loaded regardless of distance, so they give a blind player a way to
            // set off toward a far destination from anywhere — the compass beacon then guides.
            GatherLandmarkTargets(playerPos, allObjects);

            // Ground drops (bodies/loot) are DropResGameObjects, not WorldGameObjects, so
            // they need their own scan or they stay invisible to the screen reader. FindObjectsOfType
            // only returns ACTIVE drops, so outdoor drops culled while you're in an interior are
            // already excluded — no extra no-x-ray handling is needed here.
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
            // Landmarks and quest targets have no Object/DropGo, so fall back to matching by
            // label to keep the cursor on the same entry across refreshes.
            var list = CurrentList;
            if (previouslySelected != null || previouslySelectedDrop != null || previouslySelectedLabel != null)
            {
                var idx = list.FindIndex(t =>
                    (previouslySelected != null && t.Object == previouslySelected) ||
                    (previouslySelectedDrop != null && t.DropGo == previouslySelectedDrop) ||
                    (previouslySelected == null && previouslySelectedDrop == null &&
                     previouslySelectedLabel != null && t.Object == null && t.DropGo == null &&
                     t.Label == previouslySelectedLabel));
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

    // Named-NPC landmarks: key shops/services that are world objects rather than zones,
    // resolved by obj_id map-wide.
    private static readonly (string objId, string label)[] NpcLandmarks =
    {
        ("npc_merchant", "Merchant"),
    };

    // Building landmarks anchored on their EXTERIOR entrance door (a teleport WGO), not an interior
    // NPC/zone — interiors are separate, navmesh-disconnected regions auto-walk can't reach. The
    // door's place comes from its custom_tag (InteractionDetector.DoorPlaceFromTag). (place, label).
    private static readonly (string doorPlace, string label)[] DoorLandmarks =
    {
        ("Tavern", "Tavern"),
        ("House", "Home"),
        // The church IS a separate teleport interior (place tag "Church", a "teleport_outside"
        // door at the graveyard). Its zone members (pulpit, candles) are staged far away inside,
        // so the generic zone anchor sent auto-walk indoors — anchor on the real outdoor door
        // instead, exactly like the Tavern/Home (the Doors category's "Door outside: Church").
        ("Church", "Church"),
    };

    // World-zone ids NOT to add as landmarks — superseded by a door landmark above (the zone's
    // geometric centre is inside the building and unroutable).
    private static readonly HashSet<string> SkipZoneIds = new() { "home" };

    // Friendlier spoken names for known world-zone ids; any zone not listed falls back to its
    // prettified id so every zone in the world is still reachable.
    private static readonly Dictionary<string, string> ZoneLabelOverrides = new()
    {
        ["graveyard"] = "Graveyard, home",
        ["church"] = "Church",
        ["players_tavern"] = "Player's tavern, DLC",
        ["player_tavern_cellar"] = "Player's tavern cellar, DLC",
        ["refugees_camp"] = "Refugee camp",
    };

    /// <summary>
    /// Which DLC (if any) a world zone belongs to, or null for base-game zones. DLC zones
    /// (the Stranger Sins player tavern, the Game of Crone refugee camp, etc.) ship in the
    /// scene as always-active GameObjects even when you don't own the DLC — the game gates
    /// them by quest-unlock / DisableWorldZone, NOT by deactivating the object — so neither
    /// IsDisabled() nor activeInHierarchy filters them. We map the zone id to its DLC and
    /// hide it unless <see cref="DLCEngine.IsDLCAvailable"/> says you own it.
    ///
    /// IsDLCAvailable is a LIVE check for the DLC's gamedata_*.dat file, so this needs no code
    /// change to keep working: the moment you buy a DLC its zones start appearing, and if you
    /// don't own it they stay hidden. Match by substring so id variants (player_tavern_cellar,
    /// players_tavern_2, ...) are all covered.
    /// </summary>
    private static DLCEngine.DLCVersion? ZoneRequiredDLC(string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId)) return null;
        var id = zoneId.ToLowerInvariant();

        // Stranger Sins — the player-run tavern and its cellar (the town tavern is base game,
        // so require BOTH "player" and "tavern" to avoid hiding any base-game tavern zone).
        if (id.Contains("tavern") && id.Contains("player"))
            return DLCEngine.DLCVersion.Stories;

        // Game of Crone — the refugee camp and Alarich's tent.
        if (id.Contains("refugee") || id.Contains("alarich") || id.Contains("crone"))
            return DLCEngine.DLCVersion.Refugees;

        // Better Save Soul — any soul-content zone (no base-game zone uses this word).
        if (id.Contains("soul"))
            return DLCEngine.DLCVersion.Souls;

        return null;
    }

    /// <summary>
    /// Populate the Landmarks category with key NPC services (Tavern barman, Merchant) and
    /// every world zone. Zones are always loaded, and the named NPCs resolve map-wide, so
    /// these targets exist even from across the map; the compass/auto-walk then heads there.
    /// Like quest targets, landmarks ignore the distance cap.
    /// </summary>
    private static void GatherLandmarkTargets(Vector2 playerPos, WorldGameObject[] allObjects)
    {
        try
        {
            var list = _byCategory[NavCategory.Landmarks];

            // Key NPC-anchored destinations.
            foreach (var (objId, label) in NpcLandmarks)
            {
                var wgo = WorldMap.GetWorldGameObjectByObjId(objId, ignore_not_found_error: true);
                if (wgo == null || wgo.is_removed || !wgo.gameObject.activeInHierarchy) continue;
                list.Add(new NavigationTarget
                {
                    Object = wgo,
                    Label = label,
                    Position = wgo.pos,
                    Distance = Vector2.Distance(wgo.pos, playerPos)
                });
            }

            // Building entrances (Tavern, Home), anchored on the exterior door you press E on.
            var doorLandmarkLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (doorPlace, label) in DoorLandmarks)
            {
                var door = FindEntranceDoor(allObjects, doorPlace, playerPos);
                if (door == null) continue;
                doorLandmarkLabels.Add(label);
                list.Add(new NavigationTarget
                {
                    Object = door,
                    Label = label,
                    Position = door.pos,
                    Distance = Vector2.Distance(door.pos, playerPos)
                });
            }

            // Every world zone, de-duplicated by id.
            var seenZones = new HashSet<string>();
            var zones = UnityEngine.Object.FindObjectsOfType<WorldZone>(true);
            foreach (var zone in zones)
            {
                if (zone == null || zone.IsDisabled()) continue;
                // Hide zones that belong to a DLC the player doesn't own. These zones are present
                // and active in the scene regardless of DLC, so we gate them on the live
                // gamedata_*.dat check (see ZoneRequiredDLC); buying the DLC makes them appear
                // automatically with no code change.
                var reqDlc = ZoneRequiredDLC(zone.id);
                if (reqDlc.HasValue && !DLCEngine.IsDLCAvailable(reqDlc.Value)) continue;
                if (string.IsNullOrEmpty(zone.id) || !seenZones.Add(zone.id)) continue;
                if (SkipZoneIds.Contains(zone.id)) continue;   // superseded by a door landmark
                // Skip a zone that duplicates a building-entrance landmark (e.g. the "tavern" zone vs
                // the Tavern door). The door anchors on the real outdoor entrance; the zone would
                // anchor on whatever member object is nearest — often the interior staging — giving a
                // second, wrong "Tavern" at a different distance.
                if (doorLandmarkLabels.Contains(ZoneLabel(zone.id))) continue;

                // Anchor on an actual object in the zone (closest to the player), NOT the
                // geometric centre: a zone centre often falls inside a building (the church in
                // the graveyard, etc.) — a disconnected navmesh pocket auto-walk can't route to.
                // Zone member objects sit on/next to walkable ground, so routing reaches them.
                var anchor = ZoneAnchorObject(zone, playerPos);
                var pos = anchor != null ? anchor.pos : (Vector2)(zone.center_tf?.position ?? Vector3.zero);
                if (zone.center_tf == null && anchor == null) continue;

                list.Add(new NavigationTarget
                {
                    Object = anchor,
                    Label = ZoneLabel(zone.id),
                    Position = pos,
                    Distance = Vector2.Distance(pos, playerPos)
                });
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error gathering landmark targets: {ex.Message}");
        }
    }

    /// <summary>
    /// Pick the zone's member object closest to the player as the zone's walkable anchor. Zone
    /// objects sit on/next to walkable ground (unlike the geometric centre, which can land inside
    /// a building), so auto-walk can actually route there. Null if the zone has no usable objects.
    /// </summary>
    private static WorldGameObject ZoneAnchorObject(WorldZone zone, Vector2 playerPos)
    {
        try
        {
            var wgos = zone.GetZoneWGOs();
            if (wgos == null) return null;
            WorldGameObject best = null;
            float bestSq = float.MaxValue;
            foreach (var w in wgos)
            {
                if (w == null || w.is_removed) continue;
                float sq = (w.pos - playerPos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = w; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>
    /// Find a building's exterior entrance door — the teleport WGO whose custom_tag resolves to
    /// <paramref name="place"/> (via the same logic that labels doors in the Doors category).
    ///
    /// Critically this uses the SAME filter the Doors category does: a USABLE door has
    /// <c>interaction_type != None</c>. The <c>None</c> teleports are non-interactive arrival
    /// ANCHORS (where you land); a door's anchor is what the old snap-distance heuristic kept
    /// latching onto, sending "Home" to an interior spot.
    ///
    /// Among the usable same-place doors we can't just take the one NEAREST the player: a building
    /// exposes several teleports under one place — the street entrance plus interior stairs/landings
    /// (e.g. "tp_tavern_up_to_2nd_floor", "tp_tavern_from_cellar"). The euclidean-nearest of those is
    /// often an interior door, sending auto-walk to "an inside door" instead of the entrance. The
    /// game already names each endpoint by its side of the wall — <c>teleport_outside</c> for the
    /// street entrance, <c>teleport_inside</c> for interior doors — so we rank by that name first
    /// (see <see cref="DoorNameTier"/>) and only break ties by distance. The genuine outside-door
    /// pick is cached per place (the entrance is stable); a fallback interior pick is not, so once
    /// the real entrance loads near the player it takes over.
    /// </summary>
    private static readonly Dictionary<string, WorldGameObject> _entranceDoorCache = new();

    private static WorldGameObject FindEntranceDoor(WorldGameObject[] allObjects, string place, Vector2 playerPos)
    {
        if (allObjects == null) return null;

        // Reuse the resolved outside entrance while it's still valid. We deliberately don't require
        // it to be active: the real entrance is culled to inactive while the player is far away (see
        // below), and it must stay the cached answer the whole way there.
        if (_entranceDoorCache.TryGetValue(place, out var cached) &&
            cached != null && !cached.is_removed)
            return cached;

        // Rank candidates by the game's own endpoint naming. A building exposes several teleports
        // under one place: the street entrance ("teleport_outside") plus interior doors/landings
        // ("teleport_inside" — e.g. tp_tavern_up_to_2nd_floor's staircase). Crucially the building's
        // INTERIOR is staged in a far-off corner of the world whose coordinates happen to sit near
        // the player's home region, so those interior teleports stay loaded/active near home while
        // the real outdoor entrance — way across the map — is culled to inactive. That's why the old
        // "skip inactive" + nearest logic kept choosing an inside door. So we do NOT filter on active
        // state here (FindObjectsOfType is scanned includeInactive, so the culled entrance is still
        // in the list) and instead tier strictly by name: outside first, then neutral, inside last.
        //
        // This function is only used for the base-game DoorLandmarks (Tavern, House); DLC buildings
        // carry distinct place tags (e.g. "players tavern"), so dropping the active filter doesn't
        // resurface not-owned DLC doors for these places.
        WorldGameObject best = null;
        int bestTier = int.MaxValue;
        float bestSq = float.MaxValue;
        foreach (var w in allObjects)
        {
            if (w == null || w.is_removed) continue;
            if (w.name.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!string.Equals(InteractionDetector.DoorPlaceFromTag(w.custom_tag), place,
                               StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip the non-usable arrival anchors (interaction_type None) exactly as the Doors
            // category does — those are landing spots, not the door you press E on, and some sit
            // inside the building.
            if (w.obj_def == null ||
                w.obj_def.interaction_type == ObjectDefinition.InteractionType.None)
                continue;

            int tier = DoorNameTier(w.name);     // 0 = outside, 1 = neutral, 2 = inside
            float sq = (w.pos - playerPos).sqrMagnitude;
            // Better tier wins outright; within a tier, take the nearest.
            if (tier < bestTier || (tier == bestTier && sq < bestSq))
            {
                bestTier = tier;
                bestSq = sq;
                best = w;
            }
        }

        // Cache only a genuine outside-door pick: it's the stable entrance. A neutral/inside pick
        // means this building has no outside-tagged door — don't pin it, so a better match can win
        // on a later refresh.
        if (best != null && bestTier == 0)
            _entranceDoorCache[place] = best;

        return best;
    }

    /// <summary>
    /// Tier a teleport WGO by the side of the building its spawn name marks it on:
    /// 0 = "teleport_outside" (the street-facing entrance to walk to), 2 = "teleport_inside" (an
    /// interior door/landing — stairs, back rooms), 1 = anything else. Lower is preferred.
    /// </summary>
    private static int DoorNameTier(string name)
    {
        if (string.IsNullOrEmpty(name)) return 1;
        if (name.IndexOf("outside", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
        if (name.IndexOf("inside", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
        return 1;
    }

    private static string ZoneLabel(string zoneId)
    {
        if (ZoneLabelOverrides.TryGetValue(zoneId, out var nice))
            return nice;

        // Prettify the raw id: "flat_under_waterflow_3" -> "Flat under waterflow 3".
        var text = zoneId.Replace('_', ' ').Replace('-', ' ').Trim();
        if (text.Length == 0) return zoneId;
        return char.ToUpper(text[0]) + text.Substring(1);
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

                var dropTarget = new NavigationTarget
                {
                    Object = null,
                    Label = GetDropLabelSafe(res),
                    Position = pos,
                    Distance = distance,
                    IsDrop = true,
                    DropGo = drop.gameObject
                };
                itemList.Add(dropTarget);

                // A corpse lying on the ground is also mirrored into the Corpses list so it shows
                // up alongside bodies in morgue storage and graves.
                if (res.definition.type == ItemDefinition.ItemType.Body)
                    _byCategory[NavCategory.Corpses].Add(dropTarget);
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
    /// <summary>
    /// True when a grave can actually be dug up via the GraveGUI "Exhume" button. Mirrors the
    /// game's own enable condition (GraveGUI.Redraw): the grave must hold a body AND be
    /// undecorated — placing a gravestone or fence locks the body in and disables exhuming.
    /// Most filled graves in the starting graveyard already have a body, so the body alone is
    /// far too broad a marker; the no-cross/no-fence test is what narrows it to graves you can
    /// dig right now (e.g. Yorick's neighbour). We skip the transient is_crafting check on
    /// purpose: reading obj.components lazily allocates a manager for every scene object each
    /// refresh, which the discovery loop deliberately avoids.
    /// </summary>
    /// <summary>
    /// True when an object currently holds a corpse in its inventory — regardless of where it
    /// sits. Unlike <see cref="HasExhumableBody"/> this drops the no-cross/no-fence test, so it
    /// catches bodies in morgue storage (corpse_bed / corpse_fridge), on prep / autopsy tables,
    /// and in any grave. Used to mirror corpse-holders into the dedicated Corpses list.
    /// </summary>
    private static bool HoldsBody(WorldGameObject obj)
    {
        try
        {
            var body = obj.GetBodyFromInventory();
            return body != null && body.definition != null
                && body.definition.type == ItemDefinition.ItemType.Body
                && !body.IsEmpty();
        }
        catch { return false; }
    }

    private static bool HasExhumableBody(WorldGameObject obj)
    {
        try
        {
            var body = obj.GetBodyFromInventory();
            if (body == null || body.definition == null
                || body.definition.type != ItemDefinition.ItemType.Body
                || body.IsEmpty())
                return false;

            // A cross or fence disables exhuming, exactly as GraveGUI does.
            var cross = obj.data.GetItemOfType(ItemDefinition.ItemType.GraveStone);
            var fence = obj.data.GetItemOfType(ItemDefinition.ItemType.GraveFence);
            return cross == null && fence == null;
        }
        catch { return false; }
    }

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

        // Broken/worn fences the player can fix with a repair kit. A fence is matched by its
        // obj_id ("fence") and only listed while it is actually repairable — i.e. it still
        // carries a repair craft (a Fixing craft, or a change_wgo craft that rebuilds the fence
        // rather than producing an item). Once repaired it swaps to an obj without that craft and
        // drops out of the list. Checked before the interaction_type switch because a repairable
        // fence often has interaction_type Craft and would otherwise be filed under Stations.
        if (!string.IsNullOrEmpty(obj.obj_id) &&
            obj.obj_id.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0 &&
            IsRepairableFence(obj))
        {
            category = NavCategory.Fences;
            return true;
        }

        // Roofs and other structural building pieces (obj_id contains "roof"): the player
        // builds these over a building via the hammer/build desk, and removes them the same
        // way — they carry no E-interaction of their own. Give them a dedicated navigable
        // bucket so a blind player can locate one (e.g. to demolish it from the build desk)
        // instead of having them swell the generic Built-objects list. Checked before the
        // interaction_type switch so it catches them whether the game flags them None or Builder.
        if (!string.IsNullOrEmpty(obj.obj_id) &&
            obj.obj_id.IndexOf("roof", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            category = NavCategory.Roofs;
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
                if (def.has_craft)
                {
                    category = NavCategory.Stations;
                    return true;
                }
                // A script object with no craft that the player built (carries a removal craft,
                // so the build desk can demolish it) goes under Buildables; otherwise Other.
                category = obj.has_removal_craft ? NavCategory.Buildables : NavCategory.Other;
                return true;
            default:
                // Resource nodes worked with a tool (chop a tree, mine a stone, dig out a
                // bush) or gathered/picked up by hand: these have no special interaction_type
                // (None) but carry a non-empty tool_actions list. Sort them into Trees /
                // Stones / Bushes / Gatherables so the player can head straight to e.g. a
                // bush to dig out (improving the graveyard rating).
                if (TryClassifyHarvestable(obj, def, out category))
                    return true;

                // Non-interactive grave fixtures (empty grave grounds, graveyard zone
                // markers) have no Grave interaction but read as graves by id — keep them
                // navigable under Graves. Everything else (grass, scenery) is skipped.
                if (!string.IsNullOrEmpty(obj.obj_id) &&
                    obj.obj_id.IndexOf("grave", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    category = NavCategory.Graves;
                    return true;
                }

                // Player-built objects with no other interaction (decorations, structures, signs,
                // lamps, beds, etc.) would otherwise be skipped and become impossible to find. A
                // finished built object carries a removal craft (the build desk's "Entfernen" can
                // demolish it — same marker BuildPlacementHandler.BuildRemovableList uses), so list
                // those under Buildables. has_removal_craft is a cheap WGO flag (no components touch).
                //
                // A placed-but-unbuilt construction site (e.g. a garden bed/"Beet" you finish by
                // pressing F) is the same idea but slips through: has_removal_craft is keyed on the
                // FINISHED obj_id, so the under-construction stage has no removal craft and used to
                // fall through to "skip". Catch it by its Hammer build action — you literally hammer
                // it to complete it — so unfinished builds still show up under Buildables to walk to.
                if (obj.has_removal_craft || HasHammerBuildAction(def))
                {
                    category = NavCategory.Buildables;
                    return true;
                }
                return false;
        }
    }

    /// <summary>
    /// True when a fence object is currently broken/worn and still repairable. The repair is a
    /// craft the object only carries while damaged: either a <c>Fixing</c> craft, or a craft that
    /// rebuilds the fence in place (<c>change_wgo</c> set) without producing a real item
    /// (<c>GetFirstRealOutput() == null</c>) — the same "this is the broken variant" signal the
    /// repair readout uses (see InteractionDetector.GetFixingCraft and the repair recipe rows).
    /// An obj_id containing "broken" is treated as a fallback signal. Intact fences carry no such
    /// craft and are skipped, so the category stays a short list of things actually needing a kit.
    /// </summary>
    // A grave fence below this durability (0..1) is "worn" enough to list for repair. Lenient on
    // purpose (anything with visible wear); raise it if the list feels too noisy.
    private const float WornFenceThreshold = 0.999f;

    /// <summary>
    /// True when a grave carries a fence item that has worn down (durability below
    /// <see cref="WornFenceThreshold"/>). Outputs a spoken description with the wear percentage.
    /// The fence item is the same one the grave menu shows; it decays over time and is restored
    /// with a repair kit. Returns false for graves with no fence or a pristine one.
    /// </summary>
    private static bool TryGetWornFence(WorldGameObject grave, out string desc)
    {
        desc = null;
        try
        {
            var fence = grave?.data?.GetItemOfType(ItemDefinition.ItemType.GraveFence);
            if (fence == null) return false;
            float dur = fence.durability;
            if (dur >= WornFenceThreshold) return false;
            desc = $"Worn fence {Mathf.RoundToInt(Mathf.Clamp01(dur) * 100f)} percent";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when a real (interaction_type Grave) grave is missing a fence and/or a cross, so the
    /// player can add decoration to it. Outputs what it still needs. Restricted to genuine graves —
    /// the non-interactive grave scenery that lists under Graves by obj_id has no parts and would
    /// otherwise all read as "needs everything".
    /// </summary>
    private static bool TryGetMissingDecoration(WorldGameObject grave, out string desc)
    {
        desc = null;
        try
        {
            if (grave?.obj_def == null ||
                grave.obj_def.interaction_type != ObjectDefinition.InteractionType.Grave)
                return false;

            var fence = grave.data?.GetItemOfType(ItemDefinition.ItemType.GraveFence);
            var cross = grave.data?.GetItemOfType(ItemDefinition.ItemType.GraveStone);
            bool noFence = fence == null || fence.IsEmpty();
            bool noCross = cross == null || cross.IsEmpty();
            if (!noFence && !noCross) return false;

            desc = (noFence && noCross) ? "Undecorated grave, needs cross and fence"
                 : noCross ? "Grave needs a cross"
                 : "Grave needs a fence";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRepairableFence(WorldGameObject wgo)
    {
        try
        {
            if (wgo?.obj_def == null) return false;

            if (!string.IsNullOrEmpty(wgo.obj_id) &&
                wgo.obj_id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (!wgo.obj_def.has_craft) return false;
            var crafts = wgo.components?.craft?.crafts;
            if (crafts == null) return false;

            foreach (var c in crafts)
            {
                if (c == null) continue;
                if (c.craft_type == CraftDefinition.CraftType.Fixing) return true;
                // A craft that swaps the object for another (change_wgo) and yields no real item
                // is a rebuild/repair, not a production recipe.
                if (!string.IsNullOrEmpty(c.change_wgo) && c.GetFirstRealOutput() == null) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The tool-worked / hand-gathered resource-node categories — the ones we keep navigable even
    /// when the object is culled (deactivated off-screen), because a blind player can't pan the
    /// camera to find e.g. an iron-ore rock they can't see. Everything else stays active-only.
    /// </summary>
    /// <summary>
    /// True when an object is built/completed by hitting it with the Hammer (the F build action) —
    /// i.e. a placed-but-unfinished construction site such as a garden bed under construction. These
    /// have no removal craft on their construction-stage obj_id (that lives on the finished id), so
    /// they would otherwise be skipped by navigation. Cheap obj_def-only check (no components touch).
    /// Note this also matches Hammer-repairable broken objects, which are legitimately "built things"
    /// and fine to list under Buildables.
    /// </summary>
    private static bool HasHammerBuildAction(ObjectDefinition def)
    {
        try
        {
            var tools = def?.tool_actions;
            if (tools == null || tools.no_actions) return false;
            return tools.HasToolK(ItemDefinition.ItemType.Hammer);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHarvestableCategory(NavCategory category) =>
        category == NavCategory.Trees ||
        category == NavCategory.Stones ||
        category == NavCategory.Ores ||
        category == NavCategory.Bushes ||
        category == NavCategory.Flowers ||
        category == NavCategory.Mushrooms ||
        category == NavCategory.Gatherables;

    /// <summary>
    /// Classify a tool-worked / hand-gathered resource node into Trees, Stones, Ores, Bushes or
    /// the catch-all Gatherables. The game marks what tool a node needs in
    /// <c>obj_def.tool_actions.action_tools</c> (Axe = chop, Pickaxe = mine, Shovel = dig,
    /// Hand = gather); we lead with the obj_id keyword (bush/tree/stone) so a node that takes
    /// several tools (e.g. a tree you chop then dig the stump) still lands in the right bucket,
    /// then fall back to the tool. Pure-Hammer nodes (construction/repair) are not harvestables
    /// and are skipped. Returns false when the object isn't a resource node.
    /// </summary>
    private static bool TryClassifyHarvestable(WorldGameObject obj, ObjectDefinition def, out NavCategory category)
    {
        category = NavCategory.Other;
        try
        {
            var tools = def.tool_actions;
            if (tools == null || tools.no_actions) return false;

            bool axe = tools.HasToolK(ItemDefinition.ItemType.Axe);
            bool pickaxe = tools.HasToolK(ItemDefinition.ItemType.Pickaxe);
            bool shovel = tools.HasToolK(ItemDefinition.ItemType.Shovel);
            bool hand = tools.HasToolK(ItemDefinition.ItemType.Hand);

            // A node you can only build/repair on (Hammer) is not something to harvest.
            if (!axe && !pickaxe && !shovel && !hand) return false;

            var id = obj.obj_id ?? "";
            if (id.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = NavCategory.Bushes;
                return true;
            }
            // Wild flowers (flower_small_N, flower_spawner): hand-picked decoratives that are
            // scattered everywhere and were swamping the Gatherables list. Give them their own
            // bucket so Gatherables stays focused on mushrooms/herbs/branches/etc.
            if (id.IndexOf("flower", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = NavCategory.Flowers;
                return true;
            }
            // Mushrooms (mushroom_N, forest_mushroom, mushroom_spawner): hand-picked, want their
            // own bucket so the player can head straight to them instead of digging through the
            // generic Gatherables list.
            if (id.IndexOf("mushroom", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = NavCategory.Mushrooms;
                return true;
            }
            if (axe || id.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("stump", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = NavCategory.Trees;
                return true;
            }
            // Ore-bearing rocks (iron_ore, gold_ore, …) and the mountainside mining deposits
            // (steep_iron, steep_coal, …) get their own bucket, checked before the generic Stones
            // bucket, so the player can head straight to a metal/fuel source instead of sifting it
            // out from plain stone/marble. Matched by keyword in the obj_id. Coal is included here
            // (rather than Stones) because it lives among the iron deposits in the mountains and
            // that is where the player expects to find it.
            if (id.IndexOf("ore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("iron", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("gold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("coal", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = NavCategory.Ores;
                return true;
            }
            if (pickaxe || id.IndexOf("stone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("boulder", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = NavCategory.Stones;
                return true;
            }

            // Anything else worked by shovel/hand (dig out or pick up with F): flowers,
            // mushrooms, herbs, fallen branches, etc.
            category = NavCategory.Gatherables;
            return true;
        }
        catch
        {
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

    /// <summary>
    /// Localized "broken, repair it" note appended to a broken build desk's navigator label so the
    /// player knows it can't be used to build yet. The actual repair materials are read out by the
    /// proximity/E repair readout (InteractionDetector.WithRepairInfo) when the player reaches it.
    /// </summary>
    private static string BrokenWord()
    {
        string code = "";
        try { code = (GJL.GetCurrentLocaleCode() ?? "").ToLowerInvariant(); } catch { }
        return code switch
        {
            "de" => "kaputt, reparieren",
            "fr" => "cassé, à réparer",
            "es" => "roto, reparar",
            "it" => "rotto, da riparare",
            "ru" => "сломан, починить",
            _ => "broken, repair it",
        };
    }

    private static string GetObjectLabelSafe(WorldGameObject obj)
    {
        try
        {
            // The broken morgue's throw-in (obj_id "morgue_throw_in_broken") localizes to
            // "Leiche hineinwerfen" (Throw body in) — identical to the river-disposal the Yorick
            // quest needs, but it only opens an unusable craft window; the real spot is the
            // separate "throw_body_river" object. Relabel so the player isn't lured here. Only
            // the BROKEN one — a repaired morgue throw-in is a legitimate disposal station.
            if (obj != null && !string.IsNullOrEmpty(obj.obj_id) &&
                obj.obj_id.IndexOf("morgue_throw", StringComparison.OrdinalIgnoreCase) >= 0 &&
                obj.obj_id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Broken morgue, can't throw bodies here";
            }

            // Build desks (the "planning table" Gerry sends you to) localize to their zone
            // name, e.g. "Alter Friedhof", which doesn't match what the player is told to look
            // for. Lead with the recognizable planning-table term, appending the zone name so
            // desks in different zones stay distinguishable. A build desk's BROKEN stage (e.g.
            // the morgue build desk near Gerry, obj_id "morgue_builddesk_broken") is
            // interaction_type Craft, not Builder, so it skips this relabel and reads as a raw
            // zone name — unrecognizable in the Stations list. Match the "builddesk" obj_id too
            // so broken desks are still named as build desks, and flag the broken state: pressing
            // E there opens the repair craft (the proximity repair readout names the materials),
            // not a build catalog.
            bool isBuildDesk =
                obj?.obj_def?.interaction_type == ObjectDefinition.InteractionType.Builder ||
                (!string.IsNullOrEmpty(obj?.obj_id) &&
                 obj.obj_id.IndexOf("builddesk", StringComparison.OrdinalIgnoreCase) >= 0);
            if (isBuildDesk)
            {
                var zoneName = InteractionDetector.GetObjectLabel(obj);
                var planning = PlanningTableWord();
                var label = string.IsNullOrEmpty(zoneName) || zoneName == planning
                    ? planning
                    : $"{planning}: {zoneName}";
                bool broken = !string.IsNullOrEmpty(obj.obj_id) &&
                              obj.obj_id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0;
                return broken ? $"{label} ({BrokenWord()})" : label;
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
