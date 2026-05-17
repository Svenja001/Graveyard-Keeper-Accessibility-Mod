namespace RestInPatches.Patches;

// Only rebuild the day/r/g/b/hint labels when the value actually changed, instead of
// allocating fresh strings for them every frame.
[Harmony]
public static class HudStringPatches
{
    private sealed class State
    {
        public int Day = int.MinValue;
        public int R = int.MinValue;
        public int G = int.MinValue;
        public int B = int.MinValue;
        public bool HintInit;
        public bool HintHasOverhead;
    }

    private static readonly ConditionalWeakTable<HUD, State> States = new();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HUD), nameof(HUD.Update))]
    public static bool HUD_Update_Prefix(HUD __instance)
    {
        if (!__instance._inited || !MainGame.game_started)
        {
            return false;
        }

        var save = MainGame.me.save;
        var player = MainGame.me.player;

        __instance.bar_hp.value = save.GetHPPercentage();
        __instance.bar_energy.value = player.energy / (float)save.max_energy;
        __instance.bar_sanity.value = player.sanity / (float)save.max_sanity;

        var state = States.GetOrCreateValue(__instance);

        var day = save.day;
        if (day != state.Day)
        {
            state.Day = day;
            __instance.day_label.text = "day " + day;
        }

        var r = Mathf.RoundToInt(player.GetParam("r"));
        if (r != state.R)
        {
            state.R = r;
            __instance.r_label.text = r.ToString();
        }

        var g = Mathf.RoundToInt(player.GetParam("g"));
        if (g != state.G)
        {
            state.G = g;
            __instance.g_label.text = g.ToString();
        }

        var b = Mathf.RoundToInt(player.GetParam("b"));
        if (b != state.B)
        {
            state.B = b;
            __instance.b_label.text = b.ToString();
        }

        if (__instance.hint_x != null)
        {
            var hasOverhead = MainGame.me.player_char.has_overhead;
            if (!state.HintInit || hasOverhead != state.HintHasOverhead)
            {
                state.HintInit = true;
                state.HintHasOverhead = hasOverhead;
                __instance.hint_x.text = !hasOverhead ? "(X) - attack/use tool" : "(X) - drop";
            }
        }

        __instance.RedrawTime(TimeOfDay.me.GetTimeK());
        return false;
    }
}
