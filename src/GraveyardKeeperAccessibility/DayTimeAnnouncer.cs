namespace GraveyardKeeperAccessibility;

/// <summary>
/// Announces the current in-game day and time when the player presses Q.
/// The game itself only shows a "day X" label and a circular time meter, so we
/// derive a readable clock from <see cref="TimeOfDay"/> and name the weekday after
/// the sin shown in the HUD's sins circle.
/// </summary>
internal static class DayTimeAnnouncer
{
    private static ManualLogSource _log;

    // Weekday names, indexed the same way the HUD's sins circle picks "today".
    // num = (12 - day_of_week) % 6 maps onto these six sins.
    private static readonly string[] WeekdayNames =
        { "Sloth", "Wrath", "Envy", "Gluttony", "Lust", "Pride" };

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    internal static void Announce()
    {
        try
        {
            if (!MainGame.game_started || MainGame.me == null || MainGame.me.save == null)
            {
                ScreenReader.Say("No game in progress");
                return;
            }

            int day = MainGame.me.save.day;
            int dayOfWeek = MainGame.me.save.day_of_week;
            string weekday = WeekdayNames[(12 - dayOfWeek) % 6];

            string clock = FormatTime();

            ScreenReader.Say($"Day {day}, {weekday}. {clock}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"DayTimeAnnouncer error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static string FormatTime()
    {
        var tod = TimeOfDay.me;
        if (tod == null)
            return string.Empty;

        // time_k runs 0..1 across the day (0 = midnight). Map it onto a 24-hour clock.
        float timeK = tod.GetTimeK();
        int totalMinutes = Mathf.RoundToInt(timeK * 24f * 60f) % (24 * 60);
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;

        string phase;
        switch (tod.time_of_day_enum)
        {
            case TimeOfDay.TimeOfDayEnum.Morning: phase = "morning"; break;
            case TimeOfDay.TimeOfDayEnum.Day: phase = "day"; break;
            case TimeOfDay.TimeOfDayEnum.Evening: phase = "evening"; break;
            default: phase = "night"; break;
        }

        return $"{hours:00}:{minutes:00}, {phase}";
    }
}
