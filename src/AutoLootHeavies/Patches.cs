namespace AutoLootHeavies;

[Harmony]
[HarmonyPriority(1)]
public static class Patches
{
    // Hard cap so the stash-drain loop can't spin forever if another mod refills the carried
    // slot without ever clearing it. Far higher than any real stack size.
    private const int StackDrainLimit = 10000;

    [HarmonyPrefix]
    [HarmonyPriority(1)]
    [HarmonyPatch(typeof(BaseCharacterComponent), nameof(BaseCharacterComponent.DropOverheadItem))]
    public static bool BaseCharacterComponent_DropOverheadItem_Postfix(BaseCharacterComponent __instance)
    {
        if (!__instance.wgo.is_player || !__instance.has_overhead || !Plugin.OverheadItemIsHeavy(__instance.overhead_item))
            return true;
        var itemId = __instance.overhead_item.id;

        Plugin.SortedStockpiles.RemoveAll(pile => pile.Wgo == null);

        if (Plugin.DebugEnabled) Plugin.WriteLog($"[DropOverhead] {itemId}: refresh + sort ({Plugin.SortedStockpiles.Count} stockpiles)");
        foreach (var pile in Plugin.SortedStockpiles)
        {
            pile.DistanceFromPlayer = Vector3.Distance(MainGame.me.player_pos, pile.Wgo.pos3);
        }

        Plugin.SortedStockpiles.Sort((x, y) => x.DistanceFromPlayer.CompareTo(y.DistanceFromPlayer));

        var stashedCount = 0;
        var drained = 0;
        // Super Strong Mod refills the slot from its stack after each insert, so keep stashing
        // until the carry empties or every pile is full. A normal single carry runs this once.
        while (__instance.has_overhead && Plugin.OverheadItemIsHeavy(__instance.overhead_item) && drained++ < StackDrainLimit)
        {
            var item = __instance.overhead_item;
            List<Item> insert = [item];
            var placed = false;
            foreach (var stockpile in Plugin.SortedStockpiles)
            {
                if (Plugin.DebugEnabled) Plugin.WriteLog($"[DropOverhead] try {itemId} → {stockpile.Wgo.obj_id} @ {stockpile.DistanceFromPlayer:F1}");
                if (!Plugin.TryPutToInventoryAndNull(__instance, stockpile.Wgo, insert)) continue;
                placed = true;
                stashedCount += item.value;
                if (Plugin.DebugEnabled) Plugin.WriteLog($"[DropOverhead] inserted {itemId} → {stockpile.Wgo.obj_id} @ {stockpile.DistanceFromPlayer:F1}");
                break;
            }

            if (!placed) break;
        }

        if (stashedCount > 0) Plugin.ShowLootAddedIcon(new Item(itemId) { value = stashedCount });

        if (__instance.overhead_item != null || stashedCount == 0)
        {
            if (Plugin.TeleportToDumpSiteWhenAllStockPilesFull.Value)
            {
                Plugin.TeleportItem(__instance, __instance.overhead_item);
                if (Plugin.DebugEnabled) Plugin.WriteLog($"[DropOverhead] fallback → teleport {itemId} to dump site");
            }
            else
            {
                Plugin.DropObjectAndNull(__instance, __instance.overhead_item);
                if (Plugin.DebugEnabled) Plugin.WriteLog($"[DropOverhead] fallback → drop {itemId} (teleport disabled)");
            }
        }

        return false;
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldZone), nameof(WorldZone.OnPlayerEnter))]
    public static void WorldZone_OnPlayerEnter_Postfix()
    {
        Plugin.StartScan();
    }

    [HarmonyPostfix]
    [HarmonyPriority(1)]
    [HarmonyPatch(typeof(WorldMap), nameof(WorldMap.OnAddNewWGO), typeof(WorldGameObject))]
    public static void WorldGameObject_OnAddNewWGOt(WorldGameObject wgo)
    {
        if (Plugin.StockpileIsValid(wgo))
        {
            Plugin.AddStockpile(wgo);
        }
    }

    [HarmonyPostfix]
    [HarmonyPriority(1)]
    [HarmonyPatch(typeof(WorldMap), nameof(WorldMap.OnDestroyWGO), typeof(WorldGameObject))]
    public static void WorldGameObject_OnDestroyWGO(WorldGameObject wgo)
    {
        if (Plugin.StockpileIsValid(wgo))
        {
            Plugin.RemoveStockpile(wgo);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.Interact))]
    public static void WorldGameObject_Interact_Postfix(WorldGameObject __instance)
    {
        Plugin.WorldGameObjectInteract(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    public static void MainGame_Update()
    {
        if (!MainGame.game_started)
        {
            Plugin.InitialFullUpdate = false;
            return;
        }

        if (!Plugin.InitialFullUpdate)
        {
            Plugin.InitialFullUpdate = true;
            Plugin.SortedStockpiles.Clear();
            Plugin.StartScan();
        }

        Plugin.CheckKeybinds();
    }
}
