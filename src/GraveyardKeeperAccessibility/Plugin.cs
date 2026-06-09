namespace GraveyardKeeperAccessibility;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; }

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("[PLUGIN_INIT] NEW CODE PATH EXECUTING");
        ScreenReader.Init(Log);
        MovementFeedback.Init(Log);
        InteractionDetector.Init(Log);
        InventoryItemHandler.Init(Log);
        ClickHandler.Init(Log);
        ObjectNavigator.Init(Log);

        // Test TTS
        Log.LogInfo("[TTS TEST] Speaking test message...");
        ScreenReader.Say("Game starting", interrupt: true);

        var harmony = new HarmonyLib.Harmony(MyPluginInfo.PLUGIN_GUID);

        TryPatch(harmony, typeof(Patches), nameof(Patches.UIButtonColor_OnHover_Postfix),
            typeof(UIButtonColor), "OnHover", new[] { typeof(bool) });

        // Patch WorldGameObject.Say method for dialogue capture
        TryPatchWorldGameObjectSay(harmony);

        Log.LogInfo("Graveyard Keeper Accessibility loaded");
    }

    private int _tickCounter;
    private string _lastSceneName;

    private void Update()
    {
        try
        {
            // Log scene changes
            var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (currentScene != _lastSceneName)
            {
                Log.LogInfo($"[SCENE CHANGE] {_lastSceneName ?? "null"} -> {currentScene}");
                _lastSceneName = currentScene;
            }

            // Handle click input (Z/X keys)
            ClickHandler.Update();

            // Check inventory for item changes
            InventoryItemHandler.Update();

            // Update persistent navigation system
            ObjectNavigator.Update();

            // Handle object navigation input
            HandleNavigationInput();

            var guiCheck = ++_tickCounter % 10 == 0;

            if (Input.GetKeyDown(KeyCode.Escape))
                guiCheck = true;

            // Check for GUI first
            if (guiCheck)
            {
                GUIAccessibility.CheckForNewGUI();
            }

            // Check for interactable objects (when not in GUI/menu)
            if (!GUIAccessibility.HasActiveGUI && !TitleScreenAccessibility.HasActiveScreen)
            {
                // MovementFeedback temporarily disabled - needs debugging
                InteractionDetector.Update();
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

    private static bool TryPatchByName(HarmonyLib.Harmony harmony, Type patchClass, string methodName,
        string targetTypeName, string targetMethod, Type[] parameters)
    {
        try
        {
            var targetType = AccessTools.TypeByName(targetTypeName);
            if (targetType == null)
            {
                Log.LogWarning($"Type '{targetTypeName}' not found, skipping");
                return false;
            }

            return TryPatch(harmony, patchClass, methodName, targetType, targetMethod, parameters);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to find and patch {targetTypeName}.{targetMethod}: {ex.Message}");
            return false;
        }
    }


    private static void TryPatchWorldGameObjectSay(HarmonyLib.Harmony harmony)
    {
        try
        {
            var speechBubbleType = AccessTools.TypeByName("SpeechBubbleGUI");
            if (speechBubbleType == null)
            {
                Log.LogWarning("SpeechBubbleGUI type not found");
                return;
            }

            // Hook into SpeechBubbleGUI.SpeechText(string s) - this method converts dialogue IDs to actual text
            var speechTextMethod = speechBubbleType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "SpeechText" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));

            if (speechTextMethod == null)
            {
                Log.LogWarning("SpeechBubbleGUI.SpeechText method not found");
                return;
            }

            var postfix = new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.SpeechBubbleGUI_SpeechText_Postfix)));
            harmony.Patch(speechTextMethod, postfix: postfix);
            Log.LogInfo("Patched SpeechBubbleGUI.SpeechText");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to patch SpeechBubbleGUI.SpeechText: {ex.Message}");
        }
    }

    private static bool LogDialogueCall(string __methodName, object[] __args)
    {
        try
        {
            var argStr = __args != null && __args.Length > 0
                ? string.Join(", ", __args.Take(3).Select(a => a?.ToString() ?? "null"))
                : "no args";
            Log.LogInfo($"[DIALOGUE METHOD] {__methodName}({argStr})");

            // If this looks like it might contain dialogue text, try to speak it
            if (__args != null && __args.Length > 0)
            {
                var firstArg = __args[0];
                if (firstArg is string text && !string.IsNullOrWhiteSpace(text))
                {
                    var cleanedText = ScreenReader.StripNguiCodes(text).Trim();
                    if (!string.IsNullOrEmpty(cleanedText))
                    {
                        Log.LogInfo($"[DIALOGUE TEXT] {cleanedText}");
                        ScreenReader.Say(cleanedText);
                    }
                }
            }
        }
        catch { }
        return true;
    }

    private void HandleNavigationInput()
    {
        try
        {
            // Only handle navigation input when not in GUI (persistent navigation in world)
            if (GUIAccessibility.HasActiveGUI || TitleScreenAccessibility.HasActiveScreen)
                return;

            // Navigation controls
            if (Input.GetKeyDown(KeyCode.PageDown))
                ObjectNavigator.SelectNext();
            else if (Input.GetKeyDown(KeyCode.PageUp))
                ObjectNavigator.SelectPrevious();
            else if (Input.GetKeyDown(KeyCode.Home) && !Input.GetKey(KeyCode.LeftControl))
                ObjectNavigator.AnnounceSelected();
            else if (Input.GetKeyDown(KeyCode.Home) && Input.GetKey(KeyCode.LeftControl))
                ObjectNavigator.WalkToSelected();
            else if (Input.GetKeyDown(KeyCode.Escape) && ObjectNavigator.IsWalking)
                ObjectNavigator.StopWalking();
        }
        catch (Exception ex)
        {
            Log.LogError($"Navigation input error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void OnDestroy()
    {
        ScreenReader.Shutdown();
    }
}
