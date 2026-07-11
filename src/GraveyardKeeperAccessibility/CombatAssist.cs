namespace GraveyardKeeperAccessibility;

/// <summary>
/// Makes the game's real-time combat playable without sight. Combat in Graveyard Keeper means
/// facing a hostile mob and swinging a sword (GameKey.Attack, default Space); the hit lands only
/// if the character's facing (its combat collider, rotated to dir_angle each frame in
/// BaseCharacterAttack.UpdateComponent) points at the enemy. A blind player can't see where the
/// enemy is, so they swing into empty air and get killed.
///
/// This module solves three things:
///   1. AWARENESS  — announces enemies entering range, hits landing, enemy deaths, and the player
///                    taking damage; the V key scans the situation on demand.
///   2. AIMING      — every frame it turns the player to face the nearest enemy (via
///                    BaseCharacterComponent.LookAt), but only while the player isn't steering.
///                    Because ProcessAttack only re-faces on a NON-zero movement input
///                    (ProcessDirection ignores a zero vector), a stationary Space press then
///                    swings in the direction we aimed, so the hit connects.
///   3. ONE-KEY HIT — C faces the nearest enemy and performs the swing directly
///                    (attack.Perform), so the player doesn't even have to aim with Space.
///   4. AUTO-ATTACK  — X toggles a mode that swings on its own whenever an enemy is actually in
///                    melee reach and the player is standing still, so they don't have to jab C /
///                    Space (and waste energy) while still out of range. Moving cancels it, so you
///                    can always walk away to flee.
/// Toggle the awareness/auto-facing with B (in case it ever fights the player's own movement).
/// </summary>
internal static class CombatAssist
{
    private const float TileSize = 96f;
    private const float DetectRange = 12f * TileSize;    // announce enemies inside this radius
    private const float AutoFaceRange = 4f * TileSize;   // keep facing an enemy this close
    private const float AttackRange = 4f * TileSize;     // C will swing at an enemy this close
    private const float AdjacentRange = 1.8f * TileSize; // "enemy striking" danger range
    private const float AutoAttackRange = 2.4f * TileSize; // auto-attack only when a hit will connect
    private const float SwingReach = 1.5f * TileSize;      // auto-walk in until this close, then swing
    private const float ApproachRange = 6f * TileSize;     // auto-walk toward an enemy up to this far
    private const float MeleeHitReach = 1.7f * TileSize;   // deal a swing's damage deterministically within this
    private const float WhiffHoldSeconds = 1.5f;  // pause auto-swings after repeated misses
    private const int WhiffLimit = 2;             // consecutive misses before pausing

    private static ManualLogSource _log;
    private static bool _enabled = true;
    private static bool _autoAttack;   // X toggles auto-swinging at in-reach enemies (off by default)

    // Per-enemy HP bookkeeping, keyed by Unity instance id. We treat the highest HP ever seen for
    // an enemy as its max so we can report a percentage without resolving the obj_def HP formula.
    private static readonly Dictionary<int, float> _enemyMaxHp = new Dictionary<int, float>();
    private static readonly Dictionary<int, float> _enemyLastHp = new Dictionary<int, float>();
    // Keep the actual object per known enemy so that when one leaves the scan list we can tell a
    // real death (is_dead / hp<=0) apart from an enemy the player simply ran away from.
    private static readonly Dictionary<int, WorldGameObject> _knownEnemies = new Dictionary<int, WorldGameObject>();

    private static float _lastPlayerHp = -1f;
    private static float _hitCooldown;      // throttles "Hit" spam across rapid multi-frame damage
    private static float _strikeCooldown;   // throttles the "enemy striking" warning

    // Auto-attack whiff handling: the sword's combat collider only reaches ~1 tile and snaps to a
    // cardinal direction, so a swing at a mob milling just out of reach connects with nobody yet
    // still spends energy up front. Rather than tell the player to move, we walk them in (see
    // AutoApproach) and only swing point-blank; a brief hold after any residual miss stops us
    // draining energy on a stuck diagonal until the enemy's own movement lines it up.
    private static int _consecutiveWhiffs;
    private static float _reachHoldUntil;   // suppress auto-swings until this Time.time

    // Auto-walk toward an out-of-reach enemy by feeding the game its own movement input via
    // LazyInput.simulate_direction (so the player moves through the normal, collision-respecting
    // path — no scripted-control conflicts). We only ever clear what WE set.
    private static LazyInput _lazyInput;
    private static bool _weSetSimulate;     // true while our simulate_direction override is active
    private static bool _approaching;       // true this engagement so "Closing in" is said just once
    private static float _lastApproachSpeak;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[COMBAT] CombatAssist initialized (X auto-attack, C attack, V scan, B toggle)");
    }

    internal static void Update()
    {
        try
        {
            if (!MainGame.game_started || MainGame.me == null
                || MainGame.me.player == null || MainGame.me.save == null)
                return;

            var player = MainGame.me.player;
            var character = player.components?.character;
            if (character == null) return;

            if (_hitCooldown > 0f) _hitCooldown -= Time.deltaTime;
            if (_strikeCooldown > 0f) _strikeCooldown -= Time.deltaTime;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (!ctrl && Input.GetKeyDown(KeyCode.B))
            {
                _enabled = !_enabled;
                ScreenReader.Say(_enabled ? "Combat assist on" : "Combat assist off");
            }

            if (!ctrl && Input.GetKeyDown(KeyCode.X))
            {
                _autoAttack = !_autoAttack;
                if (!_autoAttack) ClearApproach();
                ScreenReader.Say(_autoAttack
                    ? "Auto attack on. I'll walk you to enemies and swing when in reach; press a movement key to take over."
                    : "Auto attack off");
            }

            var enemies = FindEnemies(player.pos);
            var nearest = Nearest(enemies, player.pos);
            // Destructible loot props (dungeon vases/pots) smashed by a sword swing, not a tool.
            // A blind player can't aim a manual swing, so C/X target the nearest one when no enemy
            // is in reach.
            var nearestBreakable = Nearest(FindBreakables(player.pos), player.pos);

            if (!ctrl && Input.GetKeyDown(KeyCode.V))
                AnnounceScan(player, enemies, nearest);

            if (!ctrl && Input.GetKeyDown(KeyCode.C))
                AttackNearest(player, character, nearest, nearestBreakable);

            if (_autoAttack)
                AutoAttack(player, character, enemies, nearestBreakable);
            else
                ClearApproach();

            if (_enabled)
                AutoFace(player, character, nearest);

            TrackFeedback(player, enemies, nearest);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[COMBAT] Update error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ── Aiming ────────────────────────────────────────────────────────────────────────────────

    // Turn the player to face the nearest enemy so a plain Space press connects. We skip this when
    // the player is steering (a non-zero movement input), because then the game faces the movement
    // direction anyway and overriding it would trap the player next to an enemy they're fleeing.
    private static void AutoFace(WorldGameObject player, BaseCharacterComponent character, WorldGameObject nearest)
    {
        try
        {
            if (nearest == null || !character.control_enabled) return;
            if (player.GetEquippedWeaponType() != ItemDefinition.ItemType.Sword) return;
            if (Vector2.Distance(player.pos, nearest.pos) > AutoFaceRange) return;

            // Player is actively walking/steering — let their own facing stand.
            if (LazyInput.GetDirection().magnitude > 0.01f) return;

            // LookAt only updates the animator when the direction actually changes, so calling it
            // every frame is cheap and keeps the swing collider tracking a moving enemy.
            character.LookAt(nearest);
        }
        catch { }
    }

    // ── One-key attack ──────────────────────────────────────────────────────────────────────────

    private static void AttackNearest(WorldGameObject player, BaseCharacterComponent character,
        WorldGameObject nearest, WorldGameObject nearestBreakable)
    {
        try
        {
            if (player.GetEquippedWeaponType() != ItemDefinition.ItemType.Sword)
            {
                ScreenReader.Say("Equip a sword to attack", interrupt: true);
                return;
            }

            // Prefer an enemy in reach; otherwise fall back to smashing the nearest loot prop
            // (vase/pot). Mirror the game's own energy gate
            // (BaseCharacterComponent.CheckEnegryForPlayerAtack) before any swing so we don't drive
            // energy negative or swing for nothing.
            bool enemyInReach = nearest != null && Vector2.Distance(player.pos, nearest.pos) <= AttackRange;
            if (enemyInReach)
            {
                if (!HasEnergyToAttack(player)) { ScreenReader.Say("Too tired to attack", interrupt: true); return; }
                PerformSwing(player, character, nearest);
                return;
            }

            bool propInReach = nearestBreakable != null && Vector2.Distance(player.pos, nearestBreakable.pos) <= AttackRange;
            if (propInReach)
            {
                // Loot props are destroyed directly (BreakProp) — one blow, no energy — so no
                // energy gate here, unlike the enemy path above.
                ScreenReader.Say($"Smashing {EnemyName(nearestBreakable)}", interrupt: true);
                BreakProp(nearestBreakable);
                return;
            }

            // Neither an enemy nor a prop is in reach.
            if (nearest == null && nearestBreakable == null)
            {
                ScreenReader.Say("No enemies nearby", interrupt: true);
                return;
            }

            // Something exists but is out of range — point the player at the closest of the two.
            var target = Nearest(new List<WorldGameObject> { nearest, nearestBreakable }, player.pos);
            ScreenReader.Say($"Too far, move closer. {CompassDirection(player.pos, target.pos)}", interrupt: true);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[COMBAT] AttackNearest error: {ex.Message}");
        }
    }

    // ── Auto-attack (X) ───────────────────────────────────────────────────────────────────────────

    // Drive the whole close-quarters loop: walk the player to the nearest enemy, then swing once
    // they're point-blank. Walking in (instead of announcing "step closer") is what a blind player
    // actually needs, and it means swings only fire at melee range so we stop draining energy on
    // whiffs. Pressing a movement key hands control straight back, so fleeing always works.
    private static void AutoAttack(WorldGameObject player, BaseCharacterComponent character,
        List<WorldGameObject> enemies, WorldGameObject nearestBreakable)
    {
        try
        {
            if (!character.control_enabled) { ClearApproach(); return; }
            if (player.GetEquippedWeaponType() != ItemDefinition.ItemType.Sword) { ClearApproach(); return; }

            // Player is steering by hand — release our auto-walk and don't pin them with swings.
            if (PlayerIsSteering()) { ClearApproach(); return; }

            var nearest = Nearest(enemies, player.pos);
            if (nearest == null)
            {
                ClearApproach();
                // No enemy: smash an adjacent loot prop (one blow, no energy — not gated).
                if (nearestBreakable != null && Vector2.Distance(player.pos, nearestBreakable.pos) <= AutoAttackRange)
                    BreakProp(nearestBreakable);
                return;
            }

            float dist = Vector2.Distance(player.pos, nearest.pos);

            // Out of melee reach: walk in if it's close enough to be worth it, otherwise stand pat
            // (don't march blindly across the map — awareness cues already flag distant enemies).
            if (dist > SwingReach)
            {
                if (dist <= ApproachRange) AutoApproach(player, nearest);
                else ClearApproach();
                return;
            }

            // Point-blank: stop walking and swing at the most hittable enemy (energy-gated).
            ClearApproach();
            var target = PickAutoAttackTarget(player, enemies);
            if (target == null) return;
            if (Time.time < _reachHoldUntil) return;         // brief pause after a residual whiff
            if (!HasEnergyToAttack(player)) return;
            PerformSwing(player, character, target, auto: true);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[COMBAT] AutoAttack error: {ex.Message}");
            ClearApproach();
        }
    }

    // ── Auto-approach (walk the player into melee range) ─────────────────────────────────────────

    private static LazyInput Lazy
    {
        get
        {
            if (_lazyInput == null) _lazyInput = UnityEngine.Object.FindObjectOfType<LazyInput>();
            return _lazyInput;
        }
    }

    // Any real movement key held means the player wants to steer themselves. We can't read
    // LazyInput.GetDirection() to detect this while we're overriding it via simulate_direction, so
    // we watch the raw keys (WASD + arrows cover the game's default bindings).
    private static bool PlayerIsSteering()
    {
        return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D)
            || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow)
            || Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow);
    }

    // Feed the game a movement input toward the enemy. LazyInput.GetDirection() returns
    // simulate_direction whenever it's non-zero, so the player's own UpdatePlayer walks them there
    // through the normal, collision-respecting movement path — no scripted control, no state fights.
    private static void AutoApproach(WorldGameObject player, WorldGameObject enemy)
    {
        var dir = enemy.pos - player.pos;
        if (dir.sqrMagnitude < 0.0001f) { ClearApproach(); return; }

        var lz = Lazy;
        if (lz == null) return;
        lz.simulate_direction = dir.normalized;
        _weSetSimulate = true;

        if (!_approaching)
        {
            _approaching = true;
            if (Time.time - _lastApproachSpeak >= 4f)
            {
                _lastApproachSpeak = Time.time;
                ScreenReader.Say($"Closing in, {CompassDirection(player.pos, enemy.pos)}", interrupt: false);
            }
        }
    }

    // Drop our movement override (only ever clearing what we set, so we never stomp a cutscene's).
    private static void ClearApproach()
    {
        if (_weSetSimulate)
        {
            if (_lazyInput != null) _lazyInput.simulate_direction = Vector2.zero;
            _weSetSimulate = false;
        }
        _approaching = false;
    }

    // Among enemies within melee reach, choose the one a cardinal-snapped swing is most likely to
    // hit: closest, but penalised for how far its bearing sits off the nearest N/E/S/W axis (the
    // combat collider only faces cardinals). Returns null when nothing is in reach, so we hold fire
    // instead of swinging at empty air.
    private static WorldGameObject PickAutoAttackTarget(WorldGameObject player, List<WorldGameObject> enemies)
    {
        WorldGameObject best = null;
        float bestScore = float.MaxValue;
        foreach (var e in enemies)
        {
            if (e == null) continue;
            var d = e.pos - player.pos;
            float dist = d.magnitude;
            if (dist > AutoAttackRange) continue;
            float bearing = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            // Distance from the bearing to its nearest cardinal (0..45 degrees). 1.6 px/degree makes
            // a fully diagonal (45 degree) mob cost ~0.75 tile of extra "distance" versus one dead
            // ahead, so a square-on mob is preferred without ignoring a much closer diagonal one.
            float misalign = Mathf.Abs(Mathf.DeltaAngle(bearing, Mathf.Round(bearing / 90f) * 90f));
            float score = dist + misalign * 1.6f;
            if (score < bestScore) { bestScore = score; best = e; }
        }
        return best;
    }

    // Called (via the swing's own result callback) after an auto-swing resolves. success is true iff
    // the game registered a hit. Repeated point-blank misses (a mob stuck exactly diagonal to the
    // cardinal-only collider) briefly pause swinging so we don't drain energy — the enemy's own
    // movement, or our re-approach, lines it up again. Silent: approach handles the positioning, and
    // the player asked us not to chatter. Any landed hit clears the streak immediately.
    private static void OnAutoSwingResult(bool success)
    {
        if (success)
        {
            _consecutiveWhiffs = 0;
            _reachHoldUntil = 0f;
            return;
        }

        _consecutiveWhiffs++;
        if (_consecutiveWhiffs < WhiffLimit) return;
        _reachHoldUntil = Time.time + WhiffHoldSeconds;
    }

    // Mirror the game's energy gate (BaseCharacterComponent.CheckEnegryForPlayerAtack): a swing
    // costs the weapon's params_on_use "energy" (stored negative), so don't swing below that.
    private static bool HasEnergyToAttack(WorldGameObject player)
    {
        float cost = 0f;
        try
        {
            var p = player.GetEquippedWeapon()?.definition?.params_on_use;
            if (p != null && !p.IsEmpty()) cost = p.Get("energy") * -1f;
        }
        catch { }
        return player.energy >= cost;
    }

    // Face the enemy and perform one sword swing. Facing first is what makes the hit connect (the
    // combat collider tracks anim_direction); ProcessAttack ignores a zero movement input, so a
    // stationary swing keeps the aim we just set. No-op if a swing is already in progress.
    private static bool PerformSwing(WorldGameObject player, BaseCharacterComponent character,
        WorldGameObject target, bool auto = false)
    {
        character.LookAt(target);
        var attack = character.attack;
        if (attack == null || attack.performing_attack) return false;

        // Wrap the game's own on-performed callback so we learn whether the swing actually connected
        // (success is set by CombatComponent.SuccessAttack only when a hit lands). Auto-swings feed
        // the whiff tracker; a manual C miss just nudges the player to close the gap.
        BaseCharacterAttack.AttackResult cb = success =>
        {
            try
            {
                if (auto) OnAutoSwingResult(success);
                else if (!success) ScreenReader.Say("Missed, get closer", interrupt: false);
            }
            catch { }
            character.OnPlayersAttackPerformed(success);
        };

        bool started = attack.Perform(character.anim_direction, 0, cb);

        // The sword swing is a CARDINAL lunge (CurvedAttack.Perform → CurveMove along a snapped
        // N/E/S/W direction): the collider only sweeps along that axis, so an enemy that's even
        // slightly off-axis is physically missed — the swing costs energy and hurts nobody. Since
        // a blind player can't nudge themselves onto the axis, deal the hit deterministically once
        // the swing actually starts, but only when the target is genuinely at melee range. This
        // routes through the game's own damage path (HitOther → WasHitBy, real weapon damage) and
        // shares CombatComponent._collided_ids with the physics collider, so it can't double-hit.
        if (started && target != null && !target.is_dead
            && Vector2.Distance(player.pos, target.pos) <= MeleeHitReach)
            ApplyMeleeHit(player, target);

        return started;
    }

    // Deterministically land the current swing on an in-reach enemy via the game's own attacker
    // path, so accessibility swings don't whiff on the cardinal lunge geometry. Requires an
    // in-progress attack (HitOther's own guard) — always true here because we just started one.
    private static void ApplyMeleeHit(WorldGameObject player, WorldGameObject target)
    {
        try
        {
            var pc = player.components?.combat;
            var tc = target.components?.combat;
            if (pc == null || tc == null) return;
            pc.HitOther(tc, GetSwordDamageType(player));
        }
        catch (Exception ex)
        {
            _log?.LogError($"[COMBAT] ApplyMeleeHit error: {ex.Message}");
        }
    }

    // The damage type the equipped weapon's swing collider deals. GetDamage maps it to the weapon's
    // "damage"/"damage_N" param, so reading it off the player's own collider keeps the deterministic
    // hit identical to a physics-connected one. Falls back to Damage_0 (the base "damage" param).
    private static ObjectDefinition.DamageType GetSwordDamageType(WorldGameObject player)
    {
        try
        {
            var cols = player.components?.combat?.combat_colliders;
            if (cols != null)
                foreach (var c in cols)
                    if (c != null) return c.damage;
        }
        catch { }
        return ObjectDefinition.DamageType.Damage_0;
    }

    // Destroy a loot prop (vase/pot) in one blow. Sighted players chip a prop's hp with several
    // sword swings, but each of our aimed swings costs the player energy — punishing for a blind
    // player who can't see the pot's remaining hp. Instead we deal lethal damage straight through
    // the game's own kill path, HPActionComponent.DecHP (the same call CombatComponent/projectiles
    // use to break objects). DecHP costs the player NO energy and the object's destruction + loot
    // drop fires exactly once via HPActionComponent.UpdateComponent, so there's no double-drop.
    private static void BreakProp(WorldGameObject prop)
    {
        try
        {
            LogSmash(prop);
            var hpc = prop?.components?.hp;
            if (hpc != null && hpc.enabled && !prop.is_dead)
            {
                // A large value guarantees a one-shot kill after the object's armor/damage_factor
                // are applied inside DecHP (loot props always have damage_factor > 0, else nothing
                // could damage them).
                hpc.DecHP(999999f);
            }
            else if (prop != null && !prop.is_dead)
            {
                // No active hp component to run the passive zero-hp check — trigger the break
                // directly (safe here precisely because that component isn't updating to re-fire it).
                prop.hp = 0f;
                prop.DoPreZeroHPActivity();
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[COMBAT] BreakProp error: {ex.Message}");
        }
    }

    // ── On-demand scan (V) ──────────────────────────────────────────────────────────────────────

    private static void AnnounceScan(WorldGameObject player, List<WorldGameObject> enemies, WorldGameObject nearest)
    {
        int hp = Mathf.RoundToInt(player.hp);
        int maxHp = MainGame.me.save.max_hp;
        int energy = Mathf.RoundToInt(player.energy);

        if (enemies.Count == 0)
        {
            ScreenReader.Say($"No enemies. Health {hp} of {maxHp}, energy {energy}", interrupt: true);
            return;
        }

        float dist = Vector2.Distance(player.pos, nearest.pos);
        string name = EnemyName(nearest);
        string ehp = EnemyHealthText(nearest);
        string weapon = player.GetEquippedWeaponType() == ItemDefinition.ItemType.Sword ? "" : ". No sword equipped";

        ScreenReader.Say(
            $"{enemies.Count} {(enemies.Count == 1 ? "enemy" : "enemies")}. " +
            $"Nearest {name}, {CompassDirection(player.pos, nearest.pos)}, {Meters(dist)}{ehp}. " +
            $"Your health {hp} of {maxHp}, energy {energy}{weapon}",
            interrupt: true);
    }

    // ── Continuous feedback ─────────────────────────────────────────────────────────────────────

    private static void TrackFeedback(WorldGameObject player, List<WorldGameObject> enemies, WorldGameObject nearest)
    {
        // Player taking damage — the most important cue, interrupts everything.
        float php = player.hp;
        if (_lastPlayerHp < 0f) _lastPlayerHp = php;
        if (php < _lastPlayerHp - 0.5f)
            ScreenReader.Say($"Hurt. Health {Mathf.RoundToInt(php)} of {MainGame.me.save.max_hp}", interrupt: true);
        _lastPlayerHp = php;

        var currentIds = new HashSet<int>();

        foreach (var e in enemies)
        {
            int id = e.GetInstanceID();
            currentIds.Add(id);
            float hp = e.hp;

            // First sighting: announce the approach and seed HP tracking.
            if (!_knownEnemies.ContainsKey(id))
            {
                _enemyMaxHp[id] = hp;
                _enemyLastHp[id] = hp;
                ScreenReader.Say($"Enemy approaching. {EnemyName(e)}, {CompassDirection(player.pos, e.pos)}, {Meters(Vector2.Distance(player.pos, e.pos))}", interrupt: false);
                continue;
            }

            float knownMax = _enemyMaxHp.TryGetValue(id, out var mx) ? mx : 0f;
            if (hp > knownMax) _enemyMaxHp[id] = hp;
            float last = _enemyLastHp.TryGetValue(id, out var l) ? l : hp;

            // Our swing landed: HP dropped. Throttle so a multi-hit flurry isn't a wall of speech.
            if (hp < last - 0.5f && _hitCooldown <= 0f)
            {
                ScreenReader.Say($"Hit. {EnemyHealthText(e).TrimStart(',', ' ')}", interrupt: false);
                _hitCooldown = 0.5f;
            }
            _enemyLastHp[id] = hp;
        }

        // Anything known last frame that's no longer in the scan list either died or the player
        // ran out of range. FindEnemies drops both cases, so distinguish them via the kept object:
        // announce a defeat only if the enemy is actually dead — otherwise it just fled, stay quiet.
        foreach (var kv in _knownEnemies)
        {
            int id = kv.Key;
            if (currentIds.Contains(id)) continue;

            var obj = kv.Value;
            bool defeated;
            try
            {
                // A killed mob has is_dead set / hp<=0 (it's still a valid object on the death
                // frame). A Unity-destroyed reference (obj == null) also means it's gone for good,
                // which only happens on death here, not on running away.
                defeated = obj == null || obj.is_dead || obj.hp <= 0f;
            }
            catch { defeated = false; }

            if (defeated)
                ScreenReader.Say("Enemy defeated", interrupt: false);

            _enemyMaxHp.Remove(id);
            _enemyLastHp.Remove(id);
        }

        _knownEnemies.Clear();
        foreach (var e in enemies) _knownEnemies[e.GetInstanceID()] = e;

        // Pre-hit warning: an adjacent enemy is mid-swing — a chance to step away.
        if (nearest != null && _strikeCooldown <= 0f
            && Vector2.Distance(player.pos, nearest.pos) <= AdjacentRange)
        {
            bool striking = false;
            try { striking = nearest.components?.character?.attack?.performing_attack ?? false; } catch { }
            if (striking)
            {
                ScreenReader.Say("Enemy striking", interrupt: true);
                _strikeCooldown = 1.2f;
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static List<WorldGameObject> FindEnemies(Vector2 playerPos)
    {
        var list = new List<WorldGameObject>();
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType<WorldGameObject>(false))
            {
                if (obj == null || obj.is_dead) continue;
                if (obj.obj_def == null || !obj.obj_def.IsMob()) continue;
                if (!obj.gameObject.activeInHierarchy) continue;
                if (obj.hp <= 0f) continue;
                if (Vector2.Distance(obj.pos, playerPos) > DetectRange) continue;
                list.Add(obj);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[COMBAT] FindEnemies failed: {ex.Message}");
        }
        return list;
    }

    // Smashable loot props (dungeon vases/pots, barrels/crates/urns). Uses the SAME predicate as the
    // nav tracker (ObjectNavigator.IsBreakableLootProp) so what the Breakables category lists is
    // exactly what C/X can smash — no more "it's in the list but won't break". That predicate already
    // excludes mobs/NPCs, spent "..._broken" leftovers, and tool-harvested resource nodes (trees/ore).
    private static List<WorldGameObject> FindBreakables(Vector2 playerPos)
    {
        var list = new List<WorldGameObject>();
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType<WorldGameObject>(false))
            {
                if (obj == null || obj.is_dead) continue;
                if (!obj.gameObject.activeInHierarchy) continue;
                if (obj.hp <= 0f) continue;
                if (Vector2.Distance(obj.pos, playerPos) > DetectRange) continue;
                if (!ObjectNavigator.IsBreakableLootProp(obj)) continue;
                list.Add(obj);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[COMBAT] FindBreakables failed: {ex.Message}");
        }
        return list;
    }

    private static void LogSmash(WorldGameObject prop)
    {
        try
        {
            int drops = prop?.obj_def?.drop_items?.Count ?? 0;
            _log?.LogInfo($"[COMBAT] Smash {prop?.obj_id} hp={prop?.hp:0} drops={drops}");
        }
        catch { }
    }

    private static WorldGameObject Nearest(List<WorldGameObject> enemies, Vector2 from)
    {
        WorldGameObject best = null;
        float bestDist = float.MaxValue;
        foreach (var e in enemies)
        {
            if (e == null) continue;
            float d = Vector2.Distance(e.pos, from);
            if (d < bestDist) { bestDist = d; best = e; }
        }
        return best;
    }

    private static string EnemyName(WorldGameObject e)
    {
        try
        {
            if (e?.obj_def != null && !string.IsNullOrEmpty(e.obj_def.id))
            {
                var name = InteractionDetector.LocalizedObjectName(e.obj_def.id);
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch { }
        return "Enemy";
    }

    private static string EnemyHealthText(WorldGameObject e)
    {
        try
        {
            int id = e.GetInstanceID();
            float max = _enemyMaxHp.TryGetValue(id, out var mx) ? mx : e.hp;
            if (max <= 0f) return "";
            int pct = Mathf.Clamp(Mathf.RoundToInt(e.hp / max * 100f), 0, 100);
            return $", enemy health {pct} percent";
        }
        catch { return ""; }
    }

    private static string Meters(float worldDistance)
    {
        return $"{worldDistance / TileSize:F0} meters";
    }

    // Eight-point compass bearing, matching ObjectNavigator's convention (+x east, +y north).
    private static string CompassDirection(Vector2 from, Vector2 to)
    {
        var d = to - from;
        if (d.sqrMagnitude < 1f) return "right here";
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        int sector = Mathf.RoundToInt(angle / 45f) % 8;
        return sector switch
        {
            0 => "to the east",
            1 => "to the north-east",
            2 => "to the north",
            3 => "to the north-west",
            4 => "to the west",
            5 => "to the south-west",
            6 => "to the south",
            7 => "to the south-east",
            _ => "",
        };
    }
}
