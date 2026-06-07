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
