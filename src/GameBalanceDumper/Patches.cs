namespace GameBalanceDumper;

[Harmony]
[HarmonyWrapSafe]
public static class Patches
{
    // Run before other mods' postfixes so we dump the untouched balance.
    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    public static void GameBalance_LoadGameBalance_Postfix()
    {
        Dumper.DumpOnce();
    }
}
