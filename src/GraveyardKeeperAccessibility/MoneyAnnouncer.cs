namespace GraveyardKeeperAccessibility;

/// <summary>
/// Announces how much money the player is carrying when R is pressed. Money is stored as the
/// player param "money" (see Trading.player_money -> player.data.money), a float in gold units
/// where 1 gold = 100 silver and 1 silver = 100 bronze, matching Trading.FormatMoney's coin split.
/// </summary>
internal static class MoneyAnnouncer
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

            float money = MainGame.me.player.data.GetParam("money");
            ScreenReader.Say($"You have {MoneyToSpeech(money)}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"MoneyAnnouncer error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Decompose a money value into spoken gold/silver/bronze (mirrors MainMenuPatches.MoneyToSpeech).
    private static string MoneyToSpeech(float value)
    {
        value = Mathf.Round(Mathf.Abs(value) * 100f) / 100f;
        int gold = Mathf.FloorToInt(value / 100f);
        int silver = Mathf.FloorToInt(value - gold * 100f);
        int bronze = Mathf.RoundToInt((value - gold * 100f - silver) * 100f);

        var parts = new List<string>();
        if (gold > 0) parts.Add($"{gold} gold");
        if (silver > 0) parts.Add($"{silver} silver");
        if (bronze > 0) parts.Add($"{bronze} bronze");
        return parts.Count > 0 ? string.Join(", ", parts) : "no money";
    }
}
