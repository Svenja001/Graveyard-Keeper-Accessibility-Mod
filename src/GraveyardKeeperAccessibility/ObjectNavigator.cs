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

    // A bare map position with no object behind it (see GdPointZoneAnchors): walk onto the exact
    // spot, like a drop, and use the tight arrival radius. Standing "about a tile short" is fine
    // when the goal is to press E on something, but not when the goal is to be INSIDE a trigger
    // zone — the game fires those on the player collider entering, and a tile short is outside.
    internal bool ExactPoint;
}

/// <summary>
/// Categories of navigable points of interest. Ordered for cycling with
/// Ctrl+PageUp / Ctrl+PageDown.
/// </summary>
internal enum NavCategory
{
    Quests,
    SomethingNew,
    Landmarks,
    Items,
    Corpses,
    Doors,
    Graves,
    EmptyGraves,
    ExhumableGraves,
    DiggableGraves,
    People,
    Enemies,
    Vendors,
    Storage,
    LoadedPallets,
    EmptyPallets,
    Stations,
    Trees,
    Stones,
    Ores,
    Bushes,
    Flowers,
    Mushrooms,
    Beehives,
    Gatherables,
    GardenBeds,
    Breakables,
    Destructibles,
    Fences,
    GravesToDecorate,
    Buildables,
    Roofs,
    FishingSpots,
    ZombieMines,
    Other
}

internal static class ObjectNavigator
{
    private static ManualLogSource _log;
    private static bool _initialized = false;

    // One ordered list of targets per category.
    private static readonly Dictionary<NavCategory, List<NavigationTarget>> _byCategory = new();

    // Objects the scene scan found carrying a quest script's one-shot interaction event, held aside
    // so GatherQuestTargets can mirror them into Quests after the arrow-driven entries. Refilled
    // from scratch on every refresh.
    private static readonly List<NavigationTarget> _pendingInteractionTargets = new();
    private static readonly NavCategory[] _categoryOrder =
    {
        NavCategory.Quests,
        NavCategory.SomethingNew,
        NavCategory.Landmarks,
        NavCategory.Items,
        NavCategory.Corpses,
        NavCategory.Doors,
        NavCategory.Graves,
        NavCategory.EmptyGraves,
        NavCategory.ExhumableGraves,
        NavCategory.DiggableGraves,
        NavCategory.People,
        NavCategory.Enemies,
        NavCategory.Vendors,
        NavCategory.Storage,
        NavCategory.LoadedPallets,
        NavCategory.EmptyPallets,
        NavCategory.Stations,
        NavCategory.ZombieMines,
        NavCategory.Trees,
        NavCategory.Stones,
        NavCategory.Ores,
        NavCategory.Bushes,
        NavCategory.Flowers,
        NavCategory.Mushrooms,
        NavCategory.Beehives,
        NavCategory.Gatherables,
        NavCategory.GardenBeds,
        NavCategory.Breakables,
        NavCategory.Destructibles,
        NavCategory.Fences,
        NavCategory.GravesToDecorate,
        NavCategory.Buildables,
        NavCategory.Roofs,
        NavCategory.FishingSpots,
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

    // Post-teleport navmesh recovery. TeleportWithFade moves the player but never rescans the A*
    // navmesh at the new spot — the game only re-activates interior navmesh as chunks stream in
    // while you move, so right after a teleport only the tile-patch around the landing is walkable
    // (the far side of the house reads unreachable). We track the player's position EVERY frame
    // (independent of _lastBusyPos, which is busy-only) and, on a single-frame jump, force a few
    // bounded rescans around the new position over the next ~1s so the whole room is walkable.
    private static Vector2 _lastPlayerPos;
    private static bool _hasLastPlayerPos = false;
    private static int _teleportRescanFramesLeft = 0;

    // ---- World-transition detection (see NotifyWorldTransition) -------------
    //
    // Every way the player's surroundings can be swapped out — walking through a door, a scripted
    // teleport, descending a dungeon level, sleeping, dying, loading a save, crossing into another
    // map zone — replaces which objects exist and which are culled around them. None of these are
    // ordinary movement, so without an explicit signal the destination list only catches up at the
    // next 30-frame boundary and then only if everything had already streamed in by that one frame.
    // They arrive by completely different routes, so rather than giving each its own timing they all
    // funnel into NotifyWorldTransition, which rebuilds the list at once and keeps rebuilding it on
    // a short interval until the new surroundings have settled. The individual detectors below are
    // just the signals; the handling is identical for all of them, which is what makes going in,
    // coming back out, and every other switch feel the same.

    // Signal 1 — the game's interior lighting state (Inside vs RealTime). Walking through a door
    // flips it, and it is NOT a position jump, so nothing else would notice.
    private static EnvironmentEngine.State _lastEnvironmentState = EnvironmentEngine.State.RealTime;
    private static bool _hasLastEnvironmentState = false;

    // Signal 2 — the named WorldZone the player stands in (church, tavern, cellar, town...). The
    // game resolves this on its own 0.5s poll (PlayerComponent.UpdateZone); we mirror the result so
    // an area change that neither teleports nor changes the lighting still refreshes the list. The
    // PlayerComponent is cached because it never changes within a session (cleared on scene change).
    private static PlayerComponent _playerComponent;
    private static WorldZone _lastPlayerZone;
    private static bool _hasLastPlayerZone = false;

    // Signal 3 — the loaded dungeon level. Descending puts the player back on the same entry tile of
    // a brand-new level, so the position-jump detector can miss it entirely while the ENTIRE object
    // set has been replaced. Tracked as (loaded, level number) so both entering/leaving and moving
    // between levels register.
    private static bool _lastDungeonLoaded = false;
    private static int _lastDungeonLevel = -1;
    private static bool _hasLastDungeonState = false;

    // Signal 4 — the game's camera fade. Every scripted transition (door, sleep, respawn, dungeon,
    // cutscene teleport) brackets itself in CameraTools.Fade/UnFade, so the fade flag is the one
    // signal that covers transitions we have no specific detector for. It also tells us the world
    // is still being rebuilt: the destination objects stream in while the screen is black, so the
    // quick-refresh window must not start counting down until the fade is over — otherwise it can
    // expire before the player can act at all, which is exactly what made entering a building feel
    // slower than leaving one. Read reflectively (private static bool) and cached; null if the
    // field ever moves, in which case the other signals still cover the common cases.
    private static FieldInfo _cameraFadeField;
    private static bool _cameraFadeFieldResolved = false;

    // The quick-refresh window itself. While it is open the list is rebuilt every few frames instead
    // of every 30, so objects that activate a few frames after the switch appear almost at once
    // rather than up to half a second later. Held in unscaled time so it behaves the same at any
    // frame rate and while the game is time-scaled (sleeping, cutscenes).
    private static float _fastRefreshUntil = 0f;
    private static bool _refreshNextUpdate = false;    // rebuild on the very next Update, whatever the counter says
    private static string _lastTransitionReason;       // only for logging, so a held fade doesn't spam
    private static int _lastRefreshFrame = -1;         // guards against rebuilding twice in one frame
    private const float FastRefreshSeconds = 2f;       // quick refreshes for this long after the last signal
    private const int FastRefreshInterval = 5;         // frames between rebuilds inside the window

    // The window closes early once the new surroundings stop changing. A full rebuild walks every
    // object in the scene, so holding the quick interval open for the full two seconds after every
    // door would spend most of it re-deriving an answer that already stopped moving. Instead we
    // count consecutive rebuilds that found the same number of targets: once the count holds still
    // (and the fade is over) the room has finished streaming in and the normal cadence takes back
    // over — which is never more than half a second behind, and every key that reads the list out
    // re-measures it against where the player is standing first (see EnsureFreshList).
    private static int _fastRefreshLastCount = -1;
    private static int _fastRefreshStableTicks = 0;
    private const int FastRefreshStableTicks = 3;      // identical rebuilds needed to call it settled

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
    // Where this journey (or its latest re-plan) set off from, and how many times the pulled-back
    // landing has been re-planned. See TryReplanFromPullbackLanding.
    private static Vector2 _longWalkStartPos;
    private static int _journeyReplans;
    // One fine-player-graph route attempt per long walk, after graph 0 has given up. See HandleNoRoute.
    private static bool _fineRouteTried;
    // The PLAYER, not the target, is the end with no node on the NPC navmesh — set by
    // RequestGraph0Route when the start will not snap. Pulling the destination in cannot help then;
    // see HandleNoRoute.
    private static bool _startOffWorldMesh;
    // Escape leg: a short first hop back onto the NPC navmesh before the real route. See
    // TryEscapeLegToWorldMesh. The original destination is parked here while the hop runs.
    // The route currently being driven, and whether it came from the WORLD mesh (graph 0, which
    // knows the roads) or from the fine player graph. Kept so a wall recovery can resume the route
    // it was already on instead of throwing it away — see StopInsideWall.
    private static List<Vector3> _currentRoute;
    private static bool _routeIsWorldMesh;

    private static bool _escapeLegActive;
    private static NavigationTarget _escapeLegTarget;
    private static Vector2 _escapeLegRealDest;   // _longWalkDest parked while the hop borrows it
    private static int _escapeLegsUsed;

    // Breadcrumb for the return glide. A Direct glide is the only way into a spot no graph can
    // route to (the bed inside the house, the graveyard chest); the price is that once standing
    // there, nothing routes back OUT either. So remember where the glide launched from, and whether
    // graph 0 could route from that spot — see TryReturnGlideToOrigin.
    private static Vector2 _glideOrigin;
    private static bool _glideOriginValid;
    private static int _returnGlidesUsed;

    private static Vector2 _longWalkDest;            // route end (approach point near the target)
    // Partial-route chaining: when the target is unreachable on the navmesh (e.g. an NPC inside
    // a building), graph 0 returns a path to the closest reachable node — the entrance/outside.
    // We walk that, then re-route from the new spot to advance region-by-region, until either
    // the target becomes reachable or the closest-reachable gap stops improving (navmesh limit).
    private static bool _routeReachesTarget = true;  // this route's endpoint actually reaches the goal
    private static bool _finalPartial = false;       // navmesh can't get closer; stop at route end
    private static float _bestEndGap;                // smallest route-endpoint-to-goal gap seen so far
    private static int _stalledRecomputes = 0;       // consecutive partial routes with no gap improvement

    // Emergency dungeon escape (L). The exit object of the walk L last started, so a later route
    // failure can be recognised as "the escape walk failed" no matter what else happened in
    // between, and the armed one-shot offer to be moved onto the exit outright. Being unable to
    // leave a dungeon level is unrecoverable for a blind player, so the key must always have an
    // answer even when the navmesh has none.
    private static WorldGameObject _escapeExitObject;
    private static bool _escapeTeleportArmed;
    private static float _escapeTeleportArmedAt;

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

    // How close (to the object's collider edge) the arrived target must be for the E-interaction
    // prefix to force it through even when the player's forward interaction box isn't overlapping
    // it. The game's forward box reaches ~1 tile; 1.5 tiles to the collider edge covers the "auto-
    // walk left me a hair off-axis" case while staying tight enough not to reach past a wall.
    private const float InteractionForceReach = 1.5f * TileSize;

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

    // Reactive navmesh recovery: when a near-walk A* fails, the target may simply sit on navmesh
    // that hasn't streamed/activated yet (post-teleport, post-sleep, or any partial-navmesh state).
    // The first failure per user walk forces a bounded rescan around player<->target and retries the
    // walk ONCE (deferred a few frames so the queued graph update processes) before falling through
    // to the existing graph-0 escalation. _rescanRetried gates it to one shot so there's no loop.
    private static bool _rescanRetried = false;
    private static bool _rescanRetryPending = false;
    private static int _rescanRetryFramesLeft = 0;
    private static NavigationTarget _rescanRetryTarget;

    // A body-sized ground drop is a DropResGameObject with a DYNAMIC Rigidbody2D and a kick
    // component (ChangeKickableState(now_kickable: true)) while it lies there, so it is physically
    // shovable. InteractionDest deliberately walks the player ONTO a drop's exact tile, and the
    // moment the walk ends we hand control back and the body turns Dynamic again — the physics
    // solver then separates the two overlapping colliders by ejecting the DROP, half a tile to a
    // tile clear of the player. The arrival check has already passed by then (it measures at the
    // arrival frame, while the drop is still underfoot), so the mod says "arrived" and plain E
    // finds nothing to pick up. Measured twice in one session at the morgue: the same delivered
    // corpse shoved 65u south-west on one delivery and 49u north-west on the next, with the player
    // standing at the identical spot both times.
    //
    // So after arriving at a drop, watch it for a moment and ask the GAME whether it is pickable —
    // DropResGameObject.currently_higlighted_obj, set from InteractionComponent.FindNearestDrop.
    // That is exact and needs no distance guess: the pickup zone is the interaction box rotated to
    // the player's facing, not a radius. If the drop never lights up, chase it once.
    private static bool _dropSettlePending = false;
    private static int _dropSettleFramesLeft = 0;
    private static NavigationTarget _dropSettleTarget;
    private static int _dropChases = 0;

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
    // While the player is inside an interior that ISN'T a scored WorldZone (e.g. the home), People/
    // Vendors can't be filtered by zone, so keep only those within this tight radius — an interior
    // room is a few tiles across, while the outdoor crowd sits spatially offset behind the walls.
    private const float InteriorPeopleFallbackRadius = 12f * TileSize;
    // How far the "reveal the room I'm standing in" rule reaches while inside (see
    // IsInPlayerInterior). Big enough to take in a whole interior from the doorway — the church
    // nave, the cellar workshop — the way a sighted player does on stepping through the door, but
    // never map-wide: this is a broader local reach, not x-ray vision.
    private const float InteriorRevealRadius = 22f * TileSize;
    // The same reveal for objects the zone test can't vouch for: an interior the game doesn't model
    // as a WorldZone at all (the morgue, the home), or an object inside a zoned interior that sits
    // outside the zone's own collider. With no zone to match on, a tight radius is the only thing
    // separating "in here with me" from "through that wall", so keep it to about a room's width.
    private const float InteriorRevealUnzonedRadius = 10f * TileSize;
    private const int UpdateInterval = 30;                 // refresh list every 30 frames

    // Reused snapshot buffer for RefreshDestinations. Sized for a full Graveyard Keeper scene so
    // it stops growing after the first rebuild and the per-refresh allocation drops to zero.
    private static readonly List<WorldGameObject> _scanBuffer = new(4096);

    // Cached WorldZone sweep. FindObjectsOfType is O(everything in the scene) whatever type you
    // ask it for, and the landmark pass ran one on every destination rebuild — up to 12 times a
    // second inside a fast-refresh window. World zones are static scene content: they are placed
    // with the level and never spawn or despawn during play, so re-sweeping for them at that rate
    // bought nothing. Invalidated on every world transition (which covers scene loads, teleports
    // and dungeon changes) and re-taken periodically as a backstop.
    private static WorldZone[] _cachedZones;
    private static float _cachedZonesAt = float.NegativeInfinity;
    private const float ZoneCacheSeconds = 10f;

    private static WorldZone[] CachedWorldZones()
    {
        if (_cachedZones != null && Time.unscaledTime - _cachedZonesAt < ZoneCacheSeconds)
            return _cachedZones;

        try
        {
            _cachedZones = UnityEngine.Object.FindObjectsOfType<WorldZone>(true) ?? new WorldZone[0];
        }
        catch
        {
            _cachedZones = _cachedZones ?? new WorldZone[0];
        }
        _cachedZonesAt = Time.unscaledTime;
        return _cachedZones;
    }

    /// <summary>Force the next landmark pass to re-sweep for world zones.</summary>
    private static void InvalidateZoneCache()
    {
        _cachedZones = null;
        _cachedZonesAt = float.NegativeInfinity;
    }
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
    // After a teleport jump we drive a short rescan schedule (in frames) from Update(), firing a few
    // bounded UpdateAstarBounds passes across ~1s so late-streaming interior colliders get picked up.
    private const int TeleportRescanTotalFrames = 60;
    // A failed near-walk defers its one-shot rescan retry this many frames so the queued A* graph
    // update has processed before we re-issue the walk (same reason as the _escalatePending defer).
    private const int RescanRetryDelayFrames = 6;
    // How long after arriving at a ground drop we keep asking whether the game highlights it, before
    // concluding it was kicked clear and chasing it. Long enough for the solver to push it out and
    // for it to slide to a stop (it carries the shove for a few fixed steps), short enough that the
    // re-approach still feels like part of the same walk.
    private const int DropSettleWindowFrames = 24;
    // One chase is the fix for a kicked drop; a second covers the rare case where the chase kicks it
    // again. Past that, stop walking the player around and let them nudge with WASD.
    private const int MaxDropChases = 2;
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
    // How many times a pulled-back landing may re-plan the journey from where it stopped. Two is
    // enough for the case this exists for (a walk that starts boxed in and only needs to get out
    // into open country before the road network can answer) and short enough that a genuinely
    // unreachable target still reaches the beacon quickly. See TryReplanFromPullbackLanding.
    private const int MaxJourneyReplans = 2;

    // Un-wedge search (see TryFreeWedgedPlayer): how far around the player we look for a walkable
    // graph-0 node that is actually connected to where they want to go, and how finely we sample.
    // Bounded to a room-ish radius so freeing the player is always a short, explicable hop.
    private const float UnwedgeMaxRadius = 10f * TileSize;
    private const float UnwedgeStep = 0.5f * TileSize;
    private const int UnwedgeRayCount = 16;
    // How long an armed "press L again to be moved onto the exit" offer stays valid.
    private const float EscapeConfirmSeconds = 20f;

    // Beacon (manual fallback) thresholds.
    private const float BeaconHandoffDistance = 15f * TileSize;
    private const float BeaconReannounceDistance = 6f * TileSize;
    // How far the player must move after an A* failure before the beacon retries the A* handoff
    // (enough to have streamed in / activated the destination area; small enough to keep finishing).
    private const float HandoffRetryDistance = 3f * TileSize;

    internal static bool IsWalking => _isWalking;
    internal static bool IsBeaconActive => _beaconActive;
    internal static bool IsBusy => _isWalking || _beaconActive || _longWalkActive || GuidedWalk.IsActive;

    // Set true only while we drive an A* GoTo, so the RefreshPlayerGraph patch pads
    // the player-graph bounds for our walks without affecting vanilla pathfinding.
    internal static bool PadPlayerGraph { get; private set; }

    /// <summary>
    /// How much slack to add on every side of the player-graph scan (see
    /// <c>Patches.RefreshPlayerGraph_Prefix</c>).
    ///
    /// WHY IT IS NOT ONE NUMBER: the game scans the player graph over a thin rectangle between the
    /// player and the destination, so a route that has to leave that strip is simply not in the
    /// graph and the search answers "no path" for somewhere the player can plainly walk. Five tiles
    /// of slack covers going round a fence. It does not cover going round a BUILDING, which is what
    /// the graveyard asks for: walking from the graves to the mortuary door, both A* attempts and
    /// the graph-0 escalation all failed, and then turn-by-turn guidance found a perfectly good
    /// 243-waypoint route the moment it rescanned a wider box. Nothing was in the way that a player
    /// could not walk around; the way around was outside the search area.
    ///
    /// A wide scan is not free — a 16-tile box measured 113ms — so it is spent only on the retry
    /// after a walk has already failed once, where the alternative is giving up.
    /// </summary>
    internal static float PlayerGraphPadUnits =>
        _padOverrideUnits ?? (_widePlayerGraphPad ? WidePlayerGraphPad : NormalPlayerGraphPad);

    /// Set for one scan when a caller needs its own box size — see TryEscapeLegByExploring, which
    /// scans a square round the player rather than a strip toward anything.
    private static float? _padOverrideUnits;

    private static bool _widePlayerGraphPad;
    private const float NormalPlayerGraphPad = 5f * TileSize;
    private const float WidePlayerGraphPad = 14f * TileSize;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        foreach (var cat in _categoryOrder)
            _byCategory[cat] = new List<NavigationTarget>();
        GuidedWalk.Init(log);
        _initialized = true;
        _log?.LogInfo("[NAVIGATOR] ObjectNavigator initialized (native pathfinding, categorized)");
    }

    internal static void Update()
    {
        if (!_initialized) return;

        try
        {
            // Always-on teleport detector (runs whether or not navigation is busy): a single-frame
            // position jump means the player teleported (stone/fast-travel/dungeon). TeleportWithFade
            // doesn't rescan the navmesh at the landing, so schedule a few bounded rescans over the
            // next ~1s to re-activate the whole room (see ForceNavmeshRescanAround). The busy-only
            // guard below still handles tearing down a stale in-progress walk.
            {
                var plr = MainGame.me?.player;
                if (plr != null)
                {
                    var ppos = plr.pos;
                    if (_hasLastPlayerPos && Vector2.Distance(ppos, _lastPlayerPos) >= TeleportJumpDistance)
                    {
                        // A jump lands the player among an entirely different set of objects, so the
                        // destination list is as stale as the navmesh is — refresh both. This covers
                        // the transitions that keep the same lighting state (cellar to church, one
                        // dungeon room to the next), which nothing else here would notice.
                        NotifyWorldTransition($"teleport jump ({Vector2.Distance(ppos, _lastPlayerPos):F0}u)");
                    }
                    _lastPlayerPos = ppos;
                    _hasLastPlayerPos = true;
                }
            }

            // Fire the scheduled post-teleport rescans around the (now-settled) player position. The
            // position change is deferred into a camera fade, and interior colliders stream in over a
            // few frames, so we rescan a few times across the window rather than once immediately.
            if (_teleportRescanFramesLeft > 0)
            {
                int elapsed = TeleportRescanTotalFrames - _teleportRescanFramesLeft;
                if (elapsed == 5 || elapsed == 25 || elapsed == 55)
                {
                    var prp = MainGame.me?.player?.pos;
                    if (prp.HasValue) ForceNavmeshRescanAround(prp.Value);
                }
                _teleportRescanFramesLeft--;
            }

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

            // Just arrived at a ground drop: watch whether the arrival kicked it out of reach.
            // Independent of the walk-retry chain below — it only ever runs when no walk is in
            // progress, and it starts one of its own at most twice.
            if (_dropSettlePending) TickDropSettle();

            // A near-walk failed and we forced a navmesh rescan around the target — re-issue the
            // walk once the queued graph update has processed (a few frames later). If it fails again
            // _rescanRetried is already set, so it falls through to the graph-0 escalation below.
            if (_rescanRetryPending)
            {
                if (--_rescanRetryFramesLeft <= 0)
                {
                    _rescanRetryPending = false;
                    _log?.LogInfo($"[NAVIGATOR] Retrying walk to {_rescanRetryTarget.Label} after navmesh rescan");
                    // The first attempt already failed with the normal bounds, so widen them for
                    // this one — see WidePlayerGraphPad. Costs one bigger graph scan, on a path
                    // whose only other outcome is giving up.
                    _widePlayerGraphPad = true;
                    try { WalkToTarget(_rescanRetryTarget); }
                    finally { _widePlayerGraphPad = false; }
                }
            }
            // A* failed on a short walk — retry through the fence-aware graph-0 route (gates)
            // before resorting to the straight line. Runs next frame so the game's OnPathFailed
            // has finished clobbering the previous request.
            else if (_escalatePending)
            {
                _escalatePending = false;
                StartLongWalk(_shortWalkTarget);
            }
            // Run a queued straight-line fallback (A* couldn't find a path). Same rule as the
            // glide: a Kinematic body on a two-point path goes through whatever is between here and
            // there, so the line has to be clear before we drive the player down it.
            else if (_fallbackPending)
            {
                _fallbackPending = false;
                var from = PlayerBodyPos(MainGame.me?.player);
                if (StraightLineIsWalkable(from, _fallbackDest))
                {
                    StartWalk(_fallbackDest, _fallbackLabel, MovementComponent.GoToMethod.Direct);
                }
                else
                {
                    _isWalking = false;
                    ReleaseScriptControl();
                    _log?.LogWarning($"[NAVIGATOR] Direct fallback to {_fallbackLabel} would pass through geometry; guiding instead");
                    ScreenReader.Say(Loc.Fmt("nav.manual_guidance", _fallbackLabel), interrupt: true);
                    // This fallback only runs for a target with no world object behind it (a
                    // landmark point), so hand guidance the destination itself rather than a
                    // half-filled target struct.
                    GuidedWalk.StartTo(
                        new NavigationTarget { Label = _fallbackLabel, Position = _fallbackDest },
                        announceStart: false, allowBeaconFallback: true);
                }
            }

            // Stop a walk of ours at the face of a wall rather than let it slide through — and log
            // it either way. See WatchForWallCrossing.
            WatchForWallCrossing();

            // Watch for every kind of world switch and hand them all to the same handler.
            DetectWorldTransitions();

            _updateCounter++;
            bool fastWindowOpen = Time.unscaledTime < _fastRefreshUntil;
            int refreshInterval = fastWindowOpen ? FastRefreshInterval : UpdateInterval;
            if (_refreshNextUpdate || _updateCounter >= refreshInterval)
            {
                _refreshNextUpdate = false;
                RefreshDestinations();
            }

            // Monitor the long-distance auto-walk (the native follower does the moving).
            if (_longWalkActive)
                TickLongWalk();

            // Drive the compass beacon (manual fallback guidance) if one is active.
            if (_beaconActive)
                UpdateBeacon();

            // Drive turn-by-turn guidance (the player is walking themselves).
            GuidedWalk.Update();

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

    // ---- World transitions --------------------------------------------------

    /// <summary>
    /// Poll every signal that says "the world around the player was just swapped out" and route
    /// them all through <see cref="NotifyWorldTransition"/>. Run once per frame from Update; each
    /// check is a field read or a cached reference compare, no scene queries.
    /// </summary>
    private static void DetectWorldTransitions()
    {
        try
        {
            // A camera fade is in progress: a scripted transition is running RIGHT NOW and the
            // destination world is still being built behind the black screen. Re-arm every frame it
            // lasts so the quick-refresh window only starts counting down once the player can see
            // again — this is what makes stepping into a building settle as fast as stepping out.
            // No navmesh rescan from this one: fades also cover pure camera work (cutscenes,
            // sleeping) where nothing moved, and the teleport detector already covers the rest.
            if (IsCameraFading())
                NotifyWorldTransition("camera fade", rescanNavmesh: false);

            // The interior lighting state. Walking through a door flips it without moving the
            // player anywhere the teleport detector would notice.
            var envState = EnvironmentEngine.me?.data?.state;
            if (envState.HasValue)
            {
                if (_hasLastEnvironmentState && envState.Value != _lastEnvironmentState)
                    NotifyWorldTransition($"lighting {_lastEnvironmentState} -> {envState.Value}");
                _lastEnvironmentState = envState.Value;
                _hasLastEnvironmentState = true;
            }

            // The named area the player stands in. Covers the switches that neither teleport nor
            // change the lighting — crossing from the yard into the graveyard, or from an outdoor
            // zone into a roofless interior — so an area change always refreshes the list.
            var zone = CurrentPlayerZone();
            if (_hasLastPlayerZone && zone != _lastPlayerZone)
            {
                // Zone names are the player-facing handle on where they are, so log by id.
                string from = _lastPlayerZone != null ? _lastPlayerZone.id : "open ground";
                string to = zone != null ? zone.id : "open ground";
                // Crossing an area boundary re-activates a different slice of the map, but nothing
                // was teleported, so the navmesh under the player is untouched — list only.
                NotifyWorldTransition($"zone {from} -> {to}", rescanNavmesh: false);
            }
            _lastPlayerZone = zone;
            _hasLastPlayerZone = true;

            // The loaded dungeon level. Descending drops the player on the entry tile of a freshly
            // generated level — often barely a step from where they stood — so the position jump can
            // be too small to detect while every single object around them has been replaced.
            var dungeonRoot = GameRefs.DungeonRoot();
            if (dungeonRoot != null)
            {
                bool loaded = dungeonRoot.dungeon_is_loaded_now;
                int level = (loaded && dungeonRoot.cur_dungeon_preset != null)
                    ? dungeonRoot.cur_dungeon_preset.dungeon_level : -1;
                if (_hasLastDungeonState && (loaded != _lastDungeonLoaded || level != _lastDungeonLevel))
                    NotifyWorldTransition($"dungeon level {_lastDungeonLevel} -> {level}");
                _lastDungeonLoaded = loaded;
                _lastDungeonLevel = level;
                _hasLastDungeonState = true;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Transition detection error: {ex.Message}");
        }
    }

    /// <summary>
    /// The single handler for "the world around the player just changed". Rebuilds the destination
    /// list on the next Update and keeps rebuilding it on a short interval for
    /// <see cref="FastRefreshSeconds"/>, so objects that stream in / activate over the following
    /// frames are picked up almost immediately instead of at the next 30-frame boundary. Repeat
    /// calls while a transition is still running simply push the window further out.
    /// </summary>
    /// <param name="reason">Logged once per distinct transition (a held fade re-arms every frame).</param>
    /// <param name="rescanNavmesh">
    /// Also run the bounded post-teleport navmesh rescans. True when the player was physically moved
    /// (the game doesn't rescan at the landing, so the far side of the new room reads unwalkable);
    /// false when only the surroundings changed and the navmesh under the player is untouched.
    /// </param>
    internal static void NotifyWorldTransition(string reason, bool rescanNavmesh = true)
    {
        // A fade is re-armed on every frame it lasts, so distinguish a genuinely new signal from
        // that hold: a new signal logs once and forces a rebuild on the spot, the hold only keeps
        // the window from expiring (rebuilding every single frame of a fade would buy nothing —
        // the window's own interval already covers the streaming).
        bool isNewSignal = Time.unscaledTime >= _fastRefreshUntil || reason != _lastTransitionReason;
        if (isNewSignal)
        {
            _log?.LogInfo($"[NAVIGATOR] World transition: {reason} — refreshing destinations");
            _lastTransitionReason = reason;
            _refreshNextUpdate = true;
            // The object set and the zone set both change across a transition, so drop the caches
            // that assume they didn't. Both are rebuilt lazily on the refresh this just queued.
            InvalidateZoneCache();
            StockPointFilter.Invalidate();
            WorldObjectRegistry.RequestResync(reason);
        }

        _fastRefreshUntil = Time.unscaledTime + FastRefreshSeconds;
        // A fresh signal means the surroundings are moving again: whatever had settled no longer has.
        _fastRefreshStableTicks = 0;
        _fastRefreshLastCount = -1;

        if (rescanNavmesh && _teleportRescanFramesLeft <= 0)
            _teleportRescanFramesLeft = TeleportRescanTotalFrames;
    }

    /// <summary>
    /// True while the game is playing a screen fade. Every scripted transition brackets itself in
    /// CameraTools.Fade/UnFade, which flips the private <c>_playing_transition</c> flag, so this is
    /// the catch-all signal for transitions that have no detector of their own. Returns false if the
    /// field can't be resolved — the other signals still cover the common cases.
    /// </summary>
    private static bool IsCameraFading()
    {
        if (!_cameraFadeFieldResolved)
        {
            _cameraFadeFieldResolved = true;
            _cameraFadeField = AccessTools.Field(typeof(CameraTools), "_playing_transition");
            if (_cameraFadeField == null)
                _log?.LogWarning("[NAVIGATOR] CameraTools._playing_transition not found — fade-based transition detection is off");
        }
        if (_cameraFadeField == null) return false;
        try { return (bool)_cameraFadeField.GetValue(null); }
        catch { return false; }
    }

    /// <summary>
    /// The named zone the player currently stands in, straight off the game's own 0.5s zone poll
    /// (PlayerComponent.current_zone) — no physics query of our own. Null in open ground.
    /// </summary>
    private static WorldZone CurrentPlayerZone()
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null) return null;
            if (_playerComponent == null)
                _playerComponent = player.GetComponent<PlayerComponent>();
            return _playerComponent != null ? _playerComponent.current_zone : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Bring the lists up to date for a key the player just pressed. This deliberately does NOT do a
    /// full rebuild: navigation keys get pressed in quick succession, and walking the whole scene on
    /// every press puts a stall between the keystroke and the speech. What the lists CONTAIN is
    /// already kept current by the scheduled rebuild and, right after a transition, by the
    /// quick-refresh window; what a keypress needs on top of that is the part that changes
    /// continuously as the player walks — distances, the positions of anything that moved, and
    /// dropping whatever has been destroyed since. That is a pass over a few dozen entries instead
    /// of a pass over the scene.
    /// </summary>
    private static void EnsureFreshList()
    {
        if (!_initialized) return;
        if (_lastRefreshFrame == Time.frameCount) return;

        // Nothing built yet (first navigation key of a session) — there is no cheap path, so build.
        if (_lastRefreshFrame < 0)
        {
            RefreshDestinations();
            return;
        }

        RemeasureTargets();
    }

    /// <summary>
    /// Re-measure the existing lists against where the player is standing right now: refresh each
    /// target's position and distance, drop entries whose object has been removed since the last
    /// rebuild, re-sort by distance, and keep the cursor on whatever target it was on.
    /// </summary>
    private static void RemeasureTargets()
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null) return;
            var playerPos = player.pos;

            // Hold the selection by identity: the re-sort below can move it, and landmarks/quest
            // targets have no object behind them, so they match by label (same rule as a rebuild).
            var curList = CurrentList;
            WorldGameObject selectedObject = null;
            GameObject selectedDrop = null;
            string selectedLabel = null;
            if (curList.Count > 0 && _selectedIndex < curList.Count)
            {
                selectedObject = curList[_selectedIndex].Object;
                selectedDrop = curList[_selectedIndex].DropGo;
                selectedLabel = curList[_selectedIndex].Label;
            }

            foreach (var cat in _categoryOrder)
            {
                var list = _byCategory[cat];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var t = list[i];

                    // ReferenceEquals, not ==: Unity's == reports a DESTROYED object as null, which
                    // is exactly the case that has to be told apart from a target that never had an
                    // object behind it (a landmark or quest arrow). Only the former gets dropped.
                    if (!ReferenceEquals(t.Object, null))
                    {
                        // Destroyed or removed since the last rebuild — never announce or walk to it.
                        if (t.Object == null || t.Object.is_removed) { list.RemoveAt(i); continue; }
                        t.Position = t.Object.pos;   // NPCs and workers move between rebuilds
                    }
                    else if (!ReferenceEquals(t.DropGo, null))
                    {
                        if (t.DropGo == null) { list.RemoveAt(i); continue; }   // picked up / despawned
                        t.Position = t.DropGo.transform.position;
                    }
                    // Landmarks, quest arrows and bare map points are fixed: keep their position.

                    t.Distance = Vector2.Distance(t.Position, playerPos);
                    list[i] = t;   // NavigationTarget is a struct — write the updated copy back
                }

                list.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            }

            var newList = CurrentList;
            if (selectedObject != null || selectedDrop != null || selectedLabel != null)
            {
                var idx = newList.FindIndex(t =>
                    (selectedObject != null && t.Object == selectedObject) ||
                    (selectedDrop != null && t.DropGo == selectedDrop) ||
                    (selectedObject == null && selectedDrop == null &&
                     selectedLabel != null && t.Object == null && t.DropGo == null &&
                     t.Label == selectedLabel));
                _selectedIndex = idx >= 0 ? idx : 0;
            }
            if (_selectedIndex >= newList.Count)
                _selectedIndex = 0;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Error re-measuring targets: {ex.Message}");
        }
    }

    private static List<NavigationTarget> CurrentList =>
        _byCategory.TryGetValue(_currentCategory, out var list) ? list : new List<NavigationTarget>();

    // ---- Category cycling (Ctrl+PageUp / Ctrl+PageDown) ---------------------

    internal static void NextCategory() => CycleCategory(+1);
    internal static void PreviousCategory() => CycleCategory(-1);

    private static void CycleCategory(int dir)
    {
        // Measure against where the player is standing now, so the category the cursor lands in and
        // the distance it reads out are current.
        EnsureFreshList();

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
                GuidedWalk.NotifySelectionChanged();
                return;
            }
        }

        ScreenReader.Say(Loc.Get("nav.none_nearby"), interrupt: true);
    }

    private static void AnnounceCategory()
    {
        var list = CurrentList;
        var name = CategoryName(_currentCategory);
        if (list.Count == 0)
        {
            ScreenReader.Say(Loc.Fmt("nav.category_empty", name), interrupt: true);
            return;
        }

        var target = list[_selectedIndex];
        ScreenReader.Say(Loc.Fmt("nav.category_entry", name, list.Count, target.Label, DirectionTo(target), DistanceText(target.Distance), SkullSuffix(target)), interrupt: true);
        _log?.LogInfo($"[NAVIGATOR] Category {name} ({list.Count}) -> {target.Label}");
    }

    // One lang key per category ("nav.category.Quests" …), so the tracker's headings translate
    // with the rest of the mod instead of being fixed English.
    private static string CategoryName(NavCategory cat) => Loc.Get("nav.category." + cat);

    // ---- Item cycling within the current category (PageUp / PageDown) -------

    internal static void SelectNext()
    {
        // Re-measure first: the cursor is kept on the same object across it, so stepping through the
        // list stays coherent while the distances and ordering are the ones for right now.
        EnsureFreshList();

        var list = CurrentList;
        if (list.Count == 0) { EnsureNonEmptyCategory(); return; }

        _selectedIndex = (_selectedIndex + 1) % list.Count;
        AnnounceSelected();
        GuidedWalk.NotifySelectionChanged();
    }

    internal static void SelectPrevious()
    {
        EnsureFreshList();

        var list = CurrentList;
        if (list.Count == 0) { EnsureNonEmptyCategory(); return; }

        _selectedIndex = (_selectedIndex - 1 + list.Count) % list.Count;
        AnnounceSelected();
        GuidedWalk.NotifySelectionChanged();
    }

    internal static void AnnounceSelected()
    {
        // No-op when SelectNext/Previous already re-measured this frame; does the work when the
        // player pressed the plain "what's selected" key after walking a stretch.
        EnsureFreshList();

        var list = CurrentList;
        if (list.Count == 0)
        {
            ScreenReader.Say(Loc.Get("nav.none_nearby"), interrupt: false);
            return;
        }

        try
        {
            if (_selectedIndex >= list.Count) _selectedIndex = 0;
            var target = list[_selectedIndex];
            var dir = DirectionTo(target);
            var message = Loc.Fmt("nav.entry", target.Label, dir, DistanceText(target.Distance), _selectedIndex + 1, list.Count, SkullSuffix(target));
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
        ScreenReader.Say(Loc.Get("nav.none_nearby"), interrupt: false);
    }

    private static string DistanceText(float worldDistance)
    {
        var tiles = worldDistance / TileSize;
        return Loc.Fmt("nav.meters_away", tiles.ToString("F0"));
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
        // Walk to where the target is now, not to where it was at the last scheduled rebuild — and
        // never set off toward one that has been destroyed in the meantime.
        EnsureFreshList();

        var list = CurrentList;
        if (list.Count == 0)
        {
            ScreenReader.Say(Loc.Get("nav.nothing_selected"), interrupt: true);
            return;
        }

        if (_selectedIndex >= list.Count) _selectedIndex = 0;
        var target = list[_selectedIndex];

        // Fresh user walk: clear the "A* already failed" guard so this attempt may use A*/handoff,
        // give this walk a fresh one-shot rescan retry, and drop any previous arrival bias (the new
        // arrival sets its own).
        _astarFailedForWalk = false;
        _rescanRetried = false;
        _wallRecoveries = 0;
        _currentRoute = null;
        _routeIsWorldMesh = false;
        _rescanRetryPending = false;
        // A walk the player asked for replaces any drop chase still watching the last arrival, and
        // gives the new arrival a fresh chase budget.
        _dropSettlePending = false;
        _dropChases = 0;
        _routeRejectedForSolid = null;
        // A clear spot remembered during an EARLIER walk is not somewhere to be teleported back to
        // during this one; the wall guard records a fresh one on the first clear frame.
        _hasLastClearPos = false;
        ClearArrivedTarget();
        // Asking to be walked there replaces any turn-by-turn guidance in progress.
        GuidedWalk.Stop(announce: false);

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
    /// Emergency "take me to the way out" for dungeons (bound to L). Finds the up-exit
    /// (obj_id "dungeon_exit", NOT the deeper gated "dungeon_exit2") among the loaded level's
    /// objects and auto-walks there regardless of the current category or selection, so a blind
    /// player can never be stranded on a level. We only POSITION at the exit — the player then
    /// presses E to leave (the game's Save-and-Exit teleports to the mortuary, no need to clear
    /// the level) or simply turns back to keep fighting. Locked arenas like level 10 spawn two
    /// identically-generated exits and seal the downward one behind a grille; the way out,
    /// however, is always the spawn-in point and is never gated, so this guarantees an escape.
    ///
    /// Walking is only the FIRST answer, though — it depends on the navmesh, and the navmesh can
    /// have no answer at all. If the player's own position is on a graph-0 island (wedged in
    /// scenery, or dropped into a pocket by a glide fallback) nothing on the level is routable and
    /// the old behaviour was to hand them the compass beacon, which is useless to someone whose
    /// body cannot move: that is a lost save. So this now escalates — free the player onto
    /// connected ground first, and if even that fails, offer to put them on the exit outright on a
    /// second press. See [[dungeon-two-exits-and-escape-key]].
    /// </summary>
    internal static void WalkToDungeonExit()
    {
        var dr = GameRefs.DungeonRoot();
        if (dr == null || !dr.dungeon_is_loaded_now)
        {
            ScreenReader.Say(Loc.Get("nav.not_in_dungeon"), interrupt: true);
            return;
        }

        var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;

        // Prefer the non-"2" exit (the way up/out); pick the nearest match. Include inactive
        // children so a culled/off-screen exit still counts — it re-activates as we approach.
        WorldGameObject best = null;
        float bestDist = float.MaxValue;
        foreach (var wgo in dr.GetComponentsInChildren<WorldGameObject>(true))
        {
            if (wgo == null || wgo.is_removed || string.IsNullOrEmpty(wgo.obj_id)) continue;
            var id = wgo.obj_id.ToLowerInvariant();
            if (id.IndexOf("dungeon_exit", StringComparison.Ordinal) < 0) continue;
            if (id.IndexOf("dungeon_exit2", StringComparison.Ordinal) >= 0) continue; // deeper, gated
            var d = Vector2.Distance(wgo.pos, playerPos);
            if (d < bestDist) { bestDist = d; best = wgo; }
        }

        if (best == null)
        {
            ScreenReader.Say(Loc.Get("nav.no_way_out"), interrupt: true);
            return;
        }

        var target = new NavigationTarget
        {
            Object = best,
            Label = Loc.Get("door.dungeon_exit"),
            Position = best.pos,
            Distance = bestDist
        };

        // Where we would put the player if walking turns out to be impossible: the exit's own
        // interaction tile, pulled onto real navmesh so they land standing rather than in a wall.
        var standPos = InteractionDest(target, out _);
        if (TryGraph0Node(standPos, out _, out var snappedStand) &&
            Vector2.Distance(standPos, snappedStand) <= 2f * TileSize)
            standPos = snappedStand;

        // Second press of an armed offer: skip the navmesh entirely and put them on the exit. Still
        // POSITIONING only — they press E to leave, or walk off and keep fighting, exactly as when
        // the walk succeeds.
        if (_escapeTeleportArmed && Time.realtimeSinceStartup - _escapeTeleportArmedAt <= EscapeConfirmSeconds)
        {
            _escapeTeleportArmed = false;
            _log?.LogWarning($"[NAVIGATOR] Escape-to-exit: confirmed teleport to {best.obj_id} at {standPos}");
            if (TeleportPlayerTo(standPos, Loc.Get("nav.moved_to_exit"),
                                 () => SetArrivedTarget(best)))
                return;
            ScreenReader.Say(Loc.Get("nav.could_not_move_to_exit"), interrupt: true);
            return;
        }
        _escapeTeleportArmed = false;

        // Fresh user walk: clear the "A* already failed" guards (mirrors WalkToSelected).
        _astarFailedForWalk = false;
        _rescanRetried = false;
        _wallRecoveries = 0;
        _currentRoute = null;
        _routeIsWorldMesh = false;
        _rescanRetryPending = false;
        // A walk the player asked for replaces any drop chase still watching the last arrival, and
        // gives the new arrival a fresh chase budget.
        _dropSettlePending = false;
        _dropChases = 0;
        _routeRejectedForSolid = null;
        // A clear spot remembered during an EARLIER walk is not somewhere to be teleported back to
        // during this one; the wall guard records a fresh one on the first clear frame.
        _hasLastClearPos = false;
        ClearArrivedTarget();
        GuidedWalk.Stop(announce: false);
        _escapeExitObject = best;

        // Ask up front whether a route can exist at all, rather than discovering it through a dozen
        // failing async queries. If it can't, the player — not the exit — is usually the problem.
        if (!CanRouteOnGraph0(playerPos, standPos))
        {
            _log?.LogWarning($"[NAVIGATOR] Escape-to-exit: no graph-0 connection from {playerPos} to {standPos}");
            if (TryFreeWedgedPlayer(standPos, () => StartEscapeWalk(target, best)))
                return;

            ArmEscapeTeleport();
            return;
        }

        StartEscapeWalk(target, best);
    }

    private static void StartEscapeWalk(NavigationTarget target, WorldGameObject exit)
    {
        var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
        target.Distance = Vector2.Distance(playerPos, target.Position);
        _escapeExitObject = exit;
        _log?.LogInfo($"[NAVIGATOR] Escape-to-exit: walking to {exit.obj_id} at {exit.pos} ({target.Distance:F0}u)");
        if (target.Distance > LongWalkStartDistance) StartLongWalk(target);
        else WalkToTarget(target);
    }

    /// <summary>
    /// Offer the last-resort move-me-onto-the-exit, taken by pressing L again. Kept behind a
    /// confirmation because it is a teleport: automatic on every failed escape it would quietly
    /// paper over navigation bugs, but a stranded player must never be told "no".
    /// </summary>
    private static void ArmEscapeTeleport()
    {
        _escapeTeleportArmed = true;
        _escapeTeleportArmedAt = Time.realtimeSinceStartup;
        ScreenReader.Say(Loc.Get("nav.no_path_to_exit"),
                         interrupt: true);
    }

    /// <summary>
    /// Auto-walk (native A*) to a target that is within the player graph's reach. Used both
    /// by Ctrl+Home on a near target and as the final approach when a compass beacon brings
    /// the player into range.
    /// </summary>
    /// <param name="silent">
    /// Skip the "walking to X" announcement. Used by the drop settle chase, which is the tail of a
    /// walk the player already heard announced a second ago — saying it again would just be the
    /// same sentence twice for what is, to them, one continuous approach.
    /// </param>
    private static void WalkToTarget(NavigationTarget target, bool silent = false)
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
            if (!silent)
                ScreenReader.Say(Loc.Fmt("nav.walking_to", target.Label, DistanceText(target.Distance)), interrupt: true);
            StartWalk(dest, target.Label, MovementComponent.GoToMethod.AStar);
        }
        finally
        {
            PadPlayerGraph = false;
        }
    }

    // ---- Long-distance auto-walk (native full-path follow) -----------------

    /// <param name="afterEscapeLeg">
    /// True when this is the real walk being resumed after an escape leg put the player back on the
    /// NPC navmesh. Two things follow. It keeps <see cref="_escapeLegsUsed"/>, so a second failure
    /// cannot start another hop and loop; a walk the player asks for themselves always gets a fresh
    /// budget. And it stays SILENT: the destination was announced when the player asked for it a
    /// second or two ago, and the hop is an implementation detail of that one walk, not a new one.
    /// </param>
    private static void StartLongWalk(NavigationTarget target, bool afterEscapeLeg = false)
    {
        _longWalkActive = true;
        _longWalkTarget = target;
        _longWalkStuckTicks = 0;
        _routeNeedsRecompute = false;
        _exitAssisting = false;
        _escapeLegActive = false;
        if (!afterEscapeLeg) { _escapeLegsUsed = 0; _returnGlidesUsed = 0; }
        _pullbackTries = 0;
        if (!afterEscapeLeg) _journeyReplans = 0;
        _fineRouteTried = false;
        _routeRejectedForSolid = null;
        _startOffWorldMesh = false;
        _routeReachesTarget = true;
        _finalPartial = false;
        _bestEndGap = float.MaxValue;
        _stalledRecomputes = 0;
        var pp = MainGame.me?.player?.pos ?? Vector2.zero;
        _longWalkProgressPos = pp;
        _longWalkAnnouncePos = pp;
        _longWalkStartPos = pp;
        if (!afterEscapeLeg)
            ScreenReader.Say(Loc.Fmt("nav.walking_to_dir", target.Label, DirectionTo(target), DistanceText(Vector2.Distance(pp, target.Position))), interrupt: true);
        _log?.LogInfo($"[NAVIGATOR] Long walk started to {target.Label}" +
                      (afterEscapeLeg ? " (resumed after escape leg, not re-announced)" : ""));

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
            else
            {
                _log?.LogInfo($"[NAVIGATOR] Route dest {to} has no walkable graph-0 node");
            }

            // And ask the same of the START. Graph 0's nodes are 76 units — nearly a whole tile —
            // so among packed graves, or standing in an alcove, there is no walkable node under the
            // player at all, and then EVERY query from here errors no matter where it is pointed.
            // That is not a fact the old log could show: a walk out of the graveyard burned five
            // "pulling dest toward player" retries that were moving the end that was never the
            // problem, and the player was left on the beacon. Recording it lets HandleNoRoute skip
            // straight to the graph that can see the gaps.
            _startOffWorldMesh = !TrySnapGraph0(from, out _, out _);
            if (_startOffWorldMesh)
                _log?.LogWarning($"[NAVIGATOR] Player at {from} is off the NPC navmesh " +
                                 "(no walkable graph-0 node) — graph-0 routing cannot work from here");

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
                // A* says WHY it failed, and the reason names the end that broke ("Couldn't find a
                // close node to the start point" vs the end point vs a genuinely searched-out graph).
                // Without it the log only ever said "unreachable", which is the one thing that was
                // never in doubt.
                var why = p?.errorLog;
                if (!string.IsNullOrEmpty(why))
                    _log?.LogInfo($"[NAVIGATOR] Graph-0 route failed: {why.Replace('\n', ' ').Trim()}");
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
    /// <param name="checkAgainstWorldMesh">
    /// Whether to vet the route against graph 0 first. True for a graph-0 route, whose own legs must
    /// stay on graph-0 walkable ground. FALSE for a route from the fine player graph: that one is
    /// legitimately threading gaps graph 0 calls solid — that is the entire reason it was asked —
    /// so checking it against graph 0 would reject every route it ever produces.
    /// </param>
    /// <param name="fromWorldMesh">
    /// Whether this route came from the road network, which is what a wall recovery must not
    /// discard. Normally the same thing as <paramref name="checkAgainstWorldMesh"/>; passed
    /// separately only by <see cref="WalkRejectedRouteAnyway"/>, which re-issues a road route with
    /// the checks off and must not have it demoted to a fine-graph route in the process.
    /// </param>
    private static void StartNativePathWalk(List<Vector3> waypoints, bool checkAgainstWorldMesh = true,
                                            bool? fromWorldMesh = null)
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null) { StopLongWalk(announce: false); return; }

            // Copy with z=0 — a waypoint with z>=1000 is a teleport marker in the follower.
            var path = new List<Vector3>(waypoints.Count);
            foreach (var w in waypoints) path.Add(new Vector3(w.x, w.y, 0f));

            // The follower walks the route as straight lines between waypoints, and it does it with
            // the body Kinematic, so a leg that cuts across a corner takes the player THROUGH it
            // rather than jamming. Check the route before handing it over — once, here, rather than
            // watching the player every frame, because the route is the thing that is either right
            // or wrong and a per-frame probe under a moving body misreads walking close to a wall.
            var corner = checkAgainstWorldMesh ? FirstRouteLegThroughGeometry(path) : null;
            if (corner.HasValue)
            {
                _log?.LogWarning($"[NAVIGATOR] Graph-0 route cuts through solid ground near {corner.Value}; guiding instead");
                BeaconBail("route would pass through geometry");
                return;
            }

            // Second, finer pass. The check above can only see obstacles graph 0 itself calls
            // solid, which is why fences, tree trunks and cliff lips sailed through it. Ask physics
            // what the body would really have hit, and route AROUND it on the fine player graph —
            // which is built from these same colliders — rather than gliding through it.
            //
            // Only graph-0 routes get here. A fine-graph route is not re-checked, which is both
            // correct (it already respects these colliders) and what stops this looping.
            if (checkAgainstWorldMesh)
            {
                var hit = FirstRouteLegThroughSolid(path, out var what);
                if (hit.HasValue)
                {
                    _log?.LogWarning($"[NAVIGATOR] Road route passes through '{what}' at {hit.Value}; routing around it");
                    _routeRejectedForSolid = path;
                    // Shares the one fine-graph attempt per walk with the wall recovery, on
                    // purpose. If this attempt succeeds we are already ON the fine graph, so
                    // recovery 2's "switch to the fine graph" has nothing left to do; if it fails,
                    // it failed seconds ago and would fail again. Either way the budget is spent
                    // on the same question.
                    if (!_fineRouteTried && TryFineGraphRoute("road route passes through solid ground",
                                                             WalkRejectedRouteAnyway))
                        return;
                    // The fine graph was already spent on this walk, or could not be started at
                    // all. Walk the original rather than lose the journey.
                    WalkRejectedRouteAnyway();
                    return;
                }
            }

            var finalDest = (Vector2)path[path.Count - 1];

            // Disable player control so the body becomes Kinematic (UpdateBodyPhysics). A Dynamic
            // body physically collides and JAMS at fences/gates; Kinematic glides along the navmesh
            // path exactly like an NPC. This is the key to scripted long-distance walking.
            character.control_enabled = false;
            _weDisabledControl = true;
            AllowTriggersWhileScripted(character);

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
            _currentRoute = path;
            // checkAgainstWorldMesh is only ever true for a graph-0 route, so it doubles as "this
            // route came from the road network" — the thing a wall recovery must not discard.
            _routeIsWorldMesh = fromWorldMesh ?? checkAgainstWorldMesh;

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

    /// <summary>
    /// Walk the route the game's follower is about to be given and return the first point on it
    /// that graph 0 — the very graph the route came from — calls solid, or null if it is clean.
    ///
    /// A graph-0 node is 76 units across, so a route between two waypoints several nodes apart can
    /// pass over ground neither endpoint knew about. Asked of graph 0 and ONLY graph 0 on purpose:
    /// the player graph calls every village gate blocked (that is why routes get escalated to
    /// graph 0 in the first place), so asking it here would reject every route through town. Where
    /// graph 0 has no data at all the answer is "clear" — the same rule as
    /// <see cref="StraightLineIsWalkable"/>, and for the same reason.
    /// </summary>
    private static Vector2? FirstRouteLegThroughGeometry(List<Vector3> path)
    {
        try
        {
            if (path == null || path.Count < 2) return null;

            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector2 a = path[i], b = path[i + 1];
                var delta = b - a;
                float length = delta.magnitude;
                if (length <= RouteProbeStep) continue;
                var dir = delta / length;

                // Endpoints are route nodes and walkable by construction; only the span between
                // them can hide a wall, so sample strictly inside it.
                for (float t = RouteProbeStep; t < length - RouteProbeStep * 0.5f; t += RouteProbeStep)
                {
                    var p = a + dir * t;
                    if (IsKnownBlockedOnWorldMesh(p)) return p;
                }
            }
        }
        catch (Exception ex)
        {
            // A probe that cannot run must never be the reason auto-walk stops working.
            _log?.LogWarning($"[NAVIGATOR] Route clearance probe failed: {ex.Message}");
        }
        return null;
    }

    /// Slightly under one graph-0 node (76 units), so no node on a leg can be stepped over.
    private const float RouteProbeStep = 64f;

    /// <summary>
    /// Walk the route the follower is about to be given and return the first point where a real
    /// COLLIDER sits on it, naming what it was, or null if the route is clean.
    ///
    /// This is the companion to <see cref="FirstRouteLegThroughGeometry"/>, and the reason clipping
    /// survived that one: it asks graph 0 — the same 76-unit grid the route was computed on — so
    /// anything thinner than a node is invisible to it, and where graph 0 has no data at all the
    /// answer is "clear". Measured over one play session: it rejected NOTHING while the player
    /// passed through 87 solid things, 78 of them on this very code path.
    ///
    /// Asks physics instead — what the player's body would have hit had it not been Kinematic.
    ///
    /// Walls AND props count here, unlike the per-frame wall guard, which lets props through
    /// because refusing to walk past a chair would take the indoor walks away. That argument does
    /// not apply at route level: a route rejected here is re-routed AROUND the obstacle, not
    /// stopped in front of it, so a fence can be out of bounds without a chair blocking anything.
    /// </summary>
    private static Vector2? FirstRouteLegThroughSolid(List<Vector3> path, out string what)
    {
        what = null;
        try
        {
            if (path == null || path.Count < 2) return null;

            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector2 a = path[i], b = path[i + 1];
                var delta = b - a;
                float length = delta.magnitude;
                if (length <= SolidProbeStep) continue;
                var dir = delta / length;

                // Skirt the two ends of the whole route, the same concession
                // StraightLineIsWalkable makes and for the same reason: the player very often
                // starts pressed against the station they were just using, and the last waypoint is
                // by definition right up against the thing being walked to. Without this a route
                // would be rejected before it began roughly whenever the player stood anywhere
                // interesting. Only the FIRST and LAST legs are skirted — a mid-route waypoint has
                // no such excuse.
                float from = (i == 0) ? SolidEndSkirt : SolidProbeStep;
                float to = (i == path.Count - 2) ? length - SolidEndSkirt : length - SolidProbeStep * 0.5f;

                for (float t = from; t < to; t += SolidProbeStep)
                {
                    var solid = SolidAt(a + dir * t, out _);
                    if (solid != null) { what = solid; return a + dir * t; }
                }
            }
        }
        catch (Exception ex)
        {
            // A probe that cannot run must never be the reason auto-walk stops working.
            _log?.LogWarning($"[NAVIGATOR] Route solidity probe failed: {ex.Message}");
        }
        return null;
    }

    /// Unity layer 9, the game's "Characters" layer. ComponentsManager.CheckCharacterStuff puts
    /// every character on it and drags every non-character off it, so it is a reliable "this thing
    /// walks around under its own power" test — see <see cref="SolidAt(Vector2, out bool)"/>.
    private const int CharactersLayer = 9;

    /// A sixth of a tile. Finer than <see cref="RouteProbeStep"/> because this is looking for real
    /// colliders rather than grid flags: the measured crossings start at 0.17 tiles, so a
    /// quarter-tile stride could straddle the thin ones entirely and see nothing.
    private const float SolidProbeStep = 16f;

    /// Half a tile of grace at each end of a route — see FirstRouteLegThroughSolid.
    private const float SolidEndSkirt = 0.5f * TileSize;

    /// The road route a solidity check rejected, kept so that if nothing can route around the
    /// obstacle we can still walk it rather than abandoning the journey over a bush.
    private static List<Vector3> _routeRejectedForSolid;

    /// <summary>
    /// Nothing could route around the obstacle on the road route. Walking through it is still a far
    /// better outcome for the player than losing the journey, so re-issue the original route — with
    /// the check off, which is also what stops this from looping — but keep it marked as a road
    /// route so a later wall recovery still treats it as one.
    /// </summary>
    private static void WalkRejectedRouteAnyway()
    {
        var path = _routeRejectedForSolid;
        _routeRejectedForSolid = null;
        if (path == null || !_longWalkActive) { FallBackToEscapeOrBeacon(); return; }
        _log?.LogInfo("[NAVIGATOR] Nothing routes around it; walking the road route as it is");
        StartNativePathWalk(path, checkAgainstWorldMesh: false, fromWorldMesh: true);
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

        // Escape-leg arrival: back on the NPC navmesh. Pick the real walk up again from here without
        // saying anything — to the player this is one continuous walk that took a moment to get
        // going, so re-announcing the destination is just the same sentence twice. It keeps the
        // escape budget, so if this spot is somehow still off the navmesh the fine-graph route and
        // the beacon take over rather than another hop.
        if (_escapeLegActive)
        {
            _escapeLegActive = false;
            var resumed = _escapeLegTarget;
            _log?.LogInfo($"[NAVIGATOR] Escape leg done; resuming walk to {resumed.Label}");
            StartLongWalk(resumed, afterEscapeLeg: true);
            return;
        }

        // Exit-assist arrival: at the door. Face it and remind the player to step outside.
        if (_exitAssisting)
        {
            _exitAssisting = false;
            _longWalkActive = false;
            _walkFacePos = target.Position;
            FacePlayerAtTarget();
            SetArrivedTarget(target.Object);
            ScreenReader.Say(Loc.Fmt("nav.at_the_door", _exitAssistLabel), interrupt: true);
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
            var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
            var remaining = Vector2.Distance(playerPos, target.Position);
            _log?.LogInfo($"[NAVIGATOR] Native walk ended {remaining:F0}u from {target.Label}");

            // We may not actually be anywhere near the target: see TryReplanFromPullbackLanding.
            if (remaining > FinalApproachReach &&
                TryReplanFromPullbackLanding(target, playerPos, remaining))
                return;

            _longWalkActive = false;

            // Stations/build desks need the player within ~1 tile to interact; doors/teleports can
            // be triggered from the lenient AtTargetDistance. Pick the right "arrived" radius so a
            // close-interaction target that ended a tile-plus short still gets the precise final
            // approach below (onto its synthetic dock) instead of being declared arrived too far out.
            var arrivedRadius = (target.ExactPoint || NeedsCloseInteraction(target.Object))
                ? InteractionArrivalDistance
                : AtTargetDistance;

            if (remaining <= arrivedRadius)
            {
                // At the interaction tile. Face it (don't graph-2 onto teleport tiles, which
                // fails) so vanilla E works.
                _walkFacePos = target.Position;
                FacePlayerAtTarget();
                SetArrivedTarget(target.Object);
                ScreenReader.Say(Loc.Fmt("nav.arrived_at", target.Label, DistanceText(remaining)), interrupt: true);
                // A drop we just walked onto gets shoved clear the moment the body turns Dynamic
                // again; watch it and chase it if E would no longer reach it.
                ArmDropSettleCheck(target);
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
                ScreenReader.Say(Loc.Fmt("nav.as_close_as_possible", target.Label, DistanceText(remaining), DirectionTo(target)), interrupt: true);
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
            ScreenReader.Say(Loc.Fmt("nav.arrived_near_entrance", target.Label, DistanceText(Vector2.Distance(playerPos, target.Position))), interrupt: true);
            _log?.LogInfo($"[NAVIGATOR] Reached closest navmesh point to {target.Label}");
        }
        else
        {
            // Partial route still closing in: re-route from here to continue into the next region.
            _routeNeedsRecompute = true;
        }
    }

    /// <summary>
    /// A journey that "arrived" a long way from where the player asked to go, because the thing it
    /// actually walked to was not the target at all.
    ///
    /// When graph 0 cannot route to the destination, <see cref="HandleNoRoute"/> drags
    /// <see cref="_longWalkDest"/> toward the player a step at a time until something answers. That
    /// is a good idea — it gets a player off an island and moving. What it is NOT is a shorter
    /// version of the same journey: after twelve pulls the destination can be a point next to where
    /// the player is standing, and whichever graph finally routes to it sets _routeReachesTarget,
    /// because as far as it knows it did reach the destination it was given. The walk then ends,
    /// and the player is told "as close as possible" about somewhere they never set off toward.
    ///
    /// Log, 2026-09-19: asked for the fishing spot from the vineyard, twelve pulls, a 199-point
    /// fine-graph route to a spot ~13 tiles away, and "Native walk ended 6952u from Angelplatz am
    /// Fluss" — ninety-one tiles short, announced as the best that could be done. The player asked
    /// again from exactly where it had left them and graph 0 returned an 85-waypoint route that
    /// reached the spot within 29 units. Nothing was in the way; the journey simply stopped
    /// believing in itself one leg early, and the player had to know to ask a second time.
    ///
    /// So: when a pulled-back leg lands us well short, and the leg actually MOVED us, re-plan the
    /// whole journey from here instead of announcing a result. The position is new, so graph 0 is
    /// being asked a genuinely different question — which is exactly what the player did by hand.
    /// Bounded by <see cref="MaxJourneyReplans"/>, and only where the leg made progress, so a walk
    /// that truly cannot get closer still reaches "as close as possible" or the beacon.
    /// </summary>
    private static bool TryReplanFromPullbackLanding(NavigationTarget target, Vector2 playerPos, float remaining)
    {
        // Only the pulled-back case. A route that was aimed at the real destination all along and
        // ended short has genuinely got as close as it can, and that is what we should say.
        if (_pullbackTries <= 0) return false;
        if (_journeyReplans >= MaxJourneyReplans) return false;

        // The leg has to have carried us somewhere. Re-planning from the spot we just failed at
        // asks the same graph the same question and is how this would loop.
        if (Vector2.Distance(playerPos, _longWalkStartPos) < TileSize)
        {
            _log?.LogInfo($"[NAVIGATOR] Pulled-back leg landed {remaining:F0}u short of {target.Label} " +
                          "without moving us; not re-planning");
            return false;
        }

        _journeyReplans++;
        _pullbackTries = 0;
        _fineRouteTried = false;
        _routeRejectedForSolid = null;
        _startOffWorldMesh = false;
        _routeReachesTarget = true;
        _finalPartial = false;
        _bestEndGap = float.MaxValue;
        _stalledRecomputes = 0;
        _longWalkStuckTicks = 0;
        _longWalkStartPos = playerPos;
        _longWalkProgressPos = playerPos;
        _longWalkAnnouncePos = playerPos;

        // Back to the REAL interaction tile — _longWalkDest is still the pulled-back point.
        _longWalkDest = InteractionDest(target, out _);
        _log?.LogInfo($"[NAVIGATOR] Pulled-back leg landed {remaining:F0}u short of {target.Label}; " +
                      $"re-planning from {playerPos} (replan {_journeyReplans}/{MaxJourneyReplans})");
        RequestGraph0Route(playerPos, _longWalkDest);
        return true;
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
                ScreenReader.Say(Loc.Fmt("nav.inside_building", _exitAssistLabel), interrupt: true);
                _log?.LogInfo($"[NAVIGATOR] Inside building; exit-assist to {door.Value.Label}");
                RequestGraph0Route(MainGame.me.player.pos, _longWalkDest);
                return;
            }
        }

        // Target on a graph-0 island (e.g. the house): pull the destination toward the player and
        // retry. The first point that routes is the reachable navmesh nearest the target; we walk
        // there and OnNativeWalkComplete reports the remaining gap to the real target.
        //
        // Only when the destination is the end that failed. If the PLAYER has no walkable graph-0
        // node under them, dragging the destination closer changes nothing — every query still
        // errors at the start — and each retry is a wasted path search before the graph that could
        // actually answer gets asked. A walk out of the graveyard spent five of them, converging on
        // the same point twice over, and then beaconed.
        var player = MainGame.me?.player;
        if (_startOffWorldMesh && !_fineRouteTried &&
            TryFineGraphRoute("player is off the NPC navmesh; pulling the destination cannot help",
                              FallBackToEscapeOrBeacon))
            return;

        if (player != null && !_startOffWorldMesh && _pullbackTries < MaxPullbackTries)
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

        // Before giving up on driving at all: ask the FINE graph.
        //
        // Graph 0 is the NPC navmesh and its nodes are 76 units — nearly a whole tile. In a place
        // packed with objects there is no walkable node left in the gaps, so it reports "no route"
        // for ground a player walks through without thinking. The graveyard is exactly that: from
        // among the graves, both A* attempts AND graph 0 failed to reach the mortuary door, and
        // then turn-by-turn guidance found an ordinary 243-waypoint route on the PLAYER graph,
        // whose 8-unit nodes fit between the headstones. The route was always there; nothing was
        // asking the graph that could see it.
        //
        // So try that route and drive it, exactly like a graph-0 one. Once per walk — if the fine
        // graph cannot find one either, we really are out of options and fall through below.
        if (!_fineRouteTried && TryFineGraphRoute("graph 0 gave up", FallBackToEscapeOrBeacon))
            return;

        FallBackToEscapeOrBeacon();
    }

    /// <summary>
    /// Everything aimed AT the destination has failed. Before handing the player a compass bearing,
    /// look around: <see cref="TryEscapeLegByExploring"/>.
    ///
    /// The gate used to be "the player cannot snap to a graph-0 node", and that was wrong in the one
    /// place it mattered. At the graveyard chest dock the player snaps perfectly well — to a node on
    /// an ISOLATED island — so the escape leg never ran there at all and the walk went straight to
    /// the pullback loop and the beacon. Total routing failure is the honest signal; how the player's
    /// own node happens to snap says nothing about whether they are stuck.
    /// </summary>
    private static void FallBackToEscapeOrBeacon()
    {
        if (_escapeLegsUsed < MaxEscapeLegs && TryEscapeLegByExploring()) return;
        FallBackToGlideOrBeacon();
    }

    /// One exploration per walk. If it does not get the player somewhere a route exists, a second
    /// would flood the same ground again; the glide and the beacon take over instead.
    private const int MaxEscapeLegs = 1;

    /// How far the exploration may wander, as PATH length in tiles (not straight-line distance).
    /// The player got out of the graveyard pocket by hand in under four tiles; twelve leaves room
    /// for a pocket whose way out doubles back.
    private const int EscapeFloodTiles = 12;

    /// Grid-graph connection costs are nodeSize * 1000 (GridGraph.SetUpOffsetsAndCosts), so a cost
    /// unit is a thousandth of a world unit and a path-length budget converts straight across.
    private const int EscapeFloodMaxGScore = (int)(EscapeFloodTiles * TileSize) * 1000;

    /// Slack added round the player-graph scan for the exploration, on every side. Eight tiles gives
    /// a 16-tile box - the size the perf note measured at 113ms - and comfortably contains a way out
    /// that the known case found in four.
    private const float EscapeGraphPadUnits = 8f * TileSize;

    /// Do not bother hopping for less than this: a walk shorter than a tile and a half is the 1-tile
    /// nudge guided walk already loops on, and it would burn the one exploration this walk gets.
    private const float EscapeMinProgress = 1.5f * TileSize;

    /// <summary>
    /// Look around, then walk to wherever local exploration got closest to the target, and resume.
    ///
    /// WHY EXPLORING RATHER THAN AIMING. Every other attempt in this class is a point-to-point
    /// search aimed AT the destination, over a player graph the game rebuilds as a thin strip
    /// between the player and that destination (AStarTools.RefreshPlayerGraph). Inside a pocket that
    /// is unanswerable twice over: the way out is a dogleg, and the nodes for its first leg are not
    /// in the strip, so they do not exist while the question is being asked. The player solved it by
    /// hand in seconds - walked to a nearby grave, and from there a route existed. The log has both
    /// halves: from the chest dock and from (1984,-1112) every graph said "no path", and from
    /// (2300.9,-1118.3), under four tiles further east, the fine graph immediately returned 355
    /// waypoints and drove the whole way to the target.
    ///
    /// So this does what the player did. It rescans the player graph as a BOX round the player
    /// (<see cref="EscapeGraphPadUnits"/>), floods it with a <c>ConstantPath</c> - which searches
    /// outward from a start with no destination at all and hands back every node it reached - and
    /// walks to whichever reached node lies nearest the real target. That node is reachable by
    /// construction, so the hop cannot fail the way an aimed search does.
    ///
    /// DO NOT go back to picking the escape point geometrically. Two versions did and both failed:
    /// nearest walkable graph-0 node (0.9 tiles, an isolated island node, changed nothing), then
    /// nearest node on the destination's graph-0 Area - which also misses, because graph 0 still
    /// gave up at (2300.9,-1118.3), the very spot that worked. Being on the NPC navmesh is not the
    /// property that matters; being somewhere the FINE graph can route from is, and only walking the
    /// fine graph can tell you that.
    /// </summary>
    /// <returns>True if an exploration was started (the caller must not do anything else).</returns>
    private static bool TryEscapeLegByExploring()
    {
        var pl = MainGame.me?.player;
        if (pl == null) return false;
        var from = pl.pos;
        var goal = _longWalkDest;

        try
        {
            if (AstarPath.active == null) return false;

            // A box round the player, not a strip toward the goal - the way out may lead away from
            // it. Costly, so it happens once, only after everything aimed at the target has failed.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _padOverrideUnits = EscapeGraphPadUnits;
            PadPlayerGraph = true;
            try { AStarTools.RefreshPlayerGraph(from, from); }
            catch (Exception ex) { _log?.LogWarning($"[NAVIGATOR] Escape rescan failed: {ex.Message}"); }
            finally { PadPlayerGraph = false; _padOverrideUnits = null; }

            var flood = Pathfinding.ConstantPath.Construct(
                new Vector3(from.x, from.y, 0f), EscapeFloodMaxGScore, p =>
                {
                    _routePending = false;
                    if (!_longWalkActive) return;
                    OnEscapeFloodComplete(p as Pathfinding.ConstantPath, from, goal);
                });

            var constraint = Pathfinding.NNConstraint.Default;
            constraint.graphMask = 1 << AStarTools.PLAYER_GRAPH_N;
            flood.nnConstraint = constraint;

            // Counted here, not on commit: the flood itself is the expensive part, so a walk that
            // explores and finds nowhere better must not be able to pay for it twice.
            _escapeLegsUsed++;
            _routePending = true;
            AstarPath.StartPath(flood);
            _log?.LogInfo($"[NAVIGATOR] Nothing routes to {_longWalkTarget.Label}; exploring on foot " +
                          $"from {from} (rescan {sw.ElapsedMilliseconds}ms)");
            return true;
        }
        catch (Exception ex)
        {
            _routePending = false;
            _log?.LogWarning($"[NAVIGATOR] Escape exploration failed to start: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The exploration finished. Walk to the reached spot that gets nearest the real target.
    /// </summary>
    private static void OnEscapeFloodComplete(Pathfinding.ConstantPath flood, Vector2 from, Vector2 goal)
    {
        var nodes = flood?.allNodes;
        if (flood == null || flood.error || nodes == null || nodes.Count == 0)
        {
            _log?.LogInfo("[NAVIGATOR] Exploration reached nowhere; falling back");
            FallBackToGlideOrBeacon();
            return;
        }

        float here = Vector2.Distance(from, goal);
        float bestGain = 0f;
        var best = from;
        bool found = false;

        foreach (var node in nodes)
        {
            if (node == null || !node.Walkable) continue;
            var np = (Vector3)node.position;
            var at = new Vector2(np.x, np.y);
            float gain = here - Vector2.Distance(at, goal);
            if (gain <= bestGain) continue;
            bestGain = gain;
            best = at;
            found = true;
        }

        // Nothing reachable is meaningfully closer than where we stand. On the navmesh this is a
        // sealed pocket - so stop believing the navmesh and ask physics instead.
        if (!found || bestGain < EscapeMinProgress || Vector2.Distance(from, best) < EscapeMinProgress)
        {
            _log?.LogInfo($"[NAVIGATOR] Explored {nodes.Count} reachable spots; none gets closer to " +
                          $"{_longWalkTarget.Label} (best gain {bestGain / TileSize:F1} tiles)");
            if (TrySqueezeOutOfPocket(from, goal, nodes)) return;
            FallBackToGlideOrBeacon();
            return;
        }

        _escapeLegTarget = _longWalkTarget;
        _escapeLegRealDest = _longWalkDest;
        _escapeLegActive = true;
        _longWalkDest = best;
        _routeReachesTarget = true;
        _finalPartial = false;
        _log?.LogInfo($"[NAVIGATOR] Explored {nodes.Count} reachable spots; escape leg to {best} " +
                      $"({Vector2.Distance(from, best) / TileSize:F1} tiles away, " +
                      $"{bestGain / TileSize:F1} tiles closer) before {_escapeLegTarget.Label}");

        _routePending = true;
        if (StartRouteQuery(from, best, AStarTools.PLAYER_GRAPH_N, route =>
            {
                _routePending = false;
                if (!_longWalkActive) return;
                if (route != null && route.Count >= 2)
                {
                    var wps = new List<Vector3>(route.Count);
                    foreach (var w in route) wps.Add(new Vector3(w.x, w.y, 0f));
                    _log?.LogInfo($"[NAVIGATOR] Escape leg: {wps.Count} points");
                    // checkAgainstWorldMesh: false - the whole point is that graph 0 calls this
                    // ground solid; vetting the route against it would reject every one.
                    StartNativePathWalk(wps, checkAgainstWorldMesh: false);
                    return;
                }
                // Should not happen: the node came out of a flood from here, so it is reachable.
                _log?.LogInfo("[NAVIGATOR] Escape leg: no route to the explored spot; falling back");
                RestoreWalkAfterFailedEscape();
                FallBackToGlideOrBeacon();
            }))
            return;

        _routePending = false;
        RestoreWalkAfterFailedEscape();
        FallBackToGlideOrBeacon();
    }

    /// <summary>
    /// Put BOTH halves of the parked walk back. Restoring the label alone left _longWalkDest
    /// pointing at the escape point, so the beacon aimed at that instead of at the thing the player
    /// actually asked for.
    /// </summary>
    private static void RestoreWalkAfterFailedEscape()
    {
        _escapeLegActive = false;
        _longWalkTarget = _escapeLegTarget;
        _longWalkDest = _escapeLegRealDest;
    }

    /// How far to look for ground outside the pocket. The gap is by definition right next to the
    /// player, so this stays short — a long straight glide is exactly what must not happen here.
    private const int EscapeSqueezeTiles = 6;

    /// A candidate must snap to a node this close to the probe point. Without it, GetNearest happily
    /// returns a walkable node on the far side of the map and the "way out" is nonsense.
    private const float EscapeSqueezeSnapSlack = 0.75f * TileSize;

    /// How much "level geometry" the squeeze may cross in one unbroken run. A bush or a fence post
    /// is a fraction of a tile on the line; a cliff face or a building shell keeps going. One tile
    /// separates them without needing a list of object names.
    private const float EscapeSqueezePassableThickness = 1f * TileSize;

    /// <summary>
    /// Cross a gap the navmesh says is shut, because the player can physically walk through it.
    ///
    /// THE SITUATION, measured. Flooding the player graph from the graveyard chest dock reached
    /// **134 nodes** — at 8 units a node, under one tile of area — and the same 134 from a second
    /// spot a tile away. The player is sealed into a sub-tile pocket ON THE GRAPH. They are not
    /// sealed in reality: they walked out by hand both times. Graph 2 is scanned with a collision
    /// radius fatter than the player's own body, so the gap between two graves closes on the navmesh
    /// while the real collider fits through — and a player pressing into a headstone SLIDES round it,
    /// which no graph models at all. That is why every graph-based attempt failed and no better
    /// search could have helped: they were all asking a graph in which the exit does not exist.
    ///
    /// So this asks physics. It looks for a spot that (a) is on a DIFFERENT graph-2 component from
    /// the pocket, so it is genuinely outside, (b) is somewhere a player could stand — nothing solid
    /// at that point — and (c) has no level geometry on the straight line to it. Then it glides
    /// there: two waypoints through the native follower, which walks them Kinematic, so the too-tight
    /// gap is crossed.
    ///
    /// The wall/prop distinction in <see cref="StraightLineIsWalkable"/> is what makes that safe and
    /// is not negotiable: sliding past a GRAVE is something the player does by hand, passing through
    /// a BUILDING SHELL is not. Kept short (<see cref="EscapeSqueezeTiles"/>) so it can only ever be
    /// the step through the gap, never a shortcut across the map.
    /// </summary>
    /// <returns>True if a squeeze was started (the caller must not do anything else).</returns>
    private static bool TrySqueezeOutOfPocket(Vector2 from, Vector2 goal, List<Pathfinding.GraphNode> pocket)
    {
        if (pocket == null || pocket.Count == 0) return false;
        uint pocketArea = pocket[0].Area;
        float here = Vector2.Distance(from, goal);

        for (int ring = 1; ring <= EscapeSqueezeTiles; ring++)
        {
            float bestGain = 0f;
            var best = from;
            bool found = false;

            int samples = 8 * ring;
            for (int i = 0; i < samples; i++)
            {
                float a = (float)(2.0 * Math.PI * i / samples);
                var cand = from + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (ring * TileSize);

                if (!TryPlayerGraphNode(cand, out var node, out var nodePos)) continue;
                if (node == null || !node.Walkable) continue;
                if (node.Area == pocketArea) continue;                              // still inside
                if (Vector2.Distance(cand, nodePos) > EscapeSqueezeSnapSlack) continue;
                if (SolidAt(nodePos) != null) continue;                             // cannot stand there
                // A bush's worth of solid may be crossed, a cliff face may not — see the parameter's
                // note. Without this every way out of the woods is vetoed by scenery.
                if (!StraightLineIsWalkable(from, nodePos, EscapeSqueezePassableThickness)) continue;

                float gain = here - Vector2.Distance(nodePos, goal);
                if (gain <= bestGain) continue;
                bestGain = gain;
                best = nodePos;
                found = true;
            }

            if (!found || bestGain < EscapeMinProgress) continue;

            _escapeLegTarget = _longWalkTarget;
            _escapeLegRealDest = _longWalkDest;
            _escapeLegActive = true;
            _longWalkDest = best;
            _routeReachesTarget = true;
            _finalPartial = false;
            _log?.LogInfo($"[NAVIGATOR] Pocket is {pocket.Count} nodes on graph area {pocketArea}; " +
                          $"squeezing out to {best} ({Vector2.Distance(from, best) / TileSize:F1} tiles, " +
                          $"{bestGain / TileSize:F1} tiles closer) before {_escapeLegTarget.Label}");

            // Two points: the native follower walks that as a straight Kinematic line, which is the
            // only thing that gets through a gap the graph does not have.
            StartNativePathWalk(
                new List<Vector3> { new Vector3(from.x, from.y, 0f), new Vector3(best.x, best.y, 0f) },
                checkAgainstWorldMesh: false);
            return true;
        }

        _log?.LogInfo($"[NAVIGATOR] No way out of the pocket within {EscapeSqueezeTiles} tiles that a " +
                      "player could walk; falling back");
        return false;
    }

    /// <summary>
    /// The nearest player-graph (fine, 8-unit) node to a world point, plus its own position. Same
    /// shape as <see cref="TryGraph0Node"/>; the caller must have scanned the graph over the area it
    /// is asking about, since graph 2 only ever holds the last box that was scanned.
    /// </summary>
    private static bool TryPlayerGraphNode(Vector2 p, out Pathfinding.GraphNode node, out Vector2 nodePos)
    {
        node = null;
        nodePos = p;
        try
        {
            var astar = AstarPath.active;
            if (astar == null) return false;

            var constraint = Pathfinding.NNConstraint.Default;
            constraint.graphMask = 1 << AStarTools.PLAYER_GRAPH_N;

            var nn = astar.GetNearest(new Vector3(p.x, p.y, 0f), constraint);
            if (nn.node == null) return false;
            node = nn.node;
            nodePos = new Vector2(nn.clampedPosition.x, nn.clampedPosition.y);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] TryPlayerGraphNode failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Ask the fine player graph for a route to the current long-walk destination and drive it.
    ///
    /// Graph 0 is the NPC navmesh and its nodes are 76 units — nearly a whole tile. Where objects
    /// are packed together there is no walkable node left in the gaps, so it reports "no route" for
    /// ground a player walks across without thinking. The graveyard is exactly that: from among the
    /// graves both A* attempts AND graph 0 failed to reach the mortuary door, and turn-by-turn
    /// guidance then found an ordinary 243-waypoint route on the PLAYER graph, whose 8-unit nodes
    /// fit between the headstones. The route was always there; nothing was asking the graph that
    /// could see it.
    /// </summary>
    /// <returns>True if a query was started (the caller must not do anything else).</returns>
    private static bool TryFineGraphRoute(string reason, Action onFail)
    {
        var from = MainGame.me?.player?.pos;
        if (!from.HasValue) return false;

        _fineRouteTried = true;
        var to = _longWalkDest;

        // Forced, and with the wide bounds: a route that has to go around a building leaves the thin
        // strip the game scans by default, and the rate-limited refresh would skip the rescan.
        _widePlayerGraphPad = true;
        PadPlayerGraph = true;
        try { AStarTools.RefreshPlayerGraph(from.Value, to); }
        catch (Exception ex) { _log?.LogWarning($"[NAVIGATOR] Fine-graph refresh failed: {ex.Message}"); }
        finally { PadPlayerGraph = false; _widePlayerGraphPad = false; }

        _routePending = true;
        _log?.LogInfo($"[NAVIGATOR] {reason}; trying the fine player graph {from.Value} -> {to}");
        if (StartRouteQuery(from.Value, to, AStarTools.PLAYER_GRAPH_N, route =>
            {
                _routePending = false;
                if (!_longWalkActive) return;
                if (route != null && route.Count >= 2)
                {
                    var wps = new List<Vector3>(route.Count);
                    foreach (var w in route) wps.Add(new Vector3(w.x, w.y, 0f));
                    _routeReachesTarget = true;
                    _finalPartial = false;
                    _log?.LogInfo($"[NAVIGATOR] Fine player-graph route found: {wps.Count} points");
                    StartNativePathWalk(wps, checkAgainstWorldMesh: false);
                    return;
                }
                _log?.LogInfo("[NAVIGATOR] Fine player graph has no route either");
                onFail();
            }))
            return true;

        _routePending = false;
        return false;
    }

    /// <summary>
    /// Last resorts once no graph can produce a route: a short straight-line glide if the target is
    /// close enough for one to be safe, otherwise turn-by-turn guidance.
    /// </summary>
    private static void FallBackToGlideOrBeacon()
    {
        // For a NEARBY target a total routing failure is the tell-tale of a building interior — the
        // house/mortuary sit on a graph-0 island disconnected from the outdoor navmesh, and the
        // player graph frequently has no node near the object either (e.g. the bed inside the
        // house: "No walkable player-graph node near target"). Rather than dumping a blind player
        // onto the manual compass beacon, glide there in a straight line: during a scripted walk the
        // body is Kinematic (control disabled), so it slides to the spot without jamming on the
        // walls, and inside a single room the line to the target is clear. StraightLineIsWalkable
        // refuses the glide if a wall is actually in the way. Bounded to short hops — a FAR failure
        // would try to glide across the whole map, so that still beacons.
        var pl = MainGame.me?.player;
        if (pl != null && Vector2.Distance(pl.pos, _longWalkTarget.Position) <= LongWalkStartDistance)
        {
            var target = _longWalkTarget;
            _log?.LogInfo($"[NAVIGATOR] Graph-0 unreachable but {target.Label} is near; direct glide fallback");
            if (DirectGlideTo(target)) return;

            // A wall is on the line. Before handing a blind player the compass: if we are standing
            // where an earlier glide dropped us, go back to where it launched from — that spot is
            // on the navmesh by construction, and the wall we cannot cross here is one the route
            // from there goes around. Keeps _longWalkActive, so the resumed walk continues silently.
            if (_returnGlidesUsed < MaxReturnGlides && TryReturnGlideToOrigin()) return;

            _longWalkActive = false;
            ScreenReader.Say(Loc.Fmt("nav.manual_guidance", target.Label), interrupt: true);
            GuidedWalk.StartTo(target, announceStart: false, allowBeaconFallback: true);
            return;
        }

        BeaconBail("Graph-0 route unavailable");
    }

    /// <summary>
    /// Last-resort short auto-walk: a straight-line Kinematic glide to a nearby target when neither
    /// the player graph nor graph-0 can path to it — the typical situation inside a building interior
    /// (a disconnected navmesh island). Used instead of the compass beacon so a blind player still
    /// gets driven to the bed/chest inside the house. StartWalk disables control (Kinematic body), so
    /// the straight line slides along without colliding; on Direct failure it releases and reports.
    /// </summary>
    /// <returns>
    /// True if the glide was started (the caller must not do anything else). False if a wall sits on
    /// the line, in which case the walk is untouched and still active for the caller to fall back.
    /// </returns>
    private static bool DirectGlideTo(NavigationTarget target)
    {
        var dest = InteractionDest(target, out var facePos);

        // Land on a tile the player body actually fits on. The glide is Kinematic, so it passes
        // straight through geometry and drops the player wherever the raw approach point happens to
        // be — and for an object tucked into an alcove (a dungeon stairwell, a prop against a wall)
        // that point is INSIDE the scenery. Control comes back, the body turns Dynamic inside a
        // collider, and the player is wedged: nothing routes out of that pocket and manual walking
        // does nothing either. Snapping to the nearest walkable player-graph node costs a fraction
        // of a tile of precision and makes that outcome impossible.
        PadPlayerGraph = true;
        try
        {
            var snapped = SnapToWalkable(dest);
            if (Vector2.Distance(dest, snapped) <= 1.5f * TileSize) dest = snapped;
            else _log?.LogWarning($"[NAVIGATOR] Glide dest {dest}: nearest walkable node too far, gliding to the raw point");
        }
        finally
        {
            PadPlayerGraph = false;
        }

        _fallbackPending = false;
        _escalatePending = false;
        _shortWalkTarget = target;   // so on_complete biases vanilla E onto it
        _walkFacePos = facePos;      // face it on arrival so plain E interacts

        // The glide is a straight line driven through a Kinematic body, so nothing physically stops
        // it: if the line crosses a wall, the player is dragged through the wall. A sighted player
        // cannot do that and neither may this — even at the cost of the auto-walk. Checked here,
        // after SnapToWalkable, because that call is what refreshes the player graph over the whole
        // player->target span, so the navmesh half of the test has data to answer with.
        // _shortWalkTarget is assigned first so the probe knows not to count the target itself.
        var player = MainGame.me?.player;
        var here = PlayerBodyPos(player);
        if (!StraightLineIsWalkable(here, dest))
        {
            _log?.LogWarning($"[NAVIGATOR] Direct glide to {target.Label} would pass through geometry");
            return false;
        }

        // Drop the breadcrumb. This glide may be about to land the player somewhere no graph has a
        // node — that is precisely when it is used — and the spot we are leaving is the one known to
        // route, so it is the only way back out. Recorded even when graph 0 cannot snap here: the
        // flag says how much to trust it, TryReturnGlideToOrigin decides.
        if (player != null)
        {
            _glideOrigin = player.pos;
            _glideOriginValid = TrySnapGraph0(player.pos, out _, out _);
        }

        _longWalkActive = false;
        // No fresh "Walking to…" — StartLongWalk already announced this walk; a second would double up.
        StartWalk(dest, target.Label, MovementComponent.GoToMethod.Direct);
        return true;
    }

    /// One per walk. If going back to where we came from does not produce a route, a second trip
    /// would only walk the same line again; guided walk takes over instead.
    private const int MaxReturnGlides = 1;

    /// The breadcrumb is only meaningful while the player is still standing roughly where the glide
    /// put them. Beyond this they have walked off by hand and the spot says nothing about them.
    private const float ReturnGlideMaxTiles = 8f;

    /// Only "are we still standing on the breadcrumb". This is NOT the escape leg's 1.5-tile
    /// minimum and must not be tied to it: that one stops a pointless nudge from burning the one
    /// exploration a walk gets, and it measures progress TOWARD the goal. Here the whole point is a
    /// short hop backwards, and the value of it has nothing to do with its length — half a tile is
    /// enough to land on a graph-0 node (they are 76 units) and that is all that is being bought.
    /// Gating this on the escape minimum is what made the first build a no-op: the bed→door
    /// breadcrumb is 1.4 tiles, so every single return glide was declined (2026-09-05 log).
    private const float ReturnGlideMinTiles = 0.5f;

    /// <summary>
    /// Go back to where the last Direct glide launched from, then resume the walk from there.
    ///
    /// THE SITUATION, from the 2026-09-05 log. Inside the house the mod glides the player to the
    /// bed at (2452.9,-6247.6) — a spot with no walkable node on graph 0 AND none on the player
    /// graph (both were logged at the time: "Route dest … has no walkable graph-0 node", "No
    /// walkable player-graph node near target"). Every walk asked for from there is then dead on
    /// arrival: graph-0 routing has no start node, the exploration flood reaches NOWHERE (zero
    /// nodes, not a small pocket — <see cref="TrySqueezeOutOfPocket"/> cannot help, it needs a
    /// pocket to compare graph areas against), and the one remaining tool, a straight glide, is
    /// correctly refused by the wall between the bedroom and the kitchen. The same walk to the same
    /// oven succeeded earlier in that log purely because the player happened to be standing at the
    /// chest instead, which does have graph nodes.
    ///
    /// Vetoing the glide IN is not the fix — it is the only way the mod reaches the bed or the
    /// graveyard chest at all, and refusing it strands a blind player at the door. The way out is
    /// the way in, backwards: the launch spot routed a moment ago, and the line back to it is one
    /// we have already glided along, so <see cref="StraightLineIsWalkable"/> passes it.
    ///
    /// Driven as an escape leg, so the arrival handler in OnNativeWalkComplete resumes the real walk
    /// silently — to the player this is one walk that took a moment to get going.
    /// </summary>
    /// <returns>True if the return glide was started (the caller must not do anything else).</returns>
    private static bool TryReturnGlideToOrigin()
    {
        // Logged on every decline, deliberately. The first build of this declined silently and the
        // log showed only the glide veto it was supposed to rescue — which reads exactly like the
        // code not running at all, and cost a whole test round to tell apart.
        if (!_glideOriginValid) { _log?.LogInfo("[NAVIGATOR] No return glide: no breadcrumb from a glide that routed"); return false; }
        if (!_longWalkActive) return false;
        var pl = MainGame.me?.player;
        if (pl == null) return false;

        var from = pl.pos;
        float d = Vector2.Distance(from, _glideOrigin);
        if (d < ReturnGlideMinTiles * TileSize || d > ReturnGlideMaxTiles * TileSize)
        {
            _log?.LogInfo($"[NAVIGATOR] No return glide: breadcrumb {_glideOrigin} is {d / TileSize:F1} tiles away");
            return false;
        }
        if (!StraightLineIsWalkable(PlayerBodyPos(pl), _glideOrigin)) return false;

        _returnGlidesUsed++;
        _glideOriginValid = false;   // consumed: we are leaving that spot behind either way
        _escapeLegTarget = _longWalkTarget;
        _escapeLegRealDest = _longWalkDest;
        _escapeLegActive = true;
        _longWalkDest = _glideOrigin;
        _routeReachesTarget = true;
        _finalPartial = false;
        _log?.LogInfo($"[NAVIGATOR] Stranded where a glide dropped us; gliding back to {_glideOrigin} " +
                      $"({d / TileSize:F1} tiles) before {_escapeLegTarget.Label}");

        // Two points, walked Kinematic by the native follower — the same vehicle the squeeze uses,
        // and the same line we came in on.
        StartNativePathWalk(
            new List<Vector3> { new Vector3(from.x, from.y, 0f), new Vector3(_glideOrigin.x, _glideOrigin.y, 0f) },
            checkAgainstWorldMesh: false);
        return true;
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
        _log?.LogWarning($"[NAVIGATOR] {reason}; beacon fallback");

        // The walk that just died was the dungeon escape: bearing-and-distance is no answer when the
        // reason you can't reach the exit may be that you can't move at all. Offer the teleport.
        if (_escapeExitObject != null && target.Object == _escapeExitObject)
        {
            StartBeacon(target);
            ArmEscapeTeleport();
            return;
        }

        // Hand over to turn-by-turn rather than a straight-line bearing: a route the auto-walker
        // cannot DRIVE is usually still a route the player can WALK (a gate it jams on, a stall it
        // cannot squeeze past). GuidedWalk drops back to the beacon itself if graph 0 has no route.
        ScreenReader.Say(Loc.Fmt("nav.manual_guidance", target.Label), interrupt: true);
        GuidedWalk.StartTo(target, announceStart: false, allowBeaconFallback: true);
    }

    internal static void StopLongWalk(bool announce)
    {
        if (!_longWalkActive) return;
        _longWalkActive = false;
        _routePending = false;
        _routeNeedsRecompute = false;
        _exitAssisting = false;
        _escapeLegActive = false;
        ReleaseScriptControl();
        _isWalking = false;
        if (announce)
            ScreenReader.Say(Loc.Get("nav.walking_stopped"), interrupt: true);
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
            ScreenReader.Say(Loc.Fmt("nav.autowalk_blocked", target.Label), interrupt: true);
            _log?.LogWarning($"[NAVIGATOR] Long walk stuck near {playerPos} (walking={_isWalking}), beacon fallback");
            StartBeacon(target);
            return;
        }

        // Periodic remaining-distance announcement so the player knows it's progressing.
        if (Vector2.Distance(playerPos, _longWalkAnnouncePos) >= AnnounceProgressDistance)
        {
            _longWalkAnnouncePos = playerPos;
            ScreenReader.Say(Loc.Fmt("nav.label_distance", target.Label, DistanceText(Vector2.Distance(playerPos, target.Position))), interrupt: false);
        }
    }

    // ---- Guided walk hooks (turn-by-turn manual walking; see GuidedWalk) ----
    //
    // GuidedWalk needs the same three things the auto-walker uses — the selected target, the
    // interaction tile to aim at, and an obstacle-aware graph-0 route — but drives none of them
    // itself: the player walks. These are the only doors into this class it needs.

    /// <summary>The currently selected navigable object, refreshed first (mirrors WalkToSelected).</summary>
    internal static bool TryGetSelectedTarget(out NavigationTarget target)
    {
        target = default;
        EnsureFreshList();
        var list = CurrentList;
        if (list.Count == 0) return false;
        if (_selectedIndex >= list.Count) _selectedIndex = 0;
        target = list[_selectedIndex];
        return true;
    }

    /// <summary>The tile to guide the player onto, plus what to face there so vanilla E works.</summary>
    internal static Vector2 GuidedDestFor(NavigationTarget target, out Vector2? facePos)
        => InteractionDest(target, out facePos);

    internal static string DistanceWords(float worldDistance) => DistanceText(worldDistance);

    internal static string CompassWord(Vector2 from, Vector2 to) => CompassDirection(from, to);

    // Cached probes for IsWalkableSpot. NNConstraint.Default ALLOCATES a new object on every read
    // and — the part that matters — constrains to walkable nodes, which makes it the wrong tool
    // for asking whether a spot is walkable: it happily returns a walkable node several tiles away
    // and the caller concludes the fence isn't there. These take the nearest node whatever its
    // state, so node.Walkable is the actual answer. One per graph: see IsWalkableSpot.
    private static readonly Pathfinding.NNConstraint[] _walkProbe = new Pathfinding.NNConstraint[3];
    private static readonly float[] _probeNodeSize = new float[3];

    private enum Ground { Unknown, Walkable, Blocked }

    /// <summary>
    /// May the player stand on this exact spot? Asked of the PLAYER graph (graph 2) first: its
    /// nodes are 8 units across and it is scanned with the player's own collision diameter, so
    /// where it has data it answers exactly the question — and it is the graph auto-walk uses for
    /// anything short, which is why auto-walk gets through gaps that graph 0 calls solid. Graph 0
    /// (the whole-map NPC navmesh, 76-unit nodes) answers everywhere else.
    /// </summary>
    internal static bool IsWalkableSpot(Vector2 p)
    {
        var player = ProbeGround(p, AStarTools.PLAYER_GRAPH_N);
        if (player != Ground.Unknown) return player == Ground.Walkable;
        return ProbeGround(p, 0) == Ground.Walkable;
    }

    /// <summary>
    /// May the player be dragged along this straight line, or does it pass through a wall?
    ///
    /// WHY THIS EXISTS: auto-walk drives the player with control disabled, which makes the game
    /// switch their Rigidbody2D to Kinematic (UpdateBodyPhysics). That is deliberate and load-bearing
    /// — a Dynamic body jams against every fence rail and gate on a long route, which is what made
    /// auto-walk useless before — but a Kinematic body is not stopped by anything, so a movement the
    /// game is asked to make in a STRAIGHT LINE goes through whatever is in the way. Following an
    /// A* route that is fine: the route only runs over ground the navmesh says is walkable, and the
    /// gates it threads are the same ones NPCs walk through. Issuing GoToMethod.Direct is not: the
    /// game builds a two-point path (current position, destination) and slides the player down it.
    /// A sighted player cannot walk through a wall, so neither may this.
    ///
    /// The test is PHYSICS ONLY — see <see cref="SolidAt"/>. The first version also sampled the
    /// navmesh and refused a line that crossed a blocked node, and that broke the one thing the
    /// glide exists for: walking from the door of the house to the bed. It could not have worked.
    /// A glide only ever happens BECAUSE the navmesh failed — the house interior sits on a
    /// disconnected island the player graph cannot path across — so asking that same navmesh
    /// whether the line is clear will always say no. Confirmed in a play session: every glide to
    /// the bed and to an inner door was vetoed, and the walks the player actually clipped through
    /// were long native routes, which this check never sees.
    ///
    /// On a refusal the caller falls back to turn-by-turn guidance, which walks the player there
    /// under their own control and cannot clip anything.
    /// </summary>
    /// <param name="passableThickness">
    /// How much solid the line may cross before it counts as a wall, measured as the length of an
    /// UNBROKEN blocked run along the line. Zero — the default — means any level geometry at all
    /// stops the walk, which is right for a glide across open ground.
    ///
    /// The squeeze out of a pocket passes about a tile, and it must, because "level geometry" is a
    /// far blunter category than its name suggests: it means only that the collider has no
    /// WorldGameObject behind it, so a decorative <c>bush_1_simple</c> is a wall while the
    /// harvestable <c>bush_3_berry(Clone)</c> standing next to it is a prop. The walk home from the
    /// village died on exactly that — the clearance probe showed 3.3 tiles of open ground south and
    /// 3.2 west, and every candidate that way was vetoed by a bush. A run length tells the two apart
    /// without a list of names: a bush is a fraction of a tile thick on the line, a cliff face or a
    /// building shell goes on and on.
    /// </param>
    private static bool StraightLineIsWalkable(Vector2 from, Vector2 to, float passableThickness = 0f)
    {
        var delta = to - from;
        float length = delta.magnitude;
        if (length <= WallProbeStep) return true;
        var dir = delta / length;

        float run = 0f;
        string runName = null;
        Vector2 runAt = from;

        // Skip the first and last step: the player commonly starts pressed against a prop, and the
        // destination is by definition right up against the thing being walked to.
        for (float t = WallProbeStep; t < length - WallProbeStep; t += WallProbeStep)
        {
            var p = from + dir * t;
            // Anything solid counts, prop or not. What decides whether the glide may pass is the
            // THICKNESS of the run below, not what the collider is attached to: a table is a few
            // units of nothing much and a scripted walk has always slid past it, a building wall is
            // not. Skipping world objects here was how a walk went through the smith's house — the
            // house is a world object, and so are canopies and roofs.
            var solid = SolidAt(p, out _);
            if (solid == null)
            {
                run = 0f;
                continue;
            }

            if (run <= 0f) { runAt = p; runName = solid; }
            run += WallProbeStep;
            if (run <= passableThickness) continue;

            // Over the limit, so the answer is already no. Keep probing anyway, purely to report
            // how thick the thing actually is: bailing here made the message always say exactly one
            // step (0.25 tiles) whether it had hit a sliver or a cliff face, which is useless for
            // deciding what a sane passableThickness would be — and it did mislead a reading of the
            // log once (2026-09-05). The decision above is unchanged; only the number is now true.
            float measured = run;
            for (float u = t + WallProbeStep; u < length - WallProbeStep; u += WallProbeStep)
            {
                if (SolidAt(from + dir * u, out _) == null) break;
                measured += WallProbeStep;
            }

            _log?.LogInfo($"[NAVIGATOR] Straight line rejected: wall '{runName}' fills " +
                          $"{measured / TileSize:F2} tiles from {runAt}");
            return false;
        }

        return true;
    }

    /// A quarter tile, so a wall thinner than the sampling stride cannot be stepped over.
    private const float WallProbeStep = 0.25f * TileSize;

    // Reused so the probes below allocate nothing.
    private static readonly Collider2D[] _overlapBuffer = new Collider2D[16];

    /// <summary>
    /// The name of the solid thing occupying this exact spot, or null if a player could stand there.
    ///
    /// An OVERLAP test, deliberately, not a linecast. A linecast from A to B reports every collider
    /// whose edge the segment crosses, which includes ones it merely grazes — walking past a tree,
    /// along a fence, or through a doorway all register. That is why the first version of this check
    /// vetoed the walk from the door to the bed: the line brushed the furniture. "Is this point
    /// inside something solid" has no such ambiguity, and it is the actual question — a player
    /// cannot stand inside a wall.
    ///
    /// "Solid" means what it means for the player: a non-trigger collider on a layer the player's
    /// own collider is not set to ignore. Zone volumes and script triggers stop nobody, the player's
    /// own colliders are not an obstacle to themselves, and the object being walked TO is never in
    /// the way — arriving at a chest means ending up against it.
    /// </summary>
    private static string SolidAt(Vector2 p) => SolidAt(p, out _);

    /// <summary>
    /// <see cref="SolidAt(Vector2)"/>, also telling the caller whether what it found is a WALL —
    /// level geometry with no WorldGameObject behind it — or a prop.
    ///
    /// The distinction is what keeps this useful. Level geometry is the building shells, the cliff
    /// faces and the ground colliders: things no player ever passes, and the things the play log
    /// caught auto-walk sliding through (the tavern's <c>collider</c>, <c>landslide (1)</c>,
    /// <c>steep_small_diag (6)</c>). Props are chairs, tables, trees, fence rails — the scenery a
    /// scripted walk has always slid past, and the whole reason the body is held Kinematic in the
    /// first place. Refusing to walk past a chair would take the door-to-bed walk away again, which
    /// is exactly the regression this pass exists to undo, so only walls stop a walk. Props are
    /// still reported to the log.
    /// </summary>
    private static string SolidAt(Vector2 p, out bool isWall)
    {
        isWall = false;
        try
        {
            var player = MainGame.me?.player;
            int playerLayer = PlayerCollisionLayer(player);

            int n = Physics2D.OverlapPointNonAlloc(p, _overlapBuffer);
            string prop = null;
            for (int i = 0; i < n; i++)
            {
                var col = _overlapBuffer[i];
                if (col == null || col.isTrigger) continue;
                if (playerLayer >= 0 &&
                    Physics2D.GetIgnoreLayerCollision(playerLayer, col.gameObject.layer)) continue;

                // Creatures are not obstacles. A villager, a zombie worker or a mob standing on
                // the route is a SNAPSHOT of where somebody happened to be at planning time; a
                // second later they have walked on, and during a scripted walk our own body is
                // Kinematic and slides past them regardless. Counting them cost a whole journey
                // on 2026-09-19: an 85-waypoint road route that reached the fishing spot within
                // 29 units was thrown away because 'worker_zombie_1(Clone)' was standing on it,
                // and the 628-point fine-graph detour that replaced it drove into the cliff at
                // 'steep_2 (15)' three times and lost the walk.
                //
                // Layer 9 is the game's own answer to "is this a creature": ComponentsManager
                // .CheckCharacterStuff forces every ObjectDefinition.IsCharacter() object onto it
                // and forces everything else off it, logging a warning if it finds a stray. So the
                // layer is authoritative here in a way an obj_id or a component lookup is not.
                if (col.gameObject.layer == CharactersLayer) continue;

                var wgo = col.GetComponentInParent<WorldGameObject>();
                if (wgo != null)
                {
                    if (wgo == player || wgo.is_player) continue;
                    if (_shortWalkTarget.Object != null && wgo == _shortWalkTarget.Object) continue;
                    if (_longWalkTarget.Object != null && wgo == _longWalkTarget.Object) continue;
                    // Remember it, but keep looking: a wall at the same point outranks a prop.
                    prop ??= $"{col.gameObject.name} (object {wgo.obj_id})";
                    continue;
                }

                isWall = true;
                return $"{col.gameObject.name} (level geometry)";
            }
            return prop;
        }
        catch (Exception ex)
        {
            // A probe that cannot run must not be the reason a blind player loses auto-walk.
            _log?.LogWarning($"[NAVIGATOR] Solidity probe failed: {ex.Message}");
        }
        return null;
    }

    // ---- Wall guard --------------------------------------------------------

    private static Vector2 _wallWatchLastPos;
    private static bool _hasWallWatchPos;
    private static Vector2 _lastClearPlayerPos;
    private static bool _hasLastClearPos;
    private static int _insideWallTicks;
    private static float _lastWallReportAt = float.NegativeInfinity;

    /// <summary>
    /// How long the player has to be INSIDE a wall before the walk is called off. About a quarter of
    /// a second, which at walking speed is the better part of a tile — far longer than clipping the
    /// corner of a cliff, and well short of crossing a building.
    /// </summary>
    private const int InsideWallTicksToStop = 12;

    /// <summary>
    /// Catch an auto-walk that is dragging the player through a wall, and undo it.
    ///
    /// WHAT DID NOT WORK, and why this is shaped the way it is. The first version looked a third of
    /// a tile ahead along the direction of travel and stopped when it found something solid. That
    /// reads plausibly and is useless in practice: the player walks parallel to cliffs and building
    /// fronts for most of a cross-map journey, and the direction of travel wobbles enough to point
    /// into them. Adding a navmesh second opinion and a three-frame persistence rule did not save
    /// it — in one session it stopped the walk to the tavern ten tiles short and killed the walk to
    /// the village four times over, at <c>steep_vert_L</c>, <c>steep_2</c> and <c>steep_end_L</c>,
    /// which are the ordinary cliff edges the road runs beside. Six wrong stops, against one real
    /// wall crossing in the same session. A blind player stranded mid-journey by "no clear path" is
    /// worse off than one who briefly clipped a cliff corner.
    ///
    /// So the guard no longer predicts. It waits until the player is demonstrably INSIDE level
    /// geometry — not near it, not pointing at it — and has been for <see cref="InsideWallTicksToStop"/>
    /// frames, which no graze survives. Then it puts them back on the last spot where they were in
    /// the clear and hands them to turn-by-turn guidance.
    ///
    /// Putting them back is the part that makes stopping safe at all. Simply ending the walk inside
    /// a wall hands control back with the body turning Dynamic inside a collider, and the player is
    /// wedged in a pocket nothing can path out of. The last clear position is somewhere they stood
    /// under their own weight a fraction of a second ago, so it is walkable by construction.
    ///
    /// Props — chairs, trees, fence rails, anything with a WorldGameObject behind it — never count.
    /// Sliding past those is what auto-walk has always done and what lets it thread a gate.
    /// </summary>
    private static void WatchForWallCrossing()
    {
        // Only while WE are driving. Cutscenes move the player through anything by design.
        if (!_weDisabledControl || _gameOwnsPlayer)
        {
            _hasWallWatchPos = false;
            _hasLastClearPos = false;
            _insideWallTicks = 0;
            _crossingName = null;
            return;
        }

        var player = MainGame.me?.player;
        if (player == null) { _hasWallWatchPos = false; return; }

        var pos = PlayerBodyPos(player);
        if (!_hasWallWatchPos)
        {
            _wallWatchLastPos = pos;
            _hasWallWatchPos = true;
            return;
        }

        var prev = _wallWatchLastPos;
        _wallWatchLastPos = pos;

        float moved = (pos - prev).sqrMagnitude;
        if (moved > TeleportJumpDistance * TeleportJumpDistance)
        {
            // A teleport, not a walk. Nothing before it is a safe place to be put back to.
            _hasLastClearPos = false;
            _insideWallTicks = 0;
            return;
        }

        try
        {
            // Anything solid counts here, prop or not.
            //
            // This used to ignore every collider that belonged to a WorldGameObject, on the theory
            // that those are chairs and trees while walls are raw level geometry. That theory is
            // wrong, and a player's sighted partner watching over their shoulder is how it came out:
            // walking from the tavern to the smith went straight through his HOUSE, because the
            // house is a world object — the log recorded it as "passing through prop
            // 'mf_canopy_1_back_wall (object roof_1)'" and waved it through. Buildings, canopies and
            // roofs are world objects in this game just as chairs are.
            //
            // So the distinction is not what a collider is attached to, it is how big it is. Two
            // measurements below make that judgement together: IsDeepInsideWall asks whether open
            // ground is within reach (an edge grazed, versus properly inside something), and the
            // crossing distance asks how far the run of solid ground has already gone on for (a
            // prop, versus a building). Neither is enough alone — a decorative coal heap looks like
            // a wall to the first, and a cliff edge accumulates no distance for the second.
            var inside = SolidAt(pos, out bool insideWall);

            NoteCrossing(inside, pos);

            if (inside == null)
            {
                // In the clear. Remember it as somewhere it is safe to be put back to.
                _lastClearPlayerPos = player.pos;
                _hasLastClearPos = true;
                _insideWallTicks = 0;
                return;
            }

            if (++_insideWallTicks < InsideWallTicksToStop) return;

            // GRAZING AN EDGE, OR ACTUALLY INSIDE? That is the only question worth asking here, and
            // the two things this used to ask instead both answered it wrongly.
            //
            // It used to let the walk carry on whenever the world navmesh called the spot walkable.
            // Graph 0's nodes are 76 units and it is simply wrong in places: a whole session's clips
            // went through on that excuse, including walking through `Sea Collider` and through
            // `dungeon_wall01_back`, both of which it happily calls walkable.
            //
            // It then also let the walk carry on whenever the player was still ON the graph-0 route.
            // That reasoning assumed what it needed to prove — the route is exactly what drags the
            // player through the wall, so "the route goes here" can never be the reason it is safe.
            // With that hatch in place the guard stopped nothing at all: twelve clips in one session,
            // through the tavern wall, through dungeon walls, through cliffs and through the sea.
            //
            // What the hatch was really protecting is genuine, though. The road to the village runs
            // along a cliff, the player's collider centre dips a few units into the cliff edge as
            // they walk it, and stopping for that killed three walks in a row. The difference is
            // DEPTH: brushing an edge leaves open ground half a tile away, being inside a building,
            // a cliff or the sea does not. So measure that instead — see IsDeepInsideWall.
            if (!IsDeepInsideWall(pos))
            {
                if (Time.unscaledTime - _lastWallReportAt > 5f)
                {
                    _lastWallReportAt = Time.unscaledTime;
                    _log?.LogInfo(
                        $"[NAVIGATOR] Brushing the edge of '{inside}' at {pos} during {WalkDescription()} " +
                        $"({OffRouteText(pos)}) — carrying on");
                }
                _insideWallTicks = 0;
                return;
            }

            // DEEP INSIDE A PROP, OR ACTUALLY INSIDE A BUILDING? Depth alone cannot tell, and the
            // smithy's decorative coal heap is what proved it: `mf_coal_1_decor` is about a tile and
            // a third across, so a line through its middle leaves no open ground within reach in any
            // of the eight directions and reads exactly like a wall. The guard stopped a walk to
            // Krezvold in the middle of the smithy yard, where the player could simply have carried
            // on east; the recovery then found no route and the journey was lost. (The same heap was
            // crossed cleanly on a later attempt whose line clipped its edge — that is how narrow
            // the margin is on a prop this size.)
            //
            // So add the one measurement a prop cannot fake: HOW FAR the player has already been
            // carried through solid ground in this one continuous run. A heap, a wood panel, a cliff
            // edge brushed on the road are all a tile or so end to end (measured: 1.27, 0.79, 0.39
            // tiles). A building, a cliff face or the sea is not — anything the guard exists to catch
            // keeps accumulating well past two tiles, and the stop then fires the moment it does.
            //
            // Under-reacting is the right way to be wrong here: a clipped corner costs nothing,
            // while a false stop strands a blind player mid-journey — which is exactly what happened.
            // A player pinned motionless inside something is not this method's problem either; the
            // long-walk stuck watchdog already covers that, and it needs no distance to fire.
            if (_crossingDistance < MinCrossingToStop)
            {
                if (Time.unscaledTime - _lastWallReportAt > 5f)
                {
                    _lastWallReportAt = Time.unscaledTime;
                    _log?.LogInfo(
                        $"[NAVIGATOR] Deep inside '{inside}' at {pos} during {WalkDescription()}, but only " +
                        $"{_crossingDistance / TileSize:F2} tiles into it — prop-sized, carrying on");
                }
                return;
            }

            _log?.LogWarning(
                $"[NAVIGATOR] Inside wall '{inside}' at {pos} for {_insideWallTicks} frames during " +
                $"{WalkDescription()} ({OffRouteText(pos)}), " +
                $"{_crossingDistance / TileSize:F2} tiles inside it and not near its edge; " +
                "stopping and stepping back out");
            StopInsideWall();
        }
        catch { /* the guard must never be the thing that breaks a walk */ }
    }

    /// How far off the route still counts as "on it". A graph-0 waypoint sits on a 76-unit node
    /// centre and the probe uses the body collider centre, so a tile and a half of slack is ordinary
    /// walking, not wandering.
    private const float OnRouteSlack = 1.5f * TileSize;

    /// <summary>
    /// How far from the player's body centre to look for open ground.
    ///
    /// A QUARTER of a tile, not a half. Half a tile was too generous and the smith's wall proved it:
    /// walking to Krezvold the player spent twelve-plus frames — over half a tile of walking —
    /// inside <c>mf_wood_panel_2_complete</c>, a built wooden panel, and the ring still found open
    /// ground and waved it through. A panel is a thin wall, and thin walls are most of what a
    /// building is made of.
    ///
    /// 24 units sits just outside the player's own collider (a circle of radius 14), so requiring
    /// all eight points to be solid still means "boxed in", but now boxed in by something roughly
    /// half a tile thick rather than a full tile.
    /// </summary>
    private const float WallDepthProbe = 0.25f * TileSize;

    /// <summary>
    /// How far a driven walk may carry the player through solid ground in one continuous run before
    /// the guard treats it as a wall rather than a prop.
    ///
    /// Two tiles, from measurements in the log: the smithy's decorative coal heap is 1.27 tiles end
    /// to end, a built wood panel 0.79, a cliff edge grazed on the road 0.39. Everything the guard
    /// exists to catch — a house, a cliff face, the sea — runs far longer than that, so two tiles
    /// separates them with room to spare while no prop in the game reaches it.
    /// </summary>
    private const float MinCrossingToStop = 2f * TileSize;

    /// <summary>
    /// Is the player properly INSIDE something solid, rather than clipping its edge?
    ///
    /// This is what separates the two things that look identical frame by frame: walking the road
    /// where it runs along a cliff (the body centre dips into the cliff edge for a step or two, with
    /// the road right there beside it) from being carried through a building wall, a cliff face or
    /// the sea (solid in every direction).
    ///
    /// Eight points at half a tile. If any one of them is open ground, the way out is a step away
    /// and this is an edge — leave the walk alone. Only when the player is boxed in on all sides has
    /// the walk genuinely put them somewhere they cannot be. Costs eight point checks, and only on a
    /// detection that has already survived <see cref="InsideWallTicksToStop"/> frames.
    ///
    /// It replaces judging a chair from a house by what the collider is attached to — a chair, a
    /// fence rail and a tree base all have open ground a step away, and a building does not, whether
    /// or not the game models that building as a world object. On its own, though, it is only half
    /// the test: a prop merely BIG enough (the smithy's coal heap, a tile and a third across) boxes
    /// the probe in just as a wall does, so the caller pairs it with how far the run of solid ground
    /// has gone on for. See MinCrossingToStop.
    ///
    /// A consequence worth stating: a wall thinner than half a tile does not trigger this, because
    /// the far side reads as open ground. That is the right way to be wrong — under-reacting costs a
    /// clipped corner, over-reacting strands a blind player mid-journey.
    /// </summary>
    // ---- Crossing record (pure diagnostics) --------------------------------

    private static string _crossingName;
    private static Vector2 _crossingEntry;
    private static Vector2 _crossingLastPos;
    private static int _crossingFrames;
    private static float _crossingDistance;

    /// <summary>
    /// Log every piece of solid geometry a driven walk passes into and out of, with how long the
    /// player was inside it and how far they travelled while there.
    ///
    /// Purely a record — it changes nothing. It exists because the guard above only ever reports
    /// after <see cref="InsideWallTicksToStop"/> frames, so everything crossed faster than that
    /// happened invisibly, and three rounds of tuning were argued from a handful of surviving lines.
    /// Entry and exit positions give the thickness of what was crossed, which is the number every
    /// threshold here is really about.
    /// </summary>
    private static void NoteCrossing(string solid, Vector2 pos)
    {
        try
        {
            if (solid != null)
            {
                if (_crossingName == null)
                {
                    _crossingName = solid;
                    _crossingEntry = pos;
                    _crossingFrames = 0;
                    _crossingDistance = 0f;
                }
                else
                {
                    _crossingDistance += (pos - _crossingLastPos).magnitude;
                }
                _crossingFrames++;
                _crossingLastPos = pos;
                return;
            }

            if (_crossingName == null) return;

            // Out the other side (or back the way we came). Report what it was.
            float across = (pos - _crossingEntry).magnitude;
            _log?.LogInfo(
                $"[NAVIGATOR] Crossed '{_crossingName}' during {WalkDescription()}: {_crossingFrames} frames, " +
                $"{_crossingDistance / TileSize:F2} tiles walked inside, {across / TileSize:F2} tiles " +
                $"entry->exit, entry {_crossingEntry} exit {pos}");
            _crossingName = null;
        }
        catch { }
    }

    private static bool IsDeepInsideWall(Vector2 pos)
    {
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI * 0.25f;
            var p = pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * WallDepthProbe;
            if (SolidAt(p, out _) == null) return false;   // open ground within reach — an edge
        }
        return true;
    }

    /// <summary>
    /// Is the player still on the route being driven? Distance to the nearest SEGMENT, not to the
    /// nearest waypoint — waypoints on a long road route are most of a tile apart, so measuring to
    /// the points alone reports a player walking perfectly down the middle of a leg as being off it.
    /// </summary>
    /// <summary>
    /// How far off the followed route this point is, for a log line. A SHORT walk has no injected
    /// route to be off, so <see cref="NearRouteBeingFollowed"/> leaves the distance at
    /// float.MaxValue — which printed as "3544608000000000000000000000000000000.0 tiles off route".
    /// Both wall-guard messages go through here so the two cannot drift apart again.
    /// </summary>
    private static string OffRouteText(Vector2 pos)
    {
        NearRouteBeingFollowed(pos, out float offRoute);
        return (offRoute < float.MaxValue)
            ? $"{offRoute / TileSize:F1} tiles off route"
            : "no route to be off";
    }

    private static bool NearRouteBeingFollowed(Vector2 pos, out float distance)
    {
        distance = float.MaxValue;
        var route = _currentRoute;
        if (route == null || route.Count < 2) return false;

        float slackSq = OnRouteSlack * OnRouteSlack;
        float bestSq = float.MaxValue;

        for (int i = 1; i < route.Count; i++)
        {
            var a = new Vector2(route[i - 1].x, route[i - 1].y);
            var b = new Vector2(route[i].x, route[i].y);
            var ab = b - a;
            float lenSq = ab.sqrMagnitude;
            var closest = lenSq <= 0.0001f
                ? a
                : a + ab * Mathf.Clamp01(Vector2.Dot(pos - a, ab) / lenSq);

            float sq = (pos - closest).sqrMagnitude;
            if (sq >= bestSq) continue;
            bestSq = sq;
            if (bestSq <= slackSq) break;   // close enough; no need to measure the rest
        }

        distance = Mathf.Sqrt(bestSq);
        return bestSq <= slackSq;
    }

    private static string WalkDescription()
    {
        string what = _longWalkActive ? "long walk" : (_isWalking ? "short walk" : "scripted move");
        string target = _longWalkActive ? _longWalkTarget.Label : _shortWalkTarget.Label;
        return $"{what} to '{target}'";
    }

    /// <summary>
    /// Recover a walk that has carried the player into a wall: put them back where they last stood
    /// in the clear, then find a different way there.
    ///
    /// It used to hand straight over to turn-by-turn guidance, and that was the wrong end of the
    /// problem — the player would be dropped mid-graveyard and have to steer themselves back onto
    /// the route, which is precisely the work auto-walk exists to save them. Backing out and asking
    /// the FINE graph for another route does the same thing they were doing by hand: its 8-unit
    /// nodes see the gaps between graves that the 76-unit NPC mesh does not, so the way round is
    /// usually right there. Guidance is still the answer if that fails too, or if this keeps
    /// happening — <see cref="MaxWallRecoveries"/> stops it looping into the same wall forever.
    /// </summary>
    private static void StopInsideWall()
    {
        _insideWallTicks = 0;
        _hasWallWatchPos = false;

        // Step back out BEFORE control is handed back: the body turns Dynamic the moment it is, and
        // inside a collider that means wedged.
        if (_hasLastClearPos)
        {
            try
            {
                var player = MainGame.me?.player;
                if (player != null)
                {
                    var here = player.transform.position;
                    player.transform.position = new Vector3(_lastClearPlayerPos.x, _lastClearPlayerPos.y, here.z);
                    player.RefreshPositionCache();
                    _log?.LogInfo($"[NAVIGATOR] Stepped back out of the wall to {_lastClearPlayerPos}");
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[NAVIGATOR] Could not step back out of the wall: {ex.Message}");
            }
        }
        // NOT cleared. It used to be, and that is how a journey ended with the player left standing
        // INSIDE the geometry: the resumed route drove straight back into the same collider without
        // ever passing over clear ground, so no new clear position was ever recorded, and the final
        // give-up had nowhere to put them back to. The beacon then handed guidance to a player boxed
        // in on all four sides, which is the worst place this code can leave anybody. Stepping back
        // to the same spot twice costs nothing; being stranded inside a wall costs the journey.

        // The pullback is a teleport, so the crossing measurement has to start again — otherwise the
        // jump backwards is added to "how far we have been carried through this thing" and the next
        // stop fires at the 12-frame minimum with a distance it never walked (2.03 tiles, then 4.61,
        // then 5.33, in one journey to the witch on 2026-09-05).
        _crossingName = null;
        _crossingDistance = 0f;
        _crossingFrames = 0;

        // Try to carry on from where they now stand, rather than giving the problem back.
        //
        // KEEP THE ROAD. This used to go straight to TryFineGraphRoute, and on a cross-map journey
        // that was worse than the wall it was recovering from. The walk home to the village had a
        // perfectly good 152-waypoint GRAPH-0 route — the NPC road network, the way a sighted player
        // goes — and had walked most of it when the guard fired at `steep_vert_L (4)`, one of the
        // cliff edges the road legitimately runs beside. The recovery threw that route away and
        // asked the fine player graph, which is scanned as a thin STRIP between the player and the
        // destination: the only route that exists inside that corridor is the straight one, across
        // the cliffs. It drove into `steep_L (18)`, recovered the same way, drove into it again, and
        // beaconed. The road was never in the graph it was searching.
        //
        // So: resume the route we were already on, and if that is not possible re-ask the SAME graph
        // that produced it. The fine graph is the right tool for a short walk among the graves — its
        // 8-unit nodes see gaps the 76-unit NPC mesh cannot — and the wrong tool for crossing a map.
        if (_longWalkActive && ++_wallRecoveries <= MaxWallRecoveries)
        {
            var pos = MainGame.me?.player?.pos ?? _lastClearPlayerPos;

            // FIRST hit: assume the road is right. One clipped cliff corner is not a reason to throw
            // away a 152-waypoint road route — that mistake is what the comment above is about.
            if (_wallRecoveries == 1)
            {
                if (TryResumeRouteAfterWall(pos)) return;

                if (_routeIsWorldMesh)
                {
                    _log?.LogInfo($"[NAVIGATOR] Walked into a wall (recovery {_wallRecoveries}); " +
                                  "re-asking the road network rather than dropping to the fine graph");
                    RequestGraph0Route(pos, _longWalkDest);
                    return;
                }
            }

            // SECOND hit on the same journey: stop believing the route. It has now driven the player
            // into geometry twice, and resuming it a second time cannot help — the tail is re-injected
            // as the new route, so the nearest waypoint to the pulled-back position is index 0 again
            // and the resume restarts two waypoints along, walking the identical failing stretch.
            // (Log, 2026-09-05: "resuming the same road route from waypoint 34/92", then "from
            // waypoint 2/58", then the beacon — a route heading west into the river at the swamp
            // crossing, three attempts at the same water's edge.)
            //
            // Ask the FINE graph instead, which is exactly what the player then did by hand: they
            // stepped one tile back east, asked again, and it returned a 712-waypoint route SOUTH
            // over the stone bridge — the real way to the witch, which the road network never
            // offered. Deliberately not the first move: the fine graph is scanned as a thin strip
            // between player and destination, so preferring it on hit one is what once replaced a
            // road with a straight corridor across the cliffs. Second chances go to the other graph.
            //
            // "The OTHER graph" has to mean the other one BOTH WAYS, and for a long time it did
            // not. Everything above is written for a route that came from the road, and the only
            // rung that changes graph is guarded by _routeIsWorldMesh — so when the route was
            // already a fine-graph one (the road was rejected at planning time, or graph 0 gave
            // up), recovery 1 and recovery 2 were the SAME call: TryFineGraphRoute, same graph,
            // from the same pulled-back position, because the step-back always returns the player
            // to the same clear spot. It answered with the identical route both times and the
            // player drove into the identical wall both times.
            //
            // Log, 2026-09-19, walking to the fishing spot: a 628-point fine-graph route hit
            // 'steep_2 (15)', stepped back to (3047.6, 1911.5), re-asked the fine graph -> 455
            // waypoints, hit the same cliff, stepped back to (3047.6, 1911.5) again, re-asked the
            // fine graph -> 455 waypoints again, hit it a third time, beacon. The beacon then
            // asked GRAPH 0 from that very position and got a 50-waypoint route around the slope
            // in one go. The way round was there the whole time, on the graph nothing asked.
            if (_wallRecoveries >= 2 && !_routeIsWorldMesh)
            {
                _log?.LogInfo($"[NAVIGATOR] Walked into a wall (recovery {_wallRecoveries}); the fine " +
                              "graph has now failed twice from the same spot — asking the road network");
                RequestGraph0Route(pos, _longWalkDest);
                return;
            }

            if (TryFineGraphRoute($"walked into a wall (recovery {_wallRecoveries})",
                                  () => BeaconBail("no way round the wall")))
                return;

            if (_routeIsWorldMesh)
            {
                _log?.LogInfo($"[NAVIGATOR] Walked into a wall (recovery {_wallRecoveries}); " +
                              "the fine graph had nothing either — re-asking the road network");
                RequestGraph0Route(pos, _longWalkDest);
                return;
            }
        }

        if (_longWalkActive)
        {
            BeaconBail("walked into a wall");
            return;
        }

        // A short walk has no route of its own to replace, so escalate it into a long one: that
        // path tries graph 0 and then the fine graph, which is the same second chance.
        var target = _shortWalkTarget;
        _isWalking = false;
        _fallbackPending = false;
        _escalatePending = false;
        if (++_wallRecoveries <= MaxWallRecoveries && (target.Object != null || target.DropGo != null))
        {
            _log?.LogInfo($"[NAVIGATOR] Short walk hit a wall; re-routing to {target.Label}");
            StartLongWalk(target);
            return;
        }

        ReleaseScriptControl();
        ScreenReader.Say(Loc.Fmt("nav.manual_guidance", target.Label), interrupt: true);
        GuidedWalk.StartTo(target, announceStart: false, allowBeaconFallback: true);
    }

    /// How far past the nearest waypoint to rejoin a resumed route, so the leg that hit the wall is
    /// not simply walked again. One waypoint on a graph-0 route is about 77 units.
    private const int ResumeWaypointSkip = 2;

    /// <summary>
    /// Pick the route back up beyond the spot that stopped it, instead of asking for a new one.
    ///
    /// A wall stop does not mean the route was wrong. The guard needs the player to be inside level
    /// geometry for 12 frames AND off the world mesh, which a cross-map route can satisfy by clipping
    /// the corner of a cliff it is legitimately running beside — `steep_vert_L`, `steep_2`,
    /// `steep_end_L` are the ordinary cliff edges the road passes, and they are exactly the names in
    /// the log. Throwing away a 152-waypoint road route because of one clipped corner, and replacing
    /// it with a straight corridor across the same cliffs, is how a good journey became a stuck one.
    ///
    /// So step forward past the offending leg and re-inject the tail. Bounded by
    /// <see cref="MaxWallRecoveries"/> exactly as before, so a route that really does run into a wall
    /// still gives up rather than grinding at it.
    /// </summary>
    private static bool TryResumeRouteAfterWall(Vector2 fromPos)
    {
        // Only a road route is worth preserving; a fine-graph route is a straight corridor and
        // re-asking for one is no worse than resuming it.
        if (!_routeIsWorldMesh || _currentRoute == null || _currentRoute.Count < 2) return false;

        int nearest = 0;
        float bestSq = float.MaxValue;
        for (int i = 0; i < _currentRoute.Count; i++)
        {
            var w = new Vector2(_currentRoute[i].x, _currentRoute[i].y);
            float sq = (w - fromPos).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; nearest = i; }
        }

        // Past the leg that hit, then past any waypoint that is itself sitting in geometry — those
        // are the ones that would stop the walk again a second later.
        int start = nearest + ResumeWaypointSkip;
        while (start < _currentRoute.Count)
        {
            var w = new Vector2(_currentRoute[start].x, _currentRoute[start].y);
            SolidAt(w, out bool isWall);
            if (!isWall) break;
            start++;
        }

        // Nothing meaningful left: let the normal re-route handle the last stretch.
        if (_currentRoute.Count - start < 2) return false;

        var tail = new List<Vector3>(_currentRoute.Count - start);
        for (int i = start; i < _currentRoute.Count; i++) tail.Add(_currentRoute[i]);

        _log?.LogInfo($"[NAVIGATOR] Walked into a wall (recovery {_wallRecoveries}); resuming the same " +
                      $"road route from waypoint {start}/{_currentRoute.Count}");

        // checkAgainstWorldMesh: false — this route already passed that test when it was first
        // injected, and re-testing it now would judge it by the leg from the stepped-back position,
        // which is exactly the clipped corner that stopped it.
        StartNativePathWalk(tail, checkAgainstWorldMesh: false);
        _routeIsWorldMesh = true;   // it is still the road route; StartNativePathWalk just cleared that
        return true;
    }

    /// <summary>
    /// How many times one walk may back out of a wall and try a different route before the mod
    /// accepts it cannot drive this one and hands over. Without a cap a route that leads into the
    /// same wall would be re-driven into it forever.
    /// </summary>
    private const int MaxWallRecoveries = 2;
    private static int _wallRecoveries;

    private static int _playerCollisionLayer = -1;
    private static Collider2D _playerCollider;

    /// <summary>The layer the player's own solid collider sits on, cached; -1 if undeterminable.</summary>
    private static int PlayerCollisionLayer(WorldGameObject player)
    {
        if (_playerCollisionLayer >= 0) return _playerCollisionLayer;
        ResolvePlayerCollider(player);
        return _playerCollisionLayer;
    }

    private static void ResolvePlayerCollider(WorldGameObject player)
    {
        if (_playerCollider != null) return;
        try
        {
            if (player == null) return;
            foreach (var c in player.GetComponentsInChildren<Collider2D>(true))
            {
                if (c == null || c.isTrigger) continue;
                _playerCollider = c;
                _playerCollisionLayer = c.gameObject.layer;
                // Logged once: every solidity probe is taken at this collider's centre, so if the
                // wrong one is picked (a big sprite-sized box rather than the small body at the
                // feet) the mod would report walls where the player is plainly in the open. That
                // mistake is invisible from inside the game and obvious in one line here.
                try
                {
                    var offset = (Vector2)c.bounds.center - (player.pos);
                    _log?.LogInfo($"[NAVIGATOR] Player body collider '{c.gameObject.name}' " +
                                  $"({c.GetType().Name}) size {c.bounds.size} offset from pos {offset} " +
                                  $"on layer {_playerCollisionLayer} ({LayerMask.LayerToName(_playerCollisionLayer)})");
                }
                catch { }
                return;
            }
        }
        catch { }
    }

    /// <summary>
    /// Where the player's BODY is, which is not where <c>player.pos</c> is.
    ///
    /// A WorldGameObject's position is its sprite anchor; its physical collider sits somewhere else
    /// — the game itself works around this ("bed interaction basis: pos=(2376, -6216)
    /// colliderCenter=(2376, -6225.6)"). Probing solidity at the anchor asks about a point that is
    /// roughly at the player's head, which is how walking up to the front door of a house came out
    /// as "INSIDE A PROP house_1" while the player's feet were plainly outside on the path.
    /// </summary>
    private static Vector2 PlayerBodyPos(WorldGameObject player)
    {
        ResolvePlayerCollider(player);
        try
        {
            if (_playerCollider != null) return _playerCollider.bounds.center;
        }
        catch { }
        return player != null ? player.pos : Vector2.zero;
    }

    /// <summary>
    /// What one graph says about a point: walkable, blocked, or "not my area" — the last of which
    /// is what a graph returns outside its scanned bounds, and must not be read as a wall.
    /// </summary>
    private static Ground ProbeGround(Vector2 p, int graph)
    {
        try
        {
            var astar = AstarPath.active;
            if (astar?.graphs == null || astar.graphs.Length <= graph) return Ground.Unknown;

            if (_walkProbe[graph] == null)
            {
                _walkProbe[graph] = new Pathfinding.NNConstraint
                {
                    graphMask = 1 << graph,
                    constrainWalkability = false,   // report the node that's really there
                    constrainTags = false,
                    constrainArea = false,
                };
                // Node size decides how far "the node covering this point" can legitimately be.
                if (astar.graphs[graph] is Pathfinding.GridGraph gg)
                {
                    _probeNodeSize[graph] = gg.nodeSize;
                    _log?.LogInfo($"[NAVIGATOR] Graph {graph} grid: nodeSize={gg.nodeSize} {gg.width}x{gg.depth}");
                }
                if (_probeNodeSize[graph] <= 0f) _probeNodeSize[graph] = TileSize;
            }

            var nn = astar.GetNearest(new Vector3(p.x, p.y, 0f), _walkProbe[graph]);
            if (nn.node == null) return Ground.Unknown;

            // Outside this graph's scanned area the nearest node is clamped to its edge, which can
            // be any distance away — that is "no data here", not "blocked".
            var np = new Vector2(nn.clampedPosition.x, nn.clampedPosition.y);
            if (Vector2.Distance(p, np) > _probeNodeSize[graph]) return Ground.Unknown;

            return nn.node.Walkable ? Ground.Walkable : Ground.Blocked;
        }
        catch { return Ground.Unknown; }
    }

    /// <summary>
    /// Does the whole-map NPC navmesh say this spot is solid? Coarse (76-unit nodes), so this is
    /// a hint rather than a verdict — but a hint worth having when the player graph is silent.
    /// </summary>
    internal static bool IsKnownBlockedOnWorldMesh(Vector2 p) => ProbeGround(p, 0) == Ground.Blocked;

    /// <summary>
    /// Rescan the PLAYER graph around a point, so walkability questions near the player get
    /// answered by the graph built from the player's own collision instead of the coarse world
    /// mesh. Costs a synchronous scan — the same one the game runs before every auto-walk — so it
    /// is bounded to a box around the player and rate-limited.
    /// </summary>
    internal static void RefreshPlayerGraphAround(Vector2 center, float tiles = 8f, bool force = false)
    {
        if (!force && Time.realtimeSinceStartup - _lastPlayerGraphRefresh < PlayerGraphRefreshInterval)
            return;
        try
        {
            _lastPlayerGraphRefresh = Time.realtimeSinceStartup;
            var box = new Vector2(tiles * TileSize, tiles * TileSize);
            var started = Time.realtimeSinceStartup;
            AStarTools.RefreshPlayerGraph(center - box, center + box);
            // Logged with its cost: this is a synchronous graph scan on the main thread, and if it
            // ever shows up as a stutter this line is where to look first.
            _log?.LogInfo($"[NAVIGATOR] Player graph rescanned around {center} (~{tiles:F0} tiles, " +
                          $"{(Time.realtimeSinceStartup - started) * 1000f:F0}ms)");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Player graph rescan failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Does the PLAYER graph specifically say this spot is solid? That graph models the player's
    /// own collision at 8-unit resolution, so when it has an opinion it is the last word — worth
    /// asking separately, because a caller may otherwise let a nearby route vouch for a point that
    /// is really the wall of a house.
    /// </summary>
    internal static bool IsKnownBlockedForPlayer(Vector2 p) =>
        ProbeGround(p, AStarTools.PLAYER_GRAPH_N) == Ground.Blocked;

    /// <summary>
    /// Which way the player is TRYING to go, taken from the movement keys they are holding RIGHT
    /// NOW (<see cref="LazyInput.GetDirection"/>). This is the only honest answer to "are they
    /// pushing against something": position says nothing (they are not moving), and the character's
    /// state can read as idle while they lean on a fence. Zero when they are not pressing anything.
    ///
    /// Falls back to the character's facing, which the game sets from the same input, if the input
    /// layer is unavailable for any reason.
    /// </summary>
    internal static Vector2 PlayerHeading()
    {
        try
        {
            var keys = LazyInput.GetDirection();
            if (keys.sqrMagnitude > 0.0001f) return keys.normalized;
        }
        catch { }

        try
        {
            var ch = MainGame.me?.player?.components?.character;
            if (ch == null) return Vector2.zero;
            var d = ch.direction;
            return d.sqrMagnitude < 0.0001f ? Vector2.zero : d.normalized;
        }
        catch { return Vector2.zero; }
    }

    /// <summary>Are they holding a movement key at all?</summary>
    internal static bool PlayerIsPressingMove()
    {
        try { return LazyInput.GetDirection().sqrMagnitude > 0.0001f; }
        catch { return false; }
    }

    /// Which graph answered the last guided route: 0 = coarse world mesh, 2 = fine player graph.
    internal static int LastGuidedRouteGraph { get; private set; }

    /// <summary>
    /// Is the game currently walking the player (their own keys, or ours)? Used by the guided walk
    /// to tell "pushing into a wall" — walking state, no movement — from simply standing still.
    /// </summary>
    internal static bool PlayerIsWalking()
    {
        try { return MainGame.me?.player?.components?.character?.IsInMovingState() ?? false; }
        catch { return false; }
    }

    /// <summary>
    /// Ask for a walking route and hand the waypoints to <paramref name="onDone"/> (null on
    /// failure). Graph 0 (the whole-map NPC navmesh) answers most of it; when it has nothing —
    /// doors, building interiors, short hops onto an interaction tile — the PLAYER graph is asked
    /// instead, which is exactly what auto-walk falls back on and why auto-walk reaches places
    /// turn-by-turn used to declare unreachable. Standalone: touches none of the long-walk state,
    /// so a guided walk and an auto-walk can never confuse each other.
    /// Returns false if no query could be started at all.
    /// </summary>
    internal static bool RequestGuidedRoute(Vector2 from, Vector2 to, bool preferPlayerGraph,
                                            Action<List<Vector2>> onDone)
    {
        // Which graph to ask FIRST. The player graph (graph 2) has 8-unit nodes and is built from
        // the player's own collision, so it knows every fence, gate and farm plot; the world mesh
        // (graph 0) has 76-unit nodes and covers the map. Auto-walk picks between them by distance
        // and is reliable, so turn-by-turn now does the same: near targets, and anything after
        // walking into something, get the fine graph.
        float dist = Vector2.Distance(from, to);
        bool fineFirst = preferPlayerGraph || dist <= LongWalkStartDistance;

        if (fineFirst)
        {
            // SnapToWalkable rescans the player graph over player->target and pulls the destination
            // onto a real node — exactly what auto-walk does before its own A*. PadPlayerGraph is
            // the other half of that, and its absence here was the bug: the game scans only a thin
            // rectangle between the two points plus about two tiles, so a way round that leaves
            // that strip — through the gate of a fenced yard, say — simply is not in the graph, and
            // the query answers "no path" while the player can plainly walk it. Auto-walk pads;
            // this now pads too.
            Vector2 fineDest;
            PadPlayerGraph = true;
            try { fineDest = SnapToWalkable(to); }
            finally { PadPlayerGraph = false; }
            _lastPlayerGraphRefresh = Time.realtimeSinceStartup;

            return StartRouteQuery(from, fineDest, AStarTools.PLAYER_GRAPH_N, route =>
            {
                if (route != null) { onDone(route); return; }

                // Still nothing. Before handing the job to a graph that cannot see fences, scan a
                // proper area around the player and ask the fine one once more.
                RefreshPlayerGraphAround(from, WideGuidedRescanTiles, force: true);
                if (StartRouteQuery(from, fineDest, AStarTools.PLAYER_GRAPH_N, wider =>
                {
                    if (wider != null) { onDone(wider); return; }
                    var coarse = TrySnapGraph0(to, out var s2, out _) ? s2 : to;
                    if (!StartRouteQuery(from, coarse, 0, onDone)) onDone(null);
                })) return;

                var fallback = TrySnapGraph0(to, out var s, out _) ? s : to;
                if (!StartRouteQuery(from, fallback, 0, onDone)) onDone(null);
            });
        }

        // Far away: the world mesh is the only one that spans the distance. If it has nothing,
        // fall back to the fine graph around the player.
        var snapped = TrySnapGraph0(to, out var s0, out _) ? s0 : to;
        return StartRouteQuery(from, snapped, 0, route =>
        {
            if (route != null) { onDone(route); return; }
            if (!RefreshPlayerGraphFor(from, to) ||
                !StartRouteQuery(from, to, AStarTools.PLAYER_GRAPH_N, onDone))
                onDone(null);
        });
    }

    /// One async path query on one graph. Null to the callback on any failure.
    private static bool StartRouteQuery(Vector2 from, Vector2 to, int graph, Action<List<Vector2>> onDone)
    {
        try
        {
            if (AstarPath.active == null) return false;

            var path = Pathfinding.ABPath.Construct(
                new Vector3(from.x, from.y, 0f),
                new Vector3(to.x, to.y, 0f),
                p =>
                {
                    try
                    {
                        if (p == null || p.error || p.vectorPath == null || p.vectorPath.Count < 2)
                        {
                            _log?.LogInfo($"[NAVIGATOR] Guided route on graph {graph}: no path");
                            onDone(null);
                            return;
                        }
                        var pts = new List<Vector2>(p.vectorPath.Count);
                        foreach (var w in p.vectorPath) pts.Add(new Vector2(w.x, w.y));
                        _log?.LogInfo($"[NAVIGATOR] Guided route on graph {graph}: {pts.Count} waypoints");
                        LastGuidedRouteGraph = graph;
                        onDone(pts);
                    }
                    catch (Exception ex)
                    {
                        _log?.LogWarning($"[NAVIGATOR] Guided route callback failed: {ex.Message}");
                        onDone(null);
                    }
                });

            var constraint = Pathfinding.NNConstraint.Default;
            constraint.graphMask = 1 << graph;
            path.nnConstraint = constraint;

            AstarPath.StartPath(path);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Guided route request on graph {graph} failed: {ex.Message}");
            return false;
        }
    }

    // The player-graph rescan is synchronous (AstarPath.Scan on graph 2), so it is rate-limited:
    // the guided walk may ask for a route several times a minute, and the game already does this
    // scan on every auto-walk.
    private static float _lastPlayerGraphRefresh;
    private const float PlayerGraphRefreshInterval = 2f;
    // How much ground to scan when the fine graph's first answer is "no path": enough to contain
    // the way round a fenced yard rather than just the strip between here and there.
    private const float WideGuidedRescanTiles = 16f;

    private static bool RefreshPlayerGraphFor(Vector2 from, Vector2 to)
    {
        if (Time.realtimeSinceStartup - _lastPlayerGraphRefresh < PlayerGraphRefreshInterval)
            return true;   // recent enough; the existing scan almost certainly still covers this
        try
        {
            _lastPlayerGraphRefresh = Time.realtimeSinceStartup;
            // PadPlayerGraph widens the scanned rectangle (see RefreshPlayerGraph_Prefix) so the
            // search has room to go around a wall instead of only along the straight line.
            PadPlayerGraph = true;
            try { AStarTools.RefreshPlayerGraph(from, to); }
            finally { PadPlayerGraph = false; }
            _log?.LogInfo($"[NAVIGATOR] Player graph rescanned for guided route {from} -> {to}");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Player graph refresh failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Silently drop whatever the mod was driving so the player has their own legs back. Never
    /// announces: the guided walk speaks its own opening line right after.
    /// </summary>
    internal static void StopMovementForGuidedWalk()
    {
        _longWalkActive = false;
        _beaconActive = false;
        _routePending = false;
        _routeNeedsRecompute = false;
        _exitAssisting = false;
        _escapeLegActive = false;
        _fallbackPending = false;
        _escalatePending = false;
        _isWalking = false;
        _walkWatchdog = 0;
        ReleaseScriptControl();
    }

    internal static void StartBeaconFor(NavigationTarget target) => StartBeacon(target);

    /// <summary>
    /// How far a point is from an object's actual outline, rather than from the anchor the game
    /// sorts it by. For anything large — a farm plot, a building, a workbench — those are metres
    /// apart, which is why "am I there yet" measured against the anchor can still say no while the
    /// player is standing flat against the thing.
    /// </summary>
    internal static float DistanceToObjectEdge(WorldGameObject obj, Vector2 p)
    {
        try
        {
            if (obj == null || obj.is_removed) return float.MaxValue;
            var b = obj.GetTotalBounds();
            if (b.size.sqrMagnitude <= 0.0001f) return Vector2.Distance(p, obj.pos);
            return Mathf.Sqrt(b.SqrDistance(new Vector3(p.x, p.y, b.center.z)));
        }
        catch { return float.MaxValue; }
    }

    /// <summary>Is the player inside a building right now (the game's own environment flag)?</summary>
    internal static bool PlayerIsInsideBuilding()
    {
        try
        {
            var ch = MainGame.me?.player?.components?.character;
            return ch != null && ch.cur_environment == BaseCharacterComponent.Environment.Inside;
        }
        catch { return false; }
    }

    /// <summary>The nearest door, for guiding someone out of a building they are shut inside.</summary>
    internal static bool TryNearestDoorTarget(out NavigationTarget door)
    {
        var d = NearestDoor();
        door = d ?? default;
        return d != null;
    }

    /// <summary>
    /// The player walked themselves all the way in. Do what an auto-walk arrival does — face the
    /// object and bias the game's interaction pick onto it — so plain E works without nudging.
    /// </summary>
    internal static void NotifyGuidedArrival(NavigationTarget target, Vector2? facePos)
    {
        var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
        FaceGuidedArrival(target, facePos);
        ScreenReader.Say(Loc.Fmt("nav.arrived_at", target.Label,
                                 DistanceText(Vector2.Distance(playerPos, target.Position))),
                         interrupt: true);
    }

    /// <summary>
    /// The silent half of an arrival: face the object and bias the game's interaction onto it, so
    /// plain E works. Split out for arrivals that want to say something of their own (reaching a
    /// door on the way somewhere else).
    /// </summary>
    internal static void FaceGuidedArrival(NavigationTarget target, Vector2? facePos)
    {
        _walkFacePos = facePos ?? target.Position;
        FacePlayerAtTarget();
        SetArrivedTarget(target.Object);
    }

    // ---- Compass beacon (manual fallback guidance) -------------------------

    private static void StartBeacon(NavigationTarget target)
    {
        // The player walks manually in beacon mode, so make sure scripted control is released
        // (a failed auto-walk hop can leave the player frozen otherwise).
        ReleaseScriptControl();
        GuidedWalk.Stop(announce: false);   // one manual guidance mode at a time
        _isWalking = false;
        _beaconActive = true;
        _beaconTarget = target;
        var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
        _beaconLastAnnouncePos = playerPos;
        _log?.LogInfo($"[NAVIGATOR] Beacon started to {target.Label}");
        AnnounceBeacon(playerPos, prefix: Loc.Get("nav.guiding_to_prefix"));
    }

    internal static void StopBeacon(bool announce = true)
    {
        if (!_beaconActive) return;
        _beaconActive = false;
        if (announce)
            ScreenReader.Say(Loc.Get("nav.guidance_stopped"), interrupt: true);
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
            ScreenReader.Say(Loc.Fmt("nav.close_walking_rest", target.Label), interrupt: true);
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
        ScreenReader.Say(Loc.Fmt("nav.beacon", prefix, _beaconTarget.Label, dir, DistanceText(dist)), interrupt: true);
    }

    /// <summary>
    /// Eight-point compass direction from one world point toward another. The world plane is
    /// x-y with +x east and +y north, so the bearing is atan2(dy, dx).
    /// </summary>
    private static string CompassDirection(Vector2 from, Vector2 to)
    {
        var d = to - from;
        if (d.sqrMagnitude < 1f) return Loc.Get("compass.here");

        // 0 deg = east, increasing counter-clockwise. Convert to a 0..8 sector.
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        int sector = Mathf.RoundToInt(angle / 45f) % 8;
        return sector switch
        {
            0 => Loc.Get("compass.east"),
            1 => Loc.Get("compass.north_east"),
            2 => Loc.Get("compass.north"),
            3 => Loc.Get("compass.north_west"),
            4 => Loc.Get("compass.west"),
            5 => Loc.Get("compass.south_west"),
            6 => Loc.Get("compass.south"),
            7 => Loc.Get("compass.south_east"),
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
    /// <summary>
    /// Arm the post-arrival settle watch for a ground drop — see <see cref="_dropSettlePending"/>
    /// for why a drop is not where we left it a moment later.
    ///
    /// Only drops the game can highlight are worth watching. Small items and tech points are never
    /// highlighted (InteractionComponent.FindNearestDrop skips both) because walking near them
    /// collects them outright, which the walk has just done — watching those would chase a drop that
    /// is already in the inventory, or one that can never light up.
    /// </summary>
    private static void ArmDropSettleCheck(NavigationTarget target)
    {
        _dropSettlePending = false;
        if (!target.IsDrop || ReferenceEquals(target.DropGo, null) || target.DropGo == null) return;
        try
        {
            var drop = target.DropGo.GetComponent<DropResGameObject>();
            if (drop == null || drop.is_collected) return;
            var res = drop.res;
            if (res == null || res.is_tech_point || res.definition == null || res.definition.is_small) return;

            _dropSettleTarget = target;
            _dropSettleFramesLeft = DropSettleWindowFrames;
            _dropSettlePending = true;
        }
        catch { /* the settle watch must never be the thing that breaks an arrival */ }
    }

    /// <summary>
    /// One frame of the drop settle watch. Ends the moment the game highlights the drop (it is
    /// pickable, nothing to do and nothing to say), and otherwise re-approaches once the window
    /// runs out. Silent throughout: to the player this is the tail of the walk they already asked
    /// for, not a new one.
    /// </summary>
    private static void TickDropSettle()
    {
        try
        {
            // Anything that moves the player on purpose owns them now — a new walk, the beacon,
            // guidance, a cutscene. Drop the watch rather than fight it for control.
            if (_isWalking || _longWalkActive || _beaconActive || _gameOwnsPlayer)
            {
                _dropSettlePending = false;
                return;
            }

            var go = _dropSettleTarget.DropGo;
            var drop = (go == null) ? null : go.GetComponent<DropResGameObject>();
            if (drop == null || drop.is_collected)
            {
                _dropSettlePending = false;   // picked up or despawned — the walk did its job
                return;
            }

            // The game's own answer to "would E pick this up": set by FindNearestDrop from the
            // interaction box, so it already accounts for the player's facing.
            if (ReferenceEquals(DropResGameObject.currently_higlighted_obj, drop))
            {
                _dropSettlePending = false;
                _dropChases = 0;
                return;
            }

            if (--_dropSettleFramesLeft > 0) return;
            _dropSettlePending = false;

            if (_dropChases >= MaxDropChases)
            {
                _log?.LogInfo($"[NAVIGATOR] {_dropSettleTarget.Label} still not in reach after " +
                              $"{_dropChases} chase(s); leaving it to the player");
                return;
            }

            // Re-read where the drop actually ended up — target.Position is where it was when the
            // list was last built, which is the spot we just walked to and it has left.
            var target = _dropSettleTarget;
            var pos = (Vector2)go.transform.position;
            var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
            target.Position = pos;
            target.Distance = Vector2.Distance(pos, playerPos);
            _dropSettleTarget = target;
            _dropChases++;

            _log?.LogInfo($"[NAVIGATOR] {target.Label} was kicked clear on arrival " +
                          $"(now {target.Distance:F0}u at {pos}); re-approaching (chase {_dropChases})");
            WalkToTarget(target, silent: true);
        }
        catch (Exception ex)
        {
            _dropSettlePending = false;
            _log?.LogWarning($"[NAVIGATOR] Drop settle check failed: {ex.Message}");
        }
    }

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

    /// <summary>
    /// The navigated target IF the player is physically within interaction reach of it right now —
    /// close enough that vanilla would interact if only the player were facing it. Used by the
    /// E-interaction prefix to fire the interaction even when the player's forward interaction box
    /// (which points in one of 4 cardinal directions, offset ahead of the player) isn't overlapping
    /// the object. That box-miss is exactly the "I'm standing at it but E does nothing until I nudge
    /// with WASD" case. Distance is measured to the object's collider bounds, not its pos, so large
    /// objects (build desks, ovens) count from their near edge rather than their depth-sort anchor.
    /// </summary>
    internal static WorldGameObject InteractionTargetWithinReach()
    {
        var obj = PreferredInteractionTarget();
        if (obj == null) return null;
        try
        {
            var pp = MainGame.me?.player?.pos;
            if (pp == null) return null;
            var p = pp.Value;

            float dist;
            var b = obj.GetTotalBounds();
            if (b.size.sqrMagnitude > 0.0001f)
            {
                var cp = b.ClosestPoint(new Vector3(p.x, p.y, b.center.z));
                dist = Vector2.Distance(p, new Vector2(cp.x, cp.y));
            }
            else
            {
                dist = Vector2.Distance(p, obj.pos);
            }

            return dist <= InteractionForceReach ? obj : null;
        }
        catch { return null; }
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
        if (target.IsDrop || target.ExactPoint) return target.Position;
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

    /// <summary>
    /// The nearest graph-0 node to a world point, plus that node's own position. Graph 0 is the
    /// whole-map NPC navmesh and is always scanned, so this works anywhere without a rescan.
    /// </summary>
    private static bool TryGraph0Node(Vector2 p, out Pathfinding.GraphNode node, out Vector2 nodePos)
    {
        node = null;
        nodePos = p;
        try
        {
            var astar = AstarPath.active;
            if (astar == null) return false;

            var constraint = Pathfinding.NNConstraint.Default;
            constraint.graphMask = 1 << 0;

            var nn = astar.GetNearest(new Vector3(p.x, p.y, 0f), constraint);
            if (nn.node == null) return false;
            node = nn.node;
            nodePos = new Vector2(nn.clampedPosition.x, nn.clampedPosition.y);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] TryGraph0Node failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether graph 0 could route between two points AT ALL, decided the same way the pathfinder
    /// itself decides it: <see cref="Pathfinding.ABPath"/> aborts with "no valid path to the target"
    /// when the start and end nodes carry different flood-fill Area ids, i.e. they sit on
    /// disconnected components. Asking up front lets us react to a hopeless route immediately
    /// instead of watching twelve async pull-back queries fail one after another.
    /// </summary>
    private static bool CanRouteOnGraph0(Vector2 from, Vector2 to)
    {
        if (!TryGraph0Node(from, out var a, out _)) return false;
        if (!TryGraph0Node(to, out var b, out _)) return false;
        try { return Pathfinding.PathUtilities.IsPathPossible(a, b); }
        catch { return false; }
    }

    /// <summary>
    /// Free a player who is standing somewhere the navmesh can't route out of, by hopping them to
    /// the nearest walkable spot that IS connected to where they are trying to go.
    ///
    /// This is the "hard stuck" case: a scripted glide (or the game's own physics) can leave the
    /// body in a pocket of scenery — inside a stair alcove, wedged behind a prop — that maps to a
    /// graph-0 node on its own tiny island. From there every route request fails, auto-walk has
    /// nothing to offer, and walking out manually doesn't work either because the body is jammed.
    /// We sample rings outward from the player and take the first walkable node that shares an Area
    /// with the goal, then teleport there. Bounded to <see cref="UnwedgeMaxRadius"/> so this is
    /// always a short hop out of the pocket, never a shortcut across the level.
    ///
    /// Only called from the dungeon escape key, where the player has explicitly asked to be got
    /// out; ordinary walks must not teleport people through walls (a building interior is also a
    /// disconnected island, and stepping "out of" it would mean clipping through its wall).
    /// </summary>
    private static bool TryFreeWedgedPlayer(Vector2 goal, Action onFreed)
    {
        var player = MainGame.me?.player;
        if (player == null) return false;

        if (!TryGraph0Node(goal, out var goalNode, out _) || !goalNode.Walkable) return false;

        var pp = player.pos;
        try
        {
            for (float r = UnwedgeStep; r <= UnwedgeMaxRadius; r += UnwedgeStep)
            {
                for (int i = 0; i < UnwedgeRayCount; i++)
                {
                    float a = i * Mathf.PI * 2f / UnwedgeRayCount;
                    var probe = pp + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                    if (!TryGraph0Node(probe, out var node, out var nodePos)) continue;
                    if (!node.Walkable) continue;
                    if (!Pathfinding.PathUtilities.IsPathPossible(node, goalNode)) continue;

                    _log?.LogWarning($"[NAVIGATOR] Player wedged at {pp}; freeing to connected node {nodePos} " +
                                     $"({Vector2.Distance(pp, nodePos):F0}u)");
                    return TeleportPlayerTo(nodePos,
                        Loc.Get("nav.unstuck"), onFreed);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] TryFreeWedgedPlayer failed: {ex.Message}");
        }

        _log?.LogWarning($"[NAVIGATOR] Player wedged at {pp}; no connected node within {UnwedgeMaxRadius / TileSize:F0} tiles");
        return false;
    }

    /// <summary>
    /// Move the player to a world position using the game's own faded teleport (which also
    /// recalculates their chunk and pulls the camera along). Navigation is torn down first so no
    /// in-flight route/beacon survives the jump; Update's teleport detector schedules the navmesh
    /// rescans at the landing.
    /// </summary>
    private static bool TeleportPlayerTo(Vector2 worldPos, string announcement, Action after = null)
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null) return false;

            AbortForTeleport();
            ReleaseScriptControl();

            // TeleportWithFade takes a GRID position (it multiplies by the 96-unit tile size).
            character.TeleportWithFade(worldPos / TileSize, null, () => after?.Invoke());
            ScreenReader.Say(announcement, interrupt: true);
            _log?.LogInfo($"[NAVIGATOR] Teleported player to {worldPos}");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] TeleportPlayerTo failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Let the player's body keep firing trigger events while auto-walk holds it Kinematic.
    ///
    /// Turning off control makes UpdateBodyPhysics switch the player Rigidbody2D to Kinematic, and
    /// Unity gives a kinematic body NO trigger callbacks against static colliders unless
    /// useFullKinematicContacts is set (default false; the game never sets it — it only ever puts
    /// the player kinematic during its own cutscenes, which don't need to trip anything). GDZone
    /// trigger volumes are static colliders, so every auto-walk was silently gliding straight
    /// through the story zones that a walking player would set off: that is how the pagan-amulet
    /// delivery on dungeon floor 8 could be walked over with all its conditions met and nothing
    /// happening (Player.log had no GDZone.OnTriggerEnter2D line for it at all). Setting this makes
    /// auto-walk trip zones exactly like manual walking.
    ///
    /// Safe to leave on: nothing pushes a kinematic body, so movement is unchanged, and the only
    /// other 2D trigger consumers on the player are drop pickup and grass rustle — both of which
    /// SHOULD fire while walking anyway.
    /// </summary>
    private static void AllowTriggersWhileScripted(BaseCharacterComponent character)
    {
        try
        {
            var body = character?.body;
            if (body != null && !body.useFullKinematicContacts) body.useFullKinematicContacts = true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Could not enable kinematic trigger contacts: {ex.Message}");
        }
    }

    private static void StartWalk(Vector2 dest, string label, MovementComponent.GoToMethod method)
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null)
            {
                _log?.LogError("[NAVIGATOR] Player character component is null");
                ScreenReader.Say(Loc.Get("nav.cannot_walk"), interrupt: true);
                _isWalking = false;
                return;
            }

            // Disable player control so the body becomes Kinematic (UpdateBodyPhysics), exactly
            // like the long-distance native walk. A Dynamic body physically collides and JAMS on
            // walls/fences — the reason auto-walk got stuck bumping around inside the house — while
            // a Kinematic body glides along the A* path like an NPC. Restored in on_complete /
            // ReleaseScriptControl. Idempotent if a retry re-enters here with control already off.
            character.control_enabled = false;
            _weDisabledControl = true;
            AllowTriggersWhileScripted(character);

            // from_script:true suspends player input so the movement state machine
            // drives the character cleanly. AStar routes around obstacles; Direct is
            // the straight-line fallback used when A* can't find a valid path.
            character.GoTo(
                dest,
                snap_to_node: false,   // we pre-snap to a walkable node ourselves
                on_complete: () =>
                {
                    _isWalking = false;
                    // Restore player control / Dynamic body (we forced Kinematic for the glide),
                    // unless a cutscene has grabbed the player mid-walk — then leave control alone
                    // so we don't re-Dynamic the body and jam its scripted scene.
                    if (!_gameOwnsPlayer)
                    {
                        var ch = MainGame.me?.player?.components?.character;
                        if (ch != null) ch.control_enabled = true;
                        _weDisabledControl = false;
                    }
                    FacePlayerAtTarget();
                    // Bias vanilla E onto the object we walked to, not a closer neighbour.
                    SetArrivedTarget(_shortWalkTarget.Object);
                    ScreenReader.Say(Loc.Fmt("nav.arrived_at_simple", label), interrupt: true);
                    _log?.LogInfo($"[NAVIGATOR] Arrived at {label} ({method})");
                    // A drop we just walked onto gets shoved clear the moment the body turns
                    // Dynamic again; watch it and chase it if E would no longer reach it.
                    ArmDropSettleCheck(_shortWalkTarget);
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
                        // First failure this walk: the target may just sit on navmesh that hasn't
                        // streamed/activated yet (post-teleport, post-sleep, any partial-navmesh
                        // state). Force a bounded rescan around player<->target and retry the walk
                        // once (deferred a few frames so the queued graph update processes) before
                        // resorting to the graph-0 escalation. _rescanRetried gates it to one shot.
                        if (!_rescanRetried && (_shortWalkTarget.Object != null || _shortWalkTarget.DropGo != null))
                        {
                            _rescanRetried = true;
                            var pPos = MainGame.me?.player?.pos ?? Vector2.zero;
                            var mid = (pPos + _shortWalkTarget.Position) * 0.5f;
                            float span = Vector2.Distance(pPos, _shortWalkTarget.Position) / TileSize + 12f;
                            ForceNavmeshRescanAround(mid, span);
                            _rescanRetryTarget = _shortWalkTarget;
                            _rescanRetryFramesLeft = RescanRetryDelayFrames;
                            _rescanRetryPending = true;
                            _isWalking = false;
                            _log?.LogWarning($"[NAVIGATOR] A* failed to {label}, forcing navmesh rescan + retry");
                            return;
                        }
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
                        ScreenReader.Say(Loc.Fmt("nav.could_not_reach", label), interrupt: true);
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
            ScreenReader.Say(Loc.Get("nav.walk_failed"), interrupt: true);
            _isWalking = false;
        }
    }

    /// <summary>
    /// Cancel whatever navigation is active — the compass beacon or an in-progress
    /// auto-walk. Bound to Escape so one key always stops guidance.
    /// </summary>
    internal static void CancelNavigation()
    {
        // Order matters: Escape means "stop what is happening to me". Something actually moving the
        // player is always that; the guidance MODE is only what Escape means when nothing is.
        if (_longWalkActive) StopLongWalk(announce: true);
        else if (_beaconActive) StopBeacon();
        else if (_isWalking) StopWalking();
        else if (GuidedWalk.IsEnabled) GuidedWalk.Disable(announce: true);
    }

    /// <summary>
    /// Clear stale per-walk recovery state on a scene change / save-load / day change. Sleeping and
    /// loading don't teleport or reload navmesh, so without this the "A* already failed" guard and
    /// the one-shot rescan retry could linger into the new session. Cheap insurance — the reactive
    /// rescan path self-heals anyway. Called from Plugin.Update's scene-change branch.
    /// </summary>
    internal static void ResetNavStateOnSceneChange()
    {
        GuidedWalk.Stop(announce: false);
        _astarFailedForWalk = false;
        _rescanRetried = false;
        _wallRecoveries = 0;
        _currentRoute = null;
        _routeIsWorldMesh = false;
        _rescanRetryPending = false;
        _dropSettlePending = false;
        _dropChases = 0;
        _routeRejectedForSolid = null;
        // A clear spot remembered during an EARLIER walk is not somewhere to be teleported back to
        // during this one; the wall guard records a fresh one on the first clear frame.
        _hasLastClearPos = false;
        _teleportRescanFramesLeft = 0;
        _hasLastPlayerPos = false;
        // Drop every transition baseline so the new session establishes its own instead of comparing
        // against the last one (which would fire a phantom transition, or worse, miss a real one
        // because the old value happens to match). The player object itself is replaced on load, so
        // the cached PlayerComponent has to go with it.
        _hasLastEnvironmentState = false;
        _playerComponent = null;
        _hasLastPlayerZone = false;
        _lastPlayerZone = null;
        _hasLastDungeonState = false;
        // A load IS a world transition — the biggest one there is — so let the arriving scene settle
        // under the same quick-refresh window every other switch gets.
        NotifyWorldTransition("scene change");
        // The escape offer belongs to one dungeon level; never let it survive into the next.
        _escapeExitObject = null;
        _escapeTeleportArmed = false;
        // Which doorway variant the game runs is a property of the SAVE (see DedupeDoorVariants),
        // so what was learned in the last one must not carry into the next.
        _liveDoorTags.Clear();
        _liveDoorVariant = null;
    }

    /// <summary>
    /// Force the game's own bounded navmesh update (graph 0 + the player graph) over a room-sized
    /// box around a point, mirroring what the game does when a chunk streams in
    /// (ChunkedGameObject.RescanAStar -> ChunkManager.RecalcAStarBounds -> UpdateAstarBounds). Used to
    /// re-activate interior navmesh after a teleport/sleep, where the game otherwise waits for the
    /// player to physically move before the far side of the room becomes walkable. Cheap vs a full
    /// AStarTools.Rescan(); no visible/audible effect.
    /// </summary>
    private static void ForceNavmeshRescanAround(Vector2 center, float tiles = 20f)
    {
        try
        {
            float size = tiles * TileSize;
            AStarTools.UpdateAstarBounds(new Bounds(center, Vector3.one * size));
            _log?.LogInfo($"[NAVIGATOR] Forced navmesh rescan around {center} (~{tiles:F0} tiles)");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] ForceNavmeshRescanAround failed: {ex.Message}");
        }
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
            GuidedWalk.Suspend();
            _longWalkActive = false;
            _beaconActive = false;
            _isWalking = false;
            _routePending = false;
            _routeNeedsRecompute = false;
            _exitAssisting = false;
            _escapeLegActive = false;
            _fallbackPending = false;
            _escalatePending = false;
            _walkWatchdog = 0;
            _longWalkStuckTicks = 0;
            _pullbackTries = 0;
            _journeyReplans = 0;
            _fineRouteTried = false;
            _startOffWorldMesh = false;
            _escapeLegsUsed = 0;
            _returnGlidesUsed = 0;
            _glideOriginValid = false;   // wherever we glided from, it is not near us any more
            _stalledRecomputes = 0;
            _astarFailedForWalk = false;
            _rescanRetried = false;
            _rescanRetryPending = false;
            _dropSettlePending = false;
            _dropChases = 0;
            _routeRejectedForSolid = null;
            _exitAssisting = false;
            _hasBusyPos = false;
            ClearArrivedTarget();
            ReleaseScriptControl();
            ScreenReader.Say(Loc.Get("nav.cancelled"), interrupt: true);
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
            ScreenReader.Say(Loc.Get("nav.walking_stopped"), interrupt: true);
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
            // Turn-by-turn never touches the body, so it just has to shut up and let the scene run.
            GuidedWalk.Stop(announce: false);
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
                _escapeLegActive = false;
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
        // Stamped before the work, not after: an early return (no player yet, empty scene) still
        // counts as "this frame's rebuild attempt", so an on-demand call can't loop it per key.
        _lastRefreshFrame = Time.frameCount;
        _updateCounter = 0;

        // A rebuild is the one heavy thing the mod does, and it runs on its own cadence rather than
        // every frame, so a per-frame average hides it. Metered here and reported by Perf.
        long startedAt = Perf.Now();
        int walked = 0, classified = 0, labelled = 0;
        _classCacheHits = 0;
        _labelCacheHits = 0;

        try
        {
            var player = MainGame.me?.player;
            if (player == null)
                return;

            // The world is a 2D x-y plane (z is only render-sorting depth), so use
            // WorldGameObject.pos which is the authoritative (x, y) world position.
            var playerPos = player.pos;
            // A snapshot of the shared registry, not a fresh scene sweep. FindObjectsOfType walked
            // every object of every type natively and allocated a multi-thousand-element array on
            // each rebuild — and a rebuild can be requested on a keypress, which is what made the
            // nav categories feel like they hitched. Snapshotting also makes the walk below safe:
            // labelling an object can spawn or destroy one, which would otherwise mutate the list
            // we're iterating. See WorldObjectRegistry.
            long phaseAt = Perf.Now();
            WorldObjectRegistry.Snapshot(_scanBuffer);
            Perf.NoteRefreshPhase(Perf.RefreshPhase.Snapshot, phaseAt);
            var allObjects = _scanBuffer;
            if (allObjects.Count == 0)
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

            // The scored WorldZone the player is standing in (tavern/church/cellar/... — null in the
            // open or in an unzoned interior). Resolved once per refresh so the per-object People/
            // Vendors interior filter below is a cheap reference compare, not a physics query each.
            WorldZone interiorPlayerZone = interiorSightBlocked
                ? SafeWorldZone(MainGame.me?.player)
                : null;

            // A dungeon is the ONE enclosed interior where we deliberately reveal everything at
            // once — a blind player can't scout ahead, so they need every enemy, the exit, and the
            // loot located in one pass instead of only whatever happens to be on screen. This is
            // safe from the outdoor x-ray the guard above prevents because a dungeon level is a
            // single self-contained unit: every tile/mob/object is instantiated as a child of
            // dungeon_root (TextureDrawer), and NO outdoor object is — so scoping to dungeon_root's
            // children reveals the whole level and nothing beyond it. See isDungeonObj in the loop.
            var dungeonDrawer = GameRefs.DungeonRoot();
            bool inDungeon = dungeonDrawer != null && dungeonDrawer.dungeon_is_loaded_now;
            Transform dungeonRoot = inDungeon ? dungeonDrawer.transform : null;

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
            _pendingInteractionTargets.Clear();

            phaseAt = Perf.Now();
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
                // Player/prefab verdict comes from the registry's per-object cache. Computing it
                // inline read obj.name four times, and Unity allocates a fresh string on every
                // single name read — thousands of objects per rebuild made that pure GC churn.
                if (WorldObjectRegistry.IsExcluded(obj)) continue;

                // Whether this object is part of the loaded dungeon level (a child of dungeon_root).
                // Computed up here (not just below) because the DLC filter needs it: see the
                // exemption on the next line. isDungeonObj is only ever true while inDungeon.
                bool isDungeonObj = dungeonRoot != null && obj.transform != null &&
                                    obj.transform.IsChildOf(dungeonRoot);

                // Distance first, before any classification work. Every category caps out at
                // MaxHarvestableNavDistance or below, so anything farther is dropped no matter what
                // it turns out to be — and finding out what it is costs a definition lookup plus
                // reading obj.name, which allocates a fresh string out of the engine on every access.
                // The scene holds thousands of objects and only a small ring around the player can
                // ever be listed, so testing the cheap thing first (pos is a per-frame cached
                // transform read) is what keeps a rebuild small enough to run on a keypress. Dungeon
                // objects are exempt: a loaded level is revealed whole, with no distance cap at all.
                var objPos = obj.pos;
                walked++;
                var distance = Vector2.Distance(objPos, playerPos);
                if (!isDungeonObj && distance > MaxHarvestableNavDistance) continue;

                // Skip DLC "ruins" the player doesn't own (souls zone, Euric's room, etc.) — they
                // spawn into every save regardless of ownership but are inert without the DLC.
                // EXCEPTION: objects the game generated into the active dungeon level (children of
                // dungeon_root) are always the player's real content, so never DLC-cull them — this
                // is what un-hid the pickaxe mining veins (dungeon_source_*), which share an obj_id
                // with the unowned-Souls overworld ruins diamond source (a world_root child, still
                // hidden). Without this the veins were dropped here before ever being classified.
                if (!isDungeonObj && !WorldObjectRegistry.IsDlcAvailable(obj)) continue;

                classified++;
                long classifyAt = Perf.Now();
                bool got = TryClassifyCached(obj, out var category);
                Perf.NoteRefreshPhase(Perf.RefreshPhase.Classify, classifyAt);
                if (!got) continue;

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
                // Fishing spots share the harvestables' reach rules: they sit out across open water,
                // get culled (deactivated) when off-screen, and are simple static world objects that
                // stay valid while culled. Without this a blind player can only "see" a fishing spot
                // once it's already on screen — i.e. can never navigate TO one. They're always
                // outdoors, so keeping them while culled poses no interior x-ray risk.
                // In a dungeon, reveal the whole self-contained level: keep every dungeon object
                // (a child of dungeon_root) listed even while culled, and lift its distance cap so
                // far rooms of a large level still appear. Scoped to dungeon_root children, so the
                // outdoor world is never x-rayed. isDungeonObj (computed above, before the DLC
                // filter) is only ever true while inDungeon, so there's zero cost/behaviour change
                // anywhere else.
                bool farReach = IsHarvestableCategory(category) || category == NavCategory.FishingSpots;

                // Built/placed structures — crafting stations, other built objects, roofs — are static
                // world objects the player deliberately placed and often needs to walk back to. The
                // marquee case is a one-time quest build like "Das Buffet aufbauen" on the witch hill:
                // you find it once, then can't relocate it. Like harvestables, a blind player can't pan
                // the camera to spot one, and the game culls (deactivates) it the moment it leaves the
                // screen — so from a landmark anchor a few tiles away it silently drops out of the
                // "Crafting stations" list. Keep these listed while culled too, under the same interior-
                // sight guard as harvestables (never x-ray a wall-enclosed interior) and the same normal
                // 60-tile cap (no reach bump — they're not part of farReach). They stay valid culled.
                // Vendors join this group for the same reason: a stall like the egg seller ("frische
                // Eier") is a static world object that the game culls the moment it's off-screen, so
                // without this it only appears once you're a few steps away — exactly what a blind
                // player can't do (find it from across the map to walk there). Vendor NPCs stay
                // active on their own, so this only affects the static stalls. The interior filter
                // added below still hides culled outdoor vendors when you're inside.
                bool builtCategory = category == NavCategory.Stations ||
                                     category == NavCategory.Buildables ||
                                     category == NavCategory.Roofs ||
                                     category == NavCategory.LoadedPallets ||
                                     category == NavCategory.EmptyPallets ||
                                     category == NavCategory.Vendors ||
                                     // Zombie mines are placed outdoor structures the game culls the
                                     // moment they leave the screen; without this the mine only appears
                                     // once you're already next to it — the opposite of "find it easily".
                                     category == NavCategory.ZombieMines;
                // Graves are the same case as built objects and then some. They are static, the
                // graveyard holds dozens of them, and the flow that matters most — carrying a fresh
                // corpse out of the morgue before it rots — needs the nearest EMPTY grave at a
                // moment when not one of them is on screen. The game culls every grave that isn't on
                // camera, so without this the Graves list is empty the second you step out of the
                // morgue door and the only route back to the graveyard is the Landmarks entry (and
                // the seconds it costs). Grave state (body, cross, fence) lives in the serialized
                // obj.data, which stays valid while the object is culled, so the mirrored Empty /
                // Exhumable / Decorate / Fence lists below are correct for culled graves too.
                // Marked-but-undug grave plots belong to the same group: they sit in the graveyard,
                // never move, and are looked for precisely when they're off-camera — you plan a
                // grave at the build desk, then have to walk to the plot you just marked.
                bool graveCategory = category == NavCategory.Graves ||
                                     category == NavCategory.DiggableGraves;

                bool keepIfCulled =
                    ((farReach || builtCategory || graveCategory) && !interiorSightBlocked) ||
                    isDungeonObj;
                // Last chance for a culled object: it's in the room the player is standing in. A
                // sighted player entering the church or the cellar takes in the whole room from the
                // doorway; a blind player would otherwise only get the handful of objects the camera
                // happens to frame and would have to walk to a second spot to "see" the rest. This
                // reveals the current interior at once without x-raying the outdoors — see
                // IsInPlayerInterior. Ordered last (and after the cull test) so its physics query
                // only runs for objects that would otherwise be dropped.
                // Doors take one extra test before the reveal. A teleport door is not a static prop:
                // the scene ships several variants of the same doorway (level3 has
                // tp_church_a_/2_a_/3_a_ and tp_mortuary_from_church_b_/2_b_/3_b_ — the church exit
                // and the mortuary hatch each exist three times over) and the game runs exactly ONE
                // of each, leaving the others switched off. An object switched off by the GAME has a
                // deactivated ancestor; one merely off-camera is deactivated on itself with a live
                // parent chain (that's how the chunk culler turns things off). So only camera-culled
                // doors may be revealed — and DedupeDoorVariants below collapses whatever variants
                // still make it through into a single entry per doorway.
                bool revealable = category != NavCategory.Doors || IsCameraCulled(obj);
                if (!keepIfCulled && !obj.gameObject.activeInHierarchy &&
                    !(revealable &&
                      IsInPlayerInterior(obj, distance, interiorSightBlocked, interiorPlayerZone)))
                    continue;

                // Remember which door variants the game actually runs: a variant seen active once is
                // the live one for this save, and stays the right answer later when it's off-camera
                // and its dead siblings are indistinguishable from it. See DedupeDoorVariants.
                if (category == NavCategory.Doors && obj.gameObject.activeInHierarchy)
                    NoteLiveDoor(obj);

                var maxDist = isDungeonObj ? float.MaxValue
                                           : (farReach ? MaxHarvestableNavDistance : MaxNavDistance);
                if (distance > maxDist) continue;

                // No x-ray of the town's characters through the walls: NPCs (People), enemies
                // (Enemies) and vendor NPCs (the traveling merchant) keep simulating while
                // off-screen, so unlike static objects they stay active in the hierarchy and the
                // cull above never drops them. (cur_environment is unreliable here — the game never
                // sets it, so it can't tell an indoor character from an outdoor one.) When the player
                // is in an enclosed interior, keep only characters in the SAME scored WorldZone as
                // the player — the tavern, church and cellar are all zones, so their occupants
                // survive while the outdoor crowd (a different zone / no zone) drops out. If the
                // player's interior isn't a zone (e.g. the home), fall back to a tight radius the
                // spatially-offset outdoor crowd won't fall inside. Dungeon objects are exempt:
                // there we deliberately reveal the whole self-contained level (isDungeonObj), so
                // every enemy stays listed no matter how far. GetMyWorldZone is a geometric physics
                // query, run only for the handful of active characters while indoors.
                if (interiorSightBlocked && !isDungeonObj &&
                    (category == NavCategory.People || category == NavCategory.Enemies ||
                     category == NavCategory.Vendors))
                {
                    if (interiorPlayerZone != null)
                    {
                        if (SafeWorldZone(obj) != interiorPlayerZone) continue;
                    }
                    else if (distance > InteriorPeopleFallbackRadius)
                    {
                        continue;
                    }
                }

                labelled++;
                long labelAt = Perf.Now();
                var label = GetObjectLabelCached(obj);
                if (category == NavCategory.LoadedPallets || category == NavCategory.EmptyPallets)
                    label = PalletLabel(obj, label);
                // Which stage the bed is at — marked out, empty, growing, ready — since the game
                // gives every stage of one crop the same name.
                if (category == NavCategory.GardenBeds)
                    label = GardenBedLabel(obj, label);
                // Whether the node can be worked at all yet. A list of trees is otherwise a row of
                // entries that look alike, and the player learns which ones the game will refuse
                // only by walking to each in turn. What each one YIELDS is deliberately not here —
                // it would double the length of every row, and the walk-up readout says it at the
                // moment the decision is actually made (see ResourceYield).
                if (IsYieldCategory(category))
                    label = WorkUnlock.With(label, obj?.obj_def);
                // Worker zombies read out their efficiency + assignment here, since pressing E on
                // one picks it up rather than inspecting it. No-op for non-workers.
                label = InteractionDetector.AppendWorkerInfo(label, obj);
                // The game's "talk to ME next" bubble: a script armed this one copy with a one-shot
                // interaction event. Say so in the list, then route it by what was armed. An NPC
                // ("wants to talk", the game's (speak) icon) is a quest script picking out whom to
                // address, so it's mirrored into Quests. Anything else ("has something new", the
                // (view) icon) is just as often a plain container the game flagged — the tavern
                // money box, a delivery crate — which has no business in the quest list, so it goes
                // to its own Something new category instead. No-op for unarmed objects.
                Perf.NoteRefreshPhase(Perf.RefreshPhase.Label, labelAt);

                if (InteractionDetector.HasPendingScriptedInteraction(obj))
                {
                    label = InteractionDetector.WithPendingInteraction(label, obj);
                    var pending = new NavigationTarget
                    {
                        Object = obj,
                        Label = label,
                        Position = objPos,
                        Distance = distance
                    };
                    if (obj.obj_def != null && obj.obj_def.IsNPC())
                        _pendingInteractionTargets.Add(pending);
                    else
                        _byCategory[NavCategory.SomethingNew].Add(pending);
                }
                _byCategory[category].Add(new NavigationTarget
                {
                    Object = obj,
                    Label = label,
                    Position = objPos,
                    Distance = distance
                });

                // An empty grave — a real grave (its own Grave interaction, so E opens the grave
                // menu) with no body in it — is where the corpse in your hands goes. Mirror those
                // into their own list: an established graveyard is dozens of graves and the Graves
                // list is mostly full ones, which is a long cycle while a body rots. Non-interactive
                // grave scenery (listed under Graves by obj_id) is excluded — there's nothing to
                // bury in it. They stay under Graves too, so the general browse stays complete.
                if (category == NavCategory.Graves && IsEmptyGrave(obj))
                {
                    _byCategory[NavCategory.EmptyGraves].Add(new NavigationTarget
                    {
                        Object = obj,
                        Label = label,
                        Position = objPos,
                        Distance = distance
                    });
                }

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

            Perf.NoteRefreshPhase(Perf.RefreshPhase.Objects, phaseAt);

            // Collapse the scene's duplicate copies of a doorway down to the one that works.
            phaseAt = Perf.Now();
            DedupeDoorVariants();
            Perf.NoteRefreshPhase(Perf.RefreshPhase.Doors, phaseAt);

            // Active quest targets are gathered separately: they are resolved by
            // obj_id from the save's task list (not by walking the scene), and they
            // bypass the distance cap so a far-off quest objective always shows up.
            phaseAt = Perf.Now();
            GatherQuestTargets(playerPos);

            // Where to get rid of the corpse you are carrying (Crafting stations).
            AddCarriedBodyDisposal(playerPos);
            Perf.NoteRefreshPhase(Perf.RefreshPhase.Quests, phaseAt);

            // Fixed landmarks (Tavern, Church, home Graveyard). These are world zones that
            // are always loaded regardless of distance, so they give a blind player a way to
            // set off toward a far destination from anywhere — the compass beacon then guides.
            phaseAt = Perf.Now();
            GatherLandmarkTargets(playerPos, allObjects);
            Perf.NoteRefreshPhase(Perf.RefreshPhase.Landmarks, phaseAt);

            // Ground drops (bodies/loot) are DropResGameObjects, not WorldGameObjects, so
            // they need their own pass or they stay invisible to the screen reader.
            phaseAt = Perf.Now();
            GatherDropTargets(playerPos);
            Perf.NoteRefreshPhase(Perf.RefreshPhase.Drops, phaseAt);

            phaseAt = Perf.Now();
            // Teach the parking-spot filter about any pile-up the naming rule missed, from the
            // characters this rebuild just listed. Costs one pass over three short lists.
            NoteCharacterStacks();

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

            NoteRefreshSettled();
            Perf.NoteRefreshPhase(Perf.RefreshPhase.Finish, phaseAt);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error refreshing destinations: {ex.Message}");
        }
        finally
        {
            Perf.NoteRefresh(startedAt, walked, classified, _classCacheHits, labelled, _labelCacheHits);
        }
    }

    // Reused so the per-rebuild pile-up check allocates nothing.
    private static readonly List<(WorldGameObject Obj, Vector2 Pos)> _characterSpots = new(64);

    /// <summary>
    /// Hand every character this rebuild listed to <see cref="StockPointFilter"/>, which reports
    /// (and only reports) any spot where several of them are standing on top of one another. That
    /// is what an off-stage parking point looks like from the outside, so if one ever turns up that
    /// the tag rules do not recognise, the log names it instead of leaving it to guesswork.
    /// </summary>
    private static void NoteCharacterStacks()
    {
        _characterSpots.Clear();
        AddSpots(_byCategory[NavCategory.People]);
        AddSpots(_byCategory[NavCategory.Enemies]);
        AddSpots(_byCategory[NavCategory.Vendors]);
        StockPointFilter.NoteCharacters(_characterSpots);

        static void AddSpots(List<NavigationTarget> from)
        {
            foreach (var t in from)
                if (t.Object != null) _characterSpots.Add((t.Object, t.Position));
        }
    }

    /// <summary>
    /// Close the quick-refresh window early once the rebuilt list has stopped changing size for
    /// <see cref="FastRefreshStableTicks"/> rebuilds in a row and no fade is still running. See the
    /// field comments above — this is what keeps a transition from paying for a full two seconds of
    /// scene-wide rebuilds when the new room finished streaming in after a couple of frames.
    /// </summary>
    private static void NoteRefreshSettled()
    {
        if (Time.unscaledTime >= _fastRefreshUntil)
        {
            _fastRefreshStableTicks = 0;
            _fastRefreshLastCount = -1;
            return;
        }

        int total = 0;
        foreach (var cat in _categoryOrder)
            total += _byCategory[cat].Count;

        if (total == _fastRefreshLastCount) _fastRefreshStableTicks++;
        else _fastRefreshStableTicks = 0;
        _fastRefreshLastCount = total;

        if (_fastRefreshStableTicks >= FastRefreshStableTicks && !IsCameraFading())
        {
            _fastRefreshUntil = 0f;
            _fastRefreshStableTicks = 0;
            _fastRefreshLastCount = -1;
            _log?.LogInfo($"[NAVIGATOR] New surroundings settled ({total} targets), back to the normal refresh cadence");
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
    ///
    /// Then the two hand-authored task tables (<see cref="TaskGdPointLandmarks"/>,
    /// <see cref="TaskObjectLandmarks"/>) for objectives the vanilla arrow simply doesn't cover.
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

                // No arrow target set (or its object isn't loaded): nothing to walk to. An NPC the
                // game has parked off-stage is not in the world either — the arrow is stale until
                // the schedule brings them back, and a sighted player sees no arrow at all.
                if (target == null || InteractionDetector.IsPlayer(target)) continue;
                if (StockPointFilter.IsParked(target)) continue;
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

            // Hand-authored objectives the vanilla arrow can't express. The loop above can only
            // point at quests whose definition names an arrow_wgo (see [[quest-arrow-limitation]]),
            // and meeting/trigger spots are exactly the kind that don't: they are bare ground or an
            // invisible zone volume. Both tables below are gated on the task being Visible, so they
            // show up with the objective and vanish when it completes — same lifetime as an arrow.
            // ExactPoint on both: these fire on the player collider ENTERING them, and the normal
            // approach offset stops about a tile short, i.e. outside.
            foreach (var (npcId, taskId, gdPoint, labelKey) in TaskGdPointLandmarks)
            {
                if (!IsTaskVisible(npcId, taskId)) continue;
                // GD points can be disabled per quest state and GetGDPointBy* skip disabled ones,
                // so a null here just means "not in the world right now" — stay silent.
                var point = WorldMap.GetGDPointByGDTag(gdPoint, log_if_null: false)
                            ?? WorldMap.GetGDPointByName(gdPoint, log_if_null: false);
                if (point == null) continue;
                questList.Add(new NavigationTarget
                {
                    Label = Loc.Get(labelKey),
                    Position = point.pos,
                    Distance = Vector2.Distance(point.pos, playerPos),
                    ExactPoint = true
                });
            }

            foreach (var (npcId, taskId, objectName, labelKey) in TaskObjectLandmarks)
            {
                if (!IsTaskVisible(npcId, taskId)) continue;
                var objPos = TaskObjectPosition(objectName);
                if (objPos == null) continue;
                questList.Add(new NavigationTarget
                {
                    Label = Loc.Get(labelKey),
                    Position = objPos.Value,
                    Distance = Vector2.Distance(objPos.Value, playerPos),
                    ExactPoint = true
                });
            }

            AddPendingRiverMeeting(questList, playerPos);

            // Objects a quest script armed with a one-shot interaction event (collected by the scene
            // scan, see InteractionDetector.HasPendingScriptedInteraction). This is the game telling
            // the player "interact HERE next" — the same thing a quest arrow says — so mirror them
            // into Quests. They stay in their own category too, so the general browse is unchanged.
            // Without this, a ritual that arms one of five identically named NPCs (Clotho's memories)
            // could only be solved by pressing E on each copy until one of them answered.
            foreach (var pending in _pendingInteractionTargets)
            {
                if (pending.Object != null && !seen.Add(pending.Object)) continue;
                questList.Add(pending);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error gathering quest targets: {ex.Message}");
        }
    }

    // Named-NPC landmarks: key shops/services that are world objects rather than zones,
    // resolved by obj_id map-wide.
    private static readonly (string objId, string labelKey)[] NpcLandmarks =
    {
        ("npc_merchant", "landmark.merchant"),
    };

    // Permanent map features that are a static WORLD OBJECT rather than an NPC or a zone: the spot
    // itself is the landmark, and it is there for the whole game. Matched on an obj_id FRAGMENT
    // (a placed object carries a numbered id, "throw_body_river_1"), map-wide and with no distance
    // cap, and kept while the object is culled — a landmark's whole job is to be findable from far
    // away, which is precisely where the game has every static prop deactivated.
    // (obj_id fragment, spoken label).
    private static readonly (string objIdFragment, string labelKey)[] ObjectLandmarks =
    {
        // The river bank a carried corpse gets thrown off (obj_id "throw_body_river"). Yorick asks
        // for it once — "throw the neighbour in the river" — but it stays the free way to dispose of
        // any corpse for the rest of the game, so this is NOT task-gated the way the meeting spots
        // in TaskGdPointLandmarks are: a player who took that quest long ago still cannot find it.
        // Nothing in the game marks the place. There is no quest arrow, the dialogue only says "the
        // river", and the object has no translated name, so it read as a prettified id in the Other
        // category and dropped out of that list from more than a screen away (Other is neither a
        // far-reach nor a built category, so a culled one is skipped). See
        // [[exhumation-grave-disposal]] for the rest of that flow.
        ("throw_body_river", "landmark.river_body_throw"),
    };

    // Building landmarks anchored on their EXTERIOR entrance door (a teleport WGO), not an interior
    // NPC/zone — interiors are separate, navmesh-disconnected regions auto-walk can't reach. The
    // door's place comes from its custom_tag (InteractionDetector.DoorPlaceFromTag). (place, label).
    private static readonly (string doorPlace, string labelKey, string zoneId)[] DoorLandmarks =
    {
        // doorPlace is the RAW tag word matched by FindEntranceDoor — never translate it.
        // zoneId is the world zone this door supersedes, so the zone isn't listed twice.
        ("Tavern", "landmark.tavern", "tavern"),
        ("House", "landmark.home", "home"),
        // The church IS a separate teleport interior (place tag "Church", a "teleport_outside"
        // door at the graveyard). Its zone members (pulpit, candles) are staged far away inside,
        // so the generic zone anchor sent auto-walk indoors — anchor on the real outdoor door
        // instead, exactly like the Tavern/Home (the Doors category's "Door outside: Church").
        ("Church", "landmark.church", "church"),
    };

    // World-zone ids NOT to add as landmarks — superseded by a door landmark above (the zone's
    // geometric centre is inside the building and unroutable).
    private static readonly HashSet<string> SkipZoneIds = new() { "home" };

    // Zone landmarks anchored on a named GD point instead of the generic "nearest member object".
    // Same idea as DoorLandmarks: when the spot that MATTERS in a zone isn't any of the zone's own
    // objects, name it explicitly. (zone id, GD point gd_tag/name).
    private static readonly (string zoneId, string gdPoint)[] GdPointZoneAnchors =
    {
        // The cliffs are open ground: ZoneAnchorObject picks whatever scenery (grass, trees) sits
        // nearest the player, which lands ~9 m short of gd_actors_hiding_point and OUTSIDE
        // actor_hiding_place_gd_zone — the trigger for Vagner's night meeting after the theatre
        // scene (flow script on_enter_actor_hiding_place: needs the player flag
        // actor_is_waiting_at_the_sea plus TimeOfDay == Night, and fires on zone ENTRY). Walking
        // to the GD point puts the player inside the trigger. Harmless once that quest is done —
        // it just makes "Cliff" mean the cliff-top clearing rather than the nearest shrub.
        ("cliff", "gd_actors_hiding_point"),
    };

    // QUEST targets anchored on a named GD point that exist ONLY while an NPC task is running, for
    // meeting spots that aren't inside a world zone of their own (so GdPointZoneAnchors has no
    // zone to attach to, and hijacking a neighbouring zone would drag a useful landmark off its
    // real place). They appear when the task goes Visible and vanish when it completes — which is
    // why they belong in Quests, not Landmarks: a Landmark is a permanent feature of the map, and
    // someone hunting an objective looks under Quests. Gathered in GatherQuestTargets.
    // (npc id, task id, GD point gd_tag/name, spoken label).
    private static readonly (string npcId, string taskId, string gdPoint, string labelKey)[] TaskGdPointLandmarks =
    {
        // Snake's trap for the vampire hunter (task snake_trap: "meet me at the Witch Hill, right
        // above the road"). He is teleported to gd_cultist_near_stone (7272, -1452) and locked
        // there, ~6 m north of the mountain road and ~35 m south-west of the burning site — open
        // ground below the hill, i.e. no zone anchors it. Bring one wooden plank; the flow charges
        // it as the price of the "here's a plank" answer.
        ("npc_cultist", "snake_trap", "gd_cultist_near_stone", "landmark.snake_meeting_point"),
    };

    // Task-gated quest targets anchored on a named scene OBJECT rather than a GD point. Same
    // appear-with-the-task/vanish-on-completion rule and same Quests category as
    // TaskGdPointLandmarks; the difference is what the game built the spot out of. Dungeon story
    // triggers are WorldSimpleObjects baked into a room
    // interior preset, so there is no GD point to aim at AND no WorldGameObject for the normal
    // object scan to pick up — without an entry here they are unreachable by any category.
    // (npc id, task id, object/prefab name, spoken label).
    private static readonly (string npcId, string taskId, string objectName, string labelKey)[] TaskObjectLandmarks =
    {
        // "Bring the pagan amulet to the last room of the eighth dungeon floor" (Game of Crone).
        // The trigger is the WSO gd_zone_refugees_exit_8, baked into the Exit_8 exit-room preset at
        // room tile (5,5) — ~3 tiles north of the stairs down at (4.5,8), i.e. the middle of the
        // room, with a collider only about a tile wide. Its flow script on_enter_gd_zone_s23 fires
        // on zone ENTRY and additionally wants floor 8 cleared plus the amulet in the inventory, so
        // ExactPoint matters here exactly as it does for the cliff meeting: a tile short is outside.
        ("player", "s_ev_22_goto_8lvl", "gd_zone_refugees_exit_8", "landmark.amulet_delivery_spot"),

        // "Bring the leg to the ghost on the eighth dungeon floor" (task s_ev_28_leg_return, after
        // the golem fight). Same trigger object as the amulet above — on_enter_gd_zone_s23 has a
        // SECOND branch wired after the amulet one: player flag gd_zone_s29_2_is_active >= 1 plus
        // skeleton_leg in the inventory runs refugee_ev_s29_2, which is what sets this task to
        // Complete. No dungeon-cleared condition on this branch, so the leg can be handed over on a
        // revisit; only the two tasks differ, hence a row of its own rather than a shared one.
        ("npc_ghost_priest", "s_ev_28_leg_return", "gd_zone_refugees_exit_8", "landmark.ghost_leg_delivery_spot"),
    };

    /// <summary>
    /// While a corpse is on the player's shoulder, list the river bank under Crafting stations —
    /// alongside the morgue throw-in and the crematorium, the game's other ways to be rid of a body.
    /// That is the list you go to when the question is "where do I put this", and it is short.
    ///
    /// It is gated on the carried body rather than on the quest that first sends you here, because
    /// the game registers no task for that step at all: the authored text (task_ghost_body, "Get rid
    /// of the body from the grave at the lower-right corner. Just throw it in the river.") is cut
    /// content — all 788 Flow_SetTaskState nodes in the game carry a literal task id and not one of
    /// them is this, so a task gate could never fire. Yorick only ever says it out loud. The body in
    /// your hands is the better signal anyway: it covers that first quest and every corpse after it,
    /// and it goes quiet the moment your hands are free. The permanent Landmarks entry
    /// (<see cref="ObjectLandmarks"/>) is unaffected and stays listed either way.
    ///
    /// Same label as everywhere else the spot appears — one place, one name.
    /// </summary>
    private static void AddCarriedBodyDisposal(Vector2 playerPos)
    {
        try
        {
            if (!InteractionDetector.IsCarryingBody()) return;

            var river = FindLandmarkObject("throw_body_river", playerPos);
            if (river == null) return;

            _byCategory[NavCategory.Stations].Add(new NavigationTarget
            {
                Object = river,
                Label = Loc.Get("landmark.river_body_throw"),
                Position = river.pos,
                Distance = Vector2.Distance(river.pos, playerPos)
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] carried-body disposal check failed: {ex.Message}");
        }
    }

    // The trigger zones that start Gerry's river scene, and the player param that arms them.
    private const string RiverMeetingParam = "showing_gerry_near_river";
    private static readonly string[] RiverMeetingZones = { "gd_zone_gerry_up", "gd_zone_gerry_down" };

    /// <summary>
    /// After you throw your first corpse in the river, Gerry turns up on the bank to comment on it
    /// (and that scene is what opens the NPC list). It does NOT play on the throw. The game runs a
    /// three-step flag relay: Yorick's visit after your first burial arms Gerry's own
    /// <c>on_showing_gerry_near_river</c>; the throw spends that and sets the PLAYER param
    /// <c>showing_gerry_near_river</c>; and only walking into the invisible <c>gd_zone_gerry_up</c> /
    /// <c>_down</c> GDZone spends THAT and spawns him.
    ///
    /// A sighted player never notices the last step — the zones sit right by the throw spot, so they
    /// cross one the moment they wander off and the scene feels immediate. A blind player arrives by
    /// auto-walk, throws, and stands still, so nothing ever fires and the questline stalls with no
    /// hint that anything is pending. There is no task and no quest arrow to expose (see the comment
    /// on the carried-body entry above), so read the param the relay itself uses and offer the zone
    /// as somewhere to walk. ExactPoint: this is a collider you must be INSIDE, not an object to
    /// stand next to.
    ///
    /// The FindObjectsOfType sweep is the expensive way to find a zone, which is why it is gated on
    /// the param first: it only runs in the minutes between that throw and that meeting, once a save.
    /// </summary>
    private static void AddPendingRiverMeeting(List<NavigationTarget> questList, Vector2 playerPos)
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null || player.GetParam(RiverMeetingParam) < 1f) return;

            Vector2? best = null;
            float bestSqr = float.MaxValue;
            foreach (var zone in UnityEngine.Object.FindObjectsOfType<GDZone>(true))
            {
                if (zone == null) continue;
                bool match = false;
                foreach (var n in RiverMeetingZones)
                    if (zone.name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                if (!match) continue;

                Vector2 p = zone.transform.position;
                float dx = p.x - playerPos.x, dy = p.y - playerPos.y;
                float sqr = dx * dx + dy * dy;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = p;
            }
            if (best == null) return;

            questList.Add(new NavigationTarget
            {
                Label = Loc.Get("quest.gerry_river_meeting"),
                Position = best.Value,
                Distance = Vector2.Distance(best.Value, playerPos),
                ExactPoint = true
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] pending river meeting check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The world object nearest the player whose obj_id contains <paramref name="fragment"/>, for
    /// <see cref="ObjectLandmarks"/>, or null when the scene holds none.
    ///
    /// Deliberately accepts a CULLED object: the game deactivates every static prop that leaves the
    /// screen, and a landmark exists to be walked to from across the map. An object the GAME switched
    /// off — a dead scene variant — has a deactivated ANCESTOR instead and is still rejected, the same
    /// test the Doors category uses (see <see cref="IsCameraCulled"/>). Nearest rather than first so
    /// that if the map ever holds several of one kind, the one offered is the one worth walking to.
    /// </summary>
    private static WorldGameObject FindLandmarkObject(string fragment, Vector2 playerPos)
    {
        WorldGameObject best = null;
        float bestSqr = float.MaxValue;

        var objects = WorldObjectRegistry.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            if (obj == null || obj.is_removed) continue;
            if (string.IsNullOrEmpty(obj.obj_id)) continue;
            if (obj.obj_id.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;

            Vector2 p;
            try { p = obj.pos; } catch { continue; }
            float dx = p.x - playerPos.x, dy = p.y - playerPos.y;
            float sqr = dx * dx + dy * dy;
            if (sqr >= bestSqr) continue;

            try
            {
                if (!obj.gameObject.activeInHierarchy && !IsCameraCulled(obj)) continue;
            }
            catch { continue; }

            bestSqr = sqr;
            best = obj;
        }

        return best;
    }

    /// <summary>
    /// True while <paramref name="taskId"/> is a Visible (active) task on <paramref name="npcId"/> —
    /// the same state the HUD task tracker shows, read the way QuestAnnouncer reads it.
    /// </summary>
    private static bool IsTaskVisible(string npcId, string taskId)
    {
        try
        {
            var npcs = MainGame.me?.save?.known_npcs?.npcs;
            if (npcs == null) return false;
            foreach (var npc in npcs)
            {
                if (npc?.tasks == null || !string.Equals(npc.npc_id, npcId, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var task in npc.tasks)
                {
                    if (task == null || task.state != KnownNPC.TaskState.State.Visible) continue;
                    if (string.Equals(task.id, taskId, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] IsTaskVisible failed for {npcId}/{taskId}: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// World position of a named scene object for <see cref="TaskObjectLandmarks"/>, or null when
    /// it isn't in the world right now (wrong dungeon level, quest state, whatever) — a miss is
    /// normal and stays silent, exactly like a disabled GD point.
    /// </summary>
    private static Vector2? TaskObjectPosition(string objectName)
    {
        try
        {
            // Dungeon room interiors Instantiate() their WSOs, so the live GameObject is named
            // "<prefab>(Clone)" — match on the prefix. Inactive children count: the dungeon culls
            // objects that are off-screen and re-activates them as the player approaches, and the
            // whole point of this entry is to be findable from across the level.
            var dr = GameRefs.DungeonRoot();
            if (dr != null && dr.dungeon_is_loaded_now)
            {
                foreach (var tf in dr.GetComponentsInChildren<Transform>(true))
                {
                    if (tf == null) continue;
                    if (!tf.name.StartsWith(objectName, StringComparison.OrdinalIgnoreCase)) continue;
                    return tf.position;
                }
            }

            // Same table can name an overworld trigger zone, which lives in the scene rather than
            // under dungeon_root. GDZone is the component that makes such an object matter, so
            // searching by it keeps this cheap instead of walking every Transform in the world.
            foreach (var zone in UnityEngine.Object.FindObjectsOfType<GDZone>(true))
            {
                if (zone == null) continue;
                if (!zone.name.StartsWith(objectName, StringComparison.OrdinalIgnoreCase)) continue;
                return zone.transform.position;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] TaskObjectPosition failed for {objectName}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// The GD point a zone landmark should anchor on, or null if the zone has no override (or the
    /// point isn't in the world right now — GD points can be disabled per quest state, and
    /// GetGDPointBy* skip disabled ones, so a missing point just falls back to the normal anchor).
    /// </summary>
    private static GDPoint ZoneAnchorGdPoint(string zoneId)
    {
        try
        {
            foreach (var (id, gdPoint) in GdPointZoneAnchors)
            {
                if (!string.Equals(id, zoneId, StringComparison.OrdinalIgnoreCase)) continue;
                // gd_tag is the game's own lookup key, but scene points don't always carry one —
                // fall back to the GameObject name (both are silent, no error-log spam).
                return WorldMap.GetGDPointByGDTag(gdPoint, log_if_null: false)
                       ?? WorldMap.GetGDPointByName(gdPoint, log_if_null: false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] ZoneAnchorGdPoint failed for {zoneId}: {ex.Message}");
        }
        return null;
    }

    // Friendlier spoken names for known world-zone ids; any zone not listed falls back to its
    // prettified id so every zone in the world is still reachable.
    private static readonly Dictionary<string, string> ZoneLabelOverrides = new()
    {
        ["graveyard"] = "zone.graveyard",
        ["church"] = "zone.church",
        ["players_tavern"] = "zone.players_tavern",
        ["player_tavern_cellar"] = "zone.player_tavern_cellar",
        ["refugees_camp"] = "zone.refugees_camp",
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

        // Better Save Soul — any soul-content zone (no base-game zone uses this word), plus
        // Euric's abandoned room (zone_euric_room), a Souls "ruin" that spawns into every save
        // via the save-version migration regardless of ownership (see ObjectRequiredDLC).
        if (id.Contains("soul") || id.Contains("euric"))
            return DLCEngine.DLCVersion.Souls;

        return null;
    }

    /// <summary>
    /// The object-level twin of <see cref="ZoneRequiredDLC"/>. DLC content is spawned into every
    /// save by GameSave save-version migrations (GameSave.cs — the Souls <c>num &lt;= 1310</c> block,
    /// the Stories <c>num &lt; 1200</c> block) REGARDLESS of DLC ownership, so it sits in the scene
    /// as inert set-dressing for players who don't own the DLC. ObjectDefinition has no requires_dlc
    /// field, so we infer membership from the obj_id using deliberately specific tokens (hatch_rust
    /// not "hatch", broken_glass not "glass", players_tavern not "tavern" — the base-game town tavern
    /// must stay visible) to avoid catching base-game objects. Buying the DLC flips IsDLCAvailable
    /// and the objects reappear with no code change.
    /// </summary>
    internal static DLCEngine.DLCVersion? ObjectRequiredDLC(string objId)
    {
        if (string.IsNullOrEmpty(objId)) return null;
        var id = objId.ToLowerInvariant();

        if (id.Contains("soul")              // souls_zone_wall_closed, soul_healer_broken, souls_builddesk, candelabrum_3_3_souls, ...
            || id.Contains("broken_glass")   // pile_of_broken_glass_1..6
            || id.Contains("smiler")         // smilers_box_closed
            || id.Contains("hatch_rust")     // rusty souls-dungeon hatch (NOT the base cellar hatch)
            || id.Contains("dungeon_source") // dungeon_source_diamond
            || id.Contains("euric")          // eurics_room_* (abandoned set-dressing)
            || id.Contains("sin_shard"))     // sin_shard_body_part (the game itself gates this on Souls)
            return DLCEngine.DLCVersion.Souls;

        // Stranger Sins — the player-run tavern, its cellar, and all their equipment (spawned at the
        // player-tavern coords by the num < 1200 migration; see GameSave lines ~1096–1121). Every
        // token below is unambiguous — the base-game town tavern is a separate open zone that never
        // uses these ids (verified: tavern_oven/tavern_kitchen exist only as player-tavern
        // barmen-output stations, and no base teleport tag contains "tavern"+"cellar"). The
        // teleport doors carry a generic obj_id ("teleport_point"/"teleport_inside") but their
        // custom_tag is "tp_tavern_*_cellar_*", so IsObjectDlcAvailable feeds the tag through here too.
        if (id.Contains("players_tavern")     // players_tavern_builddesk, players_tavern_cellar_builddesk
            || id.Contains("tavern_time_machin") // tavern_time_machin_wall_inactive (the time machine)
            || id.Contains("tavern_oven")     // player-tavern cooking oven (barmen-output station)
            || id.Contains("tavern_kitchen")  // player-tavern kitchen (barmen-output station)
            || (id.Contains("tavern") && id.Contains("cellar"))) // tavern_cellar_rack + tp_tavern_*_cellar_* doors (via custom_tag)
            return DLCEngine.DLCVersion.Stories;

        return null;
    }

    /// <summary>
    /// True if <paramref name="wgo"/> may be announced/navigated — i.e. it's base-game content, or
    /// it's DLC content the player actually owns (live gamedata_*.dat check). Used to suppress the
    /// DLC "ruins" that spawn into the world regardless of ownership (see <see cref="ObjectRequiredDLC"/>).
    /// </summary>
    internal static bool IsObjectDlcAvailable(WorldGameObject wgo)
    {
        try
        {
            // Check obj_id first, then custom_tag: teleport doors share generic obj_ids
            // ("teleport_point"/"teleport_inside") and only carry their DLC identity in the
            // custom_tag (e.g. "tp_tavern_from_cellar_b"), so the obj_id alone can't gate them.
            var req = ObjectRequiredDLC(wgo?.obj_id) ?? ObjectRequiredDLC(wgo?.custom_tag);
            return !req.HasValue || DLCEngine.IsDLCAvailable(req.Value);
        }
        catch { return true; }
    }

    /// <summary>
    /// True if a world zone may be announced — i.e. it's base-game, or it's a DLC zone the player
    /// owns (live gamedata_*.dat check). The zone twin of <see cref="IsObjectDlcAvailable"/>: DLC
    /// zones sit active in the scene regardless of ownership, so the zone announcer must gate on
    /// this or it voices e.g. "Tavernenkeller" to a non-owner.
    /// </summary>
    internal static bool IsZoneDlcAvailable(string zoneId)
    {
        try
        {
            var req = ZoneRequiredDLC(zoneId);
            return !req.HasValue || DLCEngine.IsDLCAvailable(req.Value);
        }
        catch { return true; }
    }

    /// <summary>
    /// Populate the Landmarks category with key NPC services (Tavern barman, Merchant) and
    /// every world zone. Zones are always loaded, and the named NPCs resolve map-wide, so
    /// these targets exist even from across the map; the compass/auto-walk then heads there.
    /// Like quest targets, landmarks ignore the distance cap.
    /// </summary>
    private static void GatherLandmarkTargets(Vector2 playerPos, List<WorldGameObject> allObjects)
    {
        try
        {
            var list = _byCategory[NavCategory.Landmarks];

            // Key NPC-anchored destinations.
            foreach (var (objId, labelKey) in NpcLandmarks)
            {
                var wgo = WorldMap.GetWorldGameObjectByObjId(objId, ignore_not_found_error: true);
                if (wgo == null || wgo.is_removed || !wgo.gameObject.activeInHierarchy) continue;
                if (StockPointFilter.IsParked(wgo)) continue;   // parked off-stage, not in the world
                list.Add(new NavigationTarget
                {
                    Object = wgo,
                    Label = Loc.Get(labelKey),
                    Position = wgo.pos,
                    Distance = Vector2.Distance(wgo.pos, playerPos)
                });
            }

            // Static world objects that are a destination in their own right (the river throw spot).
            // Resolved over the registry rather than with WorldMap.GetWorldGameObjectByObjId because
            // that one demands an exact obj_id, and because a culled object has to stay listed here.
            foreach (var (fragment, labelKey) in ObjectLandmarks)
            {
                var wgo = FindLandmarkObject(fragment, playerPos);
                if (wgo == null) continue;
                list.Add(new NavigationTarget
                {
                    Object = wgo,
                    Label = Loc.Get(labelKey),
                    Position = wgo.pos,
                    Distance = Vector2.Distance(wgo.pos, playerPos)
                });
            }

            // Building entrances (Tavern, Home), anchored on the exterior door you press E on.
            // Zone ids superseded by a door landmark we actually resolved. Keyed by ID, not by
            // spoken label: labels are translated, so comparing them only ever worked in English.
            var doorLandmarkZoneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (doorPlace, labelKey, zoneId) in DoorLandmarks)
            {
                var door = FindEntranceDoor(allObjects, doorPlace, playerPos);
                if (door == null) continue;   // no door found: leave the zone landmark in place
                if (!string.IsNullOrEmpty(zoneId)) doorLandmarkZoneIds.Add(zoneId);
                list.Add(new NavigationTarget
                {
                    Object = door,
                    Label = Loc.Get(labelKey),
                    Position = door.pos,
                    Distance = Vector2.Distance(door.pos, playerPos)
                });
            }

            // Every world zone, de-duplicated by id.
            var seenZones = new HashSet<string>();
            var zones = CachedWorldZones();
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
                if (doorLandmarkZoneIds.Contains(zone.id)) continue;

                // Anchor on an actual object in the zone (closest to the player), NOT the
                // geometric centre: a zone centre often falls inside a building (the church in
                // the graveyard, etc.) — a disconnected navmesh pocket auto-walk can't route to.
                // Zone member objects sit on/next to walkable ground, so routing reaches them.
                var anchor = ZoneAnchorObject(zone, playerPos);
                var pos = anchor != null ? anchor.pos : (Vector2)(zone.center_tf?.position ?? Vector3.zero);
                if (zone.center_tf == null && anchor == null) continue;

                // A named GD point (see GdPointZoneAnchors) beats both: it's an authored spot, so
                // walk exactly onto it and drop the object anchor — otherwise InteractionDest would
                // route to the object's collider and ignore the position we just set.
                var gdAnchor = ZoneAnchorGdPoint(zone.id);
                var exact = gdAnchor != null;
                if (exact)
                {
                    anchor = null;
                    pos = gdAnchor.pos;
                }

                list.Add(new NavigationTarget
                {
                    Object = anchor,
                    Label = ZoneLabel(zone.id),
                    Position = pos,
                    Distance = Vector2.Distance(pos, playerPos),
                    ExactPoint = exact
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

            // The anchor has to be somewhere the zone actually IS. A zone's member list is built
            // once, by testing each object's position against the zone's colliders
            // (WorldZone.DoesObjectBelongToZone) — and it is never rebuilt when an object moves. So
            // the list keeps members that have since been carried somewhere else entirely: an NPC
            // teleported to the off-map parking row, and above all a building's INTERIOR, which this
            // game stages in a far-off corner of the world under coordinates that have nothing to do
            // with where the building stands.
            //
            // Taking the nearest member without that check is what put "The village" at (7876, 2250)
            // — about 30 tiles northeast of the player — while the village and its tavern are 120
            // tiles due east. Auto-walk then set off confidently in the wrong direction. A bounds
            // test costs nothing per candidate and rules all of that out; the zone's own geometry is
            // the only honest answer to "where is this place".
            var bounds = zone.GetBounds();
            bool haveBounds = bounds.size.x > 0f && bounds.size.y > 0f;
            Vector2 aim = haveBounds ? (Vector2)bounds.center : playerPos;

            // Aim for the MIDDLE of the place, not its nearest edge.
            //
            // "The village" is an area some 80 tiles across. Anchoring it on whichever of its
            // objects happened to be closest to the player put it on whatever edge the player was
            // facing — from the keeper's yard that is the high ground to the north, reached by
            // scrambling over the cliffs (steep_vert_L, steep_2, steep_end_L in the walk log)
            // instead of by the road everyone actually uses. The tavern, a landmark of its own
            // inside the same village, routed correctly along the road the whole time, which is
            // what "it should take the same way to both" means.
            //
            // A landmark for a PLACE should also stay put: with the nearest-edge rule the announced
            // distance to the village changed every few steps, because the anchor was moving too.
            var target = aim;

            WorldGameObject best = null, bestAnywhere = null;
            float bestSq = float.MaxValue, bestAnywhereSq = float.MaxValue;
            foreach (var w in wgos)
            {
                if (w == null || w.is_removed) continue;
                var wp = w.pos;
                float sq = (wp - target).sqrMagnitude;

                if (sq < bestAnywhereSq) { bestAnywhereSq = sq; bestAnywhere = w; }

                if (haveBounds && !bounds.Contains(new Vector3(wp.x, wp.y, bounds.center.z))) continue;
                if (sq < bestSq) { bestSq = sq; best = w; }
            }

            var chosen = best ?? (haveBounds ? null : bestAnywhere);

            // Logged once per zone: an anchor in the wrong part of a large area sends auto-walk off
            // in the wrong direction, and there is no way to see that from inside the game.
            if (chosen != null && _loggedZoneAnchors.Add(zone.id))
                _log?.LogInfo($"[NAVIGATOR] Zone '{zone.id}' anchored on {chosen.obj_id} at {chosen.pos} " +
                              $"(centre {aim}, bounds {bounds.min} .. {bounds.max})");

            return chosen;
        }
        catch { return null; }
    }

    private static readonly HashSet<string> _loggedZoneAnchors = new(StringComparer.Ordinal);

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

    private static WorldGameObject FindEntranceDoor(List<WorldGameObject> allObjects, string place, Vector2 playerPos)
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

    // Zone ids we've already reported as unnamed, so the log gets one line each rather than one
    // per refresh.
    private static readonly HashSet<string> _unnamedZonesLogged = new();

    private static string ZoneLabel(string zoneId)
    {
        // 1. Our own curated name, where we want something friendlier than the game's.
        if (ZoneLabelOverrides.TryGetValue(zoneId, out var niceKey))
            return Loc.Get(niceKey);

        // 2. The game's own zone name — the same token the HUD banner shows. This was missing,
        //    so landmarks spoke the raw id ("Beegarden") even where the game had a translation.
        //    ZoneScoreAnnouncer and BuildZoneAudit already resolved zones this way.
        try
        {
            var key = "zone_" + zoneId;
            var loc = ScreenReader.StripNguiCodes(GJL.L(key) ?? "").Trim();
            if (!string.IsNullOrEmpty(loc) && loc != key && loc.IndexOf('!') < 0)
                return loc;
        }
        catch { }

        // 3. Keyword rules for zones the game leaves unnamed.
        var described = DescriptiveNames.ForZone(zoneId);
        if (!string.IsNullOrEmpty(described)) return described;

        // 4. Nothing named it. Speak the prettified id ("flat_under_waterflow_3" -> "Flat under
        //    waterflow 3") and say so in the log: that line is how we find out which zone ids still
        //    need a rule, instead of guessing from the spoken text alone.
        var text = zoneId.Replace('_', ' ').Replace('-', ' ').Trim();
        if (text.Length == 0) return zoneId;
        if (_unnamedZonesLogged.Add(zoneId))
            _log?.LogInfo($"[NAVIGATOR] Zone '{zoneId}' has no game name and no rule - speaking raw id");
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
            // DropsList.me.drops is the game's OWN live list of every ground drop — it adds on
            // spawn and removes in its Update the frame a drop is collected, so it is exact and
            // free to read. This used to be FindObjectsOfType<DropResGameObject>(), which walks
            // every object of every type in the scene natively: measured at ~55ms per call in a
            // loaded save (the registry's re-sync sweep costs the same), and a destination rebuild
            // runs one to twelve times a second. It was the single most expensive thing the mod
            // did. Never put a scene sweep back here.
            var drops = DropsList.me?.drops;
            if (drops == null || drops.Count == 0) return;

            var itemList = _byCategory[NavCategory.Items];

            foreach (var drop in drops)
            {
                if (drop == null || drop.is_collected) continue;
                // FindObjectsOfType returned only ACTIVE objects, and this pass relied on that for
                // its no-x-ray behaviour: a drop lying outdoors is deactivated while the player is
                // in an interior and must stay unlisted. DropsList keeps culled drops, so make the
                // same test explicitly.
                if (!drop.gameObject.activeInHierarchy) continue;

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

    /// <summary>
    /// A dug-out grave plot: the open hole you get by digging a marked plot (grave_empty), and the
    /// grave ground it becomes (grave_ground). Both take a body and neither has the Grave
    /// interaction, so they're recognised the way the GAME recognises them — by exact obj id.
    /// WorldGameObject.CanInsertItem hard-codes these two ids to accept an ItemType.Body, and
    /// CustomDrawers.OnObjectRedraw hard-codes the same pair to draw as a grave; there is no
    /// structural flag to test instead. Exact equality, so the marked-but-undug placeholder
    /// grave_empty_place (a shovel node, listed under Diggable graves) is not swept in.
    /// </summary>
    private static bool IsGravePlot(WorldGameObject obj, ObjectDefinition def)
    {
        var id = def?.id ?? obj?.obj_id;
        return id == "grave_empty" || id == "grave_ground";
    }

    /// <summary>
    /// True for a grave that can take the body you're carrying: a real grave — one with the Grave
    /// interaction (E opens the grave menu) or a dug-out plot (see <see cref="IsGravePlot"/>), as
    /// opposed to the obj_id-matched grave scenery that also lists under Graves and has nothing to
    /// bury in — that currently holds no body. Reads the serialized inventory, so it is correct for
    /// culled graves too.
    /// </summary>
    private static bool IsEmptyGrave(WorldGameObject obj)
    {
        try
        {
            if (obj.obj_def == null) return false;
            if (obj.obj_def.interaction_type != ObjectDefinition.InteractionType.Grave &&
                !IsGravePlot(obj, obj.obj_def))
                return false;
            return !HoldsBody(obj);
        }
        catch { return false; }
    }

    private static bool HasExhumableBody(WorldGameObject obj)
    {
        try
        {
            // Exhuming runs through the grave menu, so a grave without the Grave interaction (a
            // dug-out plot that has just been filled) has no Exhume button to press — listing it
            // would send the player to a grave they can't open.
            if (obj?.obj_def == null ||
                obj.obj_def.interaction_type != ObjectDefinition.InteractionType.Grave)
                return false;

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

    // Number of crates currently on a pallet (sums the stack values of its inventory). Reads the
    // serialized data.inventory, which is valid even while the pallet is culled/inactive.
    internal static int PalletCrateCount(WorldGameObject obj)
    {
        try
        {
            var inv = obj?.data?.inventory;
            if (inv == null) return 0;
            int n = 0;
            foreach (var it in inv)
                if (it != null && !it.IsEmpty()) n += it.value;
            return n;
        }
        catch { return 0; }
    }

    // Append the crate count to a loaded pallet's list label ("Palette, 2 crates"); empty pallets
    // keep their plain localized name (the "Empty pallets" category already conveys the state).
    private static string PalletLabel(WorldGameObject obj, string baseLabel)
    {
        int n = PalletCrateCount(obj);
        return n <= 0 ? baseLabel : Loc.Plural("nav.pallet_crates", n, baseLabel, n);
    }

    /// <summary>
    /// Every stage of a vegetable/grape bed: the plot marked out at the build desk
    /// (garden_empty_place), the prepared bed you plant into (garden_empty, garden_empty_stick),
    /// the growing crop (garden_wheat, garden_hop_growing, …) and the ripe one (garden_beet_ready).
    /// All of them carry the "garden_" prefix; a few ids merely share it and are NOT beds — the
    /// two build desks, the graveyard's stone-garden decoration and the garden totem. The vineyard's
    /// grape beds and the refugee camp's beds (Game Of Crone) are the same thing under their own
    /// ids; the camp's enclosure fence shares the camp prefix, so its beds are matched by the
    /// fuller "…garden_bed" prefix rather than "…garden". Orchards and berry patches
    /// (tree_apple_garden, bush_berry_garden), the bee garden and the zombie garden desk do not
    /// carry the prefix at all and keep their own categories.
    ///
    /// The VILLAGE FARM's fields are excluded (see IsInertGardenBed). They are the farmer's, not
    /// yours: garden_lentils_ready_village and its siblings are permanently ripe scenery, and
    /// listing them sent the beacon 8600 units across the map to a crop announced as "ready to
    /// harvest" that then ignored every keypress.
    /// </summary>
    private static bool IsGardenBed(WorldGameObject obj)
    {
        var id = obj?.obj_def?.id ?? obj?.obj_id;
        if (string.IsNullOrEmpty(id)) return false;

        bool isBed;
        if (id.Equals("garden", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("garden_", StringComparison.OrdinalIgnoreCase))
        {
            isBed = id.IndexOf("builddesk", StringComparison.OrdinalIgnoreCase) < 0 &&
                    id.IndexOf("of_stones", StringComparison.OrdinalIgnoreCase) < 0 &&
                    id.IndexOf("totem", StringComparison.OrdinalIgnoreCase) < 0;
        }
        else
        {
            isBed = id.StartsWith("vineyard_garden", StringComparison.OrdinalIgnoreCase) ||
                    id.StartsWith("refugee_camp_garden_bed", StringComparison.OrdinalIgnoreCase);
        }

        return isBed && !IsInertGardenBed(obj);
    }

    /// <summary>
    /// A garden bed that is pure decoration — the village farm's fields. Told apart structurally
    /// rather than by the "_village" id suffix, so any other scenery field is caught the same way:
    /// a real bed always offers at least ONE of the three things a bed can have — an E interaction
    /// (the prepared bed's planting craft), a tool action (dig the plot open, pull the ripe crop,
    /// clear a wrecked trellis), or a running craft (a crop growing towards ripe). The farmer's
    /// fields have none of the three: interaction None, no craft, no tool. Nothing the player does
    /// can work, enter or change them, so there is nothing to navigate to.
    /// </summary>
    private static bool IsInertGardenBed(WorldGameObject obj)
    {
        try
        {
            var def = obj?.obj_def;
            if (def == null) return false;
            if (def.interaction_type != ObjectDefinition.InteractionType.None) return false;
            if (def.has_craft) return false;
            var tools = def.tool_actions;
            return tools == null || tools.no_actions ||
                   tools.action_tools == null || tools.action_tools.Count == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// A garden bed's list label: its name plus which stage it is at, because the game names every
    /// stage of one crop the same ("Weizenbeet" whether it is two days old or ready to pull) and
    /// the whole point of one Beds category is being able to tell, from the list alone, which bed
    /// wants something from you. Stage is read structurally, not from a table of ids: the marked
    /// plot is the "_place" construction id, the ripe crop carries "ready", a bed with nothing in
    /// it is either an "empty" id or (the vineyard/camp beds) still offers its planting craft, and
    /// anything else is a crop still growing.
    /// </summary>
    private static string GardenBedLabel(WorldGameObject obj, string baseLabel)
    {
        try
        {
            var id = obj?.obj_def?.id ?? obj?.obj_id ?? "";

            // A trampled/destroyed trellis is neither growing nor plantable until it is cleared.
            if (id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0)
                return Loc.Fmt("nav.label_broken", baseLabel, BrokenWord());

            string stateKey;
            if (id.EndsWith("_place", StringComparison.OrdinalIgnoreCase))
                stateKey = "garden.state_marked";
            else if (id.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0)
                stateKey = "garden.state_ready";
            else if (id.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     obj?.obj_def?.interaction_type == ObjectDefinition.InteractionType.Craft)
                stateKey = "garden.state_empty";
            else
                stateKey = "garden.state_growing";

            return Loc.Fmt("nav.label_state", baseLabel, Loc.Get(stateKey));
        }
        catch { return baseLabel; }
    }

    /// <summary>
    /// A bed the player sleeps in — the house bed ("bed"), the starting bed, the mining-hut bed,
    /// the keeper's-room beds and the non-sleepable decorative copies. Excludes the corpse bed
    /// (a body container, so it belongs with storage), garden beds and flower beds, which only
    /// share the word.
    /// </summary>
    internal static bool IsSleepingBed(WorldGameObject obj)
    {
        var id = obj?.obj_def?.id ?? obj?.obj_id;
        if (string.IsNullOrEmpty(id)) return false;
        if (id.IndexOf("garden", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("flowerbed", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        // "bed" as a word of its own, so nothing that merely contains the letters can match.
        return id.Equals("bed", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith("bed_", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith("bed ", StringComparison.OrdinalIgnoreCase) ||
               id.EndsWith("_bed", StringComparison.OrdinalIgnoreCase) ||
               id.IndexOf("_bed_", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// <see cref="TryClassify"/> with its answer remembered per object.
    ///
    /// WHY: classification is by far the most expensive thing a destination rebuild does. It is a
    /// long ladder of case-insensitive obj_id substring tests, GameBalance lookups, craft-list
    /// walks and tool_action checks, and the rebuild runs it over every object within
    /// <see cref="MaxHarvestableNavDistance"/> — thousands of them outdoors — one to twelve times a
    /// second. Computing it once per object instead turns that into a dictionary lookup.
    ///
    /// SAFE because the verdict is a pure function of the object's obj_id and its definition, and
    /// the cache is keyed on the obj_id it was computed for: a change_wgo craft (a dug grave plot
    /// becoming a grave, a barrel becoming its smashed remains, a garden bed advancing a stage)
    /// swaps the obj_id on the same WorldGameObject, which invalidates the entry by itself. The two
    /// classifications that depend on the individual object's RUNTIME state rather than its id are
    /// excluded by <see cref="HasVolatileClass"/> and recomputed every time.
    /// </summary>
    private static bool TryClassifyCached(WorldGameObject obj, out NavCategory category)
    {
        string objId = null;
        try { objId = obj.obj_id; } catch { }

        if (string.IsNullOrEmpty(objId) || HasVolatileClass(objId))
            return TryClassify(obj, out category);

        if (WorldObjectRegistry.TryGetClassCode(obj, out int cached))
        {
            _classCacheHits++;
            category = (NavCategory)(cached & 0xFFFF);
            return (cached & ClassifiedBit) != 0;
        }

        bool found = TryClassify(obj, out category);
        WorldObjectRegistry.StoreClassCode(obj, ((int)category & 0xFFFF) | (found ? ClassifiedBit : 0));
        return found;
    }

    /// <summary>Marks a cached code as "TryClassify returned true", above the category bits.</summary>
    private const int ClassifiedBit = 1 << 16;

    /// <summary>
    /// obj_ids whose category can change while the obj_id does not, so their verdict must never be
    /// cached:
    ///
    ///   * a pallet flips between LoadedPallets and EmptyPallets as crates are put on and taken off
    ///     (<see cref="PalletCrateCount"/> reads the live inventory), and
    ///   * a zombie-mine fence counts as a mine part only while it carries a craft or a docked
    ///     worker (<see cref="IsZombieMinePart"/> reads <c>has_linked_worker</c>), and a fence is
    ///     also the one thing whose repairable state is read off its live craft list.
    ///
    /// Both are a handful of objects in a save, so recomputing them costs nothing.
    /// </summary>
    private static bool HasVolatileClass(string objId)
    {
        // Memoised per id. Two case-insensitive substring scans do not sound like much, but this
        // runs over every object in reach on every rebuild, and Mono's OrdinalIgnoreCase compare
        // folds case character by character — the very cost the classification cache exists to
        // remove. A few hundred distinct ids means this dictionary stops growing almost at once.
        if (_volatileClassById.TryGetValue(objId, out bool v)) return v;
        v = objId.IndexOf("pallet", StringComparison.OrdinalIgnoreCase) >= 0 ||
            objId.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0;
        _volatileClassById[objId] = v;
        return v;
    }

    private static readonly Dictionary<string, bool> _volatileClassById = new(StringComparer.Ordinal);
    private static int _classCacheHits;

    private static bool TryClassify(WorldGameObject obj, out NavCategory category)
    {
        category = NavCategory.Other;

        // The river throw spot is listed by hand, not by the scene scan: permanently in Landmarks
        // (ObjectLandmarks) and, while you are actually carrying a corpse, under Crafting stations
        // with the other places a body can be got rid of (AddCarriedBodyDisposal). Left to the scan
        // it also landed in Other, so standing near the bank offered the same spot three times.
        if (!string.IsNullOrEmpty(obj.obj_id) &&
            obj.obj_id.IndexOf("throw_body_river", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        // The dungeon exit (the portal back up to the cellar) is a WorldGameObject whose obj_id
        // contains "dungeon_exit" — the game's own door constant
        // (DungeonRoomInterior.DOORS_CONTAINS_THIS_WORDS). Its object NAME carries no "teleport"
        // token, so the check below would miss it; file it under Doors explicitly so a blind player
        // can always find and auto-walk back to the way out instead of dying to leave the level.
        if (!string.IsNullOrEmpty(obj.obj_id) &&
            obj.obj_id.IndexOf("dungeon_exit", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            category = NavCategory.Doors;
            return true;
        }

        // Dungeon mining veins (obj_id "dungeon_source_diamond"/_gold/_silver/…): the pickaxe-
        // mined crystal/metal formations. Their obj_def is script-driven and may carry no
        // standard tool_action, so the harvestable sort (which is gated on a tool_action) misses
        // them and they never appear in ANY list — confirmed in testing: Ctrl+M found them but the
        // Ores/Stones categories were empty. Intercept by obj_id HERE, before the interaction_type
        // switch — exactly like the dungeon exit above and fishing spots/vendors below — so
        // classification is independent of tool_actions/interaction_type. File under Ores (valuable
        // mining targets, a short list); GetObjectLabelSafe → DungeonSourceLabel names each by
        // resource. Confirmed ids from the Ctrl+M dump: dungeon_source_gold/silver/diamond.
        if (!string.IsNullOrEmpty(obj.obj_id) &&
            obj.obj_id.IndexOf("dungeon_source", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            category = NavCategory.Ores;
            return true;
        }

        // Zombie mines (Best Save Soul DLC): the placed mining operation a zombie works. One mine is
        // a cluster — a base building, one or two production benches (iron/stone), and for a marble/
        // granite quarry the FRONT-GATE fence that carries the production craft plus a ring of plain
        // enclosure-wall fences. They ALL localize to a generic "Zombiemine" and were scattered
        // across Crafting stations / Other / Built objects, so a blind player couldn't find a given
        // mine or tell its staffing spot from a wall. Give the acted-on parts a dedicated category
        // (IsZombieMinePart excludes the bare walls); GetObjectLabelSafe → MineLabel names each by
        // resource + staffing state. Intercepted here by obj_id, like the dungeon veins above.
        if (!string.IsNullOrEmpty(obj.obj_id) && IsZombieMinePart(obj))
        {
            category = NavCategory.ZombieMines;
            return true;
        }

        // Doors / zone exits are detected by name (the game has no explicit
        // teleport interaction_type). Skip the non-usable arrival anchors
        // (e.g. teleport_point, interaction_type None) — you can't walk through those,
        // they are only where you land, and listing them clutters the door list.
        // Via the registry rather than obj.name: Unity allocates a fresh string out of native code
        // on every name read, and this runs over every object in reach on every rebuild. The
        // verdict is cached per object for its lifetime (see WorldObjectRegistry.HasTeleportName).
        if (WorldObjectRegistry.HasTeleportName(obj))
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

        // Fishing spots: the water tiles you cast into. They have no dedicated interaction_type
        // (they run a FlowCanvas script on E and carry no craft), so they used to fall through to
        // Other. The definitive signal is a ReservoirsDefinition keyed by obj_id — the exact lookup
        // FishingGUI.Open does to load the spot's fish table. GetDataOrNull is an O(1) cached
        // dictionary hit, so this is cheap to check for every object.
        if (!string.IsNullOrEmpty(obj.obj_id) && IsFishingSpot(obj.obj_id))
        {
            category = NavCategory.FishingSpots;
            return true;
        }

        // Vendors: anything you can trade with (the traveling merchant, the egg seller's basket,
        // etc.). The definitive signal is a VendorDefinition keyed by obj_id — the exact lookup
        // WorldGameObject.vendor / Trading does to build the trade, so it matches the game exactly
        // and catches every vendor without hard-coding ids. Some vendors are NPCs and some are plain
        // script objects (the egg stall has no craft, so it used to fall through to Other); checked
        // BEFORE the People/NPC branch so a vendor NPC files under Vendors rather than being buried
        // among ordinary townsfolk. O(1) cached dictionary hit, guarded so a bad cache can't break
        // the pass.
        if (!string.IsNullOrEmpty(obj.obj_id) && IsVendor(obj.obj_id))
        {
            category = NavCategory.Vendors;
            return true;
        }

        // Enemies (mobs) get their own category, split from townsfolk: a blind player in a dungeon
        // wants to cycle enemies separately from People (and, above ground, keep wolves/monsters out
        // of the villager list). Checked before the NPC branch because IsMob is the more specific
        // signal. They still reveal across a whole dungeon level (that's driven by the isDungeonObj
        // reveal in RefreshDestinations, which is category-agnostic).
        try
        {
            if (def.IsMob() || def.type == ObjectDefinition.ObjType.Mob)
            {
                category = NavCategory.Enemies;
                return true;
            }
        }
        catch { }

        // People: townsfolk / NPCs.
        try
        {
            if (def.type == ObjectDefinition.ObjType.NPC || def.IsNPC())
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

        // A dug-out grave plot (grave_empty / grave_ground) — the hole left after digging a marked
        // plot open, which is exactly where the corpse you're carrying goes. It is a real grave but
        // carries NO Grave interaction (there's no grave menu until something is buried), so the
        // rule above passes it over and its demolish craft dropped it into the catch-all
        // Built-objects list, between beds and lamps. File it under Graves so the Empty-graves
        // mirror below can pick it up: after digging a grave the player needs to find it again
        // carrying a body, and that list is the one that answers "where can this corpse go".
        if (IsGravePlot(obj, def))
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

        // Garden beds — every stage of one, from the plot you just marked out to the crop that is
        // ready to pull. The game scatters them across three different buckets because each stage
        // looks like a different kind of object: the marked plot (garden_empty_place) is a shovel
        // node and landed in Gatherables, the prepared bed (garden_empty / garden_empty_stick) has
        // a Craft interaction — planting seeds — and landed among the crafting stations, the
        // growing crop has neither and was listed nowhere at all, and the ripe crop is a Hand node
        // and landed back in Gatherables. A blind farmer therefore had to hunt through three lists
        // to work one field. Collect them all in one category and let the label carry the stage
        // (see GardenBedLabel). Checked before the interaction_type switch so the planting Craft
        // can't claim the prepared bed first.
        if (IsGardenBed(obj))
        {
            category = NavCategory.GardenBeds;
            return true;
        }

        // Beds you sleep in. They are RunScript objects with no craft and (for the ones the game
        // placed rather than you) no removal craft, so the RunScript branch below dropped them into
        // the catch-all Other — the one list a player never browses — even though the branch's own
        // comment lists beds as built furniture. File them with the rest of the furniture.
        // See IsSleepingBed for what counts (garden beds and the corpse bed are not beds).
        if (IsSleepingBed(obj))
        {
            category = NavCategory.Buildables;
            return true;
        }

        // Shipping pallets (box_pallet) split into two navigable buckets by whether they hold
        // crates: LoadedPallets (has crates to grab with E) vs EmptyPallets (room to leave a crate
        // you're carrying). Checked before the interaction_type switch because a pallet is a
        // RunScript object with a removal craft and would otherwise fall into Buildables/Other.
        if (!string.IsNullOrEmpty(obj.obj_id) &&
            obj.obj_id.IndexOf("pallet", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            category = PalletCrateCount(obj) > 0 ? NavCategory.LoadedPallets : NavCategory.EmptyPallets;
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
                category = BuildPlacementHandler.HasRemovalCraft(obj) ? NavCategory.Buildables : NavCategory.Other;
                return true;
            default:
                // Smashable loot props (dungeon vases/pots, barrels/crates/urns): destructible
                // objects you break for loot. Decided here — BEFORE the harvestable sort — because a
                // barrel carries an Axe action and would otherwise be swept into Trees. Shared with
                // CombatAssist via IsBreakableLootProp, so what's listed is exactly what C/X can smash.
                if (IsBreakableLootProp(obj))
                {
                    category = NavCategory.Breakables;
                    return true;
                }

                // Tool-worked destructibles you TEAR DOWN with the Work key (F) for loot — dungeon
                // broken furniture/barrels (chair/bench/barrel *_broken) that keep an Axe/Pickaxe/
                // Shovel action + real drops (wood, planks). These have a "_broken" id and a loot
                // keyword, so the spent-scenery skip just below would wrongly hide them. Checked here
                // (before that skip and before the harvestable sort, which would call a broken chair
                // a "tree") so a blind player gets a dedicated Destructibles list to walk to.
                if (IsWorkedDestructible(obj, def))
                {
                    category = NavCategory.Destructibles;
                    return true;
                }

                // Story rubble you clear away with the HAMMER (tavern_broken_bottles /
                // warehouse_broken_barrels, the two halves of the village-cleanup task). Checked
                // here for the same reason as the block above: their "broken"/"barrel" ids make the
                // spent-scenery skip just below drop them outright, and a hammer action is rejected
                // by both IsWorkedDestructible and TryClassifyHarvestable — so they were reachable
                // from no category at all. See IsScriptedCleanupProp.
                if (IsScriptedCleanupProp(obj, def))
                {
                    category = NavCategory.Destructibles;
                    return true;
                }

                // The spent "..._broken" replacement left after a smash is inert scenery. Skip it
                // outright (don't let a broken barrel's leftover Axe action drop it into Trees below).
                if (IsSpentBrokenProp(obj) && HasLootPropKeyword(obj))
                {
                    category = NavCategory.Other;
                    return false;
                }

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
                if (BuildPlacementHandler.HasRemovalCraft(obj) || HasHammerBuildAction(def))
                {
                    category = NavCategory.Buildables;
                    return true;
                }

                // A container the game doesn't flag as a Chest — a rack/shelf that opens its
                // inventory from a script instead of the chest interaction — is still storage to
                // the player, so file it there rather than dropping it. Only in this fallthrough:
                // crafting stations carry an inventory too (their input/output buffer) and are
                // already classified as Stations well before here.
                if (def.inventory_size > 0)
                {
                    category = NavCategory.Storage;
                    return true;
                }

                // Interior furniture the build desk places through a FlowScript (the cupboard, the
                // improved cooking table…). It is spawned at a fixed room slot and has no
                // interaction, no tool action and no removal craft — you replace it rather than
                // demolish it — so every check above misses it and a piece the player had just
                // built was findable in NO category at all. Recognise it from the build crafts
                // themselves (see ScriptPlacedBuilds) and list it as a built object.
                if (IsScriptPlacedBuild(obj.obj_id))
                {
                    category = NavCategory.Buildables;
                    return true;
                }
                return false;
        }
    }

    /// <summary>
    /// True when an obj_id names a fishing spot — i.e. GameBalance holds a ReservoirsDefinition
    /// (the spot's fish table, keyed by obj_id) for it. This is the same lookup FishingGUI.Open
    /// uses to decide a spot is fishable, so it matches the game exactly. The lookup is an O(1)
    /// cached dictionary hit once GameBalance's cache is built (it is, in-game); wrapped in a
    /// try/catch so a missing cache/type can never break the whole classification pass.
    /// </summary>
    private static bool IsFishingSpot(string objId)
    {
        try
        {
            return GameBalance.me != null
                && GameBalance.me.GetDataOrNull<ReservoirsDefinition>(objId) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// True when an obj_id names something you can trade with — i.e. GameBalance holds a
    /// VendorDefinition (the vendor's stock/pricing table, keyed by obj_id) for it. This is the same
    /// lookup <c>WorldGameObject.vendor</c> and <c>Trading</c> use to build the trade, so it matches
    /// the game exactly and needs no hard-coded id list. O(1) cached dictionary hit once GameBalance's
    /// cache is built (it is, in-game); wrapped in a try/catch so a missing cache/type can never break
    /// the whole classification pass.
    /// </summary>
    private static bool IsVendor(string objId)
    {
        try
        {
            return GameBalance.me != null
                && GameBalance.me.GetDataOrNull<VendorDefinition>(objId) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Furniture the build desk places through a FlowScript instead of a floating ghost — the
    /// keeper's-room cupboard, the improved cooking table, and every other fixed-slot interior
    /// piece (BuildModeLogics.Mode.ScriptBuilding). Maps the obj_id that actually gets spawned
    /// to the id the build catalog names the piece by.
    ///
    /// Such a craft carries <c>wait_script_callback</c> and an <c>end_script</c> of the form
    /// "script:event:obj_id" (e.g. "keeper_cupboard_place:place:cupboard_home" — the exact
    /// script/event/param split BuildModeLogics does), and the script spawns that obj_id at the
    /// room slot. The spawned object is a dead end for the tracker: no interaction, no tool
    /// action, and no removal craft (you replace it rather than demolish it). It usually has no
    /// translation of its own either — the catalog entry is named after the craft's
    /// <c>out_obj</c> ("cupboard" → "Schrank") while the placed object is "cupboard_home" — so
    /// the value here is that out_obj, which <see cref="InteractionDetector.GetObjectLabel"/>
    /// uses to give the piece its real name.
    ///
    /// Built once from GameBalance (static game data) and cached.
    /// </summary>
    private static Dictionary<string, string> _scriptPlacedBuilds;

    private static Dictionary<string, string> ScriptPlacedBuilds
    {
        get
        {
            if (_scriptPlacedBuilds != null)
                return _scriptPlacedBuilds;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var crafts = GameBalance.me?.craft_obj_data;
                if (crafts == null)
                    return map;   // balance not loaded yet — retry on the next call

                foreach (var craft in crafts)
                {
                    if (craft == null || !craft.wait_script_callback) continue;
                    if (string.IsNullOrEmpty(craft.end_script)) continue;

                    var parts = craft.end_script.Split(':');
                    if (parts.Length < 3) continue;

                    var placedId = parts[2].Trim();
                    if (placedId.Length == 0 || map.ContainsKey(placedId)) continue;

                    map[placedId] = string.IsNullOrEmpty(craft.out_obj) ? placedId : craft.out_obj;
                }
            }
            catch { return map; }

            _scriptPlacedBuilds = map;
            return map;
        }
    }

    /// <summary>
    /// True when <paramref name="objId"/> names a piece of furniture placed by a build-desk
    /// script (see <see cref="ScriptPlacedBuilds"/>).
    /// </summary>
    internal static bool IsScriptPlacedBuild(string objId)
    {
        return !string.IsNullOrEmpty(objId) && ScriptPlacedBuilds.ContainsKey(objId);
    }

    /// <summary>
    /// The id the build catalog names a script-placed piece by (its craft's <c>out_obj</c>), or
    /// null when <paramref name="objId"/> isn't such a piece. See <see cref="ScriptPlacedBuilds"/>.
    /// </summary>
    internal static string ScriptPlacedBuildNameId(string objId)
    {
        if (string.IsNullOrEmpty(objId)) return null;
        return ScriptPlacedBuilds.TryGetValue(objId, out var nameId) ? nameId : null;
    }

    /// <summary>
    /// The scored WorldZone a world object geometrically sits in (the game's own
    /// <c>GetMyWorldZone</c>, a physics OverlapPoint on the zone layer), or null when it's in no
    /// zone or the object is missing/removed. Guarded so a malformed object can't break the refresh.
    /// </summary>
    // ---- Teleport-door variants ------------------------------------------
    //
    // The scene carries several copies of the same doorway (tp_church_a_ / tp_church_2_a_ /
    // tp_church_3_a_, and the same for the church's mortuary hatch) and the game runs exactly one of
    // them per save, switching the others off. They share a position, a label and an obj_id, so once
    // they're off-camera nothing on the object itself says which one teleports — the player just
    // sees the church door listed three times and two of them do nothing. These two remember what
    // was observed while a variant WAS on camera and active, which is proof it's the live one.
    private static readonly HashSet<string> _liveDoorTags = new(StringComparer.OrdinalIgnoreCase);
    // The variant number the live doorways of the current save carry ("2" in a save where
    // tp_church_2_a_ is the working church door). Interiors are switched as a set, so a variant seen
    // live on one doorway is the best guess for a doorway that has never been seen live at all.
    private static string _liveDoorVariant;

    private static void NoteLiveDoor(WorldGameObject obj)
    {
        try
        {
            var tag = obj?.custom_tag;
            if (string.IsNullOrEmpty(tag)) return;
            _liveDoorTags.Add(tag);
            var variant = DoorVariantNumber(tag);
            if (variant != null) _liveDoorVariant = variant;
        }
        catch { }
    }

    /// <summary>
    /// The family a teleport door belongs to: its tag with the variant number taken out, so
    /// "tp_church_a_", "tp_church_2_a_" and "tp_church_3_a_" all key to "church_a". The a/b end
    /// marker is KEPT — those are the two opposite ends of one teleport (inside vs outside), i.e.
    /// genuinely different doorways. Null for a door with no usable tag (dungeon exits), which is
    /// then left alone.
    /// </summary>
    private static string DoorVariantFamily(WorldGameObject obj)
    {
        try
        {
            var tag = (obj?.custom_tag ?? "").ToLowerInvariant().Trim();
            if (!tag.StartsWith("tp_")) return null;
            var parts = new List<string>();
            foreach (var part in tag.Substring(3).Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, out _)) continue;
                parts.Add(part);
            }
            return parts.Count == 0 ? null : string.Join("_", parts.ToArray());
        }
        catch { return null; }
    }

    /// <summary>The variant number in a door tag ("tp_church_2_a_" → "2"), or null when it has none.</summary>
    private static string DoorVariantNumber(string tag)
    {
        try
        {
            foreach (var part in (tag ?? "").ToLowerInvariant().Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out _)) return part;
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Keep one entry per doorway. Doors are grouped into variant families (see DoorVariantFamily)
    /// and each family is reduced to the copy that actually teleports, ranked by how strong the
    /// evidence is: active right now (the game's own answer) beats seen-active-earlier, which beats
    /// carrying the same variant number as the doorways that have been seen live, which beats
    /// nearest. When a family has several ACTIVE members they are all kept — never hide a door that
    /// is demonstrably working. Doors with no tag (dungeon exits) are passed through untouched.
    /// </summary>
    private static void DedupeDoorVariants()
    {
        try
        {
            var list = _byCategory[NavCategory.Doors];
            if (list.Count < 2) return;

            var families = new Dictionary<string, List<NavigationTarget>>();
            var keep = new List<NavigationTarget>();
            foreach (var t in list)
            {
                var family = DoorVariantFamily(t.Object);
                if (family == null) { keep.Add(t); continue; }
                if (!families.TryGetValue(family, out var members))
                    families[family] = members = new List<NavigationTarget>();
                members.Add(t);
            }

            foreach (var members in families.Values)
            {
                if (members.Count == 1) { keep.Add(members[0]); continue; }

                int best = 0;
                foreach (var m in members) best = Math.Max(best, DoorLiveScore(m));

                // Every active member survives — never hide a door that demonstrably works.
                if (best == 4)
                {
                    foreach (var m in members)
                        if (DoorLiveScore(m) == 4) keep.Add(m);
                    continue;
                }

                // Otherwise the family is all off-camera: keep the single best-evidenced copy,
                // nearest first among equals.
                int pick = -1;
                for (int i = 0; i < members.Count; i++)
                {
                    if (DoorLiveScore(members[i]) != best) continue;
                    if (pick < 0 || members[i].Distance < members[pick].Distance) pick = i;
                }
                if (pick >= 0) keep.Add(members[pick]);
            }

            keep.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            list.Clear();
            list.AddRange(keep);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error de-duplicating doors: {ex.Message}");
        }
    }

    /// <summary>How strong the evidence is that this door copy is the one the game runs (4 = best).</summary>
    private static int DoorLiveScore(NavigationTarget t)
    {
        try
        {
            var obj = t.Object;
            if (obj == null) return 0;
            if (obj.gameObject != null && obj.gameObject.activeInHierarchy) return 4;
            var tag = obj.custom_tag;
            if (!string.IsNullOrEmpty(tag) && _liveDoorTags.Contains(tag)) return 3;
            if (_liveDoorVariant != null && DoorVariantNumber(tag) == _liveDoorVariant) return 2;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>
    /// True when an object is deactivated by the CAMERA (the chunk culler switches the object itself
    /// off and leaves its parents alone) rather than by the game (which switches off a whole group,
    /// so an ancestor is inactive). Lets an off-screen door in the room still be listed while the
    /// scene's switched-off spare copies of that doorway stay hidden.
    /// </summary>
    private static bool IsCameraCulled(WorldGameObject obj)
    {
        try
        {
            var tf = obj?.transform?.parent;
            while (tf != null)
            {
                if (!tf.gameObject.activeSelf) return false;
                tf = tf.parent;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// True when a culled (off-camera, deactivated) object should still be listed because it is in
    /// the interior the player is standing in. Inside a building the no-x-ray rule drops every
    /// culled object, which leaves the tracker holding only what the camera happens to frame — so
    /// entering the church or the church cellar showed a fraction of the room and the player had to
    /// walk to a second spot to "see" the rest. This gives back the one thing a sighted player gets
    /// for free on stepping through a door: the whole room at once.
    ///
    /// The outdoors is still not x-rayed, because the object has to pass one of two tests:
    /// it is in the SAME WorldZone as the player (church, cellar, tavern... — the outdoor world and
    /// the neighbouring graveyard are different zones or none), or it belongs to no zone at all and
    /// sits within a room's width. GetMyWorldZone is an OverlapPoint on the zone layer, so it works
    /// on a deactivated object; it's a physics query, hence the distance gate first and the caller
    /// only asking about objects it is otherwise about to drop.
    /// </summary>
    private static bool IsInPlayerInterior(WorldGameObject obj, float distance,
                                           bool interiorSightBlocked, WorldZone playerZone)
    {
        if (!interiorSightBlocked || distance > InteriorRevealRadius) return false;
        if (playerZone == null) return distance <= InteriorRevealUnzonedRadius;
        var zone = SafeWorldZone(obj);
        return zone == playerZone || (zone == null && distance <= InteriorRevealUnzonedRadius);
    }

    private static WorldZone SafeWorldZone(WorldGameObject obj)
    {
        try
        {
            if (obj == null || obj.is_removed || obj.gameObject == null) return null;
            return obj.GetMyWorldZone();
        }
        catch { return null; }
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
            desc = Loc.Fmt("grave.worn_fence", Mathf.RoundToInt(Mathf.Clamp01(dur) * 100f));
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

            desc = Loc.Get((noFence && noCross) ? "grave.needs_both"
                 : noCross ? "grave.needs_cross"
                 : "grave.needs_fence");
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

    /// <summary>
    /// Categories whose entries are worth annotating with what they drop (see
    /// <see cref="ResourceYield"/>). These are the lists where the mod's family naming leaves every
    /// entry reading the same — a screen of "Tree", "Tree", "Tree" — and where the drop is the
    /// thing that decides whether to walk over and swing at it.
    ///
    /// Garden beds are deliberately out: <see cref="GardenBedLabel"/> already names the crop and
    /// the growth stage, so a yield clause would only say it a second time. Breakables stay in the
    /// list too but rarely qualify — a barrel answers to the sword, not to a harvest tool, and
    /// ResourceYield only speaks for the four harvest tools.
    /// </summary>
    private static bool IsYieldCategory(NavCategory category) =>
        category == NavCategory.Trees ||
        category == NavCategory.Stones ||
        category == NavCategory.Ores ||
        category == NavCategory.Bushes ||
        category == NavCategory.Flowers ||
        category == NavCategory.Mushrooms ||
        category == NavCategory.Beehives ||
        category == NavCategory.Gatherables ||
        category == NavCategory.Breakables ||
        category == NavCategory.Destructibles;

    private static bool IsHarvestableCategory(NavCategory category) =>
        category == NavCategory.Trees ||
        category == NavCategory.Stones ||
        category == NavCategory.Ores ||
        category == NavCategory.Bushes ||
        category == NavCategory.Flowers ||
        category == NavCategory.Mushrooms ||
        category == NavCategory.Beehives ||
        category == NavCategory.Gatherables ||
        // Garden beds are the same kind of thing: static outdoor nodes you walk to and work with
        // a tool (dig / plant / harvest), culled the moment they leave the screen. Without the
        // keep-while-culled reach a blind farmer could only find a bed already on screen.
        category == NavCategory.GardenBeds ||
        category == NavCategory.Breakables ||
        category == NavCategory.Destructibles;

    /// <summary>
    /// Classify a tool-worked / hand-gathered resource node into Trees, Stones, Ores, Bushes or
    /// the catch-all Gatherables. The game marks what tool a node needs in
    /// <c>obj_def.tool_actions.action_tools</c> (Axe = chop, Pickaxe = mine, Shovel = dig,
    /// Hand = gather); we lead with the obj_id keyword (bush/tree/stone) so a node that takes
    /// several tools (e.g. a tree you chop then dig the stump) still lands in the right bucket,
    /// then fall back to the tool. Pure-Hammer nodes (construction/repair) are not harvestables
    /// and are skipped. Returns false when the object isn't a resource node.
    /// </summary>
    /// <summary>
    /// A spent, already-smashed loot prop. When a vase/pot/barrel is destroyed the game runs
    /// ReplaceWithObject to swap it for a "..._broken" variant (e.g. dungeon_obj_vase02 →
    /// dungeon_obj_vase01_broken) — inert scenery with no interaction and no loot. Those broken defs
    /// still carry an hp formula + drop_items, so IsSmashableLootProp (and the keyword rule) would
    /// keep listing them under Breakables forever. Drop them by their "_broken" id so the tracker
    /// only shows props still worth smashing. (Repairable broken fences / morgue desks embed
    /// "broken" too but are classified earlier by their fence/craft interaction, so this never
    /// reaches them.)
    /// </summary>
    private static bool IsSpentBrokenProp(WorldGameObject obj) =>
        !string.IsNullOrEmpty(obj?.obj_id) &&
        obj.obj_id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// A tool-worked destructible loot prop the player TEARS DOWN with the Work key (F), not a
    /// combat smash (that's <see cref="IsBreakableLootProp"/> / the Breakables category, C/X).
    /// These are dungeon broken furniture and barrels — dungeon_obj_chair/bench/table_*_broken,
    /// barrelNN_broken — that keep an hp formula, a NON-sword tool_action (Axe/Pickaxe/Shovel:
    /// chop/mine/dig) and real drops (wood, planks...). The generic "_broken → spent scenery" rule
    /// (<see cref="IsSpentBrokenProp"/>) wrongly hides them because their id ends in "_broken" and
    /// they carry a loot-prop keyword, so a blind player could never find or clear them. Scoped by
    /// the loot-prop keyword so forest trees/stones/ore (no such keyword) never fall in here — those
    /// keep their own Trees/Stones/Ores buckets. The drops>0 + tool requirement also excludes truly
    /// inert broken scenery (vase01_broken: no tool, 0 drops) and worthless furniture with hp but no
    /// tool/loot (dungeon_obj_table/rack: can't even be F-worked).
    /// </summary>
    private static bool IsWorkedDestructible(WorldGameObject obj, ObjectDefinition def)
    {
        try
        {
            if (def == null || def.hp == null) return false;
            if (def.IsMob() || def.IsNPC() ||
                def.type == ObjectDefinition.ObjType.NPC ||
                def.type == ObjectDefinition.ObjType.Mob) return false;
            if (def.drop_items == null || def.drop_items.Count == 0) return false;
            if (!HasLootPropKeyword(obj)) return false;

            var tools = def.tool_actions;
            if (tools == null || tools.no_actions ||
                tools.action_tools == null || tools.action_tools.Count == 0) return false;
            // Must be a Work-key tool (chop/mine/dig/gather), not a Sword — a sword prop is a
            // combat smash and belongs in Breakables, handled before this by IsBreakableLootProp.
            var t = tools.action_tools[0];
            return t == ItemDefinition.ItemType.Axe ||
                   t == ItemDefinition.ItemType.Pickaxe ||
                   t == ItemDefinition.ItemType.Shovel ||
                   t == ItemDefinition.ItemType.Hand;
        }
        catch { return false; }
    }

    /// <summary>
    /// Story rubble the player clears away by HAMMERING it down — the broken bottles and the broken
    /// warehouse barrels a flowscript drops in front of the tavern for the village-cleanup task
    /// (dlc_souls_s40_1: "Mache vor dem Toten Pferd sauber"). Recognised structurally rather than by
    /// id: a destructible non-mob object worked with the Hammer whose destruction fires a script or
    /// craft (script_after_hp_0 / craft_after_hp_0 — that's the node that ticks the quest flag).
    ///
    /// They need a rule of their own because every generic bucket rejected them and they ended up
    /// listed nowhere: the tool is a HAMMER, which <see cref="IsWorkedDestructible"/> and
    /// <see cref="TryClassifyHarvestable"/> both exclude (a hammer means build/repair, not harvest);
    /// they carry no craft and no E-interaction, so the interaction_type switch passes them over;
    /// and their "broken"/"barrel" ids trip the spent-scenery skip, which dropped the barrels
    /// outright. Requiring the Hammer AND no harvest tool also keeps this from stealing trees or ore
    /// that happen to run a script when felled — those keep their own categories.
    /// </summary>
    private static bool IsScriptedCleanupProp(WorldGameObject obj, ObjectDefinition def)
    {
        try
        {
            if (def == null || def.hp == null) return false;
            if (def.IsMob() || def.IsNPC() ||
                def.type == ObjectDefinition.ObjType.NPC ||
                def.type == ObjectDefinition.ObjType.Mob) return false;

            // The destruction must DO something scripted — that's what separates quest rubble from
            // ordinary broken scenery left lying around after a smash.
            if (string.IsNullOrEmpty(def.script_after_hp_0) &&
                string.IsNullOrEmpty(def.craft_after_hp_0)) return false;

            var tools = def.tool_actions;
            if (tools == null || tools.no_actions) return false;
            if (!tools.HasToolK(ItemDefinition.ItemType.Hammer)) return false;
            return !tools.HasToolK(ItemDefinition.ItemType.Axe) &&
                   !tools.HasToolK(ItemDefinition.ItemType.Pickaxe) &&
                   !tools.HasToolK(ItemDefinition.ItemType.Shovel) &&
                   !tools.HasToolK(ItemDefinition.ItemType.Hand);
        }
        catch { return false; }
    }

    /// <summary>
    /// Same rule as <see cref="IsScriptedCleanupProp(WorldGameObject, ObjectDefinition)"/>, for
    /// callers that only hold the object — the proximity readout uses it to explain that F plus a
    /// hammer is what clears the thing, since these props answer to neither E nor an attack.
    /// </summary>
    internal static bool IsScriptedCleanupProp(WorldGameObject obj) =>
        IsScriptedCleanupProp(obj, obj?.obj_def);

    /// <summary>
    /// Obj_id keyword for an explicit smashable loot prop — barrels/crates/vases/urns and generic
    /// dungeon smashables. These may carry a tool_action (you can chop a barrel), which is what
    /// distinguishes them from a plain resource node: they're still loot props, not trees.
    /// </summary>
    private static bool HasLootPropKeyword(WorldGameObject obj)
    {
        var id = obj?.obj_id;
        if (string.IsNullOrEmpty(id)) return false;
        return id.IndexOf("dungeon_obj", StringComparison.OrdinalIgnoreCase) >= 0 ||
               id.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0 ||
               id.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0 ||
               id.IndexOf("vase", StringComparison.OrdinalIgnoreCase) >= 0 ||
               id.IndexOf("urn", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// A smashable loot prop: a destructible object (has an hp formula) that the player breaks for
    /// loot — dungeon vases/pots (smashed by attacking, no tool_action) and barrels/crates/urns
    /// (which may also carry a tool_action). This is the SINGLE source of truth shared by the nav
    /// tracker (Breakables category) and CombatAssist (what C/X can smash), so anything listed can
    /// actually be broken and vice-versa. Excludes: mobs/NPCs (enemies, not loot); the spent
    /// "..._broken" replacement left after a smash (inert scenery); and plain resource nodes
    /// (trees/stone/ore/bushes — tool-worked, no loot-prop keyword) so X never chops a tree.
    /// </summary>
    internal static bool IsBreakableLootProp(WorldGameObject obj)
    {
        try
        {
            var def = obj?.obj_def;
            if (def == null) return false;
            if (def.IsMob() || def.IsNPC() ||
                def.type == ObjectDefinition.ObjType.NPC ||
                def.type == ObjectDefinition.ObjType.Mob) return false;
            if (def.hp == null) return false;          // not destructible
            if (IsSpentBrokenProp(obj)) return false;  // already smashed
            // Must actually drop loot when broken. Excludes inert destructibles that give nothing
            // (e.g. dungeon_obj_table02: hpFormula but 0 drops) — pointless to list/smash.
            if (def.drop_items == null || def.drop_items.Count == 0) return false;

            // A named loot prop (barrel/vase/crate/urn/dungeon smashable) counts even if it carries
            // a tool_action — that's how it differs from a resource node.
            if (HasLootPropKeyword(obj)) return true;

            // Otherwise only an attack-smashed prop with no tool_action — this excludes tool-worked
            // resource nodes (trees/stone/ore) and random hp scenery.
            var tools = def.tool_actions;
            bool hasTool = tools != null && !tools.no_actions;
            return !hasTool;
        }
        catch
        {
            return false;
        }
    }

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
            // Note: smashable loot props (barrels/crates/vases/urns/dungeon smashables) are handled
            // by IsBreakableLootProp in TryClassify's default branch BEFORE this method is called, so
            // they never reach the Axe→Trees rule below.

            // A grave plot marked out at the graveyard build desk (grave_empty_place) is a shovel
            // node, not a resource: you dig it and it becomes a real empty grave (the game runs
            // ReplaceWithObject grave_empty_place → grave_empty). Because it carries no Grave
            // interaction yet, TryClassify's grave rules pass it over and the shovel action dropped
            // it into the catch-all Gatherables, buried among mushrooms and branches — so after
            // planning a grave the player had no way to find the spot they just marked. Give the
            // marked-but-undug plots their own bucket, next to the other grave lists. Checked first
            // so no later keyword rule can claim one.
            //
            // Matched on a grave-PREFIXED id plus the shovel, not a loose "grave" substring: the
            // graveyard's paving tiles are road_stone_small_graveyard_* (that substring plus the
            // "stone" keyword) and the enclosure is graveyard_fence_* / graveyard_gate — the prefix
            // rules the roads out, and requiring the dig/gather tool rules out anything you work
            // with an axe or pickaxe rather than dig open.
            if ((shovel || hand) && id.StartsWith("grave", StringComparison.OrdinalIgnoreCase))
            {
                category = NavCategory.DiggableGraves;
                return true;
            }
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
            // Bee hives sit ON trees, so their obj_id is a tree id with a "bees" suffix
            // (tree_3_2_bees while producing, tree_3_2_bees_done when honey is ready — confirmed
            // in-game) — that "tree" substring dumped them into the Trees bucket, burying the honey
            // producers among every plain tree. Also the standalone beehouse / refugee-camp hive.
            // They're harvested by whacking (Axe tool_action), so this must be checked BEFORE the
            // "tree"/Axe→Trees rule below. Gives the player a short list to walk to for honey/wax/bees.
            if (id.IndexOf("bees", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("beehouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("hive", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = NavCategory.Beehives;
                return true;
            }
            if (axe || id.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("stump", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = NavCategory.Trees;
                return true;
            }
            // (Dungeon mining veins "dungeon_source_*" are intercepted earlier in TryClassify by
            // obj_id, before this tool_action-gated path, so they never reach here.)
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
    private static string PlanningTableWord() => Loc.Get("nav.planning_table");

    /// <summary>
    /// Distinct, informative label for a part of a zombie mine cluster (base building, production
    /// bench, or enclosure fence), so the tracker doesn't read as a row of identical "Zombiemine"
    /// entries. Benches are named by the resource they produce (the iron-vs-stone tell) plus their
    /// staffing state; the empty bench is the one the player still needs to put a zombie on.
    /// </summary>
    private static string MineLabel(WorldGameObject obj)
    {
        string mine = Loc.Get("mine.name");
        string id = obj?.obj_id ?? "";
        try
        {
            bool worker = false;
            try { worker = obj.has_linked_worker; } catch { }
            bool hasCraft = false;
            try { hasCraft = obj?.obj_def != null && obj.obj_def.has_craft; } catch { }

            // Staffing / production node: the spot you press E on to attach a zombie and where the
            // mining craft runs. Iron/stone mines use a dedicated "..._bench" object; the marble/
            // granite mine instead uses its FRONT-GATE fence ("zombie_mine_fence_front"), which —
            // unlike the plain enclosure walls — carries the production craft. Detect either by
            // has_craft (or an already-linked worker) and name it by the resource it produces plus
            // whether a zombie is assigned, so the player finds the exact spot to staff and can tell
            // which mine makes what. Checked BEFORE the fence branch so the gate isn't read as a wall.
            if (hasCraft || worker || id.IndexOf("bench", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string product = MineBenchProduct(obj);
                string state = worker ? MineWord("working") : MineWord("empty");
                return string.IsNullOrEmpty(product)
                    ? Loc.Fmt("mine.bench_generic", mine, MineWord("bench"), state)
                    : Loc.Fmt("mine.bench_product", mine, product, state);
            }

            // Plain enclosure walls — mark them so they don't masquerade as the staffing gate/bench.
            if (id.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0)
                return Loc.Fmt("mine.fence", mine, MineWord("fence"));
        }
        catch { }

        // The building base (obj_id "mine_zombie") and anything else in the cluster.
        return mine;
    }

    /// <summary>Localized name of the resource a mine bench currently produces, or null if idle/unknown.</summary>
    private static string MineBenchProduct(WorldGameObject obj)
    {
        try
        {
            var craft = (obj?.obj_def != null && obj.obj_def.has_craft) ? obj.components?.craft : null;
            if (craft == null) return null;
            CraftDefinition cd = craft.current_craft;
            if (cd == null && craft.craft_queue != null && craft.craft_queue.Count > 0)
                cd = craft.craft_queue[0].craft;
            // Idle / unstaffed mine: nothing is actively running, so name it by its DEFINED
            // production recipe (the "zombie_mine_..._production" in the object's craft list) — that
            // way an empty gate still reads "Zombiemine: Marmor (frei)" instead of a generic bench.
            if (cd == null && craft.crafts != null && craft.crafts.Count > 0)
            {
                foreach (var c in craft.crafts)
                {
                    string cid = c?.id ?? "";
                    if (cid.IndexOf("production", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cid.IndexOf("zombie_mine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cid.IndexOf("mine_zombie", StringComparison.OrdinalIgnoreCase) >= 0)
                    { cd = c; break; }
                }
                if (cd == null) cd = craft.crafts[0];
            }
            if (cd == null) return null;
            var name = ScreenReader.StripNguiCodes(cd.GetFirstRealOutput()?.definition?.GetItemName() ?? "").Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    /// <summary>
    /// True if the object is a navigable part of a zombie mine cluster — the base building, a
    /// production bench, or the marble/granite quarry's front-gate staffing fence. The plain
    /// enclosure-wall fences (a "fence" id with neither a craft nor a linked worker) are excluded so
    /// the Zombie mines category stays to the parts the player actually walks to and staffs.
    /// </summary>
    private static bool IsZombieMinePart(WorldGameObject obj)
    {
        try
        {
            string id = obj?.obj_id ?? "";
            bool isMineId = id.IndexOf("mine_zombie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            id.IndexOf("zombie_mine", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isMineId && !IsZombieQuarryMine(obj)) return false;

            // Drop the bare enclosure walls (fence id, no craft, no worker) to keep the list tight.
            if (id.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool hasCraft = false;
                try { hasCraft = obj.obj_def != null && obj.obj_def.has_craft; } catch { }
                bool worker = false;
                try { worker = obj.has_linked_worker; } catch { }
                if (!hasCraft && !worker) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// True if a cliff-quarry node (obj_id "steep_marble"/"steep_granite"/…, also its worked
    /// "..._2" stages) has been turned into a zombie-operated mine — i.e. it runs a
    /// "zombie_mine_..._production" craft or has a zombie linked to it. A plain quarry the player
    /// mines by hand has neither, so it keeps its normal "Marmorsteinbruch" crafting-station label
    /// and is NOT relabelled as a zombie mine.
    /// </summary>
    private static bool IsZombieQuarryMine(WorldGameObject obj)
    {
        try
        {
            string id = obj?.obj_id ?? "";
            if (id.IndexOf("steep_", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (MineActiveCraftId(obj).IndexOf("zombie_mine", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            try { if (obj.has_linked_worker) return true; } catch { }
        }
        catch { }
        return false;
    }

    /// <summary>Id of the craft currently running / first queued on a mine node, or "" if none.</summary>
    private static string MineActiveCraftId(WorldGameObject obj)
    {
        try
        {
            var craft = (obj?.obj_def != null && obj.obj_def.has_craft) ? obj.components?.craft : null;
            if (craft == null) return "";
            var cd = craft.current_craft;
            if (cd == null && craft.craft_queue != null && craft.craft_queue.Count > 0)
                cd = craft.craft_queue[0].craft;
            return cd?.id ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Localized descriptor words used in mine labels.</summary>
    private static string MineWord(string which) => Loc.Get("mine.word." + which);

    /// <summary>
    /// Distinct label for a dungeon mining vein (obj_id "dungeon_source_&lt;resource&gt;", e.g.
    /// dungeon_source_diamond) — the crystal/metal formation you break with the pickaxe. The game
    /// localizes these to a generic rock name, so we name the resource (localized where known) plus
    /// a "vein"/"Ader" descriptor. Resource matched by keyword so id variants (…_2 etc.) still work.
    /// </summary>
    private static string DungeonSourceLabel(string objId)
    {
        string res;
        if (Has(objId, "diamond")) res = Loc.Get("resource.diamond");
        else if (Has(objId, "gold")) res = Loc.Get("resource.gold");
        else if (Has(objId, "silver")) res = Loc.Get("resource.silver");
        else if (Has(objId, "iron")) res = Loc.Get("resource.iron");
        else if (Has(objId, "marble")) res = Loc.Get("resource.marble");
        else if (Has(objId, "granite")) res = Loc.Get("resource.granite");
        else if (Has(objId, "stone")) res = Loc.Get("resource.stone");
        else
        {
            // Unknown resource: fall back to the raw suffix after "dungeon_source_", capitalized,
            // so an unmapped vein still reads distinctly (and the Ctrl+M dump surfaces its id).
            const string prefix = "dungeon_source_";
            int p = objId.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            string suffix = p >= 0 ? objId.Substring(p + prefix.Length) : objId;
            res = suffix.Length > 0 ? char.ToUpperInvariant(suffix[0]) + suffix.Substring(1) : objId;
        }

        // German compounds naturally ("Diamant-Ader"); other locales read "<resource> vein". Which
        // it is comes from the lang file's own "nav.vein" pattern, not a hard-coded locale check.
        return Loc.Fmt("nav.vein", res);

        static bool Has(string s, string kw) => s.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Localized "broken, repair it" note appended to a broken build desk's navigator label so the
    /// player knows it can't be used to build yet. The actual repair materials are read out by the
    /// proximity/E repair readout (InteractionDetector.WithRepairInfo) when the player reaches it.
    /// </summary>
    private static string BrokenWord() => Loc.Get("nav.broken_repair_it");

    /// <summary>
    /// <see cref="GetObjectLabelSafe"/> with its answer remembered per object.
    ///
    /// WHY: naming was, after the classification cache landed, the single most expensive thing a
    /// destination rebuild did — 38ms of a 55ms rebuild, about 27µs each across ~1800 objects, twice
    /// a second, forever. Like classification it is a long ladder of case-insensitive obj_id
    /// matching plus localisation lookups, and for almost every object in the world the answer is
    /// the same every time.
    ///
    /// WHAT IS NOT CACHED, and this is the whole safety of it: a name that can change while the
    /// object stays the same object. Getting one of those wrong does not make the mod slow, it makes
    /// it SAY something false to somebody who cannot check it against the screen — telling a player
    /// a grave is empty when there is a body in it. See <see cref="HasVolatileLabel"/>.
    ///
    /// Cached per OBJECT, never per obj_id: a door is named after where it leads, and that comes
    /// from its own custom_tag (InteractionDetector.GetDoorLabel), so two doors sharing an obj_id
    /// can legitimately have different names. The registry also drops the entry if the object's
    /// obj_id changes underneath it, which is how a barrel becoming its smashed remains re-names
    /// itself, and every name is dropped if the player switches language.
    ///
    /// The live extras the rebuild appends AFTER this — a pallet's crate count, a garden bed's
    /// growth stage, a worker zombie's efficiency, the "wants to talk" marker — are applied outside
    /// the cache and so stay live for free.
    /// </summary>
    private static string GetObjectLabelCached(WorldGameObject obj)
    {
        string objId = null, defId = null;
        try { objId = obj?.obj_id; defId = obj?.obj_def?.id; } catch { }

        // Both ids, because the grave-stage branch reads `obj_def?.id ?? obj_id` and the two are
        // only USUALLY the same string. Testing one of them would leave a grave that answers to the
        // other permanently stuck on whichever wording it had the first time it was named.
        if (string.IsNullOrEmpty(objId) || HasVolatileLabel(objId))
            return GetObjectLabelSafe(obj);
        if (defId != null && !string.Equals(defId, objId, StringComparison.Ordinal) &&
            HasVolatileLabel(defId))
            return GetObjectLabelSafe(obj);

        if (WorldObjectRegistry.TryGetLabel(obj, out var cached))
        {
            _labelCacheHits++;
            return cached;
        }

        var label = GetObjectLabelSafe(obj);
        WorldObjectRegistry.StoreLabel(obj, label);
        return label;
    }

    /// <summary>
    /// obj_ids whose spoken name is read off the object's LIVE state, so it must be built fresh
    /// every time. There are exactly two such branches in <see cref="GetObjectLabelSafe"/>, and both
    /// were found by reading it rather than guessed at:
    ///
    ///   * <c>grave_empty</c> / <c>grave_ground</c> — the name is "Empty grave" or "Grave with a
    ///     body" depending on <see cref="HoldsBody"/>, and burying someone does not change the id.
    ///   * zombie mines — <see cref="MineLabel"/> names the mine by its resource AND its staffing.
    ///     That includes any <c>steep_*</c> id, because <see cref="IsZombieQuarryMine"/> decides
    ///     whether a cliff face is a quarry by reading its active craft and <c>has_linked_worker</c>,
    ///     so an ordinary cliff becomes a mine the moment a zombie is docked on it.
    ///
    /// Everything else in that function reads the obj_id, the definition or the custom_tag, none of
    /// which change without the object becoming a different object.
    /// </summary>
    private static bool HasVolatileLabel(string objId)
    {
        if (_volatileLabelById.TryGetValue(objId, out bool v)) return v;
        v = objId == "grave_empty" || objId == "grave_ground" ||
            objId.IndexOf("steep_", StringComparison.OrdinalIgnoreCase) >= 0 ||
            objId.IndexOf("mine_zombie", StringComparison.OrdinalIgnoreCase) >= 0 ||
            objId.IndexOf("zombie_mine", StringComparison.OrdinalIgnoreCase) >= 0;
        _volatileLabelById[objId] = v;
        return v;
    }

    private static readonly Dictionary<string, bool> _volatileLabelById = new(StringComparer.Ordinal);
    private static int _labelCacheHits;

    private static string GetObjectLabelSafe(WorldGameObject obj)
    {
        try
        {
            // The dungeon exit localizes to a raw id / tileset name; give it a clear, recognizable
            // label so the Doors entry reads as the way out (it's classified Doors in TryClassify).
            // A level has TWO of these and they must NOT read identically: DungeonRoomInterior names
            // the first-room exit "dungeon_exit" (the portal back UP to the surface / the way you
            // came in) and the deeper-room exit "dungeon_exit2" (the stairs DOWN to the next level,
            // gated by clearing the level and — deeper down — Snake's key). When both said just
            // "Dungeon exit" a blind player could not tell the way out from the way deeper and got
            // stuck pressing the locked downward one.
            if (obj != null && !string.IsNullOrEmpty(obj.obj_id) &&
                obj.obj_id.IndexOf("dungeon_exit", StringComparison.OrdinalIgnoreCase) >= 0)
                return obj.obj_id.IndexOf("dungeon_exit2", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Loc.Get("door.dungeon_stairs_down")
                    : Loc.Get("door.dungeon_exit");

            // Dungeon mining veins (obj_id "dungeon_source_diamond"/_gold/…): the crystal/metal
            // formations broken with the pickaxe. They localize to a generic rock name, so give
            // each a distinct label naming its resource (see DungeonSourceLabel). Classified Ores.
            if (obj != null && !string.IsNullOrEmpty(obj.obj_id) &&
                obj.obj_id.IndexOf("dungeon_source", StringComparison.OrdinalIgnoreCase) >= 0)
                return DungeonSourceLabel(obj.obj_id);

            // The broken morgue's throw-in (obj_id "morgue_throw_in_broken") localizes to
            // "Leiche hineinwerfen" (Throw body in) — identical to the river-disposal the Yorick
            // quest needs, but it only opens an unusable craft window; the real spot is the
            // separate "throw_body_river" object. Relabel so the player isn't lured here. Only
            // the BROKEN one — a repaired morgue throw-in is a legitimate disposal station.
            if (obj != null && !string.IsNullOrEmpty(obj.obj_id) &&
                obj.obj_id.IndexOf("morgue_throw", StringComparison.OrdinalIgnoreCase) >= 0 &&
                obj.obj_id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Loc.Get("nav.broken_morgue");
            }

            // ...and the real one. "throw_body_river" has no translation in any language, so it read
            // as the prettified id ("Throw body river"), which says nothing about what it is for.
            // Same label as its Landmarks entry (see ObjectLandmarks), so the two agree wherever the
            // player meets it.
            if (obj != null && !string.IsNullOrEmpty(obj.obj_id) &&
                obj.obj_id.IndexOf("throw_body_river", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Loc.Get("landmark.river_body_throw");
            }

            // A placed zombie mine is a cluster of objects that ALL localize to the generic
            // "Zombiemine": the building base (obj_id "mine_zombie"), two production benches
            // ("mine_zombie_bench"), and the enclosure fences ("zombie_mine_fence*"). In the
            // tracker that reads as a wall of identical "Zombiemine" entries, so a blind player
            // can't tell the building from a fence, can't find the empty bench to staff, and —
            // since two mines (e.g. a stone and an iron one) share the same obj_ids — can't tell
            // which mine produces what. Give each part a distinct, informative label; benches are
            // named by the resource they produce (localized, from the running craft) plus whether
            // a zombie is assigned. That resource name is the reliable iron-vs-stone tell.
            // Iron/stone mines carry the "mine_zombie"/"zombie_mine" id outright. The marble/granite
            // zombie mine instead reuses the "steep_..." cliff-quarry node (see IsZombieQuarryMine),
            // which otherwise reads as a generic "Marmorsteinbruch" crafting station indistinguishable
            // from a hand-mined quarry — so it never grouped with the other zombie mines.
            if (obj != null && !string.IsNullOrEmpty(obj.obj_id) &&
                (obj.obj_id.IndexOf("mine_zombie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 obj.obj_id.IndexOf("zombie_mine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 IsZombieQuarryMine(obj)))
            {
                return MineLabel(obj);
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
                    : Loc.Fmt("nav.planning_table_zone", planning, zoneName);
                bool broken = !string.IsNullOrEmpty(obj.obj_id) &&
                              obj.obj_id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0;
                return broken ? Loc.Fmt("nav.label_broken", label, BrokenWord()) : label;
            }

            // Story rubble cleared with the hammer (see IsScriptedCleanupProp). Nothing in the name
            // says how to get rid of it, and E — the thing a player tries first — does nothing here,
            // so spell out the tool right in the tracker entry. (The name itself comes from
            // InteractionDetector.UntranslatedObjectNames; the game translates neither id.)
            if (IsScriptedCleanupProp(obj))
                return Loc.Fmt("nav.clear_with_hammer", InteractionDetector.GetObjectLabel(obj));

            // Smashable loot props (Breakables) are the one category whose action is an ATTACK, not
            // E and not F: their tool_action is the Sword, and HPActionComponent deliberately shows
            // no work bubble for a Sword action. So the entry reads like an ordinary object that
            // simply refuses to respond — the exact complaint raised about the barrels left behind
            // once the tavern cleanup swapped them back to plain "Fass". Say what breaks them.
            // Guarded on interaction_type None because that's the only branch of TryClassify that
            // can reach Breakables — a chest whose id merely contains "crate" is filed under Storage
            // and must not be told to attack it.
            if (obj?.obj_def != null &&
                obj.obj_def.interaction_type == ObjectDefinition.InteractionType.None &&
                IsBreakableLootProp(obj))
                return Loc.Fmt("nav.attack_to_smash", InteractionDetector.GetObjectLabel(obj));

            // The three stages of a self-built grave share one id family, and the generic grave
            // relabel below would read them out as "Grave grave empty place" / "Grave grave empty"
            // — the raw id with a word bolted on, which says nothing about which stage it is or
            // what to do there. Name the stage instead. The dug-out plot borrows the game's OWN
            // header for it (grave_empty_hdr / grave_body_hdr, what the HUD shows when you stand at
            // one), so it stays localized; the marked plot has no such string, so it falls back to
            // plain wording plus the tool, the way the hammer/attack hints above do.
            var graveStageId = obj?.obj_def?.id ?? obj?.obj_id;
            if (graveStageId == "grave_empty_place")
            {
                return InteractionDetector.HasTranslation(graveStageId)
                    ? Loc.Fmt("grave.dig_it_out", InteractionDetector.LocalizedObjectName(graveStageId))
                    : Loc.Get("grave.marked_plot");
            }
            if (graveStageId == "grave_empty" || graveStageId == "grave_ground")
            {
                var hdr = HoldsBody(obj) ? "grave_body_hdr" : "grave_empty_hdr";
                if (InteractionDetector.HasTranslation(hdr))
                    return InteractionDetector.LocalizedObjectName(hdr);
                return Loc.Get(HoldsBody(obj) ? "grave.with_body" : "grave.empty");
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
                return Loc.Fmt("grave.generic", cleanId.Trim());
            }

            return InteractionDetector.GetObjectLabel(obj);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NAVIGATOR] Failed to get label for object {obj?.name}: {ex.Message}");
            return Loc.Get("common.unknown_object");
        }
    }
}
