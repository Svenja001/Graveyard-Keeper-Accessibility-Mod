namespace RestInPatches.Patches;

// Cache the ChunkedGameObject lookup the original makes every frame, and only touch the
// active_now_because_of_events flag when its value actually flips.
[Harmony]
public static class WorldGameObjectEventPatches
{
    private static readonly ConditionalWeakTable<WorldGameObject, ChunkedGameObjectRef> ChunkedRefs = new();

    private sealed class ChunkedGameObjectRef
    {
        public ChunkedGameObject Value;
        public bool Resolved;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.UpdateDelayedEvents))]
    public static bool WorldGameObject_UpdateDelayedEvents_Prefix(WorldGameObject __instance, float delta_time)
    {
        var events = __instance._events;
        if (events == null)
        {
            return false;
        }

        var ids = events.event_ids;
        var delays = events.event_delays;

        for (var i = 0; i < ids.Count; i++)
        {
            delays[i] -= delta_time;
            if (delays[i] > 0f)
            {
                continue;
            }

            __instance.FireEvent(ids[i]);
            ids.RemoveAt(i);
            delays.RemoveAt(i);
            --i;
        }

        if (ids.Count != 0)
        {
            return false;
        }

        var cached = ChunkedRefs.GetValue(__instance, _ => new ChunkedGameObjectRef());
        if (!cached.Resolved)
        {
            cached.Value = __instance.GetComponent<ChunkedGameObject>();
            cached.Resolved = true;
        }

        var chunked = cached.Value;
        if (chunked != null && chunked.active_now_because_of_events)
        {
            chunked.active_now_because_of_events = false;
        }

        return false;
    }
}
