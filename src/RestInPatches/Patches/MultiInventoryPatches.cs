namespace RestInPatches.Patches;

// MultiInventory.GetTotalCount logs "#BAG# Found bag in multiinventory: ..."
// for every bag encountered while iterating the inventory list. Pure dev-debug
// spam. Redirect the lone Debug.Log(object) call in this method to a no-op.
[Harmony]
public static class MultiInventoryPatches
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(MultiInventory), nameof(MultiInventory.GetTotalCount), typeof(string), typeof(MultiInventory.DestinationType), typeof(bool))]
    public static IEnumerable<CodeInstruction> GetTotalCount_StripBagLog(IEnumerable<CodeInstruction> instructions)
    {
        var debugLogObj = AccessTools.Method(typeof(Debug), nameof(Debug.Log), [typeof(object)]);
        var noOp = AccessTools.Method(typeof(MultiInventoryPatches), nameof(NoOpLog));

        foreach (var instr in instructions)
        {
            if (instr.Calls(debugLogObj))
            {
                instr.operand = noOp;
            }
            yield return instr;
        }
    }

    private static void NoOpLog(object _)
    {
    }
}
