namespace BeamMeUpGerry;

[Harmony]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class Patches
{

    private static int OneTimeCraftCount;
    private static int KnownZoneCount;
    private static int KnownNpcCount;

    // English-text -> JSON key map used by the SpeechBubble postfix below.
    // The zone strings double as stable zone IDs (see LocationLists + Helpers.UnusualMaps),
    // so they have to stay English on the wire; this map just routes display through Lang.
    private static readonly Dictionary<string, string> EnglishToKey = new(StringComparer.Ordinal)
    {
        [Constants.ZoneLLighthouse]    = "zone.lighthouse",
        [Constants.ZoneLQuarry]        = "zone.quarry",
        [Constants.ZoneLFellingsite]   = "zone.felling_site",
        [Constants.ZoneLCoal]          = "zone.coal",
        [Constants.ZoneLClay]          = "zone.clay",
        [Constants.ZoneLSand]          = "zone.sand",
        [Constants.ZoneLMill]          = "zone.mill",
        [Constants.ZoneLFarmer]        = "zone.farmer",
        [Constants.ZoneLBlacksmith]    = "zone.blacksmith",
        [Constants.ZoneLBodyDump]      = "zone.body_dump",
        [Constants.ZoneLMarketStorage] = "zone.market_storage",
        [Constants.Page1]  = "runtime.page.1",
        [Constants.Page2]  = "runtime.page.2",
        [Constants.Page3]  = "runtime.page.3",
        [Constants.Page4]  = "runtime.page.4",
        [Constants.Page5]  = "runtime.page.5",
        [Constants.Page6]  = "runtime.page.6",
        [Constants.Page7]  = "runtime.page.7",
        [Constants.Page8]  = "runtime.page.8",
        [Constants.Page9]  = "runtime.page.9",
        [Constants.Page10] = "runtime.page.10"
    };

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.GlobalEventsCheck))]
    public static void GameSave_GlobalEventsCheck()
    {
        UpdateZoneUpdaters();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.OnEnteredWorldZone))]
    [HarmonyPatch(typeof(BuildModeLogics), nameof(BuildModeLogics.DoPlace))]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.OnMetNPC))]
    public static void BuildModeLogics_DoPlace_Prefix()
    {
        OneTimeCraftCount = MainGame.me.save.completed_one_time_crafts.Count(a => a.Contains("blockage") || a.Contains("fix_bridge"));
        KnownZoneCount = MainGame.me.save.known_world_zones.Count;
        KnownNpcCount = MainGame.me.save.known_npcs.npcs.Count;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.OnEnteredWorldZone))]
    [HarmonyPatch(typeof(BuildModeLogics), nameof(BuildModeLogics.DoPlace))]
    [HarmonyPatch(typeof(GameSave), nameof(GameSave.OnMetNPC))]
    public static void BuildModeLogics_DoPlace_Postfix()
    {
        var newCraftCount = MainGame.me.save.completed_one_time_crafts.Count(a => a.Contains("blockage") || a.Contains("fix_bridge"));
        var newZoneCount = MainGame.me.save.known_world_zones.Count;
        var newNpcCount = MainGame.me.save.known_npcs.npcs.Count;

        if (newCraftCount > OneTimeCraftCount || newZoneCount > KnownZoneCount || newNpcCount > KnownNpcCount)
        {
            Plugin.InitConfiguration();
        }
    }

    private static void UpdateZoneUpdaters()
    {
        if (!MainGame.me || MainGame.me.save == null) return;

        MainGame.me.save.completed_one_time_crafts = MainGame.me.save.completed_one_time_crafts.Distinct().ToList();

        var steepCoals = WorldMap._objs.Where(a => a.obj_id == "steep_coal").ToList();
        foreach (var steepCoal in steepCoals)
        {
            if (steepCoal.GetMyWorldZoneId() != "flat_under_waterflow") continue;

            var kzu = steepCoal.gameObject.TryAddComponent<KnownZoneUpdater>();
            kzu.Initialize(Constants.NorthCoalZone);
        }

        var zombieTrees = WorldMap._objs.Where(a => a.obj_id == "tree_big_sawmill").ToList();
        foreach (var zombieTree in zombieTrees)
        {
            if (zombieTree.GetMyWorldZoneId() != "flat_under_waterflow_2") continue;

            var kzu = zombieTree.gameObject.TryAddComponent<KnownZoneUpdater>();
            kzu.Initialize(Constants.ZombieSawmill);
        }

        MainGame.me.save.known_world_zones.TryAdd(Constants.SandMoundZone);
        MainGame.me.save.known_world_zones.TryAdd(Constants.ClayPitZone);

        MainGame.me.save.known_world_zones = MainGame.me.save.known_world_zones.Distinct().ToList();
    }


    [HarmonyPostfix]
    [HarmonyWrapSafe]
    [HarmonyPatch(typeof(MultiAnswerGUI), nameof(MultiAnswerGUI.Update))]
    public static void MultiAnswerGUI_Update(MultiAnswerGUI __instance)
    {
        try
        {
            if (!MultiAnswerGUI.talker_wgo.is_player || !Helpers.MakingChoice) return;

            Plugin.CachedPlayer ??= ReInput.players.GetPlayer(0);

            var find = __instance._answers.Find(a => a._answer_id.Contains("cancel"));
            if (find && (Input.GetKeyUp(KeyCode.Escape) || LazyInput.gamepad_active && (Plugin.CachedPlayer.GetButtonUp("B") || Plugin.CachedPlayer.GetButtonUp("Back"))))
            {
                find.OnChosen();
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(Item), nameof(Item.GetGrayedCooldownPercent))]
    public static void Item_GetGrayedCooldownPercent(Item __instance, ref int __result)
    {
        if (__instance is not {id: Constants.Hearthstone}) return;

        __result = 0;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Item), nameof(Item.UseItem))]
    public static bool Item_UseItem(Item __instance, ref GameRes __result)
    {
        if (!Plugin.EnableListExpansion.Value) return true;
        if (__instance is not {id: Constants.Hearthstone}) return true;

        // Block teleport inside dungeons no matter how the item was triggered
        // (keybind or clicking the hearthstone directly in the toolbar).
        if (DungeonState.IsInDungeon)
        {
            Helpers.SpawnGerry(Lang.Get("runtime.cant_use_here"), Vector3.zero);
            __result = new GameRes();
            return false;
        }

        // Rebuild the list against the current save before showing it, so both
        // entry points reflect what the player has actually discovered.
        LocationLists.CreatePages();

        if (__instance.definition.cooldown.has_expression)
        {
            if (__instance.GetGrayedCooldownPercent() != 0)
            {
                __result = new GameRes();
            }

            MainGame.me.player.SetParam("_cooldown_" + __instance.id, __instance.RecalculateTotalCooldown());
        }

        MainGame.me.save.quests.CheckKeyQuests("use_item_" + __instance.id);
        Stats.DesignEvent("Item:Use:" + __instance.id);
        GUIElements.me.hud.toolbar.Redraw();
        if (!string.IsNullOrEmpty(__instance.definition.on_use_snd))
        {
            Sounds.PlaySound(__instance.definition.on_use_snd);
        }

        MainGame.me.player.AddToParams(__instance.definition.params_on_use);
        var gameRes = new GameRes(__instance.definition.params_on_use);
        __result = gameRes;

        if (Plugin.DebugEnabled)
        {
            for (var index = 0; index < LocationLists.Locations.Count; index++)
            {
                var loc = LocationLists.Locations[index];
                Plugin.Log.LogInfo($"Page: {index + 1}");
                foreach (var locItem in loc)
                {
                    Plugin.Log.LogInfo($"LocationItem: {locItem.id}");
                }
            }
        }

        Menus.ShowMultiAnswer(LocationLists.Locations[0]);


        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    public static void MainGame_Update()
    {
        if (!Helpers.IsUpdateConditionsMet()) return;

        Plugin.CachedPlayer ??= ReInput.players.GetPlayer(0);

        if (Plugin.EnableCustomLocations.Value && !DungeonState.IsInDungeon)
        {
            if (Plugin.SaveCustomLocationKeybind.Value.IsUp() || LazyInput.gamepad_active && Plugin.CachedPlayer.GetButtonDown(Plugin.SaveCustomLocationControllerButton.Value))
            {
                MainGame.me.StartCoroutine(Helpers.LogPosition(Plugin.InitConfiguration));
                if (!Plugin.CustomLocationMessage.Value)
                {
                    GUIElements.me.dialog.OpenOK("Beam Me Up Gerry!", null, "You have just saved your first custom location! Please ensure you open the saved file (Locations folder) and change the 'zone' name to something proper!", true, string.Empty);
                    Plugin.CustomLocationMessage.Value = true;
                }
            }

            if (Plugin.ReloadCustomLocationsKeybind.Value.IsUp())
            {
                Plugin.UpdateLists();
            }
        }

        var shouldHandleTeleport = LazyInput.gamepad_active && Plugin.CachedPlayer.GetButtonDown(Plugin.TeleportMenuControllerButton.Value) || Plugin.TeleportMenuKeyBind.Value.IsUp();

        if (!shouldHandleTeleport || Helpers.PlayerDisabled() || Helpers.InTutorial()) return;

        Plugin.CachedHearthstone ??= Helpers.GetHearthstone();

        if (Plugin.CachedHearthstone != null)
        {
            if (DungeonState.IsInDungeon)
            {
                Helpers.SpawnGerry(Lang.Get("runtime.cant_use_here"), Vector3.zero);
            }
            else
            {
                Plugin.CachedHearthstone.UseItem(MainGame.me.player);
            }
        }
        else
        {
            Helpers.SpawnGerry(Lang.Get("runtime.where_is_it"), Vector3.zero);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameSettings), nameof(GameSettings.Init))]
    [HarmonyPatch(typeof(GameSettings), nameof(GameSettings.ApplyLanguageChange))]
    public static void GameSettings_Init()
    {
        Plugin.UpdateLists();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SpeechBubbleGUI), nameof(SpeechBubbleGUI.SpeechText), typeof(string))]
    public static void SpeechBubbleGUI_SpeechText(string s, ref string __result)
    {
        if (s == null || !EnglishToKey.TryGetValue(s, out var key)) return;
        __result = Lang.Get(key);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MultiAnswerGUI), nameof(MultiAnswerGUI.ShowAnswers), typeof(List<AnswerVisualData>), typeof(bool))]
    public static void MultiAnswerGUI_ShowAnswers(MultiAnswerGUI __instance)
    {
        var playerRequest = MultiAnswerGUI.talker_wgo == MainGame.me.player;

        if (playerRequest && Plugin.IncreaseMenuAnimationSpeed.Value)
        {
            __instance.anim_delay /= 3f;
            __instance.anim_time /= 3f;
        }
    }
}
