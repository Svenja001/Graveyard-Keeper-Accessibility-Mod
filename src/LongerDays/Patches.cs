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

        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && instruction.OperandIs(time))
            {
                instruction.operand = AccessTools.Method(typeof(Patches), nameof(GetTime));
            }
            yield return instruction;
        }
    }

    // Buffs time their length and on-screen timer off a hard-coded 450-second day, so a longer
    // day makes a debuff last longer while its damage keeps ticking in real seconds (a "1 minute"
    // poison can triple and kill). Hand back the mod's day length so buffs keep their normal
    // wall-clock duration at any setting. One per IL type the game loads the 450 constant as.
    public static double DayLengthSecondsDouble()
    {
        return Plugin.Seconds;
    }

    public static float DayLengthSecondsFloat()
    {
        return Plugin.Seconds;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(BuffsLogics), nameof(BuffsLogics.AddBuff))]
    private static IEnumerable<CodeInstruction> AddBuffTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldc_R8 && instruction.operand is double d && d == 450.0)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(Patches), nameof(DayLengthSecondsDouble));
            }
            yield return instruction;
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(PlayerBuff), nameof(PlayerBuff.GetTimerText))]
    private static IEnumerable<CodeInstruction> GetTimerTextTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float f && f == 450f)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(Patches), nameof(DayLengthSecondsFloat));
            }
            yield return instruction;
        }
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