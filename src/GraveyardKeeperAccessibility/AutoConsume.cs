namespace GraveyardKeeperAccessibility;

/// <summary>
/// "Dungeon survival mode." A sighted player who is losing a fight just spams food and health
/// potions from the quick-bar to stay alive; a blind player can't open the inventory, find the
/// right item and click it fast enough while a bat is chewing on them. This module does that
/// refilling automatically: while it's ON, whenever health or energy drops below a threshold it
/// picks a suitable consumable out of the player's inventory and uses it — the same
/// <see cref="WorldGameObject.UseItemFromInventory"/> call the game runs when you eat something.
///
/// It is OFF by default and toggled with the U key, because energy is drained by ordinary work
/// (mining, chopping) too — you only want the mod eating your food when you're deliberately in a
/// dangerous place like the dungeon, not all day on the farm.
///
/// Design notes:
///  - We read the live bars every frame; the existing <see cref="HealthEnergyAnnouncer"/> then
///    speaks the actual "Got N health/energy", so here we only add a short "Ate X" so the player
///    knows which item was spent.
///  - One item per <see cref="ConsumeCooldown"/> window, then we re-check. This paces consumption
///    (no burning a whole stack in a single frame) and lets the swallowed value settle first.
///  - "Just enough" item choice: we prefer the smallest single item that lifts the bar back to a
///    comfortable level, and only reach for a bigger one when nothing small enough exists. That
///    avoids both waste (gulping a full potion at 44%) and spam (nibbling twenty tiny snacks).
///  - Health is checked before energy: staying alive beats staying rested.
/// </summary>
internal static class AutoConsume
{
    private static ManualLogSource _log;
    private static bool _enabled;

    // Bars are compared as a fraction of their max so the thresholds hold up as the player levels
    // and their max_hp / max_energy grow.
    private const float HealthTrigger = 0.45f;   // heal when health falls below this fraction
    private const float EnergyTrigger = 0.35f;   // eat when energy falls below this fraction
    private const float RefillTarget = 0.9f;     // aim to bring the bar back up to this fraction

    // Minimum seconds between two auto-consumes, so a single low reading doesn't dump the whole bag.
    private const float ConsumeCooldown = 0.9f;
    private static float _cooldown;

    // Throttle the "no food/health items left" warnings so they don't machine-gun once the bag runs dry.
    private const float EmptyWarnInterval = 8f;
    private static float _healEmptyWarn;
    private static float _energyEmptyWarn;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[AUTOCONSUME] AutoConsume initialized (U toggles)");
    }

    /// <summary>Toggle the whole feature (U key). Announces the new state and what it does.</summary>
    internal static void Toggle()
    {
        _enabled = !_enabled;
        if (_enabled)
        {
            _cooldown = 0f;
            _healEmptyWarn = _energyEmptyWarn = 0f;
            ScreenReader.Say("Auto healing on. I will eat food and drink potions when your health or energy runs low.", interrupt: true);
        }
        else
        {
            ScreenReader.Say("Auto healing off", interrupt: true);
        }
    }

    /// <summary>
    /// Called every frame from the world block of Plugin.Update (no GUI open). Does nothing unless
    /// the feature is on and a bar is below its trigger.
    /// </summary>
    internal static void Tick()
    {
        try
        {
            if (!_enabled) return;

            if (!MainGame.game_started || MainGame.me == null
                || MainGame.me.player == null || MainGame.me.save == null)
                return;

            var player = MainGame.me.player;
            if (player.is_dead) return;

            if (_cooldown > 0f)
            {
                _cooldown -= Time.deltaTime;
                return;
            }

            var save = MainGame.me.save;
            float maxHp = Mathf.Max(1, save.max_hp);
            float maxEnergy = Mathf.Max(1, save.max_energy);

            // Health first — survival beats stamina.
            if (player.hp / maxHp < HealthTrigger)
            {
                if (TryConsume(player, Bar.Health, maxHp))
                    return;
                WarnEmpty(Bar.Health);
            }

            if (player.energy / maxEnergy < EnergyTrigger)
            {
                if (TryConsume(player, Bar.Energy, maxEnergy))
                    return;
                WarnEmpty(Bar.Energy);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[AUTOCONSUME] Tick error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private enum Bar { Health, Energy }

    /// <summary>
    /// Find the best consumable for the given bar, use it, and announce it. Returns true if
    /// something was eaten (so the caller starts the cooldown and stops for this tick).
    /// </summary>
    private static bool TryConsume(WorldGameObject player, Bar bar, float max)
    {
        var inv = player.data?.inventory;
        if (inv == null || inv.Count == 0) return false;

        float current = bar == Bar.Health ? player.hp : player.energy;
        int deficit = Mathf.Max(0, Mathf.RoundToInt(max * RefillTarget - current));

        Item best = null;
        int bestGain = 0;
        // Rank: an item that covers the deficit on its own beats one that doesn't; among the
        // "covers it" group prefer the SMALLEST such gain (least overheal/waste); among the
        // "doesn't cover it" group prefer the LARGEST gain (make the most progress per bite).
        bool bestCovers = false;

        foreach (var item in inv)
        {
            if (item == null || item.IsEmpty()) continue;
            var def = item.definition;
            if (def == null || !def.can_be_used) continue;

            // Teleport stones, firecrackers, etc. are "usable" but aren't food — never auto-fire them.
            if (def.close_inv_on_use) continue;

            // Respect any per-item use cooldown (some potions have one).
            if (item.GetGrayedCooldownPercent() > 0) continue;

            if (!InventoryItemHandler.ComputeUseEffect(def, out int energy, out int hp, out int sanity))
                continue;

            int gain = bar == Bar.Health ? hp : energy;
            if (gain <= 0) continue;

            // Don't spend a dedicated healing item just to top up energy, and skip anything that
            // would drain the OTHER survival bar or wreck sanity — there's almost always a cleaner
            // choice, and if there isn't we'd rather warn "no food" than poison the player.
            int otherBar = bar == Bar.Health ? energy : hp;
            if (otherBar < 0) continue;
            if (sanity < 0) continue;

            bool covers = gain >= deficit;
            if (IsBetter(covers, gain, bestCovers, bestGain, best == null))
            {
                best = item;
                bestGain = gain;
                bestCovers = covers;
            }
        }

        if (best == null) return false;

        var name = ScreenReader.StripNguiCodes(best.definition?.GetItemName() ?? best.id)?.Trim();
        if (string.IsNullOrEmpty(name)) name = best.id;

        try
        {
            player.UseItemFromInventory(best);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[AUTOCONSUME] use '{name}' threw: {ex.Message}");
            return false;
        }

        _cooldown = ConsumeCooldown;
        _log?.LogInfo($"[AUTOCONSUME] ate '{name}' for {bar} (gain {bestGain})");
        // The amount ("Got N health/energy") is spoken by HealthEnergyAnnouncer; we just name the item.
        ScreenReader.Say($"Ate {name}", interrupt: false);
        return true;
    }

    /// <summary>Ranking rule for candidate items — see the comment at the call site in TryConsume.</summary>
    private static bool IsBetter(bool covers, int gain, bool bestCovers, int bestGain, bool noBestYet)
    {
        if (noBestYet) return true;
        if (covers != bestCovers) return covers;          // covering the deficit always wins
        if (covers) return gain < bestGain;               // both cover it -> smaller is less waste
        return gain > bestGain;                            // neither covers it -> bigger makes more progress
    }

    private static void WarnEmpty(Bar bar)
    {
        if (bar == Bar.Health)
        {
            if (Time.unscaledTime - _healEmptyWarn < EmptyWarnInterval) return;
            _healEmptyWarn = Time.unscaledTime;
            ScreenReader.Say("Low health, no healing items left", interrupt: false);
        }
        else
        {
            if (Time.unscaledTime - _energyEmptyWarn < EmptyWarnInterval) return;
            _energyEmptyWarn = Time.unscaledTime;
            ScreenReader.Say("Low energy, no food left", interrupt: false);
        }
    }
}
