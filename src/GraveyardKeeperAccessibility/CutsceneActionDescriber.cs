namespace GraveyardKeeperAccessibility;

/// <summary>
/// Says out loud what a cutscene is <em>doing</em>, as opposed to what its characters are saying.
///
/// A Graveyard Keeper cutscene carries a lot of its story in mime. Between the speech bubbles the
/// script raises corpses off the ground, lights bonfires, drops a table out of nowhere, has two
/// legionaries draw their swords on the Master, collapses a burning villa. A sighted player reads
/// all of that off the screen; a blind player gets a pause, and the pauses are long — the report
/// that prompted this was "in some it takes a long while until something narrated new is
/// happening, until the point the mod even says cutscene is still running".
///
/// <para>Sound is deliberately <b>not</b> described. A door, a cheering crowd, a body hitting the
/// ground — the player already hears those, and narrating them would only talk over the scene.
/// What is missing is the silent half, and every silent beat in these scripts is a
/// <c>Trigger Animation</c> node, so one hook on
/// <see cref="WorldGameObject.TriggerSmartAnimation(string)"/> catches all of them. (The two-arg
/// overload delegates to this one, so it is covered too.)</para>
///
/// <para>The descriptions are ours, not the game's — the game has no words for any of this. They
/// live in the lang files under <c>cutscene.action.&lt;trigger name&gt;</c> and were written from
/// the flowscripts: each trigger was read in place, with the dialogue on either side of it, to
/// establish who does what. A trigger with no entry says nothing rather than reading out an
/// internal id, which is the common case — most triggers are fades, glares and smoke puffs that
/// carry no story, and those are left out on purpose.</para>
///
/// <para>Where a description names a person it is because the flowscript pins them down. Where the
/// same trigger is reused by different actors in different scenes (<c>body_takeoff</c> in three,
/// <c>support_start</c> twice, <c>hand_up</c> twice) the wording stays deliberately neutral, since
/// one string has to fit every use.</para>
/// </summary>
internal static class CutsceneActionDescriber
{
    // One trigger is often fired on several objects in the same instant — both legionaries drawing
    // a sword, both bonfires catching light, all three priests laying their palms on the table.
    // That is one beat on screen and must be one line of speech, so a repeat of the same trigger
    // inside this window is swallowed.
    private const float DuplicateWindow = 2f;

    private static string _lastTrigger;
    private static float _lastTriggerAt;

    /// <summary>Forget the last trigger, so a new scene starts with a clean slate.</summary>
    internal static void Reset()
    {
        _lastTrigger = null;
        _lastTriggerAt = 0f;
    }

    /// <summary>
    /// Postfix on <c>WorldGameObject.TriggerSmartAnimation(string)</c>.
    ///
    /// Gated on <see cref="CutsceneAnnouncer.IsRunning"/> first and foremost. The same method runs
    /// constantly in ordinary play — doors, workbenches, anything with a smart animation — and this
    /// must stay out of that path entirely, so the very first thing it does is read one static
    /// bool and leave.
    /// </summary>
    public static void WorldGameObject_TriggerSmartAnimation_Postfix(string __0)
    {
        try
        {
            if (!CutsceneAnnouncer.IsRunning) return;
            if (string.IsNullOrEmpty(__0)) return;

            float now = Time.unscaledTime;
            if (__0 == _lastTrigger && now - _lastTriggerAt < DuplicateWindow)
            {
                _lastTriggerAt = now;
                return;
            }
            _lastTrigger = __0;
            _lastTriggerAt = now;

            var desc = Loc.Find("cutscene.action." + __0);
            if (string.IsNullOrEmpty(desc))
            {
                // Debug rather than Info: a scene fires a lot of these and nearly all of them are
                // transitions we never want to describe. Raise BepInEx's LogLevels to Debug when
                // hunting for a beat that went unnarrated.
                Plugin.Log.LogDebug($"[CUTSCENE] animation '{__0}' has no description");
                return;
            }

            Plugin.Log.LogInfo($"[CUTSCENE] action {__0}");
            // Non-interrupting, like the illustration descriptions: a line of dialogue that is
            // already being read out matters more than the stage direction next to it, and the
            // scene will wait.
            ScreenReader.Say(desc, interrupt: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[CUTSCENE] action error: {ex.Message}");
        }
    }
}
