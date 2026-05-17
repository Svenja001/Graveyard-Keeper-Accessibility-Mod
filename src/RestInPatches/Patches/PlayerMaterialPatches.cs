namespace RestInPatches.Patches;

// Swap the per-frame string-keyed SetColor on the player material for a cached int ID,
// and skip the write entirely when the colour is unchanged.
[Harmony]
public static class PlayerMaterialPatches
{
    private static readonly int AdditionalColourId = Shader.PropertyToID("_AdditionalColour");

    private static readonly ConditionalWeakTable<Material, ColorBox> LastColors = new();

    private sealed class ColorBox
    {
        public Color Value;
        public bool Set;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(PlayerComponent), nameof(PlayerComponent.Update))]
    public static IEnumerable<CodeInstruction> PlayerComponent_Update_Transpiler(IEnumerable<CodeInstruction> codes)
    {
        var setColorStr = AccessTools.Method(typeof(Material), nameof(Material.SetColor), new[] { typeof(string), typeof(Color) });
        var replacement = AccessTools.Method(typeof(PlayerMaterialPatches), nameof(SetAdditionalColourCached));

        foreach (var code in codes)
        {
            if (code.Calls(setColorStr))
            {
                yield return new CodeInstruction(OpCodes.Call, replacement);
            }
            else
            {
                yield return code;
            }
        }
    }

    private static void SetAdditionalColourCached(Material material, string _, Color color)
    {
        if (material == null)
        {
            return;
        }

        var box = LastColors.GetOrCreateValue(material);
        if (box.Set && box.Value == color)
        {
            return;
        }

        box.Value = color;
        box.Set = true;
        material.SetColor(AdditionalColourId, color);
    }
}
