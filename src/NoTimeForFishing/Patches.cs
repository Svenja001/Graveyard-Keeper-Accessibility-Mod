namespace NoTimeForFishing;

[Harmony]
public static class Patches
{
    private static bool AutoHookOnly => Plugin.Mode.Value == FishingMode.AutoHookOnly;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FishLogic), nameof(FishLogic.CalculateFishPos))]
    public static void FishLogic_CalculateFishPos(ref float pos, ref float rod_zone_size)
    {
        if (AutoHookOnly) return;

        pos = 0f;
        rod_zone_size = 100f;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FishingGUI), nameof(FishingGUI.UpdateWaitingForBite), null)]
    private static void FishingGUI_UpdateWaitingForBite_Postfix(FishingGUI __instance)
    {
        if (AutoHookOnly) return;

        var fishy = __instance.GetRandomFish(out __instance._waiting_for_bite_delay);
        __instance._fish_def = fishy;
        __instance._fish = new Item(__instance._fish_def.item_id, 1);
        __instance._fish_preset = Resources.Load<FishPreset>("MiniGames/Fishing/" + __instance._fish_def.fish_preset);
        __instance.ChangeState(FishingGUI.FishingState.WaitingForPulling);

        var spot = __instance._fishing_spot_wgo != null ? __instance._fishing_spot_wgo.obj_id : "unknown";
        var rod = __instance._equipped_fishing_rod != null ? __instance._equipped_fishing_rod.id : "none";
        var rare = IsRareFish(fishy.item_id) && Plugin.RareFishRate.Value != RareFishCatchRate.Vanilla ? " [boosted]" : string.Empty;
        Plugin.Log.LogInfo($"Caught {fishy.item_id} at {spot}, distance {__instance._throwing_distance_int}, rod {rod}{rare}.");
    }

    // Auto Hook Only: hook the bite before the game can time it out. The reel decides the catch.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FishingGUI), nameof(FishingGUI.UpdateWaitingForPulling), null)]
    private static bool FishingGUI_UpdateWaitingForPulling_Prefix(FishingGUI __instance)
    {
        if (!AutoHookOnly) return true;
        if (__instance._fish_preset == null) return true;

        __instance.ChangeState(FishingGUI.FishingState.Pulling);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FishingGUI), nameof(FishingGUI.UpdateWaitingForPulling), null)]
    private static void FishingGUI_UpdateWaitingForPulling(FishingGUI __instance)
    {
        if (AutoHookOnly) return;

        __instance.is_success_fishing = true;
        __instance.ChangeState(FishingGUI.FishingState.Pulling);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FishingGUI), nameof(FishingGUI.UpdatePulling), null)]
    private static void FishingGUI_UpdatePulling(FishingGUI __instance)
    {
        if (AutoHookOnly) return;

        __instance.ChangeState(FishingGUI.FishingState.TakingOut);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FishDefinition), nameof(FishDefinition.GetTotalWeight))]
    public static void FishDefinition_GetTotalWeight_Postfix(ref float __result, FishDefinition __instance)
    {
        if (__result <= 0f) return;
        if (Plugin.RareFishRate.Value == RareFishCatchRate.Vanilla) return;
        if (!IsRareFish(__instance.item_id)) return;

        var multiplier = Plugin.RareFishRate.Value switch
        {
            RareFishCatchRate.SlightlyIncreased => 2.5f,
            RareFishCatchRate.Increased         => 5.0f,
            RareFishCatchRate.GreatlyIncreased  => 10.0f,
            RareFishCatchRate.VeryCommon        => 25.0f,
            _                                   => 1.0f,
        };

        __result *= multiplier;
    }

    private static bool IsRareFish(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return false;
        if (itemId == "fish_goldfish") return true;
        if (itemId.EndsWith("_gold")) return true;
        if (itemId.EndsWith(":3")) return true;
        return false;
    }
}
