namespace GraveyardKeeperAccessibility;

/// <summary>
/// The controls page (pause menu → Steuerung / Controls, a TutorialGUI page carrying a
/// <see cref="ControlsGUI"/>). The page is two things at once: an illustrated summary block that
/// is plain text, and a grid of <see cref="ControlKeyLineGUI"/> rows that each rebind one key.
/// The summary reads as body text via the usual tutorial path; this class turns the grid into
/// navigable rows and drives the game's own rebinding for them.
///
/// Rebinding needs care. <c>ControlKeyLineGUI.Update</c> grabs the next key that goes down while
/// it is rebinding — including, potentially, the very Enter press that started it, because Unity
/// gives no ordering guarantee between our Update and the game's. So the rebind is armed first
/// and only started once every key is back up; from then on this class owns the keyboard until
/// the game reports the rebind finished.
/// </summary>
internal static class ControlsHandler
{
    private enum Phase { Idle, Armed, Rebinding }

    private static Phase _phase = Phase.Idle;
    private static ControlKeyLineGUI _line;
    private static string _lineName;
    private static string _keyBefore;

    // ControlKeyLineGUI._is_rebinding is private and is the only honest signal that the game has
    // captured a key (or that Escape cancelled), so read it reflectively.
    private static FieldInfo _isRebindingField;

    internal static bool IsRebinding => _phase != Phase.Idle;

    /// <summary>
    /// The page's "reset to defaults" action, or null when this window isn't a controls page.
    /// The page draws it as a button, but nothing in the tutorial path scans for UIButtons, so
    /// without this a mis-bound key would be unrecoverable from the keyboard.
    /// </summary>
    internal static ControlsGUI Page(BaseGUI gui)
    {
        try
        {
            foreach (var page in gui.GetComponentsInChildren<ControlsGUI>(true))
            {
                if (page != null && page.gameObject.activeInHierarchy) return page;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[CONTROLS] page scan failed: {ex.Message}");
        }
        return null;
    }

    internal static void ResetBindings(ControlsGUI page)
    {
        try
        {
            page.ResetKeyBindings();
            ScreenReader.Say(Loc.Get("controls.reset_done"), interrupt: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[CONTROLS] reset failed: {ex.Message}");
        }
    }

    /// <summary>The rows of a controls page, or an empty list when this window has none.</summary>
    internal static List<ControlKeyLineGUI> KeyLines(BaseGUI gui)
    {
        var lines = new List<ControlKeyLineGUI>();
        try
        {
            foreach (var line in gui.GetComponentsInChildren<ControlKeyLineGUI>(true))
            {
                if (line == null || !line.gameObject.activeInHierarchy) continue;
                lines.Add(line);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[CONTROLS] key line scan failed: {ex.Message}");
        }
        return lines;
    }

    /// <summary>"Inventar: I" — the row's action and the key currently bound to it.</summary>
    internal static string Describe(ControlKeyLineGUI line)
    {
        try
        {
            var action = ActionName(line);
            var key = CurrentKey(line);
            if (string.IsNullOrWhiteSpace(key)) return Loc.Fmt("controls.row_unbound", action);
            return Loc.Fmt("controls.row", action, key);
        }
        catch
        {
            return line != null ? line.key.ToString() : "";
        }
    }

    /// <summary>Enter on a key row: arm a rebind and tell the player what to do next.</summary>
    internal static void BeginRebind(ControlKeyLineGUI line)
    {
        if (line == null) return;

        _line = line;
        _lineName = ActionName(line);
        _keyBefore = CurrentKey(line);
        _phase = Phase.Armed;
        ScreenReader.Say(Loc.Fmt("controls.press_new_key", _lineName), interrupt: true);
    }

    /// <summary>
    /// Runs every frame. Returns true while a rebind is in flight, so Plugin.Update stops there
    /// and no other handler eats the key the player is trying to bind.
    /// </summary>
    internal static bool Update()
    {
        if (_phase == Phase.Idle) return false;

        try
        {
            // The window went away mid-rebind (Escape closed it, the flow moved on): drop the state
            // rather than holding the keyboard hostage.
            if (_line == null || !_line.gameObject.activeInHierarchy)
            {
                Cancel();
                return false;
            }

            if (_phase == Phase.Armed)
            {
                // Wait for a clean keyboard. Starting while Enter is still held would let the
                // game's own Update capture that same Enter as the new binding.
                if (Input.anyKey) return true;
                _line.OnRebind();
                _phase = Phase.Rebinding;
                return true;
            }

            // Rebinding: the game consumes the next key itself. Watch its private flag and report
            // once it clears.
            if (StillRebinding(_line)) return true;

            var after = CurrentKey(_line);
            var line = _line;
            _phase = Phase.Idle;
            _line = null;

            // Escape cancels inside ControlKeyLineGUI.Update without changing anything, and a
            // player who rebinds a key to the key it already had should hear the same thing.
            if (string.IsNullOrEmpty(after) || after == _keyBefore)
                ScreenReader.Say(Loc.Fmt("controls.unchanged", _lineName, _keyBefore), interrupt: true);
            else
                ScreenReader.Say(Loc.Fmt("controls.rebound", _lineName, after), interrupt: true);

            // The row's spoken label is computed on read (ReadDynamic), so the list needs no
            // rebuild — but the game only redraws the line it changed, and a key stolen from
            // another action leaves that other row stale. Redraw them all.
            RedrawAll(line);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[CONTROLS] rebind failed: {ex.Message}");
            Cancel();
            return false;
        }
    }

    internal static void Cancel()
    {
        _phase = Phase.Idle;
        _line = null;
        _lineName = null;
        _keyBefore = null;
    }

    private static bool StillRebinding(ControlKeyLineGUI line)
    {
        try
        {
            _isRebindingField ??= AccessTools.Field(typeof(ControlKeyLineGUI), "_is_rebinding");
            if (_isRebindingField == null) return false;
            return (bool)_isRebindingField.GetValue(line);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// What the binding is FOR. Our own name (<c>controls.key.&lt;GameKey&gt;</c>) wins over the
    /// game's: the game's page labels the I / T / N keys "character", which is not what I does —
    /// it opens the inventory. Naming each GameKey ourselves is also the only way to be sure the
    /// wording matches the rest of the mod's speech. Falls back to the game's own description for
    /// any key we haven't named, so a new binding is never silent.
    /// </summary>
    private static string ActionName(ControlKeyLineGUI line)
    {
        var own = Loc.Find("controls.key." + line.key);
        if (!string.IsNullOrWhiteSpace(own)) return own;

        var action = ScreenReader.StripNguiCodes(line.key_description?.text)?.Trim();
        if (!string.IsNullOrWhiteSpace(action))
        {
            Plugin.Log.LogInfo($"[CONTROLS] no name for GameKey.{line.key}, using the game's '{action}'");
            return action;
        }
        return line.key.ToString();
    }

    /// <summary>
    /// The key currently bound, spoken as a person would name it. The game hands us the raw
    /// <see cref="KeyCode"/> identifier — "Space", "LeftShift", "Return" — which is English and,
    /// read aloud, is not what the key is called on a German keyboard ("Leertaste").
    /// </summary>
    private static string CurrentKey(ControlKeyLineGUI line)
    {
        var key = ScreenReader.StripNguiCodes(line.key_value?.text)?.Trim();
        if (string.IsNullOrEmpty(key)) return "";

        // Redraw() already strips the brackets off the key icon, but a line that has never been
        // drawn (or a gamepad glyph) can still carry them.
        key = key.Replace("[", "").Replace("]", "").Trim();
        if (key.Length == 0) return "";

        var named = Loc.Find("controls.keyname." + key);
        return string.IsNullOrWhiteSpace(named) ? key : named;
    }

    // KeyBindings.RedefineKey can take a key away from whichever action held it before, and the
    // game only calls Redraw() on the row the player edited. Refresh every row on the page so the
    // list the player arrows through is not lying about the other bindings.
    private static void RedrawAll(ControlKeyLineGUI edited)
    {
        try
        {
            var page = edited.GetComponentInParent<ControlsGUI>();
            if (page == null) return;
            foreach (var line in page.GetComponentsInChildren<ControlKeyLineGUI>(true))
            {
                if (line != null) line.Redraw();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[CONTROLS] redraw failed: {ex.Message}");
        }
    }
}
