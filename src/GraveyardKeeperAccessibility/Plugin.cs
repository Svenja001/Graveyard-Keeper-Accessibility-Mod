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
        ItemPickupAnnouncer.Init(Log);
        ClickHandler.Init(Log);
        ObjectNavigator.Init(Log);
        DayTimeAnnouncer.Init(Log);
        QuestAnnouncer.Init(Log);
        ZoneScoreAnnouncer.Init(Log);
        ZoneAnnouncer.Init(Log);
        TechPointsAnnouncer.Init(Log);
        HealthEnergyAnnouncer.Init(Log);
        MoneyAnnouncer.Init(Log);
        CorpseScanner.Init(Log);
        BuildPlacementHandler.Init(Log);
        DialogueChoiceHandler.Init(Log);
        CombatAssist.Init(Log);
        FishingAssist.Init(Log);

        // Test TTS
        Log.LogInfo("[TTS TEST] Speaking test message...");
        ScreenReader.Say("Game starting", interrupt: true);

        var harmony = new HarmonyLib.Harmony(MyPluginInfo.PLUGIN_GUID);

        TryPatch(harmony, typeof(Patches), nameof(Patches.UIButtonColor_OnHover_Postfix),
            typeof(UIButtonColor), "OnHover", new[] { typeof(bool) });

        // Pad the player pathfinding graph bounds while our navigator drives a walk,
        // so A* can route around fences/walls to enclosed targets (e.g. graves).
        TryPatchPrefix(harmony, typeof(Patches), nameof(Patches.RefreshPlayerGraph_Prefix),
            typeof(AStarTools), "RefreshPlayerGraph", new[] { typeof(Vector2), typeof(Vector2) });

        // Stop the build ghost snapping to the mouse while we drive it from the keyboard
        // (see BuildPlacementHandler). MoveObjectToMouse is a private, parameterless method.
        TryPatchPrefix(harmony, typeof(Patches), nameof(Patches.MoveObjectToMouse_Prefix),
            typeof(BuildModeLogics), "MoveObjectToMouse", Type.EmptyTypes);

        // After auto-walk, bias the game's E-interaction onto the object the player navigated to so a
        // closer neighbour (chest beside a bed, etc.) doesn't steal it. FindCurrentInteractionNearest
        // is a private, parameterless method. See Patches.InteractionComponent_*_Postfix.
        TryPatch(harmony, typeof(Patches), nameof(Patches.InteractionComponent_FindCurrentInteractionNearest_Postfix),
            typeof(InteractionComponent), "FindCurrentInteractionNearest", Type.EmptyTypes);

        // Patch WorldGameObject.Say method for dialogue capture
        TryPatchWorldGameObjectSay(harmony);

        // Make dialogue answer choices (MultiAnswerGUI) keyboard-accessible: announce options
        // when shown and clear our state once one is committed. See DialogueChoiceHandler.
        TryPatch(harmony, typeof(DialogueChoiceHandler), nameof(DialogueChoiceHandler.OnAnswersShown),
            typeof(MultiAnswerGUI), "ShowAnswers",
            new[] { typeof(List<AnswerVisualData>), typeof(bool) });
        TryPatch(harmony, typeof(DialogueChoiceHandler), nameof(DialogueChoiceHandler.OnAnswerChosen),
            typeof(MultiAnswerGUI), "OnChosen", new[] { typeof(string) });

        // Announce newly visible quest objectives ("Neue Aufgabe") so the player knows the
        // next step after a dialogue/cutscene. See Patches.GameSave_SetTaskState_Postfix.
        TryPatch(harmony, typeof(Patches), nameof(Patches.GameSave_SetTaskState_Postfix),
            typeof(GameSave), "SetTaskState",
            new[] { typeof(string), typeof(string), typeof(KnownNPC.TaskState.State), typeof(Action) });

        // Read out the corpse's skull bar each time the autopsy/preparation table opens, so
        // the player can tell whether extracting a part changed the red/white skull counts.
        TryPatch(harmony, typeof(Patches), nameof(Patches.AutopsyGUI_Open_Postfix),
            typeof(AutopsyGUI), "Open", new[] { typeof(WorldGameObject) });

        // Also read the skull bar the instant a part is extracted (before the GUI hides), so
        // the player gets direct feedback. See Patches.AutopsyGUI_RemoveBodyPartFromBody_Postfix.
        TryPatch(harmony, typeof(Patches), nameof(Patches.AutopsyGUI_RemoveBodyPartFromBody_Postfix),
            typeof(AutopsyGUI), "RemoveBodyPartFromBody", new[] { typeof(Item), typeof(Item) });

        // Vendor confirm: announce why a trade was rejected (e.g. vendor out of money) instead
        // of the silent unread "cant_accept_offer" modal, and announce completion otherwise.
        // Patching FinishOffer covers both our nav button and the game's own confirm key.
        TryPatchPrefixPostfix(harmony, typeof(Patches),
            nameof(Patches.VendorGUI_FinishOffer_Prefix), nameof(Patches.VendorGUI_FinishOffer_Postfix),
            typeof(VendorGUI), "FinishOffer", Type.EmptyTypes);

        // Speak "Got 4 wood" whenever an item lands in the player's inventory (ground pickups,
        // finished crafts, fishing, vendor buys). See Patches.WorldGameObject_AddToInventory_Postfix.
        TryPatch(harmony, typeof(Patches), nameof(Patches.WorldGameObject_AddToInventory_Postfix),
            typeof(WorldGameObject), "AddToInventory", new[] { typeof(Item) });

        // Auto-collect study/research rewards (stories etc.) into the bag instead of letting them
        // drop silently on the ground at the survey table. See Patches.WorldGameObject_DropItem_Prefix.
        TryPatchPrefix(harmony, typeof(Patches), nameof(Patches.WorldGameObject_DropItem_Prefix),
            typeof(WorldGameObject), "DropItem",
            new[] { typeof(Item), typeof(Direction), typeof(Vector3), typeof(float), typeof(bool) });

        // Voice each coin donated into the church donation box during a sermon ("3 bronze").
        // See Patches.WorldGameObject_AddToParams_Postfix.
        TryPatch(harmony, typeof(Patches), nameof(Patches.WorldGameObject_AddToParams_Postfix),
            typeof(WorldGameObject), "AddToParams", new[] { typeof(string), typeof(float) });

        // Speak earned tech points ("Got 3 green tech points") at the award moment.
        // See Patches.TechPointsDrop_Drop_Postfix.
        TryPatch(harmony, typeof(Patches), nameof(Patches.TechPointsDrop_Drop_Postfix),
            typeof(TechPointsDrop), "Drop", new[] { typeof(Vector3), typeof(int), typeof(int), typeof(int) });

        // Announce the map area the player walks into ("Town", "Graveyard") by voicing the game's
        // own localized zone-name HUD banner. See Patches.HUD_UpdateZoneInfo_Postfix / ZoneAnnouncer.
        TryPatch(harmony, typeof(Patches), nameof(Patches.HUD_UpdateZoneInfo_Postfix),
            typeof(HUD), "UpdateZoneInfo", new[] { typeof(string), typeof(string) });

        // Track cutscene takeovers so an in-progress auto-walk never re-enables control mid-cutscene
        // (that re-Dynamics the body and freezes the scene). See Patches.GS_SetPlayerEnable_Postfix.
        TryPatch(harmony, typeof(Patches), nameof(Patches.GS_SetPlayerEnable_Postfix),
            typeof(GS), "SetPlayerEnable", new[] { typeof(bool), typeof(bool) });

        // Accessible fishing (see FishingAssist): narrate every fishing state change, the selected
        // bait, and the live cast-distance tier; poll the Ctrl+F auto-catch toggle each frame; and
        // (prefix) auto-complete the bite + reel mini-game when auto-catch is on.
        TryPatch(harmony, typeof(FishingAssist), nameof(FishingAssist.FishingGUI_ChangeState_Postfix),
            typeof(FishingGUI), "ChangeState", new[] { typeof(FishingGUI.FishingState) });
        TryPatch(harmony, typeof(FishingAssist), nameof(FishingAssist.FishingGUI_RedrawSelectedBait_Postfix),
            typeof(FishingGUI), "RedrawSelectedBait", Type.EmptyTypes);
        TryPatch(harmony, typeof(FishingAssist), nameof(FishingAssist.FishingGUI_UpdateDistanceChoosing_Postfix),
            typeof(FishingGUI), "UpdateDistanceChoosing", Type.EmptyTypes);
        TryPatch(harmony, typeof(FishingAssist), nameof(FishingAssist.FishingGUI_Update_Postfix),
            typeof(FishingGUI), "Update", Type.EmptyTypes);
        // Auto-catch: on the bite, force a successful TakingOut and set taking_out_animation_finished
        // ourselves (the award is otherwise gated on an animator state-exit event that never fires
        // when we drive the state machine programmatically — that was the hang).
        TryPatch(harmony, typeof(FishingAssist), nameof(FishingAssist.FishingGUI_UpdateWaitingForPulling_Postfix),
            typeof(FishingGUI), "UpdateWaitingForPulling", Type.EmptyTypes);

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
                // Clear the zone de-dup so loading a save re-announces the current area once.
                ZoneAnnouncer.Reset();
                // Re-baseline health/energy silently so a save load doesn't announce the whole bar.
                HealthEnergyAnnouncer.Reset();
            }

            // Speak any items the player just received ("Got 4 wood"). Runs regardless of GUI
            // state so a craft finished with the station window open still announces its output.
            ItemPickupAnnouncer.Update();

            // Speak any change to the player's health/energy bars ("Got 20 energy", "Lost 3
            // health"). Runs regardless of GUI state so eating from the inventory still announces.
            HealthEnergyAnnouncer.Tick();

            // Speak the sermon/donation report a beat after it opens, so it isn't drowned by the
            // end-of-sermon burst of coin/health speech. See GUIAccessibility.FlushPendingReport.
            GUIAccessibility.FlushPendingReport();

            // Accessible build placement: while the build ghost is live, this owns the
            // keyboard (arrows move, Enter places, etc.). Skip the rest of the update so the
            // nav system and menu reader don't fight it for the same keys.
            if (BuildPlacementHandler.Update())
                return;

            // Dialogue answer choices own the keyboard while shown (Up/Down to pick an answer,
            // Enter to confirm) so the world nav and menu reader don't grab the same keys.
            if (DialogueChoiceHandler.Update())
                return;

            // The quest list (J): while open it owns Up/Down/Enter/Escape so the player can step
            // through objectives without the world nav stealing the arrows.
            if (QuestAnnouncer.Update())
                return;

            // Handle click input (Z/X keys)
            ClickHandler.Update();

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

                // Real-time combat assistance (auto-aim, enemy radar, one-key attack).
                CombatAssist.Update();
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

            // Voice live changes to a watched amount/price slider (the game steps it on
            // Left/Right; this announces the new value).
            GUIAccessibility.UpdateWatchers();

            var activeGUI = GUIAccessibility.GetActiveElements();
            var countGUI = activeGUI.Count;
            if (countGUI == 0) return;

            var idxGUI = GUIAccessibility.SelectedIndex;

            // Hold Enter to repeat a hold-capable action (the resource-craft "zerlegen"/study
            // button decomposes a whole stack in one hold). Consumes Enter when it applies so the
            // single-press path below doesn't also fire; returns false for everything else.
            if (GUIAccessibility.TryHandleHoldRepeat())
                return;

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
            else if (Input.GetKeyDown(KeyCode.Delete))
            {
                GUIAccessibility.DeleteSelected();
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

    private static bool TryPatchPrefix(HarmonyLib.Harmony harmony, Type patchClass, string methodName,
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

            var prefix = new HarmonyMethod(AccessTools.Method(patchClass, methodName));
            harmony.Patch(original, prefix: prefix);
            Log.LogInfo($"Patched (prefix) {targetType.Name}.{targetMethod}");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to patch {targetType.Name}.{targetMethod}: {ex.Message}");
            return false;
        }
    }

    // Patch one method with both a prefix and a postfix in a single Patch call, so they share
    // Harmony __state (the prefix tells the postfix whether the trade actually went through).
    private static bool TryPatchPrefixPostfix(HarmonyLib.Harmony harmony, Type patchClass,
        string prefixName, string postfixName, Type targetType, string targetMethod, Type[] parameters)
    {
        try
        {
            var original = AccessTools.Method(targetType, targetMethod, parameters);
            if (original == null)
            {
                Log.LogWarning($"Method {targetType.Name}.{targetMethod} not found, skipping");
                return false;
            }

            var prefix = new HarmonyMethod(AccessTools.Method(patchClass, prefixName));
            var postfix = new HarmonyMethod(AccessTools.Method(patchClass, postfixName));
            harmony.Patch(original, prefix: prefix, postfix: postfix);
            Log.LogInfo($"Patched (prefix+postfix) {targetType.Name}.{targetMethod}");
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

            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            // Navigation controls:
            //   PageUp / PageDown            -> previous / next object in category
            //   Ctrl+PageUp / Ctrl+PageDown  -> previous / next category
            //   Home / Ctrl+Home             -> announce / walk to selected
            //   Escape (while walking)       -> stop walking
            if (Input.GetKeyDown(KeyCode.PageDown))
            {
                if (ctrl) ObjectNavigator.NextCategory();
                else ObjectNavigator.SelectNext();
            }
            else if (Input.GetKeyDown(KeyCode.PageUp))
            {
                if (ctrl) ObjectNavigator.PreviousCategory();
                else ObjectNavigator.SelectPrevious();
            }
            else if (Input.GetKeyDown(KeyCode.Home) && !ctrl)
                ObjectNavigator.AnnounceSelected();
            else if (Input.GetKeyDown(KeyCode.Home) && ctrl)
                ObjectNavigator.WalkToSelected();
            else if (Input.GetKeyDown(KeyCode.Escape) && ObjectNavigator.IsBusy)
                ObjectNavigator.CancelNavigation();
            else if (Input.GetKeyDown(KeyCode.Q) && !ctrl)
                DayTimeAnnouncer.Announce();
            else if (Input.GetKeyDown(KeyCode.G) && !ctrl)
                ZoneScoreAnnouncer.Announce();
            else if (Input.GetKeyDown(KeyCode.P) && !ctrl)
                TechPointsAnnouncer.Announce();
            else if (Input.GetKeyDown(KeyCode.H) && !ctrl)
                HealthEnergyAnnouncer.Announce();
            else if (Input.GetKeyDown(KeyCode.R) && !ctrl)
                MoneyAnnouncer.Announce();
            else if (Input.GetKeyDown(KeyCode.J) && !ctrl)
                QuestAnnouncer.Open();
            else if (Input.GetKeyDown(KeyCode.K) && !ctrl)
                CorpseScanner.Announce();
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
