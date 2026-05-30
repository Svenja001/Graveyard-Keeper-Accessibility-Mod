namespace DeadGiveaway;

[Harmony]
public static class Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    public static void MainGame_Update()
    {
        if (!Plugin.CanToggle()) return;

        var keybind = Plugin.ToggleKeybind.Value;
        var keyDown = keybind.MainKey != KeyCode.None && keybind.IsDown();

        // Resolve the chosen button to its Rewired action id the same way GamePadController does,
        // so it follows the player's actual controller instead of assuming a Rewired action name.
        var padDown = Plugin.ToggleButton != GamePadButton.None
                      && LazyInput.gamepad_active
                      && LazyInput._gamepad != null
                      && LazyInput._gamepad._rewired_bindings.TryGetValue(Plugin.ToggleButton, out var actionId)
                      && ReInput.players.GetPlayer(0).GetButtonDown(actionId);

        if (!keyDown && !padDown) return;

        Plugin.OverlayVisible = !Plugin.OverlayVisible;

        if (Plugin.DebugEnabled)
        {
            Plugin.Log.LogInfo($"[Toggle] overlay {(Plugin.OverlayVisible ? "shown" : "hidden")} (key={keyDown} pad={padDown})");
        }
    }
}
