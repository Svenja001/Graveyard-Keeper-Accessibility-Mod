namespace GraveyardKeeperAccessibility;

/// <summary>
/// Turn-by-turn walking directions for a player who wants to walk the route THEMSELVES
/// (Ctrl+B), instead of being auto-walked there by <see cref="ObjectNavigator"/>.
///
/// The compass beacon it replaces was a straight-line bearing to the goal: through fences,
/// through the church wall, re-spoken every six tiles. This follows the same obstacle-aware
/// graph-0 route the auto-walker drives, spoken one instruction at a time:
///
///     "Walk 12 meters east, then north."   ... player holds D ...
///     "Now 9 meters north."                ... player holds W ...
///     "Arrived at the church door."
///
/// It NEVER moves the player. Ctrl+B is a toggle for guidance and nothing else — no auto-walk
/// under any failure, which is what <see cref="_allowBeacon"/> guards: only the auto-walker's own
/// recovery path (BeaconBail, where the player DID ask to be walked) may fall through to the
/// beacon, because the beacon hands off to A* as soon as the player gets near.
///
/// Every leg is one of the four cardinals and nothing else, because that is a single held key.
/// Diagonals are never spoken: vertical movement runs at 0.8x horizontal speed, so holding W+D
/// does not travel at 45 degrees, and a player who cannot walk a diagonal cannot act on one either.
/// Slanting stretches become a staircase.
///
/// How a leg is chosen: by FOLLOWING the route, not by re-deriving it. The route — from the game's
/// own pathfinder, the same one auto-walk drives — is boiled down to the corners worth speaking
/// about, and each corner is reached by one held key, or by an L of two when it lies diagonally.
/// An L is only offered when both of its arms are clear, so it is a shortcut that works rather than
/// a guess across ground that happens to contain a fence; where neither L is clear, a staircase
/// hugging the direct line does the same job in shorter alternating steps.
///
/// Earlier versions scored candidate directions by "how much route is left from there" and picked
/// the best. That re-derived the path badly: standing in a fence corner with the route running
/// along the far side of the fence, every direction scored the same and it shuffled about proposing
/// one-metre steps. Following the route's own corners cannot do that — the corners are on the path,
/// and the path is walkable by construction.
///
/// Every leg is capped by what the COLLIDERS allow (see <see cref="PhysicsClearDistance"/>): the
/// navigation graphs are a model, and a model with 76-unit squares does not know your fence is
/// there. Graphs choose the direction; physics decides how far.
/// </summary>
internal static class GuidedWalk
{
    private static ManualLogSource _log;
    private static bool _initialized;

    // World units per tile, as everywhere else in the mod. Spoken distances are tiles ("meters").
    private const float TileSize = 96f;

    // ---- tuning -------------------------------------------------------------------------
    // Reaching the current leg's end (measured ALONG the leg axis, so sideways error still counts
    // as "there") and reaching the destination itself.
    private const float ArriveTolerance = 0.9f * TileSize;
    private const float GoalTolerance = 1.4f * TileSize;
    // How close to the OUTLINE of the target counts as arrived (see DistanceToObjectEdge).
    private const float EdgeArriveTolerance = 1.1f * TileSize;

    // Instructions are spoken, and speech takes time: a player is still holding the PREVIOUS
    // direction while they listen to the next one. At full walking speed that is several tiles of
    // honest, unavoidable drift, so nothing is judged off course until the instruction has had
    // time to land, and the threshold itself is generous.
    private const float SettleSeconds = 2f;
    private const float OffCourseDistance = 3f * TileSize;
    private const float CorrectionCooldown = 4f;
    // How close together an identical instruction has to come round again to count as a stutter.
    private const float RepeatSuppressSeconds = 3f;

    // How far a corner may sit off the straight line before it counts as a corner of its own, and
    // how far a staircase step may stray from the line it is following.
    private const float CornerEpsilon = 0.6f * TileSize;
    // How near counts as having REACHED a corner, and how far off its approach line you may be and
    // still count as having walked past it. Both deliberately tight: see AdvanceCorner.
    private const float CornerReachedTolerance = 0.45f * TileSize;
    private const float CornerPassedLateral = 1.5f * TileSize;
    // The shortest step worth speaking, when the step is what carries the player through a corner.
    // Everywhere else the floor stays a full tile: nobody wants a metre-by-metre commentary, but
    // "walk 1 metre north" is exactly right when a metre is what stands between you and the gate.
    private const float MinNudgeLength = 0.45f * TileSize;
    private const float StairDeviation = 2f * TileSize;
    // How far to move to get out of a pocket when nothing on the route can be reached.
    private const float BackOutLength = 4f * TileSize;
    // Legs. Kept short enough at the bottom end to handle "it is just there, three metres away" —
    // a floor of a tile and a half used to make every short walk unguidable.
    private const float MinLegLength = 1f * TileSize;
    private const float MaxLegLength = 20f * TileSize;
    private const float MinGain = 0.5f * TileSize;        // route progress a leg must actually buy
    private const float StepSize = 0.5f * TileSize;       // sampling stride; graph-0 nodes are ~0.8 tiles
    // Ground right under the player can read as unwalkable (stood against a wall, or in an eroded
    // corner), so sampling tolerates that for the first couple of tiles.
    private const float StartGrace = 2f * TileSize;
    // How much worse the score may get before we stop extending a leg (small dips happen where the
    // route bends away and back).
    private const float ScoreBacktrack = 1.5f * TileSize;
    // How far off the route a step may stray and still count as "on the path". The route is the
    // one thing here that is KNOWN walkable — the auto-walker drives it — so hugging it is more
    // reliable than testing straight lines of our own against the navmesh. Kept under a tile so a
    // corridor between two graves is followed rather than cut across.
    private const float Corridor = 0.9f * TileSize;
    // ...but where the world mesh actively says "solid", the route only gets to overrule it within
    // a hair's breadth — its own width. A fence with the route running along the far side of it is
    // comfortably inside the full corridor, and that is exactly how a step east into a fence kept
    // being offered while the player scraped along it.
    private const float CorridorOverSolid = 0.4f * TileSize;
    // Last-resort aim point distance along the route, when the ground data says nothing at all,
    // and how far a step is allowed to be when even that aim looks solid in both axes.
    private const float DeadReckonAhead = 4f * TileSize;
    private const float DeadReckonBlindLength = 2f * TileSize;

    // Walking straight INTO something does not slide the body along it, it simply stops — so the
    // "moved but got no closer" test below never fires and the player is left pushing at a wall in
    // silence. Walking state with no movement at all is the clearest signal there is: it needs no
    // distance to accumulate, it cannot be a false alarm from speech lag, and so it runs on a short
    // fuse and is checked even inside the settle window.
    private const float WallSeconds = 1.4f;
    // How long to wait before acting when the player is not moving but nothing can be found in
    // their way — long enough that a pause, a glance at the inventory or a moment of thought does
    // not produce an announcement.
    private const float SoftStuckSeconds = 3f;
    private const float WallMoveEpsilon = 0.25f * TileSize;

    // "Holding a key but not getting closer" — scraping along something the navmesh thinks is open.
    private const float BlockedSeconds = 1.5f;
    private const float BlockedMoveDistance = 0.75f * TileSize;
    private const float BlockedProgress = 0.3f * TileSize;
    // A direction that just failed is not offered again near that spot for a while. Deliberately
    // small: the usual obstruction is a prop you step around in a metre or two, and the bench must
    // stop applying once the sidestep below has cleared it, or the way on stays forbidden.
    private const float BlockedMemorySeconds = 10f;
    // Once the mod has said the way is blocked and handed out something else, that instruction gets
    // a hearing before the next one replaces it. Without this the player is still turning round
    // when the plan changes under them, which reads as the mod flailing rather than helping.
    private const float ObstructionCooldown = 3f;
    private const float BlockedMemoryRadius = 1.5f * TileSize;
    // Sidestepping an obstruction. The offset GROWS until the blocked direction actually opens up
    // again: a fixed two metres is fine for a barrel and useless against a fence, and guessing
    // short produced a loop of sidestep-blocked-sidestep along the whole length of one.
    private const float SidestepStep = 0.5f * TileSize;
    private const float SidestepMin = 1f * TileSize;
    private const float SidestepMax = 8f * TileSize;
    // How far the blocked direction must open up at the new spot for it to count as a way round.
    private const float SidestepProbeAhead = 2.5f * TileSize;

    // Physics probing. The navigation graphs are a MODEL of what blocks the player; the colliders
    // ARE what blocks the player, and the two disagree often enough to have walked someone into a
    // Holzzaun repeatedly. Roughly the player's own body radius (their graph is built with a
    // collision diameter of 3.4 nodes at 8 units each), plus a little clearance so a leg ends just
    // short of what it found rather than flush against it.
    // The player's own collision radius, as the game builds it: the player graph is scanned with a
    // collision diameter of 3.4 nodes at 8 units each, so about 14 units, and this is set a little
    // wider still. Probing thinner than the body threads gaps the player cannot fit through — which
    // is exactly what a wooden fence is, a row of posts with air between them, and why the mod kept
    // offering steps west that ended against a fence post a metre later.
    private const float BodyRadius = 0.16f * TileSize;
    private const float ObstacleStandoff = 0.15f * TileSize;
    // The "is something right in front of me" test: one short step ahead, at the same width.
    private const float NearFieldReach = 0.3f * TileSize;
    private const float NearFieldRadius = 0.16f * TileSize;

    // Straying this far from the route means the route is stale — ask for a new one. Generous,
    // because a cardinal staircase legitimately cuts corners off the route it is following.
    private const float StrayRerouteDistance = 8f * TileSize;
    // The target itself moved this far (an NPC wandered off) -> route again.
    private const float TargetMovedDistance = 3f * TileSize;
    // Standing this close to the goal, the last step is given without a walkability test.
    private const float FinalApproachDistance = 4f * TileSize;
    // Consecutive routes that produce no usable instruction before turn-by-turn gives up.
    private const int MaxRouteAttempts = 4;
    // How much ground the fine graph is given on that last attempt.
    private const float WideRescanTiles = 18f;
    // How long a route is given before another block may replace it. Re-planning on every bump
    // let two graphs that disagree take turns, and their disagreement was spoken as east, west,
    // east, west.
    private const float RerouteCooldown = 8f;
    // Being unable to join the route at all is a broken plan rather than a bump, so it may ask for
    // a new one sooner.
    private const float BoxedInRerouteCooldown = 3f;
    // How often one journey may step clear because no route could be found from where they stand.
    private const int MaxNoRouteEscapes = 2;
    // Only check a few times a second; nothing here needs per-frame precision.
    private const int TickInterval = 5;
    // How far the player must move before a target we could not reach is tried again, and how
    // rarely the "I cannot work out a way" line may repeat while that keeps failing.
    private const float IdleResumeDistance = 2f * TileSize;
    private const float LostAnnounceInterval = 20f;
    // How long the selection must sit still before guidance follows it. Long enough that paging
    // through a category does not fire a route query per keypress, short enough that landing on
    // the thing you want starts guiding you to it without another keystroke.
    private const float RetargetDelay = 1.2f;

    // ---- state --------------------------------------------------------------------------
    // The MODE, as opposed to a live session. Ctrl+B turns this on and only Ctrl+B (or Escape)
    // turns it off: arriving somewhere, losing the way, a cutscene or an auto-walk all end the
    // session below while leaving the mode standing, so the player never has to re-arm it.
    private static bool _enabled;
    private static bool _active;
    private static NavigationTarget _target;
    private static Vector2 _dest;               // the interaction tile we are guiding to
    private static Vector2? _facePos;           // what to face on arrival, so vanilla E works
    private static Vector2 _destAnchor;         // target position the current route was built for
    private static bool _allowBeacon;           // may this session end in the (auto-walking) beacon?

    private static List<Vector2> _route;
    // The route boiled down to the corners worth speaking about, and which one we are walking to.
    private static List<Vector2> _corners;
    private static int _cornerIndex;
    // The second arm of an L, or the other axis of a staircase: known when the leg is built, so it
    // never has to be guessed at by re-planning from the far end.
    private static string _pendingNextDir;
    private static string _legSource;           // which rung produced the current step, for the log
    private static bool _freeform;              // no route: head straight at the target
    private static bool _routePending;
    private static StepPrefix _pendingPrefix;
    private static int _routeAttempts;

    private static bool _hasLeg;
    private static Leg _leg;
    private static string _nextDir;             // the turn after this one, spoken as a heads-up

    private static float _settleUntil;
    private static float _lastCorrection;
    private static float _lastSpokeAt;
    private static float _lastRouteAt;          // when the current route arrived
    private static bool _rerouteWhenLegDone;    // set after a get-unstuck step
    private static Vector2 _clearedAxis;        // the direction a detour was taken to open up
    private static Vector2 _clearedFrom;        // and where it should be walked from
    private static bool _lookahead;             // planning the NEXT step, not the one being given
    private static bool _oscillating;           // this step undid the last, which undid the one before
    private static Vector2 _lastLegAxis;        // the last two committed directions, to spot a loop
    private static Vector2 _prevLegAxis;
    private static int _noRouteEscapes;         // times we stepped clear because nothing would route
    private static float _bestAlong;            // best remaining-along-the-leg seen (progress watch)
    private static float _progressSince;
    private static Vector2 _progressPos;

    private static Vector2 _blockedAxis;
    private static Vector2 _blockedAt;
    private static float _blockedUntil;
    private static float _blockedRadius = BlockedMemoryRadius;
    private static string _blockerName;         // what stopped them, for the next instruction
    // The layer the player's own body collides on, and whether collider probing works at all here.
    private static int _playerLayer = -1;
    private static bool _physicsUsable = true;
    // Guiding someone out of a building before taking them where they asked.
    private static bool _doorAssist;
    private static string _doorAssistLabel;
    // Set the moment anything physically stops the player: from then on this journey is planned on
    // the fine player graph, which knows the fence that the world mesh does not.
    private static bool _preferPlayerGraph;

    private static int _tick;
    private static bool _retargetPending;
    private static float _retargetAt;
    // A target we could not guide to from where we were. Kept so that walking somewhere else can
    // silently try it again — the mode is on, so the player is still asking to be taken there.
    private static bool _hasIdleTarget;
    private static NavigationTarget _idleTarget;
    private static Vector2 _idleFrom;
    private static float _lastLostAnnounce;
    private static StepPrefix _startPrefix = StepPrefix.Start;

    private struct Leg
    {
        internal Vector2 Start, End, Axis, Perp;
        internal float Length;
        internal string Dir;
    }

    /// How an instruction is introduced. The words differ, the leg does not.
    private enum StepPrefix { Start, Plain, Next, Correction, Blocked, Replan, Retarget, Door }

    private static readonly Vector2[] Cardinals =
    {
        new Vector2(0f, 1f), new Vector2(0f, -1f), new Vector2(1f, 0f), new Vector2(-1f, 0f),
    };

    /// A live session: a target, a route, an instruction in the air.
    internal static bool IsActive => _active;

    /// The mode itself, which outlives any one session. Escape and Ctrl+B are the only things
    /// that clear it.
    internal static bool IsEnabled => _enabled;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _initialized = true;
    }

    // ---- entry points -------------------------------------------------------------------

    /// <summary>Ctrl+B: turn guidance on for the selected object, or turn the mode off.</summary>
    internal static void Toggle()
    {
        if (!_initialized) return;

        if (_enabled)
        {
            Disable(announce: true);
            return;
        }

        _enabled = true;
        _log?.LogInfo("[GUIDE] Guidance mode ON");

        // Nothing selected yet is not a reason to refuse the mode: it stays on, and the moment the
        // player pages onto something it starts guiding them there.
        if (!ObjectNavigator.TryGetSelectedTarget(out var target))
        {
            ScreenReader.Say(Loc.Get("guide.on_waiting"), interrupt: true);
            return;
        }

        StartTo(target);
    }

    /// <summary>Turn the mode off — the player asked, via Ctrl+B or Escape.</summary>
    internal static void Disable(bool announce)
    {
        if (!_enabled) return;
        _enabled = false;
        _hasIdleTarget = false;
        Stop(announce: false);
        if (announce) ScreenReader.Say(Loc.Get("nav.guidance_stopped"), interrupt: true);
        _log?.LogInfo("[GUIDE] Guidance mode OFF");
    }

    /// <summary>
    /// Begin guiding to <paramref name="target"/>.
    /// </summary>
    /// <param name="allowBeaconFallback">
    /// Whether giving up may hand over to the compass beacon. The beacon auto-walks the player once
    /// they get close (its A* handoff), so this is false for Ctrl+B — that key must never move
    /// anyone — and true only when the auto-walker itself fell back to us, where the player had
    /// asked to be walked in the first place.
    /// </param>
    internal static void StartTo(NavigationTarget target, bool announceStart = true,
                                bool allowBeaconFallback = false)
    {
        if (!_initialized) return;

        // How the first instruction opens. A selection change has already asked for "Now guiding
        // to X"; a fresh Ctrl+B names the target and the distance; the auto-walker's recovery path
        // has said its piece already and just wants the step.
        var firstPrefix = _startPrefix;
        _startPrefix = StepPrefix.Start;
        if (firstPrefix == StepPrefix.Start && !announceStart) firstPrefix = StepPrefix.Plain;

        // The player walks themselves from here, so nothing of ours may still be driving them.
        ObjectNavigator.StopMovementForGuidedWalk();

        var playerPos = MainGame.me?.player?.pos ?? Vector2.zero;
        var dest = ObjectNavigator.GuidedDestFor(target, out var facePos);

        // Already standing on it — which is also how "route from A to A" used to come back as
        // "no route" and drop the player onto the beacon for no reason.
        if (Vector2.Distance(playerPos, dest) <= GoalTolerance ||
            Vector2.Distance(playerPos, target.Position) <= GoalTolerance)
        {
            ScreenReader.Say(Loc.Fmt("guide.already_there", target.Label), interrupt: true);
            return;
        }

        _active = true;
        _target = target;
        _dest = dest;
        _facePos = facePos;
        _destAnchor = target.Position;
        _allowBeacon = allowBeaconFallback;
        // The corners go with the route. Leaving them behind meant a new journey whose route had
        // not arrived yet - or could not be found at all - planned against the LAST target's
        // corners: the player was sent north toward their house while the thing they had asked for
        // lay fifty metres south.
        _route = null;
        _corners = null;
        _cornerIndex = 0;
        _freeform = false;
        _hasLeg = false;
        _nextDir = null;
        _lastCorrection = 0f;
        _blockedUntil = 0f;
        _routeAttempts = 0;
        _settleUntil = 0f;
        _retargetPending = false;
        _preferPlayerGraph = false;
        _noRouteEscapes = 0;
        _clearedAxis = Vector2.zero;
        _lastLegAxis = Vector2.zero;
        _prevLegAxis = Vector2.zero;
        _oscillating = false;
        if (firstPrefix != StepPrefix.Door) _doorAssist = false;
        ResetProgress(playerPos, float.MaxValue);

        // Get the player graph scanned around here BEFORE the first instruction, not after walking
        // into something: near fences and props it is the only data fine enough to be right, and
        // the first instruction is the one most likely to be given while standing next to one.
        ObjectNavigator.RefreshPlayerGraphAround(playerPos);

        _log?.LogInfo($"[GUIDE] Guided walk started to {target.Label} at {_dest} (beacon fallback: {_allowBeacon})");

        RequestRoute(playerPos, firstPrefix);
    }

    /// <summary>
    /// The navigator's selection moved (Page up/down, or a category change) while guidance is
    /// running. Guidance follows it, so a player browsing for somewhere to go does not have to
    /// press Ctrl+B again for every candidate — but only once the selection has settled, since
    /// paging through a list would otherwise fire a route query per keypress.
    /// </summary>
    internal static void NotifySelectionChanged()
    {
        if (!_enabled) return;
        _retargetPending = true;
        _retargetAt = Time.realtimeSinceStartup + RetargetDelay;
    }

    /// Switch guidance to whatever is selected now, unless that is what we are already guiding to.
    private static void Retarget()
    {
        _retargetPending = false;

        if (!ObjectNavigator.TryGetSelectedTarget(out var target)) return;
        // Only compare against a LIVE session's target. When idle, _target is whatever we last
        // guided to, and paging back onto it is a request to be taken there again.
        if (_active && SameTarget(target, _target)) return;

        _log?.LogInfo($"[GUIDE] Selection moved to {target.Label}; guidance follows");
        Stop(announce: false);
        _startPrefix = StepPrefix.Retarget;     // the first instruction says what it is guiding to
        StartTo(target, announceStart: false);
    }

    /// <summary>
    /// Mode on, nothing being guided, and a target we owe the player: try it again once they have
    /// moved somewhere the answer might be different. Silent — the reason it stopped was announced
    /// when it happened, and repeating it every few tiles would be nagging.
    /// </summary>
    private static void TryResumeIdle(Vector2 p)
    {
        if (!_hasIdleTarget) return;
        if (Vector2.Distance(p, _idleFrom) < IdleResumeDistance) return;

        var target = _idleTarget;
        _hasIdleTarget = false;
        _log?.LogInfo($"[GUIDE] Trying {target.Label} again from {p}");
        _startPrefix = StepPrefix.Retarget;   // time has passed: name the target again
        StartTo(target, announceStart: false);
    }

    /// <summary>Same object, or (for drops and bare map points) the same spot?</summary>
    private static bool SameTarget(NavigationTarget a, NavigationTarget b)
    {
        if (a.Object != null || b.Object != null) return ReferenceEquals(a.Object, b.Object);
        if (a.DropGo != null || b.DropGo != null) return ReferenceEquals(a.DropGo, b.DropGo);
        return a.Label == b.Label && Vector2.Distance(a.Position, b.Position) < TileSize;
    }

    /// <summary>
    /// End the current session. The MODE is untouched: everything that interrupts guidance —
    /// arriving, a cutscene, an auto-walk, a teleport — goes through here, and none of them mean
    /// the player has finished being guided. Use <see cref="Disable"/> for that.
    /// </summary>
    internal static void Stop(bool announce = true)
    {
        if (!_active) return;
        _active = false;
        _hasLeg = false;
        _route = null;
        _corners = null;
        _cornerIndex = 0;
        _routePending = false;
        _retargetPending = false;
        _hasIdleTarget = false;
        if (announce) ScreenReader.Say(Loc.Get("nav.guidance_stopped"), interrupt: true);
        _log?.LogInfo("[GUIDE] Guided walk stopped");
    }

    /// <summary>
    /// End the session but remember the target, so that moving somewhere else picks it up again.
    /// For interruptions where the player still plainly wants to get there — a teleport dropped
    /// them elsewhere, or no route could be found from where they stood.
    /// </summary>
    internal static void Suspend()
    {
        if (!_active) return;
        var target = _target;
        Stop(announce: false);
        KeepForRetry(target);
    }

    private static void KeepForRetry(NavigationTarget target)
    {
        if (!_enabled) return;
        _hasIdleTarget = true;
        _idleTarget = target;
        _idleFrom = MainGame.me?.player?.pos ?? Vector2.zero;
    }

    /// <summary>Home while guiding: say the current instruction again, plus what is left.</summary>
    internal static void RepeatStep()
    {
        if (!_active) return;

        var p = MainGame.me?.player?.pos ?? Vector2.zero;
        var left = ObjectNavigator.DistanceWords(Vector2.Distance(p, _target.Position));

        if (!_hasLeg)
        {
            ScreenReader.Say(Loc.Fmt("guide.working_it_out", _target.Label, left), interrupt: true);
            return;
        }

        // Re-measure the remaining leg from where they are now, so the repeat is a usable
        // instruction rather than a replay of what was true two corners ago.
        float along = Mathf.Max(0f, Vector2.Dot(_leg.End - p, _leg.Axis));
        ScreenReader.Say(_nextDir == null
                             ? Loc.Fmt("guide.repeat", Meters(along), _leg.Dir, left, _target.Label)
                             : Loc.Fmt("guide.repeat_then", Meters(along), _leg.Dir, _nextDir, left, _target.Label),
                         interrupt: true);
    }

    // ---- per-frame driver ---------------------------------------------------------------

    internal static void Update()
    {
        if (!_initialized || !_enabled) return;

        try
        {
            if (++_tick < TickInterval) return;
            _tick = 0;

            var player = MainGame.me?.player;
            if (player == null) { Stop(announce: false); return; }

            // A settled selection change re-aims guidance; until it settles, say nothing about the
            // target being left behind.
            if (_retargetPending)
            {
                if (Time.realtimeSinceStartup < _retargetAt) return;
                Retarget();
                return;
            }

            // The mode is on but nothing is being guided: wait for a new selection, or pick up a
            // target we could not reach from where we last stood.
            if (!_active)
            {
                TryResumeIdle(player.pos);
                return;
            }

            if (_routePending) return;

            var p = player.pos;

            // The object we are guiding to can vanish (looted, harvested) or walk away (an NPC).
            if (!TargetStillValid(p)) return;

            if (Vector2.Distance(p, _dest) <= GoalTolerance ||
                Vector2.Distance(p, _target.Position) <= GoalTolerance ||
                // Standing against the thing itself. A farm plot or a workbench is metres across,
                // so the point the game sorts it by can still be far away while the player is flat
                // against its edge — and being told that the thing they walked to is blocking
                // their way is the least useful sentence the mod could produce.
                (_target.Object != null &&
                 ObjectNavigator.DistanceToObjectEdge(_target.Object, p) <= EdgeArriveTolerance))
            {
                Arrive();
                return;
            }

            if (!_hasLeg) { NextLeg(p, StepPrefix.Correction); return; }

            float along = Vector2.Dot(_leg.End - p, _leg.Axis);     // still to go on this leg

            // What counts as "there" has to scale with the step. A fixed metre-wide tolerance means
            // a step SHORTER than that is finished the instant it is spoken — so it was re-planned,
            // spoken again, finished again, over and over, saying the same thing dozens of times
            // while the player stood still. It also reset the stuck watch each time, which is why
            // pushing against something went unnoticed throughout.
            float doneAt = Mathf.Min(ArriveTolerance, _leg.Length * 0.4f);

            // Reached (or walked past) the end of this leg — hand out the next one from here.
            if (along <= doneAt)
            {
                NextLeg(p, StepPrefix.Next);
                return;
            }

            // Progress bookkeeping runs every tick, settle window or not: it is what both of the
            // stuck tests below measure against.
            if (along < _bestAlong - BlockedProgress) ResetProgress(p, along);

            float stuckFor = Time.realtimeSinceStartup - _progressSince;
            float movedSince = Vector2.Distance(p, _progressPos);
            bool settled = Time.realtimeSinceStartup >= _settleUntil;

            // Pinned: walking, and not moving at all. Checked even inside the settle window — that
            // window exists to give the player time to REACT to an instruction, and someone held
            // against a wall has already reacted. Being inside it is what used to make this take
            // three seconds to notice: the window suppressed the check, then the fuse ran.
            // Are they actually trying to walk the way they were told? Pressing into a fence and
            // sliding along it looks exactly like strolling off in another direction if you only
            // watch the position — and treating the second as "blocked" is what set off a re-plan,
            // which flipped the route, which bounced them back and forth.
            var heading = ObjectNavigator.PlayerHeading();
            bool following = heading == Vector2.zero || Vector2.Dot(heading, _leg.Axis) > 0.5f;

            // Holding the key and going nowhere IS being blocked. Asking the game whether the
            // character is "moving" was the wrong question: pushed up against something it can
            // report standing still, and the mod then said nothing at all while the player leaned
            // on a fence waiting to be told.
            bool pushing = ObjectNavigator.PlayerIsPressingMove() &&
                           Vector2.Dot(heading, _leg.Axis) > 0.5f;

            // Being stuck means being STOPPED, and you can only be stopped by something if you are
            // walking into it. A hand on the keyboard is the whole difference between "held against
            // a fence" and "listening to the instruction before setting off" — and the game's own
            // idea of whether the character is moving cannot tell those apart, which is why five of
            // the eight alarms in the last session came from the player simply pausing. No key
            // held, no verdict, ever.
            bool pinned = stuckFor >= WallSeconds &&
                          movedSince < WallMoveEpsilon &&
                          pushing;

            // Scraping: moving, but the leg is not getting shorter. Gated by the settle window,
            // because still holding the previous direction looks exactly like this for a second or
            // two after a new instruction.
            bool scraping = !pinned && following && settled &&
                            stuckFor >= BlockedSeconds &&
                            movedSince >= BlockedMoveDistance;

            if (!following && stuckFor >= BlockedSeconds)
            {
                // Off doing something else. Say nothing, and start the clock again from here.
                ResetProgress(p, along);
                return;
            }

            if (pinned || scraping)
            {
                // Does anything actually stand in the way, or did they simply stop? Saying "that
                // way is blocked" about ground the player can plainly walk is worse than saying
                // nothing, so the claim has to be backed by the colliders, not just by the fact
                // that they were not moving for a moment.
                bool reallyBlocked =
                    PhysicsClearDistance(p, _leg.Axis, MinLegLength) < MinLegLength;

                // If the colliders say the way is open, be patient before saying anything at all:
                // the player may have paused, turned aside for a moment, or be pressed against
                // something too thin to detect. Acting on the first second of stillness is how the
                // mod ended up announcing obstructions on ground that was perfectly walkable.
                // Nothing is logged in that window either - waiting is not an event, and repeating
                // the same warning every frame for three seconds buries the ones that matter.
                if (!reallyBlocked && stuckFor < SoftStuckSeconds) return;

                _log?.LogWarning($"[GUIDE] {(pinned ? "Pinned" : "Scraping")} heading {_leg.Dir} at {p} " +
                                 $"after {stuckFor:F1}s ({along / TileSize:F1} tiles still on the leg), " +
                                 $"colliders {(reallyBlocked ? "agree" : "say the way is open")}");

                // Not yet: the last change of plan is still being carried out. Keep the clock
                // running so this fires the moment the cooldown is up if they really are stuck.
                if (Time.realtimeSinceStartup - _lastCorrection < ObstructionCooldown) return;

                ResetProgress(p, along);
                Obstructed(p, reallyBlocked);
                return;
            }

            if (!settled) return;

            // Drifted well off the line — say how to get back onto it rather than repeating the
            // leg, which would leave the error in place and walk them past the corner.
            float lateral = Mathf.Abs(Vector2.Dot(p - _leg.Start, _leg.Perp));
            if (lateral > OffCourseDistance &&
                Time.realtimeSinceStartup - _lastCorrection >= CorrectionCooldown)
            {
                NextLeg(p, StepPrefix.Correction);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[GUIDE] Update error: {ex.Message}\n{ex.StackTrace}");
            Stop(announce: false);
        }
    }

    private static bool TargetStillValid(Vector2 p)
    {
        var obj = _target.Object;
        if (obj == null) return true;                 // drops / bare map points have no WGO
        try
        {
            if (obj.is_removed)
            {
                ScreenReader.Say(Loc.Fmt("nav.could_not_reach", _target.Label), interrupt: true);
                Stop(announce: false);
                return false;
            }

            // An NPC that wandered off: keep guiding, but to where they are now.
            if (Vector2.Distance(obj.pos, _destAnchor) > TargetMovedDistance)
            {
                _target.Position = obj.pos;
                _dest = ObjectNavigator.GuidedDestFor(_target, out _facePos);
                _destAnchor = obj.pos;
                _log?.LogInfo($"[GUIDE] {_target.Label} moved; re-routing to {_dest}");
                RequestRoute(p, StepPrefix.Correction);
                return false;
            }
        }
        catch { }
        return true;
    }

    private static void Arrive()
    {
        var target = _target;
        var face = _facePos;

        // Reached the door we detoured to: say what to do with it, and keep the real target owed
        // so that stepping outside and walking on picks it up without another keypress.
        if (_doorAssist)
        {
            var waiting = _doorAssistLabel;
            bool owed = _hasIdleTarget;
            var owedTarget = _idleTarget;

            _log?.LogInfo($"[GUIDE] Reached {target.Label}; {waiting} still owed");
            _doorAssist = false;
            Stop(announce: false);
            if (owed)
            {
                _hasIdleTarget = true;
                _idleTarget = owedTarget;
                _idleFrom = MainGame.me?.player?.pos ?? Vector2.zero;
            }

            ObjectNavigator.FaceGuidedArrival(target, face);
            ScreenReader.Say(Loc.Fmt("guide.at_door", waiting), interrupt: true);
            return;
        }

        _log?.LogInfo($"[GUIDE] Arrived at {target.Label}");
        _hasIdleTarget = false;         // nothing owed: they are there
        Stop(announce: false);
        ObjectNavigator.NotifyGuidedArrival(target, face);
    }

    // ---- progress / blocked watch --------------------------------------------------------

    private static void ResetProgress(Vector2 p, float along)
    {
        _bestAlong = along;
        _progressSince = Time.realtimeSinceStartup;
        _progressPos = p;
    }

    /// <summary>
    /// Something is in the way. Step around it — a short move across the blocked direction, then
    /// carry on — which is what a sighted player does without thinking, and what the player
    /// previously had to work out for themselves. Only when neither side is any good does this
    /// fall back to re-planning from here.
    /// </summary>
    private static void Obstructed(Vector2 p, bool reallyBlocked = true)
    {
        // Whatever stopped them is not in the data we used to route them into it. The player graph
        // is built from the player's own collision, so rescan it around here before deciding what
        // to say next — every probe below then has the real answer rather than the coarse one.
        ObjectNavigator.RefreshPlayerGraphAround(p);

        // Naming it turns "that way is blocked" into something the player can picture and
        // remember: a fence they now know runs along here, not an invisible refusal.
        _blockerName = reallyBlocked ? IdentifyObstruction(p, _leg.Axis) : null;
        if (_blockerName != null) _log?.LogInfo($"[GUIDE] Blocked by {_blockerName} at {p}");

        // Two probes, and they disagree: the sweep says something stopped them, but looking around
        // the spot finds nothing solid at all — not a fence, not a wall, not even bare level
        // geometry. Something the mod cannot see is not something it should assert, so it stops
        // claiming the way is barred and just says it is trying another way. The player is far
        // better served by an honest change of plan than by being told about a wall that is not
        // there, and being told that repeatedly is what made the whole feature feel unreliable.
        if (reallyBlocked && _blockerName == null)
        {
            _log?.LogInfo($"[GUIDE] Nothing solid found at {p}; treating it as a re-plan, not a wall");
            reallyBlocked = false;
        }

        // What to call it. Only a confirmed obstruction gets "that way is blocked"; everything else
        // is the mod changing its mind, which is what it sounds like to the player anyway.
        var prefix = reallyBlocked ? StepPrefix.Blocked : StepPrefix.Replan;

        // A direction is only barred from being offered again once we know what barred it.
        if (reallyBlocked) RememberBlocked(p);

        var blockedAxis = _leg.Axis;

        // Whatever we walked into, the plan that produced that step did not know about it. Every
        // route from here on is planned on the fine player graph instead.
        _preferPlayerGraph = true;

        if (!TrySidestep(p, out var side))
        {
            // No way round within eight metres means this is not a prop to step around, it is
            // structure — a fence line, a farm plot, the corner of an enclosure. Guessing a
            // direction here is what produced "walk 1 metre west, then south" into the fence it
            // had just hit. Ask for a proper route around it instead.
            // A fresh route is the right answer once, not every few seconds: the plan needs long
            // enough to be walked before it is thrown away.
            if (Time.realtimeSinceStartup - _lastRouteAt >= RerouteCooldown)
            {
                _log?.LogInfo("[GUIDE] Nothing to sidestep round; re-routing on the fine graph");
                RequestRoute(p, prefix);
                return;
            }

            _log?.LogInfo("[GUIDE] Nothing to sidestep round, and the route is still fresh; backing out");
            if (TryBackOut(p, out var out_leg, avoid: blockedAxis))
            {
                Commit(out_leg, null, prefix, p);
                return;
            }
            NextLeg(p, prefix);
            return;
        }

        // The follow-up to a sidestep is not a matter of opinion: TrySidestep only accepted this
        // spot BECAUSE the blocked direction opens up from it, so that is the direction to hint.
        // Asking the scorer instead is what produced "go south, then north" — it would rather undo
        // the sidestep than use it.
        //
        // And it is not just a hint. The whole point of stepping a metre west was to get past the
        // thing barring the way south; if the next instruction is worked out from scratch on
        // arrival, the route — which still runs through the obstruction — pulls them straight back,
        // and the player is walked west, east, west along the same fence. The cleared direction is
        // therefore remembered and taken as the next step.
        Commit(side, CardinalWord(blockedAxis), prefix, p);
    }

    /// <summary>
    /// A short move across the direction that just failed. Preference goes to the side the route
    /// lies on — that is the way round an obstruction that also makes progress — and a side the
    /// player graph calls solid is not offered at all.
    /// </summary>
    private static bool TrySidestep(Vector2 from, out Leg leg) =>
        TryDetour(from, _leg.Axis, _freeform ? _dest : RouteAheadPoint(from, DeadReckonAhead), out leg);

    /// <summary>
    /// Find the way round something blocking <paramref name="blocked"/>: step across it, further
    /// and further, until that direction opens up again. Used both when the player has walked into
    /// something and — better — when the mod can see beforehand that the way it wants is solid.
    /// </summary>
    private static bool TryDetour(Vector2 from, Vector2 blocked, Vector2 aim, out Leg leg)
    {
        leg = default;
        if (blocked.sqrMagnitude < 0.5f) return false;
        var perp = new Vector2(-blocked.y, blocked.x);

        // Which way round: the side the route lies on, since that is the detour that also makes
        // progress. Ties go to the route's side by default.
        var preferred = Vector2.Dot(aim - from, perp) >= 0f ? perp : -perp;

        // Grow the offset until the way on genuinely opens. This is what tells a barrel (one metre)
        // from a fence (six), instead of stepping two metres and walking into it again.
        for (float off = SidestepMin; off <= SidestepMax + 0.01f; off += SidestepStep)
        {
            foreach (var side in new[] { preferred, -preferred })
            {
                var spot = from + side * off;
                // Both halves are checked against the colliders as well as the graphs: getting
                // there, and the blocked direction genuinely opening up once there.
                if (PhysicsClearDistance(from, side, off) < off) continue;
                if (PhysicsClearDistance(spot, blocked, SidestepProbeAhead) < SidestepProbeAhead) continue;
                // Indoors there is no graph data at all, so the collider checks above stand alone.
                if (!_freeform &&
                    (!LineClear(from, side, off) || !LineClear(spot, blocked, SidestepProbeAhead)))
                    continue;

                leg = MakeLeg(from, spot);
                _legSource = "detour";

                // Remember WHY this step is being given. A detour is a means, not an end: the whole
                // point of a metre west is the south that opens up from there. Working the next step
                // out from scratch on arrival throws that away - the route still runs through the
                // obstruction, so it pulls the player straight back, and they are walked west, east,
                // west along the same fence. Recorded here, where every detour passes, rather than
                // at the one call site that used to do it.
                if (!_lookahead)
                {
                    _clearedAxis = blocked;
                    _clearedFrom = spot;
                }

                _log?.LogInfo($"[GUIDE] Way round: {off / TileSize:F1} tiles {leg.Dir} clears {CardinalWord(blocked)} at {spot}");
                return true;
            }
        }

        _log?.LogInfo($"[GUIDE] No way round within {SidestepMax / TileSize:F0} tiles of {from}");
        return false;
    }

    /// <summary>
    /// What is standing in front of the player? Asked of the physics colliders rather than any
    /// navigation graph, because the thing that stopped the body IS a collider — and because the
    /// graphs demonstrably do not always know it is there. Returns null when nothing nameable is
    /// found, in which case the instruction simply says the way is blocked.
    /// </summary>
    private static string IdentifyObstruction(Vector2 from, Vector2 axis)
    {
        try
        {
            var player = MainGame.me?.player;
            bool sawSolid = false;

            // Along the line they were walking, at about their own width. A fatter probe than that
            // picks up whatever happens to stand BESIDE the path and announces it as the obstacle.
            for (float t = 0.3f * TileSize; t <= 1.2f * TileSize; t += 0.3f * TileSize)
            {
                var hits = Physics2D.OverlapCircleAll(from + axis * t, NearFieldRadius);
                if (hits == null) continue;

                int layer = PlayerCollisionLayer(player);
                foreach (var hit in hits)
                {
                    // Triggers are zones and scripts, not walls: they stop nobody.
                    if (hit == null || hit.isTrigger) continue;

                    // Nor does anything the player walks straight through. Without this the mod
                    // announced a rug as the thing barring the way, because a collider was all it
                    // checked for — the same mistake the clearance probe made.
                    if (layer >= 0 && Physics2D.GetIgnoreLayerCollision(layer, hit.gameObject.layer))
                        continue;

                    var wgo = hit.GetComponentInParent<WorldGameObject>();
                    if (IsTargetObject(wgo)) continue;      // arriving, not blocked
                    if (wgo == null)
                    {
                        // A collider owning no object is level geometry — a building shell, a
                        // doorframe, a cliff edge. Nothing to name, but worth saying it is solid.
                        sawSolid = true;
                        continue;
                    }
                    if (wgo == player || wgo.is_player) continue;

                    var objId = wgo.obj_id;
                    if (string.IsNullOrEmpty(objId)) { sawSolid = true; continue; }
                    if (objId.EndsWith("_place")) objId = objId.Substring(0, objId.Length - 6);

                    var name = InteractionDetector.LocalizedObjectName(objId);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                    sawSolid = true;
                }
            }

            return sawSolid ? Loc.Get("guide.blocker_solid") : null;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[GUIDE] Could not identify the obstruction: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// How far the player can actually walk this way before something physical stops them, asked
    /// of the colliders rather than of any navigation graph. The graphs are a coarse model that has
    /// repeatedly failed to know a fence was there; this is the very thing the game uses to stop
    /// the body.
    ///
    /// Only colliders that actually collide WITH THE PLAYER count. Unity's layer collision matrix
    /// decides that, and consulting it is the whole difference between a useful probe and one that
    /// calls every direction solid — the world is full of non-trigger colliders on layers the
    /// player passes straight through, and counting those rejected 254 directions in one session.
    /// </summary>
    private static float PhysicsClearDistance(Vector2 from, Vector2 dir, float maxDistance)
    {
        if (!_physicsUsable) return maxDistance;
        try
        {
            var player = MainGame.me?.player;
            int layer = PlayerCollisionLayer(player);

            // The near field first, and this is the part that was missing. A swept circle that
            // STARTS inside a collider reports a distance of zero, which was being discarded as
            // "already overlapping, ignore" — so a chest the player was pressed against read as
            // open ground, a two-and-a-half metre step west was offered into it, and the only thing
            // that noticed was the stuck watch a second later. Anything solid immediately ahead
            // blocks the way, whether or not the sweep can measure how far away it is.
            if (BlockedJustAhead(from, dir, player, layer)) return 0f;

            var hits = Physics2D.CircleCastAll(from, BodyRadius, dir, maxDistance);
            if (hits == null || hits.Length == 0) return maxDistance;

            // Only what actually stops the player counts, and — this is the part that was wrong —
            // the standoff is subtracted only when something WAS hit. Taking it off the full
            // distance as well meant this never once returned the distance it was asked about, so
            // every "is the way clear for N?" answered "no, N minus a hand's width", and every
            // caller believed it: the way ahead was always blocked, no L-shaped step was ever
            // clear enough to offer, and no way round a fence was ever wide enough to accept.
            // One line, and it made the mod distrust perfectly open ground all day.
            float nearest = float.MaxValue;
            foreach (var h in hits)
            {
                if (!Blocks(h, player, layer)) continue;
                if (h.distance < nearest) nearest = h.distance;
            }
            if (nearest == float.MaxValue) return maxDistance;       // nothing in the way at all
            return Mathf.Clamp(nearest - ObstacleStandoff, 0f, maxDistance);
        }
        catch (Exception ex)
        {
            // If the collision matrix cannot be read at all, stop probing rather than limp along
            // rejecting everything: the graphs on their own are better than a probe that lies.
            _physicsUsable = false;
            _log?.LogWarning($"[GUIDE] Collider probing disabled: {ex.Message}");
            return maxDistance;
        }
    }

    /// <summary>
    /// Is there something solid in the very next step? Asked as an overlap rather than a sweep,
    /// because a sweep cannot measure an obstacle the player is already touching — and being
    /// already touching it is exactly the case that matters when they are pressed against a chest.
    /// </summary>
    private static bool BlockedJustAhead(Vector2 from, Vector2 dir, WorldGameObject player, int layer)
    {
        var cols = Physics2D.OverlapCircleAll(from + dir * NearFieldReach, NearFieldRadius);
        if (cols == null) return false;

        foreach (var col in cols)
        {
            if (col == null || col.isTrigger) continue;
            if (layer >= 0 && Physics2D.GetIgnoreLayerCollision(layer, col.gameObject.layer)) continue;

            var wgo = col.GetComponentInParent<WorldGameObject>();
            if (wgo != null && (wgo == player || wgo.is_player)) continue;
            if (IsTargetObject(wgo)) continue;              // arriving, not blocked

            // Their own collider, found without an owning object.
            if (col.transform != null && player != null &&
                col.transform.IsChildOf(player.transform)) continue;

            return true;
        }
        return false;
    }

    /// <summary>Would this hit stop the player: solid, not theirs, and on a layer they collide with?</summary>
    private static bool Blocks(RaycastHit2D hit, WorldGameObject player, int playerLayer)
    {
        var col = hit.collider;
        if (col == null || col.isTrigger) return false;   // zones and scripts stop nobody
        if (hit.distance <= 0.01f) return false;          // already overlapping at the start

        if (playerLayer >= 0 && Physics2D.GetIgnoreLayerCollision(playerLayer, col.gameObject.layer))
            return false;                                 // the player walks through this

        var wgo = col.GetComponentInParent<WorldGameObject>();
        if (wgo == null) return true;
        if (wgo == player || wgo.is_player) return false;
        return !IsTargetObject(wgo);
    }

    /// <summary>
    /// The thing we are guiding them TO is never in the way. Walking up to a chest ends with the
    /// player against the chest, and calling that an obstruction turns every arrival into a
    /// complaint — which is exactly what "Zombiefarm blocks the way" was, said while standing at
    /// the Zombiefarm they had asked to be taken to.
    /// </summary>
    private static bool IsTargetObject(WorldGameObject wgo) =>
        wgo != null && _target.Object != null && wgo == _target.Object;

    /// The layer the player's own solid collider sits on, cached; -1 if it cannot be determined.
    private static int PlayerCollisionLayer(WorldGameObject player)
    {
        if (_playerLayer >= 0) return _playerLayer;
        try
        {
            if (player == null) return -1;
            foreach (var c in player.GetComponentsInChildren<Collider2D>(true))
            {
                if (c == null || c.isTrigger) continue;
                _playerLayer = c.gameObject.layer;
                _log?.LogInfo($"[GUIDE] Player collides on layer {_playerLayer} ({LayerMask.LayerToName(_playerLayer)})");
                return _playerLayer;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// What the collider probe thinks is in each direction, named and measured. Logged when it
    /// claims everything is solid, which is the shape a mis-tuned probe takes.
    /// </summary>
    private static string DescribePhysics(Vector2 from)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var player = MainGame.me?.player;
            int layer = PlayerCollisionLayer(player);
            foreach (var axis in Cardinals)
            {
                sb.Append($"| {CardinalWord(axis)} ");
                var hits = Physics2D.CircleCastAll(from, BodyRadius, axis, MaxLegLength);
                RaycastHit2D nearest = default;
                float best = float.MaxValue;
                foreach (var h in hits)
                {
                    if (!Blocks(h, player, layer)) continue;
                    if (h.distance < best) { best = h.distance; nearest = h; }
                }
                if (nearest.collider == null) { sb.Append("clear "); continue; }
                var go = nearest.collider.gameObject;
                sb.Append($"{best / TileSize:F1}t by '{go.name}' layer={go.layer} ");
            }
        }
        catch (Exception ex) { sb.Append("probe failed: " + ex.Message); }
        return sb.ToString();
    }

    /// Every half tile along a stretch walkable? Used to check both halves of a sidestep.
    private static bool LineClear(Vector2 start, Vector2 dir, float length)
    {
        for (float t = StepSize; t <= length + 0.01f; t += StepSize)
            if (!ObjectNavigator.IsWalkableSpot(start + dir * t)) return false;
        return true;
    }

    private static void RememberBlocked(Vector2 p)
    {
        _blockedAxis = _leg.Axis;
        _blockedAt = p;
        _blockedUntil = Time.realtimeSinceStartup + BlockedMemorySeconds;
        _blockedRadius = BlockedMemoryRadius;
    }

    /// A direction that just failed here is not offered again until the player has moved on.
    private static bool IsBlockedDirection(Vector2 from, Vector2 axis)
    {
        if (Time.realtimeSinceStartup > _blockedUntil) return false;
        if (Vector2.Distance(from, _blockedAt) > _blockedRadius) return false;
        return Vector2.Dot(axis, _blockedAxis) > 0.9f;
    }

    // ---- routing ------------------------------------------------------------------------

    private static void RequestRoute(Vector2 from, StepPrefix prefix)
    {
        // Every route that yields no usable instruction asks for another, so without a ceiling a
        // hopeless spot would spin path queries forever.
        if (++_routeAttempts > MaxRouteAttempts)
        {
            GiveUp("no route led anywhere");
            return;
        }

        // On the last attempt, look properly before concluding there is no way. The fine graph only
        // covers the patch it was last scanned over, and "no path" from it usually means the way
        // round simply lay outside that patch — the player finding a way on foot moments later is
        // the proof. Scan a much bigger area and ask it again.
        if (_routeAttempts == MaxRouteAttempts)
        {
            _log?.LogInfo("[GUIDE] Last attempt: rescanning a wide area before giving up");
            ObjectNavigator.RefreshPlayerGraphAround(from, WideRescanTiles, force: true);
            _preferPlayerGraph = true;
        }

        _routePending = true;
        _pendingPrefix = prefix;
        _hasLeg = false;
        if (!ObjectNavigator.RequestGuidedRoute(from, _dest, _preferPlayerGraph, OnRoute))
            OnRoute(null);
    }

    private static void OnRoute(List<Vector2> route)
    {
        _routePending = false;
        if (!_active) return;                     // stopped while the query was in flight

        var p = MainGame.me?.player?.pos ?? Vector2.zero;

        if (route == null || route.Count < 2)
        {
            // Shut inside a building with no way through to the target: the walls are real and no
            // amount of cardinal stepping goes through them. Take them to the door first, exactly
            // as auto-walk does, and pick the real target back up once they are outside. Without
            // this, guidance spent whole minutes proposing steps into the walls of the player's own
            // house, because the route it was scoring against ran straight through them.
            if (!_doorAssist && ObjectNavigator.PlayerIsInsideBuilding() &&
                ObjectNavigator.TryNearestDoorTarget(out var door) &&
                Vector2.Distance(p, door.Position) > GoalTolerance)
            {
                var original = _target;
                _log?.LogInfo($"[GUIDE] Inside a building with no route to {original.Label}; " +
                              $"guiding to {door.Label} first");
                Stop(announce: false);
                KeepForRetry(original);          // resumed once they are out and have moved a little
                _doorAssist = true;
                _doorAssistLabel = original.Label;
                _startPrefix = StepPrefix.Door;
                StartTo(door, announceStart: false);
                return;
            }

            // Outdoors, "no route at all" to something a few metres away almost never means there
            // is no way — it means the PLAYER is standing somewhere the pathfinder cannot start
            // from: boxed in among workshop furniture, wedged between a chest and an anvil. Marching
            // a straight line from there walks them into the very things that caused it, which is
            // exactly what happened at (4761,-176): chest, anvil, oven, one after another. Step out
            // to open ground and ask again from there.
            if (!ObjectNavigator.PlayerIsInsideBuilding() &&
                _noRouteEscapes < MaxNoRouteEscapes &&
                TryBackOut(p, out var escape))
            {
                _noRouteEscapes++;
                _log?.LogWarning($"[GUIDE] No route from {p} — stepping clear and asking again");
                _rerouteWhenLegDone = true;
                _lastRouteAt = Time.realtimeSinceStartup;
                Commit(escape, null, StepPrefix.Replan, p);
                return;
            }

            // Otherwise: the target is on an island of its own, or we are already heading for the
            // door. Head straight at it in cardinal steps and let the colliders bound each one.
            _log?.LogWarning($"[GUIDE] No graph-0 route to {_target.Label}; guiding straight at it");
            _freeform = true;
            _route = new List<Vector2> { p, _dest };
            _lastRouteAt = Time.realtimeSinceStartup;
        }
        else
        {
            // The fine graph gave up and the coarse one answered instead. Its 76-unit squares do
            // not know about the fence that just stopped us, so its route can point the opposite
            // way round the obstacle. Keep the plan we have — but ONLY when the plan is still
            // usable, i.e. we asked because something physically stopped us. When we asked because
            // the route could not be joined at all, keeping it means failing again immediately,
            // which is a loop that ends in giving up. Then a coarse route beats no route.
            if (_pendingPrefix == StepPrefix.Blocked &&
                _preferPlayerGraph && ObjectNavigator.LastGuidedRouteGraph == 0 &&
                _corners != null && _corners.Count > 1 && !_freeform)
            {
                _log?.LogInfo("[GUIDE] Ignoring a coarse re-route after a block; keeping the current route");
                NextLeg(p, _pendingPrefix);
                return;
            }

            _freeform = false;
            _route = route;
            _lastRouteAt = Time.realtimeSinceStartup;
            _log?.LogInfo($"[GUIDE] Route: {route.Count} waypoints to {_target.Label}");
        }

        _corners = Simplify(_route, CornerEpsilon);
        _cornerIndex = _corners.Count > 1 ? 1 : 0;

        // Log where the corners actually are. Reading the route's shape out of the legs it produces
        // is guesswork, and guesswork is how three rounds went by blaming the wrong thing.
        var shape = new System.Text.StringBuilder();
        for (int i = 0; i < _corners.Count && i < 10; i++)
            shape.Append($"{i}:({_corners[i].x:F0},{_corners[i].y:F0}) ");
        _log?.LogInfo($"[GUIDE] Route simplified to {_corners.Count} corners: {shape}");
        NextLeg(p, _pendingPrefix);
    }

    /// <summary>
    /// Nothing left to try. Only the auto-walker's own recovery path may hand over to the beacon
    /// (which auto-walks on its A* handoff) — a Ctrl+B session just says so and stops, because a
    /// key pressed to walk THEMSELVES must never take the controls.
    /// </summary>
    private static void GiveUp(string reason)
    {
        var target = _target;
        var p = MainGame.me?.player?.pos ?? Vector2.zero;
        bool beacon = _allowBeacon;
        _log?.LogWarning($"[GUIDE] Giving up on turn-by-turn ({reason}); beacon fallback allowed: {beacon}");
        Stop(announce: false);

        if (beacon)
        {
            ScreenReader.Say(Loc.Fmt("guide.no_route", target.Label), interrupt: true);
            ObjectNavigator.StartBeaconFor(target);
            return;
        }

        // The mode stays on and so does the target: walking a few tiles will quietly try again
        // from there, which is often all it takes. Say why only now and then — the retry loop
        // must not turn into nagging.
        KeepForRetry(target);
        if (Time.realtimeSinceStartup - _lastLostAnnounce < LostAnnounceInterval) return;
        _lastLostAnnounce = Time.realtimeSinceStartup;

        // Leave them with the bearing and the distance, and leave their legs alone.
        ScreenReader.Say(Loc.Fmt("guide.lost", target.Label,
                                 ObjectNavigator.CompassWord(p, target.Position),
                                 ObjectNavigator.DistanceWords(Vector2.Distance(p, target.Position))),
                         interrupt: true);
    }

    /// Distance from a point to a line segment — the one piece of geometry everything here needs.
    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 0.0001f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * t);
    }

    /// How far a point sits off the route polyline — "am I still on it at all", on its own.
    private static float DistanceToRoute(Vector2 p)
    {
        if (_route == null || _route.Count < 2) return float.MaxValue;

        float best = float.MaxValue;
        for (int i = 0; i < _route.Count - 1; i++)
        {
            var a = _route[i];
            var ab = _route[i + 1] - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 0.0001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            float d = Vector2.Distance(p, a + ab * t);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// The nearest point on the route ahead of us — the spot to step onto to be back on the path.
    /// Searched from the corner we are heading to, never behind it, for the same reason the corner
    /// cursor only moves forward.
    /// </summary>
    private static Vector2 NearestRoutePoint(Vector2 p)
    {
        if (_corners == null || _corners.Count < 2) return p;

        var best = p;
        float bestDist = float.MaxValue;
        for (int i = Mathf.Max(1, _cornerIndex); i < _corners.Count; i++)
        {
            var a = _corners[i - 1];
            var b = _corners[i];
            var ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 0.0001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            var proj = a + ab * t;
            float d = Vector2.Distance(p, proj);
            if (d < bestDist) { bestDist = d; best = proj; }
        }
        return best;
    }

    /// <summary>
    /// A point roughly <paramref name="ahead"/> further along the route than the player, walked
    /// forward from the corner they are currently heading for.
    ///
    /// It used to find the nearest piece of the route and walk forward from there — the same
    /// nearest-piece mistake that made the corner cursor bounce, and with the same result: where a
    /// route doubles back, the nearest piece can be the one going the other way, so the "aim" fell
    /// behind the player and the instructions alternated north, south, north.
    /// </summary>
    private static Vector2 RouteAheadPoint(Vector2 p, float ahead)
    {
        if (_corners == null || _corners.Count == 0) return _dest;

        var cursor = p;
        float remaining = ahead;
        for (int i = Mathf.Clamp(_cornerIndex, 0, _corners.Count - 1); i < _corners.Count; i++)
        {
            float len = Vector2.Distance(cursor, _corners[i]);
            if (len >= remaining && len > 0.001f)
                return cursor + (_corners[i] - cursor).normalized * remaining;
            remaining -= len;
            cursor = _corners[i];
        }
        return _corners[_corners.Count - 1];
    }

    // ---- legs ---------------------------------------------------------------------------

    /// <summary>
    /// Work out the next single-direction instruction from where the player is standing, look one
    /// turn further ahead so it can be announced with it, and speak it.
    /// </summary>
    private static void NextLeg(Vector2 from, StepPrefix prefix)
    {
        // A step taken to get out of a pocket is finished: plan properly from where they now are,
        // rather than resuming a route that could not be joined from where they were.
        if (_rerouteWhenLegDone && prefix == StepPrefix.Next)
        {
            _rerouteWhenLegDone = false;
            _preferPlayerGraph = true;
            _lastRouteAt = 0f;                 // this one is not subject to the cooldown
            RequestRoute(from, StepPrefix.Correction);
            return;
        }

        if (Vector2.Distance(from, _dest) <= GoalTolerance)
        {
            Arrive();
            return;
        }

        // Wandered off the route entirely — the route we are scoring against is the wrong one.
        if (!_freeform && _route != null && DistanceToRoute(from) > StrayRerouteDistance)
        {
            _log?.LogInfo("[GUIDE] Off the route; re-routing");
            RequestRoute(from, StepPrefix.Correction);
            return;
        }

        // A detour has just been walked, and it was walked for a reason: the direction it was
        // taken to clear is now open, and it is the one to take before anything else has a say.
        if (_clearedAxis != Vector2.zero)
        {
            var cleared = _clearedAxis;
            _clearedAxis = Vector2.zero;

            var aim = _corners != null && _cornerIndex < _corners.Count ? _corners[_cornerIndex] : _dest;
            float want = Vector2.Dot(aim - from, cleared);
            if (prefix == StepPrefix.Next &&
                Vector2.Distance(from, _clearedFrom) <= CornerReachedTolerance * 2f &&
                want > 0f &&
                AxisLeg(from, cleared, Mathf.Max(want, SidestepProbeAhead), out var onward))
            {
                _legSource = "past-the-obstacle";
                Commit(onward, null, prefix, from);
                return;
            }
        }

        // Walked in a circle: this step undoes the last, which undid the one before it. Whatever the
        // mod is reasoning from is wrong - usually a route that runs through something solid, with
        // each end of the obstruction pointing at the other. Another step of the same reasoning
        // gives another lap, so throw the plan away and get a fresh one from here, on the graph that
        // knows the fences.
        if (_oscillating)
        {
            _oscillating = false;
            _log?.LogWarning($"[GUIDE] Walking in circles at {from}; the plan is wrong, re-routing");
            _preferPlayerGraph = true;
            _lastRouteAt = 0f;
            _clearedAxis = Vector2.zero;
            RequestRoute(from, StepPrefix.Replan);
            return;
        }

        var started = Time.realtimeSinceStartup;
        if (!TryFindLeg(from, out var leg))
        {
            // Right on top of it: say the last metre or two outright rather than re-routing.
            if (Vector2.Distance(from, _dest) <= FinalApproachDistance && TryFinalApproach(from, out leg))
            {
                Commit(leg, null, prefix, from);
                return;
            }

            // Nothing on the route can be reached from here. That is not the player's doing and
            // not a stray step — it means the PLAN is unusable from where they stand, which is
            // exactly what a coarse route through a fenced yard looks like from inside the fence.
            // Get a route from the graph that knows the fences.
            _log?.LogWarning($"[GUIDE] Cannot join the route at {from}: {DescribePhysics(from)}");
            _preferPlayerGraph = true;

            if (Time.realtimeSinceStartup - _lastRouteAt >= BoxedInRerouteCooldown)
            {
                RequestRoute(from, StepPrefix.Correction);
                return;
            }

            // Too soon for another plan: get out of the pocket, and forbid undoing that step, or
            // the route pulls them straight back in and the two take turns forever.
            if (TryBackOut(from, out leg))
            {
                // Re-plan the moment they are clear, and do not bar the way back in the meantime:
                // barring it once meant barring the only way home, because the pocket happened to
                // lie between the player and where they were going.
                _rerouteWhenLegDone = true;
                Commit(leg, null, StepPrefix.Replan, from);
                return;
            }

            if (TryDeadReckon(from, out leg))
            {
                Commit(leg, null, StepPrefix.Replan, from);
                return;
            }

            RequestRoute(from, StepPrefix.Correction);
            return;
        }

        // One turn of lookahead, so the player knows which key comes next before they need it.
        // Never hint the REVERSE of the leg being given: "walk 4 south, then north" is the way back
        // and reads as nonsense. It happens when the step ends somewhere the route wants to undo,
        // and the honest answer there is to say nothing and re-measure on arrival.
        // The leg builder knows what comes next when it built an L or a staircase — no guessing,
        // and no chance of it contradicting the step it is attached to.
        string nextDir = _pendingNextDir;
        if (nextDir != null && PhysicsClearDistance(leg.End, AxisOf(nextDir), MinLegLength) < MinLegLength)
            nextDir = null;

        if (nextDir == null)
        {
            // Planning from the far end of the leg moves the corner cursor forward as a side
            // effect, and the player is not there yet — put it back, or arriving at the corner
            // would find the guidance already aiming past it with no way to look back.
            int savedCorner = _cornerIndex;
            var savedSource = _legSource;
            _lookahead = true;

            if (TryFindLeg(leg.End, out var after) && after.Dir != leg.Dir &&
                Vector2.Dot(after.Axis, leg.Axis) > -0.9f &&
                // And never hint a direction that is solid from there: "walk 1 metre west, then
                // south" — south being the fence just walked into — is worse than no hint at all.
                PhysicsClearDistance(leg.End, after.Axis, MinLegLength) >= MinLegLength)
                nextDir = after.Dir;

            _lookahead = false;
            _legSource = savedSource;     // or the log names the lookahead's reasoning, not this step's
            _cornerIndex = savedCorner;
            _pendingNextDir = null;
        }

        var ms = (Time.realtimeSinceStartup - started) * 1000f;
        if (ms > 8f) _log?.LogInfo($"[GUIDE] Leg search took {ms:F0}ms");

        _routeAttempts = 0;
        Commit(leg, nextDir, prefix, from);
    }

    private static void Commit(Leg leg, string nextDir, StepPrefix prefix, Vector2 from)
    {
        // Never say the same thing twice in a row within a breath of itself. Any loop that manages
        // to re-plan the identical step — and one did, saying "walk 1 metre east" some thirty times
        // while the player stood still — is a bug, but the player should not have to listen to it
        // while it is found. The step still takes effect; it just is not repeated aloud.
        bool repeat = _hasLeg && leg.Dir == _leg.Dir &&
                      Mathf.Abs(leg.Length - _leg.Length) < 0.2f * TileSize &&
                      Vector2.Distance(leg.Start, _leg.Start) < 0.2f * TileSize &&
                      Time.realtimeSinceStartup - _lastSpokeAt < RepeatSuppressSeconds;
        if (repeat)
        {
            _log?.LogWarning($"[GUIDE] Suppressed a repeat of the same step ({leg.Dir}, " +
                             $"{leg.Length / TileSize:F1} tiles) at {from}");
            _leg = leg;
            _hasLeg = true;
            _settleUntil = Time.realtimeSinceStartup + SettleSeconds;
            return;
        }

        // A then B then A on one axis is walking on the spot. Noted here, where every step passes,
        // and acted on when the next one is asked for.
        _oscillating = Vector2.Dot(leg.Axis, _lastLegAxis) < -0.9f &&
                       Vector2.Dot(_lastLegAxis, _prevLegAxis) < -0.9f;
        _prevLegAxis = _lastLegAxis;
        _lastLegAxis = leg.Axis;

        _leg = leg;
        _hasLeg = true;
        _nextDir = nextDir;
        _settleUntil = Time.realtimeSinceStartup + SettleSeconds;
        if (prefix == StepPrefix.Correction || prefix == StepPrefix.Blocked ||
            prefix == StepPrefix.Replan)
            _lastCorrection = Time.realtimeSinceStartup;
        ResetProgress(from, Vector2.Dot(leg.End - from, leg.Axis));

        _lastSpokeAt = Time.realtimeSinceStartup;

        var why = _legSource ?? "?";
        _legSource = null;
        var aimingAt = _corners != null && _cornerIndex < _corners.Count
            ? $" [corner {_cornerIndex} at ({_corners[_cornerIndex].x:F0},{_corners[_cornerIndex].y:F0})]"
            : "";
        _log?.LogInfo($"[GUIDE] Leg: {leg.Length / TileSize:F1} tiles {leg.Dir} from {leg.Start} to {leg.End}" +
                      (nextDir != null ? $", then {nextDir}" : "") + aimingAt + $" via {why}");

        // Openers that name the target: said as ONE utterance with the step, because anything said
        // separately is cut off by the step arriving a frame or two later.
        if (prefix == StepPrefix.Retarget)
        {
            ScreenReader.Say(nextDir == null
                                 ? Loc.Fmt("guide.retarget_step", _target.Label, Meters(leg.Length), leg.Dir)
                                 : Loc.Fmt("guide.retarget_step_then", _target.Label, Meters(leg.Length), leg.Dir, nextDir),
                             interrupt: true);
            return;
        }

        if (prefix == StepPrefix.Door)
        {
            ScreenReader.Say(nextDir == null
                                 ? Loc.Fmt("guide.door_step", _target.Label, Meters(leg.Length), leg.Dir)
                                 : Loc.Fmt("guide.door_step_then", _target.Label, Meters(leg.Length), leg.Dir, nextDir),
                             interrupt: true);
            return;
        }

        if (prefix == StepPrefix.Start)
        {
            var away = ObjectNavigator.DistanceWords(
                Vector2.Distance(MainGame.me?.player?.pos ?? from, _target.Position));
            ScreenReader.Say(nextDir == null
                                 ? Loc.Fmt("guide.start_step", _target.Label, away, Meters(leg.Length), leg.Dir)
                                 : Loc.Fmt("guide.start_step_then", _target.Label, away, Meters(leg.Length), leg.Dir, nextDir),
                             interrupt: true);
            return;
        }

        // "The fence is in the way. Walk 6 meters south, then east."
        if (prefix == StepPrefix.Blocked && _blockerName != null)
        {
            var blocker = _blockerName;
            _blockerName = null;
            ScreenReader.Say(nextDir == null
                                 ? Loc.Fmt("guide.blocked_by", blocker, Meters(leg.Length), leg.Dir)
                                 : Loc.Fmt("guide.blocked_by_then", blocker, Meters(leg.Length), leg.Dir, nextDir),
                             interrupt: true);
            return;
        }
        _blockerName = null;

        var key = prefix switch
        {
            StepPrefix.Next => "guide.next",
            StepPrefix.Correction => "guide.correction",
            StepPrefix.Blocked => "guide.blocked",
            StepPrefix.Replan => "guide.replan",
            _ => "guide.step",
        };
        ScreenReader.Say(nextDir == null
                             ? Loc.Fmt(key, Meters(leg.Length), leg.Dir)
                             : Loc.Fmt(key + "_then", Meters(leg.Length), leg.Dir, nextDir),
                         interrupt: true);
    }

    /// <summary>
    /// The ladder. A cardinal leg over walkable ground is what we want; the rungs below it exist
    /// so that "I cannot see a way" is never the answer while the player is standing somewhere
    /// perfectly ordinary. Each rung is a worse instruction but still a real one, and the blocked
    /// watch catches it when the ground disagrees.
    /// </summary>
    private static bool TryFindLeg(Vector2 from, out Leg leg)
    {
        _pendingNextDir = null;
        leg = default;

        // No corners means no route (an interior with nothing to plan on): head at the target.
        if (_corners == null || _corners.Count == 0)
            return TryLegToward(from, _dest, out leg, MinNudgeLength) || FindWayRound(from, out leg);

        AdvanceCorner(from);

        // The corner in hand gets the short floor: when a metre is all that stands between the
        // player and the gate, "walk 1 metre north" is the whole instruction, and refusing to say
        // it is what let the mod skip to the next corner and send them back along the fence.
        _legSource = "corner";
        if (_cornerIndex < _corners.Count &&
            TryLegToward(from, _corners[_cornerIndex], out leg, MinNudgeLength)) return true;

        // The corner cannot be reached in one step, and the reason is physical: the way to it is
        // solid from right here. Then the answer is the way ROUND it — asked now, before any of the
        // fallbacks below get to pick a different aim.
        //
        // This is the last of the bouncing. Standing a foot east of a gate with the route running
        // south through it, the step south is barred and the step west is too short to be worth
        // saying, so the mod aimed six metres further along the route instead — and six metres
        // along, the route has come through the gate and turned EAST. So it said "walk two metres
        // east", away from the gate; the next plan said west; and back and forth along the fence.
        // Nothing about that was a bad route: it was the mod answering "which way round this?"
        // with "somewhere else entirely".
        bool triedWayRound = false;
        if (_cornerIndex < _corners.Count)
        {
            var toCorner = _corners[_cornerIndex] - from;
            var main = Mathf.Abs(toCorner.x) >= Mathf.Abs(toCorner.y)
                ? new Vector2(Mathf.Sign(toCorner.x), 0f)
                : new Vector2(0f, Mathf.Sign(toCorner.y));

            if (PhysicsClearDistance(from, main, MinLegLength) < MinLegLength)
            {
                triedWayRound = true;
                if (FindWayRound(from, out leg)) return true;
            }
        }

        // Back onto the line first. Being a few units off the route is enough to turn a clear
        // walk west into walking into the fence the route runs beside, and the fix is a short step
        // sideways rather than anything clever.
        _legSource = "back-on-route";
        var onRoute = NearestRoutePoint(from);
        if (Vector2.Distance(from, onRoute) >= MinNudgeLength &&
            TryLegToward(from, onRoute, out leg, MinNudgeLength)) return true;

        // Then along the route toward the corner. These points sit ON the path, so they are
        // walkable, and they keep the player following it instead of striking out on their own.
        // Longest first, so open ground still gets long strides.
        _legSource = "along-route";
        foreach (float ahead in RouteAimDistances)
        {
            if (TryLegToward(from, RouteAheadPoint(from, ahead * TileSize), out leg)) return true;
        }

        // Only now consider corners further on — and WITHOUT moving the cursor, because a corner
        // being unreachable this second is a passing condition, not progress. Skipping ahead used
        // to walk the player off the route (north, when the route went south then west), and the
        // next route promptly dragged them back: the north-south bouncing, in one move.
        _legSource = "later-corner";
        for (int j = _cornerIndex + 1; j < _corners.Count && j <= _cornerIndex + 2; j++)
        {
            if (TryLegToward(from, _corners[j], out leg)) return true;
        }

        _legSource = "straight-at-target";
        if (TryLegToward(from, _dest, out leg, MinNudgeLength)) return true;

        return !triedWayRound && FindWayRound(from, out leg);

        // Nothing else belongs here. Backing out and dead reckoning are RECOVERIES, and this method
        // is also used to look one turn ahead — a lookahead that answered with a recovery is how
        // "walk 1 metre south, then north" got spoken, and how backing out ended up alternating
        // with the route pulling straight back. NextLeg owns recovery; see there.
    }

    /// <summary>
    /// Nothing along the route can be reached in a straight step. Go looking for the way through:
    /// step across the direction we want until it opens up again. That is how a gate is found in a
    /// fence, instead of waiting for the player to walk into the fence to discover it is there.
    /// Deliberately the LAST thing tried, and tried once — it sweeps eight metres of colliders, and
    /// running it for every aim point in turn cost eight such sweeps in a single frame.
    /// </summary>
    private static bool FindWayRound(Vector2 from, out Leg leg)
    {
        leg = default;

        // Not while looking one step ahead. This sweeps eight metres of colliders to answer a
        // question the lookahead only needs a word from, and - worse - a detour found here would be
        // remembered as the plan for a step the player has not been given yet.
        if (_lookahead) return false;
        var toward = _corners != null && _cornerIndex < _corners.Count ? _corners[_cornerIndex] : _dest;
        var d = toward - from;
        var xAxis = new Vector2(Mathf.Sign(d.x), 0f);
        var yAxis = new Vector2(0f, Mathf.Sign(d.y));
        _legSource = "way-round";

        // Across the longer leg of the journey first: that is the direction we are actually being
        // stopped in, and the one worth finding a gate for.
        return Mathf.Abs(d.x) >= Mathf.Abs(d.y)
            ? TryDetour(from, xAxis, toward, out leg) || TryDetour(from, yAxis, toward, out leg)
            : TryDetour(from, yAxis, toward, out leg) || TryDetour(from, xAxis, toward, out leg);
    }

    /// <summary>
    /// Which corner we are walking to. Progress along a route is ONE WAY: the cursor never moves
    /// back, and only moves on once the corner is reached or passed.
    ///
    /// It used to pick the nearest segment of the route instead, which sounds reasonable and is
    /// not: a route that comes back on itself — out of a yard, along a fence, back past where you
    /// started — has two segments running close together, and "nearest" flips between them as you
    /// walk. Each flip pointed at the far end of the other segment, so the player was sent west,
    /// then east, then west again, walking the same forty metres over and over. Nothing else in
    /// here caused that; this did.
    ///
    /// Straying far enough to make this cursor wrong is handled where it should be — by noticing
    /// the player is off the route entirely and asking for a new one.
    /// </summary>
    private static void AdvanceCorner(Vector2 from)
    {
        if (_corners.Count < 2) { _cornerIndex = 0; return; }
        _cornerIndex = Mathf.Clamp(_cornerIndex, 1, _corners.Count - 1);

        while (_cornerIndex < _corners.Count - 1)
        {
            var corner = _corners[_cornerIndex];

            // Standing on it. This has to be TIGHT — tighter than "close enough to call the step
            // done" — because a corner is often a gate, and being a metre short of a gate means
            // being on the wrong side of the fence. Consuming it there hands out the next corner,
            // which lies beyond the gate, and walking to that from the wrong side means walking
            // back the way you came.
            if (Vector2.Distance(from, corner) <= CornerReachedTolerance) { _cornerIndex++; continue; }

            // Or past it: beyond the corner along the leg that led to it, AND actually on that
            // line rather than off to one side of it.
            var approach = corner - _corners[_cornerIndex - 1];
            if (approach.sqrMagnitude > 0.0001f)
            {
                var dir = approach.normalized;
                float beyond = Vector2.Dot(from - corner, dir);
                float aside = Mathf.Abs(Vector2.Dot(from - corner, new Vector2(-dir.y, dir.x)));
                if (beyond > 0f && aside <= CornerPassedLateral) { _cornerIndex++; continue; }
            }

            break;
        }
    }

    /// <summary>
    /// Turn "get from here to there" into one held key. A single axis when the target lines up;
    /// otherwise an L — all the way along one axis, then the other — but only when BOTH arms are
    /// clear, so the L is a shortcut that genuinely works rather than a guess across open ground
    /// that happens to contain a fence. When neither L is clear, a staircase hugging the direct
    /// line does the job in shorter alternating steps.
    /// </summary>
    private static bool TryLegToward(Vector2 from, Vector2 target, out Leg leg,
                                     float minLength = MinLegLength)
    {
        leg = default;
        var d = target - from;
        float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y);
        var xAxis = new Vector2(Mathf.Sign(d.x), 0f);
        var yAxis = new Vector2(0f, Mathf.Sign(d.y));

        if (ax < minLength && ay < minLength) return false;            // we are already there
        if (ay < minLength) return AxisLeg(from, xAxis, ax, out leg, minLength);
        if (ax < minLength) return AxisLeg(from, yAxis, ay, out leg, minLength);

        bool xFirst = ax >= ay;
        var first = xFirst ? xAxis : yAxis;
        var second = xFirst ? yAxis : xAxis;
        float firstLen = xFirst ? ax : ay;
        float secondLen = xFirst ? ay : ax;

        if (TryLShape(from, first, firstLen, second, secondLen, out leg, minLength)) return true;
        if (TryLShape(from, second, secondLen, first, firstLen, out leg, minLength)) return true;

        return StaircaseLeg(from, target, first, second, out leg, minLength);
    }

    /// How far along the route to aim when the corner itself cannot be reached, longest first.
    private static readonly float[] RouteAimDistances = { 6f, 4f, 2.5f, 1.5f };

    /// <summary>
    /// Boxed in. Take the way with the most room, whatever the route thinks — standing still is
    /// not an option, and a step into open space always beats a step into what is holding you.
    /// </summary>
    private static bool TryBackOut(Vector2 from, out Leg leg, Vector2 avoid = default)
    {
        leg = default;
        var toward = _dest - from;
        float best = MinLegLength;
        bool bestHelps = false;

        foreach (var axis in Cardinals)
        {
            // Not the direction that just failed. Answering "you cannot go south" with "walk four
            // metres south" is the mod contradicting itself in consecutive sentences, and it is
            // what the player hears as flailing.
            if (avoid != default(Vector2) && Vector2.Dot(axis, avoid) > 0.5f) continue;

            float reach = Mathf.Min(BackOutLength, PhysicsClearDistance(from, axis, BackOutLength));
            if (reach < MinLegLength) continue;

            // Room matters, but not as much as not walking away from where they are going: a way
            // out that keeps some progress beats a longer one straight backwards.
            bool helps = Vector2.Dot(toward, axis) > 0f;
            if ((helps && !bestHelps) || (helps == bestHelps && reach > best))
            {
                best = reach;
                bestHelps = helps;
                leg = MakeLeg(from, from + axis * reach);
            }
        }

        if (leg.Length < MinLegLength) return false;
        _log?.LogInfo($"[GUIDE] Boxed in at {from}; backing out {best / TileSize:F1} tiles {leg.Dir}");
        return true;
    }

    /// One held key, as far as the colliders allow.
    private static bool AxisLeg(Vector2 from, Vector2 axis, float length, out Leg leg,
                                float minLength = MinLegLength)
    {
        leg = default;
        if (IsBlockedDirection(from, axis)) return false;
        length = Mathf.Min(length, MaxLegLength);
        float reach = Mathf.Min(length, PhysicsClearDistance(from, axis, length));
        if (reach < minLength) return false;
        leg = MakeLeg(from, from + axis * reach);
        return true;
    }

    /// <summary>
    /// Both arms of an L, checked end to end. The player is given the first arm and told the second
    /// as the follow-up, which is why this is worth verifying: it is the difference between two
    /// long instructions and a dozen short ones.
    /// </summary>
    private static bool TryLShape(Vector2 from, Vector2 a1, float len1, Vector2 a2, float len2,
                                  out Leg leg, float minLength = MinLegLength)
    {
        leg = default;
        if (IsBlockedDirection(from, a1)) return false;
        if (len1 < minLength || len1 > MaxLegLength || len2 > MaxLegLength) return false;
        if (PhysicsClearDistance(from, a1, len1) < len1) return false;

        var corner = from + a1 * len1;
        if (PhysicsClearDistance(corner, a2, len2) < len2) return false;

        leg = MakeLeg(from, corner);
        _pendingNextDir = len2 >= minLength ? CardinalWord(a2) : null;
        return true;
    }

    /// <summary>
    /// The diagonal case with something in the way: walk the dominant axis while staying close to
    /// the straight line to the target, then the other axis, and so on. Short steps, but each one
    /// is on ground next to a line the route says is walkable.
    /// </summary>
    private static bool StaircaseLeg(Vector2 from, Vector2 target, Vector2 first, Vector2 second,
                                     out Leg leg, float minLength = MinLegLength)
    {
        leg = default;
        if (IsBlockedDirection(from, first))
            return AxisLeg(from, second, Mathf.Abs(second.x != 0f ? target.x - from.x : target.y - from.y),
                           out leg, minLength);

        float reach = PhysicsClearDistance(from, first, MaxLegLength);
        float travelled = 0f;

        for (float t = StepSize; t <= reach + 0.01f; t += StepSize)
        {
            if (DistanceToSegment(from + first * t, from, target) > StairDeviation) break;
            travelled = t;
        }

        if (travelled < minLength)
        {
            // Cannot make headway on this axis at all — try the other one before giving up.
            return AxisLeg(from, second, Mathf.Abs(second.x != 0f ? target.x - from.x : target.y - from.y),
                           out leg, minLength);
        }

        leg = MakeLeg(from, from + first * travelled);
        _pendingNextDir = CardinalWord(second);
        return true;
    }

    /// <summary>
    /// Boil the route down to the corners that matter. A route has a waypoint every node — on the
    /// fine graph that is every eight units — and nobody needs to be told about those. Standard
    /// Douglas-Peucker, iterative so a long route cannot blow the stack.
    /// </summary>
    private static List<Vector2> Simplify(List<Vector2> pts, float epsilon)
    {
        if (pts == null || pts.Count <= 2) return pts == null ? new List<Vector2>() : new List<Vector2>(pts);

        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;

        var stack = new Stack<KeyValuePair<int, int>>();
        stack.Push(new KeyValuePair<int, int>(0, pts.Count - 1));

        while (stack.Count > 0)
        {
            var span = stack.Pop();
            int a = span.Key, b = span.Value;
            if (b <= a + 1) continue;

            float worst = 0f;
            int worstIdx = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > worst) { worst = d; worstIdx = i; }
            }

            if (worstIdx < 0 || worst <= epsilon) continue;
            keep[worstIdx] = true;
            stack.Push(new KeyValuePair<int, int>(a, worstIdx));
            stack.Push(new KeyValuePair<int, int>(worstIdx, b));
        }

        var result = new List<Vector2>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    /// <summary>
    /// Last rung: aim at the route a few tiles ahead and give its dominant axis, ground or no
    /// ground. Something is always better than "I do not know" for someone who cannot see.
    /// </summary>
    private static bool TryDeadReckon(Vector2 from, out Leg leg)
    {
        leg = default;
        var aim = _freeform ? _dest : RouteAheadPoint(from, DeadReckonAhead);
        var d = aim - from;
        if (d.magnitude < MinLegLength) d = _dest - from;
        if (d.magnitude < MinLegLength) return false;

        // Both axes of the aim, dominant first — but take the other one if the dominant leads
        // straight into something the ground data already knows is solid. This is the rung that
        // used to say "north" while the player stood at the wall of their own house.
        var dominant = Mathf.Abs(d.x) >= Mathf.Abs(d.y)
            ? new Vector2(Mathf.Sign(d.x), 0f)
            : new Vector2(0f, Mathf.Sign(d.y));
        var other = dominant.x != 0f ? new Vector2(0f, Mathf.Sign(d.y)) : new Vector2(Mathf.Sign(d.x), 0f);

        Leg? fallback = null;
        foreach (var axis in new[] { dominant, other })
        {
            if (axis.sqrMagnitude < 0.5f) continue;                  // no component on this axis
            float onAxis = Mathf.Min(Mathf.Abs(axis.x != 0f ? d.x : d.y), MaxLegLength);
            if (onAxis < MinLegLength) continue;

            // Even with no map data to go on, the colliders still know: never dead-reckon further
            // than the player can physically walk, and prefer an axis that is not solid at all.
            float reach = PhysicsClearDistance(from, axis, onAxis);
            if (reach >= MinLegLength)
            {
                leg = MakeLeg(from, from + axis * Mathf.Min(onAxis, reach));
                _log?.LogInfo($"[GUIDE] Dead reckoning toward {aim} ({leg.Dir}, {reach / TileSize:F1} tiles clear)");
                return true;
            }

            // Solid within a step. Keep it only as the thing to say if the other axis is no better,
            // and keep it SHORT — a guess into a wall should cost a metre, not the whole leg.
            fallback ??= MakeLeg(from, from + axis * MinLegLength);
        }

        if (fallback == null) return false;

        // Neither axis looks clear — say the dominant one anyway rather than nothing, but only a
        // short way, so a guess that turns out to be a wall costs two metres and not nine. This is
        // the only instruction the mod gives that it cannot stand behind, so log what the colliders
        // actually saw: if these lines are common, the probe is the thing to look at.
        _log?.LogWarning($"[GUIDE] Dead reckoning blind at {from}: {DescribePhysics(from)}");
        var f = fallback.Value;
        float capped = Mathf.Min(f.Length, DeadReckonBlindLength);
        leg = MakeLeg(f.Start, f.Start + f.Axis * capped);
        _log?.LogWarning($"[GUIDE] Dead reckoning toward {aim} ({leg.Dir}), no clear ground either way");
        return true;
    }

    /// <summary>
    /// The last metre or two onto the interaction tile, with no walkability test: at this range
    /// the player is inside the object's own footprint and the navmesh under it says nothing.
    /// </summary>
    private static bool TryFinalApproach(Vector2 from, out Leg leg)
    {
        leg = default;
        var d = _dest - from;
        var axis = Mathf.Abs(d.x) >= Mathf.Abs(d.y)
            ? new Vector2(Mathf.Sign(d.x), 0f)
            : new Vector2(0f, Mathf.Sign(d.y));
        float onAxis = Mathf.Abs(axis.x != 0f ? d.x : d.y);
        if (onAxis < MinLegLength) return false;
        leg = MakeLeg(from, from + axis * onAxis);
        return true;
    }

    private static Leg MakeLeg(Vector2 from, Vector2 end)
    {
        var d = end - from;
        float len = d.magnitude;
        var axis = len > 0.001f ? d / len : Vector2.up;
        return new Leg
        {
            Start = from,
            End = end,
            Axis = axis,
            Perp = new Vector2(-axis.y, axis.x),
            Length = len,
            // Always one of the four. Every leg this class produces is a single held key, and a
            // player who cannot walk a diagonal cannot act on one either.
            Dir = CardinalWord(d),
        };
    }

    // ---- wording ------------------------------------------------------------------------

    private static string Meters(float worldDistance) =>
        Loc.Fmt("guide.meters", Mathf.Max(1, Mathf.RoundToInt(worldDistance / TileSize)));

    /// The axis a spoken direction refers to, for checking a hint before giving it.
    private static Vector2 AxisOf(string dir)
    {
        if (dir == Loc.Get("compass.north")) return new Vector2(0f, 1f);
        if (dir == Loc.Get("compass.south")) return new Vector2(0f, -1f);
        if (dir == Loc.Get("compass.east")) return new Vector2(1f, 0f);
        if (dir == Loc.Get("compass.west")) return new Vector2(-1f, 0f);
        return Vector2.zero;
    }

    /// The four directions a single held key produces. +x is east, +y is north.
    private static string CardinalWord(Vector2 d)
    {
        if (Mathf.Abs(d.x) >= Mathf.Abs(d.y))
            return Loc.Get(d.x >= 0f ? "compass.east" : "compass.west");
        return Loc.Get(d.y >= 0f ? "compass.north" : "compass.south");
    }
}
