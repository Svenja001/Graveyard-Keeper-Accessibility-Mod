namespace GraveyardKeeperAccessibility;

/// <summary>
/// Tells the player when the game has taken control for a cutscene, and keeps saying so through
/// the quiet stretches.
///
/// A cutscene is not just dialogue: between the spoken lines the game pans the camera, walks NPCs
/// to their marks and sits on <c>Wait</c> nodes. A sighted player watches that happen and knows to
/// keep their hands off the keyboard. A blind player hears a line, then nothing, and reasonably
/// concludes the game is idle — and one keypress at that moment can be destructive: interacting
/// mid-scene once left <c>refugee_ev_s2</c> hung after its tenth line, taking the whole camp
/// questline with it until the scene was re-run with control locked. (The precise mechanism is not
/// pinned down — force-hiding a bubble does still invoke its on_disappeared callback via
/// <c>BaseBubbleGUI.StartDisappear</c> — but the failure is real and reproducible in the log.)
///
/// Every cutscene brackets itself with <c>GS.SetPlayerEnable(false…)</c> / <c>(true…)</c>, so that
/// single hook covers all of them. We wait <see cref="StartDelay"/> before announcing, because the
/// same call brackets brief control blips that aren't worth mentioning.
/// </summary>
internal static class CutsceneAnnouncer
{
    private static ManualLogSource _log;

    // Ignore takeovers shorter than this — they're transitions, not scenes.
    private const float StartDelay = 2f;
    // How long a silence inside a cutscene may last before we reassure the player it's still going.
    // Generous on purpose: ordinary pauses between lines are several seconds, and a reminder that
    // fires into those is just chatter.
    private const float SilenceReminder = 30f;
    // A cutscene beginning within this long after an interaction is one the player triggered on
    // purpose (talking to an NPC, using an object), so the "please wait" notice is redundant.
    private const float ExpectedAfterInteraction = 3f;
    // …but announce the end of a long scene even when its start was silent, so the player knows
    // control is theirs again.
    private const float EndNoticeAfter = 30f;

    private static bool _controlDisabled;
    private static float _disabledSince;
    private static bool _active;
    private static bool _spokeStart;
    private static float _lastReminder;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _controlDisabled = false;
        _active = false;
        _spokeStart = false;
    }

    /// <summary>Postfix on <c>GS.SetPlayerEnable</c>: the bracket every cutscene uses.</summary>
    internal static void OnPlayerEnableChanged(bool playerEnabled)
    {
        try
        {
            if (!playerEnabled)
            {
                if (_controlDisabled) return;          // already tracking this one
                _controlDisabled = true;
                _disabledSince = Time.unscaledTime;
                _active = false;
                _spokeStart = false;
                return;
            }

            _controlDisabled = false;
            if (!_active) return;

            bool wasLong = Time.unscaledTime - _disabledSince > EndNoticeAfter;
            _active = false;
            _log?.LogInfo("[CUTSCENE] control returned");
            // Silent start + short scene = the player knows exactly what happened; stay quiet.
            if (_spokeStart || wasLong)
                ScreenReader.Say("Cutscene over", interrupt: false);
            _spokeStart = false;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[CUTSCENE] enable-change error: {ex.Message}");
        }
    }

    /// <summary>
    /// Advance the speech bubble that's currently on screen — the keyboard equivalent of the click
    /// a sighted player uses to skip a line's remaining hold time.
    ///
    /// The game's own skip lives in <see cref="SpeechBubbleGUI"/>'s per-frame animation step and
    /// reacts to <c>GameKey.Back</c>, <c>GameKey.Select</c> or mouse button 0. Back is Escape, which
    /// also aborts scenes outright, so instead of feeding keys in we hide the bubbles directly:
    /// <c>ForceHide</c> → <c>BaseBubbleGUI.StartDisappear</c> → <c>on_disappeared</c>, which is the
    /// very callback <c>Flow_Talk</c>'s "On Finished" output waits on. So the scene continues to its
    /// next line exactly as a click would make it.
    ///
    /// Only the letter-by-letter typing animation is lost, and that carries nothing for a screen
    /// reader — the full line is spoken the moment the bubble appears. Returns true if there was
    /// something to advance, so the caller can leave Enter alone otherwise.
    /// </summary>
    internal static bool TrySkipLine()
    {
        try
        {
            var bubbles = SpeechBubbleGUI.all;
            if (bubbles == null || bubbles.Count == 0) return false;

            // ForceHide destroys the bubble, which removes it from the dictionary — iterate a copy.
            foreach (var bubble in new List<SpeechBubbleGUI>(bubbles.Values))
            {
                if (bubble == null) continue;
                bubble.ForceHide(without_anims: true);
            }
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[CUTSCENE] skip failed: {ex.Message}");
            return false;
        }
    }

    internal static void Update()
    {
        if (!_controlDisabled) return;

        try
        {
            float now = Time.unscaledTime;

            if (!_active)
            {
                if (now - _disabledSince < StartDelay) return;
                _active = true;
                _lastReminder = now;

                // Only worth saying when the scene came out of nowhere — walking into a trigger
                // zone, waking up. If the player just pressed E on an NPC, they know.
                bool expected = _disabledSince - InteractionDetector.LastInteractionAt
                                <= ExpectedAfterInteraction;
                _spokeStart = !expected;
                _log?.LogInfo($"[CUTSCENE] control taken by the game (expected={expected})");
                if (_spokeStart)
                    // Non-interrupting: the scene's own first line matters more than this notice.
                    ScreenReader.Say("Cutscene. Please wait and do not press any keys", interrupt: false);
                return;
            }

            // Quiet stretch inside the scene — say it's still running rather than leave a silence
            // the player has to interpret. Measured from the last thing spoken by anyone, so a
            // scene that keeps talking never triggers it.
            if (now - ScreenReader.LastSpokenAt > SilenceReminder && now - _lastReminder > SilenceReminder)
            {
                _lastReminder = now;
                ScreenReader.Say("Cutscene still running", interrupt: false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[CUTSCENE] update error: {ex.Message}");
        }
    }
}
