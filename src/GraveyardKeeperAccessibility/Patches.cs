namespace GraveyardKeeperAccessibility;

internal static class Patches
{
    public static void UIButtonColor_OnHover_Postfix(UIButtonColor __instance, bool isOver)
    {
        GUIAccessibility.OnHover(__instance, isOver);
    }

    // Hook into dialogue display to capture dialogue text for TTS
    // The game uses BubbleUI to display dialogue bubbles
    public static void BubbleUI_ShowBubble_Postfix(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return;

        // Clean up dialogue text and speak it
        var cleanedText = ScreenReader.StripNguiCodes(str).Trim();
        if (!string.IsNullOrEmpty(cleanedText))
        {
            Plugin.Log.LogInfo($"[DIALOGUE] {cleanedText}");
            ScreenReader.Say(cleanedText);
        }
    }
}
