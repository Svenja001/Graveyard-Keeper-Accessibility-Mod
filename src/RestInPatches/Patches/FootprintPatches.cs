namespace RestInPatches.Patches;

[Harmony]
public static class FootprintPatches
{
    // Set when the player moves the Max Footprints slider, so a lower cap applies even if
    // they never take another step.
    internal static bool TrimPending { get; set; }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LeaveTrailComponent), nameof(LeaveTrailComponent.LeaveTrail))]
    public static void LeaveTrailComponent_LeaveTrail()
    {
        TrimExcessFootprints();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    public static void MainGame_Update()
    {
        if (!TrimPending) return;

        TrimPending = false;
        TrimExcessFootprints();
    }

    // Retires oldest-first until the list is back under the cap. One removal per footstep
    // only ever cancelled out the print that step just added, so the count never came down.
    private static void TrimExcessFootprints()
    {
        var max = Plugin.MaxFootprints.Value;
        if (max <= 0) return;

        var trails = LeaveTrailComponent._all_trails;
        if (trails == null) return;

        while (trails.Count > max)
        {
            var oldest = trails[0];
            trails.RemoveAt(0);
            if (oldest == null) continue;

            Retire(oldest);
        }
    }

    // Stops the game's own fade on a print we're removing, then fades it out and destroys
    // it. Anything already off camera goes straight away.
    private static void Retire(TrailObject trail)
    {
        trail._degrading = false;

        var spr = trail._spr;
        if (spr == null)
        {
            UnityEngine.Object.Destroy(trail.gameObject);
            return;
        }

        if (trail.gameObject.GetComponent<FadeOutAndDestroy>() != null) return;

        if (!spr.isVisible)
        {
            UnityEngine.Object.Destroy(trail.gameObject);
            return;
        }

        trail.gameObject.AddComponent<FadeOutAndDestroy>();
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(TrailObject), nameof(TrailObject.Update))]
    public static IEnumerable<CodeInstruction> TrailObject_Update(IEnumerable<CodeInstruction> codes)
    {
        var original = codes.ToList();

        var colorGetter = AccessTools.PropertyGetter(typeof(SpriteRenderer), nameof(SpriteRenderer.color));
        var setAlphaMethod = AccessTools.Method(typeof(ExtentionTools), nameof(ExtentionTools.SetAlpha));
        if (colorGetter == null || setAlphaMethod == null)
        {
            return LeaveAlone(original, "SpriteRenderer.color and ExtentionTools.SetAlpha");
        }

        var matcher = new CodeMatcher(original).SearchForward(i => i.Calls(colorGetter));
        if (matcher.IsInvalid) return LeaveAlone(original, "call SpriteRenderer.get_color");

        matcher.RemoveInstruction().SearchForward(i => i.Calls(setAlphaMethod));
        if (matcher.IsInvalid) return LeaveAlone(original, "call ExtentionTools.SetAlpha");

        matcher.SetInstruction(Transpilers.EmitDelegate<Action<SpriteRenderer, float>>((spr, alpha) =>
        {
            var color = spr.color;
            color.a = alpha;
            spr.color = color;
        }));

        return matcher.InstructionEnumeration();
    }

    // Another footprint mod can rewrite this method before we get to it. Hand back the
    // instructions we were given rather than a half-edited copy.
    private static IEnumerable<CodeInstruction> LeaveAlone(List<CodeInstruction> original, string expected)
    {
        Plugin.Log.LogWarning($"[Footprints] Nothing to patch in {nameof(TrailObject)}.{nameof(TrailObject.Update)}: expected {expected}. This footprint fade patch was skipped.");
        return original;
    }
}
