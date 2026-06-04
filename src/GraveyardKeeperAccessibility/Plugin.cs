namespace GraveyardKeeperAccessibility;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; }

    private void Awake()
    {
        Log = Logger;
        ScreenReader.Init(Log);

        var harmony = new HarmonyLib.Harmony(MyPluginInfo.PLUGIN_GUID);

        TryPatch(harmony, typeof(Patches), nameof(Patches.UIButtonColor_OnHover_Postfix),
            typeof(UIButtonColor), "OnHover", new[] { typeof(bool) });

        Log.LogInfo("Graveyard Keeper Accessibility loaded");
    }

    private int _tickCounter;

    private void Update()
    {
        try
        {
            var guiCheck = ++_tickCounter % 10 == 0;

            if (Input.GetKeyDown(KeyCode.Escape))
                guiCheck = true;

            // Check for GUI first
            if (guiCheck)
            {
                GUIAccessibility.CheckForNewGUI();
            }

            // Only check title screen if no BaseGUI is active
            // This prevents the infinite loop of title screen opening/closing
            if (!GUIAccessibility.HasActiveGUI)
            {
                TitleScreenAccessibility.CheckForTitleScreen();
            }
            else if (TitleScreenAccessibility.HasActiveScreen)
            {
                // If a GUI appeared while title screen was active, close title screen
                TitleScreenAccessibility.OnScreenClosed(TitleScreenAccessibility._currentScreen);
            }

            // Title screen has priority - but only handle input if it has discoverable elements
            if (TitleScreenAccessibility.HasActiveScreen)
            {
                var active = TitleScreenAccessibility.GetActiveElements();
                var count = active.Count;

                // Only handle input and return early if there are elements
                if (count > 0)
                {
                    var idx = TitleScreenAccessibility.SelectedIndex;

                    if (Input.GetKeyDown(KeyCode.DownArrow))
                        TitleScreenAccessibility.SelectIndex((idx + 1) % count);
                    else if (Input.GetKeyDown(KeyCode.UpArrow))
                        TitleScreenAccessibility.SelectIndex((idx - 1 + count) % count);
                    else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    {
                        TitleScreenAccessibility.ActivateSelected();
                    }
                    return;
                }
                // If title screen has no elements, fall through to check GUI instead
            }

            if (!GUIAccessibility.HasActiveGUI) return;

            var activeGUI = GUIAccessibility.GetActiveElements();
            var countGUI = activeGUI.Count;
            if (countGUI == 0) return;

            var idxGUI = GUIAccessibility.SelectedIndex;

            if (Input.GetKeyDown(KeyCode.DownArrow))
                GUIAccessibility.SelectIndex((idxGUI + 1) % countGUI);
            else if (Input.GetKeyDown(KeyCode.UpArrow))
                GUIAccessibility.SelectIndex((idxGUI - 1 + countGUI) % countGUI);
            else if (Input.GetKeyDown(KeyCode.LeftArrow))
                GUIAccessibility.AdjustLeft();
            else if (Input.GetKeyDown(KeyCode.RightArrow))
                GUIAccessibility.AdjustRight();
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                GUIAccessibility.ActivateSelected();
                GUIAccessibility.CheckForNewGUI();
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"Update exception: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static bool TryPatch(HarmonyLib.Harmony harmony, Type patchClass, string methodName,
        Type targetType, string targetMethod, Type[] parameters)
    {
        try
        {
            var original = AccessTools.Method(targetType, targetMethod, parameters);
            if (original == null)
            {
                Log.LogWarning($"Method {targetType.Name}.{targetMethod} not found, skipping");
                return false;
            }

            var postfix = new HarmonyMethod(AccessTools.Method(patchClass, methodName));
            harmony.Patch(original, postfix: postfix);
            Log.LogInfo($"Patched {targetType.Name}.{targetMethod}");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to patch {targetType.Name}.{targetMethod}: {ex.Message}");
            return false;
        }
    }

    private void OnDestroy()
    {
        ScreenReader.Shutdown();
    }
}
