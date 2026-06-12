namespace GraveyardKeeperAccessibility;

// Dialogue answer choices (e.g. the bishop intro: "Ich habe den Friedhof in Ordnung
// gebracht" / "Frage nach der Urkunde") are shown by MultiAnswerGUI as floating bubble
// options. Vanilla only supports picking them with the mouse or a gamepad, so a blind
// keyboard player gets stuck — the intro can't progress. This handler announces the
// options and lets the player navigate them with Up/Down and confirm with Enter.
internal static class DialogueChoiceHandler
{
    private static ManualLogSource _log;

    private static MultiAnswerGUI _activeGui;
    private static List<MultiAnswerOptionGUI> _options;
    private static int _selectedIndex;

    // _answers is the private List<MultiAnswerOptionGUI> MultiAnswerGUI builds in ShowAnswers.
    private static readonly AccessTools.FieldRef<MultiAnswerGUI, List<MultiAnswerOptionGUI>> _answersField =
        AccessTools.FieldRefAccess<MultiAnswerGUI, List<MultiAnswerOptionGUI>>("_answers");

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[DIALOGUE_CHOICE] initialized - Up/Down navigate answers, Enter confirms");
    }

    internal static bool Active => _activeGui != null;

    // Harmony postfix on the instance MultiAnswerGUI.ShowAnswers(List<AnswerVisualData>, bool),
    // which runs after the option GUIs have been created and stored in _answers.
    internal static void OnAnswersShown(MultiAnswerGUI __instance)
    {
        try
        {
            var answers = _answersField(__instance);
            if (answers == null || answers.Count == 0) return;

            _activeGui = __instance;
            _options = new List<MultiAnswerOptionGUI>(answers);
            _selectedIndex = 0;

            _log?.LogInfo($"[DIALOGUE_CHOICE] {_options.Count} answer option(s) shown");
            AnnounceList();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[DIALOGUE_CHOICE] OnAnswersShown error: {ex.Message}");
        }
    }

    // Harmony postfix on MultiAnswerGUI.OnChosen(string) — fires whenever an answer is
    // committed (by us or otherwise), so we drop our state and release the keyboard.
    internal static void OnAnswerChosen()
    {
        Clear();
    }

    // Drives navigation each frame. Returns true while a choice is active so Plugin.Update
    // can stop other handlers (world nav, menu reader) from stealing the arrow/Enter keys.
    internal static bool Update()
    {
        if (_activeGui == null) return false;

        // Bubble was destroyed/hidden without an OnChosen we caught — clean up and bail.
        if (_activeGui.gameObject == null || !_activeGui.gameObject.activeInHierarchy ||
            _options == null || _options.Count == 0)
        {
            Clear();
            return false;
        }

        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            _selectedIndex = (_selectedIndex + 1) % _options.Count;
            AnnounceSelected();
        }
        else if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            _selectedIndex = (_selectedIndex - 1 + _options.Count) % _options.Count;
            AnnounceSelected();
        }
        else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            ChooseSelected();
        }

        return true;
    }

    private static void ChooseSelected()
    {
        try
        {
            if (_selectedIndex < 0 || _selectedIndex >= _options.Count) return;

            var opt = _options[_selectedIndex];
            if (opt == null) return;

            var label = LabelOf(opt);
            _log?.LogInfo($"[DIALOGUE_CHOICE] choosing #{_selectedIndex}: {label}");
            ScreenReader.Say($"Gewählt: {label}", interrupt: true);

            // Mirrors a mouse click on the option. This calls back into MultiAnswerGUI.OnChosen,
            // which our OnAnswerChosen postfix catches to clear state.
            opt.OnChosen();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[DIALOGUE_CHOICE] ChooseSelected error: {ex.Message}");
        }
    }

    private static string LabelOf(MultiAnswerOptionGUI opt)
    {
        if (opt == null || opt.label == null) return "";
        return ScreenReader.StripNguiCodes(opt.label.text ?? "").Trim();
    }

    private static void AnnounceList()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(_options.Count == 1
            ? "Eine Antwortmöglichkeit. "
            : $"{_options.Count} Antwortmöglichkeiten. ");
        for (int i = 0; i < _options.Count; i++)
        {
            var label = LabelOf(_options[i]);
            if (string.IsNullOrEmpty(label)) continue;
            sb.Append($"{i + 1}. {label}. ");
        }
        ScreenReader.Say(sb.ToString().Trim(), interrupt: false);
    }

    private static void AnnounceSelected()
    {
        if (_options == null || _selectedIndex < 0 || _selectedIndex >= _options.Count) return;
        var label = LabelOf(_options[_selectedIndex]);
        ScreenReader.Say($"{_selectedIndex + 1} von {_options.Count}: {label}", interrupt: true);
    }

    private static void Clear()
    {
        _activeGui = null;
        _options = null;
        _selectedIndex = 0;
    }
}
