namespace GraveyardKeeperAccessibility;

/// <summary>
/// Announces the player's technology points when P is pressed. These are the three
/// tech-point pools the HUD shows as red/green/blue counters, stored as the player
/// params "r", "g" and "b" (see PlayerComponent.GetTechPointsString).
/// </summary>
internal static class TechPointsAnnouncer
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
            if (!MainGame.game_started || MainGame.me == null || MainGame.me.player == null)
            {
                ScreenReader.Say("No game in progress");
                return;
            }

            var player = MainGame.me.player;
            int red = Mathf.RoundToInt(player.GetParam("r"));
            int green = Mathf.RoundToInt(player.GetParam("g"));
            int blue = Mathf.RoundToInt(player.GetParam("b"));

            ScreenReader.Say($"Red {red}, green {green}, blue {blue}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"TechPointsAnnouncer error: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
