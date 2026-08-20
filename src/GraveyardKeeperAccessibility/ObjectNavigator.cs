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

            // A near-walk failed and we forced a navmesh rescan around the target — re-issue the
            // walk once the queued graph update has processed (a few frames later). If it fails again
            // _rescanRetried is already set, so it falls through to the graph-0 escalation below.
            if (_rescanRetryPending)
            {
                if (--_rescanRetryFramesLeft <= 0)
                {
                    _rescanRetryPending = false;
                    _log?.LogInfo($"[NAVIGATOR] Retrying walk to {_rescanRetryTarget.Label} after navmesh rescan");
                    WalkToTarget(_rescanRetryTarget);
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
            // Run a queued straight-line fallback (A* couldn't find a path).
            else if (_fallbackPending)
            {
                _fallbackPending = false;
                StartWalk(_fallbackDest, _fallbackLabel, MovementComponent.GoToMethod.Direct);
            }

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
    }

    internal static void SelectPrevious()
    {
        EnsureFreshList();

        var list = CurrentList;
        if (list.Count == 0) { EnsureNonEmptyCategory(); return; }

        _selectedIndex = (_selectedIndex - 1 + list.Count) % list.Count;
        AnnounceSelected();
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
        _rescanRetryPending = false;
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
        _rescanRetryPending = false;
        ClearArrivedTarget();
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
            ScreenReader.Say(Loc.Fmt("nav.walking_to", target.Label, DistanceText(target.Distance)), interrupt: true);
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
        ScreenReader.Say(Loc.Fmt("nav.walking_to_dir", target.Label, DirectionTo(target), DistanceText(Vector2.Distance(pp, target.Position))), interrupt: true);
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
            _longWalkActive = false;
            var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
            var remaining = Vector2.Distance(playerPos, target.Position);
            _log?.LogInfo($"[NAVIGATOR] Native walk ended {remaining:F0}u from {target.Label}");

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

        // Graph-0 can't reach the target. For a NEARBY target this is the tell-tale of a building
        // interior — the house/mortuary sit on a graph-0 island disconnected from the outdoor
        // navmesh, and the player graph frequently has no node near the object either (e.g. the
        // bed inside the house: "No walkable player-graph node near target"). Rather than dumping a
        // blind player onto the manual compass beacon, glide there in a straight line: during a
        // scripted walk the body is Kinematic (control disabled), so it slides to the spot without
        // jamming on the walls, and inside a single room the line to the target is clear. Bounded to
        // short hops — a FAR graph-0 failure would try to glide across the whole map through walls,
        // so that still beacons. An outdoor target behind a fence never reaches here (graph-0 routes
        // it through the gate), so this doesn't clip through outdoor fences.
        var pl = MainGame.me?.player;
        if (pl != null && Vector2.Distance(pl.pos, _longWalkTarget.Position) <= LongWalkStartDistance)
        {
            var target = _longWalkTarget;
            _longWalkActive = false;
            _log?.LogInfo($"[NAVIGATOR] Graph-0 unreachable but {target.Label} is near; direct glide fallback");
            DirectGlideTo(target);
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
    private static void DirectGlideTo(NavigationTarget target)
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
        // No fresh "Walking to…" — StartLongWalk already announced this walk; a second would double up.
        StartWalk(dest, target.Label, MovementComponent.GoToMethod.Direct);
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

        ScreenReader.Say(Loc.Fmt("nav.manual_guidance", target.Label), interrupt: true);
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
        if (_longWalkActive) StopLongWalk(announce: true);
        else if (_beaconActive) StopBeacon();
        else if (_isWalking) StopWalking();
    }

    /// <summary>
    /// Clear stale per-walk recovery state on a scene change / save-load / day change. Sleeping and
    /// loading don't teleport or reload navmesh, so without this the "A* already failed" guard and
    /// the one-shot rescan retry could linger into the new session. Cheap insurance — the reactive
    /// rescan path self-heals anyway. Called from Plugin.Update's scene-change branch.
    /// </summary>
    internal static void ResetNavStateOnSceneChange()
    {
        _astarFailedForWalk = false;
        _rescanRetried = false;
        _rescanRetryPending = false;
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
            _rescanRetried = false;
            _rescanRetryPending = false;
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
        // Stamped before the work, not after: an early return (no player yet, empty scene) still
        // counts as "this frame's rebuild attempt", so an on-demand call can't loop it per key.
        _lastRefreshFrame = Time.frameCount;
        _updateCounter = 0;

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
            WorldObjectRegistry.Snapshot(_scanBuffer);
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

                var label = GetObjectLabelSafe(obj);
                if (category == NavCategory.LoadedPallets || category == NavCategory.EmptyPallets)
                    label = PalletLabel(obj, label);
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

            // Collapse the scene's duplicate copies of a doorway down to the one that works.
            DedupeDoorVariants();

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

            NoteRefreshSettled();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[NAVIGATOR] Error refreshing destinations: {ex.Message}");
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

    private static bool TryClassify(WorldGameObject obj, out NavCategory category)
    {
        category = NavCategory.Other;

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

    private static bool IsHarvestableCategory(NavCategory category) =>
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
