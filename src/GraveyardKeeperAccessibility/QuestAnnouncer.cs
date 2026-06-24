namespace GraveyardKeeperAccessibility;

/// <summary>
/// The player's active objectives, opened as a navigable list with the J key. Two separate game
/// sources are merged so nothing is missed:
///   1. Story quests — <c>save.quests.GetCurrentQuests()</c> (the QuestSystem), titled
///      <c>GJL.L("qt_" + id)</c>. These are the headline quests the QuestListGUI shows and were
///      NOT covered before.
///   2. NPC tasks — the per-NPC "Neue Aufgabe" objectives in <c>save.known_npcs</c> whose state is
///      Visible (the HUD task tracker's entries). New ones are still auto-announced elsewhere (see
///      Patches.GameSave_SetTaskState_Postfix).
///
/// While the list is open it owns the keyboard (Up/Down move, Enter re-reads, Escape or J closes),
/// so the player can step through one objective at a time instead of hearing a single long string.
/// </summary>
internal static class QuestAnnouncer
{
    private static ManualLogSource _log;

    private static bool _active;
    private static List<string> _items;
    private static int _index;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>Open the quest list (J key): gather objectives and announce the first one.</summary>
    internal static void Open()
    {
        try
        {
            if (!MainGame.game_started || MainGame.me?.save == null)
            {
                ScreenReader.Say("No game in progress");
                return;
            }

            _items = GatherTasks();

            if (_items.Count == 0)
            {
                _active = false;
                ScreenReader.Say("No active tasks");
                return;
            }

            _active = true;
            _index = 0;
            var header = _items.Count == 1 ? "1 active task" : $"{_items.Count} active tasks";
            ScreenReader.Say($"{header}. {_index + 1}: {_items[_index]}");
        }
        catch (Exception ex)
        {
            _log?.LogError($"QuestAnnouncer.Open error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    internal static bool Active => _active;

    /// <summary>
    /// Drives the open list each frame. Returns true while active so Plugin.Update stops the world
    /// nav and other handlers from grabbing the arrow/Enter keys.
    /// </summary>
    internal static bool Update()
    {
        if (!_active) return false;

        // Bail if the game went away under us (load/quit) — release the keyboard.
        if (!MainGame.game_started || MainGame.me?.save == null || _items == null || _items.Count == 0)
        {
            Close(silent: true);
            return false;
        }

        if (Input.GetKeyDown(KeyCode.Escape) || (Input.GetKeyDown(KeyCode.J) && !Ctrl()))
        {
            Close(silent: false);
        }
        else if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            _index = (_index + 1) % _items.Count;
            AnnounceCurrent();
        }
        else if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            _index = (_index - 1 + _items.Count) % _items.Count;
            AnnounceCurrent();
        }
        else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            AnnounceCurrent();
        }

        return true;
    }

    private static bool Ctrl() =>
        Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

    private static void AnnounceCurrent()
    {
        if (_items == null || _index < 0 || _index >= _items.Count) return;
        ScreenReader.Say($"{_index + 1} of {_items.Count}: {_items[_index]}", interrupt: true);
    }

    private static void Close(bool silent)
    {
        _active = false;
        _items = null;
        _index = 0;
        if (!silent) ScreenReader.Say("Quest list closed");
    }

    /// <summary>Merge story quests and visible NPC tasks into one de-duplicated list of texts.</summary>
    private static List<string> GatherTasks()
    {
        var parts = new List<string>();
        var seen = new HashSet<string>();

        void Add(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // GJL.L echoes back the raw key ("!task_x!") when there's no translation.
            if (text.IndexOf('!') >= 0) return;
            if (seen.Add(text)) parts.Add(text);
        }

        var save = MainGame.me.save;

        // 1. Story quests (QuestSystem). Title = GJL.L("qt_" + id); skip ones with no translation
        //    (where GJL.L returns the key unchanged).
        try
        {
            var quests = save.quests?.GetCurrentQuests();
            if (quests != null)
            {
                foreach (var quest in quests)
                {
                    var id = quest?.definition?.id;
                    if (string.IsNullOrEmpty(id)) continue;
                    var key = "qt_" + id;
                    string title = null;
                    try { title = ScreenReader.StripNguiCodes(GJL.L(key) ?? "").Trim(); } catch { }
                    if (string.IsNullOrEmpty(title) || title == key) continue;
                    Add(title);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"QuestAnnouncer: story quests unread: {ex.Message}");
        }

        // 2. Visible NPC tasks (the HUD task-tracker objectives).
        try
        {
            var npcs = save.known_npcs?.npcs;
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
                        Add(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"QuestAnnouncer: NPC tasks unread: {ex.Message}");
        }

        return parts;
    }
}
