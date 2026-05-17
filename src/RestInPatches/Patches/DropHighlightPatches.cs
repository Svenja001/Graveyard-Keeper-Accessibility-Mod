namespace RestInPatches.Patches;

// Skip re-highlighting every drop on the ground when the nearest target hasn't changed
// since last tick. With lots of accumulated drops the original pass gets expensive.
[Harmony]
public static class DropHighlightPatches
{
    private static readonly ConditionalWeakTable<DropsList, Box> LastTargets = new();

    private sealed class Box
    {
        public DropResGameObject Target;
        public bool Set;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DropsList), nameof(DropsList.SetHighlighted))]
    public static bool DropsList_SetHighlighted_Prefix(DropsList __instance, DropResGameObject drop)
    {
        var box = LastTargets.GetOrCreateValue(__instance);

        // ReferenceEquals avoids Unity's destroyed-object-equals-null quirk that would
        // make us skip un-highlighting a stale drop.
        if (box.Set && ReferenceEquals(box.Target, drop))
        {
            return false;
        }

        box.Set = true;
        box.Target = drop;
        return true;
    }
}
