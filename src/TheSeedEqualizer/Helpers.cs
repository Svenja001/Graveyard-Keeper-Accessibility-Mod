namespace TheSeedEqualizer;

public static class Helpers
{
    private readonly struct ItemSnapshot
    {
        public readonly Item Item;
        public readonly SmartExpression OrigMin;
        public readonly SmartExpression OrigMax;

        public ItemSnapshot(Item item)
        {
            Item = item;
            OrigMin = item.min_value;
            OrigMax = item.max_value;
        }
    }

    private static readonly List<ItemSnapshot> _expressionSnapshots = new();
    private static readonly List<(CraftDefinition def, Item addedWaste)> _addedWasteSnapshots = new();
    private static bool _captured;
    private static int _nonLiteralMaxFallbacks;

    // Read the current upper bound on a seed-drop max without firing the WGO-aware
    // evaluator at GameBalance-load time (no WGO context yet). Use the parsed literal
    // when there is one, otherwise fall back to the floor.
    private static float ReadMaxBefore(SmartExpression expr, float floor, string itemId)
    {
        if (expr is null)
        {
            if (Plugin.DebugEnabled)
            {
                Log($"[ReadMaxBefore] expr=null on '{itemId}' → fallback to floor={floor}");
            }
            _nonLiteralMaxFallbacks++;
            return floor;
        }
        if (expr._simplified)
        {
            if (Plugin.DebugEnabled)
            {
                Log($"[ReadMaxBefore] '{itemId}' simplified literal → {expr._simpified_float}");
            }
            return expr._simpified_float;
        }
        if (Plugin.DebugEnabled)
        {
            Log($"[ReadMaxBefore] '{itemId}' non-literal expr='{expr._expression}' → fallback to floor={floor} (avoiding WGO-null evaluation)");
        }
        _nonLiteralMaxFallbacks++;
        return floor;
    }

    internal static void Log(string message, bool error = false)
    {
        if (error)
        {
            LogHelper.Error(message);
        }
        else
        {
            LogHelper.Info(message);
        }
    }

    private static void ModifyOutput(ObjectDefinition obj)
    {
        var seedOutputs = obj.drop_items.Where(a => a.id.Contains("seed")).ToList();
        if (Plugin.DebugEnabled)
        {
            Log($"[ModifyOutput/Obj] obj='{obj.id}' seedDrops={seedOutputs.Count}");
        }

        foreach (var output in seedOutputs)
        {
            string craft;
            if (output.id.EndsWith(":3"))
            {
                craft = output.id.Replace("_seed:3", "_planting_3");
            }
            else if (output.id.EndsWith(":2"))
            {
                craft = output.id.Replace("_seed:2", "_planting_2");
            }
            else if (output.id.EndsWith(":1"))
            {
                craft = output.id.Replace("_seed:1", "_planting_1");
            }
            else
            {
                craft = output.id.Replace("_seed", "_planting_1");
            }

            craft = craft.Replace("hamp", "cannabis");
            craft = $"garden_{craft}";

            if (Plugin.DebugEnabled)
            {
                Log($"[ModifyOutput/Obj] resolved seedOut='{output.id}' → craftDef='{craft}'");
            }

            var craftDef = GameBalance.me.GetDataOrNull<CraftDefinition>(craft);

            float minValue;
            if (craftDef != null)
            {
                minValue = craftDef.needs[0].value;
                if (Plugin.DebugEnabled)
                {
                    Log($"[ModifyOutput/Obj] '{output.id}' min_value ← {minValue} (from craftDef '{craft}'.needs[0])");
                }
            }
            else
            {
                minValue = 4f;
                if (Plugin.DebugEnabled)
                {
                    Log($"[ModifyOutput/Obj] '{output.id}' min_value ← 4 (no craftDef '{craft}' found, default)");
                }
            }
            if (Plugin.DebugEnabled)
            {
                Log($"[ModifyOutput/Obj] '{output.id}' before - min='{output.min_value?._expression}', max='{output.max_value?._expression}'");
            }
            output.min_value = SmartExpression.ParseExpression(minValue.ToString(CultureInfo.InvariantCulture));

            var maxBefore = ReadMaxBefore(output.max_value, minValue, output.id);
            var normalBoost = maxBefore + 2;
            var extraBoost = maxBefore + 4;
            var boost = Plugin.BoostPotentialSeedOutput.Value ? extraBoost : normalBoost;
            output.max_value = SmartExpression.ParseExpression(boost.ToString(CultureInfo.InvariantCulture));

            if (Plugin.DebugEnabled)
            {
                Log($"[ModifyOutput/Obj] '{output.id}' max_value {maxBefore} → {boost} (boostPotential={Plugin.BoostPotentialSeedOutput.Value})");
            }
        }
    }

    private static void ModifyOutput(CraftDefinition craft)
    {
        var seedOutputs = craft.output.Where(a => a.id.Contains("seed")).ToList();
        if (Plugin.DebugEnabled)
        {
            Log($"[ModifyOutput/Craft] craft='{craft.id}' seedOutputs={seedOutputs.Count} need[0]={craft.needs[0].id}:{craft.needs[0].value}");
        }

        foreach (var output in seedOutputs)
        {
            var minValue = craft.needs[0].value;
            if (Plugin.DebugEnabled)
            {
                Log($"[ModifyOutput/Craft] '{output.id}' before - min='{output.min_value?._expression}', max='{output.max_value?._expression}'");
            }
            output.min_value = SmartExpression.ParseExpression(minValue.ToString(CultureInfo.InvariantCulture));

            var maxBefore = ReadMaxBefore(output.max_value, minValue, output.id);
            var normalBoost = maxBefore + 2;
            var extraBoost = maxBefore + 4;
            var boost = Plugin.BoostPotentialSeedOutput.Value ? extraBoost : normalBoost;
            output.max_value = SmartExpression.ParseExpression(boost.ToString(CultureInfo.InvariantCulture));

            if (Plugin.DebugEnabled)
            {
                Log($"[ModifyOutput/Craft] '{output.id}' min ← {minValue}, max {maxBefore} → {boost} (boostPotential={Plugin.BoostPotentialSeedOutput.Value})");
            }
        }
    }

    internal static void CaptureAndApply()
    {
        Plugin.Log.LogInfo("Running SeedEqualizer GameBalanceLoad as GameBalance has been loaded.");
        if (Plugin.DebugEnabled)
        {
            Log("[CaptureAndApply] initial capture+apply pass starting");
        }

        _expressionSnapshots.Clear();
        _addedWasteSnapshots.Clear();
        _captured = false;
        Apply();
        _captured = true;
    }

    internal static void Reconcile()
    {
        if (!_captured)
        {
            return;
        }
        if (Plugin.DebugEnabled)
        {
            Log("[Reconcile] config changed - reverting + reapplying");
        }
        Revert();
        Apply();
    }

    private static void Revert()
    {
        if (Plugin.DebugEnabled)
        {
            Log($"[Revert] reverting {_expressionSnapshots.Count} item snapshot(s) and removing {_addedWasteSnapshots.Count} added crop_waste output(s)");
        }
        foreach (var snap in _expressionSnapshots)
        {
            snap.Item.min_value = snap.OrigMin;
            snap.Item.max_value = snap.OrigMax;
        }
        foreach (var (def, addedWaste) in _addedWasteSnapshots)
        {
            def.output.Remove(addedWaste);
        }
        _addedWasteSnapshots.Clear();
    }

    private static void CaptureAll()
    {
        foreach (var obj in GameBalance.me.objs_data)
        {
            if (!(obj.id.StartsWith("garden") && obj.id.EndsWith("ready")))
            {
                continue;
            }
            foreach (var item in obj.drop_items)
            {
                if (item.id.Contains("seed"))
                {
                    _expressionSnapshots.Add(new ItemSnapshot(item));
                }
            }
        }

        foreach (var craft in GameBalance.me.craft_data)
        {
            var matchesSeedFlow =
                craft.id.Contains("grow_desk_planting") ||
                craft.id.Contains("grow_vineyard_planting") ||
                craft.id.StartsWith("refugee_garden");
            if (!matchesSeedFlow)
            {
                continue;
            }
            foreach (var item in craft.output)
            {
                if (item.id.Contains("seed"))
                {
                    _expressionSnapshots.Add(new ItemSnapshot(item));
                }
            }
        }

        if (Plugin.DebugEnabled)
        {
            Log($"[CaptureAll] snapshotted {_expressionSnapshots.Count} seed item(s) for revert");
        }
    }

    private static void Apply()
    {
        if (!_captured)
        {
            CaptureAll();
        }

        _nonLiteralMaxFallbacks = 0;

        if (Plugin.DebugEnabled)
        {
            Log($"[Apply] config snapshot - playerGardens={Plugin.ModifyPlayerGardens.Value}, zombieGardens={Plugin.ModifyZombieGardens.Value}, zombieVineyards={Plugin.ModifyZombieVineyards.Value}, refugeeGardens={Plugin.ModifyRefugeeGardens.Value}, wasteToZGardens={Plugin.AddWasteToZombieGardens.Value}, wasteToZVineyards={Plugin.AddWasteToZombieVineyards.Value}, boostSeed={Plugin.BoostPotentialSeedOutput.Value}, rainGrowth={Plugin.BoostGrowSpeedWhenRaining.Value}");
        }

        var playerGardenCandidates = GameBalance.me.objs_data
            .Where(a => a.drop_items.Count > 0 && a.drop_items.Exists(b => b.id.Contains("seed")))
            .ToList();

        if (Plugin.DebugEnabled)
        {
            Log($"[Apply/PlayerGardens] scanning {playerGardenCandidates.Count} object definitions with seed drops");
        }

        var playerModified = 0;
        foreach (var obj in playerGardenCandidates)
        {
            if (!Plugin.ModifyPlayerGardens.Value)
            {
                continue;
            }
            if (!(obj.id.StartsWith("garden") && obj.id.EndsWith("ready")))
            {
                if (Plugin.DebugEnabled)
                {
                    Log($"[Apply/PlayerGardens] skip '{obj.id}': not a garden_*_ready definition");
                }
                continue;
            }

            if (Plugin.DebugEnabled)
            {
                Log($"[Apply/PlayerGardens] modifying '{obj.id}'");
            }
            ModifyOutput(obj);
            playerModified++;
        }

        if (Plugin.DebugEnabled)
        {
            Log($"[Apply/PlayerGardens] modified {playerModified} player garden definitions (enabled={Plugin.ModifyPlayerGardens.Value})");
        }

        var craftCandidates = GameBalance.me.craft_data
            .Where(a => a.needs.Count > 0 && a.needs.Exists(b => b.id.Contains("seed")))
            .ToList();

        if (Plugin.DebugEnabled)
        {
            Log($"[Apply/Crafts] scanning {craftCandidates.Count} craft definitions with seed inputs");
        }

        var zombieModified = 0;
        var vineyardModified = 0;
        var refugeeModified = 0;
        var wasteVineyardAdded = 0;
        var wasteGardenAdded = 0;

        foreach (var craft in craftCandidates)
        {
            if (craft.id.Contains("grow_desk_planting") && Plugin.ModifyZombieGardens.Value)
            {
                if (Plugin.DebugEnabled)
                {
                    Log($"[Apply/ZombieGardens] modifying '{craft.id}'");
                }
                ModifyOutput(craft);
                zombieModified++;
            }

            if (craft.id.Contains("grow_vineyard_planting") && Plugin.ModifyZombieVineyards.Value)
            {
                if (Plugin.DebugEnabled)
                {
                    Log($"[Apply/ZombieVineyards] modifying '{craft.id}'");
                }
                ModifyOutput(craft);
                vineyardModified++;
            }

            if (craft.id.StartsWith("refugee_garden") && Plugin.ModifyRefugeeGardens.Value)
            {
                if (Plugin.DebugEnabled)
                {
                    Log($"[Apply/RefugeeGardens] modifying '{craft.id}'");
                }
                ModifyOutput(craft);
                refugeeModified++;
            }

            if (craft.id.Contains("grow_vineyard_planting") && Plugin.AddWasteToZombieVineyards.Value && !craft.output.Exists(a => a.id == "crop_waste"))
            {
                if (Plugin.DebugEnabled)
                {
                    Log($"[Apply/Waste] adding crop_waste 3-5 to vineyard craft '{craft.id}'");
                }
                var item = new Item("crop_waste", 3)
                {
                    min_value = SmartExpression.ParseExpression("3"),
                    max_value = SmartExpression.ParseExpression("5"),
                    self_chance = craft.needs[0].self_chance
                };
                craft.output.Add(item);
                _addedWasteSnapshots.Add((craft, item));
                wasteVineyardAdded++;
            }
            else if (craft.id.Contains("grow_vineyard_planting") && Plugin.AddWasteToZombieVineyards.Value)
            {
                if (Plugin.DebugEnabled)
                {
                    Log($"[Apply/Waste] vineyard craft '{craft.id}' already drops crop_waste - skipping add");
                }
            }

            if (craft.id.Contains("grow_desk_planting") && Plugin.AddWasteToZombieGardens.Value && !craft.output.Exists(a => a.id == "crop_waste"))
            {
                if (Plugin.DebugEnabled)
                {
                    Log($"[Apply/Waste] adding crop_waste 3-5 to garden craft '{craft.id}'");
                }
                var item = new Item("crop_waste", 3)
                {
                    min_value = SmartExpression.ParseExpression("3"),
                    max_value = SmartExpression.ParseExpression("5"),
                    self_chance = craft.needs[0].self_chance
                };
                craft.output.Add(item);
                _addedWasteSnapshots.Add((craft, item));
                wasteGardenAdded++;
            }
            else if (craft.id.Contains("grow_desk_planting") && Plugin.AddWasteToZombieGardens.Value)
            {
                if (Plugin.DebugEnabled)
                {
                    Log($"[Apply/Waste] garden craft '{craft.id}' already drops crop_waste - skipping add");
                }
            }
        }

        if (Plugin.DebugEnabled)
        {
            Log($"[Apply] done - zombieGardens={zombieModified}, zombieVineyards={vineyardModified}, refugeeGardens={refugeeModified}, wasteAdded(vineyards)={wasteVineyardAdded}, wasteAdded(gardens)={wasteGardenAdded}, nonLiteralMaxFallbacks={_nonLiteralMaxFallbacks}");
        }
    }
}
