namespace TheSeedEqualizer;

[Harmony]
public static class Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    public static void GameBalance_LoadGameBalance()
    {
        Helpers.CaptureAndApply();
    }

    [HarmonyPrefix]
    [HarmonyBefore("p1xel8ted.GraveyardKeeper.FasterCraftReloaded")]
    [HarmonyPriority(1)]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ReallyUpdateComponent))]
    public static void CraftComponent_ReallyUpdateComponent(CraftComponent __instance, ref float delta_time)
    {
        if (__instance?.current_craft == null) return;
        if (!Plugin.BoostGrowSpeedWhenRaining.Value) return;
        if (!EnvironmentEngine.me.is_rainy) return;

        var craftId = __instance.current_craft.id;
        string[] refugee = ["garden", "planting", "refugee", "grow"];
        var isRefugeePlanting = refugee.All(craftId.Contains);
        var isVineyard = craftId.Contains("vineyard");
        var isPlayerGarden = craftId.StartsWith("garden") && craftId.EndsWith("growing");

        if (isRefugeePlanting || isVineyard || isPlayerGarden)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[RainBoost] doubling delta_time for craft '{craftId}' (refugee={isRefugeePlanting}, vineyard={isVineyard}, playerGarden={isPlayerGarden}, before={delta_time:F3})");
            }
            delta_time *= 2f;
        }
        else if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[RainBoost] skip craft '{craftId}': not a recognised garden/vineyard/refugee planting craft");
        }
    }

    // Seed ledger: spend on CraftReally (uses needs[]), harvest on DropItems (uses the
    // rolled item values rather than craft.output template values).

    internal static bool IsTrackedPlantCraft(string craftId)
    {
        if (string.IsNullOrEmpty(craftId)) return false;
        if (craftId.StartsWith("garden") && (craftId.Contains("planting") || craftId.EndsWith("growing"))) return true;
        if (craftId.Contains("grow_desk_planting")) return true;
        if (craftId.Contains("grow_vineyard_planting")) return true;
        if (craftId.StartsWith("refugee_garden")) return true;
        return false;
    }

    private static int SumSeedQty(IEnumerable<Item> items, out string firstSeedId)
    {
        firstSeedId = null;
        if (items == null) return 0;
        var total = 0;
        foreach (var it in items)
        {
            if (it?.id == null) continue;
            if (!it.id.Contains("seed")) continue;
            if (it.value <= 0) continue;
            firstSeedId ??= it.id;
            total += it.value;
        }
        return total;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.CraftReally))]
    public static void CraftComponent_CraftReally_Postfix(CraftComponent __instance, CraftDefinition craft, int amount, bool __result)
    {
        if (!__result) return;
        if (!Plugin.TrackPlantCycles.Value) return;
        if (craft == null || __instance == null || __instance.wgo == null) return;
        if (!IsTrackedPlantCraft(craft.id)) return;

        var spent = SumSeedQty(craft.needs, out var seedId);
        if (spent == 0) return;
        var perCraft = Math.Max(amount, 1);

        try
        {
            Ledger.RecordSpend(__instance.wgo, craft.id, seedId, spent * perCraft);
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[Ledger] CraftReally hook failed: {ex.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.DropItems))]
    public static void WorldGameObject_DropItems_Postfix(WorldGameObject __instance, List<Item> items)
    {
        if (!Plugin.TrackPlantCycles.Value) return;
        if (__instance == null || items == null || items.Count == 0) return;

        var harvested = SumSeedQty(items, out var harvestSeedId);
        if (harvested == 0) return;

        string sourceId = null;
        var currentCraftId = __instance.components?.craft?.current_craft?.id;
        if (!string.IsNullOrEmpty(currentCraftId) && IsTrackedPlantCraft(currentCraftId))
        {
            sourceId = currentCraftId;
        }
        else
        {
            var objId = __instance.obj_id;
            if (string.IsNullOrEmpty(objId)) return;
            // Mirror the game's own player-garden-ready check
            // (WorldGameObject.cs:1521).
            if (!objId.Contains("garden_") || !objId.Contains("_ready")) return;
            if (objId.Contains("grow_desk")) return;
            if (objId.Contains("vineyard")) return;
            if (objId.StartsWith("refugee_")) return;
            sourceId = objId;
        }

        try
        {
            Ledger.RecordHarvest(__instance, sourceId, harvestSeedId, harvested);
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[Ledger] DropItems hook failed: {ex.Message}");
        }
    }
}
