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

        // The first ApplyCraftMutations pass (during FillCraftsList_Postfix) ran without knowing
        // about these benches — queue a selective reapply for just the discovered bench types so
        // those crafts get restored to vanilla on the next frame, without touching the other ~1000
        // unrelated craft definitions.
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
        Plugin.FasterCraftEnabled = false;
        Plugin.FasterCraftReloaded = false;
        Plugin.ExhaustlessEnabled = false;

        Plugin.FasterCraftReloaded = Harmony.HasAnyPatches("p1xel8ted.gyk.fastercraftreloaded");
        Plugin.ExhaustlessEnabled = Harmony.HasAnyPatches("p1xel8ted.gyk.exhaust-less");

        if (Plugin.FasterCraftReloaded)
        {
            Plugin.TimeAdjustment = Plugin.FcTimeAdjustment.Value;
            Plugin.FasterCraftEnabled = true;
            if (Plugin.DebugEnabled) Plugin.WriteLog($"FasterCraft Reloaded! detected, using its config.");
        }

        if (Plugin.ExhaustlessEnabled)
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"Exhaust-less! detected, using its config.");
        }

        // Per-save state must clear between loads. Without this, going Main Menu → Load Save B
        // after a session on Save A leaves Save A's WGO references in CurrentlyCrafting and
        // Save A's bench types in ZombieOccupiedBenches, both of which would mis-drive Save B.
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

        var canCraftMultiple = __instance.craft_definition.CanCraftMultiple();
        if (!canCraftMultiple)
        {
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[Redraw] {__instance.craft_definition.id}: skip (CanCraftMultiple=false) → _amount=1");
            __instance._amount = 1;
            return;
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

    // Reactive update for ZombieOccupiedBenches when the player assigns or unassigns a worker.
    // The initial scan in MainGame_Update covers what's loaded from save; this covers everything
    // after, so reassigning a zombie mid-session no longer needs a save/reload to take effect.
    // Gated on Plugin.CraftsStarted so the linked_worker writes the game does during world load
    // are ignored — those are already aggregated into the initial scan.
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

        // The instance just lost its zombie. The TYPE only leaves the set if no peer instance
        // of the same obj_def.id still has a zombie linked — otherwise we'd unblock auto-craft
        // on a craft that's still being run by a zombie elsewhere.
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

        // IsUnsafe is recomputed per apply via Plugin.IsUnsafeDefinition(craft) — the zombie-bench
        // set is populated AFTER this snapshot is captured, so caching the value here would lock
        // in the wrong answer for the second apply pass.
        public CraftCategory Category;
        public SmartExpression CachedAutoCraftTime;
    }

    private static readonly Dictionary<string, CraftSnapshot> Snapshots = new(StringComparer.Ordinal);
    private static readonly HashSet<string> WarnedUncategorized = new(StringComparer.Ordinal);
    private static readonly SmartExpression ZeroEnergyExpr = SmartExpression.ParseExpression("0");

    // Two-state reapply queue:
    //  - PendingFullReapply: a config change happened, every craft's mutation state must be
    //    re-evaluated. Wins over PendingBenchReapply.
    //  - PendingBenchReapply: bench obj_def.ids whose zombie-occupancy flipped. Only the crafts
    //    that reference one of these benches need re-evaluating; the rest are unchanged. ~1ms
    //    instead of ~5ms across the ~1000 craft definitions.
    // The drain in MainGame_Update postfix runs at most once per frame regardless of how many
    // triggers fire — natural debouncing for rapid zombie reassignments.
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

    // Selective variant: revisits only the crafts whose craft_in references one of the supplied
    // bench obj_def.ids. Used when a zombie attaches/detaches — most craft definitions don't care
    // about that bench, so paying the full ~1000-craft loop wastes ~5ms of frame time. Restoring
    // from snapshot first means a craft that was previously auto-converted (because the bench
    // wasn't yet known to be zombie-occupied) gets correctly reverted to vanilla on detach.
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
                var cost = Plugin.ExhaustlessEnabled ? Mathf.CeilToInt(num * multiplier / 2f) : num * multiplier;
                var hasEnough = MainGame.me.player.gratitude_points >= (__instance.gratitude_points_craft_cost?.EvaluateFloat(MainGame.me.player) ?? 0f);
                text += hasEnough
                    ? $"[c](gratitude_points)[/c]{cost}"
                    : $"(gratitude_points)[c][ff1111]{cost}[/c]";
            }
            else
            {
                var cost = Plugin.ExhaustlessEnabled ? Mathf.CeilToInt(num / 2f) * multiplier : Mathf.CeilToInt(num * multiplier);
                text += $"[c](en)[/c]{cost}";
            }
        }

        if (__instance.is_auto)
        {
            var craftTime = __instance.craft_time.EvaluateFloat(wgo);
            if (Plugin.DebugEnabled) Plugin.WriteLog($"[CraftText]: Craft: {__instance.id}, BaseTime: {craftTime}");

            if (craftTime != 0)
            {
                if (Plugin.FasterCraftEnabled)
                    craftTime = Plugin.TimeAdjustment < 0 ? craftTime * Plugin.TimeAdjustment : craftTime / Plugin.TimeAdjustment;

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

    // Singular +1 / -1 buttons in the expanded multi-quality view. Vanilla never
    // wired amount controls into full_detailed_go; QE owns these because QE's
    // multi-qual features are what make that view actually adjustable. Min/Max
    // in the same view are owned by MaxButtonsRedux — see ExpandViewAmountButtons.cs.
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
        if (!Plugin.FasterCraftEnabled) return;

        if (Plugin.TimeAdjustment < 0)
            time /= Plugin.TimeAdjustment;
        else
            time *= Plugin.TimeAdjustment;
    }
}
