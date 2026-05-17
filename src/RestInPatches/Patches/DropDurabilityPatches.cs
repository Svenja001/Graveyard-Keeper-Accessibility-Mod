namespace RestInPatches.Patches;

// Skip adding non-durable drops to the durability list so the periodic recalc isn't
// wasting time on entries that always no-op. Matches what RescanDropItemsList already does.
[Harmony]
public static class DropDurabilityPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WorldMap), nameof(WorldMap.OnNewDropItem))]
    public static bool WorldMap_OnNewDropItem_Prefix(Item drop_item)
    {
        if (drop_item == null)
        {
            return false;
        }

        var definition = drop_item.definition;
        if (definition == null || !definition.has_durability)
        {
            return false;
        }

        return true;
    }
}
