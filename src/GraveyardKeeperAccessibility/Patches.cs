namespace GraveyardKeeperAccessibility;

internal static class Patches
{
    public static void SpeechBubbleGUI_ShowMessage_Prefix(string __0)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(__0)) return;

            // This receives the dialogue ID like "in_the_dark_1"
            // We need to look up the actual text using the game's localization system
            // For now, log that we got it and let SpeechText handle it
            Plugin.Log.LogInfo($"[DIALOGUE_ID] {__0}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DIALOGUE_HOOK] Error: {ex.Message}");
        }
    }

    public static void SpeechBubbleGUI_SpeechText_Postfix(string __0, ref string __result)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(__result)) return;

            var cleanedText = ScreenReader.StripNguiCodes(__result).Trim();
            if (!string.IsNullOrEmpty(cleanedText) && cleanedText.Length > 2)
            {
                Plugin.Log.LogInfo($"[DIALOGUE] {cleanedText}");
                ScreenReader.Say(cleanedText);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DIALOGUE_HOOK] Error: {ex.Message}");
        }
    }

    public static void UIButtonColor_OnHover_Postfix(UIButtonColor __instance, bool isOver)
    {
        GUIAccessibility.OnHover(__instance, isOver);
    }

    // While we drive the placement ghost from the keyboard, suppress the game's mouse-follow.
    // BuildModeLogics.UpdateWhilePlacing -> ProcessMovement -> MoveObjectToMouse snaps the ghost
    // back onto the cursor every frame; skipping it lets our arrow-key MoveCurrentByDir steps
    // actually stick. Returning false skips the original method body.
    public static bool MoveObjectToMouse_Prefix()
    {
        return !BuildPlacementHandler.Active;
    }

    // The player pathfinder rescans its graph (graph 2) to a thin rectangle sized to
    // the straight player->destination line. That's too tight to route around fences
    // and walls (e.g. reaching a grave inside the cemetery), so A* fails even when the
    // destination is valid. When our navigator drives a walk, pad those bounds so the
    // search has room to go around obstacles.
    public static void RefreshPlayerGraph_Prefix(ref Vector2 from, ref Vector2 to)
    {
        try
        {
            if (!ObjectNavigator.PadPlayerGraph) return;

            const float pad = 480f; // ~5 tiles of slack on every side
            var min = Vector2.Min(from, to) - new Vector2(pad, pad);
            var max = Vector2.Max(from, to) + new Vector2(pad, pad);
            from = min;
            to = max;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NAVIGATOR] RefreshPlayerGraph_Prefix error: {ex.Message}");
        }
    }

    // Hook into any Say method to capture dialogue
    public static void OnSayMethod(object[] __args)
    {
        try
        {
            if (__args == null || __args.Length == 0) return;

            Plugin.Log.LogInfo($"[SAY_METHOD] Called with {__args.Length} args");

            // Log all arguments
            for (int i = 0; i < __args.Length && i < 3; i++)
            {
                if (__args[i] is string text)
                {
                    Plugin.Log.LogInfo($"  Arg[{i}] (string): {text}");
                }
                else
                {
                    Plugin.Log.LogInfo($"  Arg[{i}] ({__args[i]?.GetType().Name}): {__args[i]}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[SAY_METHOD] Error: {ex.Message}");
        }
    }

    // Hook into DialogueUGUI.OnSubtitlesRequest to capture and speak dialogue
    // Handles any method signature by using __args
    public static void OnSubtitlesRequest_Prefix(object __instance, object[] __args)
    {
        try
        {
            Plugin.Log.LogInfo($"[DIALOGUE_HOOK] OnSubtitlesRequest called with {__args?.Length ?? 0} args");

            // Log all arguments for debugging
            if (__args != null)
            {
                for (int i = 0; i < __args.Length && i < 5; i++)
                {
                    Plugin.Log.LogInfo($"  Arg[{i}]: {__args[i]?.GetType().Name} = '{__args[i]}'");
                }
            }

            // Try to find and speak the dialogue text from the arguments
            foreach (var arg in __args ?? new object[0])
            {
                if (arg is string text && !string.IsNullOrWhiteSpace(text))
                {
                    var cleanedText = ScreenReader.StripNguiCodes(text).Trim();
                    if (!string.IsNullOrEmpty(cleanedText) && cleanedText.Length > 2)
                    {
                        Plugin.Log.LogInfo($"[DIALOGUE] {cleanedText}");
                        ScreenReader.Say(cleanedText);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DIALOGUE_HOOK] Error: {ex.Message}");
        }
    }
}
