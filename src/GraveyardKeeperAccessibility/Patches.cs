namespace GraveyardKeeperAccessibility;

internal static class Patches
{
    // Dialogue often names a calendar day ("ich komme an Tag 6"). A sighted player can look
    // up the day-of-week; a blind player can't, so we append the weekday/profession name
    // when speaking, e.g. "Tag 6 (Astrologe)". Only the spoken string is changed.
    private static readonly Regex DayMentionRegex =
        new Regex(@"\bTag\s+(\d+)\b", RegexOptions.IgnoreCase);

    // Tasks we've already read aloud this session, so re-issued SetTaskState calls
    // (e.g. the same objective set Visible again) don't repeat the announcement.
    private static readonly HashSet<string> _announcedTasks = new();

    internal static string EnrichDayNumbers(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) < 0)
                return text;

            return DayMentionRegex.Replace(text, m =>
            {
                if (int.TryParse(m.Groups[1].Value, out var day))
                {
                    var name = DayTimeAnnouncer.DayNameForDay(day);
                    if (!string.IsNullOrEmpty(name))
                        return $"{m.Value} ({name})";
                }
                return m.Value;
            });
        }
        catch
        {
            return text;
        }
    }

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
                var spoken = EnrichDayNumbers(cleanedText);
                Plugin.Log.LogInfo($"[DIALOGUE] {spoken}");
                // Non-interrupting: ambient NPC bubbles (e.g. "the villagers are safe with me")
                // fire constantly and would otherwise cut off navigation/pickup/combat speech.
                ScreenReader.Say(spoken, interrupt: false);
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

    // Announce items the player gains. Every way the player receives an item — ground pickup
    // (DropResGameObject -> player.AddToInventory), a finished craft, a caught fish, a vendor
    // buy — funnels through WorldGameObject.AddToInventory on the player object, but none of
    // them voice the item. Speak "Got 4 wood" when the add succeeds on the player. The (string,
    // int) and (List) overloads delegate to this Item overload, so this one hook covers them all.
    public static void WorldGameObject_AddToInventory_Postfix(WorldGameObject __instance, Item __0, bool __result)
    {
        try
        {
            if (!__result || __instance == null || !__instance.is_player) return;
            ItemPickupAnnouncer.OnItemGained(__0);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PICKUP] AddToInventory postfix: {ex.Message}");
        }
    }

    // Announce the map area the player walks into. HUD.UpdateZoneInfo gets the game's own
    // localized zone-name banner (name = GJL.L("zone_"+id), or "..." for open wilderness) every
    // 0.5s; ZoneAnnouncer speaks it when it changes so the player knows when they switch areas.
    public static void HUD_UpdateZoneInfo_Postfix(string __0, string __1)
    {
        try
        {
            ZoneAnnouncer.OnZoneBanner(__0);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[ZONE] UpdateZoneInfo postfix: {ex.Message}");
        }
    }

    // Track when a cutscene takes/returns the player. GS.SetPlayerEnable(false, affect_cinematic:true)
    // grabs the player for a cinematic; SetPlayerEnable(true, ...) hands control back. The navigator
    // needs to know so it never re-enables control mid-cutscene (which freezes the scene — e.g. the
    // first-visit cellar cultist cutscene). __0 = player_enabled, __1 = affect_cinematic.
    public static void GS_SetPlayerEnable_Postfix(bool __0, bool __1)
    {
        try
        {
            ObjectNavigator.OnGameSetPlayerEnable(__0, __1);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NAVIGATOR] SetPlayerEnable postfix: {ex.Message}");
        }
    }

    // AutopsyGUI._body holds the corpse being prepared. Cached so we don't reflect every open.
    private static System.Reflection.FieldInfo _autopsyBodyField;

    // Speak the corpse's skull bar whenever the autopsy/preparation table opens. The skull
    // bar (red = q_minus, white = q_plus, plus freshness) is the whole point of preparing a
    // body, but it's purely visual. Extracting a part calls AutopsyGUI.Hide(), so the player
    // reopens the table to take the next part — reading the current red/white/fresh on every
    // Open lets a blind player tell whether the last extraction lowered or raised the skulls.
    public static void AutopsyGUI_Open_Postfix(AutopsyGUI __instance)
    {
        try
        {
            _autopsyBodyField ??= AccessTools.Field(typeof(AutopsyGUI), "_body");
            var body = _autopsyBodyField?.GetValue(__instance) as Item;

            var desc = SkullInfo.Describe(body);
            if (string.IsNullOrEmpty(desc)) return;

            Plugin.Log.LogInfo($"[AUTOPSY] {desc}");
            ScreenReader.Say(desc, interrupt: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[AUTOPSY_HOOK] Error: {ex.Message}");
        }
    }

    // Speak the corpse's skull bar the instant a part is extracted. RemoveBodyPartFromBody
    // strips the part from the body before the (timed) craft runs and before the GUI hides,
    // so __0 already reflects the new red/white skull counts — giving direct "now X red,
    // Y white" feedback without waiting for the table to be reopened.
    public static void AutopsyGUI_RemoveBodyPartFromBody_Postfix(Item __0, Item __1)
    {
        try
        {
            var desc = SkullInfo.Describe(__0);
            if (string.IsNullOrEmpty(desc)) return;

            string part = null;
            try { part = __1?.definition?.GetItemName(); } catch { }

            var spoken = string.IsNullOrEmpty(part) ? $"Now {desc}" : $"Removed {part}, now {desc}";
            Plugin.Log.LogInfo($"[AUTOPSY] {spoken}");
            ScreenReader.Say(spoken, interrupt: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[AUTOPSY_HOOK] Error: {ex.Message}");
        }
    }

    // Announce tech points the player just earned ("Got 3 green tech points"). TechPointsDrop.Drop
    // with explicit r/g/b counts is the award moment — studying/crafting (WorldGameObject.DropItems
    // aggregates the points into one call) and collecting a dropped tech-point item both route
    // here with full totals. The physics-pickup path uses the string overload instead, so this
    // int overload fires exactly once per award and never double-counts.
    public static void TechPointsDrop_Drop_Postfix(int __1, int __2, int __3)
    {
        try
        {
            if (__1 + __2 + __3 <= 0) return;
            ItemPickupAnnouncer.OnTechPointsGained(__1, __2, __3);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PICKUP] TechPointsDrop.Drop postfix: {ex.Message}");
        }
    }

    // Vendor "Confirm trade". Both our nav button and the game's own confirm key route through
    // VendorGUI.FinishOffer, which silently pops an unread "cant_accept_offer" modal when the
    // deal can't go through (e.g. the vendor has no money to pay for what you're selling). Speak
    // the reason instead and skip the original so no orphan modal is left open. __state carries
    // "this is a valid deal" to the postfix, which announces completion after the trade runs.
    public static bool VendorGUI_FinishOffer_Prefix(VendorGUI __instance, out bool __state)
    {
        __state = false;
        try
        {
            var trading = __instance.trading;
            if (trading == null) return true;

            bool empty;
            try { empty = trading.player_offer.inventory.Count == 0 && trading.trader.cur_offer.inventory.Count == 0; }
            catch { empty = false; }
            if (empty)
            {
                ScreenReader.Say("No offer to confirm");
                return false;
            }

            if (!trading.CanAcceptOffer())
            {
                ScreenReader.Say(GUIAccessibility.VendorRejectReason(trading));
                return false;
            }

            __state = true; // valid deal — let it run, announce in the postfix
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[VENDOR] FinishOffer prefix: {ex.Message}");
        }
        return true;
    }

    public static void VendorGUI_FinishOffer_Postfix(VendorGUI __instance, bool __state)
    {
        if (!__state) return;
        try
        {
            var trading = __instance.trading;
            if (trading != null)
                GUIAccessibility.AnnounceVendorState(__instance, $"Trade complete. You have {GUIAccessibility.MoneyToSpeech(trading.player_money)}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[VENDOR] FinishOffer postfix: {ex.Message}");
        }
    }

    // Auto-announce a newly visible quest objective. The game raises
    // GameSave.SetTaskState(npc, task, state) and flashes a "Neue Aufgabe" bubble when an
    // objective appears (e.g. after the bishop intro), but a blind player gets no readable
    // cue for what to do next. Speak the localized task text the first time it goes Visible.
    public static void GameSave_SetTaskState_Postfix(string __1, KnownNPC.TaskState.State __2)
    {
        try
        {
            if (__2 != KnownNPC.TaskState.State.Visible || string.IsNullOrEmpty(__1)) return;
            if (!_announcedTasks.Add(__1)) return;

            var text = TaskText(__1);
            if (string.IsNullOrEmpty(text)) return;

            Plugin.Log.LogInfo($"[TASK] New task {__1}: {text}");
            ScreenReader.Say($"Neue Aufgabe: {text}", interrupt: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[TASK_HOOK] Error: {ex.Message}");
        }
    }

    // Localized objective text for a task id, matching KnownNPC.TaskState.GetTaskText()
    // (GJL.L("task_" + id)). Returns null when there's no real translation so we stay silent
    // rather than reading back a raw key / "!task_x!" marker.
    private static string TaskText(string taskId)
    {
        try
        {
            var loc = ScreenReader.StripNguiCodes(GJL.L("task_" + taskId) ?? "").Trim();
            if (!string.IsNullOrEmpty(loc) &&
                !loc.Equals("task_" + taskId, StringComparison.OrdinalIgnoreCase) &&
                loc.IndexOf('!') < 0)
                return loc;
        }
        catch { }
        return null;
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
