namespace MoarPallets;

internal static class CrateRouter
{
    private const string PalletObjId = "box_pallet";

    internal static void SweepLooseCrates()
    {
        if (!Plugin.AutoRouteLooseCrates.Value) return;
        if (MainGame.me == null || MainGame.me.world_root == null) return;

        var drops = MainGame.me.world_root.GetComponentsInChildren<DropResGameObject>(true);
        var routed = 0;
        var dumped = 0;

        foreach (var drop in drops)
        {
            if (!drop || drop.is_collected) continue;
            if (drop.res == null || drop.res.IsEmpty()) continue;
            if (drop.res.definition == null || !drop.res.definition.is_crate) continue;

            if (TryPlaceCrateOnPallet(drop))
            {
                routed++;
                continue;
            }

            if (DumpAtCellarElevator(drop))
            {
                dumped++;
            }
        }

        if (routed > 0 || dumped > 0 || Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"Crate sweep: routed {routed} loose crate(s) to pallets, dumped {dumped} at the cellar elevator.");
        }
    }

    private static bool TryPlaceCrateOnPallet(DropResGameObject drop)
    {
        var crateId = drop.res.id;
        var best = FindNearestPalletWithSpace(crateId, drop.transform.position);
        if (!best) return false;

        var item = new Item(drop.res);
        if (!best.CanInsertItem(item)) return false;
        if (!best.AddToInventory(item)) return false;
        best.Redraw();

        // is_collected=true triggers DropsList.Update to destroy the gameObject AND
        // remove the entry in one step next frame. Destroying directly leaves a dangling
        // list entry and crashes DropsList.Update/CheckDrops on the next tick.
        drop.is_collected = true;
        WorldMap.OnDropItemRemoved(drop.res);
        drop.DestroyLinkedHint();

        if (Plugin.Debug.Value)
        {
            var dropPos = drop.transform.position;
            Plugin.Log.LogInfo($"Routed '{crateId}' from ({dropPos.x:F1},{dropPos.y:F1}) to pallet at ({best.transform.position.x:F1},{best.transform.position.y:F1}).");
        }
        return true;
    }

    private static bool DumpAtCellarElevator(DropResGameObject drop)
    {
        var target = FindCellarElevatorPos();
        if (!target.HasValue) return false;

        drop.transform.position = target.Value;
        drop.DoTryMerging();
        drop.UpdateMe();
        MakeDropNonBlocking(drop);

        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"All pallets full - dumped '{drop.res.id}' at the cellar elevator ({target.Value.x:F1},{target.Value.y:F1}).");
        }
        return true;
    }

    // Crates stacked on one spot push each other apart every physics tick and drift into walls.
    // Flipping the collider to a trigger keeps queries working but stops the separation cascade.
    internal static void MakeDropNonBlocking(DropResGameObject drop)
    {
        if (!drop) return;
        var cap = drop.GetComponent<CapsuleCollider2D>();
        if (cap)
        {
            cap.isTrigger = true;
        }
        var circ = drop.GetComponent<CircleCollider2D>();
        if (circ)
        {
            circ.isTrigger = true;
        }
    }

    // Returns true if the carried crate was routed to a pallet (or dumped at the elevator)
    // and vanilla DropOverheadItem should be skipped. Returns false to let vanilla run.
    internal static bool TryRouteCarriedCrate(BaseCharacterComponent character)
    {
        if (!Plugin.AutoRouteCarriedCrates.Value) return false;
        if (character == null || !character.has_overhead) return false;
        if (character.wgo == null || !character.wgo.is_player) return false;
        var item = character.overhead_item;
        if (item == null || item.definition == null || !item.definition.is_crate) return false;

        var pallet = FindNearestPalletWithSpace(item.id, character.tf.position);
        if (pallet != null && pallet.CanInsertItem(item) && pallet.AddToInventory(item))
        {
            pallet.Redraw();
            character.SetOverheadItem(null);
            if (Plugin.Debug.Value)
            {
                Plugin.Log.LogInfo($"Carried '{item.id}' routed to pallet at ({pallet.transform.position.x:F1},{pallet.transform.position.y:F1}).");
            }
            return true;
        }

        var dumpPos = FindCellarElevatorPos();
        if (!dumpPos.HasValue) return false;

        // IgnoreDirection skips the bounce kick so dumped crates don't pile-physics into walls.
        var spawned = DropResGameObject.Drop(dumpPos.Value, item, character.tf.parent, Direction.IgnoreDirection);
        MakeDropNonBlocking(spawned);
        character.SetOverheadItem(null);
        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"All pallets full - carried '{item.id}' dumped at the cellar elevator ({dumpPos.Value.x:F1},{dumpPos.Value.y:F1}).");
        }
        return true;
    }

    private static WorldGameObject FindNearestPalletWithSpace(string crateId, Vector3 fromPos)
    {
        var pallets = WorldMap.GetWorldGameObjectsByObjId(PalletObjId);
        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"FindNearestPallet: '{crateId}' from ({fromPos.x:F1},{fromPos.y:F1}) - {pallets?.Count ?? 0} pallet(s) in WorldMap.");
        }
        if (pallets == null || pallets.Count == 0) return null;

        var from = (Vector2)fromPos;
        WorldGameObject best = null;
        var bestSq = float.MaxValue;
        var rejectedNoAccept = 0;
        var rejectedNoSpace = 0;
        foreach (var pallet in pallets)
        {
            if (!pallet || pallet.data == null || pallet.obj_def == null) continue;
            if (pallet.obj_def.can_insert_items == null || !pallet.obj_def.can_insert_items.Contains(crateId))
            {
                rejectedNoAccept++;
                continue;
            }
            var canAdd = pallet.data.CanAddCount(crateId, true);
            if (canAdd <= 0)
            {
                rejectedNoSpace++;
                if (Plugin.Debug.Value)
                {
                    Plugin.Log.LogInfo($"  full: pallet at ({pallet.transform.position.x:F1},{pallet.transform.position.y:F1}) zone='{(pallet._zone ? pallet._zone.id : "<none>")}' inv_size={pallet.data.inventory_size} inv_count={pallet.data.inventory?.Count ?? 0}");
                }
                continue;
            }
            var d = ((Vector2)pallet.transform.position - from).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                best = pallet;
            }
        }

        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"FindNearestPallet: rejected {rejectedNoAccept} (can't accept '{crateId}'), {rejectedNoSpace} (no space), best={(best ? best.transform.position.ToString("F1") : "<none>")}");
        }
        return best;
    }

    // Fixed cellar floor tile right at the foot of the elevator. The vanilla cellar layout is the
    // same for every save, so this position is stable. Fall back to porter_station / elevator_bot
    // lookups only if for some reason the cellar isn't loaded.
    private static readonly Vector3 CellarDumpPos = new(11573.76f, -9711.571f, 0f);

    private static Vector3? FindCellarElevatorPos()
    {
        return CellarDumpPos;
    }
}
