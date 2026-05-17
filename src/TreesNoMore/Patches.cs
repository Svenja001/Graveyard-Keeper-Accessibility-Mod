namespace TreesNoMore;

[Harmony]
public static class Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.GlobalEventsCheck))]
    public static void GameSave_GlobalEventsCheck()
    {
        if (Plugin.DebugEnabled) Helpers.Log("[GlobalEventsCheck] postfix firing - destroy tracked trees");
        DestroyTrees();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InGameMenuGUI), nameof(InGameMenuGUI.ReturnToMainMenu))]
    public static void InGameMenuGUI_ReturnToMainMenu()
    {
        if (Plugin.DebugEnabled) Helpers.Log("[ReturnToMainMenu] saving tracked trees before exit to main menu");
        Plugin.SaveTrees();
    }

    internal static void DestroyTrees()
    {
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();

        if (!Plugin.LoadTrees())
        {
            if (Plugin.DebugEnabled) Helpers.Log("[DestroyTrees] LoadTrees returned false - nothing to destroy this load");
            return;
        }

        if (Plugin.DebugEnabled) Helpers.Log($"[DestroyTrees] starting; {Plugin.Trees.Count} tracked tree(s) on record, search distance {Plugin.TreeSearchDistance.Value}");

        List<WorldGameObject> treesToDestroy = [];
        var scannedCount = 0;

        foreach (var tree in WorldMap.objs.Where(o => o.name.Contains("tree") && !o.name.Contains("bees") && !o.name.Contains("apple")))
        {
            scannedCount++;
            var treeExists = Plugin.Trees.Any(x => Vector3.Distance(x.location, tree.pos3) <= Plugin.TreeSearchDistance.Value);
            if (treeExists)
            {
                if (Plugin.DebugEnabled) Helpers.Log($"[DestroyTrees] match - world tree {tree.obj_id} at {tree.pos3} is in tracked list, queued for removal");
                treesToDestroy.Add(tree);
            }
        }

        foreach (var tree in treesToDestroy)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[DestroyTrees] removing world object {tree.obj_id} at {tree.pos3}");
            WorldMap.objs.Remove(tree);
            UnityEngine.Object.DestroyImmediate(tree);
        }

        sw.Stop();
        if (Plugin.DebugEnabled) Helpers.Log($"[DestroyTrees] removed {treesToDestroy.Count} of {scannedCount} scanned world tree(s) (tracked total {Plugin.Trees.Count}) in {sw.ElapsedMilliseconds}ms");
        WorldMap.RescanWGOsList();
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.SmartInstantiate))]
    public static bool WorldGameObject_SmartInstantiate(WorldGameObject __instance, WorldObjectPart prefab, ref WorldObjectPart __result)
    {
        if (prefab == null) return true;

        var prefabName = prefab.name;
        var instancePos = __instance.pos3;

        bool suppress;
        if (prefabName.Contains("stump"))
        {
            suppress = HandleStump(__instance, instancePos, prefab);
        }
        else if (IsValidTree(prefabName))
        {
            suppress = HandleTree(__instance, instancePos, prefab);
        }
        else
        {
            return true;
        }

        if (!suppress) return true;

        __result = null;
        return false;
    }

    private static bool HandleStump(WorldGameObject instance, Vector3 instancePos, WorldObjectPart prefab)
    {
        if (Plugin.DebugEnabled) Helpers.Log($"[HandleStump] entry - instance={instance.obj_id} at {instancePos}, InstantStumpRemoval={Plugin.InstantStumpRemoval.Value}");

        // Dedup so repeated SmartInstantiate fires for the same stump don't append duplicates.
        var alreadyTracked = Plugin.Trees.Any(tree => Vector3.Distance(tree.location, instancePos) <= Plugin.TreeSearchDistance.Value);
        if (!alreadyTracked)
        {
            var tree = new Tree(instance.obj_id, instancePos);
            Plugin.Trees.Add(tree);
            Plugin.SaveTrees();

            if (Plugin.DebugEnabled) Helpers.Log($"[HandleStump] added new tracked stump for {instance.obj_id} at {instancePos}; total tracked now {Plugin.Trees.Count}");
        }
        else
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[HandleStump] {instancePos} is already in tracked list (within search distance) - skip add");
        }

        if (Plugin.InstantStumpRemoval.Value)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[HandleStump] InstantStumpRemoval=true - skipping original SmartInstantiate so the stump never spawns");
            return true;
        }

        return false;
    }

    private static bool IsValidTree(string prefabName)
    {
        return prefabName.Contains("tree") && !prefabName.Contains("bees") && !prefabName.Contains("apple");
    }

    private static bool HandleTree(WorldGameObject instance, Vector3 instancePos, WorldObjectPart prefab)
    {
        var treeExists = Plugin.Trees.Any(tree => Vector3.Distance(tree.location, instancePos) <= Plugin.TreeSearchDistance.Value);

        if (Plugin.DebugEnabled) Helpers.Log($"[HandleTree] entry - instance={instance.obj_id} at {instancePos}, treeExists={treeExists}, game_started={MainGame.game_started}");

        if (!treeExists && MainGame.game_started)
        {
            var tree = new Tree(instance.obj_id, instancePos);
            Plugin.Trees.Add(tree);
            Plugin.SaveTrees();
            if (Plugin.DebugEnabled) Helpers.Log($"[HandleTree] new tree felled - added to tracked list (total {Plugin.Trees.Count}) and skipping original SmartInstantiate so the world copy doesn't respawn this load");
            return true;
        }

        if (Plugin.DebugEnabled) Helpers.Log($"[HandleTree] no action - already tracked or game not started");
        return false;
    }

}
