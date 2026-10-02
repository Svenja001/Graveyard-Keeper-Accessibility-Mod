namespace LongerDays;

[Harmony]
public static class Patches
{
    internal static float GetTimeMulti()
    {
        return Plugin.Seconds switch
        {
            Plugin.DefaultIncreaseSeconds => 1.5f,
            Plugin.DoubleLengthSeconds => 2f,
            Plugin.EvenLongerSeconds => 2.5f,
            Plugin.MadnessSeconds => 3f,
            _ => 1f
        };
    }

    public static float GetTime()
    {
        return Time.deltaTime / GetTimeMulti();
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(EnvironmentEngine), nameof(EnvironmentEngine.Update))]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var time = AccessTools.Property(typeof(Time), nameof(Time.deltaTime)).GetGetMethod();
        var replaced = 0;

        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && instruction.OperandIs(time))
            {
                instruction.operand = AccessTools.Method(typeof(Patches), nameof(GetTime));
                replaced++;
            }
            yield return instruction;
        }

        WarnIfNoMatch(replaced, $"{nameof(EnvironmentEngine)}.{nameof(EnvironmentEngine.Update)}", "call Time.get_deltaTime");
    }

    // Buffs time their length and on-screen timer off a hard-coded 450-second day, so a longer
    // day makes a debuff last longer while its damage keeps ticking in real seconds (a "1 minute"
    // poison can triple and kill). Hand back the mod's day length so buffs keep their normal
    // wall-clock duration at any setting. The game loads that 450 as a float in both methods.
    public static float DayLengthSecondsFloat()
    {
        return Plugin.Seconds;
    }

    // A transpiler that matches nothing still hands back valid IL, so a dead patch looks healthy.
    // Another mod may have changed the method first, so this is a warning, not a broken install.
    private static void WarnIfNoMatch(int replaced, string method, string pattern)
    {
        if (replaced > 0) return;
        Plugin.Log.LogWarning($"[LongerDays] Nothing to patch in {method}: expected {pattern}. That part of the mod won't take effect.");
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(BuffsLogics), nameof(BuffsLogics.AddBuff))]
    private static IEnumerable<CodeInstruction> AddBuffTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var replaced = 0;

        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float f && f == 450f)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(Patches), nameof(DayLengthSecondsFloat));
                replaced++;
            }
            yield return instruction;
        }

        WarnIfNoMatch(replaced, $"{nameof(BuffsLogics)}.{nameof(BuffsLogics.AddBuff)}", "ldc.r4 450");
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(PlayerBuff), nameof(PlayerBuff.GetTimerText))]
    private static IEnumerable<CodeInstruction> GetTimerTextTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var replaced = 0;

        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float f && f == 450f)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(Patches), nameof(DayLengthSecondsFloat));
                replaced++;
            }
            yield return instruction;
        }

        WarnIfNoMatch(replaced, $"{nameof(PlayerBuff)}.{nameof(PlayerBuff.GetTimerText)}", "ldc.r4 450");
    }

    private static HashSet<string> _despawnCrafts;

    // The crafts that spawners give their mobs to remove them again later.
    // Not kept until the game's balance data has loaded.
    private static bool IsDespawnCraft(string craftId)
    {
        if (_despawnCrafts == null)
        {
            var spawners = GameBalance.me?.spawners_data;
            if (spawners == null || spawners.Count == 0) return false;

            var ids = new HashSet<string>();
            foreach (var spawner in spawners)
            {
                if (spawner?.mobs == null) continue;
                foreach (var mob in spawner.mobs)
                {
                    if (!string.IsNullOrEmpty(mob?.craft_name))
                    {
                        ids.Add(mob.craft_name);
                    }
                }
            }
            _despawnCrafts = ids;
        }

        return _despawnCrafts.Contains(craftId);
    }

    // The swamp slimes and bats appear on the game clock but leave on a real-seconds timer,
    // so on a longer day they were gone early. Slow that timer to match the day.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ReallyUpdateComponent))]
    public static void CraftComponent_ReallyUpdateComponent(CraftComponent __instance, ref float delta_time)
    {
        if (!__instance.is_crafting || __instance.current_craft == null) return;
        if (!IsDespawnCraft(__instance.current_craft.id)) return;
        delta_time /= GetTimeMulti();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.FromTimeKToSeconds))]
    public static void TimeOfDay_FromTimeKToSeconds(float time_in_time_k, ref float __result)
    {
        __result = time_in_time_k * Plugin.Seconds;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.FromSecondsToTimeK))]
    public static void TimeOfDay_FromSecondsToTimeK(float time_in_secs, ref float __result)
    {
        __result = time_in_secs / Plugin.Seconds;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.GetSecondsToTheMidnight))]
    public static void TimeOfDay_GetSecondsToTheMidnight(TimeOfDay __instance, ref float __result)
    {
        __result = (1f - __instance.GetTimeK()) * Plugin.Seconds;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.GetSecondsToTheMorning))]
    public static void TimeOfDay_GetSecondsToTheMorning(TimeOfDay __instance, ref float __result)
    {
        var num = __instance.GetTimeK() - 0.15f;
        if (num < 0f)
        {
            __result = num * -1f * Plugin.Seconds;
        }
        else
        {
            __result = (1f - __instance.GetTimeK() + 0.15f) * Plugin.Seconds;
        }
    }

    // Diagnostic only: at each day rollover, log the day and the corpse-delivery
    // chance so a report can show whether the roll actually climbs at longer day
    // lengths. Off unless Debug Logging is on. Changes nothing.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(EnvironmentEngine), nameof(EnvironmentEngine.OnEndOfDay))]
    public static void EnvironmentEngine_OnEndOfDay()
    {
        if (!Plugin.DebugEnabled) return;
        var player = MainGame.me?.player;
        if (player == null || MainGame.me.save == null) return;
        var chance = player.GetParam("donkey_coming_chance");
        Plugin.Log.LogInfo($"[LongerDays] End of day {MainGame.me.save.day} (x{GetTimeMulti()} length): donkey_coming_chance={chance:0.###}");
    }
}