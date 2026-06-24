namespace GraveyardKeeperAccessibility;

/// <summary>
/// Two jobs around the player's two HUD bars (health = player.hp out of save.max_hp,
/// energy = player.energy out of save.max_energy; see HUD.Redraw):
///
/// 1. <see cref="Announce"/> reads the current values on demand (H key).
/// 2. <see cref="Tick"/> (run every frame) watches both values and speaks any change —
///    "Got 20 energy", "Lost 3 health" — so the player hears every gain/loss without
///    having to poll the bars. We watch the live values rather than hooking the dozens of
///    code paths that move them (eating, combat damage, buffs, sleep, work drain, regen),
///    so nothing slips through. Changes are coalesced: we wait for the value to settle
///    (one work swing or one bite of food fires several sub-point updates) and then speak
///    the net whole-number delta once.
/// </summary>
internal static class HealthEnergyAnnouncer
{
    private static ManualLogSource _log;

    // Baseline we measure the next spoken delta against, and the last raw values we saw
    // (used purely to detect "is the number still moving?"). _initialized is false until we
    // have a valid player to read, so loading a save re-baselines silently instead of
    // announcing the whole bar as a gain.
    private static bool _initialized;
    private static float _baselineHp;
    private static float _baselineEnergy;
    private static float _lastHp;
    private static float _lastEnergy;
    private static float _lastChangeTime;

    // Quiet period (seconds) after the value stops moving before we speak the net change.
    // Long enough to gather the burst of updates from a single action, short enough to feel
    // immediate.
    private const float SettleDelay = 0.35f;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>Forget the baseline so the next valid frame re-measures without speaking.</summary>
    internal static void Reset()
    {
        _initialized = false;
    }

    internal static void Announce()
    {
        try
        {
            if (!MainGame.game_started || MainGame.me == null
                || MainGame.me.player == null || MainGame.me.save == null)
            {
                ScreenReader.Say("No game in progress");
                return;
            }

            var player = MainGame.me.player;
            var save = MainGame.me.save;

            int hp = Mathf.RoundToInt(player.hp);
            int maxHp = save.max_hp;
            int energy = Mathf.RoundToInt(player.energy);
            int maxEnergy = save.max_energy;

            ScreenReader.Say($"Health {hp} of {maxHp}. Energy {energy} of {maxEnergy}. {DescribeBuffs(save)}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"HealthEnergyAnnouncer error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// The player's active buffs (the icons in the game's buff bar), e.g. "Buffs: Well fed 2:30,
    /// Drunk 0:45". Mirrors BuffsGUI.Redraw — iterate save.buffs and skip hidden ones — so we
    /// read exactly what the game shows. The buff name is localized by the game (German on a
    /// German install); we append GetTimerText() unless the buff opts out of a timer.
    /// </summary>
    private static string DescribeBuffs(GameSave save)
    {
        try
        {
            var names = new List<string>();
            var buffs = save.buffs;
            if (buffs != null)
            {
                foreach (var buff in buffs)
                {
                    if (buff == null) continue;
                    BuffDefinition def = null;
                    try { def = buff.definition; } catch { }
                    if (def == null || def.is_hidden) continue;

                    var name = ScreenReader.StripNguiCodes(def.GetLocalizedName() ?? "").Trim();
                    if (string.IsNullOrEmpty(name) || name.IndexOf('!') >= 0) continue;

                    if (!def.do_not_show_timer)
                    {
                        var timer = buff.GetTimerText();
                        if (!string.IsNullOrEmpty(timer))
                            name = $"{name} {timer}";
                    }
                    names.Add(name);
                }
            }

            return names.Count == 0 ? "No active buffs" : "Buffs: " + string.Join(", ", names);
        }
        catch (Exception ex)
        {
            _log?.LogError($"HealthEnergyAnnouncer.DescribeBuffs error: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Watch health/energy and speak whole-number changes once they settle. Call every frame.
    /// </summary>
    internal static void Tick()
    {
        try
        {
            if (!MainGame.game_started || MainGame.me == null
                || MainGame.me.player == null || MainGame.me.save == null)
            {
                // No valid player to read yet; re-baseline silently when one appears.
                _initialized = false;
                return;
            }

            float hp = MainGame.me.player.hp;
            float energy = MainGame.me.player.energy;

            if (!_initialized)
            {
                _baselineHp = _lastHp = hp;
                _baselineEnergy = _lastEnergy = energy;
                _lastChangeTime = Time.unscaledTime;
                _initialized = true;
                return;
            }

            // Still moving? Reset the settle timer and keep waiting.
            if (hp != _lastHp || energy != _lastEnergy)
            {
                _lastHp = hp;
                _lastEnergy = energy;
                _lastChangeTime = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _lastChangeTime < SettleDelay)
                return;

            int dHp = Mathf.RoundToInt(hp - _baselineHp);
            int dEnergy = Mathf.RoundToInt(energy - _baselineEnergy);

            // Re-baseline to the settled values regardless, so leftover fractions carry over
            // and we never drift or re-announce the same delta.
            _baselineHp = hp;
            _baselineEnergy = energy;

            if (dHp == 0 && dEnergy == 0)
                return;

            var parts = new List<string>(2);
            if (dEnergy > 0) parts.Add($"Got {dEnergy} energy");
            else if (dEnergy < 0) parts.Add($"Lost {-dEnergy} energy");
            if (dHp > 0) parts.Add($"Got {dHp} health");
            else if (dHp < 0) parts.Add($"Lost {-dHp} health");

            if (parts.Count == 0) return;

            var spoken = string.Join(", ", parts);
            _log?.LogInfo($"[HP/ENERGY] {spoken}");
            ScreenReader.Say(spoken, interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogError($"HealthEnergyAnnouncer.Tick error: {ex.Message}");
        }
    }
}
