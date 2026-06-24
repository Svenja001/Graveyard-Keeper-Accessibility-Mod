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

    // _answer_data is the private AnswerVisualData each option GUI is built from. It carries the
    // full translation AND can_be_picked, which we need because "detailed" options (those with an
    // item price/reward, e.g. "give Gerry a beer") clear the visible `label` and render into a
    // separate `label_2`, so reading `label.text` alone returns empty for them.
    private static readonly AccessTools.FieldRef<MultiAnswerOptionGUI, AnswerVisualData> _answerDataField =
        AccessTools.FieldRefAccess<MultiAnswerOptionGUI, AnswerVisualData>("_answer_data");

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

            var optTexts = string.Join(" | ", _options.Select(o => LabelOf(o)));
            _log?.LogInfo($"[DIALOGUE_CHOICE] {_options.Count} answer option(s) shown: {optTexts}");
            AnnounceList();
        }
        catch (Exception ex)
        {
            _log?.LogError($"[DIALOGUE_CHOICE] OnAnswersShown error: {ex.Message}");
        }
    }

    // Harmony postfix on MultiAnswerGUI.OnChosen(string) — fires whenever an answer is
    // committed (by us or otherwise), so we drop our state and release the keyboard.
    //
    // Nested-option guard: picking an answer can synchronously advance the dialogue into a
    // *new* set of answers. Each answer set is a brand-new MultiAnswerGUI instance (the game's
    // static ShowAnswers does _me.Copy()), and that new instance's ShowAnswers runs inside the
    // _on_chosen callback — i.e. BEFORE this postfix. So by the time we get here, _activeGui may
    // already point at the nested bubble. Only clear if the GUI that was just chosen is still the
    // active one; otherwise we'd wipe the freshly-shown nested options and the player gets stuck.
    internal static void OnAnswerChosen(MultiAnswerGUI __instance)
    {
        if (_activeGui != null && !ReferenceEquals(_activeGui, __instance))
        {
            _log?.LogInfo("[DIALOGUE_CHOICE] chosen GUI replaced by nested options; keeping new state");
            return;
        }
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

            // The game locks options the player can't currently take (e.g. "give a beer" when
            // you don't have the beer): MultiAnswerOptionGUI.OnChosen silently no-ops on them.
            // A sighted player sees them greyed out; tell a blind player instead of falsely
            // confirming a choice that does nothing, and keep the dialog open to pick another.
            if (!CanPick(opt))
            {
                _log?.LogInfo($"[DIALOGUE_CHOICE] option #{_selectedIndex} not pickable: {label}");
                ScreenReader.Say($"{label} ist nicht verfügbar", interrupt: true);
                return;
            }

            // Force the appear-animation to finish first: OnChosen also rejects the pick while
            // the option is still fading in (widget alpha < 0.5).
            try { opt.FinishAnimation(); } catch { }

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

    // Full option text. Prefer the source AnswerVisualData.translation, which is always set;
    // the visible label is cleared for "detailed" (item price/reward) options.
    private static string LabelOf(MultiAnswerOptionGUI opt)
    {
        if (opt == null) return "";
        try
        {
            var data = _answerDataField(opt);
            if (data != null && !string.IsNullOrEmpty(data.translation))
                return ScreenReader.StripNguiCodes(data.translation).Trim();
        }
        catch { }
        if (opt.label_2 != null && !string.IsNullOrEmpty(opt.label_2.text))
            return ScreenReader.StripNguiCodes(opt.label_2.text).Trim();
        if (opt.label != null)
            return ScreenReader.StripNguiCodes(opt.label.text ?? "").Trim();
        return "";
    }

    // Whether the game will accept this option right now (false = locked/greyed out).
    private static bool CanPick(MultiAnswerOptionGUI opt)
    {
        try
        {
            var data = _answerDataField(opt);
            if (data != null) return data.can_be_picked;
        }
        catch { }
        return true;
    }

    // Spoken cost of an option, e.g. "5 gold, 50 silver" or "3 wood". The royal-services mailbox
    // ("Königliche Dienstleistungen") and similar paid dialogue options carry their price ONLY as a
    // price icon + number in the AnswerVisualData — never in the translated label — so without this
    // a blind player hears the service name but never learns what it costs. Returns "" when free.
    private static string PriceOf(MultiAnswerOptionGUI opt)
    {
        try
        {
            var data = _answerDataField(opt);
            return PriceFromVisual(data);
        }
        catch { return ""; }
    }

    private static string PriceFromVisual(AnswerVisualData d)
    {
        if (d == null || string.IsNullOrEmpty(d.icon_price)) return "";

        // Text price (money / tech points): SmartRes encodes these as a ":+(gld)5(slv)50"-style
        // string (see SmartRes.FillVisualData). Drop the ":+"/":-" marker and let StripNguiCodes
        // turn the coin tokens into words ("5 gold, 50 silver").
        if (d.icon_price.StartsWith(":"))
        {
            bool negative = d.icon_price.Length > 1 && d.icon_price[1] == '-';
            var raw = d.icon_price.Length > 2 ? d.icon_price.Substring(2) : "";
            var txt = ScreenReader.StripNguiCodes(raw).Trim();
            if (string.IsNullOrEmpty(txt)) return "";
            return negative ? "minus " + txt : txt;
        }

        // Item price: icon_price is a sprite name and n_price the count. Resolve the localized item
        // name from the underlying SmartRes so we say "3 wood", not just "3".
        string itemName = null;
        try
        {
            var sr = d.link_to_answer_data?.d_price;
            if (sr != null && sr.res_type == SmartRes.ResType.Item && sr.item != null)
                itemName = ScreenReader.StripNguiCodes(
                    GameBalance.me.GetData<ItemDefinition>(sr.item.id)?.GetItemName() ?? "").Trim();
        }
        catch { }
        int n = d.n_price;
        if (!string.IsNullOrEmpty(itemName)) return n > 1 ? $"{n} {itemName}" : itemName;
        return n > 1 ? n.ToString() : "";
    }

    // Option text, plus its cost when it has one, plus an availability hint when the game has
    // locked it (typically because the player can't afford that cost).
    private static string OptionPhrase(MultiAnswerOptionGUI opt)
    {
        var label = LabelOf(opt);
        var price = PriceOf(opt);
        if (!string.IsNullOrEmpty(price))
            label = string.IsNullOrEmpty(label) ? $"kostet {price}" : $"{label}, kostet {price}";
        if (!string.IsNullOrEmpty(label) && !CanPick(opt))
            label += " (nicht verfügbar)";
        return label;
    }

    private static void AnnounceList()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(_options.Count == 1
            ? "Eine Antwortmöglichkeit. "
            : $"{_options.Count} Antwortmöglichkeiten. ");
        for (int i = 0; i < _options.Count; i++)
        {
            var label = OptionPhrase(_options[i]);
            if (string.IsNullOrEmpty(label)) continue;
            sb.Append($"{i + 1}. {label}. ");
        }
        ScreenReader.Say(sb.ToString().Trim(), interrupt: false);
    }

    private static void AnnounceSelected()
    {
        if (_options == null || _selectedIndex < 0 || _selectedIndex >= _options.Count) return;
        var label = OptionPhrase(_options[_selectedIndex]);
        ScreenReader.Say($"{_selectedIndex + 1} von {_options.Count}: {label}", interrupt: true);
    }

    private static void Clear()
    {
        _activeGui = null;
        _options = null;
        _selectedIndex = 0;
    }
}
