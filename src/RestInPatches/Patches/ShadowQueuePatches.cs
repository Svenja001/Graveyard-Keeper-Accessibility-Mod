namespace RestInPatches.Patches;

// Drop the LINQ First() call the original uses to peek the shadow-init queue, since it
// allocates four times a frame during scene loads.
[Harmony]
public static class ShadowQueuePatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ObjectDynamicShadowsManager), nameof(ObjectDynamicShadowsManager.Update))]
    public static bool ObjectDynamicShadowsManager_Update_Prefix(ObjectDynamicShadowsManager __instance)
    {
        __instance._queue_size = __instance._queue.Count;
        if (__instance._queue_size == 0)
        {
            return false;
        }

        var iterations = __instance._queue_size > 4 ? 4 : __instance._queue_size;

        for (var i = 0; i < iterations; i++)
        {
            Action key = null;
            Action done = null;
            foreach (var kv in __instance._queue)
            {
                key = kv.Key;
                done = kv.Value;
                break;
            }

            if (key == null)
            {
                break;
            }

            __instance._queue.Remove(key);
            key();
            done?.Invoke();
        }

        return false;
    }
}
