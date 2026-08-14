namespace GraveyardKeeperAccessibility;

// The time machine in the tavern cellar (TimeMachineGUI, Stranger Sins). It lists every DLC
// cutscene the player has reached so far and replays the one that's picked.
//
// Each row is a TimeMachineItemGUI: a name label, a date label, an icon and a UIButton. The
// generic pass finds only the bare UIButtons — named "btn"/"bg" inside the cloned prefab — so
// every memory read as the same meaningless word and there was no way to tell which ones are
// even unlocked. Rows are owned here instead: the label reads the memory's name and in-game
// date, locked ones say so, and Enter replays an unlocked memory through the game's own
// OnPlayPressed (which hides the window, runs the machine's 2.5s activate animation and then
// starts the cutscene).
//
// Unlocking follows the player's "cutscene_dlc_num" param: every scene up to and including
// dlc_cutscene_<num> is open, everything past it is locked (see TimeMachineGUI.Open).
internal static class TimeMachineHandler
{
    private static FieldInfo _nameLabelField;
    private static FieldInfo _dateLabelField;
    private static FieldInfo _buttonField;

    // One navigable row per memory, in the order the machine shows them (oldest first).
    internal static void Discover(TimeMachineGUI gui, List<GUIElement> elements)
    {
        var items = SafeItems(gui);
        if (items == null)
        {
            Plugin.Log.LogWarning("[TIMEMACHINE] no items to discover");
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null || !item.gameObject.activeInHierarchy) continue;

            var captured = item;
            var number = i + 1;
            elements.Add(new GUIElement
            {
                Go = item.gameObject,
                Label = RowLabel(captured, number),
                Type = ElementType.Button,
                ReadDynamic = () => RowLabel(captured, number),
                OnActivate = () => Play(captured, number)
            });
        }

        Plugin.Log.LogInfo($"[TIMEMACHINE] Discovered {elements.Count} memory row(s)");
    }

    // "Time machine. 4 of 9 memories unlocked. Enter replays one." — the count is what the
    // player actually wants up-front: how much of the story is available to re-watch.
    internal static string IntroFor(TimeMachineGUI gui)
    {
        var items = SafeItems(gui);
        if (items == null || items.Count == 0)
            return "Time machine. No memories recorded yet.";

        var total = items.Count;
        var unlocked = items.Count(IsUnlocked);
        if (unlocked == 0)
            return $"Time machine. No memories unlocked yet, {total} still to come.";

        var noun = unlocked == 1 ? "memory" : "memories";
        return $"Time machine. {unlocked} of {total} {noun} unlocked. Press Enter on one to replay it.";
    }

    // Land on the newest unlocked memory: it's the one the player is most likely to want back,
    // and everything above it is a step backwards through the story.
    internal static int FocusIndex(List<GUIElement> rows)
    {
        var last = rows.FindLastIndex(e => e.Label != null && !e.Label.EndsWith("locked", StringComparison.OrdinalIgnoreCase));
        return last >= 0 ? last : (rows.Count > 0 ? 0 : -1);
    }

    private static List<TimeMachineItemGUI> SafeItems(TimeMachineGUI gui)
    {
        try { return gui?.items; }
        catch { return null; }
    }

    // A memory is replayable exactly when the game left its button enabled (TimeMachineItemGUI
    // .CheckEnabled), which is also what its own gamepad tip keys off.
    private static bool IsUnlocked(TimeMachineItemGUI item)
    {
        try
        {
            _buttonField ??= AccessTools.Field(typeof(TimeMachineItemGUI), "_button");
            var button = _buttonField?.GetValue(item) as UIButton;
            return button != null && button.enabled;
        }
        catch { return false; }
    }

    // "Memory 3, The wedding, day 12 of summer" / "Memory 7, locked". The numbering gives the
    // player a fixed handle on the list, since the locked rows all read alike.
    private static string RowLabel(TimeMachineItemGUI item, int number)
    {
        if (!IsUnlocked(item))
            return $"Memory {number}, locked";

        var name = LabelText(ref _nameLabelField, "_scene_name_label", item);
        var date = LabelText(ref _dateLabelField, "_scene_date_label", item);

        if (string.IsNullOrWhiteSpace(name)) name = $"Memory {number}";
        else name = $"Memory {number}, {name}";

        return string.IsNullOrWhiteSpace(date) ? name : $"{name}, {date}";
    }

    private static string LabelText(ref FieldInfo cache, string fieldName, TimeMachineItemGUI item)
    {
        try
        {
            cache ??= AccessTools.Field(typeof(TimeMachineItemGUI), fieldName);
            var label = cache?.GetValue(item) as UILabel;
            return ScreenReader.StripNguiCodes(label?.text ?? "")?.Trim();
        }
        catch { return null; }
    }

    // Replay through the game's own handler, so the machine animates and the cutscene starts
    // exactly as it would from a mouse click. Announce first: the window closes and the scene
    // itself takes a couple of seconds to begin, so without this the press sounds like nothing
    // happened. CutsceneAnnouncer takes over from there.
    private static void Play(TimeMachineItemGUI item, int number)
    {
        try
        {
            if (!IsUnlocked(item))
            {
                ScreenReader.Say($"Memory {number} is still locked.", interrupt: true);
                return;
            }

            var name = LabelText(ref _nameLabelField, "_scene_name_label", item);
            ScreenReader.Say(string.IsNullOrWhiteSpace(name)
                ? $"Replaying memory {number}."
                : $"Replaying {name}.", interrupt: true);
            item.OnPlayPressed();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[TIMEMACHINE] replay failed: {ex.Message}");
        }
    }
}
