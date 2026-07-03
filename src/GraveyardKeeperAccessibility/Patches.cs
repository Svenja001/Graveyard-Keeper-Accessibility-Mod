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

    // Auto-collect study/research rewards. When you study an item at the survey table, the game
    // splits the payout: tech points auto-award, but real reward items (a "story", etc.) drop on
    // the GROUND at the table and are never picked up automatically — a blind player has no way to
    // know one is lying there, and it despawns once they walk off. So when a Survey craft is about
    // to drop a non-tech-point reward, put it straight in the player's bag instead. The existing
    // AddToInventory postfix then voices it ("Got a story"). Falls back to the normal ground drop
    // if the bag is full. Scoped to plain Survey crafts: body-part extraction/insertion and souls
    // (gratitude) crafts keep their own drop handling untouched.
    public static bool WorldGameObject_DropItem_Prefix(WorldGameObject __instance, Item __0)
    {
        try
        {
            if (__instance == null || __0 == null || __0.is_tech_point) return true;

            // Stories ("story:1/2/3") are always player rewards — DropStory drops them with
            // Direction.ToPlayer. They reach here from quest flow nodes (Flow_DropStory /
            // Flow_SetTaskState) as task rewards with NO active Survey craft, so the craft gate
            // below misses them and they're left on the ground where a blind player can never find
            // them. Auto-collect any story into the bag (the AddToInventory postfix then voices
            // "Got a story"); fall back to a ground drop only if the bag is full.
            if (__0.id != null && __0.id.StartsWith("story", StringComparison.Ordinal))
            {
                var storyPlayer = MainGame.me?.player;
                if (storyPlayer != null) return !storyPlayer.AddToInventory(__0);
            }

            // Only intercept while THIS station is finishing a survey craft.
            var craft = (__instance.obj_def != null && __instance.obj_def.has_craft)
                ? __instance.components?.craft : null;
            var cur = craft?.current_craft;
            if (cur == null || cur.craft_type != CraftDefinition.CraftType.Survey) return true;
            if (cur.IsBodyPartExtractionCraft() || cur.IsBodyPartInsertionCraft()) return true;
            if (__instance.is_current_craft_gratitude) return true;

            var player = MainGame.me?.player;
            if (player == null) return true;

            // If it fits in the bag, take it (skip the ground drop); otherwise drop as normal.
            return !player.AddToInventory(__0);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PICKUP] DropItem prefix: {ex.Message}");
            return true;
        }
    }

    // Voice each coin donated into the church donation box during a sermon. A sighted player sees
    // a little "+3 bronze" bubble pop over the box (EffectBubblesManager) as each praying NPC drops
    // a coin; a blind player otherwise only hears the generic coin sound. Flow_DonateToBox adds the
    // amount to the box's "_money" param on every donation, so we hook that and speak the amount.
    // Tightly filtered: only the "_money" param on the donation box object itself (custom_tag
    // "donat_box_inside"). The sermon's silent money-spread also touches "_money" but on individual
    // prayer NPCs (different objects), so those are excluded — we only voice the visible drip.
    public static void WorldGameObject_AddToParams_Postfix(WorldGameObject __instance, string __0, float __1)
    {
        try
        {
            if (__1 <= 0f || __0 != "_money" || __instance == null) return;
            var tag = __instance.custom_tag;
            if (string.IsNullOrEmpty(tag) || tag.IndexOf("donat_box", StringComparison.OrdinalIgnoreCase) < 0) return;

            // Queue (don't interrupt) so a rapid burst of coins reads as a sequence, e.g.
            // "3 bronze. 2 bronze. 5 bronze." rather than each cutting off the last.
            ScreenReader.Say(GUIAccessibility.MoneyToSpeech(__1), interrupt: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DONATION] AddToParams postfix: {ex.Message}");
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

    // Cached reflection handle for the private InteractionComponent._collisions list (the objects
    // currently inside the player's forward interaction collider).
    private static System.Reflection.FieldInfo _interactionCollisionsField;

    // Bias the game's E-interaction toward the object the player just auto-walked to. Each frame the
    // interaction component recomputes which interactable is "nearest" in front of the player
    // (InteractionComponent.GetGameObject scores by facing angle + distance), and that object is
    // exactly what vanilla E acts on (FindObjectForInteraction returns interaction.nearest). So when
    // two points of interest sit close together — a chest beside the bed, an object next to the
    // church door — the wrong one can win the score and E opens it instead. While the player is
    // still standing at the object they navigated to, AND it's genuinely inside the interaction
    // collider, force it to be "nearest" so E acts on the object they actually chose. Scoped to the
    // player's own interaction component (NPCs keep their normal selection); leaves drops alone.
    public static void InteractionComponent_FindCurrentInteractionNearest_Postfix(
        InteractionComponent __instance, ref WorldGameObject __result)
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null || __instance != player.components?.interaction) return;

            var preferred = ObjectNavigator.PreferredInteractionTarget();
            if (preferred == null || ReferenceEquals(__result, preferred)) return;

            _interactionCollisionsField ??= AccessTools.Field(typeof(InteractionComponent), "_collisions");
            if (_interactionCollisionsField?.GetValue(__instance) is not List<WorldGameObject> collisions)
                return;

            // Only override when the navigated object is actually in reach (inside the collider), so
            // we never highlight/interact something the player can't physically touch from here.
            if (collisions.Contains(preferred))
                __result = preferred;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NAVIGATOR] interaction-nearest bias: {ex.Message}");
        }
    }

    // The game only ever interacts with (and works on) whatever sits inside the player's forward
    // interaction box — a collider offset AHEAD of the player that snaps to one of 4 cardinal
    // directions (InteractionComponent). interaction.nearest, which both E (Interact) and the tool
    // WORK path (ToolComponent.UseCurrentTool) act on via FindObjectForInteraction(), is null the
    // moment nothing is in that box. After auto-walk the navigated object frequently ends up JUST
    // outside it (a hair off-axis, or facing snapped to the wrong cardinal), so vanilla E / chop /
    // mine does nothing until the player nudges with WASD to sweep the box over it. When the box
    // found NOTHING but the object the player deliberately navigated to is within reach, fill it in
    // as 'nearest' so the action fires without the manual nudge. Only fills when nearest is null —
    // never overrides an object the box legitimately detected, so it can't grab the wrong tree in a
    // dense cluster or an object the player has since turned toward. The reach check (to the
    // object's collider edge) plus PreferredInteractionTarget's hold-distance keep it from reaching
    // past walls or acting on something the player walked away from.
    private static void FillNavTargetAsNearest(InteractionComponent interaction)
    {
        if (interaction == null || interaction.nearest != null) return;
        var target = ObjectNavigator.InteractionTargetWithinReach();
        // The setter also refreshes has_action/has_interaction, so FindObjectForInteraction (which
        // returns interaction.nearest) hands this object to the caller this frame.
        if (target != null) interaction.nearest = target;
    }

    // E / interaction path (chests, doors, stations, graves, NPCs).
    public static void InteractionComponent_Interact_Prefix(InteractionComponent __instance)
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null || __instance != player.components?.interaction) return;
            FillNavTargetAsNearest(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NAVIGATOR] force-interact: {ex.Message}");
        }
    }

    // Tool WORK path (chopping trees, mining ore, digging, harvesting) — same box-miss gate as the
    // interaction path, so the fix must apply here too for consistency. UseTool runs after the
    // work-key dock check has already pulled the player toward the object's dock point (a forgiving
    // omnidirectional 1.5-tile search), so by the time we get here reach is genuinely close.
    public static void ToolComponent_UseTool_Prefix(ToolComponent __instance)
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null || __instance != player.components?.tool) return;
            FillNavTargetAsNearest(player.components?.interaction);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NAVIGATOR] force-work: {ex.Message}");
        }
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
