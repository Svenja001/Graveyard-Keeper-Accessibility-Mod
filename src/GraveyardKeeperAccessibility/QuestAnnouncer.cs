namespace GraveyardKeeperAccessibility;

/// <summary>
/// Reads the player's currently active quest objectives on demand (R key). New objectives are
/// announced automatically when they appear (see Patches.GameSave_SetTaskState_Postfix); this
/// lets the player re-hear what to do next at any time — important after a dialogue/cutscene
/// leaves them unsure of the next step.
/// </summary>
internal static class QuestAnnouncer
{
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    internal static void Announce()
    {
        try
        {
            if (!MainGame.game_started || MainGame.me?.save == null)
            {
                ScreenReader.Say("No game in progress");
                return;
            }

            var parts = new List<string>();

            // Visible NPC tasks are the "Neue Aufgabe" objectives the game tracks per NPC.
            var npcs = MainGame.me.save.known_npcs?.npcs;
            if (npcs != null)
            {
                foreach (var npc in npcs)
                {
                    if (npc?.tasks == null) continue;
                    foreach (var task in npc.tasks)
                    {
                        if (task == null || task.state != KnownNPC.TaskState.State.Visible) continue;

                        string text = null;
                        try { text = ScreenReader.StripNguiCodes(task.GetTaskText() ?? "").Trim(); }
                        catch { }

                        // GJL.L echoes back "!task_x!" when there's no translation — skip those.
                        if (!string.IsNullOrEmpty(text) && text.IndexOf('!') < 0)
                            parts.Add(text);
                    }
                }
            }

            if (parts.Count == 0)
            {
                ScreenReader.Say("No active tasks");
                return;
            }

            var header = parts.Count == 1 ? "1 active task" : $"{parts.Count} active tasks";
            ScreenReader.Say($"{header}: {string.Join(". ", parts)}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"QuestAnnouncer error: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
