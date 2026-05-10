namespace MoarWells;

// Run before mods that scan the whole craft list. IBuildWhereIWant needs to
// see the new buildables, QueueEverything snapshots every craft on first
// menu open, and GiveMeMoar scales output values across all of them. Other
// mods either inject their own crafts or filter by id pattern, so they don't
// care about ordering against ours.
[HarmonyBefore(
    "p1xel8ted.gyk.ibuildwhereiwant",
    "p1xel8ted.gyk.queueeverything",
    "p1xel8ted.gyk.givememoar"
)]
[HarmonyPatch]
public static class Patches
{
    // Add our crafts to the game's data right after vanilla finishes loading,
    // so the build menu and craft lookups treat them like any other craft.
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
        // Setting custom_name lets the build menu show our localized label
        // instead of falling back to the raw obj id.
        clone.custom_name = Plugin.BasicWellNameKey;
        // place_well_pump runs the pump's anchor and animation setup. A plain
        // stone well doesn't need any of that.
        clone.end_script = "";
        // No vanilla water-well icon exists, so reuse the pump's.
        clone.icon = "i_b_well_pump";
        // Pointing straight at water_well skips the "no data for water_well_place"
        // warning the build menu logs on every hover.
        clone.out_obj = "water_well";
        // Vanilla pump uses build_type=None, which jumps to CraftAsPlayer with no
        // placement preview. Put gives us the normal ghost-and-click flow that
        // every other buildable uses.
        clone.build_type = ObjectCraftDefinition.BuildType.Put;
        clone.hidden = false;
        clone.needs_unlock = true;
        clone.one_time_craft = false;
        clone.needs = new List<Item>
        {
            new Item("flitch", Plugin.BasicWellFlitch),
            new Item("stone", Plugin.BasicWellStone),
        };

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.BasicWellCraftId}' (cost: {Plugin.BasicWellFlitch} flitch + {Plugin.BasicWellStone} stone).");
    }

    // Vanilla has no remove craft for either well, so the build desk's remove
    // mode just gives up. Cloning the beehouse remove and pointing it at our
    // well makes "hold to remove" work the same way it does for a beehouse.
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
        // Roughly half the build cost back on remove, matching the beehouse
        // pattern. Skip any line whose rounded-down value would be zero.
        clone.output = BuildRemoveOutput();

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.BasicWellRemoveCraftId}' (returns: {Plugin.BasicWellFlitch / 2} flitch + {Plugin.BasicWellStone / 2} stone).");
    }

    // Same shape as the basic-well remove, but for well_pump. This also removes
    // the village's pre-existing pump if the player aims at it - the game can't
    // tell player-built and world-placed wells apart, they share an obj_id.
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
        var flitchBack = Plugin.PumpWellFlitch / 2;
        var nailsBack = Plugin.PumpWellNails / 2;
        var detailBack = Plugin.PumpWellDetail / 2;
        if (flitchBack > 0) output.Add(MakeOutputItem("flitch", flitchBack));
        if (nailsBack > 0) output.Add(MakeOutputItem("nails", nailsBack));
        if (detailBack > 0) output.Add(MakeOutputItem("detail_1", detailBack));
        return output;
    }

    // Vanilla's pump craft is single-use and targets one specific well via a
    // FlowScript. Once that well's been upgraded the entry's effectively dead.
    // Cloning it as our own id, dropping the FlowScript, and clearing
    // one_time_craft lets the player run it many times. Our ProcessFinishedCraft
    // postfix handles where the pump actually goes.
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
        // Vanilla's place_well_pump only knows about one specific tagged well.
        // We replace that with our own postfix that picks the closest one to
        // the player.
        clone.end_script = "";
        clone.icon = "i_b_well_pump";
        clone.hidden = false;
        clone.needs_unlock = true;
        clone.one_time_craft = false;
        clone.needs = new List<Item>
        {
            new Item("flitch", Plugin.PumpWellFlitch),
            new Item("nails", Plugin.PumpWellNails),
            new Item("detail_1", Plugin.PumpWellDetail),
        };

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.PumpWellCraftId}' (cost: {Plugin.PumpWellFlitch} flitch + {Plugin.PumpWellNails} nails + {Plugin.PumpWellDetail} iron detail).");
    }

    private static List<Item> BuildRemoveOutput()
    {
        var output = new List<Item>();
        var flitchBack = Plugin.BasicWellFlitch / 2;
        var stoneBack = Plugin.BasicWellStone / 2;
        if (flitchBack > 0) output.Add(MakeOutputItem("flitch", flitchBack));
        if (stoneBack > 0) output.Add(MakeOutputItem("stone", stoneBack));
        return output;
    }

    // The default self_chance on a new Item is empty and evaluates to zero, which
    // the drop logic treats as "fail the chance roll". Setting it to "1" makes
    // the item always drop, the way vanilla does for remove-craft outputs.
    private static Item MakeOutputItem(string id, int value)
    {
        var item = new Item(id, value);
        item.self_chance = SmartExpression.ParseExpression("1");
        return item;
    }

    // Field-by-field copy. Lists are duplicated rather than shared so changes
    // to the clone don't bleed back into the vanilla template.
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
        builder_ids = new List<string>(template.builder_ids),
        locked_builders_ids = new List<string>(template.locked_builders_ids),
        enabled = template.enabled,
        sub_zone_id = template.sub_zone_id,
        is_remove_without_hp_work = template.is_remove_without_hp_work,
        is_destroy_worker_on_remove = template.is_destroy_worker_on_remove,
        wait_script_callback = template.wait_script_callback,
        has_variations = template.has_variations,
        needs = new List<Item>(template.needs),
    };

    private static void RegisterCraft(ObjectCraftDefinition craft)
    {
        GameBalance.me.craft_data.Add(craft);
        GameBalance.me.craft_obj_data.Add(craft);
        GameBalance.me.AddDataUniversal(craft);
        GameBalance.me.AddData(craft);
    }

    // Runs on every save load. Re-unlocking a craft that's already unlocked
    // does nothing, so this also covers existing saves that didn't have it.
    // The basic well is unlocked unconditionally. The pump well respects the
    // vanilla Engineer tech gate by default; the player can flip the config
    // to remove the gate.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.GlobalEventsCheck))]
    private static void GameSave_GlobalEventsCheck()
    {
        if (MainGame.me?.save == null) return;
        MainGame.me.save.UnlockCraft(Plugin.BasicWellCraftId);
        if (!Plugin.RequireEngineerForPumpWell.Value || MainGame.me.save.unlocked_techs.Contains("Engineer"))
        {
            MainGame.me.save.UnlockCraft(Plugin.PumpWellCraftId);
        }
        LogSaveDiagnostics();
    }

    // Logs which crafts and techs are present on the loaded save, plus how
    // many wells and pumps are in the world. Useful when triaging player
    // reports - the line answers "is the unlock there", "did vanilla mark
    // the pump as already built", "does the player have any unpumped wells".
    private static void LogSaveDiagnostics()
    {
        var save = MainGame.me?.save;
        if (save == null) return;

        var unlocked = save.unlocked_crafts;
        var locked = save.locked_crafts;
        var completed = save.completed_one_time_crafts;
        var techs = save.unlocked_techs;

        Plugin.Log.LogInfo($"basic well: unlocked={unlocked.Contains(Plugin.BasicWellCraftId)}, in locked list={locked.Contains(Plugin.BasicWellCraftId)}");
        Plugin.Log.LogInfo($"basic well remove: unlocked={unlocked.Contains(Plugin.BasicWellRemoveCraftId)}");
        Plugin.Log.LogInfo($"pump well: unlocked={unlocked.Contains(Plugin.PumpWellCraftId)}, in locked list={locked.Contains(Plugin.PumpWellCraftId)}");
        Plugin.Log.LogInfo($"pump well remove: unlocked={unlocked.Contains(Plugin.PumpWellRemoveCraftId)}");
        Plugin.Log.LogInfo($"vanilla pump craft: unlocked={unlocked.Contains(Plugin.TemplateCraftId)}, completed (one-time)={completed.Contains(Plugin.TemplateCraftId)}");
        Plugin.Log.LogInfo($"Engineer tech researched: {techs.Contains("Engineer")}");

        LogWorldWellState();
    }

    // Log line shared by save-load diagnostics, post-build, and post-upgrade.
    // Tells you at a glance how many wells exist, how many already have pumps,
    // and how many are still candidates for an upgrade.
    private static void LogWorldWellState()
    {
        var wells = WorldMap.GetWorldGameObjectsByObjId("water_well");
        var pumps = WorldMap.GetWorldGameObjectsByObjId("well_pump");
        Plugin.Log.LogInfo($"world: {wells?.Count ?? 0} water wells, {pumps?.Count ?? 0} pumps, {GetUnpumpedWaterWells().Count} eligible for upgrade.");
    }

    // ProcessFinishedCraft clears current_craft mid-method, so a postfix can't
    // see what just finished. We grab the id in the prefix and hand it to the
    // postfix through Harmony's __state.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ProcessFinishedCraft))]
    private static void CraftComponent_ProcessFinishedCraft_Prefix(CraftComponent __instance, out string __state)
    {
        __state = __instance.current_craft?.id;
    }

    // Spawn a pump alongside the closest water well to the player. The well
    // stays in place - the pump is a separate object that draws from it.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ProcessFinishedCraft))]
    private static void CraftComponent_ProcessFinishedCraft_Postfix(string __state)
    {
        if (__state != Plugin.PumpWellCraftId) return;
        UpgradeNearestWaterWellToPump();
    }

    // Square of one grid tile (96 units). Distances under this count as the
    // same tile for pump pairing.
    private const float WellPumpProximitySqr = 96f * 96f;

    // Wells that don't already have a pump within one tile. Used to pick the
    // upgrade target and to hide the build-menu entry when no eligible wells
    // are left (so the player can't waste resources on a no-op craft).
    private static List<WorldGameObject> GetUnpumpedWaterWells()
    {
        var result = new List<WorldGameObject>();
        var wells = WorldMap.GetWorldGameObjectsByObjId("water_well");
        if (wells == null || wells.Count == 0) return result;

        var pumps = WorldMap.GetWorldGameObjectsByObjId("well_pump");
        foreach (var well in wells)
        {
            if (well == null) continue;
            var hasNearbyPump = false;
            if (pumps != null)
            {
                var wellPos = well.transform.position;
                foreach (var pump in pumps)
                {
                    if (pump == null) continue;
                    if ((pump.transform.position - wellPos).sqrMagnitude >= WellPumpProximitySqr) continue;
                    hasNearbyPump = true;
                    break;
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
        if (player == null) return;

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

        if (closest == null) return;
        WorldMap.SpawnWGO(MainGame.me.world_root, "well_pump", closest.transform.position);
        Plugin.Log.LogInfo($"Spawned well_pump at {closest.transform.position} alongside the water_well.");
        LogWorldWellState();
    }

    // Log the world-well counts after a basic well lands. The build-mode
    // placement flow runs DoPlace once per click, so this fires exactly when
    // the player drops a new well in the world.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BuildModeLogics), nameof(BuildModeLogics.DoPlace))]
    private static void BuildModeLogics_DoPlace_Postfix(BuildModeLogics __instance)
    {
        if (__instance?._cd?.id != Plugin.BasicWellCraftId) return;
        LogWorldWellState();
    }

    // Log after a well or pump is destroyed so the running totals match the
    // visible state. Other WGOs route through ProcessRemove too, so filter
    // to the obj_ids we care about.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.ProcessRemove))]
    private static void WorldGameObject_ProcessRemove_Postfix(WorldGameObject __instance)
    {
        if (__instance == null) return;
        if (__instance.obj_id != "water_well" && __instance.obj_id != "well_pump") return;
        LogWorldWellState();
    }

    // Hide the pump-build entry when no eligible well exists. The build menu
    // calls IsCraftVisible at open time, so the entry shows again as soon as
    // the player builds another basic well and reopens the menu.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.IsCraftVisible))]
    private static void GameSave_IsCraftVisible_Postfix(CraftDefinition craft, ref bool __result)
    {
        if (!__result) return;
        if (craft?.id != Plugin.PumpWellCraftId) return;
        if (GetUnpumpedWaterWells().Count == 0) __result = false;
    }

    // Add our localized names to the game's string table after each language
    // load. Runs on game start and again whenever the player changes language.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GJL), nameof(GJL.LoadLanguageResource))]
    private static void GJL_LoadLanguageResource()
    {
        if (GJL.cur_lng == null) return;
        GJL.cur_lng.dict[Plugin.BasicWellNameKey] = Lang.Get("Name");
        GJL.cur_lng.dict[Plugin.PumpWellNameKey] = Lang.Get("PumpName");
    }
}
