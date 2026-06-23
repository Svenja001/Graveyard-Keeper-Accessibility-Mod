namespace WheresMaStorage;

public static class Invents
{
    internal static IEnumerator LoadInventories(Action callback = null)
    {
        if (Plugin.DebugEnabled) Helpers.Log("[LoadInventories] start");

        Fields.Mi = new MultiInventory();

        Fields.InventoryPositions.Clear();
        foreach (var wgo in WorldMap._objs)
        {
            if (wgo == null || wgo.data == null) continue;
            Fields.InventoryPositions[wgo.data] = wgo.pos3;
        }
        if (Plugin.DebugEnabled) Helpers.Log($"[LoadInventories] snapshot positions={Fields.InventoryPositions.Count} from WorldMap._objs={WorldMap._objs.Count}");

        if (Plugin.DebugEnabled)
        {
            var wellSb = new StringBuilder("[LoadInventories] all WGOs with 'well' in obj_id or obj_def.id: ");
            var wellsFound = 0;
            foreach (var wgo in WorldMap._objs)
            {
                if (wgo == null || wgo.obj_def == null) continue;
                var oid = wgo.obj_id ?? string.Empty;
                var did = wgo.obj_def.id ?? string.Empty;
                if (!oid.Contains("well") && !did.Contains("well")) continue;
                var zid = wgo.GetMyWorldZoneId();
                var invSize = wgo.obj_def.inventory_size;
                var openInMi = wgo.obj_def.open_in_multiinventory;
                var waterCount = wgo.data?.GetTotalCount("water", true) ?? 0;
                wellSb.Append($"{oid}(def={did}, zone='{zid}', invSize={invSize}, openInMi={openInMi}, water={waterCount}) | ");
                wellsFound++;
            }
            if (wellsFound == 0) wellSb.Append("(none)");
            Helpers.Log(wellSb.ToString());
        }

        var playerInventory = new Inventory(MainGame.me.player);
        Fields.Mi.AddInventory(playerInventory, 0);
        Helpers.ApplyPlayerInventorySize();

        var toolbelt = new Item
        {
            id = "Toolbelt",
            inventory = MainGame.me.player.data.secondary_inventory,
            inventory_size = 7
        };
        Fields.Mi.AddInventory(new Inventory(toolbelt));

        var zones = WorldZone._all_zones;
        var watch = Stopwatch.StartNew();

        var zonesVisited = 0;
        var zonesSkipped = 0;
        var invsAdded = 0;

        foreach (var zone in zones)
        {
            // BUG 1 FIX: argument order was reversed so the substring check never matched.
            if (Fields.AlwaysSkipZones.Any(a => zone.id.ToLowerInvariant().Contains(a)))
            {
                zonesSkipped++;
                continue;
            }

            //if it's in a zone we haven't seen, skip
            if (!MainGame.me.save.known_world_zones.Exists(a => string.Equals(a, zone.id)))
            {
                zonesSkipped++;
                continue;
            }

            var worldZoneMulti = zone.GetMultiInventory(player_mi: MultiInventory.PlayerMultiInventory.ExcludePlayer, sortWGOS: true);
            if (worldZoneMulti == null)
            {
                zonesSkipped++;
                continue;
            }

            zonesVisited++;
            foreach (var inv in worldZoneMulti.Where(inv => !Fields.AlwaysSkipZones.Any(inv._obj_id.ToLowerInvariant().Contains)))
            {
                inv.data.sub_name = string.IsNullOrEmpty(inv._obj_id) ? $"Unknown#{zone.id}" : $"{inv._obj_id}#{zone.id}";

                Fields.Mi.AddInventory(inv);
                invsAdded++;
            }
        }

        //adds bags to the inventory
        var bagsAdded = 0;
        for (var i = 0; i < Fields.Mi.all.Count; i++)
        {
            var inventoriesToAdd = Fields.Mi.all[i].data.inventory
                .Where(data => data != null && !data.IsEmpty() && data.is_bag)
                .Select(data => new Inventory(data, data.id))
                .ToList();

            foreach (var inv in inventoriesToAdd)
            {
                Fields.Mi.AddInventory(inv, i + 1);
                bagsAdded++;
            }
        }

        watch.Stop();
        Fields.InventoriesLoaded = true; // setter also clears Fields.LoadInventoriesCoroutine
        if (Plugin.DebugEnabled) Helpers.Log($"[LoadInventories] done: zones visited={zonesVisited} skipped={zonesSkipped}, inventories={invsAdded} (+{bagsAdded} bags), total Mi={Fields.Mi.all.Count} in {watch.ElapsedMilliseconds}ms");
        callback?.Invoke();
        yield return true;
    }


    internal static IEnumerator LoadWildernessInventories(Action callback = null)
    {
        if (Plugin.DebugEnabled) Helpers.Log("[LoadWilderness] start");

        var wgos = WorldMap._objs
            .Where(a => a.data.inventory_size > 0 && string.IsNullOrEmpty(a.GetMyWorldZoneId()))
            .ToList();

        var watch = Stopwatch.StartNew();
        Fields.WildernessMultiInventories.Clear();
        Fields.WildernessInventories.Clear();

        var excludedInventories = Fields.ExcludeTheseWildernessInventories;
        // Wilderness inventories are containers (chests/racks), so they use the container slider.
        var additionalInventorySpace = Plugin.AdditionalContainerInventorySpace.Value;
        var modifyInventorySize = Plugin.ModifyInventorySize.Value;

        foreach (var wgo in wgos)
        {
            if (wgo.obj_def.inventory_size <= 0 || excludedInventories.Any(wgo.obj_id.Contains)) continue;

            if (modifyInventorySize)
            {
                var size = Helpers.OriginalInventorySizes.TryGetValue(wgo.obj_id, out var originalSize)
                    ? originalSize
                    : wgo.obj_def.inventory_size;

                size += additionalInventorySpace;

                if (wgo.obj_def.inventory_size == size)
                {
                    continue;
                }
            }

            var zoneId = wgo.GetMyWorldZoneId();
            wgo.data.sub_name = $"{wgo.obj_id}#{zoneId}";

            if (!string.IsNullOrEmpty(zoneId) || wgo.unique_id.ToString().Length <= 5) continue;

            if (wgo.custom_tag.Equals(Fields.ShippingBoxTag) || wgo.data.drop_zone_id.Equals(Fields.ShippingBoxTag)) continue;

            if (!Fields.WildernessMultiInventories.ContainsKey(wgo))
            {
                Fields.WildernessMultiInventories[wgo] = wgo.GetMultiInventoryOfWGOWithoutWorldZone();
            }

            var multiInventory = Fields.WildernessMultiInventories[wgo];

            foreach (var inv in multiInventory.all.Where(inv => !Fields.WildernessInventories.Contains(inv)))
            {
                Fields.WildernessInventories.Add(inv);
            }
        }

        watch.Stop();
        if (Plugin.DebugEnabled) Helpers.Log($"[LoadWilderness] done: wgos scanned={wgos.Count}, inventories={Fields.WildernessInventories.Count} (multi={Fields.WildernessMultiInventories.Count}) in {watch.ElapsedMilliseconds}ms");
        callback?.Invoke();
        yield break;
    }


    internal static void SetInventorySizeText(BaseInventoryWidget inventoryWidget)
    {
        if (inventoryWidget.inventory_data.id.Contains(Fields.Writer)) return;
        if (inventoryWidget.header_label.text.Contains(Fields.Gerry)) return;
        if (!Plugin.ShowWorldZoneInTitles.Value && !Plugin.ShowUsedSpaceInTitles.Value) return;

        string objId;
        bool isPlayer;
        var subNameSplit = inventoryWidget.inventory_data.sub_name.Split('#');

        if (string.IsNullOrEmpty(subNameSplit[0]))
        {
            objId = Lang.Get("Player");
            isPlayer = true;
        }
        else
        {
            objId = GJL.L(subNameSplit[0].ToLowerInvariant().Trim() + "_inventory");
            isPlayer = false;
        }

        var zoneId = subNameSplit.Length > 1 ? subNameSplit[1].ToLowerInvariant().Trim() : string.Empty;
        var wzLabel = GetWorldZoneLabel(zoneId);

        var cultureInfo = CultureInfo.GetCultureInfo(
            GameSettings.me.language.Replace('_', '-').ToLower(CultureInfo.InvariantCulture).Trim());
        var textInfo = cultureInfo.TextInfo;
        wzLabel = textInfo.ToTitleCase(wzLabel);

        // BUG 3 FIX: read size and count directly instead of building a deep copy.
        var cap = inventoryWidget.inventory_data.inventory_size;
        var used = inventoryWidget.inventory_data.inventory.Count;

        inventoryWidget.header_label.overflowMethod = UILabel.Overflow.ResizeFreely;

        var header = inventoryWidget.inventory_data.is_bag ? GJL.L(inventoryWidget.inventory_data.id) : objId;

        // Fallback to inventory id if translation failed
        if (header.Contains("_"))
        {
            header = GJL.L(inventoryWidget.inventory_data.id);
        }

        var sb = new StringBuilder(header);

        if (Plugin.ShowWorldZoneInTitles.Value && !isPlayer)
        {
            sb.Append($" ({wzLabel})");
        }

        if (Plugin.ShowUsedSpaceInTitles.Value)
        {
            sb.Append($" - {used}/{cap}");
        }

        inventoryWidget.header_label.text = sb.ToString();

        string GetWorldZoneLabel(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return Lang.Get("Wilderness");
            var wzId = WorldZone.GetZoneByID(zone, false);
            return wzId != null ? GJL.L("zone_" + wzId.id) : Lang.Get("Wilderness");
        }
    }

    internal static MultiInventory GetMiInventory(string requester, string zone, Vector3 crafterPos)
    {
        var requesterInQuarry = zone.Contains("stone_workyard") || zone.Contains("marble_deposit");
        var requesterInZombieMill = zone.Contains("zombie_sawmill");

        if (requester.Contains("refugee_builddesk") || requester.Contains("storage_builddesk") || requesterInQuarry)
        {
            if (!Fields.InventoriesLoaded && Fields.LoadInventoriesCoroutine == null)
            {
                if (Plugin.DebugEnabled) Helpers.Log($"[GetMiInventory] triggering reload (requester={requester} zone={zone})");
                Fields.LoadInventoriesCoroutine = MainGame.me.StartCoroutine(LoadInventories());
                MainGame.me.StartCoroutine(LoadWildernessInventories());
            }
        }

        var wildAdded = 0;
        foreach (var inv in Fields.WildernessInventories.Where(inv => !Fields.Mi.all.Contains(inv)))
        {
            Fields.Mi.AddInventory(inv);
            wildAdded++;
        }

        // Filter into a per-requester view. Don't mutate Fields.Mi or quarry/zombie-mill
        // entries will be dropped from the shared cache until the next full rebuild.
        var view = new MultiInventory();
        var quarryFiltered = 0;
        var zombieMillFiltered = 0;
        var excludeQuarry = Plugin.ExcludeQuarryFromSharedInventory.Value && !requesterInQuarry;
        var excludeZombieMill = Plugin.ExcludeZombieMillFromSharedInventory.Value && !requesterInZombieMill;
        foreach (var inv in Fields.Mi.all)
        {
            var subName = inv.data.sub_name ?? string.Empty;
            if (excludeQuarry && (subName.Contains("stone_workyard") || subName.Contains("marble_deposit")))
            {
                quarryFiltered++;
                continue;
            }
            if (excludeZombieMill && subName.Contains("zombie_sawmill"))
            {
                zombieMillFiltered++;
                continue;
            }
            view.AddInventory(inv);
        }

        var sortedGroups = 0;
        if (Plugin.SortByDistanceFromCrafter.Value && view.all.Count > 2)
        {
            sortedGroups = SortByDistance(view, crafterPos);
        }

        if (Plugin.DebugEnabled) Helpers.Log($"[GetMiInventory] returning view (size={view.all.Count} from cache={Fields.Mi.all.Count}): +{wildAdded} wilderness, -{quarryFiltered} quarry, -{zombieMillFiltered} zombie-mill, sortedGroups={sortedGroups} (requester={requester} zone={zone})");
        return view;
    }

    // Sort the view's tail by distance from the crafter, keeping each container's bags next
    // to their parent. Player and toolbelt stay locked at indices 0/1.
    private static int SortByDistance(MultiInventory view, Vector3 crafterPos)
    {
        var all = view.all;
        var groups = new List<(Inventory parent, List<Inventory> bags, float distSq)>();

        for (var i = 2; i < all.Count; i++)
        {
            var inv = all[i];
            if (inv.data.is_bag && groups.Count > 0)
            {
                groups[groups.Count - 1].bags.Add(inv);
                continue;
            }

            var distSq = Fields.InventoryPositions.TryGetValue(inv.data, out var pos)
                ? (pos - crafterPos).sqrMagnitude
                : float.MaxValue;
            groups.Add((inv, new List<Inventory>(), distSq));
        }

        var sorted = groups.OrderBy(g => g.distSq).ToList();

        var rebuilt = new List<Inventory>(all.Count) { all[0], all[1] };
        foreach (var g in sorted)
        {
            rebuilt.Add(g.parent);
            rebuilt.AddRange(g.bags);
        }
        view.SetInventories(rebuilt);
        return sorted.Count;
    }

    public static MultiInventory GetMi(CraftDefinition craft, MultiInventory orig, WorldGameObject otherGameObject)
    {
        if (!Plugin.SharedInventory.Value)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[GetMi] SharedInventory disabled, returning orig for craft={craft?.id} obj={otherGameObject?.obj_id}");
            return orig;
        }

        var objId = otherGameObject.obj_id;
        var worldZoneId = otherGameObject.GetMyWorldZoneId();
        var isPlayer = otherGameObject.is_player;
        var hasLinkedWorker = otherGameObject.has_linked_worker;
        var linkedWorkerObjId = hasLinkedWorker ? otherGameObject.linked_worker.obj_id : string.Empty;

        var isQuarry = worldZoneId.Contains("stone_workyard") || worldZoneId.Contains("marble_deposit");
        var isWell = objId.Contains("well");
        var isZombieMill = worldZoneId.Contains("zombie_sawmill");

        var isZombie = objId.Contains("zombie") || linkedWorkerObjId.Contains("zombie");
        Fields.ZombieWorker = isZombie;

        var isBuilder = otherGameObject.obj_def.interaction_type == ObjectDefinition.InteractionType.Builder;

        // Let vanilla's force_world_zone-restricted inventory through for isolated zones
        // like refugees_camp. The builder bypass only forgives obj_id collisions (e.g. apiary).
        var objMatchesSkip = Fields.AlwaysSkipZones.Any(a => objId.Contains(a));
        var zoneMatchesSkip = Fields.AlwaysSkipZones.Any(a => worldZoneId.Contains(a));
        if (zoneMatchesSkip || (!isBuilder && objMatchesSkip))
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[GetMi] skip (AlwaysSkipZones match: zone={zoneMatchesSkip} obj={objMatchesSkip} isBuilder={isBuilder}) obj={objId} zone={worldZoneId}");
            return orig;
        }

        if (Plugin.ExcludeWellsFromSharedInventory.Value && isWell)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[GetMi] skip (well exclusion) obj={objId}");
            return orig;
        }

        if (Plugin.ExcludeZombieMillFromSharedInventory.Value && isZombieMill)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[GetMi] skip (zombie mill exclusion) obj={objId} zone={worldZoneId}");
            return orig;
        }

        if (!Plugin.AllowZombiesAccessToSharedInventory.Value && isZombie)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[GetMi] skip (zombie, shared disallowed) obj={objId} linkedWorker={linkedWorkerObjId}");
            return orig;
        }

        if (objId.Contains("storage_builddesk") && !Fields.InventoriesLoaded && Fields.LoadInventoriesCoroutine == null)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[GetMi] storage_builddesk interaction → triggering LoadInventories");
            Fields.LoadInventoriesCoroutine = MainGame.me.StartCoroutine(LoadInventories());
        }

        var isSpecialObject = isZombie || objId.StartsWith("mf_") || Fields.GratitudeCraft;

        if (isPlayer && craft.id.Length > 0 || isSpecialObject)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[GetMi] injecting shared multi (isPlayer={isPlayer},special={isSpecialObject},gratitude={Fields.GratitudeCraft}) craft={craft.id} obj={objId} zone={worldZoneId}");
            return GetMiInventory($"{objId}", otherGameObject.GetMyWorldZoneId(), otherGameObject.pos3);
        }

        if (Plugin.DebugEnabled) Helpers.Log($"[GetMi] no-match, returning orig craft={craft.id} obj={objId} zone={worldZoneId}");
        return orig;
    }

}
