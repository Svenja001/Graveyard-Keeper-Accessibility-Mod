namespace ShowMeMoar;

[Harmony]
[HarmonyPriority(0)]
public static class ScreenResolutionsPatch
{
    public static Resolution[] MyResolutions()
    {
        Plugin.Log.LogInfo("Unity Screen.resolutions intercepted!");
        var newRes = new Resolution
        {
            height = Display.main.systemHeight,
            width = Display.main.systemWidth,
            refreshRate = Screen.resolutions.Max(a => a.refreshRate)
        };
        var availableResolutions = Screen.resolutions.ToList();
        availableResolutions.Add(newRes);
        return availableResolutions.ToArray();
    }
    
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(ResolutionConfig), nameof(ResolutionConfig.InitResolutions))]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        if (!Plugin.Ultrawide.Value)
        {
            Plugin.Log.LogWarning("Ultra-wide resolutions are disabled!");
            return instructions.AsEnumerable();
        }
        var originalInstructions = instructions.ToList();
        var getResolutionsMethod = AccessTools.Property(typeof(Screen), nameof(Screen.resolutions))?.GetGetMethod();
        var myResolutionsMethod = AccessTools.Method(typeof(ScreenResolutionsPatch), nameof(MyResolutions));

        foreach (var t in originalInstructions.Where(t => t.Calls(getResolutionsMethod)))
        {
            t.operand = myResolutionsMethod;
        }

        Plugin.Log.LogInfo("Ultra-wide resolutions are enabled!.");
        return originalInstructions.AsEnumerable();
    }
}