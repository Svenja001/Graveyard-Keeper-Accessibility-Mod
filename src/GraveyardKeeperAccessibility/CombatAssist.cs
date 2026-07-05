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
/// Toggle the whole thing with B (the auto-facing in particular, in case it ever fights the
/// player's own movement).
/// </summary>
internal static class CombatAssist
{
    private const float TileSize = 96f;
    private const float DetectRange = 12f * TileSize;    // announce enemies inside this radius
    private const float AutoFaceRange = 4f * TileSize;   // keep facing an enemy this close
    private const float AttackRange = 4f * TileSize;     // C will swing at an enemy this close
    private const float AdjacentRange = 1.8f * TileSize; // "enemy striking" danger range

    private static ManualLogSource _log;
    private static bool _enabled = true;

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

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[COMBAT] CombatAssist initialized (C attack, V scan, B toggle)");
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

            var enemies = FindEnemies(player.pos);
            var nearest = Nearest(enemies, player.pos);

            if (!ctrl && Input.GetKeyDown(KeyCode.V))
                AnnounceScan(player, enemies, nearest);

            if (!ctrl && Input.GetKeyDown(KeyCode.C))
                AttackNearest(player, character, nearest);

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

    private static void AttackNearest(WorldGameObject player, BaseCharacterComponent character, WorldGameObject nearest)
    {
        try
        {
            if (player.GetEquippedWeaponType() != ItemDefinition.ItemType.Sword)
            {
                ScreenReader.Say("Equip a sword to attack", interrupt: true);
                return;
            }

            if (nearest == null)
            {
                ScreenReader.Say("No enemies nearby", interrupt: true);
                return;
            }

            float dist = Vector2.Distance(player.pos, nearest.pos);
            if (dist > AttackRange)
            {
                ScreenReader.Say($"Enemy too far, move closer. {CompassDirection(player.pos, nearest.pos)}", interrupt: true);
                return;
            }

            // Mirror the game's own energy gate (BaseCharacterComponent.CheckEnegryForPlayerAtack)
            // so we don't drive energy negative or swing for nothing.
            var weapon = player.GetEquippedWeapon();
            float cost = 0f;
            try
            {
                var p = weapon?.definition?.params_on_use;
                if (p != null && !p.IsEmpty()) cost = p.Get("energy") * -1f;
            }
            catch { }
            if (player.energy < cost)
            {
                ScreenReader.Say("Too tired to attack", interrupt: true);
                return;
            }

            character.LookAt(nearest);

            var attack = character.attack;
            if (attack == null || attack.performing_attack) return;

            // Same call ProcessAttack makes for a player sword swing; the callback chains the next
            // swing when Space/repeat is held, which we don't, so it just ends cleanly.
            attack.Perform(character.anim_direction, 0, character.OnPlayersAttackPerformed);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[COMBAT] AttackNearest error: {ex.Message}");
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

    private static WorldGameObject Nearest(List<WorldGameObject> enemies, Vector2 from)
    {
        WorldGameObject best = null;
        float bestDist = float.MaxValue;
        foreach (var e in enemies)
        {
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
