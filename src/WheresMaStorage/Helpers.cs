namespace WheresMaStorage;

public static class Helpers
{
    private static readonly Dictionary<string, bool> OriginalHandToolDestroy = new();
    internal static readonly Dictionary<string, int> OriginalInventorySizes = new();
    private static readonly Dictionary<string, int> OriginalStackSizes = new();

    internal static void RunWmsTasks()
    {
        if (!MainGame.game_started) return;

        if (Plugin.DebugEnabled) Plugin.Log.LogInfo("Running WMS Tasks (player spawned in or a new chest/craft was built/destroyed).");

        MainGame.me.StartCoroutine(Invents.LoadWildernessInventories());
        Fields.LoadInventoriesCoroutine = MainGame.me.StartCoroutine(Invents.LoadInventories());

        if (Plugin.CollectDropsOnGameLoad.Value && !Fields.DropsCleaned)
        {
            CollectDrops();
        }
    }

    internal static void GameBalanceLoad()
    {
        if (Fields.GameBalanceAlreadyRun) return;
        Fields.GameBalanceAlreadyRun = true;
        Plugin.Log.LogInfo("Running WMS GameBalanceLoad as GameBalance has been loaded.");
        var watch = Stopwatch.StartNew();

        BackupOriginalData();

        UpdateToolDestroy();
        UpdateInventorySizes();
        UpdateStackSizes();

        watch.Stop();
        if (Plugin.DebugEnabled) Log($"WMS Modifications Loaded! Completed in {watch.ElapsedMilliseconds}ms");
    }

    private static void BackupOriginalData()
    {
        foreach (var itemDefinition in GameBalance.me.items_data)
        {
            if (Plugin.DebugEnabled) Log($"Backing up stack size for: {itemDefinition.id}, {itemDefinition.stack_count}");
            OriginalStackSizes.TryAdd(itemDefinition.id, itemDefinition.stack_count);
        }

        foreach (var objectDefinition in GameBalance.me.objs_data)
        {
            if (Plugin.DebugEnabled) Log($"Backing up inventory size for: {objectDefinition.id}, {objectDefinition.inventory_size}");
            OriginalInventorySizes.TryAdd(objectDefinition.id, objectDefinition.inventory_size);
        }

        foreach (var itemDefinition in GameBalance.me.items_data.Where(a => a.is_tool))
        {
            if (Plugin.DebugEnabled) Log($"Backing up hand tool destroy for: {itemDefinition.id}, {itemDefinition.player_cant_throw_out}");
            OriginalHandToolDestroy.TryAdd(itemDefinition.id, itemDefinition.player_cant_throw_out);
        }
    }

    internal static void UpdateStackSizes()
    {
        RestoreStackSizes();
        if (!Plugin.ModifyStackSize.Value) return;

        foreach (var id in GameBalance.me.items_data.Where(id => id.stack_count is > 1 and <= 999))
        {
            if (id.is_tool || id.IsWeapon() || id.IsEquipment() || id.type == ItemDefinition.ItemType.Preach || Fields.PenPaperInkItems.Contains(id.id) || Fields.ChiselItems.Contains(id.id) || Fields.GraveItems.Contains(id.type) || Fields.SinShardItems.Contains(id.id)) continue;
            if (!OriginalStackSizes.TryGetValue(id.id, out var originalSize)) continue;

            var newSize = Mathf.Min(originalSize + Plugin.StackSizeForStackables.Value, 999);
            if (Plugin.DebugEnabled) Log($"Modifying stack size for: {id.id}, {id.stack_count} -> {newSize}");
            id.stack_count = newSize;
        }

        if (Plugin.EnableEquipmentStacking.Value)
        {
            foreach (var item in GameBalance.me.items_data.Where(item => item.IsEquipment() && !item.is_tool))
            {
                item.stack_count = Mathf.Min(item.stack_count + Plugin.StackSizeForStackables.Value, 999);
                if (Plugin.DebugEnabled) Log($"EquipmentStacking: Modifying stack size for: {item.id}, {item.stack_count}");
            }
        }

        if (Plugin.EnableWeaponStacking.Value)
        {
            foreach (var item in GameBalance.me.items_data.Where(item => item.IsWeapon()))
            {
                item.stack_count = Mathf.Min(item.stack_count + Plugin.StackSizeForStackables.Value, 999);
                if (Plugin.DebugEnabled) Log($"WeaponStacking: Modifying stack size for: {item.id}, {item.stack_count}");
            }
        }


        if (Plugin.EnableToolStacking.Value)
        {
            foreach (var item in GameBalance.me.items_data.Where(item => item.is_tool && !item.IsWeapon()))
            {
                item.stack_count = Mathf.Min(item.stack_count + Plugin.StackSizeForStackables.Value, 999);
                if (Plugin.DebugEnabled) Log($"ToolStacking: Modifying stack size for: {item.id}, {item.stack_count}");
            }
        }

        if (Plugin.EnablePrayerStacking.Value)
        {
            foreach (var item in GameBalance.me.items_data.Where(item => item.type == ItemDefinition.ItemType.Preach))
            {
                item.stack_count = Mathf.Min(item.stack_count + Plugin.StackSizeForStackables.Value, 999);
                if (Plugin.DebugEnabled) Log($"PrayerStacking: Modifying stack size for: {item.id}, {item.stack_count}");
            }
        }

        if (Plugin.EnablePenPaperInkStacking.Value)
        {
            foreach (var item in GameBalance.me.items_data.Where(item => Fields.PenPaperInkItems.Any(item.id.Contains)))
            {
                item.stack_count = Mathf.Min(item.stack_count + Plugin.StackSizeForStackables.Value, 999);
                if (Plugin.DebugEnabled) Log($"PenPaperInkStacking: Modifying stack size for: {item.id}, {item.stack_count}");
            }
        }

        if (Plugin.EnableChiselStacking.Value)
        {
            foreach (var item in GameBalance.me.items_data.Where(item => Fields.ChiselItems.Any(item.id.Contains)))
            {
                item.stack_count = Mathf.Min(item.stack_count + Plugin.StackSizeForStackables.Value, 999);
                if (Plugin.DebugEnabled) Log($"ChiselStacking: Modifying stack size for: {item.id}, {item.stack_count}");
            }
        }

        if (Plugin.EnableGraveItemStacking.Value)
        {
            foreach (var item in GameBalance.me.items_data.Where(item => Fields.GraveItems.Contains(item.type)))
            {
                item.stack_count = Mathf.Min(item.stack_count + Plugin.StackSizeForStackables.Value, 999);
                if (Plugin.DebugEnabled) Log($"GraveItemStacking: Modifying stack size for: {item.id}, {item.stack_count}");
            }
        }
    }

    private static void RestoreStackSizes()
    {
        foreach (var id in GameBalance.me.items_data)
        {
            if (!OriginalStackSizes.TryGetValue(id.id, out var originalSize)) continue;

            if (Plugin.DebugEnabled) Log($"Restoring stack size for: {id.id}, {id.stack_count} -> {originalSize}");
            id.stack_count = originalSize;
        }
    }

    private static void RestoreToolDestroy()
    {
        foreach (var itemDef in GameBalance.me.items_data.Where(a => a.is_tool))
        {
            if (!OriginalHandToolDestroy.TryGetValue(itemDef.id, out var originalValue)) continue;

            if (Plugin.DebugEnabled) Log($"Restoring hand tool destroy for: {itemDef.id}, {itemDef.player_cant_throw_out} -> {originalValue}");
            itemDef.player_cant_throw_out = originalValue;
        }
    }

    private static void RestoreInventorySizes()
    {
        ApplyPlayerInventorySize();

        foreach (var od in GameBalance.me.objs_data.Where(a => a.interaction_type == ObjectDefinition.InteractionType.Chest))
        {
            if (!OriginalInventorySizes.TryGetValue(od.id, out var originalSize)) continue;

            if (Plugin.DebugEnabled) Log($"Restoring Inventory Size for: {od.id}, {od.inventory_size} -> {originalSize}");
            od.inventory_size = originalSize;
        }
    }

    internal static void UpdateToolDestroy()
    {
        RestoreToolDestroy();
        if (!Plugin.AllowHandToolDestroy.Value) return;

        foreach (var itemDef in GameBalance.me.items_data.Where(a => a.is_tool))
        {
            if (Plugin.DebugEnabled) Log($"Modifying hand tool destroy for: {itemDef.id}");
            itemDef.player_cant_throw_out = false;
        }
    }

    internal static void UpdateInventorySizes()
    {
        RestoreInventorySizes();

        // Player live size goes through ApplyPlayerInventorySize so we never write below current item count.
        ApplyPlayerInventorySize();

        if (!Plugin.ModifyInventorySize.Value) return;

        foreach (var od in GameBalance.me.objs_data.Where(od => od.interaction_type == ObjectDefinition.InteractionType.Chest && IsValidStorage(od.inventory_size)))
        {
            if (!OriginalInventorySizes.TryGetValue(od.id, out var originalSize)) continue;

            od.inventory_size = originalSize + Plugin.AdditionalContainerInventorySpace.Value;
            if (Plugin.DebugEnabled) Log($"Modifying Inventory Size for: {od.id}, {originalSize} -> {od.inventory_size}");
        }
    }

    internal static bool IsValidStorage(int size)
    {
        return size is 20 or 25 or 5;
    }

    // The game hardcodes the player's vanilla inventory to 20 in GameSave.InitPlayersInventory.
    // Don't read OriginalInventorySizes for the player: a save with a WMS-modified size baked in
    // would back up the modified value, not the true vanilla 20.
    internal const int PlayerVanillaSize = 20;

    internal static int GetVanillaSizeForWgo(WorldGameObject wgo)
    {
        if (wgo == null) return 0;
        if (wgo.is_player) return PlayerVanillaSize;
        if (wgo.obj_def?.id != null && OriginalInventorySizes.TryGetValue(wgo.obj_def.id, out var orig))
        {
            return orig;
        }
        return 0;
    }

    internal static int? GetRequestedSize(WorldGameObject wgo)
    {
        if (wgo == null) return null;

        if (wgo.is_player)
        {
            // Always use the hardcoded vanilla (see PlayerVanillaSize note above).
            return Plugin.ModifyInventorySize.Value
                ? PlayerVanillaSize + Plugin.AdditionalPlayerInventorySpace.Value
                : PlayerVanillaSize;
        }

        if (wgo.obj_def?.id == null) return null;
        if (!OriginalInventorySizes.TryGetValue(wgo.obj_def.id, out var originalSize)) return null;

        // Non-player: only types whose vanilla size matches IsValidStorage are managed.
        if (!IsValidStorage(originalSize)) return null;
        return Plugin.ModifyInventorySize.Value
            ? originalSize + Plugin.AdditionalContainerInventorySpace.Value
            : originalSize;
    }

    internal static int GetRequestedPlayerInventorySize()
    {
        return Plugin.ModifyInventorySize.Value
            ? PlayerVanillaSize + Plugin.AdditionalPlayerInventorySpace.Value
            : PlayerVanillaSize;
    }

    // Widens the requested size up to inventory.Count so nothing visible gets hidden.
    internal static void ApplyPlayerInventorySize()
    {
        if (!MainGame.game_started || MainGame.me.player == null) return;
        var data = MainGame.me.player.data;
        if (data?.inventory == null) return;
        var requested = GetRequestedPlayerInventorySize();
        var clamped = Math.Max(requested, data.inventory.Count);
        if (data.inventory_size != clamped)
        {
            data.SetInventorySize(clamped);
        }
    }

    internal class ShrinkPlan
    {
        public int PlayerOverflow;
        public int PlayerCount;
        public int PlayerRequested;
        public int TotalChestOverflow;
        public List<(WorldGameObject Wgo, int Overflow)> ChestOverflows = [];
        public List<(WorldGameObject Wgo, int RequestedSize)> ContainersToShrink = [];
        public bool HasOverflow => PlayerOverflow > 0 || TotalChestOverflow > 0;
    }

    internal static ShrinkPlan PlanShrink()
    {
        var plan = new ShrinkPlan();
        if (WorldMap._objs == null) return plan;

        foreach (var wgo in WorldMap._objs)
        {
            if (wgo == null) continue;
            var requested = GetRequestedSize(wgo);
            if (requested == null) continue;
            var data = wgo.data;
            if (data?.inventory == null) continue;
            if (data.inventory_size == requested.Value) continue;

            plan.ContainersToShrink.Add((wgo, requested.Value));
            var overflow = data.inventory.Count - requested.Value;
            if (overflow <= 0) continue;

            if (wgo.is_player)
            {
                plan.PlayerOverflow = overflow;
                plan.PlayerCount = data.inventory.Count;
                plan.PlayerRequested = requested.Value;
            }
            else
            {
                plan.ChestOverflows.Add((wgo, overflow));
                plan.TotalChestOverflow += overflow;
            }
        }
        return plan;
    }

    internal static void ApplyShrinkPlan(ShrinkPlan plan)
    {
        // Player first (so its drops appear at the player's feet before anything else).
        if (plan.PlayerOverflow > 0 && MainGame.me.player != null)
        {
            var data = MainGame.me.player.data;
            var overflow = data.inventory.Skip(plan.PlayerRequested).ToList();
            Log($"Inventory shrink: ejecting {overflow.Count} item(s) from player onto the ground.");
            foreach (var item in overflow)
            {
                MainGame.me.player.DropItem(item);
                data.inventory.Remove(item);
            }
            if (data.inventory_size != plan.PlayerRequested) data.SetInventorySize(plan.PlayerRequested);
        }

        // Each container.
        foreach (var (wgo, requestedSize) in plan.ContainersToShrink)
        {
            if (wgo == null || wgo.is_player) continue; // player handled above
            var data = wgo.data;
            if (data?.inventory == null) continue;
            if (data.inventory.Count > requestedSize)
            {
                var overflow = data.inventory.Skip(requestedSize).ToList();
                Log($"Inventory shrink: ejecting {overflow.Count} item(s) from {wgo.obj_id} at zone '{wgo.GetMyWorldZoneId()}'.");
                foreach (var item in overflow)
                {
                    wgo.DropItem(item);
                    data.inventory.Remove(item);
                }
            }
            if (data.inventory_size != requestedSize) data.SetInventorySize(requestedSize);
        }
    }

    internal static void SnapConfigToCurrentSizes(ShrinkPlan plan)
    {
        // Find the minimum slider value per side that would cover its overflow.
        // Player and container sliders are independent, so compute each separately
        // and only nudge the slider(s) that actually have an overflow on that side.
        var playerNeeded = 0;
        if (plan.PlayerOverflow > 0 && MainGame.me.player != null)
        {
            var playerVanilla = GetVanillaSizeForWgo(MainGame.me.player);
            playerNeeded = MainGame.me.player.data.inventory.Count - playerVanilla;
        }

        var containerNeeded = 0;
        foreach (var (wgo, _) in plan.ChestOverflows)
        {
            if (wgo?.obj_def?.id == null) continue;
            if (!OriginalInventorySizes.TryGetValue(wgo.obj_def.id, out var orig)) continue;
            containerNeeded = Math.Max(containerNeeded, wgo.data.inventory.Count - orig);
        }

        // Clamp each to the slider's AcceptableValueRange<int>(0, 500).
        playerNeeded = Math.Max(0, Math.Min(500, playerNeeded));
        containerNeeded = Math.Max(0, Math.Min(500, containerNeeded));

        // Force ModifyInventorySize on if it was the toggle that triggered the prompt.
        if (!Plugin.ModifyInventorySize.Value)
        {
            Plugin.ModifyInventorySize.Value = true;
        }
        if (plan.PlayerOverflow > 0 && Plugin.AdditionalPlayerInventorySpace.Value != playerNeeded)
        {
            Plugin.AdditionalPlayerInventorySpace.Value = playerNeeded;
        }
        if (plan.TotalChestOverflow > 0 && Plugin.AdditionalContainerInventorySpace.Value != containerNeeded)
        {
            Plugin.AdditionalContainerInventorySpace.Value = containerNeeded;
        }
        // SettingChanged will fire and re-flip InventorySizesDirty; the next drain sees no overflow
        // (because we just sized the slider(s) to cover everything) and applies silently. No loop.
    }

    internal static void HandleInventorySizesDirty()
    {
        if (Fields.ShrinkDialogOpen) return; // dialog already open; let user answer first

        var plan = PlanShrink();
        if (!plan.HasOverflow)
        {
            // Nothing would be hidden, apply the new sizes silently.
            ApplyShrinkPlan(plan);
            return;
        }

        Fields.ShrinkDialogOpen = true;
        Lang.Reload();
        HideConfigurationManagerWindow();
        var message = BuildShrinkMessage(plan);
        GUIElements.me.dialog.OpenYesNo(
            message,
            delegate_1: () =>
            {
                Fields.ShrinkDialogOpen = false;
                ApplyShrinkPlan(plan);
            },
            delegate_2: () =>
            {
                Fields.ShrinkDialogOpen = false;
                SnapConfigToCurrentSizes(plan);
            });
    }

    private static string BuildShrinkMessage(ShrinkPlan plan)
    {
        if (plan.PlayerOverflow > 0 && plan.TotalChestOverflow > 0)
        {
            return string.Format(Lang.Get("ShrinkConfirmBoth"),
                plan.PlayerOverflow, plan.TotalChestOverflow, plan.ChestOverflows.Count);
        }
        if (plan.PlayerOverflow > 0)
        {
            return string.Format(Lang.Get("ShrinkConfirmPlayer"), plan.PlayerOverflow);
        }
        return string.Format(Lang.Get("ShrinkConfirmChests"),
            plan.TotalChestOverflow, plan.ChestOverflows.Count);
    }

    // BepInEx ConfigurationManager isn't a hard dependency. Use reflection to flip its
    // DisplayingWindow property when CM is present.
    private const string CmGuid = "com.bepis.bepinex.configurationmanager";
    private static object _cmInstance;
    private static PropertyInfo _cmDisplayingWindow;

    internal static void HideConfigurationManagerWindow()
    {
        EnsureCmCached();
        if (_cmInstance == null || _cmDisplayingWindow == null) return;
        try
        {
            _cmDisplayingWindow.SetValue(_cmInstance, false);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[CM] Could not hide window: {ex.Message}");
        }
    }

    private static void EnsureCmCached()
    {
        if (_cmDisplayingWindow != null) return;
        if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(CmGuid, out var info)) return;
        if (info?.Instance == null) return;
        _cmInstance = info.Instance;
        _cmDisplayingWindow = info.Instance.GetType()
            .GetProperty("DisplayingWindow", BindingFlags.Instance | BindingFlags.Public);
    }

    // Meditation spot near the graveyard gate, used as the overflow dump in CollectToInventory mode.
    private static readonly Vector3 MeditationSpotPosition = new(3708.6f, 214.6f, 0.0f);

    // Outdoor drop spot next to the Keeper's house, captured in-game via the tag-scan tool.
    private static readonly Vector3 NearHouseDropSpot = new(4601.0f, 290.6f, 0.0f);

    // Skip quest items, undroppable items, and anything with a run_script_after_drop that
    // needs to fire at the original spot.
    private static bool IsProtectedDrop(DropResGameObject drop)
    {
        var def = drop?.res?.definition;
        if (def == null) return false;
        if (def.player_cant_throw_out) return true;
        if (!string.IsNullOrEmpty(def.run_script_after_drop)) return true;
        return false;
    }

    private static void CollectDrops()
    {
        var watch = Stopwatch.StartNew();

        WorldMap.RescanDropItemsList();

        var drops = MainGame.me.world_root.GetComponentsInChildren<DropResGameObject>(true);

        switch (Plugin.DropHandlingOnGameLoad.Value)
        {
            case DropHandlingMode.MoveNearKeepersHouse:
                MoveDropsNearKeepersHouse(drops);
                break;
            case DropHandlingMode.CollectToInventory:
            default:
                CollectDropsToInventory(drops);
                break;
        }

        // Copy first because tp.Collect() removes itself from TechPointDrop._all and would
        // throw "Collection was modified" during enumeration.
        foreach (var tp in TechPointDrop.all.ToArray())
        {
            tp.Collect();
            if (Plugin.DebugEnabled) Log($"Attempting to collect TP: {tp.type}");
        }

        Fields.DropsCleaned = true;

        watch.Stop();

        if (Plugin.DebugEnabled)
        {
            Plugin.Log.LogInfo($"CollectDrops Complete! Completed in {watch.ElapsedMilliseconds}ms");
        }
        Fields.InventoriesLoaded = false; // force a refresh just in case
    }

    private static void CollectDropsToInventory(DropResGameObject[] drops)
    {
        var player = MainGame.me.player;
        var cantCollectList = new List<DropResGameObject>();

        foreach (var drop in drops)
        {
            if (drop.res.definition is {item_size: > 1}) continue;

            if (drop.is_collected)
            {
                if (Plugin.DebugEnabled) Log($"Drop already collected: {drop.res.id}");
                continue;
            }

            if (IsProtectedDrop(drop))
            {
                if (Plugin.DebugEnabled) Log($"Skipping protected drop: {drop.res.id} (quest/scripted item)");
                continue;
            }

            var amount = player.CanCollectDrop(drop);
            if (amount == 0)
            {
                drop.UnsuccessfullPickup(player);
                cantCollectList.Add(drop);
                if (Plugin.DebugEnabled) Log($"Can't collect: {drop.res.id} from '{drop.zone_id}'. Not enough space.");
                continue;
            }

            if (drop.res.value == amount)
            {
                drop.CollectDrop(player);
                if (Plugin.DebugEnabled) Log($"Collected: {drop.res.id} from '{drop.zone_id}', Qty: {amount}");
                continue;
            }

            drop.res.value -= amount;
            drop.RedrawStackCounter();
            player.data.AddItem(drop.res.id, amount);
            cantCollectList.Add(drop);
            if (Plugin.DebugEnabled) Log($"Partially Collected: {drop.res.id} from '{drop.zone_id}', Qty: {amount}");
        }

        foreach (var drop in cantCollectList)
        {
            drop.transform.position = MeditationSpotPosition;
            drop.DoTryMerging();
            drop.UpdateMe();
            if (Plugin.DebugEnabled) Log($"Moving '{drop.res.id}' from '{drop.zone_id}' next to the meditation spot.");
        }
    }

    private static void MoveDropsNearKeepersHouse(DropResGameObject[] drops)
    {
        // Skip drops already sitting inside the dump zone to avoid re-scattering items the
        // mod already relocated on a previous load.
        var dumpZoneRadiusWorld = Plugin.NearHouseDumpZoneRadius.Value * 96f;
        var dumpZoneRadiusSq = dumpZoneRadiusWorld * dumpZoneRadiusWorld;

        foreach (var drop in drops)
        {
            if (drop.is_collected)
            {
                if (Plugin.DebugEnabled) Log($"Drop already collected: {drop.res.id} from '{drop.zone_id}'");
                continue;
            }

            if (IsProtectedDrop(drop))
            {
                if (Plugin.DebugEnabled) Log($"Skipping protected drop: {drop.res.id} (quest/scripted item)");
                continue;
            }

            var distSq = ((Vector2) drop.transform.position - (Vector2) NearHouseDropSpot).sqrMagnitude;
            if (distSq <= dumpZoneRadiusSq)
            {
                if (Plugin.DebugEnabled) Log($"Skipping '{drop.res.id}' in '{drop.zone_id}' - already in the dump zone.");
                continue;
            }

            // Small random scatter so items don't all land on the exact same pixel and z-fight.
            var scatter = UnityEngine.Random.insideUnitCircle * 1.5f;
            drop.transform.position = new Vector3(NearHouseDropSpot.x + scatter.x, NearHouseDropSpot.y + scatter.y, 0f);
            drop.DoTryMerging();
            drop.UpdateMe();
            if (Plugin.DebugEnabled) Log($"Moving '{drop.res.id}' from '{drop.zone_id}' next to the Keeper's house.");
        }
    }

    internal static void LogNearbyTags(float radiusTiles)
    {
        var player = MainGame.me?.player;
        if (player == null)
        {
            Plugin.Log.LogWarning("[TagScan] Player not available.");
            return;
        }

        var playerPos = player.pos;
        var playerPos3 = player.pos3;
        var radiusWorld = radiusTiles * 96f;
        var radiusSq = radiusWorld * radiusWorld;

        Plugin.Log.LogInfo($"[TagScan] Scanning {radiusTiles:F1} tiles around player pos={playerPos3} (grid={playerPos / 96f})");

        var wgos = MainGame.me.world_root.GetComponentsInChildren<WorldGameObject>(true);
        var hits = new List<(float dist, WorldGameObject wgo)>();
        foreach (var wgo in wgos)
        {
            if (wgo == null) continue;
            var distSq = (wgo.pos - playerPos).sqrMagnitude;
            if (distSq > radiusSq) continue;
            hits.Add((Mathf.Sqrt(distSq), wgo));
        }

        hits.Sort((a, b) => a.dist.CompareTo(b.dist));

        var tagged = 0;
        foreach (var (dist, wgo) in hits)
        {
            var tag = string.IsNullOrEmpty(wgo.custom_tag) ? "-" : wgo.custom_tag;
            if (!string.IsNullOrEmpty(wgo.custom_tag)) tagged++;
            Plugin.Log.LogInfo($"[TagScan]   {dist / 96f,5:F2}t  obj_id={wgo.obj_id,-40}  tag={tag,-30}  pos={wgo.transform.position}");
        }

        Plugin.Log.LogInfo($"[TagScan] Done. {hits.Count} WGO(s) in range, {tagged} with custom_tag.");
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

    internal static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
        if (dictionary.ContainsKey(key)) return false;
        dictionary.Add(key, value);
        return true;
    }

    internal static void ResetInteractionFlags()
    {
        if (Plugin.DebugEnabled) Log("[ResetFlags] clearing all interaction flags and CurrentWgoInteraction");
        Fields.CurrentWgoInteraction = null;
        Fields.IsVendor = false;
        Fields.IsCraft = false;
        Fields.IsChest = false;
        Fields.IsBarman = false;
        Fields.IsTavernCellarRack = false;
        Fields.IsWritersTable = false;
        Fields.IsSoulBox = false;
        Fields.IsChurchPulpit = false;
    }
}
