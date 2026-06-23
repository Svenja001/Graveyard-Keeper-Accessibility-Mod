namespace GiveMeMoar;

[Harmony]
// Run after known external mods that also multiply the same drop/faith/tech/soul/happiness
// values, so this mod's multipliers have the final say (effects compound).
[HarmonyAfter("codesprint.more_resouces")]
public static class Patches
{
    // Item-ID category lists. Drawn from the fork at game_code/other_mods/GiveMeMoarFork so
    // the IDs match what the game actually spawns.
    private static readonly HashSet<string> Crops = new(StringComparer.Ordinal)
    {
        "fruit:berry", "fruit:apple_green_crop", "fruit:apple_red_crop",
        "beet_crop", "carrot_crop", "cabbage_crop", "wheat_crop",
        "fruit:grapes_crop:1", "fruit:grapes_crop:2", "fruit:grapes_crop:3",
        "hamp_crop:1",
        "hop_crop:1", "hop_crop:2", "hop_crop:3",
        "lentils_crop:1", "lentils_crop:2", "lentils_crop:3",
        "onion_crop:1", "onion_crop:2", "onion_crop:3",
        "pumpkin_crop:1", "pumpkin_crop:2", "pumpkin_crop:3",
        "crop_waste", "hiccup_grass",
        "shr_agaric", "shr_boletus",
        "flw_chamomile", "flw_dandelion", "flw_poppy"
    };

    private static readonly HashSet<string> Seeds = new(StringComparer.Ordinal)
    {
        "wheat_seed", "cabbage_seed", "carrot_seed", "beet_seed",
        "onion_seed:1", "onion_seed:2", "onion_seed:3",
        "lentils_seed:1", "lentils_seed:2", "lentils_seed:3",
        "pumpkin_seed:1", "pumpkin_seed:2", "pumpkin_seed:3",
        "hop_seed:1", "hop_seed:2", "hop_seed:3",
        "hamp_seed:1",
        "grapes_seed:1", "grapes_seed:2", "grapes_seed:3"
    };

    private static readonly HashSet<string> Bugs = new(StringComparer.Ordinal)
    {
        "bee", "butterfly", "maggot", "moth"
    };

    private static readonly HashSet<string> Ores = new(StringComparer.Ordinal)
    {
        "1h_ore_metal", "stone_plate_1", "marble_plate_1", "sulfur",
        "clay", "coal", "nugget_silver", "nugget_gold", "graphite",
        "sand_river", "faceted_diamond", "lifestone"
    };

    private static readonly HashSet<string> Misc = new(StringComparer.Ordinal)
    {
        "honey", "beeswax", "ash", "peat", "taste_booster:salt",
        "water", "drop_alcohol", "egg_chicken", "jug_milk",
        "detail_trash", "pail_water"
    };

    private static readonly HashSet<string> BodyParts = new(StringComparer.Ordinal)
    {
        "blood", "flesh", "fat", "skin", "bone", "skull"
    };

    private static readonly HashSet<string> EnemyDrops = new(StringComparer.Ordinal)
    {
        "bat_wing", "jelly_slug", "jelly_slug_blue",
        "jelly_slug_orange", "jelly_slug_black", "slime",
        "spider_web", "nails_bloody"
    };

    private static readonly HashSet<string> Logs = new(StringComparer.Ordinal)
    {
        "wood1", "wooden_plank", "wood_balk_1", "flitch"
        // sticks are handled separately via MultiplySticks so users can exclude them without
        // losing billets/planks/beams/flitches.
    };

    // Crafts containing these substrings are skipped when "Exclude Progression Crafts" is on.
    private static readonly string[] ProgressionCraftPartials =
    [
        "0_to_1", "1_to_2", "2_to_3", "3_to_4", "4_to_5", "upgr_to", "_to_lantern_",
        "rem_grave", "soul_workbench_craft", "burgers_place", "beer_barrels_place",
        "remove", "refugee", "upgrade", "fountain", "blockage", "obstacle",
        "builddesk", "fix", "broken", "elevator",
        "repair_", "place_tent", "find_zombie"
    ];

    // Vanilla water values from output_to_wgo (water pump etc.), kept for the water multiplier.
    private static readonly Dictionary<string, int> CraftWaterToWgoSnapshots = new(StringComparer.Ordinal);
    private static bool _waterToWgoApplied;

    // Set while a craft is finishing. The ResModificator postfix only scales the drop
    // when this is set, so non-craft loot (chopping a tree, mining a rock) is left alone.
    // The prefix/postfix below save and restore the previous value via __state so a nested
    // call (UI counter, end_script chain) doesn't lose it.
    [ThreadStatic] private static CraftDefinition _craftCtx;

    // Multiquality drops read craft.output[i].value directly and skip ResModificator.
    // The prefix scales those values in place, the postfix puts them back.
    [ThreadStatic] private static List<int> _mqValueSnapshot;

    // The craft the prefix scaled. The postfix restores its output from this, not from
    // __instance.current_craft, which the game has already cleared (via End()) by the time the
    // postfix runs. Restoring off the cleared field would miss, so the scaled values would stay
    // in the shared craft data and grow with every craft.
    [ThreadStatic] private static CraftDefinition _mqCraft;

    private readonly struct CraftCtxState(CraftDefinition ctx, List<int> snap, CraftDefinition mqCraft)
    {
        public readonly CraftDefinition PrevCtx = ctx;
        public readonly List<int> PrevSnapshot = snap;
        public readonly CraftDefinition PrevMqCraft = mqCraft;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameBalance), nameof(GameBalance.LoadGameBalance))]
    public static void GameBalance_LoadGameBalance_Postfix()
    {
        CaptureWaterToWgoSnapshots();
        ApplyWaterToWgoMultiplier();
    }

    internal static void RequestWaterToWgoReapply()
    {
        if (!_waterToWgoApplied) return;
        RestoreWaterToWgoSnapshots();
        ApplyWaterToWgoMultiplier();
    }

    private static void CaptureWaterToWgoSnapshots()
    {
        if (GameBalance.me == null) return;
        CraftWaterToWgoSnapshots.Clear();

        foreach (var craft in GameBalance.me.craft_data)
        {
            if (craft == null || string.IsNullOrEmpty(craft.id)) continue;
            for (var i = 0; i < craft.output_to_wgo.Count; i++)
            {
                var output = craft.output_to_wgo[i];
                if (output == null || output.id != "water") continue;
                CraftWaterToWgoSnapshots[$"{craft.id}|wgo|{i}"] = output.value;
            }
        }

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[WaterSnapshot] captured {CraftWaterToWgoSnapshots.Count} water output_to_wgo values");
        }
    }

    private static void RestoreWaterToWgoSnapshots()
    {
        if (GameBalance.me == null) return;
        var restored = 0;
        foreach (var craft in GameBalance.me.craft_data)
        {
            if (craft == null || string.IsNullOrEmpty(craft.id)) continue;
            for (var i = 0; i < craft.output_to_wgo.Count; i++)
            {
                if (!CraftWaterToWgoSnapshots.TryGetValue($"{craft.id}|wgo|{i}", out var original)) continue;
                craft.output_to_wgo[i].value = original;
                restored++;
            }
        }

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[WaterSnapshot] restored {restored} water output_to_wgo values");
        }
    }

    private static void ApplyWaterToWgoMultiplier()
    {
        if (GameBalance.me == null) return;

        var waterMulti = Plugin.WaterOutputMultiplier.Value;
        if (waterMulti == 1)
        {
            _waterToWgoApplied = true;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log("[WaterApply] water=1, nothing to multiply");
            }
            return;
        }

        var mutated = 0;
        foreach (var craft in GameBalance.me.craft_data)
        {
            if (craft == null || string.IsNullOrEmpty(craft.id)) continue;
            for (var i = 0; i < craft.output_to_wgo.Count; i++)
            {
                var output = craft.output_to_wgo[i];
                if (output == null || output.id != "water") continue;

                if (!CraftWaterToWgoSnapshots.TryGetValue($"{craft.id}|wgo|{i}", out var baseValue))
                {
                    baseValue = output.value;
                }

                var scaled = Math.Max(1, baseValue * waterMulti);
                if (scaled == output.value) continue;
                output.value = scaled;
                mutated++;
            }
        }

        _waterToWgoApplied = true;

        if (Plugin.DebugEnabled || mutated > 0)
        {
            Helpers.Log($"[WaterApply] water={waterMulti} -> mutated {mutated} water output_to_wgo values");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ProcessFinishedCraft))]
    private static void CraftComponent_ProcessFinishedCraft_Prefix(CraftComponent __instance, out CraftCtxState __state)
    {
        __state = new CraftCtxState(_craftCtx, _mqValueSnapshot, _mqCraft);
        _mqValueSnapshot = null;
        _mqCraft = null;

        var craft = __instance.current_craft;
        _craftCtx = craft;

        if (Plugin.DebugEnabled && craft != null)
        {
            Helpers.Log($"[Craft] enter id='{craft.id}', multiquality={craft.IsMultiqualityOutput()}, nested={__state.PrevCtx != null}");
        }

        if (craft == null || !craft.IsMultiqualityOutput()) return;

        var multi = Plugin.CraftOutputMultiplier.Value;
        if (multi == 1)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[CraftMQ] '{craft.id}' skipped: multi=1");
            }
            return;
        }

        if (Plugin.CraftExcludeProgressionCrafts.Value && IsProgressionCraft(craft.id))
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[CraftMQ] '{craft.id}' skipped: progression craft");
            }
            return;
        }

        if (IsBodyModificationCraft(craft.id))
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[CraftMQ] '{craft.id}' skipped: body modification craft");
            }
            return;
        }

        var excludeTools = Plugin.CraftExcludeToolsAndEquipment.Value;
        _mqValueSnapshot = new List<int>(craft.output.Count);
        _mqCraft = craft;
        var mutated = 0;
        var skippedTools = 0;
        for (var i = 0; i < craft.output.Count; i++)
        {
            var output = craft.output[i];
            _mqValueSnapshot.Add(output?.value ?? 0);
            if (output == null || string.IsNullOrEmpty(output.id)) continue;
            if (output.id is "r" or "g" or "b") continue;
            if (excludeTools && IsToolLikeOutput(output.id))
            {
                skippedTools++;
                continue;
            }
            var before = output.value;
            output.value = Math.Max(1, before * multi);
            if (output.value != before) mutated++;
        }

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[CraftMQ] '{craft.id}' multi={multi} -> mutated {mutated}/{craft.output.Count}, skippedTools={skippedTools}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.ProcessFinishedCraft))]
    private static void CraftComponent_ProcessFinishedCraft_Postfix(CraftComponent __instance, CraftCtxState __state)
    {
        var snapshot = _mqValueSnapshot;
        var craft = _mqCraft;
        if (snapshot != null && craft != null)
        {
            var restored = 0;
            for (var i = 0; i < craft.output.Count && i < snapshot.Count; i++)
            {
                if (craft.output[i] == null) continue;
                if (craft.output[i].value != snapshot[i])
                {
                    craft.output[i].value = snapshot[i];
                    restored++;
                }
            }
            if (Plugin.DebugEnabled && restored > 0)
            {
                Helpers.Log($"[CraftMQ] '{craft.id}' restored {restored} output values to vanilla");
            }
        }

        _craftCtx = __state.PrevCtx;
        _mqValueSnapshot = __state.PrevSnapshot;
        _mqCraft = __state.PrevMqCraft;

        if (Plugin.DebugEnabled && __instance.current_craft != null)
        {
            Helpers.Log($"[Craft] leave id='{__instance.current_craft.id}', resumed nested={__state.PrevCtx != null}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.GetCraftAmountCounter))]
    private static void WorldGameObject_GetCraftAmountCounter_Prefix(CraftDefinition craft_definition, out CraftDefinition __state)
    {
        __state = _craftCtx;
        _craftCtx = craft_definition;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.GetCraftAmountCounter))]
    private static void WorldGameObject_GetCraftAmountCounter_Postfix(CraftDefinition __state)
        => _craftCtx = __state;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ResModificator), nameof(ResModificator.ProcessItemsListBeforeDrop))]
    private static void ResModificator_ProcessItemsListBeforeDrop_Postfix(List<Item> __result)
    {
        var craft = _craftCtx;
        if (craft == null || __result == null || __result.Count == 0) return;

        var multi = Plugin.CraftOutputMultiplier.Value;
        if (multi == 1) return;
        if (Plugin.CraftExcludeProgressionCrafts.Value && IsProgressionCraft(craft.id))
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[CraftDrop] '{craft.id}' skipped: progression craft");
            }
            return;
        }

        if (IsBodyModificationCraft(craft.id))
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[CraftDrop] '{craft.id}' skipped: body modification craft");
            }
            return;
        }

        var excludeTools = Plugin.CraftExcludeToolsAndEquipment.Value;
        var mutated = 0;
        var skippedTools = 0;
        foreach (var item in __result)
        {
            if (item == null || string.IsNullOrEmpty(item.id)) continue;
            if (item.id is "r" or "g" or "b") continue;
            if (excludeTools && IsToolLikeOutput(item.id))
            {
                skippedTools++;
                continue;
            }
            var before = item.value;
            item.value = Math.Max(1, before * multi);
            if (item.value != before)
            {
                mutated++;
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[CraftDrop] '{craft.id}' {item.id} {before} -> {item.value} (x{multi})");
                }
            }
        }

        if (Plugin.DebugEnabled && mutated == 0 && __result.Count > 0)
        {
            Helpers.Log($"[CraftDrop] '{craft.id}' nothing mutated (count={__result.Count}, skippedTools={skippedTools})");
        }
    }

    private static bool IsProgressionCraft(string craftId)
    {
        foreach (var needle in ProgressionCraftPartials)
        {
            if (craftId.Contains(needle)) return true;
        }
        return false;
    }

    // Corpse prep at the autopsy table. Extraction crafts ("ex:") run their output through
    // ResModificator, so the craft multiplier would scale the body parts you pull out and flood
    // the inventory. Insertion crafts ("insert:") add directly and never multiply, but match the
    // same intent. The Multiply Body Parts and Sin Shard multipliers cover body prep separately.
    private static bool IsBodyModificationCraft(string craftId)
        => craftId.StartsWith("ex:", StringComparison.Ordinal)
           || craftId.StartsWith("insert:", StringComparison.Ordinal);

    private static bool IsToolLikeOutput(string outputId)
    {
        if (GameBalance.me == null) return false;
        var def = GameBalance.me.GetDataOrNull<ItemDefinition>(outputId);
        if (def == null) return false;

        // is_tool covers Axe/Pickaxe/Shovel/Sword/Hammer/FishingRod/Torch.
        if (def.is_tool) return true;

        switch (def.type)
        {
            case ItemDefinition.ItemType.HeadArmor:
            case ItemDefinition.ItemType.BodyArmor:
            case ItemDefinition.ItemType.Preach:
                return true;
            default:
                return false;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PrayLogics), nameof(PrayLogics.SpreadFaithIncome))]
    private static void PrayLogics_SpreadFaithIncome(ref int faith)
    {
        var multi = Plugin.FaithMultiplier.Value;
        if (multi <= 0 || !MainGame.game_started)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Faith] Skipped: multi={multi}, game_started={MainGame.game_started}");
            }
            return;
        }

        var original = faith;
        faith *= multi;
        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Faith] Original={original}, Multi={multi}, New={faith}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SoulsHelper), nameof(SoulsHelper.CalculatePointsAfterSoulRelease))]
    private static void SoulsHelper_CalculatePointsAfterSoulRelease(ref float __result)
    {
        var multi = Plugin.GratitudeMultiplier.Value;
        if (multi <= 0 || !MainGame.game_started)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Gratitude] Skipped: multi={multi}, game_started={MainGame.game_started}, result={__result}");
            }
            return;
        }

        var original = __result;
        __result = Mathf.Round(__result * multi);
        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Gratitude] Original={original}, Multi={multi}, New={__result}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DropResGameObject), nameof(DropResGameObject.DoDrop), typeof(Item), typeof(int), typeof(bool))]
    private static void DropResGameObject_Drop(DropResGameObject __instance, ref Item drop_item)
    {
        if (!MainGame.game_started || drop_item == null)
        {
            if (Plugin.DebugEnabled && drop_item == null)
            {
                Helpers.Log("[Drop] Skipped: drop_item is null");
            }
            return;
        }

        var id = drop_item.id;
        var isBodyPartType = drop_item.definition.type == ItemDefinition.ItemType.BodyUniversalPart;

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Drop] Incoming id='{id}', type={drop_item.definition.type}, qty={drop_item.value}");
        }

        // Only the six common body parts (blood/flesh/fat/skin/bone/skull) get multiplied.
        if (isBodyPartType)
        {
            if (Plugin.MultiplyBodyParts.Value && BodyParts.Contains(id))
            {
                ApplyResourceMultiplier(drop_item, "BodyPart");
            }
            else if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Drop] Skipped body part '{id}' (toggle={Plugin.MultiplyBodyParts.Value}, commonList={BodyParts.Contains(id)})");
            }
            return;
        }

        // Sin shards keep their dedicated multiplier.
        if (id == "sin_shard")
        {
            var sinMulti = Plugin.SinShardMultiplier.Value;
            if (sinMulti > 1)
            {
                var original = drop_item.value;
                drop_item.value = Math.Max(1, drop_item.value * sinMulti);
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[Drop] SinShard {original} x {sinMulti} -> {drop_item.value}");
                }
            }
            else if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Drop] SinShard multi={sinMulti}, no change");
            }
            return;
        }

        // Water has its own multiplier so wells and breweries don't ride the Misc setting.
        if (id == "water")
        {
            var waterMulti = Plugin.WaterOutputMultiplier.Value;
            if (waterMulti > 1)
            {
                var original = drop_item.value;
                drop_item.value = Math.Max(1, original * waterMulti);
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[Drop] Water '{id}' {original} x {waterMulti} -> {drop_item.value}");
                }
                return;
            }
            // WaterOutputMultiplier == 1 -> fall through; if MultiplyMisc is on, water still
            // gets scaled by ResourceMultiplier as before for backward compat.
        }

        // Gold nuggets have their own multiplier so they don't ride the Ores setting.
        if (id == "nugget_gold")
        {
            var goldMulti = Plugin.NuggetGoldMultiplier.Value;
            if (goldMulti > 1)
            {
                var original = drop_item.value;
                drop_item.value = Math.Max(1, original * goldMulti);
                if (Plugin.DebugEnabled)
                {
                    Helpers.Log($"[Drop] GoldNugget '{id}' {original} x {goldMulti} -> {drop_item.value}");
                }
                return;
            }
            // NuggetGoldMultiplier == 1 -> fall through; if MultiplyOres is on, gold still
            // gets scaled by ResourceMultiplier as before.
        }

        // Sticks have their own toggle so users can keep them out of the Logs multiplier.
        if (id.Contains("stick"))
        {
            if (Plugin.MultiplySticks.Value)
            {
                ApplyResourceMultiplier(drop_item, "Stick");
            }
            else if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Drop] Skipped '{id}': Multiply Sticks is OFF");
            }
            return;
        }

        if (Plugin.MultiplyCrops.Value      && Crops.Contains(id))      { ApplyResourceMultiplier(drop_item, "Crops");      return; }
        if (Plugin.MultiplySeeds.Value      && Seeds.Contains(id))      { ApplyResourceMultiplier(drop_item, "Seeds");      return; }
        if (Plugin.MultiplyLogs.Value       && Logs.Contains(id))       { ApplyResourceMultiplier(drop_item, "Logs");       return; }
        if (Plugin.MultiplyOres.Value       && Ores.Contains(id))       { ApplyResourceMultiplier(drop_item, "Ores");       return; }
        if (Plugin.MultiplyBugs.Value       && Bugs.Contains(id))       { ApplyResourceMultiplier(drop_item, "Bugs");       return; }
        if (Plugin.MultiplyEnemyDrops.Value && EnemyDrops.Contains(id)) { ApplyResourceMultiplier(drop_item, "EnemyDrops"); return; }
        if (Plugin.MultiplyMisc.Value       && Misc.Contains(id))       { ApplyResourceMultiplier(drop_item, "Misc");       return; }

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Drop] '{id}' did not match any enabled category, no multiplier applied");
        }
    }

    private static void ApplyResourceMultiplier(Item item, string tag)
    {
        var multi = Plugin.ResourceMultiplier.Value;
        if (multi <= 1)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Drop] {tag} '{item.id}', Resource Multiplier={multi}, no change");
            }
            return;
        }

        var original = item.value;
        item.value = Math.Max(1, item.value * multi);
        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Drop] {tag} '{item.id}' {original} x {multi} -> {item.value}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RefugeesCampEngine), nameof(RefugeesCampEngine.UpdateHappiness))]
    private static void RefugeesCampEngine_UpdateHappiness(ref float happiness_delta)
    {
        var multi = Plugin.HappinessMultiplier.Value;
        if (multi <= 0 || !MainGame.game_started)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Happiness] Skipped: multi={multi}, game_started={MainGame.game_started}, delta={happiness_delta}");
            }
            return;
        }

        var original = happiness_delta;
        happiness_delta *= multi;
        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Happiness] Original={original}, Multi={multi}, New={happiness_delta}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PrayLogics), nameof(PrayLogics.SpreadMoneyIncome))]
    private static void PrayLogics_SpreadMoneyIncome(ref float money)
    {
        var multi = Plugin.DonationMultiplier.Value;
        if (multi <= 0 || !MainGame.game_started)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[Donation] Skipped: multi={multi}, game_started={MainGame.game_started}, money={money}");
            }
            return;
        }

        var original = money;
        money = Mathf.Round(money * multi);
        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[Donation] Original={original}, Multi={multi}, New={money}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TechPointsDrop), nameof(TechPointsDrop.Drop), typeof(Vector3), typeof(int), typeof(int), typeof(int))]
    private static void TechPointsDrop_Drop(ref int r, ref int g, ref int b)
    {
        if (!MainGame.game_started)
        {
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[TechPoints] Skipped: game not started (r={r}, g={g}, b={b})");
            }
            return;
        }

        var redMultiplier   = Plugin.RedTechPointMultiplier.Value;
        var greenMultiplier = Plugin.GreenTechPointMultiplier.Value;
        var blueMultiplier  = Plugin.BlueTechPointMultiplier.Value;

        if (Plugin.DebugEnabled)
        {
            Helpers.Log($"[TechPoints] Incoming r={r}, g={g}, b={b}; multipliers red={redMultiplier}, green={greenMultiplier}, blue={blueMultiplier}");
        }

        if (redMultiplier > 1)
        {
            var original = r;
            r *= redMultiplier;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[TechPoints] Red {original} x {redMultiplier} -> {r}");
            }
        }

        if (greenMultiplier > 1)
        {
            var original = g;
            g *= greenMultiplier;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[TechPoints] Green {original} x {greenMultiplier} -> {g}");
            }
        }

        if (blueMultiplier > 1)
        {
            var original = b;
            b *= blueMultiplier;
            if (Plugin.DebugEnabled)
            {
                Helpers.Log($"[TechPoints] Blue {original} x {blueMultiplier} -> {b}");
            }
        }
    }
}
