using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace MoarPallets;

// Run before mods that scan the whole craft list, so our new buildable is in it.
// Run after RIP so our crate-specific collider fix is the final word on crate drops.
[HarmonyBefore(
    "p1xel8ted.gyk.ibuildwhereiwant",
    "p1xel8ted.gyk.queueeverything",
    "p1xel8ted.gyk.givememoar"
)]
[HarmonyAfter("p1xel8ted.gyk.restinpatches")]
[HarmonyPatch]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    private static void GameBalance_LoadGameBalance()
    {
        AddPalletBuildCraft();
        AddPalletRemoveCraft();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.OnGameStartedPlaying))]
    private static void MainGame_OnGameStartedPlaying_Postfix()
    {
        try
        {
            CrateRouter.SweepLooseCrates();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Crate sweep failed: {ex.Message}");
        }
    }

    // The player's collider is rebuilt on world load and area changes, so re-apply
    // walk-through to every pallet on those events.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.GlobalEventsCheck))]
    [HarmonyPatch(typeof(WorldMap), nameof(WorldMap.RescanWGOsList))]
    [HarmonyPatch(typeof(WorldZone), nameof(WorldZone.OnPlayerEnter))]
    private static void RefreshPalletWalkthrough_Postfix()
    {
        PalletCollision.RefreshAll();
    }

    // Carried crates route to nearest pallet on drop; vanilla dumps them on the floor.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BaseCharacterComponent), nameof(BaseCharacterComponent.DropOverheadItem))]
    private static bool BaseCharacterComponent_DropOverheadItem_Prefix(BaseCharacterComponent __instance)
    {
        try
        {
            return !CrateRouter.TryRouteCarriedCrate(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Crate carry route failed: {ex.Message}");
            return true;
        }
    }

    // Every crate drop (elevator distribution, vanilla craft, our dump, etc.) gets its
    // collider flipped to a trigger so multiple crates at the same tile don't physics-push.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DropResGameObject), nameof(DropResGameObject.DoDrop), typeof(Vector3), typeof(Item), typeof(Transform), typeof(Direction), typeof(float), typeof(int), typeof(bool))]
    private static void DropResGameObject_DoDrop_Postfix(Item item, DropResGameObject __result)
    {
        if (__result == null) return;
        if (item == null || item.definition == null || !item.definition.is_crate) return;
        CrateRouter.MakeDropNonBlocking(__result);
    }

    private static void AddPalletBuildCraft()
    {
        if (GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.PalletCraftId) != null) return;

        var template = GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.TemplatePalletCraftId);
        if (template == null)
        {
            Plugin.Log.LogError($"Template craft '{Plugin.TemplatePalletCraftId}' not found - pallet build craft will not be added.");
            return;
        }

        var clone = CloneObjectCraft(template);
        clone.id = Plugin.PalletCraftId;
        clone.custom_name = Plugin.PalletNameKey;
        clone.builder_ids = [Plugin.PalletMainBuilderId, Plugin.PalletCellarBuilderId];
        // Swap marketing_point (questline currency) for nails so the player can actually farm it.
        clone.needs =
        [
            new Item("flitch", Plugin.PalletFlitch),
            new Item("nails", Plugin.PalletNails)
        ];
        clone.needs_unlock = false;
        clone.hidden = false;
        clone.one_time_craft = false;

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.PalletCraftId}' (cost: {Plugin.PalletFlitch} flitch + {Plugin.PalletNails} nails).");
    }

    // Vanilla has no remove for the pallet. Clone the beehouse remove so hold-to-remove works.
    private static void AddPalletRemoveCraft()
    {
        if (GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.PalletRemoveCraftId) != null) return;

        var template = GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(Plugin.RemoveTemplateCraftId);
        if (template == null)
        {
            Plugin.Log.LogError($"Template craft '{Plugin.RemoveTemplateCraftId}' not found - pallet remove craft will not be added.");
            return;
        }

        var clone = CloneObjectCraft(template);
        clone.id = Plugin.PalletRemoveCraftId;
        clone.out_obj = "box_pallet";
        clone.build_type = ObjectCraftDefinition.BuildType.Remove;
        clone.output = BuildRemoveOutput();

        RegisterCraft(clone);
        Plugin.Log.LogInfo($"Registered '{Plugin.PalletRemoveCraftId}' (returns: {Plugin.PalletFlitch / 2} flitch + {Plugin.PalletNails / 2} nails).");
    }

    private static List<Item> BuildRemoveOutput()
    {
        return
        [
            MakeOutputItem("flitch", Plugin.PalletFlitch / 2),
            MakeOutputItem("nails", Plugin.PalletNails / 2)
        ];
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

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GJL), nameof(GJL.LoadLanguageResource))]
    private static void GJL_LoadLanguageResource()
    {
        if (!GJL.cur_lng) return;
        GJL.cur_lng.dict[Plugin.PalletNameKey] = Lang.Get("Name");
    }

    // box_pallet has quality_type=Shown, so GetDescription appends "(*){quality}".
    // The wood build desk's zone has no quality icon, leaving a bare "1" in the build menu.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CraftDefinition), nameof(CraftDefinition.GetDescription))]
    private static void CraftDefinition_GetDescription_Postfix(CraftDefinition __instance, ref string __result)
    {
        if (__instance is not ObjectCraftDefinition objCraft) return;
        if (objCraft.id != Plugin.PalletCraftId && objCraft.id != Plugin.PalletRemoveCraftId) return;
        __result = string.Empty;
    }

    // The elevator only fills its own cellar_storage zone, so pallets placed in the larger
    // cellar zone get ignored. Spill overflow into cellar and let the porter sweep both.
    private const string CellarStorageZoneId = "cellar_storage";
    private const string CellarZoneId = "cellar";

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldZone), nameof(WorldZone.PutToAllPossibleInventoriesSmart))]
    private static void WorldZone_PutToAllPossibleInventoriesSmart_Postfix(WorldZone __instance, List<Item> drop_list, ref List<Item> cant_insert)
    {
        if (!__instance) return;
        if (__instance.id != CellarStorageZoneId && __instance.id != CellarZoneId) return;

        if (Plugin.Debug.Value)
        {
            var dropped = drop_list?.Count ?? 0;
            var remaining = cant_insert?.Count ?? 0;
            var palletCount = CountBoxPalletsInZone(__instance);
            Plugin.Log.LogInfo($"Dump to '{__instance.id}': {dropped} item(s) attempted, {remaining} couldn't insert, {palletCount} box_pallet(s) in zone.");
        }

        if (__instance.id != CellarStorageZoneId) return;
        if (cant_insert == null || cant_insert.Count == 0) return;
        var cellar = WorldZone.GetZoneByID(CellarZoneId, false);
        if (!cellar) return;
        cellar.PutToAllPossibleInventoriesSmart(cant_insert, out cant_insert);
    }

    private static int CountBoxPalletsInZone(WorldZone zone)
    {
        return zone.GetZoneWGOs().Count(wgo => wgo && wgo.obj_id == "box_pallet");
    }

    // Hold "Linked to elevator" popups until build mode closes so the build menu doesn't cover them.
    private static readonly List<(Vector3 pos, string msg)> PendingAdoptionPopups = [];

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BuildModeLogics), nameof(BuildModeLogics.SetCurrentBuildZone))]
    private static void BuildModeLogics_SetCurrentBuildZone_PopupFlush_Postfix(string zone_id)
    {
        if (!string.IsNullOrEmpty(zone_id)) return;
        if (PendingAdoptionPopups.Count == 0) return;

        foreach (var entry in PendingAdoptionPopups)
        {
            try
            {
                EffectBubblesManager.ShowImmediately(entry.pos, entry.msg,
                    EffectBubblesManager.BubbleColor.Relation, true, 3f);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to show queued adoption popup: {ex.Message}");
            }
        }
        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"Flushed {PendingAdoptionPopups.Count} queued adoption popup(s).");
        }
        PendingAdoptionPopups.Clear();
    }

    // Grab the placed WGO here because cur_floating reads as null by the time the
    // RecalculateZoneBelonging postfix runs two lines later.
    private static WorldGameObject _placingWobj;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.StopCurrentFloating))]
    private static void FloatingWorldGameObject_StopCurrentFloating_Prefix(bool leave_on_scene)
    {
        if (!leave_on_scene)
        {
            _placingWobj = null;
            return;
        }
        var cur = FloatingWorldGameObject._cur_floating;
        _placingWobj = cur ? cur._wo : null;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.StopCurrentFloating))]
    private static void FloatingWorldGameObject_StopCurrentFloating_Postfix()
    {
        _placingWobj = null;
    }

    // A pallet placed in the gap between the cellar colliders gets no zone. Adopt it into
    // whichever cellar zone has the nearest existing pallet.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.RecalculateZoneBelonging))]
    private static void WorldGameObject_RecalculateZoneBelonging_Postfix(WorldGameObject __instance)
    {
        if (!__instance || __instance.obj_id != "box_pallet") return;

        // A freshly built pallet is walk-through right away, no reload needed.
        PalletCollision.Apply(__instance);

        if (!__instance._zone)
        {
            AdoptOrphanCellarPallet(__instance);
        }

        if (Plugin.Debug.Value)
        {
            var zone = __instance._zone;
            var pos = __instance.transform.position;
            Plugin.Log.LogInfo($"box_pallet at ({pos.x:F1}, {pos.y:F1}) -> zone '{(zone ? zone.id : "<none>")}'");
        }

        // Queue the floater only for fresh placements, and only once the elevator exists.
        if (!Plugin.ShowConnectedPopup.Value) return;
        if (!ReferenceEquals(__instance, _placingWobj)) return;
        var finalZone = __instance._zone;
        if (!finalZone) return;
        if (finalZone.id != CellarStorageZoneId && finalZone.id != CellarZoneId) return;
        if (!ElevatorIsBuilt()) return;

        var popupPos = __instance.transform.position;
        popupPos.y += 96f;
        PendingAdoptionPopups.Add((popupPos, Lang.Get("ConnectedMessage")));
    }

    // Let the cellar build camera pan across both cellar zones, not just the active one.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BuildModeLogics), nameof(BuildModeLogics.FocusCameraOnBuildZone))]
    private static void BuildModeLogics_FocusCameraOnBuildZone_Postfix(BuildModeLogics __instance, string zone_id)
    {
        if (string.IsNullOrEmpty(zone_id)) return;
        if (zone_id != CellarStorageZoneId && zone_id != CellarZoneId) return;
        if (!__instance._cur_build_zone) return;

        var otherId = zone_id == CellarStorageZoneId ? CellarZoneId : CellarStorageZoneId;
        var other = WorldZone.GetZoneByID(otherId, false);
        if (!other) return;

        var combined = __instance._cur_build_zone_bounds;
        combined.Encapsulate(other.GetBounds());
        __instance._cur_build_zone_bounds = combined;

        var screen = new Bounds
        {
            min = MainGame.me.world_cam.ScreenToWorldPoint(Vector3.zero),
            max = MainGame.me.world_cam.ScreenToWorldPoint(new Vector3(Screen.width, Screen.height))
        };
        var halfExtents = screen.extents / 2f;
        var outer = combined;
        outer.min += halfExtents - Vector3.one * 96f;
        outer.max += Vector3.one * 96f - halfExtents;
        __instance._outer_visible_bounds = outer;

        // Re-fit so the very first frame already shows both zones.
        if (__instance._zone_camera_tf)
        {
            __instance._zone_camera_tf.position = __instance.GetFitCameraPos(combined.center);
        }

        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"Build camera bounds extended to cover '{zone_id}' + '{otherId}'.");
        }
    }

    private static readonly HashSet<string> CrateItemIds =
    [
        "box_vegetables_silver", "box_vegetables_gold", "box_goods",
        "box_beer_1", "box_beer_2", "box_beer_3",
        "box_mead_1", "box_mead_2", "box_mead_3",
        "box_wine_1", "box_wine_2", "box_wine_3",
        "box_foodstuff_1", "box_foodstuff_2", "box_foodstuff_3",
        "box_booz"
    ];

    // Hold off the craft when no pallet has space - otherwise the crate ends up dropped on the floor.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.CanStartCraftFromQueue))]
    private static void CraftComponent_CanStartCraftFromQueue_Postfix(CraftComponent __instance, ref CraftComponent.CraftQueueItem __result)
    {
        if (__result?.craft?.output == null || __result.craft.output.Count == 0) return;

        var crateId = (from item in __result.craft.output where item != null && CrateItemIds.Contains(item.id) select item.id).FirstOrDefault();
        if (crateId == null) return;

        if (HasFreeCellarPalletFor(crateId)) return;

        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"Pausing crate craft '{__result.craft.id}' - no free pallet for '{crateId}'.");
        }
        __result = null;
    }

    private static bool HasFreeCellarPalletFor(string crateId)
    {
        var cellarStorage = WorldZone.GetZoneByID(CellarStorageZoneId, false);
        var cellar = WorldZone.GetZoneByID(CellarZoneId, false);
        if (!cellarStorage && !cellar) return true;
        return ZoneHasFreePalletFor(cellarStorage, crateId) || ZoneHasFreePalletFor(cellar, crateId);
    }

    // No elevator_bot means the player hasn't built the elevator yet and the cellar pipeline is dead.
    private static bool ElevatorIsBuilt()
    {
        var found = WorldMap.GetWorldGameObjectsByObjId("elevator_bot");
        return found is { Count: > 0 };
    }

    private static bool ZoneHasFreePalletFor(WorldZone zone, string crateId)
    {
        return zone && zone.GetZoneWGOs().Where(wgo => wgo && wgo.obj_id == "box_pallet" && wgo.data != null).Any(wgo => wgo.data.CanAddCount(crateId, true) > 0);
    }

    // Also accept ghost cells over the partner cellar zone, otherwise placement goes red there.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FlowGridCell), nameof(FlowGridCell.IsInsideWorldZone))]
    private static void FlowGridCell_IsInsideWorldZone_Postfix(FlowGridCell __instance, string zone_id, string sub_zone_id, ref bool __result)
    {
        if (__result) return;
        if (!string.IsNullOrEmpty(sub_zone_id)) return;
        if (zone_id != CellarStorageZoneId && zone_id != CellarZoneId) return;

        var otherId = zone_id == CellarStorageZoneId ? CellarZoneId : CellarStorageZoneId;
        var hits = Physics2D.OverlapBoxNonAlloc((Vector2)__instance.transform.position, BuildGrid.GRID_CHECK_BOX_SIZE, 0f, _overlapBuffer, 524288);
        for (var i = 0; i < hits; i++)
        {
            var zone = _overlapBuffer[i].GetComponent<WorldZone>();
            if (!zone || zone.id != otherId) continue;
            __result = true;
            return;
        }
    }

    private static readonly Collider2D[] _overlapBuffer = new Collider2D[16];


    private static void AdoptOrphanCellarPallet(WorldGameObject orphan)
    {
        var cellarStorage = WorldZone.GetZoneByID(CellarStorageZoneId, false);
        var cellar = WorldZone.GetZoneByID(CellarZoneId, false);
        if (!cellarStorage && !cellar) return;

        var myPos = orphan.transform.position;
        WorldZone bestZone = null;
        var bestDist = float.MaxValue;

        if (cellarStorage)
        {
            foreach (var wgo in cellarStorage.GetZoneWGOs())
            {
                if (!wgo || wgo == orphan || wgo.obj_id != "box_pallet") continue;
                var d = ((Vector2)(wgo.transform.position - myPos)).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    bestZone = cellarStorage;
                }
            }
        }
        if (cellar)
        {
            foreach (var wgo in cellar.GetZoneWGOs())
            {
                if (!wgo || wgo == orphan || wgo.obj_id != "box_pallet") continue;
                var d = ((Vector2)(wgo.transform.position - myPos)).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    bestZone = cellar;
                }
            }
        }

        if (!bestZone) return;
        if (!bestZone._wgos.Contains(orphan))
        {
            bestZone._wgos.Add(orphan);
        }
        orphan._zone = bestZone;

        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"Orphan box_pallet at ({myPos.x:F1}, {myPos.y:F1}) adopted into '{bestZone.id}'.");
        }

    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PorterStation), nameof(PorterStation.FillPorterInventoryFromSource))]
    private static void PorterStation_FillPorterInventoryFromSource_Postfix(PorterStation __instance, Item porter_backpack, bool __result)
    {
        if (!__result || !__instance || porter_backpack == null) return;
        if (!__instance.source || __instance.source.id != CellarStorageZoneId) return;

        var cellar = WorldZone.GetZoneByID(CellarZoneId, false);
        if (!cellar) return;

        var multiInventory = cellar.GetMultiInventory();
        if (multiInventory == null) return;

        // Mirror the vanilla pickup loop against the cellar zone. CanCarryItem keeps it on-route.
        for (var i = 0; i < multiInventory.Count; ++i)
        {
            var inventory = multiInventory[i]?.data?.inventory;
            if (inventory == null || inventory.Count == 0) continue;
            for (var j = 0; j < inventory.Count; ++j)
            {
                var item = inventory[j];
                if (!__instance.CanCarryItem(item)) continue;
                var canAdd = __instance.CanAddToBackPackCount(porter_backpack, item);
                if (canAdd == 0) continue;
                if (item.value - canAdd > 0)
                {
                    var split = new Item(item) { value = canAdd };
                    porter_backpack.AddItem(split);
                    item.value -= canAdd;
                }
                else
                {
                    porter_backpack.AddItem(new Item(item));
                    inventory.RemoveAt(j);
                    --j;
                }
            }
        }
    }
}
