namespace ThoughtfulReminders;

[Harmony]
public static class Patches
{
    private static int PrevDayOfWeek { get; set; }
    private static bool PendingReminder { get; set; }
    private static bool PendingHarvestReminder { get; set; }
    private static int HarvestTargetDay { get; set; }
    private static float QueuedAt { get; set; }
    private static float LastReadyAt { get; set; }

    // How long the garden has to stay quiet before the harvest reminder is said.
    private const float HarvestSettleSeconds = 3f;

    private const string ConfessionEvent = "confession_available";
    private const string BoothPrefix = "church_budka_";

    private static readonly HashSet<string> ReadyCrops =
    [
        "garden_beet_ready", "garden_cabbage_ready", "garden_cannabis_ready", "garden_carrot_ready",
        "garden_grapes_ready", "garden_hop_ready", "garden_lentils_ready", "garden_onion_ready",
        "garden_pumpkin_ready", "garden_wheat_ready"
    ];

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.ReplaceWithObject))]
    public static void WorldGameObject_ReplaceWithObject_Prefix(WorldGameObject __instance, out string __state)
    {
        __state = __instance.obj_id;
    }

    // Beds turn into their "_ready" object the moment they finish growing, so this is where a
    // harvest reminder starts. One flag for the lot, so a whole plot ripening gives one message.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.ReplaceWithObject))]
    public static void WorldGameObject_ReplaceWithObject(WorldGameObject __instance, string new_obj_id, string __state)
    {
        if (!MainGame.game_started) return;
        if (!Plugin.HarvestReminders.Value) return;
        if (!ReadyCrops.Contains(new_obj_id)) return;

        // Only a bed that really just turned ready counts.
        if (__state == new_obj_id) return;
        if (__instance.obj_id != new_obj_id) return;

        LastReadyAt = Time.unscaledTime;

        if (PendingHarvestReminder) return;

        PendingHarvestReminder = true;
        // Anything that ripens before dawn belongs to this morning; anything later waits for
        // tomorrow. Only the first bed of the batch sets this.
        var beforeDawn = (TimeOfDay.me?.GetTimeK() ?? 1f) < 0.25f;
        HarvestTargetDay = MainGame.me.save.day + (beforeDawn ? 0 : 1);
        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[ReplaceWithObject] '{new_obj_id}' is ready - queueing a harvest reminder (morning of day {HarvestTargetDay}).");
        }
    }

    // Loading another save must not carry a queued reminder across with it.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaveSlotsMenuGUI), nameof(SaveSlotsMenuGUI.PrepareScene))]
    public static void SaveSlotsMenuGUI_PrepareScene()
    {
        PrevDayOfWeek = 0;
        PendingReminder = false;
        PendingHarvestReminder = false;
        HarvestTargetDay = 0;
        QueuedAt = 0f;
        LastReadyAt = 0f;

        if (Plugin.DebugEnabled)
        {
            Helpers.Log("[PrepareScene] queued reminders cleared for the incoming save.");
        }
    }

    private static bool ConfessionIsWaiting()
    {
        foreach (var wgo in UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true))
        {
            if (wgo.obj_id == null || !wgo.obj_id.StartsWith(BoothPrefix)) continue;
            if (wgo.custom_interaction_events?.Contains(ConfessionEvent) ?? false) return true;
        }

        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    public static void MainGame_Update()
    {
        if (!MainGame.game_started) return;

        var newDayOfWeek = MainGame.me.save.day_of_week;

        if (MainGame.me.player.is_dead)
        {
            if (Plugin.DebugEnabled && PendingReminder)
            {
                Helpers.Log($"[Update] reminder suppressed - player is dead (day {newDayOfWeek}).");
            }
            return;
        }

        if (!Application.isFocused) return;

        if (PrevDayOfWeek != newDayOfWeek && !PendingReminder)
        {
            QueuedAt = Time.unscaledTime;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Update] day changed {PrevDayOfWeek} -> {newDayOfWeek} - queueing reminder (wake-up delay {Plugin.WakeUpDelay.Value:0.0}s).");
            }
            PendingReminder = true;
        }

        if (!PendingReminder && !PendingHarvestReminder) return;

        if (MainGame.me.player.components.character.player_controlled_by_script)
        {
            QueuedAt = Time.unscaledTime;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Update] reminder held back - player is scripted (cutscene/dialog). Will retry next frame.");
            }
            return;
        }
        if (EnvironmentEngine.me.IsTimeStopped())
        {
            QueuedAt = Time.unscaledTime;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Update] reminder held back - time is stopped (paused/menu). Will retry next frame.");
            }
            return;
        }

        if (GUIElements.me?.sleep_gui?.is_shown ?? false)
        {
            QueuedAt = Time.unscaledTime;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log("[Update] reminder held back - the sleep screen is still up. Will retry next frame.");
            }
            return;
        }

        // The game rolls the date over at midnight, so a player who is still up gets the
        // message in the dark. Hold it until night is over.
        if (TimeOfDay.me?.is_night ?? false)
        {
            QueuedAt = Time.unscaledTime;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log("[Update] reminder held back - still night. Will retry next frame.");
            }
            return;
        }

        var waited = Time.unscaledTime - QueuedAt;
        if (waited < Plugin.WakeUpDelay.Value)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Update] reminder held back - wake-up delay (waited {waited:0.00}s of {Plugin.WakeUpDelay.Value:0.0}s).");
            }
            return;
        }

        Lang.Reload();

        if (!PendingReminder)
        {
            DeliverHarvestReminder();
            return;
        }

        var daysOnly = Plugin.DaysOnlyConfig.Value || !Plugin.EnableEventMessages.Value;
        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Update] firing reminder - day={newDayOfWeek}, daysOnly={daysOnly} (DaysOnlyConfig={Plugin.DaysOnlyConfig.Value}, EnableEventMessages={Plugin.EnableEventMessages.Value}).");
        }

        var message = daysOnly ? DayName(newDayOfWeek) : DayThought(newDayOfWeek);

        if (!daysOnly && Plugin.ConfessionReminders.Value && ConfessionIsWaiting())
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log("[Update] someone is waiting at the confessional - adding it to the reminder.");
            }
            message += " " + Lang.Get("Confession");
        }

        Helpers.SayMessage(message);

        PrevDayOfWeek = newDayOfWeek;
        PendingReminder = false;

        // Space a waiting harvest reminder out so it doesn't land in the same breath as the
        // day's thought.
        QueuedAt = Time.unscaledTime;

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Update] reminder delivered - PrevDayOfWeek={PrevDayOfWeek}, queue cleared.");
        }
    }

    private static void DeliverHarvestReminder()
    {
        if (!Plugin.HarvestReminders.Value)
        {
            PendingHarvestReminder = false;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log("[Update] harvest reminder dropped - harvest reminders were switched off.");
            }
            return;
        }

        if (Plugin.HarvestTiming.Value == HarvestReminderTiming.NextMorning
            && MainGame.me.save.day < HarvestTargetDay)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Update] harvest reminder held back - waiting for the morning of day {HarvestTargetDay} (today is {MainGame.me.save.day}).");
            }
            return;
        }

        // Beds planted together ripen moments apart. Wait for the last one.
        if (Plugin.HarvestTiming.Value == HarvestReminderTiming.WhenReady
            && Time.unscaledTime - LastReadyAt < HarvestSettleSeconds)
        {
            return;
        }

        Helpers.SayMessage(Lang.Get("Harvest"));
        PendingHarvestReminder = false;

        if (Plugin.DebugEnabled)
        {
            Helpers.Log("[Update] harvest reminder delivered.");
        }
    }

    private static string DayName(int dayOfWeek)
    {
        switch (dayOfWeek)
        {
            case 0: return Lang.Get("dSloth");
            case 1: return Lang.Get("dPride");
            case 2: return Lang.Get("dLust");
            case 3: return Lang.Get("dGluttony");
            case 4: return Lang.Get("dEnvy");
            case 5: return Lang.Get("dWrath");
            default:
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[Update] unexpected day_of_week {dayOfWeek} - falling back to 'default' translation key.");
                }
                return Lang.Get("default");
        }
    }

    private static string DayThought(int dayOfWeek)
    {
        switch (dayOfWeek)
        {
            case 0: return Lang.Get("dhSloth");
            case 1:
                var hasPreacher = MainGame.me.save.unlocked_perks.Contains("p_preacher");
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[Update] Pride day - preacher perk={hasPreacher}, picking {(hasPreacher ? "dhPrideSermon" : "dhPride")}.");
                }
                return hasPreacher ? Lang.Get("dhPrideSermon") : Lang.Get("dhPride");
            case 2: return Lang.Get("dhLust");
            case 3: return Lang.Get("dhGluttony");
            case 4: return Lang.Get("dhEnvy");
            case 5: return Lang.Get("dhWrath");
            default:
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[Update] unexpected day_of_week {dayOfWeek} - falling back to 'default' translation key.");
                }
                return Lang.Get("default");
        }
    }
}
