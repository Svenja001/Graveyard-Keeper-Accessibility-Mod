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

    /// <summary>
    /// True while the game holds control for a scene. <see cref="CutsceneActionDescriber"/> gates
    /// on this so that an animation trigger only gets described when it is part of a scene, never
    /// during ordinary play — the same trigger names drive doors and machines out in the world.
    /// </summary>
    internal static bool IsRunning => _controlDisabled;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _controlDisabled = false;
        _active = false;
        _spokeStart = false;
        _introRunning = false;
        _introLayersFound = false;
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
                CutsceneActionDescriber.Reset();
                return;
            }

            _controlDisabled = false;
            if (!_active) return;

            bool wasLong = Time.unscaledTime - _disabledSince > EndNoticeAfter;
            _active = false;
            _log?.LogInfo("[CUTSCENE] control returned");
            // Silent start + short scene = the player knows exactly what happened; stay quiet.
            if (_spokeStart || wasLong)
                ScreenReader.Say(Loc.Get("cutscene.over"), interrupt: false);
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

    // ---- The opening crawl in front of a memory ------------------------------------------
    //
    // The first time-machine memory does not start with the scene: it starts with a Star Wars
    // style crawl, a slab of slanted text sliding away into the distance over its own music. The
    // text is a plain UILabel on a prefab — not a speech bubble, not a subtitle, not an
    // IllustrationsGUI card — so every text hook in the mod missed it. What the player did get was
    // the crawl's decorative babble: PerspectiveTextGUI mumbles SmartSpeechEngine.VoiceID.Skull
    // (Gerry's voice) syllable by syllable for the whole ride, so the report was "I only hear
    // Gerry babbling the whole time while this music runs".
    //
    // There is exactly one crawl in the game — perspective_text_before_1s, the prologue about the
    // Ancient God and the three priestess sisters — and DLC_cutscene_1 is the only script with a
    // Perspective Text node in it.

    // The game's own id for that text, used only if the label cannot be read for some reason.
    private const string CrawlTextId = "perspective_text_before_1s";

    // PerspectiveTextGUI is internal to Assembly-CSharp, so it cannot be named in our code at all:
    // the type is found by name at patch time and its label read by reflection here.
    private static FieldInfo _crawlLabelField;

    /// <summary>
    /// Postfix on <c>PerspectiveTextGUI.OpenSlidingText</c>: read the crawl out.
    ///
    /// Safe to read the label this early. <c>OpenSlidingText</c> calls <c>BaseGUI.Open</c> on its
    /// first line, and that runs <c>UpdateLocalizedLabels</c>, so the text is in place and already
    /// in the player's language before we get here — everything after that point is deferred into
    /// a timer and only moves the camera.
    ///
    /// The crawl is 500-odd characters and scrolls for a long time under its own music; speech
    /// running past the end of the slide is fine and expected, and nothing here waits on it.
    /// </summary>
    public static void PerspectiveTextGUI_OpenSlidingText_Postfix(object __instance)
    {
        try
        {
            string raw = null;
            if (__instance != null)
            {
                _crawlLabelField ??= AccessTools.Field(__instance.GetType(), "ui_label");
                if (_crawlLabelField?.GetValue(__instance) is UILabel label) raw = label.text;
            }
            if (string.IsNullOrWhiteSpace(raw)) raw = GJL.L(CrawlTextId);

            // The crawl is laid out as paragraphs with blank lines between them, and the German
            // text indents every one of them. Flatten it before stripping: StripNguiCodes squeezes
            // runs of spaces but leaves newlines alone.
            var text = ScreenReader.StripNguiCodes(Regex.Replace(raw ?? "", @"\s+", " ")).Trim();
            if (string.IsNullOrEmpty(text)) return;

            _log?.LogInfo($"[CUTSCENE] opening crawl ({text.Length} chars)");
            ScreenReader.Say(text, interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[CUTSCENE] crawl error: {ex.Message}");
        }
    }

    // ---- The opening intro --------------------------------------------------------------
    //
    // The intro is not a cutscene in the sense above and none of the tracking above sees it. It
    // plays in the TITLE scene before the world exists, so GS.SetPlayerEnable never brackets it;
    // it is an Animator moving painted sprite layers, and its three narrated lines arrive as
    // animation events calling Intro.ShowSubtitleText. Nothing there is a speech bubble either, so
    // the dialogue hook missed it too: starting a new game meant hearing "Loading", then close to a
    // minute of nothing, and then finding yourself in the graveyard with no idea what had happened.
    //
    // Everything in this section must stay clear of MainGame.me. It is null for the whole sequence —
    // Intro.Awake uses exactly that to tell "playing in the title scene" from "loaded inside a
    // running game". Loc and ScreenReader are both safe that early; the speech engine and our key
    // handlers are already live by the time a save slot has been picked.

    private static bool _introRunning;

    /// <summary>
    /// Prefix on <c>Intro.ShowIntro</c>: say that the opening scene is playing.
    ///
    /// The player has just heard "Loading" and the loading screen has gone away, so without this
    /// the silence before the first narrated line reads as a hung game rather than as a film
    /// starting. Gated on <c>need_show_first_intro</c>, the same flag ShowIntro itself checks
    /// before deciding whether to play anything — a load of an existing save clears it and must
    /// stay quiet. (ShowIntro also bails when it cannot find the Intro object at all, which we
    /// cannot see from here; that means a missing title scene, and the game is broken anyway.)
    /// </summary>
    public static void Intro_ShowIntro_Prefix()
    {
        try
        {
            if (!Intro.need_show_first_intro) return;
            _introRunning = true;
            _introLayersFound = false;
            _log?.LogInfo("[INTRO] opening scene started");
            ScreenReader.Say(Loc.Get("intro.start"), interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTRO] start error: {ex.Message}");
        }
    }

    /// <summary>
    /// Postfix on <c>Intro.ShowSubtitleText</c>: speak one of the intro's narrated lines.
    ///
    /// Only three exist (intro_txt_1..3), fired from animation events on the clip timeline. The
    /// label is filled in synchronously right above us — no fade tween stands between the call and
    /// the text, unlike the storybook card in <see cref="Patches.IllustrationsGUI_SetText_Postfix"/>
    /// — so read it straight back off the label, and fall back to resolving the id ourselves.
    /// </summary>
    public static void Intro_ShowSubtitleText_Postfix(Intro __instance, string __0)
    {
        try
        {
            string raw = null;
            try { raw = __instance?.subtitle_text?.text; } catch { }
            if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(__0))
                raw = GJL.L(__0);

            var text = ScreenReader.StripNguiCodes(raw ?? "").Trim();
            if (string.IsNullOrEmpty(text) || text.Length <= 2) return;

            _introRunning = true;   // a replay through Flow_ShowIntro reaches us here first
            _log?.LogInfo($"[INTRO] {text}");
            ScreenReader.Say(text, interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTRO] subtitle error: {ex.Message}");
        }
    }

    // Every GameObject under Intro, cached for one playthrough of it, and which of them are
    // switched on right now. The animator drives the whole sequence by toggling these; there is no
    // method call to hook, so watching them is the only way to know what the intro is doing.
    //
    // Everything is watched, not just the four scene objects, because the moment that matters most
    // is not a scene at all: the car. The scene file has `snd: car skids` and `snd: car horn`
    // sitting inactive under Sounds, and the animator switches them on at the crash. That is the
    // only signal for it — the man outside the shop stays on screen for the whole of scene one and
    // only vanishes when that scene ends, so treating his sprite as the impact put the line in
    // minutes early (reported 2026-09-05, and the log said exactly that).
    private static readonly List<GameObject> _introLayers = new();
    private static readonly HashSet<string> _introLayersOn = new();
    private static bool _introLayersFound;

    /// <summary>
    /// Watch the intro and narrate what it is doing, keyed on object name as
    /// <c>intro.scene.&lt;name&gt;</c>. Objects with no entry are silent, which is nearly all of
    /// them — the cameras, the black frames, the backdrops, the safe-zone markers.
    ///
    /// Currently five entries: the four scenes (<c>intro_1</c>..<c>intro_4</c>, played strictly in
    /// that order, one at a time) and <c>snd: car skids</c> for the crash.
    ///
    /// Two things here were learned the hard way and are worth not re-learning:
    /// <list type="bullet">
    /// <item>The asset names are NOT the object names. <c>intro_1_man</c>, <c>intro_1_no_man</c>,
    /// the clouds and the black frame are sprites; the objects that draw them are called
    /// <c>man</c>, <c>sky</c>, <c>clouds</c>. A key on a sprite name matches nothing, silently.</item>
    /// <item>The intro is not a GUI. Its layers are world <c>SpriteRenderer</c>s under an Animator,
    /// so nothing NGUI reaches it.</item>
    /// </list>
    ///
    /// No ordering is assumed anywhere: each object is announced the first time it turns on,
    /// whenever that is, and every transition is logged either way.
    ///
    /// Called from Plugin.Update on every frame and returns immediately unless an intro is running.
    /// </summary>
    internal static void IntroTick()
    {
        if (!_introRunning) return;

        try
        {
            if (!_introLayersFound) FindIntroLayers();

            foreach (var go in _introLayers)
            {
                if (go == null) continue;
                var name = go.name;
                bool on = go.activeInHierarchy;
                if (on == _introLayersOn.Contains(name)) continue;

                if (on)
                {
                    _introLayersOn.Add(name);
                    _log?.LogInfo($"[INTRO] on: {name}");
                    var line = Loc.Find("intro.scene." + name);
                    if (!string.IsNullOrEmpty(line))
                        ScreenReader.Say(line, interrupt: false);
                }
                else
                {
                    _introLayersOn.Remove(name);
                    _log?.LogInfo($"[INTRO] off: {name}");
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTRO] watch error: {ex.Message}");
            _introRunning = false;   // don't spam the log frame after frame
        }
    }

    /// <summary>
    /// Collect everything under the Intro object once per playthrough. MainGame.me is null out
    /// here, so nothing may go looking for the world. Inactive children included — most of the
    /// intro, the crash sounds among them, is switched off at this point.
    /// </summary>
    private static void FindIntroLayers()
    {
        _introLayersFound = true;
        _introLayers.Clear();
        _introLayersOn.Clear();

        var intro = UnityEngine.Object.FindObjectOfType<Intro>();
        if (intro == null)
        {
            _log?.LogInfo("[INTRO] no Intro object found - nothing to narrate this run");
            return;
        }

        var alreadyOn = new List<string>();
        foreach (var t in intro.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t.gameObject == null) continue;
            _introLayers.Add(t.gameObject);
            if (t.gameObject.activeInHierarchy) alreadyOn.Add(t.gameObject.name);
        }

        // Deliberately do NOT seed _introLayersOn from alreadyOn. The animator is triggered in the
        // same frame the Intro object is switched on, so by the time this first runs the opening
        // scene may already be up — and seeding it would swallow the one line the player most
        // needs. Treating everything as newly-on costs nothing: only names we have written a line
        // for say anything at all.
        _log?.LogInfo($"[INTRO] watching {_introLayers.Count} objects "
                      + $"(on at start: {string.Join(", ", alreadyOn.ToArray())})");
    }

    /// <summary>
    /// Postfix on <c>Intro.OnIntroAnimationFinished</c>: the scene is over and the game proper
    /// begins. Said non-interrupting, because what follows (the HUD opening, the zone you wake up
    /// in) is the more useful news and should not be talked over on its way in.
    /// </summary>
    public static void Intro_OnIntroAnimationFinished_Postfix()
    {
        try
        {
            if (!_introRunning) return;
            _introRunning = false;
            _introLayers.Clear();
            _introLayersOn.Clear();
            _log?.LogInfo("[INTRO] opening scene over");
            ScreenReader.Say(Loc.Get("intro.over"), interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTRO] finish error: {ex.Message}");
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
                    ScreenReader.Say(Loc.Get("cutscene.start"), interrupt: false);
                return;
            }

            // Quiet stretch inside the scene — say it's still running rather than leave a silence
            // the player has to interpret. Measured from the last thing spoken by anyone, so a
            // scene that keeps talking never triggers it.
            if (now - ScreenReader.LastSpokenAt > SilenceReminder && now - _lastReminder > SilenceReminder)
            {
                _lastReminder = now;
                ScreenReader.Say(Loc.Get("cutscene.still_running"), interrupt: false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[CUTSCENE] update error: {ex.Message}");
        }
    }
}
