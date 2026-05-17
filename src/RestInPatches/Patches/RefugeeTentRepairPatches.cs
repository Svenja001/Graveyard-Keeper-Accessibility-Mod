using DLCRefugees;

namespace RestInPatches.Patches;

// Fix corrupted refugee-tent occupancy on save load (busy count drifts above the tent's
// capacity and breaks new refugee spawns). Recount from the active refugee list and rewrite
// the available/busy inventory tokens so they match.
[Harmony]
public static class RefugeeTentRepairPatches
{
    private const string TentCustomTag = "refuee_camp_tent";        // game-side typo, kept verbatim
    private const string AvailableItem = "refugee_tent_place_available_item";
    private const string BusyItem = "refugee_tent_place_busy_item";

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RefugeesCampEngine), nameof(RefugeesCampEngine.Init))]
    public static void RefugeesCampEngine_Init_RepairTents(RefugeesCampEngine __instance)
    {
        try
        {
            RepairTents(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[RefugeeTentRepair] skipped: {ex}");
        }
    }

    private static void RepairTents(RefugeesCampEngine engine)
    {
        if (engine?.Data == null) return;
        if (!engine.Data.camp_was_started_at_once) return;

        var tents = WorldMap.GetWorldGameObjectsByCustomTag(TentCustomTag);
        if (tents == null || tents.Count == 0) return;

        var refugees = engine.Data.active_refugee_list;

        var expectedBusy = new Dictionary<WorldGameObject, int>();
        foreach (var tent in tents)
        {
            if (tent != null) expectedBusy[tent] = 0;
        }

        if (refugees != null)
        {
            foreach (var refugee in refugees)
            {
                var homeTag = refugee?.home_gd_point_tag;
                if (string.IsNullOrEmpty(homeTag)) continue;
                foreach (var tent in expectedBusy.Keys.ToList())
                {
                    if (engine.GetHomeGDPointForTent(tent) == homeTag)
                    {
                        expectedBusy[tent]++;
                        break;
                    }
                }
            }
        }

        var repaired = 0;
        foreach (var kv in expectedBusy)
        {
            var tent = kv.Key;
            var wantBusy = kv.Value;
            var capacity = tent.obj_def?.can_insert_items_limit ?? 0;
            if (capacity <= 0) continue;

            var wantAvail = Math.Max(0, capacity - wantBusy);
            // If more refugees point at this tent than capacity allows, clamp so the engine
            // math stays sane. One refugee ends up homeless until the player rebuilds.
            if (wantBusy > capacity) wantBusy = capacity;

            var currentAvail = tent.data?.GetItemsCount(AvailableItem) ?? 0;
            var currentBusy = tent.data?.GetItemsCount(BusyItem) ?? 0;

            if (currentAvail == wantAvail && currentBusy == wantBusy) continue;

            if (currentAvail > 0) tent.data.RemoveItem(AvailableItem, currentAvail);
            if (currentBusy > 0) tent.data.RemoveItem(BusyItem, currentBusy);
            if (wantAvail > 0) tent.AddToInventory(AvailableItem, wantAvail);
            if (wantBusy > 0) tent.AddToInventory(BusyItem, wantBusy);

            Plugin.Log.LogInfo($"[RefugeeTentRepair] {tent.obj_id} cap={capacity}: avail {currentAvail}→{wantAvail}, busy {currentBusy}→{wantBusy}");
            repaired++;
        }

        if (repaired > 0)
        {
            Plugin.Log.LogInfo($"[RefugeeTentRepair] reset {repaired}/{tents.Count} tent(s) to match active refugee list");
        }
    }
}
