namespace GraveyardKeeperAccessibility;

/// <summary>
/// Announces the player's health and energy when H is pressed — the two bars the HUD
/// draws at the top-left. Health is player.hp out of save.max_hp; energy is
/// player.energy out of save.max_energy (see HUD.Redraw).
/// </summary>
internal static class HealthEnergyAnnouncer
{
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
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

            ScreenReader.Say($"Health {hp} of {maxHp}. Energy {energy} of {maxEnergy}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"HealthEnergyAnnouncer error: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
