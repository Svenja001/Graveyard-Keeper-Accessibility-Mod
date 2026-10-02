namespace RestInPatches.Patches;

[Harmony]
public static class GamepadPatches
{
    // The D-pad also drives the left stick's axes, and they take a few milliseconds to fall
    // back to zero after you let go.
    private const float StickIgnoreSeconds = 0.05f;

    private static float _dpadLastHeld = float.MinValue;

    // The game ignores the stick while a D-pad button is held, but not on the frames just after
    // release. At high frame rates the stick is still falling then, and it counts as a second
    // press. Keep ignoring it for a moment longer.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GamePadController), nameof(GamePadController.UpdateStickNavigation))]
    public static void GamePadController_UpdateStickNavigation(GamePadController __instance, ref Vector2 gui_navigation)
    {
        var held = __instance.holded_keys;
        if (held.Contains(GameKey.Up) || held.Contains(GameKey.Down) || held.Contains(GameKey.Left) || held.Contains(GameKey.Right))
        {
            _dpadLastHeld = Time.unscaledTime;
            return;
        }

        if (Time.unscaledTime - _dpadLastHeld < StickIgnoreSeconds)
        {
            gui_navigation = Vector2.zero;
        }
    }
}
