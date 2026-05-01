using DLCRefugees;

namespace RestInPatches.Patches;

// Repairs corrupted refugee-tent occupancy state on save load. Vanilla tracks tent vacancies via
// two inventory items per tent (refugee_tent_place_available_item + refugee_tent_place_busy_item),
// where avail + busy is supposed to equal the tent's can_insert_items_limit. A save can land with
// the busy count drifted above capacity (reported on Nexus: tent_4 with 12 busy items vs capacity
// of 2), which makes GetVacantTentForRefugee log "Wrong vacant places count in tent: -10" and
// breaks every downstream refugee spawn.
//
// The canonical truth is engine.Data.active_refugee_list — each refugee's home_gd_point_tag
// names the gd-point of its assigned tent. We map each tent to its nearest gd-point (using the
// engine's own GetHomeGDPointForTent so geometry matches vanilla), count the refugees per tent,
// and reset (available, busy) to (capacity - refugees, refugees) when the values disagree.
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
            // If a save somehow has more refugees pointing at this tent than capacity allows,
            // clamp to capacity rather than over-add busy tokens — keeps the engine math sane
            // even though one refugee ends up homeless until the player rebuilds something.
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
