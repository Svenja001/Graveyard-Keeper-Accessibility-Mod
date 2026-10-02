namespace QueueEverything;

[Harmony]
public static class Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    public static void MainGame_Update()
    {
        if (!MainGame.game_started) return;

        if ((CraftComponentPatches.PendingFullReapply || CraftComponentPatches.PendingBenchReapply.Count > 0) && Plugin.CcAlreadyRun)
        {
            try
            {
                if (CraftComponentPatches.PendingFullReapply)
                {
                    CraftComponentPatches.PendingFullReapply = false;
                    CraftComponentPatches.PendingBenchReapply.Clear();
                    CraftComponentPatches.ApplyCraftMutations();
                }
                else
                {
                    var benches = CraftComponentPatches.PendingBenchReapply.ToArray();
                    CraftComponentPatches.PendingBenchReapply.Clear();
                    CraftComponentPatches.ApplyCraftMutationsForBenches(benches);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[MainGame.Update] deferred ApplyCraftMutations failed: {ex}");
            }

            if (!Plugin.AnyAutoCraftCategoryEnabled())
            {
                if (Plugin.DebugEnabled && Plugin.CurrentlyCrafting.Count > 0)
                {
                    Plugin.WriteLog($"[MainGame.Update] auto-craft fully disabled; clearing {Plugin.CurrentlyCrafting.Count} in-flight craftery tracker entries.");
                }
                Plugin.CurrentlyCrafting.Clear();
            }
        }

        if (!Plugin.AnyAutoCraftCategoryEnabled()) return;
        if (Plugin.CraftsStarted) return;

        Plugin.ZombieOccupiedBenches.Clear();
        foreach (var wgo in WorldMap._objs)
        {
            if (wgo == null) continue;

            if (wgo.components.craft.is_crafting && !wgo.has_linked_worker && wgo.linked_worker == null)
            {
                Plugin.CurrentlyCrafting.Add(wgo);
            }

            if (wgo.has_linked_worker && wgo.linked_worker != null && wgo.linked_worker.obj_id.Contains("zombie") && wgo.obj_def != null)
            {
                Plugin.ZombieOccupiedBenches.Add(wgo.obj_def.id);
            }
        }

        if (Plugin.DebugEnabled)
        {
            Plugin.WriteLog($"[MainGame.Update] initial scan found {Plugin.CurrentlyCrafting.Count} in-flight unmanned crafteries.");
            if (Plugin.ZombieOccupiedBenches.Count > 0)
            {
                Plugin.WriteLog($"[MainGame.Update] zombie-occupied bench types: {string.Join(", ", Plugin.ZombieOccupiedBenches)}");
            }
        }

        Plugin.CraftsStarted = true;

        // Queue a reapply for the freshly-discovered zombie benches so their crafts go back to vanilla.
        if (Plugin.ZombieOccupiedBenches.Count > 0)
        {
            foreach (var benchId in Plugin.ZombieOccupiedBenches)
            {
                CraftComponentPatches.PendingBenchReapply.Add(benchId);
            }
        }
    }

    [HarmonyAfter("p1xel8ted.gyk.fastercraftreloaded")]
    [HarmonyPostfix, HarmonyPatch(typeof(MainMenuGUI), nameof(MainMenuGUI.Open))]
    public static void MainMenuGUI_Open()
    {
        if (Plugin.DebugEnabled)
        {
            Plugin.WriteLog($"FasterCraft Reloaded! present: {ModCompat.IsPresent(ModCompat.FasterCraftGuid)}");
            Plugin.WriteLog($"Exhaust-less! present: {ModCompat.IsPresent(ModCompat.ExhaustlessGuid)}");
        }

        // Clear per-save state so loading a different save doesn't see the previous save's WGOs.
        Plugin.CurrentlyCrafting.Clear();
        Plugin.ZombieOccupiedBenches.Clear();
        Plugin.CraftsStarted = false;
        Plugin.CcAlreadyRun = false;
        CraftComponentPatches.PendingFullReapply = false;
        CraftComponentPatches.PendingBenchReapply.Clear();
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(CraftItemGUI), nameof(CraftItemGUI.Redraw))]
    public static void CraftItemGUI_Redraw(CraftItemGUI __instance)
    {
        if (__instance.craft_definition is ObjectCraftDefinition)
        {
            __instance._amount = 1;
            return;
        }

        // Single-craft-only recipes still get their ingredient tier picked below,
        // they just never get an amount above 1.
        var canCraftMultiple = __instance.craft_definition.CanCraftMultiple();
        if (!canCraftMultiple)
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[Redraw] {__instance.craft_definition.id}: CanCraftMultiple=false → _amount=1");
            __instance._amount = 1;
        }

        if (Plugin.AlreadyRun)
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[Redraw] {__instance.craft_definition.id}: skip (AlreadyRun)");
            return;
        }

        if (__instance.craft_definition.id.Contains("fire") || __instance.craft_definition.id.Contains("fuel"))
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[Redraw] {__instance.craft_definition.id}: skip (fire/fuel craft) → _amount=1");
            __instance._amount = 1;
            return;
        }

        var multiInventory = !GlobalCraftControlGUI.is_global_control_active
            ? MainGame.me.player.GetMultiInventoryForInteraction()
            : GUIElements.me.craft.multi_inventory;

        var craftInfo = CraftMaxCalculator.Calculate(__instance, multiInventory, Plugin.AutoSelectHighestQualRecipe.Value);

        if (!canCraftMultiple) return;

        if (Plugin.AutoMaxMultiQualCrafts.Value && craftInfo.IsMultiQualCraft)
        {
            __instance._amount = craftInfo.Min;
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[Redraw] {__instance.craft_definition.id}: auto-max multi-qual → _amount={craftInfo.Min}");
        }

        if (Plugin.AutoMaxNormalCrafts.Value && !craftInfo.IsMultiQualCraft && craftInfo.NotCraftable.Count == 0)
        {
            __instance._amount = craftInfo.Min;
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[Redraw] {__instance.craft_definition.id}: auto-max normal → _amount={craftInfo.Min}");
        }
    }

    // Catch zombie assign/unassign after load so mid-session changes don't need a save/reload.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.linked_worker), MethodType.Setter)]
    public static void WorldGameObject_linked_worker_Setter_Postfix(WorldGameObject __instance)
    {
        if (!MainGame.game_started) return;
        if (!Plugin.CraftsStarted) return;
        if (__instance?.obj_def == null) return;

        var benchId = __instance.obj_def.id;
        if (string.IsNullOrEmpty(benchId)) return;

        var hasZombieNow = IsZombieLinked(__instance);

        if (hasZombieNow)
        {
            if (Plugin.ZombieOccupiedBenches.Add(benchId))
            {
                if (Plugin.DebugEnabled) Plugin.WriteLog($"[ZombieLink] {benchId} now zombie-occupied → selective reapply queued");
                CraftComponentPatches.PendingBenchReapply.Add(benchId);
            }
            return;
        }

        if (!Plugin.ZombieOccupiedBenches.Contains(benchId)) return;

        // Only drop the bench type if no other instance still has a zombie linked.
        foreach (var wgo in WorldMap._objs)
        {
            if (wgo == null || wgo == __instance) continue;
            if (wgo.obj_def?.id != benchId) continue;
            if (IsZombieLinked(wgo)) return;
        }

        if (Plugin.ZombieOccupiedBenches.Remove(benchId))
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[ZombieLink] {benchId} no longer zombie-occupied (no peers) → selective reapply queued");
            CraftComponentPatches.PendingBenchReapply.Add(benchId);
        }
    }

    private static bool IsZombieLinked(WorldGameObject wgo)
    {
        return wgo.has_linked_worker
               && wgo.linked_worker != null
               && wgo.linked_worker.obj_id != null
               && wgo.linked_worker.obj_id.Contains("zombie");
    }

}


[HarmonyPatch(typeof(CraftComponent))]
public static class CraftComponentPatches
{
    private sealed class CraftSnapshot
    {
        public bool IsAuto;
        public CraftDefinition.EnqueueType EnqueueType;
        public SmartExpression Energy;
        public SmartExpression CraftTime;
        public bool ForceMultiCraft;
        public bool DisableMultiCraft;
        public Dictionary<int, int> FireNeedValues;
        public Dictionary<int, int> ResearchOutputValues;

        public CraftCategory Category;
        public SmartExpression CachedAutoCraftTime;
    }

    private static readonly Dictionary<string, CraftSnapshot> Snapshots = new(StringComparer.Ordinal);
    private static readonly HashSet<string> WarnedUncategorized = new(StringComparer.Ordinal);
    private static readonly SmartExpression ZeroEnergyExpr = SmartExpression.ParseExpression("0");

    internal static bool PendingFullReapply;
    internal static readonly HashSet<string> PendingBenchReapply = new(StringComparer.Ordinal);

    [HarmonyPostfix, HarmonyPatch(nameof(CraftComponent.FillCraftsList))]
    public static void FillCraftsList_Postfix()
    {
        if (Plugin.CcAlreadyRun) return;
        Plugin.CcAlreadyRun = true;

        CaptureSnapshots();
        ApplyCraftMutations();
    }

    private static void CaptureSnapshots()
    {
        Snapshots.Clear();
        foreach (var craft in GameBalance.me.craft_data)
        {
            if (craft == null || string.IsNullOrEmpty(craft.id))
            {
                continue;
            }

            var snap = new CraftSnapshot
            {
                IsAuto = craft.is_auto,
                EnqueueType = craft.enqueue_type,
                Energy = craft.energy,
                CraftTime = craft.craft_time,
                ForceMultiCraft = craft.force_multi_craft,
                DisableMultiCraft = craft.disable_multi_craft,
                FireNeedValues = null,
                ResearchOutputValues = null,
                Category = CraftCategories.Classify(craft.craft_in),
                CachedAutoCraftTime = null,
            };

            for (var i = 0; i < craft.needs_from_wgo.Count; i++)
            {
                if (craft.needs_from_wgo[i].id != "fire") continue;
                snap.FireNeedValues ??= new Dictionary<int, int>();
                snap.FireNeedValues[i] = craft.needs_from_wgo[i].value;
            }

            for (var i = 0; i < craft.output.Count; i++)
            {
                var id = craft.output[i].id;
                if (id is not ("r" or "g" or "b")) continue;
                snap.ResearchOutputValues ??= new Dictionary<int, int>();
                snap.ResearchOutputValues[i] = craft.output[i].value;
            }

            Snapshots[craft.id] = snap;
        }

        if (Plugin.DebugEnabled)
        {
            Plugin.WriteLog($"[Snapshots] captured {Snapshots.Count} craft definitions");
        }
    }

    internal static void ApplyCraftMutations()
    {
        if (!Plugin.CcAlreadyRun) return;

        var startTicks = Stopwatch.GetTimestamp();
        var counters = default(ApplyCounters);
        var visited = 0;

        foreach (var craft in GameBalance.me.craft_data)
        {
            if (craft == null || !Snapshots.TryGetValue(craft.id, out var snap)) continue;
            ApplyOne(craft, snap, ref counters);
            visited++;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        Plugin.Log.LogInfo(
            $"[ApplyCraftMutations] elapsed={elapsedMs:F2}ms visited={visited} converted={counters.Converted} halved={counters.Halved} fireAdjusted={counters.FireAdjusted} forcedMulti={counters.ForcedMulti} skipped(alreadyAuto={counters.SkippedAutoAlready}, unsafe={counters.SkippedUnsafe}, categoryOff={counters.SkippedCategoryOff})");
    }

    // Revisits only crafts that reference one of these benches, instead of all ~1000 crafts.
    internal static void ApplyCraftMutationsForBenches(IReadOnlyCollection<string> benchIds)
    {
        if (!Plugin.CcAlreadyRun) return;
        if (benchIds == null || benchIds.Count == 0) return;

        var startTicks = Stopwatch.GetTimestamp();
        var counters = default(ApplyCounters);
        var visited = 0;

        foreach (var craft in GameBalance.me.craft_data)
        {
            if (craft == null || !Snapshots.TryGetValue(craft.id, out var snap)) continue;
            if (craft.craft_in == null || craft.craft_in.Count == 0) continue;

            var matches = false;
            foreach (var bench in craft.craft_in)
            {
                if (benchIds.Contains(bench)) { matches = true; break; }
            }
            if (!matches) continue;

            ApplyOne(craft, snap, ref counters);
            visited++;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        Plugin.Log.LogInfo(
            $"[ApplyCraftMutations:Selective] benches=[{string.Join(",", benchIds)}] elapsed={elapsedMs:F2}ms visited={visited} converted={counters.Converted} halved={counters.Halved} fireAdjusted={counters.FireAdjusted} forcedMulti={counters.ForcedMulti} skipped(alreadyAuto={counters.SkippedAutoAlready}, unsafe={counters.SkippedUnsafe}, categoryOff={counters.SkippedCategoryOff})");
    }

    private struct ApplyCounters
    {
        public int Converted;
        public int Halved;
        public int FireAdjusted;
        public int ForcedMulti;
        public int SkippedAutoAlready;
        public int SkippedUnsafe;
        public int SkippedCategoryOff;
    }

    private static void ApplyOne(CraftDefinition craft, CraftSnapshot snap, ref ApplyCounters c)
    {
        RestoreFromSnapshot(craft, snap);
        if (TryAdjustFireRequirements(craft)) c.FireAdjusted++;
        if (TryApplyForcedMultiCraft(craft, snap)) c.ForcedMulti++;

        var result = TryMakeCraftAuto(craft, snap);
        switch (result)
        {
            case AutoResult.Converted: c.Converted++; break;
            case AutoResult.AlreadyAuto: c.SkippedAutoAlready++; break;
            case AutoResult.Unsafe: c.SkippedUnsafe++; break;
            case AutoResult.CategoryDisabled: c.SkippedCategoryOff++; break;
        }

        if (result == AutoResult.Converted && TryAdjustCraftOutput(craft)) c.Halved++;
    }

    private enum AutoResult { Converted, AlreadyAuto, Unsafe, CategoryDisabled }

    private static void RestoreFromSnapshot(CraftDefinition craft, CraftSnapshot snap)
    {
        craft.is_auto = snap.IsAuto;
        craft.enqueue_type = snap.EnqueueType;
        craft.energy = snap.Energy;
        craft.craft_time = snap.CraftTime;
        craft.force_multi_craft = snap.ForceMultiCraft;
        craft.disable_multi_craft = snap.DisableMultiCraft;

        if (snap.FireNeedValues != null)
        {
            foreach (var kv in snap.FireNeedValues)
            {
                if (kv.Key < craft.needs_from_wgo.Count)
                {
                    craft.needs_from_wgo[kv.Key].value = kv.Value;
                }
            }
        }

        if (snap.ResearchOutputValues != null)
        {
            foreach (var kv in snap.ResearchOutputValues)
            {
                if (kv.Key < craft.output.Count)
                {
                    craft.output[kv.Key].value = kv.Value;
                }
            }
        }
    }

    private static bool TryAdjustFireRequirements(CraftDefinition craft)
    {
        if (!Plugin.HalfFireRequirements.Value) return false;

        var touched = false;
        foreach (var item in craft.needs_from_wgo.Where(item => item.id == "fire"))
        {
            item.value = Mathf.CeilToInt(item.value / 2f);
            touched = true;
        }

        return touched;
    }

    private static bool TryApplyForcedMultiCraft(CraftDefinition craft, CraftSnapshot snap)
    {
        if (!Plugin.ForceMultiCraft.Value || snap.IsAuto || Plugin.IsUnsafeDefinition(craft)) return false;
        if (craft.IsMultiqualityOutput() && !Plugin.AllowMultiQualityMultiCraft.Value) return false;

        craft.force_multi_craft = true;
        craft.disable_multi_craft = false;
        return true;
    }

    private static AutoResult TryMakeCraftAuto(CraftDefinition craft, CraftSnapshot snap)
    {
        if (snap.IsAuto) return AutoResult.AlreadyAuto;
        if (Plugin.IsUnsafeDefinition(craft)) return AutoResult.Unsafe;

        WarnIfUncategorized(craft, snap);

        if (!Plugin.IsCategoryEnabled(snap.Category))
        {
            if (Plugin.DebugEnabled)
            {
                Plugin.WriteLog($"[MakeCraftAuto] skip '{craft.id}' (category={snap.Category} disabled)");
            }
            return AutoResult.CategoryDisabled;
        }

        if (snap.CachedAutoCraftTime == null)
        {
            var craftEnergyTime = snap.Energy.EvaluateFloat(MainGame.me.player);
            craftEnergyTime *= 1.50f;
            craftEnergyTime = Mathf.CeilToInt(craftEnergyTime);
            snap.CachedAutoCraftTime = SmartExpression.ParseExpression(craftEnergyTime.ToString(CultureInfo.InvariantCulture));
        }

        craft.craft_time = snap.CachedAutoCraftTime;
        craft.energy = ZeroEnergyExpr;
        craft.is_auto = true;
        craft.enqueue_type = CraftDefinition.EnqueueType.CanEnqueue;

        if (Plugin.DebugEnabled)
        {
            Plugin.WriteLog($"[MakeCraftAuto] convert '{craft.id}' (category={snap.Category})");
        }

        return AutoResult.Converted;
    }

    private static void WarnIfUncategorized(CraftDefinition craft, CraftSnapshot snap)
    {
        if (snap.Category != CraftCategory.Misc) return;
        if (craft.craft_in == null || craft.craft_in.Count == 0) return;

        foreach (var craftIn in craft.craft_in)
        {
            if (string.IsNullOrEmpty(craftIn) || !WarnedUncategorized.Add(craftIn))
            {
                continue;
            }

            Plugin.Log.LogWarning($"[QueueEverything] Uncategorized hand craftery: '{craftIn}' (sample craft: '{craft.id}'). Falling back to 'Misc'. If this should be in a named category, tell p1xel8ted.");
        }
    }

    private static bool TryAdjustCraftOutput(CraftDefinition craft)
    {
        if (!Plugin.HalfCraftOutputs.Value) return false;

        var touched = false;
        foreach (var output in craft.output)
        {
            if (output.id is not ("r" or "g" or "b")) continue;
            output.value /= 2;
            output.value = output.value < 1 ? 1 : Mathf.CeilToInt(output.value);
            touched = true;
        }

        if (touched && Plugin.DebugEnabled)
        {
            Plugin.WriteLog($"[AdjustCraftOutput] halved r/g/b outputs on '{craft.id}'");
        }

        return touched;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(CraftComponent.CraftReally))]
    public static void CraftReally_Prefix()
    {
        if (!MainGame.game_started || !Plugin.AnyAutoCraftCategoryEnabled()) return;

        var beforeCount = Plugin.CurrentlyCrafting.Count;
        Plugin.CurrentlyCrafting.RemoveAll(wgo => wgo == null || !wgo.components.craft.is_crafting || wgo.has_linked_worker || wgo.linked_worker != null);

        if (Plugin.DebugEnabled && Plugin.CurrentlyCrafting.Count > 0)
        {
            Plugin.WriteLog($"[CraftReally] pumping {Plugin.CurrentlyCrafting.Count} in-flight crafteries (pruned {beforeCount - Plugin.CurrentlyCrafting.Count}).");
        }

        foreach (var wgo in Plugin.CurrentlyCrafting)
        {
            wgo.OnWorkAction();
        }
    }
}


[HarmonyPatch(typeof(CraftDefinition))]
public static class CraftDefinitionPatches
{
    [HarmonyPostfix, HarmonyPatch(nameof(CraftDefinition.CanCraftMultiple))]
    public static void CraftDefinition_CanCraftMultiple(CraftDefinition __instance, ref bool __result)
    {
        if (__instance is ObjectCraftDefinition) return;

        // Quality items can't carry their chosen star through the craft queue, so multicraft
        // is unreliable for them. Block it unless the player opts in (which shows a warning).
        if (__instance.IsMultiqualityOutput() && !Plugin.AllowMultiQualityMultiCraft.Value)
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[CanCraftMultiple] {__instance.id}: multi-quality output, opt-in off → false");
            __result = false;
            return;
        }

        // The organ workbench stays on the unsafe list for everything else it hosts, but its own
        // skull upgrades are fine to run as one multi-level craft.
        if (Plugin.ForceMultiCraft.Value && OrganEnhancer.IsOrganCraft(__instance))
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[CanCraftMultiple] {__instance.id}: organ upgrade → true");
            __result = true;
            return;
        }

        if (!Plugin.ForceMultiCraft.Value || Plugin.IsUnsafeDefinition(__instance))
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[CanCraftMultiple] {__instance.id}: unsafe/force-off → {__result} (timeZero={__instance.craft_time_is_zero})");
            return;
        }

        if (Plugin.DebugEnabled) Plugin.WriteLog($"[CanCraftMultiple] {__instance.id}: forced → true (timeZero={__instance.craft_time_is_zero})");
        __result = true;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(CraftDefinition.GetSpendTxt))]
    public static void CraftDefinition_GetSpendTxt(CraftDefinition __instance, WorldGameObject wgo, ref string __result,
        int multiplier = 1)
    {
        // An organ run's energy already covers every level it was built for, so read the run's
        // figure and drop the amount multiplier that would otherwise count them all again.
        if (OrganEnhancer.TryGetPreview(__instance, out var organRun))
        {
            __instance = organRun;
            multiplier = 1;
        }

        var text = "";
        int num;
        if (GlobalCraftControlGUI.is_global_control_active)
            num = __instance.gratitude_points_craft_cost is not { has_expression: true }
                ? 0
                : Mathf.CeilToInt(__instance.gratitude_points_craft_cost.EvaluateFloat(wgo));
        else
            num = __instance.energy is not { has_expression: true }
                ? 0
                : Mathf.CeilToInt(__instance.energy.EvaluateFloat(wgo));

        if (num != 0)
        {
            var toolK = 1f;
            if (wgo?.obj_def?.tool_actions != null)
            {
                foreach (var actionTool in wgo.obj_def.tool_actions.action_tools)
                {
                    if (actionTool == ItemDefinition.ItemType.Hand) continue;
                    var equippedTool = MainGame.me.player.GetEquippedTool(actionTool);
                    if (equippedTool?.definition?.tool_energy_k is { has_expression: true })
                    {
                        var k = equippedTool.definition.tool_energy_k.EvaluateFloat(wgo, MainGame.me.player);
                        if (k < toolK) toolK = k;
                    }
                }
            }

            if (!toolK.EqualsTo(1f, 0.01f)) num = Mathf.CeilToInt(num * toolK);

            if (GlobalCraftControlGUI.is_global_control_active)
            {
                var cost = Mathf.CeilToInt(num * multiplier * ModCompat.GratitudeFactor());
                var hasEnough = MainGame.me.player.gratitude_points >= cost;
                text += hasEnough
                    ? $"[c](gratitude_points)[/c]{cost}"
                    : $"(gratitude_points)[c][ff1111]{cost}[/c]";
            }
            else
            {
                var cost = Mathf.CeilToInt(num * ModCompat.EnergyFactor()) * multiplier;
                text += $"[c](en)[/c]{cost}";
            }
        }

        if (__instance.is_auto)
        {
            var craftTime = __instance.craft_time.EvaluateFloat(wgo);
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[CraftText]: Craft: {__instance.id}, BaseTime: {craftTime}");

            if (craftTime != 0)
            {
                craftTime /= ModCompat.CraftSpeedMultiplier();

                craftTime = Mathf.CeilToInt(craftTime);
                if (craftTime > 0) craftTime *= multiplier;

                var timeSpan = TimeSpan.FromSeconds(craftTime);
                text = text.ConcatWithSeparator(timeSpan.Hours >= 1
                    ? $"[c](time)[/c]{timeSpan.Hours:0}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}"
                    : $"[c](time)[/c]{timeSpan.Minutes:0}:{timeSpan.Seconds:00}");
            }
        }

        foreach (var item in __instance.needs_from_wgo.Where(item => item.id == "fire"))
        {
            var text2 = $"[c](fire2)[/c]{item.value * multiplier:0}";
            if (!wgo!.data.IsEnoughItems(item, "", 0, multiplier)) text2 = $"[ff1111]{text2}[/c]";
            text = text.ConcatWithSeparator(text2);
        }

        if (wgo?.obj_def?.tool_actions != null && !__instance.is_auto)
        {
            for (var j = 0; j < wgo.obj_def.tool_actions.action_tools.Count; j++)
            {
                var itemType = wgo.obj_def.tool_actions.action_tools[j];
                if (itemType == ItemDefinition.ItemType.Hand) continue;
                var toolName = itemType.ToString().ToLower();
                var efficiency = Mathf.FloorToInt(100f * wgo.obj_def.tool_actions.action_k[j]);
                var equippedTool = MainGame.me.player.GetEquippedTool(itemType);
                if (equippedTool == null)
                {
                    text += $"\n[c][ff1111]({toolName}_s)[-][/c]";
                }
                else
                {
                    efficiency = Mathf.FloorToInt(efficiency * equippedTool.definition.efficiency);
                    text += $"\n[c]({toolName}_s)[/c]\n{efficiency}%";
                }
            }
        }

        __result = text;
    }
}


[HarmonyPatch(typeof(CraftGUI))]
public static class CraftGUIPatches
{
    [HarmonyPostfix, HarmonyPatch(typeof(CraftGUI), nameof(CraftGUI.ExpandItem))]
    public static void ExpandItem_Postfix(CraftGUI __instance, CraftItemGUI craft_item_gui)
    {
        if (Plugin.IsUnsafeDefinition(craft_item_gui.craft_definition)) return;
        if (!Plugin.AutoSelectCraftButtonWithController.Value) return;
        if (!LazyInput.gamepad_active) return;

        var craftBtn = craft_item_gui.multiquality_craft_btn;
        if (craftBtn == null) return;

        var navItem = craftBtn.GetComponent<GamepadNavigationItem>();
        if (navItem == null) return;

        craftBtn.gameObject.SetActive(true);
        __instance.gamepad_controller.SetFocusedItem(navItem);
    }

    // Every other way of opening the craft window says whether it wants the amount arrows, but
    // the organ one never does, so it inherits whatever the last window left behind. Ask for
    // them, or the arrows show up only when the player happened to visit a workbench first.
    [HarmonyPrefix, HarmonyPatch(nameof(CraftGUI.OpenAsOrganEnhancer))]
    public static void OpenAsOrganEnhancer_Prefix(CraftGUI __instance)
    {
        if (!Plugin.ForceMultiCraft.Value) return;

        __instance.has_amount_buttons = true;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(CraftGUI.Open))]
    public static void Open_Postfix()
    {
        Plugin.AlreadyRun = true;
        if (Plugin.DebugEnabled)
        {
            var crafteryWgo = GUIElements.me.craft.GetCrafteryWGO();
            Plugin.WriteLog($"Keeper interacted with: {crafteryWgo.obj_id}.");
        }
    }

    [HarmonyPrefix, HarmonyPatch(nameof(CraftGUI.Open))]
    public static void Open_Prefix()
    {
        Plugin.AlreadyRun = false;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(CraftGUI.SwitchTab))]
    public static void SwitchTab_Postfix()
    {
        Plugin.AlreadyRun = true;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(CraftGUI.SwitchTab))]
    public static void SwitchTab_Prefix()
    {
        Plugin.AlreadyRun = false;
    }

    // +1 / -1 buttons in the expanded multi-quality view. Min/Max in the same view live in MaxButtonsRedux.
    [HarmonyAfter("p1xel8ted.gyk.restinpatches")]
    [HarmonyPostfix, HarmonyPatch(typeof(CraftGUI), nameof(CraftGUI.Open),
        typeof(WorldGameObject), typeof(CraftsInventory), typeof(string))]
    public static void CraftGUI_Open_InstallExpandButtons(CraftGUI __instance)
    {
        if (LazyInput.gamepad_active) return;
        foreach (var t in __instance.GetComponentsInChildren<CraftItemGUI>())
        {
            ExpandViewAmountButtons.Install(t);
        }
    }

    [HarmonyAfter("p1xel8ted.gyk.restinpatches")]
    [HarmonyPostfix, HarmonyPatch(typeof(CraftGUI), nameof(CraftGUI.SwitchTab))]
    public static void CraftGUI_SwitchTab_InstallExpandButtons(CraftGUI __instance)
    {
        foreach (var t in __instance.GetComponentsInChildren<CraftItemGUI>())
        {
            ExpandViewAmountButtons.Install(t);
        }
    }
}


[HarmonyPatch(typeof(CraftItemGUI))]
public static class CraftItemGUIPatches
{
    [HarmonyPostfix, HarmonyPatch(nameof(CraftItemGUI.OnCraftPressed))]
    public static void OnCraftPressed_Postfix(WorldGameObject __state)
    {
        if (!Plugin.AnyAutoCraftCategoryEnabled() || __state == null || __state.linked_worker != null || __state.has_linked_worker) return;

        Plugin.CurrentlyCrafting.Add(__state);
        if (Plugin.DebugEnabled) Plugin.WriteLog($"[OnCraftPressed] tracking new auto-craft on '{__state.obj_id}' (total tracked: {Plugin.CurrentlyCrafting.Count})");
        __state.OnWorkAction();
    }

    [HarmonyPrefix, HarmonyPatch(nameof(CraftItemGUI.OnCraftPressed))]
    public static void OnCraftPressed_Prefix(CraftItemGUI __instance, ref WorldGameObject __state)
    {
        if (Plugin.DebugEnabled) Plugin.WriteLog($"Craft: {__instance.craft_definition.id}, One time: {__instance.craft_definition.one_time_craft}");
        if (Plugin.IsUnsafeDefinition(__instance.craft_definition)) return;
        if (__instance.craft_definition is ObjectCraftDefinition) return;

        var crafteryWgo = GUIElements.me.craft.GetCrafteryWGO();
        __state = crafteryWgo;

        var time = __instance.craft_definition.craft_time.EvaluateFloat(crafteryWgo);
        ApplyFasterCraft(ref time);
    }

    private static void ApplyFasterCraft(ref float time)
    {
        time /= ModCompat.CraftSpeedMultiplier();
    }

    // Works out the organ run before the row draws, so the shard and energy figures cover every
    // level the player asked for. Runs last so it reads the amount auto-max just settled on.
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    [HarmonyPatch(nameof(CraftItemGUI.Redraw))]
    public static void Redraw_Prefix(CraftItemGUI __instance, out OrganPreview __state)
    {
        __state = OrganEnhancer.SwapPreview(BuildOrganPreview(__instance));
    }

    [HarmonyFinalizer, HarmonyPatch(nameof(CraftItemGUI.Redraw))]
    public static void Redraw_Finalizer(OrganPreview __state)
    {
        OrganEnhancer.SwapPreview(__state);
    }

    private static OrganPreview BuildOrganPreview(CraftItemGUI craftItemGui)
    {
        var craft = craftItemGui.current_craft;
        if (!Plugin.ForceMultiCraft.Value || !OrganEnhancer.IsOrganCraft(craft)) return default;

        var bench = OrganBench();
        if (bench == null) return default;

        ClampAutoMax(craftItemGui, craft, bench);
        if (craftItemGui._amount <= 1) return default;

        // No inventory passed: the cost shown is what the chosen amount would cost, even when
        // the player can't afford it. The greyed-out ingredients say the rest.
        var run = OrganEnhancer.BuildRun(craft, bench, craftItemGui._amount, null);
        return run == null ? default : new OrganPreview(craft, run.Definition);
    }

    // Auto-max sizes the amount by dividing what you have by the first level's cost, but each
    // organ level costs more than the last and there are only so many to add. Bring the opening
    // amount back to what the run really reaches. Only on open, so a number picked by hand is
    // left where the player put it.
    private static void ClampAutoMax(CraftItemGUI craftItemGui, CraftDefinition craft, WorldGameObject bench)
    {
        if (Plugin.AlreadyRun || craftItemGui._amount <= 1) return;
        if (!Plugin.AutoMaxNormalCrafts.Value && !Plugin.AutoMaxMultiQualCrafts.Value) return;

        var run = OrganEnhancer.BuildRun(craft, bench, craftItemGui._amount, GUIElements.me.organ_enhancer_gui?._multi_inventory);
        var reachable = run?.Steps ?? 1;
        if (reachable >= craftItemGui._amount) return;

        if (Plugin.DebugEnabled) Plugin.WriteLog($"[OrganEnhancer] auto-max asked for {craftItemGui._amount}, run reaches {reachable}");
        craftItemGui._amount = reachable;
    }

    // Stop the plus arrow once the run can't reach another level. Asking for one more than the
    // current amount keeps this bounded without putting a ceiling on the run itself.
    [HarmonyPrefix, HarmonyPatch(nameof(CraftItemGUI.OnAmountPlus))]
    public static bool OnAmountPlus_Prefix(CraftItemGUI __instance)
    {
        var craft = __instance.current_craft;
        if (!Plugin.ForceMultiCraft.Value || !OrganEnhancer.IsOrganCraft(craft)) return true;

        var bench = OrganBench();
        if (bench == null) return true;

        var wanted = __instance._amount + 1;
        var run = OrganEnhancer.BuildRun(craft, bench, wanted, GUIElements.me.organ_enhancer_gui?._multi_inventory);
        return run != null && run.Steps >= wanted;
    }

    private static WorldGameObject OrganBench() => GUIElements.me.organ_enhancer_gui?._craftery_wgo;
}


[HarmonyPatch(typeof(BaseItemCellGUI))]
public static class BaseItemCellGUIPatches
{
    // The organ run's shard list already adds up every level, so draw it as it is.
    [HarmonyPrefix, HarmonyPatch(nameof(BaseItemCellGUI.DrawIngredients))]
    public static void DrawIngredients_Prefix(ref List<Item> items, ref int amount)
    {
        if (!OrganEnhancer.TryGetPreviewNeeds(items, out var aggregate)) return;

        items = aggregate;
        amount = 1;
    }
}


[HarmonyPatch(typeof(OrganEnhancerGUI))]
public static class OrganEnhancerGUIPatches
{
    [HarmonyPrefix, HarmonyPatch(nameof(OrganEnhancerGUI.CanCraft))]
    public static bool CanCraft_Prefix(OrganEnhancerGUI __instance, CraftDefinition craft, int amount, ref bool __result)
    {
        if (!Plugin.ForceMultiCraft.Value || amount <= 1 || !OrganEnhancer.IsOrganCraft(craft)) return true;

        var run = OrganEnhancer.BuildRun(craft, __instance._craftery_wgo, amount, null);
        __result = run != null && run.Steps == amount && __instance._multi_inventory.IsEnoughItems(run.Needs);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(OrganEnhancerGUI.OnCraft))]
    public static bool OnCraft_Prefix(OrganEnhancerGUI __instance, CraftDefinition craft, int amount, ref bool __result)
    {
        if (!Plugin.ForceMultiCraft.Value || amount <= 1 || !OrganEnhancer.IsOrganCraft(craft)) return true;

        var bench = __instance._craftery_wgo;
        if (bench == null) return true;

        var run = OrganEnhancer.BuildRun(craft, bench, amount, null);
        if (run == null || run.Steps < amount || !__instance._multi_inventory.IsEnoughItems(run.Needs))
        {
            // Craft nothing rather than part of it. The organ stays in the slot and the window
            // stays open so the amount can be lowered.
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[OrganEnhancer] {craft.id}: asked for {amount}, reachable {run?.Steps ?? 0} → nothing crafted");
            __result = false;
            return false;
        }

        var crafted = bench.components.craft.Craft(run.Definition, override_needs: run.Needs, ignore_crafts_list: true);
        if (Plugin.DebugEnabled)
        {
            Plugin.WriteLog($"[OrganEnhancer] {craft.id}: {amount} levels → {run.Definition.output[0].id}, needs {string.Join(", ", run.Needs.Select(n => $"{n.id}x{n.value}"))}, crafted={crafted}");
        }

        if (!crafted)
        {
            __result = false;
            return false;
        }

        __instance._current_item_gui = null;
        GUIElements.me.craft.Hide(false);
        if (GlobalCraftControlGUI.is_global_control_active)
        {
            GUIElements.me.global_craft_control_gui.Open();
        }

        __instance.Hide(true, false);
        __result = true;
        return false;
    }
}
