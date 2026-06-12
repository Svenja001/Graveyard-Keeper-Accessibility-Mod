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
                ScreenReader.Say(spoken);
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
