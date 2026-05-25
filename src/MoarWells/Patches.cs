using System.Linq;

namespace MoarWells;

// Run before mods that scan the whole craft list, so our new buildables are in it.
[HarmonyBefore(
    "p1xel8ted.gyk.ibuildwhereiwant",
    "p1xel8ted.gyk.queueeverything",
    "p1xel8ted.gyk.givememoar"
)]
[HarmonyPatch]
public static class Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    private static void GameBalance_LoadGameBalance()
    {
        AddBasicWellBuildCraft();
        AddBasicWellRemoveCraft();
        AddPumpWellUpgradeCraft();
        AddPumpWellRemoveCraft();
    }

    private static void AddBasicWellBuildCraft()
    {
        if (GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.BasicWellCraftId) != null) return;

        var template = GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.TemplateCraftId);
        if (template == null)
        {
            Plugin.Log.LogError($"Template craft '{Plugin.TemplateCraftId}' not found - basic-well craft will not be added.");
            return;
        }

        var clone = CloneObjectCraft(template);
        clone.id = Plugin.BasicWellCraftId;
        clone.custom_name = Plugin.BasicWellNameKey;
        // The pump's end_script runs anchor and animation setup the stone well doesn't need.
        clone.end_script = "";
        // No vanilla water-well icon exists, so reuse the pump's.
        clone.icon = "i_b_well_pump";
        // Skips the "no data for water_well_place" warning the build menu logs on every hover.
        clone.out_obj = "water_well";
        // Put gives us the ghost-and-click placement flow the pump's None type skips.
        clone.build_type = ObjectCraftDefinition.BuildType.Put;
        clone.hidden = false;
        // Visibility is decided live by IsCraftVisible, so don't touch unlocked_crafts on the save.
        clone.needs_unlock = false;
        clone.one_time_craft = false;
        clone.needs =
        [
            new Item("flitch", Plugin.BasicWellFlitch),
            new Item("stone", Plugin.BasicWellStone)
        ];

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.BasicWellCraftId}' (cost: {Plugin.BasicWellFlitch} flitch + {Plugin.BasicWellStone} stone).");
    }

    // Vanilla has no remove for either well. Clone the beehouse remove so hold-to-remove works.
    private static void AddBasicWellRemoveCraft()
    {
        if (GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.BasicWellRemoveCraftId) != null) return;

        var template = GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.RemoveTemplateCraftId);
        if (template == null)
        {
            Plugin.Log.LogError($"Template craft '{Plugin.RemoveTemplateCraftId}' not found - basic-well remove craft will not be added.");
            return;
        }

        var clone = CloneObjectCraft(template);
        clone.id = Plugin.BasicWellRemoveCraftId;
        clone.out_obj = "water_well";
        clone.build_type = ObjectCraftDefinition.BuildType.Remove;
        clone.output = BuildRemoveOutput();

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.BasicWellRemoveCraftId}' (returns: {Plugin.BasicWellFlitch / 2} flitch + {Plugin.BasicWellStone / 2} stone).");
    }

    // Same as the basic-well remove but for well_pump. The village's own pump shares the obj_id
    // and gets removed too if the player aims at it.
    private static void AddPumpWellRemoveCraft()
    {
        if (GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.PumpWellRemoveCraftId) != null) return;

        var template = GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.RemoveTemplateCraftId);
        if (template == null)
        {
            Plugin.Log.LogError($"Template craft '{Plugin.RemoveTemplateCraftId}' not found - pump-well remove craft will not be added.");
            return;
        }

        var clone = CloneObjectCraft(template);
        clone.id = Plugin.PumpWellRemoveCraftId;
        clone.out_obj = "well_pump";
        clone.build_type = ObjectCraftDefinition.BuildType.Remove;
        clone.output = BuildPumpRemoveOutput();

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.PumpWellRemoveCraftId}' (returns: {Plugin.PumpWellFlitch / 2} flitch + {Plugin.PumpWellNails / 2} nails + {Plugin.PumpWellDetail / 2} iron detail).");
    }

    private static List<Item> BuildPumpRemoveOutput()
    {
        var output = new List<Item>();
        const int flitchBack = Plugin.PumpWellFlitch / 2;
        const int nailsBack = Plugin.PumpWellNails / 2;
        const int detailBack = Plugin.PumpWellDetail / 2;
        output.Add(MakeOutputItem("flitch", flitchBack));
        output.Add(MakeOutputItem("nails", nailsBack));
        output.Add(MakeOutputItem("detail_1", detailBack));
        return output;
    }

    // Vanilla's pump craft is single-use against one specific well. Clone and clear the
    // FlowScript + one_time_craft so the player can build a pump on any well.
    private static void AddPumpWellUpgradeCraft()
    {
        if (GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.PumpWellCraftId) != null) return;

        var template = GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.TemplateCraftId);
        if (template == null)
        {
            Plugin.Log.LogError($"Template craft '{Plugin.TemplateCraftId}' not found - pump-well upgrade craft will not be added.");
            return;
        }

        var clone = CloneObjectCraft(template);
        clone.id = Plugin.PumpWellCraftId;
        clone.custom_name = Plugin.PumpWellNameKey;
        // Replaced by our ProcessFinishedCraft postfix that picks the nearest well to the player.
        clone.end_script = "";
        clone.icon = "i_b_well_pump";
        clone.hidden = false;
        clone.needs_unlock = false;
        clone.one_time_craft = false;
        clone.needs =
        [
            new Item("flitch", Plugin.PumpWellFlitch),
            new Item("nails", Plugin.PumpWellNails),
            new Item("detail_1", Plugin.PumpWellDetail)
        ];

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.PumpWellCraftId}' (cost: {Plugin.PumpWellFlitch} flitch + {Plugin.PumpWellNails} nails + {Plugin.PumpWellDetail} iron detail).");
    }

    private static List<Item> BuildRemoveOutput()
    {
        var output = new List<Item>();
        const int flitchBack = Plugin.BasicWellFlitch / 2;
        const int stoneBack = Plugin.BasicWellStone / 2;
        output.Add(MakeOutputItem("flitch", flitchBack));
        output.Add(MakeOutputItem("stone", stoneBack));
        return output;
    }

    // Default self_chance is empty and rolls as zero, so set it to 1 or the item never drops.
    private static Item MakeOutputItem(string id, int value)
    {
        var item = new Item(id, value)
        {
            self_chance = SmartExpression.ParseExpression("1")
        };
        return item;
    }

    // Lists are duplicated so changes to the clone don't bleed into the vanilla template.
    private static ObjectCraftDefinition CloneObjectCraft(ObjectCraftDefinition template) => new()
    {
        craft_in = template.craft_in,
        needs_from_wgo = template.needs_from_wgo,
        output = template.output,
        out_items_expressions = template.out_items_expressions,
        output_res_wgo = template.output_res_wgo,
        output_set_res_wgo = template.output_set_res_wgo,
        set_when_cancelled = template.set_when_cancelled,
        output_to_wgo = template.output_to_wgo,
        output_to_wgo_on_start = template.output_to_wgo_on_start,
        tool_actions = template.tool_actions,
        condition = template.condition,
        end_script = template.end_script,
        end_event = template.end_event,
        flag = template.flag,
        craft_time = template.craft_time,
        energy = template.energy,
        gratitude_points_craft_cost = template.gratitude_points_craft_cost,
        sanity = template.sanity,
        hidden = template.hidden,
        needs_unlock = template.needs_unlock,
        icon = template.icon,
        craft_type = template.craft_type,
        is_auto = template.is_auto,
        not_hide_gui = template.not_hide_gui,
        can_craft_always = template.can_craft_always,
        game_res_to_mirror_name = template.game_res_to_mirror_name,
        game_res_to_mirror_max = template.game_res_to_mirror_max,
        change_wgo = template.change_wgo,
        use_variations = template.use_variations,
        variation_index = template.variation_index,
        craft_after_finish = template.craft_after_finish,
        one_time_craft = template.one_time_craft,
        force_multi_craft = template.force_multi_craft,
        disable_multi_craft = template.disable_multi_craft,
        sub_type = template.sub_type,
        transfer_needs_to_wgo = template.transfer_needs_to_wgo,
        set_out_wgo_params_on_start = template.set_out_wgo_params_on_start,
        itempars_add = template.itempars_add,
        itempars_set = template.itempars_set,
        item_output = template.item_output,
        item_needs = template.item_needs,
        item_needs_leave = template.item_needs_leave,
        dur_needs_item = template.dur_needs_item,
        dur_needs_item_index = template.dur_needs_item_index,
        difficulty = template.difficulty,
        linked_perks = template.linked_perks,
        linked_buffs = template.linked_buffs,
        custom_name = template.custom_name,
        tab_id = template.tab_id,
        buff = template.buff,
        needs_quality = template.needs_quality,
        k_money = template.k_money,
        k_faith = template.k_faith,
        linked_sub_id = template.linked_sub_id,
        dont_close_window_on_craft = template.dont_close_window_on_craft,
        dur_parameter = template.dur_parameter,
        dont_show_in_hint = template.dont_show_in_hint,
        ach_key = template.ach_key,
        craft_time_is_zero = template.craft_time_is_zero,
        puff_when_replaced = template.puff_when_replaced,
        is_item_crating_craft = template.is_item_crating_craft,
        store_last_craft_slot = template.store_last_craft_slot,
        hide_quality_icon = template.hide_quality_icon,
        enqueue_type = template.enqueue_type,
        out_obj = template.out_obj,
        build_type = template.build_type,
        builder_ids = [..template.builder_ids],
        locked_builders_ids = [..template.locked_builders_ids],
        enabled = template.enabled,
        sub_zone_id = template.sub_zone_id,
        is_remove_without_hp_work = template.is_remove_without_hp_work,
        is_destroy_worker_on_remove = template.is_destroy_worker_on_remove,
        wait_script_callback = template.wait_script_callback,
        has_variations = template.has_variations,
        needs = [..template.needs],
    };

    private static void RegisterCraft(ObjectCraftDefinition craft)
    {
        GameBalance.me.craft_data.Add(craft);
        GameBalance.me.craft_obj_data.Add(craft);
        GameBalance.me.AddDataUniversal(craft);
        GameBalance.me.AddData(craft);
    }

    // Log save state on load so bug reports include the tech and world counts.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.GlobalEventsCheck))]
    private static void GameSave_GlobalEventsCheck()
    {
        if (MainGame.me?.save == null) return;
        LogSaveDiagnostics();
        EnsurePumpsAreCrafting();
    }

    private static void LogSaveDiagnostics()
    {
        var save = MainGame.me?.save;
        if (save == null) return;

        Plugin.Log.LogInfo($"Engineer tech researched: {save.unlocked_techs.Contains("Engineer")}");
        Plugin.Log.LogInfo($"pump well requires Engineer (config): {Plugin.RequireEngineerForPumpWell.Value}");
        Plugin.Log.LogInfo($"vanilla pump completed (one-time): {save.completed_one_time_crafts.Contains(Plugin.TemplateCraftId)}");

        LogWorldWellState();
    }

    private static void LogWorldWellState()
    {
        var wells = WorldMap.GetWorldGameObjectsByObjId("water_well");
        var pumps = WorldMap.GetWorldGameObjectsByObjId("well_pump");
        Plugin.Log.LogInfo($"world: {wells?.Count ?? 0} water wells, {pumps?.Count ?? 0} pumps, {GetUnpumpedWaterWells().Count} eligible for upgrade.");
    }

    // Pumps placed before the craft-start fix sit idle. Vanilla restarts the village pump's craft
    // on load; do the same for every pump so old saves recover.
    private static void EnsurePumpsAreCrafting()
    {
        var pumps = WorldMap.GetWorldGameObjectsByObjId("well_pump");
        if (pumps == null) return;
        foreach (var pump in pumps)
        {
            if (!pump) continue;
            if (pump.components?.craft == null) continue;
            if (pump.components.craft.is_crafting) continue;
            pump.TryStartCraft("water_pumping");
        }
    }

    // ProcessFinishedCraft clears current_craft mid-method, so grab the id before it runs.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ProcessFinishedCraft))]
    private static void CraftComponent_ProcessFinishedCraft_Prefix(CraftComponent __instance, out string __state)
    {
        __state = __instance.current_craft?.id;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ProcessFinishedCraft))]
    private static void CraftComponent_ProcessFinishedCraft_Postfix(string __state)
    {
        if (__state != Plugin.PumpWellCraftId) return;
        UpgradeNearestWaterWellToPump();
    }

    // One grid tile, squared. Anything closer counts as the same tile for pump pairing.
    private const float WellPumpProximitySqr = 96f * 96f;

    private static List<WorldGameObject> GetUnpumpedWaterWells()
    {
        var result = new List<WorldGameObject>();
        var wells = WorldMap.GetWorldGameObjectsByObjId("water_well");
        if (wells == null || wells.Count == 0) return result;

        var pumps = WorldMap.GetWorldGameObjectsByObjId("well_pump");
        foreach (var well in wells)
        {
            if (!well) continue;
            var hasNearbyPump = false;
            if (pumps != null)
            {
                var wellPos = well.transform.position;
                if (pumps.Where(pump => pump).Any(pump => !((pump.transform.position - wellPos).sqrMagnitude >= WellPumpProximitySqr)))
                {
                    hasNearbyPump = true;
                }
            }
            if (!hasNearbyPump) result.Add(well);
        }
        return result;
    }

    private static void UpgradeNearestWaterWellToPump()
    {
        var eligible = GetUnpumpedWaterWells();
        if (eligible.Count == 0)
        {
            Plugin.Log.LogWarning("Pump-well craft completed but no water_well without a pump nearby was found.");
            return;
        }

        var player = MainGame.me?.player;
        if (!player) return;

        var playerPos = player.transform.position;
        WorldGameObject closest = null;
        var bestDist = float.MaxValue;
        foreach (var well in eligible)
        {
            var d = (well.transform.position - playerPos).sqrMagnitude;
            if (d >= bestDist) continue;
            bestDist = d;
            closest = well;
        }

        if (!closest) return;
        var pump = WorldMap.SpawnWGO(MainGame.me.world_root, "well_pump", closest.transform.position);
        // A new pump does nothing until water_pumping runs. Vanilla started it from the
        // placement script we cleared, so start it here.
        pump.TryStartCraft("water_pumping");
        Plugin.Log.LogInfo($"Spawned well_pump at {closest.transform.position} alongside the water_well.");
        LogWorldWellState();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BuildModeLogics), nameof(BuildModeLogics.DoPlace))]
    private static void BuildModeLogics_DoPlace_Postfix(BuildModeLogics __instance)
    {
        if (__instance?._cd?.id != Plugin.BasicWellCraftId) return;
        LogWorldWellState();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.ProcessRemove))]
    private static void WorldGameObject_ProcessRemove_Postfix(WorldGameObject __instance)
    {
        if (!__instance) return;
        if (__instance.obj_id != "water_well" && __instance.obj_id != "well_pump") return;
        LogWorldWellState();
    }

    // Pause water pumping when the pump's inventory is already full, or it spams "Can not add item".
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.CanStartCraftFromQueue))]
    private static void CraftComponent_CanStartCraftFromQueue_Postfix(CraftComponent __instance, ref CraftComponent.CraftQueueItem __result)
    {
        if (__result == null) return;
        if (__instance?.wgo?.obj_id != "well_pump") return;
        if (__result.craft?.id != "water_pumping") return;
        if (__instance.wgo.data?.CanAddCount("water", true) > 0) return;
        __result = null;
    }

    // Hide the pump build entry until the player has Engineer and at least one well without a pump.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.IsCraftVisible))]
    private static void GameSave_IsCraftVisible_Postfix(CraftDefinition craft, ref bool __result)
    {
        if (!__result) return;
        if (craft?.id != Plugin.PumpWellCraftId) return;

        if (Plugin.RequireEngineerForPumpWell.Value && MainGame.me?.save?.unlocked_techs?.Contains("Engineer") != true)
        {
            __result = false;
            return;
        }

        if (GetUnpumpedWaterWells().Count == 0) __result = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GJL), nameof(GJL.LoadLanguageResource))]
    private static void GJL_LoadLanguageResource()
    {
        if (!GJL.cur_lng) return;
        GJL.cur_lng.dict[Plugin.BasicWellNameKey] = Lang.Get("Name");
        GJL.cur_lng.dict[Plugin.PumpWellNameKey] = Lang.Get("PumpName");
    }
}
