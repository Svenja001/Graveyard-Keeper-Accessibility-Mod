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

    // Graveyard Keeper's week is a 6-day cycle whose days are named after sins. We speak each
    // day by its German sin name ("Tag der Faulheit"), genitive-correct per sin. Indexed by
    // (12 - day_of_week) % 6, the same mapping the HUD's sins circle uses to pick "today"
    // (Flow_GetDayOfWeek: Sloth, Wrath, Envy, Gluttony, Lust, Pride).
    private static readonly string[] WeekdayDayNames =
        { "Tag der Faulheit", "Tag des Zorns", "Tag des Neids", "Tag der Völlerei", "Tag der Wollust", "Tag des Hochmuts" };

    // The six town-visiting NPCs each arrive on one fixed weekday, named after that day's
    // sin. Flow_GetDayOfWeek is the authoritative mapping: Sloth→Astrologer, Wrath→Inquisitor,
    // Envy→Cultist, Gluttony→Merchant, Lust→Actress, Pride→Bishop. WeekdayDayNames is indexed
    // in that same sin order (Sloth=0 … Pride=5), so each NPC maps straight to an index.
    private static readonly Dictionary<string, int> VisitingNpcDayIndex = new Dictionary<string, int>
    {
        { "npc_astrologer", 0 },
        { "npc_inquisitor", 1 },
        { "npc_cultist",    2 },
        { "npc_merchant",   3 },
        { "npc_actress",    4 },
        { "npc_bishop",     5 },
    };

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// The weekday a town-visiting NPC appears on (e.g. "Tag der Völlerei" for the merchant),
    /// or null for NPCs that have no fixed visiting day.
    /// </summary>
    internal static string VisitingDayForNpc(string npcId)
    {
        if (npcId != null && VisitingNpcDayIndex.TryGetValue(npcId, out int idx))
            return WeekdayDayNames[idx];
        return null;
    }

    /// <summary>
    /// The weekday name for an absolute calendar day number, e.g. day 6 → "Tag der Faulheit".
    /// Mirrors the game's day → day_of_week → sin cycle so dialogue like "ich komme an Tag 6"
    /// can also speak which day-of-week that falls on.
    /// </summary>
    internal static string DayNameForDay(int day)
    {
        int dow = ((day % 6) + 6) % 6;
        return WeekdayDayNames[(12 - dow) % 6];
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
            string weekday = WeekdayDayNames[(12 - dayOfWeek) % 6];

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
